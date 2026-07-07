using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// MainWindow partial: integrated DisplayPad tab.
///
/// Communicates with the hardware through an <see cref="IDisplayPadClient"/> backend:
/// either the x64 satellite process (SDK path, JSON named pipe) or the experimental
/// native raw USB-HID engine (<see cref="DisplayPadNativeClient"/>, no SDK — protocol
/// from BaseCampLinux), selected at startup via <c>AppSettings.DisplayPadNativeEngine</c>.
/// The graphic overlay replicates the MacroPad tab style:
/// Canvas with device background (mkd_bg.png, same graphic as MacroPad) and
/// 12 interactive buttons overlaid in a 2×6 grid.
/// </summary>
public partial class MainWindow
{
    // ---- DisplayPad backend (satellite SDK or native USB) ----
    private readonly IDisplayPadClient _dpClient = AppSettings.DisplayPadNativeEngine
        ? new DisplayPadNativeClient()
        : new DisplayPadSatelliteClient();
    private readonly DisplayPadStore _dpStore = new();

    // ---- Key model ----
    internal readonly DisplayPadKey[] _dpKeys = Enumerable.Range(0, 12)
        .Select(i => new DisplayPadKey(i)).ToArray();
    private readonly Button[] _dpButtons = new Button[12];
    private readonly ObservableCollection<DpDeviceRow> _dpDevices = new();
    private readonly ObservableCollection<int> _dpDeviceIds = new();
    /// <summary>Maps SDK ID → progressive label ("DisplayPad 1", "DisplayPad 2"…).</summary>
    private readonly Dictionary<int, string> _dpDeviceLabels = new();
    private readonly Dictionary<int, int> _dpMatrixToIndex = new();
    private int _dpMapAwaitingIndex = -1;
    private bool _dpSuppressProfile;
    private bool _dpSuppressBrightness;
    private bool _dpSuppressRotation;
    private int _dpRotation; // 0, 90, 270

    // ---- Folder / sub-page navigation ----
    private int _currentDpPageId = 0;
    private string? _currentDpFolderName = null;
    private readonly Stack<(int PageId, string? Name)> _dpPageHistory = new();

    // ---- Default key map (same as K2.DisplayPad) ----
    private static readonly (int Index, int Matrix)[] DpDefaultKeyMap =
    {
        (0,  0x08), (1,  0x11), (2,  0x1A), (3,  0x23),
        (4,  0x2C), (5,  0x35), (6,  0x3E), (7,  0x47),
        (8,  0x50), (9,  0x59), (10, 0x62), (11, 0x7D),
    };

    // ---- Canvas layout (mkd_bg.png coordinates at 510×370, same graphic as MacroPad) ----
    private const double DpKeyW = 60;   // BC: 60×60
    private const double DpKeyH = 60;
    private const double DpGapH = 8;
    private const double DpGapV = 10;

    // ================================================================
    // Initialization (called from MainWindow constructor)
    // ================================================================

    private void InitDisplayPadModule()
    {
        // Create the 12 overlay buttons using DpKeyButtonStyle (defined in MainWindow.xaml).
        // The style contains the full ControlTemplate: key_button.png background, rounded
        // icon clip, glossy overlay, hover/selection border — a faithful replica of Base Camp.
        // Button.Content = only the TextBlock (label), so the counter-rotate for rotation
        // operates directly on it without having to walk the visual tree.
        var dpKeyStyle = (Style)FindResource("DpKeyButtonStyle");

        for (int i = 0; i < 12; i++)
        {
            var key = _dpKeys[i];

            var label = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(DisplayPadKey.Display)));

            var btn = new Button
            {
                DataContext = key,
                Tag = key,
                Content = label,
                Style = dpKeyStyle,
                ContextMenu = BuildDpKeyContextMenu(),
            };
            btn.Click += DpKeyButton_Click;
            _dpButtons[i] = btn;
        }

        DpRebuildKeyGrid();
        DpApplyDefaultKeyMap();

        LvDpDevices.ItemsSource = _dpDevices;
        // DP device tabs are added to TcDevices by DpRefreshDevices; CbDpProfile by DpRefreshProfiles

        _dpSuppressRotation = true;
        CbDpRotation.ItemsSource = new[]
        {
            Loc.Get("pos_horizontal", 0),
            Loc.Get("pos_vertical", 90),
            Loc.Get("pos_horizontal", 180),
            Loc.Get("pos_vertical", 270),
        };
        CbDpRotation.SelectedIndex = 0;
        _dpSuppressRotation = false;

        // IPC events
        _dpClient.KeyEvent += OnDpKey;
        _dpClient.PlugEvent += OnDpPlug;
        _dpClient.ProgressEvent += OnDpProgress;
        _dpClient.SatelliteLog += (_, msg) => Dispatcher.BeginInvoke(() => DpLog(msg));

        InitDpSectionNav();
    }

    // ================================================================
    // Overlay: grid construction
    // ================================================================

    private void DpRebuildKeyGrid()
    {
        CvsDpKeys.Children.Clear();

        // Always physical 2×6 layout — rotation is handled by LayoutTransform
        const int rows = 2, cols = 6;
        double totalW = cols * DpKeyW + (cols - 1) * DpGapH;
        double totalH = rows * DpKeyH + (rows - 1) * DpGapV;

        // "Screen" area in mkd_bg.png (same as MacroPad)
        double areaLeft = 55, areaRight = 455, areaTop = 130, areaBottom = 330;
        double areaW = areaRight - areaLeft;
        double areaH = areaBottom - areaTop;
        double startX = areaLeft + (areaW - totalW) / 2;
        double startY = areaTop  + (areaH - totalH) / 2;

        for (int i = 0; i < 12; i++)
        {
            int r = i / cols;
            int c = i % cols;
            double x = startX + c * (DpKeyW + DpGapH);
            double y = startY + r * (DpKeyH + DpGapV);
            var btn = _dpButtons[i];
            Canvas.SetLeft(btn, x);
            Canvas.SetTop(btn, y);
            CvsDpKeys.Children.Add(btn);
        }

        // LayoutTransform rotates background + keys together
        CvsDpKeys.LayoutTransform = _dpRotation == 0
            ? Transform.Identity
            : new RotateTransform(_dpRotation);

        // Counter-rotate the label inside each key (Button.Content = TextBlock directly)
        var labelTransform = _dpRotation == 0
            ? Transform.Identity
            : new RotateTransform(-_dpRotation);
        foreach (var btn in _dpButtons)
        {
            if (btn.Content is TextBlock lbl)
                lbl.LayoutTransform = labelTransform;

            // Counter-rotate the user icon too: the device receives pixels that are
            // already counter-rotated (see DpHidNative/DisplayPadNativeClient.LoadBgr), so
            // for it to appear in K2 the way it will physically look on the rotated pad,
            // the same LayoutTransform applied to the Canvas needs to be undone here. Before,
            // no transform was applied to the icon (only to the label) → in the UI the icon
            // stayed at 0° relative to the source image instead of mirroring the
            // counter-rotation already in effect on the device.
            btn.ApplyTemplate();
            if (btn.Template?.FindName("ImgIcon", btn) is Image icon)
                icon.LayoutTransform = labelTransform;
        }
    }

    // (DpPhysicalForVisual removed: rotation is handled by LayoutTransform on the Canvas)

    // ================================================================
    // Toolbar
    // ================================================================

    private void BtnDpOpen_Click(object sender, RoutedEventArgs e) => DpOpenDriver();

    internal void DpOpenDriver()
    {
        if (!_dpClient.IsConnected)
        {
            DpLog("Starting DisplayPad satellite...");
            if (!_dpClient.Connect())
            {
                LblStatus.Text = Loc.Get("dp_satellite_failed");
                DpLog("Satellite not reachable — skipping");
                return;
            }
        }
        DpLog($"SDK version: {_dpClient.SdkVersion()}");
        LblDpSdk.Text = $"DisplayPadSDK (satellite x64)";

        var result = _dpClient.Open();
        bool ok = result?.GetBool("ok") ?? false;
        LblStatus.Text = ok ? Loc.Get("dp_driver_opened") : Loc.Get("dp_driver_open_failed");
        DpLog($"Open -> {ok}");
        if (ok) DpRefreshDevices();
    }

    private void BtnDpRefresh_Click(object sender, RoutedEventArgs e) => DpRefreshDevices();

    private void BtnDpClose_Click(object sender, RoutedEventArgs e)
    {
        _dpClient.Close();
        _dpDevices.Clear();
        _dpDeviceIds.Clear();
        RemoveDeviceTabs("dp_");
        _activeDpDeviceId = null;
        LblStatus.Text = Loc.Get("dp_driver_closed");
        DpLog("Close");
    }

    private void BtnDpRename_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        string current = _dpDeviceLabels.GetValueOrDefault(id, $"DisplayPad {id}");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        // Update in-memory label
        _dpDeviceLabels[id] = name;
        // Update tab header
        var tab = TcDevices.Items.OfType<TabItem>()
                      .FirstOrDefault(t => (t.Tag as string) == $"dp_{id}");
        if (tab != null) tab.Header = name;
        // Persist
        _dpStore.SetSetting($"device.{id}.name", name);
        DpLog($"[UI] Device {id} renamed to \"{name}\"");
    }

    private void BtnDpRotateCcw_Click(object sender, RoutedEventArgs e) => DpRotateAllIcons(270);
    private void BtnDpRotateCw_Click(object sender, RoutedEventArgs e)  => DpRotateAllIcons(90);

    /// <summary>
    /// Rotates all icons of the current profile by <paramref name="degrees"/> degrees (90 = CW, 270 = CCW).
    /// Saves the rotated images to the same cache as DpKeyConfigDialog (per-content-hash),
    /// updates the DB and re-uploads to the device.
    /// </summary>
    private void DpRotateAllIcons(int degrees)
    {
        if (DpSelectedDeviceId() is not int devId) return;
        int profile = DpCurrentProfile();

        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.DisplayPad", "user_rotated");
        Directory.CreateDirectory(cacheRoot);

        var flipType = degrees switch
        {
            90  => System.Drawing.RotateFlipType.Rotate90FlipNone,
            270 => System.Drawing.RotateFlipType.Rotate270FlipNone,
            _   => System.Drawing.RotateFlipType.RotateNoneFlipNone,
        };
        string dir = degrees == 90 ? "CW" : "CCW";
        int rotated = 0, failed = 0;

        for (int i = 0; i < _dpKeys.Length; i++)
        {
            var key = _dpKeys[i];
            if (string.IsNullOrEmpty(key.ImagePath) || !File.Exists(key.ImagePath))
                continue;
            if (DpGifAnimator.IsAnimatedGif(key.ImagePath))
            {
                // Baking a rotation into a single PNG would freeze the animation on one
                // frame AND permanently overwrite the key's stored path — same reason BC
                // itself special-cases ".gif" and skips it in several generic image
                // operations (see DisplayPadOperations, decompiled). Left untouched;
                // device-rotation (CbDpRotation) still applies to GIFs normally.
                DpLog($"[ROT] key {i}: animated GIF skipped (not rotated)");
                continue;
            }

            try
            {
                // Content-hash cache: avoids rotating the same source twice
                long mtime = File.GetLastWriteTimeUtc(key.ImagePath).Ticks;
                byte[] hashBytes = System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{key.ImagePath}|{mtime}|r{degrees}"));
                string cacheName = Convert.ToHexString(hashBytes).ToLowerInvariant() + $"_r{degrees}.png";
                string dest = Path.Combine(cacheRoot, cacheName);

                if (!File.Exists(dest))
                {
                    byte[] raw = File.ReadAllBytes(key.ImagePath);
                    using var ms  = new MemoryStream(raw);
                    using var bmp = new System.Drawing.Bitmap(ms);
                    bmp.RotateFlip(flipType);
                    bmp.Save(dest, System.Drawing.Imaging.ImageFormat.Png);
                }

                // Update model + DB + upload
                key.ImagePath = dest;
                _dpStore.SaveButton(devId, profile, _currentDpPageId, i, dest,
                    key.ActionType, key.ActionValue);
                _dpClient.UploadImageToProfile(devId, dest, i, profile, _dpRotation);
                rotated++;
            }
            catch (Exception ex)
            {
                DpLog($"[ROT] key {i}: {ex.Message}");
                failed++;
            }
        }

        DpLog($"[ROT] {dir} {degrees}°: {rotated} icons rotated" +
              (failed > 0 ? $", {failed} failed" : "."));
    }

    private void BtnDpRenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        if (CbDpProfile.SelectedItem is not DpProfileItem pi || pi.IsNew) return;
        int slot = pi.Slot;
        string current = _dpStore.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot);
        string? name = ShowRenameDialog(current,
            Loc.Get("rename_profile_title"),
            Loc.Get("rename_profile_prompt"));
        if (name is null) return;
        _dpStore.SetProfileName(id, slot, name);
        DpRefreshProfiles(id);
        DpSelectProfileSlot(slot);
        DpLog($"[UI] Profile {slot} renamed to \"{name}\"");
    }

    private void BtnDpDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        if (CbDpProfile.SelectedItem is not DpProfileItem pi || pi.IsNew) return;
        int slot = pi.Slot;
        // Cannot delete the last real profile
        var existing = _dpStore.GetExistingProfiles(id);
        if (existing.Count <= 1)
        {
            MessageBox.Show(Loc.Get("delete_profile_last"),
                Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string profileName = _dpStore.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("delete_profile_confirm", profileName),
            Loc.Get("delete_profile"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _dpStore.ClearProfile(id, slot);
        _dpStore.SetSetting($"profile.{id}.{slot}.name", "");
        DpLog($"[UI] Profile {slot} deleted.");
        DpRefreshProfiles(id);
        // CbDpProfile_SelectionChanged will reload the key grid automatically
    }

    // ================================================================
    // Fullscreen image (whole 2×6 panel — see DpFullscreenAnimator)
    // ================================================================

    private void BtnDpFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        int profile = DpCurrentProfile();
        int pageId = _currentDpPageId;

        var current = _dpStore.GetFullscreenImage(id, profile, pageId);
        var result = ShowFullscreenDialog(current?.Path, current?.Rotation ?? 0);
        if (result is not { } picked) return;   // cancelled

        _dpStore.SetFullscreenImage(id, profile, pageId, picked.Path, picked.Rotation);
        DpLog($"[FS] device {id} profile {profile} page {pageId} <- {Path.GetFileName(picked.Path)} (rot user={picked.Rotation})");
        LblStatus.Text = Loc.Get("dp_fullscreen_set_ok");
        DpRequestRepaint(id);
    }

    private void BtnDpFullscreenClear_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        int profile = DpCurrentProfile();
        int pageId = _currentDpPageId;

        if (_dpStore.GetFullscreenImage(id, profile, pageId) is null) return;
        _dpStore.ClearFullscreenImage(id, profile, pageId);
        DpFullscreenAnimator.Stop(id);
        DpLog($"[FS] device {id} profile {profile} page {pageId}: cleared");
        LblStatus.Text = Loc.Get("dp_fullscreen_cleared");
        DpRequestRepaint(id);
    }

    /// <summary>
    /// Picker dialog (built in code, same lightweight pattern as <see cref="ShowRenameDialog"/>):
    /// browse for an image/GIF, preview it live via the inline <see cref="CropEditor"/>
    /// (which handles both statics and animated GIFs — 2026-07-05, this dialog previously
    /// had NO image preview at all, not even for the cropped result), and pick a
    /// 0/90/180/270 user-rotation for the whole picture (independent of, and applied before,
    /// the per-tile device counter-rotation — see DpFullscreenAnimator remarks). Crop/zoom
    /// stays in THIS window (no separate popup). Returns null if cancelled.
    /// </summary>
    private (string Path, int Rotation)? ShowFullscreenDialog(string? currentPath, int currentRotation)
    {
        (string Path, int Rotation)? result = null;
        string? pendingPath = currentPath;

        // True full-panel crop target (native engine) vs. the 12-tile union fallback —
        // see DpFullscreenAnimator.PanelCanvasSize. Fixed for the lifetime of this dialog:
        // it depends on the CURRENT device rotation (_dpRotation), not the user-rotation
        // radios below (those are independent — see class remarks).
        var (cropW, cropH) = _dpClient.SupportsRawPanel
            ? DpFullscreenAnimator.PanelCanvasSize(_dpRotation)
            : (DpFullscreenAnimator.CanvasWidth, DpFullscreenAnimator.CanvasHeight);

        var prompt = new TextBlock
        {
            Text = Loc.Get("dp_fullscreen_prompt"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 12, 12, 8),
        };
        var lblCurrent = new TextBlock
        {
            Text = pendingPath is null ? Loc.Get("dp_fullscreen_none_set")
                                        : Loc.Get("dp_fullscreen_current", Path.GetFileName(pendingPath)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 8),
        };
        var btnBrowse = new Button
        {
            Content = Loc.Get("browse"),
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(12, 0, 12, 12),
            Padding = new Thickness(8, 4, 8, 4),
        };

        // Inline preview: CropEditor handles both statics and animated GIFs internally.
        var cropEditor = new CropEditor(cropW, cropH, animateGifs: true);
        cropEditor.ViewportBorder.Margin = new Thickness(12, 0, 12, 4);
        cropEditor.ControlsPanel.Margin = new Thickness(12, 0, 12, 8);
        // cropW/cropH already flip to portrait for a 90°/270° device rotation (see
        // PanelCanvasSize above) — the key-outline grid must follow the same swap,
        // otherwise the overlay keeps showing a 2×6 landscape grid on a portrait preview.
        bool portrait = cropH > cropW;
        cropEditor.SetKeyGrid(
            portrait ? DpFullscreenAnimator.Cols : DpFullscreenAnimator.Rows,
            portrait ? DpFullscreenAnimator.Rows : DpFullscreenAnimator.Cols);

        void RefreshPreview()
        {
            bool hasImage = !string.IsNullOrEmpty(pendingPath) && File.Exists(pendingPath);
            if (!hasImage)
            {
                cropEditor.ViewportBorder.Visibility = Visibility.Collapsed;
                cropEditor.ControlsPanel.Visibility = Visibility.Collapsed;
                cropEditor.Clear();
                return;
            }
            cropEditor.ViewportBorder.Visibility = Visibility.Visible;
            cropEditor.ControlsPanel.Visibility = Visibility.Visible;
            cropEditor.Load(pendingPath!);
        }

        var rotLabel = new TextBlock
        {
            Text = Loc.Get("dp_fullscreen_rotation"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Margin = new Thickness(12, 0, 12, 4),
        };
        var rotPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 4) };
        var radios = new List<RadioButton>();
        foreach (var deg in new[] { 0, 90, 180, 270 })
        {
            var rb = new RadioButton
            {
                Content = $"{deg}°",
                GroupName = "FsRot",
                Tag = deg,
                IsChecked = deg == currentRotation,
                Margin = new Thickness(0, 0, 14, 0),
                Foreground = Brushes.White,
            };
            radios.Add(rb);
            rotPanel.Children.Add(rb);
        }
        var rotHint = new TextBlock
        {
            Text = Loc.Get("dp_fullscreen_rotation_hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 12),
        };

        var btnOk = new Button
        {
            Content = Loc.Get("ok"), IsDefault = true, Width = 80,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4),
        };
        var btnCancel = new Button
        {
            Content = Loc.Get("cancel"), IsCancel = true, Width = 80,
            Padding = new Thickness(8, 4, 8, 4),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12),
        };
        buttons.Children.Add(btnOk);
        buttons.Children.Add(btnCancel);

        var panel = new StackPanel();
        panel.Children.Add(prompt);
        panel.Children.Add(lblCurrent);
        panel.Children.Add(btnBrowse);
        panel.Children.Add(cropEditor.ViewportBorder);
        panel.Children.Add(cropEditor.ControlsPanel);
        panel.Children.Add(rotLabel);
        panel.Children.Add(rotPanel);
        panel.Children.Add(rotHint);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = Loc.Get("dp_fullscreen_dialog_title"),
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
        };

        RefreshPreview();   // show whatever fullscreen image is already assigned, if any

        btnBrowse.Click += (_, _) =>
        {
            var ofd = new OpenFileDialog
            {
                Title  = Loc.Get("dp_fullscreen_dialog_title"),
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*"
            };
            if (ofd.ShowDialog(dlg) != true) return;
            pendingPath = ofd.FileName;
            lblCurrent.Text = Loc.Get("dp_fullscreen_current", Path.GetFileName(pendingPath));
            RefreshPreview();
        };
        btnOk.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(pendingPath) || !File.Exists(pendingPath))
            {
                MessageBox.Show(Loc.Get("dp_fullscreen_none_set"));
                return;
            }
            // Bake in the crop/zoom (or "as-is" stretch) chosen inline — works for both
            // static images and animated GIFs (CroppedGifRef sidecar for the latter).
            string finalPath = cropEditor.GetResultPath() ?? pendingPath;

            int rotation = radios.FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 0;
            result = (finalPath, rotation);
            dlg.Close();
        };
        btnCancel.Click += (_, _) => dlg.Close();

        dlg.ShowDialog();
        return result;
    }

    /// <summary>SDK ID of the active DisplayPad (set by TcDevices_SelectionChanged in xaml.cs).</summary>
    internal int? _activeDpDeviceId;
    private int? DpSelectedDeviceId() => _activeDpDeviceId;

    // ── Device selection (driven by top-level TcDevices) ──────────
    private void CbDpDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        DpActivateDevice(id);
    }

    /// <summary>
    /// Loads the given device's brightness/profile/rotation/keys and re-uploads its icons —
    /// the actual "make this device live" work, factored out of <see cref="CbDpDevice_SelectionChanged"/>
    /// so <see cref="DpRefreshDevices"/> can call it for auto-activation (see remarks there)
    /// without needing a real <see cref="SelectionChangedEventArgs"/>.
    /// </summary>
    private void DpActivateDevice(int id)
    {
        DpLog($"[UI] Active device: {id} ({_dpDeviceLabels.GetValueOrDefault(id, "?")})");

        _dpSuppressBrightness = true;
        try
        {
            int b = _dpClient.GetBrightness(id);
            if (b >= 0) { SldDpBrightness.Value = b; LblDpBrightness.Text = $"{b}%"; }
        }
        finally { _dpSuppressBrightness = false; }

        DpRefreshProfiles(id);

        _dpSuppressRotation = true;
        try
        {
            _dpRotation = _dpStore.GetRotation(id);
            CbDpRotation.SelectedIndex = _dpRotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };
        }
        finally { _dpSuppressRotation = false; }
        DpRebuildKeyGrid();

        DpReloadAndPreloadProfile();
    }

    /// <summary>
    /// Resolves "Next"/"Previous"/"N" and switches the DisplayPad firmware profile.
    /// Cycles through existing slots only. Called by DisplayPadActionHost.SwitchProfile.
    /// <paramref name="deviceId"/> = null targets the currently active/selected tab (and
    /// updates its UI combo); an explicit id (cross-device "switch profile" action)
    /// switches that device's stored profile and repaints it without touching the UI
    /// unless it happens to be the active tab.
    /// </summary>
    internal void DpSwitchProfile(int? deviceId, string target)
    {
        int? sel = deviceId ?? DpSelectedDeviceId();
        if (sel is not int id) return;
        bool isActive = id == DpSelectedDeviceId();

        List<int> real;
        int cur;
        if (isActive)
        {
            if (CbDpProfile.ItemsSource is not List<DpProfileItem> items) return;
            real = items.Where(x => !x.IsNew).Select(x => x.Slot).ToList();
            cur  = CbDpProfile.SelectedItem is DpProfileItem pi ? pi.Slot : (real.Count > 0 ? real[0] : 1);
        }
        else
        {
            real = _dpStore.GetExistingProfiles(id);
            cur  = _dpStore.GetCurrentProfile(id);
        }
        if (real.Count == 0) return;

        int curIdx = real.IndexOf(cur);
        if (curIdx < 0) curIdx = 0;

        var t = (target ?? "").Trim();
        int? nextSlot;
        if (t.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Next Profile", StringComparison.OrdinalIgnoreCase))
            nextSlot = real[(curIdx + 1) % real.Count];
        else if (t.Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("Previous Profile", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("prev", StringComparison.OrdinalIgnoreCase))
            nextSlot = real[(curIdx - 1 + real.Count) % real.Count];
        else if (int.TryParse(t, out var n))
            nextSlot = real.Contains(n) ? n : null;
        else { DpLog($"[EXEC] profile: target \"{t}\" not resolved"); return; }

        if (nextSlot is not int slot || slot == cur) return;

        // NOTE: do NOT call _dpClient.SwitchProfile here. Reference (decompiled BaseCamp,
        // DisplayPadOperations.ChangeProfileFromUI) confirms Base Camp never calls the
        // firmware's native SwitchProfile for the DisplayPad: "profile" is a purely
        // host-side/DB concept there. BC blanks the panel (UploadLogo/SetPanelImage) and
        // then re-uploads the new profile's icons one by one under a lock. Calling the
        // native SwitchProfile here (removed 2026-07-01) put the firmware into an
        // untested state that raced with our own image re-upload burst and corrupted
        // the icons (confirmed via photo: garbled icons except the last few uploaded).
        _dpStore.SetCurrentProfile(id, slot);
        if (isActive)
        {
            DpSelectProfileSlot(slot);
            ResetDpNavigation();
        }
        // Hardware repaint is serialized + coalesced per device (see DpRequestRepaint):
        // the store/UI switched instantly above; the device repaints when free.
        DpRequestRepaint(id);
        DpLog($"[EXEC] DisplayPad profile -> {slot} (device {id})");
    }

    /// <summary>Selects a slot in the profile combo (suppressing the event).</summary>
    private void DpSelectProfileSlot(int slot)
    {
        _dpSuppressProfile = true;
        try
        {
            if (CbDpProfile.ItemsSource is List<DpProfileItem> items)
                CbDpProfile.SelectedItem = items.Find(x => x.Slot == slot && !x.IsNew) ?? items[0];
        }
        finally { _dpSuppressProfile = false; }
    }

    /// <summary>Populates the profile combo with existing profiles + "New profile…".</summary>
    private void DpRefreshProfiles(int deviceId)
    {
        _dpSuppressProfile = true;
        try
        {
            var existing = _dpStore.GetExistingProfiles(deviceId);
            // Ensure at least profile 1 is present
            if (existing.Count == 0) existing.Add(1);
            var items = new List<DpProfileItem>();
            foreach (var slot in existing)
            {
                string name = _dpStore.GetProfileName(deviceId, slot) ?? Loc.Get("profile_n", slot);
                items.Add(new DpProfileItem(slot, name));
            }
            // Find the next free slot (1-5)
            int nextFree = Enumerable.Range(1, 5).FirstOrDefault(s => !existing.Contains(s));
            if (nextFree > 0)
                items.Add(new DpProfileItem(nextFree, Loc.Get("new_profile")));

            CbDpProfile.DisplayMemberPath = nameof(DpProfileItem.Label);
            CbDpProfile.ItemsSource = items;

            int current = _dpStore.GetCurrentProfile(deviceId);
            var match = items.Find(x => x.Slot == current && !x.IsNew);
            CbDpProfile.SelectedItem = match ?? items[0];
        }
        finally { _dpSuppressProfile = false; }
    }

    private void CbDpProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dpSuppressProfile) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (CbDpProfile.SelectedItem is not DpProfileItem pi) return;
        int profile = pi.Slot;

        if (pi.IsNew)
        {
            // Create empty profile: save a placeholder to make it appear as existing
            _dpStore.ClearProfile(id, profile);
            // Save at least key 0 empty to make the profile "exist"
            _dpStore.SaveButton(id, profile, 0, null, null, null);
            DpLog($"[UI] New empty profile created: slot {profile}");
            DpRefreshProfiles(id);
            // Select the newly created profile
            _dpSuppressProfile = true;
            try
            {
                var items = CbDpProfile.ItemsSource as List<DpProfileItem>;
                CbDpProfile.SelectedItem = items?.Find(x => x.Slot == profile && !x.IsNew);
            }
            finally { _dpSuppressProfile = false; }
        }

        // See DpSwitchProfile: no native SwitchProfile call (BC never uses it for DisplayPad).
        _dpStore.SetCurrentProfile(id, profile);
        ResetDpNavigation();
        DpLog($"SwitchProfile({id}, {profile})");
        DpRequestRepaint(id);
    }

    private void CbDpRotation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dpSuppressRotation) return;
        _dpRotation = CbDpRotation.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };
        DpRebuildKeyGrid();
        if (DpSelectedDeviceId() is int id)
        {
            _dpStore.SetRotation(id, _dpRotation);
            DpLog($"[ROT] device {id} -> {_dpRotation}°");
            DpReloadAndPreloadProfile();
        }
    }

    private void SldDpBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dpSuppressBrightness) return;
        if (DpSelectedDeviceId() is not int id) return;
        int level = (int)Math.Round(e.NewValue / 25.0) * 25;
        LblDpBrightness.Text = $"{level}%";
        _dpClient.SetBrightness(id, level);
    }

    private void BtnDpMapKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_dpMapAwaitingIndex >= 0)
        {
            _dpMapAwaitingIndex = -1;
            BtnDpMapKeys.Content = Loc.Get("remap_keys");
            LblStatus.Text = Loc.Get("mapping_cancelled");
            DpApplyDefaultKeyMap();
            return;
        }
        _dpMatrixToIndex.Clear();
        foreach (var k in _dpKeys) k.KeyMatrix = null;
        _dpMapAwaitingIndex = 0;
        BtnDpMapKeys.Content = Loc.Get("cancel_remap");
        LblStatus.Text = Loc.Get("dp_mapping_prompt", 0);
    }

    private void BtnDpResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        int profile = DpCurrentProfile();
        DpGifAnimator.StopAllForDevice(id);
        DpFullscreenAnimator.Stop(id);
        _dpClient.ResetPictures(id);
        _dpStore.ClearProfile(id, profile);
        _dpStore.ClearFullscreenImage(id, profile, _currentDpPageId);
        ResetDpNavigation();
        foreach (var k in _dpKeys) { k.ImagePath = null; k.ActionType = null; k.ActionValue = null; }
        DpLog($"ResetAllPictures({id})");
    }

    private void BtnDpImportXml_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;

        var dlg = new OpenFileDialog
        {
            Title       = Loc.Get("dp_open_bc_profile"),
            Filter      = Loc.Get("dp_filter_bc_xml"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var doc  = System.Xml.Linq.XDocument.Load(dlg.FileName);
            var root = doc.Root;
            if (root is null) return;

            // Slot (1-5) from <Id>; profile display name from <ProfileName>
            int slot = 1;
            if (int.TryParse(root.Element("Id")?.Value, out var n) && n >= 1 && n <= 5)
                slot = n;
            string profileName = root.Element("ProfileName")?.Value
                                 ?? Path.GetFileNameWithoutExtension(dlg.FileName);

            // BC XML structure: <DisplayPadKeyBindings>/<DisplayPadLayerBidings>
            var bindings = root.Descendants("DisplayPadLayerBidings").ToList();
            if (bindings.Count == 0)
            {
                DpLog("[IMP-XML] No DisplayPadLayerBidings found in XML.");
                return;
            }

            string iconsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "K2.DisplayPad", "imported_xml", profileName);
            Directory.CreateDirectory(iconsDir);

            _dpStore.ClearProfile(id, slot);
            _dpClient.APEnable(id, false);
            int rotation = _dpStore.GetRotation(id);
            int imported = 0;

            foreach (var b in bindings)
            {
                bool isAssigned = b.Element("IsKeyAssigned")?.Value
                                   ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                // <base64Image> may have "data:image/...;base64," prefix
                string? imageB64 = b.Element("base64Image")?.Value;
                // Skip only if truly empty (no action AND no image)
                if (!isAssigned && string.IsNullOrEmpty(imageB64)) continue;

                if (!int.TryParse(b.Element("KeyId")?.Value, out int keyId)) continue;
                if (!BaseCampDbImporter.KeyIdToIndex.TryGetValue(keyId, out int btnIndex)) continue;

                // Page: ParentId=0 → root; ParentId>0 → folder sub-page
                int pageId = 0;
                if (int.TryParse(b.Element("ParentId")?.Value, out int pid)) pageId = pid;

                // Image — use page-aware filename to avoid collisions across pages
                string? imagePath = null;
                if (!string.IsNullOrEmpty(imageB64))
                {
                    try
                    {
                        var imgBytes = BaseCampDbImporter.DecodeBase64Image(imageB64);
                        if (imgBytes is not null)
                        {
                            string iconFile = pageId == 0
                                ? Path.Combine(iconsDir, $"key_{btnIndex}.png")
                                : Path.Combine(iconsDir, $"key_p{pageId}_{btnIndex}.png");
                            File.WriteAllBytes(iconFile, imgBytes);
                            imagePath = iconFile;
                        }
                        // else: BC internal path (/images/DKD/...) — no image available
                    }
                    catch (Exception ex) { DpLog($"[IMP-XML] image decode failed for key {btnIndex}: {ex.Message}"); }
                }

                // Action — handle Create Folder and Back specially
                string? funcType  = b.Element("FunctionType")?.Value;
                string? subType   = b.Element("SubFunctionType")?.Value;
                string? funcValue = b.Element("FunctionValue")?.Value;

                string? actionType, actionValue;
                if (funcType == "K2Action")
                {
                    // Sentinel written by DpProfileExporter.ExportK2: SubFunctionType/
                    // FunctionValue carry the literal K2 ActionType/ActionValue, without
                    // going through BC translation (lossless K2 round-trip, including
                    // multi-character text, pyscript, command, url, etc.).
                    actionType  = subType;
                    actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;

                    // dp_folder still carries the folder name in OptionalText
                    // (DpProfileExporter.BuildFolderOptionalText) — restore it.
                    if (actionType == "dp_folder" && int.TryParse(actionValue, out var k2FolderId))
                    {
                        string? optText = b.Element("OptionalText")?.Value;
                        if (!string.IsNullOrEmpty(optText))
                        {
                            try
                            {
                                using var doc2 = System.Text.Json.JsonDocument.Parse(optText);
                                if (doc2.RootElement.TryGetProperty("TextTitle", out var tt) &&
                                    tt.GetString() is { Length: > 0 } title)
                                    _dpStore.SetFolderName(k2FolderId, title);
                            }
                            catch { /* Malformed OptionalText: ignore, the folder stays unnamed */ }
                        }
                    }
                }
                else if (funcType == "Create Folder")
                {
                    string? optText = b.Element("OptionalText")?.Value;
                    int folderPageId = BaseCampDbImporter.ParseFolderPageId(optText);
                    actionType  = "dp_folder";
                    actionValue = folderPageId > 0 ? folderPageId.ToString() : null;
                    if (folderPageId > 0 && !string.IsNullOrEmpty(subType))
                        _dpStore.SetFolderName(folderPageId, subType);
                }
                else if (funcType == "Back")
                {
                    actionType  = "dp_back";
                    actionValue = null;
                }
                else
                {
                    (actionType, actionValue) = BaseCampDbImporter.TranslateAction(funcType, subType, funcValue);
                }

                _dpStore.SaveButton(id, slot, pageId, btnIndex, imagePath, actionType, actionValue);

                // Only upload root-page images persistently at import time
                if (imagePath is not null && pageId == 0)
                {
                    bool ok = _dpClient.UploadImageToProfile(id, imagePath, btnIndex, slot, rotation);
                    if (!ok)
                        _dpClient.UploadImage(id, imagePath, btnIndex, rotation);
                }

                imported++;
            }

            // No native SwitchProfile — see DpSwitchProfile.
            _dpStore.SetCurrentProfile(id, slot);
            ResetDpNavigation();
            DpRefreshProfiles(id);
            DpSelectProfileSlot(slot);
            DpRequestRepaint(id);

            DpLog($"[IMP-XML] '{profileName}' -> device {id} slot {slot}: {imported} keys");
            LblStatus.Text = Loc.Get("dp_imported_xml", profileName, slot);
        }
        catch (Exception ex)
        {
            DpLog($"[ERR] import XML: {ex.Message}");
        }
    }

    // ================================================================
    // Export profiles — Base Camp-compatible XML / K2-only XML
    // ================================================================

    private void BtnDpExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) { LblStatus.Text = Loc.Get("dp_export_no_profile"); return; }

        var profiles = _dpStore.GetExistingProfiles(id)
            .Select(slot => (Slot: slot, Name: _dpStore.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot)))
            .ToList();
        int? currentSlot = CbDpProfile.SelectedItem is DpProfileItem pi && !pi.IsNew ? pi.Slot : null;
        string deviceLabel = _dpDeviceLabels.GetValueOrDefault(id, $"DisplayPad {id}");

        ExportProfileHelper.Run(
            owner: this,
            deviceLabel: deviceLabel,
            profiles: profiles,
            currentSlot: currentSlot,
            exportOne: (slot, name, bcCompatible, path) =>
            {
                var result = bcCompatible
                    ? DpProfileExporter.ExportBaseCamp(_dpStore, id, slot, name, path)
                    : DpProfileExporter.ExportK2(_dpStore, id, slot, name, path);
                return (result.Exported, result.SkippedActions, result.SkipReasons);
            },
            log: DpLog,
            setStatus: s => LblStatus.Text = s);
    }

    // ================================================================
    // Import from BaseCamp.db
    // ================================================================

    private void BtnDpImportBc_Click(object sender, RoutedEventArgs e)
    {
        // 1. Locate the database
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            DpLog("[IMP-BC] BaseCamp.db not found.");
            LblStatus.Text = Loc.Get("dp_bc_db_not_found");
            return;
        }
        DpLog($"[IMP-BC] DB found: {dbPath}");

        // 2. Read profiles grouped by device
        Dictionary<int, List<BaseCampDbImporter.BcProfile>> bcDevices;
        try { bcDevices = BaseCampDbImporter.ReadProfiles(dbPath); }
        catch (Exception ex)
        {
            DpLog($"[IMP-BC] Error reading DB: {ex.Message}");
            return;
        }

        if (bcDevices.Count == 0)
        {
            DpLog("[IMP-BC] No DisplayPad profiles in DB.");
            LblStatus.Text = Loc.Get("dp_no_profiles_in_bc");
            return;
        }

        // 3. Auto-mapping: BC and K2 use the same SDK → DeviceIds match.
        //    Show a summary with the automatic mapping.
        var k2Devices = new HashSet<int>(_dpDeviceIds);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Automatic Base Camp → K2 mapping:\n");

        int matchedDevices = 0;
        int totalProfiles = 0;
        foreach (var (bcDevId, profiles) in bcDevices)
        {
            bool hasK2 = k2Devices.Contains(bcDevId);
            string status = hasK2 ? "→ K2 connected" : "→ NOT connected (skip)";
            sb.AppendLine($"  DisplayPad #{bcDevId}  {status}");
            foreach (var p in profiles)
            {
                string sel = p.IsSelected ? " [ACTIVE]" : "";
                sb.AppendLine($"    Slot {p.Slot}: {p.Name}{sel}");
            }
            if (hasK2) { matchedDevices++; totalProfiles += profiles.Count; }
        }

        if (matchedDevices == 0)
        {
            sb.AppendLine("\nNo connected device matches the profiles in the DB.");
            MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        sb.AppendLine($"\nImport {totalProfiles} profiles to {matchedDevices} devices?");
        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // 4. Import: each profile goes to the K2 device with the same ID as BC
        int totalButtons = 0;
        int importedProfiles = 0;
        int selectedDevId = DpSelectedDeviceId() ?? -1;
        int selectedSlot = -1;

        foreach (var (bcDevId, profiles) in bcDevices)
        {
            if (!k2Devices.Contains(bcDevId)) continue;

            // Rotation for this device
            int rotation = _dpStore.GetRotation(bcDevId);
            // APEnable=false required before SetIconPic (UploadImageToProfile)
            _dpClient.APEnable(bcDevId, false);

            foreach (var profile in profiles)
            {
                try
                {
                    int n = BaseCampDbImporter.ImportProfile(
                        dbPath, profile, bcDevId, _dpStore);

                    // Upload root-page images only (folder pages are uploaded on navigation)
                    var buttons = _dpStore.LoadPage(bcDevId, profile.Slot, 0);
                    foreach (var btn in buttons)
                    {
                        if (!string.IsNullOrEmpty(btn.ImagePath) && File.Exists(btn.ImagePath))
                        {
                            bool ok = _dpClient.UploadImageToProfile(bcDevId, btn.ImagePath,
                                btn.ButtonIndex, profile.Slot, rotation);
                            if (!ok)
                                _dpClient.UploadImage(bcDevId, btn.ImagePath, btn.ButtonIndex, rotation);
                        }
                    }

                    DpLog($"[IMP-BC] dev#{bcDevId} {profile.Name} (slot {profile.Slot}): {n} keys");
                    totalButtons += n;
                    importedProfiles++;

                    // Track the active profile for the currently selected device
                    if (profile.IsSelected && bcDevId == selectedDevId)
                        selectedSlot = profile.Slot;
                }
                catch (Exception ex)
                {
                    DpLog($"[IMP-BC] Import error dev#{bcDevId}/{profile.Name}: {ex.Message}");
                }
            }
        }

        // 5. Activate the profile that was selected in BC for the current device
        if (selectedSlot > 0 && selectedDevId > 0)
        {
            // No native SwitchProfile — see DpSwitchProfile.
            _dpStore.SetCurrentProfile(selectedDevId, selectedSlot);
            ResetDpNavigation();
            DpRefreshProfiles(selectedDevId);
            DpSelectProfileSlot(selectedSlot);
            DpRequestRepaint(selectedDevId);
        }

        DpLog($"[IMP-BC] Done: {totalButtons} keys across {importedProfiles} profiles / {matchedDevices} devices");
        LblStatus.Text = Loc.Get("dp_imported", importedProfiles, totalButtons);
    }

    // ================================================================
    // Overlay key clicks
    // ================================================================

    private void DpKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) { DpLog("[WARN] Select a device first."); return; }

        // Handle folder/back navigation on click
        if (key.ActionType == "dp_folder" && int.TryParse(key.ActionValue, out int pageId))
        {
            DpNavigateToPage(pageId, _dpStore.GetFolderName(pageId));
            return;
        }
        if (key.ActionType == "dp_back")
        {
            DpNavigateBack();
            return;
        }

        // Key editing is only enabled while the "Key Binding" section is active
        // (folder/back navigation above always works, since that's normal usage).
        if (!IsDpKeyBindingSectionActive) return;

        // Unified dialog: image + action
        var dlg = new DpKeyConfigDialog(key.Index, key.ImagePath, key.ActionType, key.ActionValue) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Update action
        key.ActionType  = dlg.ActionType;
        key.ActionValue = dlg.ActionValue;

        // Update image (upload + persist) only if it changed
        if (dlg.ImageChanged)
        {
            if (!string.IsNullOrEmpty(dlg.NewImagePath) && File.Exists(dlg.NewImagePath))
            {
                DpUploadAndPersist(id, DpCurrentProfile(), key, dlg.NewImagePath);
            }
            else if (dlg.NewImagePath is null)
            {
                // Image removed
                DpGifAnimator.Stop(id, key.Index);
                key.ImagePath = null;
                _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, null, key.ActionType, key.ActionValue);
                DpClearKeyOnDevice(id, key.Index);
                DpLog($"[ACT] key #{key.Index} image removed");
            }
        }
        else
        {
            // Only the action changed — update store without re-uploading the image
            _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, key.ImagePath, key.ActionType, key.ActionValue);
            DpLog($"[ACT] key #{key.Index} <- {key.ActionType ?? "none"}");
        }
    }

    private void DpUploadAndPersist(int id, int profile, DisplayPadKey key, string path)
    {
        bool ok;
        if (DpGifAnimator.IsAnimatedGif(path))
        {
            // Animated GIFs are always played live (per-frame SetIconPacket-style upload,
            // see DpGifAnimator) — there is no firmware-persistent equivalent, same as BC.
            DpGifAnimator.StartOrUpdate(_dpClient, DpLogAsync, id, key.Index, path, _dpRotation);
            ok = true;
            DpLog($"[GIF] key #{key.Index} <- {Path.GetFileName(path)}");
        }
        else
        {
            DpGifAnimator.Stop(id, key.Index);
            ok = _dpClient.UploadImageToProfile(id, path, key.Index, profile, _dpRotation);
            if (!ok)
            {
                DpLog($"  Upload persistent FAIL, trying live");
                ok = _dpClient.UploadImage(id, path, key.Index, _dpRotation);
            }
        }
        if (ok)
        {
            key.ImagePath = path;
            _dpStore.SaveButton(id, profile, _currentDpPageId, key.Index, path, key.ActionType, key.ActionValue);
        }
        DpLog($"Upload key #{key.Index} -> {(ok ? "OK" : "FAIL")}");
    }

    // ================================================================
    // Context menu
    // ================================================================

    private ContextMenu BuildDpKeyContextMenu()
    {
        var menu = new ContextMenu();
        var miCfg = new MenuItem { Header = Loc.Get("dp_configure_action") };
        miCfg.Click += DpMnuConfigureAction_Click;
        var miRa = new MenuItem { Header = Loc.Get("dp_remove_action") };
        miRa.Click += DpMnuRemoveAction_Click;
        var miChImg = new MenuItem { Header = Loc.Get("dp_change_image") };
        miChImg.Click += DpMnuChangeImage_Click;
        var miRi = new MenuItem { Header = Loc.Get("dp_remove_image") };
        miRi.Click += DpMnuRemoveImage_Click;
        menu.Items.Add(miCfg);
        menu.Items.Add(miRa);
        menu.Items.Add(new Separator());
        menu.Items.Add(miChImg);
        menu.Items.Add(miRi);
        return menu;
    }

    private static DisplayPadKey? DpKeyFromMenu(object sender) =>
        sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is DisplayPadKey key ? key : null;

    private void DpMnuConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        var dlg = new ButtonActionDialog(key.Index, key.ActionType, key.ActionValue, _dpActionHost) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            key.ActionType = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none" ? null : dlg.ActionType;
            key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;
            _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, key.ImagePath, key.ActionType, key.ActionValue);
            DpLog($"[ACT] key #{key.Index} <- {key.ActionType ?? "none"}");
        }
    }

    private void DpMnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        key.ActionType = null; key.ActionValue = null;
        _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, key.ImagePath, null, null);
    }

    private void DpMnuChangeImage_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        var dlg = new OpenFileDialog
        {
            Title  = $"Choose image for key #{key.Index}",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        string picked = dlg.FileName;
        if (!DpGifAnimator.IsAnimatedGif(picked))
        {
            string? cropped = ImageCropDialog.Show(this, picked,
                DpHidNative.IconSize, DpHidNative.IconSize,
                Loc.Get("crop_title", DpHidNative.IconSize, DpHidNative.IconSize));
            if (cropped is not null) picked = cropped;
        }
        DpUploadAndPersist(id, DpCurrentProfile(), key, picked);
    }

    private void DpMnuRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        DpGifAnimator.Stop(id, key.Index);
        key.ImagePath = null;
        _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, null, key.ActionType, key.ActionValue);
        DpClearKeyOnDevice(id, key.Index);
    }

    /// <summary>
    /// Blanks a single key's icon on the physical panel (a solid-black BGR buffer —
    /// C# zero-initializes the array, so no pixel loop is needed). Removing an image
    /// only updates the UI/store above; without this the old icon stays on-screen
    /// until the next full repaint (profile switch, reconnect, ...).
    /// </summary>
    private void DpClearKeyOnDevice(int id, int btnIndex) =>
        _dpClient.TryUploadRawBgr(id, new byte[DpHidNative.IconBytes], btnIndex);

    // ================================================================
    // Refresh / Persistence
    // ================================================================

    private void DpRefreshDevices()
    {
        int? prevActive = _activeDpDeviceId;
        var previousIds = _dpDeviceIds.ToList();
        _dpDevices.Clear(); _dpDeviceIds.Clear(); _dpDeviceLabels.Clear();

        var ids = _dpClient.DeviceIds();
        DpLog($"Devices -> [{string.Join(", ", ids)}]");
        // A device that disappeared (unplugged) can't be repainted anymore — stop any
        // animation still looping for it (it would otherwise just spin uploading to a
        // pad the client has already dropped; harmless but wasteful).
        foreach (var goneId in previousIds.Except(ids))
        {
            DpGifAnimator.StopAllForDevice(goneId);
            DpFullscreenAnimator.Stop(goneId);
        }
        var items = new List<DpDeviceItem>();
        int progressive = 1;
        foreach (var id in ids)
        {
            bool plugged = _dpClient.IsPlugged(id);
            if (!plugged) continue;   // skip devices not physically connected
            string fw = _dpClient.FirmwareVersion(id);
            int br = _dpClient.GetBrightness(id);
            // Use custom name if set, otherwise default progressive label
            string defaultLabel = $"DisplayPad {progressive}";
            string label = _dpStore.GetSetting($"device.{id}.name") ?? defaultLabel;
            _dpDeviceLabels[id] = label;
            _dpDevices.Add(new DpDeviceRow
            {
                Id = id,
                Label = label,
                Plugged = "yes",
                FirmwareVersion = string.IsNullOrEmpty(fw) ? "—" : fw,
                Brightness = br < 0 ? "—" : $"{br}%"
            });
            _dpDeviceIds.Add(id);
            items.Add(new DpDeviceItem(id, label));
            progressive++;
        }

        // Sync top-level device tabs for DisplayPad (after MacroPad tab)
        RemoveDeviceTabs("dp_");
        int insertIdx = TcDevices.Items.IndexOf(TabMacroPad) + 1;
        foreach (var item in items)
        {
            var tab = new TabItem { Header = item.Label, Tag = $"dp_{item.SdkId}" };
            TcDevices.Items.Insert(insertIdx++, tab);
        }
        // Only steer the top-level selection to a DisplayPad tab if the user is
        // already on one — a background device refresh (e.g. a plug event arriving
        // after startup) must not steal focus away from whatever tab is active.
        string? selTag = (TcDevices.SelectedItem as TabItem)?.Tag as string;
        bool currentlyOnDp = selTag != null && selTag.StartsWith("dp_");
        if (items.Count > 0 && currentlyOnDp)
        {
            int targetId = prevActive.HasValue && items.Any(x => x.SdkId == prevActive.Value)
                           ? prevActive.Value : items[0].SdkId;
            TcDevices.SelectedItem = TcDevices.Items.OfType<TabItem>()
                                     .FirstOrDefault(t => (t.Tag as string) == $"dp_{targetId}");
        }
        else if (items.Count > 0 && _activeDpDeviceId is null)
        {
            // Nobody has ever opened the DisplayPad tab this session (e.g. app auto-started
            // to the tray, or the user is parked on Everest/Settings): _activeDpDeviceId would
            // otherwise stay null forever, and OnDpKey/DpReloadCurrentProfile/DpSwitchProfile
            // all no-op without it — physical key presses would silently do nothing until the
            // user happened to click the DisplayPad tab. Load the device's state (keys, action
            // bindings, icons) in the background WITHOUT touching TcDevices.SelectedItem, so it
            // starts responding immediately but doesn't steal focus (same concern as above).
            _activeDpDeviceId = items[0].SdkId;
            DpActivateDevice(_activeDpDeviceId.Value);
        }
    }

    private int DpCurrentProfile() => CbDpProfile.SelectedItem is DpProfileItem pi ? pi.Slot : 1;

    /// <summary>
    /// Reloads the current page's keys from the store and uploads images.
    /// When <paramref name="persistent"/> is true (default), uses UploadImageToProfile
    /// for the firmware slot. Pass false for folder navigation (live upload only).
    /// When <paramref name="blankFirst"/> is true, the panel blank (ResetPictures — BC's
    /// UploadLogo) runs INSIDE the chained background segment, atomically right before
    /// this batch's uploads. Calling ResetPictures synchronously from the caller (as the
    /// profile-switch paths used to) interleaved the blank with the PREVIOUS reload's
    /// still-queued uploads on fast repeated switching — stale icons landed after the
    /// blank — and froze the UI thread for the ~350 ms panel transfer.
    /// A new reload also CANCELS the not-yet-executed uploads of the previous one
    /// (BC's ChangeProfileFromUI likewise waits/cancels pending upload tasks).
    /// </summary>
    private void DpReloadCurrentProfile(bool persistent = true, bool blankFirst = false)
    {
        if (DpSelectedDeviceId() is not int id) return;
        int profile = DpCurrentProfile();
        int pageId = _currentDpPageId;
        int rotation = _dpRotation;
        foreach (var k in _dpKeys) { k.ImagePath = null; k.ActionType = null; k.ActionValue = null; }
        var rows = _dpStore.LoadPage(id, profile, pageId);
        DpLog($"[DB] loaded {rows.Count} records for device={id} profile={profile} page={pageId}");

        // Stop every animated-GIF loop on this device NOW (synchronously) — a page/profile
        // switch repurposes key indices, and a stale animation task would keep overwriting
        // whatever key it was bound to (possibly mid-blank) with frames from the OLD page.
        // Mirrors BC cancelling pending per-key GIF tasks before it starts a new batch.
        DpGifAnimator.StopAllForDevice(id);

        // A fullscreen image, if assigned to this page, REPLACES all 12 per-key icons on
        // the hardware — per-key actions (loaded into _dpKeys below) still work normally
        // when a physical key is pressed, only the visuals are overridden.
        var fullscreen = _dpStore.GetFullscreenImage(id, profile, pageId);
        // NOTE: deliberately NOT using an "is { } fs" pattern variable here — it gets
        // captured by the background continuation below, and the compiler's definite-
        // assignment analysis doesn't carry the pattern-match guarantee across an
        // anonymous-method boundary (CS0170 "use of unassigned field"), even though
        // `fullscreenActive` makes it always-safe at runtime. Using `fullscreen.Value`
        // directly (guarded by the plain bool) sidesteps that entirely.
        bool fullscreenActive = fullscreen.HasValue && File.Exists(fullscreen.Value.Path);
        _dpFullscreenByDevice[id] = fullscreenActive;
        if (!fullscreenActive) DpFullscreenAnimator.Stop(id);

        var toUpload = new List<(int btnIndex, string imagePath)>();
        var toAnimate = new List<(int btnIndex, string imagePath)>();
        foreach (var r in rows)
        {
            if (r.ButtonIndex < 0 || r.ButtonIndex >= _dpKeys.Length) continue;
            var key = _dpKeys[r.ButtonIndex];
            key.ActionType = r.ActionType;
            key.ActionValue = r.ActionValue;
            if (!string.IsNullOrEmpty(r.ImagePath) && File.Exists(r.ImagePath))
            {
                key.ImagePath = r.ImagePath;
                if (fullscreenActive) continue;   // hardware won't show per-key icons anyway
                if (DpGifAnimator.IsAnimatedGif(r.ImagePath))
                    toAnimate.Add((r.ButtonIndex, r.ImagePath));
                else
                    toUpload.Add((r.ButtonIndex, r.ImagePath));
            }
        }

        // The app's own grid above is already updated (instant). The hardware write is the
        // slow part — run it on a background thread, chained per device. A newer reload
        // supersedes the queued (not yet started) uploads of the previous one via the CTS.
        if (toUpload.Count > 0 || toAnimate.Count > 0 || fullscreenActive || blankFirst)
        {
            if (_dpUploadCts.TryGetValue(id, out var oldCts)) oldCts.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _dpUploadCts[id] = cts;
            var ct = cts.Token;

            var previous = _dpUploadChain.TryGetValue(id, out var p) ? p : Task.CompletedTask;
            var next = previous.ContinueWith(_ =>
            {
                if (ct.IsCancellationRequested) return;
                if (blankFirst) _dpClient.ResetPictures(id);

                if (fullscreenActive)
                {
                    DpFullscreenAnimator.Start(_dpClient, DpLogAsync, id,
                        fullscreen!.Value.Path, fullscreen.Value.Rotation, rotation);
                    return;
                }

                foreach (var (btnIndex, imagePath) in toUpload)
                {
                    if (ct.IsCancellationRequested) return;
                    if (persistent)
                        _dpClient.UploadImageToProfile(id, imagePath, btnIndex, profile, rotation);
                    _dpClient.UploadImage(id, imagePath, btnIndex, rotation);
                }
                // Animated GIFs start AFTER the static batch + blank settle — same order BC
                // uses (normal icon loop first, UploadGIFImage right after).
                if (ct.IsCancellationRequested) return;
                foreach (var (btnIndex, imagePath) in toAnimate)
                {
                    if (ct.IsCancellationRequested) return;
                    DpGifAnimator.StartOrUpdate(_dpClient, DpLogAsync, id, btnIndex, imagePath, rotation);
                }
            }, TaskScheduler.Default);
            _dpUploadChain[id] = next;
        }
    }

    /// <summary>Per-device chain of pending background icon uploads (see <see cref="DpReloadCurrentProfile"/>).</summary>
    private readonly Dictionary<int, Task> _dpUploadChain = new();
    /// <summary>Per-device cancellation of superseded reload batches.</summary>
    private readonly Dictionary<int, System.Threading.CancellationTokenSource> _dpUploadCts = new();
    /// <summary>Per-device: a full repaint (blank + icons) is currently running on the hardware.</summary>
    private readonly Dictionary<int, bool> _dpRepaintBusy = new();
    /// <summary>Per-device: a repaint was requested while one was running — run another when done.</summary>
    private readonly HashSet<int> _dpRepaintPending = new();
    /// <summary>Per-device: whether a fullscreen image currently owns the hardware's 12 icons
    /// (set by <see cref="DpReloadCurrentProfile"/>) — checked by <see cref="DpUploadPressVisual"/>
    /// to skip the per-key press-bounce while the fullscreen panel is in control.</summary>
    private readonly Dictionary<int, bool> _dpFullscreenByDevice = new();

    /// <summary>
    /// Serializes full hardware repaints per device. Profile switches update the
    /// UI/store state instantly, but the actual blank+upload sequence must never
    /// overlap a previous one (overlapping sequences are what corrupted icons on
    /// rapid next/prev presses). While a repaint is running, further requests
    /// coalesce into ONE pending repaint that starts when the current one ends and
    /// reloads whatever profile/page is selected at THAT moment — so hammering
    /// next/next/next paints only the final destination, and no press is lost
    /// (the store/UI selection already advanced per press).
    /// </summary>
    private void DpRequestRepaint(int id)
    {
        if (_dpRepaintBusy.GetValueOrDefault(id))
        {
            _dpRepaintPending.Add(id);
            return;
        }
        _dpRepaintBusy[id] = true;
        DpReloadAndPreloadProfile(blankFirst: true);
        var chain = _dpUploadChain.TryGetValue(id, out var t) ? t : Task.CompletedTask;
        chain.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _dpRepaintBusy[id] = false;
            if (_dpRepaintPending.Remove(id))
                DpRequestRepaint(id);
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Pre-loads all folder sub-pages for a profile onto the device via live upload,
    /// then calls <see cref="DpReloadCurrentProfile"/> for the current page (root).
    /// Call this at runtime profile switch so the device always has all images ready.
    /// At the end the device display shows the root page (uploaded last).
    /// </summary>
    private void DpReloadAndPreloadProfile(bool blankFirst = false)
    {
        if (DpSelectedDeviceId() is not int id) return;

        // NOTE (2026-07-01): dropped the eager "preload every folder sub-page" step that used
        // to run here. Each icon upload now has to be serialized + settle-delayed (see
        // SdkHandler.CmdUploadImage) to avoid corrupting the display, so eagerly re-uploading
        // every button of every folder on every plain profile switch got very slow (one full
        // extra pass over every sub-page, most of which the user may never open). Folder pages
        // are already live-uploaded lazily the moment the user actually navigates into them
        // (DpNavigateToPage), so preloading them here was redundant, not just slow.
        //
        // Also: persistent=false. The image was already persisted to the firmware profile slot
        // at the moment it was configured (DpUploadAndPersist) or during import — re-persisting
        // every button on every switch/rotation-change/reconnect was pure repeated work with no
        // benefit (nothing reads the firmware profile slot back; BC itself never does either,
        // see project_displaypad_profile_corruption memory). Skipping it roughly halves the
        // number of USB transfers per reload.
        DpReloadCurrentProfile(persistent: false, blankFirst: blankFirst);
    }

    // ---- Folder navigation ----

    /// <summary>Navigates into a folder sub-page (live image upload, no persistent slot change).</summary>
    private void DpNavigateToPage(int pageId, string? folderName)
    {
        _dpPageHistory.Push((_currentDpPageId, _currentDpFolderName));
        _currentDpPageId = pageId;
        _currentDpFolderName = folderName ?? _dpStore.GetFolderName(pageId);
        UpdateDpBreadcrumb();
        DpReloadCurrentProfile(persistent: false);
    }

    /// <summary>Navigates back to the parent page.</summary>
    private void DpNavigateBack()
    {
        if (_dpPageHistory.Count == 0) return;
        var (pageId, name) = _dpPageHistory.Pop();
        _currentDpPageId = pageId;
        _currentDpFolderName = name;
        UpdateDpBreadcrumb();
        DpReloadCurrentProfile(persistent: false);
    }

    /// <summary>Resets navigation to root (called on profile switch, reset, import).</summary>
    private void ResetDpNavigation()
    {
        _dpPageHistory.Clear();
        _currentDpPageId = 0;
        _currentDpFolderName = null;
        UpdateDpBreadcrumb();
    }

    /// <summary>Shows/hides the back button and updates the breadcrumb label.</summary>
    private void UpdateDpBreadcrumb()
    {
        bool inFolder = _currentDpPageId != 0;
        BtnDpBack.Visibility = inFolder ? Visibility.Visible : Visibility.Collapsed;
        string name = _currentDpFolderName ?? $"Page {_currentDpPageId}";
        LblDpBreadcrumb.Text = inFolder ? $"▸ {name}" : "";
    }

    private void BtnDpBack_Click(object sender, RoutedEventArgs e) => DpNavigateBack();

    // ================================================================
    // Events from the satellite
    // ================================================================

    private void OnDpPlug(object? sender, JsonElement e) =>
        Dispatcher.BeginInvoke(() =>
        {
            DpLog($"[PLUG] arg0={e.Get("arg0")} arg1={e.Get("arg1")}");
            if (_dpClient.IsConnected) DpRefreshDevices();
        });

    private void OnDpKey(object? sender, JsonElement e) =>
        Dispatcher.BeginInvoke(() =>
        {
            // Only handle events from the currently selected device.
            // Each device has independent state (_dpKeys, _dpMatrixToIndex, _currentDpPageId)
            // so events from non-selected devices would execute incorrect actions.
            int evtDevId = e.Get("deviceId");
            if (DpSelectedDeviceId() is not int selId || evtDevId != selId) return;

            int matrix = e.Get("keyMatrix");
            bool pressed = e.GetBool("pressed");

            // Per-key-press log: noisy in normal use, so it only fires at LogLevel.Verbose
            // (see General Settings tab / AppSettings.LogLevel).
            if (AppSettings.LogLevel == K2LogLevel.Verbose)
                DpLog($"[KEY] matrix 0x{matrix:X2} {(pressed ? "down" : "up")}");

            if (pressed && _dpMapAwaitingIndex >= 0 && _dpMapAwaitingIndex < 12)
            {
                int idx = _dpMapAwaitingIndex;
                _dpMatrixToIndex[matrix] = idx;
                _dpKeys[idx].KeyMatrix = matrix;
                DpLog($"[MAP] key #{idx} <- matrix 0x{matrix:X2}");
                _dpMapAwaitingIndex++;
                if (_dpMapAwaitingIndex >= 12)
                {
                    _dpMapAwaitingIndex = -1;
                    LblStatus.Text = Loc.Get("dp_mapping_done");
                    BtnDpMapKeys.Content = Loc.Get("remap_keys");
                }
                else LblStatus.Text = Loc.Get("dp_mapping_prompt", _dpMapAwaitingIndex);
                return;
            }

            if (_dpMatrixToIndex.TryGetValue(matrix, out int hi) && hi < 12)
            {
                _dpKeys[hi].IsHighlighted = pressed;
                DpUploadPressVisual(selId, hi, pressed);
                if (pressed)
                {
                    string? action = _dpKeys[hi].ActionType;
                    string? value  = _dpKeys[hi].ActionValue;
                    if (action == "dp_folder" && int.TryParse(value, out int pageId))
                        DpNavigateToPage(pageId, _dpStore.GetFolderName(pageId));
                    else if (action == "dp_back")
                        DpNavigateBack();
                    else
                        _dpEngine?.Execute(action, value, hi);
                }
            }
        });

    /// <summary>
    /// Hardware press-bounce: re-uploads the given key's icon shrunk (pressed=true, on key-down)
    /// or back at full size (pressed=false, on key-up) — mirrors Base Camp, which does the exact
    /// same re-render + re-upload on every physical press/release (see
    /// <c>DisplayPadOperations.UploadImage</c>'s <c>IsBtnPressed</c> branch in the decompiled
    /// worker; there is no separate device-side animation). Chained onto the same per-device
    /// <see cref="_dpUploadChain"/> as every other icon upload so it can never race a profile/page
    /// reload's batch upload on the wire (the documented cause of past icon corruption).
    /// Skipped for animated GIFs (already live-looping via <see cref="DpGifAnimator"/>) and while
    /// a fullscreen image owns the hardware's icons (no per-key icon to shrink).
    /// </summary>
    private void DpUploadPressVisual(int id, int btnIndex, bool pressed)
    {
        string? imgPath = _dpKeys[btnIndex].ImagePath;
        if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) return;
        if (DpGifAnimator.IsAnimatedGif(imgPath)) return;
        if (_dpFullscreenByDevice.TryGetValue(id, out bool fs) && fs) return;

        int rotation = _dpRotation;
        var previous = _dpUploadChain.TryGetValue(id, out var p) ? p : Task.CompletedTask;
        var next = previous.ContinueWith(_ => _dpClient.UploadImage(id, imgPath, btnIndex, rotation, pressed),
            TaskScheduler.Default);
        _dpUploadChain[id] = next;
    }

    private void OnDpProgress(object? sender, JsonElement e) =>
        Dispatcher.BeginInvoke(() => DpLog($"[FW] {e.Get("percent")}%"));

    // ================================================================
    // Key map default
    // ================================================================

    private void DpApplyDefaultKeyMap()
    {
        _dpMatrixToIndex.Clear();
        foreach (var (index, matrix) in DpDefaultKeyMap)
        {
            _dpMatrixToIndex[matrix] = index;
            if (index < _dpKeys.Length) _dpKeys[index].KeyMatrix = matrix;
        }
    }

    // ================================================================
    // Action engine (K2.Core)
    // ================================================================

    internal ButtonActionEngine? _dpEngine;
    internal DisplayPadActionHost? _dpActionHost;

    private void InitDpActionEngine()
    {
        // The DisplayPad ActionHost is separate from the MacroPad one
        _dpActionHost = new DisplayPadActionHost(this);
        _dpEngine = new ButtonActionEngine(_dpActionHost);
        _dpEngine.Start();
    }

    // ================================================================
    // DisplayPad debug mode
    // ================================================================

    // Driven centrally by the General Settings tab (MainWindow.Settings.cs) —
    // see AppSettings.DebugMode. No longer has its own per-device checkbox.
    private void ApplyDpDebugMode(bool debug)
    {
        var vis = debug ? Visibility.Visible : Visibility.Collapsed;
        BtnDpOpen.Visibility       = vis;
        BtnDpClose.Visibility      = vis;
        SepDpOpenDbg.Visibility    = vis;
        SepDpMapKeysDbg.Visibility = vis;
        BtnDpMapKeys.Visibility    = vis;  // remap keys: debug-only, see project rule
        BtnDpResetAll.Visibility   = vis;  // reset keys: debug-only, see project rule
        PnlDpDebugRight.Visibility = vis;
        PnlDpDebugGroup.Visibility = vis;  // common actions: Debug group (Refresh)
        LblDpSdk.Visibility        = vis;  // toolbar: SDK/DLL info label
        DisplayPadKey.DebugMode    = debug;
        foreach (var k in _dpKeys) k.NotifyDebugModeChanged();
    }

    // ================================================================
    // Log
    // ================================================================

    /// <summary>Appends a line to the DisplayPad event console and the log file.
    /// Suppressed entirely when LogLevel is Off (General Settings tab).</summary>
    private void DpLog(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtDpLog.AppendText(line + Environment.NewLine);
        TxtDpLog.ScrollToEnd();
        App.WriteLog("[DP] " + text);
    }

    /// <summary>Public wrapper for <see cref="DpLog"/> used by <see cref="DisplayPadActionHost"/>.</summary>
    internal void DpLogPublic(string text) => DpLog(text);

    /// <summary>
    /// Thread-safe version of <see cref="DpLog"/> — <see cref="DpGifAnimator"/> (and the
    /// fullscreen animator) run their playback loop on a ThreadPool thread via <c>Task.Run</c>,
    /// and <see cref="DpLog"/> touches <c>TxtDpLog</c> (a WPF control) directly. Calling it
    /// off the UI thread throws ("the calling thread cannot access this object") the moment
    /// the first frame is logged — which silently killed the whole animation task before it
    /// ever got to upload a single frame. All log delegates handed to a background-thread
    /// animator MUST go through this, exactly like <c>SatelliteLog</c> already does via
    /// <c>Dispatcher.BeginInvoke(() => DpLog(msg))</c> in <see cref="InitDisplayPadModule"/>.
    /// </summary>
    private void DpLogAsync(string text) => Dispatcher.BeginInvoke(() => DpLog(text));

    // ================================================================
    // Cleanup
    // ================================================================

    private void CleanupDisplayPad()
    {
        DpGifAnimator.StopAll();
        DpFullscreenAnimator.StopAll();
        _dpEngine?.Dispose();
        _dpClient.Dispose();
        _dpStore.Dispose();
    }
}

// ---- Device combo wrapper ----
public sealed class DpDeviceItem(int sdkId, string label)
{
    public int SdkId { get; } = sdkId;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

// ---- Profile combo wrapper ----
public sealed class DpProfileItem(int slot, string label)
{
    public int Slot { get; } = slot;
    public string Label { get; } = label;
    public bool IsNew => Label.StartsWith("+");
    public override string ToString() => Label;
}

// ---- Device table rows ----
public sealed class DpDeviceRow
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string Plugged { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string Brightness { get; set; } = "";
}
