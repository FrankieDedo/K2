// MainWindow.EvSoftwareFx.cs — partial Everest Max: HOST-DRIVEN lighting animation
// ("Diagonal wave (experimental)" in the effect dropdown).
//
// Proof of concept for a class of effect the firmware cannot do: instead of asking the
// keyboard to run one of its ~10 built-in effects, K2 computes every frame on the PC and
// streams the 126 keycap colors down the Custom-mode raw-HID channel (the same channel
// MainWindow.CustomLighting.cs uses for static per-key painting — see
// EverestService.BeginCustomFrameStream / PushCustomKeycapFrame).
//
// Two hard rules the loop is built around:
//   1. NEVER persist. A SaveFlash per frame would wear the flash out and leave the
//      keyboard unresponsive ~500ms at a time (see EverestService.FlushSaveFlash).
//      BeginCustomFrameStream passes persist:false and the frames never persist at all —
//      so this effect is intentionally NOT restored on the next power cycle.
//   2. Stay on one zone. Each frame is 7 acked HID packets (keycaps only) and the loop
//      never leaves the keycap zone, so no per-frame zone switch is needed — that would
//      add a 7-packet response burst to drain, roughly doubling the cost. The side ring
//      lives on zone 0x05 and is deliberately not touched at all (its zone switch times
//      out on this hardware, 1.2s a go — see EverestService.BeginCustomFrameStream).
//
// Measured on real hardware 2026-08-27: the 7 keycap pages go out and get acked in ~18ms
// total (~2.6ms/packet), so 30fps has plenty of headroom and ~50fps is the rough ceiling.
//
// The loop also measures itself (frame time min/avg/max, effective fps, dropped frames)
// and logs a line every FxStatsIntervalMs — that measurement is the whole point of the
// "test" effect: it decides whether a real animation engine for every device is worth
// building, and at what frame rate. Logged via App.WriteLog so it survives LogLevel=Off.
//
// SignalRGB coexistence: the loop yields the device the same way every other lighting write
// in K2 does (SignalRgbGuard) — while SignalRGB's engine is up it PAUSES, and takes the
// keyboard back on its own, setup and all, once SignalRGB exits.
//
// Listed in CbEvEffect like any firmware preset (MainWindow.Everest.cs's EvEffectList) —
// it was debug-gated while the frame rate was being measured, and shipped as a normal,
// experimental-labelled entry once it held up on hardware.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using K2.App.Models;
using K2.App.Services;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Target frame interval. 33ms ≈ 30fps — an upper bound, not a promise: the
    /// loop measures what the USB round-trips actually allow and never queues up.</summary>
    private const int FxTargetFrameMs = 33;

    /// <summary>How often the loop logs its own timing stats.</summary>
    private const int FxStatsIntervalMs = 3000;

    private CancellationTokenSource? _evSoftFxCts;
    private Task?                    _evSoftFxTask;

    /// <summary>
    /// Starts the host-driven test animation. Paints one setup frame (which also switches the
    /// device into Custom mode and blanks the side ring), then runs the frame loop on a
    /// background task — the HID I/O is synchronous and blocking, so it must not sit on the
    /// UI thread.
    /// </summary>
    /// <param name="speedPct">Speed slider, 0..100 → animation phase rate.</param>
    /// <param name="brightness">Brightness slider, 0..100 → the packets' own brightness byte.</param>
    /// <param name="style">Single/Double/Rainbow color mode and its colors.</param>
    private void StartEvSoftwareFx(int speedPct, byte brightness, EvFxStyle style)
    {
        if (!_everest.IsOpen)
        {
            LogEverest("[SWFX] skip: Everest driver not open");
            return;
        }

        StopEvSoftwareFx();

        // Rebuilt at every start so it always reflects the CURRENT keyboard layout and the
        // side the numpad is docked on.
        var pos = BuildEvFxLedPositions();

        // The Custom-mode setup is NOT done here but inside the loop: it has to be redone
        // every time the animation resumes after yielding the device to SignalRGB, so there
        // is one code path for both. Consequence: starting while SignalRGB is running is
        // allowed and simply arms the animation — it begins on its own once SignalRGB exits.
        if (Services.SignalRgbGuard.LightingYielded)
            LogEverest("[SWFX] SignalRGB owns the lighting — animation armed, starts when it exits");

        var cts = new CancellationTokenSource();
        _evSoftFxCts = cts;
        double rate = 0.15 + Math.Clamp(speedPct, 0, 100) / 100.0 * 1.35;  // 0.15..1.5 cycles/s

        LogEverest($"[SWFX] start: target {1000.0 / FxTargetFrameMs:F0}fps, rate={rate:F2} cyc/s, " +
                   $"bright={brightness}%, colors={style.Describe()}");
        App.WriteLog($"[Everest.SWFX] start target={FxTargetFrameMs}ms rate={rate:F2} " +
                     $"bright={brightness} style={style.Describe()}");

        _evSoftFxTask = Task.Run(() => EvSoftwareFxLoop(rate, brightness, pos, style, cts.Token));
    }

    /// <summary>Stops the animation and waits for the loop to release the HID channel — the
    /// caller (ApplyCurrentEffect) is about to write lighting itself, so returning while a
    /// frame is still in flight would interleave two writers on the same device.</summary>
    private void StopEvSoftwareFx()
    {
        var cts  = _evSoftFxCts;
        var task = _evSoftFxTask;
        if (cts is null) return;

        _evSoftFxCts  = null;
        _evSoftFxTask = null;

        try { cts.Cancel(); } catch { /* already disposed */ }
        try { task?.Wait(3000); }
        catch (Exception ex) { App.WriteLog("[Everest.SWFX] stop wait: " + ex.Message); }
        cts.Dispose();
    }

    /// <summary>
    /// The frame loop itself (background thread). Frames are never queued: if a push takes
    /// longer than the target interval the next frame is computed at the real elapsed time,
    /// so the animation keeps wall-clock speed and simply renders coarser.
    ///
    /// <para><b>Pacing is deadline-based, not sleep-based</b> (2026-08-27, second hardware
    /// run: 20fps and visibly uneven where 30 was asked for). Two things were wrong with
    /// <c>WaitOne(target - elapsed)</c>: (1) Windows' default timer granularity is ~15.6ms,
    /// so a 13ms wait really sleeps 15-31ms — that alone turned a 33ms budget into a ~50ms
    /// period AND made every period a different length, which is exactly what stutter looks
    /// like; (2) the wait was relative to the end of the previous frame, so every overrun
    /// pushed the whole schedule later instead of being absorbed. Now the loop asks winmm for
    /// 1ms timer resolution for as long as it runs, and sleeps until an ABSOLUTE deadline that
    /// advances by exactly one frame each time (resynced if we fall more than a frame behind,
    /// so it never tries to "catch up" with a burst).</para>
    /// </summary>
    private void EvSoftwareFxLoop(double rate, byte brightness, EvFxLedPos[] pos, EvFxStyle style,
                                   CancellationToken ct)
    {
        var buf   = new int[EverestSideLedProtocol.KeycapWireCount];
        var clock = Stopwatch.StartNew();
        var frame = new Stopwatch();

        // 1ms timer resolution for the whole run — without it every sleep below rounds up to
        // the next ~15.6ms tick. Process-wide and reference-counted by Windows, so the
        // matching timeEndPeriod in the finally block is not optional.
        bool hiRes = TimeBeginPeriod(1) == 0;
        double nextFrameMs = 0;
        try
        {
            long   statsAt = 0;
            int    frames = 0, dropped = 0, consecutiveFails = 0;
            double sum = 0, min = double.MaxValue, max = 0;

            bool needSetup = true, yielding = false;

            while (!ct.IsCancellationRequested)
            {
                // SignalRGB drives the very same raw-HID interface (Everest Max MI_03), so
                // while its engine is up K2 must not write a single frame — two streams on
                // one collection is exactly the flicker SignalRgbGuard exists to prevent.
                // The animation is PAUSED, not stopped: it takes the keyboard back by itself
                // (re-running the Custom-mode setup, since SignalRGB will have left the
                // device in another state) as soon as SignalRGB exits.
                if (Services.SignalRgbGuard.LightingYielded)
                {
                    if (!yielding)
                    {
                        yielding = true; needSetup = true;
                        App.WriteLog("[Everest.SWFX] paused — SignalRGB owns the device");
                        LogEverestSafe("[SWFX] paused: SignalRGB owns the lighting");
                    }
                    if (ct.WaitHandle.WaitOne(1000)) break;
                    nextFrameMs = clock.Elapsed.TotalMilliseconds;
                    continue;
                }

                if (yielding)
                {
                    yielding = false;
                    App.WriteLog("[Everest.SWFX] resuming — SignalRGB gone");
                    LogEverestSafe("[SWFX] resumed: lighting reclaimed from SignalRGB");
                }

                if (needSetup)
                {
                    RenderEvSoftwareFxFrame(buf, clock.Elapsed.TotalSeconds * rate, pos, style);
                    // Side ring left untouched on purpose: its zone switch (0x05) times out
                    // on this hardware, 1.2s a go — see EverestService.BeginCustomFrameStream.
                    if (!_everest.BeginCustomFrameStream(buf, brightness: brightness))
                    {
                        // A failure here while SignalRGB just took over is not an error:
                        // loop round again and pause properly. Anything else is fatal.
                        if (Services.SignalRgbGuard.LightingYielded) continue;
                        App.WriteLog("[Everest.SWFX] setup frame failed — stopping");
                        LogEverestSafe("[SWFX] setup frame failed — animation stopped");
                        break;
                    }
                    needSetup = false;
                }

                frame.Restart();
                RenderEvSoftwareFxFrame(buf, clock.Elapsed.TotalSeconds * rate, pos, style);
                bool ok = _everest.PushCustomKeycapFrame(buf, brightness);
                frame.Stop();

                double ms = frame.Elapsed.TotalMilliseconds;
                frames++; sum += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
                if (!ok) dropped++;

                // A failed frame is a MISSING ECHO, not a dead device: the packets still went out
                // and the next frame repaints everything anyway. Only a long unbroken run of
                // failures means the device really stopped listening — a couple per stats window
                // is normal while typing (see EverestHidNative.SendKeycapColorsFast).
                consecutiveFails = ok ? 0 : consecutiveFails + 1;
                if (consecutiveFails > 30)
                {
                    App.WriteLog("[Everest.SWFX] 30 consecutive failed frames — stopping");
                    LogEverestSafe("[SWFX] stopped: the device stopped accepting frames");
                    break;
                }

                long now = clock.ElapsedMilliseconds;
                if (now - statsAt >= FxStatsIntervalMs)
                {
                    double avg = sum / Math.Max(1, frames);
                    string line = $"[SWFX] {frames} frames in {(now - statsAt) / 1000.0:F1}s → " +
                                  $"{frames / Math.Max(0.001, (now - statsAt) / 1000.0):F1} fps, " +
                                  $"frame {min:F1}/{avg:F1}/{max:F1} ms (min/avg/max), failed={dropped}";
                    App.WriteLog("[Everest.SWFX] " + line);
                    LogEverestSafe(line);
                    statsAt = now; frames = 0; dropped = 0; sum = 0; min = double.MaxValue; max = 0;
                }

                // Absolute deadline: advance by exactly one frame, and resync (rather than
                // firing a burst of catch-up frames) whenever a slow frame put us a whole
                // frame or more behind.
                nextFrameMs += FxTargetFrameMs;
                double nowMs = clock.Elapsed.TotalMilliseconds;
                if (nextFrameMs < nowMs) nextFrameMs = nowMs;

                int wait = (int)Math.Round(nextFrameMs - nowMs);
                if (wait > 0 && ct.WaitHandle.WaitOne(wait)) break;
            }
        }
        finally
        {
            if (hiRes) TimeEndPeriod(1);
        }

        App.WriteLog($"[Everest.SWFX] loop ended (1ms timer resolution: {hiRes})");
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    /// <summary>Normalized physical position of one LED on the board, 0..1 on both axes
    /// across keyboard + numpad together. <see cref="Mapped"/> is false for wire slots no
    /// key owns (the 7 padding slots, plus any index the layout does not reach) — those are
    /// rendered black instead of being given a fake position.</summary>
    private readonly record struct EvFxLedPos(bool Mapped, double X, double Y);

    /// <summary>
    /// Builds wire index → real physical position, from the SAME geometry the on-screen
    /// keyboard is drawn with (<see cref="EverestKeyboardLayout"/>'s KeyDef X/Y/W/H, in the
    /// 642×260 board_left / 166×260 board_right canvases) joined to
    /// <see cref="LedMatrixMapping"/>'s VK → LED index tables.
    ///
    /// <para><b>Why this exists</b> (first hardware run, 2026-08-27): the first version
    /// derived coordinates arithmetically as <c>column = i / 9</c>, <c>row = i % 9</c>. That
    /// holds for the main board's column-major block and nothing else — the keys right of
    /// Enter (PrtSc 117, Enter 120, RShift 121, → 122, Pause 123, ↑ 124) break the pattern,
    /// and the numpad's LEDs share the SAME 0-125 index space interleaved with the main
    /// board's (NumLock=6, Num-=15, Num*=16, Num/=24, Num7=61 …), so a numpad key would land
    /// somewhere on the far left of the wave. The user saw exactly that: everything right of
    /// Enter, numpad included, out of step with the wave.</para>
    ///
    /// <para>The numpad is placed on whichever side it is actually docked
    /// (<see cref="_evNumpadPos"/>: 2 = left, otherwise right) with the same 6px gap the UI
    /// uses, so the wave crosses the two boards as one continuous surface.</para>
    /// </summary>
    private EvFxLedPos[] BuildEvFxLedPositions()
    {
        const double BoardW = 642, BoardH = 260, NumpadW = 166, Gap = 6;

        bool numpadLeft = _evNumpadPos == 2;
        bool numpadOn   = _evNumpadPos != 0;
        double totalW   = numpadOn ? BoardW + Gap + NumpadW : BoardW;
        double keyDx    = numpadOn && numpadLeft ? NumpadW + Gap : 0;
        double padDx    = numpadLeft ? 0 : BoardW + Gap;

        var map = new EvFxLedPos[EverestSideLedProtocol.KeycapWireCount];

        void Place(KeyDef kd, System.Collections.Generic.Dictionary<int, int> first,
                   System.Collections.Generic.Dictionary<int, int> second, double dx)
        {
            if (!first.TryGetValue(kd.MatrixId, out int led) &&
                !second.TryGetValue(kd.MatrixId, out led)) return;
            if (led < 0 || led >= map.Length) return;
            map[led] = new EvFxLedPos(true,
                (dx + kd.X + kd.W / 2) / totalW,
                (kd.Y + kd.H / 2) / BoardH);
        }

        foreach (var kd in EverestKeyboardLayout.GetBoardLeft(_evLayoutType))
            Place(kd, LedMatrixMapping.EverestKeyboard, LedMatrixMapping.EverestNumpad, keyDx);

        if (numpadOn)
            foreach (var kd in EverestKeyboardLayout.BoardRight)
                Place(kd, LedMatrixMapping.EverestNumpad, LedMatrixMapping.EverestKeyboard, padDx);

        int mapped = 0;
        foreach (var e in map) if (e.Mapped) mapped++;
        App.WriteLog($"[Everest.SWFX] LED position map: {mapped}/{map.Length} slots " +
                     $"(layout={_evLayoutType}, numpadPos={_evNumpadPos})");
        return map;
    }

    /// <summary>
    /// Color mode of the wave, read off the panel's own Single/Double/Rainbow radios. All
    /// three are real here — the frames are computed on the PC, so unlike a firmware preset
    /// (which can only offer what its effect table holds) supporting them costs nothing.
    /// </summary>
    private readonly record struct EvFxStyle(bool Rainbow, bool Dual, Color C1, Color C2)
    {
        public static EvFxStyle FromUi(bool rainbow, bool dual, int rgb1, int rgb2)
            => new(rainbow, !rainbow && dual, FromRgb(rgb1), FromRgb(rgb2));

        private static Color FromRgb(int rgb) => Color.FromRgb(
            (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        public string Describe() => Rainbow ? "rainbow"
            : Dual ? $"dual #{C1.R:X2}{C1.G:X2}{C1.B:X2}/#{C2.R:X2}{C2.G:X2}{C2.B:X2}"
            : $"single #{C1.R:X2}{C1.G:X2}{C1.B:X2}";
    }

    /// <summary>
    /// Renders one frame into the 133-slot keycap wire array (0xRRGGBB per slot): a wave
    /// travelling diagonally across the real board surface. Unmapped slots stay black.
    ///
    /// <para>One wave position drives all three color modes. <b>Rainbow</b> maps it to hue
    /// and keeps full value, so every key is lit. <b>Single</b> and <b>Double</b> map it
    /// through a cosine instead: Single fades the one color in and out (black at the trough,
    /// which is what makes the motion readable with a single hue), Double crossfades between
    /// the two colors at full brightness, never going dark.</para>
    /// </summary>
    private static void RenderEvSoftwareFxFrame(int[] wire, double phase, EvFxLedPos[] pos,
                                                 EvFxStyle style)
    {
        for (int i = 0; i < wire.Length; i++)
        {
            if (i >= pos.Length || !pos[i].Mapped) { wire[i] = 0; continue; }

            // Position along the wave, in turns: same quantity for every mode.
            double u = phase - pos[i].X * 0.9 - pos[i].Y * 0.35;

            Color c;
            if (style.Rainbow)
            {
                c = HsvToRgb(u * 360.0, 1.0, 1.0);
            }
            else
            {
                double t = 0.5 + 0.5 * Math.Cos(u * 2 * Math.PI);   // 0..1, smooth
                c = style.Dual
                    ? LerpColor(style.C1, style.C2, t)
                    : LerpColor(Colors.Black, style.C1, t);
            }
            wire[i] = (c.R << 16) | (c.G << 8) | c.B;
        }
    }
}
