using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.App");
        return Path.Combine(dir, "macropad.db");
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
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
    KeyId     INTEGER PRIMARY KEY,
    ColorHex  TEXT,
    ImagePath TEXT
);";
        cmd.ExecuteNonQuery();
    }

    // ---------- per-key appearance overrides (color / custom image) ----------
    // Global (not per-device/profile) like the rest of Keycap Appearance — see
    // MainWindow.MacroKeycapAppearance.cs. KeyId = physical key index (0..11, same identity
    // as _mpKeyVisuals). No Esc key on the MacroPad, so no Mountain-logo sentinel here.

    public Dictionary<int, KeycapOverrideRecord> LoadAllKeycapOverrides()
    {
        var result = new Dictionary<int, KeycapOverrideRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT KeyId, ColorHex, ImagePath FROM KeycapOverrides";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int keyId = r.GetInt32(0);
            result[keyId] = new KeycapOverrideRecord(keyId, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        return result;
    }

    public void SetKeycapOverride(int keyId, string? colorHex, string? imagePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO KeycapOverrides(KeyId, ColorHex, ImagePath) VALUES ($k, $c, $i)
ON CONFLICT(KeyId) DO UPDATE SET ColorHex=excluded.ColorHex, ImagePath=excluded.ImagePath";
        cmd.Parameters.AddWithValue("$k", keyId);
        cmd.Parameters.AddWithValue("$c", (object?)colorHex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$i", (object?)imagePath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void ClearKeycapOverride(int keyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM KeycapOverrides WHERE KeyId=$k";
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
