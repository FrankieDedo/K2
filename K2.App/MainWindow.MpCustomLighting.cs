// MainWindow.MpCustomLighting.cs — partial class: MacroPad "Custom" (per-key)
// lighting. Mirrors Everest Max's Custom Lighting panel (MainWindow.CustomLighting.cs)
// adapted to the MacroPad's own — much simpler — verified protocol: 12 physical
// keys, no LED subdivision, no side ring/numpad. See MacroPadSdkNative.cs's doc
// comment on SwitchToCustomizeEffect/ChangeCustomizeStatic/SetCustomizeTable for
// the full decompile-verified call sequence this replicates.
//
// At feature parity with Everest Max's panel: rubber-band multi-select
// (PaintMpKeysInRect, reusing BdrMpDeviceBox/CvsMpRubberBand — see
// MainWindow.MacroKeycapAppearance.cs) and the simulated per-effect animation
// preview (ComputeMpFxPreviewColor/MpCustomFxPreviewTick) are both wired up.
//
// One deliberate simplification remains (documented, not an oversight):
// Custom-mode Wave/Tornado direction does NOT get the device-rotation
// correction the whole-device preset panel applies (MacroPhysicalDirIndex).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Currently selected brush color (Static/Off paint).</summary>
    private Color _mpCustomBrushColor = Color.FromRgb(0xFF, 0x00, 0x00);

    /// <summary>Key index (0-11) -> flat color assigned by the user (Static/Off paint).</summary>
    private readonly Dictionary<int, Color> _mpCustomKeyColors = new();

    /// <summary>Key index -> dynamic effect assigned (Wave/Tornado/Breathing/Reactive/Matrix/
    /// Yeti). A key is in exactly one of this map or <see cref="_mpCustomKeyColors"/>, never
    /// both — painting with a dynamic effect removes any static color at that key and vice
    /// versa (mirrors Everest Max's _customKeyColors/_customKeyEffects split).</summary>
    private readonly Dictionary<int, MacroPadService.Effect> _mpCustomKeyEffects = new();

    /// <summary>Per-dynamic-effect shared params — every key assigned to an effect shares its
    /// ONE param set: the firmware only takes one color/speed/direction packet per effect
    /// index, not per key (see MacroPadSdkNative's Custom-mode doc comment).</summary>
    private readonly Dictionary<MacroPadService.Effect, MpCustomFxParams> _mpCustomFxParams = new();

    private enum MpColorMode { Single, Dual, Rainbow }

    private sealed class MpCustomFxParams
    {
        public MpColorMode Mode = MpColorMode.Single;
        public int Direction;
        public int Speed = 50;
        public Color Color1 = Color.FromRgb(0xFF, 0x00, 0x00);
        public Color Color2 = Color.FromRgb(0x00, 0x00, 0xFF);
    }

    /// <summary>Suppresses re-entrant saves while param controls are set programmatically.</summary>
    private bool _mpCustomFxSuppress;

    /// <summary>Prevents a key click from opening the action-configuration dialog while painting.</summary>
    private bool _mpCustomPaintMode;

    /// <summary>Paint-effect choices for <see cref="CbMpCustomPaintEffect"/> — same 8 entries as
    /// Everest Max's CustomPaintEffects (same firmware family, confirmed by a real USB capture
    /// of Base Camp's own MacroPad Custom UI, 2026-07-26: Static/Wave/Tornado/Breathing/
    /// Reactive/Matrix/Yeti/Off in sequence, dual-color and rainbow variants all present).</summary>
    private static readonly string[] MpCustomPaintEffects =
        { "Static", "Wave", "Tornado", "Breathing", "Reactive", "Matrix", "Yeti", "Off" };

    private static int MpCustomPaintOffIndex => MpCustomPaintEffects.Length - 1;

    /// <summary>Per-dynamic-effect capabilities — mirrors Everest Max's CustomFxCapsFor table
    /// (MainWindow.CustomLighting.cs), same firmware family: Wave 4-way {0,2,4,6}, Tornado
    /// CW/CCW {9,10}, Reactive/Matrix/Yeti always two colors (TwoStopLayout, no mode choice).</summary>
    private sealed record MpCustomFxCaps(
        MacroPadService.Effect Eff, bool Rainbow, bool Dual, string[] DirLabels, int[] DirCodes, bool TwoStopLayout);

    private static MpCustomFxCaps? MpCustomFxCapsFor(int paintEffectIndex) => paintEffectIndex switch
    {
        1 => new(MacroPadService.Effect.Wave,      true,  false, new[] { "Right", "Down", "Left", "Up" }, new[] { 0, 2, 4, 6 }, false),
        2 => new(MacroPadService.Effect.Tornado,   true,  false, new[] { "Clockwise", "Counter-CW" },     new[] { 9, 10 },      false),
        3 => new(MacroPadService.Effect.Breath,    true,  true,  Array.Empty<string>(), Array.Empty<int>(), false),
        4 => new(MacroPadService.Effect.ReactiveA, false, true,  Array.Empty<string>(), Array.Empty<int>(), true),
        5 => new(MacroPadService.Effect.Matrix,    false, true,  Array.Empty<string>(), Array.Empty<int>(), true),
        6 => new(MacroPadService.Effect.Yeti,      false, true,  Array.Empty<string>(), Array.Empty<int>(), true),
        _ => null, // 0=Static, 7=Off
    };

    private MpCustomFxParams MpFxParamsFor(MacroPadService.Effect eff) =>
        _mpCustomFxParams.TryGetValue(eff, out var p) ? p : (_mpCustomFxParams[eff] = new MpCustomFxParams());

    // ─────────────────────── Init ───────────────────────

    /// <summary>Called from InitMacroLedPanel (MainWindow.MacroLed.cs), after the panel's own
    /// combo/sliders are populated and the key grid (InitKeysModule) already exists.</summary>
    private void InitMpCustomLightingPanel()
    {
        BtnMpCustomBrushColor.Background = new SolidColorBrush(_mpCustomBrushColor);
        CbMpCustomPaintEffect.ItemsSource = MpCustomPaintEffects;
        CbMpCustomPaintEffect.SelectedIndex = 0;

        LoadMpCustomColorsFromStore();
        UpdateMpCustomFxParamsVisibility();

        // Edge case mirrored from Everest Max's InitCustomLightingPanel: if "Custom" was the
        // persisted macroled.effect, UpdateMpCapabilities already ran (called from
        // InitMacroLedPanel before this method) but _mpKeyVisuals doesn't exist yet at that
        // point (built later by InitLedPreview) — SetMpCustomPaintModeActive is guarded to
        // only fire while the LED Lighting section is actually active, which it never is
        // during startup init, so no catch-up is actually needed here in practice; kept for
        // symmetry/future-proofing if that init order ever changes.
        if (_mpCustomPaintMode)
            ReapplyMpCustomOverlays();
    }

    // ─────────────────────── Paint mode ───────────────────────

    /// <summary>Called from MainWindow.Keys.cs's KeyButton_Click while paint mode is active.
    /// Colors/assigns the key per the selected paint effect and consumes the click.</summary>
    internal bool TryMpCustomPaint(int keyIndex)
    {
        if (!_mpCustomPaintMode) return false;
        PaintMpKey(keyIndex);
        return true;
    }

    private void PaintMpKey(int keyIndex)
    {
        var caps = MpCustomFxCapsFor(CbMpCustomPaintEffect.SelectedIndex);
        if (caps is null)
        {
            bool off = CbMpCustomPaintEffect.SelectedIndex == MpCustomPaintOffIndex;
            var color = off ? Colors.Black : _mpCustomBrushColor;
            _mpCustomKeyEffects.Remove(keyIndex);
            _mpCustomKeyColors[keyIndex] = color;
            ApplyMpColorOverlay(keyIndex, color);
        }
        else
        {
            _mpCustomKeyColors.Remove(keyIndex);
            _mpCustomKeyEffects[keyIndex] = caps.Eff;
            // Computed the same way as every subsequent animation frame (instead of a
            // flat Color1) so a freshly-painted key immediately reads as Rainbow/Dual/
            // Single, not a placeholder solid color — see ComputeMpFxPreviewColor below.
            if (_mpKeyVisuals.TryGetValue(keyIndex, out var v))
            {
                var p = MpFxParamsFor(caps.Eff);
                double t = _mpCustomFxPreviewClock.Elapsed.TotalSeconds;
                ApplyMpColorOverlay(keyIndex, ComputeMpFxPreviewColor(caps.Eff, keyIndex, p, v.Button, t));
            }
        }
    }

    /// <summary>Re-tints every on-screen key currently assigned to <paramref name="eff"/> with
    /// its (possibly just-changed) Color1 — called after editing a dynamic effect's params.</summary>
    private void RetintMpKeysForEffect(MacroPadService.Effect eff)
    {
        var color = MpFxParamsFor(eff).Color1;
        foreach (var kvp in _mpCustomKeyEffects)
            if (kvp.Value == eff)
                ApplyMpColorOverlay(kvp.Key, color);
    }

    /// <summary>Tints a key's on-screen preview via the same keycap-appearance-aware helper the
    /// live LED preview uses (MainWindow.MacroKeycapAppearance.cs's ApplyMacroPadLedColor).</summary>
    private void ApplyMpColorOverlay(int keyIndex, Color c)
    {
        if (_mpKeyVisuals.TryGetValue(keyIndex, out var v))
            ApplyMacroPadLedColor(v, c.R, c.G, c.B);
    }

    private void ClearAllMpOverlays()
    {
        foreach (var kv in _mpKeyVisuals)
            ResetMacroPadKeyToOff(kv.Value);
    }

    private void ReapplyMpCustomOverlays()
    {
        foreach (var kvp in _mpCustomKeyColors)
            ApplyMpColorOverlay(kvp.Key, kvp.Value);
        foreach (var kvp in _mpCustomKeyEffects)
        {
            if (!_mpKeyVisuals.TryGetValue(kvp.Key, out var v)) continue;
            var p = MpFxParamsFor(kvp.Value);
            double t = _mpCustomFxPreviewClock.Elapsed.TotalSeconds;
            ApplyMpColorOverlay(kvp.Key, ComputeMpFxPreviewColor(kvp.Value, kvp.Key, p, v.Button, t));
        }
    }

    /// <summary>
    /// Paint mode is implicitly on whenever CbMacroEffect="Custom" is selected AND the LED
    /// Lighting section is the visible one — mirrors Everest Max's SetCustomPaintModeActive
    /// (no separate checkbox). Called from UpdateMpCapabilities (MainWindow.MacroLed.cs) and
    /// from ShowMpSection (MainWindow.SectionNav.cs, leaving the section forces it off).
    /// </summary>
    private void SetMpCustomPaintModeActive(bool active)
    {
        _mpCustomPaintMode = active;
        if (active)
        {
            ReapplyMpCustomOverlays();
            StartMpCustomFxPreview();
        }
        else
        {
            StopMpCustomFxPreview();
            ClearAllMpOverlays();
        }
    }

    // ─────────────── Simulated dynamic-effect preview ───────────────
    // Mirrors Everest Max's simulated animation preview (MainWindow.CustomLighting.cs's
    // StartCustomFxPreview/ComputeFxPreviewColor, 2026-07-25) — K2 has no way to see the
    // REAL animation (only exists on the firmware), so this is a cosmetic approximation,
    // not a faithful reproduction of the firmware's actual timing/curve. Reuses
    // HsvToRgb/LerpColor already defined (private, shared across this partial class) by
    // MainWindow.CustomLighting.cs. Runs at 20fps while paint mode is active; only
    // touches keys actually in _mpCustomKeyEffects.

    private DispatcherTimer? _mpCustomFxPreviewTimer;
    private readonly Stopwatch _mpCustomFxPreviewClock = Stopwatch.StartNew();

    private void StartMpCustomFxPreview()
    {
        if (_mpCustomFxPreviewTimer != null) return;
        _mpCustomFxPreviewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _mpCustomFxPreviewTimer.Tick += MpCustomFxPreviewTick;
        _mpCustomFxPreviewTimer.Start();
    }

    private void StopMpCustomFxPreview()
    {
        if (_mpCustomFxPreviewTimer is null) return;
        _mpCustomFxPreviewTimer.Stop();
        _mpCustomFxPreviewTimer.Tick -= MpCustomFxPreviewTick;
        _mpCustomFxPreviewTimer = null;
    }

    private void MpCustomFxPreviewTick(object? sender, EventArgs e)
    {
        if (_mpCustomKeyEffects.Count == 0) return;
        double t = _mpCustomFxPreviewClock.Elapsed.TotalSeconds;
        foreach (var kvp in _mpCustomKeyEffects)
        {
            if (!_mpKeyVisuals.TryGetValue(kvp.Key, out var v)) continue;
            var p = MpFxParamsFor(kvp.Value);
            ApplyMpColorOverlay(kvp.Key, ComputeMpFxPreviewColor(kvp.Value, kvp.Key, p, v.Button, t));
        }
    }

    /// <summary>Same animation shapes as Everest Max's ComputeFxPreviewColor: Rainbow cycles
    /// hue (positionally offset along the wave/tornado direction so it visibly sweeps, or by
    /// key index otherwise); two-color effects (Dual mode, or Reactive/Matrix/Yeti which are
    /// ALWAYS two colors) crossfade Color1↔Color2; Breathing in Single mode pulses Color1's
    /// brightness; Wave/Tornado in Single mode sweep a brighter band across the assigned keys
    /// along the chosen direction. Speed maps to 0.15-1.5 cycles/sec (same UI-reasonable range,
    /// not the real firmware scale).</summary>
    private Color ComputeMpFxPreviewColor(MacroPadService.Effect eff, int keyIndex, MpCustomFxParams p, Button btn, double t)
    {
        double cyclesPerSec = 0.15 + p.Speed / 100.0 * 1.35;

        if (p.Mode == MpColorMode.Rainbow)
        {
            double phase = eff is MacroPadService.Effect.Wave or MacroPadService.Effect.Tornado
                ? MpFxPreviewPositionalPhase(btn, eff, p.Direction)
                : keyIndex % 16 / 16.0;
            double hue = (t * cyclesPerSec * 360.0 + phase * 360.0) % 360.0;
            return HsvToRgb(hue, 1.0, 1.0);
        }

        bool twoColor = p.Mode == MpColorMode.Dual
            || eff is MacroPadService.Effect.ReactiveA or MacroPadService.Effect.Matrix or MacroPadService.Effect.Yeti;
        if (twoColor)
        {
            double phase = keyIndex % 16 / 16.0;
            double wave = (Math.Sin(2 * Math.PI * (t * cyclesPerSec + phase)) + 1) / 2;
            return LerpColor(p.Color2, p.Color1, wave);
        }

        if (eff == MacroPadService.Effect.Breath)
        {
            double wave = (Math.Sin(2 * Math.PI * t * cyclesPerSec) + 1) / 2;
            return LerpColor(Colors.Black, p.Color1, 0.15 + 0.85 * wave);
        }

        // Wave/Tornado, Single mode: traveling bright band along the direction.
        double bandPhase = MpFxPreviewPositionalPhase(btn, eff, p.Direction);
        double band = (Math.Cos(2 * Math.PI * (bandPhase - t * cyclesPerSec)) + 1) / 2;
        return LerpColor(Colors.Black, p.Color1, 0.2 + 0.8 * band);
    }

    /// <summary>0-1 position of <paramref name="btn"/> along the effect's direction, in
    /// CvsMpRubberBand's coordinate space (spans the whole device box). Tornado uses the
    /// angle around the box's center instead of a straight axis — mirrors Everest Max's
    /// FxPreviewPositionalPhase.</summary>
    private double MpFxPreviewPositionalPhase(Button btn, MacroPadService.Effect eff, int direction)
    {
        double w = CvsMpRubberBand.ActualWidth, h = CvsMpRubberBand.ActualHeight;
        if (w <= 0 || h <= 0 || !btn.IsVisible) return 0;
        Point center = btn.TransformToVisual(CvsMpRubberBand).Transform(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));

        if (eff == MacroPadService.Effect.Tornado)
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

    // ─────────────────────── Event handlers ───────────────────────

    private void CbMpCustomPaintEffect_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMpCustomFxParamsVisibility();

    private void BtnMpCustomBrushColor_Click(object sender, RoutedEventArgs e)
    {
        int current = (_mpCustomBrushColor.R << 16) | (_mpCustomBrushColor.G << 8) | _mpCustomBrushColor.B;
        int? picked = K2.Core.ColorPickerDialog.Pick(this, current);
        if (picked is not int rgb) return;

        _mpCustomBrushColor = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        BtnMpCustomBrushColor.Background = new SolidColorBrush(_mpCustomBrushColor);
    }

    /// <summary>Shows/hides PnlMpCustomFxParams (direction/speed/color-mode/colors) per whether
    /// CbMpCustomPaintEffect's current selection is a dynamic effect, and populates the controls
    /// from that effect's own remembered param set — mirrors Everest Max's
    /// UpdateCustomFxParamsVisibility.</summary>
    private void UpdateMpCustomFxParamsVisibility()
    {
        var caps = MpCustomFxCapsFor(CbMpCustomPaintEffect.SelectedIndex);
        bool dynamic = caps != null;
        bool isStatic = CbMpCustomPaintEffect.SelectedIndex == 0;
        PnlMpCustomFxParams.Visibility = dynamic ? Visibility.Visible : Visibility.Collapsed;
        LblMpCustomColorStatic.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        BtnMpCustomBrushColor.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        if (caps is null) return;

        var p = MpFxParamsFor(caps.Eff);
        bool prevSuppress = _mpCustomFxSuppress;
        _mpCustomFxSuppress = true;
        try
        {
            if (caps.DirLabels.Length > 0)
            {
                int di = Array.IndexOf(caps.DirCodes, p.Direction);
                if (di < 0) di = 0;
                SegmentedButtonGroup.Rebuild(GridMpCustomFxDirection, "MpCustomFxDirection", caps.DirLabels, RbMpCustomFxDirection_Checked, di);
                PnlMpCustomFxDirection.Visibility = Visibility.Visible;
            }
            else
            {
                GridMpCustomFxDirection.Children.Clear();
                PnlMpCustomFxDirection.Visibility = Visibility.Collapsed;
            }

            PnlMpCustomFxColorMode.Visibility = caps.TwoStopLayout ? Visibility.Collapsed : Visibility.Visible;
            RbMpCustomFxDual.Visibility = caps.Dual ? Visibility.Visible : Visibility.Collapsed;
            RbMpCustomFxDual.IsEnabled = caps.Dual;
            RbMpCustomFxRainbow.Visibility = caps.Rainbow ? Visibility.Visible : Visibility.Collapsed;
            RbMpCustomFxRainbow.IsEnabled = caps.Rainbow;

            var mode = caps.TwoStopLayout ? MpColorMode.Dual : p.Mode;
            if (mode == MpColorMode.Rainbow && caps.Rainbow) RbMpCustomFxRainbow.IsChecked = true;
            else if (mode == MpColorMode.Dual && caps.Dual) RbMpCustomFxDual.IsChecked = true;
            else RbMpCustomFxSingle.IsChecked = true;

            SldMpCustomFxSpeed.Value = p.Speed;
            LblMpCustomFxSpeed.Text = $"{p.Speed}%";
            BtnMpCustomFxColor1.Background = new SolidColorBrush(p.Color1);
            BtnMpCustomFxColor2.Background = new SolidColorBrush(p.Color2);

            UpdateMpCustomFxColorRowVisibility(caps, mode);
        }
        finally
        {
            _mpCustomFxSuppress = prevSuppress;
        }
    }

    private void UpdateMpCustomFxColorRowVisibility(MpCustomFxCaps caps, MpColorMode mode)
    {
        if (caps.TwoStopLayout)
        {
            PnlMpCustomFxColor1.Visibility = Visibility.Visible;
            PnlMpCustomFxColor2.Visibility = Visibility.Visible;
            return;
        }
        bool rainbow = mode == MpColorMode.Rainbow;
        PnlMpCustomFxColor1.Visibility = rainbow ? Visibility.Collapsed : Visibility.Visible;
        PnlMpCustomFxColor2.Visibility = !rainbow && mode == MpColorMode.Dual
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RbMpCustomFxColorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_mpCustomFxSuppress) return;
        var caps = MpCustomFxCapsFor(CbMpCustomPaintEffect.SelectedIndex);
        if (caps is null) return;
        var p = MpFxParamsFor(caps.Eff);
        p.Mode = RbMpCustomFxRainbow.IsChecked == true ? MpColorMode.Rainbow
               : RbMpCustomFxDual.IsChecked == true ? MpColorMode.Dual
               : MpColorMode.Single;
        UpdateMpCustomFxColorRowVisibility(caps, p.Mode);
    }

    private void RbMpCustomFxDirection_Checked(object sender, RoutedEventArgs e)
    {
        if (_mpCustomFxSuppress) return;
        var caps = MpCustomFxCapsFor(CbMpCustomPaintEffect.SelectedIndex);
        if (caps is null || sender is not RadioButton rb) return;
        int di = (int)rb.Tag;
        if (di >= 0 && di < caps.DirCodes.Length)
            MpFxParamsFor(caps.Eff).Direction = caps.DirCodes[di];
    }

    private void SldMpCustomFxSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMpCustomFxSpeed != null) LblMpCustomFxSpeed.Text = $"{(int)SldMpCustomFxSpeed.Value}%";
        if (_mpCustomFxSuppress) return;
        var caps = MpCustomFxCapsFor(CbMpCustomPaintEffect.SelectedIndex);
        if (caps is null) return;
        MpFxParamsFor(caps.Eff).Speed = (int)SldMpCustomFxSpeed.Value;
    }

    private void BtnMpCustomFxColor_Click(object sender, RoutedEventArgs e)
    {
        var caps = MpCustomFxCapsFor(CbMpCustomPaintEffect.SelectedIndex);
        if (caps is null || sender is not Button { Tag: string tag } btn) return;
        var p = MpFxParamsFor(caps.Eff);
        var current = tag == "1" ? p.Color1 : p.Color2;

        int currentRgb = (current.R << 16) | (current.G << 8) | current.B;
        int? picked = K2.Core.ColorPickerDialog.Pick(this, currentRgb);
        if (picked is not int rgb) return;

        var color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        if (tag == "1") p.Color1 = color; else p.Color2 = color;
        btn.Background = new SolidColorBrush(color);
        if (tag == "1") RetintMpKeysForEffect(caps.Eff); // Color2 never shows in the overlay preview
    }

    private void BtnMpCustomApply_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { Log("[LED ] custom: no device selected"); return; }
        ApplyMpCustomLighting(id, (int)SldMacroBrightness.Value);
        SaveMpCustomColorsToStore();
    }

    private void BtnMpCustomClear_Click(object sender, RoutedEventArgs e)
    {
        _mpCustomKeyColors.Clear();
        _mpCustomKeyEffects.Clear();
        ClearAllMpOverlays();
        SaveMpCustomColorsToStore();
        if (CurrentDeviceId() is int id)
        {
            bool ok = _macroPad.ResetCustomMode((uint)id);
            Log($"[LED ] Custom colors cleared -> ResetCustomMode={ok}");
        }
        else
        {
            Log("[LED ] Custom colors cleared (no device selected, not sent to hardware)");
        }
    }

    private void BtnMpCustomFillAll_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < MacroPadService.ButtonCount; i++)
            PaintMpKey(i);
        Log($"[LED ] Fill All ({CbMpCustomPaintEffect.SelectedItem}) applied to every key");
    }

    /// <summary>
    /// Paints every key whose on-screen bounds intersect <paramref name="rect"/>
    /// (CvsMpRubberBand coordinate space) with the currently selected paint effect —
    /// rectangular multi-select, mirrors Everest Max's PaintLedsInRect. Called from
    /// MpDeviceBox_MouseUp (MainWindow.MacroKeycapAppearance.cs) when paint mode is
    /// active, sharing the same rubber-band drag gesture Settings' "Edit individual
    /// keycaps" batch mode already uses on this device box (mutually exclusive).
    /// </summary>
    private void PaintMpKeysInRect(Rect rect)
    {
        int painted = 0;
        foreach (var (keyIndex, v) in _mpKeyVisuals)
        {
            if (!v.Button.IsVisible) continue;
            var bounds = v.Button.TransformToVisual(CvsMpRubberBand)
                .TransformBounds(new Rect(0, 0, v.Button.ActualWidth, v.Button.ActualHeight));
            if (!rect.IntersectsWith(bounds)) continue;
            PaintMpKey(keyIndex);
            painted++;
        }
        Log($"[LED ] Rubber-band selection painted {painted} key(s) with {CbMpCustomPaintEffect.SelectedItem}");
    }

    // ─────────────────────── Apply (device) ───────────────────────

    /// <summary>
    /// Sends the current in-memory paint state to the device: SwitchToCustomizeEffect (enter
    /// Custom mode) -> ChangeCustomizeStatic (per-key flat colors) -> one ChangeEffect/
    /// ChangeBlockEffect per distinct dynamic effect in use (byAll=1) -> SetCustomizeTable
    /// (per-key effect-index assignment) -> SaveFlash(ALL_PROFILE). Exact sequence verified via
    /// decompile, see MacroPadSdkNative.cs's doc comment. Called by the panel's Apply button and
    /// by ApplyCurrentMacroEffect when the Custom effect is (re)selected/profile reloaded.
    /// </summary>
    private bool ApplyMpCustomLighting(int id, int brightness)
    {
        uint uid = (uint)id;
        _macroPad.EnsureSlotInitialized(uid);

        bool ok = _macroPad.SwitchToCustomizeEffect(uid, brightness);

        var colors = new (byte, byte, byte)[MacroPadSdkNative.FW_NUM_CUSTOM_KEY];
        var table  = new byte[MacroPadSdkNative.FW_NUM_CUSTOM_KEY];
        for (int i = 0; i < colors.Length; i++)
        {
            if (_mpCustomKeyColors.TryGetValue(i, out var c))
                colors[i] = (c.R, c.G, c.B);
            else if (_mpCustomKeyEffects.TryGetValue(i, out var eff))
                table[i] = (byte)eff;
        }
        ok &= _macroPad.SetCustomStaticColors(uid, colors);

        foreach (var eff in _mpCustomKeyEffects.Values.Distinct())
        {
            var p = MpFxParamsFor(eff);
            bool rainbow = p.Mode == MpColorMode.Rainbow;
            (byte, byte, byte)? secondary = !rainbow && p.Mode == MpColorMode.Dual
                ? (p.Color2.R, p.Color2.G, p.Color2.B) : null;
            ok &= _macroPad.ApplyCustomDynamicEffect(
                uid, eff,
                primary: (p.Color1.R, p.Color1.G, p.Color1.B),
                secondary: secondary,
                brightness: brightness,
                randomColor: rainbow,
                speedByte: (byte)p.Speed,
                directionByte: p.Direction);
        }

        ok &= _macroPad.SetCustomEffectTable(uid, table);
        ok &= _macroPad.SaveFlash(uid, 6); // ALL_PROFILE, matches decompiled SetAllEffectInHWforCustom

        Log($"[LED ] ApplyMpCustomLighting id={id} colors={_mpCustomKeyColors.Count} " +
            $"dynamicKeys={_mpCustomKeyEffects.Count} distinctEffects={_mpCustomKeyEffects.Values.Distinct().Count()} -> {ok}");
        return ok;
    }

    // ─────────────────────── Persistence ───────────────────────

    private sealed record MpCustomFxParamsDto(byte Mode, int Direction, int Speed, string Color1, string Color2);

    /// <summary>Saved under the same shared/per-profile prefix as the rest of the LED panel
    /// (<see cref="MacroLedPrefix"/>) — Custom colors follow the same "sync across profiles"
    /// rule as every other MacroPad lighting setting.</summary>
    private void SaveMpCustomColorsToStore()
    {
        string p = MacroLedPrefix();

        var dict = _mpCustomKeyColors.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => $"#{kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}");
        _store.SetSetting(p + "custom.keyColors", JsonSerializer.Serialize(dict));

        var fxDict = _mpCustomKeyEffects.ToDictionary(kvp => kvp.Key.ToString(), kvp => (byte)kvp.Value);
        _store.SetSetting(p + "custom.keyEffects", JsonSerializer.Serialize(fxDict));

        var paramsDict = _mpCustomFxParams.ToDictionary(
            kvp => ((byte)kvp.Key).ToString(),
            kvp => new MpCustomFxParamsDto(
                (byte)kvp.Value.Mode, kvp.Value.Direction, kvp.Value.Speed,
                $"#{kvp.Value.Color1.R:X2}{kvp.Value.Color1.G:X2}{kvp.Value.Color1.B:X2}",
                $"#{kvp.Value.Color2.R:X2}{kvp.Value.Color2.G:X2}{kvp.Value.Color2.B:X2}"));
        _store.SetSetting(p + "custom.fxParams", JsonSerializer.Serialize(paramsDict));
    }

    private void LoadMpCustomColorsFromStore()
    {
        string p = MacroLedPrefix();
        const string gp = "macroled.";

        _mpCustomKeyColors.Clear();
        var json = _store.GetSetting(p + "custom.keyColors") ?? _store.GetSetting(gp + "custom.keyColors");
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                    foreach (var kvp in dict)
                        if (int.TryParse(kvp.Key, out int idx))
                            try { _mpCustomKeyColors[idx] = (Color)ColorConverter.ConvertFromString(kvp.Value); }
                            catch { /* ignore unparsable color */ }
            }
            catch { /* ignore invalid JSON */ }
        }

        _mpCustomKeyEffects.Clear();
        var fxJson = _store.GetSetting(p + "custom.keyEffects") ?? _store.GetSetting(gp + "custom.keyEffects");
        if (!string.IsNullOrWhiteSpace(fxJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, byte>>(fxJson);
                if (dict != null)
                    foreach (var kvp in dict)
                        if (int.TryParse(kvp.Key, out int idx))
                            _mpCustomKeyEffects[idx] = (MacroPadService.Effect)kvp.Value;
            }
            catch { /* ignore invalid JSON */ }
        }

        _mpCustomFxParams.Clear();
        var paramsJson = _store.GetSetting(p + "custom.fxParams") ?? _store.GetSetting(gp + "custom.fxParams");
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, MpCustomFxParamsDto>>(paramsJson);
                if (dict != null)
                    foreach (var kvp in dict)
                    {
                        if (!byte.TryParse(kvp.Key, out byte effByte) || kvp.Value is not { } dto) continue;
                        try
                        {
                            _mpCustomFxParams[(MacroPadService.Effect)effByte] = new MpCustomFxParams
                            {
                                Mode = (MpColorMode)dto.Mode,
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
}
