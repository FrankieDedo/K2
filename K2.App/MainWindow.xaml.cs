using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Main window of the K2 unified shell.
///
/// This step has the MacroPad, Everest Max and DisplayPad tabs active:
/// opens the USB driver via <see cref="MacroPadService"/>, enumerates
/// devices, and logs everything the SDK reports (keys, plug/unplug,
/// firmware update progress) to the event console.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MacroPadService _macroPad = new();
    private readonly ObservableCollection<MacroPadDeviceRow> _devices = new();
    private IntPtr _hWnd;

    public MainWindow()
    {
        InitializeComponent();
        InitLanguageMenu();      // Language switcher in the status bar
        LvDevices.ItemsSource = _devices;
        IcHomeTiles.ItemsSource = _homeTiles;

        InitKeysModule();        // MacroPad: 12-key grid (2×6 rotatable) + profile selector
        InitMacroLedPanel();     // MacroPad: LED lighting panel (firmware presets)
        InitMpSettingsPanel();   // MacroPad: Settings section — keycap appearance (color/style)
        InitActionEngine();      // MacroPad: action engine + Python bridge
        InitEverestModule();     // Everest Max: on-demand key list + dedicated action engine
        InitEverest60Module();   // Everest 60: raw-HID connectivity check + RGB lighting
        InitMakaluModule();      // Makalu 67/Max: raw-HID connectivity + RGB/DPI/remap/settings
        InitMacroPanel();        // Macro: top-level section (recording/playback), own nav button
        InitUsbRecorderModule(); // USB Recorder: capture Base Camp packets via tshark
        InitDisplayPadModule();  // DisplayPad: graphic overlay + x64 satellite IPC
        InitDpActionEngine();    // DisplayPad: dedicated action engine
        InitLedPreview();        // Real-time LED color preview across all devices
        InitAppSettingsPanel();  // General Settings: centralized Debug + Log level
        InitTray();              // System tray: close-to-tray + tray icon menu

        _macroPad.KeyEvent += OnMacroPadKey;
        _macroPad.DevicePlug += OnMacroPadPlug;
        _macroPad.FirmwareProgress += OnMacroPadProgress;

        Closed += OnWindowClosed;

        CheckNativeDependency();

        // Deferred to Loaded (not run inline here) so the "Import from Base Camp?"
        // prompt (see MainWindow.Settings.cs) pops up over an already-visible main
        // window instead of blocking construction before the app is even shown.
        Loaded += (_, _) => CheckFirstRunBcImport();
    }

    /// <summary>
    /// Checks at startup whether <c>MacroPadSDK.dll</c> is reachable. The DLL
    /// is not redistributable so it may be absent; in that case tells the user
    /// how to provide it without blocking the app.
    /// </summary>
    private void CheckNativeDependency()
    {
        const string dll = "MacroPadSDK.dll";
        if (NativeDependencyResolver.IsResolvable(dll))
        {
            Log($"{dll}: found, ready to use.");
            return;
        }

        LblStatus.Text = $"{dll} not found — see instructions in the console.";
        Log("──────────────────────────────────────────────");
        Log($"WARNING: {dll} was not found.");
        Log("This DLL is an internal Base Camp component and is NOT");
        Log("distributed with K2. To use the MacroPad, choose one option:");
        Log($"  1) copy '{dll}' from the Base Camp installation folder");
        Log("     next to K2.App.exe;");
        Log("  2) pick the Base Camp DLL folder in Settings > Base Camp DLL folder;");
        Log("  3) keep Base Camp installed: K2 detects it automatically;");
        Log($"  4) set the environment variable {NativeDependencyResolver.BaseCampDirEnvVar}");
        Log("     to the path of the Base Camp folder.");
        Log(NativeDependencyResolver.DescribeSearch(dll));
        Log("──────────────────────────────────────────────");
    }

    /// <summary>
    /// The HWND only exists after source initialization: this is the right
    /// moment to hook the WndProc that forwards plug/progress messages to
    /// the MacroPad SDK.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hWnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hWnd)?.AddHook(WndProc);
        App.WriteLog($"[MainWindow] HWND=0x{_hWnd.ToInt64():X}, WndProc hooked");
        Services.RawKeyboardActivityWatcher.Register(_hWnd);

        // Auto-open all drivers after the window is fully rendered
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(AutoOpenDrivers));
    }

    // Pause between eager vendor-SDK driver opens at startup — these are closed-source
    // Mountain DLLs (MacroPadSDK.dll/SDKDLL.dll/Everest360_USB.dll) known to be fragile
    // (see App.xaml.cs's VEH survival machinery); opening all of them back-to-back on the
    // same dispatcher tick was suspected of destabilizing the shared USB/HID stack widely
    // enough to affect unrelated processes (Steam's controller polling silently dying,
    // physical Xbox/PS controllers left unresponsive) — reported 2026-07-13. Staggering
    // gives each SDK's internal enumeration/driver-claim time to settle before the next
    // one starts. Mitigation, not a confirmed root cause fix — the vendor DLLs are opaque.
    private const int AutoOpenStaggerMs = 400;

    /// <summary>Opens MacroPad, Everest and DisplayPad drivers automatically on startup.</summary>
    private async void AutoOpenDrivers()
    {
        // PnlLoading is already visible (set in XAML); it covers everything
        // while we initialize.  We collapse it at the very end.

        // --- MacroPad ---
        if (NativeDependencyResolver.IsResolvable("MacroPadSDK.dll"))
        {
            int ver = _macroPad.SdkVersion();
            LblSdk.Text = $"MacroPadSDK.dll v{ver}";
            bool ok = _macroPad.Open(_hWnd);
            Log($"[AutoOpen] MacroPad -> {ok}");
            if (ok)
            {
                LblStatus.Text = "MacroPad driver opened.";
                RefreshDevices();
                StartLedPreview();
            }
        }
        else
        {
            Log("[AutoOpen] MacroPadSDK.dll not found — skipping");
        }

        await Task.Delay(AutoOpenStaggerMs);

        // --- Everest ---
        EvAutoOpen();

        await Task.Delay(AutoOpenStaggerMs);

        // --- Everest 60 (SDK session for numpad detection + LED preview,
        // needs the real HWND set just above — see Ev60AutoOpen's doc comment) ---
        Ev60AutoOpen();

        await Task.Delay(AutoOpenStaggerMs);

        // --- DisplayPad satellite ---
        DpOpenDriver();

        // --- All drivers attempted: hide loading overlay, land on the first visible tab.
        // TabHome is always visible and always first, so this normally lands on Home — the
        // intended landing page. Every device tab starts Collapsed until SetDeviceTabVisible
        // confirms a connection (see the comment above TabHome in MainWindow.xaml), so this
        // only falls through to a device tab in the (currently impossible, kept defensive)
        // case where TabHome itself isn't found. ---
        PnlLoading.Visibility = Visibility.Collapsed;
        TcDevices.SelectedItem = TcDevices.Items.OfType<TabItem>()
            .FirstOrDefault(t => t.Visibility == Visibility.Visible);
    }

    // ---- Toolbar -----------------------------------------------------------

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!NativeDependencyResolver.IsResolvable("MacroPadSDK.dll"))
        {
            CheckNativeDependency(); // reprint instructions
            return;
        }

        int ver = _macroPad.SdkVersion();
        LblSdk.Text = $"MacroPadSDK.dll v{ver}";
        Log($"GetDLLVersion -> {ver}");

        bool ok = _macroPad.Open(_hWnd);
        Log($"OpenUSBDriver -> {ok}");
        LblStatus.Text = ok ? "MacroPad driver opened." : "Driver open FAILED.";
        if (ok)
        {
            RefreshDevices();
            StartLedPreview();
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _macroPad.Close();
        _devices.Clear();
        _activeMpDeviceId = null;
        SetDeviceTabVisible(TabMacroPad, false);
        LblStatus.Text = "Driver closed.";
        Log("CloseUSBDriver");
    }

    // ---- Top-level device tab routing ------------------------------------

    /// <summary>
    /// Shows/hides device panels and updates the active device ID
    /// when the user clicks a top-level device tab.
    /// </summary>
    private void TcDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TcDevices.SelectedItem is not TabItem tab) return;
        string tag = tab.Tag as string ?? "";

        SetSettingsTabActive(false);
        SetMacroTabActive(false);

        // Show/hide content panels
        PnlSettings.Visibility   = Visibility.Collapsed;
        PnlMacro.Visibility      = Visibility.Collapsed;
        PnlHome.Visibility       = tag == "home"             ? Visibility.Visible : Visibility.Collapsed;
        PnlEverest.Visibility    = tag == "everest"          ? Visibility.Visible : Visibility.Collapsed;
        PnlEverest60.Visibility  = tag == "everest60"        ? Visibility.Visible : Visibility.Collapsed;
        PnlMakalu.Visibility     = tag == "makalu"           ? Visibility.Visible : Visibility.Collapsed;
        PnlMacroPad.Visibility   = tag == "macropad"         ? Visibility.Visible : Visibility.Collapsed;
        PnlDisplayPad.Visibility = tag.StartsWith("dp_")     ? Visibility.Visible : Visibility.Collapsed;

        // Shared top-right brightness bar: same active-device switch as the content panels
        BrEverest.Visibility    = PnlEverest.Visibility;
        BrMacroPad.Visibility   = PnlMacroPad.Visibility;
        BrDisplayPad.Visibility = PnlDisplayPad.Visibility;
        BrEverest60.Visibility  = PnlEverest60.Visibility;
        BrMakalu.Visibility     = PnlMakalu.Visibility;

        if (tag == "macropad")
            CbDevice_SelectionChanged(sender, e);
        else if (tag.StartsWith("dp_") && int.TryParse(tag[3..], out int dpId))
        {
            _activeDpDeviceId = dpId;
            CbDpDevice_SelectionChanged(sender, e);
        }

        // Everest Max: check dock/numpad attach immediately on tab open, then
        // keep polling every 3s only while this tab stays selected.
        if (tag == "everest")
        {
            UpdateKeyboardLayout();
            StartEvAccessoryPoll();
        }
        else
        {
            StopEvAccessoryPoll();
        }
    }

    /// <summary>Gear-icon Settings button: not part of TcDevices, so it's handled
    /// separately from TcDevices_SelectionChanged (deselects the device tabs).</summary>
    private void BtnSettingsTab_Click(object sender, RoutedEventArgs e)
    {
        PnlSettings.Visibility   = Visibility.Visible;
        PnlHome.Visibility       = Visibility.Collapsed;
        PnlMacro.Visibility      = Visibility.Collapsed;
        PnlEverest.Visibility    = Visibility.Collapsed;
        PnlEverest60.Visibility  = Visibility.Collapsed;
        PnlMakalu.Visibility     = Visibility.Collapsed;
        PnlMacroPad.Visibility   = Visibility.Collapsed;
        PnlDisplayPad.Visibility = Visibility.Collapsed;

        BrEverest.Visibility    = Visibility.Collapsed;
        BrMacroPad.Visibility   = Visibility.Collapsed;
        BrDisplayPad.Visibility = Visibility.Collapsed;
        BrEverest60.Visibility  = Visibility.Collapsed;
        BrMakalu.Visibility     = Visibility.Collapsed;

        TcDevices.SelectedIndex = -1;
        StopEvAccessoryPoll();
        SetSettingsTabActive(true);
        SetMacroTabActive(false);
    }

    /// <summary>Macro icon button: top-level section (not device-specific), same
    /// deselect-the-device-tabs pattern as <see cref="BtnSettingsTab_Click"/>.</summary>
    private void BtnMacroTab_Click(object sender, RoutedEventArgs e)
    {
        PnlMacro.Visibility      = Visibility.Visible;
        PnlSettings.Visibility   = Visibility.Collapsed;
        PnlHome.Visibility       = Visibility.Collapsed;
        PnlEverest.Visibility    = Visibility.Collapsed;
        PnlEverest60.Visibility  = Visibility.Collapsed;
        PnlMakalu.Visibility     = Visibility.Collapsed;
        PnlMacroPad.Visibility   = Visibility.Collapsed;
        PnlDisplayPad.Visibility = Visibility.Collapsed;

        BrEverest.Visibility    = Visibility.Collapsed;
        BrMacroPad.Visibility   = Visibility.Collapsed;
        BrDisplayPad.Visibility = Visibility.Collapsed;
        BrEverest60.Visibility  = Visibility.Collapsed;
        BrMakalu.Visibility     = Visibility.Collapsed;

        TcDevices.SelectedIndex = -1;
        StopEvAccessoryPoll();
        SetSettingsTabActive(false);
        SetMacroTabActive(true);
        SelectFirstMacro();
    }

    private void SetSettingsTabActive(bool active)
    {
        BtnSettingsTab.Background = active ? (Brush)FindResource("K2AccentBrush")     : Brushes.Transparent;
        BtnSettingsTab.Foreground = active ? (Brush)FindResource("K2AccentTextBrush") : (Brush)FindResource("K2TextMutedBrush");
    }

    private void SetMacroTabActive(bool active)
    {
        BtnMacroTab.Background = active ? (Brush)FindResource("K2AccentBrush")     : Brushes.Transparent;
        BtnMacroTab.Foreground = active ? (Brush)FindResource("K2AccentTextBrush") : (Brush)FindResource("K2TextMutedBrush");
    }

    /// <summary>Removes all top-level device tabs with the given tag prefix.</summary>
    private void RemoveDeviceTabs(string prefix)
    {
        foreach (var t in TcDevices.Items.OfType<TabItem>()
                     .Where(t => (t.Tag as string)?.StartsWith(prefix) == true)
                     .ToList())
            TcDevices.Items.Remove(t);
    }

    /// <summary>Shared by every device's profile list (K2SideProfileItemStyle's
    /// PreviewMouseRightButtonDown EventSetter): a plain ListBox doesn't move selection
    /// on a right-click the way it does on left-click, but every per-device profile
    /// ContextMenu (DpBuildProfileContextMenu etc.) is built once and acts on whatever
    /// row is currently SelectedItem — so the row under the cursor must become selected
    /// BEFORE the context menu opens, or "Rename"/"Delete"/... would silently act on the
    /// previously-selected profile instead of the one the user right-clicked.</summary>
    private void ProfileItem_PreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        // Never select the "+ New profile" row from a right-click: selecting it CREATES
        // (and activates, with a hardware repaint) a new profile via the list's
        // SelectionChanged handler — a mere right-click must not have that side effect.
        // The context menu is suppressed too (ProfileItem_ContextMenuOpening below).
        if (IsNewProfileRow(item.DataContext)) return;
        item.IsSelected = true;
    }

    /// <summary>Companion to <see cref="ProfileItem_PreviewRightClick"/>: since the "+ New
    /// profile" row is deliberately not selected on right-click, the shared ContextMenu
    /// (which acts on SelectedItem) would act on the wrong row — suppress it entirely.</summary>
    private void ProfileItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem item && IsNewProfileRow(item.DataContext))
            e.Handled = true;
    }

    /// <summary>True for the "+ New profile" placeholder row of any device's profile list
    /// (Everest 60/Makalu have fixed slots and no such row — their item records simply
    /// never match here).</summary>
    private static bool IsNewProfileRow(object? dataContext) => dataContext switch
    {
        DpProfileItem dp => dp.IsNew,
        EvProfileItem ev => ev.IsNew,
        MpProfileItem mp => mp.IsNew,
        _ => false,
    };

    /// <summary>Gear button for any device's profile row (see K2ProfileItemTemplate's doc
    /// comment in MainWindow.xaml). Wired to PreviewMouseLeftButtonDown rather than Click:
    /// marking e.Handled here, during the tunnel phase, reliably stops the row underneath
    /// from also selecting/switching profile — routing through Click instead was fragile
    /// (relied on the row's selection handler seeing an already-Handled bubble event, which
    /// didn't reliably happen and left the popup never opening) and is no longer used.
    /// Routes to the row's own device's XxShowProfileGear (rename/delete/link-launch-exe
    /// popup) based on the row's item type, since this is one shared DataTemplate used by
    /// all 5 device profile lists.</summary>
    private void ProfileGear_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        switch (fe.DataContext)
        {
            case DpProfileItem dp: DpShowProfileGear(dp); break;
            case EvProfileItem ev: EvShowProfileGear(ev); break;
            case Ev60ProfileItem ev60: Ev60ShowProfileGear(ev60); break;
            case MkProfileItem mk: MkShowProfileGear(mk); break;
            case MpProfileItem mp: MpShowProfileGear(mp); break;
        }
    }

    /// <summary>Shows or hides a static top-level device tab (Everest Max/60, Makalu,
    /// MacroPad — DisplayPad's per-device tabs are added/removed outright instead, see
    /// <see cref="RemoveDeviceTabs"/>) based on live connection state. If the tab being
    /// hidden is the one currently selected, moves selection to the next connected tab,
    /// or fully deselects (same as <see cref="BtnSettingsTab_Click"/>) if none are left —
    /// a disconnected device must never leave its content panel on screen.</summary>
    private void SetDeviceTabVisible(TabItem tab, bool connected)
    {
        var vis = connected ? Visibility.Visible : Visibility.Collapsed;
        if (tab.Visibility == vis) return;
        tab.Visibility = vis;
        RefreshHomeTiles();
        if (connected || !ReferenceEquals(TcDevices.SelectedItem, tab)) return;

        var next = TcDevices.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
        if (next is not null)
        {
            TcDevices.SelectedItem = next; // fires TcDevices_SelectionChanged, swaps the content panel
            return;
        }

        // Nothing left connected: clear every panel, same deselected state as the gear/macro buttons.
        TcDevices.SelectedIndex = -1;
        PnlEverest.Visibility = PnlEverest60.Visibility = PnlMakalu.Visibility =
            PnlMacroPad.Visibility = PnlDisplayPad.Visibility = Visibility.Collapsed;
        BrEverest.Visibility = BrEverest60.Visibility = BrMakalu.Visibility =
            BrMacroPad.Visibility = BrDisplayPad.Visibility = Visibility.Collapsed;
        StopEvAccessoryPoll();
    }

    private void BtnApOn_Click(object sender, RoutedEventArgs e) => ApEnable(true);
    private void BtnApOff_Click(object sender, RoutedEventArgs e) => ApEnable(false);

    private void ApEnable(bool enable)
    {
        if (CurrentDeviceId() is not int devId)
        {
            Log("AP Enable: no device selected");
            return;
        }
        bool ok = _macroPad.APEnable((uint)devId, enable);
        Log($"APEnable(id={devId}, enable={enable}) -> {ok}");
    }

    // ---- Device enumeration -----------------------------------------------

    /// <summary>Maps SDK ID → progressive label.</summary>
    private readonly Dictionary<uint, string> _mpDeviceLabels = new();

    private void RefreshDevices()
    {
        int count = _macroPad.DeviceCount();
        Log($"GetDevCount -> {count}");

        _devices.Clear();
        _mpDeviceLabels.Clear();

        // (active device tracked by _activeMpDeviceId, restored below)

        var items = new List<MpDeviceItem>();
        int progressive = 1;

        for (uint id = 1; id <= MacroPadService.MaxDeviceCount; id++)
        {
            if (!_macroPad.IsPlugged(id)) continue;

            string label = $"MacroPad {progressive}";
            _mpDeviceLabels[id] = label;

            var row = new MacroPadDeviceRow { Id = id, Label = label, Plugged = true };

            ushort fw = _macroPad.FirmwareVersion(id);
            row.FirmwareVersion = fw.ToString();

            if (_macroPad.TryGetDeviceInfo(id, out var di))
            {
                row.Vid = $"0x{di.vid:X4}";
                row.Pid = $"0x{di.pid:X4}";
            }

            if (_macroPad.TryGetFirmwareInfo(id, out var fi))
                row.CurrentProfile = fi.currentlyProfileIndex.ToString();

            _devices.Add(row);
            items.Add(new MpDeviceItem(id, label));
            progressive++;
        }

        // Set active device (the MacroPad tab itself is static in XAML — only its
        // Visibility is dynamic, toggled below based on whether any unit is plugged)
        if (items.Count > 0)
        {
            uint keep = (uint?)_activeMpDeviceId is uint prev && items.Any(x => x.SdkId == prev)
                        ? prev : items[0].SdkId;
            _activeMpDeviceId = (int)keep;
            // Refresh the key grid only if the MacroPad panel is currently visible
            if (PnlMacroPad.Visibility == Visibility.Visible)
                CbDevice_SelectionChanged(this, null!);
        }
        SetDeviceTabVisible(TabMacroPad, items.Count > 0);

        LblStatus.Text = $"Connected MacroPad devices: {items.Count}.";
        Log($"Connected devices -> [{string.Join(", ", items.ConvertAll(x => $"{x.SdkId}({x.Label})"))}]");
    }

    // ---- SDK events (may arrive from non-UI threads) -----------------------

    private void OnMacroPadKey(object? sender, MacroPadKeyEventArgs e) =>
        Dispatcher.BeginInvoke(() => HandleKeyEvent(e));

    private void OnMacroPadPlug(object? sender, MacroPadPlugEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            Log($"PLUG   wParam={e.WParam} lParam={e.LParam} -> re-enumerating devices");
            if (_macroPad.IsOpen) RefreshDevices();
        });

    private void OnMacroPadProgress(object? sender, MacroPadProgressEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
            Log($"FW     update progress = {e.Percent}%{(e.Failed ? " (FAILED)" : "")}"));

    // ---- WndProc: forward Windows messages to the MacroPad SDK -------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == MacroPadSdkNative.WM_DEVICE_PLUG || msg == MacroPadSdkNative.WM_FW_PROGRESS)
            _macroPad.HandleWindowMessage(msg, wParam, lParam);

        // Backlight auto-off wake, decoupled from SDKDLL.dll's own (unreliable
        // after idle, see RawKeyboardActivityWatcher's doc comment) KeyEvent —
        // real physical keyboard activity via Windows Raw Input.
        if (Services.RawKeyboardActivityWatcher.IsKeyboardInput(msg, lParam))
        {
            _evAutoOffTimer?.RegisterActivity();
            // Everest 60 (2026-07-21, user report): same symptom as Everest Max's
            // original bug (see above) — after the auto-off timer's own native
            // SetEffect(Off) call, HandleEv60Key's SDK KeyEvent (Everest60SdkService)
            // stops arriving, so physical keys no longer woke the backlight. Ev60's
            // key-binding execution still goes through the normal SDK KeyEvent path
            // (HandleEv60Key) — this only decouples the auto-off wake signal from it,
            // same split as Everest Max above.
            _ev60AutoOffTimer?.RegisterActivity();
        }

        return IntPtr.Zero;
    }

    // ---- Utilities ---------------------------------------------------------

    /// <summary>
    /// Shows a simple modal text-input dialog for renaming a device or profile.
    /// <paramref name="title"/> and <paramref name="promptText"/> default to the
    /// device-rename strings when not supplied.
    /// Returns the trimmed text entered by the user, or null if cancelled / empty.
    /// </summary>
    internal string? ShowRenameDialog(string current,
        string? title = null, string? promptText = null)
    {
        string? result = null;

        var prompt = new TextBlock
        {
            Text = promptText ?? Loc.Get("rename_device_prompt"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Margin = new Thickness(12, 12, 12, 4),
        };
        var tb = new TextBox
        {
            Text = current,
            Margin = new Thickness(12, 4, 12, 8),
            Padding = new Thickness(4, 3, 4, 3),
        };
        var btnOk = new Button
        {
            Content = Loc.Get("ok"),
            IsDefault = true,
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(8, 4, 8, 4),
        };
        var btnCancel = new Button
        {
            Content = Loc.Get("cancel"),
            IsCancel = true,
            Width = 80,
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
        panel.Children.Add(tb);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title ?? Loc.Get("rename_device_title"),
            Content = panel,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
        };

        btnOk.Click     += (_, _) => { result = tb.Text.Trim(); dlg.Close(); };
        btnCancel.Click += (_, _) => dlg.Close();
        tb.Loaded       += (_, _) => { tb.SelectAll(); tb.Focus(); };

        dlg.ShowDialog();
        return result?.Length > 0 ? result : null;
    }

    /// <summary>Appends a line to the event console and the log file.
    /// Suppressed entirely when LogLevel is Off (General Settings tab).</summary>
    private void Log(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
        App.WriteLog("[UI] " + text);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _ledPoller?.Dispose();
        _macroPad.Dispose();
        _store.Dispose();
        _usbRec.Dispose();
        CleanupDisplayPad();
        _trayIcon?.Dispose();

        // Put Base Camp back the way K2 found it, if the user asked for that (see
        // Settings > General > "Restart Base Camp on close").
        if (AppSettings.RestartBaseCampOnClose)
            Services.BaseCampProcessGuard.RestartKilledProcesses(App.WriteLog);

        // ShutdownMode is OnExplicitShutdown (see App.OnStartup) so that hiding the
        // window to the tray never ends the process — the real close must ask for it.
        Application.Current.Shutdown();
    }

    private void CkSettingsSync_Checked(object sender, RoutedEventArgs e)
    {

    }

    private void TxtEvAutoOffSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {

    }
}

// ---- MacroPad device combo wrapper ----
public sealed class MpDeviceItem(uint sdkId, string label)
{
    public uint SdkId { get; } = sdkId;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

// ---- MacroPad profile combo wrapper ----
public sealed class MpProfileItem(int slot, string label)
{
    public int Slot { get; } = slot;
    public string Label { get; } = label;
    public bool IsNew => Label.StartsWith("+");
    public bool IsRealProfile => !IsNew;
    public override string ToString() => Label;
}

// ---- Everest profile combo wrapper ----
public sealed class EvProfileItem(int slot, string label)
{
    public int Slot { get; } = slot;
    public string Label { get; } = label;
    public bool IsNew => Label.StartsWith("+");
    public bool IsRealProfile => !IsNew;
    public override string ToString() => Label;
}

/// <summary>Row in the "Connected MacroPad devices" table.</summary>
public sealed class MacroPadDeviceRow
{
    public uint Id { get; set; }
    public string Label { get; set; } = "";
    public bool Plugged { get; set; }
    public string FirmwareVersion { get; set; } = "—";
    public string Vid { get; set; } = "—";
    public string Pid { get; set; } = "—";
    public string CurrentProfile { get; set; } = "—";
}
