using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: "Open program / file" (exec) and "Open folder" (folder)
/// panels — path entry + Browse… + (exec only) icon preview + recent-paths list.
/// </summary>
public partial class ButtonActionDialog
{
    // ---- Open program / file ------------------------------------------

    private void RefreshExecPanel()
    {
        RefreshExecIcon();
        LstExecRecent.ItemsSource = AppSettings.RecentExecPaths;
    }

    private void RefreshExecIcon()
    {
        ImgExecIcon.Source = TryLoadIcon(TxtExecPath.Text);
    }

    private static BitmapSource? TryLoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void TxtExecPath_TextChanged(object sender, TextChangedEventArgs e) => RefreshExecIcon();

    private void BtnExecBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.Get("exec_path_label"),
            Filter = "Executable / all files (*.exe;*.*)|*.exe;*.*"
        };
        try
        {
            var dir = Path.GetDirectoryName(TxtExecPath.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch { /* invalid path: ignore */ }

        if (dlg.ShowDialog(this) == true)
            TxtExecPath.Text = dlg.FileName;
    }

    private void LstExecRecent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstExecRecent.SelectedItem is string path)
            TxtExecPath.Text = path;
    }

    // ---- Open folder ---------------------------------------------------

    private void RefreshFolderPanel()
    {
        LstFolderRecent.ItemsSource = AppSettings.RecentFolderPaths;
    }

    private void BtnFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.Get("folder_path_label") };
        try
        {
            if (Directory.Exists(TxtFolderPath.Text))
                dlg.InitialDirectory = TxtFolderPath.Text;
        }
        catch { /* invalid path: ignore */ }

        if (dlg.ShowDialog(this) == true)
            TxtFolderPath.Text = dlg.FolderName;
    }

    private void LstFolderRecent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstFolderRecent.SelectedItem is string path)
            TxtFolderPath.Text = path;
    }
}
