using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Keeps the DisplayPad's self-updating keys painted: the clock faces (<c>dp_clock</c>), the PC
/// monitor gauges (<c>dp_sysmon</c>) and the speed-test readouts (<c>dp_speedtest</c>).
///
/// <para>
/// Same shape as <see cref="DiscordVoiceKeyService"/> — a transient overlay pushed straight to
/// the hardware and never persisted in <c>DisplayPadStore</c>, registered by every repaint path
/// (<see cref="Sync"/>) and painted at the tail of that repaint's upload batch
/// (<see cref="Repaint"/>), with <see cref="Owns"/> telling those paths to skip the key's stored
/// picture. It differs in one deliberate way: this overlay owns its keys UNCONDITIONALLY, where
/// the Discord one only takes over auto-generated icons. There the picture is decoration on an
/// action that does something else; here the picture IS the action — a clock key that showed a
/// user-picked photo instead of the time would have no reason to exist.
/// </para>
///
/// <para>
/// <b>Cadence.</b> One 1 Hz timer for all devices, on a background thread (never the dispatcher:
/// each key costs an icon upload, ~12 ms on the wire — see DpGifAnimator's remarks on the
/// hardware's per-icon floor). A key is only re-uploaded when its CONTENT changed, decided by a
/// per-key stamp string: a minutes-only clock therefore uploads once a minute and a CPU gauge
/// only when the rounded percentage moves, so the usual cost is a couple of uploads a second
/// even with a pad full of live keys. The pathological case (12 seconds-resolution keys) is
/// ~144 ms of wire time per second, still comfortably inside the tick.
/// </para>
///
/// <para>
/// <b>Anything that takes the panel over</b> — the screensaver, a fullscreen page image, the
/// emoji browser — calls <see cref="Stop"/> for that device, exactly as it already stops the GIF
/// animator: a clock that kept ticking a tile into the middle of a fullscreen image would punch
/// a hole in it. Nothing has to resume the tiles afterwards, since every path back to the normal
/// page goes through a repaint, and a repaint calls <see cref="Sync"/>.
/// </para>
/// </summary>
internal static class DpLiveTileService
{
    /// <summary>One live key: which button, what it shows, and the key's own icon style (so the
    /// tile is drawn with the colors/font/"with text" choice made for that key —
    /// see <see cref="KeyIconSpec"/>).</summary>
    private readonly record struct LiveKey(int Button, string Type, string Value, KeyIconSpec? Spec);

    private readonly record struct DeviceCtx(IDisplayPadClient Client, Action<string> Log, int Rotation, LiveKey[] Keys);

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "K2.LiveTiles");

    private static readonly object _gate = new();
    private static readonly Dictionary<int, DeviceCtx> _devices = new();

    /// <summary>Last content stamp uploaded per key, so an unchanged tile isn't re-sent.
    /// Keyed device→button.</summary>
    private static readonly Dictionary<(int Device, int Button), string> _lastStamp = new();

    private static Timer? _timer;
    private static bool _subscribedSpeedTest;

    /// <summary>True for the action types this service paints.</summary>
    public static bool IsLiveType(string? actionType) =>
        actionType is "dp_clock" or "dp_sysmon" or "dp_speedtest";

    /// <summary>Registers (or refreshes, or stops) the live keys of one device from the page rows
    /// being painted — called from every repaint path, so the live set always matches the page
    /// actually on the panel. Registration only: the tiles themselves are painted by
    /// <see cref="Repaint"/> at the END of that repaint's batch, otherwise the profile's own
    /// icon for the same key would land on top of them.</summary>
    public static void Sync(IDisplayPadClient client, Action<string> log, int deviceId, int rotation,
                            IEnumerable<DpButtonRecord> rows)
    {
        var keys = rows
            .Where(r => IsLiveType(r.ActionType))
            .Select(r => new LiveKey(r.ButtonIndex, r.ActionType!, (r.ActionValue ?? "").Trim(),
                                     KeyIconSpec.FromJson(r.IconSpec)))
            .ToArray();

        if (keys.Length == 0) { Stop(deviceId); return; }

        lock (_gate)
        {
            _devices[deviceId] = new DeviceCtx(client, log, rotation, keys);
            foreach (var key in keys) _lastStamp.Remove((deviceId, key.Button));   // force a first paint
            EnsureTimerLocked();
        }

        // Warm up the hardware-sensor catalogue off-thread if any "PC monitor" tile needs LHM
        // (CPU/GPU temperature, a specific disk, or a "Choose sensor…" pick). LHM's first open
        // loads a kernel driver and walks the whole hardware tree — too heavy to hit inline on
        // the shared 1 Hz tile timer's first tick.
        if (keys.Any(k => k.Type == "dp_sysmon" && (k.Value.Contains(':') || k.Value.StartsWith('/'))))
            System.Threading.Tasks.Task.Run(HardwareSensors.Start);

        if (!_subscribedSpeedTest)
        {
            _subscribedSpeedTest = true;
            // A finished (or just-started) speed test must show up immediately, not on the next
            // tick — the run itself takes tens of seconds and the key says "…" throughout.
            SpeedTestService.Changed += OnSpeedTestChanged;
        }

        log($"[LIVE] device {deviceId}: " + string.Join(", ", keys.Select(k => $"btn{k.Button}={k.Type}:{k.Value}")));
    }

    /// <summary>Paints every live tile of a device that <see cref="Sync"/> has registered —
    /// called at the tail of the repaint batch that page belongs to. No-op for devices with no
    /// live keys.</summary>
    public static void Repaint(int deviceId)
    {
        DeviceCtx ctx;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out ctx)) return;
            foreach (var key in ctx.Keys) _lastStamp.Remove((deviceId, key.Button));
        }
        PushDevice(deviceId, ctx);
    }

    /// <summary>True when this overlay currently owns that key — the repaint paths use it to SKIP
    /// uploading the key's persisted picture, which would otherwise alternate with the live tile
    /// (the same rule <see cref="DiscordVoiceKeyService.Owns"/> exists for).</summary>
    public static bool Owns(int deviceId, int buttonIndex)
    {
        lock (_gate)
            return _devices.TryGetValue(deviceId, out var ctx)
                && Array.Exists(ctx.Keys, k => k.Button == buttonIndex);
    }

    /// <summary>The tile currently on that key, for the press-bounce to shrink instead of the
    /// stored picture (see <see cref="DiscordVoiceKeyService.CurrentIconPath"/>), or null when
    /// this overlay doesn't own the key.</summary>
    public static string? CurrentIconPath(int deviceId, int buttonIndex)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var ctx)) return null;
            if (!Array.Exists(ctx.Keys, k => k.Button == buttonIndex)) return null;
        }
        string path = TilePath(deviceId, buttonIndex);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Handles a press on a live key. Only the speed-test keys DO anything (they start a
    /// measurement); a clock or monitor key is a readout and deliberately ignores the press
    /// rather than running some unrelated action. Returns true when the press was consumed.</summary>
    public static bool HandlePress(int deviceId, int buttonIndex, Action<string> log)
    {
        LiveKey key;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var ctx)) return false;
            int i = Array.FindIndex(ctx.Keys, k => k.Button == buttonIndex);
            if (i < 0) return false;
            key = ctx.Keys[i];
        }

        if (key.Type == "dp_speedtest") SpeedTestService.Start(log);
        return true;
    }

    public static void Stop(int deviceId)
    {
        lock (_gate)
        {
            _devices.Remove(deviceId);
            foreach (var stale in _lastStamp.Keys.Where(k => k.Device == deviceId).ToList())
                _lastStamp.Remove(stale);
            EnsureTimerLocked();
        }
    }


    // ─────────────────────────── Timer ───────────────────────────

    /// <summary>Starts the shared tick on the first registered device, stops it when the last one
    /// goes. Call with <see cref="_gate"/> held.</summary>
    private static void EnsureTimerLocked()
    {
        if (_devices.Count > 0 && _timer is null)
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        else if (_devices.Count == 0 && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    private static void Tick()
    {
        List<(int Id, DeviceCtx Ctx)> targets;
        lock (_gate)
        {
            targets = _devices.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // Fallback warm-up for any LHM-backed tile, in case Sync's fire-and-forget Start() lost
        // the race or failed. Fire-and-forget again (never block the tile timer — LHM's open can
        // be slow) and let Start()'s own _opening guard collapse the duplicates.
        if (!HardwareSensors.Available &&
            targets.Any(t => t.Ctx.Keys.Any(k => k.Type == "dp_sysmon" &&
                                                 (k.Value.Contains(':') || k.Value.StartsWith('/')))))
            System.Threading.Tasks.Task.Run(HardwareSensors.Start);

        foreach (var (id, ctx) in targets) PushDevice(id, ctx);
    }

    private static void OnSpeedTestChanged()
    {
        List<(int Id, DeviceCtx Ctx)> targets;
        lock (_gate)
        {
            targets = _devices
                .Where(kv => kv.Value.Keys.Any(k => k.Type == "dp_speedtest"))
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }
        foreach (var (id, ctx) in targets) PushDevice(id, ctx);
    }

    /// <summary>Renders and uploads whatever changed on one device. Runs on the timer thread, so
    /// every render/upload of this service is serialized against itself — two threads writing the
    /// same per-key PNG while a third reads it for upload is exactly the kind of race the icon
    /// pipeline has been bitten by before.</summary>
    private static void PushDevice(int deviceId, DeviceCtx ctx)
    {
        var now = DateTime.Now;
        foreach (var key in ctx.Keys)
        {
            try
            {
                string stamp = StampOf(key, now);
                lock (_gate)
                {
                    if (_lastStamp.TryGetValue((deviceId, key.Button), out string? last) && last == stamp)
                    {
                        // A sysmon tile whose reading hasn't budged for ~30 s: log it once so a
                        // "numbers don't change" report says whether the value source is stuck
                        // (LHM/SystemMonitor) rather than the upload path.
                        if (key.Type == "dp_sysmon")
                        {
                            var sk = (deviceId, key.Button);
                            _stuckTicks[sk] = _stuckTicks.GetValueOrDefault(sk) + 1;
                            if (_stuckTicks[sk] == 30)
                                App.WriteLog($"[LIVE] btn{key.Button} sysmon '{key.Value}' unchanged for 30 ticks at \"{stamp}\"");
                        }
                        continue;
                    }
                    _lastStamp[(deviceId, key.Button)] = stamp;
                    _stuckTicks.Remove((deviceId, key.Button));
                }

                string path = TilePath(deviceId, key.Button);
                if (!Render(key, now, path))
                {
                    App.WriteLog($"[LIVE] btn{key.Button} {key.Type} render returned false");
                    continue;
                }
                ctx.Client.UploadImage(deviceId, path, key.Button, ctx.Rotation);
            }
            catch (Exception ex)
            {
                ctx.Log($"[LIVE] btn{key.Button} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Consecutive ticks a key's content stamp has stayed the same — for the
    /// "unchanged for 30 ticks" diagnostic in <see cref="PushDevice"/>.</summary>
    private static readonly Dictionary<(int Device, int Button), int> _stuckTicks = new();

    private static string TilePath(int deviceId, int button) =>
        Path.Combine(CacheDir, $"dev{deviceId}_btn{button}.png");

    /// <summary>Cache file for the APP'S OWN key-grid preview (<c>MainWindow.DisplayPad.cs</c>'s
    /// <c>DpRefreshLiveKeyPreviews</c>) — deliberately a DIFFERENT file than
    /// <see cref="TilePath"/>: that one belongs exclusively to this service's own 1 Hz
    /// background timer for the hardware upload, and the UI runs its own independent 1 Hz
    /// <c>DispatcherTimer</c> on the dispatcher thread. Sharing one file between two unrelated
    /// timers on two different threads would mean one write-in-progress could be read
    /// half-finished by the other — splitting them avoids that instead of relying on a lock
    /// two callers would have no reason to expect.</summary>
    public static string UiPreviewPath(int deviceId, int buttonIndex) =>
        Path.Combine(CacheDir, $"ui_dev{deviceId}_btn{buttonIndex}.png");

    /// <summary>Renders one live tile RIGHT NOW to an arbitrary path, bypassing
    /// <see cref="Sync"/>/<see cref="Owns"/>/the background timer entirely. Used by
    /// <c>MainWindow.DisplayPad.cs</c>'s own key-grid preview and by
    /// <c>DpKeyConfigDialog</c>'s live preview, so both tick every second independently of
    /// whether this service has ever registered the key as live (e.g. a key just being
    /// configured, not yet saved to any page).</summary>
    public static bool RenderNow(string type, string? value, KeyIconSpec? spec, string outputPngPath) =>
        Render(new LiveKey(0, type, (value ?? "").Trim(), spec), DateTime.Now, outputPngPath);

    // ─────────────────────── Content ───────────────────────

    /// <summary>What the key shows right now, as a string — the change detector (see the class
    /// remarks). Includes the caption, so a style/text change repaints too.</summary>
    private static string StampOf(LiveKey key, DateTime now) => key.Type switch
    {
        "dp_clock"     => "c:" + LiveTileRenderer.ClockStamp(key.Value, now),
        "dp_sysmon"    => "s:" + SysMonValue(key.Value).Text,
        "dp_speedtest" => "t:" + SpeedTestValue(key.Value).Text,
        _              => "",
    } + "|" + Caption(key);

    private static bool Render(LiveKey key, DateTime now, string path)
    {
        using (IconStyleScope.Push(key.Spec))
        {
            string caption = Caption(key);
            switch (key.Type)
            {
                case "dp_clock":
                    return LiveTileRenderer.TryRenderClock(key.Value, now, caption, DpHidNative.IconSize, path);
                case "dp_sysmon":
                {
                    var (text, fraction) = SysMonValue(key.Value);
                    return LiveTileRenderer.TryRenderGauge(text, fraction, caption, DpHidNative.IconSize, path);
                }
                case "dp_speedtest":
                {
                    var (text, fraction) = SpeedTestValue(key.Value);
                    bool isPing = key.Value == "ping";
                    return LiveTileRenderer.TryRenderSpeedTile(text, fraction, caption,
                        SpeedTestUnit(key.Value), DpHidNative.IconSize, path,
                        ownValueSize: isPing, valueTopPad: isPing ? 0.06f : 0f);
                }
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The tile's caption. Deliberately a symbol/abbreviation ("CPU", "↓ MB/s") rather than the
    /// action's full localized name: on a 102 px key the latter ("Download speed") shrinks to an
    /// unreadable two-line smudge, and these read the same in every language. A clock face gets
    /// no caption at all — it says what it is by being a clock. The user's own wording, typed in
    /// "Edit icon", overrides all of this through <see cref="IconStyleScope.OverrideCaption"/>,
    /// and "without text" (<see cref="KeyIconSpec.ShowText"/>) removes it.
    /// </summary>
    private static string Caption(LiveKey key)
    {
        if (key.Spec is { ShowText: false }) return "";
        if (key.Spec?.Text is { Length: > 0 } custom) return custom;
        return TileCaption(key.Type, key.Value);
    }

    /// <summary>The default caption for a live key of this type/value, ignoring any per-key
    /// style — also what <c>DpKeyConfigDialog</c>'s preview draws, so the configuration dialog
    /// shows the same tile the hardware will get. See <see cref="Caption"/>.</summary>
    internal static string TileCaption(string type, string? value)
    {
        return type switch
        {
            // A "Choose sensor…" pick: its captured name, left for the renderer's shrink-to-fit
            // (the user can shorten it in "Edit icon").
            "dp_sysmon" => ActionTypeHelper.ParseSensorValue(value) is { } sensor
                ? sensor.Label
                : (value ?? "") switch
                {
                    "cpu" => "CPU", "ram" => "RAM", "gpu" => "GPU", "disk" => "DISK",
                    "cpu:temp" => "CPU", "gpu:temp" => "GPU",
                    // The number itself carries the scale ("12.4M" = MB/s, "820K" = KB/s), so the
                    // caption only has to say which direction. A bare arrow was tried first and is
                    // too small to read on the tile.
                    "net_up" => "UP", "net_down" => "DOWN",
                    _ => (value ?? "").StartsWith("disk:")
                            ? DiskCaption(value!)
                            : "CPU",
                },
            "dp_speedtest" => value switch
            {
                "up"   => "upload",
                "ping" => "ping",
                _      => "download",
            },
            _ => "",   // clock faces
        };
    }

    /// <summary>Short caption for a "disk:&lt;id&gt;|&lt;name&gt;" value — the first word of the
    /// disk's model, which is what fits a 102 px tile ("Samsung", "WD", "Crucial").</summary>
    private static string DiskCaption(string value)
    {
        int bar = value.IndexOf('|');
        string name = bar >= 0 ? value[(bar + 1)..] : "";
        string first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length > 0 ? first : "DISK";
    }

    /// <summary>What a gauge-style live key (monitor or speed test) shows right now: the text in
    /// the middle and the ring fill. Shared with <c>DpKeyConfigDialog</c>'s preview, so the
    /// dialog and the hardware can't disagree about what a key looks like.</summary>
    internal static (string Text, double? Fraction) TileValue(string type, string? value) => type switch
    {
        "dp_sysmon"    => SysMonValue(value ?? ""),
        "dp_speedtest" => SpeedTestValue(value ?? ""),
        _              => ("", null),
    };

    /// <summary>Current value of a monitor metric: the text on the tile and the ring fill (null
    /// for the throughput metrics, which have no full scale — see
    /// <see cref="LiveTileRenderer.TryRenderGauge"/>).</summary>
    private static (string Text, double? Fraction) SysMonValue(string metric)
    {
        // Never opens LHM here — this is reached from both the tile timer AND (via TileValue)
        // the config dialog's preview on the UI thread. Warm-up is the callers' job:
        // DpLiveTileService.Sync and DpKeyConfigDialog both Task.Run(HardwareSensors.Start).
        // Until it's up the resolvers below just return null → the tile shows "—".

        // A key bound to a specific hardware sensor (HWiNFO-style picker) — "<lhm-id>|<stat>|<label>".
        if (ActionTypeHelper.ParseSensorValue(metric) is { } sensor)
        {
            var reading = HardwareSensors.Get(sensor.Id);
            if (reading is null) return ("—", null);
            var stat = HardwareSensors.ParseStat(sensor.Stat);
            return (reading.Display(stat), reading.Fraction(stat));
        }

        // "PC monitor" preset refinements resolved through the LHM catalogue.
        switch (metric)
        {
            case "cpu:temp": return SensorReadout(HardwareSensors.FindCpuTemp());
            case "gpu:temp": return SensorReadout(HardwareSensors.FindGpuTemp());
        }
        if (metric.StartsWith("disk:"))
        {
            string arg = metric.Substring("disk:".Length);
            int bar = arg.IndexOf('|');
            string hwId = bar >= 0 ? arg[..bar] : arg;
            return SensorReadout(HardwareSensors.FindDiskActivity(hwId));
        }

        // The six legacy built-ins keep flowing through SystemMonitor (no LHM dependency).
        switch (metric)
        {
            case "ram":  { int v = SystemMonitor.RamPercent();  return ($"{v}%", v / 100d); }
            case "gpu":  { int v = SystemMonitor.GpuPercent();  return ($"{v}%", v / 100d); }
            case "disk": { int v = SystemMonitor.DiskPercent(); return ($"{v}%", v / 100d); }
            case "net_down": return (Throughput(SystemMonitor.DownloadBytesPerSec()), null);
            case "net_up":   return (Throughput(SystemMonitor.UploadBytesPerSec()), null);
            default:     { int v = SystemMonitor.CpuPercent();  return ($"{v}%", v / 100d); }
        }
    }

    /// <summary>Formats a resolved LHM sensor's current value for a gauge tile, or "—" when the
    /// sensor isn't there (LHM still warming up, or the disk was unplugged).</summary>
    private static (string Text, double? Fraction) SensorReadout(HardwareSensors.Reading? r)
    {
        if (r is null) return ("—", null);
        return (r.Display(HardwareSensors.Stat.Current), r.Fraction(HardwareSensors.Stat.Current));
    }

    /// <summary>Bytes/s as a short human string. The unit scales down to KB/s rather than rounding
    /// to whole MB (the dock page's unit): a key that reads "0 MB/s" during a normal download
    /// looks broken.</summary>
    private static string Throughput(int bytesPerSec)
    {
        if (bytesPerSec >= 1_000_000)
            return (bytesPerSec / 1_000_000d).ToString(bytesPerSec >= 10_000_000 ? "0" : "0.0",
                                                       CultureInfo.InvariantCulture) + "M";
        if (bytesPerSec >= 1_000)
            return (bytesPerSec / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K";
        return "0";
    }

    /// <summary>Last speed-test figure for this key, and "—" before the first one. While a test
    /// is running, a leg that hasn't reported its own number yet shows a progress ring instead
    /// (ping/download/upload run one after another, not together, so at most one key is ever
    /// mid-ring) — 0% the moment the run starts, same look as the CPU/RAM gauges. The moment a
    /// leg's own number lands it switches to that final value immediately, without waiting for
    /// the other two legs still running — a finished ping shows its ms right away while the
    /// download is still filling its ring.</summary>
    private static (string Text, double? Fraction) SpeedTestValue(string metric)
    {
        double progress = metric switch
        {
            "up"   => SpeedTestService.UploadProgress,
            "ping" => SpeedTestService.PingProgress,
            _      => SpeedTestService.DownloadProgress,
        };
        double? value = metric switch
        {
            "up"   => SpeedTestService.LastUpMbps,
            "ping" => SpeedTestService.LastPingMs,
            _      => SpeedTestService.LastDownMbps,
        };

        if (SpeedTestService.IsRunning && progress < 1)
            return ($"{(int)Math.Round(progress * 100)}%", progress);

        if (value is not double v) return ("—", null);

        string text = metric == "ping"
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return (text, null);
    }

    /// <summary>Small unit strip below the value — just the number's scale ("ms", "Mbps"). No
    /// arrow: the caption above already says "download"/"upload" in words.</summary>
    internal static string SpeedTestUnit(string metric) => metric == "ping" ? "ms" : "Mbps";
}
