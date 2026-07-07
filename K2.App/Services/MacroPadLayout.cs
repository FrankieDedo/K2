namespace K2.App.Services;

/// <summary>
/// Physical orientation in which the MacroPad is mounted. The value indicates
/// how many degrees CLOCKWISE the device is rotated relative to the
/// native layout (2 rows x 6 columns, keys upright).
///
/// <para>As with the DisplayPad: 0°, 90°, 180° and 270° are all supported.</para>
/// </summary>
public enum MacroPadRotation
{
    /// <summary>Native position: 2-row x 6-column grid.</summary>
    None = 0,
    /// <summary>Device mounted rotated 90 degrees clockwise.</summary>
    Cw90 = 90,
    /// <summary>Device mounted upside down (180 degrees, same 2x6 grid).</summary>
    Cw180 = 180,
    /// <summary>Device mounted rotated 270 degrees clockwise (= 90 counter-clockwise).</summary>
    Cw270 = 270,
}

/// <summary>
/// Geometry of the MacroPad's key grid and rotation logic.
///
/// Native PHYSICAL layout: 2 rows x 6 columns, indices 0..11
/// <code>
///     0  1  2  3  4  5
///     6  7  8  9 10 11
/// </code>
///
/// "Visual slot" = position of the cell in K2's on-screen grid, which is
/// re-oriented to mirror the device as mounted (rotated).
///
/// <para>Unlike the DisplayPad, the MacroPad does NOT have per-key screens:
/// rotation only affects the arrangement of cells on screen, no icon
/// pre-rotation is needed. The internal MODEL
/// (<see cref="Models.MacroPadKey"/>.Index, matrix map, persistence) stays
/// ALWAYS in PHYSICAL indices, so key handling and actions don't change.</para>
/// </summary>
public static class MacroPadLayout
{
    /// <summary>Rows of the native physical grid.</summary>
    public const int PhysRows = 2;
    /// <summary>Columns of the native physical grid.</summary>
    public const int PhysCols = 6;
    /// <summary>Total number of keys (FW_NUM_KEY).</summary>
    public const int ButtonCount = PhysRows * PhysCols; // 12

    /// <summary>On-screen grid size for the given rotation.
    /// At 90/270 the 2x6 strip becomes 6x2; at 0/180 it stays 2x6.</summary>
    public static (int Rows, int Cols) VisualGrid(MacroPadRotation r) =>
        r is MacroPadRotation.Cw90 or MacroPadRotation.Cw270 ? (PhysCols, PhysRows) : (PhysRows, PhysCols);

    /// <summary>
    /// Permutation table: for each visual slot (0..11, in reading order of
    /// the on-screen grid) returns the PHYSICAL index of the key to display
    /// in that position.
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
                // Device rotated 90 CW: the physical top-left corner goes to top-right.
                case MacroPadRotation.Cw90:
                    vr = pc;
                    vc = PhysRows - 1 - pr;
                    break;
                // Device rotated 270 CW (= 90 CCW): top-left goes to bottom-left.
                case MacroPadRotation.Cw270:
                    vr = PhysCols - 1 - pc;
                    vc = pr;
                    break;
                // Device rotated 180: top-left goes to bottom-right.
                case MacroPadRotation.Cw180:
                    vr = PhysRows - 1 - pr;
                    vc = PhysCols - 1 - pc;
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

    /// <summary>Short label for the UI.</summary>
    public static string Label(MacroPadRotation r) => r switch
    {
        MacroPadRotation.Cw90  => "90°",
        MacroPadRotation.Cw180 => "180°",
        MacroPadRotation.Cw270 => "270°",
        _                      => "0°",
    };

    /// <summary>Converts the saved value ("0"/"90"/"180"/"270") to the enum.</summary>
    public static MacroPadRotation Parse(string? s) => s switch
    {
        "90"  => MacroPadRotation.Cw90,
        "180" => MacroPadRotation.Cw180,
        "270" => MacroPadRotation.Cw270,
        _     => MacroPadRotation.None,
    };
}
