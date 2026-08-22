// MainWindow.MacroKeycapAppearance.cs — partial class: MacroPad half of the "keycap
// appearance" feature (PnlMpSecAppearance, the MacroPad tab's own Appearance sidebar section
// — see MainWindow.SectionNav.cs; Positioning/rotation stayed behind in Settings). Exact
// mirror of the Everest half in MainWindow.KeycapAppearance.cs: same shared types
// (KeycapColorMode/KeycapStyle/KeyVisual/LedOffColor/KeycapStyleChoice(s)) and same shared
// utility (TryParseHexColor/ParseColorMode/ColorModeToString/SetLegendForeground), just a
// separate persisted setting and cache (this is a per-device, not shared, choice) and its own
// controls/handlers with an "Mp" prefix.
//
// Persisted in MacroPadStore (keys "settings.keycap_*" — same key names as the Everest half,
// but a different store/device, so no collision), always the fixed global namespace — NOT
// per-profile (user request 2026-08-22: Appearance settings are disconnected from profiles
// entirely). Loaded/saved from LoadMpKeycapAppearanceFromStore, guarded by the
// _mpSettingsSuppress flag (MacroPad had no unified "Settings" section/suppress-flag before
// this feature — Rotation and LED lighting each have their own, see
// MainWindow.Keys.cs/_suppressRotationUpdate and MainWindow.MacroLed.cs/_macroLedSuppress).
//
// The live per-tick LED color is applied by MainWindow.LedPreview.cs (OnMacroPadColorsUpdated)
// via ApplyMacroPadLedColor/ResetMacroPadKeyToOff below; ApplyMacroKeycapAppearanceToAllKeys
// re-applies the static baseline. MacroPad's key buttons are a fixed array (not rebuilt at
// runtime like Everest's layout-dependent canvas), so — unlike the Everest half — there is no
// "canvas rebuild" case to re-apply after.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

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
            int profile = CurrentProfile();
            if (dlg.ColorHex is null && dlg.ImagePath is null)
            {
                _store.ClearKeycapOverride(profile, keyId);
                _mpKeycapOverrides.Remove(keyId);
            }
            else
            {
                _store.SetKeycapOverride(profile, keyId, dlg.ColorHex, dlg.ImagePath);
                _mpKeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
            }
            ApplyMacroKeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    /// <summary>Opens a single key's dialog unchanged for a 1-key selection (identical to
    /// a plain click); for 2+ keys, opens ONE dialog (blank starting color/image) and
    /// applies whatever the user picks to every key in the selection — mirrors
    /// <see cref="OpenMpKeycapCustomizeDialog"/>'s persistence, just looped. See the
    /// Everest Max equivalent (OpenEvKeycapCustomizeDialogBatch) for the full doc.</summary>
    private void OpenMpKeycapCustomizeDialogBatch(IReadOnlyList<(int KeyId, string Label)> keys)
    {
        if (keys.Count == 0) return;
        if (keys.Count == 1) { OpenMpKeycapCustomizeDialog(keys[0].KeyId, keys[0].Label); return; }

        string label = Loc.Get("settings_keycap_edit_multi_label", keys.Count);
        var dlg = new KeycapCustomizeDialog(label, isEscKey: false, currentColorHex: null, currentImagePath: null) { Owner = this };
        dlg.Changed += () =>
        {
            int profile = CurrentProfile();
            foreach (var (keyId, _) in keys)
            {
                if (dlg.ColorHex is null && dlg.ImagePath is null)
                {
                    _store.ClearKeycapOverride(profile, keyId);
                    _mpKeycapOverrides.Remove(keyId);
                }
                else
                {
                    _store.SetKeycapOverride(profile, keyId, dlg.ColorHex, dlg.ImagePath);
                    _mpKeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
                }
            }
            ApplyMacroKeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    // ─────────────────── Rectangular multi-key selection ───────────────────
    // Drag a rubber-band square over the device box to select multiple keys at
    // once. Two mutually-exclusive uses (only one gate is ever true at a time,
    // same as Everest Max's EvDeviceBox_*/MainWindow.CustomLighting.cs, the
    // pattern this follows): batch keycap editing (Settings' "Edit individual
    // keycaps") — original 2026-07-26 use — and, since the same day's later
    // addition of MacroPad Custom Lighting, painting every key the rectangle
    // touches with the current paint effect (MainWindow.MpCustomLighting.cs's
    // PaintMpKeysInRect). Wired to BdrMpDeviceBox's Preview mouse events
    // (MainWindow.xaml) so the drag can start on top of a key Button; a plain
    // click (below the 5px threshold) falls through to the normal single-key
    // click (KeyButton_Click, MainWindow.Keys.cs).

    private Point _mpRubberStart;
    private bool _mpRubberTracking;
    private bool _mpRubberActive;

    private void MpDeviceBox_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_mpCustomPaintMode && !(_mpKeycapEditMode && IsMpAppearanceSectionActive)) return;
        _mpRubberStart = e.GetPosition(CvsMpRubberBand);
        _mpRubberTracking = true;
        _mpRubberActive = false;
    }

    private void MpDeviceBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_mpRubberTracking) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            CancelMpRubberBand();
            return;
        }
        var p = e.GetPosition(CvsMpRubberBand);
        if (!_mpRubberActive)
        {
            if (Math.Abs(p.X - _mpRubberStart.X) < 5 && Math.Abs(p.Y - _mpRubberStart.Y) < 5) return;
            _mpRubberActive = true;
            RectMpRubberBand.Visibility = Visibility.Visible;
            BdrMpDeviceBox.CaptureMouse();
        }
        var r = new Rect(_mpRubberStart, p);
        Canvas.SetLeft(RectMpRubberBand, r.X);
        Canvas.SetTop(RectMpRubberBand, r.Y);
        RectMpRubberBand.Width  = r.Width;
        RectMpRubberBand.Height = r.Height;
    }

    private void MpDeviceBox_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_mpRubberTracking) return;
        bool wasActive = _mpRubberActive;
        var rect = wasActive ? new Rect(_mpRubberStart, e.GetPosition(CvsMpRubberBand)) : Rect.Empty;
        CancelMpRubberBand();
        if (!wasActive) return;
        e.Handled = true;
        if (_mpCustomPaintMode)
            PaintMpKeysInRect(rect);
        else if (_mpKeycapEditMode && IsMpAppearanceSectionActive)
            MpOpenKeycapDialogForRect(rect);
    }

    private void CancelMpRubberBand()
    {
        _mpRubberTracking = false;
        _mpRubberActive = false;
        RectMpRubberBand.Visibility = Visibility.Collapsed;
        if (BdrMpDeviceBox.IsMouseCaptured) BdrMpDeviceBox.ReleaseMouseCapture();
    }

    /// <summary>Collects every key Button whose on-screen bounds intersect
    /// <paramref name="rect"/> (CvsMpRubberBand coordinate space) and opens ONE batch
    /// KeycapCustomizeDialog for them. Iterates _mpKeyVisuals directly, same KeyId
    /// space KeyButton_Click (MainWindow.Keys.cs) already uses for a single click.</summary>
    private void MpOpenKeycapDialogForRect(Rect rect)
    {
        var matches = new List<(int KeyId, string Label)>();
        foreach (var (keyId, v) in _mpKeyVisuals)
        {
            if (!v.Button.IsVisible) continue;
            var bounds = v.Button.TransformToVisual(CvsMpRubberBand)
                .TransformBounds(new Rect(0, 0, v.Button.ActualWidth, v.Button.ActualHeight));
            if (!rect.IntersectsWith(bounds)) continue;
            matches.Add((keyId, (v.Button.Content as TextBlock)?.Text ?? $"#{keyId}"));
        }
        OpenMpKeycapCustomizeDialogBatch(matches);
    }

    /// <summary>One-time control setup (ItemsSource) + persisted-value load, guarded by
    /// _mpSettingsSuppress. Called once from the constructor, and again on every profile
    /// switch (see ReloadCurrentProfile, MainWindow.Keys.cs).</summary>
    private void InitMpSettingsPanel()
    {
        CbMpKeycapStyle.ItemsSource       = KeycapStyleChoices;
        CbMpKeycapStyle.DisplayMemberPath = "Label";

        _mpSettingsSuppress = true;
        try { LoadMpKeycapAppearanceFromStore(); }
        finally { _mpSettingsSuppress = false; }
    }

    /// <summary>Loads settings.keycap_* from the MacroPad store into the cache fields and the
    /// Appearance-section controls. Always the fixed global "settings.keycap_*" namespace —
    /// Keycap Appearance is a cosmetic, device-wide preference, not per-profile (user request
    /// 2026-08-22: split into its own Appearance section, disconnected from "sync across
    /// profiles").</summary>
    private void LoadMpKeycapAppearanceFromStore()
    {
        string? Get(string key) => _store.GetSetting("settings." + key);

        _mpKeycapColorMode = ParseColorMode(Get("keycap_color_mode"), KeycapColorMode.Black);
        _mpKeycapCustomHex = Get("keycap_custom_hex") is { Length: > 0 } hex ? hex : "#404040";
        _mpKeycapTextColorMode = ParseColorMode(Get("keycap_text_color_mode"), KeycapColorMode.White);
        _mpKeycapTextCustomHex = Get("keycap_text_custom_hex") is { Length: > 0 } txt ? txt : "#FFFFFF";

        // Migration — see the Everest Max equivalent in LoadKeycapAppearanceFromStore
        // (MainWindow.KeycapAppearance.cs) for the full explanation of the old 4-value scheme.
        int rawStyle = int.TryParse(Get("keycap_style"), out var s) ? s : 0;
        if (Get("keycap_translucent_legend") is not { } translucentRaw)
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
        foreach (var (keyId, rec) in _store.LoadAllKeycapOverrides(CurrentProfile()))
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
        foreach (int keyId in _mpKeyVisuals.Keys)
        {
            // A key currently mid-physical-press has its IsHighlighted style trigger
            // (MacroKeyStyle) active, which outranks whatever Background/BorderBrush we'd
            // write here via SetCurrentValue — but the value we DO write still becomes the
            // new baseline the trigger reverts to on release, same hazard
            // OnMacroPadColorsUpdated already guards against for the LED-color path (see
            // UpdateMpLedPreviewActive's doc comment). Left unguarded here, a settings
            // change/profile switch landing mid-press (e.g. a macro key bound to "switch
            // profile") silently rewrote that baseline to the new profile's default
            // color, which only became visible once released — reading as the key
            // "turning gray and staying gray" after a tap (user report 2026-07-27).
            // Skipped keys are caught up the instant they're released, see
            // ApplyMacroKeycapAppearanceToKey's call in HandleKeyEvent (MainWindow.Keys.cs).
            if (keyId < _keys.Length && _keys[keyId].IsHighlighted) continue;
            ApplyMacroKeycapAppearanceToKey(keyId);
        }
    }

    /// <summary>Applies the static (non-LED) keycap appearance to a single MacroPad key —
    /// see <see cref="ApplyMacroKeycapAppearanceToAllKeys"/> for the full doc, this is just
    /// its per-key body pulled out so a key can be refreshed on its own right after a
    /// physical release, bypassing the IsHighlighted skip above (the key is no longer
    /// highlighted by the time this runs, so there's nothing left to protect).</summary>
    private void ApplyMacroKeycapAppearanceToKey(int keyId)
    {
        if (!_mpKeyVisuals.TryGetValue(keyId, out var v)) return;

        var defaultKeycapBrush = new SolidColorBrush(ResolveMpKeycapColor());
        var ledOffBrush        = new SolidColorBrush(LedOffColor);
        var textBrush          = new SolidColorBrush(ResolveMpKeycapTextColor());

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
