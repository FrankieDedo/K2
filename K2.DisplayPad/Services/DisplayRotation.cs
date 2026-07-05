using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace K2.DisplayPad.Services;

/// <summary>
/// Orientamento fisico col quale il DisplayPad e' montato.
/// Il valore indica di quanti gradi IN SENSO ORARIO il device e' ruotato
/// rispetto alla posizione nativa (striscia orizzontale, icone dritte).
/// </summary>
public enum DisplayRotation
{
    /// <summary>Posizione nativa: griglia 2 righe x 6 colonne.</summary>
    None = 0,
    /// <summary>Device montato ruotato di 90 gradi in senso orario.</summary>
    Cw90 = 90,
    /// <summary>Device montato ruotato di 270 gradi in senso orario (= 90 antiorario).</summary>
    Cw270 = 270,
}

/// <summary>
/// Geometria della griglia tasti del DisplayPad e logica di rotazione.
///
/// Layout FISICO nativo: 2 righe x 6 colonne, indici 0..11
/// <code>
///     0  1  2  3  4  5
///     6  7  8  9 10 11
/// </code>
///
/// "Visual slot" = posizione della cella nella griglia a schermo di K2,
/// che viene ri-orientata per rispecchiare il device montato ruotato.
///
/// Il MODELLO interno (<see cref="Models.ButtonCell"/>.Index, mappa matrix,
/// <see cref="StateStore"/>) resta SEMPRE in indici FISICI: la rotazione
/// tocca solo (a) la disposizione delle celle a schermo e (b) i pixel
/// dell'icona caricata sul firmware. Cosi' la gestione tasti, le azioni e
/// la persistenza non vanno toccate.
/// </summary>
public static class DisplayPadLayout
{
    /// <summary>Righe della griglia fisica nativa.</summary>
    public const int PhysRows = 2;
    /// <summary>Colonne della griglia fisica nativa.</summary>
    public const int PhysCols = 6;
    /// <summary>Numero totale di tasti (FW_NUM_KEY).</summary>
    public const int ButtonCount = PhysRows * PhysCols; // 12

    /// <summary>Dimensione della griglia a schermo per la rotazione data.
    /// A 90/270 la striscia 2x6 diventa 6x2.</summary>
    public static (int Rows, int Cols) VisualGrid(DisplayRotation r) =>
        r == DisplayRotation.None ? (PhysRows, PhysCols) : (PhysCols, PhysRows);

    /// <summary>
    /// Tabella di permutazione: per ogni visual slot (0..11, in ordine di
    /// lettura della griglia a schermo) restituisce l'indice FISICO del
    /// tasto da mostrare in quella posizione.
    /// </summary>
    public static int[] PhysicalForVisual(DisplayRotation r)
    {
        var (_, vCols) = VisualGrid(r);
        var map = new int[ButtonCount];
        for (int pr = 0; pr < PhysRows; pr++)
        for (int pc = 0; pc < PhysCols; pc++)
        {
            int phys = pr * PhysCols + pc;
            int vr, vc;
            switch (r)
            {
                // Device ruotato 90 CW: l'angolo fisico in alto-sx va in alto-dx.
                case DisplayRotation.Cw90:
                    vr = pc;
                    vc = PhysRows - 1 - pr;
                    break;
                // Device ruotato 270 CW (= 90 CCW): in alto-sx va in basso-sx.
                case DisplayRotation.Cw270:
                    vr = PhysCols - 1 - pc;
                    vc = pr;
                    break;
                default:
                    vr = pr;
                    vc = pc;
                    break;
            }
            map[vr * vCols + vc] = phys;
        }
        return map;
    }

    /// <summary>Etichetta breve per la UI.</summary>
    public static string Label(DisplayRotation r) => r switch
    {
        DisplayRotation.Cw90  => "90°",
        DisplayRotation.Cw270 => "270°",
        _                     => "0°",
    };

    /// <summary>Converte il valore salvato su DB ("0"/"90"/"270") in enum.</summary>
    public static DisplayRotation Parse(string? s) => s switch
    {
        "90"  => DisplayRotation.Cw90,
        "270" => DisplayRotation.Cw270,
        _     => DisplayRotation.None,
    };
}

/// <summary>
/// Ruota i PNG delle icone prima dell'upload sul firmware.
///
/// Le icone del DisplayPad sono 102x102 px (quadrate, verificato sui
/// profili BaseCamp): una rotazione di 90/270 e' senza perdita e non
/// cambia le dimensioni, quindi non serve alcun re-fit.
///
/// L'angolo applicato all'IMMAGINE e' OPPOSTO a quello del device, cosi'
/// che (rotazione device) + (pre-rotazione icona) = icona dritta per chi
/// guarda il pad montato ruotato.
///
/// I file ruotati sono messi in cache su disco; la chiave include il path
/// originale, la data di modifica e l'angolo, percio' la cache si
/// auto-invalida quando l'immagine sorgente cambia.
/// </summary>
public static class IconRotator
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2.DisplayPad", "rotated");

    /// <summary>Angolo orario da applicare ALL'IMMAGINE per compensare il
    /// montaggio del device.</summary>
    private static int ImageAngleCw(DisplayRotation r) => r switch
    {
        DisplayRotation.Cw90  => 270, // device 90 CW  -> icona ruotata 90 CCW
        DisplayRotation.Cw270 => 90,  // device 270 CW -> icona ruotata 90 CW
        _                     => 0,
    };

    /// <summary>
    /// Restituisce il path di un PNG ruotato pronto per l'upload.
    /// Se la rotazione e' <see cref="DisplayRotation.None"/>, il path non
    /// esiste o la rotazione fallisce, restituisce il path originale.
    /// </summary>
    public static string ResolveForUpload(string? originalPath, DisplayRotation r)
    {
        if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath))
            return originalPath ?? "";

        int angle = ImageAngleCw(r);
        if (angle == 0) return originalPath;

        try
        {
            Directory.CreateDirectory(CacheDir);
            string cached = Path.Combine(CacheDir, CacheName(originalPath, angle));
            if (File.Exists(cached)) return cached;

            // Caricamento via MemoryStream: cosi' il file sorgente non resta
            // lockato e possiamo lavorare su una copia indipendente.
            byte[] bytes = File.ReadAllBytes(originalPath);
            using (var ms  = new MemoryStream(bytes))
            using (var src = new Bitmap(ms))
            using (var bmp = new Bitmap(src))
            {
                bmp.RotateFlip(angle == 90
                    ? RotateFlipType.Rotate90FlipNone
                    : RotateFlipType.Rotate270FlipNone);
                bmp.Save(cached, ImageFormat.Png);
            }
            return cached;
        }
        catch
        {
            // In caso di errore meglio caricare l'icona non ruotata che
            // lasciare il tasto vuoto.
            return originalPath;
        }
    }

    private static string CacheName(string path, int angle)
    {
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(path).Ticks; } catch { /* best effort */ }
        var key  = $"{path}|{mtime}|{angle}";
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(hash.Length * 2 + 8);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        sb.Append("_r").Append(angle).Append(".png");
        return sb.ToString();
    }
}
