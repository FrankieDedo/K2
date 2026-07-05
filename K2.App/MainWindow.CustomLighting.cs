// MainWindow.CustomLighting.cs — partial class: "Custom Lighting" panel.
// Per-key custom color painting: select a color, click keys on the
// keyboard overlay to color them, apply to device via ChangeCustomizeEffect.
// Panel separate from the RGB preset, as per spec.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using K2.App.Services;

namespace K2.App;

public partial class MainWindow
{
    // Currently selected brush color
    private Color _customBrushColor = Color.FromRgb(0x5B, 0xBE, 0xC3); // teal accent

    // Map matrixId → custom color assigned by the user
    private readonly Dictionary<int, Color> _customKeyColors = new();

    // Flag to prevent a key click from being interpreted as action capture
    // while painting
    private bool _customPaintMode;

    // ─────────────────────── Init ───────────────────────

    private void InitCustomLightingPanel()
    {
        // Set the initial brush button color
        BtnCustomBrushColor.Background = new SolidColorBrush(_customBrushColor);

        // Load previously saved colors
        LoadCustomColorsFromStore();
    }

    // ─────────────────────── Paint mode ───────────────────────

    /// <summary>
    /// Called when the user clicks a key on the keyboard overlay while paint
    /// mode is active. Colors the key and records the color.
    /// </summary>
    internal bool TryCustomPaint(Button keyButton, int matrixId)
    {
        if (!_customPaintMode) return false;

        _customKeyColors[matrixId] = _customBrushColor;
        ApplyColorOverlay(keyButton, _customBrushColor);
        return true; // consumed, do not open action dialog
    }

    private void ApplyColorOverlay(Button keyButton, Color c)
    {
        // Simple: use the button Background with semi-transparent color.
        // Original color is saved as an attached property via secondary Tag.
        keyButton.Background = new SolidColorBrush(Color.FromArgb(160, c.R, c.G, c.B));
    }

    private void ClearAllOverlays()
    {
        ClearOverlaysInCanvas(CvsEvKeyboard);
        ClearOverlaysInCanvas(CvsEvNumpad);
    }

    private static void ClearOverlaysInCanvas(Canvas? canvas)
    {
        if (canvas == null) return;
        foreach (var btn in canvas.Children.OfType<Button>())
            btn.ClearValue(Button.BackgroundProperty);
    }

    /// <summary>Reapplies overlays from the colors saved in the map.</summary>
    private void ReapplyCustomOverlays()
    {
        foreach (var kvp in _customKeyColors)
        {
            int matrixId = kvp.Key;
            Color c = kvp.Value;
            // Find the button with Tag == matrixId in the canvases
            var btn = FindKeyButton(matrixId);
            if (btn != null)
                ApplyColorOverlay(btn, c);
        }
    }

    private Button? FindKeyButton(int matrixId)
    {
        Button? found = FindKeyInCanvas(CvsEvKeyboard, matrixId);
        return found ?? FindKeyInCanvas(CvsEvNumpad, matrixId);
    }

    private static Button? FindKeyInCanvas(Canvas? canvas, int matrixId)
    {
        if (canvas == null) return null;
        return canvas.Children.OfType<Button>()
            .FirstOrDefault(b => b.Tag is int id && id == matrixId);
    }

    // ─────────────────────── Event handlers ───────────────────────

    private void CkCustomPaint_Checked(object sender, RoutedEventArgs e)
    {
        _customPaintMode = CkCustomPaint.IsChecked == true;
        if (_customPaintMode)
            ReapplyCustomOverlays();
    }

    private void BtnCustomBrushColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(
                _customBrushColor.R, _customBrushColor.G, _customBrushColor.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _customBrushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        BtnCustomBrushColor.Background = new SolidColorBrush(_customBrushColor);
    }

    private void BtnCustomApply_Click(object sender, RoutedEventArgs e)
    {
        if (_everest is null || !_everest.IsOpen) return;

        int profile = _evStore.GetCurrentProfile();

        // Build the CustomEffect from saved colors
        var effect = new EverestSdkNative.CustomEffect
        {
            data = new EverestSdkNative.CustomData[171]
        };

        // Initialize all to black (off)
        for (int i = 0; i < 171; i++)
            effect.data[i] = new EverestSdkNative.CustomData
            {
                byMatrix = (byte)i,
                color = new EverestSdkNative.FWColor(0, 0, 0)
            };

        // Apply custom colors
        foreach (var kvp in _customKeyColors)
        {
            int matrixId = kvp.Key;
            Color c = kvp.Value;
            if (matrixId >= 0 && matrixId < 171)
            {
                effect.data[matrixId].color = new EverestSdkNative.FWColor(c.R, c.G, c.B);
            }
        }

        // Switch to custom mode and send
        _everest.SwitchToCustomize(profile);
        bool ok = _everest.SetCustomEffect(profile, 0, effect, save: true);
        LogEverest($"[CUSTOM] Applied {_customKeyColors.Count} custom keys -> {ok}");

        SaveCustomColorsToStore();
    }

    private void BtnCustomRead_Click(object sender, RoutedEventArgs e)
    {
        if (_everest is null || !_everest.IsOpen) return;

        int profile = _evStore.GetCurrentProfile();
        if (!_everest.TryGetCustomEffect(profile, 0, out var effect))
        {
            LogEverest("[CUSTOM] Failed to read custom effect");
            return;
        }

        _customKeyColors.Clear();
        ClearAllOverlays();

        int coloredCount = 0;
        if (effect.data != null)
        {
            for (int i = 0; i < effect.data.Length; i++)
            {
                var d = effect.data[i];
                if (d.color.r != 0 || d.color.g != 0 || d.color.b != 0)
                {
                    var c = Color.FromRgb(d.color.r, d.color.g, d.color.b);
                    _customKeyColors[d.byMatrix] = c;
                    coloredCount++;
                }
            }
        }

        ReapplyCustomOverlays();
        SaveCustomColorsToStore();
        LogEverest($"[CUSTOM] Read {coloredCount} colored keys from device");
    }

    private void BtnCustomClear_Click(object sender, RoutedEventArgs e)
    {
        _customKeyColors.Clear();
        ClearAllOverlays();
        SaveCustomColorsToStore();
        LogEverest("[CUSTOM] Custom colors cleared");
    }

    private void BtnCustomFillAll_Click(object sender, RoutedEventArgs e)
    {
        // Fill all 171 LEDs with the current brush color
        for (int i = 0; i < 171; i++)
            _customKeyColors[i] = _customBrushColor;
        ReapplyCustomOverlays();
        LogEverest($"[CUSTOM] All 171 LEDs set to #{_customBrushColor.R:X2}{_customBrushColor.G:X2}{_customBrushColor.B:X2}");
    }

    // ─────────────────────── Persistence ───────────────────────

    private void SaveCustomColorsToStore()
    {
        if (_evStore is null) return;
        // Save as JSON: { "matrixId": "#RRGGBB", ... }
        var dict = _customKeyColors.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => $"#{kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}");
        _evStore.SetSetting("custom.keyColors", JsonSerializer.Serialize(dict));
    }

    private void LoadCustomColorsFromStore()
    {
        if (_evStore is null) return;
        var json = _evStore.GetSetting("custom.keyColors");
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict == null) return;
            _customKeyColors.Clear();
            foreach (var kvp in dict)
            {
                if (int.TryParse(kvp.Key, out int matrixId))
                {
                    try
                    {
                        var c = (Color)ColorConverter.ConvertFromString(kvp.Value);
                        _customKeyColors[matrixId] = c;
                    }
                    catch { /* ignore unparsable colors */ }
                }
            }
        }
        catch { /* ignore invalid JSON */ }
    }
}
