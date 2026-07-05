namespace K2.App.Services;

/// <summary>
/// Physical orientation in which the MacroPad is mounted. The value indicates
/// how many degrees CLOCKWISE the device is rotated relative to the
/// nativa (2 righe × 6 colonne, tasti dritti).
///
/// <para>As with the DisplayPad: 90° and 270° are supported, 180° is excluded
/// per scelta (mounting mai capovolto).</para>
/// </summary>
public enum MacroPadRotation
{
    /// <summary>Posizione nativa: griglia 2 righe × 6 colonne.</summary>
    None = 0,
    /// <summary>Device montato ruotato di 90 gradi in senso orario.</summary>
    Cw90 = 90,
    /// <summary>Device montato ruotato di 270 gradi in senso orario (= 90 antiorario).</summary>
    Cw270 = 270,
}

/// <summary>
/// Geometria della griglia tasti del MacroPad e logica di rotazione.
///
/// Layout FISICO nativo: 2 righe × 6 colonne, indici 0..11
/// <code>
///     0  1  2  3  4  5
///     6  7  8  9 10 11
/// </code>
///
/// "Visual slot" = posizione della cella nella griglia a schermo di K2, che
/// viene ri-orientata per rispecchiare il device montato ruotato.
///
/// <para>A differenza del DisplayPad, il MacroPad NON ha schermi per-tasto:
/// la rotazione tocca SOLO la disposizione delle celle a schermo, non serve
/// alcuna pre-rotazione di icone. Il MODELLO interno
/// (<see cref="Models.MacroPadKey"/>.Index, mappa matrice, persistenza) resta
/// ALWAYS in PHYSICAL indices, so key handling and actions don't change.</para>
/// </summary>
public static class MacroPadLayout
{
    /// <summary>Righe della griglia fisica nativa.</summary>
    public const int PhysRows = 2;
    /// <summary>Colonne della griglia fisica nativa.</summary>
    public const int PhysCols = 6;
    /// <summary>Numero totale di tasti (FW_NUM_KEY).</summary>
    public const int ButtonCount = PhysRows * PhysCols; // 12

    /// <summary>Dimensione della griglia a schermo per la rotazione data.
    /// A 90/270 la striscia 2×6 diventa 6×2.</summary>
    public static (int Rows, int Cols) VisualGrid(MacroPadRotation r) =>
        r == MacroPadRotation.None ? (PhysRows, PhysCols) : (PhysCols, PhysRows);

    /// <summary>
    /// Tabella di permutazione: per ogni visual slot (0..11, in ordine di
    /// lettura della griglia a schermo) restituisce l'indice FISICO del tasto
    /// da mostrare in quella posizione.
    /// </summary>
    public static int[] PhysicalForVisual(MacroPadRotation r)
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
                case MacroPadRotation.Cw90:
                    vr = pc;
                    vc = PhysRows - 1 - pr;
                    break;
                // Device ruotato 270 CW (= 90 CCW): in alto-sx va in basso-sx.
                case MacroPadRotation.Cw270:
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
    public static string Label(MacroPadRotation r) => r switch
    {
        MacroPadRotation.Cw90  => "90°",
        MacroPadRotation.Cw270 => "270°",
        _                      => "0°",
    };

    /// <summary>Converte il valore salvato ("0"/"90"/"270") in enum.</summary>
    public static MacroPadRotation Parse(string? s) => s switch
    {
        "90"  => MacroPadRotation.Cw90,
        "270" => MacroPadRotation.Cw270,
        _     => MacroPadRotation.None,
    };
}
