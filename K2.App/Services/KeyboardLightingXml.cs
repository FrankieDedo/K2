using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Builds the <c>&lt;EverestLightings&gt;</c> block of an exported profile — shared by
/// Everest Max and the MacroPad, which really do use the same table/wrapper and the
/// same effect byte space (see <see cref="BaseCampDbImporter"/>'s "Shared keyboard
/// lighting" section). Exact inverse of the import side
/// (<see cref="BaseCampDbImporter.ApplyLightingToStore"/> /
/// <see cref="BaseCampDbImporter.ParseKeyboardCustomLighting"/>), added 2026-07-26:
/// both exporters used to write key bindings ONLY, so a K2 export → re-import
/// round-trip silently lost every lighting and settings value it had just imported.
/// </summary>
internal static class KeyboardLightingXml
{
    /// <summary>The 9 effect rows Base Camp writes per profile, in its own menu order,
    /// with the exact three strings a real export carries: <c>EffIndex</c> (the enum
    /// member name), <c>EffMenuIndex</c> and the display <c>EffectName</c> — all three
    /// confirmed 2026-07-26 against real Base Camp XML exports.</summary>
    private static readonly (byte Eff, string Index, string MenuIndex, string Name)[] s_effects =
    {
        ((byte)EverestSdkNative.EffectIndex.Static,    "Static",    "Static",    "Static"),
        ((byte)EverestSdkNative.EffectIndex.Wave,      "ColorWave", "Colorwave", "Color Wave"),
        ((byte)EverestSdkNative.EffectIndex.Tornado,   "Tornado",   "Tornado",   "Tornado"),
        ((byte)EverestSdkNative.EffectIndex.Breath,    "Breathing", "Breathing", "Breathing"),
        ((byte)EverestSdkNative.EffectIndex.ReactiveA, "Reactivea", "Reactive",  "Reactive"),
        ((byte)EverestSdkNative.EffectIndex.Matrix,    "Matrix",    "Matrix",    "Matrix"),
        ((byte)EverestSdkNative.EffectIndex.Custom,    "Custom",    "Custom",    "Custom"),
        ((byte)EverestSdkNative.EffectIndex.Yeti,      "Yeti",      "Yetimode",  "YETI MODE"),
        ((byte)EverestSdkNative.EffectIndex.Off,       "OFF",       "Off",       "Off"),
    };

    /// <summary>The 8 paint brushes nested inside the Custom row's own
    /// <c>CustomLightings</c> payload (Custom itself isn't one of them).</summary>
    private static readonly (byte Eff, int MenuIndex, string Name)[] s_customBrushes =
    {
        ((byte)EverestSdkNative.EffectIndex.Static,    0, "Static"),
        ((byte)EverestSdkNative.EffectIndex.Wave,      1, "Color Wave"),
        ((byte)EverestSdkNative.EffectIndex.Tornado,   2, "Tornado"),
        ((byte)EverestSdkNative.EffectIndex.Breath,    3, "Breathing"),
        ((byte)EverestSdkNative.EffectIndex.ReactiveA, 4, "Reactive"),
        ((byte)EverestSdkNative.EffectIndex.Matrix,    5, "Matrix"),
        ((byte)EverestSdkNative.EffectIndex.Yeti,      7, "YETI MODE"),
        ((byte)EverestSdkNative.EffectIndex.Off,       8, "Off"),
    };

    /// <summary>
    /// One <c>&lt;EverestLightings&gt;</c> wrapper with a <c>&lt;Lighting&gt;</c> row per
    /// effect, read back from the store keys the RGB panel persists
    /// (<paramref name="rgbPrefix"/> + <c>{effByte}.speed|direction|brightness|color1..3</c>,
    /// plus <c>{rgbPrefix}effect</c> for which one is active).
    /// <paramref name="customColorsKey"/>/<paramref name="customEffectsKey"/> feed the
    /// Custom row's per-key payload; pass null to leave it empty.
    /// </summary>
    public static XElement BuildLightings(
        Func<string, string?> getSetting, string rgbPrefix,
        string? customColorsKey, string? customEffectsKey, int ledCount)
    {
        int? activeEff = int.TryParse(getSetting(rgbPrefix + "effect"), out var ae) ? ae : null;

        var wrapper = new XElement("EverestLightings");
        foreach (var (eff, index, menuIndex, name) in s_effects)
        {
            string p = $"{rgbPrefix}{eff}.";
            int Int(string key, int fallback) =>
                int.TryParse(getSetting(p + key), out var v) ? v : fallback;

            var row = new XElement("Lighting",
                new XElement("ProfileId", 0),
                new XElement("EffIndex", index),
                new XElement("EffMenuIndex", menuIndex),
                new XElement("EffectName", name),
                new XElement("Type", 0),
                new XElement("Speed", Int("speed", 50)),
                new XElement("Brightness", Int("brightness", 100)),
                // Base Camp's Direction is a 0-based UI index, not the wire code —
                // see BaseCampDbImporter.LightingDirIndex.
                new XElement("Direction", Int("direction", 0)),
                new XElement("Color1", Hex(Int("color1", 0x900000))),
                new XElement("Color2", Hex(Int("color2", 0))),
                new XElement("Color3", Hex(Int("color3", 0))),
                new XElement("isAcrossProfile", "false"),
                new XElement("isAcrossDevice", "false"),
                new XElement("IsActive", activeEff == eff ? "true" : "false"),
                new XElement("modified_at", DateTime.Now.ToString("o")));

            if (eff == (byte)EverestSdkNative.EffectIndex.Custom)
                row.Add(new XElement("CustomLightings",
                    BuildCustomJson(getSetting, customColorsKey, customEffectsKey, ledCount)));

            wrapper.Add(row);
        }
        return wrapper;
    }

    /// <summary>Rebuilds the nested per-key payload of the Custom row — exact inverse of
    /// <see cref="BaseCampDbImporter.ParseKeyboardCustomLighting"/>, whose doc comment
    /// describes the shape.</summary>
    private static string BuildCustomJson(
        Func<string, string?> getSetting, string? customColorsKey, string? customEffectsKey, int ledCount)
    {
        var colors = ParseStoredColors(customColorsKey is null ? null : getSetting(customColorsKey));
        var effects = ParseStoredEffects(customEffectsKey is null ? null : getSetting(customEffectsKey));

        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var (eff, menuIndex, name) in s_customBrushes)
        {
            if (!first) sb.Append(',');
            first = false;

            string inner;
            if (eff == (byte)EverestSdkNative.EffectIndex.Static)
            {
                var parts = new List<string>(ledCount);
                for (int i = 0; i < ledCount; i++)
                {
                    int rgb = colors.TryGetValue(i, out var c) ? c : 0;
                    parts.Add($"{{\"r\":{(rgb >> 16) & 0xFF},\"g\":{(rgb >> 8) & 0xFF},\"b\":{rgb & 0xFF}}}");
                }
                inner = "{\"color\":[" + string.Join(",", parts) + "]}";
            }
            else
            {
                var flags = new List<string>(ledCount);
                for (int i = 0; i < ledCount; i++)
                    flags.Add(effects.TryGetValue(i, out var e) && e == eff ? "1" : "0");
                inner = "{\"effValue\":[" + string.Join(",", flags) + "]}";
            }

            sb.Append($"{{\"Id\":0,\"ProfileId\":0,\"EffIndex\":{eff},\"EffMenuIndex\":{menuIndex},")
              .Append($"\"EffectName\":\"{name}\",\"IsActive\":false,")
              .Append("\"CustomLightings\":").Append(JsonString(inner)).Append('}');
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Reads back a <c>{"ledIndex":"#RRGGBB"}</c> settings blob (the shape the
    /// Custom Lighting panels persist).</summary>
    private static Dictionary<int, int> ParseStoredColors(string? json)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return result;
            foreach (var kv in dict)
                if (int.TryParse(kv.Key, out int led))
                    result[led] = BaseCampDbImporter.ParseBcColor(kv.Value);
        }
        catch { /* malformed blob — export an unpainted board */ }
        return result;
    }

    private static Dictionary<int, byte> ParseStoredEffects(string? json)
    {
        var result = new Dictionary<int, byte>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, byte>>(json);
            if (dict is null) return result;
            foreach (var kv in dict)
                if (int.TryParse(kv.Key, out int led))
                    result[led] = kv.Value;
        }
        catch { /* malformed blob */ }
        return result;
    }

    /// <summary>Base Camp stores the inner payload as a JSON STRING inside the outer
    /// JSON, so it has to be escaped rather than embedded raw.</summary>
    private static string JsonString(string raw) => System.Text.Json.JsonSerializer.Serialize(raw);

    internal static string Hex(int rgb) =>
        "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
}
