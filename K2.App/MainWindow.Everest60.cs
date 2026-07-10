using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: Everest 60 tab shell — sidebar, device image, right
/// column, section navigation. Section CONTENT (RGB Lighting/Side Ring)
/// lives in <see cref="Everest60RgbPanel"/>, wired as a direct child of
/// MainWindow.xaml (not nested inside another custom control — see
/// MakaluDpiRemapPanel.xaml for why). See <see cref="Everest60HidNative"/>
/// for why this talks HID Feature Reports instead of the SDK, and its
/// remarks for what's NOT implemented yet (key remapping/macros: firmware
/// protocol not reverse-engineered by any known source — that's also why,
/// unlike Makalu, the device image here has no clickable hotspots).
///
/// RbEv60SecRgb.IsChecked is set in <see cref="InitEv60SectionNav"/>, NOT
/// via IsChecked="True" in XAML — see the identical note on RbMkSecRgb in
/// MainWindow.Makalu.cs: WPF fires RadioButton.Checked synchronously the
/// instant BAML sets IsChecked="True", mid-InitializeComponent(), before
/// later-declared elements (Ev60RgbPanel here) are assigned. Root-caused
/// with WinDbg+SOS 2026-07-10 on the Makalu tab — see CHANGELOG.md.
///
/// State (effect + params + colors) lives only in memory for this first cut —
/// per-session persistence (like Everest Max's <c>rgb.*</c> Settings keys) is
/// a future step once the panel has proven itself on real hardware.
/// </summary>
public partial class MainWindow
{
    private Everest60Service _ev60 = null!;
    private DispatcherTimer? _ev60PollTimer;
    private bool _ev60Connected;

    /// <summary>Called once from the MainWindow constructor.</summary>
    private void InitEverest60Module()
    {
        _ev60 = new Everest60Service(LogEverest60);

        Ev60RgbPanel.Init(_ev60, LogEverest60);
        InitEv60SectionNav();

        _ev60PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _ev60PollTimer.Tick += (_, _) => Ev60RefreshStatus();
        _ev60PollTimer.Start();
        Ev60RefreshStatus();
    }

    // ------------------------------------------------------------
    // Section navigation — toggles SecRgb/SecSideRing inside Ev60RgbPanel.
    // ------------------------------------------------------------

    private FrameworkElement? _activeEv60Section;

    /// <summary>Sets the default section AFTER InitializeComponent() has
    /// fully run — see the class doc comment for why this isn't
    /// IsChecked="True" in XAML.</summary>
    private void InitEv60SectionNav() => RbEv60SecRgb.IsChecked = true; // fires Ev60Section_Changed -> ShowEv60Section

    private void Ev60Section_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbEv60SecRgb)      => Ev60RgbPanel.SecRgb,
            nameof(RbEv60SecSideRing) => Ev60RgbPanel.SecSideRing,
            _                         => null
        };

        if (panel is not null)
            ShowEv60Section(panel);
    }

    private void ShowEv60Section(FrameworkElement panel)
    {
        if (_activeEv60Section is not null)
            _activeEv60Section.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
        _activeEv60Section = panel;
    }

    // ------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------

    private void Ev60RefreshStatus()
    {
        bool connected = _ev60.IsConnected(out string model);
        _ev60Connected = connected;
        Ev60RgbPanel.SetConnected(connected);
        LblEv60Status.Text = connected
            ? Loc.Get("ev60_status_connected", model)
            : Loc.Get("ev60_status_disconnected");
        LblEv60Status.Foreground = connected
            ? (Brush)FindResource("K2AccentBrush")
            : (Brush)FindResource("K2TextMutedBrush");
    }

    private void BtnEv60Refresh_Click(object sender, RoutedEventArgs e) => Ev60RefreshStatus();

    // ------------------------------------------------------------
    // Device rename (no per-device SQLite store for Everest 60 — see
    // AppSettings.Everest60DeviceName)
    // ------------------------------------------------------------

    private void BtnEv60Rename_Click(object sender, RoutedEventArgs e)
    {
        string current = AppSettings.Everest60DeviceName ?? (TabEverest60.Header as string) ?? Loc.Get("tab_everest60");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabEverest60.Header = name;
        AppSettings.SetEverest60DeviceName(name);
    }

    // ------------------------------------------------------------
    // Log
    // ------------------------------------------------------------

    private void LogEverest60(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        App.WriteLog("[Everest60] " + text);
        if (TxtEv60Log == null) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtEv60Log.AppendText(line + Environment.NewLine);
        TxtEv60Log.ScrollToEnd();
    }
}
