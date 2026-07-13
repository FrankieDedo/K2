using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistence for the Makalu module. The mouse has no firmware profile concept
/// (raw HID, no SwitchProfile-equivalent — see architectural note in
/// _PROJECT_MAP.md): a "profile" here is purely a K2-side slot (1..5, same
/// count as every other device) whose saved lighting/DPI/remap/settings are
/// re-sent to the device via the existing HID write calls whenever the slot
/// is selected. Same schema shape as <see cref="EverestStore"/>: a generic
/// Settings k/v table (JSON blobs for the composite state) plus one typed
/// table (Remap) for the one piece of state that's naturally a list of rows.
/// </summary>
public sealed class MakaluStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public MakaluStore(string? dbPath = null)
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
        return Path.Combine(dir, "makalu.db");
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Remap (
    Profile      INTEGER NOT NULL,
    ButtonIndex  INTEGER NOT NULL,
    FunctionName TEXT,
    PRIMARY KEY (Profile, ButtonIndex)
);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);";
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

    public MakaluLightingRecord? LoadLighting(int slot)
    {
        var json = GetSetting($"profile.{slot}.lighting");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MakaluLightingRecord>(json); }
        catch { return null; }
    }

    public void SaveLighting(int slot, MakaluLightingRecord r) =>
        SetSetting($"profile.{slot}.lighting", JsonSerializer.Serialize(r));

    // ---------- DPI ----------

    public MakaluDpiRecord? LoadDpi(int slot)
    {
        var json = GetSetting($"profile.{slot}.dpi");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MakaluDpiRecord>(json); }
        catch { return null; }
    }

    public void SaveDpi(int slot, MakaluDpiRecord r) =>
        SetSetting($"profile.{slot}.dpi", JsonSerializer.Serialize(r));

    // ---------- device settings (polling/debounce/angle/lift-off) ----------

    public MakaluDeviceSettingsRecord? LoadSettings(int slot)
    {
        var json = GetSetting($"profile.{slot}.settings");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MakaluDeviceSettingsRecord>(json); }
        catch { return null; }
    }

    public void SaveSettings(int slot, MakaluDeviceSettingsRecord r) =>
        SetSetting($"profile.{slot}.settings", JsonSerializer.Serialize(r));

    // ---------- button remap ----------

    /// <summary>Button index (1-based) -> function key, e.g. "left", "dpi+", "sniper:800".</summary>
    public Dictionary<int, string> LoadRemap(int slot)
    {
        var result = new Dictionary<int, string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT ButtonIndex, FunctionName FROM Remap WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", slot);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(1)) result[r.GetInt32(0)] = r.GetString(1);
        return result;
    }

    public void SaveRemapButton(int slot, int buttonIndex, string functionName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Remap(Profile, ButtonIndex, FunctionName) VALUES ($p, $b, $f)
ON CONFLICT(Profile, ButtonIndex) DO UPDATE SET FunctionName=excluded.FunctionName";
        cmd.Parameters.AddWithValue("$p", slot);
        cmd.Parameters.AddWithValue("$b", buttonIndex);
        cmd.Parameters.AddWithValue("$f", functionName);
        cmd.ExecuteNonQuery();
    }

    private void ClearRemap(int slot)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Remap WHERE Profile=$p";
        cmd.Parameters.AddWithValue("$p", slot);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes every saved setting of a profile (lighting/DPI/remap/settings/name).</summary>
    public void ClearProfile(int slot)
    {
        ClearRemap(slot);
        SetSetting($"profile.{slot}.lighting", "");
        SetSetting($"profile.{slot}.dpi", "");
        SetSetting($"profile.{slot}.settings", "");
        SetSetting($"profile.{slot}.name", "");
    }

    public void Dispose() => _conn.Dispose();
}

public sealed record MakaluLightingRecord(
    int Effect, int Color1, int Color2, int SpeedIndex, int DirIndex,
    double Brightness, bool CustomActive, int[] CustomColors);

public sealed record MakaluDpiRecord(int[] Levels, int Active);

public sealed record MakaluDeviceSettingsRecord(
    int PollingHz, int DebounceMs, bool AngleSnapping, bool LiftOffHigh);
