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
    /// Marker prefixed to a "macro" action's value by import when the referenced Base Camp
    /// macro name didn't match any macro in the user's K2 library — the original name is
    /// kept right after the marker (e.g. "***Volume ramp") so the UI can still tell the
    /// user WHICH macro the key was pointing at instead of discarding the reference.
    /// A marked value is never played (<see cref="ButtonActionEngine"/> skips it) and is
    /// shown with a yellow warning triangle rather than the red "action not found" one.
    /// </summary>
    public const string UnresolvedMacroPrefix = "***";

    /// <summary>
    /// True for a "macro" (Play Macro) action with no playable macro assigned — either no
    /// value at all, or an unresolved imported reference (value carrying the
    /// <see cref="UnresolvedMacroPrefix"/> marker; see BaseCampDbImporter.
    /// TranslateDefaultAction). Used to show a warning instead of silently doing nothing.
    /// </summary>
    public static bool IsMacroMissingTarget(string? actionType, string? actionValue) =>
        string.Equals(actionType, "macro", StringComparison.Ordinal)
        && (string.IsNullOrEmpty(actionValue) || IsUnresolvedMacroValue(actionValue));

    /// <summary>True when <paramref name="actionValue"/> carries the
    /// <see cref="UnresolvedMacroPrefix"/> marker (unresolved imported macro reference,
    /// original Base Camp name preserved after the marker).</summary>
    public static bool IsUnresolvedMacroValue(string? actionValue) =>
        actionValue is not null
        && actionValue.StartsWith(UnresolvedMacroPrefix, StringComparison.Ordinal);

    /// <summary>The original Base Camp macro name behind an
    /// <see cref="UnresolvedMacroPrefix"/>-marked value — or the value unchanged when it
    /// carries no marker.</summary>
    public static string? StripUnresolvedMacroPrefix(string? actionValue) =>
        IsUnresolvedMacroValue(actionValue)
            ? actionValue![UnresolvedMacroPrefix.Length..]
            : actionValue;

    /// <summary>Display text for a "macro" action: the assigned macro's name, or a visible
    /// "unassigned" warning when <see cref="IsMacroMissingTarget"/> (with the original Base
    /// Camp name appended when the marker preserved it) — used by every device's key-list
    /// Display/ActionSummary so an unresolved imported macro reference doesn't just show up
    /// as the raw "macro" action-type string with no indication anything is wrong.</summary>
    public static string MacroSummary(string? actionValue) =>
        string.IsNullOrEmpty(actionValue) ? Loc.Get("act_macro_unresolved")
        : IsUnresolvedMacroValue(actionValue) ? $"{Loc.Get("act_macro_unresolved")}: {StripUnresolvedMacroPrefix(actionValue)}"
        : $"{Loc.Get("act_macro")}: {actionValue}";

    /// <summary>Short, human-readable summary for the key-list "assigned action" row —
    /// every <c>ActionType</c> must resolve to something meaningful here, never the raw
    /// internal tag on its own (e.g. "oscmd", "exec"; user report 2026-07-19). Shared by
    /// DisplayPadKey/EverestKey/MacroPadKey's list-display property so every device's key
    /// list explains what a key actually does. Callers handle their own device-specific
    /// types (DisplayPad's "dp_folder"/"dp_back" page navigation) before falling back here.</summary>
    public static string Summary(string? actionType, string? actionValue)
    {
        string val = actionValue ?? "";
        return actionType switch
        {
            "keys"     => val,
            "exec"     => System.IO.Path.GetFileName(val),
            "folder"   => FileOrFolderName(val),
            "url"      => val,
            "browser"  => BrowserSummary(val),
            "profile"  => ProfileSummary(val),
            "oscmd"    => val,
            "media"    => val,
            "mouse"    => val,
            "text"     => val,
            "command"  => val,
            "macro"    => MacroSummary(actionValue),
            "pyscript" => Loc.Get("act_pyscript"),
            _          => IsUnrecognized(actionType) ? Loc.Get("act_unrecognized") : actionType ?? "",
        };
    }

    private static string FileOrFolderName(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string BrowserSummary(string val)
    {
        var payload = BrowserActionPayload.Parse(val);
        if (payload is null) return val; // legacy plain URL string (or empty = launch default browser)

        string browserLabel = payload.Browser switch
        {
            "chrome"  => "Chrome",
            "edge"    => "Edge",
            "firefox" => "Firefox",
            "opera"   => "Opera",
            "brave"   => "Brave",
            _         => string.IsNullOrEmpty(payload.CustomPath) ? Loc.Get("browser_other") : FileOrFolderName(payload.CustomPath),
        };
        return string.IsNullOrEmpty(payload.Url) ? browserLabel : $"{browserLabel} — {payload.Url}";
    }

    private static string ProfileSummary(string val)
    {
        var payload = ProfileTargetPayload.Parse(val);
        if (payload is null) return val; // legacy plain "Next" | "Previous" | "1".."N"
        return payload.Targets.Count == 0 ? Loc.Get("act_profile") : string.Join(", ", payload.Targets.ConvertAll(t => t.Target));
    }

    /// <summary>Normalizes Base Camp's "OS Commands" SubFunctionType/FunctionValue (e.g.
    /// "Run task manager", "Lock computer" — confirmed verbatim against a real BC XML export,
    /// <c>Profili_BaseCamp/test/test1.xml</c>) to K2's own oscmd vocabulary
    /// (<c>ButtonActionDialog.Simple.OsCmdOptions</c>'s <c>Value</c>s: "Task Manager", "Lock",
    /// ...). Without this, importing e.g. "Lock computer" left the raw BC string as
    /// <c>ActionValue</c> — <see cref="ActionExecutor.RunOsCommand"/> still ran it fine
    /// (case-insensitive alias match), but opening the key's action dialog afterward found no
    /// matching combo entry and silently fell back to the first item ("Task Manager"), so an
    /// untouched Save would corrupt the binding (user report 2026-07-19). Falls back to the
    /// raw value unchanged for anything not in BC's known list (unrecognized OS command,
    /// preserved rather than dropped).</summary>
    public static string? NormalizeOsCommand(string? bcValue) => bcValue?.Trim().ToLowerInvariant() switch
    {
        "run task manager" or "task manager" or "taskmgr" => "Task Manager",
        "run explorer" or "explorer"                       => "Explorer",
        "calculator" or "calc"                             => "Calculator",
        "lock computer" or "lock"                           => "Lock",
        "shutdown" or "shut down"                           => "Shutdown",
        "restart"                                            => "Restart",
        "sleep"                                              => "Sleep",
        "hibernate"                                          => "Hibernate",
        _                                                    => bcValue,
    };
}
