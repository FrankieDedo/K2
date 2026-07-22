// MainWindow.KeycapAppearance.cs — partial class: Everest half of the "keycap appearance"
// feature, in the Everest tab's own Settings section (PnlSecSettings, alongside Game Mode /
// Indicator LED / Keyboard color — see MainWindow.Everest.cs). The MacroPad mirror of this
// same feature (identical UX, same shared types below) lives in
// MainWindow.MacroKeycapAppearance.cs / PnlMpSecSettings.
//
// Purely cosmetic: it only changes how the on-screen keyboard preview renders in K2.App. It
// never touches the physical keycaps (fixed at manufacture) or anything sent to the device —
// the real per-key RGB effect is the existing "Illuminazione RGB"/"Illuminazione LED" panels.
//
// Persisted per-device (EverestStore here, MacroPadStore for the MacroPad half — keys
// "settings.keycap_*" in both, same store/pattern as the sibling settings.game_mode/
// settings.keyboard_color) — loaded/saved from LoadKeycapAppearanceFromStore, guarded by
// the shared _evSettingsSuppress flag.
//
// Independent choices (KeycapColorMode/KeycapStyle below, shared by both devices):
//   - keycap color: Black / White / Custom (a picked RGB), default Black — the key's base/fill
//     color, and (Normal/ReversePudding) also the border + bottom Mount strip color.
//   - text color: Black / White / Custom (a picked RGB), default White — used for the legend
//     unless the "Translucent legends" checkbox is on, in which case the legend is always
//     dynamically tinted with the live LED color instead (independent of style, see
//     _evKeycapTranslucentLegend below).
//   - keycap style: how the live LED color combines with the keycap color
//     (Normal = halo glow around the key, border + Mount strip = the static keycap color;
//     Pudding = border + Mount follow the live LED color, center = keycap color;
//     Reverse Pudding = border + Mount are the static keycap color, center follows the live LED color)
//
// The live per-tick LED color is applied by MainWindow.LedPreview.cs via
// ApplyEverestLedColor/ResetEverestKeyToOff below; ApplyKeycapAppearanceToAllKeys
// re-applies the static baseline (and must run after any rebuild of the keyboard
// canvas, since new Button instances start from the EverestKeyStyle default).
//
// The bottom "Mount" strip always mirrors Button.BorderBrush — in EverestKeyStyle (and
// KeyCapStyle, MacroPad's equivalent) its Background is a TemplateBinding to BorderBrush
// (MainWindow.xaml), not something this file ever sets directly. That keeps it eligible for
// the template's Hover trigger (a value set directly from code on Mount would permanently
// outrank that trigger).

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>Base color of the on-screen keycap (Everest and MacroPad — cosmetic preview
/// only, does not affect the physical keyboard's plastic).</summary>
internal enum KeycapColorMode
{
    Black = 0,
    White = 1,
    Custom = 2,
}

/// <summary>
/// How the live LED color is combined with the keycap color in the on-screen keyboard
/// preview (Everest and MacroPad):
///   Normal         — keycap solid color (incl. border + bottom Mount strip), LED shown
///                    as a glow/halo around the key.
///   Pudding        — border and bottom Mount strip colored like the LED, center = keycap color.
///   ReversePudding — border and bottom Mount strip = keycap color, center = LED color.
/// Independent of the style, the "Translucent legends" checkbox (see
/// _evKeycapTranslucentLegend below) makes the legend track the live LED color instead of the
/// static configured text color — orthogonal to which of the 3 styles above is active. Before
/// 2026-07-13 this was a 4th style value ("Translucent" = Normal + legend tint bundled
/// together); it's now a checkbox so it can be combined with Pudding/ReversePudding too.
/// </summary>
internal enum KeycapStyle
{
    Normal = 0,
    Pudding = 1,
    ReversePudding = 2,
}

public partial class MainWindow
{
    /// <summary>Button + LedHalo border captured per key (Everest: ledIndex-keyed; MacroPad:
    /// button-index-keyed), built in MainWindow.LedPreview.cs. The bottom "Mount" strip is NOT
    /// captured here: in EverestKeyStyle/KeyCapStyle its Background is bound to the Button's own
    /// BorderBrush ({TemplateBinding BorderBrush}), so it always mirrors BorderBrush automatically
    /// and — unlike a value set directly from code — stays overridable by the template's Hover
    /// trigger (see MainWindow.xaml).</summary>
    private readonly record struct KeyVisual(Button Button, Border Halo);

    /// <summary>Slightly-gray white used as the "LED off" accent color: Pudding's border +
    /// Mount strip, and Reverse Pudding's center, when no LED effect is lighting the key.</summary>
    private static readonly Color LedOffColor = Color.FromRgb(0x77, 0x77, 0x77);

    /// <summary>LED index of the Esc key — identical on both Everest Max (LedMatrixMapping.
    /// EverestKeyboard maps VK 27 → 0) and Everest 60 (Everest60KeyboardLayout.MainBoard's first
    /// key). Used by KeycapCustomizeDialog to decide whether to offer the "Use Mountain logo"
    /// checkbox. The MacroPad has no Esc key, so this never applies there.</summary>
    internal const int EscKeyId = 0;

    /// <summary>Sentinel ImagePath value (stored in KeycapOverrides.ImagePath) meaning "the
    /// bundled Mountain logo asset", not a real file on disk — see LoadKeycapOverrideImage.</summary>
    internal const string MountainLogoImagePath = "::mountain-logo::";

    /// <summary>Loads a per-key custom image override — either the bundled Mountain logo asset
    /// (Esc-only sentinel) or a user-picked file (already cropped/cached on disk by
    /// ImageCropDialog/CropEditor, same pipeline as the numpad display keys). Returns null if the
    /// file no longer exists (e.g. the cropped-image cache was cleared). Frozen so it's safe to
    /// share/reuse across threads and cheap to reassign on every appearance refresh.
    /// Internal (not private): also used by KeycapCustomizeDialog's own image preview.</summary>
    internal static BitmapImage? LoadKeycapOverrideImage(string imagePath)
    {
        try
        {
            var uri = imagePath == MountainLogoImagePath
                ? new Uri("pack://application:,,,/K2.App;component/Assets/mountain_logo.png")
                : new Uri(imagePath, UriKind.Absolute);
            if (uri.IsFile && !System.IO.File.Exists(uri.LocalPath)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Swaps a key's Content between its cached original legend and a custom image
    /// override, shared by all 3 devices (Everest Max, Everest 60, MacroPad). Safe to call every
    /// appearance refresh: a no-op re-swap when nothing changed. Background/BorderBrush styling
    /// (Pudding/Reverse Pudding border tint, halo, etc.) is untouched — it still applies to the
    /// Button underneath the image.</summary>
    private static void ApplyKeycapImageOverride(Button btn, FrameworkElement? originalContent, string? imagePath)
    {
        if (imagePath is { Length: > 0 } && LoadKeycapOverrideImage(imagePath) is { } bmp)
        {
            btn.Content = new Image { Source = bmp, Stretch = Stretch.Uniform };
        }
        else if (originalContent != null && !ReferenceEquals(btn.Content, originalContent))
        {
            btn.Content = originalContent;
        }
    }

    // In-memory cache of the persisted settings.keycap_* values (read once at load,
    // avoids hitting the SQLite store on every ~100ms LED poll tick).
    private KeycapColorMode _evKeycapColorMode = KeycapColorMode.Black;
    private string _evKeycapCustomHex = "#404040";
    private KeycapColorMode _evKeycapTextColorMode = KeycapColorMode.White;
    private string _evKeycapTextCustomHex = "#FFFFFF";
    private KeycapStyle _evKeycapStyleValue = KeycapStyle.Normal;

    /// <summary>"Translucent legends" checkbox (2026-07-13, replaces the old 4th
    /// KeycapStyle.Translucent value): independent of style, makes the legend track the live
    /// LED color instead of the static configured text color. See LoadKeycapAppearanceFromStore
    /// for the one-time migration from the old combined style value.</summary>
    private bool _evKeycapTranslucentLegend;

    /// <summary>Per-key color/image overrides (KeyId = LED index, same identity as
    /// _evKeyVisuals), loaded once from _evStore alongside the rest of Keycap Appearance and
    /// refreshed after every KeycapCustomizeDialog edit. See MainWindow.Everest.cs's keyboard
    /// click handler for how the dialog is opened (Edit-individual-keycaps mode).</summary>
    private readonly Dictionary<int, KeycapOverrideRecord> _evKeycapOverrides = new();

    /// <summary>"Edit individual keycaps" checkbox — transient UI mode (not persisted), reset to
    /// off on every app start. While checked and the Settings section is active, clicking a key
    /// opens KeycapCustomizeDialog instead of that section's normal (no-op) click behavior — see
    /// EvKeyboardButton_Click in MainWindow.Everest.cs.</summary>
    private bool _evKeycapEditMode;

    private void CkEvKeycapEditMode_Click(object sender, RoutedEventArgs e) =>
        _evKeycapEditMode = CkEvKeycapEditMode.IsChecked == true;

    /// <summary>Opens KeycapCustomizeDialog for the given key (KeyId = LED index) and persists/
    /// re-renders live on every change — called from EvKeyboardButton_Click when edit mode is
    /// active. Shared logic (dialog wiring identical across all 3 devices) factored here since
    /// only the store/apply-function/label differ.</summary>
    private void OpenEvKeycapCustomizeDialog(int keyId, string label)
    {
        _evKeycapOverrides.TryGetValue(keyId, out var current);
        var dlg = new KeycapCustomizeDialog(label, keyId == EscKeyId, current?.ColorHex, current?.ImagePath) { Owner = this };
        dlg.Changed += () =>
        {
            if (dlg.ColorHex is null && dlg.ImagePath is null)
            {
                _evStore.ClearKeycapOverride(keyId);
                _evKeycapOverrides.Remove(keyId);
            }
            else
            {
                _evStore.SetKeycapOverride(keyId, dlg.ColorHex, dlg.ImagePath);
                _evKeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
            }
            ApplyKeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    private sealed record KeycapStyleChoice(KeycapStyle Style, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly KeycapStyleChoice[] KeycapStyleChoices =
    {
        new(KeycapStyle.Normal,         Loc.Get("settings_keycap_style_normal")),
        new(KeycapStyle.Pudding,        Loc.Get("settings_keycap_style_pudding")),
        new(KeycapStyle.ReversePudding, Loc.Get("settings_keycap_style_reverse_pudding")),
    };

    /// <summary>One-time control setup (ItemsSource). Called from InitEverestSettingsPanel,
    /// before LoadEverestSettingsFromStore populates the values.</summary>
    private void InitKeycapAppearanceControls()
    {
        CbEvKeycapStyle.ItemsSource       = KeycapStyleChoices;
        CbEvKeycapStyle.DisplayMemberPath = "Label";
    }

    /// <summary>Loads settings.keycap_* from the Everest store into the cache fields and the
    /// Settings-section controls. Called from LoadEverestSettingsFromStore (MainWindow.Everest.cs),
    /// guarded by the shared _evSettingsSuppress flag so this doesn't re-save while loading.</summary>
    private void LoadKeycapAppearanceFromStore()
    {
        _evKeycapColorMode = ParseColorMode(_evStore.GetSetting("settings.keycap_color_mode"), KeycapColorMode.Black);
        _evKeycapCustomHex = _evStore.GetSetting("settings.keycap_custom_hex") is { Length: > 0 } hex ? hex : "#404040";
        _evKeycapTextColorMode = ParseColorMode(_evStore.GetSetting("settings.keycap_text_color_mode"), KeycapColorMode.White);
        _evKeycapTextCustomHex = _evStore.GetSetting("settings.keycap_text_custom_hex") is { Length: > 0 } txt ? txt : "#FFFFFF";

        // Migration (2026-07-13): the old KeycapStyle had 4 values (Normal/Translucent/Pudding/
        // ReversePudding = 0/1/2/3); Translucent is now the independent checkbox below and
        // Pudding/ReversePudding shifted down to 1/2. "settings.keycap_translucent_legend" never
        // existing yet is the marker that settings.keycap_style (if present) is still in the old
        // scheme — migrate once, then persist both in the new scheme so this never re-runs.
        int rawStyle = int.TryParse(_evStore.GetSetting("settings.keycap_style"), out var s) ? s : 0;
        if (_evStore.GetSetting("settings.keycap_translucent_legend") is not { } translucentRaw)
        {
            _evKeycapTranslucentLegend = rawStyle == 1; // old Translucent
            _evKeycapStyleValue = rawStyle switch
            {
                2 => KeycapStyle.Pudding,
                3 => KeycapStyle.ReversePudding,
                _ => KeycapStyle.Normal, // covers old Normal (0) and old Translucent (1)
            };
            _evStore.SetSetting("settings.keycap_style", ((int)_evKeycapStyleValue).ToString());
            _evStore.SetSetting("settings.keycap_translucent_legend", _evKeycapTranslucentLegend ? "1" : "0");
        }
        else
        {
            _evKeycapTranslucentLegend = translucentRaw == "1";
            _evKeycapStyleValue = rawStyle is >= 0 and <= 2 ? (KeycapStyle)rawStyle : KeycapStyle.Normal;
        }
        CkEvKeycapTranslucentLegend.IsChecked = _evKeycapTranslucentLegend;

        _evKeycapOverrides.Clear();
        foreach (var (keyId, rec) in _evStore.LoadAllKeycapOverrides())
            _evKeycapOverrides[keyId] = rec;

        switch (_evKeycapColorMode)
        {
            case KeycapColorMode.White:  RbEvKeycapWhite.IsChecked  = true; break;
            case KeycapColorMode.Custom: RbEvKeycapCustom.IsChecked = true; break;
            default:                             RbEvKeycapBlack.IsChecked  = true; break;
        }
        BtnEvKeycapCustomColor.IsEnabled = _evKeycapColorMode == KeycapColorMode.Custom;
        if (TryParseHexColor(_evKeycapCustomHex, out var custom))
            BtnEvKeycapCustomColor.Background = new SolidColorBrush(custom);

        switch (_evKeycapTextColorMode)
        {
            case KeycapColorMode.Black:  RbEvKeycapTextBlack.IsChecked  = true; break;
            case KeycapColorMode.Custom: RbEvKeycapTextCustom.IsChecked = true; break;
            default:                             RbEvKeycapTextWhite.IsChecked  = true; break;
        }
        BtnEvKeycapTextColor.IsEnabled = _evKeycapTextColorMode == KeycapColorMode.Custom;
        if (TryParseHexColor(_evKeycapTextCustomHex, out var textCustom))
            BtnEvKeycapTextColor.Background = new SolidColorBrush(textCustom);

        int idx = (int)_evKeycapStyleValue;
        CbEvKeycapStyle.SelectedIndex = idx >= 0 && idx < KeycapStyleChoices.Length ? idx : 0;

        ApplyKeycapAppearanceToAllKeys();
    }

    private static KeycapColorMode ParseColorMode(string? stored, KeycapColorMode fallback) => stored switch
    {
        "black"  => KeycapColorMode.Black,
        "white"  => KeycapColorMode.White,
        "custom" => KeycapColorMode.Custom,
        _        => fallback,
    };

    private static string ColorModeToString(KeycapColorMode mode) => mode switch
    {
        KeycapColorMode.Black  => "black",
        KeycapColorMode.White  => "white",
        KeycapColorMode.Custom => "custom",
        _                              => "black",
    };

    private void RbEvKeycapColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        _evKeycapColorMode = sender == RbEvKeycapWhite  ? KeycapColorMode.White
                           : sender == RbEvKeycapCustom ? KeycapColorMode.Custom
                           :                              KeycapColorMode.Black;
        _evStore.SetSetting("settings.keycap_color_mode", ColorModeToString(_evKeycapColorMode));
        BtnEvKeycapCustomColor.IsEnabled = _evKeycapColorMode == KeycapColorMode.Custom;
        ApplyKeycapAppearanceToAllKeys();
    }

    private void RbEvKeycapTextColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        _evKeycapTextColorMode = sender == RbEvKeycapTextBlack  ? KeycapColorMode.Black
                                : sender == RbEvKeycapTextCustom ? KeycapColorMode.Custom
                                :                                  KeycapColorMode.White;
        _evStore.SetSetting("settings.keycap_text_color_mode", ColorModeToString(_evKeycapTextColorMode));
        BtnEvKeycapTextColor.IsEnabled = _evKeycapTextColorMode == KeycapColorMode.Custom;
        ApplyKeycapAppearanceToAllKeys();
    }

    private void BtnEvKeycapCustomColor_Click(object sender, RoutedEventArgs e)
    {
        TryParseHexColor(_evKeycapCustomHex, out var current);

        // WinForms ColorDialog: the one system dialog WPF doesn't have (same pattern as the RGB panel's color pickers).
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen       = true,
            AnyColor       = true,
            SolidColorOnly = true,
            Color          = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _evKeycapCustomHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        _evStore.SetSetting("settings.keycap_custom_hex", _evKeycapCustomHex);
        BtnEvKeycapCustomColor.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));

        if (RbEvKeycapCustom.IsChecked != true)
            RbEvKeycapCustom.IsChecked = true; // RbEvKeycapColor_Checked above calls ApplyKeycapAppearanceToAllKeys
        else
            ApplyKeycapAppearanceToAllKeys();
    }

    private void BtnEvKeycapTextColor_Click(object sender, RoutedEventArgs e)
    {
        TryParseHexColor(_evKeycapTextCustomHex, out var current);

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen       = true,
            AnyColor       = true,
            SolidColorOnly = true,
            Color          = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _evKeycapTextCustomHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        _evStore.SetSetting("settings.keycap_text_custom_hex", _evKeycapTextCustomHex);
        BtnEvKeycapTextColor.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));

        if (RbEvKeycapTextCustom.IsChecked != true)
            RbEvKeycapTextCustom.IsChecked = true; // RbEvKeycapTextColor_Checked above calls ApplyKeycapAppearanceToAllKeys
        else
            ApplyKeycapAppearanceToAllKeys();
    }

    private void CbEvKeycapStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        if (CbEvKeycapStyle.SelectedItem is not KeycapStyleChoice pick) return;
        _evKeycapStyleValue = pick.Style;
        _evStore.SetSetting("settings.keycap_style", ((int)pick.Style).ToString());
        ApplyKeycapAppearanceToAllKeys();
    }

    private void CkEvKeycapTranslucentLegend_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        _evKeycapTranslucentLegend = CkEvKeycapTranslucentLegend.IsChecked == true;
        _evStore.SetSetting("settings.keycap_translucent_legend", _evKeycapTranslucentLegend ? "1" : "0");
        ApplyKeycapAppearanceToAllKeys();
    }

    private Color ResolveEverestKeycapColor() => _evKeycapColorMode switch
    {
        KeycapColorMode.White  => Color.FromRgb(0xE4, 0xE4, 0xE4),
        KeycapColorMode.Custom => TryParseHexColor(_evKeycapCustomHex, out var c) ? c : Color.FromRgb(0x40, 0x40, 0x40),
        _                              => Color.FromRgb(0x15, 0x15, 0x15),
    };

    private Color ResolveEverestKeycapTextColor() => _evKeycapTextColorMode switch
    {
        KeycapColorMode.Black  => Colors.Black,
        KeycapColorMode.Custom => TryParseHexColor(_evKeycapTextCustomHex, out var c) ? c : Colors.White,
        _                              => Colors.White,
    };

    private static bool TryParseHexColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            color = Colors.Transparent;
            return false;
        }
    }

    /// <summary>
    /// Re-applies the static (non-LED) part of the keycap appearance to every captured
    /// Everest key: Background/BorderBrush baseline (Mount follows BorderBrush automatically
    /// via TemplateBinding) and legend color. Call after a settings change, and after any
    /// rebuild of the keyboard canvas (new Button instances start from the style default).
    /// </summary>
    private void ApplyKeycapAppearanceToAllKeys()
    {
        var defaultKeycapBrush = new SolidColorBrush(ResolveEverestKeycapColor());
        var ledOffBrush        = new SolidColorBrush(LedOffColor);
        var textBrush          = new SolidColorBrush(ResolveEverestKeycapTextColor());

        foreach (var (keyId, v) in _evKeyVisuals)
        {
            _evKeycapOverrides.TryGetValue(keyId, out var ov);
            var keycapBrush = ov?.ColorHex is { Length: > 0 } hex && TryParseHexColor(hex, out var c)
                ? new SolidColorBrush(c)
                : defaultKeycapBrush;

            switch (_evKeycapStyleValue)
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
            SetLegendForeground(v.Button, _evKeycapTranslucentLegend ? Brushes.White : textBrush);

            _evOriginalKeyContent.TryGetValue(keyId, out var original);
            ApplyKeycapImageOverride(v.Button, original, ov?.ImagePath);
        }
    }

    /// <summary>Applies one LED-poll tick's live color to a single Everest key, routed to the
    /// visual element that matches the current keycap style; independently of style, the
    /// "Translucent legends" checkbox additionally tints the legend with the live color.</summary>
    private void ApplyEverestLedColor(KeyVisual v, byte r, byte g, byte b)
    {
        bool lit = r != 0 || g != 0 || b != 0;
        var ledBrush = lit ? new SolidColorBrush(Color.FromRgb(r, g, b)) : null;

        switch (_evKeycapStyleValue)
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

        if (_evKeycapTranslucentLegend)
            SetLegendForeground(v.Button, ledBrush ?? Brushes.White);
    }

    /// <summary>Resets a single key to its "LED off" appearance for the current style — used
    /// when the RGB &amp; Lighting section is hidden or after an SDK crash.</summary>
    private void ResetEverestKeyToOff(KeyVisual v) => ApplyEverestLedColor(v, 0, 0, 0);

    /// <summary>
    /// Sets Background/BorderBrush via SetCurrentValue rather than a plain property assignment.
    /// A plain assignment establishes a WPF "local value" (the highest precedence short of
    /// animation/coercion), which would permanently outrank any Style Trigger — in particular
    /// MacroKeyStyle's HasAction/IsHighlighted DataTriggers (green "has action" border, red
    /// press-flash highlight), breaking them the first time keycap appearance is applied.
    /// SetCurrentValue updates the value without acquiring that precedence, so those triggers
    /// keep working exactly as before. Everest has no such competing triggers today, but using
    /// the same helper here keeps the two mirrored implementations consistent.
    /// </summary>
    private static void SetKeyBackground(Button btn, Brush brush) =>
        btn.SetCurrentValue(Button.BackgroundProperty, brush);

    private static void SetKeyBorderBrush(Button btn, Brush brush) =>
        btn.SetCurrentValue(Button.BorderBrushProperty, brush);

    /// <summary>
    /// Flashes/clears the ControlTemplate's "Tint" overlay (the same layer the Hover/Press
    /// triggers use, see MainWindow.xaml) directly from code — used by the physical-key-press
    /// indicator and the guided key-mapping highlight in MainWindow.Everest.cs. Unlike Background/
    /// BorderBrush, Tint is never touched by the keycap appearance system above, so flashing it
    /// never fights with (or gets wiped by) the configured keycap color/style, and clearing it back
    /// to Transparent always restores the exact right baseline — unlike the old approach of
    /// ClearValue-ing Background, which fell back to the Style's default color instead of the
    /// user's configured keycap color.
    /// </summary>
    private static void SetKeyTint(Button btn, Brush brush)
    {
        btn.ApplyTemplate();
        if (btn.Template?.FindName("Tint", btn) is Border tint)
            tint.Background = brush;
    }

    /// <summary>Sets the Foreground of the legend TextBlock(s) — or the Fill of the legend
    /// Shape(s) — inside a key's Content, which is either a single TextBlock (MacroPad, always;
    /// Everest, most keys), a StackPanel (Everest two-line legend), or a Grid (Everest 4-corner
    /// legend from BuildCornerLegend, or the 4-square Win-key icon from BuildWinIcon — the only
    /// non-text legend today, hence the Shape branch below); see BuildEverestKeyboardOverlay in
    /// MainWindow.Everest.cs / InitKeysModule in MainWindow.Keys.cs.</summary>
    private static void SetLegendForeground(Button btn, Brush brush)
    {
        switch (btn.Content)
        {
            case TextBlock tb:
                tb.Foreground = brush;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (child is TextBlock ctb) ctb.Foreground = brush;
                    else if (child is System.Windows.Shapes.Shape shape) shape.Fill = brush;
                }
                break;
        }
    }
}
