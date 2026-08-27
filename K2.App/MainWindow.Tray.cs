// MainWindow.Tray.cs — partial class: system-tray integration.
//
// Three related General Settings toggles (see MainWindow.Settings.cs / PnlSettings
// in MainWindow.xaml):
//   - "Close to tray"           (AppSettings.CloseToTray)          — closing the
//     window (X button) hides it to the tray instead of exiting the app.
//   - "Start with Windows"      (Services.K2AutostartService)      — per-user
//     Scheduled Task (ONLOGON, /RL HIGHEST) for K2.App.exe itself, distinct from
//     Base Camp's own (see Services.BaseCampProcessGuard / MainWindow.Settings.cs
//     CkBcAutostart). Not the HKCU Run key: K2.App's requireAdministrator manifest
//     means Windows would silently skip a Run-key entry at logon (no auto-elevation).
//   - "Start minimized to tray" (AppSettings.StartMinimizedToTray) — read once by
//     App.OnStartup, which Shows() the window (so drivers still auto-open via
//     OnSourceInitialized -> AutoOpenDrivers) then immediately hides it to the tray,
//     instead of leaving it on screen.
//
// The tray icon is created once (constructor) and stays Visible for the whole
// lifetime of the process — window shown, hidden, maximized or full screen alike,
// so it never disappears out from under the user. It is disposed in OnWindowClosed
// alongside the other per-process resources.
//
// It is a Services.TrayIconNative, NOT System.Windows.Forms.NotifyIcon: the latter
// registers the icon under an identity Windows recycles across processes, which let
// K2's icon collide with another program's (shared slot, clicks delivered to both).
// See TrayIconNative.cs for the full rationale.

using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using K2.App.Services;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    private TrayIconNative? _trayIcon;

    // Set by the tray's "Exit" item before calling Close(), so MainWindow_Closing
    // lets the close proceed instead of redirecting it to the tray.
    private bool _reallyClosing;

    private void InitTray()
    {
        // Fully qualified: MainWindow (a Window) already has an instance member named
        // "Icon" (ImageSource), which would otherwise shadow the System.Drawing.Icon type.
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
        _trayIcon = new TrayIconNative { Text = "K2" };
        if (icon is not null) _trayIcon.Icon = icon;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.BalloonClick += (_, _) => OpenSettingsFromNotification();

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
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Startup "an update is available" toast (see MainWindow.Settings.cs,
    /// the silent check kicked off by InitUpdatesPanel). It is a tray balloon, which
    /// Windows 10/11 renders as a normal toast and files in the Action Center; if the
    /// user has K2's notifications turned off the shell just drops it, and the badge
    /// on the Settings gear (SetUpdateBadge) is then the only cue — by design, a
    /// missed toast should never be the only way to learn about an update.</summary>
    internal void ShowUpdateNotification(string version)
    {
        _trayIcon?.ShowBalloon(
            Loc.Get("update_notify_title"),
            Loc.Get("update_notify_body", version));
    }

    /// <summary>Click on the update toast: bring K2 back up (it may well be hidden in
    /// the tray, since the check runs at startup) and land on Settings, scrolled to the
    /// Updates group so the Download button is right there.</summary>
    private void OpenSettingsFromNotification()
    {
        Dispatcher.Invoke(() =>
        {
            RestoreFromTray();
            BtnSettingsTab_Click(this, new RoutedEventArgs());
            GbAppUpdate.BringIntoView();
        });
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
