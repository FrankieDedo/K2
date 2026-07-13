using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Exports a Makalu profile to XML — mirrors <see cref="EvProfileExporter"/>'s shape
/// (one root &lt;Profile&gt; header + child elements per real Base Camp table), on the
/// schema confirmed via the decompiled BaseCamp.Data classes (MakaluKeyBinding/
/// MakaluLighting/MakaluSetting/DPILevel) — see BaseCampDbImporter's Makalu section
/// for the same schema used on the import side. Button remap uses MakaluRemapData's
/// own function-key vocabulary in K2 mode; Base Camp mode maps back to BC's
/// "Left button"/"DPI +"/... strings (the reverse of
/// BaseCampDbImporter.TranslateMakaluRemapFunction) — UNVERIFIED against a real
/// Base Camp import, no physical Makalu ever paired with this dev's Base Camp
/// install (see _PROJECT_MAP.md).
/// </summary>
public static class MkProfileExporter
{
    public sealed record ExportResult(int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons);

    public static ExportResult ExportBaseCamp(MakaluStore store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: true);

    public static ExportResult ExportK2(MakaluStore store, int slot, string profileName, string filePath)
        => Export(store, slot, profileName, filePath, bcCompatible: false);

    private static ExportResult Export(
        MakaluStore store, int slot, string profileName, string filePath, bool bcCompatible)
    {
        int exported = 0, skipped = 0;
        var reasons = new List<string>();

        var root = new XElement("Profile",
            new XElement("ProfileId", 0),
            new XElement("Id", slot),
            new XElement("DeviceType", "Makalu"),
            new XElement("ProfileName", profileName),
            new XElement("OrderNo", slot));

        // ---- Button remap ----
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

            root.Add(new XElement("MakaluKeyBindings",
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

        // ---- Lighting ----
        var lighting = store.LoadLighting(slot);
        if (lighting is not null)
        {
            string effectName = lighting.CustomActive ? "Custom" : ((MakaluProtocol.Effect)lighting.Effect).ToString();
            root.Add(new XElement("MakaluLightings",
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
                new XElement("CustomMakaluLightings", BuildCustomJson(lighting.CustomColors))));
        }

        // ---- Settings + DPI ----
        var settings = store.LoadSettings(slot);
        if (settings is not null)
        {
            root.Add(new XElement("MakaluSettings",
                new XElement("ProfileId", 0),
                new XElement("PollingRate", settings.PollingHz),
                new XElement("ButtonResponseTime", settings.DebounceMs),
                new XElement("AngleSnapping", settings.AngleSnapping ? "On" : "Off"),
                new XElement("LiftOffDistance", settings.LiftOffHigh ? "High" : "Low")));
        }

        var dpi = store.LoadDpi(slot);
        if (dpi is not null)
        {
            for (int i = 0; i < dpi.Levels.Length; i++)
            {
                root.Add(new XElement("DPILevels",
                    new XElement("ProfileId", 0),
                    new XElement("DPILevelId", i + 1),
                    new XElement("LevelName", $"Level {i + 1}"),
                    new XElement("DPI", dpi.Levels[i]),
                    new XElement("IsSelected", i == dpi.Active ? "true" : "false")));
            }
        }

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
            "scroll_up"  => ("Mouse Wheel", "Scroll Up", ""),
            "scroll_down"=> ("Mouse Wheel", "Scroll Down", ""),
            "disabled"   => ("Disable", "", ""),
            _ => null,
        };
    }
}
