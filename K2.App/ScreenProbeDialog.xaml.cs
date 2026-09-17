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

    /// <summary>Profile this probe is being calibrated for, or null when it belongs to no profile
    /// (the editor opened from a key's own dialog). Decides whose "last game" is offered and
    /// whose gets updated on save.</summary>
    private readonly string? _profileKey;

    /// <summary>The frame everything is measured on. Owned by this window.</summary>
    private Bitmap? _frame;

    /// <summary>Process the frame on screen was taken from, so switching the combo to another
    /// program can put ITS kept frame up instead of leaving the old game's pixels under a
    /// rectangle that is now aimed at something else.</summary>
    private string _frameProcess = "";

    /// <summary>When the frame on screen was taken, or null when it was captured just now. What
    /// it really answers is "are you looking at the game or at a memory of it".</summary>
    private DateTime? _frameSavedAt;

    // The probe rectangle, in the frame's own relative coordinates (0..1) — the form the probe is
    // stored in, so there is no pixel geometry to convert at save time.
    private double _rx, _ry, _rw, _rh;

    private System.Windows.Point? _dragStart;
    private DispatcherTimer? _countdown;
    private int _countdownLeft;

    /// <summary>What the left button is doing right now: drawing a new rectangle, or carrying the
    /// one that is already there. A rectangle you cannot move is a rectangle you have to redraw
    /// from scratch every time it lands two pixels off.</summary>
    private enum Drag { None, New, Move, Resize }

    private Drag _drag;

    /// <summary>Sides of the selection a resize is carrying, <see cref="Side.None"/> when the
    /// press was not on its border.</summary>
    [Flags]
    private enum Side { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private Side _resizeSide;

    /// <summary>The rectangle as it was when the resize started, in relative coordinates: the sides
    /// that are NOT being dragged come from here, so they cannot creep while the pointer
    /// wanders.</summary>
    private Rect _resizeFrom;

    /// <summary>Width of the grab band around the selection's border, in CANVAS pixels.</summary>
    private const double GrabBand = 7;

    /// <summary>Where inside the rectangle the move started, in relative coordinates, so the
    /// rectangle follows the pointer instead of jumping its top-left corner under it.</summary>
    private System.Windows.Point _moveGrab;

    // The view: 1 = the whole frame fitted in the box, more = zoomed in, with the pan holding the
    // fitted picture's offset in canvas pixels. A game's minimap is a hundred pixels wide on a 4K
    // frame, and aiming at it fitted to a 600px box means aiming at a smudge.
    private double _zoom = 1;
    private double _panX, _panY;
    private System.Windows.Point? _panStart;

    private const double MinZoom = 1, MaxZoom = 16;

    /// <summary>Side of the box a plain click drops, as a fraction of the frame's short side.</summary>
    private const double ClickBoxShare = 0.12;

    /// <summary>A capture is copied onto a SQUARE key, so its rectangle is kept square (in frame
    /// PIXELS, which is what gets cropped) instead of being squashed into the key later.</summary>
    private bool SquareRect => CurrentMode() == ProbeMode.Capture;

    /// <summary>Blocks the field handlers while the dialog is populating its own controls.</summary>
    private bool _loading = true;

    /// <summary>Size in FRAME pixels a NEW probe should take from the last one, applied when the
    /// first frame arrives and then forgotten — a remembered size is a starting point, not a rule
    /// that keeps re-imposing itself on a rectangle the user has since drawn.</summary>
    private (int W, int H)? _seedSize;

    private sealed record ModeItem(ProbeMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DirItem(ProbeDir Dir, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RefItem(ProbeRef Ref, string Label)
    {
        public override string ToString() => Label;
    }

    /// <param name="probeId">Probe to edit, or null for a new one.</param>
    /// <param name="seedMode">Mode a NEW probe starts in. The studio passes it when the action
    /// being built reads text: the rectangle is then drawn around a number from the first moment,
    /// instead of being calibrated as a colour fill and switched over afterwards.</param>
    /// <param name="profileKey">The game profile this probe is being calibrated for, when there is
    /// one. It is the bucket the chosen program is remembered in and read back from, so each
    /// profile starts on its OWN last game — see <see cref="ScreenProbeDefaults.ProcessFor"/>.</param>
    /// <param name="profileExe">That profile's own program, used the first time a profile is
    /// calibrated and has nothing remembered yet.</param>
    public ScreenProbeDialog(string? probeId, ProbeMode? seedMode = null,
                             string? profileKey = null, string? profileExe = null)
    {
        InitializeComponent();
        _profileKey = profileKey;

        var existing = ScreenProbeStore.ById(probeId);
        _isNew = existing is null;
        _id = existing?.Id ?? ScreenProbeStore.NewId();

        foreach (var (proc, title) in WindowCapture.ListCapturable())
            CbProcess.Items.Add(new ComboBoxItem { Content = $"{proc}  —  {title}", Tag = proc });

        CbMode.Items.Add(new ModeItem(ProbeMode.Fill,  Loc.Get("screen_probe_mode_fill")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Edge,  Loc.Get("screen_probe_mode_edge")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Color, Loc.Get("screen_probe_mode_color")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Number, Loc.Get("screen_probe_mode_number")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Text, Loc.Get("screen_probe_mode_text")));
        CbMode.Items.Add(new ModeItem(ProbeMode.Capture, Loc.Get("screen_probe_mode_capture")));

        CbRefX.Items.Add(new RefItem(ProbeRef.Start,  Loc.Get("screen_probe_ref_left")));
        CbRefX.Items.Add(new RefItem(ProbeRef.Center, Loc.Get("screen_probe_ref_hcenter")));
        CbRefX.Items.Add(new RefItem(ProbeRef.End,    Loc.Get("screen_probe_ref_right")));
        CbRefY.Items.Add(new RefItem(ProbeRef.Start,  Loc.Get("screen_probe_ref_top")));
        CbRefY.Items.Add(new RefItem(ProbeRef.Center, Loc.Get("screen_probe_ref_vcenter")));
        CbRefY.Items.Add(new RefItem(ProbeRef.End,    Loc.Get("screen_probe_ref_bottom")));
        SelectRef(CbRefX, ScreenProbeDefaults.RefX);
        SelectRef(CbRefY, ScreenProbeDefaults.RefY);

        CbDir.Items.Add(new DirItem(ProbeDir.LeftToRight, Loc.Get("screen_probe_dir_l2r")));
        CbDir.Items.Add(new DirItem(ProbeDir.RightToLeft, Loc.Get("screen_probe_dir_r2l")));
        CbDir.Items.Add(new DirItem(ProbeDir.TopToBottom, Loc.Get("screen_probe_dir_t2b")));
        CbDir.Items.Add(new DirItem(ProbeDir.BottomToTop, Loc.Get("screen_probe_dir_b2t")));

        bool seedCapture = _isNew && seedMode == ProbeMode.Capture;
        // A new probe opens on the program the last one read — four readings of one HUD is the
        // normal case, and retyping the game every time is work for nothing.
        var probe = existing ?? new ScreenProbe
        {
            Id = _id,
            Process = ScreenProbeDefaults.ProcessFor(profileKey, profileExe),
            // A brand-new probe starts on the middle of the frame rather than on nothing: an
            // empty rectangle would give a dash for a reading and no hint that a rectangle is
            // what the window is waiting for. A capture starts as a box in the middle instead of
            // a bar at the bottom: it is a picture, not a gauge.
            X = 0.4, W = 0.2,
            Y = seedCapture ? 0.4 : 0.9, H = seedCapture ? 0.2 : 0.03,
            Color = 0xFFFFFF,
            Tolerance = 60,
        };

        _rx = probe.X; _ry = probe.Y; _rw = probe.W; _rh = probe.H;
        // The last size is applied once there are PIXELS to apply it against (it is stored in frame
        // pixels, and the frame arrives later) — see ShowFrame.
        _seedSize = _isNew ? ScreenProbeDefaults.Size : null;
        TxtName.Text = probe.Name;
        CbProcess.Text = probe.Process;
        SelectMode(_isNew && seedMode is { } seed ? seed : probe.Mode);
        SelectDir(probe.Dir);
        SetColor(probe.Color);
        SldTolerance.Value = probe.Tolerance;
        ChkInvert.IsChecked = probe.Invert;
        ChkAnyText.IsChecked = probe.AnyText;
        BtnDelete.Visibility = _isNew ? Visibility.Collapsed : Visibility.Visible;

        _loading = false;
        RefreshModePanels();
        UpdateTolerometerLabel();

        Loaded += (_, _) =>
        {
            // Worth showing straight away when the program happens to be up and reachable — for a
            // NEW probe too, since it opens on the program the last one read; if it isn't, the
            // frame kept from the last time it was takes its place, which is what lets a reading be
            // edited with the game closed. Silent: a game that simply isn't running is not an
            // error anyone needs told.
            TryCaptureNow(silent: true);
            // Only for a probe that already exists. A NEW one is never handed a picture it did
            // not ask for: last week's frame under a brand-new rectangle looks exactly like the
            // game running, and the user calibrates against a HUD that has since moved. They
            // capture, or they pick a saved screenshot from the browser.
            if (_frame is null && !_isNew) ShowKeptFrame(CurrentProcess());
            FillShots(null);
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
            // The program isn't up: for a probe that already exists the frame kept from last time
            // is the next best thing, and the failure is only worth saying out loud when there
            // isn't one. A new probe is told the truth instead — see the Loaded handler.
            if (!_isNew && ShowKeptFrame(process)) return;
            if (!silent) LblCaptureState.Text = Loc.Get("screen_probe_capture_failed");
            return;
        }

        // Kept before it is shown: this is the frame that will still be here next week, when the
        // game is closed and the user wants to nudge the rectangle.
        ScreenFrameCache.Save(process, shot);
        ShowFrame(shot, process, savedAt: null);
    }

    /// <summary>Puts the frame kept for that program on screen, if there is one.</summary>
    /// <returns>Whether a frame was found.</returns>
    private bool ShowKeptFrame(string process)
    {
        if (ScreenFrameCache.StampOf(process) is not { } stamp) return false;
        if (ScreenFrameCache.Load(process) is not { } kept) return false;

        ShowFrame(kept, process, stamp);
        return true;
    }

    /// <summary>The one way a frame gets on screen. <paramref name="savedAt"/> null means it was
    /// captured just now; anything else says when it was taken, and the line above the picture
    /// says so too — a measurement taken off last week's frame is worth knowing about.</summary>
    private void ShowFrame(Bitmap frame, string process, DateTime? savedAt)
    {
        _frame?.Dispose();
        _frame = frame;
        _frameProcess = process;
        _frameSavedAt = savedAt;

        ImgFrame.Source = ToImageSource(frame);
        LblNoFrame.Visibility = Visibility.Collapsed;
        LblViewHint.Visibility = Visibility.Visible;
        LblCaptureState.Text = savedAt is { } when
            ? string.Format(Loc.Get("screen_probe_saved_fmt"), when.ToString("g"), frame.Width, frame.Height)
            : string.Format(Loc.Get("screen_probe_captured_fmt"), frame.Width, frame.Height);

        // Now there are pixels: the size carried over from the last probe can be applied (once),
        // and a capture's rectangle can finally be squared against them.
        if (_seedSize is { } seed)
        {
            _seedSize = null;
            _rw = Math.Clamp(seed.W / (double)frame.Width, 0, 1);
            _rh = Math.Clamp(seed.H / (double)frame.Height, 0, 1);
            _rx = Math.Clamp(_rx, 0, 1 - _rw);
            _ry = Math.Clamp(_ry, 0, 1 - _rh);
        }
        SquareUpRect();
        RedrawOverlay();
    }

    /// <summary>Naming another program puts ITS kept frame up, and re-sorts the screenshot browser
    /// around the new program. The kept frame is for an EXISTING probe only: a new one shows
    /// nothing until the user captures or picks a saved screenshot, so that what is on screen is
    /// always something they chose.</summary>
    private void ProcessChanged()
    {
        if (_loading) return;
        string process = CurrentProcess();
        if (process.Length == 0 ||
            string.Equals(process, _frameProcess, StringComparison.OrdinalIgnoreCase)) return;

        FillShots(SelectedShotId());
        if (!_isNew) ShowKeptFrame(process);
    }

    // ───────────────────────────── saved screenshots ─────────────────────────────

    /// <summary>Set while the browser is being refilled, so putting the list back does not read as
    /// the user picking a shot and swap the frame under them.</summary>
    private bool _fillingShots;

    private string? SelectedShotId() => (CbShots.SelectedItem as ShotItem)?.Id;

    /// <summary>One row of the browser. A record so the label is computed once and the combo has
    /// something to show without a template.</summary>
    private sealed record ShotItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Refills the browser, leading with the shots of the program in the box — a
    /// screenshot of another game is never the one being looked for, but it stays listed, because
    /// the same HUD sometimes lives in a launcher under a different process name.</summary>
    private void FillShots(string? select)
    {
        _fillingShots = true;
        try
        {
            string process = CurrentProcess();
            CbShots.Items.Clear();

            foreach (var shot in ScreenShotStore.All()
                         .OrderByDescending(s => process.Length > 0 &&
                                                 string.Equals(s.Process, process, StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(s => s.TakenAt))
                CbShots.Items.Add(new ShotItem(shot.Id, shot.Name));

            CbShots.SelectedItem = CbShots.Items.OfType<ShotItem>().FirstOrDefault(i => i.Id == select);
            CbShots.IsEnabled = CbShots.Items.Count > 0;
            BtnShotDelete.IsEnabled = CbShots.SelectedItem is not null;
            if (CbShots.Items.Count == 0) LblShotState.Text = Loc.Get("screen_shot_none");
        }
        finally
        {
            _fillingShots = false;
        }
    }

    private void CbShots_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnShotDelete.IsEnabled = CbShots.SelectedItem is not null;
        if (_fillingShots || SelectedShotId() is not { } id) return;
        if (ScreenShotStore.ById(id) is not { } def) return;
        if (ScreenShotStore.Load(id) is not { } frame)
        {
            LblShotState.Text = Loc.Get("screen_shot_unreadable");
            return;
        }

        // The shot carries the program it was taken from: without following it, a rectangle would
        // be measured on one game's pixels and saved against another game's name.
        if (def.Process.Length > 0 &&
            !string.Equals(def.Process, CurrentProcess(), StringComparison.OrdinalIgnoreCase))
        {
            bool was = _loading;
            _loading = true;
            CbProcess.Text = def.Process;
            _loading = was;
        }

        ShowFrame(frame, def.Process, def.TakenAt);
        LblShotState.Text = "";
        RedrawOverlay();
    }

    /// <summary>Keeps what is on screen as a screenshot of its own. The only way one is ever
    /// created — nothing in this window saves a shot by itself.</summary>
    private void BtnShotSave_Click(object sender, RoutedEventArgs e)
    {
        if (_frame is null)
        {
            LblShotState.Text = Loc.Get("screen_shot_nothing");
            return;
        }

        var saved = ScreenShotStore.Save(_frameProcess.Length > 0 ? _frameProcess : CurrentProcess(), _frame);
        if (saved is null)
        {
            LblShotState.Text = Loc.Get("screen_shot_failed");
            return;
        }

        FillShots(saved.Id);
        LblShotState.Text = Loc.Get("screen_shot_saved");
    }

    private void BtnShotDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedShotId() is not { } id || ScreenShotStore.ById(id) is not { } def) return;
        if (MessageBox.Show(this, Loc.Get("screen_shot_delete_confirm", def.Name),
                            Loc.Get("screen_probe_title"), MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        ScreenShotStore.Delete(id);
        // The picture stays on screen on purpose: it is the frame being calibrated against, and
        // pulling it away because its copy was deleted would throw away work in progress.
        FillShots(null);
        LblShotState.Text = Loc.Get("screen_shot_deleted");
    }

    private void CbProcess_SelectionChanged(object sender, SelectionChangedEventArgs e) => ProcessChanged();

    private void CbProcess_LostFocus(object sender, RoutedEventArgs e) => ProcessChanged();

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

    /// <summary>Where the frame is actually drawn inside the overlay: the fitted picture, zoomed
    /// and panned, so canvas coordinates and frame coordinates differ by this rectangle.
    /// Everything else on this window — the selection, the eyedropper, the Image element's own
    /// size and offset — is derived from it, so the view can never disagree with what the mouse
    /// thinks it is pointing at.</summary>
    private Rect DisplayRect()
    {
        if (_frame is null) return Rect.Empty;
        double cw = OverlayCanvas.ActualWidth, ch = OverlayCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return Rect.Empty;

        double scale = FitScale() * _zoom;
        double w = _frame.Width * scale, h = _frame.Height * scale;
        return new Rect((cw - w) / 2 + _panX, (ch - h) / 2 + _panY, w, h);
    }

    /// <summary>Scale at which the whole frame fits the box — zoom 1.</summary>
    private double FitScale()
    {
        if (_frame is null) return 1;
        double cw = OverlayCanvas.ActualWidth, ch = OverlayCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return 1;
        return Math.Min(cw / _frame.Width, ch / _frame.Height);
    }

    /// <summary>Keeps the panned picture covering the box, so the frame can never be dragged off
    /// into the void. At zoom 1 the picture is smaller than the box in at least one direction and
    /// the pan is pinned to zero — fitted means fitted.</summary>
    private void ClampPan()
    {
        if (_frame is null) { _panX = _panY = 0; return; }
        double cw = OverlayCanvas.ActualWidth, ch = OverlayCanvas.ActualHeight;
        double scale = FitScale() * _zoom;
        double maxX = Math.Max(0, (_frame.Width * scale - cw) / 2);
        double maxY = Math.Max(0, (_frame.Height * scale - ch) / 2);
        _panX = Math.Clamp(_panX, -maxX, maxX);
        _panY = Math.Clamp(_panY, -maxY, maxY);
    }

    /// <summary>Puts the Image itself exactly where <see cref="DisplayRect"/> says — same
    /// rectangle, no second opinion. The picture is sized and offset by the one function the
    /// selection and the mouse mapping also go through, instead of being letterboxed by the Image:
    /// Stretch=Uniform centres the bitmap INSIDE the element, so scaling about the canvas centre
    /// scaled about a point the letterbox had already moved, and a rectangle drawn while zoomed
    /// landed on the pixels it would have covered unzoomed.</summary>
    private void UpdateFrameTransform()
    {
        var disp = DisplayRect();
        if (disp.IsEmpty || _frame is null)
        {
            ImgFrame.Width = ImgFrame.Height = 0;
            ImgFrame.RenderTransform = Transform.Identity;
        }
        else
        {
            // The element's LAYOUT size is the FITTED picture, never bigger than the box: WPF
            // layout-clips an element whose render size overflows its arrange slot, and sizing the
            // element to the ZOOMED rectangle is what cut the picture off at the box's width.
            double fit = FitScale();
            ImgFrame.Width = Math.Min(_frame.Width * fit, OverlayCanvas.ActualWidth);
            ImgFrame.Height = Math.Min(_frame.Height * fit, OverlayCanvas.ActualHeight);

            // The zoom happens in RENDER space, which layout never sees — only the frame box's own
            // ClipToBounds trims it. Scaled about the element's ORIGIN and then moved to
            // DisplayRect's corner: with the element left/top aligned, that origin is the canvas
            // origin, so the picture ends up exactly on DisplayRect — the same rectangle the mouse
            // mapping and the selection are computed from.
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(_zoom, _zoom));
            group.Children.Add(new TranslateTransform(disp.X, disp.Y));
            ImgFrame.RenderTransform = group;
        }

        // Zoomed in, the point is to see the PIXELS; fitted, smoothing is what makes a 4K frame
        // readable in a 600px box.
        RenderOptions.SetBitmapScalingMode(
            ImgFrame, _zoom > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        LblZoomLevel.Text = $"{(int)Math.Round(_zoom * 100)}%";
        var chrome = _frame is null ? Visibility.Collapsed : Visibility.Visible;
        PnlZoomBar.Visibility = LblViewHint.Visibility = chrome;
    }

    /// <summary>Zooms around a point of the CANVAS, keeping whatever is under it right where it
    /// is — the only kind of zoom that lets a user keep aiming at the thing they were aiming at.</summary>
    private void ZoomAt(double zoom, System.Windows.Point anchor)
    {
        if (_frame is null) return;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        var before = DisplayRect();
        double fx = before.Width > 0 ? (anchor.X - before.X) / before.Width : 0.5;
        double fy = before.Height > 0 ? (anchor.Y - before.Y) / before.Height : 0.5;

        _zoom = zoom;
        double cw = OverlayCanvas.ActualWidth, ch = OverlayCanvas.ActualHeight;
        double scale = FitScale() * _zoom;
        double w = _frame.Width * scale, h = _frame.Height * scale;
        _panX = anchor.X - fx * w - (cw - w) / 2;
        _panY = anchor.Y - fy * h - (ch - h) / 2;
        RedrawOverlay();   // clamps the pan on the way through
    }

    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_frame is null) return;
        ZoomAt(_zoom * (e.Delta > 0 ? 1.25 : 1 / 1.25), e.GetPosition(OverlayCanvas));
        e.Handled = true;
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(1.25);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(1 / 1.25);

    /// <summary>Buttons zoom on the SELECTION when there is one — that is the thing being aimed,
    /// and it would otherwise slide out of the box on the second click.</summary>
    private void ZoomStep(double factor)
    {
        var disp = DisplayRect();
        if (disp.IsEmpty) return;
        var anchor = _rw > 0 && _rh > 0
            ? new System.Windows.Point(disp.X + (_rx + _rw / 2) * disp.Width,
                                       disp.Y + (_ry + _rh / 2) * disp.Height)
            : new System.Windows.Point(OverlayCanvas.ActualWidth / 2, OverlayCanvas.ActualHeight / 2);
        ZoomAt(_zoom * factor, anchor);
    }

    private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _zoom = 1;
        _panX = _panY = 0;
        RedrawOverlay();
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

    /// <summary>Same, but a point outside the picture is pulled back to its edge instead of being
    /// thrown away: a drag that leaves the frame has to keep sizing the rectangle, not freeze it.</summary>
    private System.Windows.Point? ToRelativeClamped(System.Windows.Point p)
    {
        var disp = DisplayRect();
        if (disp.IsEmpty || disp.Width <= 0 || disp.Height <= 0) return null;
        return new System.Windows.Point(Math.Clamp((p.X - disp.X) / disp.Width, 0, 1),
                                        Math.Clamp((p.Y - disp.Y) / disp.Height, 0, 1));
    }

    /// <summary>The selection in CANVAS pixels — where it is drawn, and therefore what the mouse
    /// is tested against.</summary>
    private Rect SelectionBox()
    {
        if (_rw <= 0 || _rh <= 0) return Rect.Empty;
        var disp = DisplayRect();
        if (disp.IsEmpty) return Rect.Empty;
        return new Rect(disp.X + _rx * disp.Width, disp.Y + _ry * disp.Height,
                        Math.Max(_rw * disp.Width, 1), Math.Max(_rh * disp.Height, 1));
    }

    /// <summary>Whether a canvas point is on the selection — the test that decides between moving
    /// the rectangle and starting a new one.</summary>
    private bool IsOnSelection(System.Windows.Point p)
    {
        var box = SelectionBox();
        return !box.IsEmpty && box.Contains(p);
    }

    /// <summary>Which sides of the selection a point is grabbing. The band is capped at a third of
    /// the side: a HUD bar is a handful of canvas pixels tall, and a fixed band would turn it into
    /// nothing but handles — a rectangle that can only be resized, never moved.</summary>
    private Side HitSide(System.Windows.Point p)
    {
        var box = SelectionBox();
        if (box.IsEmpty) return Side.None;

        double bx = Math.Min(GrabBand, Math.Max(box.Width / 3, 2));
        double by = Math.Min(GrabBand, Math.Max(box.Height / 3, 2));
        if (p.X < box.X - bx || p.X > box.Right + bx ||
            p.Y < box.Y - by || p.Y > box.Bottom + by) return Side.None;

        var side = Side.None;
        if (Math.Abs(p.X - box.X) <= bx) side |= Side.Left;
        else if (Math.Abs(p.X - box.Right) <= bx) side |= Side.Right;
        if (Math.Abs(p.Y - box.Y) <= by) side |= Side.Top;
        else if (Math.Abs(p.Y - box.Bottom) <= by) side |= Side.Bottom;
        return side;
    }

    /// <summary>The cursor that says which way a border will go.</summary>
    private static Cursor CursorFor(Side side) => side switch
    {
        Side.Left | Side.Top or Side.Right | Side.Bottom => Cursors.SizeNWSE,
        Side.Right | Side.Top or Side.Left | Side.Bottom => Cursors.SizeNESW,
        Side.Left or Side.Right => Cursors.SizeWE,
        Side.Top or Side.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeAll,
    };

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_frame is null) return;
        var at = e.GetPosition(OverlayCanvas);
        if (ToRelative(at) is not { } rel) return;

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

        // On the border the press RESIZES, inside it carries the rectangle, outside it draws a new
        // one: three gestures on one surface, told apart by the only thing the user can see — where
        // the rectangle is. Without the first, a rectangle two pixels too short had to be drawn
        // again from scratch.
        _resizeSide = HitSide(at);
        _resizeFrom = new Rect(_rx, _ry, _rw, _rh);
        _drag = _resizeSide != Side.None ? Drag.Resize
              : IsOnSelection(at) ? Drag.Move
              : Drag.New;
        // Aiming with the mouse and nudging with the arrows are one gesture, so the picture takes
        // the keyboard as soon as it is clicked.
        OverlayCanvas.Focus();
        _moveGrab = new System.Windows.Point(rel.X - _rx, rel.Y - _ry);
        _dragStart = rel;
        OverlayCanvas.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(OverlayCanvas);

        if (_panStart is { } from && e.RightButton == MouseButtonState.Pressed)
        {
            _panX += at.X - from.X;
            _panY += at.Y - from.Y;
            _panStart = at;
            RedrawOverlay();
            return;
        }

        if (_drag == Drag.None || e.LeftButton != MouseButtonState.Pressed)
        {
            // Nothing is being dragged: the cursor says what a press would do here.
            if (_frame is null) { OverlayCanvas.Cursor = Cursors.Cross; return; }
            var over = HitSide(at);
            OverlayCanvas.Cursor = over != Side.None ? CursorFor(over)
                                 : IsOnSelection(at) ? Cursors.SizeAll
                                 : Cursors.Cross;
            return;
        }

        if (ToRelativeClamped(at) is not { } rel) return;
        if (_drag == Drag.Resize) ApplyResize(rel);
        else if (_drag == Drag.Move) MoveTo(rel);
        else if (_dragStart is { } start) ApplyDrag(start, rel);
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag != Drag.None && _dragStart is { } start &&
            ToRelativeClamped(e.GetPosition(OverlayCanvas)) is { } rel)
        {
            if (_drag == Drag.Resize) ApplyResize(rel);
            else if (_drag == Drag.Move) MoveTo(rel);
            // A press that never really moved is a CLICK, and a click drops a ready-made box
            // centred on it: aiming at a minimap or a portrait is pointing AT it, not drawing a
            // rectangle around it by hand. The box is then dragged into place like any other.
            else if (IsClick(start, rel)) DropBoxAt(rel);
            else ApplyDrag(start, rel);
        }

        _drag = Drag.None;
        _dragStart = null;
        OverlayCanvas.ReleaseMouseCapture();
        RefreshReading();
    }

    /// <summary>A press and release at (very nearly) the same place. Measured in CANVAS pixels, so
    /// the threshold means the same thing to the hand at any zoom.</summary>
    private bool IsClick(System.Windows.Point start, System.Windows.Point end)
    {
        var disp = DisplayRect();
        if (disp.IsEmpty) return false;
        return Math.Abs((end.X - start.X) * disp.Width) < 4 &&
               Math.Abs((end.Y - start.Y) * disp.Height) < 4;
    }

    /// <summary>The box a click drops: square, a fixed share of the frame's short side, centred on
    /// the point that was clicked.</summary>
    private void DropBoxAt(System.Windows.Point rel)
    {
        if (_frame is null) return;
        double side = Math.Min(_frame.Width, _frame.Height) * ClickBoxShare;
        double w = side / _frame.Width, h = side / _frame.Height;
        SetRect(rel.X - w / 2, rel.Y - h / 2, w, h);
    }

    private void MoveTo(System.Windows.Point rel) =>
        SetRect(rel.X - _moveGrab.X, rel.Y - _moveGrab.Y, _rw, _rh);

    /// <summary>Drags the grabbed sides to the pointer, leaving the others where the gesture found
    /// them. A side dragged past its opposite keeps a two-pixel sliver instead of collapsing, so the
    /// gesture can always be dragged back out of.</summary>
    private void ApplyResize(System.Windows.Point rel)
    {
        if (_frame is null) return;

        double x0 = _resizeFrom.X, y0 = _resizeFrom.Y, x1 = _resizeFrom.Right, y1 = _resizeFrom.Bottom;
        double minW = 2.0 / _frame.Width, minH = 2.0 / _frame.Height;

        if (_resizeSide.HasFlag(Side.Left)) x0 = Math.Min(rel.X, x1 - minW);
        else if (_resizeSide.HasFlag(Side.Right)) x1 = Math.Max(rel.X, x0 + minW);
        if (_resizeSide.HasFlag(Side.Top)) y0 = Math.Min(rel.Y, y1 - minH);
        else if (_resizeSide.HasFlag(Side.Bottom)) y1 = Math.Max(rel.Y, y0 + minH);

        // The corner that is NOT moving is the anchor, which is what keeps a capture's square rule
        // (see SetRect) from shifting the sides the user never touched.
        var anchor = new System.Windows.Point(_resizeSide.HasFlag(Side.Left) ? x1 : x0,
                                              _resizeSide.HasFlag(Side.Top) ? y1 : y0);
        var toward = new System.Windows.Point(_resizeSide.HasFlag(Side.Left) ? x0 : x1,
                                              _resizeSide.HasFlag(Side.Top) ? y0 : y1);
        SetRect(x0, y0, x1 - x0, y1 - y0, anchor, toward);
    }

    private void Overlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_frame is null || _zoom <= MinZoom) return;   // fitted: there is nothing to pan to
        _panStart = e.GetPosition(OverlayCanvas);
        OverlayCanvas.Cursor = Cursors.ScrollAll;
        OverlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart is null) return;
        _panStart = null;
        OverlayCanvas.Cursor = Cursors.Cross;
        OverlayCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ApplyDrag(System.Windows.Point a, System.Windows.Point b) =>
        SetRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y),
                anchor: a, toward: b);

    /// <summary>The one door into the rectangle: every gesture goes through here, which is where
    /// the square rule and the "stay inside the frame" rule live — so no gesture can produce a
    /// rectangle another gesture could not.</summary>
    /// <param name="anchor">Corner a drag started from, held still while the square grows out of
    /// it; without it a square drag would creep away from the point the user pressed on.</param>
    private void SetRect(double x, double y, double w, double h,
                         System.Windows.Point? anchor = null, System.Windows.Point? toward = null)
    {
        if (SquareRect && _frame is not null && w > 0 && h > 0)
        {
            // Square in PIXELS, which is what gets cropped: the relative sides differ by the frame's
            // own aspect ratio, so 1:1 here would be a rectangle on screen.
            double side = Math.Min(Math.Max(w * _frame.Width, h * _frame.Height),
                                   Math.Min(_frame.Width, _frame.Height));
            double sw = side / _frame.Width, sh = side / _frame.Height;
            if (anchor is { } a && toward is { } t)
            {
                x = t.X >= a.X ? a.X : a.X - sw;
                y = t.Y >= a.Y ? a.Y : a.Y - sh;
            }
            else
            {
                x += (w - sw) / 2;
                y += (h - sh) / 2;
            }
            w = sw; h = sh;
        }

        w = Math.Clamp(w, 0, 1);
        h = Math.Clamp(h, 0, 1);
        _rx = Math.Clamp(x, 0, 1 - w);
        _ry = Math.Clamp(y, 0, 1 - h);
        _rw = w; _rh = h;
        RedrawOverlay();
    }

    /// <summary>Squares the rectangle that is already there, keeping its centre. Called when the
    /// mode becomes a capture and when the first frame arrives — a rectangle cannot be squared in
    /// pixels before there are pixels to square it against.</summary>
    private void SquareUpRect()
    {
        if (!SquareRect || _frame is null || _rw <= 0 || _rh <= 0) return;
        if (Math.Abs(_rw * _frame.Width - _rh * _frame.Height) < 0.5) return;
        SetRect(_rx, _ry, _rw, _rh);
    }

    /// <summary>Puts the selection rectangle back where the relative coordinates say, and refreshes
    /// everything derived from it (the view itself, the magnified crop, the reading).</summary>
    private void RedrawOverlay()
    {
        ClampPan();
        UpdateFrameTransform();

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
        RefreshSizeFields();
        RefreshPosFields();
        RefreshZoom();
        RefreshReading();
    }

    /// <summary>Blocks the size boxes from being read back while this code is the one writing
    /// them.</summary>
    private bool _writingSize;

    /// <summary>Puts the rectangle's size in PIXELS of the frame into the two boxes — the very
    /// pixels the probe will be measured on (<see cref="ScreenProbeReader.PixelRect"/>), so the
    /// number that is typed and the number that is read are the same number. The box the user is
    /// typing in is left alone.</summary>
    /// <param name="force">Writes the boxes even the one being typed in — what "apply this size"
    /// needs, so a number that got clamped is corrected under the cursor instead of at the moment
    /// the field is left.</param>
    private void RefreshSizeFields(bool force = false)
    {
        _writingSize = true;
        try
        {
            if (_frame is null)
            {
                TxtPxW.Text = TxtPxH.Text = "";
                return;
            }
            var box = ScreenProbeReader.PixelRect(_frame.Width, _frame.Height, BuildProbe());
            if (force || !TxtPxW.IsKeyboardFocusWithin) TxtPxW.Text = box.Width.ToString();
            if (force || !TxtPxH.IsKeyboardFocusWithin) TxtPxH.Text = box.Height.ToString();
        }
        finally { _writingSize = false; }
    }

    /// <summary>Resizes the rectangle to the typed pixel size, keeping its TOP-LEFT corner: a typed
    /// size must not slide the rectangle off the thing it was already aimed at. Nonsense in either
    /// box simply puts the real size back.</summary>
    /// <param name="edited">The box the user typed in, when there is one. It matters for a SQUARE
    /// rectangle (a capture): the side is whatever was just typed, and the other box follows —
    /// without it the square rule keeps the LARGER of the two, so typing a smaller number looked
    /// like it had been ignored.</param>
    private void ApplySizeFields(object? edited = null)
    {
        if (_loading || _writingSize || _frame is null) return;

        bool okW = int.TryParse((TxtPxW.Text ?? "").Trim(), out int w);
        bool okH = int.TryParse((TxtPxH.Text ?? "").Trim(), out int h);

        int? side = null;
        if (SquareRect)
        {
            // The box being typed in names the side; when the gesture says nothing (Enter with the
            // caret elsewhere, a field left) whichever box holds a number does.
            if (ReferenceEquals(edited, TxtPxH) && okH) side = h;
            else if (ReferenceEquals(edited, TxtPxW) && okW) side = w;
            else if (okW) side = w;
            else if (okH) side = h;

            if (side is { } sq) { w = sq; h = sq; okW = okH = true; }
        }

        if (!okW || !okH)
        {
            // Mid-edit nonsense (an empty box) is left alone; the real size comes back when the
            // field is left, which is the moment the user is done typing.
            if (!TxtPxW.IsKeyboardFocusWithin && !TxtPxH.IsKeyboardFocusWithin) RefreshSizeFields();
            return;
        }

        double rw = Math.Clamp(w, 1, _frame.Width) / (double)_frame.Width;
        double rh = Math.Clamp(h, 1, _frame.Height) / (double)_frame.Height;
        SetRect(_rx, _ry, rw, rh,
                anchor: new System.Windows.Point(_rx, _ry),
                toward: new System.Windows.Point(_rx + rw, _ry + rh));
    }

    private void SizeBox_LostFocus(object sender, RoutedEventArgs e) => ApplySizeFields(sender);

    private ProbeRef CurrentRefX() => (CbRefX.SelectedItem as RefItem)?.Ref ?? ProbeRef.Start;
    private ProbeRef CurrentRefY() => (CbRefY.SelectedItem as RefItem)?.Ref ?? ProbeRef.Start;

    private static void SelectRef(ComboBox cb, ProbeRef value) =>
        cb.SelectedItem = cb.Items.OfType<RefItem>().FirstOrDefault(r => r.Ref == value)
                          ?? cb.Items.OfType<RefItem>().FirstOrDefault();

    /// <summary>The rectangle's position on one axis, in FRAME pixels, measured from what the combo
    /// says: the near edge, the middle (signed — negative is left/up, and it is the rectangle's
    /// CENTRE that is measured, which is what "centred" has to mean), or the far edge.</summary>
    private static int PositionOf(ProbeRef reference, int start, int size, int frameSize) => reference switch
    {
        ProbeRef.Center => start + size / 2 - frameSize / 2,
        ProbeRef.End    => frameSize - (start + size),
        _               => start,
    };

    /// <summary>Inverse of <see cref="PositionOf"/>: the near edge a typed position asks for.</summary>
    private static double StartFor(ProbeRef reference, double position, double size, double frameSize) =>
        reference switch
        {
            ProbeRef.Center => frameSize / 2 + position - size / 2,
            ProbeRef.End    => frameSize - position - size,
            _               => position,
        };

    /// <summary>Writes the current position into the two boxes, in the unit the combos name. Same
    /// rule as the size boxes: the field being typed in is left alone.</summary>
    private void RefreshPosFields(bool force = false)
    {
        _writingSize = true;
        try
        {
            if (_frame is null)
            {
                TxtPosX.Text = TxtPosY.Text = "";
                return;
            }
            var box = ScreenProbeReader.PixelRect(_frame.Width, _frame.Height, BuildProbe());
            if (force || !TxtPosX.IsKeyboardFocusWithin)
                TxtPosX.Text = PositionOf(CurrentRefX(), box.X, box.Width, _frame.Width).ToString();
            if (force || !TxtPosY.IsKeyboardFocusWithin)
                TxtPosY.Text = PositionOf(CurrentRefY(), box.Y, box.Height, _frame.Height).ToString();
        }
        finally { _writingSize = false; }
    }

    /// <summary>Moves the rectangle to the typed position, keeping its SIZE — the size has its own
    /// two boxes, and a position that resized the rectangle would fight them.</summary>
    private void ApplyPosFields()
    {
        if (_loading || _writingSize || _frame is null) return;

        bool okX = int.TryParse((TxtPosX.Text ?? "").Trim(), out int px);
        bool okY = int.TryParse((TxtPosY.Text ?? "").Trim(), out int py);
        if (!okX || !okY)
        {
            if (!TxtPosX.IsKeyboardFocusWithin && !TxtPosY.IsKeyboardFocusWithin) RefreshPosFields();
            return;
        }

        double x = StartFor(CurrentRefX(), px, _rw * _frame.Width, _frame.Width) / _frame.Width;
        double y = StartFor(CurrentRefY(), py, _rh * _frame.Height, _frame.Height) / _frame.Height;
        SetRect(x, y, _rw, _rh);
    }

    private void PosBox_LostFocus(object sender, RoutedEventArgs e) => ApplyPosFields();

    private void PosBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyPosFields();

    private void PosBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyPosFields();
        RefreshPosFields(force: true);
        e.Handled = true;
    }

    /// <summary>Changing what a position is measured FROM does not move anything — it only changes
    /// the number that describes where the rectangle already is.</summary>
    private void RefBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshPosFields(force: true);
    }

    /// <summary>Arrow keys nudge the rectangle ONE frame pixel, Shift ten. This is the only way to
    /// land on an exact pixel on a 4K frame shown at 12%, where one canvas pixel is eight of the
    /// frame's. Skipped while a field has the keyboard: there the arrows belong to the caret.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_frame is null || _rw <= 0 || _rh <= 0) return;
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;

        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left:  dx = -1; break;
            case Key.Right: dx = 1; break;
            case Key.Up:    dy = -1; break;
            case Key.Down:  dy = 1; break;
            default: return;
        }

        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        SetRect(_rx + dx * step / (double)_frame.Width,
                _ry + dy * step / (double)_frame.Height, _rw, _rh);
        e.Handled = true;
    }

    /// <summary>Every keystroke moves the rectangle. A field that only applied on Enter reads as a
    /// field that does nothing — and half-typed nonsense is simply ignored here (rather than
    /// rewritten under the cursor), so clearing the box to type a new number is possible.</summary>
    private void SizeBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySizeFields(sender);

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplySizeFields(sender);
        RefreshSizeFields(force: true);
        // Enter in a size box is "this size", not "OK": the default button must not close the
        // window on the keystroke meant to apply a number.
        e.Handled = true;
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
        // Switching TO a capture squares whatever rectangle was drawn for the old mode, rather
        // than saving a 1:12 bar as the picture that goes on a square key.
        SquareUpRect();
        RefreshReading();
    }

    private void RefreshModePanels()
    {
        var mode = CurrentMode();
        bool ocr = mode is ProbeMode.Number or ProbeMode.Text;
        // A capture measures nothing, so everything that describes HOW to measure goes away and
        // only the rectangle is left — which is the whole of what a capture is.
        bool capture = mode == ProbeMode.Capture;

        PnlDir.Visibility = mode == ProbeMode.Edge ? Visibility.Visible : Visibility.Collapsed;

        // Only an OCR probe gets the choice: for the pixel-counting modes the reference colour IS
        // the measurement, so there is nothing to read "any" of.
        PnlAnyText.Visibility = ocr ? Visibility.Visible : Visibility.Collapsed;
        bool anyText = ocr && ChkAnyText.IsChecked == true;
        PnlColor.Visibility = PnlTolerance.Visibility =
            capture || anyText ? Visibility.Collapsed : Visibility.Visible;
        // An armed eyedropper behind a hidden panel would still eat the next click on the frame.
        if (PnlColor.Visibility != Visibility.Visible) TglPick.IsChecked = false;
        // An OCR probe keeps the colour and the tolerance — they pick out the TEXT's own colour so
        // the game's artwork behind it is thrown away before recognition (tolerance at maximum =
        // no filter). "Invert" is the one control with nothing to say about text.
        ChkInvert.Visibility = ocr || capture ? Visibility.Collapsed : Visibility.Visible;
        LblColorCaption.Text = Loc.Get(ocr ? "screen_probe_text_color" : "screen_probe_color");

        // The line under the big reading says what that reading IS, so it follows the mode too.
        LblReadingHint.Text = Loc.Get(capture ? "screen_probe_reading_capture_hint"
                                              : "screen_probe_reading_hint");

        LblModeHint.Text = Loc.Get(mode switch
        {
            ProbeMode.Edge   => "screen_probe_mode_edge_hint",
            ProbeMode.Color  => "screen_probe_mode_color_hint",
            ProbeMode.Number => ScreenTextReader.Available
                                ? "screen_probe_mode_number_hint" : "screen_probe_ocr_missing",
            ProbeMode.Text   => ScreenTextReader.Available
                                ? "screen_probe_mode_text_hint" : "screen_probe_ocr_missing",
            ProbeMode.Capture => "screen_probe_mode_capture_hint",
            _                => "screen_probe_mode_fill_hint",
        });
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshReading();
    }

    /// <summary>"Read any text" also decides whether the colour and the tolerance are on screen at
    /// all — leaving up a swatch nothing is filtered by would be a control that lies.</summary>
    private void ChkAnyText_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshModePanels();
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
        AnyText = ChkAnyText.IsChecked == true,
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

        // A capture has no value to show. What it CAN show is the size of what it will copy, which
        // is the one number that tells the user whether the rectangle is worth putting on a key.
        if (probe.Mode == ProbeMode.Capture)
        {
            var box = ScreenProbeReader.PixelRect(_frame.Width, _frame.Height, probe);
            LblReading.Text = box.Width > 0 && box.Height > 0 ? $"{box.Width}×{box.Height}" : "—";
            return;
        }

        var reading = ScreenProbeReader.Measure(_frame, probe);
        LblReading.Text = !reading.Valid
            ? "—"
            : probe.Mode is ProbeMode.Number or ProbeMode.Text
                // What the engine actually read, verbatim: seeing "8S" instead of "85" is the
                // whole point of a preview on a real frame.
                ? reading.Text
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
        // What the NEXT probe starts from: same game, same size, same way of saying where it is.
        var saved = _frame is null
            ? new System.Drawing.Rectangle()
            : ScreenProbeReader.PixelRect(_frame.Width, _frame.Height, probe);
        ScreenProbeDefaults.Remember(probe.Process, saved.Width, saved.Height,
                                     CurrentRefX(), CurrentRefY(), _profileKey);
        App.WriteLog($"[PROBE] saved \"{probe.Name}\" ({probe.Id}) on {probe.Process}: " +
                     $"{probe.Mode} rect=({probe.X:F3},{probe.Y:F3},{probe.W:F3},{probe.H:F3}) " +
                     $"colour=#{probe.Color:X6} tol={probe.Tolerance} any={probe.AnyText}");
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
