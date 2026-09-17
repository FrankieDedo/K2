// GameProfileModule.cs — the seam an out-of-tree game profile plugs into.
//
// Why this exists: a game profile changes far more often than K2 does — a new game, a bind that
// moved in a patch, a tile that reads better the other way round — and every one of those used to
// cost a full K2 release because the profiles were compiled into K2.App/K2.Core. A module is the
// same profile shipped as its own assembly, loaded at startup and updated on its own cadence.
//
// The MACHINE stays in the app: tile rendering, the reserved DisplayPad slot, the launch watcher,
// action execution, the studio. A module contributes CONTENT — what the profile is, what it looks
// like, and (through the interfaces it may also implement) what it knows about the running game.
//
// The contract is deliberately small and grows one member at a time, each added with the code that
// consumes it. An interface member nothing reads is a promise to a future author that nobody has
// checked.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace K2.Core;

/// <summary>What a module may ask of the app it was loaded into. Passed once to
/// <see cref="IGameProfileModule.Initialize"/>; a module holds onto it rather than reaching for
/// K2.App types it cannot see.</summary>
public interface IGameProfileHost
{
    /// <summary>Writes to K2's own log. The only diagnostics channel a module has — it runs inside
    /// K2's process but cannot see <c>App.WriteLog</c>.</summary>
    void Log(string message);

    /// <summary>Where the game is installed, by the same resolution the app uses for its icons
    /// (Steam library, then the uninstall registry), or null when it isn't found. For a module that
    /// has to read the game's own files — key binds, a config it must switch a setting on in.</summary>
    string? ResolveGameInstallDir(string exeName);
}

/// <summary>One loadable bundle of game profiles.
///
/// <para>Implemented once per module assembly, found by the loader through this interface and
/// nothing else — no attribute, no naming convention, no manifest to keep in sync.</para></summary>
public interface IGameProfileModule
{
    /// <summary>Which revision of this contract the module was built against. The host refuses a
    /// module claiming more than <see cref="GameProfileModules.HostApiVersion"/> — it would be
    /// expecting members this K2 does not have. The other direction always loads: an older module
    /// is a module that simply contributes less.</summary>
    int ApiVersion { get; }

    /// <summary>Stable identity of the bundle, e.g. "k2.gameprofiles". Used in logs and to tell
    /// two loaded modules apart; not shown to the user.</summary>
    string Id { get; }

    /// <summary>The profiles this module contributes, merged into the catalogue alongside the ones
    /// K2 ships and the ones the user built in the studio.</summary>
    IReadOnlyList<GameProfileDefinition> Definitions { get; }

    /// <summary>Presentation for those profiles — art, accent, command families. Joined to
    /// <see cref="Definitions"/> by the id string, exactly as in-tree profiles are. A profile that
    /// is just a page of shortcuts needs no entry here.</summary>
    IReadOnlyList<GameProfileSpec> Specs { get; }

    /// <summary>The module's own UI strings for <paramref name="culture"/> — the tile captions its
    /// definitions name as loc keys, and anything its command families are labelled with. The
    /// module owns the fallback: return the English table with the requested language layered over
    /// it, so a half-translated module reads in English rather than in <c>[brackets]</c>. Null when
    /// it ships no strings at all.</summary>
    IReadOnlyDictionary<string, string>? GetStrings(string culture);

    /// <summary>Called once, before anything is read off the module.</summary>
    void Initialize(IGameProfileHost host);
}

/// <summary>The modules loaded into this process, and the merge points the rest of K2 reads them
/// through.
///
/// <para>Lives in K2.Core because both sides need it and neither can see the other: K2.App fills
/// it at startup, and <see cref="GameProfileSpecs"/> — which K2.Core itself hosts, for the action
/// dialog — reads it.</para></summary>
public static class GameProfileModules
{
    /// <summary>The contract revision this build of K2 implements. Bump when a member is ADDED to
    /// the interfaces above; a module built against a higher number is refused.</summary>
    public const int HostApiVersion = 1;

    private static readonly List<(IGameProfileModule Module, string Directory)> _loaded = new();

    public static IReadOnlyList<IGameProfileModule> Loaded =>
        _loaded.Select(e => e.Module).ToList();

    /// <summary>Takes a module into the process. <paramref name="directory"/> is the folder its
    /// assembly was loaded from — where its art is looked for, so the module does not have to
    /// describe its own layout. Returns false — without throwing — when the module asks for a
    /// contract this K2 does not implement, so a mismatched module degrades to "no game profiles
    /// from it" rather than to an app that won't start.</summary>
    public static bool Register(IGameProfileModule module, string directory)
    {
        if (module.ApiVersion > HostApiVersion) return false;
        if (_loaded.Any(e => string.Equals(e.Module.Id, module.Id, StringComparison.Ordinal))) return false;
        _loaded.Add((module, directory));
        return true;
    }

    /// <summary>Every profile the loaded modules contribute, in load order.</summary>
    public static IEnumerable<GameProfileDefinition> Definitions =>
        _loaded.SelectMany(e => e.Module.Definitions);

    /// <summary>The spec a module supplies for a profile id, or null when no module owns it.</summary>
    public static GameProfileSpec? SpecById(string profileId) =>
        _loaded.SelectMany(e => e.Module.Specs)
               .FirstOrDefault(s => string.Equals(s.Id, profileId, StringComparison.Ordinal));

    /// <summary>Where to look for a module-owned profile's tile art: <c>Assets\GameProfiles</c>
    /// under the module's own folder, the same layout K2 uses for the art it ships. Null when no
    /// loaded module owns that profile.
    ///
    /// <para>Beside the assembly rather than inside it because the art is read by the GDI+ tile
    /// renderers, which want a file path — embedding it would mean unpacking it to a cache and
    /// then keeping that cache honest across module updates, to arrive at the same file.</para></summary>
    public static string? ArtRootFor(string profileId)
    {
        var owner = _loaded.FirstOrDefault(e => e.Module.Definitions.Any(
            d => string.Equals(d.Id, profileId, StringComparison.Ordinal)));

        return owner.Directory is null
            ? null
            : Path.Combine(owner.Directory, "Assets", "GameProfiles");
    }
}
