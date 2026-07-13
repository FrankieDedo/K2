using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.App.Models;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: Home tab — a grid of cards, one per currently connected
/// device (mirrors Base Camp's own device-picker screen), each linking to its
/// top-level tab. Rebuilt from scratch on every call (cheap: a handful of items)
/// rather than diffed, whenever ANY device's connection state — or, for Everest
/// Max/60, its attached accessories — changes. See call sites: SetDeviceTabVisible
/// (MainWindow.xaml.cs), DpRefreshDevices (MainWindow.DisplayPad.cs),
/// UpdateKeyboardLayout (MainWindow.Layout.cs), ApplyEv60NumpadPosition
/// (MainWindow.Everest60.cs).
/// </summary>
public partial class MainWindow
{
    private readonly System.Collections.ObjectModel.ObservableCollection<HomeDeviceTile> _homeTiles = new();

    /// <summary>Rebuilds the Home tab's tile grid from the current tab visibility/
    /// header state — same fixed order as the tab strip (Everest Max > Everest 60 >
    /// Makalu > DisplayPad > MacroPad). Only tabs that are actually Visible (i.e.
    /// connected — see SetDeviceTabVisible) get a tile.</summary>
    private void RefreshHomeTiles()
    {
        _homeTiles.Clear();

        if (TabEverest.Visibility == Visibility.Visible)
            _homeTiles.Add(new HomeDeviceTile(
                TabEverest.Header as string ?? Loc.Get("tab_everest"),
                HomeImage(EvHomeImageFile()), TabEverest));

        if (TabEverest60.Visibility == Visibility.Visible)
            _homeTiles.Add(new HomeDeviceTile(
                TabEverest60.Header as string ?? Loc.Get("tab_everest60"),
                HomeImage(Ev60HomeImageFile()), TabEverest60));

        if (TabMakalu.Visibility == Visibility.Visible)
            _homeTiles.Add(new HomeDeviceTile(
                TabMakalu.Header as string ?? Loc.Get("tab_makalu"),
                HomeImage(MkHomeImageFile()), TabMakalu));

        // DisplayPad: one tile per connected unit (its tabs are added/removed
        // outright by DpRefreshDevices, not toggled via SetDeviceTabVisible).
        foreach (var dpTab in TcDevices.Items.OfType<TabItem>()
                     .Where(t => (t.Tag as string)?.StartsWith("dp_") == true))
            _homeTiles.Add(new HomeDeviceTile(
                dpTab.Header as string ?? Loc.Get("tab_displaypad"),
                HomeImage("displaypad.png"), dpTab));

        if (TabMacroPad.Visibility == Visibility.Visible)
            _homeTiles.Add(new HomeDeviceTile(
                TabMacroPad.Header as string ?? Loc.Get("tab_macropad"),
                HomeImage("macropad.png"), TabMacroPad));

        PnlHomeEmpty.Visibility  = _homeTiles.Count == 0 ? Visibility.Visible   : Visibility.Collapsed;
        ScrHomeTiles.Visibility  = _homeTiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string HomeImage(string file) => $"pack://application:,,,/Assets/Home/{file}";

    /// <summary>See the user's naming rules: both accessories -> Max artwork, one
    /// of the two -> that accessory's artwork, neither -> the bare keyboard.
    /// Reads the cached _evDockConnected/_evNumpadConnected (MainWindow.Layout.cs)
    /// rather than re-querying the SDK — see that field's doc comment for why.</summary>
    private string EvHomeImageFile() => (_evDockConnected, _evNumpadConnected) switch
    {
        (true, true)   => "everest_max.png",
        (true, false)  => "everest_mediadock.png",
        (false, true)  => "everest_numpad.png",
        (false, false) => "everest.png",
    };

    /// <summary>No numpad -> bare board; numpad attached -> side-specific artwork
    /// (no generic "numpad attached, side unknown" case: _ev60NumpadPosition is
    /// only ever None/Left/Right).</summary>
    private string Ev60HomeImageFile() => _ev60NumpadPosition switch
    {
        Ev60NumpadPosition.Left  => "everest60_left.png",
        Ev60NumpadPosition.Right => "everest60_right.png",
        _                        => "everest60.png",
    };

    private string MkHomeImageFile() =>
        _mkInfo.Model == MakaluService.Model.MakaluMax ? "makalu_max.png" : "makalu67.png";

    /// <summary>Click anywhere on a tile — the whole card is a Button (see its
    /// ControlTemplate in MainWindow.xaml) — jumps to the matching device tab.</summary>
    private void HomeTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HomeDeviceTile tile })
            TcDevices.SelectedItem = tile.Target;
    }
}
