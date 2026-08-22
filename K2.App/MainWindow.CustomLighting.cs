// MainWindow.CustomLighting.cs — partial class: "Custom Lighting" panel.
// Per-key custom color painting: select a color, click keys on the
// keyboard overlay to color them, apply to device via ChangeCustomizeEffect.
// Panel separate from the RGB preset, as per spec.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;

namespace K2.App;

public partial class MainWindow
{
    // Currently selected brush color
    private Color _customBrushColor = Color.FromRgb(0xFF, 0x00, 0x00); // default paint red (user request 2026-07-22)

    /// <summary>Map LED index (0-125, LedMatrixMapping domain = wire position) →
    /// custom color assigned by the user. Keyed by LED index, NOT by VK/matrixId
    /// (changed 2026-07-22): K2 reuses numpad VK codes for the nav cluster
    /// (Ins/Home/PgUp/Del/End/PgDn share VK 96/103/105/110/97/99 with
    /// Num0/7/9/./1/3, and both Enters share VK 13), so a VK-keyed map could not
    /// tell them apart — painting Num7 colored Home's LED and Num7 stayed dark
    /// (user report: numpad 7/9/1/3/0/./Enter never lit; those are exactly the
    /// colliding VKs). The button→LED translation happens at click time, where the
    /// owning canvas disambiguates (see <see cref="TryButtonToLed"/>).</summary>
    private readonly Dictionary<int, Color> _customKeyColors = new();

    /// <summary>Map LED index → dynamic effect assigned by the user (Wave/Breathing/
    /// Reactive/Tornado/Matrix/Yeti — everything except Static/Off, which just write a
    /// plain color into <see cref="_customKeyColors"/> instead). A LED is either in
    /// this map OR <see cref="_customKeyColors"/>, never both — painting with a dynamic
    /// effect removes any static color at that LED and vice versa (see
    /// <see cref="TryCustomPaint"/>). All LEDs sharing the same effect share that
    /// effect's ONE param set in <see cref="_customFxParams"/> — the firmware only
    /// takes one param packet per effect code, not per LED (2026-07-22 capture finding,
    /// see EverestSideLedProtocol's per-region-effects section).</summary>
    private readonly Dictionary<int, EverestService.Effect> _customKeyEffects = new();

    /// <summary>Per-dynamic-effect parameters (color mode, colors, direction, speed) —
    /// keyed by <see cref="EverestService.Effect"/>, populated lazily via
    /// <see cref="FxParamsFor"/>. Brightness is NOT here: it's the single device-wide
    /// <c>SldEvBrightness</c>, same as the global RGB panel and the Static/side-ring
    /// custom colors.</summary>
    private readonly Dictionary<EverestService.Effect, CustomFxParams> _customFxParams = new();

    private sealed class CustomFxParams
    {
        public Services.EverestSideLedProtocol.CustomEffectColorMode Mode = Services.EverestSideLedProtocol.CustomEffectColorMode.Single;
        public int Direction;
        public int Speed = 50;
        public Color Color1 = Color.FromRgb(0xFF, 0x00, 0x00);
        public Color Color2 = Color.FromRgb(0x00, 0x00, 0xFF);
    }

    /// <summary>Suppresses re-entrant saves while <see cref="UpdateCustomFxParamsVisibility"/>
    /// programmatically sets the param controls — same pattern as _evRgbSuppress.</summary>
    private bool _customFxSuppress;

    /// <summary>Map wire index (0-44, see <see cref="Services.EverestSideLedProtocol"/>)
    /// → custom color for the 45 border LEDs. Separate channel from
    /// <see cref="_customKeyColors"/>: sent via <see cref="EverestService.SetSideLedColors"/>
    /// (raw HID), not the SDKDLL.dll struct used for the 126 keycaps — see
    /// EverestSideLedProtocol's doc comment for why.</summary>
    private readonly Dictionary<int, Color> _customSideColors = new();

    /// <summary>Border-square Button per wire index, built once by
    /// <see cref="BuildBorderSquares"/> — used to repaint on undo/load/clear.</summary>
    private readonly Dictionary<int, Button> _customSideButtons = new();

    // Flag to prevent a key click from being interpreted as action capture
    // while painting
    private bool _customPaintMode;

    // ─────────────────────── Init ───────────────────────

    /// <summary>Paint-effect choices for <see cref="CbCustomPaintEffect"/> — the 8
    /// effects the user asked for (matches Base Camp's own Custom section). Static (0)
    /// and Off (7) write a plain color via <see cref="_customKeyColors"/> (Off = black);
    /// the other 6 are dynamic per-region effects wired via <see cref="CustomFxCapsFor"/>
    /// (2026-07-22, see EverestSideLedProtocol's per-region-effects section). Plain
    /// hardcoded English strings, same pattern as MainWindow.Everest.cs's EvEffectList
    /// (that combo isn't localized either).</summary>
    private static readonly string[] CustomPaintEffects =
        { "Static", "Wave", "Tornado", "Breathing", "Reactive", "Matrix", "Yeti", "Off" };

    /// <summary>Per-dynamic-effect capabilities driving <see cref="UpdateCustomFxParamsVisibility"/>
    /// — which color modes and direction options to show for the effect currently selected
    /// in <see cref="CbCustomPaintEffect"/>. Direction codes/labels are the SAME ones
    /// MainWindow.Everest.cs's CapsFor uses for the global RGB panel (already hardware-
    /// validated there) — reused here on the assumption the raw per-region packet uses an
    /// identical byte, per <see cref="Services.EverestSideLedProtocol.BuildCustomEffectParamPacket"/>'s
    /// doc. <c>TwoStopLayout</c> = Reactive/Matrix/Yeti's always-two-colors shape
    /// (<see cref="Services.EverestSideLedProtocol.BuildTwoStopEffectParamPacket"/>) — no
    /// color-mode choice, no direction, no rainbow (matches BaseCampLinux's global
    /// encoding for these three, cross-checked byte-for-byte for Reactive by capture).
    /// Null for Static(0)/Off(7) — those aren't dynamic effects.</summary>
    private sealed record CustomFxCaps(
        EverestService.Effect Eff, bool Rainbow, bool Dual, string[] DirLabels, int[] DirCodes, bool TwoStopLayout);

    private static CustomFxCaps? CustomFxCapsFor(int paintEffectIndex) => paintEffectIndex switch
    {
        1 => new(EverestService.Effect.Wave,      true,  false, new[] { "Right", "Down", "Left", "Up" }, new[] { 0, 2, 4, 6 }, false),
        2 => new(EverestService.Effect.Tornado,   true,  false, new[] { "Clockwise", "Counter-CW" },     new[] { 9, 10 },      false),
        3 => new(EverestService.Effect.Breath,    true,  true,  System.Array.Empty<string>(), System.Array.Empty<int>(), false),
        4 => new(EverestService.Effect.ReactiveA, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>(), true),
        5 => new(EverestService.Effect.Matrix,    false, true,  System.Array.Empty<string>(), System.Array.Empty<int>(), true),
        6 => new(EverestService.Effect.Yeti,      false, true,  System.Array.Empty<string>(), System.Array.Empty<int>(), true),
        _ => null, // 0=Static, 7=Off
    };

    /// <summary>Lazily creates/returns the shared param set for a dynamic effect —
    /// every LED painted with that effect uses these SAME values (one param packet per
    /// effect code on the wire, not per LED).</summary>
    private CustomFxParams FxParamsFor(EverestService.Effect eff) =>
        _customFxParams.TryGetValue(eff, out var p) ? p : (_customFxParams[eff] = new CustomFxParams());

    private void InitCustomLightingPanel()
    {
        // Set the initial brush button color
        BtnCustomBrushColor.Background = new SolidColorBrush(_customBrushColor);

        CbCustomPaintEffect.ItemsSource = CustomPaintEffects;
        CbCustomPaintEffect.SelectedIndex = 0;

        BuildBorderSquares();

        // Load previously saved colors + dynamic-effect assignments/params
        LoadCustomColorsFromStore();
        UpdateCustomFxParamsVisibility();

        // Edge case: if "Custom" was the persisted rgb.effect, UpdateEvCapabilities
        // already called SetCustomPaintModeActive(true) earlier in InitEverestModule
        // (InitEverestRgbPanel runs before this method) — before CvsEvKeyboard/
        // CvsEvNumpad/the border squares existed, so that ReapplyCustomOverlays was a
        // no-op. Catch up now that everything above is actually built.
        if (_customPaintMode)
            ReapplyCustomOverlays();
    }

    // ─────────────────────── Border (side LED) squares ───────────────────────

    /// <summary>Square size / gap-from-board, in the SAME local pixel space as
    /// <see cref="CvsEvBorderMain"/>/<see cref="CvsEvBorderNumpad"/> — each overlay is
    /// sized identically to the board canvas it sits on (642x260 / 166x260) and shares
    /// its Grid cell, so local (0,0) is that board's own top-left corner. Squares are
    /// placed at negative/overflowing coordinates (Canvas doesn't clip by default) to
    /// sit just outside the board's edge.</summary>
    private const double BorderSz = 12, BorderGap = 2;

    /// <summary>Builds the 45 border-square Buttons (31 into <see cref="CvsEvBorderMain"/>,
    /// 14 into <see cref="CvsEvBorderNumpad"/>), positioned around the main board and
    /// numpad bezels per the physical clockwise order confirmed by a real USB capture
    /// 2026-07-22 (see <see cref="Services.EverestSideLedProtocol.MainOrder"/>/
    /// <c>NumpadOrder</c> and CHANGELOG). Geometry is a first-pass proportional placement
    /// (even spacing along each edge, like BaseCampLinux's own hstrip/vstrip).</summary>
    private void BuildBorderSquares()
    {
        CvsEvBorderMain.Children.Clear();
        CvsEvBorderNumpad.Children.Clear();
        _customSideButtons.Clear();

        const double mw = 642, mh = 260;
        double topY = -BorderGap - BorderSz, bottomY = mh + BorderGap;
        double leftX = -BorderGap - BorderSz, rightX = mw + BorderGap;
        PlaceEdge(CvsEvBorderMain, Services.EverestSideLedProtocol.MainOrder, 0, 11, new Point(0, topY), new Point(mw - BorderSz, topY));
        PlaceEdge(CvsEvBorderMain, Services.EverestSideLedProtocol.MainOrder, 11, 4, new Point(rightX, 0), new Point(rightX, mh - BorderSz));
        PlaceEdge(CvsEvBorderMain, Services.EverestSideLedProtocol.MainOrder, 15, 12, new Point(mw - BorderSz, bottomY), new Point(0, bottomY));
        PlaceEdge(CvsEvBorderMain, Services.EverestSideLedProtocol.MainOrder, 27, 4, new Point(leftX, mh - BorderSz), new Point(leftX, 0));

        const double nw = 166, nh = 260;
        double npTopY = -BorderGap - BorderSz, npBottomY = nh + BorderGap;
        double npLeftX = -BorderGap - BorderSz, npRightX = nw + BorderGap;
        PlaceEdge(CvsEvBorderNumpad, Services.EverestSideLedProtocol.NumpadOrder, 0, 3, new Point(0, npTopY), new Point(nw - BorderSz, npTopY));
        PlaceEdge(CvsEvBorderNumpad, Services.EverestSideLedProtocol.NumpadOrder, 3, 4, new Point(npRightX, 0), new Point(npRightX, nh - BorderSz));
        PlaceEdge(CvsEvBorderNumpad, Services.EverestSideLedProtocol.NumpadOrder, 7, 3, new Point(nw - BorderSz, npBottomY), new Point(0, npBottomY));
        PlaceEdge(CvsEvBorderNumpad, Services.EverestSideLedProtocol.NumpadOrder, 10, 4, new Point(npLeftX, nh - BorderSz), new Point(npLeftX, 0));
    }

    /// <summary>Places <paramref name="count"/> squares from <paramref name="wireOrder"/>
    /// (starting at <paramref name="skip"/>) evenly between <paramref name="p0"/> (first)
    /// and <paramref name="p1"/> (last), inclusive — one edge of the border ring.</summary>
    private void PlaceEdge(Canvas target, byte[] wireOrder, int skip, int count, Point p0, Point p1)
    {
        for (int i = 0; i < count; i++)
        {
            double t = count > 1 ? (double)i / (count - 1) : 0;
            double x = p0.X + t * (p1.X - p0.X);
            double y = p0.Y + t * (p1.Y - p0.Y);
            int wireIdx = wireOrder[skip + i];

            var btn = new Button
            {
                Width = BorderSz,
                Height = BorderSz,
                Style = (Style)FindResource("K2ColorSquareButton"),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4F)),
                Background = Brushes.Transparent,
                Tag = wireIdx,
            };
            btn.Click += BorderSquare_Click;
            Canvas.SetLeft(btn, x);
            Canvas.SetTop(btn, y);
            target.Children.Add(btn);
            _customSideButtons[wireIdx] = btn;
        }
    }

    private void BorderSquare_Click(object sender, RoutedEventArgs e)
    {
        if (!_customPaintMode) return;
        if (sender is not Button btn || btn.Tag is not int wireIdx) return;
        _customSideColors[wireIdx] = _customBrushColor;
        ApplyColorOverlay(btn, _customBrushColor);
    }

    // ─────────────────────── Paint mode ───────────────────────

    /// <summary>
    /// Called when the user clicks a key on the keyboard overlay while paint
    /// mode is active. Colors/assigns the key per the selected paint effect.
    /// </summary>
    internal bool TryCustomPaint(Button keyButton, int matrixId)
    {
        if (!_customPaintMode) return false;

        if (TryButtonToLed(keyButton, matrixId, out int led))
            PaintLed(led, keyButton);
        return true; // consumed, do not open action dialog
    }

    /// <summary>0-based index of "Off" in <see cref="CustomPaintEffects"/> — writes
    /// black via the Static color channel, not a real dynamic effect.</summary>
    private static int CustomPaintOffIndex => CustomPaintEffects.Length - 1;

    /// <summary>
    /// Colors/assigns one LED per the currently selected paint effect: Static writes
    /// the brush color, Off writes black (both via <see cref="_customKeyColors"/>), any
    /// dynamic effect assigns the LED to that effect's region
    /// (<see cref="_customKeyEffects"/>) instead — a LED is in exactly one of the two
    /// maps, never both. The on-screen tint for a dynamic effect is computed the same
    /// way as every subsequent animation frame (<see cref="ComputeFxPreviewColor"/>,
    /// evaluated at the current clock position) instead of a flat Color1 — so a
    /// freshly-painted key immediately reads as Rainbow/Dual/Single, not a placeholder
    /// solid color that only turns into the right animation on the next 50ms tick.
    /// </summary>
    private void PaintLed(int led, Button keyButton)
    {
        var caps = CustomFxCapsFor(CbCustomPaintEffect.SelectedIndex);
        if (caps is null)
        {
            bool off = CbCustomPaintEffect.SelectedIndex == CustomPaintOffIndex;
            var color = off ? Colors.Black : _customBrushColor;
            _customKeyEffects.Remove(led);
            _customKeyColors[led] = color;
            ApplyColorOverlay(keyButton, color);
        }
        else
        {
            _customKeyColors.Remove(led);
            _customKeyEffects[led] = caps.Eff;
            var p = FxParamsFor(caps.Eff);
            double t = _customFxPreviewClock.Elapsed.TotalSeconds;
            ApplyColorOverlay(keyButton, ComputeFxPreviewColor(caps.Eff, led, p, keyButton, t));
        }
    }

    /// <summary>Re-tints every on-screen key currently assigned to <paramref name="eff"/>
    /// with its (possibly just-changed) Color1 — called after editing a dynamic effect's
    /// params so the preview stays in sync without a full ReapplyCustomOverlays pass.</summary>
    private void RetintKeysForEffect(EverestService.Effect eff)
    {
        var color = FxParamsFor(eff).Color1;
        foreach (var kvp in _customKeyEffects)
        {
            if (kvp.Value != eff) continue;
            var btn = FindKeyButtonByLed(kvp.Key);
            if (btn != null) ApplyColorOverlay(btn, color);
        }
    }

    /// <summary>Translates a clicked key Button to its LED index, using the owning
    /// canvas to pick the right table first (numpad VKs collide with the nav
    /// cluster's — see <see cref="_customKeyColors"/>' doc).</summary>
    private bool TryButtonToLed(Button keyButton, int vk, out int led)
    {
        bool onNumpad = ReferenceEquals(keyButton.Parent, CvsEvNumpad);
        var first  = onNumpad ? Models.LedMatrixMapping.EverestNumpad  : Models.LedMatrixMapping.EverestKeyboard;
        var second = onNumpad ? Models.LedMatrixMapping.EverestKeyboard : Models.LedMatrixMapping.EverestNumpad;
        if (first.TryGetValue(vk, out led)) return true;
        return second.TryGetValue(vk, out led);
    }

    /// <summary>
    /// Delegates to the SAME per-style routing the live LED-poll preview uses
    /// (<see cref="ApplyEverestLedColor"/> in MainWindow.KeycapAppearance.cs: Normal →
    /// LedHalo glow, Pudding → border/mount, ReversePudding → center — plus translucent-
    /// legend tinting when that checkbox is on) instead of recoloring the keycap face
    /// itself, and instead of a fixed "always show the halo" assumption that ignored the
    /// Pudding/ReversePudding styles (user feedback 2026-07-22: "non devi mostrare l'alone
    /// se ci sono i pudding... colora la parte che viene colorata dalla led preview").
    /// Border squares have no keycap-style template at all (plain Buttons built by
    /// <see cref="PlaceEdge"/>), so they fall back to a plain Background tint.
    /// </summary>
    private void ApplyColorOverlay(Button keyButton, Color c)
    {
        if (keyButton.Template?.FindName("LedHalo", keyButton) is Border halo)
            ApplyEverestLedColor(new KeyVisual(keyButton, halo), c.R, c.G, c.B);
        else
            keyButton.Background = new SolidColorBrush(Color.FromArgb(160, c.R, c.G, c.B));
    }

    private void ClearAllOverlays()
    {
        ClearOverlaysInCanvas(CvsEvKeyboard);
        ClearOverlaysInCanvas(CvsEvNumpad);
        foreach (var btn in _customSideButtons.Values)
            btn.ClearValue(Button.BackgroundProperty);
    }

    /// <summary>
    /// The 4 numpad display keys (<see cref="_ndkButtons"/>) share the
    /// CvsEvNumpad canvas with the real keyboard keys but are not part of
    /// custom-lighting paint mode — they have their own image/action UI, not
    /// an LED matrix color. Skip them here so paint mode neither clears their
    /// distinct background nor risks a Tag collision (both use small int Tags:
    /// NDK keyIndex 0-3 vs. LED matrixId) in <see cref="FindKeyInCanvas"/>.
    /// </summary>
    private void ClearOverlaysInCanvas(Canvas? canvas)
    {
        if (canvas == null) return;
        foreach (var btn in canvas.Children.OfType<Button>())
        {
            if (_ndkButtons.Contains(btn)) continue;
            if (btn.Template?.FindName("LedHalo", btn) is Border halo)
                ResetEverestKeyToOff(new KeyVisual(btn, halo));
            else
                btn.ClearValue(Button.BackgroundProperty);
        }
    }

    /// <summary>Reapplies overlays from the colors/effect assignments saved in the maps.</summary>
    private void ReapplyCustomOverlays()
    {
        foreach (var kvp in _customKeyColors)
        {
            var btn = FindKeyButtonByLed(kvp.Key);
            if (btn != null)
                ApplyColorOverlay(btn, kvp.Value);
        }
        foreach (var kvp in _customKeyEffects)
        {
            var btn = FindKeyButtonByLed(kvp.Key);
            if (btn != null)
            {
                var p = FxParamsFor(kvp.Value);
                double t = _customFxPreviewClock.Elapsed.TotalSeconds;
                ApplyColorOverlay(btn, ComputeFxPreviewColor(kvp.Value, kvp.Key, p, btn, t));
            }
        }
        foreach (var kvp in _customSideColors)
            if (_customSideButtons.TryGetValue(kvp.Key, out var btn))
                ApplyColorOverlay(btn, kvp.Value);
    }

    /// <summary>LED index → key Button. Prefers the LED-preview visuals table
    /// (<see cref="_evKeyVisuals"/>, already LED-index-keyed and canvas-
    /// disambiguated); falls back to a reverse map lookup + canvas scan for the
    /// window between overlay build and visuals build.</summary>
    private Button? FindKeyButtonByLed(int led)
    {
        if (_evKeyVisuals.TryGetValue(led, out var vis)) return vis.Button;
        foreach (var kvp in Models.LedMatrixMapping.EverestNumpad)
            if (kvp.Value == led) return FindKeyInCanvas(CvsEvNumpad, kvp.Key);
        foreach (var kvp in Models.LedMatrixMapping.EverestKeyboard)
            if (kvp.Value == led) return FindKeyInCanvas(CvsEvKeyboard, kvp.Key);
        return null;
    }

    private Button? FindKeyInCanvas(Canvas? canvas, int matrixId)
    {
        if (canvas == null) return null;
        return canvas.Children.OfType<Button>()
            .FirstOrDefault(b => !_ndkButtons.Contains(b) && b.Tag is int id && id == matrixId);
    }

    // ─────────────────── Simulated dynamic-effect preview ───────────────────
    // K2 has no way to see the REAL animation (that only exists on the firmware),
    // so this is a cosmetic approximation — good enough to tell Rainbow/Breathing/
    // Wave/Tornado apart at a glance and confirm a color/direction/speed change
    // took effect, not a faithful reproduction of the firmware's actual timing or
    // curve. User request 2026-07-25: painting a dynamic effect used to just tint
    // every assigned key with a frozen Color1 (Rainbow always looked plain red,
    // since Color1 is irrelevant/hidden for that mode). Runs at 20fps while paint
    // mode is active; only touches LEDs actually in <see cref="_customKeyEffects"/>.

    private DispatcherTimer? _customFxPreviewTimer;
    private readonly Stopwatch _customFxPreviewClock = Stopwatch.StartNew();

    private void StartCustomFxPreview()
    {
        if (_customFxPreviewTimer != null) return;
        _customFxPreviewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _customFxPreviewTimer.Tick += CustomFxPreviewTick;
        _customFxPreviewTimer.Start();
    }

    private void StopCustomFxPreview()
    {
        if (_customFxPreviewTimer is null) return;
        _customFxPreviewTimer.Stop();
        _customFxPreviewTimer.Tick -= CustomFxPreviewTick;
        _customFxPreviewTimer = null;
    }

    private void CustomFxPreviewTick(object? sender, EventArgs e)
    {
        if (_customKeyEffects.Count == 0) return;
        double t = _customFxPreviewClock.Elapsed.TotalSeconds;
        foreach (var kvp in _customKeyEffects)
        {
            var btn = FindKeyButtonByLed(kvp.Key);
            if (btn is null) continue;
            var p = FxParamsFor(kvp.Value);
            ApplyColorOverlay(btn, ComputeFxPreviewColor(kvp.Value, kvp.Key, p, btn, t));
        }
    }

    /// <summary>Picks an animation shape per color mode/effect: Rainbow cycles hue
    /// (positionally offset along the wave/tornado direction so it visibly sweeps,
    /// or by LED index for effects with no direction concept); two-color effects
    /// (Dual mode, or Reactive/Matrix/Yeti which are ALWAYS two colors — see
    /// CustomFxCapsFor's TwoStopLayout) crossfade Color1↔Color2 like a slow blink;
    /// Breathing in Single mode pulses Color1's brightness; Wave/Tornado in Single
    /// mode sweep a brighter band of Color1 across the assigned keys along the
    /// chosen direction. Speed maps to 0.15-1.5 animation cycles/sec (0=slowest,
    /// 100=fastest, same convention as the speed slider) — not the real firmware
    /// scale, just a UI-reasonable range.</summary>
    private Color ComputeFxPreviewColor(EverestService.Effect eff, int led, CustomFxParams p, Button btn, double t)
    {
        double cyclesPerSec = 0.15 + p.Speed / 100.0 * 1.35;

        if (p.Mode == EverestSideLedProtocol.CustomEffectColorMode.Rainbow)
        {
            double phase = eff is EverestService.Effect.Wave or EverestService.Effect.Tornado
                ? FxPreviewPositionalPhase(btn, eff, p.Direction)
                : led % 16 / 16.0;
            double hue = (t * cyclesPerSec * 360.0 + phase * 360.0) % 360.0;
            return HsvToRgb(hue, 1.0, 1.0);
        }

        bool twoColor = p.Mode == EverestSideLedProtocol.CustomEffectColorMode.Dual
            || eff is EverestService.Effect.ReactiveA or EverestService.Effect.Matrix or EverestService.Effect.Yeti;
        if (twoColor)
        {
            double phase = led % 16 / 16.0;
            double wave = (Math.Sin(2 * Math.PI * (t * cyclesPerSec + phase)) + 1) / 2;
            return LerpColor(p.Color2, p.Color1, wave);
        }

        if (eff == EverestService.Effect.Breath)
        {
            double wave = (Math.Sin(2 * Math.PI * t * cyclesPerSec) + 1) / 2;
            return LerpColor(Colors.Black, p.Color1, 0.15 + 0.85 * wave);
        }

        // Wave/Tornado, Single mode: traveling bright band along the direction.
        double bandPhase = FxPreviewPositionalPhase(btn, eff, p.Direction);
        double band = (Math.Cos(2 * Math.PI * (bandPhase - t * cyclesPerSec)) + 1) / 2;
        return LerpColor(Colors.Black, p.Color1, 0.2 + 0.8 * band);
    }

    /// <summary>0-1 position of <paramref name="btn"/> along the effect's direction,
    /// in <see cref="CvsEvRubberBand"/>'s coordinate space (spans the whole device
    /// box, so keyboard/numpad keys share one consistent frame). Tornado uses the
    /// angle around the box's center instead of a straight axis.</summary>
    private double FxPreviewPositionalPhase(Button btn, EverestService.Effect eff, int direction)
    {
        double w = CvsEvRubberBand.ActualWidth, h = CvsEvRubberBand.ActualHeight;
        if (w <= 0 || h <= 0 || !btn.IsVisible) return 0;
        Point center = btn.TransformToVisual(CvsEvRubberBand).Transform(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));

        if (eff == EverestService.Effect.Tornado)
        {
            double angle = Math.Atan2(center.Y - h / 2, center.X - w / 2); // -pi..pi
            double norm = (angle + Math.PI) / (2 * Math.PI); // 0..1
            return direction == 10 ? 1 - norm : norm; // 10=Counter-CW, reversed sweep
        }

        return direction switch
        {
            4 => 1 - center.X / w, // Left
            2 => center.Y / h,     // Down
            6 => 1 - center.Y / h, // Up
            _ => center.X / w,     // Right (0) and fallback
        };
    }

    private static Color HsvToRgb(double hueDeg, double s, double v)
    {
        hueDeg = (hueDeg % 360 + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(hueDeg / 60.0 % 2 - 1));
        double m = v - c;
        var (r, g, b) = hueDeg switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    // ─────────────────────── Event handlers ───────────────────────

    /// <summary>
    /// Paint mode is no longer a separate checkbox (user feedback 2026-07-22: "quando
    /// metto custom dalla tendina considera sempre paint mode attiva e togli la
    /// checkbox") — it's implicitly on whenever CbEvEffect="Custom" is selected. Called
    /// from MainWindow.Everest.cs's UpdateEvCapabilities (which already computes
    /// isCustom) and from ResetCustomLightingViewState (leaving the RGB section
    /// entirely forces it off regardless of CbEvEffect's stored selection).
    /// </summary>
    private void SetCustomPaintModeActive(bool active)
    {
        _customPaintMode = active;
        UpdateBorderOverlayVisibility();
        UpdateDockVisibility();
        if (_customPaintMode)
        {
            ReapplyCustomOverlays();
            StartCustomFxPreview();
        }
        else
        {
            StopCustomFxPreview();
            ClearAllOverlays();
        }
    }

    /// <summary>
    /// Forces Custom Lighting's view state off (paint mode, border overlay, wide
    /// numpad gap) — called when leaving the RGB &amp; Lighting section entirely
    /// (ShowEvSection in MainWindow.SectionNav.cs), independently of whatever
    /// CbEvEffect is still set to, so the border squares/wide gap don't linger over
    /// Key Binding/Settings/Dial (user feedback 2026-07-22: "se esco dalla sezione
    /// lighting riavvicina tastiera e numpad e disattiva la visualizzazione del
    /// bordo led").
    /// </summary>
    private void ResetCustomLightingViewState() => SetCustomPaintModeActive(false);

    /// <summary>Border squares are only paintable under the "Static" paint effect —
    /// matches Base Camp's own behavior (user description 2026-07-22: "con gli effetti
    /// dinamici si possono dipingere solo i led dei keycap"). Called on both paint-mode
    /// toggle and paint-effect change. The board-to-board gap widening (each side's
    /// squares extend 14px past its own canvas edge — see BuildBorderSquares — so 28px+
    /// is needed between the two canvases to avoid touching) is delegated to
    /// MainWindow.Layout.cs's ApplyNumpadGap, which also knows which side the numpad is
    /// currently on and re-asserts against the 3s accessory-poll timer that would
    /// otherwise stomp a margin set only here.</summary>
    private void UpdateBorderOverlayVisibility()
    {
        bool show = _customPaintMode && CbCustomPaintEffect.SelectedIndex <= 0;
        CvsEvBorderMain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The numpad's 14 border squares only make sense with a numpad to draw them
        // around: UpdateKeyboardLayout collapses CvsEvNumpad when detached but this
        // overlay is a separate canvas, so gate it on _evNumpadConnected too (user
        // report 2026-07-22) — the accessory poll re-calls us on attach/detach.
        CvsEvBorderNumpad.Visibility = show && _evNumpadConnected ? Visibility.Visible : Visibility.Collapsed;
        ApplyNumpadGap();
    }

    private void CbCustomPaintEffect_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateBorderOverlayVisibility();
        UpdateCustomFxParamsVisibility();
    }

    private void BtnCustomBrushColor_Click(object sender, RoutedEventArgs e)
    {
        int current = (_customBrushColor.R << 16) | (_customBrushColor.G << 8) | _customBrushColor.B;
        int? picked = K2.Core.ColorPickerDialog.Pick(this, current);
        if (picked is not int rgb) return;

        _customBrushColor = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        BtnCustomBrushColor.Background = new SolidColorBrush(_customBrushColor);
    }

    // ─────────────────────── Dynamic effect params panel ───────────────────────

    /// <summary>
    /// Shows/hides <c>PnlCustomFxParams</c> (direction/speed/color-mode/colors) per
    /// whether <see cref="CbCustomPaintEffect"/>'s current selection is a dynamic effect
    /// (<see cref="CustomFxCapsFor"/> non-null), and populates the controls from that
    /// effect's own remembered param set (<see cref="FxParamsFor"/>) — mirrors
    /// MainWindow.Everest.cs's UpdateEvCapabilities for the global RGB panel. Only
    /// Static keeps showing the plain brush-color swatch (Off always paints black
    /// regardless of the brush color, so the swatch would be misleading there).
    /// </summary>
    private void UpdateCustomFxParamsVisibility()
    {
        var caps = CustomFxCapsFor(CbCustomPaintEffect.SelectedIndex);
        bool dynamic = caps != null;
        bool isStatic = CbCustomPaintEffect.SelectedIndex == 0;
        PnlCustomFxParams.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
        LblCustomColorStatic.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        BtnCustomBrushColor.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        if (caps is null) return;

        var p = FxParamsFor(caps.Eff);
        bool prevSuppress = _customFxSuppress;
        _customFxSuppress = true;
        try
        {
            if (caps.DirLabels.Length > 0)
            {
                int di = System.Array.IndexOf(caps.DirCodes, p.Direction);
                if (di < 0) di = 0;
                SegmentedButtonGroup.Rebuild(GridCustomFxDirection, "CustomFxDirection", caps.DirLabels, RbCustomFxDirection_Checked, di);
                PnlCustomFxDirection.Visibility = Visibility.Visible;
            }
            else
            {
                GridCustomFxDirection.Children.Clear();
                PnlCustomFxDirection.Visibility = Visibility.Collapsed;
            }

            // Reactive/Matrix/Yeti (TwoStopLayout) are always two colors, no mode
            // choice — hide the radio group entirely and force Dual for the color-row
            // visibility logic below.
            PnlCustomFxColorMode.Visibility = caps.TwoStopLayout ? Visibility.Collapsed : Visibility.Visible;
            RbCustomFxDual.Visibility = caps.Dual ? Visibility.Visible : Visibility.Collapsed;
            RbCustomFxDual.IsEnabled = caps.Dual;
            RbCustomFxRainbow.Visibility = caps.Rainbow ? Visibility.Visible : Visibility.Collapsed;
            RbCustomFxRainbow.IsEnabled = caps.Rainbow;

            var mode = caps.TwoStopLayout ? EverestSideLedProtocol.CustomEffectColorMode.Dual : p.Mode;
            if (mode == EverestSideLedProtocol.CustomEffectColorMode.Rainbow && caps.Rainbow) RbCustomFxRainbow.IsChecked = true;
            else if (mode == EverestSideLedProtocol.CustomEffectColorMode.Dual && caps.Dual) RbCustomFxDual.IsChecked = true;
            else RbCustomFxSingle.IsChecked = true;

            SldCustomFxSpeed.Value = p.Speed;
            LblCustomFxSpeed.Text = $"{p.Speed}%";
            BtnCustomFxColor1.Background = new SolidColorBrush(p.Color1);
            BtnCustomFxColor2.Background = new SolidColorBrush(p.Color2);

            UpdateCustomFxColorRowVisibility(caps, mode);
        }
        finally
        {
            _customFxSuppress = prevSuppress;
        }
    }

    /// <summary>Color-swatch rows follow the color mode, same pattern as the global RGB
    /// panel's UpdateEvColorRowVisibility — Color1 hidden under Rainbow, Color2 shown
    /// only under Dual (or always, for the TwoStopLayout effects which have no mode
    /// choice and are always two colors).</summary>
    private void UpdateCustomFxColorRowVisibility(CustomFxCaps caps, EverestSideLedProtocol.CustomEffectColorMode mode)
    {
        if (caps.TwoStopLayout)
        {
            PnlCustomFxColor1.Visibility = Visibility.Visible;
            PnlCustomFxColor2.Visibility = Visibility.Visible;
            return;
        }
        bool rainbow = mode == EverestSideLedProtocol.CustomEffectColorMode.Rainbow;
        PnlCustomFxColor1.Visibility = rainbow ? Visibility.Collapsed : Visibility.Visible;
        PnlCustomFxColor2.Visibility = !rainbow && mode == EverestSideLedProtocol.CustomEffectColorMode.Dual
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RbCustomFxColorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_customFxSuppress) return;
        var caps = CustomFxCapsFor(CbCustomPaintEffect.SelectedIndex);
        if (caps is null) return;
        var p = FxParamsFor(caps.Eff);
        p.Mode = RbCustomFxRainbow.IsChecked == true ? EverestSideLedProtocol.CustomEffectColorMode.Rainbow
               : RbCustomFxDual.IsChecked == true ? EverestSideLedProtocol.CustomEffectColorMode.Dual
               : EverestSideLedProtocol.CustomEffectColorMode.Single;
        UpdateCustomFxColorRowVisibility(caps, p.Mode);
    }

    private void RbCustomFxDirection_Checked(object sender, RoutedEventArgs e)
    {
        if (_customFxSuppress) return;
        var caps = CustomFxCapsFor(CbCustomPaintEffect.SelectedIndex);
        if (caps is null || sender is not RadioButton rb) return;
        int di = (int)rb.Tag;
        if (di >= 0 && di < caps.DirCodes.Length)
            FxParamsFor(caps.Eff).Direction = caps.DirCodes[di];
    }

    private void SldCustomFxSpeed_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblCustomFxSpeed != null) LblCustomFxSpeed.Text = $"{(int)SldCustomFxSpeed.Value}%";
        if (_customFxSuppress) return;
        var caps = CustomFxCapsFor(CbCustomPaintEffect.SelectedIndex);
        if (caps is null) return;
        FxParamsFor(caps.Eff).Speed = (int)SldCustomFxSpeed.Value;
    }

    private void BtnCustomFxColor_Click(object sender, RoutedEventArgs e)
    {
        var caps = CustomFxCapsFor(CbCustomPaintEffect.SelectedIndex);
        if (caps is null || sender is not Button { Tag: string tag } btn) return;
        var p = FxParamsFor(caps.Eff);
        var current = tag == "1" ? p.Color1 : p.Color2;

        int currentRgb = (current.R << 16) | (current.G << 8) | current.B;
        int? picked = K2.Core.ColorPickerDialog.Pick(this, currentRgb);
        if (picked is not int rgb) return;

        var color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        if (tag == "1") p.Color1 = color; else p.Color2 = color;
        btn.Background = new SolidColorBrush(color);
        if (tag == "1") RetintKeysForEffect(caps.Eff); // Color2 never shows in the overlay preview
    }

    /// <summary>
    /// Keycaps AND border in one raw-HID apply (<see cref="EverestService.
    /// ApplyEverestCustomLighting"/>), replicating Base Camp's own wire sequence
    /// byte-for-byte. Resolved 2026-07-22 by diffing real BC captures
    /// (evmax_anchors_bc / evmax_numpad_bc / evmax_fillall_bc / evmax_fillall_k2 in
    /// _reference/usb_dumps): (1) the raw <c>14 2C 00 01</c> keycap pages ARE positional
    /// in the same index domain as Models.LedMatrixMapping — every one of the 25 keys
    /// painted individually in BC landed exactly on its mapped index, so the "borrowed"
    /// mapping was never the problem; (2) SDKDLL.dll's ChangeCustomizeEffect produced NO
    /// wire traffic at all in K2's captured apply — that's why keys never changed; (3)
    /// what K2 was missing on the raw path: the <c>11 01 00 02 02 02</c> zone switch
    /// before the pages, the correct page count (7, not BaseCampLinux's 8), and the
    /// 0-100 brightness scale (not 0-255).
    /// </summary>
    private void BtnCustomApply_Click(object sender, RoutedEventArgs e)
    {
        if (_everest is null || !_everest.IsOpen) return;
        ApplyCustomColorsToDevice((byte)SldEvBrightness.Value);
        SaveCustomColorsToStore();
    }

    /// <summary>
    /// Sends the current in-memory paint state to the device via the raw-HID custom
    /// apply: <see cref="_customKeyColors"/>/<see cref="_customSideColors"/> (plain
    /// Static/Off colors — unpainted positions go out black, so an empty state means
    /// "everything off") PLUS <see cref="_customKeyEffects"/>/<see cref="_customFxParams"/>
    /// (dynamic per-region effects, added 2026-07-22 — see EverestSideLedProtocol's
    /// per-region-effects section). Called by the panel's Apply button and by
    /// ApplyCurrentEffect when the Custom effect is (re)selected (auto-apply of the
    /// remembered state, user request 2026-07-22).
    /// </summary>
    private bool ApplyCustomColorsToDevice(byte brightness = 100)
    {
        if (_everest is null || !_everest.IsOpen) return false;

        // _customKeyColors is already keyed by LED index = wire position. LEDs governed
        // by a dynamic effect are deliberately NOT in this map (PaintLed keeps the two
        // maps mutually exclusive), so they stay black here — the effect's own param
        // packet + region bitmap below are what actually light them.
        var keycapWire = new int[Services.EverestSideLedProtocol.KeycapWireCount];
        foreach (var kvp in _customKeyColors)
        {
            if (kvp.Key < 0 || kvp.Key >= keycapWire.Length) continue;
            keycapWire[kvp.Key] = (kvp.Value.R << 16) | (kvp.Value.G << 8) | kvp.Value.B;
        }

        var sideWire = new int[Services.EverestSideLedProtocol.TotalCount];
        foreach (var kvp in _customSideColors)
            if (kvp.Key >= 0 && kvp.Key < sideWire.Length)
                sideWire[kvp.Key] = (kvp.Value.R << 16) | (kvp.Value.G << 8) | kvp.Value.B;

        var (ledEffectCode, effectParamPackets) = BuildEffectRegionState(brightness);

        bool ok = _everest.ApplyEverestCustomLighting(keycapWire, sideWire, brightness,
            ledEffectCode: ledEffectCode, effectParamPackets: effectParamPackets);
        LogEverest($"[CUSTOM] Applied {_customKeyColors.Count} keycap + {_customSideColors.Count} border LEDs + " +
                    $"{_customKeyEffects.Count} dynamic-effect LED(s) ({effectParamPackets.Count} effect(s)) via raw HID (bright={brightness}) -> {ok}");
        return ok;
    }

    /// <summary>Builds the 180-slot LED→effect-code bitmap and one parameter packet per
    /// distinct effect currently in use, from <see cref="_customKeyEffects"/>/
    /// <see cref="_customFxParams"/> — see <see cref="EverestService.ApplyEverestCustomLighting"/>'s
    /// new optional args and EverestSideLedProtocol's per-region-effects section.
    /// <paramref name="brightness"/> is the single device-wide brightness (SldEvBrightness),
    /// same value used for the static keycap pages.</summary>
    private (byte[] ledEffectCode, List<byte[]> effectParamPackets) BuildEffectRegionState(byte brightness)
    {
        var ledEffectCode = new byte[Services.EverestSideLedProtocol.EffectRegionSlotCount];
        foreach (var kvp in _customKeyEffects)
            if (kvp.Key >= 0 && kvp.Key < ledEffectCode.Length)
                ledEffectCode[kvp.Key] = (byte)kvp.Value;

        var packets = new List<byte[]>();
        foreach (var eff in _customKeyEffects.Values.Distinct())
            packets.Add(BuildEffectParamPacket(eff, brightness));
        return (ledEffectCode, packets);
    }

    /// <summary>Builds ONE effect's parameter packet from its remembered
    /// <see cref="CustomFxParams"/>, dispatching to <see cref="EverestSideLedProtocol.
    /// BuildCustomEffectParamPacket"/> (Wave/Tornado/Breathing) or
    /// <see cref="EverestSideLedProtocol.BuildTwoStopEffectParamPacket"/> (Reactive/
    /// Matrix/Yeti, always two colors — see CustomFxCapsFor's TwoStopLayout flag).
    /// hwSpeed uses the same inversion formula as the global RGB panel/BaseCampLinux:
    /// 1=fastest, 100=slowest.</summary>
    private byte[] BuildEffectParamPacket(EverestService.Effect eff, byte brightness)
    {
        var p = FxParamsFor(eff);
        byte hwSpeed = (byte)Math.Clamp(101 - p.Speed, 1, 100);
        (byte, byte, byte) C(Color c) => (c.R, c.G, c.B);

        if (eff is EverestService.Effect.ReactiveA or EverestService.Effect.Matrix or EverestService.Effect.Yeti)
            return Services.EverestSideLedProtocol.BuildTwoStopEffectParamPacket(
                (byte)eff, brightness, hwSpeed, C(p.Color1), C(p.Color2));

        byte direction = (byte)p.Direction;
        (byte, byte, byte)? color2 = p.Mode == Services.EverestSideLedProtocol.CustomEffectColorMode.Dual ? C(p.Color2) : null;
        return Services.EverestSideLedProtocol.BuildCustomEffectParamPacket(
            (byte)eff, brightness, hwSpeed, p.Mode, direction, C(p.Color1), color2);
    }

    // NB: the "Read from device" button was removed 2026-07-22 (user request): it had
    // already been demoted to a local-store reload (SDKDLL's GetEffCustomizeContent
    // returned garbage on real hardware and raw HID has no known read-back command),
    // and that reload happens implicitly on panel init and paint-mode activation
    // (LoadCustomColorsFromStore + ReapplyCustomOverlays).

    private void BtnCustomClear_Click(object sender, RoutedEventArgs e)
    {
        _customKeyColors.Clear();
        _customKeyEffects.Clear();
        _customSideColors.Clear();
        ClearAllOverlays();
        SaveCustomColorsToStore();
        LogEverest("[CUSTOM] Custom colors cleared");
    }

    private void BtnCustomFillAll_Click(object sender, RoutedEventArgs e)
    {
        // Fill every known LED index (values of both LedMatrixMapping tables — main
        // board + numpad, no overlap) per the selected paint effect — plain color
        // (Static/Off) via PaintLed, or a dynamic-effect region assignment. Border
        // squares only fill under Static (matches the border's own paint-click rule —
        // UpdateBorderOverlayVisibility already hides them for every other effect).
        foreach (var led in Models.LedMatrixMapping.EverestKeyboard.Values.Concat(Models.LedMatrixMapping.EverestNumpad.Values))
        {
            var btn = FindKeyButtonByLed(led);
            if (btn != null) PaintLed(led, btn);
        }
        if (CbCustomPaintEffect.SelectedIndex == 0)
            for (int i = 0; i < Services.EverestSideLedProtocol.TotalCount; i++)
                _customSideColors[i] = _customBrushColor;
        ReapplyCustomOverlays();
        LogEverest($"[CUSTOM] Fill All ({CbCustomPaintEffect.SelectedItem}) applied to every key" +
                    (CbCustomPaintEffect.SelectedIndex == 0 ? $" + {Services.EverestSideLedProtocol.TotalCount} border LEDs" : ""));
    }

    // ─────────────────────── Persistence ───────────────────────

    private void SaveCustomColorsToStore()
    {
        if (_evStore is null) return;
        string p = EvCustomPrefix();

        // Save as JSON: { "ledIndex": "#RRGGBB", ... }. Key renamed keyColors →
        // keyLedColors 2026-07-22 when the dictionary switched from VK-keyed to
        // LED-index-keyed (see _customKeyColors' doc) — old VK-keyed data under the
        // previous name would be misread as LED indices, so it's simply orphaned.
        var dict = _customKeyColors.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => $"#{kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}");
        _evStore.SetSetting(p + "keyLedColors", JsonSerializer.Serialize(dict));

        var sideDict = _customSideColors.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => $"#{kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}");
        _evStore.SetSetting(p + "sideColors", JsonSerializer.Serialize(sideDict));

        // LED → dynamic-effect assignment (byte value = EverestService.Effect).
        var fxDict = _customKeyEffects.ToDictionary(kvp => kvp.Key.ToString(), kvp => (byte)kvp.Value);
        _evStore.SetSetting(p + "keyEffects", JsonSerializer.Serialize(fxDict));

        // Per-effect param sets — only the ones actually touched (FxParamsFor is lazy).
        var paramsDict = _customFxParams.ToDictionary(
            kvp => ((byte)kvp.Key).ToString(),
            kvp => new CustomFxParamsDto(
                (byte)kvp.Value.Mode, kvp.Value.Direction, kvp.Value.Speed,
                $"#{kvp.Value.Color1.R:X2}{kvp.Value.Color1.G:X2}{kvp.Value.Color1.B:X2}",
                $"#{kvp.Value.Color2.R:X2}{kvp.Value.Color2.G:X2}{kvp.Value.Color2.B:X2}"));
        _evStore.SetSetting(p + "fxParams", JsonSerializer.Serialize(paramsDict));
    }

    /// <summary>JSON shape for one dynamic effect's persisted param set — colors as hex
    /// strings since <see cref="Color"/> itself doesn't round-trip through
    /// JsonSerializer.</summary>
    private sealed record CustomFxParamsDto(byte Mode, int Direction, int Speed, string Color1, string Color2);

    /// <summary>Restores the painted board from Settings (see <see cref="EvCustomPrefix"/>):
    /// falls back once to the legacy always-global <c>custom.*</c> keys for a profile that
    /// has no per-profile value yet (existing installs), same pattern as
    /// LoadMacroLedFromStore.</summary>
    private void LoadCustomColorsFromStore()
    {
        if (_evStore is null) return;
        string p = EvCustomPrefix();
        const string gp = "custom.";
        string? Setting(string key) => _evStore.GetSetting(p + key) ?? _evStore.GetSetting(gp + key);

        // Cleared up front, not inside each parse block: on a profile switch the new
        // profile may simply have no painted board, and the previous one's colors must
        // not survive into it.
        _customKeyColors.Clear();
        _customSideColors.Clear();

        var json = Setting("keyLedColors");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    _customKeyColors.Clear();
                    foreach (var kvp in dict)
                    {
                        if (int.TryParse(kvp.Key, out int led))
                        {
                            try
                            {
                                var c = (Color)ColorConverter.ConvertFromString(kvp.Value);
                                _customKeyColors[led] = c;
                            }
                            catch { /* ignore unparsable colors */ }
                        }
                    }
                }
            }
            catch { /* ignore invalid JSON */ }
        }

        var sideJson = Setting("sideColors");
        if (!string.IsNullOrWhiteSpace(sideJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(sideJson);
                if (dict != null)
                {
                    _customSideColors.Clear();
                    foreach (var kvp in dict)
                    {
                        if (int.TryParse(kvp.Key, out int wireIdx))
                        {
                            try
                            {
                                var c = (Color)ColorConverter.ConvertFromString(kvp.Value);
                                _customSideColors[wireIdx] = c;
                            }
                            catch { /* ignore unparsable colors */ }
                        }
                    }
                }
            }
            catch { /* ignore invalid JSON */ }
        }

        _customKeyEffects.Clear();
        var fxJson = Setting("keyEffects");
        if (!string.IsNullOrWhiteSpace(fxJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, byte>>(fxJson);
                if (dict != null)
                    foreach (var kvp in dict)
                        if (int.TryParse(kvp.Key, out int led))
                            _customKeyEffects[led] = (EverestService.Effect)kvp.Value;
            }
            catch { /* ignore invalid JSON */ }
        }

        _customFxParams.Clear();
        var paramsJson = Setting("fxParams");
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, CustomFxParamsDto>>(paramsJson);
                if (dict != null)
                    foreach (var kvp in dict)
                    {
                        if (!byte.TryParse(kvp.Key, out byte effByte) || kvp.Value is not { } dto) continue;
                        try
                        {
                            _customFxParams[(EverestService.Effect)effByte] = new CustomFxParams
                            {
                                Mode = (Services.EverestSideLedProtocol.CustomEffectColorMode)dto.Mode,
                                Direction = dto.Direction,
                                Speed = dto.Speed,
                                Color1 = (Color)ColorConverter.ConvertFromString(dto.Color1),
                                Color2 = (Color)ColorConverter.ConvertFromString(dto.Color2),
                            };
                        }
                        catch { /* ignore unparsable entry */ }
                    }
            }
            catch { /* ignore invalid JSON */ }
        }
    }

    // ─────────────────── Rectangular multi-LED selection ───────────────────
    // Drag a rubber-band square anywhere over the device box (keys, numpad,
    // border squares) to paint every LED it touches with the brush color (user
    // request 2026-07-22, mirrors Base Camp's multi-select). Wired to
    // BdrEvDeviceBox's Preview mouse events (MainWindow.xaml) so the drag can
    // start on top of a key Button; a plain click (below the 5px threshold)
    // falls through to the normal single-key paint.
    //
    // Also engages during Settings' "Edit individual keycaps" mode (user request
    // 2026-07-26): same drag gesture, but instead of painting it collects every
    // key the rectangle touches and opens ONE KeycapCustomizeDialog whose
    // result is applied to all of them — see OpenKeycapDialogForRect
    // (MainWindow.KeycapAppearance.cs). The two modes are mutually exclusive
    // (Custom Lighting vs. Settings section), so only one gate is ever true.

    private Point _rubberStart;
    private bool _rubberTracking; // mouse down seen, watching for drag threshold
    private bool _rubberActive;   // threshold passed, rubber band visible

    private void EvDeviceBox_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_customPaintMode && !(_evKeycapEditMode && IsEvAppearanceSectionActive)) return;
        _rubberStart = e.GetPosition(CvsEvRubberBand);
        _rubberTracking = true;
        _rubberActive = false;
    }

    private void EvDeviceBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_rubberTracking) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            CancelRubberBand();
            return;
        }
        var p = e.GetPosition(CvsEvRubberBand);
        if (!_rubberActive)
        {
            if (Math.Abs(p.X - _rubberStart.X) < 5 && Math.Abs(p.Y - _rubberStart.Y) < 5) return;
            _rubberActive = true;
            RectEvRubberBand.Visibility = Visibility.Visible;
            // Steal capture from whatever key Button the drag started on, so it
            // neither clicks on release nor keeps eating our move events.
            BdrEvDeviceBox.CaptureMouse();
        }
        var r = new Rect(_rubberStart, p);
        Canvas.SetLeft(RectEvRubberBand, r.X);
        Canvas.SetTop(RectEvRubberBand, r.Y);
        RectEvRubberBand.Width  = r.Width;
        RectEvRubberBand.Height = r.Height;
    }

    private void EvDeviceBox_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_rubberTracking) return;
        bool wasActive = _rubberActive;
        var rect = wasActive ? new Rect(_rubberStart, e.GetPosition(CvsEvRubberBand)) : Rect.Empty;
        CancelRubberBand();
        if (!wasActive) return; // plain click: let the Button handle it normally
        e.Handled = true;       // suppress the click that would otherwise fire on release
        if (_customPaintMode)
            PaintLedsInRect(rect);
        else if (_evKeycapEditMode && IsEvAppearanceSectionActive)
            OpenKeycapDialogForRect(rect);
    }

    private void CancelRubberBand()
    {
        _rubberTracking = false;
        _rubberActive = false;
        RectEvRubberBand.Visibility = Visibility.Collapsed;
        if (BdrEvDeviceBox.IsMouseCaptured) BdrEvDeviceBox.ReleaseMouseCapture();
    }

    /// <summary>Paints every key Button and (under the Static paint effect) every
    /// border square whose on-screen bounds intersect <paramref name="rect"/>
    /// (CvsEvRubberBand coordinate space, which spans the whole device box).</summary>
    private void PaintLedsInRect(Rect rect)
    {
        int painted = 0;

        void TryPaintButton(Button btn, Action paint)
        {
            if (!btn.IsVisible) return;
            var bounds = btn.TransformToVisual(CvsEvRubberBand)
                .TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));
            if (!rect.IntersectsWith(bounds)) return;
            paint();
            painted++;
        }

        foreach (var canvas in new[] { CvsEvKeyboard, CvsEvNumpad })
        {
            if (canvas is null || !canvas.IsVisible) continue;
            foreach (var btn in canvas.Children.OfType<Button>())
            {
                if (_ndkButtons.Contains(btn)) continue;
                if (btn.Tag is not int vk) continue;
                TryPaintButton(btn, () =>
                {
                    if (TryButtonToLed(btn, vk, out int led))
                        PaintLed(led, btn);
                });
            }
        }

        // Border squares are only paintable under Static — same rule as
        // BorderSquare_Click/UpdateBorderOverlayVisibility.
        if (CbCustomPaintEffect.SelectedIndex <= 0)
        {
            foreach (var kvp in _customSideButtons)
            {
                var btn = kvp.Value;
                TryPaintButton(btn, () =>
                {
                    _customSideColors[kvp.Key] = _customBrushColor;
                    ApplyColorOverlay(btn, _customBrushColor);
                });
            }
        }

        LogEverest($"[CUSTOM] Rubber-band selection painted {painted} LED(s) with #{_customBrushColor.R:X2}{_customBrushColor.G:X2}{_customBrushColor.B:X2}");
    }
}
