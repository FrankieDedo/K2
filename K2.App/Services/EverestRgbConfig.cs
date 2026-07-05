using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>
/// Configurazione ESTERNA dei parametri RGB per-effetto della tastiera Everest,
/// caricata da <c>everest_rgb.json</c> (accanto all'eseguibile) <b>ad ogni
/// applicazione</b> dell'effetto.
///
/// <para><b>Scopo.</b> Permette di regolare i campi della struct
/// <see cref="EverestSdkNative.EffData"/> per ciascun preset (byAll, bySpeed,
/// byDirection, byWidth, byRandColor, numero di colori) <b>senza ricompilare</b>:
/// si modifica il JSON, si ri-applica l'effetto e i nuovi valori vengono usati
/// immediately. Useful while we're reverse-engineering the firmware protocol
/// confrontando i dump USB con Base Camp.</para>
///
/// <para><b>Dove sta il file.</b> Viene letto da
/// <c>AppContext.BaseDirectory\everest_rgb.json</c> (la cartella dell'exe).
/// È incluso nel progetto come Content <c>PreserveNewest</c>: la copia
/// "canonica" sta in <c>K2.App\everest_rgb.json</c> e viene copiata in
/// <c>bin\</c> alla build. Per un tuning lampo (niente rebuild) si edita la
/// copia in <c>bin\</c>; per renderlo permanente si edita quella nel progetto.
/// If the file is missing or unreadable, <see cref="Default"/> values are used.</para>
/// </summary>
public sealed class EverestRgbConfig
{
    /// <summary>Parametri della struct EffData per un singolo preset.</summary>
    public sealed class EffectDef
    {
        /// <summary>1 = applica a tutti i tasti. Base Camp usa 0 per alcuni preset.</summary>
        public int ByAll { get; set; } = 1;

        /// <summary>Speed: -1 = use the UI value; otherwise fixed value (0/1/2 or 255).</summary>
        public int BySpeed { get; set; } = -1;

        /// <summary>Direzione: di norma 255 (il firmware la ignora qui).</summary>
        public int ByDirection { get; set; } = 255;

        /// <summary>Larghezza onda: di norma 255.</summary>
        public int ByWidth { get; set; } = 255;

        /// <summary>1 = colori casuali / rainbow.</summary>
        public int ByRandColor { get; set; } = 0;

        /// <summary>Quanti colori inviare (1/2/3). I rimanenti vengono azzerati.</summary>
        public int ColorCount { get; set; } = 3;
    }

    /// <summary>Mappa nome-preset (come l'enum <c>EverestService.Effect</c>) → parametri.</summary>
    public Dictionary<string, EffectDef> Effects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "everest_rgb.json");

    /// <summary>Legge il file (se presente e valido), altrimenti i default.</summary>
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
                    // Ricrea il dizionario case-insensitive (il deserializer ne crea uno ordinale).
                    var ci = new Dictionary<string, EffectDef>(cfg.Effects, StringComparer.OrdinalIgnoreCase);
                    return new EverestRgbConfig { Effects = ci };
                }
            }
        }
        catch (Exception ex)
        {
            App.WriteLog("[EverestRgbConfig] lettura everest_rgb.json fallita, uso i default: " + ex.Message);
        }
        return Default();
    }

    /// <summary>Parametri per il preset indicato; se assente, valori neutri di default.</summary>
    public EffectDef For(string effectName) =>
        Effects.TryGetValue(effectName, out var def) ? def : new EffectDef();

    /// <summary>
    /// Default "di fabbrica" — ricavati dal confronto USB con Base Camp
    /// (2026-05-30). Da affinare via JSON man mano che testiamo gli altri preset.
    /// </summary>
    public static EverestRgbConfig Default() => new()
    {
        Effects = new(StringComparer.OrdinalIgnoreCase)
        {
            // Static: ChangeEffect lo rifiuta (probabile via software) — qui per completezza.
            ["Static"]    = new EffectDef { ByAll = 0, BySpeed = 255, ColorCount = 1 },
            // Breath: confronto bc_breath vs k2_breath -> byAll=0, un colore (gli altri 0).
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
