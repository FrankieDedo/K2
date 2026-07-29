using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace K2.Core.Services;

/// <summary>
/// Catalog of app-wide UI accent color themes selectable in Settings (General &gt;
/// Accent color): "K2 Red" (the original brand color) and "Mountain Blue" (#0044FF,
/// Mountain's own brand color). Mirrors <see cref="FontCatalog"/> exactly — same
/// "Options list + Resolve + Apply overrides DynamicResource keys on
/// Application.Current.Resources" shape (see K2Theme.xaml's K2AccentBrush family,
/// all referenced via DynamicResource so this takes effect live, no restart needed).
/// </summary>
public static class AccentCatalog
{
    public sealed record AccentOption(string Key, string DisplayName, Color Accent, Color Hover, Color Dim, Color Text);

    public const string DefaultKey = "K2Red";

    public static readonly IReadOnlyList<AccentOption> Options = new[]
    {
        new AccentOption("K2Red", "K2 Red",
            Color.FromRgb(0x90, 0x00, 0x00), Color.FromRgb(0xB2, 0x22, 0x22), Color.FromRgb(0x5C, 0x00, 0x00), Colors.White),
        new AccentOption("MountainBlue", "Mountain Blue",
            Color.FromRgb(0x00, 0x44, 0xFF), Color.FromRgb(0x3D, 0x6F, 0xFF), Color.FromRgb(0x00, 0x2F, 0x99), Colors.White),
    };

    public static AccentOption Resolve(string? key) =>
        Options.FirstOrDefault(o => o.Key == key) ?? Options[0];

    /// <summary>
    /// Applies the given accent theme (by <see cref="AccentOption.Key"/>) to the whole
    /// app by overriding the K2AccentBrush/K2AccentHoverBrush/K2AccentDimBrush/
    /// K2AccentTextBrush DynamicResources (see K2Theme.xaml) at the Application level.
    /// Every window/control style referencing them picks up the change live.
    /// </summary>
    public static void Apply(string? key)
    {
        var option = Resolve(key);
        if (Application.Current is null) return;

        Application.Current.Resources["K2AccentBrush"] = new SolidColorBrush(option.Accent);
        Application.Current.Resources["K2AccentHoverBrush"] = new SolidColorBrush(option.Hover);
        Application.Current.Resources["K2AccentDimBrush"] = new SolidColorBrush(option.Dim);
        Application.Current.Resources["K2AccentTextBrush"] = new SolidColorBrush(option.Text);

        Applied?.Invoke();
    }

    /// <summary>Raised after <see cref="Apply"/> updates the live resources — code-behind
    /// that resolved K2AccentBrush once via FindResource (not a binding, so it won't
    /// auto-refresh) should re-run its coloring logic from this event.</summary>
    public static event Action? Applied;
}
