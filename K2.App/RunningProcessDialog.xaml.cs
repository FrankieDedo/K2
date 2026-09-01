using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.App;

/// <summary>
/// Small picker listing currently-running applications that own a visible main window,
/// opened from <see cref="ProfileSettingsDialog"/>'s "from a running app" button as an
/// alternative to browsing for an .exe by hand. Returns the selected process's full
/// executable path when readable (a 32-bit host cannot read a 64-bit process's module),
/// falling back to "<ProcessName>.exe" — either form is enough for
/// <see cref="K2.Core.Services.ProfileLaunchWatcher"/>, which matches on the file name
/// without extension.
/// </summary>
public partial class RunningProcessDialog : Window
{
    public sealed record ProcRow(string Title, string ExeName, string PathOrName, ImageSource? Icon);

    /// <summary>Chosen executable path (or "<name>.exe"), valid only when ShowDialog()==true.</summary>
    public string SelectedPath { get; private set; } = "";

    public RunningProcessDialog()
    {
        InitializeComponent();
        LoadProcesses();
    }

    private void LoadProcesses()
    {
        var rows = new List<ProcRow>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { all = Array.Empty<Process>(); }

        foreach (var p in all)
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                string title = p.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;

                string pathOrName;
                try { pathOrName = p.MainModule?.FileName ?? p.ProcessName + ".exe"; }
                catch { pathOrName = p.ProcessName + ".exe"; }

                rows.Add(new ProcRow(title, p.ProcessName + ".exe", pathOrName, TryGetIcon(pathOrName)));
            }
            catch { /* process exited mid-enumeration, or access denied */ }
            finally { p.Dispose(); }
        }

        LstProcesses.ItemsSource = rows
            .GroupBy(r => r.ExeName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Small (typically 32×32) shell icon for an executable, as a frozen
    /// ImageSource. Best-effort: returns null for a bare "name.exe" with no readable
    /// path, or if extraction throws.</summary>
    private static ImageSource? TryGetIcon(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path))
                return null;
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico is null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadProcesses();

    private void LstProcesses_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstProcesses.SelectedItem is ProcRow) Commit();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (LstProcesses.SelectedItem is not ProcRow row) return;
        SelectedPath = row.PathOrName;
        DialogResult = true;
    }
}
