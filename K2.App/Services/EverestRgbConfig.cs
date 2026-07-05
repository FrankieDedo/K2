using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>
/// EXTERNAL configuration of the Everest keyboard's per-effect RGB parameters,
/// loaded from <c>everest_rgb.json</c> (next to the executable) <b>every time
/// the effect is applied</b>.
///
/// <para><b>Purpose.</b> Lets you tweak the fields of the
/// <see cref="EverestSdkNative.EffData"/> struct for each preset (byAll, bySpeed,
/// byDirection, byWidth, byRandColor, color count) <b>without recompiling</b>:
/// edit the JSON, re-apply the effect, and the new values are used
/// immediately. Useful while we're reverse-engineering the firmware protocol
/// by comparing USB dumps against Base Camp.</para>
///
/// <para><b>Where the file lives.</b> Read from
/// <c>AppContext.BaseDirectory\everest_rgb.json</c> (the exe's folder).
/// It's included in the project as Content <c>PreserveNewest</c>: the
/// "canonical" copy lives in <c>K2.App\everest_rgb.json</c> and gets copied
/// to <c>bin\</c> on build. For quick tuning (no rebuild) edit the copy in
/// <c>bin\</c>; to make it permanent, edit the one in the project.
/// If the file is missing or unreadable, <see cref="Default"/> values are used.</para>
/// </summary>
public sealed class EverestRgbConfig
{
    /// <summary>EffData struct parameters for a single preset.</summary>
    public sealed class EffectDef
    {
        /// <summary>1 = apply to all keys. Base Camp uses 0 for some presets.</summary>
        public int ByAll { get; set; } = 1;

        /// <summary>Speed: -1 = use the UI value; otherwise fixed value (0/1/2 or 255).</summary>
        public int BySpeed { get; set; } = -1;

        /// <summary>Direction: normally 255 (the firmware ignores it here).</summary>
        public int ByDirection { get; set; } = 255;

        /// <summary>Wave width: normally 255.</summary>
        public int ByWidth { get; set; } = 255;

        /// <summary>1 = random / rainbow colors.</summary>
        public int ByRandColor { get; set; } = 0;

        /// <summary>How many colors to send (1/2/3). The rest are zeroed out.</summary>
        public int ColorCount { get; set; } = 3;
    }

    /// <summary>Preset name (matching the <c>EverestService.Effect</c> enum) -> parameters map.</summary>
    public Dictionary<string, EffectDef> Effects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "everest_rgb.json");

    /// <summary>Reads the file (if present and valid), otherwise falls back to defaults.</summary>
    public static EverestRgbConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<EverestRgbConfig>(
                    File.ReadAllText(FilePath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                if (cfg?.Effects is { Count: > 0 })
                {
                    // Recreate the dictionary as case-insensitive (the deserializer creates an ordinal one).
                    var ci = new Dictionary<string, EffectDef>(cfg.Effects, StringComparer.OrdinalIgnoreCase);
                    return new EverestRgbConfig { Effects = ci };
                }
            }
        }
        catch (Exception ex)
        {
            App.WriteLog("[EverestRgbConfig] failed to read everest_rgb.json, using defaults: " + ex.Message);
        }
        return Default();
    }

    /// <summary>Parameters for the given preset; if absent, neutral default values.</summary>
    public EffectDef For(string effectName) =>
        Effects.TryGetValue(effectName, out var def) ? def : new EffectDef();

    /// <summary>
    /// "Factory" defaults — derived from comparing USB dumps against Base Camp
    /// (2026-05-30). To be refined via JSON as we test the other presets.
    /// </summary>
    public static EverestRgbConfig Default() => new()
    {
        Effects = new(StringComparer.OrdinalIgnoreCase)
        {
            // Static: ChangeEffect rejects it (probably software-only) — listed here for completeness.
            ["Static"]    = new EffectDef { ByAll = 0, BySpeed = 255, ColorCount = 1 },
            // Breath: comparing bc_breath vs k2_breath -> byAll=0, one color (the others 0).
            ["Breath"]    = new EffectDef { ByAll = 0, ColorCount = 2 },
            ["Wave"]      = new EffectDef { ByAll = 0, ColorCount = 3 },
            ["ReactiveA"] = new EffectDef { ByAll = 0, ColorCount = 1 },
            ["ReactiveB"] = new EffectDef { ByAll = 0, ColorCount = 1 },
            ["ReactiveC"] = new EffectDef { ByAll = 0, ColorCount = 1 },
            ["Yeti"]      = new EffectDef { ByAll = 0, ColorCount = 3 },
            ["Tornado"]   = new EffectDef { ByAll = 0, ColorCount = 3 },
            ["Matrix"]    = new EffectDef { ByAll = 0, ColorCount = 1 },
            ["Off"]       = new EffectDef { ByAll = 0, ColorCount = 0 },
        }
    };
}
