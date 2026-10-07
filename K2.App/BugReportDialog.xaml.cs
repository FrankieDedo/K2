using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>Collects a description + logs and mails them to the maintainer through
/// <see cref="BugReportService"/>. The attached files are listed up front and nothing
/// is sent until the user presses Send.</summary>
public partial class BugReportDialog : Window
{
    private readonly ObservableCollection<BugReportFile> _files = new();

    public BugReportDialog()
    {
        InitializeComponent();
        RefreshFiles();
        if (!BugReportService.IsConfigured)
        {
            TxtStatus.Text = Loc.Get("bugreport_not_configured");
            BtnSend.IsEnabled = false;
        }
    }

    private void RefreshFiles()
    {
        _files.Clear();
        foreach (var f in BugReportService.CollectFiles()) _files.Add(f);
        LstFiles.ItemsSource = _files;
        TxtNoFiles.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtSysInfo.Text = BugReportService.BuildSystemInfo().TrimEnd();
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Loc.Get("bugreport_add_files") };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var path in dlg.FileNames)
        {
            if (_files.Any(f => f.Path == path)) continue;
            var fi = new System.IO.FileInfo(path);
            // "extra/" keeps a user file from colliding with a log of the same name.
            _files.Add(new BugReportFile { Path = path, EntryName = fi.Name, ZipPath = "extra/" + fi.Name,
                                           Size = fi.Length, IsSelected = true });
        }
        TxtNoFiles.Visibility = Visibility.Collapsed;
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        string desc = TxtDescription.Text.Trim();
        if (desc.Length == 0)
        {
            TxtStatus.Text = Loc.Get("bugreport_need_description");
            return;
        }

        BtnSend.IsEnabled = false;
        TxtStatus.Text = Loc.Get("bugreport_sending");

        string contact = TxtContact.Text.Trim();
        var files = _files.Where(f => f.IsSelected).ToList();
        if (CkProfiles.IsChecked == true) files.AddRange(BugReportService.CollectProfileFiles());
        var zip = await System.Threading.Tasks.Task.Run(() => BugReportService.BuildZip(desc, contact, files));
        if (zip == null)
        {
            TxtStatus.Text = Loc.Get("bugreport_too_big");
            BtnSend.IsEnabled = true;
            return;
        }

        var (ok, error) = await BugReportService.SendAsync(desc, contact, zip);
        if (ok)
        {
            MessageBox.Show(this, Loc.Get("bugreport_sent"), Loc.Get("bugreport_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        else
        {
            App.WriteLog($"[BugReport] send failed: {error}");
            TxtStatus.Text = Loc.Get("bugreport_failed", error ?? "?");
            BtnSend.IsEnabled = true;
        }
    }
}
