using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistence for the Everest 60 module. Like Makalu, this keyboard has no
/// firmware profile concept — remap goes straight to firmware via the vendor
/// SDK (Everest360_USB.dll) with no onboard profile slots, and lighting is
/// raw HID (see architectural note in _PROJECT_MAP.md). A "profile" here is
/// purely a K2-side slot (1..5): switching means re-sending the stored
/// lighting state and re-writing the stored 64-key binding table live to
/// firmware — it does NOT call SaveFlash automatically (that stays behind
/// the existing manual "Save" button, to avoid wearing the keyboard's flash
/// on every switch).
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
CREATE TABLE IF NOT EXISTS KeyBindings (
    Profile      INTEGER NOT NULL,
    LedIndex     INTEGER NOT NULL,
    Mode         TEXT NOT NULL,
    Value        INTEGER NOT NULL,
    ModifierMask INTEGER NOT NULL DEFAULT 0,
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

    // ---------- key bindings ----------

    public List<Ev60KeyBindingRecord> LoadKeyBindings(int slot)
    {
        var result = new List<Ev60KeyBindingRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT LedIndex, Mode, Value, ModifierMask
                            FROM KeyBindings WHERE Profile=$p ORDER BY LedIndex";
        cmd.Parameters.AddWithValue("$p", slot);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new Ev60KeyBindingRecord(
                r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        return result;
    }

    public void SaveKeyBinding(int slot, int ledIndex, string mode, int value, int modifierMask)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO KeyBindings(Profile, LedIndex, Mode, Value, ModifierMask)
VALUES ($p, $l, $m, $v, $mm)
ON CONFLICT(Profile, LedIndex) DO UPDATE SET
  Mode=excluded.Mode, Value=excluded.Value, ModifierMask=excluded.ModifierMask";
        cmd.Parameters.AddWithValue("$p",  slot);
        cmd.Parameters.AddWithValue("$l",  ledIndex);
        cmd.Parameters.AddWithValue("$m",  mode);
        cmd.Parameters.AddWithValue("$v",  value);
        cmd.Parameters.AddWithValue("$mm", modifierMask);
        cmd.ExecuteNonQuery();
    }

    public void RemoveKeyBinding(int slot, int ledIndex)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM KeyBindings WHERE Profile=$p AND LedIndex=$l";
        cmd.Parameters.AddWithValue("$p", slot);
        cmd.Parameters.AddWithValue("$l", ledIndex);
        cmd.ExecuteNonQuery();
    }

    private void ClearKeyBindings(int slot)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM KeyBindings WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", slot);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes every saved setting of a profile (lighting/key bindings/name).</summary>
    public void ClearProfile(int slot)
    {
        ClearKeyBindings(slot);
        SetSetting($"profile.{slot}.lighting", "");
        SetSetting($"profile.{slot}.name", "");
    }

    public void Dispose() => _conn.Dispose();
}

public sealed record Ev60LightingRecord(
    int Effect, int Color1, int Color2, int SpeedPct, int DirIndex, bool Rainbow,
    double Brightness, int SideColor, double CustomBrightness,
    string ActiveMode, Dictionary<int, int> CustomKeyColors);

/// <summary>Mode: "key"|"fn"|"shortcut"|"media" (same vocabulary as Everest60KeyBindingPanel.Modes).
/// Value = target DllKeyId (key/fn/shortcut) or media code; ModifierMask only used by "shortcut".</summary>
public sealed record Ev60KeyBindingRecord(int LedIndex, string Mode, int Value, int ModifierMask);
