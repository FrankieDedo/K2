using System;

namespace K2.Core;

/// <summary>
/// Shared helper for classifying an action's <c>ActionType</c> string — there's no central
/// enum, every device (DisplayPad, MacroPad, Everest, Everest60) just stores/reads raw
/// strings set by <see cref="ButtonActionDialog"/> or produced by import.
/// </summary>
public static class ActionTypeHelper
{
    /// <summary>
    /// True for a "bc:XYZ" action type — Base Camp's own function type preserved verbatim by
    /// import because K2 has no native equivalent for it (see BaseCampDbImporter.TranslateAction's
    /// default arm). Excludes "bc:Default", Base Camp's own "no binding" placeholder, which store
    /// loaders already filter out before it reaches any display code.
    /// </summary>
    public static bool IsUnrecognized(string? actionType) =>
        actionType is not null
        && actionType.StartsWith("bc:", StringComparison.Ordinal)
        && !string.Equals(actionType, "bc:Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a "macro" (Play Macro) action with no macro assigned — the state left by
    /// importing a Base Camp named-macro reference that didn't match any macro in the user's
    /// K2 library by name (see BaseCampDbImporter.TranslateDefaultAction). Used to show the
    /// same "action not found" warning as an unrecognized "bc:" action type.
    /// </summary>
    public static bool IsMacroMissingTarget(string? actionType, string? actionValue) =>
        string.Equals(actionType, "macro", StringComparison.Ordinal)
        && string.IsNullOrEmpty(actionValue);

    /// <summary>Display text for a "macro" action: the assigned macro's name, or a visible
    /// "unassigned" warning when <see cref="IsMacroMissingTarget"/> — used by every device's
    /// key-list Display/ActionSummary so an unresolved imported macro reference doesn't just
    /// show up as the raw "macro" action-type string with no indication anything is wrong.</summary>
    public static string MacroSummary(string? actionValue) =>
        string.IsNullOrEmpty(actionValue) ? Loc.Get("act_macro_unresolved") : $"{Loc.Get("act_macro")}: {actionValue}";
}
