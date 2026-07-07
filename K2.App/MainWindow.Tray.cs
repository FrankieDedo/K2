// MainWindow.Tray.cs — partial class: system-tray integration.
//
// Three related General Settings toggles (see MainWindow.Settings.cs / PnlSettings
// in MainWindow.xaml):
//   - "Close to tray"           (AppSettings.CloseToTray)          — closing the
//     window (X button) hides it to the tray instead of exiting the app.
//   - "Start with Windows"      (Services.K2AutostartService)      — HKCU Run key
//     entry for K2.App.exe itself (distinct from Base Camp's own, see
//     Services.BaseCampProcessGuard / MainWindow.Settings.cs CkBcAutostart).
//   - "Start minimized to tray" (AppSettings.StartMinimizedToTray) — read once by
//     App.OnStartup, which Shows() the window (so drivers still auto-open via
//     OnSourceInitialized -> AutoOpenDrivers) then immediately hides it to the tray,
//     instead of leaving it on screen.
//
// The NotifyIcon is created once (constructor) so "close to tray" always has it
// ready; it is only made Visible while the window itself is hidden, and disposed
// in OnWindowClosed alongside the other per-process resources.

using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    private NotifyIcon? _trayIcon;

    // Set by the tray's "Exit" item before calling Close(), so MainWindow_Closing
    // lets the close proceed instead of redirecting it to the tray.
    private bool _reallyClosing;

    private void InitTray()
    {
        // Fully qualified: MainWindow (a Window) already has an instance member named
        // "Icon" (ImageSource), which would otherwise shadow the System.Drawing.Icon type.
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
        _trayIcon = new NotifyIcon
        {
            Text = "K2",
            Visible = false,
        };
        if (icon is not null) _trayIcon.Icon = icon;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.Get("tray_show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.Get("tray_exit"), null, (_, _) => ExitFromTray());
        _trayIcon.ContextMenuStrip = menu;

        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_reallyClosing || !AppSettings.CloseToTray) return;
        e.Cancel = true;
        HideToTray();
    }

    /// <summary>Hides the window and shows the tray icon. Used both by "close to
    /// tray" and by the "start minimized to tray" startup path (see App.OnStartup /
    /// StartMinimizedToTray below).</summary>
    private void HideToTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        _reallyClosing = true;
        Close();
    }

    /// <summary>Called by App.OnStartup instead of Show() when AppSettings.StartMinimizedToTray
    /// is set. Shows the window first (so OnSourceInitialized -> AutoOpenDrivers still runs
    /// exactly as on a normal start) then immediately hides it to the tray — no flicker,
    /// since nothing yields back to the message loop between the two calls.</summary>
    internal void StartMinimizedToTray()
    {
        Show();
        HideToTray();
    }
}
