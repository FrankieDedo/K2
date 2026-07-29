using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// EXTERNAL, user-editable action-type → gallery-icon mapping, loaded from
/// <c>icon_mapping.xml</c> (next to the executable) — same "edit the file, no rebuild needed"
/// philosophy as <see cref="EverestRgbConfig"/>. One row per physical icon file (user request,
/// 2026-07-29: "una riga per ogni icona nelle cartelle color e black, collegata ad una relativa
/// azione ... anche se non riesci a mapparle" — every icon gets a row, even ones nobody's
/// figured out an action for yet) rather than the fuzzy filename-matching rule engine this
/// replaced: each row explicitly states which action type + value (if any) that ONE file is
/// for, so the resolver in <see cref="IconGalleryDefaults"/> is a plain lookup, no runtime
/// guessing. Rows with an empty <c>actionType</c> are catalogued but inert (decorative Base
/// Camp art with no known K2 action) — harmless, left for future manual completion. Rows whose
/// <c>value</c> starts with <c>"ref:"</c> are also inert by construction (a real ActionValue
/// never starts with that) — they exist to record which Adobe/DaVinci reference action a file
/// represents even though K2 has no way to match it live (see the XML file's own header
/// comment for why).
/// </summary>
public sealed class IconMappingConfig
{
    /// <summary>One <c>&lt;icon&gt;</c> row: a specific file is THE icon for
    /// <paramref name="ActionType"/>, optionally scoped to one specific <paramref name="Value"/>
    /// (empty value = applies regardless of the action's value, e.g. "mouse"/"macro").</summary>
    public sealed record IconRow(string Style, string Category, string File, string ActionType, string Value);

    public IReadOnlyList<IconRow> Rows { get; init; } = Array.Empty<IconRow>();

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "icon_mapping.xml");

    private static IconMappingConfig? _cached;

    /// <summary>Cached singleton — re-parsing 502 small XML rows on every icon resolve would be
    /// wasteful; <see cref="IconGalleryDefaults"/> resolves per action, not per pixel.</summary>
    public static IconMappingConfig Current => _cached ??= Load();

    /// <summary>Forces the next <see cref="Current"/> access to re-read the file from disk.</summary>
    public static void Reload() => _cached = null;

    private static IconMappingConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var doc = XDocument.Load(FilePath);
                var root = doc.Root;
                if (root is not null)
                {
                    var rows = new List<IconRow>();
                    foreach (var el in root.Elements("icon"))
                    {
                        string? style = (string?)el.Attribute("style");
                        string? category = (string?)el.Attribute("category");
                        string? file = (string?)el.Attribute("file");
                        if (string.IsNullOrWhiteSpace(style) || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(file))
                            continue;

                        string actionType = (string?)el.Attribute("actionType") ?? "";
                        string value = (string?)el.Attribute("value") ?? "";
                        rows.Add(new IconRow(style, category, file, actionType, value));
                    }

                    if (rows.Count > 0)
                        return new IconMappingConfig { Rows = rows };
                }
            }
        }
        catch (Exception ex)
        {
            App.WriteLog("[IconMappingConfig] failed to read icon_mapping.xml, using built-in defaults: " + ex.Message);
        }
        return Default();
    }

    /// <summary>Minimal built-in fallback — used when <c>icon_mapping.xml</c> is missing,
    /// unreadable, or empty. Deliberately NOT a full copy of the shipped file's 502 rows (that
    /// would mean maintaining the same data twice); just enough that "Default icon" keeps
    /// working for the handful of action types with no per-value matching (mouse/macro/multi/
    /// hotkeyswitch/youtube) even with a missing/corrupt file — the value-matched types
    /// (zoom/obs/twitch/spotify) simply return no icon until the real file is restored.</summary>
    public static IconMappingConfig Default() => new()
    {
        Rows = new List<IconRow>
        {
            new("black", "default", "Icon_Mouse.jpg", "mouse", ""),
            new("black", "default", "Icon_Macro.jpg", "macro", ""),
            new("black", "default", "Multi_Action black-01.png", "multi", ""),
            new("color", "default", "49_Multi Action solid-01.png", "multi", ""),
            new("black", "default", "hotkey1.png", "hotkeyswitch", ""),
            new("color", "default", "47_Hotkey Switch ON solid-01.png", "hotkeyswitch", ""),
            new("black", "youtube", "16_chat-message.png", "youtube", ""),
            new("color", "youtube", "01_chat-message.png", "youtube", ""),
        },
    };
}
