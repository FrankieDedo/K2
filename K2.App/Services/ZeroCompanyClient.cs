using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// Reads Star Wars: Zero Company's live tactical state — and sends its squad commands — for the
/// DisplayPad's <c>dp_zcstatus</c> tiles.
///
/// <para>
/// <b>Where the state comes from.</b> The game is Unreal Engine 5.6 and ships Epic's own
/// <c>WebRemoteControl</c> plugin compiled into the retail build. Once it is switched on (see
/// <see cref="EnsureRemoteControlEnabled"/>) the game itself serves a local HTTP API on
/// 127.0.0.1:30010 — <c>/remote/object/call</c> invokes a Blueprint function,
/// <c>/remote/object/property</c> reads a property. So this is the game's own supported surface,
/// like Elite's <c>Status.json</c>: no hooking, no memory reading, nothing injected. Unlike
/// Elite's file it is also a COMMAND channel, which is why a Zero Company key can select a squad
/// member for real instead of pressing a keystroke someone guessed.
/// </para>
///
/// <para>
/// <b>The entry chain, and why it is not optional.</b> Runtime objects are addressed by path and
/// the paths contain the map name, so nothing here can be a constant. The one fixed anchor is the
/// game instance, <c>/Engine/Transient.GameEngine_0:BP_BrunoGameInstance_C_0</c>: it is a valid
/// world-context object, so <c>GameplayStatics</c> resolves the controller, the game state and the
/// tactical actor off it. <see cref="Resolve"/> walks that chain once per mission and caches it,
/// re-walking whenever the level name changes or a call starts failing.
/// </para>
///
/// <para>
/// <b>Cadence and threading.</b> The pad's 1 Hz tick must never wait on a socket, so
/// <see cref="Snapshot"/> is a pure cache read and a background poll refreshes it about once a
/// second while a Zero Company tile is actually on a pad (<see cref="Wake"/> is called from the
/// live-tile service each tick; the poller sleeps itself away when no one asks). A full refresh is
/// a handful of sub-millisecond loopback calls.
/// </para>
///
/// <para>
/// <b>Gotchas paid for in blood, do not re-derive.</b> (1) The GAS getter
/// <c>GetGameplayAttributeValue</c> takes an <c>FGameplayAttribute</c>, which cannot be
/// reconstructed from JSON: it silently returns 0 for every attribute. Values must be read as
/// properties off the attribute-set OBJECT (<c>GetAttributeSet</c>), which answers
/// <c>{BaseValue, CurrentValue}</c> — and that pair is a gift, because "3 of 3 AP" needs both.
/// (2) <c>/remote/object/property</c> works on actors but 400s on some engine singletons, so a
/// 400 is not a reason to declare the API broken. (3) There is no end-turn function exposed
/// anywhere on the turn director; <c>SetTeamTurn</c>/<c>PerformTeamSwitch</c> exist but drive the
/// team state machine directly, so they are deliberately NOT offered as a command.
/// </para>
/// </summary>
internal static class ZeroCompanyClient
{
    private const string Host = "http://127.0.0.1:30010";

    /// <summary>The one path in this file that is a constant — see the class remarks.</summary>
    private const string GameInstance = "/Engine/Transient.GameEngine_0:BP_BrunoGameInstance_C_0";

    private const string GameplayStatics = "/Script/Engine.Default__GameplayStatics";
    private const string TacticalStateClass =
        "/Game/Game/Core/Blueprints/BP_BrunoTacticalGameState.BP_BrunoTacticalGameState_C";
    private const string HealthSet = "/Script/BitReactorGame.BitReactorHealthSet";
    private const string CombatSet = "/Script/BitReactorGame.BitReactorCombatSet";

    /// <summary>One squad member as the tiles need it. <paramref name="MaxAp"/> is the attribute's
    /// BaseValue and <paramref name="Ap"/> its CurrentValue — the game spends AP by moving the
    /// current value, so the pair is the "2 of 3" a tile shows.</summary>
    internal readonly record struct Unit(string Path, string Name,
                                         int Hp, int MaxHp, int Ap, int MaxAp, int Armor);

    /// <summary>One ability of the selected soldier, as the tiles need it. <paramref name="GameKey"/>
    /// is the digit the game itself binds this ability to on the action wheel (1…9, then 0 for a
    /// tenth), or -1 beyond that. A DisplayPad ability key PRESSES this digit rather than activating
    /// the ability over the API: going through the wheel's own input is the only path on which the
    /// game's turn/menu state machine stays consistent — activating it out-of-band left the wheel
    /// half-drawn and the turn stuck once the action finished (user report 2026-09-07).</summary>
    internal readonly record struct Ability(int Handle, string Name, string ClassPath, int GameKey);

    /// <summary>One reading of the tactical state. <see cref="Valid"/> is false whenever we are
    /// not in a mission (or the API is off): tiles then paint "unknown" rather than claiming a
    /// squad that isn't deployed.</summary>
    internal readonly record struct Status(bool Valid, bool PlayerTurn, int Round, int GameTurn,
                                           IReadOnlyList<Unit> Units, int SelectedIndex, bool CanAct,
                                           IReadOnlyList<Ability> Abilities)
    {
        /// <summary>Ability in bar slot <paramref name="index"/> (0-based), or null when the
        /// selected soldier has fewer than that — an empty slot, not an error.</summary>
        public Ability? AbilityAt(int index) =>
            index >= 0 && index < Abilities.Count ? Abilities[index] : null;

        public Unit? Selected =>
            SelectedIndex >= 0 && SelectedIndex < Units.Count ? Units[SelectedIndex] : null;

        public Unit? At(int index) =>
            index >= 0 && index < Units.Count ? Units[index] : null;
    }

    private static readonly Status Unknown =
        new(false, false, 0, 0, Array.Empty<Unit>(), -1, false, Array.Empty<Ability>());

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(900) };
    private static readonly object _gate = new();

    private static Status _last = Unknown;
    private static DateTime _lastWantedUtc = DateTime.MinValue;
    private static Task? _poller;

    // Resolved once per mission — see Resolve().
    private static string? _level, _pc, _tactical, _squad, _selComp;

    /// <summary>Highest action-point capacity seen per soldier this mission — see
    /// <see cref="MaxApFor"/>. Cleared with the paths, so it never carries a previous mission's
    /// numbers into a new one.</summary>
    private static readonly Dictionary<string, int> _maxAp = new();

    /// <summary>Latest reading. Never blocks: the poll runs on its own task.</summary>
    internal static Status Snapshot()
    {
        lock (_gate) { _lastWantedUtc = DateTime.UtcNow; }
        EnsurePoller();
        lock (_gate) { return _last; }
    }

    /// <summary>Called by the live-tile tick so the poller knows a Zero Company tile is on a pad
    /// right now. Without a caller the poller stops after a few seconds instead of hammering a
    /// game nobody is watching.</summary>
    internal static void Wake() => Snapshot();

    private static void EnsurePoller()
    {
        lock (_gate)
        {
            if (_poller is { IsCompleted: false }) return;
            _poller = Task.Run(PollLoop);
        }
    }

    private static async Task PollLoop()
    {
        while (true)
        {
            lock (_gate)
            {
                // Nobody has asked for a snapshot recently: the page moved off the Zero Company
                // tiles (or the pad went away). Stop rather than poll a game in the background.
                if (DateTime.UtcNow - _lastWantedUtc > TimeSpan.FromSeconds(5)) { _poller = null; return; }
            }

            try
            {
                var s = await ReadAsync().ConfigureAwait(false);
                lock (_gate) { _last = s; }
            }
            catch
            {
                lock (_gate) { _last = Unknown; }
                InvalidatePaths();
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }
    }

    // ─────────────────────────── Reading ───────────────────────────

    private static async Task<Status> ReadAsync()
    {
        if (!await Resolve().ConfigureAwait(false)) return Unknown;

        string? teamTurn = await CallStringAsync(_tactical!, "GetTeamTurn").ConfigureAwait(false);
        int round    = await CallIntAsync(_tactical!, "GetRoundNumber").ConfigureAwait(false);
        int gameTurn = await CallIntAsync(_tactical!, "GetGameTurn").ConfigureAwait(false);

        // The team whose turn it is comes back as a class path; the player's team is the one
        // asset named PlayerTeam. Matching on the name rather than on a full path keeps this
        // working if the asset ever moves folder.
        bool playerTurn = teamTurn is { Length: > 0 } &&
                          teamTurn.Contains("PlayerTeam", StringComparison.OrdinalIgnoreCase);

        var units = new List<Unit>();
        foreach (string path in await SquadMembersAsync().ConfigureAwait(false))
            units.Add(await ReadUnitAsync(path).ConfigureAwait(false));

        string? selected = await CallStringAsync(_selComp!, "GetSelectedCharacter").ConfigureAwait(false);
        bool canAct = await CallBoolAsync(_selComp!, "CanSelectedCharacterAct").ConfigureAwait(false);
        int selIndex = selected is { Length: > 0 }
            ? units.FindIndex(u => string.Equals(u.Path, selected, StringComparison.Ordinal))
            : -1;

        var abilities = selected is { Length: > 0 }
            ? await CachedWheelAbilitiesAsync(selected).ConfigureAwait(false)
            : Array.Empty<Ability>();

        return new Status(true, playerTurn, round, gameTurn, units, selIndex, canAct, abilities);
    }

    private static async Task<Unit> ReadUnitAsync(string path)
    {
        string asc = path + ".AbilitySystemComponent";
        string? health = await CallStringAsync(asc, "GetAttributeSet",
            new { AttributeSetClass = HealthSet }).ConfigureAwait(false);
        string? combat = await CallStringAsync(asc, "GetAttributeSet",
            new { AttributeSetClass = CombatSet }).ConfigureAwait(false);

        (int Current, int Base) hp = default, maxHp = default;
        if (health is { Length: > 0 })
        {
            hp = await AttrAsync(health, "Health").ConfigureAwait(false);
            maxHp = await AttrAsync(health, "MaxHealth").ConfigureAwait(false);
        }

        (int Current, int Base) ap = default, refreshAp = default, armor = default;
        if (combat is { Length: > 0 })
        {
            ap = await AttrAsync(combat, "ActionPoints").ConfigureAwait(false);
            refreshAp = await AttrAsync(combat, "RefreshActionPoints").ConfigureAwait(false);
            armor = await AttrAsync(combat, "CurrentArmor").ConfigureAwait(false);
        }

        return new Unit(path, PrettyName(path),
                        hp.Current, Math.Max(maxHp.Current, hp.Current),
                        ap.Current, MaxApFor(path, ap, refreshAp), armor.Current);
    }

    /// <summary>How many action points the soldier HAS, as opposed to how many are left.
    ///
    /// <para>Spending a point moves both <c>CurrentValue</c> and <c>BaseValue</c> of
    /// <c>ActionPoints</c> down, so neither of them is the maximum — reading it off that pair is
    /// what made the tile's bars vanish one by one instead of going dark (user report,
    /// 2026-09-05). <c>RefreshActionPoints</c> — the number handed back at the start of a turn —
    /// is the real capacity, and the high-water mark seen this mission is the backstop for a
    /// soldier whose refresh attribute reads zero.</para></summary>
    private static int MaxApFor(string path, (int Current, int Base) ap,
                                (int Current, int Base) refreshAp)
    {
        int candidate = Math.Max(Math.Max(refreshAp.Current, refreshAp.Base),
                                 Math.Max(ap.Current, ap.Base));

        lock (_gate)
        {
            if (_maxAp.TryGetValue(path, out int seen) && seen > candidate) return seen;
            if (candidate > 0) _maxAp[path] = candidate;
        }

        // Nothing known yet — the attributes read zero for a moment at the start of a mission,
        // before the game has granted the turn's points. Falling back to 1 there drew a single
        // bar in the middle of the tile and hid the other two (user report, 2026-09-05), which
        // looks like a soldier with one action point rather than like "not known yet". The usual
        // capacity is the honest guess, and it is replaced by the real number a tick later.
        return candidate > 0 ? candidate : DefaultApBars;
    }

    /// <summary>Bars to draw while the game has not said how many action points a soldier has.
    /// Three is what every squad member seen so far starts a turn with.</summary>
    private const int DefaultApBars = 3;

    /// <summary>One GAS attribute as the pair the game keeps: BaseValue is the unspent maximum,
    /// CurrentValue what is left. See the class remarks for why this is not read with
    /// <c>GetGameplayAttributeValue</c>.</summary>
    private static async Task<(int Current, int Base)> AttrAsync(string setPath, string attribute)
    {
        var doc = await PutAsync("/remote/object/property", new
        {
            objectPath = setPath,
            access = "READ_ACCESS",
            propertyName = attribute,
        }).ConfigureAwait(false);

        if (doc is null || !doc.RootElement.TryGetProperty(attribute, out var v)) return (0, 0);
        int cur  = v.TryGetProperty("CurrentValue", out var c) ? (int)Math.Round(c.GetDouble()) : 0;
        int base_ = v.TryGetProperty("BaseValue", out var b) ? (int)Math.Round(b.GetDouble()) : cur;
        return (cur, base_);
    }

    /// <summary>The wheel for one soldier, read once per selection and then cached until the
    /// selection changes.
    ///
    /// <para>Reading it costs three calls per wedge plus the ability-system property — around
    /// forty round trips. It used to also refresh on a slow heartbeat "to catch an ability
    /// learned mid-mission", but reading the wheel drives the game's own radial menu: the burst
    /// of <c>Get Wedge by Index</c> calls sweeps the hover across every wedge, and with a soldier
    /// selected but the wheel closed the game pulled the menu back up on every heartbeat (user
    /// report 2026-09-07). A soldier's wheel only really changes when the SELECTION changes, so
    /// that is the only thing that now triggers a re-read; an empty result is retried (the read
    /// can land while the menu widgets are mid-teardown).</para></summary>
    private static async Task<IReadOnlyList<Ability>> CachedWheelAbilitiesAsync(string characterPath)
    {
        lock (_gate)
        {
            if (string.Equals(_wheelFor, characterPath, StringComparison.Ordinal) && _wheel.Count > 0)
                return _wheel;
        }

        var fresh = await WheelAbilitiesAsync(characterPath).ConfigureAwait(false);

        lock (_gate)
        {
            _wheelFor = characterPath;
            _wheel = fresh;
        }
        return fresh;
    }

    private static string? _wheelFor;
    private static IReadOnlyList<Ability> _wheel = Array.Empty<Ability>();

    /// <summary>The abilities on the soldier's action wheel, in the order the GAME numbers them.
    ///
    /// <para><b>Why not from the ability system.</b> <c>ActivatableAbilities</c> lists everything
    /// granted — 41 entries on a typical soldier — and nothing in it separates the abilities a
    /// player presses from the passives, the listeners and the turn-start plumbing: the native
    /// properties that would say so (AbilityTags, CostGameplayEffectClass) all 400 on the CDO,
    /// and <c>ActiveCount</c> is zero across the board. Filtering by name was a guess, and the
    /// guess was wrong — it kept mostly passives (user report, 2026-09-06).</para>
    ///
    /// <para><b>So we ask the game's own UI, slice by slice.</b> The radial menu is built from
    /// <c>WBP_RadialSlice</c> widgets — the standard move/shot/overwatch, the class abilities and
    /// the utility items — each of which answers <c>Get Wedge by Index</c> with the view model of
    /// the ability in that position. Walking the slices in <see cref="SliceOrder"/> reproduces the
    /// game's own 1…9,0 numbering, verified against a live wheel: three standard actions, four
    /// class abilities, three utilities. The call-for-backup slice is skipped — the game keeps it
    /// off the numbered wheel (see <see cref="SliceOrder"/>).</para>
    ///
    /// <para><b>The slice order is NOT the order the wedges enumerate in.</b> Asking UMG for every
    /// wedge returns them utility-first, which is the reverse of how the game numbers them — a
    /// key labelled 1 on the pad would have opened the ability the game calls 8. That is the
    /// whole reason this goes through the slices instead of the flat wedge list.</para></summary>
    private static async Task<IReadOnlyList<Ability>> WheelAbilitiesAsync(string characterPath)
    {
        var slices = await CallArrayAsync(WidgetLibrary, "GetAllWidgetsOfClass", new
        {
            WorldContextObject = GameInstance,
            WidgetClass = SliceWidgetClass,
            TopLevelOnly = false,
        }).ConfigureAwait(false);
        if (slices.Count == 0) return Array.Empty<Ability>();

        var handles = await ActivatableAbilitiesAsync(characterPath).ConfigureAwait(false);
        var result = new List<Ability>();

        foreach (string sliceName in SliceOrder)
        {
            string? slice = slices.FirstOrDefault(
                s => s.EndsWith("." + sliceName, StringComparison.Ordinal));
            if (slice is null) continue;

            for (int index = 0; index < MaxWedgesPerSlice; index++)
            {
                using var wedge = await CallAsync(slice, "Get Wedge by Index",
                                                  new { Index = index }).ConfigureAwait(false);
                if (wedge is null ||
                    !wedge.RootElement.TryGetProperty("Found Wedge", out var found) ||
                    found.ValueKind != JsonValueKind.True)
                    break;   // past the end of this slice

                // The out parameter is called "Hovered Ability" — it is simply the view model of
                // the ability sitting at that index, hovered or not.
                if (!wedge.RootElement.TryGetProperty("Hovered Ability", out var vmValue) ||
                    vmValue.GetString() is not { Length: > 0 } vm)
                    continue;

                string? instance = await CallStringAsync(vm, "GetAbility").ConfigureAwait(false);
                if (instance is null or "") continue;

                // The view model hands back the ability INSTANCE on the pawn
                // ("…Char_Hero_X.GA_StandardMove_C_3"); everything downstream keys on the class.
                string cls = ClassOfAbilityInstance(instance, handles.Keys);
                if (cls.Length == 0) continue;

                // SliceOrder reproduces the game's own 1…9,0 numbering, so this ability's wheel
                // position IS the digit the game bound it to (10th -> "0", past that -> none).
                int pos = result.Count + 1;
                int gameKey = pos <= 9 ? pos : pos == 10 ? 0 : -1;
                result.Add(new Ability(handles.GetValueOrDefault(cls), AbilityName(cls), cls, gameKey));
            }
        }
        // Call for Backup / Reinforcements is dropped: the game does NOT bind it to a wheel digit
        // (it has its own dedicated key), so a tile pressing this slot's number fired whatever the
        // game actually has on that digit instead — a wrong action, worse than a missing one (user
        // report 2026-09-07). If it ever needs to come back it wants its real key, not a wheel
        // position.
        result.RemoveAll(
            a => a.ClassPath.Contains("CallForBackup", StringComparison.OrdinalIgnoreCase));

        return result;
    }

    /// <summary>The wheel's slices in the order the game NUMBERS them (1…9,0), which is not the
    /// order they are laid out or enumerated in. CallForBackupSlice is deliberately absent: the
    /// game does not put it on the numbered wheel, so walking it only added a mis-keyed tile and
    /// extra <c>Get Wedge by Index</c> calls to the sweep.</summary>
    private static readonly string[] SliceOrder =
        { "StandardActionSlice", "ClassAbilitySlice", "UtilitySlice" };

    /// <summary>Guard on the per-slice walk: the widest slice seen holds six wedges, and the loop
    /// stops on the first index the slice does not have anyway.</summary>
    private const int MaxWedgesPerSlice = 10;

    /// <summary>Class path of an ability from its INSTANCE path: the instance is named after its
    /// class with a suffix ("GA_StandardMove_C_3"), so the class is matched by name among the
    /// ones the ability system granted — which is also where its handle comes from.</summary>
    private static string ClassOfAbilityInstance(string instancePath, IEnumerable<string> knownClasses)
    {
        string leaf = instancePath.Split('.')[^1];

        // "GA_StandardMove_C_3" -> "GA_StandardMove"
        int marker = leaf.LastIndexOf("_C_", StringComparison.Ordinal);
        string stem = marker > 0 ? leaf[..marker] : leaf;

        foreach (string cls in knownClasses)
            if (string.Equals(AbilityObjectName(cls), stem, StringComparison.OrdinalIgnoreCase))
                return cls;
        return "";
    }

    /// <summary>The radial menu's per-slice widget.</summary>
    private const string SliceWidgetClass =
        "/Game/Game/UI/Tactical/RadialAbilityMenu/WBP_RadialSlice.WBP_RadialSlice_C";

    /// <summary>Spec handle of every granted ability, keyed by its class path — the other half of
    /// what <see cref="WheelAbilitiesAsync"/> needs, since the wheel names the ability but only
    /// the ability system knows the handle that activates it.</summary>
    private static async Task<Dictionary<string, int>> ActivatableAbilitiesAsync(string characterPath)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);

        var doc = await PutAsync("/remote/object/property", new
        {
            objectPath = characterPath + ".AbilitySystemComponent",
            access = "READ_ACCESS",
            propertyName = "ActivatableAbilities",
        }).ConfigureAwait(false);

        if (doc is null ||
            !doc.RootElement.TryGetProperty("ActivatableAbilities", out var container) ||
            !container.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Ability", out var a) || a.GetString() is not { Length: > 0 } cls) continue;
            int handle = item.TryGetProperty("Handle", out var h) &&
                         h.TryGetProperty("Handle", out var hv) ? hv.GetInt32() : 0;
            map[cls] = handle;
        }
        return map;
    }

    /// <summary>UMG's static library — how a widget is found without an actor to hang off.</summary>
    private const string WidgetLibrary = "/Script/UMG.Default__WidgetBlueprintLibrary";

    /// <summary>The radial menu's per-ability widget.</summary>
    private const string WedgeWidgetClass =
        "/Game/Game/UI/Tactical/RadialAbilityMenu/WBP_RadialAbilityWedge.WBP_RadialAbilityWedge_C";

    /// <summary>Folder the game's own ability icons are expected in, exported as PNG.
    ///
    /// <para><b>Why they are not read from the game.</b> The artwork is in the game's IoStore
    /// container (<c>pakchunk*.utoc/.ucas</c>, 38 GB) and the remote-control API serves object
    /// properties, not texture pixels — it will happily NAME the icon of an ability
    /// (<c>/Game/Game/UI/Icons/Abilities/T_UI_Abilities_ForcePush</c>) but cannot hand it over.
    /// Extracting them is a job for a container reader such as FModel, run once.</para>
    ///
    /// <para>A file is looked up by the ability's own object name with the <c>GA_</c> prefix and
    /// the tier suffix removed — <c>GA_ForcePush_T2</c> → <c>ForcePush.png</c> — which is also how
    /// the game names its icons, so an export can be dropped in unrenamed. A missing file is not
    /// an error: that key falls back to its caption.</para></summary>
    internal static string AbilityIconDir => Path.Combine(
        AppContext.BaseDirectory, "Assets", "GameProfiles", "ZeroCompany", "Abilities");

    /// <summary>The profile's own art folder, whose SUB-folders are all searched for icons —
    /// exports come out of the game in several folders (Abilities, Utilities, Utilities/Images)
    /// and there is no reason to make the user flatten them into one. Files sitting directly in
    /// this folder are skipped: that is where the tile frame art lives, not icons.</summary>
    private static string ProfileArtDir => Path.Combine(
        AppContext.BaseDirectory, "Assets", "GameProfiles", "ZeroCompany");

    /// <summary>Icon file for an ability, or null when nothing in the folder matches it.
    ///
    /// <para>The game does NOT name its icons after its ability classes, so an exact match is
    /// only the first of four attempts. Real cases from a single export:
    /// <list type="bullet">
    /// <item><c>GA_RocketStrike_T4</c> → <c>RocketStrike.png</c> — exact;</item>
    /// <item><c>GA_StandardShot_BlasterRifle</c> → <c>StandardShot.png</c> — the weapon suffix is
    ///   part of the ability, not of the icon;</item>
    /// <item><c>GA_Overwatch_Rifle</c> → <c>Overwatch.png</c> — same;</item>
    /// <item><c>GA_ConcussionGrenade_T2</c> → <c>Rex_ConcussionGrenade.png</c> — the export
    ///   carries a character prefix;</item>
    /// <item><c>GA_StandardMove</c> → <c>Move.png</c> — nothing shared to match on, hence the
    ///   alias table.</item>
    /// </list>
    /// Each rule is narrower than a plain "contains" would be, because a loose match here puts
    /// the WRONG picture on a key, which is worse than no picture at all.</para></summary>
    internal static string? AbilityIconPath(Ability ability)
    {
        try
        {
            string key = AbilityIconKey(ability.ClassPath);
            if (key.Length == 0) return null;

            var files = IconFiles();
            if (files.Count == 0) return null;

            // 0. a mapping made from the ability's DISPLAYED name, which is the one thing the
            //    wheel and the user agree on. It comes first, and beats every rule below: these
            //    were read off a live wheel, while the rest is inference from class names — and
            //    the class name can be someone else's entirely (Electroweb Overload is a
            //    PowerSurge class, and matched the PowerSurge icon, which is a different
            //    ability).
            if (NameAliases.TryGetValue(NameKey(ability.Name), out string? byName) &&
                files.TryGetValue(byName, out string? namedIcon)) return namedIcon;

            // 1. the ability's own stem, with or without the game's texture prefix
            if (files.TryGetValue(key, out string? exact)) return exact;

            // 2. an alias for the handful the game names differently
            if (IconAliases.TryGetValue(key, out string? alias) &&
                files.TryGetValue(alias, out string? aliased)) return aliased;

            // 3. drop a trailing weapon/variant word: StandardShot_BlasterRifle -> StandardShot
            string head = AbilityIconKeyHead(ability.ClassPath);
            if (head.Length > 0 && files.TryGetValue(head, out string? byHead)) return byHead;
            if (IconAliases.TryGetValue(head, out string? headAlias) &&
                files.TryGetValue(headAlias, out string? byHeadAlias)) return byHeadAlias;

            // 4. a file whose name ENDS with the stem, which is how the character-prefixed
            //    exports look (Rex_ConcussionGrenade). Anchored at the end on purpose: a plain
            //    "contains" would let Strike match VibroswordStrike.
            foreach (var (name, path) in files)
                if (name.EndsWith(key, StringComparison.OrdinalIgnoreCase) &&
                    name.Length > key.Length && name[^(key.Length + 1)] == '_')
                    return path;

            return null;
        }
        catch { return null; }
    }

    /// <summary>Icons pinned by the ability's DISPLAYED name — the wheel's own label, stripped of
    /// spaces and case. Read off a live wheel (2026-09-06) and authoritative: whatever the class
    /// is called, this is the picture the game puts on that action.</summary>
    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELFREPAIR"]         = "SelfHeal",
        ["ENHANCEDSCANNERS"]   = "EnhancedScanner",
        ["SMOKEBOMB"]          = "SmokeGrenade",
        ["SMOKEGRENADE"]       = "SmokeGrenade",
        ["MEDIKIT"]            = "Medkit",
        ["MEDKIT"]             = "Medkit",
        ["BRUTALSTRIKE"]       = "MeleeStrike",
        // The two Electrowebs wear the status effect each one inflicts, from a folder of their
        // own. Overload's class is PowerSurge, so nothing but the name gets this right.
        ["ELECTROWEBOVERLOAD"] = "StatusEffects_Webbed",
        // ...and the same entry under the name K2 actually has for it. Ability.Name is
        // BUILT from the class ("GA_PowerSurge_T2" -> "POWER SURGE"): the game exposes no
        // displayed name over the remote API, so the alias above can never fire for this
        // one and the key kept matching PowerSurge.png through rule 1.
        ["POWERSURGE"]         = "StatusEffects_Webbed",
        ["ELECTROWEBDART"]     = "StatusEffects_Darted",
    };

    /// <summary>An ability's displayed name reduced to a lookup key: letters and digits only, so
    /// "Smoke Bomb", "SMOKE BOMB" and "Smoke-Bomb" are one key.</summary>
    private static string NameKey(string name) =>
        new string((name ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The few abilities whose icon the game names differently, with nothing in common
    /// to match on. Kept as data rather than as special cases in the lookup.</summary>
    private static readonly Dictionary<string, string> IconAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StandardMove"]  = "Move",
        ["StandardShot"]  = "StandardShot",
        ["FreeMove"]      = "Move",

        // Named nothing like their icons — confirmed against a live wheel (2026-09-06). Brutal
        // Strike borrows the generic melee glyph, and the Electroweb dart is drawn with the
        // status effect it inflicts, which lives in a different icon folder entirely.
        ["BrutalStrike"]   = "MeleeStrike",

        // The two Electroweb abilities are drawn with the status effect each one inflicts, which
        // lives in a different icon folder entirely: the dart darts, the overload webs. Overload
        // was landing on PowerSurge through the generic match, which is a different ability.
        ["ElectrowebDart"]     = "StatusEffects_Darted",
        ["ElectrowebOverload"] = "StatusEffects_Webbed",

        // Spelling drift between the ability and its texture, mapped from a live wheel
        // (2026-09-06): plural vs singular, British vs American, "repair" vs "heal".
        ["SelfRepair"]        = "SelfHeal",
        ["EnhancedScanners"]  = "EnhancedScanner",
        ["Medikit"]           = "Medkit",
        ["SmokeBomb"]         = "SmokeGrenade",
        // The icon drops the "Combat": GA_CombatRecon_T1 -> T_UI_Passives_Recon.
        ["CombatRecon"]  = "Recon",
    };

    /// <summary>Every PNG in the icon folder, keyed by its name with the game's
    /// <c>T_UI_Abilities_</c> prefix removed so an un-renamed export matches too. Re-read when
    /// the folder's timestamp moves, so dropping new icons in does not need a restart.</summary>
    private static IReadOnlyDictionary<string, string> IconFiles()
    {
        try
        {
            // Stamped on the deepest write time under the art folder, so dropping icons into a
            // sub-folder invalidates the cache too.
            var stamp = Directory.Exists(ProfileArtDir)
                ? Directory.GetDirectories(ProfileArtDir, "*", SearchOption.AllDirectories)
                      .Select(Directory.GetLastWriteTimeUtc)
                      .Append(Directory.GetLastWriteTimeUtc(ProfileArtDir))
                      .Max()
                : DateTime.MinValue;

            lock (_gate)
                if (_iconFiles is not null && stamp == _iconStamp) return _iconFiles;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Every sub-folder of the profile's art folder, so an export can keep the shape it
            // came out of the game with.
            if (Directory.Exists(ProfileArtDir))
                foreach (string file in Directory.GetDirectories(ProfileArtDir)
                             .SelectMany(dir => Directory.GetFiles(dir, "*.png",
                                                                   SearchOption.AllDirectories))
                             .OrderBy(IconFolderRank))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    foreach (string prefix in IconTexturePrefixes)
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            name = name[prefix.Length..];
                            break;
                        }
                    // First one wins, and IconFolderRank decides who is first when the same name
                    // was exported twice.
                    if (!map.ContainsKey(name)) map[name] = file;
                }

            lock (_gate)
            {
                _iconFiles = map;
                _iconStamp = stamp;
            }
            return map;
        }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>Which folder wins when the same icon name was exported into two of them.
    /// <c>Utilities\Images</c> comes first: those are the ITEM pictures the game actually shows
    /// on the wheel (grenades, medkits, stims), and the same names elsewhere are the flat
    /// utility glyphs — for Flamethrower the two folders hold different art under one
    /// name.</summary>
    private static int IconFolderRank(string path) =>
        path.Contains(@"Utilities\Images", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    /// <summary>Texture-name prefixes the game uses for the icon folders worth exporting, so an
    /// un-renamed export matches whichever folder it came from.</summary>
    private static readonly string[] IconTexturePrefixes =
        { "T_UI_Abilities_", "T_UI_Utilities_", "T_UI_Passives_", "T_UI_Generic_", "T_UI_" };
    private static IReadOnlyDictionary<string, string>? _iconFiles;
    private static DateTime _iconStamp;

    /// <summary>The stem up to the first underscore — "StandardShot" out of
    /// "GA_StandardShot_BlasterRifle".</summary>
    private static string AbilityIconKeyHead(string classPath)
    {
        string name = AbilityObjectName(classPath);
        if (name.StartsWith("GA_", StringComparison.OrdinalIgnoreCase)) name = name[3..];
        int underscore = name.IndexOf('_');
        return underscore > 0 ? name[..underscore] : "";
    }

    /// <summary>"…/GA_ForcePush_T2.Default__…" → "ForcePush": the stem both the ability class and
    /// the game's icon texture are named after.</summary>
    internal static string AbilityIconKey(string classPath)
    {
        string name = AbilityObjectName(classPath);
        if (name.StartsWith("GA_", StringComparison.OrdinalIgnoreCase)) name = name[3..];

        var parts = name.Split('_').ToList();
        if (parts.Count > 1)
        {
            string last = parts[^1];
            if (last.Length >= 2 && (last[0] == 'T' || last[0] == 't') &&
                int.TryParse(last.AsSpan(1), out _))
                parts.RemoveAt(parts.Count - 1);
        }
        return string.Concat(parts);
    }

    /// <summary>"…/GA_RocketStrike_T6.Default__GA_RocketStrike_T6_C" → "GA_RocketStrike_T6".</summary>
    private static string AbilityObjectName(string classPath)
    {
        string tail = classPath.Split('/')[^1];
        int dot = tail.IndexOf('.');
        return dot > 0 ? tail[..dot] : tail;
    }

    /// <summary>The name to put on a key: "GA_RocketStrike_T6" → "ROCKET STRIKE". The GA_ prefix
    /// and the tier are the game's bookkeeping, and CamelCase is split so a two-word ability wraps
    /// onto the tile's two caption lines by itself.</summary>
    private static string AbilityName(string classPath)
    {
        string name = AbilityObjectName(classPath);
        if (name.StartsWith("GA_", StringComparison.OrdinalIgnoreCase)) name = name[3..];

        var parts = name.Split('_').ToList();
        if (parts.Count > 1)
        {
            string last = parts[^1];
            if (last.Length >= 2 && (last[0] == 'T' || last[0] == 't') &&
                int.TryParse(last.AsSpan(1), out _))
                parts.RemoveAt(parts.Count - 1);
        }
        return string.Join(" ", parts.Select(SplitCamelCase)).ToUpperInvariant();
    }

    private static async Task<IReadOnlyList<string>> SquadMembersAsync()
    {
        if (_squad is null) return Array.Empty<string>();
        var doc = await PutAsync("/remote/object/call", new
        {
            objectPath = _squad,
            functionName = "GetSquadMembers",
        }).ConfigureAwait(false);
        if (doc is null) return Array.Empty<string>();

        // The return array's property name is the function's out-parameter name, which we do not
        // want to hard-code: take the first array of strings in the answer.
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value.EnumerateArray()
                           .Where(e => e.ValueKind == JsonValueKind.String)
                           .Select(e => e.GetString()!)
                           .ToList();
        return Array.Empty<string>();
    }

    /// <summary>Walks the entry chain, or confirms the cached one still belongs to the level we
    /// are in. Everything downstream is addressed off these four paths.</summary>
    private static async Task<bool> Resolve()
    {
        string? level = await CallStringAsync(GameplayStatics, "GetCurrentLevelName",
            new { WorldContextObject = GameInstance, bRemovePrefixString = true }).ConfigureAwait(false);
        if (level is null or "") { InvalidatePaths(); return false; }

        if (_level == level && _pc is not null && _tactical is not null &&
            _squad is not null && _selComp is not null) return true;

        InvalidatePaths();

        string? pc = await CallStringAsync(GameplayStatics, "GetPlayerController",
            new { WorldContextObject = GameInstance, PlayerIndex = 0 }).ConfigureAwait(false);
        if (pc is null or "") return false;

        string? tactical = await FirstActorOfClassAsync(TacticalStateClass).ConfigureAwait(false);
        if (tactical is null) return false;   // not a tactical mission (hub, shell, cinematic)

        var doc = await PutAsync("/remote/object/property", new
        {
            objectPath = tactical,
            access = "READ_ACCESS",
            propertyName = "PlayerSquad",
        }).ConfigureAwait(false);
        string? squad = doc?.RootElement.TryGetProperty("PlayerSquad", out var sq) == true
            ? sq.GetString() : null;
        if (squad is null or "") return false;

        _level = level;
        _pc = pc;
        _tactical = tactical;
        _squad = squad;
        _selComp = pc + ".BrunoSelectedCharacterComponent";
        return true;
    }

    private static async Task<string?> FirstActorOfClassAsync(string classPath)
    {
        var doc = await PutAsync("/remote/object/call", new
        {
            objectPath = GameplayStatics,
            functionName = "GetAllActorsOfClass",
            parameters = new { WorldContextObject = GameInstance, ActorClass = classPath },
        }).ConfigureAwait(false);
        if (doc is null) return null;

        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var e in prop.Value.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) return e.GetString();
        return null;
    }

    private static void InvalidatePaths()
    {
        _level = _pc = _tactical = _squad = _selComp = null;
        lock (_gate)
        {
            _maxAp.Clear();
            _wheelFor = null;
            _wheel = Array.Empty<Ability>();
        }
    }

    /// <summary>The soldier's name out of the engine's object name:
    /// <c>"Char_Hero_LucoBronc_Sniper_Bespin_C_0"</c> → <c>"Luco Bronc"</c>.
    ///
    /// <para>The authored pawns are named <c>Char_[Hero_]&lt;Character&gt;_&lt;Class&gt;_...</c>,
    /// so the character is the first part that is neither the prefix nor a role word, and the
    /// two halves of a name are welded together in CamelCase — which is what the split below
    /// undoes, giving the tile a first and last name to put on its two lines. A single-word name
    /// (Anakin) simply stays one word; nothing is invented to fill the second line.</para>
    ///
    /// <para>An all-caps token (HAWKS) is left exactly as it is: splitting on capitals there
    /// would spell it out letter by letter.</para></summary>
    private static string PrettyName(string path)
    {
        string name = path.Split('.').LastOrDefault() ?? path;
        var parts = name.Split('_');

        string token = parts.Length > 1 ? parts[1] : name;
        foreach (string p in parts.Skip(1))
            if (p.Length > 2 && !p.Equals("Hero", StringComparison.OrdinalIgnoreCase) &&
                !p.Equals("Char", StringComparison.OrdinalIgnoreCase))
            { token = p; break; }

        return SplitCamelCase(token);
    }

    /// <summary>"LucoBronc" → "Luco Bronc"; "HAWKS" and "Anakin" come back untouched.</summary>
    private static string SplitCamelCase(string token)
    {
        if (token.Length < 2 || token.All(char.IsUpper)) return token;

        var sb = new StringBuilder(token.Length + 2);
        for (int i = 0; i < token.Length; i++)
        {
            // A capital that follows a lower-case letter starts the second word. Runs of
            // capitals (an acronym) are not broken up.
            if (i > 0 && char.IsUpper(token[i]) && char.IsLower(token[i - 1])) sb.Append(' ');
            sb.Append(token[i]);
        }
        return sb.ToString();
    }

    // ─────────────────────────── Commands ───────────────────────────

    /// <summary>Fires a squad command at the game. Fire-and-forget on purpose: a DisplayPad press
    /// must not wait on a socket, and a command that misses (game closed, not in a mission) is a
    /// no-op rather than an error worth interrupting the player over.</summary>
    internal static void Send(string command, Action<string> log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await Resolve().ConfigureAwait(false)) return;

                bool ok = command switch
                {
                    "nextchar" => await CallVoidAsync(_selComp!, "SelectNextCharacter").ConfigureAwait(false),
                    "prevchar" => await CallVoidAsync(_selComp!, "SelectPrevCharacter").ConfigureAwait(false),
                    _ when command.StartsWith("ability", StringComparison.Ordinal) &&
                           int.TryParse(command.AsSpan("ability".Length), out int slot) =>
                        await ActivateAbilityAsync(slot, log).ConfigureAwait(false),
                    _ when command.StartsWith("unit", StringComparison.Ordinal) &&
                           int.TryParse(command.AsSpan("unit".Length), out int n) =>
                        await SelectUnitAsync(n, log).ConfigureAwait(false),
                    _ => false,
                };
                log($"[ZC] btn press -> {command} {(ok ? "sent" : "ignored")}");

                // next/prev go through the API and still need the wheel opening on whoever
                // they landed on; a squad key has already done its own walking.
                if (ok && command is "nextchar" or "prevchar") ConfirmSelection();
            }
            catch (Exception ex) { log($"[ZC] command {command} failed: {ex.Message}"); }
        });
    }

    /// <summary>Walks the game's own selection to <paramref name="target"/> with the TAB key and
    /// then opens its action wheel with CTRL.
    ///
    /// <para><b>Why not just select through the API.</b> The API moves the game's selection but
    /// not its UI: the wheel is opened by the input layer and nothing on the player controller
    /// exposes it. Pressing TAB/CTRL afterwards opens the wheel — but TAB also ADVANCES the
    /// selection, so the wheel came up on the next soldier. Correcting the selection through the
    /// API afterwards made it worse: the game then had the right soldier selected underneath a
    /// wheel belonging to the wrong one (user report, 2026-09-05).</para>
    ///
    /// <para>So the selection is moved the way the game itself moves it. TAB is pressed until the
    /// API — which can always be READ cheaply — says the right soldier is under the cursor, then
    /// CTRL opens the wheel on them. No assumption is made about TAB's cycle order or its
    /// direction: we simply look after each press. Worst case is one lap of the squad.</para>
    ///
    /// <para>The API selection is kept only as a FALLBACK, for when the keys are going nowhere
    /// (the game not focused, a binding changed): the state ends up right even if the wheel does
    /// not open, which is better than a key that does nothing at all.</para></summary>
    private static async Task<bool> WalkSelectionToAsync(string target, Action<string> log)
    {
        for (int step = 0; step < MaxTabSteps; step++)
        {
            string? now = await CallStringAsync(_selComp!, "GetSelectedCharacter").ConfigureAwait(false);
            if (string.Equals(now, target, StringComparison.Ordinal))
            {
                ConfirmSelection();
                return true;
            }

            K2.Core.HotkeySender.TrySend("Tab", out _);
            await Task.Delay(TabStepMs).ConfigureAwait(false);
        }

        log($"[ZC] TAB never reached {Short(target)} — selecting through the API instead");
        return await CallVoidAsync(_selComp!, "SelectSpecificCharacter",
                                   new { Candidate = target }).ConfigureAwait(false);
    }

    /// <summary>CTRL — the game's own "act on the selected soldier", which is what brings the
    /// action wheel up.</summary>
    private static void ConfirmSelection()
    {
        Thread.Sleep(WheelKeyDelayMs);
        K2.Core.HotkeySender.TrySend("Ctrl", out _);
    }

    /// <summary>Object name only — a full actor path in a log line hides what it says.</summary>
    private static string Short(string? path) =>
        path is { Length: > 0 } ? path.Split('.')[^1] : "(none)";

    /// <summary>Pause after a TAB before asking the game who is selected now. Too short and we
    /// read the previous soldier and press TAB again, overshooting.</summary>
    private const int TabStepMs = 90;

    /// <summary>Pause before CTRL, so it lands after the selection has settled.</summary>
    private const int WheelKeyDelayMs = 70;

    /// <summary>How many TAB presses may be spent looking for the target. A generous lap of the
    /// biggest squad seen so far; past this something is wrong and the API fallback takes over
    /// rather than the pad hammering a key forever.</summary>
    private const int MaxTabSteps = 10;

    /// <summary>Presses the digit the game binds the selected soldier's ability in bar slot
    /// <paramref name="oneBased"/> to, as if the player pressed it on the open action wheel.
    ///
    /// <para>The wheel has to be open for the game to take the key — the player opens it as part of
    /// normal play. This replaced a <c>TryActivateAbility</c> API call (2026-09-07): that did fire
    /// the ability, but outside the wheel's input flow, and once the action finished the game was
    /// left with the menu half-drawn and the turn not advancing (user report). Driving the ability
    /// through its real key is the only path the game's own state machine stays consistent on.
    /// <c>TryAndSelectAbility(Index)</c> — the component's by-index call — does nothing from
    /// outside that flow either.</para></summary>
    private static Task<bool> ActivateAbilityAsync(int oneBased, Action<string> log)
    {
        Status snapshot;
        lock (_gate) snapshot = _last;

        if (snapshot.AbilityAt(oneBased - 1) is not { } ability)
        {
            log($"[ZC] ability slot {oneBased} is empty on the selected soldier");
            return Task.FromResult(false);
        }
        if (ability.GameKey < 0)
        {
            log($"[ZC] ability {oneBased} ({ability.Name}) has no wheel key to press");
            return Task.FromResult(false);
        }

        string digit = ((char)('0' + ability.GameKey)).ToString();
        bool ok = K2.Core.HotkeySender.TrySend(digit, out string err);
        log($"[ZC] ability {oneBased} ({ability.Name}) -> key '{digit}' {(ok ? "sent" : "failed: " + err)}");
        return Task.FromResult(ok);
    }

    /// <summary>Puts squad slot <paramref name="oneBased"/> under the cursor with its action
    /// wheel open — see <see cref="WalkSelectionToAsync"/> for why this is done with the game's
    /// own key rather than through the API.</summary>
    private static async Task<bool> SelectUnitAsync(int oneBased, Action<string> log)
    {
        var members = await SquadMembersAsync().ConfigureAwait(false);
        if (oneBased < 1 || oneBased > members.Count) return false;

        return await WalkSelectionToAsync(members[oneBased - 1], log).ConfigureAwait(false);
    }

    // ─────────────────────────── Transport ───────────────────────────

    private static async Task<JsonDocument?> PutAsync(string route, object body)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body),
                                                  Encoding.UTF8, "application/json");
            using var res = await Http.PutAsync(Host + route, content).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            string json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            return json.Length == 0 ? null : JsonDocument.Parse(json);
        }
        catch { return null; }
    }

    private static async Task<JsonDocument?> CallAsync(string objectPath, string function, object? parameters)
    {
        object body = parameters is null
            ? new { objectPath, functionName = function }
            : new { objectPath, functionName = function, parameters };
        return await PutAsync("/remote/object/call", body).ConfigureAwait(false);
    }

    /// <summary>Calls a function whose answer is an array of object paths, whatever the out
    /// parameter happens to be called.</summary>
    private static async Task<IReadOnlyList<string>> CallArrayAsync(string objectPath, string function,
                                                                    object? parameters)
    {
        using var doc = await CallAsync(objectPath, function, parameters).ConfigureAwait(false);
        if (doc is null) return Array.Empty<string>();

        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value.EnumerateArray()
                           .Where(e => e.ValueKind == JsonValueKind.String)
                           .Select(e => e.GetString()!)
                           .ToList();
        return Array.Empty<string>();
    }

    private static async Task<string?> CallStringAsync(string objectPath, string function, object? parameters = null)
    {
        using var doc = await CallAsync(objectPath, function, parameters).ConfigureAwait(false);
        return doc is not null && doc.RootElement.TryGetProperty("ReturnValue", out var v) &&
               v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static async Task<int> CallIntAsync(string objectPath, string function, object? parameters = null)
    {
        using var doc = await CallAsync(objectPath, function, parameters).ConfigureAwait(false);
        return doc is not null && doc.RootElement.TryGetProperty("ReturnValue", out var v) &&
               v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }

    private static async Task<bool> CallBoolAsync(string objectPath, string function, object? parameters = null)
    {
        using var doc = await CallAsync(objectPath, function, parameters).ConfigureAwait(false);
        return doc is not null && doc.RootElement.TryGetProperty("ReturnValue", out var v) &&
               v.ValueKind == JsonValueKind.True;
    }

    private static async Task<bool> CallVoidAsync(string objectPath, string function, object? parameters = null)
    {
        using var doc = await CallAsync(objectPath, function, parameters).ConfigureAwait(false);
        return doc is not null;
    }

    // ─────────────────────── Turning the API on ───────────────────────

    /// <summary>Path of the config file that switches the game's remote-control server on. It is
    /// the packaged game's own user config directory, the same hierarchy UE reads
    /// <c>GameUserSettings.ini</c> from — nothing in the install is touched.</summary>
    internal static string EngineIniPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SWZeroCompany", "Saved", "Config", "Windows", "Engine.ini");

    /// <summary>True when the game is already configured to open its remote-control server.</summary>
    internal static bool RemoteControlEnabled()
    {
        try
        {
            return File.Exists(EngineIniPath) &&
                   File.ReadAllText(EngineIniPath)
                       .Contains("bAutoStartWebServer=True", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Writes the autostart settings the tiles need, PRESERVING anything already in the
    /// file — a user with their own Engine.ini tweaks keeps them; only our section is appended.
    /// Takes effect at the game's next launch. Deleting the file undoes it completely, which is
    /// why nothing else in K2 depends on its presence.</summary>
    internal static bool EnsureRemoteControlEnabled(Action<string> log)
    {
        try
        {
            if (RemoteControlEnabled()) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(EngineIniPath)!);
            string existing = File.Exists(EngineIniPath) ? File.ReadAllText(EngineIniPath) : "";
            var sb = new StringBuilder(existing);
            if (existing.Length > 0 && !existing.EndsWith('\n')) sb.AppendLine();

            // Written under BOTH plausible section names: the settings class lives in
            // RemoteControlCommon in 5.6, but the older RemoteControl spelling costs one ignored
            // section and saves a silent no-op if the game is ever patched to an engine that
            // moved it back.
            sb.AppendLine();
            sb.AppendLine("; Added by K2 — enables Zero Company's built-in remote control API so the");
            sb.AppendLine("; DisplayPad can show squad state. Delete this block to switch it off.");
            foreach (string section in new[]
                     {
                         "[/Script/RemoteControlCommon.RemoteControlSettings]",
                         "[/Script/RemoteControl.RemoteControlSettings]",
                     })
            {
                sb.AppendLine(section);
                sb.AppendLine("bAutoStartWebServer=True");
                sb.AppendLine("bAutoStartWebSocketServer=True");
                sb.AppendLine("RemoteControlHttpServerPort=30010");
                sb.AppendLine("RemoteControlWebSocketServerPort=30020");
                sb.AppendLine();
            }

            File.WriteAllText(EngineIniPath, sb.ToString());
            log("[ZC] remote control enabled in " + EngineIniPath);
            return true;
        }
        catch (Exception ex)
        {
            log("[ZC] could not enable remote control: " + ex.Message);
            return false;
        }
    }
}
