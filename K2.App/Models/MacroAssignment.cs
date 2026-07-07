// Models/MacroAssignment.cs — a single "this macro is bound to this key" entry,
// shown in the macro editor's "Assigned to" section.

namespace K2.App.Models;

public sealed class MacroAssignment
{
    /// <summary>Key label, e.g. "M3" (MacroPad) or "F13" (Everest).</summary>
    public string KeyLabel { get; set; } = "";

    /// <summary>Device + profile, e.g. "MacroPad #1 · Profile 2".</summary>
    public string Subtitle { get; set; } = "";
}
