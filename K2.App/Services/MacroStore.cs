// Services/MacroStore.cs — macro persistence in the Macros table of the Everest DB
// The table is created on first use (same pattern as EverestStore).

using System;
using System.Collections.Generic;
using K2.App.Models;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

public sealed class MacroStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly bool _ownsConnection;

    /// <summary>Uses a shared connection (will not be closed in Dispose).</summary>
    public MacroStore(SqliteConnection sharedConnection)
    {
        _conn = sharedConnection;
        _ownsConnection = false;
        EnsureTable();
    }

    /// <summary>Opens its own connection to the given DB.</summary>
    public MacroStore(string? dbPath = null)
    {
        dbPath ??= EverestStore.DefaultDbPath();
        _conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        _conn.Open();
        _ownsConnection = true;
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Macros (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT    NOT NULL DEFAULT '',
    RecordMouse     INTEGER NOT NULL DEFAULT 0,
    RecordKeyboard  INTEGER NOT NULL DEFAULT 1,
    DelayOption     TEXT    NOT NULL DEFAULT 'Recorded',
    CustomDelayMs   INTEGER NOT NULL DEFAULT 50,
    PlaybackOption  TEXT    NOT NULL DEFAULT 'Once',
    RepeatCount     INTEGER NOT NULL DEFAULT 1,
    InputsJson      TEXT    NOT NULL DEFAULT '[]',
    IsActive        INTEGER NOT NULL DEFAULT 1,
    MacroOrder      INTEGER NOT NULL DEFAULT 0,
    ModifiedAt      TEXT    NOT NULL DEFAULT ''
)";
        cmd.ExecuteNonQuery();
    }

    // ─────────────────────── CRUD ───────────────────────

    public List<MacroDefinition> GetAll()
    {
        var list = new List<MacroDefinition>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Macros ORDER BY MacroOrder, Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadRow(r));
        return list;
    }

    public MacroDefinition? GetById(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Macros WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    public int Insert(MacroDefinition m)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Macros(Name, RecordMouse, RecordKeyboard, DelayOption, CustomDelayMs,
    PlaybackOption, RepeatCount, InputsJson, IsActive, MacroOrder, ModifiedAt)
VALUES ($name, $rm, $rk, $delay, $cd, $play, $rep, $json, $act, $ord, $mod);
SELECT last_insert_rowid()";
        BindParams(cmd, m);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Update(MacroDefinition m)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
UPDATE Macros SET Name=$name, RecordMouse=$rm, RecordKeyboard=$rk,
    DelayOption=$delay, CustomDelayMs=$cd, PlaybackOption=$play,
    RepeatCount=$rep, InputsJson=$json, IsActive=$act,
    MacroOrder=$ord, ModifiedAt=$mod
WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", m.Id);
        BindParams(cmd, m);
        cmd.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Macros WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Macros";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Imports a list of macros (overwrites everything).</summary>
    public int ImportAll(IEnumerable<MacroDefinition> macros)
    {
        int count = 0;
        using var tx = _conn.BeginTransaction();
        foreach (var m in macros)
        {
            Insert(m);
            count++;
        }
        tx.Commit();
        return count;
    }

    // ─────────────────────── Import from BaseCamp.db ───────────────────────

    /// <summary>Reads macros from the Macros table of the BaseCamp DB.</summary>
    public static List<MacroDefinition> ReadFromBaseCampDb(string dbPath)
    {
        var list = new List<MacroDefinition>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // Check that the table exists
        using var chk = conn.CreateCommand();
        chk.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Macros'";
        if (chk.ExecuteScalar() is null) return list;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT Id, Name, RecordMouse, RecordKeyboard, DelayOption,
            CustomDelay, PlaybackOption, InputsJson, IsActive, MacroOrder FROM Macros ORDER BY MacroOrder, Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var m = MacroDefinition.FromBaseCamp(
                id:            r.GetInt32(0),
                name:          r.IsDBNull(1) ? "" : r.GetString(1),
                recordMouse:   !r.IsDBNull(2) && r.GetInt32(2) != 0,
                recordKeyboard:r.IsDBNull(3) || r.GetInt32(3) != 0,
                delayOption:   r.IsDBNull(4) ? null : r.GetString(4),
                customDelay:   r.IsDBNull(5) ? 50 : r.GetInt32(5),
                playbackOption:r.IsDBNull(6) ? null : r.GetString(6),
                inputsJson:    r.IsDBNull(7) ? null : r.GetString(7),
                isActive:      r.IsDBNull(8) || r.GetInt32(8) != 0,
                order:         r.IsDBNull(9) ? 0 : r.GetInt32(9));
            list.Add(m);
        }
        return list;
    }

    // ─────────────────────── Helpers ───────────────────────

    private static MacroDefinition ReadRow(SqliteDataReader r)
    {
        var m = new MacroDefinition
        {
            Id             = r.GetInt32(r.GetOrdinal("Id")),
            Name           = r.GetString(r.GetOrdinal("Name")),
            RecordMouse    = r.GetInt32(r.GetOrdinal("RecordMouse")) != 0,
            RecordKeyboard = r.GetInt32(r.GetOrdinal("RecordKeyboard")) != 0,
            CustomDelayMs  = r.GetInt32(r.GetOrdinal("CustomDelayMs")),
            RepeatCount    = r.GetInt32(r.GetOrdinal("RepeatCount")),
            IsActive       = r.GetInt32(r.GetOrdinal("IsActive")) != 0,
            Order          = r.GetInt32(r.GetOrdinal("MacroOrder")),
        };

        var delayStr = r.GetString(r.GetOrdinal("DelayOption"));
        m.DelayOption = delayStr switch
        {
            "NoDelay" => MacroDelay.NoDelay,
            "Custom"  => MacroDelay.Custom,
            _         => MacroDelay.Recorded
        };

        var playStr = r.GetString(r.GetOrdinal("PlaybackOption"));
        m.PlaybackOption = playStr switch
        {
            "RepeatN"   => MacroPlayback.RepeatN,
            "WhileHeld" => MacroPlayback.WhileHeld,
            "Toggle"    => MacroPlayback.Toggle,
            _           => MacroPlayback.Once
        };

        m.Inputs = MacroDefinition.InputsFromJson(r.GetString(r.GetOrdinal("InputsJson")));

        var modStr = r.GetString(r.GetOrdinal("ModifiedAt"));
        m.ModifiedAt = DateTime.TryParse(modStr, out var dt) ? dt : DateTime.Now;

        return m;
    }

    private static void BindParams(SqliteCommand cmd, MacroDefinition m)
    {
        cmd.Parameters.AddWithValue("$name",  m.Name);
        cmd.Parameters.AddWithValue("$rm",    m.RecordMouse ? 1 : 0);
        cmd.Parameters.AddWithValue("$rk",    m.RecordKeyboard ? 1 : 0);
        cmd.Parameters.AddWithValue("$delay", m.DelayOption.ToString());
        cmd.Parameters.AddWithValue("$cd",    m.CustomDelayMs);
        cmd.Parameters.AddWithValue("$play",  m.PlaybackOption.ToString());
        cmd.Parameters.AddWithValue("$rep",   m.RepeatCount);
        cmd.Parameters.AddWithValue("$json",  m.InputsToJson());
        cmd.Parameters.AddWithValue("$act",   m.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$ord",   m.Order);
        cmd.Parameters.AddWithValue("$mod",   m.ModifiedAt.ToString("o"));
    }

    public void Dispose()
    {
        if (_ownsConnection) _conn.Dispose();
    }
}
