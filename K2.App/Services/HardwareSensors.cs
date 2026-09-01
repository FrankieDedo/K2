// HardwareSensors.cs — a HWiNFO-style catalogue of every hardware sensor on the PC, for the
// DisplayPad "PC monitor" live tiles (dp_sysmon).
//
// Base Camp reads the same library (LibreHardwareMonitorLib — see the comment block in
// SystemMonitor.cs and _reference/BaseCamp_Decompiled/BaseCamp.LibreHardwareMonitor). K2's
// dock "PC Info" pages deliberately DON'T take that dependency (SystemMonitor reproduces the
// six fixed numbers from GetSystemTimes/PDH/Core Audio instead), but the DisplayPad tile
// picker wants the full tree — per-core temperatures, clocks, power, fan RPM, VRAM, voltages,
// drive temps — and there is no lightweight Win32 equivalent for most of that. So this is the
// one place LHM is used.
//
//   • The kernel ring0 driver LHM loads for CPU/GPU temperatures needs elevation. K2.App runs
//     as administrator already (app.manifest), so that is a non-issue here.
//   • Everything is best-effort: a provider that fails to open just contributes no sensors, and
//     Start() never throws. A machine with no compatible sensor at all leaves an empty list and
//     the picker says so.
//   • Lazy AND non-blocking: nothing is opened until someone calls Start(), and the query
//     methods (Snapshot/Get/StorageDisks/Find*) are pure reads that return empty/null until it
//     has. Callers kick Start() on a background thread (never inline on the UI thread — the
//     first open loads a driver and walks the whole hardware tree): DpLiveTileService.Sync when
//     a hardware tile appears, SensorPickerDialog's ctor, DpKeyConfigDialog when editing one.
//     A profile with only the six legacy dp_sysmon metrics (cpu/ram/gpu/disk/net_*) never
//     touches this class; those keep flowing through SystemMonitor.
//
// Min/Max/Average are K2's own rolling accumulators (LHM exposes Min/Max too, but with reset
// semantics we don't control and no average), so a tile set to "Average" or "Maximum" reads a
// figure this process has watched since it started — or since ResetStats().

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace K2.App.Services;

// public (not internal like SystemMonitor): the sensor types below surface on
// SensorPickerDialog's public XAML-generated partial, which WPF emits as public — an internal
// type there would be a CS0053 accessibility clash. K2.App is a leaf executable, so nothing
// outside it can see these anyway.
public static class HardwareSensors
{
    /// <summary>Which statistic of a sensor a tile shows — the second half of a dp_sysmon
    /// value's <c>"&lt;sensor-id&gt;|&lt;stat&gt;"</c> wire form.</summary>
    public enum Stat { Current, Minimum, Maximum, Average }

    public static Stat ParseStat(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "min" => Stat.Minimum,
        "max" => Stat.Maximum,
        "avg" => Stat.Average,
        _     => Stat.Current,
    };

    public static string StatWire(Stat s) => s switch
    {
        Stat.Minimum => "min",
        Stat.Maximum => "max",
        Stat.Average => "avg",
        _            => "cur",
    };

    /// <summary>Coarse hardware family, for the picker's grouping (mirrors HWiNFO's tree roots).</summary>
    public enum Group { Cpu, Gpu, Memory, Storage, Motherboard, Network, Battery, Cooler, Other }

    /// <summary>One sensor, resolved: its stable id, where it lives, its kind, and the four
    /// figures a tile can show. <see cref="Current"/> is null when the sensor has not reported a
    /// reading yet (or has stopped); Min/Max/Avg are 0 until the first reading lands.</summary>
    public sealed record Reading(
        string Id,            // LHM Identifier string, e.g. "/amdcpu/0/temperature/2" — persisted in the tile
        string HardwareId,    // owning hardware node's Identifier, e.g. "/amdcpu/0", "/nvme/1"
        string HardwareName,  // e.g. "AMD Ryzen 7 5800X"
        Group  Group,
        string Kind,          // SensorType name: "Temperature", "Load", "Clock", "Power", "Fan", ...
        string Name,          // e.g. "CPU Core #3", "GPU Hot Spot", "Used Memory"
        float? Current,
        float  Min,
        float  Max,
        float  Average,
        string Unit)          // "°C", "%", "MHz", "W", "RPM", "V", "GB", "MB/s", ...
    {
        public float? Pick(Stat s) => s switch
        {
            Stat.Minimum => Min,
            Stat.Maximum => Max,
            Stat.Average => Average,
            _            => Current,
        };

        /// <summary>The value for <paramref name="s"/> formatted for a 102 px tile — short, unit
        /// folded in where it helps ("64°", "3.8G", "72%"). "—" before the first reading.</summary>
        public string Display(Stat s) => Pick(s) is float v ? FormatValue(Kind, v) : "—";

        /// <summary>Ring fill 0..1 for the gauge tile, or null for a reading with no natural full
        /// scale (a clock speed, a wattage, a throughput) — same rule as
        /// <c>LiveTileRenderer.TryRenderGauge</c>: no ring rather than a misleading "0%".</summary>
        public double? Fraction(Stat s)
        {
            if (Pick(s) is not float v) return null;
            return Kind is "Load" or "Level" or "Control" or "Humidity"
                ? Math.Clamp(v / 100d, 0d, 1d)
                : null;
        }

        /// <summary>A little glyph for the picker's icon column, chosen by sensor kind.</summary>
        public string Icon => KindIcon(Kind);
    }

    // ─────────────────────────── State ───────────────────────────

    private static readonly object _gate = new();
    private static Computer? _computer;
    private static Timer? _timer;
    private static bool _polling;

    private sealed class Accum
    {
        public string HardwareId = "";
        public string HardwareName = "";
        public Group  Group;
        public string Kind = "";
        public string Name = "";
        public string Unit = "";
        public float? Last;
        public float  Min;
        public float  Max;
        public double Sum;
        public long   Count;
        public bool   Seen;

        public void Add(float v)
        {
            if (!Seen) { Min = Max = v; Seen = true; }
            else { if (v < Min) Min = v; if (v > Max) Max = v; }
            Sum += v; Count++;
            Last = v;
        }

        public Reading ToReading(string id) => new(
            id, HardwareId, HardwareName, Group, Kind, Name,
            Last, Seen ? Min : 0f, Seen ? Max : 0f,
            Count > 0 ? (float)(Sum / Count) : 0f, Unit);
    }

    // Insertion-ordered so the picker's stable sort has a sensible tie-breaker (the order LHM
    // enumerates a hardware node's sensors, which tracks the vendor's own grouping).
    private static readonly Dictionary<string, Accum> _sensors = new();

    // ─────────────────────────── Lifecycle ───────────────────────────

    /// <summary>Opens LHM and starts the 1 Hz poll. Idempotent; best-effort (a provider that
    /// throws on open is skipped, the rest still come up). Safe to call from any thread — but
    /// the first open loads a kernel driver and walks the whole hardware tree, so keep it off
    /// the UI thread.</summary>
    public static void Start()
    {
        Computer c;
        lock (_gate)
        {
            if (_computer is not null || _opening) return;
            _opening = true;
        }

        try
        {
            c = new Computer
            {
                IsCpuEnabled         = true,
                IsGpuEnabled         = true,
                IsMemoryEnabled      = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled     = true,
                IsNetworkEnabled     = true,
                IsBatteryEnabled     = true,
                // IsControllerEnabled stays OFF: external fan/RGB controllers do slow serial
                // probing on Open()/Update() and contribute nothing a PC-monitor tile wants.
            };
            c.Open();
        }
        catch (Exception ex)
        {
            lock (_gate) _opening = false;
            App.WriteLog($"[HWSensors] LHM open failed: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            _computer = c;
            _opening = false;
        }

        Sample();   // first pass now, so a picker/tile opened immediately isn't blank
        lock (_gate)
        {
            _timer ??= new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        int n; lock (_gate) n = _sensors.Count;
        App.WriteLog($"[HWSensors] LHM open OK — {n} sensors after first poll");
    }

    private static bool _opening;

    /// <summary>Stops the poll and unloads the driver. Called from <c>MainWindow.OnWindowClosed</c>;
    /// safe when never started.</summary>
    public static void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            try { _computer?.Close(); } catch { /* ignore */ }
            _computer = null;
        }
    }

    /// <summary>Zeroes every sensor's Min/Max/Average accumulator — the "reset statistics" the
    /// picker offers, matching HWiNFO's own. Current readings are untouched.</summary>
    public static void ResetStats()
    {
        lock (_gate)
            foreach (var a in _sensors.Values)
            {
                a.Seen = false; a.Min = a.Max = 0f; a.Sum = 0; a.Count = 0;
            }
    }

    // ─────────────────────────── Query ───────────────────────────

    /// <summary>Every sensor seen so far, ordered for the picker: by hardware family, then by the
    /// device name, then in the order LHM lists that device's sensors. Empty until <see cref="Start"/> runs.</summary>
    public static IReadOnlyList<Reading> Snapshot()
    {
        lock (_gate)
            return _sensors
                .Select(kv => kv.Value.ToReading(kv.Key))
                .OrderBy(r => r.Group)
                .ThenBy(r => r.HardwareName, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static readonly HashSet<string> _loggedMissing = new();

    /// <summary>The one sensor with this id, or null if it isn't present (a device unplugged
    /// since the tile was assigned), or before <see cref="Start"/> has run.</summary>
    public static Reading? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            if (_sensors.TryGetValue(id, out var a)) return a.ToReading(id);
            if (_computer is not null && _sensors.Count > 0 && _loggedMissing.Add(id))
                App.WriteLog($"[HWSensors] tile wants sensor '{id}' — not among the {_sensors.Count} LHM reports");
            return null;
        }
    }

    /// <summary>True once <see cref="Start"/> has successfully opened LHM.</summary>
    public static bool Available
    {
        get { lock (_gate) return _computer is not null; }
    }

    // ── Convenience resolvers for the "PC monitor" (dp_sysmon) preset refinements ──

    /// <summary>Physical storage devices seen so far — <c>(HardwareId, Name)</c>, one per disk,
    /// for the "which disk" picker. Empty until <see cref="Start"/> runs.</summary>
    public static IReadOnlyList<(string Id, string Name)> StorageDisks()
    {
        lock (_gate)
            return _sensors.Values
                .Where(a => a.Group == Group.Storage && a.HardwareId.Length > 0)
                .GroupBy(a => a.HardwareId)
                .Select(g => (g.Key, g.First().HardwareName))
                .OrderBy(t => t.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>Best "the CPU is this hot" temperature (<c>dp_sysmon</c> "cpu:temp"), or null.
    /// Prefers the vendor's package/overall reading — Intel "CPU Package", AMD "Core
    /// (Tctl/Tdie)" — over a single core, an I/O-die or CCD reading, or a distance-to-TjMax.</summary>
    public static Reading? FindCpuTemp() => PickTemp(Group.Cpu,
        prefer: new[]
        {
            "cpu package", "core (tctl/tdie)", "core (tdie)", "core (tctl)", "tctl/tdie",
            "package", "cpu ccd1 (tdie)", "core max", "core average", "cpu",
        },
        avoid: new[] { "distance to tjmax", "tjmax", "ccd", "soc", "i/o", "iod" });

    /// <summary>Best "the GPU is this hot" temperature (<c>dp_sysmon</c> "gpu:temp"), or null.
    /// Prefers the main die/core sensor over hot-spot, memory (VRAM) or VRM readings.</summary>
    public static Reading? FindGpuTemp() => PickTemp(Group.Gpu,
        prefer: new[] { "gpu core", "gpu temperature", "core", "gpu", "temperature", "gpu die" },
        avoid: new[] { "hot spot", "hotspot", "memory", "vram", "vrm", "junction", "mem" });

    /// <summary>Best "how busy" sensor for one disk (<c>dp_sysmon</c> "disk:&lt;id&gt;"), or
    /// null — LHM's NVMe/SSD "Total Activity" load, falling back to any load sensor, then
    /// "Used Space".</summary>
    public static Reading? FindDiskActivity(string hardwareId)
    {
        lock (_gate)
        {
            var rows = _sensors
                .Where(kv => kv.Value.HardwareId == hardwareId)
                .Select(kv => kv.Value.ToReading(kv.Key))
                .ToList();
            return PreferByName(rows.Where(r => r.Kind == "Load"), null, "total activity", "activity")
                ?? rows.FirstOrDefault(r => r.Kind == "Load")
                ?? rows.FirstOrDefault(r => r.Kind == "Level" && r.Name.Contains("Used", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Picks the most representative Temperature sensor of a hardware family: the first
    /// <paramref name="prefer"/> name that matches, skipping anything whose name contains an
    /// <paramref name="avoid"/> term unless nothing else is left.</summary>
    private static Reading? PickTemp(Group group, string[] prefer, string[] avoid)
    {
        lock (_gate)
        {
            var rows = _sensors
                .Where(kv => kv.Value.Group == group && kv.Value.Kind == "Temperature")
                .Select(kv => kv.Value.ToReading(kv.Key))
                .ToList();
            if (rows.Count == 0) return null;

            var clean = rows.Where(r => !avoid.Any(a => r.Name.Contains(a, StringComparison.OrdinalIgnoreCase))).ToList();
            return PreferByName(clean, avoid, prefer)
                ?? clean.FirstOrDefault()
                ?? PreferByName(rows, null, prefer)
                ?? rows[0];
        }
    }

    private static Reading? PreferByName(IEnumerable<Reading> rows, string[]? avoid, params string[] namePriority)
    {
        var list = rows as IList<Reading> ?? rows.ToList();
        bool Blocked(Reading r) => avoid is not null
            && avoid.Any(a => r.Name.Contains(a, StringComparison.OrdinalIgnoreCase));

        foreach (var want in namePriority)
        {
            var hit = list.FirstOrDefault(r => !Blocked(r) && r.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        foreach (var want in namePriority)
        {
            var hit = list.FirstOrDefault(r => !Blocked(r) && r.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    // ─────────────────────────── Poll ───────────────────────────

    private static int _pollCount;

    private static void Sample()
    {
        // Re-entrancy guard: an Update() that runs long must not stack a second one behind it.
        lock (_gate) { if (_polling || _computer is null) return; _polling = true; }
        try
        {
            Computer c;
            lock (_gate) { c = _computer!; }

            foreach (var hw in c.Hardware)
                VisitHardware(hw);

            // Heartbeat every ~30 s: proof the poll is alive, plus the CPU-temperature readings
            // (the ones users compare against HWiNFO) so a "reads 0" report says whether LHM is
            // getting the value at all vs. the tile pipeline losing it.
            if (++_pollCount % 30 == 1)
            {
                string cpuTemps; int n;
                lock (_gate)
                {
                    n = _sensors.Count;
                    cpuTemps = string.Join(", ", _sensors.Values
                        .Where(a => a.Group == Group.Cpu && a.Kind == "Temperature")
                        .Select(a => $"{a.Name}={a.Last?.ToString("0.#") ?? "null"}"));
                }
                App.WriteLog($"[HWSensors] poll #{_pollCount}: {n} sensors; CPU temps: " +
                             (cpuTemps.Length > 0 ? cpuTemps : "(none reported)"));
            }
        }
        catch { /* a flaky provider must not kill the timer */ }
        finally { lock (_gate) _polling = false; }
    }

    private static void VisitHardware(IHardware hw)
    {
        try { hw.Update(); } catch { /* keep going with whatever it managed */ }

        Group group = GroupOf(hw.HardwareType);
        string hwId = hw.Identifier.ToString();
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v || float.IsNaN(v) || float.IsInfinity(v)) continue;

            string id = s.Identifier.ToString();
            lock (_gate)
            {
                if (!_sensors.TryGetValue(id, out var a))
                {
                    a = new Accum
                    {
                        HardwareId   = hwId,
                        HardwareName = hw.Name,
                        Group        = group,
                        Kind         = s.SensorType.ToString(),
                        Name         = s.Name,
                        Unit         = UnitOf(s.SensorType),
                    };
                    _sensors[id] = a;
                }
                a.Add(v);
            }
        }

        foreach (var sub in hw.SubHardware)
            VisitHardware(sub);
    }

    // ─────────────────────────── Maps ───────────────────────────

    private static Group GroupOf(HardwareType t) => t switch
    {
        HardwareType.Cpu                                      => Group.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd
            or HardwareType.GpuIntel                          => Group.Gpu,
        HardwareType.Memory                                   => Group.Memory,
        HardwareType.Storage                                  => Group.Storage,
        HardwareType.Motherboard or HardwareType.SuperIO      => Group.Motherboard,
        HardwareType.Network                                  => Group.Network,
        HardwareType.Battery                                  => Group.Battery,
        HardwareType.Cooler or HardwareType.EmbeddedController => Group.Cooler,
        _                                                     => Group.Other,
    };

    private static string UnitOf(SensorType t) => t switch
    {
        SensorType.Voltage     => "V",
        SensorType.Current     => "A",
        SensorType.Power       => "W",
        SensorType.Clock       => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load        => "%",
        SensorType.Frequency   => "Hz",
        SensorType.Fan         => "RPM",
        SensorType.Flow        => "L/h",
        SensorType.Control     => "%",
        SensorType.Level       => "%",
        SensorType.Factor      => "x",
        SensorType.Data        => "GB",
        SensorType.SmallData   => "MB",
        SensorType.Throughput  => "B/s",
        SensorType.TimeSpan    => "s",
        SensorType.Energy      => "mWh",
        SensorType.Noise       => "dBA",
        SensorType.Humidity    => "%",
        _                      => "",
    };

    /// <summary>Short glyph for the picker's icon column — one per sensor kind, HWiNFO-ish.</summary>
    private static string KindIcon(string kind) => kind switch
    {
        "Temperature" => "🌡",
        "Load"        => "📊",
        "Clock"       => "⏱",
        "Power"       => "⚡",
        "Fan"         => "🌀",
        "Voltage"     => "🔌",
        "Current"     => "🔋",
        "Data"        => "💾",
        "SmallData"   => "💾",
        "Throughput"  => "🌐",
        "Level"       => "🧪",
        "Control"     => "🎚",
        "Flow"        => "💧",
        "Frequency"   => "〰",
        "Factor"      => "✳",
        "Energy"      => "🔋",
        "Noise"       => "🔊",
        "Humidity"    => "💦",
        _             => "•",
    };

    /// <summary>Compact value string for a tile of a given sensor kind. Kept short on purpose —
    /// the tile is 102 px and the label sits in the caption, so the value band only needs the
    /// number and, where it disambiguates, a one-char unit.</summary>
    public static string FormatValue(string kind, float v)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case "Temperature":
                return Math.Round(v).ToString("0", inv) + "°";
            case "Load":
            case "Level":
            case "Control":
            case "Humidity":
                return Math.Round(v).ToString("0", inv) + "%";
            case "Clock":
            case "Frequency":
                return v >= 1000f
                    ? (v / 1000f).ToString("0.00", inv) + "G"
                    : Math.Round(v).ToString("0", inv) + "M";
            case "Power":
                return v >= 100f ? Math.Round(v).ToString("0", inv) + "W"
                                 : v.ToString("0.0", inv) + "W";
            case "Fan":
                return Math.Round(v).ToString("0", inv);
            case "Voltage":
                return v.ToString("0.00", inv) + "V";
            case "Current":
                return v.ToString("0.00", inv) + "A";
            case "Data":
                return v.ToString(v >= 100f ? "0" : "0.0", inv) + "G";
            case "SmallData":
                return v >= 1024f ? (v / 1024f).ToString("0.0", inv) + "G"
                                  : Math.Round(v).ToString("0", inv) + "M";
            case "Throughput":
                return Throughput(v);
            case "Energy":
                return Math.Round(v).ToString("0", inv);
            case "Noise":
                return Math.Round(v).ToString("0", inv);
            case "Factor":
                return v.ToString("0.00", inv);
            default:
                return v.ToString("0.##", inv);
        }
    }

    /// <summary>Bytes/s as a short human string (same scaling the net_* tiles already use — a
    /// key reading "0 MB/s" during a real transfer looks broken, so it drops to KB/s).</summary>
    private static string Throughput(float bytesPerSec)
    {
        var inv = CultureInfo.InvariantCulture;
        if (bytesPerSec >= 1_000_000f)
            return (bytesPerSec / 1_000_000f).ToString(bytesPerSec >= 10_000_000f ? "0" : "0.0", inv) + "M";
        if (bytesPerSec >= 1_000f)
            return (bytesPerSec / 1_000f).ToString("0", inv) + "K";
        return "0";
    }
}
