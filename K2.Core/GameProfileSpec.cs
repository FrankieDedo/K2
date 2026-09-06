using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace K2.Core;

/// <summary>
/// Everything about a game profile that is <b>presentation and integration</b> rather than key
/// mapping: the art its tiles are drawn on, its accent colour, and the commands its action picker
/// offers. The key mapping itself — which command sits in which slot — lives in
/// <c>GameProfileCatalog.Definition</c> over in K2.App.
///
/// <para>
/// <b>Why it is a record and not a switch.</b> Every one of these used to be an
/// <c>if (profileId == "elite_dangerous")</c> scattered across four files (the theme, the tile
/// styling rule, the action picker's family list, the live tile painter). With one game that reads
/// fine; with the second it becomes four places to remember and three of them are easy to miss.
/// Adding a game is now filling in one entry of <see cref="GameProfileSpecs.All"/> — and a game
/// that has nothing special to say simply has no entry, which is the plain-shortcuts default.
/// </para>
///
/// <para>
/// This lives in K2.Core, not K2.App, because both sides need it: the App draws the tiles and the
/// Core hosts <see cref="ButtonActionDialog"/>, whose picker shows the game's own commands.
/// </para>
/// </summary>
/// <param name="Id">Matches <c>GameProfileCatalog.Definition.Id</c>. The two are joined by this
/// string and nothing else, so they must agree exactly.</param>
/// <param name="ArtFolder">Sub-folder of <c>Assets\GameProfiles\</c> holding the tile backgrounds,
/// or empty for a game that ships none (its tiles then use K2's ordinary action icons).</param>
/// <param name="ArtPrefix">File-name prefix inside <paramref name="ArtFolder"/>. The art is looked
/// up as <c>{prefix}_{colour}_{on|off}.png</c> — e.g. <c>ed_orange_on.png</c> — so a game supplies
/// its whole tile set by dropping six files in and naming them.</param>
/// <param name="Accent">Resting accent: the caption colour of every generated tile, and the hue
/// the page reads as. Usually the game's own HUD colour.</param>
/// <param name="StyleAllTiles">Rule of the house: when true EVERY mapped key of the profile gets
/// the game's art and caption instead of an action glyph, including keys the user added by hand,
/// so the page reads as one panel rather than a grid with holes in it. An UNMAPPED slot is never
/// styled — a dark key is honest, a lit frame around nothing is not.</param>
/// <param name="CommandFamilies">What the action picker offers under the game's own card, instead
/// of the generic action-type grid. Empty = the game contributes nothing and the picker falls
/// straight through to Input and the ordinary categories.</param>
/// <param name="RestingArtColor">The colour word in the art's file names for a tile with nothing
/// special to say — <c>{prefix}_{RestingArtColor}_{on|off}.png</c>. Elite's set is keyed on
/// "orange" and treats green/red as the exceptions; a game whose art is another colour simply
/// says so here instead of shipping blue pictures named orange.</param>
public sealed record GameProfileSpec(
    string Id,
    string ArtFolder,
    string ArtPrefix,
    Color Accent,
    bool StyleAllTiles,
    (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] CommandFamilies,
    string RestingArtColor = "orange");

/// <summary>The per-game specs K2 ships. One entry per game that has art, colours or commands of
/// its own; see <see cref="GameProfileSpec"/> for what each field buys.</summary>
public static class GameProfileSpecs
{
    /// <summary>Elite's catalogue id, spelled once. The <c>dp_edstatus</c> live-tile painter is
    /// unavoidably Elite-specific — it decodes that game's status bitfield — so it refers to the
    /// game by this constant rather than repeating the literal.</summary>
    public const string EliteId = "elite_dangerous";

    /// <summary>The game's own HUD orange.</summary>
    public static readonly Color EliteOrange = Color.FromArgb(255, 122, 20);

    /// <summary>Zero Company's catalogue id, spelled once — same role as <see cref="EliteId"/>:
    /// the <c>dp_zcstatus</c> painter is unavoidably specific to this game's tactical state.</summary>
    public const string ZeroCompanyId = "zero_company";

    /// <summary>The blue of the game's tactical overlay.</summary>
    public static readonly Color ZeroCompanyBlue = Color.FromArgb(94, 190, 235);

    /// <summary>Deadside's catalogue id. No live-tile painter refers to it — the game reports
    /// nothing back — but the id is spelled once here for the same reason as the others.</summary>
    public const string DeadsideId = "deadside";

    /// <summary>The sand/khaki the game's HUD and menus are lettered in.</summary>
    public static readonly Color DeadsideSand = Color.FromArgb(198, 176, 122);

    public static IReadOnlyList<GameProfileSpec> All { get; } = new[]
    {
        new GameProfileSpec(
            Id: EliteId,
            ArtFolder: "EliteDangerous",
            ArtPrefix: "ed",
            Accent: EliteOrange,
            StyleAllTiles: true,
            CommandFamilies: ActionTypeHelper.EliteCommandFamilies),

        new GameProfileSpec(
            Id: ZeroCompanyId,
            ArtFolder: "ZeroCompany",
            ArtPrefix: "zc",
            Accent: ZeroCompanyBlue,
            StyleAllTiles: true,
            CommandFamilies: ActionTypeHelper.ZeroCompanyCommandFamilies,
            RestingArtColor: "blue"),

        // Art only, no live state: Deadside ships a resting ("orange") pair like the others, and
        // has no state variants because it has no state to report.
        new GameProfileSpec(
            Id: DeadsideId,
            ArtFolder: "Deadside",
            ArtPrefix: "ds",
            Accent: DeadsideSand,
            StyleAllTiles: true,
            CommandFamilies: ActionTypeHelper.DeadsideCommandFamilies),
    };

    /// <summary>The spec for a profile, or null when the game ships none — which is the normal
    /// case for a profile that is just a page of keyboard shortcuts.</summary>
    public static GameProfileSpec? ById(string? profileId) =>
        profileId is null ? null
        : All.FirstOrDefault(s => string.Equals(s.Id, profileId, StringComparison.Ordinal));

    /// <summary>The commands a profile's action picker shows beside Input. Empty for a game with
    /// no integration of its own.</summary>
    public static (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] FamiliesFor(
        string? profileId) =>
        ById(profileId)?.CommandFamilies
        ?? Array.Empty<(string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)>();
}
