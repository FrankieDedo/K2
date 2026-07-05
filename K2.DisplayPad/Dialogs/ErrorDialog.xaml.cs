using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace K2.DisplayPad.Dialogs;

public partial class ErrorDialog : Window
{
    private readonly string _logPath;

    public ErrorDialog(string title, string message, string logPath)
    {
        InitializeComponent();
        Title = "K2 — " + title;
        LblHeader.Text = title;
        TxtMessage.Text = message;
        _logPath = logPath;
        LblHint.Text = $"Dettaglio anche in: {logPath}";

        // Selezione automatica del testo per facilitare il Ctrl+C
        Loaded += (_, _) =>
        {
            TxtMessage.Focus();
            TxtMessage.SelectAll();
        };
    }

    /// <summary>Helper per chiamare il dialog senza preoccuparsi del thread UI.</summary>
    public static void Show(string title, string message, string logPath, Window? owner = null)
    {
        var d = new ErrorDialog(title, message, logPath);
        if (owner is not null)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        d.ShowDialog();
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TxtMessage.Text);
            LblHint.Text = "Copiato negli appunti.";
        }
        catch (Exception ex)
        {
            LblHint.Text = $"Copia fallita: {ex.Message}";
        }
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_logPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _logPath,
                    UseShellExecute = true
                });
            }
            else
            {
                LblHint.Text = $"File log non trovato: {_logPath}";
            }
        }
        catch (Exception ex)
        {
            LblHint.Text = $"Apri log fallita: {ex.Message}";
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
