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
        return Path.Combine(dir, "everest.db");
    }

    private void EnsureSchema()
    {
        using (var cmd = _conn.CreateCommand())
        {
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

    /// <summary>
    /// One-time migration (2026-07-25): KeycapOverrides used to be keyed by KeyId only
    /// (one row per key, shared by every profile). Detects the old single-column PK via
    /// PRAGMA table_info and, if found, copies every existing override into all 5 fixed
    /// profile slots (see EverestSdkNative.FW_NUM_PROFILE) — so nobody loses their per-key
    /// customization on upgrade, every profile just starts from what was there before and
    /// can diverge from there — then replaces the table with the new (Profile, KeyId)
    /// composite key. No-ops on every call after the first (column already present).
    /// </summary>
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
    // Per-profile (2026-07-25, migrated from device-wide) — see MainWindow.KeycapAppearance.cs.
    // KeyId = LED index (same identity as _evKeyVisuals).

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

    /// <summary>Deletes a profile completely: its keys, keycap overrides and EVERY
    /// Settings row it owns — name/exists/launchExe (<c>profile.{N}.*</c>), lighting
    /// (<c>rgb.p{N}.*</c>, <c>custom.p{N}.*</c>), keyboard settings
    /// (<c>settings.p{N}.*</c>), Display Dial (<c>dial.p{N}.*</c>) and the numpad
    /// display keys (<c>ndk.{N}.*</c>).
    ///
    /// CHANGED 2026-08-21 (user report: "non vengono eliminati tutti i profili, ma
    /// vengono lasciate le entry"): this used to blank the name/exists keys with
    /// <c>SetSetting(..., "")</c> and leave every other per-profile namespace behind,
    /// so a deleted profile kept a pile of empty/stale rows in the Settings table that
    /// silently became the starting point the next time that slot was reused. Deleting
    /// a profile now really deletes it.</summary>
    public void ClearProfile(int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE Profile=$p; DELETE FROM KeycapOverrides WHERE Profile=$p;";
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
        DeleteSettingsWithPrefix(
            $"profile.{profile}.", $"rgb.p{profile}.", $"custom.p{profile}.",
            $"settings.p{profile}.", $"dial.p{profile}.", $"ndk.{profile}.");
    }

    /// <summary>Deletes only this profile's key bindings — unlike <see cref="ClearProfile"/>,
    /// keeps the profile's name. Used by "Restore defaults" (resets content, not identity).
    /// RGB lighting/Settings/Display Dial/keycap appearance are per-profile (2026-07-22/25)
    /// and stay untouched HERE on purpose — this resets the key bindings, not the profile's
    /// look. Deleting the profile outright does remove them (see <see cref="ClearProfile"/>,
    /// which since 2026-08-21 deletes every namespace the slot owns).</summary>
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
    private void ClearNdkSettings(int profile) => DeleteSettingsWithPrefix($"ndk.{profile}.");

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

    /// <summary>Wipes every profile, key binding, setting and keycap override — used by the
    /// app-wide "Restore all defaults" (Settings tab) and by the Everest hardware factory
    /// reset, not by the per-device "Restore defaults" above (that one keeps the current
    /// profile). Returns how many rows each table lost, so callers can LOG the wipe instead
    /// of assuming it happened: "the profiles are still there after a reset" is otherwise
    /// indistinguishable from "the reset never ran" in a bug report (user report
    /// 2026-08-21). One statement per command, each with its own count.</summary>
    public (int Keys, int Settings, int KeycapOverrides) ResetAllData()
    {
        int Del(string table)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM " + table;
            return cmd.ExecuteNonQuery();
        }
        return (Del("Keys"), Del("Settings"), Del("KeycapOverrides"));
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
