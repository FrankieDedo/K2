using System.Collections.Generic;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Static remap tables shared between MakaluTabPanel (hotspot tooltips) and
/// MakaluDpiRemapPanel (the button list + category/function dropdowns) —
/// factored out so the two controls (kept separate deliberately, see
/// MakaluDpiRemapPanel.xaml) don't duplicate this data. Mirrors panel.py's
/// _REMAP_CATEGORIES/_FN_LANG_KEYS from BaseCampLinux.
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

    public static readonly Dictionary<string, string[]> RemapCategories = new()
    {
        ["Mouse"]  = new[] { "left", "right", "middle", "back", "forward", "disabled" },
        ["DPI"]    = new[] { "dpi+", "dpi-", "disabled" },
        ["Scroll"] = new[] { "scroll_up", "scroll_down", "disabled" },
        ["Sniper"] = new[] { "sniper" },
    };

    private static readonly Dictionary<string, string> FnLangKeys = new()
    {
        ["left"] = "makalu_remap_fn_left", ["right"] = "makalu_remap_fn_right",
        ["middle"] = "makalu_remap_fn_middle", ["back"] = "makalu_remap_fn_back",
        ["forward"] = "makalu_remap_fn_forward",
        ["dpi+"] = "makalu_remap_fn_dpi_plus", ["dpi-"] = "makalu_remap_fn_dpi_minus",
        ["scroll_up"] = "makalu_remap_fn_scroll_up", ["scroll_down"] = "makalu_remap_fn_scroll_down",
        ["disabled"] = "makalu_remap_fn_disabled", ["sniper"] = "makalu_remap_fn_sniper",
    };

    private static readonly Dictionary<string, string> CatLangKeys = new()
    {
        ["Mouse"] = "makalu_remap_cat_mouse", ["DPI"] = "makalu_remap_cat_dpi",
        ["Scroll"] = "makalu_remap_cat_scroll", ["Sniper"] = "makalu_remap_cat_sniper",
    };

    public static string FnLabel(string key) => Loc.Get(FnLangKeys.GetValueOrDefault(key, key));
    public static string CatLabel(string key) => Loc.Get(CatLangKeys.GetValueOrDefault(key, key));

    public static string RemapBtnText(string btnLabel, string assignment) =>
        assignment.StartsWith("sniper:")
            ? $"{btnLabel}\n{FnLabel("sniper")} {assignment.Split(':')[1]}"
            : $"{btnLabel}\n{FnLabel(assignment)}";
}
