using System;
using System.Collections.Generic;

namespace K2.Core.Services;

/// <summary>
/// Last-resort default icon for a display key whose action has no picture of its own —
/// a Segoe MDL2 Assets glyph + the action's own localized summary, rendered by
/// <see cref="IconImageGenerator.TryGenerateGlyphIcon"/> (same tile layout as the folder/
/// back/emoji-browser tiles, so it lines up with them on the grid).
///
/// It sits BELOW the two existing auto-icon layers, and only runs when both come up empty:
/// 1. the per-type generators (exec/folder/dp_folder/googlehome/emoji — a real icon extracted
///    from the target itself), then
/// 2. <c>IconGalleryDefaults</c> (ported Base Camp gallery art, driven by <c>icon_mapping.xml</c>).
///
/// Layer 2 only covers the action types Base Camp shipped art for (mouse/macro/multi/
/// hotkeyswitch/youtube + the per-app packs), so before this class every OTHER type —
/// "oscmd" above all, but also url/browser/profile/media/keys/command/text/pyscript/disable —
/// silently got NO default icon at all on DisplayPad and on the Everest numpad display keys
/// (user report 2026-08-23): the key kept whatever picture was there before, or stayed blank.
///
/// Every codepoint below was verified by rendering it from the installed Segoe MDL2 Assets
/// and looking at the result — the same "a wrong guess is worse than no icon" concern
/// <see cref="GoogleHomeIconCatalog"/> documents. Unknown/unmapped action types return null
/// (no icon) rather than a meaningless placeholder, keeping the caller's "keeps whatever it
/// already had" contract.
/// </summary>
public static class ActionIconFallback
{
    /// <summary>Resolve + render in one step — the signature every other auto-icon layer
    /// uses (<c>IconGalleryDefaults.TryGenerateKeyIcon</c>, <c>IconImageGenerator.TryGenerate*</c>),
    /// so it can be chained after them with a plain <c>||</c>.</summary>
    public static bool TryGenerate(string? actionType, string? actionValue, int size, string outputPngPath, bool showCaption = true)
    {
        // "Previous/Next track" and single-target "profile: Next/Previous" get the emoji
        // browser's own bold nav-arrow triangle instead of a thin MDL2 outline glyph — same
        // shape used for its scroll keys (see MainWindow.DisplayPad.EmojiBrowser.cs's
        // EmbNavTile), more legible on a 102/72 px tile and visually consistent with it
        // (user request 2026-08-24).
        if ((MediaNavShape(actionType, actionValue) ?? ProfileNavShape(actionType, actionValue))
            is IconImageGenerator.NavShape navShape)
        {
            string caption = showCaption ? Caption(actionType, actionValue) : "";
            return IconImageGenerator.TryGenerateNavIcon(navShape, caption, size, outputPngPath);
        }

        string? glyph = ResolveGlyph(actionType, actionValue);
        return glyph is not null
            && IconImageGenerator.TryGenerateGlyphIcon(glyph, Caption(actionType, actionValue), size, outputPngPath, showCaption);
    }

    /// <summary>Nav-arrow shape for a "media" action's Previous/Next track value, or null for
    /// every other action (including every other media key).</summary>
    private static IconImageGenerator.NavShape? MediaNavShape(string? actionType, string? actionValue)
    {
        if (!string.Equals(actionType, "media", StringComparison.OrdinalIgnoreCase)) return null;
        string media = ActionTypeHelper.NormalizeMediaKey((actionValue ?? "").Trim()) ?? (actionValue ?? "").Trim();
        return media switch
        {
            "Previous track" => IconImageGenerator.NavShape.Left,
            "Next track"     => IconImageGenerator.NavShape.Right,
            _                => null,
        };
    }

    /// <summary>Nav-arrow shape for a single-target "profile" action whose target is the
    /// legacy Next/Previous keyword — a named profile or a multi-device payload keeps the
    /// generic profile glyph (a switch destination isn't "next" or "previous" of anything).</summary>
    private static IconImageGenerator.NavShape? ProfileNavShape(string? actionType, string? actionValue)
    {
        if (!string.Equals(actionType, "profile", StringComparison.OrdinalIgnoreCase)) return null;
        string val = (actionValue ?? "").Trim();
        var payload = ProfileTargetPayload.Parse(val);
        string target = payload is null
            ? val
            : (payload.Targets.Count == 1 ? payload.Targets[0].Target : "");
        return target switch
        {
            "Next"     => IconImageGenerator.NavShape.Right,
            "Previous" => IconImageGenerator.NavShape.Left,
            _          => null,
        };
    }

    /// <summary>True when this class has a glyph for the action — lets a caller decide
    /// whether a default icon is available without rendering it.</summary>
    public static bool CanGenerate(string? actionType, string? actionValue) =>
        ResolveGlyph(actionType, actionValue) is not null;

    /// <summary>Value-specific glyph first (an "oscmd" key is far more useful showing a
    /// padlock/power button than a generic "system command" pictogram), then the per-type
    /// one.</summary>
    private static string? ResolveGlyph(string? actionType, string? actionValue)
    {
        if (string.IsNullOrEmpty(actionType) || actionType == "none") return null;

        string type = actionType.ToLowerInvariant();
        string value = (actionValue ?? "").Trim();

        switch (type)
        {
            case "oscmd":
                // Imported Base Camp values use their own wording ("Run Task Manager",
                // "Lock Computer") — normalize to K2's vocabulary before looking up.
                string os = ActionTypeHelper.NormalizeOsCommand(value) ?? value;
                if (OsCmdGlyphs.TryGetValue(os, out var osGlyph)) return osGlyph;
                break;

            case "media":
                string media = ActionTypeHelper.NormalizeMediaKey(value) ?? value;
                if (MediaGlyphs.TryGetValue(media, out var mediaGlyph)) return mediaGlyph;
                break;

            case "discord":
                // Value is "command~arg" (see ButtonActionEngine) — the command alone decides
                // the glyph; a mic/speaker tile says far more than the generic Discord one.
                string discord = value.Split('~')[0];
                if (DiscordGlyphs.TryGetValue(discord, out var discordGlyph)) return discordGlyph;
                break;
        }

        if (TypeGlyphs.TryGetValue(type, out var typeGlyph)) return typeGlyph;
        return ActionTypeHelper.IsUnrecognized(actionType) ? "" : null; // warning triangle
    }

    /// <summary>Tile caption: the action's own key-list summary (already localized where a
    /// localized form exists) when it says something, plus a localized per-value label for
    /// the two picker-backed types whose summary is the raw stored English value. Falls back
    /// to the action type's UI name so a tile is never captionless. Public so the key-config
    /// dialogs can recompute the exact text a default icon baked in, to prefill "Add/Edit
    /// text" instead of stacking new text on top of it.</summary>
    public static string Caption(string? actionType, string? actionValue)
    {
        string type = (actionType ?? "").ToLowerInvariant();
        string value = (actionValue ?? "").Trim();

        if (type == "oscmd")
        {
            string os = ActionTypeHelper.NormalizeOsCommand(value) ?? value;
            if (OsCmdLocKeys.TryGetValue(os, out var locKey)) return Loc.Get(locKey);
        }

        string summary = ActionTypeHelper.Summary(actionType, actionValue);
        if (!string.IsNullOrWhiteSpace(summary) && !string.Equals(summary, actionType, StringComparison.Ordinal))
            return summary;

        string typeLoc = Loc.Get("act_" + type);
        return string.IsNullOrWhiteSpace(typeLoc) ? summary : typeLoc;
    }

    /// <summary>Canonical "oscmd" values (see <see cref="ActionTypeHelper.NormalizeOsCommand"/>,
    /// which is also what the picker in <c>ButtonActionDialog.Simple</c> stores).</summary>
    private static readonly Dictionary<string, string> OsCmdGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Task Manager"] = "", // chart in a window
        ["Calculator"]   = "",
        ["Explorer"]     = "", // file explorer
        ["Lock"]         = "", // padlock
        ["Shutdown"]     = "", // power button
        ["Restart"]      = "", // circular arrow
        ["Sleep"]        = "", // crescent moon
        ["Hibernate"]    = "", // crescent moon (thinner — the deeper of the two)
    };

    /// <summary>Same keys as <see cref="OsCmdGlyphs"/> → the loc keys the picker itself uses,
    /// so the tile caption reads exactly like the option the user chose in the dialog (the
    /// generic <see cref="ActionTypeHelper.Summary"/> returns the raw English value here).</summary>
    private static readonly Dictionary<string, string> OsCmdLocKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Task Manager"] = "oscmd_taskmgr",
        ["Calculator"]   = "oscmd_calc",
        ["Explorer"]     = "oscmd_explorer",
        ["Lock"]         = "oscmd_lock",
        ["Shutdown"]     = "oscmd_shutdown",
        ["Restart"]      = "oscmd_restart",
        ["Sleep"]        = "oscmd_sleep",
        ["Hibernate"]    = "oscmd_hibernate",
    };

    /// <summary>Canonical "media" values (see <see cref="ActionTypeHelper.MediaKeys"/>) —
    /// "Previous track"/"Next track" are absent on purpose, handled by <see cref="MediaNavShape"/>
    /// before this dictionary is ever consulted.</summary>
    private static readonly Dictionary<string, string> MediaGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Play/Pause"]     = "",
        ["Stop"]           = "",
        ["Volume Up"]      = "",
        ["Volume Down"]    = "",
        ["Mute"]           = "",
        ["Shuffle"]        = "",
    };

    /// <summary>Per-command glyphs for the "discord" action (see
    /// <see cref="ActionTypeHelper.DiscordCommands"/>) — rendered and eyeballed like every
    /// other codepoint in this file:  microphone,  microphone crossed out,  speaker,
    ///  speaker muted,  phone,  exit door,  people,  chat bubble.</summary>
    private static readonly Dictionary<string, string> DiscordGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mute_toggle"]       = "",
        ["mute_on"]           = "",
        ["mute_off"]          = "",
        ["deafen_toggle"]     = "",
        ["deafen_on"]         = "",
        ["deafen_off"]        = "",
        ["input_mode_toggle"] = "",
        ["input_volume"]      = "",
        ["output_volume"]     = "",
        ["join_voice"]        = "",
        ["leave_voice"]       = "",
        ["user_volume"]       = "",
        ["user_mute_toggle"]  = "",
        ["send_message"]      = "",
    };

    /// <summary>Per-action-type glyph, used when no value-specific one applies. Types already
    /// covered by an earlier layer (exec/folder/dp_folder/googlehome/emoji/dp_back) are absent
    /// on purpose — they never reach this class.</summary>
    private static readonly Dictionary<string, string> TypeGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["url"]          = "", // chain link
        ["browser"]      = "", // globe
        ["profile"]      = "", // person + switch arrows
        ["oscmd"]        = "", // PC
        ["media"]        = "", // music notes
        ["mouse"]        = "",
        ["keys"]         = "", // keyboard
        ["hotkeyswitch"] = "", // sync arrows
        ["multi"]        = "", // list
        ["command"]      = "", // console window
        ["text"]         = "", // pencil
        ["macro"]        = "", // clock with history arrow
        ["pyscript"]     = "", // { }
        ["disable"]      = "", // cancel
        ["obs"]          = "",
        ["twitch"]       = "",
        ["spotify"]      = "",
        ["youtube"]      = "",
        ["discord"]      = "", // headset
        ["audiodevice"]  = "", // speaker (Segoe MDL2 "Volume")
        ["adobe"]        = "",
        ["davinci"]      = "",
        ["zoom"]         = "",
    };
}
