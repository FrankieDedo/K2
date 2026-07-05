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
        e.Handled = true; // mantieni viva la finestra
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        WriteLog($"[DomainUnhandled terminating={e.IsTerminating}] " + e.ExceptionObject);
        // Se l'app sta terminando, prova comunque a mostrare il dialog
        try
        {
            Dispatcher.Invoke(() =>
                ShowError("Errore non gestito (dominio)", e.ExceptionObject?.ToString() ?? "?"));
        }
        catch { /* il dispatcher potrebbe essere gia' morto */ }
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
            // Ultima spiaggia
            try
            {
                MessageBox.Show($"{message}\n\n(log: {LogPath})", title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Append thread-safe a un file di log accanto all'eseguibile.</summary>
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
        catch { /* mai far esplodere il logger */ }
    }
}
