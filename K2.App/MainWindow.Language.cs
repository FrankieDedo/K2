// MainWindow.Language.cs — language switcher (status bar button + context menu)
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    // Called from the MainWindow constructor, after InitializeComponent.
    private void InitLanguageMenu()
    {
        TxtLangLabel.Text = Loc.CurrentLang.ToUpperInvariant();
        Loc.RestartRequested += _ => RestartApp();
    }

    // Left-click the 🌐 button → open the context menu above it.
    private void BtnLang_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = PlacementMode.Top;
        btn.ContextMenu.IsOpen = true;
    }

    // Shared handler for both language menu items — language code comes from Tag.
    private void MnuLang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string lang)
            Loc.SetLanguage(lang);
    }

    private static void RestartApp()
    {
        // Environment.ProcessPath is reliable on .NET 6+ (returns K2.App.exe, not dotnet.exe)
        var exe = Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null && System.IO.File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
