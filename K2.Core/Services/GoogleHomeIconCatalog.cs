using System;
using System.Collections.Generic;
using System.IO;

namespace K2.Core.Services;

/// <summary>
/// The Material icon each home.google.com device tile shows (its <c>&lt;mat-icon&gt;</c> ligature
/// name, captured as <see cref="GoogleHomeBinding.IconName"/> — see <c>GoogleHomeJs.iconNameFor</c>):
/// a friendly label for it, plus the on-disk cache of the glyph rasterized straight from the page.
///
/// Why rasterized from the page rather than drawn from an icon font K2 ships: rendering these
/// from Segoe MDL2 Assets/Fluent would mean hardcoding a codepoint per icon, and a wrong guess
/// produces a silently blank or plainly wrong tile — which is exactly why this was left as a
/// follow-up when the icon NAMES were first captured. <c>GoogleHomeJs.renderIcons</c> instead
/// draws the ligature to a canvas using the icon font home.google.com itself has loaded, so the
/// glyph is by construction the one the user sees in Google Home. The PNGs are white-on-
/// transparent at <see cref="RenderSize"/> and get composited onto a K2 tile by
/// <see cref="K2.Core.IconImageGenerator.TryGenerateGoogleHomeIcon"/>.
///
/// The cache is filled opportunistically by <see cref="GoogleHomeSetupWindow"/> whenever it has
/// a live page to render from; a missing entry is never fatal, it just means a caption-only tile.
/// </summary>
public static class GoogleHomeIconCatalog
{
    /// <summary>Rendered generously large so the same PNG stays sharp on both a DisplayPad tile
    /// and an Everest numpad display key without re-rendering per device.</summary>
    public const int RenderSize = 256;

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["lightbulb"] = "Lampadina",
        ["outlet"] = "Presa elettrica",
        ["switch"] = "Interruttore",
        ["devices_other"] = "Altro dispositivo",
        ["tv"] = "TV",
        ["vacuum"] = "Aspirapolvere",
        ["power_settings_new"] = "Simbolo on/off",
        ["light_group"] = "Gruppo di luci",
    };

    /// <summary>Friendly label for a captured Material icon name, or the raw name itself
    /// (or "" for an empty/unknown one) when there's no known mapping.</summary>
    public static string LabelFor(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return "";
        return Labels.TryGetValue(iconName, out var label) ? label : iconName;
    }

    /// <summary>Default key picture for a "googlehome" action: the bound device's own Material
    /// icon plus its name. Shared by the DisplayPad and Everest numpad key dialogs so the two
    /// don't each re-resolve the binding. False when the binding no longer exists — the caller
    /// then leaves the key's picture alone, same as any other failed auto-icon.</summary>
    public static bool TryGenerateKeyIcon(string? bindingId, int size, string outputPngPath)
    {
        var binding = GoogleHomeStore.Find(bindingId);
        if (binding is null) return false;
        return K2.Core.IconImageGenerator.TryGenerateGoogleHomeIcon(
            binding.IconName, DeviceNameOnly(binding.Name), size, outputPngPath);
    }

    /// <summary>Drops the room prefix from a "Room / Device" name for use as a key caption: a
    /// tile is a few dozen pixels wide, and the room is the least useful half there (the user
    /// knows which key they bound). The room IS kept everywhere the binding is listed — see
    /// <see cref="GoogleHomeBinding.DisplayLabel"/> — since that is where same-named devices in
    /// different rooms have to be told apart. Splits on the LAST separator, so a device whose
    /// own name contains one keeps it.</summary>
    private static string DeviceNameOnly(string name)
    {
        int sep = name.LastIndexOf(" / ", StringComparison.Ordinal);
        if (sep < 0) return name;
        string device = name[(sep + 3)..].Trim();
        return device.Length > 0 ? device : name;
    }

    private static string CacheDir => Path.Combine(K2Paths.Root, "GoogleHome", "icons");

    /// <summary>Cached PNG path for an icon name, or null when nothing has been rendered for it
    /// (or the name is unusable as a file name). Never throws.</summary>
    public static string? TryGetCachedPng(string? iconName)
    {
        string? path = SafeCachePath(iconName);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Stores one glyph rendered by <c>GoogleHomeJs.renderIcons</c>, given its
    /// <c>data:image/png;base64,…</c> URL. Best-effort: a failure just leaves the icon uncached.</summary>
    public static void SaveFromDataUrl(string iconName, string dataUrl)
    {
        try
        {
            string? path = SafeCachePath(iconName);
            if (path is null) return;

            int comma = dataUrl.IndexOf(',');
            if (comma < 0 || !dataUrl.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)) return;

            byte[] png = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(path, png);
        }
        catch
        {
            // Caching is an optimisation, not a requirement — see the class doc.
        }
    }

    /// <summary>Ligature names are plain lowercase identifiers ("light_group"), but they come
    /// from a live page, so they are validated rather than trusted as file names.</summary>
    private static string? SafeCachePath(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return null;
        foreach (char c in iconName)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-') return null;
        }
        return Path.Combine(CacheDir, iconName + ".png");
    }
}
