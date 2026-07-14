using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports an Everest 60 profile to XML — mirrors <see cref="EvProfileExporter"/>'s
/// shape (same confirmed <c>Everest60KeyBidings</c>/<c>Everest60Lightings</c> schema,
/// see BaseCampDbImporter's Everest 60 section). Key Binding (2026-07-14, second pass)
/// is a K2Action like every other device now, not a raw firmware remap — so the
/// FunctionType/SubFunctionType/FunctionValue vocabulary below is the SAME one
/// <see cref="EvProfileExporter"/> uses for Everest Max (shared by Base Camp across
/// devices via <c>BaseCampDbImporter.TranslateAction</c>), not the old Mode/Value/
/// ModifierMask remap encoding.
/// </summary>
public static class Ev60ProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    public static ExportResult ExportBaseCamp(Everest60Store store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: true);

    public static ExportResult ExportK2(Everest60Store store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: false);

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

        // ---- Keys ----
        var table = Everest60RemapData.LedIndexToDllKeyIdArray;
        foreach (var k in store.LoadProfile(slot))
        {
            if (string.IsNullOrEmpty(k.ActionType)) continue;
            if (k.LedIndex < 0 || k.LedIndex >= table.Length) continue;
            int dllKeyId = table[k.LedIndex];

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
                    reasons.Add($"key led {k.LedIndex}: action \"{k.ActionType}\" doesn't exist in Base Camp — omitted");
                    continue;
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

            root.Add(new XElement("Everest60KeyBidings",
                new XElement("ProfileId", 0),
                new XElement("KeyId", dllKeyId),
                new XElement("DLLKeyId", dllKeyId),
                new XElement("DLLMatrixIndex", k.LedIndex),
                new XElement("LayerType", 1),
                new XElement("IsKeyAssigned", isAssigned ? "true" : "false"),
                new XElement("FunctionType", functionType ?? "Default"),
                new XElement("SubFunctionType", subType ?? ""),
                new XElement("FunctionValue", funcValue ?? ""),
                new XElement("FunctionEnteredValue", ""),
                new XElement("IsSyncAcrossProfiles", "false"),
                new XElement("CustomURL", "")));
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

    /// <summary>Same confirmed vocabulary as <see cref="EvProfileExporter"/>'s own copy
    /// (the native FunctionType/SubFunctionType strings are shared by Base Camp across
    /// devices via <c>BaseCampDbImporter.TranslateAction</c>).</summary>
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

            default:
                return null;
        }
    }
}
