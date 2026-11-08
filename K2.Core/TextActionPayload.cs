namespace K2.Core;

/// <summary>
/// Value of a "text" (Paste text) action: the text itself, optionally led by
/// <see cref="TypedMarker"/> when the user chose to have it TYPED (as keystrokes, all in one
/// go) instead of pasted through the clipboard. A plain value — every text action saved before
/// the option existed — is the pasted mode, so old profiles keep working unchanged.
/// Read the value only through <see cref="Parse"/>: the marker is not part of the text.
/// </summary>
public static class TextActionPayload
{
    public const string TypedMarker = "[[k2:type]]";

    public static (string Text, bool Typed) Parse(string? value)
    {
        string v = value ?? "";
        return v.StartsWith(TypedMarker, System.StringComparison.Ordinal)
            ? (v[TypedMarker.Length..], true)
            : (v, false);
    }

    public static string TextOf(string? value) => Parse(value).Text;

    public static string Build(string text, bool typed) => typed ? TypedMarker + text : text;
}
