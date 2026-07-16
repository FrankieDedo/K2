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
/// **XML element names (confirmed 2026-07-15 against a real Base Camp XML export
/// and the decompiled <c>Profile</c>/<c>KeyboardBinding</c> classes)**: the DB
/// TABLE name (<c>EverestKeyBidings</c>, with Base Camp's own typo) is NOT what
/// appears in exported XML — that's a distinct navigation PROPERTY on
/// <c>Profile</c>, correctly spelled <c>EverestKeyBindings</c>, rendered by
/// XmlSerializer as a wrapper element containing one child per item named after
/// the item's CLASS, <c>KeyboardBinding</c> (same convention verified for
/// DisplayPad: wrapper <c>DisplayPadKeyBindings</c> / item
/// <c>DisplayPadLayerBidings</c>, its class name). This exporter previously wrote
/// flat <c>EverestKeyBidings</c> elements (typo, no wrapper) — self-consistent
/// with K2's own importer but never actually readable by real Base Camp, and real
/// Base Camp XML was never readable by K2 either (see
/// <c>MainWindow.Everest.cs</c>'s <c>BtnEvImportXml_Click</c>).
///
/// Includes both the regular keys (KeyMatrix stored in <see cref="EverestStore"/>)
/// and the 4 numpad LCD keys (NDK), PER PROFILE (<c>EverestStore</c>'s
/// <c>ndk.{slot}.{i}.*</c> settings — confirmed via USB capture against real Base Camp
/// that each firmware profile stores its own 4 NDK pictures, see
/// MainWindow.NumpadDisplayKeys.cs's UploadNdkImage doc comment). The 4 synthetic
/// KeyIds used for the NDKs (9001-9004) are
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

        // Wrapper element matching Base Camp's real Profile.EverestKeyBindings
        // navigation property (see class doc comment) — each binding below is a
        // <KeyboardBinding> child of this wrapper, not a flat sibling of Profile.
        var bindingsEl = new XElement("EverestKeyBindings");
        root.Add(bindingsEl);

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

            bindingsEl.Add(BuildBinding(k.KeyMatrix, k.KeyMatrix, k.Label ?? $"K{k.KeyMatrix}",
                isAssigned, isTouchKey: false, functionType, subType, funcValue, imageB64: null));
        }

        // ---- Numpad LCD keys (NDK), 0-3 — per-profile ----
        for (int i = 0; i < 4; i++)
        {
            string? imagePath = store.GetSetting($"ndk.{slot}.{i}.imagePath");
            string? ndkType   = store.GetSetting($"ndk.{slot}.{i}.actionType");
            string? ndkValue  = store.GetSetting($"ndk.{slot}.{i}.actionValue");
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
                bindingsEl.Add(BuildBinding(keyId, keyId, $"NDK{i}",
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
        // Tag "KeyboardBinding" = real Base Camp per-item class name (confirmed
        // 2026-07-15 against a real Base Camp XML export + decompiled classes —
        // see this file's doc comment; NOT the DB table name "EverestKeyBidings").
        return new XElement("KeyboardBinding",
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
