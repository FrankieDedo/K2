using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistence of DisplayPad key actions/images in the K2.App (x86) process.
/// Uses the same schema as <c>K2.DisplayPad.Services.StateStore</c> but on a
/// separate database (<c>K2.DisplayPad.AppSide/state.db</c>) to avoid
/// concurrent locking between the two processes.
/// </summary>
public sealed class DisplayPadStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public DisplayPadStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.DisplayPad.AppSide");
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "state.db");
        _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        // Migrate: if Buttons exists without PageId, recreate with new PK
        if (TableExists("Buttons") && !ColumnExists("Buttons", "PageId"))
        {
            Exec("ALTER TABLE Buttons RENAME TO Buttons_old");
            Exec(@"CREATE TABLE Buttons (
                DeviceId     INTEGER NOT NULL,
                Profile      INTEGER NOT NULL,
                PageId       INTEGER NOT NULL DEFAULT 0,
                ButtonIndex  INTEGER NOT NULL,
                ImagePath    TEXT,
                ActionType   TEXT,
                ActionValue  TEXT,
                PRIMARY KEY (DeviceId, Profile, PageId, ButtonIndex)
            )");
            Exec(@"INSERT INTO Buttons (DeviceId, Profile, PageId, ButtonIndex, ImagePath, ActionType, ActionValue)
                   SELECT DeviceId, Profile, 0, ButtonIndex, ImagePath, ActionType, ActionValue FROM Buttons_old");
            Exec("DROP TABLE Buttons_old");
        }

        Exec(@"CREATE TABLE IF NOT EXISTS Buttons (
            DeviceId     INTEGER NOT NULL,
            Profile      INTEGER NOT NULL,
            PageId       INTEGER NOT NULL DEFAULT 0,
            ButtonIndex  INTEGER NOT NULL,
            ImagePath    TEXT,
            ActionType   TEXT,
            ActionValue  TEXT,
            PRIMARY KEY (DeviceId, Profile, PageId, ButtonIndex)
        )");
        Exec(@"CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT PRIMARY KEY,
            Value TEXT
        )");
    }

    private bool TableExists(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (r.GetString(1) == column) return true;
        return false;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Saves a button on a specific page (pageId=0 = root page).</summary>
    public void SaveButton(int deviceId, int profile, int pageId, int btn,
        string? imagePath, string? actionType, string? actionValue)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Buttons(DeviceId, Profile, PageId, ButtonIndex, ImagePath, ActionType, ActionValue)
VALUES ($d, $p, $pg, $b, $img, $at, $av)
ON CONFLICT(DeviceId, Profile, PageId, ButtonIndex) DO UPDATE SET
  ImagePath=excluded.ImagePath, ActionType=excluded.ActionType, ActionValue=excluded.ActionValue";
        cmd.Parameters.AddWithValue("$d",   deviceId);
        cmd.Parameters.AddWithValue("$p",   profile);
        cmd.Parameters.AddWithValue("$pg",  pageId);
        cmd.Parameters.AddWithValue("$b",   btn);
        cmd.Parameters.AddWithValue("$img", (object?)imagePath   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at",  (object?)actionType  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av",  (object?)actionValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Backward-compat overload — saves to root page (pageId=0).</summary>
    public void SaveButton(int deviceId, int profile, int btn,
        string? imagePath, string? actionType, string? actionValue)
        => SaveButton(deviceId, profile, 0, btn, imagePath, actionType, actionValue);

    /// <summary>Loads buttons for a specific page within a profile.</summary>
    public IReadOnlyList<DpButtonRecord> LoadPage(int deviceId, int profile, int pageId)
    {
        var result = new List<DpButtonRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT PageId, ButtonIndex, ImagePath, ActionType, ActionValue
                            FROM Buttons WHERE DeviceId=$d AND Profile=$p AND PageId=$pg
                            ORDER BY ButtonIndex";
        cmd.Parameters.AddWithValue("$d",  deviceId);
        cmd.Parameters.AddWithValue("$p",  profile);
        cmd.Parameters.AddWithValue("$pg", pageId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new DpButtonRecord(deviceId, profile,
                r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return result;
    }

    /// <summary>Loads ALL buttons across all pages for a profile (used by import/upload flows).</summary>
    public IReadOnlyList<DpButtonRecord> LoadAllButtons(int deviceId, int profile)
    {
        var result = new List<DpButtonRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT PageId, ButtonIndex, ImagePath, ActionType, ActionValue
                            FROM Buttons WHERE DeviceId=$d AND Profile=$p
                            ORDER BY PageId, ButtonIndex";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new DpButtonRecord(deviceId, profile,
                r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return result;
    }

    /// <summary>Legacy alias — loads root page (pageId=0) only.</summary>
    public IReadOnlyList<DpButtonRecord> LoadProfile(int deviceId, int profile)
        => LoadPage(deviceId, profile, 0);

    /// <summary>
    /// Allocates a fresh, unused page ID for a new folder sub-page on this device/profile
    /// (0 is always the root page). Computed fresh from the current DB state — the max of
    /// any page a button already lives on, and the max "dp_folder" ActionValue pointing at
    /// a page (which may still be completely empty, e.g. right after creation) — rather than
    /// a persisted counter, so it can never collide with page IDs brought in by a BaseCamp.db
    /// import (which uses BC's own arbitrary page IDs).
    /// </summary>
    public int AllocatePageId(int deviceId, int profile)
    {
        int max = 0;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT MAX(PageId) FROM Buttons WHERE DeviceId=$d AND Profile=$p";
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$p", profile);
            if (cmd.ExecuteScalar() is long l) max = (int)l;
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT MAX(CAST(ActionValue AS INTEGER)) FROM Buttons
                                WHERE DeviceId=$d AND Profile=$p AND ActionType='dp_folder'";
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$p", profile);
            if (cmd.ExecuteScalar() is long l && l > max) max = (int)l;
        }
        return max + 1;
    }

    /// <summary>Every key currently configured with the given action (e.g. macro assignment lookup).</summary>
    public List<(int DeviceId, int Profile, int PageId, int ButtonIndex)> GetKeysByAction(string actionType, string actionValue)
    {
        var result = new List<(int, int, int, int)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT DeviceId, Profile, PageId, ButtonIndex FROM Buttons
                            WHERE ActionType=$t AND ActionValue=$v
                            ORDER BY DeviceId, Profile, PageId, ButtonIndex";
        cmd.Parameters.AddWithValue("$t", actionType);
        cmd.Parameters.AddWithValue("$v", actionValue);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return result;
    }

    public void ClearProfile(int deviceId, int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Buttons WHERE DeviceId=$d AND Profile=$p";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Wipes every device's buttons/settings (profiles, pages, fullscreen images,
    /// folder names, rotation) — used by the app-wide "Restore all defaults" (Settings tab).
    /// Per-device "Restore defaults" instead reuses <see cref="ClearProfile"/> directly.</summary>
    public void ResetAllData()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Buttons; DELETE FROM Settings;";
        cmd.ExecuteNonQuery();
    }

    // ---- Settings ----

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
        cmd.CommandText = @"INSERT INTO Settings(Key, Value) VALUES ($k, $v)
ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the profile slots that have at least one saved key for the device.</summary>
    public List<int> GetExistingProfiles(int deviceId)
    {
        var result = new List<int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Profile FROM Buttons WHERE DeviceId=$d ORDER BY Profile";
        cmd.Parameters.AddWithValue("$d", deviceId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetInt32(0));
        return result;
    }

    public int GetCurrentProfile(int deviceId, int fallback = 1)
    {
        var s = GetSetting($"device.{deviceId}.currentProfile");
        return int.TryParse(s, out var v) && v >= 1 && v <= 5 ? v : fallback;
    }

    public void SetCurrentProfile(int deviceId, int profile) =>
        SetSetting($"device.{deviceId}.currentProfile", profile.ToString());

    public int GetRotation(int deviceId)
    {
        var s = GetSetting($"device.{deviceId}.rotation");
        return s switch { "90" => 90, "270" => 270, _ => 0 };
    }

    public void SetRotation(int deviceId, int rotation) =>
        SetSetting($"device.{deviceId}.rotation", rotation.ToString());

    // ---------- profile names ----------

    public string? GetProfileName(int deviceId, int slot)
    {
        var v = GetSetting($"profile.{deviceId}.{slot}.name");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public void SetProfileName(int deviceId, int slot, string name) =>
        SetSetting($"profile.{deviceId}.{slot}.name", name.Trim());

    // ---------- folder (sub-page) names ----------

    public string? GetFolderName(int pageId)
    {
        var v = GetSetting($"folder.{pageId}.name");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public void SetFolderName(int pageId, string name) =>
        SetSetting($"folder.{pageId}.name", name.Trim());

    /// <summary>Renames an existing page — thin, named wrapper over <see cref="SetFolderName"/>
    /// used by the "Page" action type's rename flow in <c>ButtonActionDialog</c>.</summary>
    public void RenamePage(int pageId, string name) => SetFolderName(pageId, name);

    /// <summary>Clears ActionType/ActionValue (keeping ImagePath — only the navigation
    /// target goes away, not the tile's picture) on every key across this device+profile
    /// currently configured with the given action. Used by <see cref="DeletePage"/> so a
    /// deleted page's "dp_folder" references don't turn into dead links.</summary>
    private void ClearActionEverywhere(int deviceId, int profile, string actionType, string actionValue)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"UPDATE Buttons SET ActionType=NULL, ActionValue=NULL
                            WHERE DeviceId=$d AND Profile=$p AND ActionType=$t AND ActionValue=$v";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.Parameters.AddWithValue("$t", actionType);
        cmd.Parameters.AddWithValue("$v", actionValue);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes a folder sub-page entirely: its own button rows (the page's contents), its
    /// stored name, and clears (without touching the tile picture of) every key elsewhere on
    /// this device+profile that navigated into it — otherwise that key would become a dead
    /// link. Pages nested INSIDE the deleted page (a "dp_folder" key living on one of its
    /// buttons, pointing at yet another page) are NOT recursively deleted — they become
    /// unreachable but their rows stay in the DB; out of scope for a first cut of deletion.
    /// </summary>
    public void DeletePage(int deviceId, int profile, int pageId)
    {
        ClearActionEverywhere(deviceId, profile, "dp_folder", pageId.ToString());

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM Buttons WHERE DeviceId=$d AND Profile=$p AND PageId=$pg";
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$p", profile);
            cmd.Parameters.AddWithValue("$pg", pageId);
            cmd.ExecuteNonQuery();
        }
        SetSetting($"folder.{pageId}.name", "");
    }

    /// <summary>All DisplayPad sub-pages reachable from this device+profile via a
    /// "dp_folder" key (i.e. anything <see cref="AllocatePageId"/> could collide with) —
    /// used to populate the "assign existing page" picker in <c>ButtonActionDialog</c>.
    /// Falls back to "Page {id}" for a page whose name was never set.</summary>
    public List<(int PageId, string Name)> ListPages(int deviceId, int profile)
    {
        var ids = new List<int>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT DISTINCT ActionValue FROM Buttons
                                WHERE DeviceId=$d AND Profile=$p AND ActionType='dp_folder'";
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$p", profile);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0) && int.TryParse(r.GetString(0), out int pageId))
                    ids.Add(pageId);
        }

        var result = new List<(int, string)>();
        foreach (int pageId in ids)
            result.Add((pageId, GetFolderName(pageId) ?? $"Page {pageId}"));
        result.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    // ---------- fullscreen image (whole 2×6 panel, per device+profile+page) ----------

    /// <summary>Fullscreen image assigned to a page, if any. Null if none or the file no
    /// longer exists on disk (caller treats that the same as "not assigned").</summary>
    public (string Path, int Rotation)? GetFullscreenImage(int deviceId, int profile, int pageId)
    {
        string key = $"dp.fullscreen.{deviceId}.{profile}.{pageId}";
        var path = GetSetting($"{key}.path");
        if (string.IsNullOrEmpty(path)) return null;
        int rotation = int.TryParse(GetSetting($"{key}.rot"), out var r) ? r : 0;
        return (path, rotation);
    }

    public void SetFullscreenImage(int deviceId, int profile, int pageId, string path, int rotation)
    {
        string key = $"dp.fullscreen.{deviceId}.{profile}.{pageId}";
        SetSetting($"{key}.path", path);
        SetSetting($"{key}.rot", rotation.ToString());
    }

    public void ClearFullscreenImage(int deviceId, int profile, int pageId) =>
        SetSetting($"dp.fullscreen.{deviceId}.{profile}.{pageId}.path", "");

    public void Dispose() => _conn.Dispose();
}

public sealed record DpButtonRecord(
    int DeviceId, int Profile, int PageId, int ButtonIndex,
    string? ImagePath, string? ActionType, string? ActionValue);
