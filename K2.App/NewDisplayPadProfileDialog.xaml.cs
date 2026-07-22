using System.Windows;

namespace K2.App;

/// <summary>
/// Shown when the user picks the "+ New profile" row in the DisplayPad profile list
/// (<see cref="MainWindow.LstDpProfile_SelectionChanged"/>). Lets the user choose between
/// a plain empty profile (Generic) and one of the built-in app/function-specific profile
/// types (Dedicated — today only "Spotify", see <see cref="MainWindow.DpCreateOrSwitchSpotifyProfile"/>).
/// Replaces the old direct-create behavior and the now-removed "Create Spotify profile"
/// menu entry.
/// </summary>
public partial class NewDisplayPadProfileDialog : Window
{
    /// <summary>Dedicated profile type names offered in <see cref="CbDedicatedType"/>.
    /// Only "Spotify" exists today; add more here as new dedicated profile types appear.</summary>
    private static readonly string[] DedicatedTypes = { "Spotify" };

    public bool IsDedicated { get; private set; }
    public string? DedicatedType { get; private set; }

    public NewDisplayPadProfileDialog()
    {
        InitializeComponent();
        CbDedicatedType.ItemsSource = DedicatedTypes;
        CbDedicatedType.SelectedIndex = 0;
    }

    private void RbKind_Checked(object sender, RoutedEventArgs e)
    {
        if (PnlDedicated is null) return; // fires during InitializeComponent before the panel exists
        PnlDedicated.IsEnabled = RbDedicated.IsChecked == true;
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        IsDedicated = RbDedicated.IsChecked == true;
        DedicatedType = IsDedicated ? CbDedicatedType.SelectedItem as string : null;
        DialogResult = true;
    }
}
