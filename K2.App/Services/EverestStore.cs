using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using K2.Core;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistence for the Everest module. For each (profile, key matrix code)
/// stores the assigned name and action.
///
/// The keyboard is single-device, so there is no deviceId; and a key's identity
/// is directly its hardware matrix code — no separate lookup table
/// mapping like for the MacroPad.
/// </summary>
public sealed class EverestStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public EverestStore(string? dbPath = null)
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
        return Path.Combine(dir, "everest.db");
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Keys (
    Profile     INTEGER NOT NULL,
    KeyMatrix   INTEGER NOT NULL,
    Label       TEXT,
    ActionType  TEXT,
    ActionValue TEXT,
    PRIMARY KEY (Profile, KeyMatrix)
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

    // ---------- per-key appearance overrides (color / custom image, incl. Esc Mountain logo) ----------
    // Global/device-wide like the rest of Keycap Appearance (not per-profile) — see
    // MainWindow.KeycapAppearance.cs. KeyId = LED index (same identity as _evKeyVisuals).

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

    // ---------- keys ----------

    /// <summary>Profile slots that are actually configured — mirrors MacroPadStore/
    /// DpStore's GetExistingProfiles, used to hide empty slots from the profile
    /// combo (the device firmware always has 5 fixed slots, but K2's UI only lists
    /// the ones actually in use, same as every other module). A slot counts as
    /// existing if it has a bound key, a custom name, or the "exists" marker set
    /// by <see cref="MarkProfileExists"/> for brand-new empty profiles — unlike
    /// MacroPad/DisplayPad, Everest's key list is a sparse ListView (not a
    /// fixed-size grid), so a dummy placeholder Keys row would show up as a
    /// visible blank row instead of being invisible filler.</summary>
    public List<int> GetExistingProfiles()
    {
        var result = new SortedSet<int>();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Profile FROM Keys WHERE ActionType IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetInt32(0));
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT Key, Value FROM Settings
                                WHERE Key LIKE 'profile.%.name' OR Key LIKE 'profile.%.exists'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                string value = r.IsDBNull(1) ? "" : r.GetString(1);
                if (string.IsNullOrEmpty(value)) continue;
                var parts = key.Split('.');
                if (parts.Length == 3 && int.TryParse(parts[1], out int slot))
                    result.Add(slot);
            }
        }

        return new List<int>(result);
    }

    /// <summary>Marks an otherwise-empty profile as "existing" so it shows up in the
    /// profile combo — see <see cref="GetExistingProfiles"/> for why this uses a
    /// Settings flag instead of a placeholder Keys row.</summary>
    public void MarkProfileExists(int profile) => SetSetting($"profile.{profile}.exists", "1");

    public IReadOnlyList<EverestKeyRecord> LoadProfile(int profile)
    {
        var result = new List<EverestKeyRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT KeyMatrix, Label, ActionType, ActionValue
                            FROM Keys WHERE Profile=$p
                            ORDER BY KeyMatrix";
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? at = r.IsDBNull(2) ? null : r.GetString(2);
            // Leftover bc:Default from an old BC import = "no custom binding": empty key.
            if (BaseCampDbImporter.IsBcDefaultAction(at)) continue;
            result.Add(new EverestKeyRecord(
                profile, r.GetInt32(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                at,
                r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return result;
    }

    public void SaveKey(EverestKeyRecord k)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Keys(Profile, KeyMatrix, Label, ActionType, ActionValue)
VALUES ($p, $k, $l, $at, $av)
ON CONFLICT(Profile, KeyMatrix) DO UPDATE SET
  Label       = excluded.Label,
  ActionType  = excluded.ActionType,
  ActionValue = excluded.ActionValue";
        cmd.Parameters.AddWithValue("$p",  k.Profile);
        cmd.Parameters.AddWithValue("$k",  k.KeyMatrix);
        cmd.Parameters.AddWithValue("$l",  (object?)k.Label       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (object?)k.ActionType  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av", (object?)k.ActionValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every key currently configured with the given action (e.g. macro assignment lookup).</summary>
    public List<(int Profile, int KeyMatrix, string? Label)> GetKeysByAction(string actionType, string actionValue)
    {
        var result = new List<(int, int, string?)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT Profile, KeyMatrix, Label FROM Keys
                            WHERE ActionType=$t AND ActionValue=$v
                            ORDER BY Profile, KeyMatrix";
        cmd.Parameters.AddWithValue("$t", actionType);
        cmd.Parameters.AddWithValue("$v", actionValue);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return result;
    }

    public void RemoveKey(int profile, int keyMatrix)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE Profile=$p AND KeyMatrix=$k";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.Parameters.AddWithValue("$k", keyMatrix);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes all actions of a profile (keys and name).</summary>
    public void ClearProfile(int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
        // Clear the saved name and "exists" marker too, so the slot disappears
        // from the profile combo (see GetExistingProfiles) until reused.
        SetSetting($"profile.{profile}.name", "");
        SetSetting($"profile.{profile}.exists", "");
        ClearNdkSettings(profile);
    }

    /// <summary>Deletes only this profile's key bindings — unlike <see cref="ClearProfile"/>,
    /// keeps the profile's name. Used by "Restore defaults" (resets content, not identity).
    /// RGB lighting/keycap appearance are device-wide (not per-profile) for the Everest Max,
    /// so they are untouched here — see the architectural note in _PROJECT_MAP.md.</summary>
    public void ResetProfileToDefaults(int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
        // Keep the slot visible in the profile combo — this clears content, not
        // identity/existence (unlike ClearProfile/delete).
        MarkProfileExists(profile);
        ClearNdkSettings(profile);
    }

    /// <summary>Clears this profile's 4 NDK (numpad display key) settings — image path and
    /// action — from local storage. Each firmware profile keeps its own 4 pictures in flash
    /// (see MainWindow.NumpadDisplayKeys.cs's UploadNdkImage doc comment), but there's no SDK
    /// call to blank an individual picture slot on the device, so the stale image stays
    /// resident on hardware until the user assigns a new one — matching the same limitation
    /// as regular keys, which are also only cleared locally here (no per-profile hardware
    /// reset call exists either).</summary>
    private void ClearNdkSettings(int profile)
    {
        for (int i = 0; i < 4; i++)
        {
            SetSetting($"ndk.{profile}.{i}.imagePath", "");
            SetSetting($"ndk.{profile}.{i}.actionType", "");
            SetSetting($"ndk.{profile}.{i}.actionValue", "");
        }
    }

    /// <summary>Wipes every profile, key binding, setting and keycap override — used by the
    /// app-wide "Restore all defaults" (Settings tab), not by the per-device reset above.</summary>
    public void ResetAllData()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys; DELETE FROM Settings; DELETE FROM KeycapOverrides;";
        cmd.ExecuteNonQuery();
    }

    // ---------- settings ----------

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

    public int GetCurrentProfile(int fallback = 1)
    {
        var s = GetSetting("currentProfile");
        return int.TryParse(s, out var v) && v >= 1 ? v : fallback;
    }

    public void SetCurrentProfile(int profile) =>
        SetSetting("currentProfile", profile.ToString());

    // ---------- profile names ----------

    public string? GetProfileName(int slot)
    {
        var v = GetSetting($"profile.{slot}.name");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public void SetProfileName(int slot, string name) =>
        SetSetting($"profile.{slot}.name", name.Trim());

    // ---------- wMatrix → matrixId map (keyboard remapping) ----------

    /// <summary>
    /// Persisted <c>wMatrix SDK → matrixId layout</c> map.
    /// Allows translating the codes reported by the KEY_CALLBACK callback
    /// into the MatrixIds used by the visual layout.
    /// </summary>
    public Dictionary<int, int> GetKeyMap()
    {
        var json = GetSetting("keyboard.keymap");
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, int>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, int>>(json)
                   ?? new Dictionary<int, int>();
        }
        catch { return new Dictionary<int, int>(); }
    }

    public void SetKeyMap(IReadOnlyDictionary<int, int> map) =>
        SetSetting("keyboard.keymap", JsonSerializer.Serialize(map));

    public void Dispose() => _conn.Dispose();
}

public sealed record EverestKeyRecord(
    int     Profile,
    int     KeyMatrix,
    string? Label,
    string? ActionType,
    string? ActionValue);

/// <summary>Per-key appearance override: a custom keycap color and/or a custom image
/// (replacing the legend, incl. the fixed Esc "Mountain logo" sentinel path) — see
/// MainWindow.KeycapAppearance.cs's KeycapCustomizeDialog integration.</summary>
public sealed record KeycapOverrideRecord(int KeyId, string? ColorHex, string? ImagePath);
