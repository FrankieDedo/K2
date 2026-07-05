using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports a DisplayPad profile to two XML formats, both based on the same real
/// Base Camp schema (<c>DisplayPadLayerBidings</c>/<c>KeyId</c>/<c>ParentId</c>...,
/// verified against the original profiles in <c>Profili_BaseCamp/</c>):
///
/// <list type="bullet">
/// <item><b>Base Camp compatible</b> (<see cref="ExportBaseCamp"/>): uses only the
/// actually confirmed native <c>FunctionType</c>s (Run Program, Open Folder,
/// Run browser, Profile, Keyboard Shortcuts, OS Commands, Media, Mouse,
/// Create Folder, Back). K2-only actions (pyscript, command, url with a
/// custom target, macro, multi-character text, ...) are NOT exported: the key
/// stays without a function (but the icon, if present, is still included).</item>
/// <item><b>K2 only</b> (<see cref="ExportK2"/>): same XML schema (so it stays
/// readable by a human/other tool and reuses the existing BC-XML parser),
/// but <c>FunctionType="K2Action"</c> is a sentinel that carries the literal
/// K2 ActionType/ActionValue losslessly in SubFunctionType/FunctionValue — see
/// the dedicated branch in MainWindow.DisplayPad.cs BtnDpImportXml_Click.</item>
/// </list>
/// </summary>
public static class DpProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    // Button index (0-11) -> (KeyId, KeyName, KeyNameFull, DLLMatrixIndex)
    // Values verified against the original Base Camp profiles (Profili_BaseCamp/*.xml).
    private static readonly (int KeyId, string KeyName, string KeyNameFull, int DllMatrix)[] KeyMeta =
    {
        (170, "M1",  "SW1(M1)",   8),
        (171, "M2",  "SW2(M2)",  17),
        (172, "M3",  "SW3(M3)",  26),
        (173, "M4",  "SW4(M4)",  35),
        (174, "M5",  "SW5(M5)",  44),
        (175, "M6",  "SW6(M6)",  53),
        (176, "M7",  "SW7(M7)",  62),
        (177, "M8",  "SW8(M8)",  71),
        (178, "M9",  "SW9(M9)",  80),
        (179, "M10", "SW10(M10)",89),
        (220, "M11", "SW11(M11)",98),
        (221, "M12", "SW12(M12)",125),
    };

    /// <summary>Exports in Base Camp compatible format: only actions with a confirmed
    /// native FunctionType. K2-only actions are omitted (key = no function).</summary>
    public static ExportResult ExportBaseCamp(
        DisplayPadStore store, int deviceId, int slot, string profileName, string filePath)
        => Export(store, deviceId, slot, profileName, filePath, bcCompatible: true);

    /// <summary>Exports in K2 format (lossless): same XML schema, but actions
    /// are carried over 1:1 via the FunctionType="K2Action" sentinel.</summary>
    public static ExportResult ExportK2(
        DisplayPadStore store, int deviceId, int slot, string profileName, string filePath)
        => Export(store, deviceId, slot, profileName, filePath, bcCompatible: false);

    private static ExportResult Export(
        DisplayPadStore store, int deviceId, int slot, string profileName, string filePath, bool bcCompatible)
    {
        var all = store.LoadAllButtons(deviceId, slot);
        var byPage = all.GroupBy(b => b.PageId).ToDictionary(g => g.Key, g => g.ToList());
        if (!byPage.ContainsKey(0)) byPage[0] = new List<DpButtonRecord>();

        var pageIds = byPage.Keys.OrderBy(p => p).ToList();

        int exported = 0, skippedActions = 0;
        var skipReasons = new List<string>();

        var root = new XElement("Profile",
            new XElement("ProfileId", 0),
            new XElement("Id", slot),
            new XElement("DeviceType", "DisplayPad"),
            new XElement("ProfileName", profileName),
            new XElement("OrderNo", slot));

        foreach (var pageId in pageIds)
        {
            var byIndex = byPage[pageId].ToDictionary(b => b.ButtonIndex);

            for (int i = 0; i < 12; i++)
            {
                byIndex.TryGetValue(i, out var rec);
                var meta = KeyMeta[i];

                string? functionType = null, subType = null, funcValue = null, optionalText = null;
                bool isAssigned = false;

                if (rec is not null && !string.IsNullOrEmpty(rec.ActionType))
                {
                    if (bcCompatible)
                    {
                        var mapped = MapActionToBc(rec.ActionType, rec.ActionValue, store);
                        if (mapped is not null)
                        {
                            (functionType, subType, funcValue, optionalText) = mapped.Value;
                            isAssigned = true;
                            exported++;
                        }
                        else
                        {
                            skippedActions++;
                            skipReasons.Add($"page {pageId} key #{i}: action \"{rec.ActionType}\" doesn't exist in Base Camp — omitted");
                        }
                    }
                    else
                    {
                        // K2-only: literal passthrough via the sentinel FunctionType.
                        functionType = "K2Action";
                        subType      = rec.ActionType;
                        funcValue    = rec.ActionValue ?? "";
                        isAssigned   = true;
                        exported++;

                        // "dp_folder" sub-pages still carry the OptionalText with
                        // the page Id, so the K2->K2 round-trip reconstructs the folder
                        // even by just re-reading the same BC "Create Folder"-like branch.
                        if (rec.ActionType == "dp_folder" && int.TryParse(rec.ActionValue, out var fid))
                            optionalText = BuildFolderOptionalText(fid, store.GetFolderName(fid) ?? "");
                    }
                }

                string? imageB64 = null;
                if (rec?.ImagePath is not null && File.Exists(rec.ImagePath))
                {
                    try { imageB64 = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(rec.ImagePath)); }
                    catch { /* unreadable image: export without icon */ }
                }

                root.Add(new XElement("DisplayPadLayerBidings",
                    new XElement("ProfileId", 0),
                    new XElement("ParentId", pageId),
                    new XElement("KeyId", meta.KeyId),
                    new XElement("KeyName", meta.KeyName),
                    new XElement("KeyNameFull", meta.KeyNameFull),
                    new XElement("IsKeyAssigned", isAssigned ? "true" : "false"),
                    new XElement("IsTouchKey", "true"),
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
                    new XElement("DLLKeyId", meta.KeyId),
                    new XElement("DLLKeyName", meta.KeyName),
                    new XElement("DLLMatrixIndex", meta.DllMatrix),
                    new XElement("IsActive", "true"),
                    new XElement("OptionalText", optionalText ?? ""),
                    new XElement("SecondBase64Image", ""),
                    new XElement("SecondImageFilePath", ""),
                    new XElement("SecondOptionalText", ""),
                    new XElement("IsSecondDefaultTouchKeyImage", "true"),
                    new XElement("IsHardWarePress", "false"),
                    new XElement("IsFirstImageSelected", "true"),
                    new XElement("IsFirstImageDeleted", "false"),
                    new XElement("IsSecondImageDeleted", "false")));
            }
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);

        return new ExportResult(exported, skippedActions, skipReasons);
    }

    private static string BuildFolderOptionalText(int folderPageId, string folderName)
    {
        // Same set of fields seen in real Base Camp profiles (Id is the only one
        // strictly necessary for the round-trip, the others are reasonable
        // default values for label formatting).
        var obj = new
        {
            Id = folderPageId,
            TextAlign = "Center",
            TextTitle = folderName,
            TextFontFamily = "Arial",
            TextFontSize = "12",
            TextBold = true,
            TextItalic = false,
            TextUnderline = false,
            TextColor = "#ffffff",
            OriginalImagePath = "",
            IsDpFirstImage = true
        };
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>
    /// Translates a K2 action (ActionType/ActionValue) into Base Camp's native
    /// FunctionType/SubFunctionType/FunctionValue/OptionalText. Returns <c>null</c>
    /// if the action has no confirmed real equivalent in Base Camp (in that case
    /// the caller exports the key as unassigned, still keeping the icon).
    ///
    /// The vocabulary (exact strings "Run task manager", "Volume up", "Left button", ...)
    /// was verified against the original Base Camp profiles in Profili_BaseCamp/test/*.xml,
    /// not inferred — see _PROJECT_MAP.md.
    /// </summary>
    private static (string FunctionType, string SubFunctionType, string FunctionValue, string? OptionalText)? MapActionToBc(
        string actionType, string? actionValue, DisplayPadStore store)
    {
        var v = (actionValue ?? "").Trim();

        switch (actionType)
        {
            case "exec":
                return string.IsNullOrEmpty(v) ? null : ("Run Program", v, v, null);

            case "folder":
                return string.IsNullOrEmpty(v) ? null : ("Open Folder", v, v, null);

            case "browser":
                return ("Run browser", "Run browser", "Run browser", null);

            case "keys":
                return string.IsNullOrEmpty(v) ? null : ("Keyboard Shortcuts", v, v, null);

            case "profile":
            {
                string? sft = v.ToLowerInvariant() switch
                {
                    "next" or "next profile" => "Next Profile",
                    "previous" or "previous profile" or "prev" => "Previous Profile",
                    _ => int.TryParse(v, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null
                };
                return sft is null ? null : ("Profile", sft, sft, null);
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
                return sft is null ? null : ("OS Commands", sft, sft, null);
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
                return sft is null ? null : ("Media", sft, sft, null);
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
                return sft is null ? null : ("Mouse", sft, sft, null);
            }

            case "text":
                // Base Camp (FunctionType "Default") handles a single literal
                // character per key: multi-character text has no equivalent.
                return v.Length == 1 ? ("Default", v, v, null) : null;

            case "dp_folder":
            {
                if (!int.TryParse(v, out var folderPageId) || folderPageId <= 0) return null;
                string name = store.GetFolderName(folderPageId) ?? "";
                return ("Create Folder", name, name, BuildFolderOptionalText(folderPageId, name));
            }

            case "dp_back":
                return ("Back", "", "", null);

            // pyscript, command, url, macro, multi, createfolder, back (generic),
            // pcinfo, clock, none: no confirmed Base Camp equivalent -> omitted.
            default:
                return null;
        }
    }
}
