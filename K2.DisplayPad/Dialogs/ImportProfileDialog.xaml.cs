using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;

namespace K2.DisplayPad.Dialogs;

public partial class ImportProfileDialog : Window
{
    public int  SelectedSlot { get; private set; } = 1;
    public bool SwitchAfterImport { get; private set; } = true;

    public ImportProfileDialog(string xmlPath, int defaultSlot, int profileCount)
    {
        InitializeComponent();

        LblFile.Text = $"File: {xmlPath}";
        try
        {
            var doc = XDocument.Load(xmlPath);
            var r = doc.Root;
            var name = r?.Element("ProfileName")?.Value ?? "?";
            var id   = r?.Element("Id")?.Value ?? "?";
            var did  = r?.Element("DeviceId")?.Value ?? "?";
            LblName.Text   = $"Profilo: \"{name}\"  (Id={id})";
            LblDevice.Text = $"Device originario: {did}";
        }
        catch (System.Exception ex)
        {
            LblName.Text = $"Errore lettura XML: {ex.Message}";
        }

        CbSlot.ItemsSource = Enumerable.Range(1, profileCount).ToArray();
        CbSlot.SelectedItem = defaultSlot >= 1 && defaultSlot <= profileCount ? defaultSlot : 1;
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (CbSlot.SelectedItem is int s) SelectedSlot = s;
        SwitchAfterImport = ChkSwitchTo.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
