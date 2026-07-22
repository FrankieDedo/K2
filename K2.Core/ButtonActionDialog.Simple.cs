using System.Linq;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the shared "System command" / "Media key" / "Mouse action"
/// combo-box panel. Each type has a small fixed set of values, so a picker replaces the old
/// free-text box; the emitted <c>ActionValue</c> strings are unchanged (still exactly what
/// <see cref="ActionExecutor.RunOsCommand"/>/<see cref="ActionExecutor.SendMediaKey"/>/
/// <see cref="ActionExecutor.DoMouse"/> already recognize), so execution is untouched.
/// </summary>
public partial class ButtonActionDialog
{
    private readonly record struct ComboOption(string Value, string LocKey);

    private static readonly ComboOption[] OsCmdOptions =
    {
        new("Task Manager", "oscmd_taskmgr"),
        new("Calculator",   "oscmd_calc"),
        new("Explorer",     "oscmd_explorer"),
        new("Lock",         "oscmd_lock"),
        new("Shutdown",     "oscmd_shutdown"),
        new("Restart",      "oscmd_restart"),
        new("Sleep",        "oscmd_sleep"),
        new("Hibernate",    "oscmd_hibernate"),
    };

    private static readonly ComboOption[] MediaOptions =
    {
        new("Play/Pause",      "media_play_pause"),
        new("Stop",            "media_stop"),
        new("Previous track",  "media_prev"),
        new("Next track",      "media_next"),
        new("Volume Up",       "media_vol_up"),
        new("Volume Down",     "media_vol_down"),
        new("Mute",            "media_mute"),
        new("Shuffle",         "media_shuffle"),
    };

    private static readonly ComboOption[] MouseOptions =
    {
        new("Left Button",   "mouse_left"),
        new("Right Button",  "mouse_right"),
        new("Middle Button", "mouse_middle"),
        new("Forward",       "mouse_forward"),
        new("Backward",      "mouse_backward"),
        new("Scroll Up",     "mouse_scroll_up"),
        new("Scroll Down",   "mouse_scroll_down"),
        new("Scroll Left",   "mouse_scroll_left"),
        new("Scroll Right",  "mouse_scroll_right"),
    };

    private static ComboOption[] OptionsFor(string tag) => tag switch
    {
        "oscmd" => OsCmdOptions,
        "media" => MediaOptions,
        "mouse" => MouseOptions,
        _       => System.Array.Empty<ComboOption>(),
    };

    private static string LabelKeyFor(string tag) => tag switch
    {
        "oscmd" => "act_oscmd",
        "media" => "act_media",
        "mouse" => "act_mouse",
        "macro" => "act_macro",
        _       => "dlg_value",
    };

    private string? _comboPanelTag;

    /// <summary>Repopulates the combo only when the type actually changed (keeps the current selection on unrelated UpdatePanels refreshes).</summary>
    private void EnsureComboPanel(string tag)
    {
        if (_comboPanelTag == tag) return;
        _comboPanelTag = tag;
        LblComboPanel.Text = Loc.Get(LabelKeyFor(tag));
        PopulateCombo(tag, null);
    }

    private void LoadComboSpec(string tag, string currentValue)
    {
        _comboPanelTag = tag;
        LblComboPanel.Text = Loc.Get(LabelKeyFor(tag));
        PopulateCombo(tag, currentValue);
    }

    private void PopulateCombo(string tag, string? selectValue)
    {
        CbComboValue.Items.Clear();

        if (tag == "macro")
        {
            // Dynamic list (not a fixed enum like oscmd/media/mouse): the macro library
            // is owned by the host app (K2.App), so K2.Core asks it via IActionHost rather
            // than referencing MacroStore directly. Names are shown as-is (no Loc lookup).
            foreach (var name in _host?.ListMacroNames() ?? System.Array.Empty<string>())
                CbComboValue.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }
        else
        {
            foreach (var opt in OptionsFor(tag))
                CbComboValue.Items.Add(new ComboBoxItem { Content = Loc.Get(opt.LocKey), Tag = opt.Value });
        }

        // An unresolved imported macro reference ("***Name", see ActionTypeHelper.
        // UnresolvedMacroPrefix) matches by its preserved original name: if the user has
        // created a same-named macro since the import, opening the dialog pre-selects it,
        // and saving resolves the reference for good.
        if (tag == "macro")
            selectValue = ActionTypeHelper.StripUnresolvedMacroPrefix(selectValue);

        var match = CbComboValue.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, selectValue, System.StringComparison.OrdinalIgnoreCase));
        // For "macro" (dynamic library list), no match is a real, expected state — an
        // imported Base Camp named-macro reference that didn't resolve to any macro in the
        // user's K2 library (see BaseCampDbImporter.TranslateDefaultAction). Defaulting to
        // the first macro in the list here would silently bind the key to an unrelated macro
        // the moment the user opens and saves the dialog without noticing. Fixed enums
        // (oscmd/media/mouse) keep the old fallback since a mismatch there shouldn't happen.
        CbComboValue.SelectedItem = match ?? (tag == "macro" ? null : (CbComboValue.Items.Count > 0 ? CbComboValue.Items[0] : null));
    }

    private string SaveComboSpec()
        => CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";
}
