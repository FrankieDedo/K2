using System.Collections.Generic;
using System.Linq;
using System.Windows;
using K2.Core;

namespace K2.App;

/// <summary>
/// Lets the user pick which profiles to export (checkboxes) and in which format
/// (Base Camp compatible vs K2 lossless). Used by the DisplayPad, MacroPad and
/// Everest tabs (see <see cref="Services.ExportProfileHelper"/>).
/// </summary>
public partial class ExportProfilesDialog : Window
{
    public sealed class Item(int slot, string name, bool isChecked)
    {
        public int Slot { get; } = slot;
        public string Name { get; } = name;
        public bool IsChecked { get; set; } = isChecked;
    }

    public List<Item> Profiles { get; }
    public bool BcCompatible { get; private set; } = true;
    public IEnumerable<Item> SelectedProfiles => Profiles.Where(p => p.IsChecked);

    public ExportProfilesDialog(IReadOnlyList<(int Slot, string Name)> profiles, int? preselectSlot)
    {
        InitializeComponent();
        Profiles = profiles
            .Select(p => new Item(p.Slot, p.Name, preselectSlot is null || p.Slot == preselectSlot))
            .ToList();
        LstProfiles.ItemsSource = Profiles;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!Profiles.Any(p => p.IsChecked))
        {
            MessageBox.Show(Loc.Get("export_select_at_least_one"), Loc.Get("export_profiles_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        BcCompatible = RbBc.IsChecked == true;
        DialogResult = true;
    }
}
