using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace K2.DisplayPad.Services;

/// <summary>
/// Imports an XML profile exported from Mountain Base Camp into a profile
/// slot (1..5) of a device managed by K2.
/// - decodes base64 images and saves them to disk,
/// - translates <c>FunctionType</c>/<c>FunctionValue</c> into K2 actions,
/// - uses <c>DLLMatrixIndex</c> to map to the right button,
/// - persists the result into <c>StateStore</c> and (optionally) uploads
///   the images to the device via <c>DisplayPadService</c>.
/// </summary>
public sealed class BaseCampProfileImporter
{
    private readonly MacroLibrary _macros;

    /// <summary>Fixed <c>DLLMatrixIndex</c> -> K2 button index (0..11) map,
    /// derived empirically on real devices and confirmed by the XML files.</summary>
    public static readonly IReadOnlyDictionary<int, int> MatrixToCell = new Dictionary<int, int>
    {
        {  8, 0 }, { 17, 1 }, { 26, 2 }, { 35, 3 },
        { 44, 4 }, { 53, 5 }, { 62, 6 }, { 71, 7 },
        { 80, 8 }, { 89, 9 }, { 98,10 }, {125,11 },
    };

    private readonly string _imageDir;

    public BaseCampProfileImporter(string? imageDir = null, MacroLibrary? macros = null)
    {
        _imageDir = imageDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.DisplayPad", "images");
        Directory.CreateDirectory(_imageDir);
        _macros = macros ?? MacroLibrary.Load();
    }

    /// <summary>Imports the profile, saves the records to the store, uploads the
    /// images to the device if <paramref name="service"/> is provided.
    /// Images are <b>saved to the firmware</b> via SetIconPic
    /// (so they become the profile's persistent icon, not just a live overlay).
    /// <paramref name="rotation"/> indicates the device's mounting: icons
    /// are pre-rotated before upload, while the store keeps the path
    /// of the ORIGINAL, unrotated image.</summary>
    public ImportResult Import(string xmlPath, int deviceId, int profileSlot,
                               DisplayPadService? service, StateStore store,
                               DisplayRotation rotation = DisplayRotation.None)
    {
        var doc = XDocument.Load(xmlPath);
        var profile = doc.Root
            ?? throw new InvalidDataException("XML has no root element.");
        var name = profile.Element("ProfileName")?.Value ?? "";
        var bindings = profile.Descendants("DisplayPadLayerBidings").ToList();

        var imported = new List<int>();
        var skipped  = new List<string>();
        var errors   = new List<string>();

        foreach (var b in bindings)
        {
            if (!int.TryParse(b.Element("DLLMatrixIndex")?.Value, out int matrix))
            {
                skipped.Add("binding without DLLMatrixIndex");
                continue;
            }
            if (!MatrixToCell.TryGetValue(matrix, out int cellIdx))
            {
                skipped.Add($"unknown matrix {matrix}");
                continue;
            }

            // 1) image
            string? imagePath = ResolveImage(
                b.Element("base64Image")?.Value,
                b.Element("ImageFilePath")?.Value);

            // 2) action
            var (actionType, actionValue, reason) = MapActionExt(
                b.Element("FunctionType")?.Value,
                b.Element("SubFunctionType")?.Value,
                b.Element("FunctionValue")?.Value,
                b.Element("FunctionEnteredValue")?.Value);

            // 3) PERSISTENT upload into the FW profile (SetIconPic). If it fails,
            // fall back to the live upload (SetIconPacket) so at least the user
            // sees the icon right away.
            if (service is not null && imagePath is not null && File.Exists(imagePath))
            {
                try
                {
                    string uploadPath = IconRotator.ResolveForUpload(imagePath, rotation);
                    bool ok = service.UploadImageToProfile(deviceId, uploadPath, cellIdx, profileSlot);
                    if (!ok)
                    {
                        errors.Add($"cell #{cellIdx}: persistent upload returned false, fallback live");
                        service.UploadImage(deviceId, uploadPath, cellIdx);
                    }
                }
                catch (Exception ex)
                { errors.Add($"cell #{cellIdx}: upload fail: {ex.Message}"); }
            }

            // 4) persist
            store.SaveButton(new ButtonRecord(deviceId, profileSlot, cellIdx,
                                              imagePath, actionType, actionValue));
            imported.Add(cellIdx);

            if (reason is not null)
                skipped.Add($"cell #{cellIdx}: unmapped action ({reason})");
        }

        return new ImportResult(name, profileSlot, imported, skipped, errors);
    }

    // ---------- image ----------

    private string? ResolveImage(string? base64, string? filePath)
    {
        // 1) valid base64 (data:image/...;base64,xxxxx) -> decode and save
        if (!string.IsNullOrWhiteSpace(base64))
        {
            var trimmed = base64.Trim();
            if (trimmed.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var commaIdx = trimmed.IndexOf(',');
                if (commaIdx > 0)
                {
                    try
                    {
                        var payload = trimmed[(commaIdx + 1)..];
                        var bytes   = Convert.FromBase64String(payload);
                        var hash    = ShortHash(bytes);
                        var fp      = Path.Combine(_imageDir, $"{hash}.png");
                        if (!File.Exists(fp)) File.WriteAllBytes(fp, bytes);
                        return fp;
                    }
                    catch
                    {
                        // ignore: the fallback will be attempted
                    }
                }
            }
            else if (File.Exists(trimmed))
            {
                return trimmed;
            }
        }
        // 2) fallback to the classic path
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            return filePath;
        return null;
    }

    private static string ShortHash(byte[] bytes)
    {
        using var sha = SHA1.Create();
        var h = sha.ComputeHash(bytes);
        return Convert.ToHexString(h, 0, 8).ToLowerInvariant();
    }

    // ---------- action mapping ----------

    /// <summary>Extended version that handles all FunctionType values known
    /// in real profiles (including named macro lookup via MacroLibrary).</summary>
    public (string? Type, string? Value, string? UnmappedReason) MapActionExt(
        string? functionType, string? subFunctionType,
        string? functionValue, string? functionEnteredValue)
    {
        var ft  = (functionType ?? "").Trim();
        var sft = (subFunctionType ?? "").Trim();
        var fv  = (functionValue ?? "").Trim();
        var fev = (functionEnteredValue ?? "").Trim();

        switch (ft)
        {
            case "Run Program":   return string.IsNullOrEmpty(fv) ? (null,null,"Run Program without FunctionValue") : ("exec",  fv, null);
            case "Open Folder":   return string.IsNullOrEmpty(fv) ? (null,null,"Open Folder without FunctionValue") : ("folder",fv, null);
            case "Run browser":   return ("browser", "", null);
            case "Profile":       return ("profile", string.IsNullOrEmpty(fv) ? sft : fv, null);
            case "Adobe":
            case "DaVinci":
            case "Zoom":
            case "Keyboard Shortcuts":
                return string.IsNullOrEmpty(fv) ? (null,null,ft+" without FunctionValue") : ("keys", fv, null);

            case "OS Commands":   return ("oscmd",  string.IsNullOrEmpty(sft) ? fv : sft, null);
            case "Media":         return ("media",  string.IsNullOrEmpty(sft) ? fv : sft, null);
            case "Mouse":         return ("mouse",  string.IsNullOrEmpty(sft) ? fv : sft, null);
            case "Multi Action":  return ("multi",  fv, null); // payload = JSON array
            case "Create Folder": return ("createfolder", fv, null);
            case "Back":          return ("back",   "", null);

            // Dynamic displays on the button: for now we save the record with type
            // pcinfo/clock and the SubFunctionType:color payload, but on press
            // they don't execute anything (live rendering will be implemented later).
            case "PC Info":       return ("pcinfo", string.IsNullOrEmpty(fv) ? sft : $"{sft}:{fv}", null);
            case "Clock":         return ("clock",  string.IsNullOrEmpty(fv) ? sft : $"{sft}:{fv}", null);

            case "Default":
                // 1) literal character as SubFunctionType
                if (sft.Length == 1) return ("text", sft, null);
                if (!string.IsNullOrEmpty(fv) && fv.Length == 1) return ("text", fv, null);
                // 2) named macro resolved via MacroLibrary
                if (!string.IsNullOrEmpty(sft) && _macros.TryGet(sft, out var def))
                {
                    if (def.Type == "text" && !string.IsNullOrEmpty(def.Value))
                        return ("text", def.Value, null);
                    if (def.Type == "keys" && !string.IsNullOrEmpty(def.Value))
                        return ("keys", def.Value, null);
                    // raw or unknown: leave as a placeholder
                    return ("none", $"[macro raw] {sft}", $"macro \"{sft}\" not reducible (raw)");
                }
                if (!string.IsNullOrEmpty(sft))
                    return ("none", $"[macro] {sft}", $"macro \"{sft}\" not in library");
                return (null, null, "Default with no information");

            case "":
                return (null, null, null);

            default:
                return (null, null, $"FunctionType \"{ft}\" not handled");
        }
    }

    /// <summary>Legacy version without macro lookup (for compatibility).</summary>
    public static (string? Type, string? Value, string? UnmappedReason) MapAction(
        string? functionType, string? subFunctionType,
        string? functionValue, string? functionEnteredValue)
    {
        var ft  = (functionType ?? "").Trim();
        var sft = (subFunctionType ?? "").Trim();
        var fv  = (functionValue ?? "").Trim();
        var fev = (functionEnteredValue ?? "").Trim();

        switch (ft)
        {
            case "Run Program":
                return string.IsNullOrEmpty(fv)
                    ? (null, null, "Run Program without FunctionValue")
                    : ("exec", fv, null);

            case "Open Folder":
                return string.IsNullOrEmpty(fv)
                    ? (null, null, "Open Folder without FunctionValue")
                    : ("folder", fv, null);

            case "Run browser":
                return ("browser", "", null);

            case "Profile":
                // E.g. "Next Profile", "Previous Profile", or a profile name
                return ("profile", string.IsNullOrEmpty(fv) ? sft : fv, null);

            case "Adobe":
            case "Keyboard Shortcuts":
                // FunctionValue like "Ctrl + Shift + A" — the runtime passes it
                // to SendKeysTranslator. We leave it as-is.
                return string.IsNullOrEmpty(fv)
                    ? (null, null, ft + " without FunctionValue")
                    : ("keys", fv, null);

            case "Default":
                // Literal character (e.g. "À", "É") or a named macro.
                if (sft.Length == 1)
                    return ("text", sft, null);
                if (!string.IsNullOrEmpty(fv) && fv.Length == 1)
                    return ("text", fv, null);
                // Named macro: we don't know how to translate it. We just leave
                // a placeholder that the user can fill in by hand
                // in the dialog editor.
                if (!string.IsNullOrEmpty(sft))
                    return ("none", $"[macro] {sft}", $"macro \"{sft}\" not resolved");
                return (null, null, "Default with no information");

            case "":
                return (null, null, null);

            default:
                return (null, null, $"FunctionType \"{ft}\" not handled");
        }
    }
}

public sealed record ImportResult(
    string                  ProfileName,
    int                     ProfileSlot,
    IReadOnlyList<int>      ImportedCells,
    IReadOnlyList<string>   Skipped,
    IReadOnlyList<string>   Errors);
