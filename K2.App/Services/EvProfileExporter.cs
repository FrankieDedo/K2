using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports an Everest profile to XML, on the same real schema confirmed for the
/// DisplayPad (Base Camp table <c>EverestKeyBidings</c>, decompiled class
/// <c>KeyboardBinding</c> — structure identical to <c>DisplayPadLayerBidings</c> but
/// WITHOUT <c>ParentId</c>/pages, since the Everest has no folders). The
/// FunctionType/SubFunctionType/FunctionValue vocabulary is the same one already
/// confirmed for the DisplayPad (<see cref="DpProfileExporter"/>), shared in the
/// real Base Camp code via <c>BaseCampDbImporter.TranslateAction</c>.
///
/// Includes both the regular keys (KeyMatrix stored in <see cref="EverestStore"/>)
/// and the 4 numpad LCD keys (NDK). Note: in K2, NDK images/actions are
/// GLOBAL device settings (not per-profile — see
/// <c>EverestStore</c>/<c>ndk.{i}.*</c>), so every exported profile will show the
/// same NDK content: this is a simplification of K2's current data model, not
/// a Base Camp limitation. The 4 synthetic KeyIds used for the NDKs (9001-9004) are
/// a K2 invention (no real DLLMatrixIndex known) — that's why the NDKs are
/// exported ONLY in K2 mode (never in Base Camp compatible mode).
/// </summary>
public static class EvProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    // Synthetic KeyIds for the 4 numpad LCD keys (no real DLLMatrixIndex known).
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

        // ---- Regular keys ----
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
                    reasons.Add($"key matrix {k.KeyMatrix}: action \"{k.ActionType}\" doesn't exist in Base Camp — omitted");
                    continue; // nothing else (image, etc.) to preserve for a regular key with no icon
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

        // ---- Numpad LCD keys (NDK), 0-3 — device-global in K2 ----
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
                catch { /* unreadable image: export without icon */ }
            }

            int keyId = NdkKeyIdBase + i;
            string? functionType = null, subType = null, funcValue = null;
            bool isAssigned = false;

            if (hasAction)
            {
                if (bcCompatible)
                {
                    // No real KeyId/DLLMatrixIndex known for the NDKs: we can't
                    // guarantee that Base Camp will accept this key -> always omit it
                    // in compatible mode, still keeping the icon if present.
                    skipped++;
                    reasons.Add($"ndk #{i}: numpad LCD key not exportable in Base Camp mode (no real KeyId known)");
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
        // Tag "EverestKeyBidings" = real Base Camp table name (same assumption
        // already made for the DisplayPad — NEVER verified against a real
        // export of an Everest profile, see _PROJECT_MAP.md).
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

    /// <summary>Same confirmed vocabulary as <see cref="DpProfileExporter"/> (the
    /// native FunctionType/SubFunctionType strings are shared by Base Camp between
    /// Everest and DisplayPad via <c>BaseCampDbImporter.TranslateAction</c>).</summary>
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

            // dp_folder/dp_back: DisplayPad-only concepts, not applicable
            // to the Everest. pyscript/command/url/macro/multi/pcinfo/clock/none:
            // no confirmed Base Camp equivalent -> omitted.
            default:
                return null;
        }
    }
}
