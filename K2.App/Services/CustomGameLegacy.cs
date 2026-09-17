using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Reads an action written by an older build of the studio.
///
/// <para>
/// A tile used to BE one reading with a fixed set of parts: one probe, one indicator, one value,
/// one caption, each with its own anchor field. It is now a list of readings and a list of
/// elements, which is what lets a tile carry two readings, or show one twice. The file on disk is
/// not migrated in place — it is upgraded on the way in, and rewritten in the new shape the first
/// time the user saves. That way a downgrade loses the day's edits rather than the whole tile, and
/// nothing here has to be run "once" and remembered.
/// </para>
/// </summary>
internal static class CustomGameLegacy
{
    /// <summary>An action as stored, in whichever shape it was stored in.</summary>
    public static CustomGameAction? ReadAction(JsonNode? node, JsonSerializerOptions options)
    {
        if (node is not JsonObject obj) return null;

        // The new shape says so by carrying readings (or elements) of its own.
        bool current = obj["Readings"] is JsonArray { Count: > 0 } ||
                       obj["Elements"] is JsonArray { Count: > 0 } &&
                       obj["Elements"]![0]?["Kind"] is not null;

        return current ? obj.Deserialize<CustomGameAction>(options) : Upgrade(obj, options);
    }

    private static CustomGameAction Upgrade(JsonObject obj, JsonSerializerOptions options)
    {
        string id = Str(obj, "Id");
        string name = Str(obj, "Name");

        var reading = new TileReading
        {
            Id = "r" + (id.Length > 0 ? id : Guid.NewGuid().ToString("N")[..8]),
            Name = name.Length > 0 ? name : Loc.Get("studio_reading_untitled"),
            Source = Enum<CustomSource>(obj, "Source", CustomSource.Screen),
            ValueKind = Enum<CustomValueKind>(obj, "ValueKind", CustomValueKind.Range),
            ProbeId = Str(obj, "ProbeId"),
            MultiMode = Enum<CustomMultiMode>(obj, "MultiMode", CustomMultiMode.Colors),
            States = obj["States"]?.Deserialize<List<CustomActionState>>(options) ?? new(),
            Min = Num(obj, "Min"),
            Max = Num(obj, "Max"),
        };

        // Placement came either from the short-lived "Layout" list or, before that, from one anchor
        // field per part. Both are read here so no tile loses where its pieces were put.
        var placements = ReadPlacements(obj);

        var elements = new List<TileElement>();
        foreach (var (kind, place) in placements)
        {
            var element = new TileElement
            {
                Id = NewId(),
                Kind = kind,
                Anchor = place.Anchor,
                Snap = place.Snap,
                X = place.X,
                Y = place.Y,
                Margin = place.Margin,
                Box = place.Box,
            };

            switch (kind)
            {
                case TileElementKind.Icon:
                    // Kept even with no picture of its own: a multi-state reading may be the one
                    // supplying it, and dropping the element would lose where it sat.
                    element = element with
                    {
                        IconPath = obj["IconPath"]?.GetValue<string?>(),
                        ReadingId = reading.ValueKind == CustomValueKind.MultiState ? reading.Id : "",
                    };
                    break;

                case TileElementKind.Indicator:
                    element = element with
                    {
                        ReadingId = reading.Id,
                        Shape = Enum(obj, "IndicatorShape", CustomIndicatorShape.LinearHorizontal),
                    };
                    break;

                case TileElementKind.Value:
                    // "Print the reading on the tile" used to be a switch; an element that isn't
                    // wanted is now simply not in the list.
                    if (obj["ShowValue"]?.GetValue<bool>() == false) continue;
                    element = element with
                    {
                        ReadingId = reading.Id,
                        FontFamily = obj["FontFamily"]?.GetValue<string?>(),
                        FontSize = Num(obj, "ValueFontSize") ?? 0,
                    };
                    break;

                case TileElementKind.Label:
                    element = element with
                    {
                        FontFamily = obj["FontFamily"]?.GetValue<string?>(),
                        FontSize = Num(obj, "CaptionFontSize") ?? 0,
                    };
                    break;
            }

            elements.Add(element);
        }

        return new CustomGameAction
        {
            Id = id,
            Name = name,
            ProfileId = Str(obj, "ProfileId"),
            Category = Str(obj, "Category"),
            Style = obj["Style"]?.Deserialize<CustomTileOverride>(options),
            Readings = new[] { reading },
            Elements = elements,
        };
    }

    /// <summary>Placement of each old part, in drawing order.</summary>
    private sealed record Placement(TileAnchor Anchor, bool Snap, double X, double Y, double Margin, TileBox? Box);

    private static List<(TileElementKind Kind, Placement Place)> ReadPlacements(JsonObject obj)
    {
        var order = new List<(TileElementKind, Placement)>();

        if (obj["Layout"] is JsonArray layout && layout.Count > 0)
        {
            foreach (var entry in layout.OfType<JsonObject>())
            {
                var kind = (entry["Element"]?.GetValue<string>() ?? "") switch
                {
                    "Icon" => TileElementKind.Icon,
                    "Indicator" => TileElementKind.Indicator,
                    "Value" => TileElementKind.Value,
                    _ => TileElementKind.Label,
                };
                order.Add((kind, new Placement(
                    Anchor: Enum(entry, "Anchor", TileAnchor.Center),
                    Snap: entry["Snap"]?.GetValue<bool>() ?? true,
                    X: Num(entry, "X") ?? 0.5,
                    Y: Num(entry, "Y") ?? 0.5,
                    Margin: Num(entry, "Margin") ?? 0.04,
                    Box: ReadBox(entry["Box"]))));
            }
            return order;
        }

        // Older still: one anchor per part, always in this order.
        var textAnchor = Enum(obj, "TextAnchor", TileAnchor.TopCenter);
        order.Add((TileElementKind.Icon, Simple(Enum(obj, "IconAnchor", TileAnchor.Center), ReadBox(obj["IconBox"]))));
        order.Add((TileElementKind.Indicator, Simple(Enum(obj, "IndicatorAnchor", TileAnchor.BottomCenter), ReadBox(obj["IndicatorBox"]))));
        order.Add((TileElementKind.Value, Simple(textAnchor, ReadBox(obj["ValueBox"]))));
        order.Add((TileElementKind.Label, Simple(
            obj["CaptionAnchor"] is null ? textAnchor : Enum(obj, "CaptionAnchor", textAnchor),
            ReadBox(obj["CaptionBox"]))));
        return order;

        static Placement Simple(TileAnchor anchor, TileBox? box) =>
            new(anchor, Snap: true, X: 0.5, Y: 0.5, Margin: 0.04, Box: box);
    }

    private static TileBox? ReadBox(JsonNode? node) =>
        node is JsonObject box && Num(box, "Width") is { } w && Num(box, "Height") is { } h
            ? new TileBox(w, h)
            : null;

    private static string Str(JsonObject obj, string name) => obj[name]?.GetValue<string?>() ?? "";

    private static double? Num(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<double>(); }
        catch { return null; }
    }

    private static T Enum<T>(JsonObject obj, string name, T fallback) where T : struct =>
        System.Enum.TryParse<T>(obj[name]?.GetValue<string?>(), ignoreCase: true, out var parsed)
            ? parsed : fallback;

    /// <summary>A fresh element id. Time-based plus a counter, so a whole tile's worth created in
    /// the same tick still comes out unique.</summary>
    public static string NewId() => "e" + DateTime.UtcNow.Ticks.ToString("x") + (_seq++).ToString("x");

    private static int _seq;
}
