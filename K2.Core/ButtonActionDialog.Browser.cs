using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: "Open browser" panel — one radio button per detected
/// browser (Chrome/Edge/Firefox/Opera/Brave, only if installed) + an always-present
/// "Other" radio with its own path picker, plus an optional initial URL.
/// </summary>
public partial class ButtonActionDialog
{
    private void EnsureBrowserChoicesPopulated()
    {
        if (PnlBrowserChoices.Children.Count > 0) return;

        foreach (var b in BrowserDetector.DetectInstalled())
        {
            var rb = new RadioButton
            {
                Content = b.Name,
                Tag = b.Id,
                GroupName = "BrowserChoice",
                Margin = new Thickness(0, 2, 0, 2),
            };
            rb.Checked += BrowserChoice_Changed;
            PnlBrowserChoices.Children.Add(rb);
        }

        if (!PnlBrowserChoices.Children.OfType<RadioButton>().Any())
            RbBrowserOther.IsChecked = true;
    }

    private void LoadBrowserSpec(BrowserActionPayload spec)
    {
        EnsureBrowserChoicesPopulated();

        var match = PnlBrowserChoices.Children.OfType<RadioButton>()
            .FirstOrDefault(rb => (string?)rb.Tag == spec.Browser);
        if (match is not null) match.IsChecked = true;
        else RbBrowserOther.IsChecked = true;

        TxtBrowserOtherPath.Text = spec.CustomPath;
        TxtBrowserUrl.Text = spec.Url;
        UpdateBrowserOtherRowEnabled();
    }

    private BrowserActionPayload SaveBrowserSpec()
    {
        var chosen = PnlBrowserChoices.Children.OfType<RadioButton>()
            .FirstOrDefault(rb => rb.IsChecked == true);
        return new BrowserActionPayload
        {
            Browser    = chosen is not null ? (string)chosen.Tag! : "other",
            CustomPath = TxtBrowserOtherPath.Text?.Trim() ?? "",
            Url        = TxtBrowserUrl.Text?.Trim() ?? "",
        };
    }

    private void BrowserChoice_Changed(object sender, RoutedEventArgs e) => UpdateBrowserOtherRowEnabled();

    private void UpdateBrowserOtherRowEnabled()
    {
        bool other = RbBrowserOther.IsChecked == true;
        TxtBrowserOtherPath.IsEnabled = other;
        BtnBrowserOtherBrowse.IsEnabled = other;
    }

    private void BtnBrowserOtherBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.Get("browser_other"),
            Filter = "Executable (*.exe)|*.exe|All files|*.*"
        };
        try
        {
            var dir = Path.GetDirectoryName(TxtBrowserOtherPath.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch { /* invalid path: ignore */ }

        if (dlg.ShowDialog(this) == true)
            TxtBrowserOtherPath.Text = dlg.FileName;
    }
}
