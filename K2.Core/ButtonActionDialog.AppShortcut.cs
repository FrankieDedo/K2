using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the shared panel for "adobe"/"davinci"/"zoom" — mechanically
/// identical to "keys" (same human-syntax shortcut, same <see cref="SendKeysTranslator"/>
/// execution in <see cref="ButtonActionEngine"/>), just with an app/action picker on top.
/// Every catalog in <see cref="AppShortcutCatalog"/> carries each action's real default
/// keyboard shortcut, so picking one autofills the shortcut editor below (still overridable,
/// "Custom" skips it) — only the resulting shortcut string ends up in <c>ActionValue</c>,
/// exactly like "keys" today; the App/Action combos themselves are not persisted. A handful
/// of actions have no fixed vendor default (see the per-catalog remarks in
/// <see cref="AppShortcutCatalog"/>) and carry an empty shortcut, so picking them leaves the
/// editor untouched, same as "Custom".
/// </summary>
public partial class ButtonActionDialog
{
    private string? _appShortcutTag;
    private bool _appShortcutKeyItemsPopulated;

    private void EnsureAppShortcutPanel(string tag)
    {
        bool isAdobe = tag == "adobe";
        LblAppShortcutApp.Visibility = isAdobe ? Visibility.Visible : Visibility.Collapsed;
        CbAppShortcutApp.Visibility  = isAdobe ? Visibility.Visible : Visibility.Collapsed;

        if (!_appShortcutKeyItemsPopulated)
        {
            _appShortcutKeyItemsPopulated = true;
            PopulateKeyItems(CbAppValue);
        }

        if (_appShortcutTag == tag) return;
        _appShortcutTag = tag;

        if (isAdobe)
        {
            CbAppShortcutApp.Items.Clear();
            foreach (var app in AppShortcutCatalog.AdobeApps) CbAppShortcutApp.Items.Add(app);
            CbAppShortcutApp.SelectedIndex = 0; // triggers CbAppShortcutApp_SelectionChanged -> populates actions
        }
        else
        {
            PopulateAppShortcutActions(tag, "");
        }
    }

    private void CbAppShortcutApp_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => PopulateAppShortcutActions(_appShortcutTag ?? "adobe", CbAppShortcutApp.SelectedItem as string ?? "");

    private void PopulateAppShortcutActions(string tag, string app)
    {
        CbAppShortcutAction.Items.Clear();
        CbAppShortcutAction.Items.Add(new ComboBoxItem { Content = Loc.Get("appshortcut_custom"), Tag = null });

        (string Shortcut, string Label)[] actions = tag switch
        {
            "adobe"   => AppShortcutCatalog.ActionsForAdobeApp(app),
            "davinci" => AppShortcutCatalog.DaVinciActions,
            "zoom"    => AppShortcutCatalog.ZoomShortcuts,
            _         => System.Array.Empty<(string, string)>(),
        };
        foreach (var (shortcut, label) in actions)
            CbAppShortcutAction.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = shortcut.Length > 0 ? shortcut : null,
            });
        CbAppShortcutAction.SelectedIndex = 0;
    }

    private void CbAppShortcutAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Entries with no vendor default (Tag == null — see AppShortcutCatalog remarks) and
        // the "Custom" first item both leave the shortcut editor untouched, since there's
        // nothing to prefill.
        if (CbAppShortcutAction.SelectedItem is ComboBoxItem { Tag: string shortcut })
            ParseShortcut(shortcut, ChkAppCtrl, ChkAppShift, ChkAppAlt, ChkAppWin, CbAppValue);
    }

    private void LoadAppShortcutSpec(string tag, string value)
    {
        EnsureAppShortcutPanel(tag);
        ParseShortcut(value, ChkAppCtrl, ChkAppShift, ChkAppAlt, ChkAppWin, CbAppValue);
    }

    private string SaveAppShortcutSpec() => BuildShortcut(ChkAppCtrl, ChkAppShift, ChkAppAlt, ChkAppWin, CbAppValue);
}
