using System.Windows;

namespace K2.Core.Themes;

/// <summary>
/// Code-behind for the shared theme. Contains ONLY the logic for the three
/// custom title bar buttons (minimize / maximize-restore / close): the
/// window template lives in <c>K2Theme.xaml</c> and its <c>Click</c>
/// handlers point here.
/// <para/>
/// The dictionary is instantiated once per app that merges it (see each
/// module's App.xaml). <see cref="System.Windows.SystemCommands"/> applies
/// system commands respecting snap/Aero.
/// </summary>
public partial class K2Theme : ResourceDictionary
{
    public K2Theme()
    {
        InitializeComponent();
    }

    private static Window? HostWindow(object sender) =>
        sender is DependencyObject d ? Window.GetWindow(d) : null;

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (HostWindow(sender) is { } w)
            SystemCommands.MinimizeWindow(w);
    }

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (HostWindow(sender) is not { } w) return;
        if (w.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(w);
        else
            SystemCommands.MaximizeWindow(w);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (HostWindow(sender) is { } w)
            SystemCommands.CloseWindow(w);
    }
}
