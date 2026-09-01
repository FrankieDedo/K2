// MainWindow.Guide.cs — the per-section "Guide" button (bottom-right of every
// device panel, see the K2GuideButton style + BtnGuide instances in
// MainWindow.xaml).
//
// One handler for all five device panels: it works out which device panel is
// visible and which sidebar section RadioButton is checked, then opens
// K2.Core's GuideWindow with the "device:section" key. Content + window live in
// K2.Core (Guides\guide.<lang>.md, GuideContent, GuideWindow) so the action
// picker's own Guide button can reuse them.

using System.Windows;
using System.Windows.Controls;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    private void BtnGuide_Click(object sender, RoutedEventArgs e)
    {
        var (device, label) = CurrentGuideDevice();
        if (device is null) return;

        string section = CurrentGuideSection(device);

        var win = new GuideWindow(device + ":" + section, label) { Owner = this };
        win.ShowDialog();
    }

    /// <summary>Home tab: the same button relabelled "Highlights" — opens the
    /// "what K2 does that Base Camp doesn't" guide (generic panels + key
    /// mappings/display keys + per-device notes).</summary>
    private void BtnHighlights_Click(object sender, RoutedEventArgs e)
    {
        string[] keys = { "highlights", "highlights:displaypad" };
        new GuideWindow(keys, Loc.Get("highlights_button")) { Owner = this }.ShowDialog();
    }

    /// <summary>The visible device panel → (guide device id, fallback heading label).</summary>
    private (string? device, string label) CurrentGuideDevice()
    {
        if (PnlEverest.Visibility   == Visibility.Visible) return ("everest",    Loc.Get("tab_everest"));
        if (PnlEverest60.Visibility == Visibility.Visible) return ("everest60",  Loc.Get("tab_everest60"));
        if (PnlMakalu.Visibility    == Visibility.Visible) return ("makalu",     Loc.Get("tab_makalu"));
        if (PnlMacroPad.Visibility  == Visibility.Visible) return ("macropad",   Loc.Get("tab_macropad"));
        if (PnlDisplayPad.Visibility == Visibility.Visible) return ("displaypad", "DisplayPad");
        return (null, "");
    }

    /// <summary>Which sidebar section RadioButton is checked for the given device.</summary>
    private string CurrentGuideSection(string device) => device switch
    {
        "everest" =>
            RbSecRgb.IsChecked        == true ? "lighting"   :
            RbSecDial.IsChecked       == true ? "dial"       :
            RbSecAppearance.IsChecked == true ? "appearance" :
            RbSecSettings.IsChecked   == true ? "settings"   :
            RbSecUsb.IsChecked        == true ? "usb"        : "keybinding",

        "everest60" =>
            RbEv60SecLighting.IsChecked   == true ? "lighting"   :
            RbEv60SecAppearance.IsChecked == true ? "appearance" :
            RbEv60SecSettings.IsChecked   == true ? "settings"   : "keybinding",

        "makalu" =>
            RbMkSecRgb.IsChecked      == true ? "lighting" :
            RbMkSecSettings.IsChecked == true ? "settings" : "keybinding",

        "macropad" =>
            RbMpSecLed.IsChecked        == true ? "lighting"   :
            RbMpSecAppearance.IsChecked == true ? "appearance" :
            RbMpSecSettings.IsChecked   == true ? "settings"   : "keybinding",

        "displaypad" =>
            RbDpSecPages.IsChecked    == true ? "pages"    :
            RbDpSecSettings.IsChecked == true ? "settings" : "keybinding",

        _ => "keybinding",
    };

    // ── Profiles guide ──────────────────────────────────────────────────
    // Appended (after a separator) to every device's two profile menus — the
    // per-row right-click menu and the header "…" menu. DisplayPad also gets
    // the "profiles-dp" block (dedicated profiles); other devices show only
    // the generic "profiles" block.

    /// <summary>Adds a trailing separator + "Guide" item to a profile
    /// <see cref="ContextMenu"/> and returns it, so call sites can wrap the
    /// existing builders inline.</summary>
    private ContextMenu WithProfileGuide(ContextMenu menu, string device)
    {
        menu.Items.Add(new Separator());
        var mi = new MenuItem { Header = Loc.Get("guide_button") };
        mi.Click += (_, _) => OpenProfilesGuide(device);
        menu.Items.Add(mi);
        return menu;
    }

    private void OpenProfilesGuide(string device)
    {
        string[] keys = device == "displaypad"
            ? new[] { "profiles", "profiles-dp" }
            : new[] { "profiles" };
        new GuideWindow(keys, Loc.Get("profile")) { Owner = this }.ShowDialog();
    }
}
