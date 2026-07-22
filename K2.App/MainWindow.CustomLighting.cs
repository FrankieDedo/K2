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
    /// effects the user asked for (matches Base Camp's own Custom section). Index 0
    /// (Static) is the only one wired to actually send data today — see
    /// <see cref="BtnCustomApply_Click"/>'s guard and TODO.md's "mixed dynamic effects"
    /// open question (2026-07-22). Plain hardcoded English strings, same pattern as
    /// MainWindow.Everest.cs's EvEffectList (that combo isn't localized either).</summary>
    private static readonly string[] CustomPaintEffects =
        { "Static", "Wave", "Tornado", "Breathing", "Reactive", "Matrix", "Yeti", "Off" };

    private void InitCustomLightingPanel()
    {
        // Set the initial brush button color
        BtnCustomBrushColor.Background = new SolidColorBrush(_customBrushColor);

        CbCustomPaintEffect.ItemsSource = CustomPaintEffects;
        CbCustomPaintEffect.SelectedIndex = 0;

        BuildBorderSquares();

        // Load previously saved colors
        LoadCustomColorsFromStore();

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
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4F)),
                Background = Brushes.Transparent,
                Tag = wireIdx,
                Cursor = System.Windows.Input.Cursors.Hand,
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
    /// mode is active. Colors the key and records the color (by LED index).
    /// </summary>
    internal bool TryCustomPaint(Button keyButton, int matrixId)
    {
        if (!_customPaintMode) return false;

        if (TryButtonToLed(keyButton, matrixId, out int led))
        {
            _customKeyColors[led] = _customBrushColor;
            ApplyColorOverlay(keyButton, _customBrushColor);
        }
        return true; // consumed, do not open action dialog
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

    /// <summary>Reapplies overlays from the colors saved in the map.</summary>
    private void ReapplyCustomOverlays()
    {
        foreach (var kvp in _customKeyColors)
        {
            var btn = FindKeyButtonByLed(kvp.Key);
            if (btn != null)
                ApplyColorOverlay(btn, kvp.Value);
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
            ReapplyCustomOverlays();
        else
            ClearAllOverlays();
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
    }

    private void BtnCustomBrushColor_Click(object sender, RoutedEventArgs e)
    {
        int current = (_customBrushColor.R << 16) | (_customBrushColor.G << 8) | _customBrushColor.B;
        int? picked = K2.Core.ColorPickerDialog.Pick(this, current);
        if (picked is not int rgb) return;

        _customBrushColor = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        BtnCustomBrushColor.Background = new SolidColorBrush(_customBrushColor);
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

        if (CbCustomPaintEffect.SelectedIndex > 0)
        {
            // Only "Static" (per-key color) is wired to a real wire protocol today —
            // see CustomPaintEffects' doc comment. Refuse rather than silently send
            // Static-shaped data under a different effect's name.
            LogEverest($"[CUSTOM] '{CbCustomPaintEffect.SelectedItem}' paint effect not yet implemented — only Static sends data. Nothing applied.");
            return;
        }

        ApplyCustomColorsToDevice();
        SaveCustomColorsToStore();
    }

    /// <summary>
    /// Sends the current in-memory paint state (<see cref="_customKeyColors"/>/
    /// <see cref="_customSideColors"/>) to the device via the raw-HID custom apply.
    /// Unpainted positions go out black, same as Base Camp sends for unselected keys —
    /// so an empty state means "everything off". Called by the panel's Apply button and
    /// by ApplyCurrentEffect when the Custom effect is (re)selected (auto-apply of the
    /// remembered colors, user request 2026-07-22).
    /// </summary>
    private bool ApplyCustomColorsToDevice(byte brightness = 100)
    {
        if (_everest is null || !_everest.IsOpen) return false;

        // _customKeyColors is already keyed by LED index = wire position.
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

        bool ok = _everest.ApplyEverestCustomLighting(keycapWire, sideWire, brightness);
        LogEverest($"[CUSTOM] Applied {_customKeyColors.Count} keycap + {_customSideColors.Count} border LEDs via raw HID (bright={brightness}) -> {ok}");
        return ok;
    }

    // NB: the "Read from device" button was removed 2026-07-22 (user request): it had
    // already been demoted to a local-store reload (SDKDLL's GetEffCustomizeContent
    // returned garbage on real hardware and raw HID has no known read-back command),
    // and that reload happens implicitly on panel init and paint-mode activation
    // (LoadCustomColorsFromStore + ReapplyCustomOverlays).

    private void BtnCustomClear_Click(object sender, RoutedEventArgs e)
    {
        _customKeyColors.Clear();
        _customSideColors.Clear();
        ClearAllOverlays();
        SaveCustomColorsToStore();
        LogEverest("[CUSTOM] Custom colors cleared");
    }

    private void BtnCustomFillAll_Click(object sender, RoutedEventArgs e)
    {
        // Fill every known LED index (values of both LedMatrixMapping tables — main
        // board + numpad, no overlap) + all 45 border LEDs with the brush color.
        foreach (var led in Models.LedMatrixMapping.EverestKeyboard.Values.Concat(Models.LedMatrixMapping.EverestNumpad.Values))
            _customKeyColors[led] = _customBrushColor;
        for (int i = 0; i < Services.EverestSideLedProtocol.TotalCount; i++)
            _customSideColors[i] = _customBrushColor;
        ReapplyCustomOverlays();
        LogEverest($"[CUSTOM] All keys + {Services.EverestSideLedProtocol.TotalCount} border LEDs set to #{_customBrushColor.R:X2}{_customBrushColor.G:X2}{_customBrushColor.B:X2}");
    }

    // ─────────────────────── Persistence ───────────────────────

    private void SaveCustomColorsToStore()
    {
        if (_evStore is null) return;
        // Save as JSON: { "ledIndex": "#RRGGBB", ... }. Key renamed keyColors →
        // keyLedColors 2026-07-22 when the dictionary switched from VK-keyed to
        // LED-index-keyed (see _customKeyColors' doc) — old VK-keyed data under the
        // previous name would be misread as LED indices, so it's simply orphaned.
        var dict = _customKeyColors.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => $"#{kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}");
        _evStore.SetSetting("custom.keyLedColors", JsonSerializer.Serialize(dict));

        var sideDict = _customSideColors.ToDictionary(
            kvp => kvp.Key.ToString(),
            kvp => $"#{kvp.Value.R:X2}{kvp.Value.G:X2}{kvp.Value.B:X2}");
        _evStore.SetSetting("custom.sideColors", JsonSerializer.Serialize(sideDict));
    }

    private void LoadCustomColorsFromStore()
    {
        if (_evStore is null) return;
        var json = _evStore.GetSetting("custom.keyLedColors");
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

        var sideJson = _evStore.GetSetting("custom.sideColors");
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
    }

    // ─────────────────── Rectangular multi-LED selection ───────────────────
    // Drag a rubber-band square anywhere over the device box (keys, numpad,
    // border squares) to paint every LED it touches with the brush color (user
    // request 2026-07-22, mirrors Base Camp's multi-select). Wired to
    // BdrEvDeviceBox's Preview mouse events (MainWindow.xaml) so the drag can
    // start on top of a key Button; a plain click (below the 5px threshold)
    // falls through to the normal single-key paint.

    private Point _rubberStart;
    private bool _rubberTracking; // mouse down seen, watching for drag threshold
    private bool _rubberActive;   // threshold passed, rubber band visible

    private void EvDeviceBox_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_customPaintMode) return;
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
        PaintLedsInRect(rect);
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
                    {
                        _customKeyColors[led] = _customBrushColor;
                        ApplyColorOverlay(btn, _customBrushColor);
                    }
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
