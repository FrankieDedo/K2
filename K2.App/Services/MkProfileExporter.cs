using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports a Makalu profile to XML — mirrors <see cref="EvProfileExporter"/>'s shape
/// (one root &lt;Profile&gt; header + child elements per real Base Camp table). Fixed
/// 2026-07-29 to the REAL wrapper/item shape confirmed via the decompiled
/// BaseCamp.Data classes (a previous session's version got the class/field NAMES
/// right but not the NESTING — same "wrapper name == item name" bug already fixed for
/// Everest/Everest 60): <c>MakaluKeyBindings</c>/<c>MakaluLightings</c>/
/// <c>MakaluSettings</c> are wrappers whose children are named after the class
/// (<c>MakaluKeyBinding</c>/<c>MakaluLighting</c>/<c>MakaluSetting</c>), and DPI levels
/// nest INSIDE the settings element as <c>&lt;lstDPI&gt;&lt;DPILevel&gt;</c>, not as
/// separate root-level elements — see BaseCampDbImporter's Makalu section (import
/// side) and MainWindow.Makalu.cs's BtnMkImportXml_Click (which still accepts the old
/// flat shape too, for files this exporter wrote before the fix). Button remap uses
/// MakaluRemapData's own function-key vocabulary in K2 mode; Base Camp mode maps back
/// to BC's "Left button"/"DPI +"/... strings (the reverse of
/// BaseCampDbImporter.TranslateMakaluRemapFunction) — UNVERIFIED against a real Base
/// Camp import (Base Camp itself was never fed a K2-exported file in any session so
/// far).
/// </summary>
public static class MkProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    public static ExportResult ExportBaseCamp(MakaluStore store, int slot, string profileName, string filePath, string deviceType = "Makalu67")
        => Export(store, slot, profileName, filePath, bcCompatible: true, deviceType);

    public static ExportResult ExportK2(MakaluStore store, int slot, string profileName, string filePath, string deviceType = "Makalu67")
        => Export(store, slot, profileName, filePath, bcCompatible: false, deviceType);

    private static ExportResult Export(
        MakaluStore store, int slot, string profileName, string filePath, bool bcCompatible, string deviceType)
    {
        int exported = 0, skipped = 0;
        var reasons = new List<string>();

        var root = new XElement("Profile",
            new XElement("ProfileId", 0),
            new XElement("Id", slot),
            new XElement("DeviceType", deviceType),
            new XElement("ProfileName", profileName),
            new XElement("OrderNo", slot));

        // ---- Button remap ----
        var kbWrapper = new XElement("MakaluKeyBindings");
        foreach (var kv in store.LoadRemap(slot))
        {
            int buttonIndex = kv.Key;
            string fnKey = kv.Value;

            string functionType, functionValue, enteredValue = "";
            if (bcCompatible)
            {
                var mapped = MapFunctionToBc(fnKey);
                if (mapped is null)
                {
                    skipped++;
                    reasons.Add($"button {buttonIndex}: function \"{fnKey}\" has no Base Camp equivalent — omitted");
                    continue;
                }
                (functionType, functionValue, enteredValue) = mapped.Value;
            }
            else
            {
                functionType = "K2Remap";
                functionValue = fnKey;
            }

            kbWrapper.Add(new XElement("MakaluKeyBinding",
                new XElement("ProfileId", 0),
                new XElement("KeyId", buttonIndex),
                new XElement("KeyName", $"Button{buttonIndex}"),
                new XElement("IsKeyAssigned", "true"),
                new XElement("FunctionType", functionType),
                new XElement("FunctionValue", functionValue),
                new XElement("FunctionEnteredValue", enteredValue),
                new XElement("ONKeyPressRelease", "Press"),
                new XElement("SyncAcrossProfilesKeyBinding", "false"),
                new XElement("CustomURL", "")));
            exported++;
        }
        root.Add(kbWrapper);

        // ---- Lighting ----
        var lighting = store.LoadLighting(slot);
        if (lighting is not null)
        {
            string effectName = lighting.CustomActive ? "Custom" : ((MakaluProtocol.Effect)lighting.Effect).ToString();
            root.Add(new XElement("MakaluLightings",
                new XElement("MakaluLighting",
                    new XElement("ProfileId", 0),
                    new XElement("EffectName", effectName),
                    new XElement("ColorType", lighting.Color2 != 0 ? "DUAL" : "SINGLE"),
                    new XElement("SingleColor", Hex(lighting.Color1)),
                    new XElement("DualColor1", Hex(lighting.Color1)),
                    new XElement("DualColor2", Hex(lighting.Color2)),
                    new XElement("Speed", lighting.SpeedIndex),
                    new XElement("Brightness", (int)lighting.Brightness),
                    new XElement("Direction", lighting.DirIndex),
                    new XElement("IsEffectSelected", "true"),
                    new XElement("CustomMakaluLightings", BuildCustomJson(lighting.CustomColors)))));
        }

        // ---- Settings + DPI (DPI nests inside the settings element) ----
        var settings = store.LoadSettings(slot);
        var dpi = store.LoadDpi(slot);
        if (settings is not null)
        {
            int activeDpiLevelId = dpi is not null ? dpi.Active + 1 : 1; // 1-based DPILevelId
            var settingEl = new XElement("MakaluSetting",
                new XElement("ProfileId", 0),
                // Always literal Hz here (K2's own data) — see NormalizeMakaluPollingHz's
                // doc comment, which passes literal Hz values straight through unchanged.
                new XElement("PollingRate", settings.PollingHz),
                new XElement("Sensitivity", settings.Sensitivity),
                new XElement("ClickSpeed", settings.ClickSpeed),
                new XElement("ButtonResponseTime", settings.DebounceMs),
                new XElement("AngleSnapping", settings.AngleSnapping ? "On" : "Off"),
                new XElement("LiftOffDistance", settings.LiftOffCustom ? "Custom" : settings.LiftOffHigh ? "High" : "Low"),
                new XElement("SelectedDPILevelId", activeDpiLevelId));

            if (dpi is not null)
            {
                var lstDpi = new XElement("lstDPI");
                for (int i = 0; i < dpi.Levels.Length; i++)
                {
                    lstDpi.Add(new XElement("DPILevel",
                        new XElement("DPILevelId", i + 1),
                        new XElement("ProfileId", 0),
                        new XElement("LevelName", $"Level {i + 1}"),
                        new XElement("DPI", dpi.Levels[i])));
                }
                settingEl.Add(lstDpi);
            }

            root.Add(new XElement("MakaluSettings", settingEl));
        }

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

    private static string BuildCustomJson(int[] customColors)
    {
        var items = new List<string>();
        for (int i = 0; i < customColors.Length; i++)
            items.Add($"{{\"Ids\":{i + 1},\"KeyCode\":{i},\"ColorHex\":\"{Hex(customColors[i])}\"}}");
        return "[" + string.Join(",", items) + "]";
    }

    /// <summary>Reverse of BaseCampDbImporter.TranslateMakaluRemapFunction.</summary>
    private static (string FunctionType, string FunctionValue, string EnteredValue)? MapFunctionToBc(string fnKey)
    {
        if (fnKey.StartsWith("sniper:"))
        {
            string dpi = fnKey.Length > 7 ? fnKey[7..] : "800";
            return ("Mouse", "DPI Sniper", dpi);
        }
        return fnKey switch
        {
            "left"       => ("Mouse", "Left button", ""),
            "right"      => ("Mouse", "Right button", ""),
            "middle"     => ("Mouse", "Middle button", ""),
            "back"       => ("Mouse", "Backward", ""),
            "forward"    => ("Mouse", "Forward", ""),
            "dpi+"       => ("Mouse", "DPI +", ""),
            "dpi-"       => ("Mouse", "DPI -", ""),
            // Casing CONFIRMED 2026-07-29 against a real BaseCamp.db row — lowercase
            // "profile", not "Profile" (see TranslateMakaluRemapFunction's doc comment).
            "profile_next"     => ("Mouse", "Next profile", ""),
            "profile_prev"     => ("Mouse", "Previous profile", ""),
            "brightness_cycle" => ("Mouse", "Brightness cycle", ""),
            "effect_cycle"     => ("Mouse", "Effect cycle", ""),
            "scroll_up"  => ("Mouse Wheel", "Scroll Up", ""),
            "scroll_down"=> ("Mouse Wheel", "Scroll Down", ""),
            "disabled"   => ("Disable", "", ""),
            _ => null,
        };
    }
}
