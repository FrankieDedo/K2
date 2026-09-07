// MainWindow.Language.cs — language switcher (Settings > Language combo)
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    private static readonly (string Code, string Name)[] LanguageOptions =
    {
        ("en", "English"),
        ("it", "Italiano"),
        ("es", "Español"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("pt", "Português"),
        ("pl", "Polski"),
        ("ja", "日本語"),
        ("zh", "中文"),
        ("ko", "한국어"),
    };

    // True only while InitLanguageMenu is populating the combo, so the
    // SelectionChanged handler doesn't treat the initial programmatic
    // selection as a user choice and clobber K2.lang (which reset the
    // saved language to English on every restart).
    private bool _initializingLanguageMenu;

    // Called from the MainWindow constructor, after InitializeComponent.
    private void InitLanguageMenu()
    {
        _initializingLanguageMenu = true;
        try
        {
            CmbAppLanguage.Items.Clear();
            foreach (var (code, name) in LanguageOptions)
                CmbAppLanguage.Items.Add(new ComboBoxItem { Content = name, Tag = code });

            CmbAppLanguage.SelectedIndex = 0;
            for (int i = 0; i < CmbAppLanguage.Items.Count; i++)
            {
                if ((string)((ComboBoxItem)CmbAppLanguage.Items[i]).Tag == Loc.CurrentLang)
                {
                    CmbAppLanguage.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _initializingLanguageMenu = false;
        }

        Loc.RestartRequested += _ => RestartApp();
    }

    private void CmbAppLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingLanguageMenu) return;
        if (CmbAppLanguage.SelectedItem is not ComboBoxItem item) return;
        string code = (string)item.Tag;
        if (code != Loc.CurrentLang)
            Loc.SetLanguage(code);
    }

    private static void RestartApp()
    {
        // Release the single-instance lock BEFORE launching the replacement process:
        // this process hasn't exited yet at this point, so without releasing it here
        // the new process would see the mutex still held and immediately bail out as
        // "already running", leaving no instance running.
        App.ReleaseSingleInstanceLockForRestart();

        // Environment.ProcessPath is reliable on .NET 6+ (returns K2.App.exe, not dotnet.exe)
        var exe = Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null && System.IO.File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
