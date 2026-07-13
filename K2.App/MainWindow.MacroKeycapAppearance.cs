// MainWindow.MacroKeycapAppearance.cs — partial class: MacroPad half of the "keycap
// appearance" feature (PnlMpSecSettings, the MacroPad tab's own Settings sidebar section —
// see MainWindow.SectionNav.cs). Exact mirror of the Everest half in
// MainWindow.KeycapAppearance.cs: same shared types (KeycapColorMode/KeycapStyle/KeyVisual/
// LedOffColor/KeycapStyleChoice(s)) and same shared utility (TryParseHexColor/ParseColorMode/
// ColorModeToString/SetLegendForeground), just a separate persisted setting and cache (this
// is a per-device, not shared, choice) and its own controls/handlers with an "Mp" prefix.
//
// Persisted in MacroPadStore (keys "settings.keycap_*" — same key names as the Everest half,
// but a different store/device, so no collision), loaded/saved from
// LoadMpKeycapAppearanceFromStore, guarded by the new _mpSettingsSuppress flag (MacroPad had
// no unified "Settings" section/suppress-flag before this feature — Rotation and LED lighting
// each have their own, see MainWindow.Keys.cs/_suppressRotationUpdate and
// MainWindow.MacroLed.cs/_macroLedSuppress).
//
// The live per-tick LED color is applied by MainWindow.LedPreview.cs (OnMacroPadColorsUpdated)
// via ApplyMacroPadLedColor/ResetMacroPadKeyToOff below; ApplyMacroKeycapAppearanceToAllKeys
// re-applies the static baseline. MacroPad's key buttons are a fixed array (not rebuilt at
// runtime like Everest's layout-dependent canvas), so — unlike the Everest half — there is no
// "canvas rebuild" case to re-apply after.

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Guards LoadMpKeycapAppearanceFromStore from re-saving/re-applying while it
    /// populates the Settings-section controls (MacroPad's equivalent of _evSettingsSuppress —
    /// see the file header for why this is a new, dedicated flag).</summary>
    private bool _mpSettingsSuppress;

    // In-memory cache of the persisted settings.keycap_* values (read once at load, avoids
    // hitting the SQLite store on every ~100ms LED poll tick).
    private KeycapColorMode _mpKeycapColorMode = KeycapColorMode.Black;
    private string _mpKeycapCustomHex = "#404040";
    private KeycapColorMode _mpKeycapTextColorMode = KeycapColorMode.White;
    private string _mpKeycapTextCustomHex = "#FFFFFF";
    private KeycapStyle _mpKeycapStyleValue = KeycapStyle.Normal;

    /// <summary>"Translucent legends" checkbox — see the Everest Max equivalent
    /// (_evKeycapTranslucentLegend in MainWindow.KeycapAppearance.cs) for the full doc.</summary>
    private bool _mpKeycapTranslucentLegend;

    /// <summary>Per-key color/image overrides (KeyId = physical key index 0..11, same identity
    /// as _mpKeyVisuals) — see the Everest Max equivalent (_evKeycapOverrides in
    /// MainWindow.KeycapAppearance.cs) for the full doc. No Esc key on the MacroPad.</summary>
    private readonly Dictionary<int, KeycapOverrideRecord> _mpKeycapOverrides = new();

    /// <summary>"Edit individual keycaps" checkbox — see the Everest Max equivalent
    /// (_evKeycapEditMode in MainWindow.KeycapAppearance.cs) for the full doc.</summary>
    private bool _mpKeycapEditMode;

    private void CkMpKeycapEditMode_Click(object sender, RoutedEventArgs e) =>
        _mpKeycapEditMode = CkMpKeycapEditMode.IsChecked == true;

    /// <summary>Opens KeycapCustomizeDialog for the given key (KeyId = physical index 0..11) —
    /// see the Everest Max equivalent (OpenEvKeycapCustomizeDialog) for the full doc. The
    /// MacroPad has no Esc key, so isEscKey is always false.</summary>
    private void OpenMpKeycapCustomizeDialog(int keyId, string label)
    {
        _mpKeycapOverrides.TryGetValue(keyId, out var current);
        var dlg = new KeycapCustomizeDialog(label, isEscKey: false, current?.ColorHex, current?.ImagePath) { Owner = this };
        dlg.Changed += () =>
        {
            if (dlg.ColorHex is null && dlg.ImagePath is null)
            {
                _store.ClearKeycapOverride(keyId);
                _mpKeycapOverrides.Remove(keyId);
            }
            else
            {
                _store.SetKeycapOverride(keyId, dlg.ColorHex, dlg.ImagePath);
                _mpKeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
            }
            ApplyMacroKeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    /// <summary>One-time control setup (ItemsSource) + persisted-value load, guarded by
    /// _mpSettingsSuppress. Called once from the constructor.</summary>
    private void InitMpSettingsPanel()
    {
        CbMpKeycapStyle.ItemsSource       = KeycapStyleChoices;
        CbMpKeycapStyle.DisplayMemberPath = "Label";

        _mpSettingsSuppress = true;
        try { LoadMpKeycapAppearanceFromStore(); }
        finally { _mpSettingsSuppress = false; }
    }

    /// <summary>Loads settings.keycap_* from the MacroPad store into the cache fields and the
    /// Settings-section controls.</summary>
    private void LoadMpKeycapAppearanceFromStore()
    {
        _mpKeycapColorMode = ParseColorMode(_store.GetSetting("settings.keycap_color_mode"), KeycapColorMode.Black);
        _mpKeycapCustomHex = _store.GetSetting("settings.keycap_custom_hex") is { Length: > 0 } hex ? hex : "#404040";
        _mpKeycapTextColorMode = ParseColorMode(_store.GetSetting("settings.keycap_text_color_mode"), KeycapColorMode.White);
        _mpKeycapTextCustomHex = _store.GetSetting("settings.keycap_text_custom_hex") is { Length: > 0 } txt ? txt : "#FFFFFF";

        // Migration — see the Everest Max equivalent in LoadKeycapAppearanceFromStore
        // (MainWindow.KeycapAppearance.cs) for the full explanation of the old 4-value scheme.
        int rawStyle = int.TryParse(_store.GetSetting("settings.keycap_style"), out var s) ? s : 0;
        if (_store.GetSetting("settings.keycap_translucent_legend") is not { } translucentRaw)
        {
            _mpKeycapTranslucentLegend = rawStyle == 1; // old Translucent
            _mpKeycapStyleValue = rawStyle switch
            {
                2 => KeycapStyle.Pudding,
                3 => KeycapStyle.ReversePudding,
                _ => KeycapStyle.Normal,
            };
            _store.SetSetting("settings.keycap_style", ((int)_mpKeycapStyleValue).ToString());
            _store.SetSetting("settings.keycap_translucent_legend", _mpKeycapTranslucentLegend ? "1" : "0");
        }
        else
        {
            _mpKeycapTranslucentLegend = translucentRaw == "1";
            _mpKeycapStyleValue = rawStyle is >= 0 and <= 2 ? (KeycapStyle)rawStyle : KeycapStyle.Normal;
        }
        CkMpKeycapTranslucentLegend.IsChecked = _mpKeycapTranslucentLegend;

        _mpKeycapOverrides.Clear();
        foreach (var (keyId, rec) in _store.LoadAllKeycapOverrides())
            _mpKeycapOverrides[keyId] = rec;

        switch (_mpKeycapColorMode)
        {
            case KeycapColorMode.White:  RbMpKeycapWhite.IsChecked  = true; break;
            case KeycapColorMode.Custom: RbMpKeycapCustom.IsChecked = true; break;
            default:                     RbMpKeycapBlack.IsChecked  = true; break;
        }
        BtnMpKeycapCustomColor.IsEnabled = _mpKeycapColorMode == KeycapColorMode.Custom;
        if (TryParseHexColor(_mpKeycapCustomHex, out var custom))
            BtnMpKeycapCustomColor.Background = new SolidColorBrush(custom);

        switch (_mpKeycapTextColorMode)
        {
            case KeycapColorMode.Black:  RbMpKeycapTextBlack.IsChecked  = true; break;
            case KeycapColorMode.Custom: RbMpKeycapTextCustom.IsChecked = true; break;
            default:                     RbMpKeycapTextWhite.IsChecked  = true; break;
        }
        BtnMpKeycapTextColor.IsEnabled = _mpKeycapTextColorMode == KeycapColorMode.Custom;
        if (TryParseHexColor(_mpKeycapTextCustomHex, out var textCustom))
            BtnMpKeycapTextColor.Background = new SolidColorBrush(textCustom);

        int idx = (int)_mpKeycapStyleValue;
        CbMpKeycapStyle.SelectedIndex = idx >= 0 && idx < KeycapStyleChoices.Length ? idx : 0;

        ApplyMacroKeycapAppearanceToAllKeys();
    }

    private void RbMpKeycapColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_mpSettingsSuppress) return;
        _mpKeycapColorMode = sender == RbMpKeycapWhite  ? KeycapColorMode.White
                           : sender == RbMpKeycapCustom ? KeycapColorMode.Custom
                           :                              KeycapColorMode.Black;
        _store.SetSetting("settings.keycap_color_mode", ColorModeToString(_mpKeycapColorMode));
        BtnMpKeycapCustomColor.IsEnabled = _mpKeycapColorMode == KeycapColorMode.Custom;
        ApplyMacroKeycapAppearanceToAllKeys();
    }

    private void RbMpKeycapTextColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_mpSettingsSuppress) return;
        _mpKeycapTextColorMode = sender == RbMpKeycapTextBlack  ? KeycapColorMode.Black
                                : sender == RbMpKeycapTextCustom ? KeycapColorMode.Custom
                                :                                  KeycapColorMode.White;
        _store.SetSetting("settings.keycap_text_color_mode", ColorModeToString(_mpKeycapTextColorMode));
        BtnMpKeycapTextColor.IsEnabled = _mpKeycapTextColorMode == KeycapColorMode.Custom;
        ApplyMacroKeycapAppearanceToAllKeys();
    }

    private void BtnMpKeycapCustomColor_Click(object sender, RoutedEventArgs e)
    {
        TryParseHexColor(_mpKeycapCustomHex, out var current);

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen       = true,
            AnyColor       = true,
            SolidColorOnly = true,
            Color          = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _mpKeycapCustomHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        _store.SetSetting("settings.keycap_custom_hex", _mpKeycapCustomHex);
        BtnMpKeycapCustomColor.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));

        if (RbMpKeycapCustom.IsChecked != true)
            RbMpKeycapCustom.IsChecked = true; // RbMpKeycapColor_Checked above calls ApplyMacroKeycapAppearanceToAllKeys
        else
            ApplyMacroKeycapAppearanceToAllKeys();
    }

    private void BtnMpKeycapTextColor_Click(object sender, RoutedEventArgs e)
    {
        TryParseHexColor(_mpKeycapTextCustomHex, out var current);

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen       = true,
            AnyColor       = true,
            SolidColorOnly = true,
            Color          = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _mpKeycapTextCustomHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        _store.SetSetting("settings.keycap_text_custom_hex", _mpKeycapTextCustomHex);
        BtnMpKeycapTextColor.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));

        if (RbMpKeycapTextCustom.IsChecked != true)
            RbMpKeycapTextCustom.IsChecked = true; // RbMpKeycapTextColor_Checked above calls ApplyMacroKeycapAppearanceToAllKeys
        else
            ApplyMacroKeycapAppearanceToAllKeys();
    }

    private void CbMpKeycapStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mpSettingsSuppress) return;
        if (CbMpKeycapStyle.SelectedItem is not KeycapStyleChoice pick) return;
        _mpKeycapStyleValue = pick.Style;
        _store.SetSetting("settings.keycap_style", ((int)pick.Style).ToString());
        ApplyMacroKeycapAppearanceToAllKeys();
    }

    private void CkMpKeycapTranslucentLegend_Click(object sender, RoutedEventArgs e)
    {
        if (_mpSettingsSuppress) return;
        _mpKeycapTranslucentLegend = CkMpKeycapTranslucentLegend.IsChecked == true;
        _store.SetSetting("settings.keycap_translucent_legend", _mpKeycapTranslucentLegend ? "1" : "0");
        ApplyMacroKeycapAppearanceToAllKeys();
    }

    private Color ResolveMpKeycapColor() => _mpKeycapColorMode switch
    {
        KeycapColorMode.White  => Color.FromRgb(0xE4, 0xE4, 0xE4),
        KeycapColorMode.Custom => TryParseHexColor(_mpKeycapCustomHex, out var c) ? c : Color.FromRgb(0x40, 0x40, 0x40),
        _                      => Color.FromRgb(0x15, 0x15, 0x15),
    };

    private Color ResolveMpKeycapTextColor() => _mpKeycapTextColorMode switch
    {
        KeycapColorMode.Black  => Colors.Black,
        KeycapColorMode.Custom => TryParseHexColor(_mpKeycapTextCustomHex, out var c) ? c : Colors.White,
        _                      => Colors.White,
    };

    /// <summary>
    /// Re-applies the static (non-LED) part of the keycap appearance to every captured
    /// MacroPad key: Background/BorderBrush baseline (Mount follows BorderBrush automatically
    /// via TemplateBinding) and legend color. Call after a settings change.
    /// </summary>
    private void ApplyMacroKeycapAppearanceToAllKeys()
    {
        var defaultKeycapBrush = new SolidColorBrush(ResolveMpKeycapColor());
        var ledOffBrush        = new SolidColorBrush(LedOffColor);
        var textBrush          = new SolidColorBrush(ResolveMpKeycapTextColor());

        foreach (var (keyId, v) in _mpKeyVisuals)
        {
            _mpKeycapOverrides.TryGetValue(keyId, out var ov);
            var keycapBrush = ov?.ColorHex is { Length: > 0 } hex && TryParseHexColor(hex, out var c)
                ? new SolidColorBrush(c)
                : defaultKeycapBrush;

            switch (_mpKeycapStyleValue)
            {
                case KeycapStyle.Pudding:
                    // Center/Background = keycap color (static); border (+ Mount, which mirrors it
                    // via TemplateBinding) gets the live LED color per-tick — this is just the
                    // "LED off" baseline (slightly-gray white).
                    SetKeyBackground(v.Button, keycapBrush);
                    SetKeyBorderBrush(v.Button, ledOffBrush);
                    break;
                case KeycapStyle.ReversePudding:
                    // Border (+ Mount) = the static keycap color; center/Background gets the live
                    // LED color per-tick — this is just the "LED off" baseline.
                    SetKeyBackground(v.Button, ledOffBrush);
                    SetKeyBorderBrush(v.Button, keycapBrush);
                    break;
                default: // Normal — border (+ Mount) = the static keycap color.
                    SetKeyBackground(v.Button, keycapBrush);
                    SetKeyBorderBrush(v.Button, keycapBrush);
                    break;
            }

            v.Halo.Background = Brushes.Transparent;
            SetLegendForeground(v.Button, _mpKeycapTranslucentLegend ? Brushes.White : textBrush);

            _mpOriginalKeyContent.TryGetValue(keyId, out var original);
            ApplyKeycapImageOverride(v.Button, original, ov?.ImagePath);
        }
    }

    /// <summary>Applies one LED-poll tick's live color to a single MacroPad key, routed to the
    /// visual element that matches the current keycap style; independently of style, the
    /// "Translucent legends" checkbox additionally tints the legend with the live color.</summary>
    private void ApplyMacroPadLedColor(KeyVisual v, byte r, byte g, byte b)
    {
        bool lit = r != 0 || g != 0 || b != 0;
        var ledBrush = lit ? new SolidColorBrush(Color.FromRgb(r, g, b)) : null;

        switch (_mpKeycapStyleValue)
        {
            case KeycapStyle.Pudding:
                // Mount mirrors BorderBrush via TemplateBinding — no separate assignment needed.
                SetKeyBorderBrush(v.Button, ledBrush ?? new SolidColorBrush(LedOffColor));
                break;
            case KeycapStyle.ReversePudding:
                SetKeyBackground(v.Button, ledBrush ?? new SolidColorBrush(LedOffColor));
                break;
            default: // Normal — Pudding/ReversePudding already visualize the LED via border/center.
                v.Halo.Background = lit ? new SolidColorBrush(Color.FromArgb(160, r, g, b)) : Brushes.Transparent;
                break;
        }

        if (_mpKeycapTranslucentLegend)
            SetLegendForeground(v.Button, ledBrush ?? Brushes.White);
    }

    /// <summary>Resets a single key to its "LED off" appearance for the current style.</summary>
    private void ResetMacroPadKeyToOff(KeyVisual v) => ApplyMacroPadLedColor(v, 0, 0, 0);
}
