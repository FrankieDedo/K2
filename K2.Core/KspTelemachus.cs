using System;
using System.Collections.Generic;
using System.Linq;

namespace K2.Core;

/// <summary>How a Kerbal Space Program tile turns the value Telemachus answers into something a
/// 102 px key can show. Decided per item in <see cref="KspTelemachus.Items"/>, never guessed from
/// the answer: <c>v.altitude</c> and <c>o.period</c> are both bare doubles on the wire.</summary>
public enum KspFormat
{
    /// <summary>Metres, scaled to km / Mm / Gm as it grows.</summary>
    Distance,
    /// <summary>Metres per second, km/s past 10 000.</summary>
    Speed,
    /// <summary>A duration in seconds — m:ss, h:mm, then days of KERBIN (6 h), which is what
    /// the game's own clocks count in.</summary>
    Duration,
    /// <summary>Degrees.</summary>
    Angle,
    GForce,
    /// <summary>Kilopascals.</summary>
    Pressure,
    /// <summary>Kelvin, as the game prints it.</summary>
    Temperature,
    Mach,
    /// <summary>Tonnes (Telemachus reports <c>v.mass</c> in tonnes, not the kg its docs say).</summary>
    Mass,
    /// <summary>A plain decimal with no unit (eccentricity, science, reputation).</summary>
    Number,
    /// <summary>A whole count (stage, crew, nodes).</summary>
    Integer,
    /// <summary>Funds — thousands and millions shortened.</summary>
    Funds,
    /// <summary>A word the game already wrote (body, biome, situation, target name).</summary>
    Text,
    /// <summary>0…1 shown as a percentage on a segmented dial (throttle, signal).</summary>
    Fraction,
    /// <summary>A resource: the item's read key is the amount, <see cref="KspItem.ReadMax"/> the
    /// capacity, and the dial shows how full it is.</summary>
    Resource,
    /// <summary>An on/off light: the key is lit when the read value is true — or, with
    /// <see cref="KspItem.LitWhen"/>, when it equals that text.</summary>
    Lamp,
    /// <summary>Time-warp multiplier ("×50").</summary>
    Warp,
    /// <summary>The SAS mode word, translated through the same names the mode keys use.</summary>
    SasMode,
}

/// <summary>One entry of the API listing Telemachus serves about itself (<c>a.api</c>) — the
/// mod's own documentation of what it can answer, read from the running game rather than copied
/// into K2, so a Telemachus update that adds entries adds them to K2's pickers too.</summary>
/// <param name="Category">The mod's own grouping ("vessel", "orbit", ...). Empty for the entries it
/// registers without one — its flight commands among them.</param>
/// <param name="Units">The mod's unit tag: DISTANCE, VELOCITY, TIME, DATE, DEG, LATLON, G, ...</param>
/// <param name="Params">The argument list for an entry that needs one; such entries are not
/// offered, since a key has nowhere to type an argument.</param>
public sealed record KspApiEntry(string Api, string Name, string Category, string Units,
                                 string ReturnType, bool IsAction, string Params);

/// <summary>One thing a Kerbal Space Program key can be: a reading, a command, or both — a SAS key
/// sends <c>f.sas</c> and lights up from <c>v.sasValue</c>.</summary>
/// <param name="Value">What the key stores as its action value. It is the Telemachus API string
/// of the thing the key is ABOUT — the command for a command key, the reading otherwise — so a
/// saved profile reads like the mod's own documentation and never needs a K2-only vocabulary.</param>
/// <param name="LocKey">Name in the action picker, also the tile's caption.</param>
/// <param name="GroupLocKey">Family heading in the picker.</param>
/// <param name="Read">API string polled to paint the tile, or null for a key with nothing to
/// report (the tile then only says whether Telemachus is answering).</param>
/// <param name="Press">API string sent when the key is pressed, or null for a readout.</param>
/// <param name="ReadMax">Second key polled for <see cref="KspFormat.Resource"/> — the capacity.</param>
/// <param name="LitWhen">For a lamp whose reading is a word rather than a bool: the word that
/// lights it (a SAS-mode key is lit while <c>f.sasMode</c> equals its own mode).</param>
public sealed record KspItem(string Value, string LocKey, string GroupLocKey, string? Read, string? Press,
                             KspFormat Format, string? ReadMax = null, string? LitWhen = null)
{
    /// <summary>Every API string this item needs polled.</summary>
    public IEnumerable<string> ReadKeys()
    {
        if (Read is { Length: > 0 }) yield return Read;
        if (ReadMax is { Length: > 0 }) yield return ReadMax;
    }
}

/// <summary>
/// The Kerbal Space Program profile's vocabulary: the entries of the Telemachus Reborn API
/// (<c>github.com/TeaGuild/Telemachus-1</c>, README "API reference") a DisplayPad key can
/// usefully be, grouped the way that documentation groups them.
///
/// <para><b>Curated list first, the mod's own listing after.</b> <see cref="Items"/> is the hand-made
/// part: translated names, the documentation's grouping, careful formatting (resources as dials,
/// SAS keys that read one entry and press another), each checked against the listing a real
/// Telemachus 1.7.0 served on 2026-09-17. Everything else comes from that listing itself
/// (<c>a.api</c>, cached by K2.App — see <see cref="ApiProvider"/>): of its ~530 entries, the ~290
/// a key can be are offered under "Telemachus · …" families, named in the mod's own English. Left
/// out are vectors, whole objects and entries that need an argument. Either way the player picks
/// by name and K2 composes the <c>datalink</c> request; nobody types <c>o.ApA</c>.</para>
///
/// <para><b>The antenna gate.</b> Telemachus answers the flight-control, landing and thermal
/// families only while the vessel carries a powered Telemachus antenna part: otherwise, instead
/// of the value, those entries answer the <c>p.paused</c> code (2 = no power, 3 = off, 4 = no
/// antenna) — a gear state of "2". <see cref="NeedsAntenna"/> names those entries so the tile
/// shows "unknown" rather than a confident wrong number, and commands in those families are not
/// executed by the mod at all in that state.</para>
/// </summary>
public static class KspTelemachus
{
    /// <summary>The action type every KSP key uses. Lives only in the game's own picker: the
    /// "games" category is hidden outside game profiles, and inside one only that game's families
    /// are listed — which is what keeps Telemachus a Kerbal-only feature.</summary>
    public const string ActionType = "dp_ksp";

    /// <summary>Telemachus' default port. The mod lets it be changed in its own settings; K2
    /// follows the default because that is what every install starts with.</summary>
    public const int DefaultPort = 8085;

    /// <summary>The SAS modes in the order the game's navball lists them — the values
    /// <c>f.setSASMode</c> accepts and <c>f.sasMode</c> reports.</summary>
    public static readonly string[] SasModes =
    {
        "StabilityAssist", "Prograde", "Retrograde", "Normal", "Antinormal",
        "RadialIn", "RadialOut", "Target", "AntiTarget", "Maneuver",
    };

    /// <summary>Resources offered as dials: the stock ones a stock craft carries.</summary>
    private static readonly string[] Resources =
    {
        "LiquidFuel", "Oxidizer", "MonoPropellant", "ElectricCharge", "SolidFuel",
        "XenonGas", "IntakeAir", "Ore", "Ablator",
    };

    /// <summary>The curated entries: named in K2's own languages, grouped as the documentation
    /// groups them, and formatted with care. Everything else the mod offers comes from
    /// <see cref="ApiItems"/>.</summary>
    public static IReadOnlyList<KspItem> Items { get; } = Build();

    private static readonly Dictionary<string, KspItem> ByValue =
        Items.ToDictionary(i => i.Value, StringComparer.Ordinal);

    /// <summary>The API listing the running mod last served, as the app has it cached. Set once at
    /// startup by K2.App, which owns the fetch and the file it is kept in; null (or an empty list)
    /// just means only the curated entries are offered.</summary>
    public static Func<IReadOnlyList<KspApiEntry>>? ApiProvider { get; set; }

    /// <summary>The item a key stores: a curated entry, or one built from the mod's own listing.
    /// Null for a value neither knows — a listing not fetched yet, or an entry a newer Telemachus
    /// dropped — and the tile then reads "unknown" rather than guessing how to show it.</summary>
    public static KspItem? Find(string? value)
    {
        if (value is not { Length: > 0 }) return null;
        string v = value.Trim();
        if (ByValue.TryGetValue(v, out var item)) return item;
        return ApiIndex().TryGetValue(v, out var dynamicItem) ? dynamicItem : null;
    }

    /// <summary>Everything the mod's listing offers that a key can be, minus what the curated list
    /// already covers, in the listing's own order.</summary>
    public static IReadOnlyList<KspItem> ApiItems() => ApiIndex().Values.ToList();

    /// <summary>Every entry a key can hold — curated first, then the mod's own.</summary>
    public static IEnumerable<KspItem> AllItems() => Items.Concat(ApiItems());

    private static IReadOnlyList<KspApiEntry>? _indexedFrom;
    private static Dictionary<string, KspItem> _apiIndex = new(StringComparer.Ordinal);
    private static readonly object _indexGate = new();

    /// <summary>Rebuilt only when the provider hands back a different list — the app replaces the
    /// list wholesale on every refresh, so reference identity is the change signal.</summary>
    private static Dictionary<string, KspItem> ApiIndex()
    {
        var entries = ApiProvider?.Invoke();
        lock (_indexGate)
        {
            if (ReferenceEquals(entries, _indexedFrom)) return _apiIndex;
            var index = new Dictionary<string, KspItem>(StringComparer.Ordinal);
            foreach (var e in entries ?? Array.Empty<KspApiEntry>())
                if (!ByValue.ContainsKey(e.Api) && !index.ContainsKey(e.Api) && FromApi(e) is { } it)
                    index[e.Api] = it;
            _indexedFrom = entries;
            _apiIndex = index;
            return index;
        }
    }

    /// <summary>What the mod's listing says an entry is, turned into a key — or null when no key
    /// can be it: an entry that needs an argument, answers a vector/object/list, or is the mod's
    /// own plumbing (<c>a.*</c>, <c>p.paused</c>).</summary>
    public static KspItem? FromApi(KspApiEntry e)
    {
        if (e.Api.Length == 0 || e.Params.Length > 0) return null;
        if (e.Api.StartsWith("a.", StringComparison.Ordinal) || e.Api == "p.paused") return null;

        string group = ApiGroupLocKey(e);

        // The listing registers a handful of flight commands without a category and with no
        // action flag (f.stage, f.gear, the MechJeb and time-warp commands); their argument, when
        // they take one, is spelled into the NAME ("Time Warp [int rate]"). Only the prefixes known
        // to be commands are trusted to be commands.
        bool uncategorised = e.Category.Length == 0;
        bool command = e.IsAction ||
            (uncategorised && (e.Api.StartsWith("f.", StringComparison.Ordinal) ||
                               e.Api.StartsWith("mj.", StringComparison.Ordinal) ||
                               e.Api.StartsWith("t.", StringComparison.Ordinal) ||
                               e.Api.StartsWith("m.", StringComparison.Ordinal)));
        if (uncategorised && !command) return null;   // Astrogator's uncategorised flags and objects
        if (uncategorised && e.Name.Contains('[') &&
            !e.Name.Contains("[optional", StringComparison.OrdinalIgnoreCase))
            return null;

        if (command)
            return new KspItem(e.Api, "!" + StripArgs(e.Name), group, null, e.Api, KspFormat.Lamp);

        KspFormat? format = e.ReturnType switch
        {
            "bool" => KspFormat.Lamp,
            "string" => KspFormat.Text,
            "int" => KspFormat.Integer,
            "double" or "float" => e.Units switch
            {
                "DISTANCE" => KspFormat.Distance,
                // The listing tags angular velocity VELOCITY too, and that one is rad/s.
                "VELOCITY" when !e.Name.Contains("Angular", StringComparison.Ordinal) => KspFormat.Speed,
                "TIME" or "DATE" => KspFormat.Duration,
                "DEG" or "LATLON" => KspFormat.Angle,
                "G" => KspFormat.GForce,
                // Pressure, temperature and the rest: the listing's unit tag does not say WHICH
                // unit (the same "TEMP" covers K and °C), the name does — so the number goes out
                // bare rather than wearing a unit that might be the wrong one.
                _ => KspFormat.Number,
            },
            _ => null,
        };
        return format is { } f ? new KspItem(e.Api, "!" + e.Name, group, e.Api, null, f) : null;
    }

    /// <summary>"RCS [optional bool on/off]" → "RCS".</summary>
    private static string StripArgs(string name)
    {
        int b = name.IndexOf('[');
        return (b > 0 ? name[..b] : name).Trim();
    }

    /// <summary>Family of an entry from the mod's listing: a loc key for the categories K2 knows,
    /// the mod's own word otherwise.</summary>
    public static string ApiGroupLocKey(KspApiEntry e)
    {
        string cat = e.Category.Length > 0 ? e.Category : e.Api.Split('.')[0] switch
        {
            "f" => "flight",
            "mj" => "mechjeb",
            "t" => "timewarp",
            "m" => "map",
            var other => other,
        };
        return KnownApiCategories.Contains(cat)
            ? "ksptm_cat_" + cat
            : "!Telemachus · " + (cat.Length > 0 ? char.ToUpperInvariant(cat[0]) + cat[1..] : "?");
    }

    private static readonly HashSet<string> KnownApiCategories = new(StringComparer.Ordinal)
    {
        "vessel", "orbit", "flight", "navigation", "maneuver", "docking", "deltav", "target",
        "resource", "timewarp", "map", "alarm", "landing", "thermal", "science", "career", "comms",
        "mechjeb", "far", "realchute", "principia", "kerbalism",
    };

    /// <summary>The mod's own entries as picker families, one per category, after the curated
    /// ones. Recomputed on every call: the listing can arrive while K2 is running.</summary>
    public static (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] ApiFamilies() =>
        ApiItems()
            .GroupBy(i => i.GroupLocKey)
            .Select(g => (g.Key, "📡", g
                .OrderBy(i => Loc.Get(i.LocKey), StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new ActionTypeHelper.GameCommand(ActionType, i.Value, i.LocKey))
                .ToArray()))
            .OrderBy(f => Loc.Get(f.Item1), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>True for the API strings Telemachus gates on a powered antenna part — see the class
    /// remarks. Mirrors which of the mod's handlers override <c>pausedHandler</c>: flight control
    /// (<c>f.*</c> and the action-group <c>v.*Value</c> readings), landing and thermal.</summary>
    public static bool NeedsAntenna(string? api) =>
        api is { Length: > 0 } &&
        (api.StartsWith("f.", StringComparison.Ordinal) ||
         api.StartsWith("land.", StringComparison.Ordinal) ||
         api.StartsWith("therm.", StringComparison.Ordinal) ||
         (api.StartsWith("v.", StringComparison.Ordinal) && api.EndsWith("Value", StringComparison.Ordinal)));

    /// <summary>Loc key of a SAS mode's name — shared by the mode keys and the "SAS mode" reading.</summary>
    public static string SasModeLocKey(string mode) => "ksp_sas_" + mode.ToLowerInvariant();

    private static List<KspItem> Build()
    {
        var list = new List<KspItem>();

        void Read(string api, string loc, string group, KspFormat format) =>
            list.Add(new KspItem(api, loc, group, api, null, format));

        // ── Flight controls: commands that also report their own state. ──
        const string controls = "kspgrp_controls";
        // Stage shows WHICH stage the craft is on — the number the staging stack itself shows.
        list.Add(new KspItem("f.stage", "ksp_stage", controls, "v.currentStage", "f.stage", KspFormat.Integer));
        list.Add(new KspItem("f.sas",   "ksp_sas",   controls, "v.sasValue",   "f.sas",   KspFormat.Lamp));
        list.Add(new KspItem("f.rcs",   "ksp_rcs",   controls, "v.rcsValue",   "f.rcs",   KspFormat.Lamp));
        list.Add(new KspItem("f.gear",  "ksp_gear",  controls, "v.gearValue",  "f.gear",  KspFormat.Lamp));
        list.Add(new KspItem("f.light", "ksp_light", controls, "v.lightValue", "f.light", KspFormat.Lamp));
        list.Add(new KspItem("f.brake", "ksp_brake", controls, "v.brakeValue", "f.brake", KspFormat.Lamp));
        list.Add(new KspItem("f.abort", "ksp_abort", controls, "v.abortValue", "f.abort", KspFormat.Lamp));
        list.Add(new KspItem("f.precisionControl", "ksp_precision", controls,
                             "v.precisionControlValue", "f.precisionControl", KspFormat.Lamp));
        list.Add(new KspItem("m.toggleMapView", "ksp_map", controls, "m.mapIsEnabled", "m.toggleMapView", KspFormat.Lamp));

        // ── Throttle ──
        const string throttle = "kspgrp_throttle";
        Read("f.throttle", "ksp_throttle", throttle, KspFormat.Fraction);
        list.Add(new KspItem("f.throttleUp",   "ksp_throttle_up",   throttle, "f.throttle", "f.throttleUp",   KspFormat.Fraction));
        list.Add(new KspItem("f.throttleDown", "ksp_throttle_down", throttle, "f.throttle", "f.throttleDown", KspFormat.Fraction));
        list.Add(new KspItem("f.throttleFull", "ksp_throttle_full", throttle, "f.throttle", "f.throttleFull", KspFormat.Fraction));
        list.Add(new KspItem("f.throttleZero", "ksp_throttle_zero", throttle, "f.throttle", "f.throttleZero", KspFormat.Fraction));

        // ── SAS modes: each key selects its mode and is lit while that mode is the active one. ──
        const string sas = "kspgrp_sas";
        Read("f.sasMode", "ksp_sasmode", sas, KspFormat.SasMode);
        foreach (string mode in SasModes)
            list.Add(new KspItem($"f.setSASMode[{mode}]", SasModeLocKey(mode), sas,
                                 "f.sasMode", $"f.setSASMode[{mode}]", KspFormat.Lamp, LitWhen: mode));

        // ── Action groups 1–10 ──
        const string groups = "kspgrp_actiongroups";
        for (int n = 1; n <= 10; n++)
            list.Add(new KspItem($"f.ag{n}", $"ksp_ag{n}", groups, $"v.ag{n}Value", $"f.ag{n}", KspFormat.Lamp));

        // ── Flight readings ──
        const string flight = "kspgrp_flight";
        Read("v.altitude",           "ksp_altitude",     flight, KspFormat.Distance);
        Read("v.heightFromTerrain",  "ksp_radar_alt",    flight, KspFormat.Distance);
        Read("v.verticalSpeed",      "ksp_vspeed",       flight, KspFormat.Speed);
        Read("v.surfaceSpeed",       "ksp_hspeed",       flight, KspFormat.Speed);
        Read("v.srfSpeed",           "ksp_srfspeed",     flight, KspFormat.Speed);
        Read("v.obtSpeed",           "ksp_obtspeed",     flight, KspFormat.Speed);
        Read("v.indicatedAirSpeed",  "ksp_ias",          flight, KspFormat.Speed);
        Read("v.mach",               "ksp_mach",         flight, KspFormat.Mach);
        Read("v.geeForce",           "ksp_gforce",       flight, KspFormat.GForce);
        Read("v.dynamicPressurekPa", "ksp_dynpressure",  flight, KspFormat.Pressure);
        Read("v.externalTemperature","ksp_exttemp",      flight, KspFormat.Temperature);
        Read("v.mass",               "ksp_mass",         flight, KspFormat.Mass);
        Read("n.heading",            "ksp_heading",      flight, KspFormat.Angle);
        Read("n.pitch",              "ksp_pitch",        flight, KspFormat.Angle);
        Read("n.roll",               "ksp_roll",         flight, KspFormat.Angle);

        // ── Orbit ──
        const string orbit = "kspgrp_orbit";
        Read("o.ApA",            "ksp_apoapsis",       orbit, KspFormat.Distance);
        Read("o.PeA",            "ksp_periapsis",      orbit, KspFormat.Distance);
        Read("o.timeToAp",       "ksp_time_ap",        orbit, KspFormat.Duration);
        Read("o.timeToPe",       "ksp_time_pe",        orbit, KspFormat.Duration);
        Read("o.inclination",    "ksp_inclination",    orbit, KspFormat.Angle);
        Read("o.eccentricity",   "ksp_eccentricity",   orbit, KspFormat.Number);
        Read("o.period",         "ksp_period",         orbit, KspFormat.Duration);
        Read("o.sma",            "ksp_sma",            orbit, KspFormat.Distance);
        Read("v.body",           "ksp_body",           orbit, KspFormat.Text);
        Read("o.encounterBody",  "ksp_encounter",      orbit, KspFormat.Text);
        Read("o.encounterTime",  "ksp_encounter_time", orbit, KspFormat.Duration);

        // ── Maneuver & delta-V ──
        const string maneuver = "kspgrp_maneuver";
        Read("o.maneuverNodes.timeTo[0]",          "ksp_node_time", maneuver, KspFormat.Duration);
        Read("o.maneuverNodes.deltaVMagnitude[0]", "ksp_node_dv",   maneuver, KspFormat.Speed);
        Read("o.maneuverNodes.count",              "ksp_node_count",maneuver, KspFormat.Integer);
        Read("dv.totalDVActual",                   "ksp_dv_actual", maneuver, KspFormat.Speed);
        Read("dv.totalDVVac",                      "ksp_dv_vac",    maneuver, KspFormat.Speed);
        Read("dv.totalBurnTime",                   "ksp_burn_time", maneuver, KspFormat.Duration);

        // ── Resources: whole craft, then what is left in the current stage. ──
        const string resources = "kspgrp_resources";
        foreach (string r in Resources)
            list.Add(new KspItem($"r.resource[{r}]", "ksp_res_" + r.ToLowerInvariant(), resources,
                                 $"r.resource[{r}]", null, KspFormat.Resource, ReadMax: $"r.resourceMax[{r}]"));
        foreach (string r in new[] { "LiquidFuel", "Oxidizer", "SolidFuel" })
            list.Add(new KspItem($"r.resourceCurrent[{r}]", "ksp_stage_" + r.ToLowerInvariant(), resources,
                                 $"r.resourceCurrent[{r}]", null, KspFormat.Resource,
                                 ReadMax: $"r.resourceCurrentMax[{r}]"));

        // ── Target ──
        const string target = "kspgrp_target";
        Read("tar.name",                 "ksp_target_name",  target, KspFormat.Text);
        Read("tar.distance",             "ksp_target_dist",  target, KspFormat.Distance);
        Read("tar.o.relativeVelocity",   "ksp_target_relv",  target, KspFormat.Speed);
        Read("tar.o.relativeInclination","ksp_target_incl",  target, KspFormat.Angle);

        // ── Mission status ──
        const string mission = "kspgrp_mission";
        Read("v.situationString",      "ksp_situation",   mission, KspFormat.Text);
        Read("v.biome",                "ksp_biome",       mission, KspFormat.Text);
        Read("v.missionTime",          "ksp_met",         mission, KspFormat.Duration);
        Read("v.crewCount",            "ksp_crew",        mission, KspFormat.Integer);
        Read("comm.connected",         "ksp_comm",        mission, KspFormat.Lamp);
        Read("comm.signalStrength",    "ksp_signal",      mission, KspFormat.Fraction);
        Read("therm.hottestPartTempRatio", "ksp_heat",    mission, KspFormat.Fraction);
        Read("therm.anyEnginesOverheating","ksp_overheat",mission, KspFormat.Lamp);
        Read("land.timeToImpact",      "ksp_impact",      mission, KspFormat.Duration);
        Read("land.suicideBurnCountdown", "ksp_suicide_burn", mission, KspFormat.Duration);

        // ── Time ──
        const string time = "kspgrp_time";
        Read("t.currentRate", "ksp_warp", time, KspFormat.Warp);
        // Lit while the game is NOT warping — the key's job is to get back to that.
        list.Add(new KspItem("t.timeWarp[0]", "ksp_warp_stop", time, "t.currentRateIndex", "t.timeWarp[0]",
                             KspFormat.Lamp, LitWhen: "0"));
        list.Add(new KspItem("t.quickSave", "ksp_quicksave", time, null, "t.quickSave", KspFormat.Lamp));

        // ── Career ──
        const string career = "kspgrp_career";
        Read("career.funds",      "ksp_funds",      career, KspFormat.Funds);
        Read("career.science",    "ksp_science",    career, KspFormat.Number);
        Read("career.reputation", "ksp_reputation", career, KspFormat.Number);

        return list;
    }

    /// <summary>The picker's families, in screen order.</summary>
    public static readonly (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] CommandFamilies =
        BuildFamilies();

    private static (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] BuildFamilies()
    {
        var order = new[]
        {
            ("kspgrp_controls",     "🕹"),
            ("kspgrp_throttle",     "🔥"),
            ("kspgrp_sas",          "🧭"),
            ("kspgrp_actiongroups", "🔢"),
            ("kspgrp_flight",       "✈"),
            ("kspgrp_orbit",        "🪐"),
            ("kspgrp_maneuver",     "🎯"),
            ("kspgrp_resources",    "⛽"),
            ("kspgrp_target",       "📡"),
            ("kspgrp_mission",      "🚀"),
            ("kspgrp_time",         "⏱"),
            ("kspgrp_career",       "💰"),
        };

        return order
            .Select(o => (o.Item1, o.Item2, Items
                .Where(i => i.GroupLocKey == o.Item1)
                .Select(i => new ActionTypeHelper.GameCommand(ActionType, i.Value, i.LocKey))
                .ToArray()))
            .Where(f => f.Item3.Length > 0)
            .ToArray();
    }
}
