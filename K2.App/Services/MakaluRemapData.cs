using System.Collections.Generic;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Static remap tables shared between MakaluTabPanel (hotspot tooltips) and
/// MakaluDpiRemapPanel (the button list) — factored out so the two controls
/// (kept separate deliberately, see MakaluDpiRemapPanel.xaml) don't duplicate
/// this data. The functions a button can take are listed in K2.Core's
/// MakaluActionTypes, since the shared picker shows them.
/// </summary>
internal static class MakaluRemapData
{
    private static readonly Dictionary<int, string> BtnNameKeys67 = new()
    {
        [1] = "makalu_remap_btn_left", [2] = "makalu_remap_btn_right", [3] = "makalu_remap_btn_middle",
        [4] = "makalu_remap_btn_back", [5] = "makalu_remap_btn_forward", [6] = "makalu_remap_btn_dpi",
    };
    private static readonly Dictionary<int, string> BtnNameKeysMax = new()
    {
        [1] = "makalu_remap_btn_left", [2] = "makalu_remap_btn_right", [3] = "makalu_remap_btn_middle",
        [4] = "makalu_remap_btn_dpi", [5] = "makalu_remap_btn_5", [6] = "makalu_remap_btn_6",
        [7] = "makalu_remap_btn_forward", [8] = "makalu_remap_btn_back",
    };
    private static readonly Dictionary<int, string> RemapDefaults67 = new()
    {
        [1] = "left", [2] = "right", [3] = "middle", [4] = "back", [5] = "forward", [6] = "dpi+",
    };
    private static readonly Dictionary<int, string> RemapDefaultsMax = new()
    {
        [1] = "left", [2] = "right", [3] = "middle", [4] = "dpi+",
        [5] = "disabled", [6] = "disabled", [7] = "forward", [8] = "back",
    };

    public static Dictionary<int, string> BtnNames(MakaluService.Model model) =>
        model == MakaluService.Model.MakaluMax ? BtnNameKeysMax : BtnNameKeys67;

    public static Dictionary<int, string> RemapDefaults(MakaluService.Model model) =>
        model == MakaluService.Model.MakaluMax ? RemapDefaultsMax : RemapDefaults67;

    private static readonly Dictionary<string, string> FnLangKeys = new()
    {
        ["left"] = "makalu_remap_fn_left", ["right"] = "makalu_remap_fn_right",
        ["middle"] = "makalu_remap_fn_middle", ["back"] = "makalu_remap_fn_back",
        ["forward"] = "makalu_remap_fn_forward",
        ["dpi+"] = "makalu_remap_fn_dpi_plus", ["dpi-"] = "makalu_remap_fn_dpi_minus",
        ["scroll_up"] = "makalu_remap_fn_scroll_up", ["scroll_down"] = "makalu_remap_fn_scroll_down",
        ["disabled"] = "makalu_remap_fn_disabled", ["sniper"] = "makalu_remap_fn_sniper",
        ["profile_next"] = "makalu_remap_fn_profile_next", ["profile_prev"] = "makalu_remap_fn_profile_prev",
        ["brightness_cycle"] = "makalu_remap_fn_brightness_cycle", ["effect_cycle"] = "makalu_remap_fn_effect_cycle",
    };

    public static string FnLabel(string key) => Loc.Get(FnLangKeys.GetValueOrDefault(key, key));

    // ---------------------------------------------------------------
    // K2 actions on a button: the same K2Action (type + value) every other device stores,
    // packed into the one assignment string the Remap table already holds —
    // "action:{type}|{value}". The firmware only learns "notify the host"
    // (MakaluProtocol.SetButtonHostAction); the action itself stays here.
    // ---------------------------------------------------------------

    private const string ActionPrefix = "action:";

    public static bool IsAction(string assignment) => assignment.StartsWith(ActionPrefix);

    public static string MakeAction(string type, string? value) => $"{ActionPrefix}{type}|{value}";

    public static bool TryParseAction(string assignment, out string type, out string value)
    {
        type = value = "";
        if (!IsAction(assignment)) return false;
        string body = assignment[ActionPrefix.Length..];
        int sep = body.IndexOf('|');
        if (sep <= 0) return false;
        type = body[..sep];
        value = body[(sep + 1)..];
        return true;
    }

    // ---------------------------------------------------------------
    // Assignment string <-> what the shared picker (ButtonActionDialog) speaks. A firmware
    // function shows up there as one of MakaluActionTypes' types (the "Makalu" category);
    // anything else the picker returns is a K2 action.
    // ---------------------------------------------------------------

    /// <summary>The picker's (type, value) for a stored assignment.</summary>
    public static (string Type, string Value) ToPicker(string assignment)
    {
        if (TryParseAction(assignment, out var type, out var value)) return (type, value);
        if (assignment.StartsWith("sniper:")) return (MakaluActionTypes.Sniper, assignment.Split(':')[1]);
        if (assignment == "disabled") return (MakaluActionTypes.Disable, "");
        return MakaluActionTypes.GroupOf(assignment) is { } group ? (group, assignment) : ("none", "");
    }

    /// <summary>The assignment to store for what the picker returned, or null for "no action"
    /// — which on a mouse means "back to the button's own function".</summary>
    public static string? FromPicker(string? type, string? value, int dpiMin)
    {
        value ??= "";
        if (string.IsNullOrEmpty(type) || type == "none") return null;
        if (type is "disable" or MakaluActionTypes.Disable) return "disabled";
        if (type == MakaluActionTypes.Sniper)
        {
            if (!int.TryParse(value.Trim(), out int dpi)) dpi = dpiMin;
            dpi = System.Math.Clamp(MakaluProtocol.QuantizeDpiTiered(dpi), dpiMin, MakaluProtocol.DpiMax);
            return $"sniper:{dpi}";
        }
        if (MakaluActionTypes.IsCombo(type))
            return System.Array.IndexOf(MakaluActionTypes.FunctionsOf(type), value) >= 0 ? value : null;
        return MakeAction(type, value);
    }

    /// <summary>User-facing text of any assignment string (function, sniper or K2 action).</summary>
    public static string AssignmentLabel(string assignment)
    {
        if (TryParseAction(assignment, out var type, out var value))
            return ActionTypeHelper.Summary(type, value);
        return assignment.StartsWith("sniper:")
            ? $"{FnLabel("sniper")} {assignment.Split(':')[1]}"
            : FnLabel(assignment);
    }

    /// <summary>"Type  —  Action" body of the mapped-buttons list row: the function's category
    /// (or the K2 action's type) in front of <see cref="AssignmentLabel"/>'s text.</summary>
    public static string ListLabel(string assignment)
    {
        if (TryParseAction(assignment, out var type, out var value))
            return ActionTypeHelper.ListSummary(type, value);
        if (assignment.StartsWith("sniper:"))
            return $"{Loc.Get("act_" + MakaluActionTypes.Sniper)}  —  {assignment.Split(':')[1]}";
        // "disabled" belongs to no group: it is its own type.
        return MakaluActionTypes.GroupOf(assignment) is { } group
            ? $"{Loc.Get("act_" + group)}  —  {FnLabel(assignment)}"
            : FnLabel(assignment);
    }

    public static string RemapBtnText(string btnLabel, string assignment) =>
        $"{btnLabel}\n{AssignmentLabel(assignment)}";
}
