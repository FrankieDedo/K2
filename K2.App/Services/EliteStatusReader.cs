using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>
/// Reads Elite Dangerous' live cockpit state for the DisplayPad's <c>dp_edstatus</c> tiles.
///
/// <para>
/// <b>Where the state comes from.</b> The game writes <c>Status.json</c> into
/// <c>%USERPROFILE%\Saved Games\Frontier Developments\Elite Dangerous\</c> and REPLACES the whole
/// file every time something changes (a few times a second at most). This is Frontier's own
/// documented Player Journal surface — the same one EDMC, EDDI and the Stream Deck plugins read —
/// so there is no hooking, no memory reading and nothing the game could consider tampering: we
/// only ever open the file for reading.
/// </para>
///
/// <para>
/// <b>Two gotchas this class exists to absorb.</b> (1) The game keeps its own handle on the file,
/// so it MUST be opened with <see cref="FileShare.ReadWrite"/> — the default share mode throws
/// <c>IOException</c> at random and would make the tiles flicker between "known" and "unknown".
/// (2) Because the file is rewritten rather than edited in place, a read can land mid-write and
/// see truncated JSON; that is expected, not an error, so a parse failure just keeps the previous
/// snapshot instead of blanking the tiles.
/// </para>
///
/// <para>
/// <b>Cadence.</b> No timer of its own: <see cref="Snapshot"/> is pulled by
/// <see cref="DpLiveTileService"/>'s existing 1 Hz tile tick, and re-reads the file only when its
/// last-write timestamp moved. A pad full of Elite tiles therefore costs one <c>stat</c> per
/// second and a ~1 KB parse only when the ship actually changed state.
/// </para>
/// </summary>
internal static class EliteStatusReader
{
    /// <summary>One reading of the ship's state. <see cref="Valid"/> is false when the game has
    /// never run (no file) or the file has never parsed — the tiles then paint an "unknown"
    /// state rather than lying about a gear that may well be down.</summary>
    internal readonly record struct Status(bool Valid, uint Flags, uint Flags2,
                                           int PipsSys, int PipsEng, int PipsWep,
                                           double FuelMain, double FuelReservoir, int Cargo)
    {
        public bool Has(uint mask) => Valid && (Flags & mask) != 0;

        /// <summary>Same test against the Odyssey on-foot bitfield. Kept separate from
        /// <see cref="Has"/> because the two words reuse the same bit values for different
        /// states — reading <c>LowOxygen</c> out of <c>Flags</c> would light up in supercruise.</summary>
        public bool Has2(uint mask) => Valid && (Flags2 & mask) != 0;
    }

    // ── Flags bitfield (Player Journal "Status File" reference) ──────────────────────────────
    // Only the bits the DisplayPad tiles can show are named; the rest are deliberately omitted
    // rather than transcribed "just in case".
    public const uint FlagDocked        = 0x00000001;
    public const uint FlagLanded        = 0x00000002;
    public const uint FlagLandingGear   = 0x00000004;
    public const uint FlagShieldsUp     = 0x00000008;
    public const uint FlagSupercruise   = 0x00000010;
    public const uint FlagFlightAssistOff = 0x00000020;   // NOTE: set when assist is OFF
    public const uint FlagHardpoints    = 0x00000040;
    public const uint FlagLightsOn      = 0x00000100;
    public const uint FlagCargoScoop    = 0x00000200;
    public const uint FlagSilentRunning = 0x00000400;
    public const uint FlagScoopingFuel  = 0x00000800;
    public const uint FlagFsdMassLocked = 0x00010000;
    public const uint FlagFsdCharging   = 0x00020000;
    public const uint FlagFsdCooldown   = 0x00040000;
    public const uint FlagLowFuel       = 0x00080000;
    public const uint FlagOverHeating   = 0x00100000;
    public const uint FlagInDanger      = 0x00400000;
    public const uint FlagInterdicted   = 0x00800000;
    public const uint FlagNightVision   = 0x10000000;
    public const uint FlagFsdJump       = 0x40000000;
    public const uint FlagInWing        = 0x00000080;
    public const uint FlagSrvHandbrake  = 0x00001000;
    public const uint FlagSrvTurret     = 0x00002000;
    public const uint FlagSrvUnderShip  = 0x00004000;
    public const uint FlagSrvDriveAssist = 0x00008000;
    public const uint FlagHasLatLong    = 0x00200000;
    public const uint FlagInMainShip    = 0x01000000;
    public const uint FlagInFighter     = 0x02000000;
    public const uint FlagInSrv         = 0x04000000;
    public const uint FlagAnalysisMode  = 0x08000000;
    public const uint FlagSrvHighBeam   = 0x80000000;

    // ── Flags2 bitfield (Odyssey on-foot state; same reference document) ─────────────────────
    // A separate word with its OWN bit meanings — see Status.Has2.
    public const uint Flag2OnFoot       = 0x00000001;
    public const uint Flag2InTaxi       = 0x00000002;
    public const uint Flag2InMulticrew  = 0x00000004;
    public const uint Flag2OnFootStation = 0x00000008;
    public const uint Flag2OnFootPlanet = 0x00000010;
    public const uint Flag2AimDownSight = 0x00000020;
    public const uint Flag2LowOxygen    = 0x00000040;
    public const uint Flag2LowHealth    = 0x00000080;
    public const uint Flag2Cold         = 0x00000100;
    public const uint Flag2Hot          = 0x00000200;
    public const uint Flag2VeryCold     = 0x00000400;
    public const uint Flag2VeryHot      = 0x00000800;
    public const uint Flag2GlideMode    = 0x00001000;
    public const uint Flag2OnFootHangar = 0x00002000;
    public const uint Flag2OnFootSocial = 0x00004000;
    public const uint Flag2OnFootExterior = 0x00008000;
    public const uint Flag2BreathableAtmos = 0x00010000;
    public const uint Flag2FsdHyperdriveCharging = 0x00080000;

    private static readonly object _gate = new();
    private static Status _last;
    private static DateTime _lastWriteUtc = DateTime.MinValue;
    private static string? _path;
    private static bool _loggedMissing;

    /// <summary>Full path of the game's status file, resolved once. Uses the real "Saved Games"
    /// known folder rather than assuming it sits under the profile root — it is relocatable.</summary>
    public static string StatusPath => _path ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Saved Games", "Frontier Developments", "Elite Dangerous", "Status.json");

    /// <summary>True when the game has written a status file at least once — i.e. Elite is
    /// installed AND has been run. Used by the key config dialog to explain a dead tile.</summary>
    public static bool FileExists => File.Exists(StatusPath);

    /// <summary>The current ship state, re-reading the file only when it changed on disk.
    /// Never throws: an unreadable or half-written file yields the previous snapshot.</summary>
    public static Status Snapshot()
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(StatusPath);
                if (!info.Exists)
                {
                    if (!_loggedMissing)
                    {
                        _loggedMissing = true;
                        App.WriteLog($"[ED] Status.json not found at \"{StatusPath}\" " +
                                     "(game never launched?) — Elite tiles will show 'unknown'");
                    }
                    return _last = default;
                }
                _loggedMissing = false;

                if (info.LastWriteTimeUtc <= _lastWriteUtc) return _last;
                _lastWriteUtc = info.LastWriteTimeUtc;

                string json = ReadShared(StatusPath);
                if (json.Length == 0) return _last;          // caught it mid-rewrite

                _last = Parse(json) ?? _last;
                return _last;
            }
            catch (Exception ex)
            {
                App.WriteLog($"[ED] Status.json read failed: {ex.Message}");
                return _last;
            }
        }
    }

    /// <summary>Opens the file the only way the game permits: read access while it holds its own
    /// write handle. See the class remarks — without <see cref="FileShare.ReadWrite"/> this
    /// throws intermittently.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>Parses one status document, or null if it is truncated/not a status record —
    /// both of which are normal transient states, not failures worth logging every tick.</summary>
    private static Status? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            uint flags  = ReadUInt(root, "Flags");
            uint flags2 = ReadUInt(root, "Flags2");

            int sys = 0, eng = 0, wep = 0;
            if (root.TryGetProperty("Pips", out var pips) &&
                pips.ValueKind == JsonValueKind.Array && pips.GetArrayLength() == 3)
            {
                // Pips are half-pips (0..8 for the 4.0 maximum), in SYS/ENG/WEP order.
                sys = pips[0].GetInt32();
                eng = pips[1].GetInt32();
                wep = pips[2].GetInt32();
            }

            // Fuel is absent from the file while on foot or in an SRV, and Cargo is absent in
            // the same states — a missing property means "no reading", not zero, so the gauge
            // tiles fall back to the unknown look rather than screaming empty tank.
            double fuelMain = 0, fuelRes = 0;
            if (root.TryGetProperty("Fuel", out var fuel) && fuel.ValueKind == JsonValueKind.Object)
            {
                fuelMain = ReadDouble(fuel, "FuelMain");
                fuelRes  = ReadDouble(fuel, "FuelReservoir");
            }

            int cargo = (int)Math.Round(ReadDouble(root, "Cargo"));

            return new Status(true, flags, flags2, sys, eng, wep, fuelMain, fuelRes, cargo);
        }
        catch (JsonException)
        {
            return null;    // half-written file: keep the previous snapshot
        }
    }

    /// <summary>Reads a bitfield property that the game writes as a signed number — the Flags
    /// value uses bit 31 (srvHighBeam), so it arrives negative and must round-trip through
    /// unsigned rather than being clamped.</summary>
    private static uint ReadUInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind != JsonValueKind.Number) return 0;
        if (el.TryGetInt64(out long l)) return unchecked((uint)l);
        return uint.TryParse(el.GetRawText(), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out uint u) ? u : 0;
    }

    /// <summary>Reads a numeric property, tolerating both the integer and the fractional form
    /// the game uses for the same field across versions. Missing/blank yields 0.</summary>
    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetDouble(out double d) ? d : 0;
    // ── Ship capacities (Journal "Loadout") ─────────────────────────────────────────────────
    // Status.json carries the CURRENT fuel and cargo but never the tank/hold SIZE, so a gauge
    // has no denominator without this. The capacities only change when the ship or its outfit
    // changes, both of which write a fresh "Loadout" line, so the newest journal is scanned at
    // most once every few seconds and only after it actually grew.

    private static readonly object _loadoutGate = new();
    private static double _fuelCapacity;
    private static int _cargoCapacity;
    private static DateTime _loadoutCheckedUtc = DateTime.MinValue;
    private static long _loadoutJournalLen = -1;
    private static string? _loadoutJournalPath;

    /// <summary>Main tank size in tons, 0 when unknown (game never run, or run only on foot).</summary>
    public static double FuelCapacity { get { RefreshLoadout(); return _fuelCapacity; } }

    /// <summary>Cargo hold size in tons, 0 when unknown.</summary>
    public static int CargoCapacity { get { RefreshLoadout(); return _cargoCapacity; } }

    private static void RefreshLoadout()
    {
        lock (_loadoutGate)
        {
            var now = DateTime.UtcNow;
            if ((now - _loadoutCheckedUtc).TotalSeconds < 5) return;
            _loadoutCheckedUtc = now;

            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(StatusPath)!);
                if (!dir.Exists) return;

                FileInfo? newest = null;
                foreach (var f in dir.GetFiles("Journal.*.log"))
                    if (newest is null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
                if (newest is null) return;

                // Nothing appended since the last scan: the capacities cannot have changed.
                if (newest.FullName == _loadoutJournalPath && newest.Length == _loadoutJournalLen) return;
                _loadoutJournalPath = newest.FullName;
                _loadoutJournalLen  = newest.Length;

                using var fs = new FileStream(newest.FullName, FileMode.Open,
                                              FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) is not null)
                {
                    // Cheap pre-filter: only the handful of lines that can carry a capacity get
                    // parsed as JSON, instead of every line of a multi-megabyte session log.
                    if (line.IndexOf("\"FuelCapacity\"", StringComparison.Ordinal) < 0) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;

                        // "Loadout" nests it as { Main, Reserve }; "LoadGame" writes a flat number.
                        if (root.TryGetProperty("FuelCapacity", out var fc))
                        {
                            if (fc.ValueKind == JsonValueKind.Object)
                                _fuelCapacity = ReadDouble(fc, "Main");
                            else if (fc.ValueKind == JsonValueKind.Number)
                                _fuelCapacity = fc.GetDouble();
                        }
                        if (root.TryGetProperty("CargoCapacity", out var cc) &&
                            cc.ValueKind == JsonValueKind.Number)
                            _cargoCapacity = cc.GetInt32();
                    }
                    catch (JsonException) { }   // truncated last line of a live journal
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
