using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using K2.App.Services;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: "LED Lighting" panel for the MacroPad.
///
/// Structurally aligned to the Everest RGB panel (MainWindow.Everest.cs, "RGB
/// lighting panel" region) 2026-07-09: same per-effect capability gating
/// (speed/direction/rainbow enable-disable, direction options depend on the
/// effect), same 5-position speed scale (0/25/50/75/100), same re-send-on-
/// reopen combo behavior. Drives the SLOT of the selected MacroPad device
/// (<see cref="CurrentDeviceId"/>): every native MacroPad command takes the
/// slot id as last parameter.
///
/// Panel state is persisted globally in Settings (keys <c>macroled.*</c>)
/// and reapplied when the driver opens.
/// </summary>
public partial class MainWindow
{
    private bool _macroLedInitialized;
    private bool _macroLedSuppress;
    private int  _macroColor1 = 0x900000; // teal K2
    private int  _macroColor2 = 0x000000;
    private int  _macroColor3 = 0x000000;

    /// <summary>Backlight-off-when-idle timer (device setting, global across
    /// profiles — see BacklightIdleTimer). SetBacklight(false/true) is a real
    /// firmware on/off toggle, so it doesn't disturb the configured effect.</summary>
    private BacklightIdleTimer? _macroAutoOffTimer;

    /// <summary>Effect combo item: (preset, label).</summary>
    private sealed record MacroEffectChoice(MacroPadService.Effect Eff, string Label)
    {
        // See RotationChoice.ToString() in MainWindow.Keys.cs for why this matters.
        public override string ToString() => Label;
    }

    private static readonly MacroEffectChoice[] MacroEffectList =
    {
        new(MacroPadService.Effect.Static,    "Static"),
        new(MacroPadService.Effect.Breath,    "Breath"),
        new(MacroPadService.Effect.Wave,      "Wave"),
        new(MacroPadService.Effect.ReactiveA, "Reactive"),
        new(MacroPadService.Effect.ReactiveC, "Reactive Wave"),
        new(MacroPadService.Effect.Yeti,      "Yeti"),
        new(MacroPadService.Effect.Tornado,   "Tornado"),
        new(MacroPadService.Effect.Matrix,    "Matrix"),
        new(MacroPadService.Effect.Off,       "Off"),
    };

    // ------------------------------------------------------------
    // Per-effect capabilities — mirrors EvCaps/CapsFor in MainWindow.Everest.cs.
    // Wave and Tornado are "block effects" (ChangeBlockEffect, not ChangeEffect):
    // their direction codes come from the decompiled BC
    // MacroPadDLLHelper.getChangeBlockEffect, confirmed byte-identical to the
    // Everest's (Wave 4-way 0/2/4/6, Tornado CW/CCW 9/10 — same firmware family).
    // ------------------------------------------------------------
    private sealed record MacroCaps(
        int MaxColors,        // 1 or 2 color pickers usable
        bool Rainbow,         // supports rainbow colors
        bool Speed,           // supports speed (Static/Off force bySpeed=255 in BC)
        string[] DirLabels,   // direction options (empty = none)
        int[] DirCodes);      // byDirection for each option

    // MaxColors mirrors EvCaps/CapsFor in MainWindow.Everest.cs — same firmware
    // family (Wave/Tornado direction codes already confirmed byte-identical,
    // see the class doc comment above), so effect->color-count is reused as-is.
    private static MacroCaps CapsFor(MacroPadService.Effect e) => e switch
    {
        MacroPadService.Effect.Static    => new(1, false, false, Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Breath    => new(2, true,  true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Wave      => new(2, true,  true,  new[] { "Right", "Down", "Left", "Up" }, new[] { 0, 2, 4, 6 }),
        MacroPadService.Effect.Tornado   => new(1, true,  true,  new[] { "Clockwise", "Counter-CW" }, new[] { 9, 10 }),
        MacroPadService.Effect.ReactiveA => new(2, false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.ReactiveB => new(2, false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.ReactiveC => new(2, false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Yeti      => new(2, false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Matrix    => new(2, false, true,  Array.Empty<string>(), Array.Empty<int>()),
        _                                => new(1, false, false, Array.Empty<string>(), Array.Empty<int>()), // Off
    };

    /// <summary>Direction index restored from settings (applied if valid for the effect).</summary>
    private int _macroSavedDirIndex;

    /// <summary>Backs GridMacroDirection's segmented buttons — mirrors what
    /// CbMacroDirection.SelectedIndex used to provide before the direction
    /// ComboBox became a dynamically-rebuilt RadioButton row (see
    /// SegmentedButtonGroup).</summary>
    private int _macroDirIndex;

    /// <summary>
    /// Wave's 4-way direction (Right/Down/Left/Up, index 0..3 clockwise from
    /// Right — see <see cref="CapsFor"/>'s DirLabels order) is picked by the
    /// user as a SCREEN-relative direction ("make it flow down, as I look at
    /// the mounted pad"), but the firmware byDirection byte is relative to the
    /// device's own native, unrotated frame. <see cref="_rotation"/> (see
    /// MainWindow.Keys.cs — same field the key grid uses) says how many
    /// degrees CLOCKWISE the device is physically mounted, so a vector fixed
    /// to the device appears rotated by that same amount to an outside
    /// observer. To reproduce a desired on-screen direction we must send the
    /// firmware the direction that, after that rotation, lands on it —
    /// i.e. subtract the rotation (in 90° steps) from the visual index.
    /// Tornado's Clockwise/Counter-CW (2-way) has no screen orientation to
    /// correct — a pure rotation never flips rotational handedness — so only
    /// the 4-way set is remapped (detected via codesLength == 4).
    /// </summary>
    private int MacroPhysicalDirIndex(int visualIndex, int codesLength)
    {
        if (codesLength != 4) return visualIndex;
        int steps = (int)_rotation / 90;
        return ((visualIndex - steps) % 4 + 4) % 4;
    }

    /// <summary>
    /// Aligns LED controls to the selected effect's capabilities: enables/
    /// disables speed, direction (with the right options) and rainbow.
    /// Suppresses events to avoid spurious applies while repopulating controls.
    /// </summary>
    private void UpdateMpCapabilities()
    {
        if (CbMacroEffect.SelectedItem is not MacroEffectChoice pick) return;
        var caps = CapsFor(pick.Eff);

        bool prev = _macroLedSuppress;
        _macroLedSuppress = true;
        try
        {
            // Effects that don't support a parameter have the whole label+combo
            // group removed from the layout (Collapsed), not just grayed out —
            // requested by the user after testing: "andrebbe tolta la combo di
            // velocità e direzione per gli effetti che non ce l'hanno".
            PnlMpSpeed.Visibility = caps.Speed ? Visibility.Visible : Visibility.Collapsed;

            if (caps.DirLabels.Length > 0)
            {
                int di = (_macroSavedDirIndex >= 0 && _macroSavedDirIndex < caps.DirLabels.Length) ? _macroSavedDirIndex : 0;
                _macroDirIndex = di;
                SegmentedButtonGroup.Rebuild(GridMacroDirection, "MacroDirection", caps.DirLabels, RbMacroDirection_Checked, di);
                PnlMpDirection.Visibility = Visibility.Visible;
            }
            else
            {
                GridMacroDirection.Children.Clear();
                PnlMpDirection.Visibility = Visibility.Collapsed;
            }

            // Color mode: Single/Double/Rainbow are one mutually-exclusive radio
            // group now (GroupName="MacroColorMode"), so no manual uncheck logic
            // is needed between them — WPF's RadioButton group does that. Rainbow
            // and Double are each only selectable when the effect supports them;
            // falls back to Single otherwise (mirrors the Direction/Speed
            // Collapsed-when-unsupported pattern above).
            RbMacroRainbow.IsEnabled = caps.Rainbow;
            RbMacroRainbow.Visibility = caps.Rainbow ? Visibility.Visible : Visibility.Collapsed;
            if (!caps.Rainbow && RbMacroRainbow.IsChecked == true)
                RbMacroColorSingle.IsChecked = true;

            RbMacroColorDouble.IsEnabled = caps.MaxColors >= 2;
            if (caps.MaxColors < 2 && RbMacroColorDouble.IsChecked == true)
                RbMacroColorSingle.IsChecked = true;

            UpdateMacroColorRowVisibility();
        }
        finally
        {
            _macroLedSuppress = prev;
        }
    }

    /// <summary>Populates the panel and restores the saved state. Called from constructor.</summary>
    private void InitMacroLedPanel()
    {
        _macroAutoOffTimer = new BacklightIdleTimer(Dispatcher, MacroAutoOffTimeout, MacroAutoOffWake);

        _macroLedSuppress = true;
        try
        {
            CbMacroEffect.ItemsSource       = MacroEffectList;
            CbMacroEffect.DisplayMemberPath = "Label";

            CbMacroEffect.SelectedIndex    = 2; // Wave
            SldMacroSpeed.Value             = 50; // wire scale 0/25/50/75/100, same as Everest.
            SldMacroBrightness.Value       = 100;
            RbMacroColorSingle.IsChecked    = true; // default, overridden by LoadMacroLedFromStore if persisted

            LoadMacroLedFromStore();
            UpdateMpCapabilities();

            LblMacroBrightness.Text = $"{(int)SldMacroBrightness.Value}%";
            ApplyColorButton(BtnMacroColor1, _macroColor1);
            ApplyColorButton(BtnMacroColor2, _macroColor2);
        }
        finally
        {
            _macroLedSuppress = false;
        }
        _macroLedInitialized = true;
    }

    /// <summary>Restores the panel from <c>macroled.*</c> keys (global state).</summary>
    private void LoadMacroLedFromStore()
    {
        if (IntSetting("macroled.effect") is int eIdx)
            for (int i = 0; i < MacroEffectList.Length; i++)
                if ((byte)MacroEffectList[i].Eff == eIdx) { CbMacroEffect.SelectedIndex = i; break; }
        if (IntSetting("macroled.speed")      is int sp && sp is >= 0 and <= 100) SldMacroSpeed.Value = sp;
        // Direction is set by UpdateMpCapabilities (depends on effect);
        // here we only restore the saved index, applied later if valid.
        if (IntSetting("macroled.direction")  is int dr && dr >= 0) _macroSavedDirIndex = dr;
        if (IntSetting("macroled.brightness") is int br && br is >= 0 and <= 100) SldMacroBrightness.Value = br;
        // Rainbow/Double are mutually exclusive (one radio group) — Rainbow wins
        // if both were somehow persisted true (shouldn't happen going forward).
        if (IntSetting("macroled.rainbow") is int rb && rb != 0) RbMacroRainbow.IsChecked = true;
        else if (IntSetting("macroled.colorDouble") is int cd && cd != 0) RbMacroColorDouble.IsChecked = true;
        if (IntSetting("macroled.color1")     is int c1) _macroColor1 = c1 & 0xFFFFFF;
        if (IntSetting("macroled.color2")     is int c2) _macroColor2 = c2 & 0xFFFFFF;
        if (IntSetting("macroled.color3")     is int c3) _macroColor3 = c3 & 0xFFFFFF;
        if (IntSetting("macroled.sync")       is int sy) CkMacroSync.IsChecked = sy != 0;

        CkMacroAutoOffEnable.IsChecked = IntSetting("macroled.autoOffEnable") == 1;
        TxtMacroAutoOffSeconds.Text    = (IntSetting("macroled.autoOffSeconds") ?? 60).ToString();
        MacroApplyAutoOffConfig();
    }

    private void MacroApplyAutoOffConfig()
    {
        bool enabled = CkMacroAutoOffEnable.IsChecked == true;
        int  seconds = int.TryParse(TxtMacroAutoOffSeconds.Text, out int s) ? s : 0;
        _macroAutoOffTimer?.Configure(enabled, seconds);
    }

    private void MacroAutoOffTimeout()
    {
        CkMacroBacklight.IsChecked = false;
        if (CurrentDeviceId() is not int id) return;
        Log($"[LED ] auto-off: SetBacklight(false) -> {_macroPad.SetBacklight((uint)id, false)}");
    }

    private void MacroAutoOffWake()
    {
        CkMacroBacklight.IsChecked = true;
        if (CurrentDeviceId() is not int id) return;
        Log($"[LED ] auto-off wake: SetBacklight(true) -> {_macroPad.SetBacklight((uint)id, true)}");
    }

    private void CkMacroAutoOffEnable_Click(object sender, RoutedEventArgs e)
    {
        _store.SetSetting("macroled.autoOffEnable", CkMacroAutoOffEnable.IsChecked == true ? "1" : "0");
        MacroApplyAutoOffConfig();
    }

    private void TxtMacroAutoOffSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtMacroAutoOffSeconds.Text, out int seconds) || seconds < 0)
        {
            seconds = 60;
            TxtMacroAutoOffSeconds.Text = seconds.ToString();
        }
        _store.SetSetting("macroled.autoOffSeconds", seconds.ToString());
        MacroApplyAutoOffConfig();
    }

    /// <summary>Reads a Settings key as integer (null if absent/invalid).</summary>
    private int? IntSetting(string key) =>
        int.TryParse(_store.GetSetting(key), out int v) ? v : null;

    /// <summary>Saves the current panel payload to Settings.</summary>
    private void SaveMacroLedToStore()
    {
        if (!_macroLedInitialized || _macroLedSuppress) return;
        if (CbMacroEffect.SelectedItem is not MacroEffectChoice pick) return;
        _store.SetSetting("macroled.effect",     ((int)(byte)pick.Eff).ToString());
        _store.SetSetting("macroled.speed",      ((int)SldMacroSpeed.Value).ToString());
        _store.SetSetting("macroled.direction",  _macroDirIndex.ToString());
        _store.SetSetting("macroled.brightness", ((int)SldMacroBrightness.Value).ToString());
        _store.SetSetting("macroled.rainbow",    RbMacroRainbow.IsChecked == true ? "1" : "0");
        _store.SetSetting("macroled.colorDouble", RbMacroColorDouble.IsChecked == true ? "1" : "0");
        _store.SetSetting("macroled.color1",     _macroColor1.ToString());
        _store.SetSetting("macroled.color2",     _macroColor2.ToString());
        _store.SetSetting("macroled.color3",     _macroColor3.ToString());
        _store.SetSetting("macroled.sync",       CkMacroSync.IsChecked == true ? "1" : "0");
    }

    // WPF does NOT raise SelectionChanged when re-clicking the already selected item,
    // so the effect would not be re-sent. To allow re-sending on the same item
    // we use DropDownClosed. The flag prevents double-sending when the item
    // actually changes (SelectionChanged already handles it); reset on menu open.
    private bool _macroEffectChangedWhileOpen;

    private void CbMacroEffect_DropDownOpened(object sender, EventArgs e) =>
        _macroEffectChangedWhileOpen = false;

    private void CbMacroEffect_DropDownClosed(object sender, EventArgs e)
    {
        if (_macroEffectChangedWhileOpen) { _macroEffectChangedWhileOpen = false; return; }
        ApplyCurrentMacroEffect(); // same item re-clicked -> resend anyway
    }

    private void CbMacroEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _macroEffectChangedWhileOpen = true;
        UpdateMpCapabilities();   // realign the controls to the new effect
        ApplyCurrentMacroEffect();
    }

    private void SldMacroSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMacroSpeed != null) LblMacroSpeed.Text = $"{(int)SldMacroSpeed.Value}%";
        ApplyCurrentMacroEffect();
    }

    private void RbMacroDirection_Checked(object sender, RoutedEventArgs e)
    {
        _macroDirIndex = (int)((RadioButton)sender).Tag;
        ApplyCurrentMacroEffect();
    }

    /// <summary>Single/Double/Rainbow color mode — one mutually-exclusive radio
    /// group (GroupName="MacroColorMode"), so no manual uncheck logic is needed.</summary>
    private void RbMacroColorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_macroLedSuppress) return;
        UpdateMacroColorRowVisibility();
        ApplyCurrentMacroEffect();
    }

    /// <summary>Swatch rows follow the selected color mode: hidden entirely
    /// under Rainbow (colors are ignored), primary-only under Single, both
    /// under Double.</summary>
    private void UpdateMacroColorRowVisibility()
    {
        bool rainbow = RbMacroRainbow.IsChecked == true;
        PnlMacroColor1.Visibility = rainbow ? Visibility.Collapsed : Visibility.Visible;
        PnlMacroColor2.Visibility = !rainbow && RbMacroColorDouble.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SldMacroBrightness_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMacroBrightness != null) LblMacroBrightness.Text = $"{(int)e.NewValue}%";
        ApplyCurrentMacroEffect();
    }

    private void BtnMacroColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        int current = tag switch { "1" => _macroColor1, "2" => _macroColor2, _ => _macroColor3 };

        // WinForms ColorDialog: the only system color dialog WPF doesn't have.
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen       = true,
            AnyColor       = true,
            SolidColorOnly = true,
            Color          = System.Drawing.Color.FromArgb(
                                 (current >> 16) & 0xFF, (current >> 8) & 0xFF, current & 0xFF),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        int rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
        switch (tag)
        {
            case "1": _macroColor1 = rgb; break;
            case "2": _macroColor2 = rgb; break;
            default:  _macroColor3 = rgb; break;
        }
        ApplyColorButton(btn, rgb);
        ApplyCurrentMacroEffect();
    }

    private void CkMacroSync_Click(object sender, RoutedEventArgs e)
    {
        SaveMacroLedToStore();
        if (CurrentDeviceId() is not int id) { Log("[LED ] sync: no device selected"); return; }
        bool ok = _macroPad.SetSyncAcrossProfiles((uint)id, CkMacroSync.IsChecked == true);
        Log($"[LED ] SetSyncAcrossProfiles({CkMacroSync.IsChecked == true}) -> {ok}");
    }

    private void CkMacroBacklight_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { Log("[LED ] backlight: no device selected"); return; }
        bool on = CkMacroBacklight.IsChecked == true;
        Log($"[LED ] SetBacklight({on}) -> {_macroPad.SetBacklight((uint)id, on)}");
        // Keep the idle timer in sync with a manual toggle — see the matching
        // comment on Everest Max's CkEvBacklight_Click for why this is needed
        // (otherwise re-enabling after an auto-off leaves the timer dead).
        _macroAutoOffTimer?.RegisterActivity();
    }

    /// <summary>
    /// Reads all current panel parameters and sends them to the firmware.
    /// State is also persisted to Settings. No-op while the driver is not open
    /// or the first initialization is not yet complete.
    /// </summary>
    private void ApplyCurrentMacroEffect()
    {
        if (!_macroLedInitialized) return;
        if (_macroLedSuppress) return;
        SaveMacroLedToStore();

        if (CurrentDeviceId() is not int id)
        {
            Log("[LED ] skip: no device selected");
            return;
        }
        if (CbMacroEffect.SelectedItem is not MacroEffectChoice pick) return;

        var effect = pick.Eff;
        var caps   = CapsFor(effect);

        // Speed: slider already snaps to 0/25/50/75/100 (scale 0..100, 0=slow, 100=fast).
        // Static/Off ignore it (EffData.New/BlockData.New force bySpeed=255 for
        // Static; Off doesn't reach either path with a meaningful value).
        byte speedByte = (byte)(caps.Speed ? (int)SldMacroSpeed.Value : 0);

        // Direction: per-effect byte (Wave Right0/Down2/Left4/Up6,
        // Tornado CW9/CCW10). -1 = effect has no direction. Wave's index is
        // first corrected for the device's mounting rotation — see
        // MacroPhysicalDirIndex.
        int dirByte = -1;
        if (caps.DirCodes.Length > 0)
        {
            int idx = Math.Clamp(_macroDirIndex, 0, caps.DirCodes.Length - 1);
            idx = MacroPhysicalDirIndex(idx, caps.DirCodes.Length);
            dirByte = caps.DirCodes[idx];
        }

        bool rainbow   = caps.Rainbow && RbMacroRainbow.IsChecked == true;
        bool useDouble = !rainbow && caps.MaxColors >= 2 && RbMacroColorDouble.IsChecked == true;
        int  bright    = (int)SldMacroBrightness.Value;

        // (byte,byte,byte) from 0xRRGGBB.
        static (byte, byte, byte) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        // No colorCountOverride on this device's SetEffect (unlike Everest's) —
        // in single-color mode, duplicate the primary into the secondary slot
        // so the effect renders as one solid color instead of sending a stale
        // secondary the user never picked for this mode.
        (byte, byte, byte) secondary = useDouble ? C(_macroColor2) : C(_macroColor1);

        Log($"[LED ] apply id={id} eff={effect} speedByte={speedByte} dir={dirByte} rainbow={rainbow} " +
            $"double={useDouble} bright={bright} c1=#{_macroColor1:X6} c2=#{_macroColor2:X6} c3=#{_macroColor3:X6}");

        bool ok = _macroPad.SetEffect(
            id:            (uint)id,
            effect:        effect,
            primary:       C(_macroColor1),
            secondary:     secondary,
            tertiary:      C(_macroColor3),
            brightness:    bright,
            randomColor:   rainbow,
            speedByte:     speedByte,
            directionByte: dirByte,
            profile:       CurrentProfile());
        Log($"[LED ] ChangeEffect -> {ok}");
    }
}
