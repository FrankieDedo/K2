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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;
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
    // Wrapped in RemappingDisplayPadClient so every id used below (and by DpGifAnimator/
    // DpFullscreenAnimator/SpotifyCoverService/DisplayPadStore keys) is a STABLE logical
    // id, immune to the SDK renumbering a pad when its USB port changes — see
    // RemappingDisplayPadClient/DisplayPadDeviceMap and the "DisplayPad device mapping"
    // popup in General Settings (DpDeviceMapWindow).
    private readonly IDisplayPadClient _dpClient = new RemappingDisplayPadClient(
        AppSettings.DisplayPadNativeEngine
            ? new DisplayPadNativeClient()
            : new DisplayPadSatelliteClient());
    /// <summary>Internal (not private): read directly by <see cref="DisplayPadBackgroundActionHost"/>,
    /// which executes actions for connected pads other than the foreground tab (see
    /// <see cref="DpActivateBackgroundDevice"/>) and cannot go through the UI-bound fields.</summary>
    internal readonly DisplayPadStore _dpStore = new();

    // ---- Key model ----
    internal readonly DisplayPadKey[] _dpKeys = Enumerable.Range(0, 12)
        .Select(i => new DisplayPadKey(i)).ToArray();
    private readonly Button[] _dpButtons = new Button[12];
    /// <summary>Mapped-keys list for the Key Binding section (LvDpKeys) — mirrors
    /// _mpMappedKeys (MacroPad): only holds keys of the foreground page that HAVE
    /// an action, rebuilt via <see cref="RefreshDpMappedKeys"/> whenever any
    /// _dpKeys entry's HasAction changes (subscribed once in InitDisplayPadModule,
    /// since _dpKeys is mutated in place across every reload/page-navigation path
    /// instead of being recreated — see the _dpKeys field doc).</summary>
    private readonly ObservableCollection<DisplayPadKey> _dpMappedKeys = new();
    private readonly ObservableCollection<DpDeviceRow> _dpDevices = new();
    private readonly ObservableCollection<int> _dpDeviceIds = new();
    /// <summary>Backing list for the "Pages" sidebar section — see <see cref="RefreshDpPagesList"/>.</summary>
    private readonly ObservableCollection<DpPageRow> _dpPages = new();
    /// <summary>Maps SDK ID → progressive label ("DisplayPad 1", "DisplayPad 2"…).</summary>
    private readonly Dictionary<int, string> _dpDeviceLabels = new();
    private readonly Dictionary<int, int> _dpMatrixToIndex = new();
    private int _dpMapAwaitingIndex = -1;
    private bool _dpSuppressProfile;
    private bool _dpSuppressBrightness;
    private bool _dpSuppressRotation;
    private bool _dpSuppressAutoOff;
    private int _dpRotation; // 0, 90, 270

    /// <summary>Backlight-off-when-idle timers, one per physical DisplayPad
    /// (device setting, global across profiles — DisplayPad supports several
    /// simultaneously connected units, each with its own countdown). Lazily
    /// created by <see cref="DpGetAutoOffTimer"/>, disposed when a device
    /// disappears (see the "goneId" cleanup in DpRefreshDevices).</summary>
    private readonly Dictionary<int, BacklightIdleTimer> _dpAutoOffTimers = new();
    private readonly Dictionary<int, int> _dpSavedBrightness = new();

    /// <summary>Screensaver idle timers, one per physical DisplayPad — same
    /// <see cref="BacklightIdleTimer"/> mechanism as the backlight auto-off above
    /// (idle countdown + wake on the first key event), but wired to show/hide the
    /// page's fullscreen image instead of touching brightness. Unlike auto-off this
    /// is NOT a device-global setting: it is configured per device+profile+page from
    /// whatever page is currently displayed (see <see cref="DpConfigureScreensaver"/>,
    /// called from both repaint paths).</summary>
    private readonly Dictionary<int, BacklightIdleTimer> _dpScreensaverTimers = new();
    /// <summary>Per-device: the screensaver image is currently on the panel (so the next
    /// key event must restore the profile's icons before running its action).</summary>
    private readonly HashSet<int> _dpScreensaverShowing = new();
    /// <summary>Matrix code of the key whose press dismissed the screensaver, per device:
    /// that press (and its matching release) only wakes the panel, it must NOT run the key's
    /// action — see <see cref="OnDpKey"/>.</summary>
    private readonly Dictionary<int, int> _dpScreensaverWakeKey = new();

    /// <summary>
    /// Cache folder for images auto-generated from an action (exec icon / folder glyph,
    /// see <see cref="DpKeyConfigDialog.TryAutoGenerateKeyImage"/>) — generated upright,
    /// like any other image; rotated for the device's mounting the same way at upload
    /// time (<see cref="_dpRotation"/>), no special-casing needed.
    /// </summary>
    /// <remarks>Internal, not private: <see cref="Services.DiscordVoiceKeyService"/> needs it
    /// to tell an auto-generated icon from one the user picked themselves.</remarks>
    internal static readonly string DpAutoIconDir = Path.Combine(
        K2Paths.For("K2.DisplayPad"), "auto_icons");

    /// <summary>Cache path for an auto-generated icon, under <see cref="DpAutoIconDir"/>.</summary>
    private static string DpAutoIconCachePath(string kind, string sourceValue)
    {
        Directory.CreateDirectory(DpAutoIconDir);
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}|{sourceValue}"));
        return Path.Combine(DpAutoIconDir, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
    }

    // ---- Folder / sub-page navigation ----
    private int _currentDpPageId = 0;
    private string? _currentDpFolderName = null;
    private readonly Stack<(int PageId, string? Name)> _dpPageHistory = new();
    /// <summary>Which device the foreground navigation state above currently belongs to —
    /// see the handoff in <see cref="DpActivateDevice"/>. Distinct from _activeDpDeviceId,
    /// which TcDevices_SelectionChanged reassigns BEFORE DpActivateDevice runs.</summary>
    private int? _dpNavStateDeviceId;

    // ---- Default key map (same as K2.DisplayPad) ----
    private static readonly (int Index, int Matrix)[] DpDefaultKeyMap =
    {
        (0,  0x08), (1,  0x11), (2,  0x1A), (3,  0x23),
        (4,  0x2C), (5,  0x35), (6,  0x3E), (7,  0x47),
        (8,  0x50), (9,  0x59), (10, 0x62), (11, 0x7D),
    };

    /// <summary>
    /// Matrix→index map for connected DisplayPads that are NOT the foreground tab (see
    /// <see cref="DpHandleBackgroundKey"/>). There is no persisted per-device remap for the
    /// DisplayPad (unlike MacroPad's <c>GetKeyMap</c>) — <c>_dpMatrixToIndex</c>/remap-mode
    /// only ever apply to whichever device is on-screen, so every physical pad uses the same
    /// hardware-constant default map; built once from <see cref="DpDefaultKeyMap"/>.
    /// </summary>
    private static readonly Dictionary<int, int> DpDefaultMatrixToIndex =
        DpDefaultKeyMap.ToDictionary(m => m.Matrix, m => m.Index);

    // ---- Background devices: connected DisplayPads the user hasn't opened the tab for yet.
    // _dpKeys/_dpMatrixToIndex/_currentDpPageId only ever reflect the foreground tab
    // (_activeDpDeviceId); every OTHER connected pad still needs to respond to its own
    // physical key presses using ITS OWN persisted profile/page — see DpActivateBackgroundDevice/
    // DpHandleBackgroundKey/DpUploadPageForDevice.
    private readonly Dictionary<int, ButtonActionEngine> _dpBgEngines = new();
    private readonly Dictionary<int, int> _dpBgPageId = new();
    private readonly Dictionary<int, Stack<int>> _dpBgPageHistory = new();

    // ---- Canvas layout (mkd_bg.png coordinates at 510×370, same graphic as MacroPad) ----
    private const double DpKeyW = 60;   // BC: 60×60
    private const double DpKeyH = 60;
    private const double DpGapH = 8;
    private const double DpGapV = 10;

    // ---- Drag & drop (swap two keys' action + icon) — mirrors K2.DisplayPad's
    // standalone MainWindow (CellButton_*/SwapCells) and MacroPad's KeyButton_* ----
    private const string DpKeyDragFormat = "K2.DpKeyIndex";
    private Point _dpDragStartPoint;
    private DisplayPadKey? _dpDragCandidate;

    // ================================================================
    // Initialization (called from MainWindow constructor)
    // ================================================================

    private void InitDisplayPadModule()
    {
        // Live Discord voice page (MainWindow.DisplayPad.DiscordRoom.cs) — subscribes once and
        // opens the page straight away if K2 starts while the user is already in a call.
        DvpEnsureHooked();

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
            btn.AllowDrop = true;
            btn.PreviewMouseLeftButtonDown += DpKeyButton_PreviewMouseLeftButtonDown;
            btn.PreviewMouseMove += DpKeyButton_PreviewMouseMove;
            btn.DragEnter += DpKeyButton_DragEnter;
            btn.DragLeave += DpKeyButton_DragLeave;
            btn.Drop += DpKeyButton_Drop;
            _dpButtons[i] = btn;
        }

        DpRebuildKeyGrid();
        DpApplyDefaultKeyMap();
        DpInitLiveKeyPreviewTimer();

        LvDpKeys.ItemsSource = _dpMappedKeys;
        foreach (var k in _dpKeys)
            k.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName == nameof(DisplayPadKey.HasAction)) RefreshDpMappedKeys();
            };

        // The Google Home account can be connected/disconnected from the setup window at any
        // time, independently of any key's own ActionType/ActionValue — refresh the foreground
        // tab's warning triangles (K2.Core.Services.GoogleHomeStore.ConnectionChanged) whenever
        // that happens. Background device tabs repaint from their own stored profile on
        // activation, which already reads the live connection flag.
        K2.Core.Services.GoogleHomeStore.ConnectionChanged += () =>
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var k in _dpKeys) k.NotifyGoogleHomeConnectionChanged();
            });

        LvDpDevices.ItemsSource = _dpDevices;
        // DP device tabs are added to TcDevices by DpRefreshDevices; LstDpProfile by DpRefreshProfiles
        LstDpProfile.ContextMenu = DpBuildProfileContextMenu();
        BtnDpProfileMenu.ContextMenu = DpBuildProfileMenuNoEdit();

        LstDpPages.ItemsSource = _dpPages;

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
        if (ok)
        {
            DpRefreshDevices();
        }
        else
        {
            // DpRefreshDevices (the only place that adds "dp_*" tabs to TcDevices) is never
            // reached when Open fails — so without this line, "no DisplayPad tab" shows up in
            // the log as nothing at all, indistinguishable from "DpRefreshDevices ran and found
            // zero devices". Spelling it out here turns a silent no-op into a diagnosable fact.
            DpLog("Open failed — DisplayPad tab will NOT be created this session " +
                  "(DpRefreshDevices/device enumeration never runs without a successful Open)");
        }
    }

    /// <summary>
    /// Startup-only variant of <see cref="DpOpenDriver"/>: right after Open() succeeds, the
    /// vendor SDK still reports zero devices for a bit — <c>lstDeviceID</c> (and its
    /// per-slot fallback, see SdkHandler.CmdDeviceIds) is only populated once the SDK's own
    /// async plug callback fires, same path as a live hardware replug — so a refresh done
    /// immediately after Open() finds nothing, and the "dp_*" tabs only appear a couple of
    /// seconds later, on top of an already-visible Home. Polls DeviceIds() until two
    /// consecutive reads agree (including two empty reads, for the "nothing connected" case)
    /// before the caller (AutoOpenDrivers) hides the loading overlay, so the tabs are already
    /// there when Home is first shown. Capped at maxWaitMs total so a truly empty setup
    /// doesn't hang the startup sequence.
    /// </summary>
    internal async Task DpOpenDriverAutoAsync()
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
        LblDpSdk.Text = "DisplayPadSDK (satellite x64)";

        var result = _dpClient.Open();
        bool ok = result?.GetBool("ok") ?? false;
        LblStatus.Text = ok ? Loc.Get("dp_driver_opened") : Loc.Get("dp_driver_open_failed");
        DpLog($"Open -> {ok}");
        if (!ok)
        {
            DpLog("Open failed — DisplayPad tab will NOT be created this session " +
                  "(DpRefreshDevices/device enumeration never runs without a successful Open)");
            return;
        }

        const int pollMs = 300, requiredStableHits = 2, maxWaitMs = 3000;
        int lastCount = -1, stableHits = 0, waited = 0;
        while (waited < maxWaitMs)
        {
            int count = _dpClient.DeviceIds().Count;
            if (count == lastCount) stableHits++;
            else { stableHits = 1; lastCount = count; }
            if (stableHits >= requiredStableHits) break;
            await Task.Delay(pollMs);
            waited += pollMs;
        }

        DpRefreshDevices();
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

        string cacheRoot = Path.Combine(K2Paths.For("K2.DisplayPad"), "user_rotated");
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
        if (LstDpProfile.SelectedItem is not DpProfileItem pi || pi.IsNew) return;
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
        if (LstDpProfile.SelectedItem is not DpProfileItem pi || pi.IsNew) return;
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
        DpDeleteProfileSlot(id, slot);
        DpLog($"[UI] Profile {slot} deleted.");
        DpRefreshProfiles(id);
        // LstDpProfile_SelectionChanged will reload the key grid automatically
    }

    /// <summary>Clears one profile slot's buttons+name — no "last profile" guard
    /// (that's the button handler's job). Also used by the Base Camp wipe-before-import
    /// flow (<see cref="DpImportBcForDevice"/>), which intentionally clears every slot
    /// including the last one, since fresh profiles replace them right after.</summary>
    private void DpDeleteProfileSlot(int deviceId, int slot)
    {
        _dpStore.DeleteProfile(deviceId, slot);
    }

    /// <summary>Gear-icon popup for a DisplayPad profile row (see ProfileGear_Click in
    /// MainWindow.xaml.cs): rename, delete (same guard as <see cref="BtnDpDeleteProfile_Click"/>),
    /// or link an executable whose launch auto-switches to this profile (see
    /// K2.Core.Services.ProfileLaunchWatcher, registered from <see cref="DpRefreshProfiles"/>).</summary>
    private void DpShowProfileGear(DpProfileItem pi)
    {
        if (DpSelectedDeviceId() is not int id) return;
        string currentName = _dpStore.GetProfileName(id, pi.Slot) ?? Loc.Get("profile_n", pi.Slot);
        string currentExe = _dpStore.GetSetting($"profile.{id}.{pi.Slot}.launchExe") ?? "";
        var dlg = new ProfileSettingsDialog(currentName, currentExe) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.DeleteRequested)
        {
            var existing = _dpStore.GetExistingProfiles(id);
            if (existing.Count <= 1)
            {
                MessageBox.Show(Loc.Get("delete_profile_last"),
                    Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var res = MessageBox.Show(
                Loc.Get("delete_profile_confirm", currentName),
                Loc.Get("delete_profile"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
            DpDeleteProfileSlot(id, pi.Slot);
            DpLog($"[UI] Profile {pi.Slot} deleted (gear).");
        }
        else
        {
            _dpStore.SetProfileName(id, pi.Slot, dlg.ProfileName);
            _dpStore.SetSetting($"profile.{id}.{pi.Slot}.launchExe", dlg.ExePath);
            DpLog($"[UI] Profile {pi.Slot} settings updated (gear).");
        }
        DpRefreshProfiles(id);
        DpSelectProfileSlot(_dpStore.GetCurrentProfile(id));
    }

    /// <summary>Wipes EVERY profile of the selected DisplayPad unit back to K2's defaults:
    /// other profiles are deleted outright, pages/folders included (mirrors
    /// BtnDpDeleteProfile_Click/DpDeleteProfileSlot), the current one keeps its name but
    /// has its button icons/actions/pages cleared, then the device is repainted. User
    /// request 2026-07-29 (previously only reset the current profile).</summary>
    private void BtnDpRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_device_confirm", Loc.Get("tab_displaypad")),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        int current = DpCurrentProfile();
        foreach (var slot in _dpStore.GetExistingProfiles(id))
            if (slot != current) DpDeleteProfileSlot(id, slot);

        _dpStore.ClearProfile(id, current);
        DpLog($"[UI] DisplayPad device {id} restored to factory defaults (all profiles).");
        DpRefreshProfiles(id);
        ResetDpNavigation();
        DpRequestRepaint(id);
    }

    // ================================================================
    // Screensaver / fullscreen image (whole 2×6 panel — see DpFullscreenAnimator)
    //
    // Two modes share the same stored image (see DisplayPadStore.GetScreensaverDelay):
    //   • delay disabled — the image owns the panel for as long as the page is shown
    //     (the original always-on "fullscreen image" behaviour);
    //   • delay enabled  — real screensaver: the profile's own per-key icons are shown,
    //     the image only takes over after N seconds with no physical key press, and the
    //     first press dismisses it (restoring the icons) BEFORE running that key's
    //     action — see DpScreensaverTimeout/DpScreensaverWake and OnDpKey.
    // ================================================================

    private void BtnDpFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        int profile = DpCurrentProfile();
        int pageId = _currentDpPageId;

        var current = _dpStore.GetFullscreenImage(id, profile, pageId);
        var delay = _dpStore.GetScreensaverDelay(id, profile, pageId);
        var result = ShowFullscreenDialog(current?.Path, current?.Rotation ?? 0, delay.Enabled, delay.Seconds);
        if (result is not { } picked) return;   // cancelled

        _dpStore.SetFullscreenImage(id, profile, pageId, picked.Path, picked.Rotation);
        _dpStore.SetScreensaverDelay(id, profile, pageId, picked.DelayEnabled, picked.DelaySeconds);
        DpLog($"[FS] device {id} profile {profile} page {pageId} <- {Path.GetFileName(picked.Path)} " +
              $"(rot user={picked.Rotation}, screensaver={(picked.DelayEnabled ? picked.DelaySeconds + "s" : "off")})");
        LblStatus.Text = Loc.Get("dp_fullscreen_set_ok");
        DpRequestRepaint(id);   // also (re)configures this device's screensaver timer
    }

    private void BtnDpFullscreenClear_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        int profile = DpCurrentProfile();
        int pageId = _currentDpPageId;

        if (_dpStore.GetFullscreenImage(id, profile, pageId) is null) return;
        _dpStore.ClearFullscreenImage(id, profile, pageId);
        DpFullscreenAnimator.Stop(id);
        _dpScreensaverShowing.Remove(id);   // the delay setting is kept, but has nothing to show now
        DpLog($"[FS] device {id} profile {profile} page {pageId}: cleared");
        LblStatus.Text = Loc.Get("dp_fullscreen_cleared");
        DpRequestRepaint(id);
    }

    /// <summary>
    /// DEBUG: toggles <see cref="DpFullscreenAnimator.StartLiveTest"/> — a synthetic moving
    /// pattern streamed straight to the raw panel, used to measure real sustained fps of the
    /// <c>SetPanelImage</c>/panel wire path instead of guessing from protocol pacing alone.
    /// Shares the fullscreen slot, so it temporarily takes over from whatever fullscreen
    /// image/GIF is configured for the current page; stopping it restores the normal repaint.
    /// </summary>
    private void BtnDpLiveTest_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;

        if (_dpLiveTestActive.Contains(id))
        {
            DpFullscreenAnimator.Stop(id);
            _dpLiveTestActive.Remove(id);
            DpLog($"[DP-FS] dev {id}: live test stopped, restoring normal repaint.");
            DpRequestRepaint(id);
            return;
        }

        if (!_dpClient.SupportsRawPanel)
        {
            DpLog($"[DP-FS] dev {id}: live test needs the native raw-panel backend — not available on this connection.");
            return;
        }

        _dpLiveTestActive.Add(id);
        DpFullscreenAnimator.StartLiveTest(_dpClient, DpLogAsync, id);
    }

    /// <summary>
    /// DEBUG: toggles <see cref="DpGifAnimator.StartLiveTest"/> on key #0 — same idea as
    /// <see cref="BtnDpLiveTest_Click"/> but for a SINGLE icon instead of the whole panel, to
    /// compare real measured fps between the two paths (single icon is ~1/18th the bytes per
    /// frame, so it should land far above the panel's measured ~5 fps).
    /// </summary>
    private void BtnDpLiveTestIcon_Click(object sender, RoutedEventArgs e)
    {
        if (DpSelectedDeviceId() is not int id) return;
        const int testKey = 0;

        if (_dpLiveTestIconActive.Contains(id))
        {
            DpGifAnimator.Stop(id, testKey);
            _dpLiveTestIconActive.Remove(id);
            DpLog($"[DP-GIF] dev {id}: live icon test stopped, restoring normal repaint.");
            DpRequestRepaint(id);
            return;
        }

        _dpLiveTestIconActive.Add(id);
        DpGifAnimator.StartLiveTest(_dpClient, DpLogAsync, id, testKey);
    }

    /// <summary>
    /// Picker dialog (built in code, same lightweight pattern as <see cref="ShowRenameDialog"/>):
    /// browse for an image/GIF, preview it live via the inline <see cref="CropEditor"/>
    /// (which handles both statics and animated GIFs — 2026-07-05, this dialog previously
    /// had NO image preview at all, not even for the cropped result), and pick a
    /// 0/90/180/270 user-rotation for the whole picture (independent of, and applied before,
    /// the per-tile device counter-rotation — see DpFullscreenAnimator remarks). Crop/zoom
    /// stays in THIS window (no separate popup). Also carries the screensaver delay
    /// (checkbox + seconds, mirroring the Settings tab's "turn off backlight when idle"
    /// row) — see the section header above for what the two modes mean.
    /// Returns null if cancelled.
    /// </summary>
    private (string Path, int Rotation, bool DelayEnabled, int DelaySeconds)? ShowFullscreenDialog(
        string? currentPath, int currentRotation, bool currentDelayEnabled, int currentDelaySeconds)
    {
        (string Path, int Rotation, bool DelayEnabled, int DelaySeconds)? result = null;
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

        // Screensaver delay — same shape as the Settings tab's backlight auto-off row
        // (CkDpAutoOffEnable / TxtDpAutoOffSeconds): checkbox + seconds entry + "s".
        var ckDelay = new CheckBox
        {
            Content = Loc.Get("dp_screensaver_delay_enable"),
            IsChecked = currentDelayEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var txtDelay = new TextBox
        {
            Text = (currentDelaySeconds > 0 ? currentDelaySeconds : DisplayPadStore.DefaultScreensaverSeconds).ToString(),
            Width = 60,
            Height = 25,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 2, 5, 0),
        };
        var delayPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 4) };
        delayPanel.Children.Add(ckDelay);
        delayPanel.Children.Add(txtDelay);
        delayPanel.Children.Add(new TextBlock
        {
            Text = Loc.Get("unit_seconds"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var delayHint = new TextBlock
        {
            Text = Loc.Get("dp_screensaver_delay_hint"),
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
        panel.Children.Add(delayPanel);
        panel.Children.Add(delayHint);
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
            bool delayOn = ckDelay.IsChecked == true;
            if (!int.TryParse(txtDelay.Text, out int delaySeconds) || delaySeconds <= 0)
                delaySeconds = DisplayPadStore.DefaultScreensaverSeconds;
            result = (finalPath, rotation, delayOn, delaySeconds);
            dlg.Close();
        };
        btnCancel.Click += (_, _) => dlg.Close();

        dlg.ShowDialog();
        return result;
    }

    /// <summary>SDK ID of the active DisplayPad (set by TcDevices_SelectionChanged in xaml.cs).</summary>
    internal int? _activeDpDeviceId;
    private int? DpSelectedDeviceId() => _activeDpDeviceId;

    private BacklightIdleTimer DpGetAutoOffTimer(int id)
    {
        if (!_dpAutoOffTimers.TryGetValue(id, out var t))
        {
            t = new BacklightIdleTimer(Dispatcher, () => DpAutoOffTimeout(id), () => DpAutoOffWake(id));
            _dpAutoOffTimers[id] = t;
        }
        return t;
    }

    private BacklightIdleTimer DpGetScreensaverTimer(int id)
    {
        if (!_dpScreensaverTimers.TryGetValue(id, out var t))
        {
            t = new BacklightIdleTimer(Dispatcher, () => DpScreensaverTimeout(id), () => DpScreensaverWake(id));
            _dpScreensaverTimers[id] = t;
        }
        return t;
    }

    /// <summary>Which page a device is currently showing — the foreground tab's navigation
    /// state for the visible device, the per-device background one for every other.</summary>
    private int DpCurrentPageIdFor(int id) =>
        DpSelectedDeviceId() == id ? _currentDpPageId : _dpBgPageId.GetValueOrDefault(id, 0);

    /// <summary>
    /// (Re)arms the screensaver countdown for a device after a repaint. Always called with
    /// the settings of the page that was just painted; <paramref name="enabled"/> is false
    /// when that page has no screensaver image or the delay is switched off (the image then
    /// behaves like the old always-on fullscreen mode, painted by the repaint itself).
    /// <see cref="BacklightIdleTimer.RegisterActivity"/> at the end clears any latched
    /// "already fired" state left over from the previous page/profile — without it the
    /// timer would refuse to fire again after the first time.
    /// </summary>
    private void DpConfigureScreensaver(int id, bool enabled, int seconds)
    {
        _dpScreensaverShowing.Remove(id);
        var t = DpGetScreensaverTimer(id);
        t.Configure(enabled, seconds);
        t.RegisterActivity();
    }

    /// <summary>Idle timeout: the page's image takes over the whole panel.</summary>
    private void DpScreensaverTimeout(int id)
    {
        if (!_dpDeviceIds.Contains(id)) return;
        // The emoji browser owns the panel while it is up; waking from the screensaver
        // repaints the stored page, which would silently drop the browser under the user.
        if (DpEmojiBrowserActive(id)) return;
        int profile = _dpStore.GetCurrentProfile(id);
        int pageId = DpCurrentPageIdFor(id);
        var image = _dpStore.GetFullscreenImage(id, profile, pageId);
        if (image is null || !File.Exists(image.Value.Path)) return;
        if (!_dpStore.GetScreensaverDelay(id, profile, pageId).Enabled) return;

        _dpScreensaverShowing.Add(id);
        // Same flag the always-on fullscreen mode sets: it suppresses the per-key
        // press-bounce re-uploads that would otherwise punch holes in the image.
        _dpFullscreenByDevice[id] = true;
        // Per-key GIF loops would keep repainting their own tiles over the screensaver.
        DpGifAnimator.StopAllForDevice(id);
        // Same reason: a live clock/monitor tile would tick right over the screensaver image.
        DpLiveTileService.Stop(id);

        int rotation = _dpStore.GetRotation(id);
        var (path, userRotation) = image.Value;
        DpLog($"[FS] device {id}: screensaver on ({Path.GetFileName(path)})");
        var previous = _dpUploadChain.TryGetValue(id, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[id] = previous.ContinueWith(
            _ => DpFullscreenAnimator.Start(_dpClient, DpLogAsync, id, path, userRotation, rotation),
            TaskScheduler.Default);
    }

    /// <summary>First key event after the screensaver kicked in: drop the image and repaint
    /// the page's own icons. The key's action does NOT run — see <see cref="OnDpKey"/>, which
    /// swallows the press (and its release) that dismissed the screensaver.</summary>
    private void DpScreensaverWake(int id)
    {
        if (!_dpScreensaverShowing.Remove(id)) return;
        DpFullscreenAnimator.Stop(id);
        _dpFullscreenByDevice[id] = false;
        DpLog($"[FS] device {id}: screensaver dismissed — restoring page icons");
        DpRequestRepaint(id);
    }

    private void DpAutoOffTimeout(int id)
    {
        int b = _dpClient.GetBrightness(id);
        _dpSavedBrightness[id] = b >= 0 ? b : (int)SldDpBrightness.Value;
        DpLog($"[UI] auto-off: SetBrightness(id={id}, 0) -> {_dpClient.SetBrightness(id, 0)}");
    }

    private void DpAutoOffWake(int id)
    {
        int restore = _dpSavedBrightness.TryGetValue(id, out var b) && b > 0 ? b : 100;
        DpLog($"[UI] auto-off wake: SetBrightness(id={id}, {restore}) -> {_dpClient.SetBrightness(id, restore)}");
        if (DpSelectedDeviceId() == id)
        {
            _dpSuppressBrightness = true;
            try { SldDpBrightness.Value = restore; LblDpBrightness.Text = $"{restore}%"; }
            finally { _dpSuppressBrightness = false; }
        }
    }

    private void CkDpAutoOffEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_dpSuppressAutoOff) return;
        if (DpSelectedDeviceId() is not int id) return;
        _dpStore.SetSetting($"device.{id}.autoOffEnable", CkDpAutoOffEnable.IsChecked == true ? "1" : "0");
        DpApplyAutoOffConfig(id);
    }

    private void TxtDpAutoOffSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_dpSuppressAutoOff) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (!int.TryParse(TxtDpAutoOffSeconds.Text, out int seconds) || seconds < 0)
        {
            seconds = 60;
            TxtDpAutoOffSeconds.Text = seconds.ToString();
        }
        _dpStore.SetSetting($"device.{id}.autoOffSeconds", seconds.ToString());
        DpApplyAutoOffConfig(id);
    }

    private void DpApplyAutoOffConfig(int id)
    {
        bool enabled = CkDpAutoOffEnable.IsChecked == true;
        int  seconds = int.TryParse(TxtDpAutoOffSeconds.Text, out int s) ? s : 0;
        DpGetAutoOffTimer(id).Configure(enabled, seconds);
    }

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

        // The folder-navigation state (_currentDpPageId/_dpPageHistory) is per-device but
        // lives in shared foreground fields: on a device change, stash it into the OLD
        // device's background maps (its hardware still shows that page, and its physical
        // keys must keep resolving against it — see DpHandleBackgroundKey) and adopt the
        // NEW device's own background state. Without this, opening a folder on pad A and
        // then clicking pad B's tab showed B "inside" A's page.
        if (_dpNavStateDeviceId != id)
        {
            if (_dpNavStateDeviceId is int prev && _dpDeviceIds.Contains(prev))
            {
                _dpBgPageId[prev] = _currentDpPageId;
                // Stack<T> enumerates top→bottom and its IEnumerable ctor pushes in order,
                // so feed it bottom→top to preserve orientation.
                _dpBgPageHistory[prev] = new Stack<int>(_dpPageHistory.Reverse().Select(h => h.PageId));
            }
            _dpPageHistory.Clear();
            if (_dpBgPageHistory.TryGetValue(id, out var hist))
                foreach (int pid in hist.Reverse())
                    _dpPageHistory.Push((pid, pid == 0 ? null : _dpStore.GetFolderName(pid)));
            _currentDpPageId = _dpBgPageId.GetValueOrDefault(id, 0);
            _currentDpFolderName = _currentDpPageId == 0 ? null : _dpStore.GetFolderName(_currentDpPageId);
            UpdateDpBreadcrumb();
            _dpNavStateDeviceId = id;
        }

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

        _dpSuppressAutoOff = true;
        try
        {
            bool aoEnabled = _dpStore.GetSetting($"device.{id}.autoOffEnable") == "1";
            int  aoSeconds = int.TryParse(_dpStore.GetSetting($"device.{id}.autoOffSeconds"), out var aoS) ? aoS : 60;
            CkDpAutoOffEnable.IsChecked = aoEnabled;
            TxtDpAutoOffSeconds.Text = aoSeconds.ToString();
            DpGetAutoOffTimer(id).Configure(aoEnabled, aoSeconds);
        }
        finally { _dpSuppressAutoOff = false; }

        DpReloadAndPreloadProfile();
        DpSyncSpotifyCoverService(id);
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
            if (LstDpProfile.ItemsSource is not List<DpProfileItem> items) return;
            real = items.Where(x => !x.IsNew).Select(x => x.Slot).ToList();
            cur  = LstDpProfile.SelectedItem is DpProfileItem pi ? pi.Slot : (real.Count > 0 ? real[0] : 1);
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
        else
        {
            // Named-profile target — see MainWindow.Everest.cs's EvSwitchProfile for the
            // rationale (Base Camp XML/DB can carry a destination profile NAME instead of
            // Next/Previous/a slot number).
            int? byName = null;
            foreach (var s in real)
                if (string.Equals(_dpStore.GetProfileName(id, s), t, StringComparison.OrdinalIgnoreCase)) { byName = s; break; }
            if (byName is null) { DpLog($"[EXEC] profile: target \"{t}\" not resolved"); return; }
            nextSlot = byName;
        }

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
        else
        {
            // Background-device counterpart of ResetDpNavigation() above: a profile switch
            // always lands on that device's root page, same as the foreground path.
            _dpBgPageId[id] = 0;
            if (_dpBgPageHistory.TryGetValue(id, out var hist)) hist.Clear();
        }
        // Hardware repaint is serialized + coalesced per device (see DpRequestRepaint):
        // the store/UI switched instantly above; the device repaints when free.
        DpRequestRepaint(id);
        DpSyncSpotifyCoverService(id);
        DpLog($"[EXEC] DisplayPad profile -> {slot} (device {id})");
    }

    /// <summary>Reserved profile name that marks a DisplayPad profile as the built-in
    /// "Spotify" one (see <see cref="DpCreateSpotifyProfile"/>/<see cref="DpSyncSpotifyCoverService"/>).
    /// Profile identity is otherwise just an integer slot, so the name is the only
    /// slot-independent way to recognize it.</summary>
    private const string SpotifyProfileName = "Spotify";

    /// <summary>Starts/stops the live Spotify cover-art overlay (see SpotifyCoverService) for
    /// this device based on whether its CURRENT profile is the reserved "Spotify" profile
    /// (identified by name, since profile identity itself is just an integer slot). Called
    /// on every device activation and profile switch so the overlay always tracks whichever
    /// profile is actually showing.</summary>
    private void DpSyncSpotifyCoverService(int id)
    {
        int profile = _dpStore.GetCurrentProfile(id);
        bool isSpotify = _dpStore.GetProfileName(id, profile) == SpotifyProfileName;
        if (isSpotify)
            SpotifyCoverService.Start(_dpClient, DpLogAsync, id, _dpStore.GetRotation(id));
        else
            SpotifyCoverService.Stop(id);
    }

    /// <summary>
    /// Creates the reserved "Spotify" DisplayPad profile for the active device (if it
    /// doesn't already exist) and switches to it. Layout: keys 0,1,6,7 (the left 2×2 block)
    /// are left with no ActionType/ImagePath — they're driven live by SpotifyCoverService,
    /// not a persisted per-key icon — while 2,3,4,5,8,9,10,11 get the 8 existing media
    /// actions (Prev/Play-Pause/Next/Shuffle/VolDown/VolUp/Mute/Stop) with an auto-generated
    /// caption tile, same pattern as DpMnuSetBack_Click's auto-icon.
    /// </summary>
    private void DpCreateOrSwitchSpotifyProfile()
    {
        if (DpSelectedDeviceId() is not int id) return;

        var existing = _dpStore.GetExistingProfiles(id);
        int slot = existing.FirstOrDefault(s => _dpStore.GetProfileName(id, s) == SpotifyProfileName);
        if (slot == 0)
        {
            slot = BaseCampDbImporter.FindFreeSlot(existing, maxSlots: 999);
            _dpStore.ClearProfile(id, slot);
            _dpStore.SetProfileName(id, slot, SpotifyProfileName);

            (int Btn, string Value, string LocKey)[] seeds =
            {
                (2,  "Previous track", "media_prev"),
                (3,  "Play/Pause",     "media_play_pause"),
                (4,  "Next track",     "media_next"),
                (5,  "Shuffle",        "media_shuffle"),
                (8,  "Volume Down",    "media_vol_down"),
                (9,  "Volume Up",      "media_vol_up"),
                (10, "Mute",           "media_mute"),
                (11, "Stop",           "media_stop"),
            };
            foreach (var (btn, value, locKey) in seeds)
            {
                string dest = DpAutoIconCachePath("spotify_media", value);
                string? img = IconImageGenerator.TryGenerateCaptionIcon(Loc.Get(locKey), DpHidNative.IconSize, dest)
                    ? dest : null;
                _dpStore.SaveButton(id, slot, btn, img, "media", value);
            }
            // Materializes key 0 too (cover tile, no action) so the profile "exists" even
            // if icon generation above failed for every seed.
            _dpStore.SaveButton(id, slot, 0, null, null, null);
            DpLog($"[UI] Spotify profile created: slot {slot} (device {id})");
        }

        DpRefreshProfiles(id);
        _dpStore.SetCurrentProfile(id, slot);
        DpSelectProfileSlot(slot);
        ResetDpNavigation();
        DpRequestRepaint(id);
        DpSyncSpotifyCoverService(id);
        DpLog($"[UI] Switched to Spotify profile (device {id})");
    }

    /// <summary>Selects a slot in the profile combo (suppressing the event).</summary>
    private void DpSelectProfileSlot(int slot)
    {
        _dpSuppressProfile = true;
        try
        {
            if (LstDpProfile.ItemsSource is not List<DpProfileItem> items) return;
            // A dedicated profile (Spotify) is deliberately absent from this list — it lives in
            // the "Dedicated profiles" section below. Falling back to items[0] there would show
            // the wrong profile as current; the panel's owner is mirrored in that list instead.
            var row = items.Find(x => x.Slot == slot && !x.IsNew);
            if (row is null && DpSelectedDeviceId() is int devId && DpActiveDedicated(devId) is string ded)
            {
                LstDpProfile.SelectedItem = null;
                DpSelectDedicated(ded);
                return;
            }
            LstDpProfile.SelectedItem = row ?? items[0];
            DpSelectDedicated(null);
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
            if (existing.Count == 0)
            {
            // No profile at all — fresh install, hardware factory reset or the Settings
            // tab's "Restore all defaults": recreate one instead of only showing a
            // phantom slot 1 under the generic "Profile 1" label. Named "Default
            // profile" (localized, `default_profile_name`), the same name Base Camp
            // gives its own starting profile. User request 2026-08-21.
                if (_dpStore.GetProfileName(deviceId, 1) is null)
                    _dpStore.SetProfileName(deviceId, 1, Loc.Get("default_profile_name"));
                existing.Add(1);
            }
            var items = new List<DpProfileItem>();
            foreach (var slot in existing)
            {
                string name = _dpStore.GetProfileName(deviceId, slot) ?? Loc.Get("profile_n", slot);
                // Reserved slot of a dedicated profile: it is listed in the "Dedicated profiles"
                // section instead (see MainWindow.DisplayPad.Dedicated.cs), never twice.
                if (DpIsDedicatedName(name)) continue;
                items.Add(new DpProfileItem(slot, name));
            }
            // Find the next free slot — DisplayPad profiles are pure K2-side bookkeeping
            // (see DpSwitchProfile's doc comment), no firmware slot cap, so this is
            // uncapped (999 is just a generous sanity ceiling, not a real limit).
            int nextFree = BaseCampDbImporter.FindFreeSlot(existing, maxSlots: 999);
            if (nextFree > 0)
                items.Add(new DpProfileItem(nextFree, Loc.Get("new_profile")));

            LstDpProfile.ItemsSource = items;

            DpRefreshDedicated(deviceId);

            int current = _dpStore.GetCurrentProfile(deviceId);
            var match = items.Find(x => x.Slot == current && !x.IsNew);
            string? dedicated = DpActiveDedicated(deviceId);
            // While a dedicated profile owns the panel NOTHING above is current — the selection
            // lives in the other list, so the two can never both look active.
            LstDpProfile.SelectedItem = dedicated is not null ? null : match ?? items[0];
            DpSelectDedicated(dedicated);

            DpRegisterProfileLaunchWatchers(deviceId, existing);
        }
        finally { _dpSuppressProfile = false; }
    }

    /// <summary>Registers this device's profiles with K2.Core.Services.ProfileLaunchWatcher
    /// so that launching a linked executable auto-switches to its profile. Called on every
    /// DpRefreshProfiles (rename/delete/create/tab activation) — cheap to rebuild since it's
    /// a handful of dictionary entries, and keeps stale keys (deleted profiles) out.</summary>
    private void DpRegisterProfileLaunchWatchers(int deviceId, List<int> existing)
    {
        string scope = $"Dp:{deviceId}:";
        var currentKeys = new HashSet<string>();
        foreach (var slot in existing)
        {
            string? exe = _dpStore.GetSetting($"profile.{deviceId}.{slot}.launchExe");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string key = scope + slot;
            currentKeys.Add(key);
            int capturedSlot = slot;
            ProfileLaunchWatcher.Instance.UpdateRegistration(key, exe,
                () => DpSwitchProfile(deviceId, capturedSlot.ToString()));
        }
        foreach (var staleKey in ProfileLaunchWatcher.Instance.KeysWithPrefix(scope).Except(currentKeys))
            ProfileLaunchWatcher.Instance.RemoveRegistration(staleKey);
    }

    private void LstDpProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dpSuppressProfile) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (LstDpProfile.SelectedItem is not DpProfileItem pi) return;
        int profile = pi.Slot;

        // Whatever the user picks here, the panel goes back to a normal profile — see
        // DpLeaveDedicatedForProfile for why Discord's exit is only temporary.
        DpLeaveDedicatedForProfile(id);

        if (pi.IsNew)
        {
            var dlg = new NewDisplayPadProfileDialog { Owner = this };
            if (dlg.ShowDialog() != true)
            {
                // Nothing was committed yet — just revert the UI to whatever is actually current.
                DpSelectProfileSlot(_dpStore.GetCurrentProfile(id));
                return;
            }
            if (dlg.IsDedicated)
            {
                // Creates (or switches to) the reserved slot on THIS device and refreshes both
                // lists — see MainWindow.DisplayPad.Dedicated.cs.
                if (dlg.DedicatedType is string dedicatedType) DpCreateDedicatedProfile(dedicatedType);
                return;
            }

            // Generic: create empty profile, save a placeholder to make it appear as existing
            _dpStore.ClearProfile(id, profile);
            // Save at least key 0 empty to make the profile "exist"
            _dpStore.SaveButton(id, profile, 0, null, null, null);
            DpLog($"[UI] New empty profile created: slot {profile}");
            DpRefreshProfiles(id);
            // Select the newly created profile
            _dpSuppressProfile = true;
            try
            {
                var items = LstDpProfile.ItemsSource as List<DpProfileItem>;
                LstDpProfile.SelectedItem = items?.Find(x => x.Slot == profile && !x.IsNew);
            }
            finally { _dpSuppressProfile = false; }
        }

        // See DpSwitchProfile: no native SwitchProfile call (BC never uses it for DisplayPad).
        _dpStore.SetCurrentProfile(id, profile);
        ResetDpNavigation();
        DpLog($"SwitchProfile({id}, {profile})");
        DpRequestRepaint(id);
        DpSyncSpotifyCoverService(id);
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
        // Idem for the clock/monitor tiles: the profile is being wiped, so nothing should keep
        // painting on it (a later repaint re-registers whatever survives).
        DpLiveTileService.Stop(id);
        DpFullscreenAnimator.Stop(id);
        _dpClient.ResetPictures(id);
        _dpStore.ClearProfile(id, profile);
        _dpStore.ClearFullscreenImage(id, profile, _currentDpPageId);
        ResetDpNavigation();
        foreach (var k in _dpKeys) { k.ImagePath = null; k.ActionType = null; k.ActionValue = null; k.IconSpecJson = null; }
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

            // Profile display name from <ProfileName>
            string profileName = root.Element("ProfileName")?.Value
                                 ?? Path.GetFileNameWithoutExtension(dlg.FileName);

            // BC XML structure: <DisplayPadKeyBindings>/<DisplayPadLayerBidings>
            var bindings = root.Descendants("DisplayPadLayerBidings").ToList();
            if (bindings.Count == 0)
            {
                DpLog("[IMP-XML] No DisplayPadLayerBidings found in XML.");
                return;
            }

            // Always land in a FRESH slot — the XML's own <Id> is just wherever the
            // profile happened to live on the machine it was exported from (see
            // BaseCampDbImporter.FindFreeSlot's doc comment).
            int slot = BaseCampDbImporter.FindFreeSlot(_dpStore.GetExistingProfiles(id));
            if (slot == 0)
            {
                MessageBox.Show(this, Loc.Get("import_no_free_slot", profileName),
                    Loc.Get("dp_open_bc_profile"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string iconsDir = Path.Combine(K2Paths.For("K2.DisplayPad"), "imported_xml", profileName);
            Directory.CreateDirectory(iconsDir);

            _dpStore.ClearProfile(id, slot);
            _dpClient.APEnable(id, false);
            int rotation = _dpStore.GetRotation(id);
            int imported = 0;

            // Existing K2 macro names, used by TranslateAction to auto-match a Base Camp
            // named-macro reference ("Default" FunctionType) against the user's own macro
            // library — see BaseCampDbImporter.TranslateDefaultAction's doc comment.
            var macroNames = _macroStore?.GetAll()
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

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
                string? customUrl = b.Element("CustomURL")?.Value;

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

                    // BC's XML rarely carries a real per-key icon for its "Back" button (no
                    // <base64Image>, or a BC-internal path with nothing to decode — see the
                    // image block above). Give it the same auto-generated arrow+caption tile
                    // as the in-app "Set as Back button" menu item (DpMnuSetBack_Click) /
                    // DpEnsureDefaultBackButton, instead of leaving it iconless. Only when the
                    // XML genuinely had no image — a real customized icon is left untouched.
                    if (imagePath is null)
                    {
                        string caption = Loc.Get("dp_back");
                        string dest = DpAutoIconCachePath("dpback", caption);
                        if (IconImageGenerator.TryGenerateBackIcon(caption, DpHidNative.IconSize, dest))
                            imagePath = dest;
                    }
                }
                else
                {
                    (actionType, actionValue) = BaseCampDbImporter.TranslateAction(funcType, subType, funcValue, macroNames, customUrl);
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

            // K2-format extra: the whole per-profile Settings namespace (see
            // K2ProfileSettingsXml). Absent from Base Camp files and from K2 exports made
            // before 2026-08-22, in which case this is a no-op.
            int k2Settings = K2ProfileSettingsXml.Apply(
                root, _dpStore.SetSetting, slot, K2ProfileSettingsXml.SettingsOnlyFamilies);
            if (k2Settings > 0) DpLog($"[IMP-XML] {k2Settings} K2 profile setting(s) restored");

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
        int? currentSlot = LstDpProfile.SelectedItem is DpProfileItem pi && !pi.IsNew ? pi.Slot : null;
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
        // Per-tab button: only ever touches the currently open tab's device — the
        // "overall import" cascade (Settings) instead calls DpImportBcForAllDevices,
        // which repeats this same per-device flow once per connected pad.
        if (DpSelectedDeviceId() is not int id) return;
        DpImportBcForDevice(id);
    }

    /// <summary>Runs the Base Camp import once per currently connected DisplayPad
    /// (used by the "Import from Base Camp" cascade in Settings, MainWindow.Settings.cs) —
    /// if Base Camp's DB has profiles for more than one physical device, the picker in
    /// <see cref="DpImportBcForDevice"/> is shown once per connected pad here.</summary>
    private void DpImportBcForAllDevices()
    {
        foreach (var id in _dpDeviceIds.ToList())
            DpImportBcForDevice(id);
    }

    /// <summary>
    /// Imports Base Camp profiles into ONE K2 DisplayPad device (<paramref name="k2DeviceId"/>).
    /// If the DB has profiles for more than one physical device, prompts the user to choose
    /// which one via <see cref="BcDevicePickerDialog"/> (skipped when there's only one
    /// candidate — nothing to choose). Unlike the old free-slot-seeking import, this always
    /// WIPES every existing K2 profile on the target device first (<see cref="DpDeleteProfileSlot"/>)
    /// so the import replaces rather than appends.
    /// </summary>
    private void DpImportBcForDevice(int k2DeviceId)
    {
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            DpLog("[IMP-BC] BaseCamp.db not found.");
            LblStatus.Text = Loc.Get("dp_bc_db_not_found");
            return;
        }

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

        string deviceLabel = _dpDeviceLabels.GetValueOrDefault(k2DeviceId, $"DisplayPad {k2DeviceId}");

        List<BaseCampDbImporter.BcProfile> profiles;
        if (bcDevices.Count == 1)
        {
            profiles = bcDevices.Values.First();
        }
        else
        {
            var options = bcDevices.Select(kv => (
                BcDeviceId: kv.Key,
                Label: Loc.Get("bc_pick_device_label", kv.Key, kv.Value.Count,
                    string.Join(", ", kv.Value.Select(p => p.Name)))
            )).ToList();
            var picker = new BcDevicePickerDialog(deviceLabel, options) { Owner = this };
            if (picker.ShowDialog() != true) return;
            profiles = bcDevices[picker.SelectedBcDeviceId!.Value];
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Import {profiles.Count} profile(s) into \"{deviceLabel}\"?\n");
        foreach (var p in profiles)
            sb.AppendLine($"  {(p.IsSelected ? "[ACTIVE] " : "")}{p.Name}");
        sb.AppendLine();
        sb.AppendLine(Loc.Get("bc_import_will_wipe", deviceLabel));
        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Pre-read every selected profile's buttons BEFORE wiping anything: this import is
        // destructive (replace, not append), so a corrupt/locked Base Camp DB must surface
        // while the existing K2 profiles are still intact — not after they're gone.
        try
        {
            foreach (var p in profiles)
                BaseCampDbImporter.ReadButtons(dbPath, p.ProfileId);
        }
        catch (Exception ex)
        {
            DpLog($"[IMP-BC] Pre-read failed, aborting before wipe: {ex.Message}");
            MessageBox.Show(this, Loc.Get("bc_import_read_failed", ex.Message),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Wipe: replace, don't append (see DpDeleteProfileSlot's doc comment) — every slot
        // that exists PLUS the first five, which is where an import lands (2026-08-22, same
        // change as the Everest Max import). A slot with no key rows is invisible to
        // GetExistingProfiles yet can still hold stale settings./page rows that would
        // become the starting point of whatever the import puts there. Unlike the other
        // devices the DisplayPad has no firmware slot cap, hence the union rather than a
        // fixed count.
        foreach (var wipeSlot in _dpStore.GetExistingProfiles(k2DeviceId)
                     .Union(Enumerable.Range(1, 5))
                     .OrderBy(x => x))
            DpDeleteProfileSlot(k2DeviceId, wipeSlot);

        int rotation = _dpStore.GetRotation(k2DeviceId);
        // APEnable=false required before SetIconPic (UploadImageToProfile)
        _dpClient.APEnable(k2DeviceId, false);

        var usedSlots = new HashSet<int>();
        int totalButtons = 0, importedProfiles = 0;

        // Existing K2 macro names, used by TranslateAction to auto-match a Base Camp
        // named-macro reference ("Default" FunctionType) against the user's own macro
        // library — same lookup the XML import path already uses (BaseCampDbImporter.
        // TranslateDefaultAction's doc comment), previously missing here so BC.db imports
        // never resolved named macros even when the library had a matching name.
        var macroNames = _macroStore?.GetAll()
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        foreach (var profile in profiles)
        {
            try
            {
                int targetSlot = BaseCampDbImporter.FindFreeSlot(usedSlots, maxSlots: 999);
                if (targetSlot == 0) continue; // sanity ceiling only, practically unreachable
                usedSlots.Add(targetSlot);

                int n = BaseCampDbImporter.ImportProfile(dbPath, profile, k2DeviceId, _dpStore, targetSlot, macroNames);

                // Upload root-page images only (folder pages are uploaded on navigation)
                var buttons = _dpStore.LoadPage(k2DeviceId, targetSlot, 0);
                foreach (var btn in buttons)
                {
                    if (!string.IsNullOrEmpty(btn.ImagePath) && File.Exists(btn.ImagePath))
                    {
                        bool ok = _dpClient.UploadImageToProfile(k2DeviceId, btn.ImagePath,
                            btn.ButtonIndex, targetSlot, rotation);
                        if (!ok)
                            _dpClient.UploadImage(k2DeviceId, btn.ImagePath, btn.ButtonIndex, rotation);
                    }
                }

                DpLog($"[IMP-BC] {profile.Name} -> K2 dev#{k2DeviceId} slot {targetSlot}: {n} keys");
                totalButtons += n;
                importedProfiles++;
            }
            catch (Exception ex)
            {
                DpLog($"[IMP-BC] Import error {profile.Name}: {ex.Message}");
            }
        }

        // Always land on the FIRST imported profile and force a reload — simpler and
        // safer than trying to restore whatever was active in Base Camp (user request:
        // a plain, predictable refresh after import beats guessing at BC's own state).
        int activateSlot = usedSlots.DefaultIfEmpty(0).Min();
        bool isActive = k2DeviceId == DpSelectedDeviceId();

        // DpRefreshProfiles/DpSelectProfileSlot (foreground-only, see their own doc
        // comments) MUST run BEFORE DpRequestRepaint below: the repaint's foreground path
        // (DpReloadAndPreloadProfile -> DpCurrentProfile()) reads the profile straight off
        // LstDpProfile.SelectedItem, not the store — repainting first left the UI showing
        // "Profile 1" selected while the panel kept whatever profile was selected BEFORE
        // the import (e.g. Profile 2), since the list hadn't been moved to slot 1 yet.
        if (isActive)
        {
            DpRefreshProfiles(k2DeviceId);
            if (activateSlot > 0) DpSelectProfileSlot(activateSlot);
        }
        if (activateSlot > 0)
        {
            _dpStore.SetCurrentProfile(k2DeviceId, activateSlot);
            if (isActive) ResetDpNavigation();
            DpRequestRepaint(k2DeviceId);
        }

        DpLog($"[IMP-BC] Done: {totalButtons} keys across {importedProfiles} profile(s) on device #{k2DeviceId}");
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
        // (folder/back navigation above always works, since that's normal usage). Clicking a
        // key from another section (Positioning, Pages, ...) jumps there instead of silently
        // doing nothing — RbDpSecKeyBinding.IsChecked fires DpSection_Changed synchronously,
        // so the user still has to click the key again to actually open the config dialog
        // (this click was ambiguous: "switch to Key Binding" and "configure this key" aren't
        // guaranteed to be the same intent).
        if (!IsDpKeyBindingSectionActive)
        {
            RbDpSecKeyBinding.IsChecked = true;
            return;
        }

        DpOpenKeyConfigDialog(key, id);
    }

    /// <summary>Unified image+action dialog for a key — shared by the canvas
    /// key click (<see cref="DpKeyButton_Click"/>) and the "Configure" button
    /// next to LvDpKeys (<see cref="BtnDpConfigure_Click"/>).</summary>
    private void DpOpenKeyConfigDialog(DisplayPadKey key, int id)
    {
        var dlg = new DpKeyConfigDialog(key.Index, key.ImagePath, key.ActionType, key.ActionValue,
            key.IconSpecJson) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Update action
        key.ActionType  = dlg.ActionType;
        key.ActionValue = dlg.ActionValue;
        key.IconSpecJson = dlg.IconSpecJson;

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

        // Icon settings live in their own column (see DisplayPadStore.SaveIconSpec) and are
        // written last, after whichever branch above created/updated the row.
        _dpStore.SaveIconSpec(id, DpCurrentProfile(), _currentDpPageId, key.Index, key.IconSpecJson);
    }

    /// <summary>Rebuilds the Key Binding section's mapped-keys list (LvDpKeys) —
    /// mirrors RefreshMpMappedKeys (MacroPad).</summary>
    private void RefreshDpMappedKeys()
    {
        _dpMappedKeys.Clear();
        foreach (var k in _dpKeys)
            if (k.HasAction) _dpMappedKeys.Add(k);
    }

    // ================================================================
    // Drag & drop — swap two keys' action + icon (same-page only, mirrors the
    // standalone K2.DisplayPad's CellButton_*/SwapCells and MacroPad's KeyButton_*)
    // ================================================================

    private void DpKeyButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dpDragStartPoint = e.GetPosition(null);
        _dpDragCandidate = (sender as Button)?.Tag as DisplayPadKey;
    }

    private void DpKeyButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dpDragCandidate is null) return;
        var key = _dpDragCandidate;
        if (!IsDpKeyBindingSectionActive || (!key.HasAction && !key.HasImage))
        {
            _dpDragCandidate = null;
            return;
        }
        if (!DragDropHelper.ExceedsDragThreshold(_dpDragStartPoint, e.GetPosition(null))) return;

        _dpDragCandidate = null;
        DragDrop.DoDragDrop((Button)sender, new DataObject(DpKeyDragFormat, key.Index), DragDropEffects.Move);
    }

    private void DpKeyButton_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DpKeyDragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, true);
    }

    private void DpKeyButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
    }

    private void DpKeyButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (!IsDpKeyBindingSectionActive) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (sender is not Button { Tag: DisplayPadKey targetKey }) return;
        if (!e.Data.GetDataPresent(DpKeyDragFormat)) return;

        int sourceIndex = (int)e.Data.GetData(DpKeyDragFormat);
        if (sourceIndex < 0 || sourceIndex >= _dpKeys.Length) return;
        var sourceKey = _dpKeys[sourceIndex];
        if (ReferenceEquals(sourceKey, targetKey)) return;

        int profile = DpCurrentProfile();
        // Deferred on purpose: DoDragDrop() runs its own nested message-pump loop, and this
        // Drop handler fires from INSIDE that loop. Swapping the keys' bound properties here
        // synchronously changes the labels' Text mid-pump, and WPF doesn't always give that a
        // clean repaint before the pump exits — the old key's label can stay rendered under
        // the new one until some unrelated redraw (e.g. the next mouse move) papers over it.
        // Running the swap after the loop has fully unwound gives it a normal layout+render
        // pass instead (user report 2026-08-26).
        Dispatcher.BeginInvoke(() => DpSwapKeys(id, profile, sourceKey, targetKey));
    }

    /// <summary>Swaps action + icon between two keys of the current page, re-uploading
    /// icons to the physical device at their new positions (each key's picture lives in
    /// firmware, keyed by button index — a local-only swap would leave the device showing
    /// the pre-swap pictures). Mirrors the standalone K2.DisplayPad's SwapCells.</summary>
    private void DpSwapKeys(int id, int profile, DisplayPadKey a, DisplayPadKey b)
    {
        (a.ActionType, b.ActionType)   = (b.ActionType, a.ActionType);
        (a.ActionValue, b.ActionValue) = (b.ActionValue, a.ActionValue);

        string? aImage = a.ImagePath;
        string? bImage = b.ImagePath;

        if (!string.IsNullOrEmpty(bImage) && File.Exists(bImage))
        {
            DpUploadAndPersist(id, profile, a, bImage);
        }
        else
        {
            DpGifAnimator.Stop(id, a.Index);
            a.ImagePath = null;
            _dpStore.SaveButton(id, profile, _currentDpPageId, a.Index, null, a.ActionType, a.ActionValue);
            DpClearKeyOnDevice(id, a.Index);
        }

        if (!string.IsNullOrEmpty(aImage) && File.Exists(aImage))
        {
            DpUploadAndPersist(id, profile, b, aImage);
        }
        else
        {
            DpGifAnimator.Stop(id, b.Index);
            b.ImagePath = null;
            _dpStore.SaveButton(id, profile, _currentDpPageId, b.Index, null, b.ActionType, b.ActionValue);
            DpClearKeyOnDevice(id, b.Index);
        }

        DpLog($"[ACT] swapped key #{a.Index} <-> #{b.Index}");
    }

    /// <summary>Configure/Remove only make sense with a row selected — mirrors
    /// LvEvKeys_SelectionChanged/LvMpKeys_SelectionChanged.</summary>
    private void LvDpKeys_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = LvDpKeys.SelectedItem is not null;
        BtnDpConfigure.IsEnabled = hasSelection;
        BtnDpRemoveAction.IsEnabled = hasSelection;
    }

    /// <summary>"Configure" button next to LvDpKeys — same unified image+action
    /// dialog as clicking the key on the canvas, for the selected list row.</summary>
    private void BtnDpConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (LvDpKeys.SelectedItem is not DisplayPadKey key)
        {
            DpLog("[WARN] select a key first");
            return;
        }
        if (DpSelectedDeviceId() is not int id) { DpLog("[WARN] Select a device first."); return; }
        DpOpenKeyConfigDialog(key, id);
    }

    /// <summary>"Remove" button next to LvDpKeys, for the currently selected list row.</summary>
    private void BtnDpRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (LvDpKeys.SelectedItem is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        DpRemoveKeyAction(key, id);
        DpLog($"[ACT] key #{key.Index} action removed");
    }

    private void DpUploadAndPersist(int id, int profile, DisplayPadKey key, string path)
    {
        int rotation = _dpRotation;
        bool ok;
        if (DpGifAnimator.IsAnimatedGif(path))
        {
            // Animated GIFs are always played live (per-frame SetIconPacket-style upload,
            // see DpGifAnimator) — there is no firmware-persistent equivalent, same as BC.
            DpGifAnimator.StartOrUpdate(_dpClient, DpLogAsync, id, key.Index, path, rotation);
            ok = true;
            DpLog($"[GIF] key #{key.Index} <- {Path.GetFileName(path)}");
        }
        else
        {
            DpGifAnimator.Stop(id, key.Index);
            ok = _dpClient.UploadImageToProfile(id, path, key.Index, profile, rotation);
            if (!ok)
            {
                DpLog($"  Upload persistent FAIL, trying live");
                ok = _dpClient.UploadImage(id, path, key.Index, rotation);
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

    /// <summary>Right-click menu for LstDpProfile rows — replaces the old standalone
    /// Rename/Delete/Import/Export buttons (see MainWindow.xaml's Profile group).
    /// A single ContextMenu is shared by every row; K2SideProfileItemStyle's
    /// PreviewMouseRightButtonDown EventSetter (ProfileItem_PreviewRightClick, in
    /// MainWindow.xaml.cs) selects the right-clicked row first, so every handler below
    /// (already written to read LstDpProfile.SelectedItem) works unmodified.</summary>
    private ContextMenu DpBuildProfileContextMenu()
    {
        var menu = new ContextMenu();
        var miRename = new MenuItem { Header = Loc.Get("rename_profile") };
        miRename.Click += BtnDpRenameProfile_Click;
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnDpImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("dp_import_bc") };
        miImportBc.Click += BtnDpImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnDpExportProfiles_Click;
        var miDelete = new MenuItem { Header = Loc.Get("delete_profile") };
        miDelete.Click += BtnDpDeleteProfile_Click;
        menu.Items.Add(miRename);
        menu.Items.Add(new Separator());
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDelete);
        return menu;
    }

    /// <summary>Same items as <see cref="DpBuildProfileContextMenu"/> minus Rename/Delete —
    /// opened from the small "…" button in the Profile header (BtnDpProfileMenu_Click),
    /// which is not tied to a specific row so renaming/deleting a specific profile
    /// wouldn't make sense there.</summary>
    private ContextMenu DpBuildProfileMenuNoEdit()
    {
        var menu = new ContextMenu();
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnDpImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("dp_import_bc") };
        miImportBc.Click += BtnDpImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnDpExportProfiles_Click;
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        return menu;
    }

    private void BtnDpProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = btn;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private ContextMenu BuildDpKeyContextMenu()
    {
        var menu = new ContextMenu();
        var miCfg = new MenuItem { Header = Loc.Get("dp_configure_action") };
        miCfg.Click += DpMnuConfigureAction_Click;
        var miRa = new MenuItem { Header = Loc.Get("dp_remove_action") };
        miRa.Click += DpMnuRemoveAction_Click;
        var miCopy = new MenuItem { Header = Loc.Get("act_copy_action") };
        miCopy.Click += DpMnuCopyAction_Click;
        var miCut = new MenuItem { Header = Loc.Get("act_cut_action") };
        miCut.Click += DpMnuCutAction_Click;
        var miPaste = new MenuItem { Header = Loc.Get("act_paste_action") };
        miPaste.Click += DpMnuPasteAction_Click;
        var miChImg = new MenuItem { Header = Loc.Get("dp_change_image") };
        miChImg.Click += DpMnuChangeImage_Click;
        var miFolder = new MenuItem { Header = Loc.Get("dp_create_folder") };
        miFolder.Click += DpMnuCreateFolder_Click;
        var miBack = new MenuItem { Header = Loc.Get("dp_set_back") };
        miBack.Click += DpMnuSetBack_Click;
        menu.Items.Add(miCfg);
        menu.Items.Add(miRa);
        menu.Items.Add(new Separator());
        menu.Items.Add(miCopy);
        menu.Items.Add(miCut);
        menu.Items.Add(miPaste);
        menu.Items.Add(new Separator());
        menu.Items.Add(miChImg);
        menu.Items.Add(new Separator());
        menu.Items.Add(miFolder);
        menu.Items.Add(miBack);
        return menu;
    }

    private static DisplayPadKey? DpKeyFromMenu(object sender) =>
        sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is DisplayPadKey key ? key : null;

    /// <summary>Routes through the same unified image+action dialog as clicking the key
    /// itself (<see cref="DpOpenKeyConfigDialog"/>) — previously opened a bare
    /// <see cref="ButtonActionDialog"/> directly, which skipped auto-icon generation
    /// entirely (a fresh "Page"/exec/folder action got no default picture, and there was
    /// no way back into the image step short of "Cambia immagine") and never showed the
    /// image/action config screen "other actions" land on after being configured.</summary>
    private void DpMnuConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        DpOpenKeyConfigDialog(key, id);
    }

    /// <summary>Removing the action also clears the key's picture — a picture with no
    /// action behind it is just a stale, misleading tile (same behavior as the unified
    /// config dialog's "Remove action" button).</summary>
    private void DpMnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        DpRemoveKeyAction(key, id);
    }

    /// <summary>Shared by the context menu's "Remove action" and the "Remove"
    /// button next to LvDpKeys (<see cref="BtnDpRemoveAction_Click"/>).</summary>
    private void DpRemoveKeyAction(DisplayPadKey key, int id)
    {
        key.ActionType = null; key.ActionValue = null;
        DpGifAnimator.Stop(id, key.Index);
        key.ImagePath = null;
        _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, null, null, null);
        DpClearKeyOnDevice(id, key.Index);
    }

    /// <summary>Copies this key's action (type+value only, no picture — see
    /// <see cref="ActionClipboard"/>'s remarks) to the app-wide clipboard, for pasting onto
    /// any other key of any device.</summary>
    private void DpMnuCopyAction_Click(object sender, RoutedEventArgs e)
    {
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        ActionClipboard.Copy(key.ActionType, key.ActionValue);
    }

    /// <summary>Copy + remove — mirrors "Cut" everywhere else in Windows.</summary>
    private void DpMnuCutAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        ActionClipboard.Copy(key.ActionType, key.ActionValue);
        DpRemoveKeyAction(key, id);
    }

    /// <summary>Pastes the clipboard's action onto this key. Rejects (with an error, see
    /// <see cref="ActionClipboard.ShowPasteUnsupportedError"/>) a DisplayPad-page action that
    /// makes no sense here — can't actually happen for DisplayPad itself (it's the only host
    /// with <c>SupportsPages</c>), but the SAME clipboard/paste code path is shared by every
    /// device, so the check stays here rather than only on the non-DisplayPad targets. When
    /// this key has no picture yet, generates the action's default icon (<see cref="ActionIconFallback"/>)
    /// instead of leaving it blank — mirrors what a freshly-configured key gets via
    /// <see cref="DpKeyConfigDialog"/>.</summary>
    private void DpMnuPasteAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (!ActionClipboard.HasContent) return;
        if (!ActionClipboard.CanPasteOn(_dpActionHost))
        {
            ActionClipboard.ShowPasteUnsupportedError(this);
            return;
        }

        key.ActionType  = ActionClipboard.ActionType;
        key.ActionValue = ActionClipboard.ActionValue;

        if (!key.HasImage)
        {
            string dest = DpAutoIconCachePath(ActionClipboard.ActionType!, ActionClipboard.ActionValue ?? "");
            if (ActionIconFallback.TryGenerate(ActionClipboard.ActionType, ActionClipboard.ActionValue, DpHidNative.IconSize, dest))
            {
                DpUploadAndPersist(id, DpCurrentProfile(), key, dest);
                DpLog($"[ACT] key #{key.Index} <- pasted action (default icon generated)");
                return;
            }
        }

        _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, key.ImagePath, key.ActionType, key.ActionValue);
        DpLog($"[ACT] key #{key.Index} <- pasted action");
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
                Loc.Get("crop_title", DpHidNative.IconSize, DpHidNative.IconSize),
                bakeRoundedCorners: true);
            if (cropped is null) return;
            picked = cropped;
        }
        DpUploadAndPersist(id, DpCurrentProfile(), key, picked);
    }

    /// <summary>
    /// Creates a brand-new folder sub-page and binds this key to navigate into it —
    /// the in-app equivalent of Base Camp's "Create Folder" button, which until now K2
    /// only ever produced via BaseCamp.db/XML import (see <see cref="BaseCampDbImporter"/>).
    /// Prompts for a display name, allocates a fresh page ID (<see cref="DisplayPadStore.AllocatePageId"/>),
    /// and saves "dp_folder" with that ID as the action — <see cref="OnDpKey"/>/<see cref="DpKeyButton_Click"/>
    /// already know how to navigate into it (<see cref="DpNavigateToPage"/>).
    /// </summary>
    private void DpMnuCreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;

        string? name = ShowRenameDialog("", Loc.Get("dp_create_folder_title"), Loc.Get("dp_create_folder_prompt"));
        if (string.IsNullOrWhiteSpace(name)) return;

        int profile = DpCurrentProfile();
        int pageId = _dpStore.AllocatePageId(id, profile);
        _dpStore.SetFolderName(pageId, name);

        key.ActionType  = "dp_folder";
        key.ActionValue = pageId.ToString();

        // Auto-generate the tile's picture — same glyph+caption convention already used
        // for the "folder" (Open Folder) action, see IconImageGenerator.TryGenerateFolderIcon —
        // so a freshly created page is never left with a blank, actionless-looking tile.
        string dest = DpAutoIconCachePath("dpfolder", name);
        if (IconImageGenerator.TryGenerateFolderIcon(name, DpHidNative.IconSize, dest))
            DpUploadAndPersist(id, profile, key, dest);
        else
            _dpStore.SaveButton(id, profile, _currentDpPageId, key.Index, key.ImagePath, key.ActionType, key.ActionValue);

        DpLog($"[ACT] key #{key.Index} <- dp_folder \"{name}\" (page {pageId})");
    }

    /// <summary>Facade for <see cref="DisplayPadActionHost"/>'s <c>IActionHost.ListPages</c> —
    /// see <see cref="DisplayPadStore.ListPages"/>.</summary>
    internal IReadOnlyList<(int PageId, string Name)> DpListPages(int deviceId, int profile) =>
        _dpStore.ListPages(deviceId, profile);

    /// <summary>Facade for <see cref="DisplayPadActionHost"/>'s <c>IActionHost.CreatePage</c> —
    /// same allocate+name convention as <see cref="DpMnuCreateFolder_Click"/>, minus the
    /// icon (the "Page" action type in <c>ButtonActionDialog</c> leaves icon generation to
    /// the key config dialog that opened it, same as "exec"/"folder").</summary>
    internal int DpCreatePage(int deviceId, int profile, string name)
    {
        int pageId = _dpStore.AllocatePageId(deviceId, profile);
        _dpStore.SetFolderName(pageId, name);
        return pageId;
    }

    /// <summary>Facade for <see cref="DisplayPadActionHost"/>'s <c>IActionHost.RenamePage</c>.</summary>
    internal void DpRenamePage(int pageId, string name) => _dpStore.RenamePage(pageId, name);

    // ================================================================
    // Pages section (list + delete existing folder sub-pages)
    // ================================================================

    /// <summary>Repopulates <see cref="_dpPages"/> from the store for the currently selected
    /// device+profile — called whenever the "Pages" section becomes active (see
    /// <see cref="ShowDpSection"/>), so it always reflects pages created/renamed elsewhere
    /// (context menu, or the "Page" action type in <c>ButtonActionDialog</c>).</summary>
    private void RefreshDpPagesList()
    {
        _dpPages.Clear();
        if (DpSelectedDeviceId() is int id)
            foreach (var (pageId, name) in _dpStore.ListPages(id, DpCurrentProfile()))
                _dpPages.Add(new DpPageRow(pageId, name));

        LblDpNoPages.Visibility = _dpPages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Deletes a folder page after a confirmation prompt — the row's "Delete" button
    /// (see <c>MainWindow.xaml</c>'s <c>LstDpPages</c> template) only shows up on hover.</summary>
    private void BtnDpDeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not int pageId) return;
        if (DpSelectedDeviceId() is not int id) return;

        string name = _dpStore.GetFolderName(pageId) ?? $"Page {pageId}";
        var res = MessageBox.Show(
            Loc.Get("dp_delete_page_confirm", name),
            Loc.Get("dp_delete_page_title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        int profile = DpCurrentProfile();
        DpDeletePage(id, profile, pageId);
        RefreshDpPagesList();
        DpLog($"[ACT] page {pageId} \"{name}\" deleted");
    }

    /// <summary>Deletes a page (see <see cref="DisplayPadStore.DeletePage"/>) and keeps the
    /// live UI in sync: if the page being deleted is the one currently shown on the device
    /// grid, navigates out of it first (it's about to stop existing); either way the grid is
    /// reloaded so any key elsewhere that pointed at the deleted page shows as actionless.</summary>
    private void DpDeletePage(int deviceId, int profile, int pageId)
    {
        _dpStore.DeletePage(deviceId, profile, pageId);

        if (_currentDpPageId == pageId && _dpPageHistory.Count > 0)
        {
            DpNavigateBack(); // pops out of the now-deleted page and reloads the grid
            return;
        }
        if (_currentDpPageId == pageId)
            ResetDpNavigation(); // no history to pop back to: fall back to root

        // Either the deleted page wasn't the one on screen (a key elsewhere pointing at it
        // must show as actionless now) or we just fell back to root above — reload either way.
        DpReloadCurrentProfile(persistent: false);
    }

    /// <summary>Binds this key to navigate back to the parent page — the in-app equivalent
    /// of Base Camp's "Back" button (see <see cref="DpMnuCreateFolder_Click"/> remarks).
    /// Auto-generates the arrow+caption tile the same way folder creation does, unless the
    /// key already carries a picture the user presumably wants kept (still replaceable
    /// afterwards via the "Change image" context-menu item).</summary>
    private void DpMnuSetBack_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDpKeyBindingSectionActive) return;
        if (DpKeyFromMenu(sender) is not DisplayPadKey key) return;
        if (DpSelectedDeviceId() is not int id) return;

        key.ActionType  = "dp_back";
        key.ActionValue = null;

        if (string.IsNullOrEmpty(key.ImagePath) || !File.Exists(key.ImagePath))
        {
            string caption = Loc.Get("dp_back");
            string dest = DpAutoIconCachePath("dpback", caption);
            if (IconImageGenerator.TryGenerateBackIcon(caption, DpHidNative.IconSize, dest))
            {
                DpUploadAndPersist(id, DpCurrentProfile(), key, dest);
                DpLog($"[ACT] key #{key.Index} <- dp_back (auto icon)");
                return;
            }
        }

        _dpStore.SaveButton(id, DpCurrentProfile(), _currentDpPageId, key.Index, key.ImagePath, key.ActionType, key.ActionValue);
        DpLog($"[ACT] key #{key.Index} <- dp_back");
    }

    /// <summary>
    /// Materializes the "top-left key = Back" default for a non-root (folder sub-)page
    /// whose button #0 has no persisted row yet — a freshly created page, or one imported
    /// from Base Camp (XML or BaseCamp.db, see <see cref="BaseCampDbImporter"/>) whose data
    /// simply never defined an equivalent tile there. Generates the same arrow+caption icon
    /// <see cref="DpMnuSetBack_Click"/> uses and persists a "dp_back" action, so a page's way
    /// out is never missing — regardless of how the page came to exist.
    /// Called from <see cref="DpReloadCurrentProfile"/>/<see cref="DpUploadPageForDevice"/>,
    /// i.e. every entry point into a page (foreground tab, background device, both
    /// import paths, since imported pages only ever get read through those two loaders) —
    /// so this needs no special-casing at import time. A no-op once button #0 has ANY row,
    /// including one the user (or Base Camp's own data) explicitly left actionless: only a
    /// genuinely never-touched page gets the default.
    /// </summary>
    private void DpEnsureDefaultBackButton(int id, int profile, int pageId)
    {
        if (pageId == 0) return; // root page has nowhere to go back to
        if (_dpStore.LoadPage(id, profile, pageId).Any(r => r.ButtonIndex == 0)) return;

        string caption = Loc.Get("dp_back");
        string dest = DpAutoIconCachePath("dpback", caption);
        string? imagePath = IconImageGenerator.TryGenerateBackIcon(caption, DpHidNative.IconSize, dest) ? dest : null;
        _dpStore.SaveButton(id, profile, pageId, 0, imagePath, "dp_back", null);
        DpLog($"[ACT] device {id} page {pageId}: key #0 defaulted to dp_back");
    }

    /// <summary>
    /// Blanks a single key's icon on the physical panel (a solid-black BGR buffer —
    /// C# zero-initializes the array, so no pixel loop is needed). Removing an image
    /// only updates the UI/store above; without this the old icon stays on-screen
    /// until the next full repaint (profile switch, reconnect, ...).
    /// </summary>
    private void DpClearKeyOnDevice(int id, int btnIndex)
    {
        if (_dpClient.TryUploadRawBgr(id, new byte[DpHidNative.IconBytes], btnIndex)) return;
        // Satellite/SDK backend: TryUploadRawBgr is a hard "false" there (no raw-buffer
        // command over the pipe), so this method used to be a silent no-op on that
        // backend — combined with DisplayPadResetPicture failing on some machines
        // (observed 2026-07-16, satellite log "[ResetPictures/native] ok=False" on every
        // profile switch), NOTHING could ever blank a key and stale icons survived every
        // switch. Fall back to uploading a solid-black PNG through the normal path,
        // which those same logs show always works.
        _dpClient.UploadImage(id, DpBlackIconPath(), btnIndex, 0);
    }

    private static string? _dpBlackIconPath;

    /// <summary>Path of a solid-black 102×102 PNG, generated once on first use (blank-key
    /// fallback for backends without raw-buffer uploads — see <see cref="DpClearKeyOnDevice"/>).</summary>
    private static string DpBlackIconPath()
    {
        if (_dpBlackIconPath is string cached && File.Exists(cached)) return cached;
        string dir = K2Paths.For("K2.DisplayPad");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "blank_black_102.png");
        if (!File.Exists(path))
        {
            using var bmp = new System.Drawing.Bitmap(DpHidNative.IconSize, DpHidNative.IconSize);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.Clear(System.Drawing.Color.Black);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        return _dpBlackIconPath = path;
    }

    // ================================================================
    // Refresh / Persistence
    // ================================================================

    private void DpRefreshDevices()
    {
        int? prevActive = _activeDpDeviceId;
        var previousIds = _dpDeviceIds.ToList();
        // Must be captured BEFORE RemoveDeviceTabs below tears down the DisplayPad tab(s):
        // removing the currently-selected tab makes WPF auto-move TcDevices.SelectedItem to
        // whatever tab is now adjacent, so reading the tag afterwards (as this used to do)
        // always saw a non-"dp_" tag on reconnect and skipped the re-select/reload branch —
        // the device came back online but its tab was silently left unselected and its icons
        // were never re-uploaded (the hardware's on-board icon memory does not survive a
        // USB replug, so a real re-upload — not just a UI refresh — is required here).
        bool wasOnDpTab = (TcDevices.SelectedItem as TabItem)?.Tag is string curDpTag
                           && curDpTag.StartsWith("dp_");
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
            // Forget this device's background-activation bookkeeping too: its onboard icon
            // memory won't survive the replug, so if it comes back it must be treated as
            // "never activated" again (re-uploaded), not silently skipped — see the
            // "already tracked" guard below.
            _dpBgPageId.Remove(goneId);
            _dpBgPageHistory.Remove(goneId);
            if (_dpAutoOffTimers.Remove(goneId, out var goneTimer)) goneTimer.Dispose();
            if (_dpScreensaverTimers.Remove(goneId, out var goneSs)) goneSs.Dispose();
            _dpScreensaverShowing.Remove(goneId);
            _dpScreensaverWakeKey.Remove(goneId);
            _dpEmojiBrowser.Remove(goneId);
            _dpDiscordRoom.Remove(goneId);
            _dpSavedBrightness.Remove(goneId);
        }
        var items = new List<DpDeviceItem>();
        int progressive = 1;
        foreach (var id in ids)
        {
            bool plugged = _dpClient.IsPlugged(id);
            if (!plugged)
            {
                // Silently skipping used to leave no trace of *why* an SDK-reported id never
                // became a tab — a phantom slot and a real-but-momentarily-unplugged device
                // looked identical in the log (nothing at all).
                DpLog($"Device {id}: reported by SDK but IsPlugged=false — skipped, no tab");
                continue;
            }
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

        // Sync top-level device tabs for DisplayPad (fixed order: Everest Max > Everest 60 >
        // Makalu > DisplayPad > MacroPad — see the comment above TabEverest in MainWindow.xaml)
        RemoveDeviceTabs("dp_");
        int insertIdx = TcDevices.Items.IndexOf(TabMakalu) + 1;
        foreach (var item in items)
        {
            var tab = new TabItem { Header = item.Label, Tag = $"dp_{item.SdkId}" };
            TcDevices.Items.Insert(insertIdx++, tab);
        }
        // Only steer the top-level selection to a DisplayPad tab if the user was
        // already on one (see wasOnDpTab above) — a background device refresh (e.g.
        // a plug event arriving after startup) must not steal focus away from
        // whatever tab is active.
        if (items.Count > 0 && wasOnDpTab)
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

        // Every OTHER connected DisplayPad (multi-device setups) must ALSO respond to physical
        // key presses immediately, not just the one that became _activeDpDeviceId above — see
        // DpActivateBackgroundDevice/DpHandleBackgroundKey. Without this, only the first pad
        // (or whichever tab the user opens) is ever "activated"; the rest stay mute until the
        // user clicks their tab too. Skip devices already tracked in _dpBgPageId (activated by
        // a previous refresh) so a plug event for ONE device doesn't re-upload every OTHER
        // already-active pad's icons for nothing.
        foreach (var item in items)
        {
            if (item.SdkId == _activeDpDeviceId) continue;
            if (_dpBgPageId.ContainsKey(item.SdkId)) continue;
            DpActivateBackgroundDevice(item.SdkId);
        }

        RefreshHomeTiles(); // DisplayPad tabs are added/removed outright, not toggled via SetDeviceTabVisible
        DpLog(items.Count > 0
            ? $"{items.Count} DisplayPad tab(s) created: [{string.Join(", ", items.Select(x => x.SdkId))}]"
            : "0 DisplayPad tabs created (SDK reported no plugged device)");
    }

    private int DpCurrentProfile() => LstDpProfile.SelectedItem is DpProfileItem pi ? pi.Slot : 1;

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
        DpEmojiBrowserAbandon(id);   // see DpRequestRepaint
        DvpAbandon(id);
        int profile = DpCurrentProfile();
        int pageId = _currentDpPageId;
        int rotation = _dpRotation;
        DpEnsureDefaultBackButton(id, profile, pageId);
        foreach (var k in _dpKeys) { k.ImagePath = null; k.ActionType = null; k.ActionValue = null; }
        var rows = _dpStore.LoadPage(id, profile, pageId);
        DpLog($"[DB] loaded {rows.Count} records for device={id} profile={profile} page={pageId}");
        DiscordVoiceKeyService.Sync(_dpClient, DpLogAsync, id, rotation, rows);   // see DpUploadPageForDevice
        DpLiveTileService.Sync(_dpClient, DpLogAsync, id, rotation, rows);        // clock/monitor/speed-test keys

        // Stop every animated-GIF loop on this device NOW (synchronously) — a page/profile
        // switch repurposes key indices, and a stale animation task would keep overwriting
        // whatever key it was bound to (possibly mid-blank) with frames from the OLD page.
        // Mirrors BC cancelling pending per-key GIF tasks before it starts a new batch.
        DpGifAnimator.StopAllForDevice(id);
        // No matching DpLiveTileService.Stop here: the Sync call above already REPLACED this
        // device's live-key set with the new page's, which is the same thing a stop+register
        // would achieve — and stopping after it would unregister the keys just synced.

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
        // In screensaver mode (delay enabled) the image is deliberately NOT painted here:
        // the page shows its normal icons and the image only takes over after the idle
        // timeout — see DpScreensaverTimeout.
        var screensaver = _dpStore.GetScreensaverDelay(id, profile, pageId);
        bool hasFullscreenImage = fullscreen.HasValue && File.Exists(fullscreen.Value.Path);
        bool fullscreenActive = hasFullscreenImage && !screensaver.Enabled;
        DpConfigureScreensaver(id, hasFullscreenImage && screensaver.Enabled, screensaver.Seconds);
        _dpFullscreenByDevice[id] = fullscreenActive;
        // A fullscreen image owns all 12 slots — nothing for the live tiles to paint on.
        if (fullscreenActive) DpLiveTileService.Stop(id);
        if (!fullscreenActive) DpFullscreenAnimator.Stop(id);

        var toUpload = new List<(int btnIndex, string imagePath)>();
        var toAnimate = new List<(int btnIndex, string imagePath)>();
        var keysWithImage = new HashSet<int>();
        foreach (var r in rows)
        {
            if (r.ButtonIndex < 0 || r.ButtonIndex >= _dpKeys.Length) continue;
            var key = _dpKeys[r.ButtonIndex];
            key.ActionType = r.ActionType;
            key.ActionValue = r.ActionValue;
            key.IconSpecJson = r.IconSpec;   // see DpOpenKeyConfigDialog / KeyIconSpec
            if (!string.IsNullOrEmpty(r.ImagePath) && File.Exists(r.ImagePath))
            {
                key.ImagePath = r.ImagePath;
                keysWithImage.Add(r.ButtonIndex);
                if (fullscreenActive) continue;   // hardware won't show per-key icons anyway
                // A key the Discord overlay owns is painted by DiscordVoiceKeyService.Repaint
                // at the tail of this batch: uploading its stored picture too would only make
                // the two alternate on the panel. The app's own grid above keeps showing it.
                if (DiscordVoiceKeyService.Owns(id, r.ButtonIndex)) continue;
                if (DpLiveTileService.Owns(id, r.ButtonIndex)) continue;          // idem, live tiles
                if (DpGifAnimator.IsAnimatedGif(r.ImagePath))
                    toAnimate.Add((r.ButtonIndex, r.ImagePath));
                else
                    toUpload.Add((r.ButtonIndex, r.ImagePath));
            }
        }

        // Any key without an image on THIS page must go blank on the panel — otherwise it
        // keeps showing whatever the previously-displayed page (or profile) had there.
        // Computed even when blankFirst is set: ResetPictures can FAIL (the SDK wrapper
        // call returns false on some machines — observed 2026-07-16), and when it does
        // these per-key blanks are the only thing standing between the user and stale
        // old-profile icons. Skipped only under fullscreenActive (a fullscreen image
        // already owns all 12 slots).
        var toBlank = fullscreenActive
            ? Array.Empty<int>()
            : Enumerable.Range(0, _dpKeys.Length).Where(i => !keysWithImage.Contains(i)).ToArray();

        // The app's own grid above is already updated (instant). The hardware write is the
        // slow part — run it on a background thread, chained per device. A newer reload
        // supersedes the queued (not yet started) uploads of the previous one via the CTS.
        if (toUpload.Count > 0 || toAnimate.Count > 0 || fullscreenActive || blankFirst || toBlank.Length > 0)
        {
            if (_dpUploadCts.TryGetValue(id, out var oldCts)) oldCts.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _dpUploadCts[id] = cts;
            var ct = cts.Token;

            var previous = _dpUploadChain.TryGetValue(id, out var p) ? p : Task.CompletedTask;
            var next = previous.ContinueWith(_ =>
            {
                if (ct.IsCancellationRequested) return;
                bool panelBlanked = false;
                if (blankFirst)
                {
                    panelBlanked = _dpClient.ResetPictures(id);
                    if (!panelBlanked)
                        DpLogAsync("ResetPictures failed — blanking empty keys individually instead");
                }

                if (fullscreenActive)
                {
                    DpFullscreenAnimator.Start(_dpClient, DpLogAsync, id,
                        fullscreen!.Value.Path, fullscreen.Value.Rotation, rotation);
                    return;
                }

                // Redundant (and skipped) when the full-panel reset above really worked.
                foreach (int btnIndex in panelBlanked ? Array.Empty<int>() : toBlank)
                {
                    if (ct.IsCancellationRequested) return;
                    DpClearKeyOnDevice(id, btnIndex);
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
                // Last: the Discord live tiles paint OVER the profile's own icon for those keys.
                if (!ct.IsCancellationRequested) DiscordVoiceKeyService.Repaint(id);
                if (!ct.IsCancellationRequested) DpLiveTileService.Repaint(id);
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
    /// <summary>Per-device: debug live-panel-test (<see cref="DpFullscreenAnimator.StartLiveTest"/>)
    /// is currently running — see <see cref="BtnDpLiveTest_Click"/>.</summary>
    private readonly HashSet<int> _dpLiveTestActive = new();
    /// <summary>Per-device: debug live-SINGLE-ICON-test (<see cref="DpGifAnimator.StartLiveTest"/>,
    /// always key #0) is currently running — see <see cref="BtnDpLiveTestIcon_Click"/>.</summary>
    private readonly HashSet<int> _dpLiveTestIconActive = new();
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
        // Any full repaint replaces whatever is on the panel, emoji browser included —
        // drop it here rather than in each of the (many) callers. The Discord voice page is
        // put BACK once the repaint has run (see the continuation below): a call still owns
        // the panel, and losing the page to an unrelated repaint would strand the user on a
        // profile they can't see their call on.
        DpEmojiBrowserAbandon(id);
        DvpAbandon(id);
        if (_dpRepaintBusy.GetValueOrDefault(id))
        {
            _dpRepaintPending.Add(id);
            return;
        }
        _dpRepaintBusy[id] = true;
        // BUG FIX: this used to call DpReloadAndPreloadProfile() unconditionally, which
        // ignores its `id` parameter entirely and always reloads DpSelectedDeviceId() (the
        // foreground tab) — so a cross-device or background-device profile switch (id !=
        // the visible tab) silently repainted the WRONG pad's screen (DB state was correct,
        // hardware just never got the new icons until the user opened that pad's own tab).
        // Route to the foreground-only reload (which also updates the UI grid) ONLY when
        // `id` really is the visible tab; every other device uses the background uploader.
        if (DpSelectedDeviceId() is int selId && id == selId)
            DpReloadAndPreloadProfile(blankFirst: true);
        else
            DpUploadPageForDevice(id, _dpStore.GetCurrentProfile(id), _dpBgPageId.GetValueOrDefault(id, 0),
                persistent: false, blankFirst: true);
        var chain = _dpUploadChain.TryGetValue(id, out var t) ? t : Task.CompletedTask;
        chain.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _dpRepaintBusy[id] = false;
            if (_dpRepaintPending.Remove(id))
                DpRequestRepaint(id);
            else
                DvpRestoreAfterRepaint(id);
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

    // ================================================================
    // Background devices (connected DisplayPad, not the foreground tab)
    // ================================================================

    /// <summary>
    /// Prepares a connected DisplayPad that is NOT the current foreground tab so it keeps
    /// responding to its own physical key presses and shows the right icons right away —
    /// see <see cref="DpHandleBackgroundKey"/>. Mirrors the essential part of
    /// <see cref="DpActivateDevice"/>/<see cref="DpReloadAndPreloadProfile"/> WITHOUT
    /// touching any UI-bound field (_dpKeys, LstDpProfile, _dpRotation, ...) — those only
    /// ever represent whichever single device tab is actually visible.
    /// </summary>
    private void DpActivateBackgroundDevice(int id)
    {
        _dpBgPageId[id] = 0;
        _dpBgPageHistory[id] = new Stack<int>();
        int profile = _dpStore.GetCurrentProfile(id);
        DpUploadPageForDevice(id, profile, 0, persistent: false);
        DpLog($"[UI] Background device {id} activated (profile {profile}).");
    }

    /// <summary>
    /// Uploads one page's icons (or its fullscreen image) to the hardware for an arbitrary
    /// device/profile/page, without touching UI-bound state — the background counterpart of
    /// <see cref="DpReloadCurrentProfile"/> (which also updates the visible key grid and is
    /// reserved for the foreground tab). Chained onto the same per-device <see cref="_dpUploadChain"/>,
    /// so it can never race the foreground reload's uploads on the wire.
    /// <paramref name="blankFirst"/> mirrors <see cref="DpReloadCurrentProfile"/>'s own flag: pass
    /// true on a real profile switch for a full-panel <see cref="IDisplayPadClient.ResetPictures"/>
    /// (BC's own "UploadLogo" reset). Independently of that flag, any key with NO image on
    /// THIS page always gets an explicit per-key blank below — otherwise it keeps showing
    /// whatever the previously-displayed page/profile had there (a folder-navigation reload
    /// never sets blankFirst, since a full ResetPictures per click would be needless flicker
    /// for what's usually only 1-2 stale keys).
    /// </summary>
    private void DpUploadPageForDevice(int id, int profile, int pageId, bool persistent, bool blankFirst = false)
    {
        DpEmojiBrowserAbandon(id);   // this page's icons take the panel back — see DpRequestRepaint
        DvpAbandon(id);
        DpEnsureDefaultBackButton(id, profile, pageId);
        int rotation = _dpStore.GetRotation(id);
        var rows = _dpStore.LoadPage(id, profile, pageId);
        // Live Discord mute/deafen tiles for this page's keys (transient overlay, see
        // DiscordVoiceKeyService) — re-synced on every repaint so it always matches the
        // page actually on the panel.
        DiscordVoiceKeyService.Sync(_dpClient, DpLogAsync, id, rotation, rows);
        DpLiveTileService.Sync(_dpClient, DpLogAsync, id, rotation, rows);   // see DpReloadCurrentProfile
        var fullscreen = _dpStore.GetFullscreenImage(id, profile, pageId);
        // Screensaver mode: painted later by DpScreensaverTimeout, not now — see
        // DpReloadCurrentProfile (the foreground counterpart) for the full reasoning.
        var screensaver = _dpStore.GetScreensaverDelay(id, profile, pageId);
        bool hasFullscreenImage = fullscreen.HasValue && File.Exists(fullscreen.Value.Path);
        bool fullscreenActive = hasFullscreenImage && !screensaver.Enabled;
        DpConfigureScreensaver(id, hasFullscreenImage && screensaver.Enabled, screensaver.Seconds);
        _dpFullscreenByDevice[id] = fullscreenActive;
        // A fullscreen image owns all 12 slots — nothing for the live tiles to paint on.
        if (fullscreenActive) DpLiveTileService.Stop(id);
        if (!fullscreenActive) DpFullscreenAnimator.Stop(id);

        var keysWithImage = new HashSet<int>(
            rows.Where(r => !string.IsNullOrEmpty(r.ImagePath) && File.Exists(r.ImagePath))
                .Select(r => r.ButtonIndex));
        // Same ResetPictures-can-fail fallback as DpReloadCurrentProfile — see the
        // comment on toBlank there.
        var toBlank = fullscreenActive
            ? Array.Empty<int>()
            : Enumerable.Range(0, 12).Where(i => !keysWithImage.Contains(i)).ToArray();

        var previous = _dpUploadChain.TryGetValue(id, out var p) ? p : Task.CompletedTask;
        var next = previous.ContinueWith(_ =>
        {
            bool panelBlanked = false;
            if (blankFirst)
            {
                panelBlanked = _dpClient.ResetPictures(id);
                if (!panelBlanked)
                    DpLogAsync($"ResetPictures failed (bg device {id}) — blanking empty keys individually instead");
            }

            if (fullscreenActive)
            {
                DpFullscreenAnimator.Start(_dpClient, DpLogAsync, id,
                    fullscreen!.Value.Path, fullscreen.Value.Rotation, rotation);
                return;
            }
            foreach (int btnIndex in panelBlanked ? Array.Empty<int>() : toBlank)
                DpClearKeyOnDevice(id, btnIndex);

            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.ImagePath) || !File.Exists(r.ImagePath)) continue;
                if (DiscordVoiceKeyService.Owns(id, r.ButtonIndex)) continue;   // see DpReloadCurrentProfile
                if (DpLiveTileService.Owns(id, r.ButtonIndex)) continue;        // idem
                if (DpGifAnimator.IsAnimatedGif(r.ImagePath))
                    DpGifAnimator.StartOrUpdate(_dpClient, DpLogAsync, id, r.ButtonIndex, r.ImagePath, rotation);
                else
                {
                    if (persistent) _dpClient.UploadImageToProfile(id, r.ImagePath, r.ButtonIndex, profile, rotation);
                    _dpClient.UploadImage(id, r.ImagePath, r.ButtonIndex, rotation);
                }
            }
            DiscordVoiceKeyService.Repaint(id);   // see DpReloadCurrentProfile
            DpLiveTileService.Repaint(id);        // idem
        }, TaskScheduler.Default);
        _dpUploadChain[id] = next;
    }

    /// <summary>
    /// Executes a physical key event from a connected DisplayPad that is NOT the foreground
    /// tab, using its OWN persisted bindings/profile/page rather than the shared UI-bound
    /// _dpKeys/_dpMatrixToIndex/_currentDpPageId (those only ever reflect whichever single
    /// device tab is visible — see <see cref="OnDpKey"/>). Always called on the UI thread.
    /// </summary>
    private void DpHandleBackgroundKey(int devId, int matrix, bool pressed)
    {
        if (!DpDefaultMatrixToIndex.TryGetValue(matrix, out int idx) || idx >= 12) return;

        int profile = _dpStore.GetCurrentProfile(devId);
        int pageId = _dpBgPageId.GetValueOrDefault(devId, 0);
        var row = _dpStore.LoadPage(devId, profile, pageId).FirstOrDefault(r => r.ButtonIndex == idx);

        // Press-bounce visual on every physical press AND release, same as the foreground
        // path (DpUploadPressVisual) — see that method's remarks. Runs regardless of whether
        // the key has an action, mirroring an icon-only (no-action) key on the foreground tab.
        DpUploadPressVisualForDevice(devId, idx, row?.ImagePath, _dpStore.GetRotation(devId), pressed);

        if (!pressed || row is null) return;

        if (AppSettings.LogLevel == K2LogLevel.Verbose)
            DpLog($"[KEY][bg {devId}] matrix 0x{matrix:X2} down");

        if (row.ActionType == "dp_folder" && int.TryParse(row.ActionValue, out int folderPageId))
            DpBgNavigateToPage(devId, folderPageId);
        else if (row.ActionType == "dp_back")
            DpBgNavigateBack(devId);
        else if (row.ActionType == "dp_emojibrowser")
            DpEmojiBrowserOpen(devId);
        else if (DvpIsVoicePageAction(row.ActionType, row.ActionValue))
            DvpReopen(devId);
        else if (DpLiveTileService.IsLiveType(row.ActionType))
            DpLiveTileService.HandlePress(devId, idx, DpLogAsync);   // speed-test keys only; clock/monitor readouts ignore the press
        else
            DpEngineFor(devId).Execute(row.ActionType, row.ActionValue, idx);
    }

    private void DpBgNavigateToPage(int devId, int pageId)
    {
        var stack = _dpBgPageHistory.TryGetValue(devId, out var s) ? s : (_dpBgPageHistory[devId] = new Stack<int>());
        stack.Push(_dpBgPageId.GetValueOrDefault(devId, 0));
        _dpBgPageId[devId] = pageId;
        DpUploadPageForDevice(devId, _dpStore.GetCurrentProfile(devId), pageId, persistent: false);
    }

    private void DpBgNavigateBack(int devId)
    {
        if (!_dpBgPageHistory.TryGetValue(devId, out var stack) || stack.Count == 0) return;
        int pageId = stack.Pop();
        _dpBgPageId[devId] = pageId;
        DpUploadPageForDevice(devId, _dpStore.GetCurrentProfile(devId), pageId, persistent: false);
    }

    /// <summary>Lazily creates (and caches) the action engine used to execute actions for a
    /// background device — see <see cref="DisplayPadBackgroundActionHost"/>.</summary>
    internal ButtonActionEngine DpEngineFor(int devId)
    {
        if (_dpBgEngines.TryGetValue(devId, out var engine)) return engine;
        engine = new ButtonActionEngine(new DisplayPadBackgroundActionHost(this, devId));
        engine.Start();
        _dpBgEngines[devId] = engine;
        return engine;
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
            int evtDevId = e.Get("deviceId");
            int matrix = e.Get("keyMatrix");
            bool pressed = e.GetBool("pressed");

            DpGetAutoOffTimer(evtDevId).RegisterActivity();
            // Dismisses the screensaver (restoring this page's icons) if it is showing, and
            // restarts its countdown.
            bool wokeScreensaver = _dpScreensaverShowing.Contains(evtDevId);
            DpGetScreensaverTimer(evtDevId).RegisterActivity();

            // The press that dismissed the screensaver only wakes the panel: the user was
            // looking at an image, not at the icons, so running that key's binding would be
            // an action they never aimed at. Its release is swallowed too, so the key-up does
            // not leave a stale press-bounce/highlight behind.
            if (wokeScreensaver)
            {
                if (pressed) _dpScreensaverWakeKey[evtDevId] = matrix;
                return;
            }
            if (!pressed && _dpScreensaverWakeKey.TryGetValue(evtDevId, out int wakeMatrix)
                && wakeMatrix == matrix)
            {
                _dpScreensaverWakeKey.Remove(evtDevId);
                return;
            }
            if (pressed) _dpScreensaverWakeKey.Remove(evtDevId);

            // While the emoji browser owns this pad's panel (see
            // MainWindow.DisplayPad.EmojiBrowser.cs) it consumes every key itself: the
            // underlying page's bindings must not fire behind the overlay. Uses the
            // foreground tab's (possibly remapped) matrix table when this IS the visible
            // tab, the default map otherwise — same split as the dispatch below.
            if (DpEmojiBrowserActive(evtDevId))
            {
                var embMap = DpSelectedDeviceId() == evtDevId ? _dpMatrixToIndex : DpDefaultMatrixToIndex;
                if (embMap.TryGetValue(matrix, out int embIdx) && embIdx < 12)
                    DpEmojiBrowserKey(evtDevId, embIdx, pressed);
                return;
            }

            // Same rule for the Discord voice page (MainWindow.DisplayPad.DiscordRoom.cs): while
            // a call owns the panel, its own 12 keys are the only ones that can fire.
            if (DpDiscordRoomActive(evtDevId))
            {
                var dvpMap = DpSelectedDeviceId() == evtDevId ? _dpMatrixToIndex : DpDefaultMatrixToIndex;
                if (dvpMap.TryGetValue(matrix, out int dvpIdx) && dvpIdx < 12)
                    DvpKey(evtDevId, dvpIdx, pressed);
                return;
            }

            // The foreground tab (_activeDpDeviceId) uses the UI-bound state (_dpKeys,
            // _dpMatrixToIndex, _currentDpPageId, remap mode, press-bounce visual). Any OTHER
            // connected DisplayPad (multi-device setups) must still execute ITS OWN bindings —
            // see DpHandleBackgroundKey — otherwise it stays mute until the user opens its tab.
            if (DpSelectedDeviceId() is not int selId || evtDevId != selId)
            {
                DpHandleBackgroundKey(evtDevId, matrix, pressed);
                return;
            }

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
                    else if (DpLiveTileService.IsLiveType(action))
                        DpLiveTileService.HandlePress(selId, hi, DpLogAsync);   // see DpHandleBackgroundKey
                    else if (action == "dp_emojibrowser")
                        DpEmojiBrowserOpen(selId);
                    else if (DvpIsVoicePageAction(action, value))
                        DvpReopen(selId);
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
    private void DpUploadPressVisual(int id, int btnIndex, bool pressed) =>
        DpUploadPressVisualForDevice(id, btnIndex, _dpKeys[btnIndex].ImagePath, _dpRotation, pressed);

    /// <summary>Device-agnostic core of <see cref="DpUploadPressVisual"/> — takes the image path
    /// and rotation explicitly instead of reading the foreground-only <c>_dpKeys</c>/<c>_dpRotation</c>,
    /// so it also works for a background (non-foreground-tab) DisplayPad — see
    /// <see cref="DpHandleBackgroundKey"/>. Previously the press-bounce was foreground-only,
    /// which with multiple DisplayPads connected made it look like "the press animation only
    /// works once you've opened that pad's tab".</summary>
    private void DpUploadPressVisualForDevice(int id, int btnIndex, string? imgPath, int rotation, bool pressed)
    {
        // A key owned by the Discord live overlay bounces ITS tile, not the stored picture:
        // re-uploading the latter on every press/release is what made the live glyph snap back
        // to the profile's icon on each click. Resolved per call, so the key-UP repaint already
        // carries the state the key-DOWN action just produced.
        imgPath = DiscordVoiceKeyService.CurrentIconPath(id, btnIndex)
                  ?? DpLiveTileService.CurrentIconPath(id, btnIndex) ?? imgPath;

        if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) return;
        if (DpGifAnimator.IsAnimatedGif(imgPath)) return;
        if (_dpFullscreenByDevice.TryGetValue(id, out bool fs) && fs) return;
        // While a full repaint (profile/page switch: blank + batch upload) is queued or
        // running, skip the bounce entirely: imgPath was captured from the PRE-switch key
        // state, so chaining it here would land a stale old-profile icon in the middle of
        // (or after) the new profile's batch — observed in real logs as "old icons overlap
        // the new profile". The batch itself repaints this key with the correct icon anyway;
        // all that's lost is the shrink effect on the very press that triggered the switch.
        if (_dpRepaintBusy.GetValueOrDefault(id)) return;

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
    // Live key preview (app window) — clock / PC monitor / speed test
    // ================================================================
    //
    // DpLiveTileService's own timer drives ONLY the physical DisplayPad — the app's own key
    // grid never gets an image at all for these keys otherwise (the stored DB row has no
    // ImagePath, same as dp_folder/dp_emojibrowser: there's nothing to load, only a
    // generated-on-the-fly picture). Renders independently, on the UI thread, into a file the
    // hardware-side service never touches — so the two 1 Hz timers can never race on the same
    // PNG (see DpLiveTileService.UiPreviewPath's remarks).

    private DispatcherTimer? _dpKeyLiveTimer;

    private void DpInitLiveKeyPreviewTimer()
    {
        if (_dpKeyLiveTimer is not null) return;
        _dpKeyLiveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dpKeyLiveTimer.Tick += (_, _) => DpRefreshLiveKeyPreviews();
        _dpKeyLiveTimer.Start();
    }

    /// <summary>Re-renders and re-binds every dp_clock/dp_sysmon/dp_speedtest key on the
    /// currently displayed foreground page — cheap no-op when there are none (a 12-key loop
    /// with an early-continue). <see cref="DisplayPadKey.TouchPreview"/> is what actually
    /// forces the WPF binding to re-read the file every tick: the path itself stays the same
    /// key-to-key, so the plain ImagePath setter's "no-op on unchanged value" guard would
    /// otherwise swallow every refresh after the first.</summary>
    private void DpRefreshLiveKeyPreviews()
    {
        if (DpSelectedDeviceId() is not int id) return;
        foreach (var key in _dpKeys)
        {
            if (!DpLiveTileService.IsLiveType(key.ActionType)) continue;
            string path = DpLiveTileService.UiPreviewPath(id, key.Index);
            var spec = KeyIconSpec.FromJson(key.IconSpecJson);
            if (!DpLiveTileService.RenderNow(key.ActionType!, key.ActionValue, spec, path)) continue;
            if (key.ImagePath != path) key.ImagePath = path;
            key.TouchPreview();
        }
    }

    // ================================================================
    // Cleanup
    // ================================================================

    private void CleanupDisplayPad()
    {
        _dpKeyLiveTimer?.Stop();
        _dpKeyLiveTimer = null;
        _dvpReconnectTimer?.Stop();
        _dvpReconnectTimer = null;
        DpGifAnimator.StopAll();
        DpFullscreenAnimator.StopAll();
        _dpEngine?.Dispose();
        foreach (var engine in _dpBgEngines.Values) engine.Dispose();
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
    public bool IsRealProfile => !IsNew;
    public override string ToString() => Label;
}

// ---- "Pages" section row ----
public sealed record DpPageRow(int PageId, string Name);

// ---- Device table rows ----
public sealed class DpDeviceRow
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string Plugged { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string Brightness { get; set; } = "";
}
