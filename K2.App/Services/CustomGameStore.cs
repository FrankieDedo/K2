using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// The profiles and actions the user built in the game studio, persisted as one JSON file next to
/// K2's log.
///
/// <para><b>Why its own file and not <c>DisplayPadStore</c>.</b> These are not per-device
/// settings: one custom action feeds keys on any pad, and the profiles are published into
/// <see cref="GameProfileCatalog"/>, which is read by static code with no store instance — the
/// same reason <see cref="ScreenProbeStore"/> sits beside it. One file, loaded once, rewritten
/// whole on every change; a user has a handful of profiles, so nothing here needs to be cleverer
/// than that.</para>
///
/// <para><b>What is NOT here.</b> Whether a profile is enabled, which pad it targets and the
/// per-key tweaks the user makes in the config popup all stay in <c>DisplayPadStore</c> under
/// <c>gameprofile.{id}.*</c>, exactly like a shipped profile's. This file is the DEFINITION; the
/// state around it belongs where every other profile's state already lives.</para>
/// </summary>
internal static class CustomGameStore
{
    /// <summary>Fired after anything here changes, so the game-profile card list and any open
    /// action browser rebuild instead of showing the state before the edit.</summary>
    public static event Action? Changed;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App");

    private static readonly string FilePath = Path.Combine(Dir, "customgames.json");

    /// <summary>Where imported and picked images are copied. A studio profile must keep working
    /// when the PNG the user picked is moved or deleted from wherever they had it, so every
    /// picture is copied in here and referenced from this folder.</summary>
    public static string AssetsDir { get; } = Path.Combine(Dir, "customgames");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The file's shape. A record with two lists rather than two files: a profile and its
    /// actions are edited together and must never be half-saved.</summary>
    private sealed record Book
    {
        public List<CustomGameProfile> Profiles { get; init; } = new();
        public List<CustomGameAction> Actions { get; init; } = new();

        /// <summary>Tile styles the user set for profiles K2 SHIPS, keyed by catalogue id. A
        /// shipped profile has no record of its own to keep them in, and inventing a fake
        /// <see cref="CustomGameProfile"/> for one would have put it in the studio's list twice.</summary>
        public Dictionary<string, CustomTileStyle> Styles { get; init; } = new();

        /// <summary>Categories the user added to a profile, keyed by profile id — studio and
        /// shipped profiles alike, for the same reason as <see cref="Styles"/>. Names as written;
        /// an action filed under one stores <see cref="CategoryKey"/> of it.</summary>
        public Dictionary<string, List<string>> Categories { get; init; } = new();
    }

    private static readonly object _gate = new();
    private static Book? _cache;

    private static Book Data
    {
        get
        {
            lock (_gate)
            {
                if (_cache is not null) return _cache;
                try
                {
                    _cache = File.Exists(FilePath) ? ReadBook(File.ReadAllText(FilePath)) : new Book();
                }
                catch (Exception ex)
                {
                    // A corrupt file must not take the app down, and must not be silently
                    // replaced either — say so in the log and carry on empty.
                    App.WriteLog($"[STUDIO] cannot read \"{FilePath}\": {ex.Message}");
                    _cache = new Book();
                }
                return _cache;
            }
        }
    }

    /// <summary>Parses the file. Profiles and styles deserialize straight; ACTIONS go through
    /// <see cref="CustomGameLegacy"/>, which upgrades one written before a tile became a list of
    /// readings and elements. Doing it here rather than in a one-off migration means there is no
    /// "have I migrated yet" flag to get wrong.</summary>
    private static Book ReadBook(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return new Book();

        var book = new Book();
        if (root["Profiles"] is JsonArray profiles)
            foreach (var p in profiles)
                if (p.Deserialize<CustomGameProfile>(Json) is { } profile)
                    book.Profiles.Add(profile);

        if (root["Actions"] is JsonArray actions)
            foreach (var a in actions)
                if (CustomGameLegacy.ReadAction(a, Json) is { } action)
                    book.Actions.Add(action);

        if (root["Styles"]?.Deserialize<Dictionary<string, CustomTileStyle>>(Json) is { } styles)
            foreach (var (id, style) in styles)
                book.Styles[id] = style;

        if (root["Categories"]?.Deserialize<Dictionary<string, List<string>>>(Json) is { } categories)
            foreach (var (id, names) in categories)
                book.Categories[id] = names;

        return book;
    }

    // ─────────────────────────── profiles ───────────────────────────

    public static IReadOnlyList<CustomGameProfile> Profiles() => Data.Profiles.ToList();

    public static CustomGameProfile? ProfileById(string? id) =>
        string.IsNullOrEmpty(id) ? null : Data.Profiles.FirstOrDefault(p => p.Id == id);

    public static void SaveProfile(CustomGameProfile profile)
    {
        lock (_gate)
        {
            var list = Data.Profiles;
            int i = list.FindIndex(p => p.Id == profile.Id);
            if (i >= 0) list[i] = profile; else list.Add(profile);
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes a profile AND the actions that belonged to it — an action outlives its
    /// profile only as an orphan nothing can reach, which is worse than losing it visibly.</summary>
    public static void DeleteProfile(string id)
    {
        lock (_gate)
        {
            Data.Profiles.RemoveAll(p => p.Id == id);
            Data.Actions.RemoveAll(a => a.ProfileId == id);
            Data.Categories.Remove(id);
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>A fresh profile id. Time-based rather than a GUID so the file stays readable by
    /// hand, and prefixed so a custom profile can never shadow a shipped one.</summary>
    public static string NewProfileId() =>
        CustomGameProfile.IdPrefix + DateTime.UtcNow.Ticks.ToString("x");

    // ─────────────────────────── actions ───────────────────────────

    public static IReadOnlyList<CustomGameAction> Actions() => Data.Actions.ToList();

    /// <summary>The actions a profile's key configuration offers: its own, and only its own. An
    /// action with no profile is parked in the studio — it belongs to nothing until the user
    /// copies it onto a profile, and offering it everywhere only crowded every game's picker with
    /// readings meant for another game.</summary>
    public static IReadOnlyList<CustomGameAction> ActionsFor(string? profileId) =>
        string.IsNullOrEmpty(profileId)
            ? Array.Empty<CustomGameAction>()
            : Data.Actions.Where(a => a.ProfileId == profileId).ToList();

    /// <summary>Every distinct value path actually referenced against one link, across EVERY action
    /// on EVERY profile — not just the one currently open in the studio. <see cref="GameLinkReader"/>
    /// uses this to tell a link's own poll where it can afford to stop walking once it knows: for a
    /// link whose payload comes from reflecting a whole game scene (<c>K2.UnityLink</c>), the
    /// difference is between resolving a handful of named fields and re-walking everything, every
    /// poll, forever.</summary>
    public static IReadOnlyList<string> WantedPathsForLink(string linkId)
    {
        if (string.IsNullOrEmpty(linkId)) return Array.Empty<string>();

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in Data.Actions)
        foreach (var reading in action.Readings)
        {
            if (reading.LinkId != linkId) continue;
            if (reading.ValuePath.Length > 0) paths.Add(reading.ValuePath);
            if (reading.MinPath.Length > 0) paths.Add(reading.MinPath);
            if (reading.MaxPath.Length > 0) paths.Add(reading.MaxPath);
            // A MultiState reading's states can each watch a DIFFERENT path on the same link (an
            // empty one just means "the reading's own path", already added above).
            foreach (var state in reading.States)
                if (state.ValuePath.Length > 0) paths.Add(state.ValuePath);
        }
        return paths.ToList();
    }

    /// <summary>Duplicates an action onto another profile: same source, same look, new identity.
    /// Copying rather than sharing is deliberate — the two live separately from here on, so
    /// re-calibrating the rectangle for the second game cannot break the first.</summary>
    /// <param name="name">Name for the copy, or null to keep the original's — which is right on
    /// ANOTHER profile (where nothing else is called that) and wrong on the same one, where two
    /// rows would read identically. See the studio's <c>UniqueCopyName</c>.</param>
    /// <returns>The new action's id, or null when there was nothing to copy.</returns>
    public static string? CopyActionTo(string actionId, string targetProfileId, string? name = null)
    {
        var source = ActionById(actionId);
        if (source is null) return null;

        var copy = source with
        {
            Id = NewActionId(),
            ProfileId = targetProfileId,
            Name = string.IsNullOrWhiteSpace(name) ? source.Name : name!.Trim(),
            // A category belongs to its profile: another profile may not have it.
            Category = targetProfileId == source.ProfileId ? source.Category : "",
        };
        SaveAction(copy);
        return copy.Id;
    }

    public static CustomGameAction? ActionById(string? id) =>
        string.IsNullOrEmpty(id) ? null : Data.Actions.FirstOrDefault(a => a.Id == id);

    public static void SaveAction(CustomGameAction action)
    {
        lock (_gate)
        {
            var list = Data.Actions;
            int i = list.FindIndex(a => a.Id == action.Id);
            if (i >= 0) list[i] = action; else list.Add(action);
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes an action and clears every tile of every custom profile that pointed at
    /// it. A tile left behind would keep the action's NAME and read like a working key.</summary>
    public static void DeleteAction(string id)
    {
        lock (_gate)
        {
            Data.Actions.RemoveAll(a => a.Id == id);
            for (int p = 0; p < Data.Profiles.Count; p++)
            {
                var profile = Data.Profiles[p];
                var pages = profile.Pages.Select(page => page with
                {
                    Tiles = page.Tiles.Select(t =>
                        t.ActionType == CustomActionType.Tag &&
                        CustomActionType.Parse(t.ActionValue)?.Id == id
                            ? new CustomGameTile("", "", "")
                            : t).ToList()
                }).ToList();
                Data.Profiles[p] = profile with { Pages = pages };
            }
            Write();
        }
        Changed?.Invoke();
    }

    // ─────────────────────────── categories ───────────────────────────

    /// <summary>What an action filed under the user category <paramref name="name"/> stores. The
    /// "!" makes <see cref="Loc.Get"/> show the name as written instead of looking it up.</summary>
    public static string CategoryKey(string name) => "!" + name;

    /// <summary>The categories the user added to a profile, in the order they were added. The
    /// profile's built-in ones (Generic, and a shipped game's own) are not listed here.</summary>
    public static IReadOnlyList<string> CategoriesFor(string? profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return Array.Empty<string>();
        lock (_gate)
            return Data.Categories.TryGetValue(profileId!, out var list)
                ? list.ToList() : Array.Empty<string>();
    }

    public static void AddCategory(string profileId, string name)
    {
        lock (_gate)
        {
            if (!Data.Categories.TryGetValue(profileId, out var list))
                Data.Categories[profileId] = list = new List<string>();
            list.Add(name);
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes a user category. Its actions are not deleted with it: they move back to
    /// Generic, where the user can still find them.</summary>
    public static void DeleteCategory(string profileId, string name)
    {
        string key = CategoryKey(name);
        lock (_gate)
        {
            if (Data.Categories.TryGetValue(profileId, out var list) &&
                list.Remove(name) && list.Count == 0)
                Data.Categories.Remove(profileId);
            for (int i = 0; i < Data.Actions.Count; i++)
                if (Data.Actions[i].ProfileId == profileId && Data.Actions[i].Category == key)
                    Data.Actions[i] = Data.Actions[i] with { Category = "" };
            Write();
        }
        Changed?.Invoke();
    }

    public static string NewActionId() => "a" + DateTime.UtcNow.Ticks.ToString("x");

    /// <summary>A fresh id for a reading or an element inside a tile — unique within the file, and
    /// short enough to stay readable by hand.</summary>
    public static string NewPartId() => CustomGameLegacy.NewId();

    // ─────────────────────────── styles ───────────────────────────

    /// <summary>The tile style everything on <paramref name="profileId"/> inherits:
    /// <list type="number">
    /// <item>a studio profile's own default style;</item>
    /// <item>the style the user set here for a SHIPPED profile;</item>
    /// <item>otherwise, for a shipped profile, the GAME's own look — its frame art in the lit and
    /// dark variants and its HUD colour for the text, so an action made for Deadside comes up
    /// already wearing Deadside;</item>
    /// <item>K2's plain dark tile for an action that belongs to no profile.</item>
    /// </list></summary>
    public static CustomTileStyle EffectiveStyleFor(string? profileId)
    {
        if (ProfileById(profileId) is { } custom) return custom.DefaultStyle;
        if (string.IsNullOrEmpty(profileId)) return new CustomTileStyle();

        lock (_gate)
            if (Data.Styles.TryGetValue(profileId!, out var stored)) return stored;

        return ShippedStyleFor(profileId!);
    }

    /// <summary>The look a shipped profile gives its tiles, read from the game's own theme. This
    /// is also what the studio shows as the starting point when the user opens one.</summary>
    public static CustomTileStyle ShippedStyleFor(string profileId) => new()
    {
        BackgroundOn = "#0B0B0D",
        TextOn = GameProfileTheme.TextHexForPreview(profileId, lit: true),
        TextOff = GameProfileTheme.TextHexForPreview(profileId, lit: false),
        BackgroundImageOn = GameProfileTheme.BgImagePathForPreview(profileId, lit: true),
        BackgroundImageOff = GameProfileTheme.BgImagePathForPreview(profileId, lit: false),
    };

    /// <summary>Stores a style for a shipped profile, or drops it (back to the game's own look)
    /// when <paramref name="style"/> is null.</summary>
    public static void SaveShippedStyle(string profileId, CustomTileStyle? style)
    {
        lock (_gate)
        {
            if (style is null) Data.Styles.Remove(profileId);
            else Data.Styles[profileId] = style;
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>The accent an action's indicator is drawn in: the studio profile's own colour, or
    /// the game's HUD colour for a shipped one.</summary>
    public static System.Drawing.Color AccentFor(string? profileId)
    {
        if (ProfileById(profileId) is { } custom)
            return CustomTileRenderer.ParseColor(custom.Accent, System.Drawing.Color.FromArgb(255, 122, 20));
        return string.IsNullOrEmpty(profileId)
            ? System.Drawing.Color.FromArgb(255, 122, 20)
            : GameProfileTheme.AccentFor(profileId!);
    }

    // ─────────────────────────── images ───────────────────────────

    /// <summary>Copies a picture the user picked into the studio's own folder and returns the new
    /// path. A file already inside the folder is left alone. Returns null when the copy fails —
    /// the caller then keeps no picture rather than a path that will break later.</summary>
    public static string? ImportImage(string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
            Directory.CreateDirectory(AssetsDir);
            string full = Path.GetFullPath(sourcePath);
            if (full.StartsWith(Path.GetFullPath(AssetsDir) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase))
                return full;

            string name = $"{Path.GetFileNameWithoutExtension(sourcePath)}_" +
                          $"{DateTime.UtcNow.Ticks.ToString("x")}{Path.GetExtension(sourcePath)}";
            string dest = Path.Combine(AssetsDir, name);
            File.Copy(full, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[STUDIO] cannot import image \"{sourcePath}\": {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────── export / import ───────────────────────────

    /// <summary>A profile packed for sharing: the profile, its actions, the screen probes and game
    /// links those actions read, and every picture they use, base64'd inline. One self-contained file —
    /// a folder of loose PNGs beside a JSON is a thing that arrives half-copied.</summary>
    private sealed record Package
    {
        public int Version { get; init; } = 1;
        public CustomGameProfile? Profile { get; init; }
        public List<CustomGameAction> Actions { get; init; } = new();
        public List<ScreenProbe> Probes { get; init; } = new();

        /// <summary>The game links those actions read. Packed for the same reason as the probes:
        /// a shared profile whose readings point at links the receiving machine has never heard of
        /// is a page of tiles that will only ever show dashes.</summary>
        public List<GameLinkDef> Links { get; init; } = new();

        /// <summary>File name (as referenced by the paths above, base name only) → base64 bytes.</summary>
        public Dictionary<string, string> Assets { get; init; } = new();

        /// <summary>The categories the user added to the profile — its actions are filed under them.</summary>
        public List<string> Categories { get; init; } = new();
    }

    /// <summary>Writes a profile and everything it needs to <paramref name="path"/>.</summary>
    public static bool Export(string profileId, string path)
    {
        try
        {
            var profile = ProfileById(profileId);
            if (profile is null) return false;

            var actions = Data.Actions.Where(a => a.ProfileId == profileId).ToList();
            var probeIds = actions.SelectMany(ProbeIdsOf).Where(id => id.Length > 0).Distinct();
            var probes = probeIds.Select(ScreenProbeStore.ById).OfType<ScreenProbe>().ToList();

            var links = actions.SelectMany(LinkIdsOf).Where(id => id.Length > 0).Distinct()
                .Select(GameLinkStore.ById).OfType<GameLinkDef>().ToList();

            var pkg = new Package
            {
                Profile = Strip(profile),
                Actions = actions.Select(Strip).ToList(),
                Probes = probes,
                Links = links,
                Categories = CategoriesFor(profileId).ToList(),
            };
            foreach (string img in ImagesOf(profile, actions))
            {
                string name = Path.GetFileName(img);
                if (pkg.Assets.ContainsKey(name) || !File.Exists(img)) continue;
                pkg.Assets[name] = Convert.ToBase64String(File.ReadAllBytes(img));
            }

            File.WriteAllText(path, JsonSerializer.Serialize(pkg, Json));
            return true;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[STUDIO] export of \"{profileId}\" failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads a package written by <see cref="Export"/> and adds it as a NEW profile —
    /// new ids throughout, so importing a profile twice gives two profiles instead of silently
    /// overwriting the copy the user has already been editing.</summary>
    /// <returns>The new profile's id, or null when the file could not be read.</returns>
    public static string? Import(string path)
    {
        try
        {
            var pkg = JsonSerializer.Deserialize<Package>(File.ReadAllText(path), Json);
            if (pkg?.Profile is null) return null;

            // Assets first: everything below rewrites its paths to point at the copies.
            Directory.CreateDirectory(AssetsDir);
            var assetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, b64) in pkg.Assets)
            {
                string dest = Path.Combine(AssetsDir,
                    $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.UtcNow.Ticks:x}{Path.GetExtension(name)}");
                File.WriteAllBytes(dest, Convert.FromBase64String(b64));
                assetPaths[name] = dest;
            }
            string? Remap(string? p) =>
                p is not null && assetPaths.TryGetValue(Path.GetFileName(p), out var np) ? np : null;

            // Probes: new ids, because the importing machine may already have a probe with the
            // same id from its own studio session.
            var probeMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var probe in pkg.Probes)
            {
                string newId = ScreenProbeStore.NewId();
                probeMap[probe.Id] = newId;
                ScreenProbeStore.Save(probe with { Id = newId });
            }
            string MapProbe(string id) => probeMap.TryGetValue(id, out var n) ? n : "";

            // Links: new ids for the same reason as the probes. A link that is a DUPLICATE of one
            // already here (same address, same name) is reused instead of copied — the addresses
            // are per-game, not per-profile, so importing three KSP profiles must not leave three
            // identical Telemachus links in the list.
            var linkMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var link in pkg.Links)
            {
                var same = GameLinkStore.All().FirstOrDefault(
                    l => string.Equals(l.Url, link.Url, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(l.Name, link.Name, StringComparison.CurrentCultureIgnoreCase));
                if (same is not null) { linkMap[link.Id] = same.Id; continue; }

                string newLinkId = GameLinkStore.NewId();
                linkMap[link.Id] = newLinkId;
                GameLinkStore.Save(link with { Id = newLinkId });
            }
            string MapLink(string id) => linkMap.TryGetValue(id, out var n) ? n : "";

            string profileId = NewProfileId();
            var actionMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var actions = new List<CustomGameAction>();
            foreach (var a in pkg.Actions)
            {
                string newId = NewActionId();
                actionMap[a.Id] = newId;
                actions.Add(a with
                {
                    Id = newId,
                    ProfileId = profileId,
                    Readings = a.Readings.Select(r => r with
                    {
                        ProbeId = MapProbe(r.ProbeId),
                        LinkId = MapLink(r.LinkId),
                        States = r.States.Select(s => s with
                        {
                            ProbeId = MapProbe(s.ProbeId),
                            IconPath = Remap(s.IconPath),
                        }).ToList(),
                    }).ToList(),
                    Elements = a.Elements.Select(e => e with { IconPath = Remap(e.IconPath) }).ToList(),
                });
            }

            var profile = pkg.Profile with
            {
                Id = profileId,
                DefaultStyle = pkg.Profile.DefaultStyle with
                {
                    BackgroundImageOn = Remap(pkg.Profile.DefaultStyle.BackgroundImageOn),
                    BackgroundImageOff = Remap(pkg.Profile.DefaultStyle.BackgroundImageOff),
                },
                // Tiles carry "<action-id>|<name>": the ids have all changed, so every tile is
                // rewritten. One that points at an action the package didn't carry becomes empty
                // rather than a key bound to nothing.
                Pages = pkg.Profile.Pages.Select(page => page with
                {
                    Tiles = page.Tiles.Select(t =>
                    {
                        if (t.ActionType != CustomActionType.Tag) return t;
                        var parsed = CustomActionType.Parse(t.ActionValue);
                        if (parsed is null || !actionMap.TryGetValue(parsed.Value.Id, out var na))
                            return new CustomGameTile("", "", "");
                        return t with { ActionValue = $"{na}|{parsed.Value.Label}" };
                    }).ToList()
                }).ToList(),
            };

            lock (_gate)
            {
                Data.Profiles.Add(profile);
                Data.Actions.AddRange(actions);
                if (pkg.Categories.Count > 0)
                    Data.Categories[profileId] = pkg.Categories.Distinct().ToList();
                Write();
            }
            Changed?.Invoke();
            return profileId;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[STUDIO] import of \"{path}\" failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Paths are absolute and machine-specific; the package refers to pictures by file
    /// NAME and carries the bytes, so what leaves here is the name only.</summary>
    private static CustomGameProfile Strip(CustomGameProfile p) => p with
    {
        DefaultStyle = p.DefaultStyle with
        {
            BackgroundImageOn = NameOnly(p.DefaultStyle.BackgroundImageOn),
            BackgroundImageOff = NameOnly(p.DefaultStyle.BackgroundImageOff),
        },
    };

    private static CustomGameAction Strip(CustomGameAction a) => a with
    {
        Elements = a.Elements.Select(e => e with { IconPath = NameOnly(e.IconPath) }).ToList(),
        Readings = a.Readings
            .Select(r => r with { States = r.States.Select(s => s with { IconPath = NameOnly(s.IconPath) }).ToList() })
            .ToList(),
    };

    private static string? NameOnly(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);

    private static IEnumerable<string> ProbeIdsOf(CustomGameAction a) =>
        a.Readings.SelectMany(r => new[] { r.ProbeId }.Concat(r.States.Select(s => s.ProbeId)));

    private static IEnumerable<string> LinkIdsOf(CustomGameAction a) =>
        a.Readings.Select(r => r.LinkId);

    private static IEnumerable<string> ImagesOf(CustomGameProfile p, IEnumerable<CustomGameAction> actions) =>
        new[] { p.DefaultStyle.BackgroundImageOn, p.DefaultStyle.BackgroundImageOff }
            .Concat(actions.SelectMany(a =>
                new[] { a.Style?.BackgroundImageOn, a.Style?.BackgroundImageOff }
                    .Concat(a.Elements.Select(e => e.IconPath))
                    .Concat(a.Readings.SelectMany(r => r.States.Select(s => s.IconPath)))))
            .OfType<string>()
            .Where(s => s.Length > 0);

    private static void Write()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, Json));
        }
        catch (Exception ex)
        {
            App.WriteLog($"[STUDIO] cannot write \"{FilePath}\": {ex.Message}");
        }
    }
}
