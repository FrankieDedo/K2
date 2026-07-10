using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: Makalu 67/Max mouse tab shell — sidebar, device image
/// + clickable hotspots, right column, section navigation. Section CONTENT
/// (RGB/Settings, DPI/Remap) lives in two UserControls
/// (<see cref="MakaluRgbSettingsPanel"/>, <see cref="MakaluDpiRemapPanel"/>)
/// wired here as direct children of MainWindow.xaml (not nested inside
/// another custom control — see MakaluDpiRemapPanel.xaml for why).
///
/// RbMkSecRgb.IsChecked is set here in <see cref="InitMkSectionNav"/>, NOT
/// via IsChecked="True" in XAML — that used to null-ref inside
/// MkSection_Changed, because WPF fires RadioButton.Checked synchronously
/// the instant BAML sets IsChecked="True", which happens mid-
/// InitializeComponent(), before MkRgbSettings/MkDpiRemap (declared later
/// in MainWindow.xaml) are assigned. Root-caused with WinDbg+SOS
/// 2026-07-10 — see CHANGELOG.md for the full session. This was never a
/// JIT/CLR bug.
/// </summary>
public partial class MainWindow
{
    private MakaluService _makalu = null!;
    private DispatcherTimer? _mkPollTimer;
    private bool _mkConnected;
    private MakaluService.DeviceInfo _mkInfo =
        new(MakaluService.Model.Makalu67, "Makalu 67", 6, MakaluProtocol.DpiMin67);

    /// <summary>Called once from the MainWindow constructor.</summary>
    private void InitMakaluModule()
    {
        _makalu = new MakaluService(LogMakalu);

        MkRgbSettings.Init(_makalu, LogMakalu);
        MkDpiRemap.Init(_makalu, LogMakalu);
        BuildMkHotspots();
        InitMkSectionNav();

        _mkPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mkPollTimer.Tick += (_, _) => MkRefreshStatus();
        _mkPollTimer.Start();
        MkRefreshStatus();
    }

    // ------------------------------------------------------------
    // Section navigation — toggles the section Grids nested inside
    // MkRgbSettings (SecRgb/SecSettings) and MkDpiRemap (SecDpi/SecRemap).
    // ------------------------------------------------------------

    private FrameworkElement? _activeMkSection;

    /// <summary>Sets the default section AFTER InitializeComponent() has fully
    /// run (called from InitMakaluModule, which runs after the ctor's
    /// InitializeComponent() call) — setting RbMkSecRgb.IsChecked here, not in
    /// XAML, is what avoids the null-ref: see the comment on RbMkSecRgb in
    /// MainWindow.xaml.</summary>
    private void InitMkSectionNav() => RbMkSecRgb.IsChecked = true; // fires MkSection_Changed -> ShowMkSection

    private void MkSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbMkSecRgb)      => MkRgbSettings.SecRgb,
            nameof(RbMkSecDpi)      => MkDpiRemap.SecDpi,
            nameof(RbMkSecRemap)    => MkDpiRemap.SecRemap,
            nameof(RbMkSecSettings) => MkRgbSettings.SecSettings,
            _                       => null
        };

        if (panel is not null)
            ShowMkSection(panel);
    }

    private void ShowMkSection(FrameworkElement panel)
    {
        if (_activeMkSection is not null)
            _activeMkSection.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
        _activeMkSection = panel;
    }

    // ------------------------------------------------------------
    // Device image hotspots — click a button on the mouse image to jump to
    // the Remap section with that physical button pre-selected. Positions
    // are hand-estimated against Assets/makalu_mouse.png (not pixel-measured
    // — no reference geometry exists for this device the way keytop.png's
    // Media Dock hotspots were).
    // ------------------------------------------------------------

    private static readonly Dictionary<int, (double X, double Y)> MkHotspotPos67 = new()
    {
        [1] = (70, 90),   // left
        [2] = (120, 90),  // right
        [3] = (95, 75),   // middle/wheel
        [4] = (15, 180),  // back
        [5] = (15, 230),  // forward
        [6] = (95, 115),  // dpi
    };
    private static readonly Dictionary<int, (double X, double Y)> MkHotspotPosMax = new()
    {
        [1] = (70, 90),    // left
        [2] = (120, 90),   // right
        [3] = (95, 75),    // middle/wheel
        [4] = (95, 115),   // dpi
        [5] = (175, 180),  // extra button 5
        [6] = (175, 230),  // extra button 6
        [7] = (15, 180),   // forward
        [8] = (15, 230),   // back
    };

    private Dictionary<int, (double X, double Y)> MkHotspotPos =>
        _mkInfo.Model == MakaluService.Model.MakaluMax ? MkHotspotPosMax : MkHotspotPos67;

    private void BuildMkHotspots()
    {
        CvsMkHotspots.Children.Clear();
        var names = MakaluRemapData.BtnNames(_mkInfo.Model);
        foreach (var kv in MkHotspotPos)
        {
            int btnIdx = kv.Key;
            var (x, y) = kv.Value;
            var dot = new Ellipse
            {
                Width = 22, Height = 22,
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Stroke = (Brush)FindResource("K2AccentBrush"),
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                ToolTip = names.TryGetValue(btnIdx, out var key) ? Loc.Get(key) : $"#{btnIdx}",
            };
            dot.MouseLeftButtonUp += (_, _) => MkHotspotClicked(btnIdx);
            Canvas.SetLeft(dot, x - dot.Width / 2);
            Canvas.SetTop(dot, y - dot.Height / 2);
            CvsMkHotspots.Children.Add(dot);
        }
    }

    private void MkHotspotClicked(int btnIdx)
    {
        RbMkSecRemap.IsChecked = true; // fires MkSection_Changed -> ShowMkSection
        MkDpiRemap.SelectRemapButton(btnIdx);
    }

    // ------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------

    private void MkRefreshStatus()
    {
        bool wasConnected = _mkConnected;
        bool connected = _makalu.IsConnected(out var info);
        _mkConnected = connected;
        MkRgbSettings.SetConnected(connected);
        LblMkStatus.Text = connected
            ? Loc.Get("makalu_status_connected", info.Label)
            : Loc.Get("makalu_status_disconnected");
        LblMkStatus.Foreground = connected
            ? (Brush)FindResource("K2AccentBrush")
            : (Brush)FindResource("K2TextMutedBrush");

        if (connected && (!wasConnected || info.Model != _mkInfo.Model))
        {
            _mkInfo = info;
            MkRgbSettings.UpdateDeviceInfo(info);
            MkDpiRemap.UpdateDeviceInfo(info);
            BuildMkHotspots();
        }
    }

    private void BtnMkRefresh_Click(object sender, RoutedEventArgs e) => MkRefreshStatus();

    // ------------------------------------------------------------
    // Device rename (no per-device SQLite store for Makalu — see
    // AppSettings.MakaluDeviceName)
    // ------------------------------------------------------------

    private void BtnMkRename_Click(object sender, RoutedEventArgs e)
    {
        string current = AppSettings.MakaluDeviceName ?? (TabMakalu.Header as string) ?? Loc.Get("tab_makalu");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabMakalu.Header = name;
        AppSettings.SetMakaluDeviceName(name);
    }

    // ------------------------------------------------------------
    // Log
    // ------------------------------------------------------------

    private void LogMakalu(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        App.WriteLog("[Makalu] " + text);
        if (TxtMkLog == null) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtMkLog.AppendText(line + Environment.NewLine);
        TxtMkLog.ScrollToEnd();
    }
}
