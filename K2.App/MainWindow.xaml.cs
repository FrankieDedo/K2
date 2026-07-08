using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        InitKeysModule();        // MacroPad: 12-key grid (2×6 rotatable) + profile selector
        InitMacroLedPanel();     // MacroPad: LED lighting panel (firmware presets)
        InitActionEngine();      // MacroPad: action engine + Python bridge
        InitEverestModule();     // Everest Max: on-demand key list + dedicated action engine
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
        Log("  2) keep Base Camp installed: K2 detects it automatically;");
        Log($"  3) set the environment variable {NativeDependencyResolver.BaseCampDirEnvVar}");
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

        // Auto-open all drivers after the window is fully rendered
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(AutoOpenDrivers));
    }

    /// <summary>Opens MacroPad, Everest and DisplayPad drivers automatically on startup.</summary>
    private void AutoOpenDrivers()
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

        // --- Everest ---
        EvAutoOpen();

        // --- DisplayPad satellite ---
        DpOpenDriver();

        // --- All drivers attempted: hide loading overlay, select the first available tab ---
        PnlLoading.Visibility = Visibility.Collapsed;
        TcDevices.SelectedItem = TcDevices.Items.OfType<TabItem>().FirstOrDefault();
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
        PnlEverest.Visibility    = tag == "everest"          ? Visibility.Visible : Visibility.Collapsed;
        PnlMacroPad.Visibility   = tag == "macropad"         ? Visibility.Visible : Visibility.Collapsed;
        PnlDisplayPad.Visibility = tag.StartsWith("dp_")     ? Visibility.Visible : Visibility.Collapsed;

        // Shared top-right brightness bar: same active-device switch as the content panels
        BrEverest.Visibility    = PnlEverest.Visibility;
        BrMacroPad.Visibility   = PnlMacroPad.Visibility;
        BrDisplayPad.Visibility = PnlDisplayPad.Visibility;

        if (tag == "macropad")
            CbDevice_SelectionChanged(sender, e);
        else if (tag.StartsWith("dp_") && int.TryParse(tag[3..], out int dpId))
        {
            _activeDpDeviceId = dpId;
            CbDpDevice_SelectionChanged(sender, e);
        }
    }

    /// <summary>Gear-icon Settings button: not part of TcDevices, so it's handled
    /// separately from TcDevices_SelectionChanged (deselects the device tabs).</summary>
    private void BtnSettingsTab_Click(object sender, RoutedEventArgs e)
    {
        PnlSettings.Visibility   = Visibility.Visible;
        PnlMacro.Visibility      = Visibility.Collapsed;
        PnlEverest.Visibility    = Visibility.Collapsed;
        PnlMacroPad.Visibility   = Visibility.Collapsed;
        PnlDisplayPad.Visibility = Visibility.Collapsed;

        BrEverest.Visibility    = Visibility.Collapsed;
        BrMacroPad.Visibility   = Visibility.Collapsed;
        BrDisplayPad.Visibility = Visibility.Collapsed;

        TcDevices.SelectedIndex = -1;
        SetSettingsTabActive(true);
        SetMacroTabActive(false);
    }

    /// <summary>Macro icon button: top-level section (not device-specific), same
    /// deselect-the-device-tabs pattern as <see cref="BtnSettingsTab_Click"/>.</summary>
    private void BtnMacroTab_Click(object sender, RoutedEventArgs e)
    {
        PnlMacro.Visibility      = Visibility.Visible;
        PnlSettings.Visibility   = Visibility.Collapsed;
        PnlEverest.Visibility    = Visibility.Collapsed;
        PnlMacroPad.Visibility   = Visibility.Collapsed;
        PnlDisplayPad.Visibility = Visibility.Collapsed;

        BrEverest.Visibility    = Visibility.Collapsed;
        BrMacroPad.Visibility   = Visibility.Collapsed;
        BrDisplayPad.Visibility = Visibility.Collapsed;

        TcDevices.SelectedIndex = -1;
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

        // Set active device (MacroPad tab is always static — no dynamic tab management)
        if (items.Count > 0)
        {
            uint keep = (uint?)_activeMpDeviceId is uint prev && items.Any(x => x.SdkId == prev)
                        ? prev : items[0].SdkId;
            _activeMpDeviceId = (int)keep;
            // Refresh the key grid only if the MacroPad panel is currently visible
            if (PnlMacroPad.Visibility == Visibility.Visible)
                CbDevice_SelectionChanged(this, null!);
        }

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
        // ShutdownMode is OnExplicitShutdown (see App.OnStartup) so that hiding the
        // window to the tray never ends the process — the real close must ask for it.
        Application.Current.Shutdown();
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
    public override string ToString() => Label;
}

// ---- Everest profile combo wrapper ----
public sealed class EvProfileItem(int slot, string label)
{
    public int Slot { get; } = slot;
    public string Label { get; } = label;
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
