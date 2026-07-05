using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using K2.DisplayPad.Dialogs;

namespace K2.DisplayPad;

public partial class App : Application
{
    public static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "K2.DisplayPad.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        // Initialize localization before any UI is created.
        Core.Loc.Init();

        WriteLog($"=== App start {DateTime.Now:O} pid={Environment.ProcessId} lang={Core.Loc.CurrentLang} ===");
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
