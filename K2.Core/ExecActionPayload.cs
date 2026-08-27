using System;
using System.IO;

namespace K2.Core;

/// <summary>
/// Encoding for the "exec" (Open program / file) action value.
///
/// The value stays a plain filesystem path in every normal case — that's what every
/// consumer (icon generation, captions, profile export, numpad firmware binding) has
/// always assumed, and old profiles must keep working untouched. The only extra bit of
/// state is for batch scripts (.bat/.cmd), which K2 runs hidden by default: appending
/// the <see cref="ConsoleSuffix"/> marker means "run it in a visible terminal instead".
/// '|' is not legal in a Windows path, so the marker can never collide with a real one.
///
/// Anything reading the value as a path must go through <see cref="PathOf"/>.
/// </summary>
public static class ExecActionPayload
{
    public const string ConsoleSuffix = "|console";

    /// <summary>Splits a stored value into its path and the "show a terminal" flag.</summary>
    public static (string Path, bool ShowConsole) Split(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.EndsWith(ConsoleSuffix, StringComparison.OrdinalIgnoreCase))
            return (v[..^ConsoleSuffix.Length].TrimEnd(), true);
        return (v, false);
    }

    /// <summary>The bare path, with any marker stripped.</summary>
    public static string PathOf(string? value) => Split(value).Path;

    /// <summary>Builds a stored value; the marker is only added when it means something.</summary>
    public static string Build(string? path, bool showConsole)
    {
        var p = (path ?? "").Trim();
        return showConsole && IsBatch(p) ? p + ConsoleSuffix : p;
    }

    /// <summary>True for the script types Windows runs through cmd.exe.</summary>
    public static bool IsBatch(string? path)
    {
        var ext = Path.GetExtension((path ?? "").Trim());
        return ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }
}
