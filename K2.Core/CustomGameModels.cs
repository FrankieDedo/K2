using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>Where a reading gets its value from. Adding a source is a new member here and a new
/// reader in <c>CustomActionTile</c> — never a rewrite of everything that stores an action.</summary>
public enum CustomSource
{
    /// <summary>A rectangle of some program's window, read by a screen probe
    /// (<c>ScreenProbeStore</c> in K2.App). The reading names probes by id.</summary>
    Screen,

    /// <summary>A value the GAME ITSELF publishes, with nothing installed, fetched from a game link
    /// (<c>GameLinkStore</c> in K2.App): the reading names a link by id and a value inside it by
    /// path. This is the honest source wherever it exists — a number read from the game beats the
    /// same number guessed from its pixels — but it exists only for the games that expose one,
    /// which is why the screen probe stays the fallback rather than the legacy.</summary>
    Link,

    /// <summary>The same thing, published by a MOD installed in the game (Ficsit Remote Monitoring
    /// in Satisfactory, K2's own Unity Link). Telemachus is deliberately not one of them: Kerbal
    /// Space Program has a built-in profile that talks to it directly (<c>KspTelemachus</c>).
    ///
    /// <para>A separate source rather than a flag on the link because it is a different ANSWER to
    /// "where does this come from", and the user picks it before there is a link to flag: choosing
    /// it is what says which links are worth listing and what to do when none of them answers —
    /// point at the mod's page instead of saying "start the game".</para></summary>
    ModLink,

    /// <summary>A Telemachus entry in Kerbal Space Program (<c>v.altitude</c>, <c>o.ApA</c>). No
    /// link to set up: the address is the mod's own, the reading stores just the entry in
    /// <see cref="TileReading.ValuePath"/>, and K2 puts it in the request itself. Offered only for
    /// the Kerbal Space Program profile's actions — the one place Telemachus lives.
    ///
    /// <para>Not a link (<see cref="CustomSourceExtensions.IsLink"/> is false): there is no link id
    /// to validate, export or poll through <c>GameLinkReader</c>.</para></summary>
    Telemachus,
}

/// <summary>Shorthands for the two sources that are links, so no caller has to remember that there
/// are two of them.</summary>
public static class CustomSourceExtensions
{
    public static bool IsLink(this CustomSource source) =>
        source is CustomSource.Link or CustomSource.ModLink;

    /// <summary>Which kind of link a source asks for. Meaningless for
    /// <see cref="CustomSource.Screen"/>, which has none.</summary>
    public static bool WantsMod(this CustomSource source) => source == CustomSource.ModLink;

    /// <summary>True for every source whose value is a named entry rather than a rectangle — the
    /// two link kinds and Telemachus. They share the value box, the states' "value + text to match"
    /// and the scale; only where the value comes from differs.</summary>
    public static bool IsValueSource(this CustomSource source) =>
        source.IsLink() || source == CustomSource.Telemachus;
}

/// <summary>What KIND of answer a reading gives — the thing that decides how it is measured and
/// how it can be shown.</summary>
public enum CustomValueKind
{
    /// <summary>A number over a full scale, 0..100%.</summary>
    Range,

    /// <summary>A number with no meaningful full scale. Printed as text; an indicator on it would
    /// be a scale that does not exist.</summary>
    Value,

    /// <summary>Two states, on and off.</summary>
    OnOff,

    /// <summary>One of several named states, each with its own probe and its own icon.</summary>
    MultiState,

    /// <summary>A number READ off the screen as text (OCR) rather than measured from pixels — the
    /// "85" a health bar prints beside itself. With <see cref="TileReading.Min"/> and
    /// <see cref="TileReading.Max"/> filled in it also drives an indicator, which is how "health,
    /// 0 to 100" becomes a real gauge instead of a bare number.</summary>
    Number,

    /// <summary>A word read off the screen (OCR): the current weapon, the mode, the zone.</summary>
    Text,

    /// <summary>The rectangle ITSELF, as a picture: the pixels the probe is aimed at, copied onto
    /// the tile. Nothing is measured — a mirror answers "what does that corner of the screen look
    /// like right now", which is the honest reading for a minimap, a portrait, an icon the game
    /// swaps out, anything K2 cannot name. Shown by a picture element pointed at it.</summary>
    Mirror,
}

/// <summary>How a VALUE element prints a number that has a scale.</summary>
public enum CustomValueLabel
{
    /// <summary>What the reading's kind has always printed: a range its percentage, a number
    /// itself. What every element saved before the choice existed reads as.</summary>
    Auto,

    /// <summary>The number the source gives — "84" of a health going to 107.</summary>
    Number,

    /// <summary>Where that number sits on its scale — "78%". Without a scale there is nothing to
    /// take a percentage of, and the number is printed instead.</summary>
    Percent,

    /// <summary>The number over the top of its scale — "84/107". Without a top the number is
    /// printed on its own.</summary>
    ValueOfMax,
}

/// <summary>How a <see cref="CustomValueKind.MultiState"/> reading tells its states apart. Both end
/// up as one probe per state — the difference is only what the studio keeps in sync while the user
/// edits, which is exactly the difference the user thinks in.</summary>
public enum CustomMultiMode
{
    /// <summary>One rectangle, one reference colour per state: an indicator that CHANGES COLOUR.
    /// The studio copies the first state's rectangle onto every other state.</summary>
    Colors,

    /// <summary>One rectangle per state: separate icons/slots that light up. Each state keeps its
    /// own geometry.</summary>
    Rects,
}

/// <summary>How an indicator is drawn.</summary>
public enum CustomIndicatorShape
{
    None,
    /// <summary>A horizontal bar filling left to right.</summary>
    LinearHorizontal,
    /// <summary>A vertical bar filling bottom to top.</summary>
    LinearVertical,
    /// <summary>A 270° ring, the shape Zero Company's health reads as.</summary>
    Circular,
}

/// <summary>The nine snap points of a tile. On a 102px key anything finer is invisible, and — more
/// to the point — two tiles of one profile can be lined up by eye only when their parts land on
/// the same small set of positions.</summary>
public enum TileAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

/// <summary>What a piece of a tile IS. The tile is a list of these, in any number and any mix:
/// two indicators and three labels is as valid as one of each.</summary>
public enum TileElementKind
{
    /// <summary>A picture: a PNG the user picked, or the icon of whichever state a multi-state
    /// reading is currently in.</summary>
    Icon,

    /// <summary>A bar or a ring, filled from a reading.</summary>
    Indicator,

    /// <summary>A reading, printed.</summary>
    Value,

    /// <summary>Words: text the user typed, or the key's own caption when they typed none.</summary>
    Label,
}

/// <summary>How much room one piece takes, as a fraction of the tile's side (so it means the same
/// at 102px on the pad and 204px in the studio's preview). Null means "the size the renderer
/// picks", which is what everything starts as.</summary>
public sealed record TileBox(double Width, double Height);

/// <summary>
/// One named state of a <see cref="CustomValueKind.MultiState"/> reading: the probe that detects
/// it and the icon a picture element wears while it holds.
/// </summary>
public sealed record CustomActionState
{
    public string Name { get; init; } = "";

    /// <summary>Screen probe that reads this state. The state is active when its probe reads ON.
    /// Unused when the reading's source is <see cref="CustomSource.Link"/>.</summary>
    public string ProbeId { get; init; } = "";

    /// <summary>For a <see cref="CustomSource.Link"/> reading: the value inside the link this
    /// state watches. Empty means "the reading's own path", which is the usual shape — several
    /// states reading ONE value and telling themselves apart by <see cref="Match"/>.</summary>
    public string ValuePath { get; init; } = "";

    /// <summary>For a <see cref="CustomSource.Link"/> reading: the text the value must equal for
    /// this state to hold (compared case-insensitively, and numerically when both sides are
    /// numbers, so "3" matches "3.0"). Empty means "any value that reads as true", which is how a
    /// state watching its own boolean works.</summary>
    public string Match { get; init; } = "";

    /// <summary>PNG shown for this state, or null to keep the element's own picture.</summary>
    public string? IconPath { get; init; }
}

/// <summary>
/// One thing a tile knows how to read: a source, a way of interpreting it, and a name to pick it by.
///
/// <para><b>Readings are separate from the pieces that show them</b> because they are not the same
/// question. "What is the health number" is asked once; "print it top-left in orange AND fill a
/// ring with it" are two pieces pointing at that one answer. It is also what lets a single tile
/// carry health and hunger at once.</para>
/// </summary>
public sealed record TileReading
{
    public string Id { get; init; } = "";

    /// <summary>What the user calls it — the name the element pickers list.</summary>
    public string Name { get; init; } = "";

    public CustomSource Source { get; init; } = CustomSource.Screen;
    public CustomValueKind ValueKind { get; init; } = CustomValueKind.Range;

    /// <summary>The probe for every kind except <see cref="CustomValueKind.MultiState"/>, which
    /// uses one probe per state instead. Only meaningful for <see cref="CustomSource.Screen"/>.</summary>
    public string ProbeId { get; init; } = "";

    /// <summary>For <see cref="CustomSource.Link"/>: which link the value comes from.</summary>
    public string LinkId { get; init; } = "";

    /// <summary>For <see cref="CustomSource.Link"/>: which value inside the link, as the dotted
    /// path the link's own reader flattens its payload to (<c>player.health</c>,
    /// <c>squad[0].name</c>). Kept as TEXT rather than anything cleverer because it is what the
    /// game publishes, and the game is free to change shape between versions: a path that stops
    /// resolving is a reading that says "—", not a crash.</summary>
    public string ValuePath { get; init; } = "";

    public CustomMultiMode MultiMode { get; init; } = CustomMultiMode.Colors;

    public IReadOnlyList<CustomActionState> States { get; init; } = Array.Empty<CustomActionState>();

    /// <summary>Bottom and top of the scale for a <see cref="CustomValueKind.Number"/> reading —
    /// the values at which an indicator reads empty and full. Null means the number has no scale
    /// and is printed on its own.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }

    /// <summary>For a value source (link, Telemachus): an end of the scale READ from the game
    /// instead of typed — <c>Stats.Health.Max</c> beside <c>Stats.Health.Value</c>, because the
    /// top of a health bar moves with levels and buffs. Non-empty wins over <see cref="Min"/> /
    /// <see cref="Max"/>; a path that does not resolve to a number leaves that end unset, so the
    /// number is printed with no indicator rather than against a made-up scale.</summary>
    public string MinPath { get; init; } = "";
    public string MaxPath { get; init; } = "";

    /// <summary>Where a number sits on its own scale, 0..1, or null when there is no scale (or the
    /// two ends are the same, which would be a division by nothing).</summary>
    public double? FractionOf(double value) => Fraction(value, Min, Max);

    /// <summary><see cref="FractionOf"/> against ends resolved elsewhere (see <see cref="MinPath"/>).</summary>
    public static double? Fraction(double value, double? min, double? max) =>
        min is { } lo && max is { } hi && Math.Abs(hi - lo) > double.Epsilon
            ? Math.Clamp((value - lo) / (hi - lo), 0, 1)
            : null;
}

/// <summary>
/// One piece of a tile: what it is, what it shows, how it looks and where it sits.
///
/// <para>Everything about placement lives here rather than in a table beside the tile, because a
/// tile is a LIST of these and two pieces of the same kind must be able to sit in different places
/// with different sizes.</para>
/// </summary>
public sealed record TileElement
{
    public string Id { get; init; } = "";

    public TileElementKind Kind { get; init; } = TileElementKind.Label;

    /// <summary>The reading this piece shows — required for a value or an indicator, optional for
    /// a picture (a multi-state reading then chooses which icon it wears). Empty for a label,
    /// which shows words rather than a measurement.</summary>
    public string ReadingId { get; init; } = "";

    /// <summary>A label's words. Empty means "the key's own caption", so a label added to a tile
    /// says what the key is called until the user overrides it.</summary>
    public string Text { get; init; } = "";

    /// <summary>A picture's PNG.</summary>
    public string? IconPath { get; init; }

    /// <summary>Colour of this piece as <c>#RRGGBB</c> — the ink for text, the fill for an
    /// indicator. Null inherits: the profile's text colour, or its accent for an indicator.</summary>
    public string? Colour { get; init; }

    public CustomIndicatorShape Shape { get; init; } = CustomIndicatorShape.LinearHorizontal;

    /// <summary>For a value element: print the reading's number or its percentage of the scale.
    /// On the element, not the reading, because the same reading can be shown twice — "84" in one
    /// corner and "78%" in the other.</summary>
    public CustomValueLabel ValueLabel { get; init; } = CustomValueLabel.Auto;

    /// <summary>Font family for a text piece, or null for K2's own tile face.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Point size for a text piece, or 0 to shrink it to fit its box.</summary>
    public double FontSize { get; init; }

    // ── placement ──

    /// <summary>Which of the nine points the piece sits on, when <see cref="Snap"/> is on.</summary>
    public TileAnchor Anchor { get; init; } = TileAnchor.Center;

    /// <summary>True to keep the piece on the nine snap points. Off lets it be dropped anywhere,
    /// at <see cref="X"/>/<see cref="Y"/>.</summary>
    public bool Snap { get; init; } = true;

    /// <summary>Centre of the piece as a fraction of the tile, used when <see cref="Snap"/> is off.</summary>
    public double X { get; init; } = 0.5;
    public double Y { get; init; } = 0.5;

    /// <summary>How far a snapped piece is kept from the tile's edge, as a fraction of the side.</summary>
    public double Margin { get; init; } = 0.04;

    /// <summary>Size the user dragged, or null for the one the renderer picks.</summary>
    public TileBox? Box { get; init; }
}

/// <summary>
/// The colours a tile is drawn with, in an ON and an OFF variant.
///
/// <para><b>OFF is optional on purpose.</b> A profile whose tiles look the same lit or dark is the
/// common case, and asking for six colours to get one look is how a colour scheme ends up
/// inconsistent. A null OFF field means "use the ON one" — see <see cref="Resolve"/>.</para>
///
/// <para>Colours are hex strings (<c>#RRGGBB</c>) rather than <see cref="System.Drawing.Color"/> so
/// the record round-trips through JSON as itself, and stays readable in the file.</para>
/// </summary>
public sealed record CustomTileStyle
{
    public string BackgroundOn { get; init; } = "#0B0B0D";
    public string TextOn { get; init; } = "#FFFFFF";

    /// <summary>Null = same as <see cref="BackgroundOn"/>.</summary>
    public string? BackgroundOff { get; init; }

    /// <summary>Null = same as <see cref="TextOn"/>.</summary>
    public string? TextOff { get; init; }

    /// <summary>Optional PNG drawn behind everything, in place of the flat colour. It is the
    /// tile's BACKGROUND, not one of its elements: it never moves, never resizes and is always at
    /// the back, so it has no business in the element list.</summary>
    public string? BackgroundImageOn { get; init; }

    /// <summary>Null = same as <see cref="BackgroundImageOn"/>.</summary>
    public string? BackgroundImageOff { get; init; }

    /// <summary>The three values that actually apply for a given state.</summary>
    public (string Background, string Text, string? Image) Resolve(bool on) =>
        on ? (BackgroundOn, TextOn, BackgroundImageOn)
           : (BackgroundOff ?? BackgroundOn, TextOff ?? TextOn, BackgroundImageOff ?? BackgroundImageOn);

    /// <summary>This style with <paramref name="over"/>'s non-null fields laid on top — how an
    /// action inherits its profile's default and then departs from it field by field.</summary>
    public CustomTileStyle MergedWith(CustomTileOverride? over) =>
        over is null ? this : new CustomTileStyle
        {
            BackgroundOn = over.BackgroundOn ?? BackgroundOn,
            TextOn = over.TextOn ?? TextOn,
            BackgroundOff = over.BackgroundOff ?? BackgroundOff,
            TextOff = over.TextOff ?? TextOff,
            BackgroundImageOn = over.BackgroundImageOn ?? BackgroundImageOn,
            BackgroundImageOff = over.BackgroundImageOff ?? BackgroundImageOff,
        };
}

/// <summary>An action's departures from its profile's default style. Every field is nullable and
/// null means "keep inheriting": an action edited today keeps following the profile's colours for
/// everything it did not touch, including changes made to the profile later.</summary>
public sealed record CustomTileOverride
{
    public string? BackgroundOn { get; init; }
    public string? TextOn { get; init; }
    public string? BackgroundOff { get; init; }
    public string? TextOff { get; init; }
    public string? BackgroundImageOn { get; init; }
    public string? BackgroundImageOff { get; init; }

    /// <summary>True when nothing is overridden — the studio stores null instead of an empty
    /// override so "inherits" is visible in the file rather than implied.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        BackgroundOn is null && TextOn is null && BackgroundOff is null &&
        TextOff is null && BackgroundImageOn is null && BackgroundImageOff is null;
}

/// <summary>
/// One user-authored action — a whole DisplayPad tile: what it reads, what it draws, and where it
/// belongs. This is the thing the studio creates and a key binds to (<c>dp_custom</c>, value
/// <c>"&lt;action-id&gt;|&lt;name&gt;"</c> — the name rides along so K2.Core can label a key
/// without reaching into the store, exactly like <c>dp_screen</c> does).
///
/// <para><b>Three layers, deliberately.</b> The ACTION is identity (name, profile, family) and the
/// tile's background. The READINGS are what it knows how to measure. The ELEMENTS are what is
/// drawn, each pointing at a reading when it shows one. Keeping them apart is what lets one tile
/// carry two readings, or show one reading twice — as a number and as a ring.</para>
/// </summary>
public sealed record CustomGameAction
{
    /// <summary>Stable identity, stored in the key's action value. Never reused.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The profile this action belongs to — also where it shows up in the key
    /// configuration's action browser. Empty means it belongs to no profile: it is kept in the
    /// studio to be copied onto one later, and is offered nowhere.</summary>
    public string ProfileId { get; init; } = "";

    /// <summary>Which family of the profile's action browser this action is filed under: the loc
    /// key of one of the game's own families, or empty for the studio's "Generic" family.</summary>
    public string Category { get; init; } = "";

    /// <summary>Style departures from the owning profile's default; null = inherits everything.</summary>
    public CustomTileOverride? Style { get; init; }

    /// <summary>A keystroke the key SENDS when it is pressed, in the same human syntax as the Keys
    /// action ("Ctrl + Shift + A"), or "" for a tile that only shows its reading.
    ///
    /// <para><b>Why it lives here and not as a Keys action.</b> A key carries ONE action, and for
    /// these tiles that action is the reading — that is what draws the picture. The keystroke is a
    /// property OF the reading tile, so every key bound to the tile sends the same combination and
    /// the tile stays what it is: a reading that also presses something, not a shortcut that
    /// happens to draw.</para></summary>
    public string Keys { get; init; } = "";

    /// <summary>What the tile can measure.</summary>
    public IReadOnlyList<TileReading> Readings { get; init; } = Array.Empty<TileReading>();

    /// <summary>What the tile draws, in order — the last one is on top.</summary>
    public IReadOnlyList<TileElement> Elements { get; init; } = Array.Empty<TileElement>();

    /// <summary>The reading with this id, or null. Elements name their reading by id so a reading
    /// can be renamed, or re-calibrated, without touching anything that shows it.</summary>
    public TileReading? ReadingById(string? id) =>
        string.IsNullOrEmpty(id) ? null : Readings.FirstOrDefault(r => r.Id == id);

    /// <summary>The value stored on a key bound to this action. Not persisted: it is derived from
    /// the two fields above, and a stored copy would be one more thing that can go stale.</summary>
    [JsonIgnore]
    public string KeyValue => $"{Id}|{Name}";
}

/// <summary>The action type a key bound to a custom action carries, and the parsing of its value.
/// Lives here rather than in the store because K2.Core draws key lists and the action dialog and
/// must be able to name one without reaching into K2.App.</summary>
public static class CustomActionType
{
    public const string Tag = "dp_custom";

    /// <summary>Answers "what does this custom action press", by action id — "" for none. Set by
    /// K2.App at startup (<c>MainWindow.GameProfiles</c>), because the definitions live in its
    /// store; the engine and the key lists here only ever need the answer. Null before it is set,
    /// which reads as "presses nothing" rather than throwing in a key handler.</summary>
    public static Func<string, string>? KeysProvider;

    /// <summary>The keystroke a <c>dp_custom</c> key VALUE sends, "" when there is none.</summary>
    public static string KeysOf(string? actionValue) =>
        Parse(actionValue) is { } parsed ? KeysProvider?.Invoke(parsed.Id) ?? "" : "";

    /// <summary>Splits a <c>dp_custom</c> value — <c>"&lt;action-id&gt;|&lt;name&gt;"</c>. Null for
    /// an empty value (a key whose action was never chosen). The name is carried in the value so a
    /// key whose action was deleted still says WHICH action it used to show.</summary>
    public static (string Id, string Label)? Parse(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        int bar = v.IndexOf('|');
        return bar < 0 ? (v, "") : (v[..bar], v[(bar + 1)..]);
    }
}

/// <summary>A profile the user built: the game it follows, the colours its tiles default to, and
/// its pages. Published into <c>GameProfileCatalog.All</c> alongside the shipped profiles, so
/// activation, the device slot and the config popup all work on it unchanged.</summary>
public sealed record CustomGameProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Process the profile follows — no extension, no path, the spelling the launch
    /// watcher matches on.</summary>
    public string ExeName { get; init; } = "";

    /// <summary>Steam app id, only so the icon resolver can ask Steam where the game lives.</summary>
    public int? SteamAppId { get; init; }

    /// <summary>Accent as <c>#RRGGBB</c> — the hue the page reads as, used for indicators and
    /// anything else that asks the profile for a colour.</summary>
    public string Accent { get; init; } = "#FF7A14";

    /// <summary>What every action of this profile starts from and keeps inheriting.</summary>
    public CustomTileStyle DefaultStyle { get; init; } = new();

    public IReadOnlyList<CustomGamePage> Pages { get; init; } = Array.Empty<CustomGamePage>();

    /// <summary>Prefix every user-made profile id carries. Nothing but the prefix separates a
    /// custom profile from a shipped one downstream, and it must never collide with a catalogue
    /// id — a custom profile that shadowed "elite_dangerous" would quietly replace it.</summary>
    public const string IdPrefix = "custom_";

    public static bool IsCustomId(string? id) =>
        id is not null && id.StartsWith(IdPrefix, StringComparison.Ordinal);
}

/// <summary>One key of a custom profile's page. Same shape as the shipped catalogue's tile, and
/// converted into one when the profile is published — a custom action is just the action type
/// <c>dp_custom</c>, so nothing downstream needs to know the tile was user-made.</summary>
public sealed record CustomGameTile(string ActionType, string ActionValue, string Caption);

/// <summary>Twelve tiles — one DisplayPad page.</summary>
public sealed record CustomGamePage
{
    public string Name { get; init; } = "";
    public IReadOnlyList<CustomGameTile> Tiles { get; init; } = Array.Empty<CustomGameTile>();
}
