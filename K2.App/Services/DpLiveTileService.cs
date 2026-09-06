using System;
using System.Collections.Generic;
using System.Drawing;
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

    private readonly record struct DeviceCtx(IDisplayPadClient Client, Action<string> Log, int Rotation,
                                             LiveKey[] Keys, string? DefaultBg);

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
        actionType is "dp_clock" or "dp_sysmon" or "dp_speedtest" or "dp_edstatus" or "dp_zcstatus"
                   or "dp_screen";

    /// <summary>Registers (or refreshes, or stops) the live keys of one device from the page rows
    /// being painted — called from every repaint path, so the live set always matches the page
    /// actually on the panel. Registration only: the tiles themselves are painted by
    /// <see cref="Repaint"/> at the END of that repaint's batch, otherwise the profile's own
    /// icon for the same key would land on top of them.</summary>
    public static void Sync(IDisplayPadClient client, Action<string> log, int deviceId, int rotation,
                            IEnumerable<DpButtonRecord> rows, string? defaultBgImage = null)
    {
        var keys = rows
            .Where(r => IsLiveType(r.ActionType))
            .Select(r => new LiveKey(r.ButtonIndex, r.ActionType!, (r.ActionValue ?? "").Trim(),
                                     KeyIconSpec.FromJson(r.IconSpec)))
            .ToArray();

        if (keys.Length == 0) { Stop(deviceId); return; }

        lock (_gate)
        {
            _devices[deviceId] = new DeviceCtx(client, log, rotation, keys, defaultBgImage);
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

    /// <summary>True when this key's tile is uploaded WITHOUT the panel's counter-rotation, so
    /// callers that re-upload the same tile outside this service — the hardware press-bounce —
    /// use the same rotation the tile was drawn for. Without it the arrow flipped orientation
    /// for the length of a press on a pad mounted vertical.</summary>
    public static bool SkipsRotation(int deviceId, int buttonIndex)
    {
        LiveKey key;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var ctx)) return false;
            int i = Array.FindIndex(ctx.Keys, k => k.Button == buttonIndex);
            if (i < 0) return false;
            key = ctx.Keys[i];
        }
        return ZcSkipsRotation(key);
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
        if (key.Type == "dp_edstatus") EdSendToggle(key.Value, log);
        if (key.Type == "dp_zcstatus")
        {
            string zcValue = ZcState(key.Value);

            // The two scroll keys are pure UI: they move the panel's window over the soldier's
            // action list and repaint, without sending the game anything.
            if (ZcScrollArrow(zcValue, ZeroCompanyClient.Snapshot()) is { } pressedArrow)
            {
                ZcScroll(pressedArrow, log);
                Repaint(deviceId);
            }
            else if (ZcActionSlot(ZcResolveScrollSlot(zcValue)) is { } slot)
            {
                // A slot is a POSITION on the panel; which ability that is depends on the page.
                ZeroCompanyClient.Send("ability" + (ZcAbilityIndexFor(slot) + 1), log);
            }
            else ZeroCompanyClient.Send(zcValue, log);
        }
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
            // Nothing is being painted any more: a screen probe's cached frame is a full-window
            // bitmap, and holding one for a game that has since closed is pure waste.
            ScreenProbeReader.Flush();
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
                string stamp = StampOf(key, now, ctx.DefaultBg);
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
                if (!Render(key, now, path, ctx.DefaultBg))
                {
                    App.WriteLog($"[LIVE] btn{key.Button} {key.Type} render returned false");
                    continue;
                }
                ctx.Client.UploadImage(deviceId, path, key.Button,
                                       ZcSkipsRotation(key) ? 0 : ctx.Rotation);
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
    public static bool RenderNow(string type, string? value, KeyIconSpec? spec, string outputPngPath,
                                 string? defaultBgImage = null) =>
        Render(new LiveKey(0, type, (value ?? "").Trim(), spec), DateTime.Now, outputPngPath, defaultBgImage);

    // ─────────────────────── Content ───────────────────────

    /// <summary>What the key shows right now, as a string — the change detector (see the class
    /// remarks). Includes the caption, so a style/text change repaints too.</summary>
    private static string StampOf(LiveKey key, DateTime now, string? defaultBg) => key.Type switch
    {
        "dp_clock"     => "c:" + LiveTileRenderer.ClockStamp(key.Value, now),
        "dp_sysmon"    => "s:" + SysMonValue(key.Value).Text,
        "dp_speedtest" => "t:" + SpeedTestValue(key.Value).Text,
        "dp_edstatus"  => "e:" + EdStamp(key.Value) + ":" + EdAccentFor(
                              ParseEdValue(key.Value).State, ParseEdValue(key.Value).Color,
                              HasExplicitEdColor(key.Value)).ToArgb()
                          // A blinking tile changes picture on its own every second: without the
                          // phase in the stamp the change detector sees no difference and never
                          // re-uploads it.
                          + (ParseEdValue(key.Value).Blink ? ":b" + (BlinkOnPhase(now) ? 1 : 0) : ""),
        "dp_zcstatus"  => "z:" + ZcStamp(key.Value),
        // The probe's own reading, rounded exactly as the tile draws it: a health bar that
        // wobbles by a fraction of a percent must not cost an upload a second.
        "dp_screen"    => "p:" + ScreenValue(key.Value).Text,
        _              => "",
    } + "|" + Caption(key) + "|bg:" + (EffectiveBg(key.Spec, defaultBg) ?? "");

    /// <summary>The pad-wide "default icon background" image to bake behind this tile: the pad's
    /// image unless the key opted out. A live tile is always a default icon.</summary>
    private static string? EffectiveBg(KeyIconSpec? spec, string? defaultBg) =>
        defaultBg is not null && spec is not { NoDefaultBg: true } ? defaultBg : null;

    private static bool Render(LiveKey key, DateTime now, string path, string? defaultBg = null)
    {
        var spec = key.Spec;
        if (EffectiveBg(spec, defaultBg) is { } bg)
        {
            spec = (spec ?? new KeyIconSpec { DefaultIcon = true }).Clone();
            spec.BgImagePath = bg;
        }
        using (IconStyleScope.Push(spec))
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
                case "dp_edstatus":
                {
                    var edParsed = ParseEdValue(key.Value);
                    string edState = edParsed.State;
                    bool edExplicit = HasExplicitEdColor(key.Value);
                    bool edBlink = edParsed.Blink;
                    LiveTileRenderer.EdAccentOverride = EdAccentFor(edState, edParsed.Color, edExplicit);
                    var ed = EliteStatusReader.Snapshot();
                    if (edState == "pips")
                        return LiveTileRenderer.TryRenderEdPips(ed.PipsSys, ed.PipsEng, ed.PipsWep,
                            caption, DpHidNative.IconSize, path);
                    if (EdGaugeStates.Contains(edState))
                    {
                        var g = EdGaugeValue(edState, ed);
                        ApplyEdStyle(g.Fraction is null
                            ? LiveTileRenderer.EdState.Unknown : LiveTileRenderer.EdState.On,
                            edState, edParsed.Color, edExplicit, edBlink, now);
                        return LiveTileRenderer.TryRenderGauge(g.Text, g.Fraction, caption,
                            DpHidNative.IconSize, path);
                    }
                    var state = EdStateOf(edState, ed);
                    ApplyEdStyle(state, edState, edParsed.Color, edExplicit, edBlink, now);
                    return LiveTileRenderer.TryRenderEdStatus(EdRenderState(edState, state),
                        caption, DpHidNative.IconSize, path);
                }
                case "dp_zcstatus":
                    return RenderZcTile(key.Value, caption, DpHidNative.IconSize, path);
                case "dp_screen":
                    return RenderScreenTile(key.Value, caption, DpHidNative.IconSize, path);
                default:
                    return false;
            }
        }
    }

    // ─────────────────── Star Wars: Zero Company tactical tiles ───────────────────

    /// <summary>The state token of a Zero Company tile. The value has no extra fields today (no
    /// per-key shortcut, no colour: the commands are real API calls and the page has one hue),
    /// but it is parsed through one function so adding a field later is a single edit.</summary>
    private static string ZcState(string? value) => (value ?? "").Trim();

    /// <summary>Renders one Zero Company tile with the current tactical state. Shared by the
    /// hardware repaint and the still-image paths, exactly like <see cref="RenderEdTile"/>.
    ///
    /// <para>Two shapes: the squad/selected/round values are GAUGES (a number in a ring — health
    /// left, AP left), everything else is the on/off annunciator. A tile whose subject isn't
    /// there — a fourth squad slot in a three-member mission, an ability with nobody selected —
    /// paints Unknown rather than a confident zero.</para></summary>
    internal static bool RenderZcTile(string? value, string caption, int size, string outputPngPath)
    {
        string v = ZcState(value);
        var zc = ZeroCompanyClient.Snapshot();
        LiveTileRenderer.EdAccentOverride = GameProfileTheme.AccentFor(GameProfileSpecs.ZeroCompanyId);

        // A squad key carries the whole soldier — health dial and number on the left, action
        // points as bars on the right — so it is drawn by its own renderer rather than by the
        // generic gauge. Everything else on the page is still a gauge or an annunciator.
        // ORDER MATTERS: key 6 is the sixth soldier on page 0 and the UP arrow once scrolled,
        // and the squad branch below will happily draw it as a soldier — which is what it did
        // (user report 2026-09-06: "l'icona dell'operative 6 non diventa la freccia su"). The
        // arrow test has to come FIRST.
        ZcClampPage(zc);
        if (ZcScrollArrow(v, zc) is { } arrow)
        {
            if (IconStyleScope.Current is { } arrowSpec) arrowSpec.BgImagePath = null;
            return LiveTileRenderer.TryRenderScrollArrow(
                arrow, enabled: true, size, outputPngPath,
                GameProfileTheme.AccentFor(GameProfileSpecs.ZeroCompanyId),
                GameProfileTheme.ScrollArrowPath(GameProfileSpecs.ZeroCompanyId, arrow));
        }

        if (ZcUnitIndex(v) is { } unitIndex)
        {
            // No soldier in this slot — a four-man squad on a six-key row, or the game not
            // running at all. A dial reading "—" looked like a soldier whose health had failed
            // to arrive; the placeholder says "nothing here" instead.
            if (zc.At(unitIndex) is not { } u)
                return ZcPlaceholderTile(ability: false, size, outputPngPath);
            ApplyZcStyle(LiveTileRenderer.EdState.On);
            // The soldier whose turn the game is waiting on wears a frame of his own, so the pad
            // says who the action row belongs to. Only the frame changes — the dial, the number
            // and the bars are read the same way on every key.
            if (unitIndex == zc.SelectedIndex &&
                GameProfileTheme.SelectedBgPath(GameProfileSpecs.ZeroCompanyId) is { } selBg &&
                IconStyleScope.Current is { } selSpec)
            {
                selSpec.BgImagePath = selBg;
                // White name to go with the brighter frame: on a row of blue captions the
                // selected one has to be findable without reading any of them.
                selSpec.TextColor = "#FFFFFF";
            }
            return LiveTileRenderer.TryRenderUnitTile(
                u.Hp.ToString(),
                u.MaxHp > 0 ? Math.Clamp(u.Hp / (double)u.MaxHp, 0, 1) : null,
                u.Ap, u.MaxAp,
                caption, size, outputPngPath,
                GameProfileTheme.AccentFor(GameProfileSpecs.ZeroCompanyId));
        }

        // On a scrolled page the DOWN arrow's key carries an action like the rest of the row.
        v = ZcResolveScrollSlot(v);

        // An action slot: the game's own icon when one has been exported, the name otherwise.
        if (ZcActionSlot(v) is { } actionSlot)
        {
            int abilitySlot = ZcAbilityIndexFor(actionSlot) + 1;
            // Nothing in this slot: the soldier's bar is shorter than the row, or nobody is
            // selected. Placeholder art rather than a bare frame, so the key still looks like a
            // key and not like one whose render failed.
            if (zc.AbilityAt(abilitySlot - 1) is not { } ability)
                return ZcPlaceholderTile(ability: true, size, outputPngPath);
            var abilityState = ZcStateOf(v, zc);
            ApplyZcStyle(abilityState);
            // No caption when there IS an icon: the picture is the label, and on a 102 px key
            // the two together leave neither of them room. A slot with no icon keeps its name,
            // otherwise the key would be blank.
            string? iconPath = ZeroCompanyClient.AbilityIconPath(ability);
            return LiveTileRenderer.TryRenderAbilityTile(
                iconPath,
                iconPath is null ? caption : "", abilityState is LiveTileRenderer.EdState.On,
                size, outputPngPath,
                GameProfileTheme.AccentFor(GameProfileSpecs.ZeroCompanyId));
        }

        if (ZcGaugeOf(v, zc) is { } gauge)
        {
            ApplyZcStyle(gauge.Fraction is null
                ? LiveTileRenderer.EdState.Unknown : LiveTileRenderer.EdState.On);
            return LiveTileRenderer.TryRenderSegmentedGauge(gauge.Text, gauge.Fraction, caption,
                size, outputPngPath,
                GameProfileTheme.AccentFor(GameProfileSpecs.ZeroCompanyId), gauge.Segments);
        }

        var state = ZcStateOf(v, zc);
        ApplyZcStyle(state);
        return LiveTileRenderer.TryRenderEdStatus(state, caption, size, outputPngPath);
    }

    /// <summary>Value and ring fill for the tiles that show a QUANTITY, or null for the ones that
    /// are a plain on/off light. A null fraction means "no subject": the number is drawn as a
    /// dash and the ring stays empty.</summary>
    /// <summary>Which page of the action list the panel is showing. Page 0 is the whole panel:
    /// four soldiers on top and seven action slots below. From page 1 on, the soldiers scroll
    /// away and slot 2 becomes the UP arrow, so a page holds six actions.
    ///
    /// <para>One value for the whole app rather than one per pad: a profile drives a single
    /// DisplayPad, and a second pad showing the same profile would want the same page.</para></summary>
    private static int _zcPage;

    /// <summary>Actions the bottom row holds: the DisplayPad is six keys by two, so the row is
    /// five actions and the DOWN arrow.</summary>
    private const int ZcActionSlots = 5;

    /// <summary>Actions on a scrolled page. There is no DOWN arrow once the panel has scrolled —
    /// nothing is below it — so that key carries an action too and the row holds six.</summary>
    private const int ZcScrolledActionSlots = 6;

    /// <summary>Bottom-row slot the DOWN arrow occupies on page 0, and which becomes an action
    /// slot on every page after it.</summary>
    private const int ZcDownArrowSlot = 6;

    /// <summary>Key 6 — last of the TOP row, the sixth soldier — becomes the UP arrow while the
    /// actions are scrolled. Key 12 is the DOWN arrow always.</summary>
    private const int ZcUpArrowButton = 5;

    /// <summary>Slot number of an <c>actN</c> value, or of a legacy <c>abilityN</c> one — the
    /// values a profile saved before the panel could scroll, kept working as fixed slots.</summary>
    private static int? ZcActionSlot(string v)
    {
        foreach (string prefix in new[] { "act", "ability" })
            if (v.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(v.AsSpan(prefix.Length), out int n) && n >= 1)
                return n;
        return null;
    }

    /// <summary>Which arrow a key is showing, or null when it is not an arrow.
    ///
    /// <para>DOWN lives on the bottom row's last key and ONLY on page 0: once scrolled there is
    /// nothing below it, so that key goes back to being an action. UP is the sixth soldier's key,
    /// and only while scrolled.</para></summary>
    private static bool? ZcScrollArrow(string v, ZeroCompanyClient.Status zc)
    {
        if (_zcPage > 0 && ZcUnitIndex(v) == ZcUpArrowButton) return false;
        if (_zcPage == 0 && v == "scroll" && ZcCanScrollDown(zc)) return true;
        return null;
    }

    /// <summary>The value a key acts as: on a scrolled page the DOWN arrow's key is action slot
    /// six. Everything that reads a tile's value — painter, caption, lit state, press — goes
    /// through this, or the key would keep meaning "scroll" while showing an ability.</summary>
    private static string ZcResolveScrollSlot(string v) =>
        _zcPage > 0 && v == "scroll" ? "act" + ZcDownArrowSlot : v;

    /// <summary>Pulls the page back to the top when it no longer has anything on it — the squad
    /// changed soldier, or this one simply has a shorter wheel. Without it the panel could sit on
    /// a page of empty slots with no way back except the arrow the user cannot see.</summary>
    private static void ZcClampPage(ZeroCompanyClient.Status zc)
    {
        if (_zcPage == 0) return;
        if (ZcAbilityIndexFor(1) >= zc.Abilities.Count) _zcPage = 0;
    }

    /// <summary>The ability a panel slot is showing on the current page. Page 0 maps slots
    /// straight through; later pages skip the slot the UP arrow took over, so the abilities stay
    /// consecutive across the page break instead of leaving a hole where the arrow sits.</summary>
    /// <summary>The ability a bottom-row slot is showing: its position plus whatever the panel
    /// is scrolled past. Every page holds the same five, so the arithmetic is a straight
    /// window over the soldier's action list.</summary>
    /// <summary>The ability a bottom-row slot shows. Page 0 holds five (its sixth key is the
    /// DOWN arrow); every page after holds six, that key being an action again.</summary>
    private static int ZcAbilityIndexFor(int slot) =>
        _zcPage == 0
            ? slot - 1
            : ZcActionSlots + (_zcPage - 1) * ZcScrolledActionSlots + (slot - 1);

    /// <summary>Whether there is another page of abilities under the current one.</summary>
    private static bool ZcCanScrollDown(ZeroCompanyClient.Status zc) =>
        zc.Abilities.Count > (_zcPage == 0
            ? ZcActionSlots
            : ZcActionSlots + _zcPage * ZcScrolledActionSlots);

    /// <summary>Moves the panel one page. Scrolling down past the last page wraps back to the
    /// top: the alternative is a key that looks pressable and does nothing.</summary>
    /// <summary>DOWN goes to the next page, UP comes back to the top.</summary>
    private static void ZcScroll(bool down, Action<string> log)
    {
        var zc = ZeroCompanyClient.Snapshot();
        _zcPage = down && ZcCanScrollDown(zc) ? _zcPage + 1 : 0;
        log($"[ZC] action panel -> page {_zcPage}");
    }

    /// <summary>Squad slot a value names (0-based), or null when it names something else.</summary>
    private static int? ZcUnitIndex(string v) =>
        v.Length == 5 && v.StartsWith("unit", StringComparison.Ordinal) && v[4] is >= '1' and <= '6'
            ? v[4] - '1'
            : null;

    private static (string Text, double? Fraction, int Segments)? ZcGaugeOf(
        string v, ZeroCompanyClient.Status zc)
    {
        ZeroCompanyClient.Unit? unit = v switch
        {
            "sel_hp" or "sel_ap" or "sel_armor" => zc.Selected,
            _ => null,
        };

        switch (v)
        {
            case "sel_hp":
                return unit is { } u
                    ? ($"{u.Hp}", u.MaxHp > 0 ? Math.Clamp(u.Hp / (double)u.MaxHp, 0, 1) : (double?)null,
                       HealthTicks)
                    : ("—", null, HealthTicks);
            case "sel_ap":
                // One block per action point: the dial then IS the game's AP pips, which is what
                // the player is already reading on screen.
                return unit is { } a
                    ? ($"{a.Ap}", a.MaxAp > 0 ? Math.Clamp(a.Ap / (double)a.MaxAp, 0, 1) : (double?)null,
                       Math.Max(a.MaxAp, 1))
                    : ("—", null, 3);
            case "sel_armor":
                return unit is { } r
                    ? ($"{r.Armor}", r.Armor > 0 ? 1d : 0d, Math.Max(r.Armor, 1))
                    : ("—", null, 3);
            case "round":
                // A round has no maximum to fill a dial with, so the number carries it alone.
                return zc.Valid ? ($"{zc.Round}", 1d, HealthTicks) : ("—", null, HealthTicks);
            default:
                return null;
        }
    }

    /// <summary>Blocks in a health dial. Health runs to dozens, so it gets a fixed dozen ticks
    /// (a manometer's scale) instead of one block per point, which at 35 HP would be a dashed
    /// ring nobody can count.</summary>
    private const int HealthTicks = 12;

    /// <summary>On/off/unknown for the annunciator tiles. "Unknown" is reserved for "we are not
    /// in a mission (or the API is off)" — an ability key with nobody selected is honestly OFF,
    /// not unknown, because the game is right there answering us.</summary>
    private static LiveTileRenderer.EdState ZcStateOf(string v, ZeroCompanyClient.Status zc)
    {
        if (!zc.Valid) return LiveTileRenderer.EdState.Unknown;
        return v switch
        {
            "turn"        => zc.PlayerTurn ? LiveTileRenderer.EdState.On : LiveTileRenderer.EdState.Alarm,
            "sel_canact"  => zc.CanAct ? LiveTileRenderer.EdState.On : LiveTileRenderer.EdState.Off,
            "nextchar" or "prevchar" => zc.Units.Count > 1
                ? LiveTileRenderer.EdState.On : LiveTileRenderer.EdState.Off,
            // An ability key is lit while it can actually be opened: the player's turn, someone
            // selected, and that someone still able to act.
            _ when ZcActionSlot(ZcResolveScrollSlot(v)) is { } slot =>
                // Lit when the slot HAS an ability on this page and the soldier could use it: an
                // empty slot stays dark rather than pretending to hold something.
                zc.PlayerTurn && zc.CanAct && zc.AbilityAt(ZcAbilityIndexFor(slot)) is not null
                    ? LiveTileRenderer.EdState.On : LiveTileRenderer.EdState.Off,
            _ => LiveTileRenderer.EdState.Off,
        };
    }

    /// <summary>True for a Zero Company key currently showing a scroll ARROW, which is uploaded
    /// WITHOUT the panel's counter-rotation.
    ///
    /// <para>Every other tile is counter-rotated so its artwork stands upright however the pad is
    /// mounted. An arrow is different: it points at the row it scrolls to, and on a pad turned
    /// vertical the action row runs sideways, so an arrow kept "upright" would point away from
    /// the keys it moves. Leaving the panel's rotation in turns the up/down pair into a
    /// left/right pair, which is what the layout actually means.</para></summary>
    private static bool ZcSkipsRotation(LiveKey key) =>
        key.Type == "dp_zcstatus" &&
        ZcScrollArrow(ZcState(key.Value), ZeroCompanyClient.Snapshot()) is not null;

    /// <summary>How much smaller the placeholder glyph of an EMPTY slot is drawn than a real
    /// ability icon, and how faded. It has to read as part of the frame rather than as a picture
    /// of something the soldier can do.</summary>
    private const float ZcPlaceholderScale = 0.85f;
    private const float ZcPlaceholderOpacity = 0.5f;

    /// <summary>The empty-SOLDIER glyph is smaller again — 40% off the ability placeholder. It is
    /// a solid silhouette rather than a line drawing, so at the same size it weighed far more on
    /// the key than the ability one does.</summary>
    private const float ZcUnitPlaceholderScale = ZcPlaceholderScale * 0.60f;

    /// <summary>A slot the game has left empty: the dim frame with the profile's own placeholder
    /// glyph in the middle, no caption. Both kinds of key use one renderer — the tile is just a
    /// centred picture either way — and differ only in which PNG they point at.</summary>
    private static bool ZcPlaceholderTile(bool ability, int size, string outputPngPath)
    {
        ApplyZcStyle(LiveTileRenderer.EdState.Unknown);
        return LiveTileRenderer.TryRenderAbilityTile(
            GameProfileTheme.PlaceholderIconPath(GameProfileSpecs.ZeroCompanyId, ability),
            caption: "", lit: false, size, outputPngPath,
            GameProfileTheme.AccentFor(GameProfileSpecs.ZeroCompanyId),
            iconScale: ability ? ZcPlaceholderScale : ZcUnitPlaceholderScale,
            iconOpacity: ZcPlaceholderOpacity);
    }

    /// <summary>Swaps the ambient icon spec to the profile's lit/dim art, the Zero Company
    /// counterpart of <see cref="ApplyEdStyle"/> — simpler because this game's tiles have one
    /// hue: only the brightness carries state.</summary>
    private static void ApplyZcStyle(LiveTileRenderer.EdState state)
    {
        if (IconStyleScope.Current is not { } spec) return;
        bool lit = state is LiveTileRenderer.EdState.On or LiveTileRenderer.EdState.Alarm;
        if (GameProfileTheme.BgImagePathFor(GameProfileSpecs.ZeroCompanyId, lit) is { } bg)
            spec.BgImagePath = bg;
        spec.TextColor = GameProfileTheme.TextHexFor(GameProfileSpecs.ZeroCompanyId, lit);
    }

    /// <summary>Caption of a squad key: the soldier's name when the game has told us who is in
    /// that slot, otherwise the numbered fallback. Upper-cased to sit with the rest of the page's
    /// legends, and left for the renderer's shrink-to-fit if a name runs long.</summary>
    private static string ZcUnitCaption(int index)
    {
        var zc = ZeroCompanyClient.Snapshot();
        if (zc.At(index) is { } u && u.Name is { Length: > 0 })
            // First name over last name: the caption strip is two lines tall, and a name broken
            // where it belongs reads better than one shrunk to fit across a single line.
            return u.Name.ToUpperInvariant().Replace(' ', '\n');
        return Loc.Get("zctile_unit") + "\n" + (index + 1);
    }

    /// <summary>Caption of an ability key: the ability's own name when the game tells us what
    /// is in that slot, otherwise the numbered fallback.</summary>
    private static string ZcAbilityCaption(int slot)
    {

        var zc = ZeroCompanyClient.Snapshot();
        if (zc.AbilityAt(ZcAbilityIndexFor(slot)) is { } ability && ability.Name is { Length: > 0 })
            // Two words go on the tile's two caption lines; anything longer keeps its spaces and
            // is left to the renderer's shrink-to-fit.
            return ability.Name.Replace(' ', '\n');

        // Nothing in this slot: the key is its frame and nothing else. A numbered placeholder
        // ("ABILITY 6") read as an ability whose icon had failed to load, when in fact the
        // soldier simply has no sixth action.
        return "";
    }

    /// <summary>Change stamp for a Zero Company tile — the rendered reading, so a tile only
    /// re-uploads when its own number or light moved and not on every unrelated squad change.</summary>
    private static string ZcStamp(string? value)
    {
        string v = ZcState(value);
        var zc = ZeroCompanyClient.Snapshot();
        if (ZcScrollArrow(v, zc) is { } stampArrow) return "arrow:" + (stampArrow ? "d" : "u");
        v = ZcResolveScrollSlot(v);

        if (ZcActionSlot(v) is { } stampSlot)
        {
            var a = zc.AbilityAt(ZcAbilityIndexFor(stampSlot));
            return $"a:{_zcPage}:{stampSlot}:{a?.Name ?? "-"}:{ZcStateOf(v, zc)}";
        }

        if (ZcUnitIndex(v) is { } idx)
        {
            var u = zc.At(idx);
            // The selection is part of the reading: it decides the frame, and without it here a
            // key would keep the selected frame after the turn moved on to somebody else.
            string sel = idx == zc.SelectedIndex ? ":sel" : "";
            return u is { } m ? $"u:{m.Name}:{m.Hp}/{m.MaxHp}:{m.Ap}/{m.MaxAp}{sel}" : "u:-";
        }
        if (ZcGaugeOf(v, zc) is { } g)
            return "g:" + v + ":" + g.Text + ":" + (g.Fraction?.ToString("0.00") ?? "-") + ":" + g.Segments;
        return v + ":" + ZcStateOf(v, zc);
    }

    // ─────────────────── Elite Dangerous status tiles ───────────────────

    /// <summary>Which <c>Status.json</c> flag backs each tile value, whether a set bit is an alarm
    /// rather than a plain "on", and whether the bit must be read INVERTED.
    ///
    /// <para>Flight assist is the odd one, and it needs BOTH readings — the game only gives us a
    /// "FlightAssist<b>Off</b>" bit, so which one a tile wants depends on what it is captioned:
    /// <list type="bullet">
    /// <item><c>flightassist</c> — the annunciator, captioned "Flight assist OFF": lights (red,
    /// alarm) when the bit is set. This is the reading the generic <c>dp_edstatus</c> action has
    /// always offered and it stays untouched.</item>
    /// <item><c>flightassist_on</c> — the cockpit-panel key, captioned just "FLIGHT ASSIST": lit
    /// when assist is actually ON, like LANDING GEAR is lit when the gear is down. The curated
    /// Elite profile uses this one; using the annunciator there is what made the key read
    /// backwards from its own label (user report 2026-09-05).</item>
    /// </list></para>
    /// See <see cref="ActionTypeHelper.EdStatusItems"/>.</summary>
    /// <para><c>Flags2</c> is a SECOND bitfield with its own bit meanings (the Odyssey on-foot
    /// state), so an entry says which word it reads — see <c>EliteStatusReader.Status.Has2</c>.</para>
    private static readonly Dictionary<string, (uint Mask, bool Alarm, bool Invert, bool Second)> EdFlagMap = new()
    {
        ["gear"]            = (EliteStatusReader.FlagLandingGear,     false, false, false),
        ["scoop"]           = (EliteStatusReader.FlagCargoScoop,      false, false, false),
        ["lights"]          = (EliteStatusReader.FlagLightsOn,        false, false, false),
        ["hardpoints"]      = (EliteStatusReader.FlagHardpoints,      false, false, false),
        ["flightassist"]    = (EliteStatusReader.FlagFlightAssistOff, true,  false, false),
        ["flightassist_on"] = (EliteStatusReader.FlagFlightAssistOff, false, true, false),
        ["silent"]          = (EliteStatusReader.FlagSilentRunning,   false, false, false),
        ["shields"]         = (EliteStatusReader.FlagShieldsUp,       false, false, false),
        ["supercruise"]     = (EliteStatusReader.FlagSupercruise,     false, false, false),
        ["nightvision"]     = (EliteStatusReader.FlagNightVision,     false, false, false),
        ["overheat"]        = (EliteStatusReader.FlagOverHeating,     true,  false, false),
        ["lowfuel"]         = (EliteStatusReader.FlagLowFuel,         true,  false, false),
        ["danger"]          = (EliteStatusReader.FlagInDanger,        true,  false, false),

        // ── Flight / navigation ──
        ["docked"]          = (EliteStatusReader.FlagDocked,           false, false, false),
        ["landed"]          = (EliteStatusReader.FlagLanded,           false, false, false),
        ["wing"]            = (EliteStatusReader.FlagInWing,           false, false, false),
        ["scooping"]        = (EliteStatusReader.FlagScoopingFuel,     false, false, false),
        ["masslock"]        = (EliteStatusReader.FlagFsdMassLocked,    true,  false, false),
        ["fsdcharging"]     = (EliteStatusReader.FlagFsdCharging,      false, false, false),
        ["fsdcooldown"]     = (EliteStatusReader.FlagFsdCooldown,      true,  false, false),
        ["fsdjump"]         = (EliteStatusReader.FlagFsdJump,          false, false, false),
        ["interdicted"]     = (EliteStatusReader.FlagInterdicted,      true,  false, false),
        ["analysis"]        = (EliteStatusReader.FlagAnalysisMode,     false, false, false),
        ["glide"]           = (EliteStatusReader.Flag2GlideMode,       false, false, true),

        // ── Which vehicle the commander is in ──
        ["inship"]          = (EliteStatusReader.FlagInMainShip,       false, false, false),
        ["infighter"]       = (EliteStatusReader.FlagInFighter,        false, false, false),
        ["insrv"]           = (EliteStatusReader.FlagInSrv,            false, false, false),

        // ── SRV ──
        ["srvhandbrake"]    = (EliteStatusReader.FlagSrvHandbrake,     false, false, false),
        ["srvturret"]       = (EliteStatusReader.FlagSrvTurret,        false, false, false),
        ["srvdriveassist"]  = (EliteStatusReader.FlagSrvDriveAssist,   false, false, false),
        ["srvhighbeam"]     = (EliteStatusReader.FlagSrvHighBeam,      false, false, false),

        // ── On foot (Odyssey), read from the Flags2 word ──
        ["onfoot"]          = (EliteStatusReader.Flag2OnFoot,          false, false, true),
        ["intaxi"]          = (EliteStatusReader.Flag2InTaxi,          false, false, true),
        ["multicrew"]       = (EliteStatusReader.Flag2InMulticrew,     false, false, true),
        ["aimdownsight"]    = (EliteStatusReader.Flag2AimDownSight,    false, false, true),
        ["breathable"]      = (EliteStatusReader.Flag2BreathableAtmos, false, false, true),
        ["lowoxygen"]       = (EliteStatusReader.Flag2LowOxygen,       true,  false, true),
        ["lowhealth"]       = (EliteStatusReader.Flag2LowHealth,       true,  false, true),
        ["verycold"]        = (EliteStatusReader.Flag2VeryCold,        true,  false, true),
        ["veryhot"]         = (EliteStatusReader.Flag2VeryHot,         true,  false, true),
    };

    /// <summary>Tile values that draw a GAUGE (a filled ring with a number) instead of the
    /// on/off annunciator: they mirror a quantity, not a bit. See <see cref="EdGaugeValue"/>.</summary>
    private static readonly HashSet<string> EdGaugeStates = new() { "fuel", "fuelres", "cargo" };

    private static LiveTileRenderer.EdState EdStateOf(string value, EliteStatusReader.Status ed)
    {
        if (!ed.Valid) return LiveTileRenderer.EdState.Unknown;
        if (!EdFlagMap.TryGetValue(value, out var f))
            f = EdFlagMap["gear"];                       // never configured: the picker's first entry
        bool set = (f.Second ? ed.Has2(f.Mask) : ed.Has(f.Mask)) ^ f.Invert;
        if (!set) return LiveTileRenderer.EdState.Off;
        return f.Alarm ? LiveTileRenderer.EdState.Alarm : LiveTileRenderer.EdState.On;
    }

    /// <summary>What a gauge-style Elite tile shows: the number in the middle and the ring fill.
    ///
    /// <para>The fill needs a denominator the status file does not carry, so the tank and hold
    /// sizes come from the journal's <c>Loadout</c> line (see
    /// <c>EliteStatusReader.FuelCapacity</c>). Until that is known — a commander who has not
    /// flown since installing K2 — the tile still shows the absolute tonnage and simply draws no
    /// ring, which is honest rather than inventing a full scale.</para></summary>
    private static (string Text, double? Fraction) EdGaugeValue(string state, EliteStatusReader.Status ed)
    {
        if (!ed.Valid) return ("--", null);

        switch (state)
        {
            case "cargo":
            {
                int cap = EliteStatusReader.CargoCapacity;
                return cap > 0
                    ? ($"{ed.Cargo}/{cap}", Math.Clamp(ed.Cargo / (double)cap, 0, 1))
                    : ($"{ed.Cargo}t", null);
            }
            case "fuelres":
                // The reservoir is a fraction of a ton and always tiny; two decimals or it reads 0.
                return (ed.FuelReservoir.ToString("0.00", CultureInfo.InvariantCulture), null);
            default:
            {
                double cap = EliteStatusReader.FuelCapacity;
                return cap > 0
                    ? ((ed.FuelMain / cap * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                       Math.Clamp(ed.FuelMain / cap, 0, 1))
                    : (ed.FuelMain.ToString("0.0", CultureInfo.InvariantCulture) + "t", null);
            }
        }
    }

    /// <summary>The keystroke an Elite tile sends when pressed — so a state tile is also the
    /// BUTTON for that state (press CARRELLO, the gear comes down and the tile lights up),
    /// instead of being an inert readout sitting next to a separate action key.
    ///
    /// <para>These are the game's DEFAULT keyboard binds, read from its own shipped preset
    /// (<c>ControlSchemes\KeyboardMouseOnly.binds</c>) rather than from a community list. A
    /// commander who rebound a control can override per key by storing the value as
    /// <c>"&lt;state&gt;|&lt;shortcut&gt;"</c> (e.g. <c>"gear|Ctrl + G"</c>) — the part after
    /// the bar goes to <see cref="ButtonActionEngine"/>'s shortcut path verbatim.</para>
    ///
    /// <para>States with no default keyboard bind, and the ones that are pure annunciators
    /// (overheating, low fuel, danger, pips), send nothing: there is no action to take.</para>
    /// </summary>
    private static readonly Dictionary<string, string> EdDefaultKeys = new()
    {
        ["gear"]         = "L",
        ["scoop"]        = "Home",
        ["lights"]       = "Insert",
        ["hardpoints"]   = "U",
        ["flightassist"] = "Z",
        ["flightassist_on"] = "Z",
        ["silent"]       = "Delete",
        ["supercruise"]  = "J",
    };

    /// <summary>Splits an Elite tile value into its state token and the optional per-key
    /// shortcut override after a bar.</summary>
    internal static (string State, string? Keys, ActionTypeHelper.EdTileColor Color, bool Blink) ParseEdValue(string? value) =>
        ActionTypeHelper.SplitEdStatusValue(value);

    private static void EdSendToggle(string value, Action<string> log)
    {
        var (state, overrideKeys, _, _) = ParseEdValue(value);
        string? keys = overrideKeys is { Length: > 0 } ? overrideKeys
                     : EdDefaultKeys.GetValueOrDefault(state);
        if (string.IsNullOrEmpty(keys)) return;      // annunciator-only tile

        // Same SendInput-first path every "Keyboard Shortcuts" action uses, so the game sees an
        // ordinary keystroke (Elite reads scan codes; the SendKeys fallback alone is unreliable).
        if (HotkeySender.TrySend(keys, out _))
            log($"[ED] btn press -> {state} sends \"{keys}\" (SendInput)");
        else
        {
            System.Windows.Forms.SendKeys.SendWait(SendKeysTranslator.Translate(keys));
            log($"[ED] btn press -> {state} sends \"{keys}\" (sendkeys)");
        }
    }

    /// <summary>Renders one Elite tile with the CURRENT cockpit state, for the still-image
    /// paths (default-icon generation and the key config dialog's preview) — the hardware's own
    /// repaint goes through <see cref="Render"/>.</summary>
    internal static bool RenderEdTile(string? value, string caption, int size, string outputPngPath)
    {
        var parsed = ParseEdValue(value);
        string v = parsed.State;
        bool explicitColor = HasExplicitEdColor(value);
        LiveTileRenderer.EdAccentOverride = EdAccentFor(v, parsed.Color, explicitColor);
        var ed = EliteStatusReader.Snapshot();
        if (v == "pips")
            return LiveTileRenderer.TryRenderEdPips(ed.PipsSys, ed.PipsEng, ed.PipsWep, caption, size, outputPngPath);
        if (EdGaugeStates.Contains(v))
        {
            var g = EdGaugeValue(v, ed);
            ApplyEdStyle(g.Fraction is null
                ? LiveTileRenderer.EdState.Unknown : LiveTileRenderer.EdState.On,
                v, parsed.Color, explicitColor, parsed.Blink, DateTime.Now);
            return LiveTileRenderer.TryRenderGauge(g.Text, g.Fraction, caption, size, outputPngPath);
        }
        var state = EdStateOf(v, ed);
        ApplyEdStyle(state, v, parsed.Color, explicitColor, parsed.Blink, DateTime.Now);
        return LiveTileRenderer.TryRenderEdStatus(EdRenderState(v, state), caption, size, outputPngPath);
    }

    /// <summary>Swaps the currently-pushed <see cref="IconStyleScope"/> spec's background image
    /// AND text colour for the variant matching this tile's OWN cockpit flag (lit when on/alarm,
    /// dim when off) — mutated in place on the ambient spec, since both live-tile render paths
    /// already pushed it before reaching this call. Recomputing the text colour here (rather than
    /// trusting whatever was last written to the key's stored spec) is what keeps the caption in
    /// step with the background on every tick, even between the once-per-flip page rewrites
    /// (<c>MainWindow.GameProfiles.PollGameAccent</c>) that would otherwise leave it stale. The
    /// hue (green/orange) still follows flight assist regardless of this tile's own state; only
    /// the lit/dim variant changes here. No-op for anything but the curated Elite Dangerous
    /// profile (<see cref="GameProfileTheme.BgImagePathFor"/> returns null for every other
    /// profile).</summary>
    private static void ApplyEdStyle(LiveTileRenderer.EdState state, string edState,
                                     ActionTypeHelper.EdTileColor color, bool explicitColor,
                                     bool blink, DateTime now)
    {
        if (IconStyleScope.Current is not { } spec) return;

        // "Lit" is the key's OWN cockpit flag; a blinking key drops back to its dim art on every
        // other second while that flag is set, so the blink IS the on-state rather than a second
        // signal layered over it. An off or unknown key never blinks — there is nothing to
        // alternate with.
        bool lit = state is LiveTileRenderer.EdState.On or LiveTileRenderer.EdState.Alarm;
        if (blink && lit && !BlinkOnPhase(now)) lit = false;

        // A colour the pilot chose for this key outranks everything else, flight assist included:
        // they asked for that key to read green, so it reads green.
        if (explicitColor)
        {
            if (GameProfileTheme.BgImagePathForColor(color, lit) is { } cbg) spec.BgImagePath = cbg;
            spec.TextColor = GameProfileTheme.TextHexForColor(color, lit);
            return;
        }

        // The flight-assist key is the one tile with its own palette: red art while assist is
        // off, green while it is on. It is never "resting", so it takes the LIT art unless a
        // blink's dark half says otherwise.
        if (IsFlightAssistTile(edState))
        {
            bool assistOn = GameProfileTheme.FlightAssistOn();
            bool faLit = !blink || BlinkOnPhase(now);
            if (GameProfileTheme.FlightAssistBgPath(assistOn, faLit) is { } faBg)
                spec.BgImagePath = faBg;
            spec.TextColor = GameProfileTheme.FlightAssistTextHex(assistOn, faLit);
            return;
        }

        if (GameProfileTheme.BgImagePathFor(GameProfileSpecs.EliteId, lit) is { } bg)
            spec.BgImagePath = bg;
        spec.TextColor = GameProfileTheme.TextHexFor(GameProfileSpecs.EliteId, lit);
    }

    /// <summary>Which half of the blink this instant falls in: one second on, one second off. The
    /// tile timer ticks at 1 Hz, so that is the fastest cadence the hardware can actually show —
    /// anything finer would just alias against the tick.</summary>
    private static bool BlinkOnPhase(DateTime now) => now.Second % 2 == 0;

    /// <summary>Both readings of the flight-assist bit — the annunciator and the cockpit-panel
    /// key. They share the red/green treatment: the bit is the same, only the caption differs.</summary>
    private static bool IsFlightAssistTile(string edState) =>
        edState is "flightassist" or "flightassist_on";

    /// <summary>Accent an Elite tile is drawn in. Everything rests in the game's HUD orange
    /// except the flight-assist key, which carries its state in its colour.</summary>
    private static Color EdAccentFor(string edState, ActionTypeHelper.EdTileColor color, bool explicitColor)
    {
        if (explicitColor) return GameProfileTheme.AccentForColor(color);
        return IsFlightAssistTile(edState)
            ? GameProfileTheme.FlightAssistAccent(GameProfileTheme.FlightAssistOn())
            : GameProfileTheme.EliteAccent();
    }

    /// <summary>True when the value names an art colour of its own. Orange is both the default
    /// and a legal explicit pick, so "did the pilot choose?" cannot be read off the enum alone —
    /// it is the presence of the third field in the value that decides.</summary>
    private static bool HasExplicitEdColor(string? value) => (value ?? "").Split('|').Length > 2;

    /// <summary>The annunciator shape's state. The flight-assist key is always drawn FILLED: it
    /// is never "off", it is reporting one of two live states and the accent says which. Without
    /// this, assist-off would draw a hollow, dimmed square on top of the red glow.</summary>
    private static LiveTileRenderer.EdState EdRenderState(string edState, LiveTileRenderer.EdState state) =>
        IsFlightAssistTile(edState) && state != LiveTileRenderer.EdState.Unknown
            ? LiveTileRenderer.EdState.On
            : state;

    /// <summary>Change stamp for an Elite tile — the rendered STATE, not the raw flags word, so
    /// a tile only re-uploads when its own bit moved and not on every unrelated cockpit change
    /// (the game rewrites the file for any of 32 bits).</summary>
    private static string EdStamp(string value)
    {
        var ed = EliteStatusReader.Snapshot();
        string state = ParseEdValue(value).State;
        if (state == "pips")
            return $"pips:{ed.Valid}:{ed.PipsSys}/{ed.PipsEng}/{ed.PipsWep}";
        if (EdGaugeStates.Contains(state))
            return "g:" + state + ":" + EdGaugeValue(state, ed).Text;
        return EdStateOf(state, ed).ToString();

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
            // The name the user gave the probe, captured in the value when the key was assigned
            // (so a deleted probe still leaves a captioned key rather than a blank one).
            "dp_screen" => ActionTypeHelper.ParseScreenValue(value)?.Label ?? "",
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
            // Elite tiles: the cockpit's own wording, short enough to read at 102 px. English
            // on purpose — these mirror labels the game itself prints in English whatever the
            // UI language, and a pilot reads the panel, not the app.
            "dp_edstatus" => ParseEdValue(value).State switch
            {
                "scoop"           => "SCOOP",
                "lights"          => "LIGHTS",
                "hardpoints"      => "HARDPT",
                "flightassist"    => "FA OFF",
                // The two readings of the same bit need two captions: without this the "_ =>"
                // below quietly captioned every flight-assist-ON tile "GEAR".
                "flightassist_on" => "FA ON",
                "silent"          => "SILENT",
                "shields"      => "SHIELDS",
                "supercruise"  => "S/CRUISE",
                "nightvision"  => "NIGHT V",
                "pips"         => "PIPS",
                "overheat"     => "HEAT",
                "lowfuel"      => "FUEL",
                "danger"       => "DANGER",
                "fuel"         => "FUEL",
                "fuelres"      => "RESERVE",
                "cargo"        => "CARGO",
                "docked"       => "DOCKED",
                "landed"       => "LANDED",
                "wing"         => "WING",
                "scooping"     => "SCOOPING",
                "masslock"     => "MASSLOCK",
                "fsdcharging"  => "FSD CHG",
                "fsdcooldown"  => "FSD COOL",
                "fsdjump"      => "JUMPING",
                "interdicted"  => "INTERDICT",
                "analysis"     => "ANALYSIS",
                "glide"        => "GLIDE",
                "inship"       => "SHIP",
                "infighter"    => "FIGHTER",
                "insrv"        => "SRV",
                "srvhandbrake" => "BRAKE",
                "srvturret"    => "TURRET",
                "srvdriveassist" => "DRIVE A",
                "srvhighbeam"  => "HIGH BEAM",
                "onfoot"       => "ON FOOT",
                "intaxi"       => "TAXI",
                "multicrew"    => "MULTICREW",
                "aimdownsight" => "AIM",
                "breathable"   => "ATMOS",
                "lowoxygen"    => "OXYGEN",
                "lowhealth"    => "HEALTH",
                "verycold"     => "V COLD",
                "veryhot"      => "V HOT",
                _              => "GEAR",
            },
            // Zero Company tiles. Short labels for the same reason as Elite's, but these are
            // localisable words rather than cockpit legends the game prints in English, so they
            // go through Loc — a squad key reading "SQUADRA 1" is what an Italian player expects.
            "dp_zcstatus" => ZcState(value) switch
            {
                // A squad key names the soldier standing in that slot as soon as the game says
                // who it is — "ANAKIN" is what the player is looking for on the pad, "SQUAD 2"
                // is only the fallback for a slot we can't fill yet (no mission, empty slot).
                "unit1"      => ZcUnitCaption(0),
                "unit2"      => ZcUnitCaption(1),
                "unit3"      => ZcUnitCaption(2),
                "unit4"      => ZcUnitCaption(3),
                "unit5"      => ZcUnitCaption(4),
                "unit6"      => ZcUnitCaption(5),
                "nextchar"   => Loc.Get("zctile_next"),
                "prevchar"   => Loc.Get("zctile_prev"),
                "sel_hp"     => Loc.Get("zctile_hp"),
                // Spelled out over the caption strip's two lines: "AP" is the game's own
                // shorthand, but on a key sitting on the desk the words read faster.
                "sel_ap"     => Loc.Get("zctile_ap_l1") + "\n" + Loc.Get("zctile_ap_l2"),
                "sel_armor"  => Loc.Get("zctile_armor"),
                "sel_canact" => Loc.Get("zctile_canact"),
                "turn"       => Loc.Get("zctile_turn"),
                "round"      => Loc.Get("zctile_round"),
                // An ability key names the ability actually in that slot on the selected
                // soldier ("ROCKET STRIKE"), and falls back to the slot number only when there
                // is nobody selected or the bar is shorter than the page.
                "scroll" when _zcPage == 0 => "",
                var a when ZcActionSlot(ZcResolveScrollSlot(a)) is { } slot => ZcAbilityCaption(slot),
                _            => Loc.Get("zctile_unit") + " 1",
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
        "dp_screen"    => ScreenValue(value ?? ""),
        _              => ("", null),
    };

    // ─────────────────── User-defined screen probes (dp_screen) ───────────────────

    /// <summary>What a screen-probe key shows right now: the percentage the probe measures, or
    /// the on/off word for a colour probe. A probe that cannot be read — game closed, window not
    /// in the foreground, probe deleted — is a DASH and an empty ring: the tile says it does not
    /// know, which is the one thing it must never fake.</summary>
    private static (string Text, double? Fraction) ScreenValue(string? value)
    {
        var probe = ScreenProbeStore.ById(ActionTypeHelper.ParseScreenValue(value)?.Id);
        if (probe is null) return ("—", null);

        var reading = ScreenProbeReader.Read(probe);
        if (!reading.Valid) return ("—", null);

        return probe.Mode == ProbeMode.Color
            ? (Loc.Get(reading.On ? "screen_probe_on" : "screen_probe_off"), reading.On ? 1 : 0)
            : ($"{(int)Math.Round(reading.Fraction * 100)}%", reading.Fraction);
    }

    /// <summary>Renders a screen-probe tile. A percentage probe is a gauge like the PC monitor's;
    /// a colour probe is the on/off annunciator. Both are drawn in the probe's OWN reference
    /// colour — the tile then wears the colour of the thing it is measuring, which is what makes
    /// a wall of custom tiles readable at a glance.</summary>
    internal static bool RenderScreenTile(string? value, string caption, int size, string outputPngPath)
    {
        var probe = ScreenProbeStore.ById(ActionTypeHelper.ParseScreenValue(value)?.Id);
        var reading = probe is null ? default : ScreenProbeReader.Read(probe);

        LiveTileRenderer.EdAccentOverride = probe is null
            ? null
            : System.Drawing.Color.FromArgb((probe.Color >> 16) & 0xFF,
                                            (probe.Color >> 8) & 0xFF, probe.Color & 0xFF);

        if (probe is not null && probe.Mode == ProbeMode.Color)
            return LiveTileRenderer.TryRenderEdStatus(
                !reading.Valid ? LiveTileRenderer.EdState.Unknown
                : reading.On   ? LiveTileRenderer.EdState.On
                               : LiveTileRenderer.EdState.Off,
                caption, size, outputPngPath);

        var (text, fraction) = ScreenValue(value);
        return LiveTileRenderer.TryRenderGauge(text, fraction, caption, size, outputPngPath);
    }

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

        var phase = metric switch
        {
            "up"   => SpeedTestService.Phase.Upload,
            "ping" => SpeedTestService.Phase.Ping,
            _      => SpeedTestService.Phase.Download,
        };

        if (SpeedTestService.IsRunning)
        {
            if (progress >= 1) { /* leg done — fall through to its final value */ }
            // A leg that hasn't started yet (still on an earlier phase): "…", not a stale "—"/number
            // that reads as "the press did nothing".
            else if (progress <= 0 && SpeedTestService.CurrentPhase != phase)
                return ("…", null);
            else
                return ($"{(int)Math.Round(progress * 100)}%", progress);
        }

        // Custom config with no upload endpoint: say so rather than show a blank "—".
        if (metric == "up" && SpeedTestService.UploadDisabled) return ("n/d", null);

        // Last run threw and this metric has no earlier good value to fall back on — the
        // user-visible "it failed" signal (reason is in the K2 log / the config popup's test).
        if (SpeedTestService.LastRunFailed && value is null) return ("ERR", null);

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
