using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Exports a MacroPad profile to XML, on the REAL Base Camp schema — CORRECTED
/// 2026-07-26 against a real BaseCamp.db and a real BC XML export: the MacroPad's
/// 12 keys (KeyId 170-179/220/221 = M1-M12, same scheme DisplayPad uses) live in
/// the SAME <c>EverestKeyBidings</c> table/<c>EverestKeyBindings</c> XML wrapper as
/// Everest Max, not a dedicated <c>MakaluKeyBindings</c> table as previously assumed
/// (that table is for the physical Makalu MOUSE — see
/// <see cref="BaseCampDbImporter.ReadMacroPadKeyBindings"/>'s doc comment). The
/// FunctionType/SubFunctionType/FunctionValue vocabulary is therefore the SAME one
/// Everest Max/DisplayPad use (<see cref="BaseCampDbImporter.TranslateAction"/>),
/// not the old MacroPad-specific one (<see cref="BaseCampDbImporter.TranslateMakaluAction"/>,
/// which only ever matched the physical Makalu mouse's own vocabulary).
///
/// <list type="bullet">
/// <item><b>Base Camp compatible</b> (<see cref="ExportBaseCamp"/>): only actions
/// with a confirmed native FunctionType/SubFunctionType/FunctionValue. The others
/// are omitted (key = no function).</item>
/// <item><b>K2 only</b> (<see cref="ExportK2"/>): <c>FunctionType="K2Action"</c>,
/// <c>SubFunctionType</c> = literal K2 ActionType, <c>FunctionValue</c> = literal
/// K2 ActionValue (lossless round-trip) — same convention as
/// <see cref="EvProfileExporter"/>.</item>
/// </list>
/// </summary>
public static class MpProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    private static readonly IReadOnlyDictionary<int, int> s_indexToKeyId =
        BaseCampDbImporter.KeyIdToIndex.ToDictionary(kv => kv.Value, kv => kv.Key);

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

        // Wrapper matching Base Camp's real EverestKeyBindings navigation property —
        // see class doc comment (same wrapper/item shape as EvProfileExporter's
        // Everest Max export, this table is genuinely shared by both devices).
        var bindingsEl = new XElement("EverestKeyBindings");
        root.Add(bindingsEl);

        for (int i = 0; i < 12; i++)
        {
            if (!s_indexToKeyId.TryGetValue(i, out int keyId)) continue;
            byIndex.TryGetValue(i, out var rec);

            string? functionType = null, subType = null, funcValue = null, customUrl = null;
            bool isAssigned = false;

            if (rec is not null && !string.IsNullOrEmpty(rec.ActionType))
            {
                if (bcCompatible)
                {
                    var mapped = MapActionToBc(rec.ActionType, rec.ActionValue);
                    if (mapped is not null)
                    {
                        (functionType, subType, funcValue) = mapped.Value;
                        customUrl = ExtractCustomUrl(rec.ActionType, rec.ActionValue);
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
                    functionType = "K2Action";
                    subType      = rec.ActionType;
                    funcValue    = rec.ActionValue ?? "";
                    isAssigned   = true;
                    exported++;
                }
            }

            string keyName = $"M{i + 1}";
            bindingsEl.Add(new XElement("KeyboardBinding",
                new XElement("ProfileId", 0),
                new XElement("KeyId", keyId),
                new XElement("KeyName", keyName),
                new XElement("KeyNameFull", $"SW{i + 1}({keyName})"),
                new XElement("IsKeyAssigned", isAssigned ? "true" : "false"),
                new XElement("IsTouchKey", "true"), // real BC data always sets this for MacroPad rows — not meaningful, kept for shape fidelity
                new XElement("FunctionType", functionType ?? "Default"),
                new XElement("SubFunctionType", subType ?? ""),
                new XElement("FunctionValue", funcValue ?? ""),
                new XElement("FunctionEnteredValue", ""),
                new XElement("OnPressRelease", "Press"),
                new XElement("IsSyncAcrossProfiles", "false"),
                new XElement("base64Image", ""),
                new XElement("ImageFilePath", ""),
                new XElement("IsDefaultTouchKeyImage", "true"),
                new XElement("modified_at", DateTime.Now.ToString("o")),
                new XElement("DLLKeyId", keyId),
                new XElement("DLLKeyName", keyName),
                new XElement("DLLMatrixIndex", keyId),
                new XElement("CustomURL", customUrl ?? ""),
                new XElement("OptionalText", "")));
        }

        // ---- Lighting ----
        // Missing until 2026-07-26 (key bindings only), so a K2 export dropped every LED
        // setting the import path already reads back. Profile-scoped keys with a fallback
        // to the shared ones, mirroring MacroLedPrefix's "synced = global" rule. No
        // settings block: unlike Everest Max, the MacroPad has no Game Mode/dial fields
        // K2 tracks, and the import path doesn't read one either.
        string? Get(string key)
        {
            var direct = store.GetSetting(key);
            if (direct is not null) return direct;
            string marker = $".p{slot}.";
            int at = key.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? null : store.GetSetting(key[..at] + "." + key[(at + marker.Length)..]);
        }

        root.Add(KeyboardLightingXml.BuildLightings(
            Get, $"macroled.p{slot}.",
            $"macroled.p{slot}.custom.keyColors", $"macroled.p{slot}.custom.keyEffects",
            BaseCampDbImporter.MacroPadKeyCount, includeK2Only: !bcCompatible));

        // K2-only: the whole per-profile Settings namespace, verbatim — see
        // K2ProfileSettingsXml for why a generic dump beats hand-written fields.
        if (!bcCompatible)
            root.Add(K2ProfileSettingsXml.Build(
                store.GetSettingsWithPrefix, slot, K2ProfileSettingsXml.SettingsOnlyFamilies));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);

        return new ExportResult(exported, skipped, reasons);
    }

    /// <summary>Same confirmed vocabulary as <see cref="EvProfileExporter"/>'s own copy
    /// (the native FunctionType/SubFunctionType strings are shared by Base Camp between
    /// Everest Max, MacroPad and DisplayPad via <c>BaseCampDbImporter.TranslateAction</c>).</summary>
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

            // Real Base Camp has no "Open URL" FunctionType — a specific destination is
            // always expressed as FunctionType="Run browser" with the URL in the sibling
            // CustomURL element (see ExtractCustomUrl / BaseCampDbImporter.TranslateAction's
            // matching comment), never in FunctionValue.
            case "browser":
                return ("Run browser", "Run browser", "Run browser");

            case "url":
                return string.IsNullOrEmpty(v) ? null : ("Run browser", "Run browser", "Run browser");

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
                    "lock computer" or "lock" => "Lock computer",
                    "shutdown" => "Shut down", // MacroPad's real vocabulary has a space, unlike DisplayPad/Everest
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
                    "play_pause" or "play/pause" => "Play/Pause",
                    "stop" => "Stop",
                    "prev_track" or "previous track" => "Previous track",
                    "next_track" or "next track" => "Next track",
                    "volume_up" or "volume up" => "Volume up",
                    "volume_down" or "volume down" => "Volume down",
                    "mute" => "Mute",
                    "mic_mute" => "Mic Mute",
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
                    "scroll up" => "Scroll Up",
                    "scroll down" => "Scroll Down",
                    _ => null
                };
                return sft is null ? null : (sft is "Scroll Up" or "Scroll Down" ? "Mouse Wheel" : "Mouse", sft, sft);
            }

            case "text":
                return v.Length == 1 ? ("Default", v, v) : null;

            // Base Camp's own "Disable" function — see Ev60ProfileExporter's twin arm.
            case "disable":
                return ("Disable", "Disable", "Disable");

            // dp_folder/dp_back/pyscript/command/macro/multi/pcinfo/clock/none:
            // no confirmed Base Camp MacroPad equivalent -> omitted.
            default:
                return null;
        }
    }

    /// <summary>The URL that goes in the exported binding's sibling <c>CustomURL</c> element
    /// (see <see cref="MapActionToBc"/>'s "browser"/"url" arms) — real Base Camp never puts it
    /// in FunctionValue. Null for a "browser" action with no URL set (just launch the browser).</summary>
    private static string? ExtractCustomUrl(string actionType, string? actionValue)
    {
        switch (actionType)
        {
            case "url":
                return string.IsNullOrWhiteSpace(actionValue) ? null : actionValue.Trim();
            case "browser":
                string? url = BrowserActionPayload.Parse(actionValue)?.Url;
                return string.IsNullOrEmpty(url) ? null : url;
            default:
                return null;
        }
    }
}
