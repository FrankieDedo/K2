using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Reproduces Base Camp's Makalu "Custom" lift-off surface calibration popup —
/// see MakaluProtocol's Lod* region for the wire protocol (confirmed 2026-07-27
/// across 3 independent real USBPcap captures, _reference/usb_dumps/
/// makalu_custom*.pcapng — including one where the user moved the physical
/// mouse before/after both Start and Done). The scribble canvas + progress bar
/// are cosmetic client-side feedback only (real Base Camp's own JS builds them
/// the same way): all 3 captures show zero USB traffic while dragging, and
/// LodGetCalibration ("Done") came back not-ready every single time regardless
/// of on-screen progress (29%..100%) — this matches Base Camp's own decompiled
/// LODSaveCalibration() exactly, which itself only writes Lod_set_surface when
/// lod_result==1 and otherwise no-ops with no retry. So "not ready" is the
/// expected steady state here, not a bug to chase further — the real
/// calibration commitment already happened at Start (Lod_calibration_start),
/// which IS confirmed and real; Done just closes, applying surface bytes on
/// the rare chance they ever do come back ready.
/// </summary>
public partial class MakaluLodCalibrationWindow : Window
{
    private const int GridCols = 20, GridRows = 20;

    private readonly MakaluService _makalu;
    private readonly Action<string> _log;
    private readonly HashSet<(int Col, int Row)> _coveredCells = new();
    private bool _started;
    private bool _dragging;
    private Polyline? _currentStroke;

    /// <summary>Fires when Done closes the popup — SurfaceA/B are non-null only
    /// on the (unconfirmed in practice) rare case LodSetSurface actually
    /// succeeded; null otherwise, meaning "Custom" is committed via Start alone
    /// (see class doc). Lets the owning panel persist whichever happened into
    /// the current profile slot (MakaluRgbSettingsPanel.MkPersistDeviceSettings).</summary>
    internal event Action<byte?, byte?>? Applied;

    internal MakaluLodCalibrationWindow(MakaluService makalu, Action<string> log)
    {
        InitializeComponent();
        _makalu = makalu;
        _log = log;
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        bool okReset = _makalu.LodResetSurface();
        bool okStart = _makalu.LodCalibrationStart();
        _log($"[Makalu] LOD calibration start -> reset={okReset} start={okStart}");
        ClearCanvas();
        _started = true;
        BtnStart.Content = Loc.Get("makalu_lod_reset");
    }

    private void ClearCanvas()
    {
        CvsDraw.Children.Clear();
        CvsDraw.Children.Add(EllHandle);
        _coveredCells.Clear();
        UpdateProgress();
    }

    private void CvsDraw_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_started) return;
        _dragging = true;
        _currentStroke = new Polyline
        {
            Stroke = (Brush)FindResource("K2AccentBrush"),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        CvsDraw.Children.Add(_currentStroke);
        AddPoint(e.GetPosition(CvsDraw));
        CvsDraw.CaptureMouse();
    }

    private void CvsDraw_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _currentStroke is null) return;
        AddPoint(e.GetPosition(CvsDraw));
    }

    private void CvsDraw_MouseUp(object sender, MouseButtonEventArgs e) => StopDragging();

    private void CvsDraw_MouseLeave(object sender, MouseEventArgs e) => StopDragging();

    private void StopDragging()
    {
        _dragging = false;
        _currentStroke = null;
        CvsDraw.ReleaseMouseCapture();
    }

    private void AddPoint(Point p)
    {
        _currentStroke!.Points.Add(p);
        if (CvsDraw.ActualWidth <= 0 || CvsDraw.ActualHeight <= 0) return;
        int col = Math.Clamp((int)(p.X / CvsDraw.ActualWidth * GridCols), 0, GridCols - 1);
        int row = Math.Clamp((int)(p.Y / CvsDraw.ActualHeight * GridRows), 0, GridRows - 1);
        _coveredCells.Add((col, row));
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        int pct = Math.Clamp((int)Math.Round(_coveredCells.Count * 100.0 / (GridCols * GridRows)), 0, 100);
        PbProgress.Value = pct;
        LblProgressVal.Text = $"{pct}%";
    }

    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        var result = _makalu.LodGetCalibration();
        if (result is { } r && _makalu.LodSetSurface(r.SurfaceA, r.SurfaceB))
        {
            _log($"[Makalu] LOD set surface -> A={r.SurfaceA} B={r.SurfaceB}");
            Applied?.Invoke(r.SurfaceA, r.SurfaceB);
        }
        else
        {
            _log("[Makalu] LOD calibration: device reported not-ready (expected — see class doc); Custom committed via Start alone.");
            Applied?.Invoke(null, null);
        }
        Close();
    }
}
