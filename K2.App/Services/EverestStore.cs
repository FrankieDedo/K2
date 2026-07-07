using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.App");
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
);";
        cmd.ExecuteNonQuery();
    }

    // ---------- keys ----------

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
            result.Add(new EverestKeyRecord(
                profile, r.GetInt32(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
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
        // Clear the saved name too
        SetSetting($"profile.{profile}.name", "");
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
