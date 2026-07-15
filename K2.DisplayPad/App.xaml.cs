using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using K2.DisplayPad.Dialogs;

namespace K2.DisplayPad;

public partial class App : Application
{
    // %LocalAppData%\K2.DisplayPad\, not next to the exe: K2 installs to Program
    // Files by default (admin-write-protected), so writing there without elevation
    // used to fail silently and drop all logging. Matches the convention already
    // used by StateStore/CellConfigDialog/etc. in this project.
    public static readonly string LogPath = EnsureLogPath();

    private static string EnsureLogPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K2.DisplayPad");
        try { Directory.CreateDirectory(dir); } catch { /* best-effort, same as the writers below */ }
        return Path.Combine(dir, "K2.DisplayPad.log");
    }

    // Held for the process lifetime; released automatically by the OS on exit.
    private static Mutex? _singleInstanceMutex;

    // Set by the constructor; checked in OnStartup — NOT shown/handled in the
    // constructor itself, because MessageBox.Show() there pumps the dispatcher and
    // can trigger the StartupUri window (MainWindow.xaml) before App's resources
    // (K2Theme.xaml, merged in App.xaml) are loaded, crashing on a missing
    // StaticResource. See the equivalent fix/comment in K2.App/App.xaml.cs.
    private static bool _singleInstanceGranted;

    public App()
    {
        // Initialize localization before any UI is created.
        Core.Loc.Init();

        _singleInstanceGranted = AcquireSingleInstanceLock();

        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        WriteLog($"=== App start {DateTime.Now:O} pid={Environment.ProcessId} lang={Core.Loc.CurrentLang} ===");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!_singleInstanceGranted)
        {
            MessageBox.Show(Core.Loc.Get("app_already_running"), Core.Loc.Get("app_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Apply the user's saved UI font (shared app_settings.json — see
        // AppSettings.AppFontFamily / Settings > Font in K2.App) before the
        // StartupUri window is created below.
        Core.Services.FontCatalog.Apply(Core.AppSettings.AppFontFamily);

        base.OnStartup(e); // creates/shows the StartupUri window (MainWindow.xaml)
    }

    /// <summary>
    /// Acquires a named mutex so only one K2.DisplayPad instance can run per user session.
    /// Returns false if another instance already holds it.
    /// </summary>
    private static bool AcquireSingleInstanceLock()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "K2DisplayPad_SingleInstance_Mutex", out bool createdNew);
        return createdNew;
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("[DispatcherUnhandled] " + e.Exception);
        ShowError("Errore non gestito (UI)", e.Exception.ToString());
        e.Handled = true; // keep the window alive
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        WriteLog($"[DomainUnhandled terminating={e.IsTerminating}] " + e.ExceptionObject);
        // If the app is terminating, still try to show the dialog
        try
        {
            Dispatcher.Invoke(() =>
                ShowError("Errore non gestito (dominio)", e.ExceptionObject?.ToString() ?? "?"));
        }
        catch { /* the dispatcher might already be dead */ }
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLog("[UnobservedTask] " + e.Exception);
        e.SetObserved();
    }

    private void ShowError(string title, string message)
    {
        try
        {
            var owner = Current?.MainWindow is { IsLoaded: true } w ? w : null;
            ErrorDialog.Show(title, message, LogPath, owner);
        }
        catch (Exception ex)
        {
            WriteLog("[ShowError] dialog itself threw: " + ex);
            // Last resort
            try
            {
                MessageBox.Show($"{message}\n\n(log: {LogPath})", title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Thread-safe append to a log file next to the executable.</summary>
    public static void WriteLog(string text)
    {
        try
        {
            lock (LogPath)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
            }
        }
        catch { /* never let the logger throw */ }
    }
}
