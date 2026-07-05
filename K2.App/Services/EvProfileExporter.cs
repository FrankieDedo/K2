using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Esporta un profilo Everest in XML, sullo stesso schema reale confermato per il
/// DisplayPad (tabella Base Camp <c>EverestKeyBidings</c>, classe decompilata
/// <c>KeyboardBinding</c> — struttura identica a <c>DisplayPadLayerBidings</c> ma
/// SENZA <c>ParentId</c>/pagine, dato che l'Everest non ha cartelle). Il vocabolario
/// FunctionType/SubFunctionType/FunctionValue è lo stesso già verificato per il
/// DisplayPad (<see cref="DpProfileExporter"/>), condiviso nel codice Base Camp
/// reale tramite <c>BaseCampDbImporter.TranslateAction</c>.
///
/// Include sia i tasti regolari (KeyMatrix salvato in <see cref="EverestStore"/>)
/// sia i 4 tasti LCD numpad (NDK). Nota: in K2 le immagini/azioni NDK sono
/// impostazioni GLOBALI del device (non per-profilo — vedi
/// <c>EverestStore</c>/<c>ndk.{i}.*</c>), quindi ogni profilo esportato mostrerà lo
/// stesso contenuto NDK: è una semplificazione del modello dati K2 attuale, non
/// un limite di Base Camp. I 4 KeyId sintetici usati per gli NDK (9001-9004) sono
/// un'invenzione K2 (nessun DLLMatrixIndex reale noto) — per questo gli NDK
/// vengono esportati SOLO in modalità K2 (mai in modalità Base Camp compatibile).
/// </summary>
public static class EvProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    // KeyId sintetici per i 4 tasti LCD numpad (nessun DLLMatrixIndex reale noto).
    private const int NdkKeyIdBase = 9001;

    public static ExportResult ExportBaseCamp(EverestStore store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: true);

    public static ExportResult ExportK2(EverestStore store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: false);

    private static ExportResult Export(
        EverestStore store, int slot, string profileName, string filePath, bool bcCompatible)
    {
        int exported = 0, skipped = 0;
        var reasons = new List<string>();

        var root = new XElement("Profile",
            new XElement("ProfileId", 0),
            new XElement("Id", slot),
            new XElement("DeviceType", "Everest"),
            new XElement("ProfileName", profileName),
            new XElement("OrderNo", slot));

        // ---- Tasti regolari ----
        foreach (var k in store.LoadProfile(slot))
        {
            if (string.IsNullOrEmpty(k.ActionType)) continue;

            string? functionType = null, subType = null, funcValue = null;
            bool isAssigned = false;

            if (bcCompatible)
            {
                var mapped = MapActionToBc(k.ActionType, k.ActionValue);
                if (mapped is not null)
                {
                    (functionType, subType, funcValue) = mapped.Value;
                    isAssigned = true;
                    exported++;
                }
                else
                {
                    skipped++;
                    reasons.Add($"key matrix {k.KeyMatrix}: azione \"{k.ActionType}\" non esiste in Base Camp — omessa");
                    continue; // niente immagine/altro da preservare per un tasto regolare senza icona
                }
            }
            else
            {
                functionType = "K2Action";
                subType      = k.ActionType;
                funcValue    = k.ActionValue ?? "";
                isAssigned   = true;
                exported++;
            }

            root.Add(BuildBinding(k.KeyMatrix, k.KeyMatrix, k.Label ?? $"K{k.KeyMatrix}",
                isAssigned, isTouchKey: false, functionType, subType, funcValue, imageB64: null));
        }

        // ---- Tasti LCD numpad (NDK), 0-3 — device-globali in K2 ----
        for (int i = 0; i < 4; i++)
        {
            string? imagePath = store.GetSetting($"ndk.{i}.imagePath");
            string? ndkType   = store.GetSetting($"ndk.{i}.actionType");
            string? ndkValue  = store.GetSetting($"ndk.{i}.actionValue");
            bool hasImage = !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath);
            bool hasAction = !string.IsNullOrEmpty(ndkType);
            if (!hasImage && !hasAction) continue;

            string? imageB64 = null;
            if (hasImage)
            {
                try { imageB64 = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(imagePath!)); }
                catch { /* immagine illeggibile: esporta senza icona */ }
            }

            int keyId = NdkKeyIdBase + i;
            string? functionType = null, subType = null, funcValue = null;
            bool isAssigned = false;

            if (hasAction)
            {
                if (bcCompatible)
                {
                    // Nessun KeyId/DLLMatrixIndex reale noto per gli NDK: non possiamo
                    // garantire che Base Camp accetti questo tasto -> lo omettiamo sempre
                    // in modalità compatibile, mantenendo comunque l'icona se presente.
                    skipped++;
                    reasons.Add($"ndk #{i}: tasto LCD numpad non esportabile in modalità Base Camp (nessun KeyId reale noto)");
                }
                else
                {
                    functionType = "K2Action";
                    subType      = ndkType;
                    funcValue    = ndkValue ?? "";
                    isAssigned   = true;
                    exported++;
                }
            }

            if (!bcCompatible || hasImage)
            {
                root.Add(BuildBinding(keyId, keyId, $"NDK{i}",
                    isAssigned, isTouchKey: true, functionType, subType, funcValue, imageB64));
            }
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);

        return new ExportResult(exported, skipped, reasons);
    }

    private static XElement BuildBinding(
        int keyId, int dllMatrixIndex, string keyName, bool isAssigned, bool isTouchKey,
        string? functionType, string? subType, string? funcValue, string? imageB64)
    {
        // Tag "EverestKeyBidings" = nome della tabella reale Base Camp (stessa
        // assunzione già fatta per il DisplayPad — MAI verificato su un export
        // reale di un profilo Everest, vedi _PROJECT_MAP.md).
        return new XElement("EverestKeyBidings",
            new XElement("ProfileId", 0),
            new XElement("KeyId", keyId),
            new XElement("KeyName", keyName),
            new XElement("KeyNameFull", keyName),
            new XElement("IsKeyAssigned", isAssigned ? "true" : "false"),
            new XElement("IsTouchKey", isTouchKey ? "true" : "false"),
            new XElement("FunctionType", functionType ?? "Default"),
            new XElement("SubFunctionType", subType ?? ""),
            new XElement("FunctionValue", funcValue ?? ""),
            new XElement("FunctionEnteredValue", ""),
            new XElement("OnPressRelease", "Press"),
            new XElement("IsSyncAcrossProfiles", "false"),
            new XElement("base64Image", imageB64 ?? ""),
            new XElement("ImageFilePath", ""),
            new XElement("IsDefaultTouchKeyImage", "false"),
            new XElement("modified_at", DateTime.Now.ToString("o")),
            new XElement("DLLKeyId", keyId),
            new XElement("DLLKeyName", keyName),
            new XElement("DLLMatrixIndex", dllMatrixIndex),
            new XElement("OptionalText", ""));
    }

    /// <summary>Stesso vocabolario confermato di <see cref="DpProfileExporter"/> (le
    /// stringhe FunctionType/SubFunctionType native sono condivise da Base Camp fra
    /// Everest e DisplayPad tramite <c>BaseCampDbImporter.TranslateAction</c>).</summary>
    private static (string FunctionType, string SubFunctionType, string FunctionValue)? MapActionToBc(
        string actionType, string? actionValue)
    {
        var v = (actionValue ?? "").Trim();

        switch (actionType)
        {
            case "exec":
                return string.IsNullOrEmpty(v) ? null : ("Run Program", v, v);

            case "folder":
                return string.IsNullOrEmpty(v) ? null : ("Open Folder", v, v);

            case "browser":
                return ("Run browser", "Run browser", "Run browser");

            case "keys":
                return string.IsNullOrEmpty(v) ? null : ("Keyboard Shortcuts", v, v);

            case "profile":
            {
                string? sft = v.ToLowerInvariant() switch
                {
                    "next" or "next profile" => "Next Profile",
                    "previous" or "previous profile" or "prev" => "Previous Profile",
                    _ => int.TryParse(v, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null
                };
                return sft is null ? null : ("Profile", sft, sft);
            }

            case "oscmd":
            {
                string? sft = v.ToLowerInvariant() switch
                {
                    "run task manager" or "task manager" or "taskmgr" => "Run task manager",
                    "calculator" or "calc" => "Calculator",
                    "run explorer" or "explorer" => "Run explorer",
                    "lock computer" or "lock" => "Lock computer",
                    "shutdown" => "Shutdown",
                    "restart" => "Restart",
                    "sleep" => "Sleep",
                    "hibernate" => "Hibernate",
                    _ => null
                };
                return sft is null ? null : ("OS Commands", sft, sft);
            }

            case "media":
            {
                string? sft = v.ToLowerInvariant() switch
                {
                    "play/pause" or "play-pause" or "playpause" => "Play/Pause",
                    "stop" => "Stop",
                    "previous track" or "prev" or "previous" => "Previous track",
                    "next track" or "next" => "Next track",
                    "volume up" or "vol up" or "volup" => "Volume up",
                    "volume down" or "vol down" or "voldown" => "Volume down",
                    "mute" => "Mute",
                    _ => null
                };
                return sft is null ? null : ("Media", sft, sft);
            }

            case "mouse":
            {
                string? sft = v.ToLowerInvariant() switch
                {
                    "left button" => "Left button",
                    "right button" => "Right button",
                    "middle button" => "Middle button",
                    "forward" => "Forward",
                    "backward" => "Backward",
                    "scroll up" => "Scroll up",
                    "scroll down" => "Scroll down",
                    "scroll left" => "Scroll left",
                    "scroll right" => "Scroll right",
                    _ => null
                };
                return sft is null ? null : ("Mouse", sft, sft);
            }

            case "text":
                return v.Length == 1 ? ("Default", v, v) : null;

            // dp_folder/dp_back: concetti DisplayPad-only, non applicabili
            // all'Everest. pyscript/command/url/macro/multi/pcinfo/clock/none:
            // nessun equivalente Base Camp confermato -> omessi.
            default:
                return null;
        }
    }
}
