using System;
using System.IO;
using System.Linq;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Maps a key's just-assigned action to a matching Base Camp gallery icon for automatic
/// default-icon assignment — the direct answer to the user's original ask ("quando viene
/// assegnata un'azione ... viene messa un'icona di default"). Mirrors the existing
/// exec/folder/googlehome auto-icon paths in <c>DpKeyConfigDialog</c>/<c>NdkKeyConfigDialog</c>,
/// but sourced from the ported picker gallery instead of a live executable/shell lookup.
/// Returns null (no forced icon) rather than guess wrong — callers keep whatever they already
/// had in that case.
///
/// Assets live under <c>Assets/IconGallery/&lt;style&gt;/&lt;category&gt;/*</c> — TWO parallel
/// trees, "black" and "color" (user-organized 2026-07-29, replacing the original flat one-style
/// copy: same underlying Base Camp art, split so <see cref="AppSettings.IconGalleryStyle"/> can
/// pick which look to use).
///
/// The actual action-type → icon rules are NOT hardcoded here — they come from
/// <see cref="IconMappingConfig"/> (backed by the user/end-user-editable <c>icon_mapping.xml</c>,
/// one explicit row per physical icon file). This class is purely the resolution ALGORITHM:
/// given the rows for a requested action type/value, pick the one for the current
/// <see cref="AppSettings.IconGalleryStyle"/>, falling back to the other style tree when the
/// preferred one has no row for that specific icon.
/// </summary>
public static class IconGalleryDefaults
{
    /// <summary>Resolves a default gallery icon's source path for the given action, or null if
    /// none applies — unmapped action type, gallery folder missing/empty (partial install), or
    /// no row in <c>icon_mapping.xml</c> covers this specific value.</summary>
    public static string? Resolve(string actionType, string? actionValue)
    {
        string style = AppSettings.IconGalleryStyle;

        // Rows whose value starts with "ref:" are reference-only (see IconMappingConfig docs)
        // and must never match a real ActionValue — no real value is ever written that way.
        // Materialized once (not re-enumerated per FirstOrDefault below).
        var candidates = IconMappingConfig.Current.Rows.Where(r =>
            string.Equals(r.ActionType, actionType, StringComparison.OrdinalIgnoreCase) &&
            !r.Value.StartsWith("ref:", StringComparison.Ordinal)).ToList();

        // "Fixed" rows (empty Value) apply regardless of ActionValue — mouse/macro/multi/
        // hotkeyswitch/youtube. Value-specific rows only match when the (possibly cmd~arg
        // tilde-stripped) value equals the row's Value, case/whitespace-insensitively.
        string cmd = actionValue ?? "";
        int tilde = cmd.IndexOf('~');
        if (tilde >= 0) cmd = cmd[..tilde];
        cmd = cmd.Trim();

        bool IsValueMatch(IconMappingConfig.IconRow r) =>
            r.Value.Length == 0 || string.Equals(r.Value, cmd, StringComparison.OrdinalIgnoreCase);

        // Prefer a row that's already in the requested style — only fall back to whichever
        // style happens to have a row when the preferred one doesn't (bug fixed 2026-07-29:
        // picking ANY matching row first, regardless of style, made every "color"-style
        // request that also had a black row silently resolve to the black file).
        var match = candidates.FirstOrDefault(r => r.Style == style && IsValueMatch(r))
                    ?? candidates.FirstOrDefault(IsValueMatch);
        if (match is null) return null;

        string? path = FindExisting(match.Category, match.Style, match.File);
        if (path is not null) return path;

        // The chosen row's file is missing from disk (partial install) — try the other
        // style's row for the SAME icon instead of giving up entirely.
        var fallback = candidates.FirstOrDefault(r =>
            r.Category == match.Category && r.Value == match.Value && r.Style != match.Style);
        return fallback is null ? null : FindExisting(fallback.Category, fallback.Style, fallback.File);
    }

    /// <summary>Resolves + renders in one step — what <c>DpKeyConfigDialog</c>/
    /// <c>NdkKeyConfigDialog</c>'s auto-icon switch actually calls. Returns false (caller keeps
    /// whatever image it already had) when there's no gallery match, same contract as every
    /// other <c>IconImageGenerator.TryGenerate*</c> method.</summary>
    public static bool TryGenerateKeyIcon(string actionType, string? actionValue, int size, string outputPngPath)
    {
        string? source = Resolve(actionType, actionValue);
        return source is not null && IconImageGenerator.TryGenerateGalleryIcon(source, size, outputPngPath);
    }

    /// <summary>True when at least one of the two style trees exists on disk — gates whether
    /// the "Default icon" button/Settings style picker should even show (partial/stripped
    /// install has neither).</summary>
    public static bool HasGallery() =>
        Directory.Exists(Path.Combine(GalleryRoot, "black")) || Directory.Exists(Path.Combine(GalleryRoot, "color"));

    private static string? FindExisting(string category, string style, string fileName)
    {
        string path = Path.Combine(GalleryRoot, style, category, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string GalleryRoot => Path.Combine(AppContext.BaseDirectory, "Assets", "IconGallery");
}
