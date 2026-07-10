using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using K2.App.Services;

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
        bool Rainbow,         // supports rainbow colors
        bool Speed,           // supports speed (Static/Off force bySpeed=255 in BC)
        string[] DirLabels,   // direction options (empty = none)
        int[] DirCodes);      // byDirection for each option

    private static MacroCaps CapsFor(MacroPadService.Effect e) => e switch
    {
        MacroPadService.Effect.Static    => new(false, false, Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Breath    => new(true,  true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Wave      => new(true,  true,  new[] { "Right", "Down", "Left", "Up" }, new[] { 0, 2, 4, 6 }),
        MacroPadService.Effect.Tornado   => new(true,  true,  new[] { "Clockwise", "Counter-CW" }, new[] { 9, 10 }),
        MacroPadService.Effect.ReactiveA => new(false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.ReactiveB => new(false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.ReactiveC => new(false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Yeti      => new(false, true,  Array.Empty<string>(), Array.Empty<int>()),
        MacroPadService.Effect.Matrix    => new(false, true,  Array.Empty<string>(), Array.Empty<int>()),
        _                                => new(false, false, Array.Empty<string>(), Array.Empty<int>()), // Off
    };

    /// <summary>Direction index restored from settings (applied if valid for the effect).</summary>
    private int _macroSavedDirIndex;

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
            CbMacroSpeed.IsEnabled = caps.Speed;

            if (caps.DirLabels.Length > 0)
            {
                CbMacroDirection.ItemsSource = caps.DirLabels;
                int di = (_macroSavedDirIndex >= 0 && _macroSavedDirIndex < caps.DirLabels.Length) ? _macroSavedDirIndex : 0;
                CbMacroDirection.SelectedIndex = di;
                CbMacroDirection.IsEnabled = true;
                PnlMpDirection.Visibility = Visibility.Visible;
            }
            else
            {
                CbMacroDirection.ItemsSource = null;
                CbMacroDirection.IsEnabled = false;
                PnlMpDirection.Visibility = Visibility.Collapsed;
            }

            CkMacroRainbow.IsEnabled = caps.Rainbow;
            CkMacroRainbow.Visibility = caps.Rainbow ? Visibility.Visible : Visibility.Collapsed;
            if (!caps.Rainbow) CkMacroRainbow.IsChecked = false;
        }
        finally
        {
            _macroLedSuppress = prev;
        }
    }

    /// <summary>Populates the panel and restores the saved state. Called from constructor.</summary>
    private void InitMacroLedPanel()
    {
        _macroLedSuppress = true;
        try
        {
            CbMacroEffect.ItemsSource       = MacroEffectList;
            CbMacroEffect.DisplayMemberPath = "Label";

            // 5 positions -> wire scale 0/25/50/75/100, same as Everest.
            CbMacroSpeed.ItemsSource     = new[] { "1 — slow", "2", "3", "4", "5 — fast" };

            CbMacroEffect.SelectedIndex    = 2; // Wave
            CbMacroSpeed.SelectedIndex     = 2; // middle
            SldMacroBrightness.Value       = 100;

            LoadMacroLedFromStore();
            UpdateMpCapabilities();

            LblMacroBrightness.Text = $"{(int)SldMacroBrightness.Value}%";
            ApplyColorButton(BtnMacroColor1, _macroColor1);
            ApplyColorButton(BtnMacroColor2, _macroColor2);
            ApplyColorButton(BtnMacroColor3, _macroColor3);
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
        if (IntSetting("macroled.speed")      is int sp && sp is >= 0 and <= 4) CbMacroSpeed.SelectedIndex = sp;
        // Direction is set by UpdateMpCapabilities (depends on effect);
        // here we only restore the saved index, applied later if valid.
        if (IntSetting("macroled.direction")  is int dr && dr >= 0) _macroSavedDirIndex = dr;
        if (IntSetting("macroled.brightness") is int br && br is >= 0 and <= 100) SldMacroBrightness.Value = br;
        if (IntSetting("macroled.rainbow")    is int rb) CkMacroRainbow.IsChecked = rb != 0;
        if (IntSetting("macroled.color1")     is int c1) _macroColor1 = c1 & 0xFFFFFF;
        if (IntSetting("macroled.color2")     is int c2) _macroColor2 = c2 & 0xFFFFFF;
        if (IntSetting("macroled.color3")     is int c3) _macroColor3 = c3 & 0xFFFFFF;
        if (IntSetting("macroled.sync")       is int sy) CkMacroSync.IsChecked = sy != 0;
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
        _store.SetSetting("macroled.speed",      CbMacroSpeed.SelectedIndex.ToString());
        _store.SetSetting("macroled.direction",  CbMacroDirection.SelectedIndex.ToString());
        _store.SetSetting("macroled.brightness", ((int)SldMacroBrightness.Value).ToString());
        _store.SetSetting("macroled.rainbow",    CkMacroRainbow.IsChecked == true ? "1" : "0");
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

    private void CbMacroEffectParam_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplyCurrentMacroEffect();

    private void CkMacroRainbow_Click(object sender, RoutedEventArgs e) =>
        ApplyCurrentMacroEffect();

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

    private void BtnMacroLightOn_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { Log("[LED ] backlight: no device selected"); return; }
        Log($"[LED ] SetBacklight(true) -> {_macroPad.SetBacklight((uint)id, true)}");
    }

    private void BtnMacroLightOff_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { Log("[LED ] backlight: no device selected"); return; }
        Log($"[LED ] SetBacklight(false) -> {_macroPad.SetBacklight((uint)id, false)}");
    }

    private void BtnMacroLightReset_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { Log("[LED ] reset: no device selected"); return; }
        Log($"[LED ] ResetEffects -> {_macroPad.ResetEffects((uint)id)}");
    }

    private void BtnMacroLightSave_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { Log("[LED ] save: no device selected"); return; }
        Log($"[LED ] SaveFlash(ALL) -> {_macroPad.SaveFlash((uint)id)}");
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

        // Speed: 5 UI positions -> scale 0..100 (pos0=0 slow … pos4=100 fast).
        // Static/Off ignore it (EffData.New/BlockData.New force bySpeed=255 for
        // Static; Off doesn't reach either path with a meaningful value).
        byte speedByte = (byte)(caps.Speed ? Math.Clamp(CbMacroSpeed.SelectedIndex, 0, 4) * 25 : 0);

        // Direction: per-effect byte (Wave Right0/Down2/Left4/Up6,
        // Tornado CW9/CCW10). -1 = effect has no direction.
        int dirByte = -1;
        if (caps.DirCodes.Length > 0 && CbMacroDirection.SelectedIndex >= 0)
            dirByte = caps.DirCodes[Math.Clamp(CbMacroDirection.SelectedIndex, 0, caps.DirCodes.Length - 1)];

        bool rainbow = caps.Rainbow && CkMacroRainbow.IsChecked == true;
        int  bright  = (int)SldMacroBrightness.Value;

        // (byte,byte,byte) from 0xRRGGBB.
        static (byte, byte, byte) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        Log($"[LED ] apply id={id} eff={effect} speedByte={speedByte} dir={dirByte} rainbow={rainbow} " +
            $"bright={bright} c1=#{_macroColor1:X6} c2=#{_macroColor2:X6} c3=#{_macroColor3:X6}");

        bool ok = _macroPad.SetEffect(
            id:            (uint)id,
            effect:        effect,
            primary:       C(_macroColor1),
            secondary:     C(_macroColor2),
            tertiary:      C(_macroColor3),
            brightness:    bright,
            randomColor:   rainbow,
            speedByte:     speedByte,
            directionByte: dirByte,
            profile:       CurrentProfile());
        Log($"[LED ] ChangeEffect -> {ok}");
    }
}
