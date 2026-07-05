using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace K2.DisplayPad.Services;

/// <summary>
/// Persistent app state: for each (deviceId, profile, buttonIndex)
/// stores the uploaded image and any action to run on press.
/// Also stores the currently selected profile for each device.
/// </summary>
public sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public StateStore(string? dbPath = null)
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
            "K2.DisplayPad");
        return Path.Combine(dir, "state.db");
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Buttons (
    DeviceId     INTEGER NOT NULL,
    Profile      INTEGER NOT NULL,
    ButtonIndex  INTEGER NOT NULL,
    ImagePath    TEXT,
    ActionType   TEXT,
    ActionValue  TEXT,
    PRIMARY KEY (DeviceId, Profile, ButtonIndex)
);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);";
        cmd.ExecuteNonQuery();
    }

    // ---------- buttons ----------

    public ButtonRecord? LoadButton(int deviceId, int profile, int btn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT ImagePath, ActionType, ActionValue
                            FROM Buttons
                            WHERE DeviceId=$d AND Profile=$p AND ButtonIndex=$b";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.Parameters.AddWithValue("$b", btn);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ButtonRecord(
            deviceId, profile, btn,
            ImagePath:   r.IsDBNull(0) ? null : r.GetString(0),
            ActionType:  r.IsDBNull(1) ? null : r.GetString(1),
            ActionValue: r.IsDBNull(2) ? null : r.GetString(2));
    }

    public IReadOnlyList<ButtonRecord> LoadProfile(int deviceId, int profile)
    {
        var result = new List<ButtonRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT ButtonIndex, ImagePath, ActionType, ActionValue
                            FROM Buttons
                            WHERE DeviceId=$d AND Profile=$p
                            ORDER BY ButtonIndex";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new ButtonRecord(
                deviceId, profile, r.GetInt32(0),
                ImagePath:   r.IsDBNull(1) ? null : r.GetString(1),
                ActionType:  r.IsDBNull(2) ? null : r.GetString(2),
                ActionValue: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return result;
    }

    public void SaveButton(ButtonRecord b)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Buttons(DeviceId, Profile, ButtonIndex, ImagePath, ActionType, ActionValue)
VALUES ($d, $p, $b, $img, $at, $av)
ON CONFLICT(DeviceId, Profile, ButtonIndex) DO UPDATE SET
  ImagePath   = excluded.ImagePath,
  ActionType  = excluded.ActionType,
  ActionValue = excluded.ActionValue";
        cmd.Parameters.AddWithValue("$d",   b.DeviceId);
        cmd.Parameters.AddWithValue("$p",   b.Profile);
        cmd.Parameters.AddWithValue("$b",   b.ButtonIndex);
        cmd.Parameters.AddWithValue("$img", (object?)b.ImagePath   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at",  (object?)b.ActionType  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av",  (object?)b.ActionValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes ALL buttons of a profile (e.g. after a device Reset).</summary>
    public void ClearProfile(int deviceId, int profile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Buttons WHERE DeviceId=$d AND Profile=$p";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$p", profile);
        cmd.ExecuteNonQuery();
    }

    // ---------- current profile per device ----------

    private static string ProfileKey(int deviceId) => $"device.{deviceId}.currentProfile";

    public int GetCurrentProfile(int deviceId, int fallback = 1)
    {
        var s = GetSetting(ProfileKey(deviceId));
        return int.TryParse(s, out var v) && v >= 1 && v <= 5 ? v : fallback;
    }

    public void SetCurrentProfile(int deviceId, int profile) =>
        SetSetting(ProfileKey(deviceId), profile.ToString());

    // ---------- rotation per device ----------

    private static string RotationKey(int deviceId) => $"device.{deviceId}.rotation";

    /// <summary>Device mounting orientation (default: native).</summary>
    public DisplayRotation GetRotation(int deviceId) =>
        DisplayPadLayout.Parse(GetSetting(RotationKey(deviceId)));

    public void SetRotation(int deviceId, DisplayRotation rotation) =>
        SetSetting(RotationKey(deviceId), ((int)rotation).ToString());

    // ---------- generic settings ----------

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

    public void Dispose() => _conn.Dispose();
}

public sealed record ButtonRecord(
    int    DeviceId,
    int    Profile,
    int    ButtonIndex,
    string? ImagePath,
    string? ActionType,
    string? ActionValue);
