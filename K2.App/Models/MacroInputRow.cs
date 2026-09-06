// Models/MacroInputRow.cs — display row for a single recorded macro input.
// Built on demand from MacroDefinition.Inputs; SourceIndex keeps the link
// back to the underlying list for reorder/delete.

using System.Windows;

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

    /// <summary>True for keydown/keyup rows — the only ones whose key can be
    /// re-recorded in place (the "capture key" row button is hidden otherwise).</summary>
    public bool IsKeyboard { get; set; }

    /// <summary>True while this row is waiting for the user to press the
    /// replacement key.</summary>
    public bool IsCapturing { get; set; }

    public Visibility KeyEditVisibility => IsKeyboard ? Visibility.Visible : Visibility.Collapsed;
}
