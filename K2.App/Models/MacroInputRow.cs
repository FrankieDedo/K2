// Models/MacroInputRow.cs — display row for a single recorded macro input.
// Built on demand from MacroDefinition.Inputs; SourceIndex keeps the link
// back to the underlying list for reorder/delete.

namespace K2.App.Models;

public sealed class MacroInputRow
{
    public int Number { get; set; }
    public string Glyph { get; set; } = "";
    public string Label { get; set; } = "";
    public int DelayMs { get; set; }
    public bool IsPress { get; set; }
    public bool ShowIndicator { get; set; }
    public int SourceIndex { get; set; }
}
