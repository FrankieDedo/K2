// GameProfileCatalogTypes.cs — the shape of a game profile's key mapping.
//
// These three records used to be nested in K2.App's GameProfileCatalog. They live in K2.Core
// because a game profile is no longer necessarily compiled into K2: an external game-profile
// module (its own assembly, updated on its own release cadence) has to be able to declare a
// profile without referencing the app, and K2.Core is the only assembly both sides share.
//
// What each profile SAYS about itself beyond the mapping — art, accent, the commands its picker
// offers — is GameProfileSpec, next door. The two are joined by the Id string and nothing else.

using System.Collections.Generic;

namespace K2.Core;

/// <summary>One key of a game profile page. Icons are NOT shipped as images: the action
/// itself is enough for K2's own default-icon renderer to draw the tile (and for
/// <c>dp_edstatus</c> keys that renderer already draws the cockpit-style annunciator), so a
/// profile stays a few lines of data instead of a folder of PNGs.</summary>
/// <param name="Caption">A LOCALISATION KEY for every tile a catalogue ships
/// (<paramref name="CaptionIsLocKey"/> true), so a curated profile reads in the user's language
/// instead of whatever language it was authored in. A tile the user edited stores their literal
/// text with the flag false — their wording is never sent through Loc.</param>
/// <param name="FontSize">The caption's size in pixels as picked in the key's "Edit icon" dialog,
/// 0 = the renderer's own shrink-to-fit default. Stored per tile because a game page's label IS
/// the tile (see <c>KeyIconSpec.TextOnly</c>): one long legend among short ones is the case the
/// slider exists for.</param>
public sealed record GameProfileTile(string ActionType, string ActionValue, string Caption,
                                     bool CaptionIsLocKey = true, double FontSize = 0);

/// <summary>Twelve tiles — one DisplayPad page. A profile can have several: the config
/// window pages through them, and a profile that needs more than 12 keys navigates with
/// arrow keys or sub-pages built out of these same slots.</summary>
public sealed record GameProfilePage(string Name, IReadOnlyList<GameProfileTile> Tiles);

/// <summary>A game profile's identity and key mapping.</summary>
/// <param name="ExeName">The process the profile follows — no extension, no path, because that is
/// what the launch watcher matches on. Verified against the real installs rather than guessed.</param>
/// <param name="SteamAppId">Only so the icon resolver can ask Steam where the game lives
/// (see <c>GameExeResolver</c>); null for a game that isn't a Steam install.</param>
/// <param name="WindowTitlePrefix">For a game that runs inside a generic host process: the
/// process only counts as the game while it has a window titled with this prefix. Minecraft: Java
/// Edition is <c>javaw</c>, and so is every other Java program on the machine.</param>
/// <param name="IconExePaths">Where the game's OWN executable usually lives, tried before any
/// detection — for the same kind of game, whose running process would hand over the host's icon
/// (a Java cup) instead of the game's. Environment variables are expanded.</param>
public sealed record GameProfileDefinition(string Id, string Name, string ExeName, int? SteamAppId,
                                           IReadOnlyList<GameProfilePage> Pages,
                                           string? WindowTitlePrefix = null,
                                           IReadOnlyList<string>? IconExePaths = null);
