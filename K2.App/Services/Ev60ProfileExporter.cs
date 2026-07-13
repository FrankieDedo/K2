using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports an Everest 60 profile to XML — mirrors <see cref="EvProfileExporter"/>'s
/// shape, on the schema confirmed against a real BaseCamp.db (Everest60KeyBidings/
/// Everest60Lightings — see BaseCampDbImporter's Everest 60 section for the same
/// schema used on the import side, including the caveats about LayerType/label
/// translation being best-effort rather than fully verified).
/// </summary>
public static class Ev60ProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    public static ExportResult ExportBaseCamp(Everest60Store store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: true);

    public static ExportResult ExportK2(Everest60Store store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: false);

    private static readonly Dictionary<int, string> DllKeyIdToLabel =
        Everest60RemapData.KeyCatalog
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);

    private static ExportResult Export(
        Everest60Store store, int slot, string profileName, string filePath, bool bcCompatible)
    {
        int exported = 0, skipped = 0;
        var reasons = new List<string>();

        var root = new XElement("Profile",
            new XElement("ProfileId", 0),
            new XElement("Id", slot),
            new XElement("DeviceType", "EverestMini"),
            new XElement("ProfileName", profileName),
            new XElement("OrderNo", slot));

        // ---- Key bindings ----
        foreach (var b in store.LoadKeyBindings(slot))
        {
            var table = Everest60RemapData.LedIndexToDllKeyIdArray;
            if (b.LedIndex < 0 || b.LedIndex >= table.Length) continue;
            int dllKeyId = table[b.LedIndex];

            string functionType, subType, functionValue, enteredValue = "";
            int layerType = b.Mode == "fn" ? 3 : 1;

            if (bcCompatible)
            {
                string? targetLabel = (b.Mode is "key" or "fn" or "shortcut") && DllKeyIdToLabel.TryGetValue(b.Value, out var lbl)
                    ? lbl : null;
                if (b.Mode == "media")
                {
                    var media = Everest60RemapData.MediaActions.FirstOrDefault(m => m.Code == b.Value);
                    if (media.Label is null) { skipped++; reasons.Add($"key led {b.LedIndex}: media code {b.Value} unrecognized — omitted"); continue; }
                    functionType = "Media"; subType = media.Label; functionValue = media.Label;
                }
                else if (targetLabel is null)
                {
                    skipped++;
                    reasons.Add($"key led {b.LedIndex}: target DllKeyId {b.Value} has no known label — omitted");
                    continue;
                }
                else if (b.Mode == "shortcut")
                {
                    functionType = "Keyboard Shortcuts"; subType = ""; functionValue = targetLabel;
                    enteredValue = b.ModifierMask.ToString();
                }
                else
                {
                    functionType = "Default"; subType = ""; functionValue = targetLabel;
                }
            }
            else
            {
                functionType = "K2Remap"; subType = b.Mode; functionValue = b.Value.ToString();
                enteredValue = b.ModifierMask.ToString();
            }

            root.Add(new XElement("Everest60KeyBidings",
                new XElement("ProfileId", 0),
                new XElement("KeyId", dllKeyId),
                new XElement("DLLKeyId", dllKeyId),
                new XElement("DLLMatrixIndex", b.LedIndex),
                new XElement("LayerType", layerType),
                new XElement("IsKeyAssigned", "true"),
                new XElement("FunctionType", functionType),
                new XElement("SubFunctionType", subType),
                new XElement("FunctionValue", functionValue),
                new XElement("FunctionEnteredValue", enteredValue),
                new XElement("IsSyncAcrossProfiles", "false"),
                new XElement("CustomURL", "")));
            exported++;
        }

        // ---- Lighting ----
        var lighting = store.LoadLighting(slot);
        if (lighting is not null)
        {
            int effIndex = lighting.ActiveMode == "custom" ? 7 : (Everest60Protocol.Effect)lighting.Effect switch
            {
                Everest60Protocol.Effect.Static    => 1,
                Everest60Protocol.Effect.Wave      => 2,
                Everest60Protocol.Effect.Tornado   => 3,
                Everest60Protocol.Effect.Breathing => 4,
                Everest60Protocol.Effect.Reactive  => 5,
                Everest60Protocol.Effect.Yeti      => 8,
                Everest60Protocol.Effect.Off       => 9,
                _ => 1,
            };

            root.Add(new XElement("Everest60Lightings",
                new XElement("ProfileId", 0),
                new XElement("EffIndex", effIndex),
                new XElement("EffectName", ((Everest60Protocol.Effect)lighting.Effect).ToString()),
                new XElement("Speed", lighting.SpeedPct),
                new XElement("Brightness", (int)lighting.Brightness),
                new XElement("Direction", lighting.DirIndex),
                new XElement("Color1", Hex(lighting.Color1)),
                new XElement("Color2", Hex(lighting.Color2)),
                new XElement("Color3", Hex(lighting.SideColor)),
                new XElement("IsActive", "true"),
                new XElement("CustomLightings", BuildCustomJson(lighting.CustomKeyColors))));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);

        return new ExportResult(exported, skipped, reasons);
    }

    private static string Hex(int rgb) => $"#{rgb:X6}".ToLowerInvariant();

    private static string BuildCustomJson(Dictionary<int, int> customColors)
    {
        var items = customColors.OrderBy(kv => kv.Key)
            .Select((kv, i) => $"{{\"Ids\":{i + 1},\"KeyCode\":{kv.Key},\"ColorHex\":\"{Hex(kv.Value)}\"}}");
        return "[" + string.Join(",", items) + "]";
    }
}
