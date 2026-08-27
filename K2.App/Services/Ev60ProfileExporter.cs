using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using K2.App.Models;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Exports an Everest 60 profile to XML on the REAL Base Camp schema — CORRECTED
/// 2026-07-26 against a real BC XML export: the wrapper/item element names are
/// <c>Everest60KeyBindings</c>/<c>Everest60KeyBinding</c> and
/// <c>Everest60Lightings</c>/<c>Everest60Lighting</c> (correct spelling, nested
/// items — a previous session guessed a flat, typo'd <c>Everest60KeyBidings</c>/
/// single-element <c>Everest60Lightings</c> shape that was never actually
/// Base-Camp-readable, matching the class-level fix already made for Everest Max's
/// own exporter, see EvProfileExporter). Key Binding (2026-07-14, second pass) is a
/// K2Action like every other device now, not a raw firmware remap — so the
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
        var bindingsEl = new XElement("Everest60KeyBindings");
        root.Add(bindingsEl);

        var table = Everest60RemapData.LedIndexToDllKeyIdArray;
        var numpad = Everest60RemapData.NumpadDllKeyId;
        foreach (var k in store.LoadProfile(slot))
        {
            if (string.IsNullOrEmpty(k.ActionType)) continue;

            // Main board (0-63) or accessory numpad (NumpadLedIndexBase + n) — the
            // numpad arm was missing, so numpad bindings were silently dropped on export
            // (the import side gained the matching arm the same session).
            int npIndex = k.LedIndex - Everest60Protocol.NumpadLedIndexBase;
            int dllKeyId;
            if (k.LedIndex >= 0 && k.LedIndex < table.Length) dllKeyId = table[k.LedIndex];
            else if (npIndex >= 0 && npIndex < numpad.Length) dllKeyId = numpad[npIndex];
            else continue;

            string? functionType = null, subType = null, funcValue = null, customUrl = null;
            bool isAssigned = false;

            if (bcCompatible)
            {
                var mapped = MapActionToBc(k.ActionType, k.ActionValue);
                if (mapped is not null)
                {
                    (functionType, subType, funcValue) = mapped.Value;
                    customUrl = ExtractCustomUrl(k.ActionType, k.ActionValue);
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

            bindingsEl.Add(new XElement("Everest60KeyBinding",
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
                new XElement("CustomURL", customUrl ?? "")));
        }

        // ---- Lighting ----
        var lighting = store.LoadLighting(slot);
        if (lighting is not null)
        {
            var effEnum = (Everest60Protocol.Effect)lighting.Effect;
            int effIndex = lighting.ActiveMode == "custom" ? 7 : effEnum switch
            {
                Everest60Protocol.Effect.Static    => 1,
                Everest60Protocol.Effect.Wave      => 2,
                Everest60Protocol.Effect.Tornado   => 3,
                Everest60Protocol.Effect.Breathing => 4,
                Everest60Protocol.Effect.Reactive  => 5,
                // Reachable since the import can now put Custom in Effect itself, not
                // just in ActiveMode (see BaseCampDbImporter.ReadEverest60LightingRaw).
                Everest60Protocol.Effect.Custom    => 7,
                Everest60Protocol.Effect.Yeti      => 8,
                Everest60Protocol.Effect.Off       => 9,
                _ => 1,
            };
            string effName = effIndex switch
            {
                1 => "Static", 2 => "ColorWave", 3 => "Tornado", 4 => "Breathing",
                5 => "Reactive", 6 => "Matrix", 7 => "Custom", 8 => "Yeti", 9 => "OFF", _ => "Static",
            };

            root.Add(new XElement("Everest60Lightings",
                new XElement("Everest60Lighting",
                    new XElement("ProfileId", 0),
                    new XElement("EffIndex", effName),
                    new XElement("EffectName", effName == "OFF" ? "Off" : effEnum.ToString()),
                    // Color-type pill: 0 single / 1 dual / 2 rainbow — inverse of the
                    // import side (BaseCampDbImporter.ApplyLightingToStore's doc comment).
                    new XElement("Type", lighting.Rainbow ? 2 : lighting.ColorDouble ? 1 : 0),
                    new XElement("Speed", lighting.SpeedPct),
                    new XElement("Brightness", (int)lighting.Brightness),
                    new XElement("Direction", lighting.DirIndex),
                    new XElement("Color1", Hex(lighting.Color1)),
                    new XElement("Color2", Hex(lighting.Color2)),
                    new XElement("IsActive", "true"),
                    new XElement("CustomLightings", BuildCustomJson(lighting)))));
        }

        // ---- Settings (Game Mode/Core LED) ----
        int mode = int.TryParse(store.GetSetting($"settings.p{slot}.game_mode"), out var m) ? m : 0;
        bool led = store.GetSetting($"settings.p{slot}.indicator_led") == "1";
        // Null = the user never picked one, so the value below is only K2's locale
        // guess: exported as IsLayoutConfigured=false so the importing side keeps its
        // own guess instead (same gate BaseCampDbImporter applies to BC's DB column).
        var layoutStored = EverestKeyboardLayout.ParseStorageString(
            store.GetSetting(EverestKeyboardLayout.LayoutSettingKey));
        var layout = layoutStored ?? EverestKeyboardLayout.DetectLayout();
        root.Add(new XElement("Everest60Settings",
            new XElement("Everest60Setting",
                new XElement("ProfileId", 0),
                new XElement("SysncAcrossProfile", "false"),
                new XElement("DisableShift", (mode & 0x1) != 0 ? "true" : "false"),
                new XElement("DisableAltF4", (mode & 0x2) != 0 ? "true" : "false"),
                new XElement("DisableWin", (mode & 0x4) != 0 ? "true" : "false"),
                new XElement("DisableAltTab", (mode & 0x8) != 0 ? "true" : "false"),
                new XElement("EnableCoreLED", led ? "true" : "false"),
                // See EvProfileExporter for why these two are written.
                new XElement("KeyboardLayout", EverestKeyboardLayout.ToStorageString(layout)),
                new XElement("IsLayoutConfigured", layoutStored is not null ? "true" : "false"),
                new XElement("modified_at", DateTime.Now.ToString("o")))));

        // K2-only: the whole per-profile Settings namespace, verbatim — see
        // K2ProfileSettingsXml for why a generic dump beats hand-written fields.
        if (!bcCompatible)
            root.Add(K2ProfileSettingsXml.Build(
                store.GetSettingsWithPrefix, slot, K2ProfileSettingsXml.SettingsOnlyFamilies));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);

        return new ExportResult(exported, skipped, reasons);
    }

    private static string Hex(int rgb) => $"#{rgb:X6}".ToLowerInvariant();

    /// <summary>
    /// Builds the 192-entry per-LED payload Base Camp expects: one entry per firmware
    /// LED hardware ADDRESS (<c>KeyCode</c>), not per logical key index — corrected
    /// 2026-07-26 together with the import side, see
    /// <c>BaseCampDbImporter.ParseEverest60Custom</c>. Addresses no physical LED uses
    /// keep Base Camp's own <c>#ffffff</c> filler; physical LEDs the user never painted
    /// go out black, which is what "unpainted" means on this device.
    /// </summary>
    private static string BuildCustomJson(Ev60LightingRecord lighting)
    {
        var byAddress = new string[Everest60Protocol.ColorEntryCount];
        for (int i = 0; i < byAddress.Length; i++) byAddress[i] = "#ffffff";

        void Put(int address, int rgb)
        {
            if (address >= 0 && address < byAddress.Length) byAddress[address] = Hex(rgb);
        }
        void Blank(byte[] addresses)
        {
            foreach (var a in addresses) Put(a, 0);
        }

        Blank(Everest60Protocol.LedIndex);
        Blank(Everest60Protocol.NumpadLedIndex);
        Blank(Everest60Protocol.SideLedIndex);
        Blank(Everest60Protocol.NumpadSideLedIndex);

        foreach (var kv in lighting.CustomKeyColors)
        {
            int np = kv.Key - Everest60Protocol.NumpadLedIndexBase;
            if (kv.Key >= 0 && kv.Key < Everest60Protocol.LedIndex.Length)
                Put(Everest60Protocol.LedIndex[kv.Key], kv.Value);
            else if (np >= 0 && np < Everest60Protocol.NumpadLedIndex.Length)
                Put(Everest60Protocol.NumpadLedIndex[np], kv.Value);
        }
        foreach (var kv in lighting.CustomSideColors ?? new Dictionary<int, int>())
            if (kv.Key >= 0 && kv.Key < Everest60Protocol.SideLedIndex.Length)
                Put(Everest60Protocol.SideLedIndex[kv.Key], kv.Value);
        foreach (var kv in lighting.CustomNumpadRingColors ?? new Dictionary<int, int>())
            if (kv.Key >= 0 && kv.Key < Everest60Protocol.NumpadSideLedIndex.Length)
                Put(Everest60Protocol.NumpadSideLedIndex[kv.Key], kv.Value);

        var items = byAddress.Select((hex, addr) =>
            $"{{\"Ids\":{addr + 1},\"KeyCode\":{addr},\"ColorHex\":\"{hex}\"}}");
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
            {
                var execPath = ExecActionPayload.PathOf(v);
                return string.IsNullOrEmpty(execPath) ? null : ("Run Program", execPath, execPath);
            }

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

            // Base Camp's own "Disable" function — real exports carry it with the
            // FunctionValue repeated as the label (confirmed in ev60_test.xml).
            case "disable":
                return ("Disable", "Disable", "Disable");

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
