using System;
using System.Collections.Generic;
using System.Linq;

namespace K2.Core;

/// <summary>How a mod-link tile turns the value the game's K2 mod answers into something a 102 px
/// key can show. Decided per item, never guessed from the answer.</summary>
public enum ModLinkFormat
{
    /// <summary>An on/off light: lit when the read value is true — or, with
    /// <see cref="ModLinkItem.LitWhen"/>, when it equals that text.</summary>
    Lamp,
    /// <summary>The read value against <see cref="ModLinkItem.ReadMax"/> (or <see cref="ModLinkItem.Max"/>),
    /// drawn as a dial with the value itself as the number (health 18 of 20).</summary>
    Gauge,
    /// <summary>0…1 drawn as a dial with a percentage (suit energy, battery).</summary>
    Percent,
    /// <summary>A whole number (coordinates, level, day).</summary>
    Integer,
    /// <summary>A decimal with <see cref="ModLinkItem.Unit"/>.</summary>
    Number,
    /// <summary>Metres per second.</summary>
    Speed,
    /// <summary>Metres, km past 10 000.</summary>
    Distance,
    /// <summary>A word the game already wrote (dimension, biome, ship name).</summary>
    Text,
    /// <summary>A picture the mod draws — a Minecraft hotbar slot exactly as the game shows it —
    /// fetched from <see cref="ModLinkItem.Icon"/> whenever <see cref="ModLinkItem.IconRev"/>
    /// changes, and lit like a lamp (<see cref="ModLinkItem.LitWhen"/>: the selected slot).</summary>
    Icon,
    /// <summary>Several readings on one key: an optional big number (<see cref="ModLinkItem.BigRead"/>)
    /// over one bar per <see cref="ModLinkItem.Bars"/> entry — Minecraft's health with hunger, or
    /// its level and XP bar with the armour under them.</summary>
    Bars,
}

/// <summary>One bar of a <see cref="ModLinkFormat.Bars"/> key.</summary>
/// <param name="Read">Field of the value.</param>
/// <param name="ReadMax">Field of the maximum, or null to use <paramref name="Max"/>.</param>
/// <param name="Max">Fixed maximum; with neither, the value is a 0…1 fraction shown as a percentage.</param>
/// <param name="LabelLocKey">Short label drawn on the left of the bar.</param>
/// <param name="Argb">The bar's colour — the game's own for that reading.</param>
/// <param name="ShowBar">False for a plain label/value line — a coordinate has no "full".</param>
public sealed record ModLinkBar(string Read, string? ReadMax, double? Max, string LabelLocKey, int Argb,
                                bool ShowBar = true);

/// <summary>One thing a mod-link key can be: a reading, a command, or both — Space Engineers'
/// dampeners key presses the player's own DAMPING bind and lights up while the dampeners are on.</summary>
/// <param name="Value">What the key stores as its action value. Prefixed with the game
/// (<c>mc.</c>, <c>se.</c>) so the painter knows which mod to ask without the profile id.</param>
/// <param name="LocKey">Name in the action picker, also the tile's caption.</param>
/// <param name="GroupLocKey">Family heading in the picker.</param>
/// <param name="Read">Field of the mod's <c>/state</c> JSON painted on the tile, or null for a
/// plain command (offered as an ordinary <c>keys</c> action, which needs no mod at all).</param>
/// <param name="Bind">The game's own name for the control the key presses (<c>key.inventory</c>,
/// <c>DAMPING</c>), looked up in the player's real bindings at press time.</param>
/// <param name="Keys">The game's SHIPPED default for <paramref name="Bind"/>, in K2's shortcut
/// syntax — used only when the player's bindings cannot be read.</param>
/// <param name="ReadMax">Second field for <see cref="ModLinkFormat.Gauge"/>: the maximum.</param>
/// <param name="Max">Fixed maximum for a gauge whose maximum never changes (food = 20).</param>
/// <param name="AlarmBelow">For a dial: below this fraction the tile turns to the alarm state.</param>
/// <param name="AlarmWhenLit">For a lamp that is bad news when on (burning, drowning).</param>
/// <param name="Icon">For <see cref="ModLinkFormat.Icon"/>: the mod's HTTP path of the picture.</param>
/// <param name="SizeRef">The widest value the key normally shows ("88:88" for a clock): its font
/// is sized from this, never from the current value, so it does not change size as it changes.
/// Null = the format's default.</param>
/// <param name="IconRev">For <see cref="ModLinkFormat.Icon"/>: the state field that changes when
/// the picture does (0 = no picture, e.g. an empty slot).</param>
public sealed record ModLinkItem(string Value, string LocKey, string GroupLocKey, string? Read,
                                 ModLinkFormat Format, string? Bind = null, string? Keys = null,
                                 string? ReadMax = null, double? Max = null, string Unit = "",
                                 string? LitWhen = null, double? AlarmBelow = null,
                                 bool AlarmWhenLit = false, string? Icon = null,
                                 string? IconRev = null, ModLinkBar[]? Bars = null,
                                 string? BigRead = null, string? SizeRef = null);

/// <summary>A game K2 talks to through a small mod of its own: the mod publishes the game's state
/// as ONE flat JSON object (see <c>K2/Mods/</c>), and K2 reads the player's key bindings from the
/// game's own config to press things.</summary>
/// <param name="Port">Where the mod serves <c>http://127.0.0.1:{Port}/state</c>, or 0 for a mod
/// that cannot open sockets and writes the same JSON to a file instead (Space Engineers: mod
/// scripts are sandboxed, and the file sits in the mod's local storage — K2.App knows where).</param>
/// <param name="Prefix">Value prefix of this game's items, dot included.</param>
/// <param name="ModName">How the mod is called where the user installs it.</param>
/// <param name="Groups">The picker's families in screen order, with their glyphs.</param>
/// <param name="DownloadUrl">Where the player gets the connector — offered as a link in the game
/// profile's settings. The connectors are released on their own (github.com/FrankieDedo/K2-Link,
/// and the Steam Workshop for Space Engineers), so a fix to one never waits for a K2 release.</param>
public sealed record ModLinkGame(string ProfileId, string Prefix, int Port, string ModName,
                                 (string LocKey, string Glyph)[] Groups,
                                 IReadOnlyList<ModLinkItem> Items,
                                 string DownloadUrl = "");

/// <summary>
/// The vocabulary of the games K2 reads through a mod it ships itself — Minecraft (Fabric mod) and
/// Space Engineers (ModAPI mod). One live action type (<see cref="ActionType"/>) serves both:
/// the painter, transport and press path do not care which game answered, only how each item is
/// formatted.
///
/// <para><b>Two kinds of item.</b> An item with a <see cref="ModLinkItem.Read"/> is a live tile:
/// it needs the mod to light up, and when it also names a <see cref="ModLinkItem.Bind"/> it
/// presses that control too. An item without one is a plain shortcut, offered to the picker as an
/// ordinary <c>keys</c> action — it works with no mod installed, and the game profile swaps its
/// shipped default for the player's real bind (<c>SyncedBind</c> in K2.App).</para>
/// </summary>
public static class ModLinkGames
{
    public const string ActionType = "dp_modlink";

    public const string MinecraftId = "minecraft";
    public const string SpaceEngineersId = "space_engineers";

    // ------------------------------------------------------------------ Minecraft

    /// <summary>Minecraft: Java Edition. Binds are the names <c>options.txt</c> stores
    /// (<c>key_key.inventory:key.keyboard.e</c>); defaults are the shipped ones. F1 (hide HUD)
    /// and F3 (debug) became rebindable in 26.x (<c>key.toggleGui</c>, <c>key.debug.overlay</c>); on
    /// older versions those names are absent and the F-key default stands.</summary>
    public static readonly ModLinkGame Minecraft = new(
        MinecraftId, "mc.", 34874, "K2 Link (Fabric)",
        new[]
        {
            ("mcgrp_player",  "❤"),
            ("mcgrp_status",  "⚡"),
            ("mcgrp_world",   "🌍"),
            ("mcgrp_pos",     "🧭"),
            ("mcgrp_hotbar",  "🎒"),
            ("mcgrp_ui",      "🖥"),
        },
        new ModLinkItem[]
        {
            new("mc.health",     "gp_mc_health",     "mcgrp_player", "health", ModLinkFormat.Gauge, ReadMax: "maxHealth", AlarmBelow: 0.3),
            new("mc.food",       "gp_mc_food",       "mcgrp_player", "food", ModLinkFormat.Gauge, Max: 20, AlarmBelow: 0.3),
            new("mc.armor",      "gp_mc_armor",      "mcgrp_player", "armor", ModLinkFormat.Gauge, Max: 20),
            new("mc.air",        "gp_mc_air",        "mcgrp_player", "air", ModLinkFormat.Gauge, ReadMax: "maxAir", AlarmBelow: 0.3),
            new("mc.saturation", "gp_mc_saturation", "mcgrp_player", "saturation", ModLinkFormat.Number),
            new("mc.xplevel",    "gp_mc_xplevel",    "mcgrp_player", "xpLevel", ModLinkFormat.Integer),
            new("mc.xpprogress", "gp_mc_xpprogress", "mcgrp_player", "xpProgress", ModLinkFormat.Percent),
            // Two readings per key, drawn in the game's own colours: hearts red, drumsticks brown,
            // the XP bar green under the level number, armour grey.
            // Short labels and a two-digit size reference: the text is as big as the key allows.
            new("mc.vitals",     "gp_mc_vitals",     "mcgrp_player", "health", ModLinkFormat.Bars, SizeRef: "88", Bars: new[]
            {
                new ModLinkBar("health", "maxHealth", null, "gp_mc_health_short", unchecked((int)0xFFE03232)),
                new ModLinkBar("food", null, 20, "gp_mc_food_short", unchecked((int)0xFFCD8C3C)),
            }),
            // The XP bar has no text of its own: it sits under the big level number.
            new("mc.xparmor",    "gp_mc_xparmor",    "mcgrp_player", "xpLevel", ModLinkFormat.Bars, BigRead: "xpLevel", SizeRef: "88", Bars: new[]
            {
                new ModLinkBar("xpProgress", null, null, "", unchecked((int)0xFF80FF20)),
                new ModLinkBar("armor", null, 20, "gp_mc_armor_short", unchecked((int)0xFFC8C8D2)),
            }),
            new("mc.gamemode",   "gp_mc_gamemode",   "mcgrp_player", "gameMode", ModLinkFormat.Text),

            new("mc.sprint",   "gp_mc_sprint",   "mcgrp_status", "sprinting", ModLinkFormat.Lamp, "key.sprint", "Ctrl"),
            new("mc.sneak",    "gp_mc_sneak",    "mcgrp_status", "sneaking", ModLinkFormat.Lamp, "key.sneak", "Shift"),
            new("mc.flying",   "gp_mc_flying",   "mcgrp_status", "flying", ModLinkFormat.Lamp),
            new("mc.swimming", "gp_mc_swimming", "mcgrp_status", "swimming", ModLinkFormat.Lamp),
            new("mc.gliding",  "gp_mc_gliding",  "mcgrp_status", "gliding", ModLinkFormat.Lamp),
            new("mc.riding",   "gp_mc_riding",   "mcgrp_status", "riding", ModLinkFormat.Lamp),
            new("mc.onfire",   "gp_mc_onfire",   "mcgrp_status", "onFire", ModLinkFormat.Lamp, AlarmWhenLit: true),
            new("mc.hudhidden","gp_mc_hidehud",  "mcgrp_status", "hudHidden", ModLinkFormat.Lamp, "key.toggleGui", "F1"),

            new("mc.dimension", "gp_mc_dimension", "mcgrp_world", "dimension", ModLinkFormat.Text),
            new("mc.biome",     "gp_mc_biome",     "mcgrp_world", "biome", ModLinkFormat.Text),
            new("mc.time",      "gp_mc_time",      "mcgrp_world", "clock", ModLinkFormat.Text, SizeRef: "88:88"),
            new("mc.day",       "gp_mc_day",       "mcgrp_world", "day", ModLinkFormat.Integer),
            new("mc.weather",   "gp_mc_weather",   "mcgrp_world", "weather", ModLinkFormat.Text),
            new("mc.light",     "gp_mc_light",     "mcgrp_world", "light", ModLinkFormat.Integer),

            new("mc.x",      "gp_mc_x",      "mcgrp_pos", "x", ModLinkFormat.Integer),
            new("mc.y",      "gp_mc_y",      "mcgrp_pos", "y", ModLinkFormat.Integer),
            new("mc.z",      "gp_mc_z",      "mcgrp_pos", "z", ModLinkFormat.Integer),
            new("mc.facing", "gp_mc_facing", "mcgrp_pos", "facing", ModLinkFormat.Text),
            new("mc.xyz",    "gp_mc_xyz",    "mcgrp_pos", "x", ModLinkFormat.Bars, SizeRef: "-8888", Bars: new[]
            {
                new ModLinkBar("x", null, null, "gp_mc_x", 0, ShowBar: false),
                new ModLinkBar("y", null, null, "gp_mc_y", 0, ShowBar: false),
                new ModLinkBar("z", null, null, "gp_mc_z", 0, ShowBar: false),
            }),
            new("mc.speed",  "gp_mc_speed",  "mcgrp_pos", "speed", ModLinkFormat.Speed),

            new("mc.helditem", "gp_mc_helditem", "mcgrp_hotbar", "heldItem", ModLinkFormat.Text),
            new("mc.slot1", "gp_mc_slot1", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.1", "1", LitWhen: "1", Icon: "hotbar/1", IconRev: "slotRev1"),
            new("mc.slot2", "gp_mc_slot2", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.2", "2", LitWhen: "2", Icon: "hotbar/2", IconRev: "slotRev2"),
            new("mc.slot3", "gp_mc_slot3", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.3", "3", LitWhen: "3", Icon: "hotbar/3", IconRev: "slotRev3"),
            new("mc.slot4", "gp_mc_slot4", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.4", "4", LitWhen: "4", Icon: "hotbar/4", IconRev: "slotRev4"),
            new("mc.slot5", "gp_mc_slot5", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.5", "5", LitWhen: "5", Icon: "hotbar/5", IconRev: "slotRev5"),
            new("mc.slot6", "gp_mc_slot6", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.6", "6", LitWhen: "6", Icon: "hotbar/6", IconRev: "slotRev6"),
            new("mc.slot7", "gp_mc_slot7", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.7", "7", LitWhen: "7", Icon: "hotbar/7", IconRev: "slotRev7"),
            new("mc.slot8", "gp_mc_slot8", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.8", "8", LitWhen: "8", Icon: "hotbar/8", IconRev: "slotRev8"),
            new("mc.slot9", "gp_mc_slot9", "mcgrp_hotbar", "slot", ModLinkFormat.Icon, "key.hotbar.9", "9", LitWhen: "9", Icon: "hotbar/9", IconRev: "slotRev9"),
            new("mc.drop",    "gp_mc_drop",    "mcgrp_hotbar", null, ModLinkFormat.Lamp, "key.drop", "Q"),
            new("mc.swap",    "gp_mc_swap",    "mcgrp_hotbar", null, ModLinkFormat.Lamp, "key.swapOffhand", "F"),

            new("mc.inventory",    "gp_mc_inventory",    "mcgrp_ui", null, ModLinkFormat.Lamp, "key.inventory", "E"),
            new("mc.chat",         "gp_mc_chat",         "mcgrp_ui", null, ModLinkFormat.Lamp, "key.chat", "T"),
            new("mc.command",      "gp_mc_command",      "mcgrp_ui", null, ModLinkFormat.Lamp, "key.command", "/"),
            new("mc.advancements", "gp_mc_advancements", "mcgrp_ui", null, ModLinkFormat.Lamp, "key.advancements", "L"),
            new("mc.social",       "gp_mc_social",       "mcgrp_ui", null, ModLinkFormat.Lamp, "key.socialInteractions", "P"),
            new("mc.perspective",  "gp_mc_perspective",  "mcgrp_ui", "perspective", ModLinkFormat.Text, "key.togglePerspective", "F5", SizeRef: "Front"),
            new("mc.screenshot",   "gp_mc_screenshot",   "mcgrp_ui", null, ModLinkFormat.Lamp, "key.screenshot", "F2"),
            new("mc.fullscreen",   "gp_mc_fullscreen",   "mcgrp_ui", null, ModLinkFormat.Lamp, "key.fullscreen", "F11"),
            new("mc.debug",        "gp_mc_debug",        "mcgrp_ui", null, ModLinkFormat.Lamp, "key.debug.overlay", "F3"),
            new("mc.fps",          "gp_mc_fps",          "mcgrp_ui", "fps", ModLinkFormat.Integer),
        },
        DownloadUrl: "https://github.com/FrankieDedo/K2-Link/releases?q=minecraft");

    // ------------------------------------------------------------------ Space Engineers

    /// <summary>Space Engineers. Binds are the <c>MyControlsSpace</c> names the game writes to
    /// <c>SpaceEngineers.cfg</c>; defaults are the ones <c>MySandboxGame</c> registers
    /// (<c>AddDefaultGameControl</c>), read from the game's own code. The readings follow what the
    /// player CONTROLS: in a cockpit "dampeners" and "speed" are the ship's, on foot the suit's.</summary>
    public static readonly ModLinkGame SpaceEngineers = new(
        SpaceEngineersId, "se.", 0, "K2 Link (mod)",
        new[]
        {
            ("segrp_flight", "🚀"),
            ("segrp_suit",   "👨‍🚀"),
            ("segrp_ship",   "🛰"),
            ("segrp_nav",    "🧭"),
            ("segrp_toolbar","🧰"),
            ("segrp_ui",     "🖥"),
        },
        new ModLinkItem[]
        {
            new("se.dampeners",  "gp_se_dampeners",  "segrp_flight", "dampeners", ModLinkFormat.Lamp, "DAMPING", "Z"),
            new("se.reldampers", "gp_se_reldampers", "segrp_flight", "relativeDampeners", ModLinkFormat.Lamp, "DAMPING_RELATIVE", "Ctrl + Z"),
            new("se.jetpack",    "gp_se_jetpack",    "segrp_flight", "jetpack", ModLinkFormat.Lamp, "THRUSTS", "X"),
            new("se.lights",     "gp_se_lights",     "segrp_flight", "lights", ModLinkFormat.Lamp, "HEADLIGHTS", "L"),
            new("se.gear",       "gp_se_gear",       "segrp_flight", "parked", ModLinkFormat.Lamp, "LANDING_GEAR", "P"),
            new("se.power",      "gp_se_power",      "segrp_flight", "shipPower", ModLinkFormat.Lamp, "TOGGLE_REACTORS", "Y"),
            new("se.handbrake",  "gp_se_handbrake",  "segrp_flight", "handbrake", ModLinkFormat.Lamp),
            new("se.speed",      "gp_se_speed",      "segrp_flight", "speed", ModLinkFormat.Speed),

            // Pressing Energy switches the ship's power (the player's choice: the key reads the reserve,
            // and the reserve is what that switch protects).
            new("se.energy",     "gp_se_energy",     "segrp_suit", "suitEnergy", ModLinkFormat.Percent, "TOGGLE_REACTORS", "Y", AlarmBelow: 0.2),
            new("se.oxygen",     "gp_se_oxygen",     "segrp_suit", "suitOxygen", ModLinkFormat.Percent, AlarmBelow: 0.2),
            new("se.hydrogen",   "gp_se_hydrogen",   "segrp_suit", "suitHydrogen", ModLinkFormat.Percent, AlarmBelow: 0.2),
            new("se.health",     "gp_se_health",     "segrp_suit", "health", ModLinkFormat.Percent, AlarmBelow: 0.3),
            new("se.helmet",     "gp_se_helmet",     "segrp_suit", "helmet", ModLinkFormat.Lamp, "HELMET", "J"),
            new("se.broadcast",  "gp_se_broadcast",  "segrp_suit", null, ModLinkFormat.Lamp, "BROADCASTING", "O"),

            new("se.shipname",   "gp_se_shipname",   "segrp_ship", "shipName", ModLinkFormat.Text),
            new("se.battery",    "gp_se_battery",    "segrp_ship", "shipBattery", ModLinkFormat.Percent, AlarmBelow: 0.2),
            new("se.shiph2",     "gp_se_shiph2",     "segrp_ship", "shipHydrogen", ModLinkFormat.Percent, AlarmBelow: 0.2),
            new("se.shipo2",     "gp_se_shipo2",     "segrp_ship", "shipOxygen", ModLinkFormat.Percent),
            new("se.cargo",      "gp_se_cargo",      "segrp_ship", "shipCargo", ModLinkFormat.Percent),
            new("se.mass",       "gp_se_mass",       "segrp_ship", "shipMass", ModLinkFormat.Number, Unit: "t"),
            new("se.connector",  "gp_se_connector",  "segrp_ship", "connectorLocked", ModLinkFormat.Lamp),
            new("se.jump",       "gp_se_jump",       "segrp_ship", "jumpCharge", ModLinkFormat.Percent),

            new("se.altitude",   "gp_se_altitude",   "segrp_nav", "altitude", ModLinkFormat.Distance),
            new("se.gravity",    "gp_se_gravity",    "segrp_nav", "gravity", ModLinkFormat.Number, Unit: "g"),
            new("se.planet",     "gp_se_planet",     "segrp_nav", "planet", ModLinkFormat.Text),
            new("se.incockpit",  "gp_se_incockpit",  "segrp_nav", "inCockpit", ModLinkFormat.Lamp),

            new("se.slot1", "gp_se_slot1", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT1", "1"),
            new("se.slot2", "gp_se_slot2", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT2", "2"),
            new("se.slot3", "gp_se_slot3", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT3", "3"),
            new("se.slot4", "gp_se_slot4", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT4", "4"),
            new("se.slot5", "gp_se_slot5", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT5", "5"),
            new("se.slot6", "gp_se_slot6", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT6", "6"),
            new("se.slot7", "gp_se_slot7", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT7", "7"),
            new("se.slot8", "gp_se_slot8", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT8", "8"),
            new("se.slot9", "gp_se_slot9", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT9", "9"),
            new("se.slot0", "gp_se_slot0", "segrp_toolbar", null, ModLinkFormat.Lamp, "SLOT0", "0"),
            new("se.toolbarup",   "gp_se_toolbarup",   "segrp_toolbar", null, ModLinkFormat.Lamp, "TOOLBAR_UP", "."),
            new("se.toolbardown", "gp_se_toolbardown", "segrp_toolbar", null, ModLinkFormat.Lamp, "TOOLBAR_DOWN", ","),

            new("se.terminal",   "gp_se_terminal",   "segrp_ui", null, ModLinkFormat.Lamp, "TERMINAL", "K"),
            new("se.inventory",  "gp_se_inventory",  "segrp_ui", null, ModLinkFormat.Lamp, "INVENTORY", "I"),
            new("se.build",      "gp_se_build",      "segrp_ui", null, ModLinkFormat.Lamp, "BUILD_SCREEN", "G"),
            new("se.controlmenu","gp_se_controlmenu","segrp_ui", null, ModLinkFormat.Lamp, "CONTROL_MENU", "Minus"),
            new("se.remote",     "gp_se_remote",     "segrp_ui", null, ModLinkFormat.Lamp, "REMOTE_ACCESS_MENU", "Shift + K"),
            new("se.camera",     "gp_se_camera",     "segrp_ui", null, ModLinkFormat.Lamp, "CAMERA_MODE", "V"),
            new("se.hud",        "gp_se_hud",        "segrp_ui", null, ModLinkFormat.Lamp, "TOGGLE_HUD", "Tab"),
            new("se.signals",    "gp_se_signals",    "segrp_ui", null, ModLinkFormat.Lamp, "TOGGLE_SIGNALS", "H"),
            new("se.colorpicker","gp_se_colorpicker","segrp_ui", null, ModLinkFormat.Lamp, "COLOR_PICKER", "P"),
            new("se.chat",       "gp_se_chat",       "segrp_ui", null, ModLinkFormat.Lamp, "CHAT_SCREEN", "Enter"),
            new("se.players",    "gp_se_players",    "segrp_ui", null, ModLinkFormat.Lamp, "PLAYERS_SCREEN", "F3"),
            new("se.blueprints", "gp_se_blueprints", "segrp_ui", null, ModLinkFormat.Lamp, "BLUEPRINTS_MENU", "F10"),
            new("se.screenshot", "gp_se_screenshot", "segrp_ui", null, ModLinkFormat.Lamp, "SCREENSHOT", "F4"),
            new("se.quicksave",  "gp_se_quicksave",  "segrp_ui", null, ModLinkFormat.Lamp, "QUICK_SAVE", "Shift + F5"),
        },
        DownloadUrl: "https://steamcommunity.com/sharedfiles/filedetails/?id=3808623622");

    public static IReadOnlyList<ModLinkGame> Games { get; } = new[] { Minecraft, SpaceEngineers };

    private static readonly Dictionary<string, (ModLinkGame Game, ModLinkItem Item)> ByValue =
        Games.SelectMany(g => g.Items.Select(i => (g, i)))
             .ToDictionary(p => p.i.Value, p => (p.g, p.i), StringComparer.Ordinal);

    private static readonly Dictionary<string, (ModLinkGame Game, ModLinkItem Item)> ByLocKey =
        Games.SelectMany(g => g.Items.Select(i => (g, i)))
             .ToDictionary(p => p.i.LocKey, p => (p.g, p.i), StringComparer.Ordinal);

    /// <summary>Splits a stored value into the item and the OPTIONAL shortcut the user pinned by
    /// hand after a bar (<c>"se.dampeners|Ctrl + D"</c>), which outranks the game's bindings.</summary>
    public static (string Item, string? Keys) Split(string? value)
    {
        string v = (value ?? "").Trim();
        int bar = v.IndexOf('|');
        if (bar < 0) return (v, null);
        string keys = v[(bar + 1)..].Trim();
        return (v[..bar].Trim(), keys.Length > 0 ? keys : null);
    }

    /// <summary>The item a key stores, or null for a value no game knows.</summary>
    public static (ModLinkGame Game, ModLinkItem Item)? Find(string? value) =>
        ByValue.TryGetValue(Split(value).Item, out var hit) ? hit : null;

    /// <summary>The item behind a catalogue tile's caption loc key — how a plain <c>keys</c> tile
    /// is matched to the control it presses, the same identity Deadside's tiles use.</summary>
    public static (ModLinkGame Game, ModLinkItem Item)? FindByLocKey(string? locKey) =>
        locKey is not null && ByLocKey.TryGetValue(locKey, out var hit) ? hit : null;

    public static ModLinkGame? GameFor(string? profileId) =>
        Games.FirstOrDefault(g => string.Equals(g.ProfileId, profileId, StringComparison.Ordinal));

    /// <summary>Every item a <c>dp_modlink</c> key can hold (the live ones), for the value combos.</summary>
    public static IEnumerable<ModLinkItem> LiveItems() =>
        Games.SelectMany(g => g.Items).Where(i => i.Read is not null);

    /// <summary>The picker's families for one game: live items as <see cref="ActionType"/>,
    /// plain commands as ordinary <c>keys</c> actions carrying the shipped default.</summary>
    public static (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] Families(ModLinkGame game) =>
        game.Groups
            .Select(g => (g.LocKey, g.Glyph, game.Items
                .Where(i => i.GroupLocKey == g.LocKey)
                .Select(i => i.Read is null
                    ? new ActionTypeHelper.GameCommand("keys", i.Keys ?? "", i.LocKey)
                    : new ActionTypeHelper.GameCommand(ActionType, i.Value, i.LocKey))
                .ToArray()))
            .Where(f => f.Item3.Length > 0)
            .ToArray();
}
