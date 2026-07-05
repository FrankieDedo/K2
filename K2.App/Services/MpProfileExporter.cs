using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports a MacroPad profile to XML, on the same schema (never verified against a
/// real export, only against the decompiled "Makalu" code) as the Base Camp table
/// <c>MakaluKeyBindings</c> — see <see cref="BaseCampDbImporter.TranslateMakaluAction"/>
/// for the confirmed FunctionType/FunctionValue vocabulary.
///
/// Unlike the DisplayPad, this schema has NO <c>SubFunctionType</c> or
/// per-key images: just KeyId (1-12), FunctionType, FunctionValue,
/// FunctionEnteredValue, ONKeyPressRelease, SyncAcrossProfilesKeyBinding, CustomURL.
///
/// <list type="bullet">
/// <item><b>Base Camp compatible</b> (<see cref="ExportBaseCamp"/>): only actions
/// with a confirmed native MacroPad FunctionType/FunctionValue. The others are
/// omitted (key = no function).</item>
/// <item><b>K2 only</b> (<see cref="ExportK2"/>): <c>FunctionType="K2Action"</c>,
/// <c>FunctionEnteredValue</c> = literal K2 ActionType, <c>FunctionValue</c> =
/// literal K2 ActionValue (lossless round-trip).</item>
/// </list>
/// </summary>
public static class MpProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    public static ExportResult ExportBaseCamp(
        MacroPadStore store, int deviceId, int slot, string profileName, string filePath)
        => Export(store, deviceId, slot, profileName, filePath, bcCompatible: true);

    public static ExportResult ExportK2(
        MacroPadStore store, int deviceId, int slot, string profileName, string filePath)
        => Export(store, deviceId, slot, profileName, filePath, bcCompatible: false);

    private static ExportResult Export(
        MacroPadStore store, int deviceId, int slot, string profileName, string filePath, bool bcCompatible)
    {
        var keys = store.LoadProfile(deviceId, slot);
        var byIndex = new Dictionary<int, MacroKeyRecord>();
        foreach (var k in keys) byIndex[k.KeyIndex] = k;

        int exported = 0, skipped = 0;
        var reasons = new List<string>();

        var root = new XElement("Profile",
            new XElement("ProfileId", 0),
            new XElement("Id", slot),
            new XElement("DeviceType", "MacroPad"),
            new XElement("ProfileName", profileName),
            new XElement("OrderNo", slot));

        for (int i = 0; i < 12; i++)
        {
            byIndex.TryGetValue(i, out var rec);
            string? functionType = null, functionValue = null, functionEntered = null;
            bool isAssigned = false;

            if (rec is not null && !string.IsNullOrEmpty(rec.ActionType))
            {
                if (bcCompatible)
                {
                    var mapped = MapActionToMakalu(rec.ActionType, rec.ActionValue);
                    if (mapped is not null)
                    {
                        (functionType, functionValue) = mapped.Value;
                        isAssigned = true;
                        exported++;
                    }
                    else
                    {
                        skipped++;
                        reasons.Add($"key #{i}: action \"{rec.ActionType}\" doesn't exist on the Base Camp MacroPad — omitted");
                    }
                }
                else
                {
                    functionType    = "K2Action";
                    functionEntered = rec.ActionType;
                    functionValue   = rec.ActionValue ?? "";
                    isAssigned      = true;
                    exported++;
                }
            }

            // Tag "MakaluKeyBindings" = real Base Camp table name (for consistency
            // with the assumption already made for DisplayPadLayerBidings/EverestKeyBidings —
            // NEVER verified against a real export, see _PROJECT_MAP.md).
            root.Add(new XElement("MakaluKeyBindings",
                new XElement("ProfileId", 0),
                new XElement("KeyId", i + 1),
                new XElement("KeyName", $"M{i + 1}"),
                new XElement("IsKeyAssigned", isAssigned ? "true" : "false"),
                new XElement("FunctionType", functionType ?? "Default"),
                new XElement("FunctionValue", functionValue ?? ""),
                new XElement("FunctionEnteredValue", functionEntered ?? ""),
                new XElement("ONKeyPressRelease", "Press"),
                new XElement("SyncAcrossProfilesKeyBinding", "false"),
                new XElement("CustomURL", "")));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);

        return new ExportResult(exported, skipped, reasons);
    }

    /// <summary>
    /// Reverse translation of <see cref="BaseCampDbImporter.TranslateMakaluAction"/>:
    /// K2 ActionType/ActionValue -> native MacroPad FunctionType/FunctionValue.
    /// Returns <c>null</c> if there's no confirmed MacroPad equivalent.
    /// </summary>
    private static (string FunctionType, string FunctionValue)? MapActionToMakalu(string actionType, string? actionValue)
    {
        var v = (actionValue ?? "").Trim();

        switch (actionType)
        {
            case "exec":
                return string.IsNullOrEmpty(v) ? null : ("Run Program", v);

            case "keys":
                return string.IsNullOrEmpty(v) ? null : ("Keyboard Shortcuts", v);

            case "browser":
                return ("OS Commands", "Run browser");

            case "media":
                return v.ToLowerInvariant() switch
                {
                    "play_pause"  => ("Media", "Play/Pause"),
                    "stop"        => ("Media", "Stop"),
                    "prev_track"  => ("Media", "Previous track"),
                    "next_track"  => ("Media", "Next track"),
                    "volume_up"   => ("Media", "Volume up"),
                    "volume_down" => ("Media", "Volume down"),
                    "mute"        => ("Media", "Mute"),
                    "mic_mute"    => ("Media", "Mic Mute"),
                    _ => ((string, string)?)null
                };

            case "mouse":
                return v.ToLowerInvariant() switch
                {
                    "left button"   => ("Mouse", "Left button"),
                    "right button"  => ("Mouse", "Right button"),
                    "middle button" => ("Mouse", "Middle button"),
                    "backward"      => ("Mouse", "Backward"),
                    "forward"       => ("Mouse", "Forward"),
                    "scroll up"     => ("Mouse Wheel", "Scroll Up"),
                    "scroll down"   => ("Mouse Wheel", "Scroll Down"),
                    // "scroll left"/"scroll right": the MacroPad only has a single
                    // vertical wheel (Mouse_Wheel_String only has Up/Down) — no equivalent.
                    _ => ((string, string)?)null
                };

            case "profile":
                return v.ToLowerInvariant() switch
                {
                    "next" or "next profile" => ("Mouse", "Next Profile"),
                    "previous" or "previous profile" or "prev" => ("Mouse", "Previous Profile"),
                    // Jumping directly to a numeric slot has no known
                    // MacroPad firmware code (only cyclic Next/Previous).
                    _ => ((string, string)?)null
                };

            case "oscmd":
                return v.ToLowerInvariant() switch
                {
                    "run task manager" or "task manager" or "taskmgr" => ("OS Commands", "Run task manager"),
                    "lock computer" or "lock" => ("OS Commands", "Lock computer"),
                    "shutdown" => ("OS Commands", "Shut down"),
                    "sleep" => ("OS Commands", "Sleep"),
                    "hibernate" => ("OS Commands", "Hibernate"),
                    "calculator" or "calc" => ("OS Commands", "Calculator"),
                    // "run explorer"/"explorer": no OS_Command_String entry for the MacroPad.
                    _ => ((string, string)?)null
                };

            // folder, url, text, command, pyscript, dp_folder, dp_back, pcinfo,
            // clock, multi, createfolder, back, none: no confirmed Base Camp
            // MacroPad equivalent -> omitted.
            default:
                return null;
        }
    }
}
