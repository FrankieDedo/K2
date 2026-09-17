using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// The bridge between a user-authored tile (<see cref="CustomGameAction"/>, which is data) and the
/// painter (<see cref="CustomTileRenderer"/>, which knows nothing about probes or profiles): reads
/// every reading the tile uses, resolves each element's colour and content, and hands over a
/// finished <see cref="CustomTilePaint"/>.
///
/// <para>Everything that draws a custom tile goes through here — the pad's own repaint
/// (<c>DpLiveTileService</c>), the still snapshot the key dialog shows
/// (<c>DpDefaultIconRenderer</c>) and the studio's live preview — so the three cannot disagree
/// about what a tile looks like.</para>
/// </summary>
internal static class CustomActionTile
{
    /// <summary>Set while a PREVIEW inside K2 is being drawn (the studio, the key dialog): sources
    /// are then read even though K2 — not the game — is the window in front.
    ///
    /// <para>A flag rather than a parameter threaded through five call sites because the preview
    /// path goes through <c>DpDefaultIconRenderer</c>, which is shared with the hardware path and
    /// has no business growing a "this is only a preview" argument.</para></summary>
    [ThreadStatic]
    private static bool _previewMode;

    /// <summary>Runs <paramref name="body"/> with previews allowed to read a background window.
    /// Restores the previous state on the way out, so nested calls behave.</summary>
    public static T InPreview<T>(Func<T> body)
    {
        bool was = _previewMode;
        _previewMode = true;
        try { return body(); }
        finally { _previewMode = was; }
    }

    /// <summary>What a custom key shows right now: the tile's headline reading as text, and the
    /// fraction behind it. A source that cannot be read is a DASH — the tile says it does not know
    /// rather than inventing a zero.</summary>
    public static (string Text, double? Fraction) Value(string? actionValue)
    {
        var action = CustomGameStore.ActionById(CustomActionType.Parse(actionValue)?.Id);
        if (action is null) return ("—", null);

        var reading = HeadlineReading(action);
        if (reading is null) return ("—", null);

        var state = Read(reading);
        return (state.Text, state.Fraction);
    }

    /// <summary>The action's name, for the key list and the "Edit icon" caption box.</summary>
    public static string Caption(string? actionValue)
    {
        var parsed = CustomActionType.Parse(actionValue);
        if (parsed is null) return "";
        return CustomGameStore.ActionById(parsed.Value.Id)?.Name ?? parsed.Value.Label;
    }

    /// <summary>Renders the tile of a <c>dp_custom</c> key.</summary>
    public static bool Render(string? actionValue, string caption, int size, string outputPngPath)
    {
        var action = CustomGameStore.ActionById(CustomActionType.Parse(actionValue)?.Id);
        if (action is null)
        {
            // The action was deleted while a key still pointed at it. Draw a tile that says so with
            // whatever name the key carried, rather than going blank as if it had never been set.
            var lost = new CustomTilePaint
            {
                Pieces = new[]
                {
                    new TilePiece
                    {
                        Element = new TileElement { Kind = TileElementKind.Label, Anchor = TileAnchor.Center },
                        Text = caption.Length > 0 ? caption : "—",
                    },
                },
            };
            return CustomTileRenderer.TryRender(lost, size, outputPngPath);
        }
        return CustomTileRenderer.TryRender(BuildPaint(action, caption), size, outputPngPath);
    }

    /// <summary>What one reading says right now.</summary>
    /// <param name="Text">The reading as printed text.</param>
    /// <param name="Fraction">0..1, or null when there is no reading (or no scale).</param>
    /// <param name="On">Which half of the style applies — only meaningful when
    /// <paramref name="Known"/>.</param>
    /// <param name="IconPath">The active state's icon, for a multi-state reading.</param>
    /// <param name="Known">False when the source could not be read at all. NOT the same as "off":
    /// a tile with nothing to report wears the ON look with a dash in it, because the OFF look is a
    /// STATE the game reported, not a failure to reach the game.</param>
    /// <param name="Stamp">What the reading's PICTURE holds, when the picture is generated rather
    /// than picked (a mirror). Changes when the pixels do, and is what the pad's change detection
    /// watches — the file's name stays the same forever.</param>
    /// <param name="Number">The number behind <paramref name="Text"/>, when there is one — what a
    /// value element set to print the number (not the percentage) prints.</param>
    /// <param name="Max">The top of the scale <paramref name="Number"/> is read against, when it
    /// has one — the "107" of a value element printing "84/107".</param>
    internal readonly record struct Reading(string Text, double? Fraction, bool On, string? IconPath,
                                            bool Known = true, string Stamp = "", double? Number = null,
                                            double? Max = null);

    /// <summary>Reads one reading's source. Never throws: an unreadable source is
    /// <c>("—", null, false, Known: false)</c>, which every caller already draws correctly.</summary>
    internal static Reading Read(TileReading reading)
    {
        // A link answers a different question from a probe — "what does the game say" instead of
        // "what is on the screen" — so it forks here rather than inside every kind below, where
        // each branch would have to carry both.
        if (reading.Source.IsLink()) return ReadLink(reading);
        if (reading.Source == CustomSource.Telemachus) return ReadTelemachus(reading);

        switch (reading.ValueKind)
        {
            case CustomValueKind.MultiState:
            {
                // First state whose probe is lit wins. Order is the studio's order, which is the
                // user's: a state list is read top to bottom, and two states matching at once is
                // their calibration to fix, not ours to arbitrate.
                foreach (var st in reading.States)
                {
                    var probe = ScreenProbeStore.ById(st.ProbeId);
                    if (probe is null) continue;
                    var r = ScreenProbeReader.Read(probe, !_previewMode);
                    if (r.Valid && r.On) return new Reading(st.Name, null, true, st.IconPath);
                }
                return new Reading("—", null, false, null, Known: false);
            }

            case CustomValueKind.Mirror:
            {
                // Nothing is measured here: the rectangle is copied to a PNG and handed on as the
                // picture the tile wears, so a mirror rides the same path as an icon the user
                // picked — with the ON look, because a mirror that CAN be read is not an off state.
                var probe = ScreenProbeStore.ById(reading.ProbeId);
                if (probe is null) return new Reading("—", null, false, null, Known: false);

                string file = MirrorPath(probe.Id);
                // The stamp travels with the reading because the FILE NAME never changes: without
                // it the pad would compare "same path, same path" every tick and never send the
                // new picture.
                return ScreenProbeReader.TryCropToFile(probe, !_previewMode, file) is { } stamp
                    ? new Reading("", null, true, file, Stamp: stamp)
                    : new Reading("—", null, false, null, Known: false);
            }

            case CustomValueKind.Number:
            case CustomValueKind.Text:
            {
                // The KIND is what decides how the rectangle is read, not the probe's own mode: the
                // same rectangle can be calibrated as a colour fill and then reused as text, and
                // the reading must get what it asked for.
                var probe = ScreenProbeStore.ById(reading.ProbeId);
                if (probe is null) return new Reading("—", null, false, null, Known: false);

                var mode = reading.ValueKind == CustomValueKind.Number ? ProbeMode.Number : ProbeMode.Text;
                var r = ScreenProbeReader.Read(probe with { Mode = mode }, !_previewMode);
                if (!r.Valid) return new Reading("—", null, false, null, Known: false);

                if (reading.ValueKind == CustomValueKind.Text)
                    return new Reading(r.Text, null, true, null);

                double n = r.Number ?? 0;
                return new Reading(Label(reading, n, reading.FractionOf(n)), reading.FractionOf(n), true, null,
                                   Number: n, Max: reading.Max);
            }

            case CustomValueKind.OnOff:
            {
                var probe = ScreenProbeStore.ById(reading.ProbeId);
                var r = probe is null ? default : ScreenProbeReader.Read(probe, !_previewMode);
                if (!r.Valid) return new Reading("—", null, false, null, Known: false);

                // A probe calibrated for TEXT feeding an on/off reading: "there is something
                // written there" is the only honest reading of it.
                bool on = probe!.Mode is ProbeMode.Number or ProbeMode.Text ? r.Text.Length > 0 : r.On;
                return new Reading(Loc.Get(on ? "screen_probe_on" : "screen_probe_off"),
                                   on ? 1 : 0, on, null);
            }

            default:
            {
                var probe = ScreenProbeStore.ById(reading.ProbeId);
                var r = probe is null ? default : ScreenProbeReader.Read(probe, !_previewMode);
                if (!r.Valid) return new Reading("—", null, false, null, Known: false);

                // Same mismatch the other way round: the probe reads TEXT but the reading was left
                // on "range"/"value", which measures filled pixels — of which an OCR reading has
                // none. Show what was actually read instead of a confident 0%.
                if (probe!.Mode is ProbeMode.Number or ProbeMode.Text)
                    return r.Number is { } num
                        ? new Reading(Label(reading, num, reading.FractionOf(num)), reading.FractionOf(num), true, null,
                                      Number: num, Max: reading.Max)
                        : new Reading(r.Text, null, true, null);

                int pct = (int)Math.Round(r.Fraction * 100);
                return reading.ValueKind == CustomValueKind.Value
                    ? new Reading(pct.ToString(CultureInfo.InvariantCulture), null, true, null)
                    // Measured off the pixels, the scale IS 0..100.
                    : new Reading(Label(reading, pct, r.Fraction), r.Fraction, true, null, Number: pct, Max: 100);
            }
        }
    }

    /// <summary>Reads a reading whose source is a game link: one value out of the link's latest
    /// payload, interpreted the way the reading's kind asks for.
    ///
    /// <para>Two differences from a probe are deliberate. A link that has never answered is
    /// UNKNOWN, not off — the game is simply not running, and a tile that showed "0%" for that
    /// would be lying. And a value that will not read as a number is printed as the WORD the game
    /// published rather than replaced by a dash: a "reloading" where a number was expected is
    /// still the truth about the game.</para></summary>
    private static Reading ReadLink(TileReading reading)
    {
        var snapshot = GameLinkReader.Snapshot(reading.LinkId);
        if (!snapshot.Known) return new Reading("—", null, false, null, Known: false);
        return ReadValues(reading, path => snapshot.Values.TryGetValue(path, out string? v) ? v : null);
    }

    /// <summary>Reads a Telemachus reading: the entries it names go into the Telemachus request
    /// (<see cref="TelemachusClient.Want"/>), and their answers are interpreted exactly like a
    /// link's values — the same kinds, states and scale.</summary>
    private static Reading ReadTelemachus(TileReading reading)
    {
        var paths = reading.States.Select(s => s.ValuePath)
                                  .Append(reading.ValuePath ?? "").Append(reading.MinPath).Append(reading.MaxPath)
                                  .Where(p => p.Length > 0).Distinct().ToList();
        var status = TelemachusClient.Want(paths);
        if (!status.Alive) return new Reading("—", null, false, null, Known: false);
        return ReadValues(reading, path => TelemachusClient.Text(status.Get(path)));
    }

    /// <summary>The part of a value-source reading that does not care where the values came from.
    /// <paramref name="valueOf"/> answers a path's current text, or null when it has none.</summary>
    private static Reading ReadValues(TileReading reading, Func<string, string?> valueOf)
    {
        if (reading.ValueKind == CustomValueKind.MultiState)
        {
            // Same rule as the probe path: first state that holds wins, in the user's own order.
            foreach (var state in reading.States)
            {
                string path = state.ValuePath.Length > 0 ? state.ValuePath : reading.ValuePath;
                if (valueOf(path) is not { } candidate) continue;
                if (GameLinkValue.Matches(candidate, state.Match))
                    return new Reading(state.Name, null, true, state.IconPath);
            }
            return new Reading("—", null, false, null, Known: false);
        }

        // A mirror is a picture of some pixels; a link has none. Nothing to do but say so.
        if (reading.ValueKind == CustomValueKind.Mirror)
            return new Reading("—", null, false, null, Known: false);

        if (valueOf(reading.ValuePath ?? "") is not { } value)
            return new Reading("—", null, false, null, Known: false);

        switch (reading.ValueKind)
        {
            case CustomValueKind.Text:
                return new Reading(value, null, true, null);

            case CustomValueKind.OnOff:
            {
                bool on = GameLinkValue.IsOn(value);
                return new Reading(Loc.Get(on ? "screen_probe_on" : "screen_probe_off"),
                                   on ? 1 : 0, on, null);
            }

            default:
            {
                if (GameLinkValue.AsNumber(value) is not { } number)
                    return new Reading(value, null, true, null);

                double? End(string path, double? typed) =>
                    path.Length > 0 ? (valueOf(path) is { } v ? GameLinkValue.AsNumber(v) : null) : typed;
                double? max = End(reading.MaxPath, reading.Max);
                double? fraction = TileReading.Fraction(number, End(reading.MinPath, reading.Min), max);

                return new Reading(Label(reading, number, fraction), fraction, true, null, Number: number, Max: max);
            }
        }
    }

    /// <summary>Where a mirrored crop is written. One file per probe, rewritten on every read —
    /// and a SEPARATE file for previews, because the studio (UI thread) and the pad's repaint
    /// (its own thread) mirror the same probe at the same time and must not write over each
    /// other.</summary>
    private static string MirrorPath(string probeId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "mirror", probeId + (_previewMode ? "_p" : "") + ".png");

    /// <summary>The number as its kind prints it by default: a range as its percentage of the
    /// scale, anything else as itself — and a percentage only once there IS a scale. Without one
    /// there is nothing to take a percentage of, and the number is the honest answer.</summary>
    private static string Label(TileReading reading, double number, double? fraction) =>
        reading.ValueKind == CustomValueKind.Range && fraction is { } f ? Percent(f) : Format(number);

    private static string Percent(double fraction) => $"{(int)Math.Round(fraction * 100)}%";

    /// <summary>What a value element prints: the reading's own text, unless the element asks for the
    /// number, the percentage or number/top and the reading has one to give. A percentage needs a scale; a
    /// reading with no number behind it (a state, a word) always prints its text.</summary>
    private static string ValueText(TileElement element, Reading reading) => element.ValueLabel switch
    {
        CustomValueLabel.Number when reading.Number is { } n => Format(n),
        CustomValueLabel.Percent when reading.Number is not null && reading.Fraction is { } f => Percent(f),
        CustomValueLabel.ValueOfMax when reading.Number is { } n && reading.Max is { } m => $"{Format(n)}/{Format(m)}",
        CustomValueLabel.ValueOfMax when reading.Number is { } n => Format(n),
        _ => reading.Text,
    };

    /// <summary>A number the way the game prints it: a whole number stays whole, so "85" does not
    /// become "85.0" on the pad.</summary>
    private static string Format(double n) =>
        n == Math.Floor(n)
            ? ((long)n).ToString(CultureInfo.InvariantCulture)
            : n.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The reading a key's summary speaks for: the one the first VALUE element shows, or
    /// failing that the tile's first reading. A tile can carry several, but a key list has room
    /// for one.</summary>
    private static TileReading? HeadlineReading(CustomGameAction action)
    {
        var value = action.Elements.FirstOrDefault(e => e.Kind == TileElementKind.Value);
        return action.ReadingById(value?.ReadingId) ?? action.Readings.FirstOrDefault();
    }

    /// <summary>The finished picture for a tile, with <paramref name="caption"/> as the key's own
    /// label (what a label element shows when the user typed no words of its own).</summary>
    internal static CustomTilePaint BuildPaint(CustomGameAction action, string caption)
    {
        // Every reading, once: two elements showing the same reading must show the same value, and
        // reading a probe twice per repaint would cost two captures.
        var readings = action.Readings.ToDictionary(r => r.Id, Read);

        // ON unless the game actually said otherwise. A tile that cannot read its sources yet — a
        // brand new action, a game that isn't running — is not "off": it wears the profile's normal
        // look, which is also what makes a new action on a shipped profile come up in that game's
        // LIT frame instead of its dark one.
        bool anyKnown = readings.Values.Any(r => r.Known);
        bool on = !anyKnown || readings.Values.Any(r => r.Known && r.On);

        var style = CustomGameStore.EffectiveStyleFor(action.ProfileId).MergedWith(action.Style);
        var (bg, text, image) = style.Resolve(on);
        var textColour = CustomTileRenderer.ParseColor(text, Color.White);
        var accent = CustomGameStore.AccentFor(action.ProfileId);

        var pieces = new List<TilePiece>(action.Elements.Count);
        foreach (var element in action.Elements)
        {
            var reading = readings.TryGetValue(element.ReadingId ?? "", out var r) ? r : default;
            bool hasReading = !string.IsNullOrEmpty(element.ReadingId) && readings.ContainsKey(element.ReadingId);

            pieces.Add(new TilePiece
            {
                Element = element,
                Colour = CustomTileRenderer.ParseColor(
                    element.Colour,
                    element.Kind == TileElementKind.Indicator ? accent : textColour),
                Text = element.Kind switch
                {
                    TileElementKind.Value => hasReading ? ValueText(element, reading) : "—",
                    // A label with no words of its own says what the KEY is called, which is what a
                    // tile that was never given a label used to print.
                    TileElementKind.Label => element.Text.Length > 0 ? element.Text : caption,
                    _ => "",
                },
                Fraction = element.Kind == TileElementKind.Indicator && hasReading ? reading.Fraction : null,
                // A multi-state reading chooses the picture; an element with its own PNG and no
                // reading just wears it.
                IconPath = element.Kind == TileElementKind.Icon
                    ? (hasReading ? reading.IconPath ?? element.IconPath : element.IconPath)
                    : null,
            });
        }

        return new CustomTilePaint
        {
            Background = CustomTileRenderer.ParseColor(bg, Color.FromArgb(11, 11, 13)),
            BackgroundImagePath = image,
            Pieces = pieces,
        };
    }
}
