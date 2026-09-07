using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Calibration window for a SCREEN PROBE — the "custom live tile" editor: point K2 at a window,
/// draw a rectangle on a real frame of it, say how to read the pixels inside, and the result is a
/// named reading any DisplayPad key (<c>dp_screen</c>) can show.
///
/// <para>
/// <b>Why it works on a still frame.</b> The capture is a copy of the screen (see
/// <see cref="WindowCapture"/>), and while this window is open K2 is the thing in front of the
/// game — a live view would be a picture of this dialog. So the user takes ONE frame with a
/// countdown long enough to switch back to the game, and calibrates against that frame; every
/// number shown here is measured on it, by the very same code the pad will run
/// (<see cref="ScreenProbeReader.Measure"/>), so the preview cannot drift from the tile.
/// </para>
///
/// <para>Opened through <see cref="IActionHost.EditScreenProbe"/>. <see cref="SavedProbeId"/> is
/// the id to bind the key to, or "" when the probe was DELETED here — the caller has to tell
/// those apart from a plain cancel (which leaves <see cref="Window.DialogResult"/> false).</para>
/// </summary>
public partial class ScreenProbeDialog : Window
{
    /// <summary>Id the caller should bind the key to; "" when the probe was deleted.</summary>
    public string? SavedProbeId { get; private set; }

    private readonly string _id;
    private readonly bool _isNew;

    /// <summary>The frame everything is measured on. Owned by this window.</summary>
    private Bitmap? _frame;

    // The probe rectangle, in the frame's own relative coordinates (0..1) — the form the probe is
    // stored in, so there is no pixel geometry to convert at save time.
    private double _rx, _ry, _rw, _rh;

    private System.Windows.Point? _dragStart;
    private DispatcherTimer? _countdown;
    private int _countdownLeft;

    /// <summary>Blocks the field handlers while the dialog is populating its own controls.</summary>
    private bool _loading = true;

    private sealed record ModeItem(ProbeMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DirItem(ProbeDir Dir, string Label)
    {
        public override string ToString() => Label;
    }

    public ScreenProbeDialog(string? probeId)
    {
        InitializeComponent();

        var existing = ScreenProbeStore.ById(probeId);
        _isNew = existing is null;
        _id = existing?.Id ?? ScreenProbeStore.NewId();

        foreach (var (proc, title) in WindowCapture.ListCapturable())
            CbProcess.Items.Add(new ComboBoxItem { Content = $"{proc}  —  {title}", Tag = proc });

        CbMode.Items.Add(new ModeItem(ProbeMode.Fill,  Loc.Get("screen_probe_mode_fill")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Edge,  Loc.Get("screen_probe_mode_edge")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Color, Loc.Get("screen_probe_mode_color")));

        CbDir.Items.Add(new DirItem(ProbeDir.LeftToRight, Loc.Get("screen_probe_dir_l2r")));
        CbDir.Items.Add(new DirItem(ProbeDir.RightToLeft, Loc.Get("screen_probe_dir_r2l")));
        CbDir.Items.Add(new DirItem(ProbeDir.TopToBottom, Loc.Get("screen_probe_dir_t2b")));
        CbDir.Items.Add(new DirItem(ProbeDir.BottomToTop, Loc.Get("screen_probe_dir_b2t")));

        var probe = existing ?? new ScreenProbe
        {
            Id = _id,
            // A brand-new probe starts on the middle of the frame rather than on nothing: an
            // empty rectangle would give a dash for a reading and no hint that a rectangle is
            // what the window is waiting for.
            X = 0.4, Y = 0.9, W = 0.2, H = 0.03,
            Color = 0xFFFFFF,
            Tolerance = 60,
        };

        _rx = probe.X; _ry = probe.Y; _rw = probe.W; _rh = probe.H;
        TxtName.Text = probe.Name;
        CbProcess.Text = probe.Process;
        SelectMode(probe.Mode);
        SelectDir(probe.Dir);
        SetColor(probe.Color);
        SldTolerance.Value = probe.Tolerance;
        ChkInvert.IsChecked = probe.Invert;
        BtnDelete.Visibility = _isNew ? Visibility.Collapsed : Visibility.Visible;

        _loading = false;
        RefreshModePanels();
        UpdateTolerometerLabel();

        Loaded += (_, _) =>
        {
            LblReadingHint.Text = Loc.Get("screen_probe_reading_hint");
            // An existing probe is worth showing straight away when its program happens to be up
            // and reachable; a new one has nothing to show until the user captures a frame.
            if (!_isNew) TryCaptureNow(silent: true);
            RedrawOverlay();
        };
        SizeChanged += (_, _) => RedrawOverlay();
    }

    // ───────────────────────────── capture ─────────────────────────────

    /// <summary>Starts the countdown, then captures. The countdown is the whole trick: K2 is in
    /// front of the game right now, so the frame has to be taken a few seconds from now, after
    /// the user has clicked back into the game.</summary>
    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_countdown is not null) return;   // already counting: a second click must not stack

        _countdownLeft = 4;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                LblCaptureState.Text = string.Format(Loc.Get("screen_probe_countdown_fmt"), _countdownLeft);
                return;
            }
            StopCountdown();
            TryCaptureNow(silent: false);
        };
        LblCaptureState.Text = string.Format(Loc.Get("screen_probe_countdown_fmt"), _countdownLeft);
        _countdown.Start();
    }

    private void StopCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
    }

    /// <summary>Takes the frame. <paramref name="silent"/> keeps a failed automatic attempt (the
    /// one made when an existing probe is opened) from shouting at the user about a game that
    /// simply isn't running.</summary>
    private void TryCaptureNow(bool silent)
    {
        string process = CurrentProcess();
        if (process.Length == 0)
        {
            if (!silent) LblCaptureState.Text = Loc.Get("screen_probe_no_process");
            return;
        }

        // requireForeground is false here on purpose: the user may well leave K2 in front and
        // capture a window that is merely visible beside it. What must not happen is a LIVE tile
        // doing that — see ScreenProbeReader.
        var hwnd = WindowCapture.FindWindow(process);
        var shot = WindowCapture.TryCaptureClient(hwnd, requireForeground: false);
        if (shot is null)
        {
            if (!silent) LblCaptureState.Text = Loc.Get("screen_probe_capture_failed");
            return;
        }

        _frame?.Dispose();
        _frame = shot;
        ImgFrame.Source = ToImageSource(shot);
        LblNoFrame.Visibility = Visibility.Collapsed;
        LblCaptureState.Text = string.Format(Loc.Get("screen_probe_captured_fmt"), shot.Width, shot.Height);
        RedrawOverlay();
    }

    private string CurrentProcess()
    {
        // The combo is editable: a typed name wins over a selected row, so a program that isn't
        // running right now can still be named by hand.
        if (CbProcess.SelectedItem is ComboBoxItem ci && (string?)ci.Tag is { } tag &&
            CbProcess.Text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            return tag;

        string typed = (CbProcess.Text ?? "").Trim();
        int dash = typed.IndexOf("  —  ", StringComparison.Ordinal);
        if (dash > 0) typed = typed[..dash];
        return typed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? typed[..^4] : typed;
    }

    // ───────────────────────── rectangle + eyedropper ─────────────────────────

    /// <summary>Where the frame is actually drawn inside the overlay: the image is letterboxed
    /// (Stretch=Uniform), so canvas coordinates and frame coordinates differ by this rectangle.</summary>
    private Rect DisplayRect()
    {
        if (_frame is null) return Rect.Empty;
        double cw = OverlayCanvas.ActualWidth, ch = OverlayCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return Rect.Empty;

        double scale = Math.Min(cw / _frame.Width, ch / _frame.Height);
        double w = _frame.Width * scale, h = _frame.Height * scale;
        return new Rect((cw - w) / 2, (ch - h) / 2, w, h);
    }

    /// <summary>A point on the canvas as a 0..1 position inside the frame, or null when the click
    /// landed on the letterbox instead of the picture.</summary>
    private System.Windows.Point? ToRelative(System.Windows.Point p)
    {
        var disp = DisplayRect();
        if (disp.IsEmpty || disp.Width <= 0 || disp.Height <= 0) return null;
        double x = (p.X - disp.X) / disp.Width;
        double y = (p.Y - disp.Y) / disp.Height;
        if (x is < 0 or > 1 || y is < 0 or > 1) return null;
        return new System.Windows.Point(x, y);
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_frame is null) return;
        if (ToRelative(e.GetPosition(OverlayCanvas)) is not { } rel) return;

        // Eyedropper mode: one click sets the reference colour and disarms itself, so the next
        // drag is a rectangle again and the user never wonders which mode they are in.
        if (TglPick.IsChecked == true)
        {
            int px = Math.Clamp((int)(rel.X * _frame.Width), 0, _frame.Width - 1);
            int py = Math.Clamp((int)(rel.Y * _frame.Height), 0, _frame.Height - 1);
            var c = _frame.GetPixel(px, py);
            SetColor((c.R << 16) | (c.G << 8) | c.B);
            TglPick.IsChecked = false;
            RefreshReading();
            return;
        }

        _dragStart = rel;
        OverlayCanvas.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        if (ToRelative(e.GetPosition(OverlayCanvas)) is not { } rel) return;
        ApplyDrag(start, rel);
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is { } start && ToRelative(e.GetPosition(OverlayCanvas)) is { } rel)
            ApplyDrag(start, rel);
        _dragStart = null;
        OverlayCanvas.ReleaseMouseCapture();
        RefreshReading();
    }

    private void ApplyDrag(System.Windows.Point a, System.Windows.Point b)
    {
        _rx = Math.Min(a.X, b.X);
        _ry = Math.Min(a.Y, b.Y);
        _rw = Math.Abs(a.X - b.X);
        _rh = Math.Abs(a.Y - b.Y);
        RedrawOverlay();
    }

    /// <summary>Puts the selection rectangle back where the relative coordinates say, and
    /// refreshes everything derived from it (the magnified crop, the reading).</summary>
    private void RedrawOverlay()
    {
        var disp = DisplayRect();
        if (disp.IsEmpty || _rw <= 0 || _rh <= 0)
        {
            SelRect.Visibility = Visibility.Collapsed;
        }
        else
        {
            Canvas.SetLeft(SelRect, disp.X + _rx * disp.Width);
            Canvas.SetTop(SelRect, disp.Y + _ry * disp.Height);
            SelRect.Width = Math.Max(_rw * disp.Width, 1);
            SelRect.Height = Math.Max(_rh * disp.Height, 1);
            SelRect.Visibility = Visibility.Visible;
        }
        RefreshZoom();
        RefreshReading();
    }

    // ───────────────────────────── fields ─────────────────────────────

    private void SelectMode(ProbeMode mode) =>
        CbMode.SelectedItem = CbMode.Items.OfType<ModeItem>().FirstOrDefault(m => m.Mode == mode)
                              ?? CbMode.Items.OfType<ModeItem>().First();

    private void SelectDir(ProbeDir dir) =>
        CbDir.SelectedItem = CbDir.Items.OfType<DirItem>().FirstOrDefault(d => d.Dir == dir)
                             ?? CbDir.Items.OfType<DirItem>().First();

    private ProbeMode CurrentMode() => (CbMode.SelectedItem as ModeItem)?.Mode ?? ProbeMode.Fill;
    private ProbeDir CurrentDir() => (CbDir.SelectedItem as DirItem)?.Dir ?? ProbeDir.LeftToRight;

    private int _color;

    private void SetColor(int rgb)
    {
        _color = rgb & 0xFFFFFF;
        // Fully qualified: both System.Drawing and System.Windows.Media are in scope here, and
        // "Color" alone is ambiguous between the frame's pixels and the swatch's brush.
        ColorSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
            (byte)(_color >> 16), (byte)(_color >> 8), (byte)_color));
    }

    private void CbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshModePanels();
        RefreshReading();
    }

    private void RefreshModePanels()
    {
        var mode = CurrentMode();
        PnlDir.Visibility = mode == ProbeMode.Edge ? Visibility.Visible : Visibility.Collapsed;
        LblModeHint.Text = Loc.Get(mode switch
        {
            ProbeMode.Edge  => "screen_probe_mode_edge_hint",
            ProbeMode.Color => "screen_probe_mode_color_hint",
            _               => "screen_probe_mode_fill_hint",
        });
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshReading();
    }

    private void SldTolerance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTolerometerLabel();
        if (_loading) return;
        RefreshReading();
    }

    private void UpdateTolerometerLabel() =>
        LblTolerance.Text = string.Format(Loc.Get("screen_probe_tolerance_fmt"), (int)SldTolerance.Value);

    // ───────────────────────── preview + reading ─────────────────────────

    /// <summary>The probe as the fields describe it right now — also exactly what OK will save,
    /// so the reading below can never be measured with different settings than the ones stored.</summary>
    private ScreenProbe BuildProbe() => new()
    {
        Id = _id,
        Name = (TxtName.Text ?? "").Trim(),
        Process = CurrentProcess(),
        X = _rx, Y = _ry, W = _rw, H = _rh,
        Mode = CurrentMode(),
        Dir = CurrentDir(),
        Color = _color,
        Tolerance = (int)SldTolerance.Value,
        Invert = ChkInvert.IsChecked == true,
    };

    /// <summary>The magnified crop: what the probe is actually looking at, big enough to see
    /// whether the rectangle is on the bar or half off it.</summary>
    private void RefreshZoom()
    {
        if (_frame is null) { ImgZoom.Source = null; return; }

        var rect = ScreenProbeReader.PixelRect(_frame.Width, _frame.Height, BuildProbe());
        if (rect.Width <= 0 || rect.Height <= 0) { ImgZoom.Source = null; return; }

        try
        {
            using var crop = _frame.Clone(rect, _frame.PixelFormat);
            ImgZoom.Source = ToImageSource(crop);
        }
        catch { ImgZoom.Source = null; }
    }

    private void RefreshReading()
    {
        if (_frame is null) { LblReading.Text = "—"; return; }

        var probe = BuildProbe();
        var reading = ScreenProbeReader.Measure(_frame, probe);
        LblReading.Text = !reading.Valid
            ? "—"
            : probe.Mode == ProbeMode.Color
                ? Loc.Get(reading.On ? "screen_probe_on" : "screen_probe_off")
                : $"{(int)Math.Round(reading.Fraction * 100)}%";
    }

    // ───────────────────────────── save ─────────────────────────────

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var probe = BuildProbe();

        if (probe.Name.Length == 0)
        {
            MessageBox.Show(this, Loc.Get("screen_probe_need_name"), Title,
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (probe.Process.Length == 0)
        {
            MessageBox.Show(this, Loc.Get("screen_probe_need_process"), Title,
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (probe.W <= 0 || probe.H <= 0)
        {
            MessageBox.Show(this, Loc.Get("screen_probe_need_rect"), Title,
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ScreenProbeStore.Save(probe);
        App.WriteLog($"[PROBE] saved \"{probe.Name}\" ({probe.Id}) on {probe.Process}: " +
                     $"{probe.Mode} rect=({probe.X:F3},{probe.Y:F3},{probe.W:F3},{probe.H:F3}) " +
                     $"colour=#{probe.Color:X6} tol={probe.Tolerance}");
        SavedProbeId = probe.Id;
        DialogResult = true;
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.Get("screen_probe_delete_confirm"), Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        ScreenProbeStore.Delete(_id);
        // "" and not null: the caller has to tell a deletion (the key must let go of this probe)
        // apart from a cancel (the key keeps what it had).
        SavedProbeId = "";
        DialogResult = true;
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        StopCountdown();
        _frame?.Dispose();
        _frame = null;
        // The live reader may be holding a frame of the same window from an earlier tick; drop it
        // so the pad re-captures instead of showing a value measured before this edit.
        ScreenProbeReader.Flush();
    }

    /// <summary>GDI bitmap → WPF image source. Goes through a PNG in memory rather than
    /// <c>CreateBitmapSourceFromHBitmap</c> so there is no GDI handle to leak on a window the
    /// user may open and close a dozen times while aiming a probe.</summary>
    private static ImageSource ToImageSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;   // decode now: the stream is about to die
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
