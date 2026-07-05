using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Persistenza azioni/immagini dei tasti DisplayPad nel processo K2.App (x86).
/// Usa lo stesso schema di <c>K2.DisplayPad.Services.StateStore</c> ma su un
/// database separato (<c>K2.DisplayPad.AppSide/state.db</c>) per evitare lock
/// concorrenti tra i due processi.
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

    public void ClearProfile(int deviceId, int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Buttons WHERE DeviceId=$d AND Profile=$p";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
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

    /// <summary>Restituisce gli slot profilo che hanno almeno un tasto salvato per il device.</summary>
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
