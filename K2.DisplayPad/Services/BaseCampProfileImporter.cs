using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace K2.DisplayPad.Services;

/// <summary>
/// Importa un profilo XML esportato da Mountain Base Camp in uno slot
/// profilo (1..5) di un device gestito da K2.
/// - decodifica le immagini base64 e le salva su disco,
/// - traduce <c>FunctionType</c>/<c>FunctionValue</c> nelle azioni K2,
/// - usa <c>DLLMatrixIndex</c> per mappare al tasto giusto,
/// - persiste il risultato nello <c>StateStore</c> e (opzionalmente) carica
///   le immagini sul device tramite <c>DisplayPadService</c>.
/// </summary>
public sealed class BaseCampProfileImporter
{
    private readonly MacroLibrary _macros;

    /// <summary>Mappa fissa <c>DLLMatrixIndex</c> -> indice tasto K2 (0..11),
    /// derivata empiricamente sui device reali e confermata dagli XML.</summary>
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

    /// <summary>Importa il profilo, salva i record nello store, carica le
    /// immagini sul device se <paramref name="service"/> e' fornito.
    /// Le immagini vengono <b>salvate nel firmware</b> tramite SetIconPic
    /// (cosi' diventano l'icona persistente del profilo, non solo un overlay live).
    /// <paramref name="rotation"/> indica il montaggio del device: le icone
    /// vengono pre-ruotate prima dell'upload, mentre nello store resta il path
    /// dell'immagine ORIGINALE non ruotata.</summary>
    public ImportResult Import(string xmlPath, int deviceId, int profileSlot,
                               DisplayPadService? service, StateStore store,
                               DisplayRotation rotation = DisplayRotation.None)
    {
        var doc = XDocument.Load(xmlPath);
        var profile = doc.Root
            ?? throw new InvalidDataException("XML senza elemento radice.");
        var name = profile.Element("ProfileName")?.Value ?? "";
        var bindings = profile.Descendants("DisplayPadLayerBidings").ToList();

        var imported = new List<int>();
        var skipped  = new List<string>();
        var errors   = new List<string>();

        foreach (var b in bindings)
        {
            if (!int.TryParse(b.Element("DLLMatrixIndex")?.Value, out int matrix))
            {
                skipped.Add("binding senza DLLMatrixIndex");
                continue;
            }
            if (!MatrixToCell.TryGetValue(matrix, out int cellIdx))
            {
                skipped.Add($"matrix {matrix} non noto");
                continue;
            }

            // 1) immagine
            string? imagePath = ResolveImage(
                b.Element("base64Image")?.Value,
                b.Element("ImageFilePath")?.Value);

            // 2) azione
            var (actionType, actionValue, reason) = MapActionExt(
                b.Element("FunctionType")?.Value,
                b.Element("SubFunctionType")?.Value,
                b.Element("FunctionValue")?.Value,
                b.Element("FunctionEnteredValue")?.Value);

            // 3) upload PERSISTENTE nel profilo FW (SetIconPic). Se fallisce,
            // fallback all'upload live (SetIconPacket) cosi' almeno l'utente
            // vede l'icona subito.
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
                skipped.Add($"cell #{cellIdx}: azione non mappata ({reason})");
        }

        return new ImportResult(name, profileSlot, imported, skipped, errors);
    }

    // ---------- image ----------

    private string? ResolveImage(string? base64, string? filePath)
    {
        // 1) base64 valido (data:image/...;base64,xxxxx) -> decodifica e salva
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
                        // ignore: si tentera' il fallback
                    }
                }
            }
            else if (File.Exists(trimmed))
            {
                return trimmed;
            }
        }
        // 2) fallback al path classico
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

    /// <summary>Versione estesa che gestisce tutti i FunctionType conosciuti
    /// nei profili reali (incluso lookup macro nominate via MacroLibrary).</summary>
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
            case "Run Program":   return string.IsNullOrEmpty(fv) ? (null,null,"Run Program senza FunctionValue") : ("exec",  fv, null);
            case "Open Folder":   return string.IsNullOrEmpty(fv) ? (null,null,"Open Folder senza FunctionValue") : ("folder",fv, null);
            case "Run browser":   return ("browser", "", null);
            case "Profile":       return ("profile", string.IsNullOrEmpty(fv) ? sft : fv, null);
            case "Adobe":
            case "DaVinci":
            case "Zoom":
            case "Keyboard Shortcuts":
                return string.IsNullOrEmpty(fv) ? (null,null,ft+" senza FunctionValue") : ("keys", fv, null);

            case "OS Commands":   return ("oscmd",  string.IsNullOrEmpty(sft) ? fv : sft, null);
            case "Media":         return ("media",  string.IsNullOrEmpty(sft) ? fv : sft, null);
            case "Mouse":         return ("mouse",  string.IsNullOrEmpty(sft) ? fv : sft, null);
            case "Multi Action":  return ("multi",  fv, null); // payload = JSON array
            case "Create Folder": return ("createfolder", fv, null);
            case "Back":          return ("back",   "", null);

            // Display dinamici sul tasto: per ora salviamo il record con type
            // pcinfo/clock e il payload SubFunctionType:colore, ma alla pressione
            // non eseguono nulla (verra' implementato il rendering live).
            case "PC Info":       return ("pcinfo", string.IsNullOrEmpty(fv) ? sft : $"{sft}:{fv}", null);
            case "Clock":         return ("clock",  string.IsNullOrEmpty(fv) ? sft : $"{sft}:{fv}", null);

            case "Default":
                // 1) carattere letterale come SubFunctionType
                if (sft.Length == 1) return ("text", sft, null);
                if (!string.IsNullOrEmpty(fv) && fv.Length == 1) return ("text", fv, null);
                // 2) macro nominata risolta via MacroLibrary
                if (!string.IsNullOrEmpty(sft) && _macros.TryGet(sft, out var def))
                {
                    if (def.Type == "text" && !string.IsNullOrEmpty(def.Value))
                        return ("text", def.Value, null);
                    if (def.Type == "keys" && !string.IsNullOrEmpty(def.Value))
                        return ("keys", def.Value, null);
                    // raw o sconosciuto: lasciamo come placeholder
                    return ("none", $"[macro raw] {sft}", $"macro \"{sft}\" non riducibile (raw)");
                }
                if (!string.IsNullOrEmpty(sft))
                    return ("none", $"[macro] {sft}", $"macro \"{sft}\" non in libreria");
                return (null, null, "Default senza informazioni");

            case "":
                return (null, null, null);

            default:
                return (null, null, $"FunctionType \"{ft}\" non gestito");
        }
    }

    /// <summary>Versione legacy senza macro lookup (compatibilita').</summary>
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
                    ? (null, null, "Run Program senza FunctionValue")
                    : ("exec", fv, null);

            case "Open Folder":
                return string.IsNullOrEmpty(fv)
                    ? (null, null, "Open Folder senza FunctionValue")
                    : ("folder", fv, null);

            case "Run browser":
                return ("browser", "", null);

            case "Profile":
                // Es. "Next Profile", "Previous Profile", oppure nome profilo
                return ("profile", string.IsNullOrEmpty(fv) ? sft : fv, null);

            case "Adobe":
            case "Keyboard Shortcuts":
                // FunctionValue come "Ctrl + Shift + A" — il runtime lo passa
                // a SendKeysTranslator. Lo lasciamo come-is.
                return string.IsNullOrEmpty(fv)
                    ? (null, null, ft + " senza FunctionValue")
                    : ("keys", fv, null);

            case "Default":
                // Carattere letterale (es. "À", "É") oppure macro nominata.
                if (sft.Length == 1)
                    return ("text", sft, null);
                if (!string.IsNullOrEmpty(fv) && fv.Length == 1)
                    return ("text", fv, null);
                // Macro nominata: non sappiamo tradurre. Lasciamo solo
                // un placeholder che l'utente potra' completare a mano
                // nel dialog editor.
                if (!string.IsNullOrEmpty(sft))
                    return ("none", $"[macro] {sft}", $"macro \"{sft}\" non risolta");
                return (null, null, "Default senza informazioni");

            case "":
                return (null, null, null);

            default:
                return (null, null, $"FunctionType \"{ft}\" non gestito");
        }
    }
}

public sealed record ImportResult(
    string                  ProfileName,
    int                     ProfileSlot,
    IReadOnlyList<int>      ImportedCells,
    IReadOnlyList<string>   Skipped,
    IReadOnlyList<string>   Errors);
