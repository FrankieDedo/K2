using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Embeddable pan/zoom "cover crop" editor — the reusable viewport UI extracted from the
/// original <see cref="ImageCropDialog"/> popup (2026-07-05) so a host dialog can drop it
/// directly into its OWN layout, right next to the load/rotate controls, instead of opening
/// a separate modal window (<c>DpKeyConfigDialog</c>, DisplayPad fullscreen dialog). Everest
/// NDK still goes through <see cref="ImageCropDialog.Show"/>, which now hosts one of these
/// internally — same visuals, no behavior change there.
///
/// <para>
/// UX: a fixed-size viewport (matching the TARGET aspect ratio) shows the source image at
/// "cover" scale by default (viewport always fully filled, no letterboxing) — drag to
/// reposition, scroll/slider to zoom further. A "no crop/zoom" checkbox switches to a plain
/// stretch-to-fit preview (the pre-crop-feature behavior — whatever the target aspect,
/// distortion and all) so the user can compare before committing. A "show key outline"
/// checkbox overlays either a single rounded-corner hint (1×1 — see <see cref="SetKeyGrid"/>)
/// or an evenly-divided N×M grid on top of the preview, purely as a visual guide to where
/// the physical key boundaries fall (approximate — not hardware-measured bezel positions).
/// </para>
///
/// <para>
/// <b>Animated GIFs (2026-07-05)</b>: when constructed with <c>animateGifs: true</c>, a GIF
/// source is decoded and CYCLED live inside the same pannable/zoomable viewport (all frames
/// share one dimensions, so one crop rectangle applies to every frame identically), and
/// <see cref="GetResultPath"/> returns a <see cref="CroppedGifRef"/> sidecar path instead of
/// a baked PNG (a GIF can't be crop-baked into a single new GIF file — no multi-frame GIF
/// ENCODER in <c>System.Drawing</c>, only a decoder). Defaults to <c>false</c> — the Everest
/// NDK flow (via <see cref="ImageCropDialog"/>) deliberately keeps the OLD behavior of
/// treating a picked GIF as a plain static image (frame 0 only, baked to PNG), because NDK
/// has no per-frame animation loop to consume a <see cref="CroppedGifRef"/> anyway.
/// </para>
/// </summary>
internal sealed class CropEditor
{
    private int _targetW, _targetH;
    private string? _sourcePath;
    private byte[]? _fileBytes;
    private int _srcW, _srcH;
    private readonly bool _animateGifs;
    private bool _isGif;
    private List<(BitmapSource Frame, int DelayMs)>? _gifFrames;
    private DispatcherTimer? _gifTimer;
    private int _gifIdx;

    private double _scale, _tx, _ty, _minScale, _maxScale;
    private Point? _dragStart;
    private double _dragStartTx, _dragStartTy;

    private int _gridRows = 1, _gridCols = 1;

    // Real-world key geometry (15×15mm keys, 3mm gap between them) — used to size the
    // "show key outline" overlay's cells + gaps proportionally instead of an even
    // edge-to-edge grid.
    private const double KeyMm = 15, GapMm = 3;

    private readonly Image _img = new() { Stretch = Stretch.Fill };
    private readonly Canvas _gridOverlay = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Canvas _viewport;
    private readonly Border _viewportBorder;
    private readonly Slider _zoomSlider;
    private readonly CheckBox _chkNoCrop;
    private readonly CheckBox _chkShowGrid;
    private readonly TextBlock _hint;
    private readonly StackPanel _controlsPanel;

    private readonly double _maxViewportPx;

    /// <summary>The pannable/zoomable image viewport ONLY — host dialogs that also preview
    /// device rotation should attach a <see cref="RotateTransform"/> to THIS element's
    /// <see cref="FrameworkElement.LayoutTransform"/>, not to <see cref="ControlsPanel"/>
    /// (rotating the slider/checkbox along with the picture would be wrong).</summary>
    public Border ViewportBorder => _viewportBorder;

    /// <summary>Zoom slider + "no crop" checkbox + "show key outline" checkbox + hint text,
    /// stacked — arrange this underneath <see cref="ViewportBorder"/> in the host's own
    /// layout.</summary>
    public FrameworkElement ControlsPanel => _controlsPanel;

    /// <summary>True once <see cref="Load"/> has succeeded and hasn't been cleared since.</summary>
    public bool HasImage => _sourcePath is not null;

    /// <param name="targetW">Crop output width in pixels.</param>
    /// <param name="targetH">Crop output height in pixels.</param>
    /// <param name="maxViewportPx">Cap on the on-screen viewport's longer side — callers
    /// embedding this inline (vs. the old standalone popup) typically want something smaller
    /// than the popup's original 440px so it fits next to their other controls.</param>
    /// <param name="animateGifs">If true, an animated GIF source is decoded and played live
    /// in the viewport, and <see cref="GetResultPath"/> emits a <see cref="CroppedGifRef"/>
    /// sidecar for it instead of baking a static PNG. Leave false for hosts with no
    /// per-frame GIF playback to consume that sidecar (see class remarks).</param>
    public CropEditor(int targetW, int targetH, double maxViewportPx = 260, bool animateGifs = false)
    {
        _targetW = targetW;
        _targetH = targetH;
        _maxViewportPx = maxViewportPx;
        _animateGifs = animateGifs;

        _viewport = new Canvas { ClipToBounds = true, Background = Brushes.Black, Cursor = Cursors.SizeAll };
        _viewport.Children.Add(_img);
        _viewport.Children.Add(_gridOverlay);   // added after _img -> renders on top
        _viewportBorder = new Border
        {
            Child = _viewport,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _zoomSlider = new Slider { Margin = new Thickness(0, 6, 0, 0) };
        _zoomSlider.ValueChanged += ZoomSlider_ValueChanged;

        _chkNoCrop = new CheckBox
        {
            Content = Loc.Get("crop_no_crop"),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _chkNoCrop.Checked += (_, _) => UpdateMode();
        _chkNoCrop.Unchecked += (_, _) => UpdateMode();

        _chkShowGrid = new CheckBox
        {
            Content = Loc.Get("crop_show_grid"),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _chkShowGrid.Checked += (_, _) => _gridOverlay.Visibility = Visibility.Visible;
        _chkShowGrid.Unchecked += (_, _) => _gridOverlay.Visibility = Visibility.Collapsed;

        _hint = new TextBlock
        {
            Text = Loc.Get("crop_hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseLeftButtonUp += (_, _) => { _dragStart = null; _viewport.ReleaseMouseCapture(); };
        _viewport.MouseWheel += Viewport_MouseWheel;

        _controlsPanel = new StackPanel();
        _controlsPanel.Children.Add(_zoomSlider);
        _controlsPanel.Children.Add(_chkNoCrop);
        _controlsPanel.Children.Add(_chkShowGrid);
        _controlsPanel.Children.Add(_hint);

        ResizeViewport();
    }

    /// <summary>Sets the key-outline overlay's grid: 1×1 (default) draws a single
    /// rounded-corner hint (approximating the physical key's rounded bezel clip); anything
    /// larger draws an evenly-divided rows×cols grid (e.g. 2×6 for the DisplayPad fullscreen
    /// dialog). Purely a visual guide, not hardware-measured bezel geometry.</summary>
    public void SetKeyGrid(int rows, int cols)
    {
        _gridRows = Math.Max(1, rows);
        _gridCols = Math.Max(1, cols);
        RebuildGridOverlay();
    }

    private void ResizeViewport()
    {
        double aspect = (double)_targetW / _targetH;
        double vw = _maxViewportPx, vh = _maxViewportPx / aspect;
        if (vh > _maxViewportPx) { vh = _maxViewportPx; vw = _maxViewportPx * aspect; }
        _viewport.Width = vw;
        _viewport.Height = vh;
        _viewportBorder.Width = vw;
        _viewportBorder.Height = vh;
        _zoomSlider.Width = vw;
        _gridOverlay.Width = vw;
        _gridOverlay.Height = vh;
        RebuildGridOverlay();
    }

    private void RebuildGridOverlay()
    {
        _gridOverlay.Children.Clear();
        double vw = _viewport.Width, vh = _viewport.Height;
        if (vw <= 0 || vh <= 0) return;
        var brush = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xC1, 0x07));   // amber, high-contrast
        const double thickness = 1.5;

        // Each key is drawn as its own rounded-rect, with a real gap between adjacent
        // ones sized to the 14×14mm key / 4mm gap ratio (rather than an even edge-to-edge
        // grid) — also covers the 1×1 case (no gap to draw, cell fills the viewport).
        double totalUnitsW = _gridCols * KeyMm + (_gridCols - 1) * GapMm;
        double totalUnitsH = _gridRows * KeyMm + (_gridRows - 1) * GapMm;
        double cellW = vw * KeyMm / totalUnitsW, gapW = vw * GapMm / totalUnitsW;
        double cellH = vh * KeyMm / totalUnitsH, gapH = vh * GapMm / totalUnitsH;
        double radius = Math.Min(cellW, cellH) * 0.12;

        for (int r = 0; r < _gridRows; r++)
        {
            for (int c = 0; c < _gridCols; c++)
            {
                double x = c * (cellW + gapW), y = r * (cellH + gapH);
                var rect = new Rectangle
                {
                    Width = Math.Max(0, cellW - thickness), Height = Math.Max(0, cellH - thickness),
                    RadiusX = radius, RadiusY = radius,
                    Stroke = brush, StrokeThickness = thickness, Fill = Brushes.Transparent,
                };
                Canvas.SetLeft(rect, x + thickness / 2);
                Canvas.SetTop(rect, y + thickness / 2);
                _gridOverlay.Children.Add(rect);
            }
        }
    }

    /// <summary>Changes the crop target aspect (the DisplayPad fullscreen dialog needs this:
    /// target depends on the CURRENT device rotation) and re-fits the current source, if any.</summary>
    public void SetTargetSize(int targetW, int targetH)
    {
        if (targetW == _targetW && targetH == _targetH) return;
        _targetW = targetW; _targetH = targetH;
        ResizeViewport();
        if (_sourcePath is not null) FitCover();
    }

    /// <summary>Loads a new source image — animated GIF (only if constructed with
    /// <c>animateGifs: true</c>) or static. Resets pan/zoom to "cover" and unchecks "no
    /// crop". Returns false (and clears any previous image) if the file can't be read.</summary>
    public bool Load(string sourcePath)
    {
        StopGifTimer();
        _isGif = false;
        _gifFrames = null;

        if (_animateGifs && DpGifAnimator.IsAnimatedGif(sourcePath))
        {
            var frames = DecodeGifFrames(sourcePath);
            if (frames is { Count: > 1 })
            {
                _sourcePath = sourcePath;
                _fileBytes = null;
                _isGif = true;
                _gifFrames = frames;
                _srcW = frames[0].Frame.PixelWidth;
                _srcH = frames[0].Frame.PixelHeight;
                _gifIdx = 0;
                _img.Source = frames[0].Frame;
                ScheduleGifTick();

                _chkNoCrop.IsChecked = false;
                FitCover();
                return true;
            }
            // Decode failed (corrupt file, single real frame, etc.) — fall through to the
            // plain static path below, same as a non-GIF file.
        }

        byte[] bytes;
        int w, h;
        try
        {
            bytes = File.ReadAllBytes(sourcePath);
            using var bmp = new System.Drawing.Bitmap(new MemoryStream(bytes));
            w = bmp.Width; h = bmp.Height;
        }
        catch { Clear(); return false; }
        if (w <= 0 || h <= 0) { Clear(); return false; }

        _sourcePath = sourcePath;
        _fileBytes = bytes;
        _srcW = w; _srcH = h;

        var wpfSrc = new BitmapImage();
        wpfSrc.BeginInit();
        wpfSrc.CacheOption = BitmapCacheOption.OnLoad;
        wpfSrc.StreamSource = new MemoryStream(bytes);
        wpfSrc.EndInit();
        wpfSrc.Freeze();
        _img.Source = wpfSrc;

        _chkNoCrop.IsChecked = false;   // fresh image always starts in normal crop mode
        FitCover();
        return true;
    }

    public void Clear()
    {
        StopGifTimer();
        _sourcePath = null; _fileBytes = null;
        _isGif = false; _gifFrames = null;
        _img.Source = null;
    }

    private void ScheduleGifTick()
    {
        if (_gifFrames is null) return;
        _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_gifFrames[_gifIdx].DelayMs) };
        _gifTimer.Tick += GifTimer_Tick;
        _gifTimer.Start();
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        if (_gifFrames is null) return;
        _gifIdx = (_gifIdx + 1) % _gifFrames.Count;
        _img.Source = _gifFrames[_gifIdx].Frame;
        if (_gifTimer is not null) _gifTimer.Interval = TimeSpan.FromMilliseconds(_gifFrames[_gifIdx].DelayMs);
    }

    private void StopGifTimer()
    {
        if (_gifTimer is null) return;
        _gifTimer.Stop();
        _gifTimer.Tick -= GifTimer_Tick;
        _gifTimer = null;
    }

    /// <summary>Decodes every GIF frame to a frozen <see cref="BitmapSource"/> + its delay in
    /// ms — same PropertyTagFrameDelay=0x5100 convention as <c>DpGifAnimator</c>. Returns
    /// null for anything that isn't a readable multi-frame GIF.</summary>
    private static List<(BitmapSource Frame, int DelayMs)>? DecodeGifFrames(string path)
    {
        const int propertyTagFrameDelay = 0x5100;
        try
        {
            using var img = System.Drawing.Image.FromFile(path);
            if (img.FrameDimensionsList.Length == 0) return null;
            var dim = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
            int count = img.GetFrameCount(dim);
            if (count < 2) return null;

            int[] delays = new int[count];
            try
            {
                var prop = img.GetPropertyItem(propertyTagFrameDelay);
                if (prop?.Value is not { } bytes) throw new InvalidOperationException("no delay data");
                for (int i = 0; i < count; i++) delays[i] = BitConverter.ToInt32(bytes, i * 4);
            }
            catch { for (int i = 0; i < count; i++) delays[i] = 10; }

            var result = new List<(BitmapSource, int)>(count);
            for (int i = 0; i < count; i++)
            {
                img.SelectActiveFrame(dim, i);
                using var frameBmp = new System.Drawing.Bitmap(img.Width, img.Height);
                using (var g = System.Drawing.Graphics.FromImage(frameBmp))
                    g.DrawImage(img, 0, 0, img.Width, img.Height);

                using var ms = new MemoryStream();
                frameBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var bmpSrc = new BitmapImage();
                bmpSrc.BeginInit();
                bmpSrc.CacheOption = BitmapCacheOption.OnLoad;
                bmpSrc.StreamSource = ms;
                bmpSrc.EndInit();
                bmpSrc.Freeze();

                int delayMs = Math.Max(20, delays[i] * 10);
                result.Add((bmpSrc, delayMs));
            }
            return result;
        }
        catch { return null; }
    }

    private void FitCover()
    {
        double vw = _viewport.Width, vh = _viewport.Height;
        _minScale = Math.Max(vw / _srcW, vh / _srcH);
        _maxScale = _minScale * 4;
        _scale = _minScale;
        _tx = (vw - _srcW * _scale) / 2;
        _ty = (vh - _srcH * _scale) / 2;
        _zoomSlider.Minimum = _minScale;
        _zoomSlider.Maximum = _maxScale;
        _zoomSlider.Value = _scale;
        UpdateMode();
    }

    private void UpdateMode()
    {
        if (_sourcePath is null) return;
        bool noCrop = _chkNoCrop.IsChecked == true;
        _zoomSlider.IsEnabled = !noCrop;
        _viewport.Cursor = noCrop ? Cursors.Arrow : Cursors.SizeAll;

        if (noCrop)
        {
            // Exactly what a plain stretch-to-fit looks like (pre-crop-feature behavior) —
            // fills the whole viewport, distorted if the source aspect differs.
            _img.Width = _viewport.Width;
            _img.Height = _viewport.Height;
            Canvas.SetLeft(_img, 0);
            Canvas.SetTop(_img, 0);
        }
        else
        {
            Layout();
        }
    }

    private void Layout()
    {
        _img.Width = _srcW * _scale;
        _img.Height = _srcH * _scale;
        Canvas.SetLeft(_img, _tx);
        Canvas.SetTop(_img, _ty);
    }

    private void ClampTranslate()
    {
        _tx = Math.Clamp(_tx, _viewport.Width - _srcW * _scale, 0);
        _ty = Math.Clamp(_ty, _viewport.Height - _srcH * _scale, 0);
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_chkNoCrop.IsChecked == true || _sourcePath is null) return;
        _dragStart = e.GetPosition(_viewport);
        _dragStartTx = _tx; _dragStartTy = _ty;
        _viewport.CaptureMouse();
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var p = e.GetPosition(_viewport);
        _tx = _dragStartTx + (p.X - start.X);
        _ty = _dragStartTy + (p.Y - start.Y);
        ClampTranslate();
        Layout();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_chkNoCrop.IsChecked == true || _sourcePath is null) return;
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        _zoomSlider.Value = Math.Clamp(_scale * factor, _minScale, _maxScale);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sourcePath is null) return;
        double vw = _viewport.Width, vh = _viewport.Height;
        double cx = (vw / 2 - _tx) / _scale, cy = (vh / 2 - _ty) / _scale;
        _scale = e.NewValue;
        _tx = vw / 2 - cx * _scale;
        _ty = vh / 2 - cy * _scale;
        ClampTranslate();
        Layout();
    }

    /// <summary>
    /// Computes the final image — cropped+resized normally, or a plain stretch if "no crop"
    /// is checked — caches it to <c>%LOCALAPPDATA%\K2\cropped\</c> and returns its path. For
    /// an animated GIF (only possible when constructed with <c>animateGifs: true</c>),
    /// returns a <see cref="CroppedGifRef"/> sidecar path instead (see class remarks).
    /// Returns null if no source is currently loaded.
    /// </summary>
    public string? GetResultPath()
    {
        if (_sourcePath is null) return null;
        if (_isGif) return GetGifCropRefPath();
        if (_fileBytes is null) return null;
        using var srcBmp = new System.Drawing.Bitmap(new MemoryStream(_fileBytes));

        bool noCrop = _chkNoCrop.IsChecked == true;
        System.Drawing.RectangleF rect;
        if (noCrop)
        {
            rect = new System.Drawing.RectangleF(0, 0, _srcW, _srcH);
        }
        else
        {
            double srcX = -_tx / _scale, srcY = -_ty / _scale;
            double cropW = _viewport.Width / _scale, cropH = _viewport.Height / _scale;
            rect = System.Drawing.RectangleF.Intersect(
                new System.Drawing.RectangleF((float)srcX, (float)srcY, (float)cropW, (float)cropH),
                new System.Drawing.RectangleF(0, 0, _srcW, _srcH));
            if (rect.Width <= 0 || rect.Height <= 0)
                rect = new System.Drawing.RectangleF(0, 0, _srcW, _srcH);
        }

        using var result = new System.Drawing.Bitmap(_targetW, _targetH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(result))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(srcBmp, new System.Drawing.Rectangle(0, 0, _targetW, _targetH),
                        rect.X, rect.Y, rect.Width, rect.Height, System.Drawing.GraphicsUnit.Pixel);
        }

        string cacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2", "cropped");
        Directory.CreateDirectory(cacheDir);
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(_sourcePath).Ticks; } catch { }
        string key = $"{_sourcePath}|{mtime}|{_targetW}x{_targetH}|" +
                     (noCrop ? "nocrop" : $"{rect.X:F1},{rect.Y:F1},{rect.Width:F1},{rect.Height:F1}");
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".png";
        string outPath = System.IO.Path.Combine(cacheDir, name);
        result.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        return outPath;
    }

    /// <summary>GIF equivalent of the static crop-bake above — since a GIF can't be
    /// crop-baked into one new GIF file, this just records the crop rectangle (in the
    /// original source's pixel coordinates, same for every frame) as a
    /// <see cref="CroppedGifRef"/> sidecar and returns ITS path.</summary>
    private string? GetGifCropRefPath()
    {
        if (_sourcePath is null) return null;
        bool noCrop = _chkNoCrop.IsChecked == true;
        CroppedGifRef cref;
        if (noCrop)
        {
            cref = new CroppedGifRef(_sourcePath, true, 0, 0, _srcW, _srcH);
        }
        else
        {
            double srcX = -_tx / _scale, srcY = -_ty / _scale;
            double cropW = _viewport.Width / _scale, cropH = _viewport.Height / _scale;
            var rect = System.Drawing.RectangleF.Intersect(
                new System.Drawing.RectangleF((float)srcX, (float)srcY, (float)cropW, (float)cropH),
                new System.Drawing.RectangleF(0, 0, _srcW, _srcH));
            if (rect.Width <= 0 || rect.Height <= 0)
                rect = new System.Drawing.RectangleF(0, 0, _srcW, _srcH);
            cref = new CroppedGifRef(_sourcePath, false, rect.X, rect.Y, rect.Width, rect.Height);
        }

        string cacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2", "cropped");
        return cref.Save(cacheDir);
    }
}
