using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace K2.Core.Services;

/// <summary>
/// Catalog of app-wide UI fonts selectable in Settings (General &gt; Font).
/// Every embedded family (all SIL OFL 1.1, see K2.Core/Fonts/&lt;Family&gt;/LICENSE.*.txt)
/// follows the same Regular/Bold/Italic/BoldItalic embedding as Roboto
/// (K2.Core.csproj), referenced via a pack URI so it works without being
/// installed on the end-user's machine. "Segoe UI" is the one non-embedded
/// entry: it ships with Windows, so it's referenced by name only.
/// </summary>
public static class FontCatalog
{
    /// <summary>FolderUri is null for the one non-embedded option (Segoe UI, a
    /// system font referenced by name only); every embedded family instead
    /// points at its own Fonts/&lt;Family&gt;/ pack URI folder (see <see cref="Apply"/>
    /// for why a folder lookup, not a "pack-uri#family" string, is required).
    /// SizeOverride lets a family render at a different nominal size than
    /// <see cref="DefaultFontSize"/> — OpenDyslexic's glyphs are noticeably
    /// larger than other UI fonts at the same point size, so it ships smaller.</summary>
    public sealed record FontOption(string Key, string DisplayName, Uri? FolderUri, double? SizeOverride = null);

    public const string DefaultKey = "Roboto";
    public const double DefaultFontSize = 13.0;

    private const string PackBase = "pack://application:,,,/K2.Core;component/Fonts";

    public static readonly IReadOnlyList<FontOption> Options = new[]
    {
        new FontOption("Roboto", "Roboto", new Uri($"{PackBase}/")),
        new FontOption("SegoeUI", "Segoe UI", null),
        new FontOption("Inter", "Inter", new Uri($"{PackBase}/Inter/")),
        new FontOption("IBMPlexSans", "IBM Plex Sans", new Uri($"{PackBase}/IBMPlexSans/")),
        new FontOption("PublicSans", "Public Sans", new Uri($"{PackBase}/PublicSans/")),
        new FontOption("WorkSans", "Work Sans", new Uri($"{PackBase}/WorkSans/")),
        new FontOption("SourceSans3", "Source Sans 3", new Uri($"{PackBase}/SourceSans3/")),
        new FontOption("OpenDyslexic", "OpenDyslexic", new Uri($"{PackBase}/OpenDyslexic/"), SizeOverride: 11.0),
    };

    public static FontOption Resolve(string? key) =>
        Options.FirstOrDefault(o => o.Key == key) ?? Options[0];

    /// <summary>
    /// Applies the given font (by <see cref="FontOption.Key"/>) to the whole app by
    /// overriding the "K2AppFontFamily"/"K2AppFontSize" DynamicResources (see
    /// K2Theme.xaml) at the Application level. Every window/control style using
    /// them picks up the change live, no restart needed.
    ///
    /// IMPORTANT: constructing a custom embedded font from CODE via
    /// <c>new FontFamily("pack://.../#Name, Segoe UI")</c> (or via
    /// <c>FontFamilyConverter</c> called without a XAML parsing context) silently
    /// resolves to the system fallback every time — verified empirically, this is
    /// NOT the same code path XAML/BAML uses (which supplies a base-URI context the
    /// plain converter/constructor don't have). <see cref="Fonts.GetFontFamilies"/>
    /// is the one API that reliably loads a font from a packed resource folder at
    /// runtime, so every embedded option is resolved that way; only the one
    /// non-embedded option (Segoe UI) goes through the plain-name constructor.
    /// </summary>
    public static void Apply(string? key)
    {
        var option = Resolve(key);
        if (Application.Current is null) return;

        FontFamily family = option.FolderUri is not null
            ? Fonts.GetFontFamilies(option.FolderUri).FirstOrDefault() ?? new FontFamily("Segoe UI")
            : new FontFamily(option.DisplayName);

        Application.Current.Resources["K2AppFontFamily"] = family;
        Application.Current.Resources["K2AppFontSize"] = option.SizeOverride ?? DefaultFontSize;
    }
}
