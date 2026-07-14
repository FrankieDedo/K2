using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistence for the Everest 60 module. Like Makalu, this keyboard has no
/// firmware profile concept for lighting — that stays raw HID (see
/// architectural note in _PROJECT_MAP.md). A "profile" here is purely a
/// K2-side slot (1..5): switching re-sends the stored lighting state and
/// reloads the stored key bindings into memory. Key Binding itself
/// (2026-07-14, second pass) is no longer a firmware remap — it went through
/// the same K2Action/IActionHost/ButtonActionEngine pipeline as Everest Max/
/// MacroPad/DisplayPad (same ButtonActionDialog, same action catalog), so
/// switching profile needs no firmware write at all for keys: only lighting
/// still round-trips to the device.
/// </summary>
public sealed class Everest60Store : IDisposable
{
    private readonly SqliteConnection _conn;

    public Everest60Store(string? dbPath = null)
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
        return Path.Combine(dir, "everest60.db");
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Keys (
    Profile     INTEGER NOT NULL,
    LedIndex    INTEGER NOT NULL,
    Label       TEXT,
    ActionType  TEXT,
    ActionValue TEXT,
    PRIMARY KEY (Profile, LedIndex)
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
    // Global (not per-profile) like the rest of Keycap Appearance — see MainWindow.Everest60.cs.
    // KeyId = LED index (same identity as _ev60KeyVisuals); Esc is LED index 0.

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

    // ---------- settings (generic k/v) ----------

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

    // ---------- lighting ----------

    public Ev60LightingRecord? LoadLighting(int slot)
    {
        var json = GetSetting($"profile.{slot}.lighting");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Ev60LightingRecord>(json); }
        catch { return null; }
    }

    public void SaveLighting(int slot, Ev60LightingRecord r) =>
        SetSetting($"profile.{slot}.lighting", JsonSerializer.Serialize(r));

    // ---------- keys (K2Action — same shape as EverestStore's Keys table) ----------

    public IReadOnlyList<Ev60KeyRecord> LoadProfile(int profile)
    {
        var result = new List<Ev60KeyRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT LedIndex, Label, ActionType, ActionValue
                            FROM Keys WHERE Profile=$p
                            ORDER BY LedIndex";
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new Ev60KeyRecord(
                profile, r.GetInt32(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return result;
    }

    public void SaveKey(Ev60KeyRecord k)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Keys(Profile, LedIndex, Label, ActionType, ActionValue)
VALUES ($p, $l, $lb, $at, $av)
ON CONFLICT(Profile, LedIndex) DO UPDATE SET
  Label       = excluded.Label,
  ActionType  = excluded.ActionType,
  ActionValue = excluded.ActionValue";
        cmd.Parameters.AddWithValue("$p",  k.Profile);
        cmd.Parameters.AddWithValue("$l",  k.LedIndex);
        cmd.Parameters.AddWithValue("$lb", (object?)k.Label       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (object?)k.ActionType  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av", (object?)k.ActionValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every key currently configured with the given action (e.g. macro assignment lookup).</summary>
    public List<(int Profile, int LedIndex, string? Label)> GetKeysByAction(string actionType, string actionValue)
    {
        var result = new List<(int, int, string?)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT Profile, LedIndex, Label FROM Keys
                            WHERE ActionType=$t AND ActionValue=$v
                            ORDER BY Profile, LedIndex";
        cmd.Parameters.AddWithValue("$t", actionType);
        cmd.Parameters.AddWithValue("$v", actionValue);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return result;
    }

    public void RemoveKey(int profile, int ledIndex)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE Profile=$p AND LedIndex=$l";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.Parameters.AddWithValue("$l", ledIndex);
        cmd.ExecuteNonQuery();
    }

    private void ClearKeys(int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes every saved setting of a profile (lighting/keys/name).</summary>
    public void ClearProfile(int slot)
    {
        ClearKeys(slot);
        SetSetting($"profile.{slot}.lighting", "");
        SetSetting($"profile.{slot}.name", "");
    }

    /// <summary>Deletes only this profile's keys — unlike <see cref="ClearProfile"/>,
    /// keeps the profile's name. Used by "Restore defaults" (see Everest60KeyBindingPanel).</summary>
    public void ResetProfileToDefaults(int slot) => ClearKeys(slot);

    /// <summary>Wipes every profile, key, lighting state and keycap override — used
    /// by the app-wide "Restore all defaults" (Settings tab), not by the per-device reset.</summary>
    public void ResetAllData()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys; DELETE FROM Settings; DELETE FROM KeycapOverrides;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

public sealed record Ev60LightingRecord(
    int Effect, int Color1, int Color2, int SpeedPct, int DirIndex, bool Rainbow,
    double Brightness, int SideColor, double CustomBrightness,
    string ActiveMode, Dictionary<int, int> CustomKeyColors);

public sealed record Ev60KeyRecord(
    int     Profile,
    int     LedIndex,
    string? Label,
    string? ActionType,
    string? ActionValue);
