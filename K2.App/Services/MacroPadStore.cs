using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using K2.Core;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistent state of the MacroPad module. For each (deviceId, profile, key)
/// stores the assigned action; also stores, per device, the
/// hardware-matrix -> key-index map and various settings.
///
/// Same schema as the DisplayPad's <c>StateStore</c>, minus the DisplayPad-specific
/// fields (image, rotation): MacroPad keys have no display.
/// </summary>
public sealed class MacroPadStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public MacroPadStore(string? dbPath = null)
    {
        dbPath ??= DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        _conn.Open();
        EnsureSchema();
    }

    public static string DefaultDbPath()
    {
        var dir = K2Paths.For("K2.App");
        return Path.Combine(dir, "macropad.db");
    }

    private void EnsureSchema()
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Keys (
    DeviceId    INTEGER NOT NULL,
    Profile     INTEGER NOT NULL,
    KeyIndex    INTEGER NOT NULL,
    ActionType  TEXT,
    ActionValue TEXT,
    PRIMARY KEY (DeviceId, Profile, KeyIndex)
);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

CREATE TABLE IF NOT EXISTS KeycapOverrides (
    Profile   INTEGER NOT NULL,
    KeyId     INTEGER NOT NULL,
    ColorHex  TEXT,
    ImagePath TEXT,
    PRIMARY KEY (Profile, KeyId)
);";
            cmd.ExecuteNonQuery();
        }
        MigrateKeycapOverridesToPerProfile();
    }

    /// <summary>One-time migration (2026-07-25) — see EverestStore.cs's identical method
    /// for the full rationale. MacroPad uses the same 5 fixed profile slots
    /// (MacroPadSdkNative.FW_NUM_PROFILE). Note: like the rest of MacroPad's Settings
    /// section, this is profile-scoped only, not deviceId-scoped — a pre-existing gap
    /// (settings.keycap_*/macropad.rotation/macroled.* already ignore deviceId too),
    /// left as-is here rather than conflated with this migration.</summary>
    private void MigrateKeycapOverridesToPerProfile()
    {
        bool hasProfileColumn = false;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(KeycapOverrides)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), "Profile", StringComparison.OrdinalIgnoreCase))
                {
                    hasProfileColumn = true;
                    break;
                }
            }
        }
        if (hasProfileColumn) return;

        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE KeycapOverrides RENAME TO KeycapOverrides_old";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
CREATE TABLE KeycapOverrides (
    Profile   INTEGER NOT NULL,
    KeyId     INTEGER NOT NULL,
    ColorHex  TEXT,
    ImagePath TEXT,
    PRIMARY KEY (Profile, KeyId)
);
INSERT INTO KeycapOverrides (Profile, KeyId, ColorHex, ImagePath)
SELECT p.value, o.KeyId, o.ColorHex, o.ImagePath
FROM KeycapOverrides_old o, (SELECT 1 AS value UNION SELECT 2 UNION SELECT 3 UNION SELECT 4 UNION SELECT 5) p;
DROP TABLE KeycapOverrides_old;";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------- per-key appearance overrides (color / custom image) ----------
    // Per-profile (2026-07-25, migrated from device-wide) — see
    // MainWindow.MacroKeycapAppearance.cs. KeyId = physical key index (0..11, same identity
    // as _mpKeyVisuals). No Esc key on the MacroPad, so no Mountain-logo sentinel here.

    public Dictionary<int, KeycapOverrideRecord> LoadAllKeycapOverrides(int profile)
    {
        var result = new Dictionary<int, KeycapOverrideRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT KeyId, ColorHex, ImagePath FROM KeycapOverrides WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int keyId = r.GetInt32(0);
            result[keyId] = new KeycapOverrideRecord(keyId, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        return result;
    }

    public void SetKeycapOverride(int profile, int keyId, string? colorHex, string? imagePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO KeycapOverrides(Profile, KeyId, ColorHex, ImagePath) VALUES ($p, $k, $c, $i)
ON CONFLICT(Profile, KeyId) DO UPDATE SET ColorHex=excluded.ColorHex, ImagePath=excluded.ImagePath";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.Parameters.AddWithValue("$k", keyId);
        cmd.Parameters.AddWithValue("$c", (object?)colorHex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", (object?)imagePath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void ClearKeycapOverride(int profile, int keyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM KeycapOverrides WHERE Profile=$p AND KeyId=$k";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.Parameters.AddWithValue("$k", keyId);
        cmd.ExecuteNonQuery();
    }

    // ---------- key actions ----------

    public IReadOnlyList<MacroKeyRecord> LoadProfile(int deviceId, int profile)
    {
        var result = new List<MacroKeyRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT KeyIndex, ActionType, ActionValue
                            FROM Keys
                            WHERE DeviceId=$d AND Profile=$p
                            ORDER BY KeyIndex";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? at = r.IsDBNull(1) ? null : r.GetString(1);
            // Leftover bc:Default from an old BC import = "no custom binding": empty key.
            if (BaseCampDbImporter.IsBcDefaultAction(at)) continue;
            result.Add(new MacroKeyRecord(
                deviceId, profile, r.GetInt32(0),
                at,
                r.IsDBNull(2) ? null : r.GetString(2)));
        }
        return result;
    }

    public void SaveKey(MacroKeyRecord k)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Keys(DeviceId, Profile, KeyIndex, ActionType, ActionValue)
VALUES ($d, $p, $k, $at, $av)
ON CONFLICT(DeviceId, Profile, KeyIndex) DO UPDATE SET
  ActionType  = excluded.ActionType,
  ActionValue = excluded.ActionValue";
        cmd.Parameters.AddWithValue("$d",  k.DeviceId);
        cmd.Parameters.AddWithValue("$p",  k.Profile);
        cmd.Parameters.AddWithValue("$k",  k.KeyIndex);
        cmd.Parameters.AddWithValue("$at", (object?)k.ActionType  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av", (object?)k.ActionValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every key currently configured with the given action (e.g. macro assignment lookup).</summary>
    public List<(int DeviceId, int Profile, int KeyIndex)> GetKeysByAction(string actionType, string actionValue)
    {
        var result = new List<(int, int, int)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT DeviceId, Profile, KeyIndex FROM Keys
                            WHERE ActionType=$t AND ActionValue=$v
                            ORDER BY DeviceId, Profile, KeyIndex";
        cmd.Parameters.AddWithValue("$t", actionType);
        cmd.Parameters.AddWithValue("$v", actionValue);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        return result;
    }

    /// <summary>Deletes all actions of a profile.</summary>
    public void ClearProfile(int deviceId, int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE DeviceId=$d AND Profile=$p";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Wipes every device's keys/settings/keycap overrides — used by the app-wide
    /// "Restore all defaults" (Settings tab). Per-device "Restore defaults" instead reuses
    /// <see cref="ClearProfile"/> directly (it already keeps the profile's name).</summary>
    public void ResetAllData()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys; DELETE FROM Settings; DELETE FROM KeycapOverrides;";
        cmd.ExecuteNonQuery();
    }

    // ---------- generic settings ----------

    public string? GetSetting(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : o.ToString();
    }

    public void SetSetting(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Settings(Key, Value) VALUES ($k, $v)
ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---------- current profile per device ----------

    /// <summary>Returns the profile slots that have at least one saved key for the device.</summary>
    public List<int> GetExistingProfiles(int deviceId)
    {
        var result = new List<int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Profile FROM Keys WHERE DeviceId=$d ORDER BY Profile";
        cmd.Parameters.AddWithValue("$d", deviceId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetInt32(0));
        return result;
    }

    public int GetCurrentProfile(int deviceId, int fallback = 1)
    {
        var s = GetSetting($"device.{deviceId}.currentProfile");
        return int.TryParse(s, out var v) && v >= 1 ? v : fallback;
    }

    public void SetCurrentProfile(int deviceId, int profile) =>
        SetSetting($"device.{deviceId}.currentProfile", profile.ToString());

    // ---------- profile names ----------

    public string? GetProfileName(int deviceId, int slot)
    {
        var v = GetSetting($"profile.{deviceId}.{slot}.name");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public void SetProfileName(int deviceId, int slot, string name) =>
        SetSetting($"profile.{deviceId}.{slot}.name", name.Trim());

    // ---------- hardware-matrix -> key-index map ----------

    /// <summary>Saved <c>matrix -> key index</c> map for the device.</summary>
    public Dictionary<int, int> GetKeyMap(int deviceId)
    {
        var json = GetSetting($"device.{deviceId}.keymap");
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, int>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, int>>(json)
                   ?? new Dictionary<int, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, int>();
        }
    }

    public void SetKeyMap(int deviceId, IReadOnlyDictionary<int, int> map) =>
        SetSetting($"device.{deviceId}.keymap", JsonSerializer.Serialize(map));

    public void Dispose() => _conn.Dispose();
}

public sealed record MacroKeyRecord(
    int     DeviceId,
    int     Profile,
    int     KeyIndex,
    string? ActionType,
    string? ActionValue);
