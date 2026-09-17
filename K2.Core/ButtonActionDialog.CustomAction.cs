using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Custom action" (<c>dp_custom</c>) type — a key showing an
/// action the user built in the game studio.
///
/// <para>
/// Deliberately the same shape as <see cref="ButtonActionDialog"/>'s screen-probe partial, and
/// for the same reason: there is no fixed list of values, because the values ARE the user's.
/// The cards are the actions defined on this machine plus one that opens the studio to create
/// another, and everything about an action — its source, its colours, its indicator — lives in
/// K2.App (<c>Services.CustomGameStore</c>) behind <see cref="IActionHost"/>.
/// </para>
///
/// <para>Wire format: <c>"&lt;action-id&gt;|&lt;name&gt;"</c> — see
/// <see cref="CustomActionType.Parse"/>.</para>
/// </summary>
public partial class ButtonActionDialog
{
    /// <summary>Tag of the synthetic "New action…" card. Contains characters no action id can
    /// (ids are <c>a</c> + hex), so it can never collide with a real wire value.</summary>
    private const string CustomActionNewTag = "__custom_action_new__";

    private void BtnCustomActionEdit_Click(object sender, RoutedEventArgs e) =>
        OpenCustomActionEditor(SelectedCustomActionId());

    /// <summary>Opens the studio and, on a save, re-lists the actions and selects the one that
    /// came back. Cancel changes nothing — including the current selection, which matters because
    /// "New action…" is a card like the others and backing out of it must not leave the key bound
    /// to a sentinel.</summary>
    private void OpenCustomActionEditor(string? actionId)
    {
        string? saved = _host?.EditCustomAction(actionId);
        if (saved is null)
        {
            if (SelectedRawTag() == CustomActionNewTag) SelectCustomAction(actionId);
            return;
        }

        // "" comes back from a DELETE: re-list and select nothing, so the key is honestly
        // unconfigured instead of pointing at something that no longer exists.
        string name = saved.Length == 0
            ? ""
            : _host?.ListCustomActions().FirstOrDefault(a => a.Id == saved).Name ?? "";
        PopulateCombo(CustomActionType.Tag, saved.Length == 0 ? "" : $"{saved}|{name}");
        UpdateSubActionCrumb();
        RefreshLivePreview();
    }

    /// <summary>Id of the action the dialog is currently on, or null on the "new" card or on
    /// nothing at all.</summary>
    private string? SelectedCustomActionId()
    {
        string raw = SelectedRawTag();
        if (raw.Length == 0 || raw == CustomActionNewTag) return null;
        return CustomActionType.Parse(raw)?.Id;
    }

    private void SelectCustomAction(string? actionId)
    {
        var match = CbComboValue.Items.OfType<ComboBoxItem>().FirstOrDefault(
            i => (string?)i.Tag is { } t && t != CustomActionNewTag &&
                 CustomActionType.Parse(t)?.Id == actionId);
        CbComboValue.SelectedItem = match;   // null clears it, which is the honest state
    }

    /// <summary>The saved value: the selected action's wire string, or "" on the "new" card or on
    /// nothing — an unconfigured key stores nothing rather than a sentinel that would later read
    /// as a deleted action.</summary>
    private string SaveCustomActionSpec()
    {
        string raw = SelectedRawTag();
        return raw == CustomActionNewTag ? "" : raw;
    }
}
