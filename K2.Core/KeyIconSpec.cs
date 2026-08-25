using System;
using System.Text.Json;

namespace K2.Core;

/// <summary>
/// Everything the icon half of a display-key config dialog lets the user choose, in a form
/// that can be persisted next to the key's action and re-opened later (user request
/// 2026-08-24: "tutte le impostazioni devono essere memorizzate e modificabili in un secondo
/// momento"). Before this, the dialog's choices only survived as the *rendered PNG* — the
/// text, colors and font behind it were lost the moment the window closed, so re-editing an
/// icon always started from scratch.
///
/// Two flavours, selected by <see cref="DefaultIcon"/>:
/// - <b>default icon</b> (the checkbox, on by default): the picture is (re)generated from the
///   key's ACTION every time, so it always matches what the key does. Only the caption text,
///   font, background color and text color are user-editable — position/crop are fixed by the
///   generators' own layout (see <c>IconImageGenerator</c>);
/// - <b>custom icon</b>: a user-loaded picture (or a free-form text tile), where the text can
///   also be placed anywhere via <see cref="Anchor"/> and drawn over the image
///   (<see cref="TextOnImage"/>).
/// </summary>
public sealed class KeyIconSpec
{
    /// <summary>"Default icon" checkbox — the key's picture is regenerated from its action.</summary>
    public bool DefaultIcon { get; set; } = true;

    /// <summary>"With text" / "Without text" for a default icon: whether the generators draw
    /// their caption strip at all.</summary>
    public bool ShowText { get; set; } = true;

    /// <summary>Which icon set a default icon prefers when BOTH have art for this action: Base
    /// Camp's own ported gallery (false, the default) or K2's hand-drawn glyph (true). Whichever
    /// side has no matching art for this specific action/value falls back to the other
    /// automatically (see <c>DpKeyConfigDialog.RenderDefaultIcon</c>) — this only breaks the tie
    /// when both do.</summary>
    public bool UseK2Icons { get; set; }

    /// <summary>Caption/text drawn on the icon. Null = whatever the generator picks on its
    /// own (the action's own name) for a default icon, no text for a custom one.</summary>
    public string? Text { get; set; }

    /// <summary>Font family name, null/empty = the built-in default (Segoe UI Semibold).</summary>
    public string? FontFamily { get; set; }

    /// <summary>Font size in pixels; 0 = auto (shrink-to-fit, the historical behaviour).</summary>
    public double FontSize { get; set; }

    /// <summary>Background color as "#RRGGBB", null = the generator's own default.</summary>
    public string? BgColor { get; set; }

    /// <summary>Text color as "#RRGGBB", null = white.</summary>
    public string? TextColor { get; set; }

    /// <summary>Text position for a CUSTOM icon — a <c>TextIconGenerator.TextAnchor</c> name.
    /// Ignored for default icons (their layout is fixed).</summary>
    public string? Anchor { get; set; }

    /// <summary>Custom icon only: draw the text on top of the loaded picture instead of on a
    /// solid background.</summary>
    public bool TextOnImage { get; set; }

    /// <summary>User rotation, 0/90/180/270 (see <c>DpKeyConfigDialog.ApplyUserRotation</c>).</summary>
    public int Rotation { get; set; }

    // -----------------------------------------------------------------
    // Serialization — one JSON blob stored in a single DB column, so adding a field later
    // needs no schema migration.
    // -----------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static KeyIconSpec? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<KeyIconSpec>(json!, JsonOpts); }
        catch { return null; }
    }

    public KeyIconSpec Clone() => (KeyIconSpec)MemberwiseClone();

    /// <summary>Everything that changes what a generated default icon LOOKS like — mixed into
    /// the auto-icon cache key so two style variants of the same action don't collide on the
    /// same cached PNG.</summary>
    public string StyleFingerprint =>
        $"{ShowText}|{Text}|{FontFamily}|{FontSize:0.##}|{BgColor}|{TextColor}|{UseK2Icons}";

    // -----------------------------------------------------------------
    // Color helpers (shared by the GDI+ and WPF renderers)
    // -----------------------------------------------------------------

    public static System.Drawing.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return System.Drawing.ColorTranslator.FromHtml(hex!); }
        catch { return null; }
    }

    public static string ToHex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// Ambient, thread-local style override consulted by the icon renderers
/// (<see cref="IconImageGenerator"/>, <see cref="EmojiGlyphRenderer"/>) while they draw.
///
/// The alternative was threading background/text color and font through the ~10
/// <c>TryGenerate*</c> signatures and every one of their call sites; an explicit scope
/// pushed around a single render call keeps the change surgical and leaves every existing
/// caller (which wants the stock look) untouched.
/// </summary>
public static class IconStyleScope
{
    [ThreadStatic] private static KeyIconSpec? _current;

    public static KeyIconSpec? Current => _current;

    /// <summary>Applies <paramref name="spec"/> to every icon rendered on this thread until
    /// the returned scope is disposed. Pass null to render with the stock look.</summary>
    public static IDisposable Push(KeyIconSpec? spec) => new Scope(spec);

    /// <summary>Background color override (GDI+), or null to keep the generator's own.</summary>
    public static System.Drawing.Color? OverrideBg => KeyIconSpec.ParseColor(_current?.BgColor);

    /// <summary>Text color override (GDI+), or null for the stock white.</summary>
    public static System.Drawing.Color? OverrideText => KeyIconSpec.ParseColor(_current?.TextColor);

    /// <summary>Caption text override — replaces whatever text a generator would draw on its
    /// own (a disk folder's name, a Google Home device's name, an action's summary), so the
    /// user's own wording survives every regeneration. Null = keep the generator's text.</summary>
    public static string? OverrideCaption =>
        string.IsNullOrWhiteSpace(_current?.Text) ? null : _current!.Text;

    /// <summary>Font family override, or null for the generator's own semibold face.</summary>
    public static string? OverrideFontFamily =>
        string.IsNullOrWhiteSpace(_current?.FontFamily) ? null : _current!.FontFamily;

    /// <summary>Fixed caption size in pixels, or null for the shrink-to-fit default.</summary>
    public static double? OverrideFontSize =>
        _current is { FontSize: > 0 } s ? s.FontSize : null;

    private sealed class Scope : IDisposable
    {
        private readonly KeyIconSpec? _previous;
        public Scope(KeyIconSpec? spec) { _previous = _current; _current = spec; }
        public void Dispose() => _current = _previous;
    }
}
