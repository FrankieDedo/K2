using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Stato persistente del modulo MacroPad. Per ogni (deviceId, profilo, tasto)
/// memorizza l'azione assegnata; memorizza inoltre, per ciascun device, la
/// mappa matrice-hardware -> indice tasto e impostazioni varie.
///
/// Stesso schema del <c>StateStore</c> del DisplayPad, senza i campi specifici
/// del DisplayPad (immagine, rotazione): i tasti del MacroPad non hanno display.
/// </summary>
public sealed class MacroPadStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public MacroPadStore(string? dbPath = null)
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
        return Path.Combine(dir, "macropad.db");
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Keys (
    DeviceId    INTEGER NOT NULL,
    Profile     INTEGER NOT NULL,
    KeyIndex    INTEGER NOT NULL,
    ActionType  TEXT,
    ActionValue TEXT,
    PRIMARY KEY (DeviceId, Profile, KeyIndex)
);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);";
        cmd.ExecuteNonQuery();
    }

    // ---------- azioni dei tasti ----------

    public IReadOnlyList<MacroKeyRecord> LoadProfile(int deviceId, int profile)
    {
        var result = new List<MacroKeyRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT KeyIndex, ActionType, ActionValue
                            FROM Keys
                            WHERE DeviceId=$d AND Profile=$p
                            ORDER BY KeyIndex";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new MacroKeyRecord(
                deviceId, profile, r.GetInt32(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2)));
        return result;
    }

    public void SaveKey(MacroKeyRecord k)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Keys(DeviceId, Profile, KeyIndex, ActionType, ActionValue)
VALUES ($d, $p, $k, $at, $av)
ON CONFLICT(DeviceId, Profile, KeyIndex) DO UPDATE SET
  ActionType  = excluded.ActionType,
  ActionValue = excluded.ActionValue";
        cmd.Parameters.AddWithValue("$d",  k.DeviceId);
        cmd.Parameters.AddWithValue("$p",  k.Profile);
        cmd.Parameters.AddWithValue("$k",  k.KeyIndex);
        cmd.Parameters.AddWithValue("$at", (object?)k.ActionType  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av", (object?)k.ActionValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Cancella tutte le azioni di un profilo.</summary>
    public void ClearProfile(int deviceId, int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Keys WHERE DeviceId=$d AND Profile=$p";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
    }

    // ---------- impostazioni generiche ----------

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

    // ---------- profilo corrente per device ----------

    /// <summary>Restituisce gli slot profilo che hanno almeno un tasto salvato per il device.</summary>
    public List<int> GetExistingProfiles(int deviceId)
    {
        var result = new List<int>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Profile FROM Keys WHERE DeviceId=$d ORDER BY Profile";
        cmd.Parameters.AddWithValue("$d", deviceId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetInt32(0));
        return result;
    }

    public int GetCurrentProfile(int deviceId, int fallback = 1)
    {
        var s = GetSetting($"device.{deviceId}.currentProfile");
        return int.TryParse(s, out var v) && v >= 1 ? v : fallback;
    }

    public void SetCurrentProfile(int deviceId, int profile) =>
        SetSetting($"device.{deviceId}.currentProfile", profile.ToString());

    // ---------- profile names ----------

    public string? GetProfileName(int deviceId, int slot)
    {
        var v = GetSetting($"profile.{deviceId}.{slot}.name");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public void SetProfileName(int deviceId, int slot, string name) =>
        SetSetting($"profile.{deviceId}.{slot}.name", name.Trim());

    // ---------- mappa matrice-hardware -> indice tasto ----------

    /// <summary>Mappa <c>matrice -> indice tasto</c> salvata per il device.</summary>
    public Dictionary<int, int> GetKeyMap(int deviceId)
    {
        var json = GetSetting($"device.{deviceId}.keymap");
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, int>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, int>>(json)
                   ?? new Dictionary<int, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, int>();
        }
    }

    public void SetKeyMap(int deviceId, IReadOnlyDictionary<int, int> map) =>
        SetSetting($"device.{deviceId}.keymap", JsonSerializer.Serialize(map));

    public void Dispose() => _conn.Dispose();
}

public sealed record MacroKeyRecord(
    int     DeviceId,
    int     Profile,
    int     KeyIndex,
    string? ActionType,
    string? ActionValue);
