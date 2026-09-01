using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using K2.Core;
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
        return Path.Combine(dir, "everest60.db");
    }

    private void EnsureSchema()
    {
        using (var cmd = _conn.CreateCommand())
        {
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
    /// for the full rationale. Ev60 uses the same 5 fixed profile slots (Ev60ProfileCount,
    /// MainWindow.Everest60.cs).</summary>
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

    // ---------- per-key appearance overrides (color / custom image, incl. Esc Mountain logo) ----------
    // Per-profile (2026-07-25, migrated from device-wide) — see MainWindow.Everest60.cs.
    // KeyId = LED index (same identity as _ev60KeyVisuals); Esc is LED index 0.

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
    /// existing if it has a key binding, a custom name, saved lighting, or the "exists"
    /// marker set by <see cref="MarkProfileExists"/> for brand-new empty profiles.</summary>
    public List<int> GetExistingProfiles()
    {
        var result = new SortedSet<int>();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Profile FROM Keys";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetInt32(0));
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT Key, Value FROM Settings
                                WHERE Key LIKE 'profile.%.name' OR Key LIKE 'profile.%.exists'
                                   OR Key LIKE 'profile.%.lighting'";
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

    public Ev60LightingRecord? LoadLighting(int slot)
    {
        var json = GetSetting($"profile.{slot}.lighting");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Ev60LightingRecord>(json); }
        catch { return null; }
    }

    public void SaveLighting(int slot, Ev60LightingRecord r) =>
        SetSetting($"profile.{slot}.lighting", JsonSerializer.Serialize(r));

    /// <summary>The one lighting record every profile shares while the Key Lighting
    /// section's "sync across profiles" flag (<c>lighting.sync</c>) is on — K2-side only,
    /// this board has no firmware sync command. Added 2026-08-28.</summary>
    public Ev60LightingRecord? LoadSharedLighting()
    {
        var json = GetSetting("lighting.shared");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Ev60LightingRecord>(json); }
        catch { return null; }
    }

    public void SaveSharedLighting(Ev60LightingRecord r) =>
        SetSetting("lighting.shared", JsonSerializer.Serialize(r));

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
        {
            string? at = r.IsDBNull(2) ? null : r.GetString(2);
            // Leftover bc:Default from an old BC import = "no custom binding": empty key.
            if (BaseCampDbImporter.IsBcDefaultAction(at)) continue;
            result.Add(new Ev60KeyRecord(
                profile, r.GetInt32(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                at,
                r.IsDBNull(3) ? null : r.GetString(3)));
        }
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

    /// <summary>Deletes a profile completely: its keys, keycap overrides and EVERY
    /// Settings row it owns — <c>profile.{N}.*</c> (name/lighting/exists/launchExe) and
    /// <c>settings.p{N}.*</c> (Game Mode / Core LED). CHANGED 2026-08-21 for the same
    /// reason as <see cref="EverestStore.ClearProfile"/>: blanking those keys with an
    /// empty string left a deleted profile's rows behind forever.</summary>
    public void ClearProfile(int slot)
    {
        ClearKeys(slot);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM KeycapOverrides WHERE Profile=$p";
            cmd.Parameters.AddWithValue("$p", slot);
            cmd.ExecuteNonQuery();
        }
        DeleteSettingsWithPrefix($"profile.{slot}.", $"settings.p{slot}.");
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
    double Brightness, double CustomBrightness,
    string ActiveMode, Dictionary<int, int> CustomKeyColors,
    bool ColorDouble = false,
    // Border-ring per-LED paint (wire index 0-43, Everest60Protocol.SideLedIndex) —
    // added 2026-07-24 alongside the Custom Lighting port from Everest Max. Nullable
    // (not just empty-default) so old JSON blobs saved before this field existed
    // deserialize cleanly via System.Text.Json's default-on-missing-property behavior.
    // NB: the old standalone uniform-color "SideColor" field (int, positioned right
    // after Brightness) was removed the same session — that section's superseded by
    // painting every border square the same color via "Fill all" under Custom.
    Dictionary<int, int>? CustomSideColors = null,
    // Numpad-ring per-LED paint (wire index 0-21, Everest60Protocol.NumpadSideLedIndex)
    // — added 2026-07-24 once the numpad ring's own addresses (170-191) were
    // confirmed via USBPcap capture. Same nullable-for-old-JSON reasoning as
    // CustomSideColors above.
    Dictionary<int, int>? CustomNumpadRingColors = null);

public sealed record Ev60KeyRecord(
    int     Profile,
    int     LedIndex,
    string? Label,
    string? ActionType,
    string? ActionValue);
