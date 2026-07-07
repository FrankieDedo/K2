using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using K2.App.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: "LED Lighting" panel for the MacroPad.
///
/// Mirrors the Everest RGB panel (effect/speed/direction combo, 3 color pickers,
/// brightness slider, cross-profile sync, backlight ON/OFF, reset and save-to-flash)
/// but drives the SLOT of the selected MacroPad device (<see cref="CurrentDeviceId"/>):
/// every native MacroPad command takes the slot id as last parameter.
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
        new(MacroPadService.Effect.ReactiveA, "Reactive A"),
        new(MacroPadService.Effect.ReactiveB, "Reactive B"),
        new(MacroPadService.Effect.ReactiveC, "Reactive C"),
        new(MacroPadService.Effect.Yeti,      "Yeti"),
        new(MacroPadService.Effect.Tornado,   "Tornado"),
        new(MacroPadService.Effect.Matrix,    "Matrix"),
        new(MacroPadService.Effect.Off,       "Off"),
    };

    /// <summary>Populates the panel and restores the saved state. Called from constructor.</summary>
    private void InitMacroLedPanel()
    {
        _macroLedSuppress = true;
        try
        {
            CbMacroEffect.ItemsSource       = MacroEffectList;
            CbMacroEffect.DisplayMemberPath = "Label";

            CbMacroSpeed.ItemsSource     = new[] { "Slow", "Normal", "Fast" };
            CbMacroDirection.ItemsSource = new[] { "Clockwise", "Counter-clockwise" };

            CbMacroEffect.SelectedIndex    = 2; // Wave
            CbMacroSpeed.SelectedIndex     = 1;
            CbMacroDirection.SelectedIndex = 0;
            SldMacroBrightness.Value       = 100;

            LoadMacroLedFromStore();

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
        if (IntSetting("macroled.speed")      is int sp && sp is >= 0 and <= 2) CbMacroSpeed.SelectedIndex = sp;
        if (IntSetting("macroled.direction")  is int dr && dr is >= 0 and <= 1) CbMacroDirection.SelectedIndex = dr;
        if (IntSetting("macroled.brightness") is int br && br is >= 0 and <= 100) SldMacroBrightness.Value = br;
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
        _store.SetSetting("macroled.color1",     _macroColor1.ToString());
        _store.SetSetting("macroled.color2",     _macroColor2.ToString());
        _store.SetSetting("macroled.color3",     _macroColor3.ToString());
        _store.SetSetting("macroled.sync",       CkMacroSync.IsChecked == true ? "1" : "0");
    }

    private void CbMacroEffect_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyCurrentMacroEffect();

    private void CbMacroEffectParam_Changed(object sender, SelectionChangedEventArgs e) =>
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
    /// Builds EffData from the panel and sends it to the selected slot.
    /// No-op while the panel is not initialized or no device is present.
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
        var speed  = (MacroPadService.Speed)System.Math.Clamp(CbMacroSpeed.SelectedIndex, 0, 2);
        int bright = (int)SldMacroBrightness.Value;

        // (byte,byte,byte) from 0xRRGGBB.
        static (byte, byte, byte) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        Log($"[LED ] apply id={id} eff={effect} speed={speed} bright={bright} " +
            $"c1=#{_macroColor1:X6} c2=#{_macroColor2:X6} c3=#{_macroColor3:X6}");

        bool ok = _macroPad.SetEffect(
            id:         (uint)id,
            effect:     effect,
            primary:    C(_macroColor1),
            secondary:  C(_macroColor2),
            tertiary:   C(_macroColor3),
            speed:      speed,
            brightness: bright);
        Log($"[LED ] ChangeEffect -> {ok}");
    }
}
