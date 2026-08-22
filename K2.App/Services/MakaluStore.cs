using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using K2.Core;
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
        PurgeEmptyProfileSettings();
    }

    /// <summary>One-time tidy-up (2026-08-21): before profile deletion started really
    /// deleting its rows (see the ClearProfile/DeleteProfile change of the same date), a
    /// deleted profile left its <c>profile.*</c> keys behind as empty strings — an empty
    /// value already means "not set" everywhere they are read, so the rows were pure
    /// clutter that made a wiped store look like it still had profiles in it. Drops them
    /// on open; no-ops from then on.</summary>
    private void PurgeEmptyProfileSettings()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Settings WHERE Value = '' AND substr(Key, 1, 8) = 'profile.'";
        cmd.ExecuteNonQuery();
    }

    public static string DefaultDbPath()
    {
        var dir = K2Paths.For("K2.App");
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

    /// <summary>Profile slots that are actually configured — mirrors EverestStore's
    /// GetExistingProfiles, used so imports can find a free slot instead of overwriting
    /// whatever profile already occupies the source's slot number. A slot counts as
    /// existing if it has a remapped button, a custom name, saved lighting/DPI/device
    /// settings, or the "exists" marker set by <see cref="MarkProfileExists"/> for
    /// brand-new empty profiles.</summary>
    public List<int> GetExistingProfiles()
    {
        var result = new SortedSet<int>();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Profile FROM Remap";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetInt32(0));
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT Key, Value FROM Settings
                                WHERE Key LIKE 'profile.%.name' OR Key LIKE 'profile.%.exists'
                                   OR Key LIKE 'profile.%.lighting' OR Key LIKE 'profile.%.dpi'
                                   OR Key LIKE 'profile.%.settings'";
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
    /// profile combo / counts as occupied for import slot-picking purposes.</summary>
    public void MarkProfileExists(int profile) => SetSetting($"profile.{profile}.exists", "1");

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
        // Every per-profile row this store owns lives under profile.{slot}. — name,
        // lighting, dpi, settings, launchExe and the "exists" marker that keeps the slot
        // in the profile list (see GetExistingProfiles). CHANGED 2026-08-21: these used
        // to be blanked with an empty string, which left the deleted profile's rows in
        // the Settings table for good (same fix as EverestStore.ClearProfile).
        DeleteSettingsWithPrefix($"profile.{slot}.");
    }

    /// <summary>Deletes every Settings row whose Key starts with one of
    /// <paramref name="prefixes"/> — the per-profile namespaces are all
    /// <c>&lt;something&gt;.{slot}.</c>-shaped, so an exact prefix match (no LIKE/GLOB
    /// wildcards to escape) is enough. Used by profile deletion, which must remove the
    /// rows rather than blank them (see <see cref="ClearProfile"/>).</summary>
    public void DeleteSettingsWithPrefix(params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Settings WHERE substr(Key, 1, length($p)) = $p";
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every Settings row whose Key starts with <paramref name="prefix"/>, keyed by what
    /// FOLLOWS the prefix (<c>settings.p3.game_mode</c> under prefix <c>settings.p3.</c>
    /// comes back as <c>game_mode</c>). Exact prefix match, no LIKE/GLOB wildcards to
    /// escape — same shape as <see cref="DeleteSettingsWithPrefix"/>.
    /// <para>Added 2026-08-22 for the profile exporters: a K2-format export carries the
    /// whole per-profile settings namespace verbatim (see <c>K2ProfileSettingsXml</c>),
    /// so panels can gain settings without every exporter needing a new hand-written
    /// field.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> GetSettingsWithPrefix(string prefix)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Settings WHERE substr(Key, 1, length($p)) = $p";
        cmd.Parameters.AddWithValue("$p", prefix);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string key = r.GetString(0);
            result[key[prefix.Length..]] = r.IsDBNull(1) ? "" : r.GetString(1);
        }
        return result;
    }


    /// <summary>Deletes only this profile's button remap — unlike <see cref="ClearProfile"/>,
    /// keeps the profile's name (lighting/DPI/settings are reset separately, with explicit
    /// default records, by MakaluRgbSettingsPanel.RestoreDefaults). Used by "Restore defaults".</summary>
    public void ResetKeyRemap(int slot) => ClearRemap(slot);

    /// <summary>Wipes every profile's remap/lighting/DPI/settings — used by the app-wide
    /// "Restore all defaults" (Settings tab), not by the per-device reset above.</summary>
    public void ResetAllData()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Remap; DELETE FROM Settings;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

public sealed record MakaluLightingRecord(
    int Effect, int Color1, int Color2, int SpeedIndex, int DirIndex,
    double Brightness, bool CustomActive, int[] CustomColors);

public sealed record MakaluDpiRecord(int[] Levels, int Active);

/// <summary>LiftOffHigh is only meaningful when LiftOffCustom is false (kept for
/// back-compat with settings saved before "Custom" existed). SurfaceA/B are the
/// 2 opaque calibration bytes from MakaluService.LodGetCalibration, re-sent via
/// LodSetSurface on profile reload/reconnect instead of re-running the whole
/// calibration flow. Sensitivity/ClickSpeed (2026-07-29) use the SAME 0-11 scale
/// Base Camp's own DB stores (confirmed via decompiled BaseCamp.Data.MakaluSetting's
/// constructor defaults, Sensitivity=10/ClickSpeed=0) — these are Windows OS-level
/// mouse settings, NOT firmware HID (confirmed via decompile: Base Camp's own
/// SystemParametersInfo/SPI_SETMOUSESPEED/SPI_SETDOUBLECLICKTIME strings sit right
/// next to "Sensitivity"/"ClickSpeed" in BaseCamp.UI.exe — see MakaluOsMouseSettings
/// for where K2 applies them), so unlike every other field here they carry no risk
/// to the physical device if the exact 0-11→OS-value curve turns out to not match
/// Base Camp's own (unverified — see MakaluOsMouseSettings' doc comment).</summary>
public sealed record MakaluDeviceSettingsRecord(
    int PollingHz, int DebounceMs, bool AngleSnapping, bool LiftOffHigh,
    bool LiftOffCustom = false, byte? SurfaceA = null, byte? SurfaceB = null,
    int Sensitivity = 10, int ClickSpeed = 0);
