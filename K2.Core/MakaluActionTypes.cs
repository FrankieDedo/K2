using System;
using System.Linq;

namespace K2.Core;

/// <summary>
/// The Makalu mouse's FIRMWARE button functions, as action types of the shared picker
/// (<see cref="ButtonActionDialog"/>, category "makalu"). They are not K2 actions: nothing
/// here is ever executed by <see cref="ButtonActionEngine"/> — the mouse stores the function
/// in its own memory and performs it by itself. The types exist so a mouse button is
/// configured in the same dialog as every other key; the Makalu module translates the
/// picked type + value to and from its own assignment string
/// (K2.App.Services.MakaluRemapData). Offered only by a host with
/// <see cref="IActionHost.SupportsMouseFirmwareActions"/>.
/// </summary>
public static class MakaluActionTypes
{
    public const string Mouse    = "mk_mouse";
    public const string Dpi      = "mk_dpi";
    public const string Scroll   = "mk_scroll";
    public const string Sniper   = "mk_sniper";
    public const string Profile  = "mk_profile";
    public const string Lighting = "mk_lighting";
    public const string Disable  = "mk_disable";

    /// <summary>Types whose value is one firmware function picked from a fixed list — the
    /// function names are the ones the Makalu protocol already speaks ("left", "dpi+", ...).</summary>
    public static readonly (string Tag, string[] Functions)[] Groups =
    {
        (Mouse,    new[] { "left", "right", "middle", "back", "forward" }),
        (Dpi,      new[] { "dpi+", "dpi-" }),
        (Scroll,   new[] { "scroll_up", "scroll_down" }),
        (Profile,  new[] { "profile_next", "profile_prev" }),
        (Lighting, new[] { "brightness_cycle", "effect_cycle" }),
    };

    /// <summary>Every type, in the order the picker shows them. <see cref="Sniper"/> carries a
    /// typed DPI value, <see cref="Disable"/> no value at all.</summary>
    public static readonly string[] All = { Mouse, Dpi, Scroll, Sniper, Profile, Lighting, Disable };

    public static bool IsCombo([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? tag) => tag is not null && Groups.Any(g => g.Tag == tag);

    public static string[] FunctionsOf(string tag) =>
        Groups.FirstOrDefault(g => g.Tag == tag).Functions ?? Array.Empty<string>();

    /// <summary>The group a firmware function belongs to, or null for a name outside them.</summary>
    public static string? GroupOf(string function) =>
        Groups.FirstOrDefault(g => g.Functions.Contains(function)).Tag;

    public static string FunctionLocKey(string function) =>
        "makalu_remap_fn_" + function.Replace("+", "_plus").Replace("-", "_minus");
}
