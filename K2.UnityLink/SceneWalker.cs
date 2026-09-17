using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace K2.UnityLink
{
    /// <summary>
    /// Turns whatever is currently loaded into a flat <c>path -&gt; object</c> tree, run once per
    /// refresh tick on Unity's main thread (reflection + <c>GameObject</c> access is not thread-safe).
    ///
    /// <para><b>Why <c>MonoBehaviour</c> only.</b> A <c>GameObject</c> also carries engine components —
    /// <c>Transform</c>, <c>Renderer</c>, <c>Collider</c>, <c>Rigidbody</c> — but those are native
    /// engine types with no reflectable game state, only geometry/physics internals nobody wants on a
    /// tile. Every <c>MonoBehaviour</c>, by contrast, is a SCRIPT the game's own developers wrote —
    /// health, ammo, score, whatever a HUD would show is a field on one of these. Restricting to
    /// <c>MonoBehaviour</c> is therefore not a limitation, it is the filter that makes "expose as much
    /// as possible" useful instead of a dump of engine noise.</para>
    ///
    /// <para><b>Why declared-only, walked up the type chain.</b> Reflecting a script's full public
    /// surface via <c>GetFields()</c> would also return every member <c>MonoBehaviour</c> itself
    /// defines (<c>enabled</c>, <c>useGUILayout</c>, <c>tag</c>...) repeated on every single script in
    /// the game. Walking the type chain up to (excluding) <c>MonoBehaviour</c> and asking each level
    /// for only what IT declares keeps the payload to what the game's own code actually added.</para>
    /// </summary>
    internal static class SceneWalker
    {
        /// <summary>Simple/leaf types that become a JSON scalar or small nested object directly. A
        /// custom class or struct beyond this list is skipped rather than recursed into: without a
        /// cycle guard, reflecting an arbitrary object graph risks walking back into the same
        /// <c>GameObject</c> (a script referencing its own transform's owner, its manager, etc.) —
        /// the primitives below are where the useful gameplay numbers actually live, and the
        /// remaining wiring is not worth the risk of an infinite reflect.</summary>
        private static bool IsLeaf(Type t) =>
            t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) ||
            t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) ||
            t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Color32);

        internal readonly struct Options
        {
            internal Options(int maxObjects, int maxDepth, bool includeInactive, int maxFieldsPerComponent,
                              bool includePrivateFields, bool includePublicFields, int maxNestedDepth)
            {
                MaxObjects = maxObjects;
                MaxDepth = maxDepth;
                IncludeInactive = includeInactive;
                MaxFieldsPerComponent = maxFieldsPerComponent;
                IncludePrivateFields = includePrivateFields;
                IncludePublicFields = includePublicFields;
                MaxNestedDepth = maxNestedDepth;
            }

            internal int MaxObjects { get; }
            internal int MaxDepth { get; }
            internal bool IncludeInactive { get; }
            internal int MaxFieldsPerComponent { get; }

            /// <summary>How many of the game's OWN objects a read may descend through before it
            /// stops. 0 restores the original behaviour of reading only scalars sitting directly on
            /// a script.
            ///
            /// <para>This is what makes a well-organised game readable at all. Measured on 7 Days to
            /// Die (2026-09-16, live endpoint): the player object IS found and 40 members of
            /// <c>EntityPlayerLocal</c> come back, with not one of health, food or water among them
            /// because every one of those lives at <c>Stats.Health.Value</c> - two of the game's own
            /// objects further down. The search proved they were SEEN and then dropped: searching
            /// "Stats" or "Health" matched the player, since matching runs off member names while
            /// the read stopped at the first non-scalar.</para></summary>
            internal int MaxNestedDepth { get; }

            /// <summary>On by default. Health/ammo/hunger and similar HUD numbers are, in most
            /// well-structured Unity games, private fields the Inspector can still see
            /// (<c>[SerializeField] private float health;</c>) — deliberate encapsulation, not
            /// obfuscation, but invisible to a public-only reflection pass. When true, also reads
            /// non-public FIELDS (never non-public properties, which are far more likely to be pure
            /// implementation-detail backing storage with nothing a tile would want).</summary>
            internal bool IncludePrivateFields { get; }

            /// <summary>Off by default (a debug/advanced setting — see
            /// <see cref="UnityLinkPlugin"/>'s <c>ShowPublicFields</c> bind). A public FIELD is
            /// overwhelmingly a Unity Inspector reference (another GameObject, a prefab, a colour),
            /// not a gameplay number — with private fields already covering the interesting state,
            /// public fields are mostly noise. Public PROPERTIES are unaffected by this flag: a
            /// read-only property is a deliberate API the developer chose to expose, not an
            /// Inspector slot, and stays far less noisy than public fields tend to be.</summary>
            internal bool IncludePublicFields { get; }
        }

        /// <summary>Builds <c>{ "scenes": [...], "objects": { "&lt;path&gt;": {...}, ... } }</c>. The
        /// object dictionary is intentionally FLAT (one entry per <c>GameObject</c>, key = its full
        /// hierarchy path) rather than nested per child — nesting would multiply the JSON depth by
        /// the scene's own hierarchy depth for no benefit, since K2's own flattener turns either shape
        /// into the same dotted paths a reading picks from.
        ///
        /// <para><b>Found BY TYPE, not by walking the hierarchy.</b> Walking scene roots was the
        /// original approach and it is the wrong primitive: it only ever finds what the engine
        /// happens to list under a loaded scene's roots, in whatever order, which for a game world is
        /// scenery. Measured on real hardware (Unturned, 2026-09-14, by querying the live endpoint
        /// rather than guessing): 367 objects, 68 roots, ALL map props — houses, boulders, vehicles —
        /// exactly 12 of them carrying any script at all, and not one player/HUD/manager object
        /// anywhere. Those live outside what scene-root enumeration reaches here (the
        /// <c>DontDestroyOnLoad</c> branch that was supposed to cover them never even ran: its scene
        /// never showed up in the snapshot's own scene list).
        ///
        /// <para>Asking the engine for every loaded <c>MonoBehaviour</c> instead sidesteps all of it —
        /// scene membership, root ordering, how deep something is nested, whether a game parents its
        /// player under something unexpected. It also shrinks the payload rather than growing it:
        /// scenery with no script stops being listed at all, and what remains is by definition the
        /// part a reading could ever want.</para></summary>
        /// <param name="find">When given, only objects whose PATH or one of whose SCRIPT NAMES
        /// contains it (case-insensitively) are included. A whole game's scripted objects is a
        /// document nobody can read — measured on Unturned: 3269 objects, 5.5 MB, with the player
        /// arriving after ~5900 values, i.e. past the point where K2's own reader stops. Filtering
        /// at the source is the difference between "somewhere in here" and an answer.</param>
        internal static Dictionary<string, object?> Snapshot(Options options, Scene dontDestroyOnLoadScene,
                                                              string? find = null)
        {
            var sceneNames = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) sceneNames.Add(scene.name);
            }
            if (dontDestroyOnLoadScene.IsValid()) sceneNames.Add(dontDestroyOnLoadScene.name);

            // FindObjectsOfType: only ACTIVE components, and only ones living in a real scene — no
            // prefab assets, which is what we want and what makes it the cheap option. Reaching
            // INACTIVE ones needs Resources.FindObjectsOfTypeAll, which also drags in every prefab
            // loaded in memory (thousands, in a game with a big item catalogue), so that is the
            // opt-in path and those assets are filtered back out below.
            MonoBehaviour[] behaviours = options.IncludeInactive
                ? Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                : UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();

            var byObject = new Dictionary<GameObject, List<MonoBehaviour>>();
            int assets = 0;
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null || behaviour is UnityLinkRunner) continue;
                var go = behaviour.gameObject;

                // A prefab/asset sitting in memory belongs to no scene and its scene NAME is empty —
                // a stabler test than Scene.IsValid(), which is exactly what the DontDestroyOnLoad
                // branch this replaces appears to have been failing on.
                if (string.IsNullOrEmpty(go.scene.name)) { assets++; continue; }
                if (!options.IncludeInactive && !go.activeInHierarchy) continue;

                if (!byObject.TryGetValue(go, out var list)) byObject[go] = list = new List<MonoBehaviour>();
                list.Add(behaviour);
            }

            var objects = new Dictionary<string, object?>(StringComparer.Ordinal);
            var memo = new Dictionary<Type, bool>[options.MaxNestedDepth + 1];
            for (int i = 0; i < memo.Length; i++) memo[i] = new Dictionary<Type, bool>();
            int budget = options.MaxObjects;
            int collisions = 0;
            int stateless = 0;
            bool truncated = false;
            foreach (var pair in byObject)
            {
                if (budget <= 0) { truncated = true; break; }

                string? path = PathOf(pair.Key.transform, options.MaxDepth);
                if (path is null) continue;                       // nested deeper than MaxDepth
                if (find is not null && !Matches(path, pair.Value, find, options, memo)) continue;

                // A path hit shows the whole object: the term named the object, not a value in it.
                var entry = EntryFor(pair.Key, pair.Value, options,
                                     find is not null && NameHas(path, find) ? null : find);

                // An object whose scripts contributed NOTHING readable does not get to spend a slot
                // of the budget. This is the MonoBehaviour filter finishing its job: the premise up
                // top is that a script means game state, and in a voxel- or prop-heavy world that
                // premise breaks — every decorative block carries a behaviour that declares nothing.
                //
                // Measured on the live endpoint (2026-09-16) and the reason this exists: of the 6000
                // objects that fitted in the budget, 5954 reported transform+active and not one
                // other field, while 25308 objects with scripts were waiting behind them. The cap
                // was never the problem — it was being spent entirely on scenery, so raising it (or
                // deriving it from a count, which costs nothing since byObject.Count is right there)
                // would only have bought more of the same, in a payload K2's own reader caps anyway.
                //
                // Skipped only when browsing everything. An explicit search is someone naming the
                // thing they want, and they get it whatever it holds — which keeps a marker object's
                // position reachable without a config switch for it.
                if (find is null && entry.Count == BareEntryKeys) { stateless++; continue; }

                // Two different GameObjects CAN share a hierarchy path — same-named siblings are
                // legal in Unity and a game world is full of them (Unturned names whole families of
                // objects after their region index: "44/44", "1006/1006"...). Measured before this
                // guard existed: 3282 objects with scripts collapsed into 265 entries, every
                // collision silently overwriting the previous one while still costing a slot of the
                // budget — which is what made the object anyone actually wanted a coin toss.
                if (objects.ContainsKey(path))
                {
                    collisions++;
                    int n = 2;
                    while (objects.ContainsKey(path + "#" + n)) n++;
                    path += "#" + n;
                }

                objects[path] = entry;
                budget--;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scenes"] = sceneNames,
                ["objects"] = objects,
                // Deliberately part of the payload, not just a log line: three rounds of this were
                // spent guessing at what the walk could and could not see. Now the numbers are in
                // the tree browser next to everything else.
                ["diag"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["behavioursFound"] = behaviours.Length,
                    ["assetsSkipped"] = assets,
                    ["objectsWithScripts"] = byObject.Count,
                    ["objectsListed"] = objects.Count,
                    ["objectsStateless"] = stateless,
                    ["pathCollisions"] = collisions,
                    ["truncated"] = truncated,
                    ["includeInactive"] = options.IncludeInactive,
                    ["ddolSceneValid"] = dontDestroyOnLoadScene.IsValid(),
                    ["ddolSceneName"] = dontDestroyOnLoadScene.name ?? "",
                },
            };
        }

        /// <summary>Whether a search term names anything about this object: its path, a script on
        /// it, or a FIELD of one of those scripts.
        ///
        /// <para>The last one is not an extra — it is what people actually search for. Matching only
        /// paths and script names meant "PlayerLife" found the character while "health" found
        /// nothing at all, which is backwards: the field name is the part somebody knows, and the
        /// script holding it is what they are trying to discover. Reading member NAMES is cheap
        /// because <see cref="MembersOf"/> has them cached per type and no value is touched here.</para>
        ///
        /// <para>Nested members count too, down to <see cref="Options.MaxNestedDepth"/>: in 7 Days to
        /// Die "health" is <c>EntityPlayerLocal.Stats.Health</c>, two objects below any script.
        /// Judged on declared types via <see cref="TypeMentions"/>, and only members that would
        /// actually be listed — matching one that is then filtered out of the answer would find an
        /// object and show nothing in it.</para></summary>
        private static bool Matches(string path, List<MonoBehaviour> behaviours, string find, Options options,
                                    Dictionary<Type, bool>[] memo)
        {
            if (NameHas(path, find)) return true;

            foreach (var b in behaviours)
            {
                if (b == null) continue;
                if (NameHas(b.GetType().Name, find)) return true;
                if (TypeMentions(b.GetType(), find, options, 0, ownSurface: true, memo)) return true;
            }
            return false;
        }

        /// <summary>How many keys an entry has when no script contributed anything: <c>transform</c>
        /// and <c>active</c>, both synthetic. What "this object holds no game state" looks like.</summary>
        private const int BareEntryKeys = 2;

        /// <summary>One GameObject's entry: the synthetic transform block every object gets, plus one
        /// sub-object per script that had anything readable on it.</summary>
        /// <param name="find">Search term that matched a MEMBER: each script lists only the branches
        /// holding it (a script whose own name matches lists everything). Without this a hit three
        /// levels down came back as the whole script, cut by the budget before reaching the hit.</param>
        private static Dictionary<string, object?> EntryFor(GameObject go, List<MonoBehaviour> behaviours,
                                                             Options options, string? find = null)
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["transform"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["position"] = VectorToDict(go.transform.position),
                    ["eulerAngles"] = VectorToDict(go.transform.eulerAngles),
                },
                ["active"] = go.activeSelf,
            };

            foreach (var behaviour in behaviours)
            {
                string? prune = find is not null && !NameHas(behaviour.GetType().Name, find) ? find : null;
                var fields = ReadMembers(behaviour, options, prune);
                if (fields.Count == 0) continue;

                // Two scripts of the same type on one GameObject (rare, but legal in Unity) would
                // otherwise silently overwrite each other's entry. SnapshotPaths knows how to read
                // this numbering back — see FindComponentByStoredKey.
                string key = behaviour.GetType().Name;
                if (entry.ContainsKey(key))
                {
                    int i = 2;
                    while (entry.ContainsKey(key + i)) i++;
                    key += i;
                }
                entry[key] = fields;
            }
            return entry;
        }

        /// <summary>Drops the <c>#2</c>/<c>#3</c> the full dump appends to tell same-path objects
        /// apart. A narrowed lookup answers with the FIRST object at that path either way: two
        /// GameObjects sharing a hierarchy path are genuinely indistinguishable by path, and a
        /// reading pointing at one of them was already a coin toss when it was picked.</summary>
        private static string StripOccurrence(string path)
        {
            int hash = path.LastIndexOf('#');
            if (hash <= 0) return path;
            for (int i = hash + 1; i < path.Length; i++)
                if (!char.IsDigit(path[i])) return path;
            return hash + 1 == path.Length ? path : path.Substring(0, hash);
        }

        private static string StripTrailingDigits(string name)
        {
            int i = name.Length;
            while (i > 0 && char.IsDigit(name[i - 1])) i--;
            return i == 0 ? name : name.Substring(0, i);
        }

        /// <summary>The "/"-joined hierarchy path an object is published under, built upwards from
        /// the object. Null when it sits deeper than <see cref="Options.MaxDepth"/>.</summary>
        private static string? PathOf(Transform t, int maxDepth)
        {
            var names = new List<string>(8) { Sanitize(t.name) };
            for (var p = t.parent; p is not null; p = p.parent)
            {
                if (names.Count > maxDepth) return null;
                names.Add(Sanitize(p.name));
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        /// <summary>Resolves ONLY the paths asked for, instead of dumping everything — what a
        /// <c>k2want</c> request gets. Each wanted member is fetched by NAME (one targeted
        /// <c>GetField</c>/<c>GetProperty</c>) rather than by enumerating a component's whole
        /// surface, which is where the full dump spends nearly all of its time. A path this cannot
        /// resolve (renamed GameObject, removed field, a shape the resolver does not understand) is
        /// silently absent from the result — the same contract a reading already has with a path
        /// that stops matching: it reads as "—", never a crash or a partial answer.
        ///
        /// <para><b>Why not <c>GameObject.Find</c>.</b> It was the obvious lookup and it is wrong
        /// here: published paths are SANITIZED (<c>.</c>, <c>[</c>, <c>]</c> replaced — see
        /// <see cref="Sanitize"/>), so any object whose real name carries one of those characters can
        /// never be found by its own published path: the lookup and the thing being looked up
        /// disagree by construction. Matching a component sweep against sanitized paths instead
        /// cannot drift from what the full dump published.
        /// <b>This closed no observed failure.</b> Measured on Unturned 2026-09-14, <c>Find</c>
        /// resolved the player fine — the object's name carries underscores at the source, so
        /// <see cref="Sanitize"/> had nothing to change and path and name happened to agree. WHY it
        /// is spelled that way was guessed at twice in one session (the player's Steam name has
        /// brackets; the game may normalise them; the name may be a stale one kept in the game's own
        /// settings) and none of it was measured, so none of it is recorded here as fact. What the
        /// measurement does say: a published path cannot be read backwards into an original name,
        /// which is the whole reason this resolver stopped depending on that.</para>
        ///
        /// <para><b>Why <see cref="Options.MaxObjects"/>/<see cref="Options.MaxDepth"/> do not apply
        /// here.</b> Those bound an UNBOUNDED walk of everything; a caller that already named the
        /// exact paths it wants has already done the bounding.</para></summary>
        internal static Dictionary<string, object?> SnapshotPaths(IEnumerable<string> wantedPaths, Options options)
        {
            var objects = new Dictionary<string, object?>(StringComparer.Ordinal);

            // Grouped by GameObject so one with several wanted members is resolved once, not once
            // per member.
            var byGameObject = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            var wantedComponents = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in wantedPaths)
            {
                const string prefix = "objects.";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string rest = path.Substring(prefix.Length);
                int dot = rest.IndexOf('.');
                if (dot < 0) continue; // just a GameObject path with nothing under it — not a leaf

                string goPath = rest.Substring(0, dot);
                string[] chain = rest.Substring(dot + 1).Split('.');
                if (!byGameObject.TryGetValue(goPath, out var list)) byGameObject[goPath] = list = new List<string[]>();
                list.Add(chain);
                if (chain[0] != "transform")
                {
                    // Both spellings: the key as published, and — since a second component of the
                    // same type is published as "PlayerLife2" — the name with a trailing count
                    // peeled off. A type whose real name ends in a digit stays covered by the first.
                    wantedComponents.Add(chain[0]);
                    wantedComponents.Add(StripTrailingDigits(chain[0]));
                }
            }
            if (byGameObject.Count == 0)
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["objects"] = objects };

            var wantedBasePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in byGameObject.Keys) wantedBasePaths.Add(StripOccurrence(key));

            // Objects already located in an earlier tick, still alive: the whole point of resolving
            // by name is that the answer keeps holding. A reading watches the same object for a
            // whole session, so sweeping for it twice a second would be pure waste — the sweep below
            // runs only for what is still missing, which after the first tick is normally nothing.
            var found = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (string basePath in wantedBasePaths)
                if (_resolved.TryGetValue(basePath, out var cached) && cached != null)
                    found[basePath] = cached;

            bool anyMissing = found.Count < wantedBasePaths.Count;
            // An object that genuinely is not there yet (player not spawned, menu still up) must not
            // buy a full sweep on every single tick either.
            if (anyMissing && Time.unscaledTime >= _nextSweepAt)
            {
                _nextSweepAt = Time.unscaledTime + SweepBackoffSeconds;

                // Cheap test first: does this component's TYPE NAME appear in any wanted path at
                // all? Only then is it worth building its object's path to compare. The thousands of
                // components nobody asked about cost one hash lookup each.
                var behaviours = options.IncludeInactive
                    ? Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                    : UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();

                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null || behaviour is UnityLinkRunner) continue;
                    if (!wantedComponents.Contains(behaviour.GetType().Name)) continue;

                    var owner = behaviour.gameObject;
                    if (string.IsNullOrEmpty(owner.scene.name)) continue;  // a prefab asset, not the live object
                    string? path = PathOf(owner.transform, options.MaxDepth);
                    if (path is null || found.ContainsKey(path)) continue;
                    if (wantedBasePaths.Contains(path)) found[path] = _resolved[path] = owner;
                }
            }

            foreach (var pair in byGameObject)
            {
                string goPath = StripOccurrence(pair.Key);
                if (!found.TryGetValue(goPath, out var go))
                {
                    // Nothing matched by component: a path wanting only transform values, or an
                    // object whose script is gone. Find is still right for a name carrying no
                    // sanitized character, which is the only case it can be right about.
                    go = GameObject.Find(StripOccurrence(goPath));
                    if (go is null) continue;
                }

                var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var chain in pair.Value)
                {
                    object? value = ResolveChain(go, chain, options);
                    if (value is not null) InsertChain(entry, chain, value);
                }
                // Keyed by the path as ASKED, "#2" and all: the reading stores that exact string, so
                // answering under the stripped one would flatten to a path it never looks up.
                if (entry.Count > 0) objects[pair.Key] = entry;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["objects"] = objects };
        }

        /// <summary>Follows one member chain off a resolved GameObject — <c>["transform","position",
        /// "x"]</c> or <c>["PlayerLife","health"]</c> — down to a single value, reusing the exact same
        /// leaf conversion <see cref="EntryFor"/>'s full dump uses so a narrowed and a full snapshot never
        /// disagree about what a given path means.</summary>
        private static object? ResolveChain(GameObject go, string[] chain, Options options)
        {
            object? current;
            int start;

            if (chain[0] == "transform")
            {
                if (chain.Length < 2) return null;
                current = chain[1] == "position" ? VectorToDict(go.transform.position)
                        : chain[1] == "eulerAngles" ? VectorToDict(go.transform.eulerAngles)
                        : null;
                start = 2;
            }
            else
            {
                var behaviour = FindComponentByStoredKey(go, chain[0]);
                if (behaviour is null || chain.Length < 2) return null;

                // Member by member, BY NAME, down to the leaf. Reading the first member through the
                // full dump's budgeted descent and drilling into what came back lost every value
                // that descent cuts: it hands each nested object a slice, and the members declared
                // LAST — the properties — are the first to go. Measured 2026-09-17 on 7 Days to Die:
                // entityStats.Food.m_baseMax (an early field) resolved, entityStats.Health.BaseMax
                // (a property of the same object) never did, although the tree browser — which
                // searches, and so hands the matching branch the whole budget — listed it.
                object target = behaviour;
                current = null;
                start = chain.Length;
                for (int i = 1; i < chain.Length; i++)
                {
                    if (FindListedMember(target.GetType(), chain[i], options, ownSurface: i == 1) is not { } m)
                        return null;

                    if (IsLeaf(m.Type))
                    {
                        var captured = target;
                        current = Convert(m.Type, () => m.Read(captured), options, 0, null, new Budget { Left = 1 });
                        if (current is NotLeaf) return null;
                        start = i + 1;
                        break;
                    }

                    // The same gate the dump descends through, so a path resolves here only if the
                    // browser could have offered it in the first place.
                    if (i == chain.Length - 1 || !CanDescendInto(m.Type)) return null;
                    object? next;
                    try { next = m.Read(target); }
                    catch { return null; }
                    if (next is null || next is UnityEngine.Object || !CanDescendInto(next.GetType())) return null;
                    target = next;
                }
            }

            // Any further segment drills into an already-converted small dict (a Vector/Quaternion/
            // Color's x/y/z/w or r/g/b/a) — never live reflection again past this point.
            for (int i = start; i < chain.Length; i++)
            {
                if (current is not Dictionary<string, object?> dict) return null;
                current = dict.TryGetValue(chain[i], out var v) ? v : null;
            }
            return current;
        }

        /// <summary>Undoes <see cref="EntryFor"/>'s own duplicate-component naming ("PlayerLife" for the
        /// first, "PlayerLife2"/"PlayerLife3" for the next ones on the same GameObject): tries the
        /// stored key as a literal type name first (covers the overwhelming majority of cases, and
        /// any real type name that happens to end in a digit), and only peels off a trailing count
        /// when that finds nothing.</summary>
        private static MonoBehaviour? FindComponentByStoredKey(GameObject go, string key)
        {
            var behaviours = go.GetComponents<MonoBehaviour>();

            foreach (var b in behaviours)
                if (b != null && b.GetType().Name == key) return b;

            int i = key.Length;
            while (i > 0 && char.IsDigit(key[i - 1])) i--;
            if (i == key.Length || i == 0) return null; // no trailing digits to peel off
            string baseName = key.Substring(0, i);
            if (!int.TryParse(key.Substring(i), out int occurrence) || occurrence < 2) return null;

            int seen = 0;
            foreach (var b in behaviours)
            {
                if (b == null || b.GetType().Name != baseName) continue;
                seen++;
                if (seen == occurrence) return b;
            }
            return null;
        }

        /// <summary>The member a path segment names, from the same cached list and the same
        /// <see cref="Listed"/> gate the full pass uses — a live tile has to read back the very path
        /// the browser offered it. Compared on the SANITIZED name, which is the spelling a path
        /// carries.</summary>
        private static Member? FindListedMember(Type type, string segment, Options options, bool ownSurface)
        {
            foreach (var m in MembersOf(type))
                if (Listed(m, options, ownSurface) && (m.Name == segment || Sanitize(m.Name) == segment))
                    return m;
            return null;
        }

        /// <summary>Rebuilds the same nested shape <see cref="EntryFor"/> would have produced for this one
        /// path, so K2's flattener sees the identical dotted key whether it came from a full dump or
        /// a narrowed one.</summary>
        private static void InsertChain(Dictionary<string, object?> root, string[] chain, object? value)
        {
            var current = root;
            for (int i = 0; i < chain.Length - 1; i++)
            {
                if (current.TryGetValue(chain[i], out var existing) && existing is Dictionary<string, object?> dict)
                    current = dict;
                else
                {
                    var next = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current[chain[i]] = next;
                    current = next;
                }
            }
            current[chain[chain.Length - 1]] = value;
        }

        /// <summary>Public instance fields and properties declared by the script's own class chain
        /// (see the class comment for why it stops at <c>MonoBehaviour</c>), reduced to the leaf
        /// types <see cref="IsLeaf"/> allows. Capped per component so one script with a huge array
        /// field cannot crowd out every other script's values under <see cref="Options.MaxObjects"/>.</summary>
        /// <param name="find">When given, only branches whose member NAME contains it are kept (a
        /// matching member brings its whole subtree). Null lists everything.</param>
        private static Dictionary<string, object?> ReadMembers(MonoBehaviour behaviour, Options options,
                                                               string? find = null)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            var budget = new Budget { Left = options.MaxFieldsPerComponent };

            // Seeded with the script itself, so a member pointing back at it cannot start a second,
            // identical pass one level down.
            var visited = new HashSet<object>(ReferenceComparer.Instance) { behaviour };

            ReadInto(result, behaviour, MembersOf(behaviour.GetType()), ownSurface: true, options, 0,
                     visited, budget, find);
            return result;
        }

        /// <summary>One object's members into <paramref name="into"/>, shared by a script's own
        /// surface and every nested object below it.
        ///
        /// <para><b>Leaves first, then a fair share per nested object.</b> One budget spent in
        /// declaration order let the FIRST nested member take all of it. Measured 2026-09-17 on
        /// 7 Days to Die: <c>EntityPlayerLocal.playerInput</c> (the input bindings, dozens of action
        /// objects) used the whole 300 and the dump listed that one member — <c>Stats</c>, a few
        /// members further on, never got a single value. Now leaves may take at most half while
        /// nested objects wait, and each nested object gets an equal slice of what is left, with what
        /// it does not use passed on to the next.</para>
        ///
        /// <para>A search (<paramref name="find"/>) prunes instead, and pruning is its own bound: a
        /// branch without a match costs nothing, so slicing there would only starve the one branch
        /// that does match.</para></summary>
        private static void ReadInto(Dictionary<string, object?> into, object target, Member[] members,
                                     bool ownSurface, Options options, int depth, HashSet<object> visited,
                                     Budget budget, string? find)
        {
            var leaves = new List<Member>();
            var nested = new List<Member>();
            foreach (var m in members)
            {
                if (!Listed(m, options, ownSurface)) continue;
                if (IsLeaf(m.Type)) leaves.Add(m);
                else if (depth < options.MaxNestedDepth && CanDescendInto(m.Type)) nested.Add(m);
            }

            var leafBudget = new Budget
            {
                Left = nested.Count > 0 && find is null ? Math.Max(1, budget.Left / 2) : budget.Left,
            };
            foreach (var m in leaves)
            {
                if (leafBudget.Left <= 0) break;
                if (find is not null && !NameHas(m.Name, find)) continue;
                var captured = m;
                var converted = Convert(captured.Type, () => captured.Read(target), options, depth, visited, leafBudget);
                if (converted is NotLeaf) continue;
                into[Sanitize(captured.Name)] = converted;
                leafBudget.Left--;
                budget.Left--;
            }

            for (int i = 0; i < nested.Count && budget.Left > 0; i++)
            {
                var captured = nested[i];
                bool named = find is not null && NameHas(captured.Name, find);
                var slice = new Budget
                {
                    Left = find is null ? Math.Max(1, budget.Left / (nested.Count - i)) : budget.Left,
                };
                int granted = slice.Left;
                var converted = Convert(captured.Type, () => captured.Read(target), options, depth, visited,
                                        slice, named ? null : find);
                budget.Left -= granted - slice.Left;
                if (converted is NotLeaf) continue;
                into[Sanitize(captured.Name)] = converted;
            }
        }

        private static bool NameHas(string name, string find) =>
            name.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Whether <paramref name="find"/> names a member anywhere a dump could reach from
        /// this type, judged on DECLARED types so no value is read. Per-call memo: a search runs over
        /// tens of thousands of behaviours sharing a few hundred types.</summary>
        private static bool TypeMentions(Type type, string find, Options options, int depth, bool ownSurface,
                                         Dictionary<Type, bool>[] memo)
        {
            if (memo[depth].TryGetValue(type, out bool known)) return known;

            bool hit = false;
            foreach (var m in MembersOf(type))
            {
                if (!Listed(m, options, ownSurface)) continue;
                if (NameHas(m.Name, find)) { hit = true; break; }
                if (depth < options.MaxNestedDepth && !IsLeaf(m.Type) && CanDescendInto(m.Type) &&
                    TypeMentions(m.Type, find, options, depth + 1, ownSurface: false, memo))
                { hit = true; break; }
            }
            return memo[depth][type] = hit;
        }

        /// <summary>What one script type exposes, worked out ONCE and kept.
        ///
        /// <para>A type's member list cannot change while the game runs, but this used to re-derive it
        /// (<c>GetFields</c>/<c>GetProperties</c> for every level of every component's type chain) on
        /// every object of every snapshot — thousands of reflection enumerations per pass, which is
        /// most of what made a full dump expensive enough to stutter the game. Caching it turns each
        /// later pass into "read these known members", which is the part that actually has to happen
        /// live.</para></summary>
        private static Member[] MembersOf(Type type)
        {
            if (_memberCache.TryGetValue(type, out var cached)) return cached;

            var list = new List<Member>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            const BindingFlags publicFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            const BindingFlags privateFlags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            for (Type? t = type; t is not null && t != typeof(MonoBehaviour) && t != typeof(Behaviour) &&
                 t != typeof(Component) && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                // A PUBLICIZED assembly (7 Days to Die ships one) declares every field public and marks
                // the ones that were not with [PublicizedFrom]. Taken at face value, all of a script's
                // private state would sit behind the public-fields switch — measured 2026-09-17: on
                // EntityPlayerLocal every field vanished and only properties were left.
                foreach (var f in t.GetFields(publicFlags))
                    if (!f.IsStatic && seen.Add(f.Name)) list.Add(Member.Field(f, isPublic: !IsPublicized(f)));

                foreach (var f in t.GetFields(privateFlags))
                {
                    // A compiler-generated backing field ("<Health>k__BackingField") duplicates the
                    // auto-property read below, under a name nobody would ever type into a reading.
                    if (f.IsStatic || f.Name.Contains("<") || !seen.Add(f.Name)) continue;
                    list.Add(Member.Field(f, isPublic: false));
                }

                foreach (var p in t.GetProperties(publicFlags))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0 || !seen.Add(p.Name)) continue;
                    list.Add(Member.Property(p));
                }
            }

            var array = list.ToArray();
            _memberCache[type] = array;
            return array;
        }

        /// <summary>Matched by NAME: the attribute is generated into the game's own assembly, so there
        /// is no type to reference. <c>GetCustomAttributesData</c> reads metadata only — it never
        /// runs an attribute constructor. Only ever called while building the per-type cache.</summary>
        private static bool IsPublicized(FieldInfo f)
        {
            try
            {
                foreach (var a in f.GetCustomAttributesData())
                    if (a.Constructor.DeclaringType?.Name == "PublicizedFromAttribute") return true;
            }
            catch
            {
                // Unresolvable attribute metadata: take the field as declared.
            }
            return false;
        }

        /// <summary>Whether a member is read at all. The public-fields switch exists because a
        /// SCRIPT's public fields are Inspector slots (prefabs, colours, other objects), so it applies
        /// to the script's own surface only. Inside one of the game's plain objects a public field IS
        /// the data — <c>EntityStats.Health</c> is one — and gating it there hid exactly what the
        /// descent was added to reach.</summary>
        private static bool Listed(Member m, Options options, bool ownSurface) =>
            !m.IsField || (m.IsPublic ? !ownSurface || options.IncludePublicFields : options.IncludePrivateFields);

        /// <summary>Only ever touched from Unity's main thread (both snapshot paths run in
        /// <c>Update</c>), so it needs no lock.</summary>
        private static readonly Dictionary<Type, Member[]> _memberCache = new();

        /// <summary>Wanted path -> the object it named, kept between ticks. Entries go stale the way
        /// Unity objects do (destroyed reads as null) and are simply re-swept for. Main thread only,
        /// like <see cref="_memberCache"/>.</summary>
        private static readonly Dictionary<string, GameObject> _resolved = new(StringComparer.Ordinal);

        private static float _nextSweepAt;
        private const float SweepBackoffSeconds = 1f;

        private readonly struct Member
        {
            private readonly FieldInfo? _field;
            private readonly PropertyInfo? _property;

            private Member(FieldInfo? field, PropertyInfo? property, bool isPublic)
            {
                _field = field;
                _property = property;
                IsPublic = isPublic;
            }

            internal static Member Field(FieldInfo f, bool isPublic) => new(f, null, isPublic);
            internal static Member Property(PropertyInfo p) => new(null, p, true);

            internal bool IsField => _field is not null;
            internal bool IsPublic { get; }
            internal string Name => _field?.Name ?? _property!.Name;
            internal Type Type => _field?.FieldType ?? _property!.PropertyType;
            internal object? Read(object target) => _field is not null ? _field.GetValue(target)
                                                                       : _property!.GetValue(target);
        }

        private sealed class NotLeaf { internal static readonly NotLeaf Instance = new(); }

        /// <summary>One component's remaining allowance of emitted values, shared by every level of
        /// the descent. Per-level caps would multiply out (300 x 300 x ...); one budget cannot.</summary>
        private sealed class Budget { internal int Left; }

        /// <summary>Whether a value of this type is one of the GAME's own objects, worth taking
        /// apart. Everything the framework or the engine declares is refused: collections, delegates,
        /// reflection handles and engine references are either loops waiting to happen or noise, and
        /// the Unity structs anyone actually wants (Vector3, Color...) are already spelled out as
        /// leaves above. A null namespace - a type declared outside any namespace, which is how a lot
        /// of game code is written, 7 Days to Die included - counts as the game's own.</summary>
        private static bool CanDescendInto(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t.IsArray || t.IsPointer) return false;
            if (typeof(Delegate).IsAssignableFrom(t)) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;

            string? ns = t.Namespace;
            if (ns is null) return true;
            return !ns.StartsWith("System", StringComparison.Ordinal) &&
                   !ns.StartsWith("UnityEngine", StringComparison.Ordinal) &&
                   !ns.StartsWith("Unity.", StringComparison.Ordinal) &&
                   !ns.StartsWith("Microsoft", StringComparison.Ordinal);
        }

        /// <summary>Identity, not <c>Equals</c>: a game type is free to define value equality, and a
        /// cycle guard that believed it would drop objects that merely LOOK alike.</summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new();
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>Turns one member into something JSON can carry: a scalar, one of the small Unity
        /// structs spelled out below, or - when it is one of the game's own objects and there is
        /// depth left - a nested block of the same treatment applied to ITS members.
        ///
        /// <para><b>Why descending is safe now.</b> The original refusal to recurse named a real
        /// danger: an arbitrary object graph walks back into the scene (a script holding its own
        /// manager, its transform's owner...) and never ends. But every route back into the scene
        /// runs through a <see cref="UnityEngine.Object"/>, so refusing to follow one of those cuts
        /// that whole class of loop at the root instead of trying to survive it. What is left - plain
        /// CLR objects referencing each other in a ring - is caught by identity
        /// (<paramref name="visited"/>), and runaway breadth by a <see cref="Budget"/> shared across
        /// the entire component. <see cref="CanDescendInto"/> keeps the walk inside types the GAME
        /// declared, which is where game state lives.</para></summary>
        private static object? Convert(Type type, Func<object?> getter, Options options, int depth,
                                        HashSet<object>? visited, Budget budget, string? find = null)
        {
            try
            {
                if (type == typeof(Vector2)) return VectorToDict((Vector2)getter()!);
                if (type == typeof(Vector3)) return VectorToDict((Vector3)getter()!);
                if (type == typeof(Vector4)) { var v = (Vector4)getter()!; return new Dictionary<string, object?> { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w }; }
                if (type == typeof(Quaternion)) { var q = (Quaternion)getter()!; return new Dictionary<string, object?> { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w }; }
                if (type == typeof(Color)) { var c = (Color)getter()!; return new Dictionary<string, object?> { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a }; }
                if (type == typeof(Color32)) { var c = (Color32)getter()!; return new Dictionary<string, object?> { ["r"] = (int)c.r, ["g"] = (int)c.g, ["b"] = (int)c.b, ["a"] = (int)c.a }; }
                if (IsLeaf(type)) return getter();

                if (depth >= options.MaxNestedDepth || !CanDescendInto(type)) return NotLeaf.Instance;

                object? value = getter();
                // The static TYPE is checked first so a hopeless member costs no read at all; the
                // RUNTIME type is checked again because a field declared as one of the game's base
                // classes can still hand back an engine object.
                if (value is null || value is UnityEngine.Object) return NotLeaf.Instance;
                Type runtime = value.GetType();
                if (!CanDescendInto(runtime)) return NotLeaf.Instance;

                visited ??= new HashSet<object>(ReferenceComparer.Instance);
                bool tracked = !runtime.IsValueType;
                if (tracked && !visited.Add(value)) return NotLeaf.Instance;

                var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                ReadInto(nested, value, MembersOf(runtime), ownSurface: false, options, depth + 1, visited,
                         budget, find);

                // Released rather than left behind: the same object reached twice by DIFFERENT routes
                // is not a cycle, and refusing the second route would make what a dump contains
                // depend on the order members happen to be declared in.
                if (tracked) visited.Remove(value);

                return nested.Count > 0 ? nested : NotLeaf.Instance;
            }
            catch
            {
                // A property getter can throw for reasons that have nothing to do with this dump
                // (not initialized yet, requires main-thread state that is not ready this frame) —
                // one bad member must not blank the rest of the script's fields.
                return NotLeaf.Instance;
            }
        }

        private static Dictionary<string, object?> VectorToDict(Vector3 v) =>
            new(StringComparer.Ordinal) { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };

        /// <summary>K2's own flattener (<c>GameLinkReader.Flatten</c>) splits a path on <c>'.'</c> and
        /// reads array indices as <c>[i]</c> — a GameObject or field NAME containing either would
        /// corrupt the path it appears in on the K2 side. Replaced, never rejected: a stray character
        /// in one name should not cost the whole object its entry.</summary>
        private static string Sanitize(string name)
        {
            if (name.IndexOfAny(BadChars) < 0) return name;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(Array.IndexOf(BadChars, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private static readonly char[] BadChars = { '.', '[', ']' };
    }
}
