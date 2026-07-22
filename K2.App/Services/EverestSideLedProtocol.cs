namespace K2.App.Services;

/// <summary>
/// Wire protocol for the Everest Max's 45 perimeter "side" LEDs (the border squares
/// around the keyboard + numpad bezels in Base Camp's Custom Lighting editor) — raw
/// HID on MI_03, command family <c>0x14 0x2C</c> (enable)/<c>0x14 0x2D</c> (colors)/
/// <c>0x13 0x55</c> (persist to flash slot 6). NOT covered by SDKDLL.dll's
/// <c>ChangeCustomizeEffect</c> (that struct only ever carried the 126 keycap LEDs,
/// see <see cref="EverestSdkNative.CustomEffect"/>) — keycap colors keep going through
/// SDKDLL.dll unchanged; this class only adds the border ring on top, via
/// <see cref="EverestHidNative"/>'s already-open MI_03 handle.
///
/// <para><b>Source: real USB capture, not the community project.</b> Byte-for-byte
/// confirmed 2026-07-22 from three user-provided USBPcap captures (painting border LEDs
/// #00FF00 one at a time, clockwise from top-left, on the keyboard alone / numpad alone
/// / both via multi-select) — see CHANGELOG. The wire-index SET for each edge
/// independently matches BaseCampLinux's <c>shared/ui_helpers.py</c>
/// <c>CustomRGBWindow._draw_keys</c> hstrip/vstrip calls (community reverse-engineering,
/// never itself verified against a real capture for Everest Max) — two independent
/// sources agreeing is why this mapping is trusted, not just BaseCampLinux's code.</para>
/// </summary>
internal static class EverestSideLedProtocol
{
    public const int MainCount = 31;
    public const int NumpadCount = 14;
    public const int TotalCount = MainCount + NumpadCount; // 45

    /// <summary>Main keyboard's 31 border LEDs, physical clockwise order starting
    /// top-left (Top 11 → Right 4 → Bottom 12 → Left 4) → wire index (0-44) used in
    /// <see cref="BuildSideLedPackets"/>.</summary>
    public static readonly byte[] MainOrder =
    {
        // Top (11, left->right)
        13, 14, 15, 7, 6, 5, 4, 3, 2, 1, 0,
        // Right (4, top->bottom)
        9, 8, 10, 11,
        // Bottom (12, right->left)
        12, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20,
        // Left (4, bottom->top)
        19, 18, 17, 16,
    };

    /// <summary>Numpad accessory's 14 border LEDs, same clockwise convention as
    /// <see cref="MainOrder"/> (Top 3 → Right 4 → Bottom 3 → Left 4).</summary>
    public static readonly byte[] NumpadOrder =
    {
        // Top (3, left->right)
        44, 43, 42,
        // Right (4, top->bottom)
        41, 40, 39, 38,
        // Bottom (3, right->left)
        37, 36, 35,
        // Left (4, bottom->top)
        34, 33, 32, 31,
    };

    /// <summary>
    /// Wire index for each of the 126 main-keycap LEDs — REUSES <see cref="Models.
    /// LedMatrixMapping"/>'s VK→SDK-LED-index table (0-125 range of it) as the raw-HID
    /// wire index too. <b>Capture-confirmed 2026-07-22</b> (evmax_anchors_bc.pcapng /
    /// evmax_numpad_bc.pcapng / evmax_fillall_bc.pcapng): Base Camp's own single-key
    /// applies were diffed apply-by-apply — 9 main-board anchors (Esc=0, Tab=2, Caps=3,
    /// LShift=4, LCtrl=5, Space=41, Backspace=87, F12=108, Enter=120, →=122) and 16
    /// numpad keys ALL landed exactly on their LedMatrixMapping index in the positional
    /// <c>14 2C 00 01</c> page stream (no per-entry index byte on the wire — position IS
    /// the LED index). So the SDK's GetColorData index domain IS the raw wire position
    /// domain; the earlier failed raw attempt was missing the <c>11 01</c> zone switch
    /// and used the wrong page count (8) and brightness scale (0-255), not a wrong
    /// mapping.
    /// </summary>
    public const int KeycapWireCount = 133; // 7 pages x 19 slots (126 real keys + 7 padding) — real BC capture; BaseCampLinux's 8x19 was wrong

    private const int KeycapPageCount = 7;

    /// <summary>Zone byte for <see cref="BuildZoneSwitchPacket"/>: main keycaps.</summary>
    public const byte ZoneKeycaps = 0x02;
    /// <summary>Zone byte for <see cref="BuildZoneSwitchPacket"/>: 45-LED side ring.</summary>
    public const byte ZoneSideRing = 0x05;

    /// <summary>Switches a lighting zone to the Custom effect: <c>11 01 00 zone 02 02</c>.
    /// Base Camp sends this before every keycap page burst (zone 02) and before every
    /// side-ring page burst (zone 05). K2 never sent it — the keycap pages were the part
    /// missing from K2's apply entirely (SDKDLL produced NO wire traffic at all, see the
    /// evmax_fillall_k2.pcapng capture), so this is the first time K2 drives keycaps
    /// raw.</summary>
    public static byte[] BuildZoneSwitchPacket(byte zone)
    {
        var pkt = new byte[64];
        pkt[0] = 0x11; pkt[1] = 0x01; pkt[2] = 0x00; pkt[3] = zone;
        pkt[4] = 0x02; pkt[5] = 0x02;
        return pkt;
    }

    /// <summary>Builds the 7 page packets (<c>14 2C 00 01 page brightness 00
    /// &lt;RGB...&gt;</c>) for the 126 main-keycap LEDs. <paramref name="wireColors"/> is
    /// indexed 0-132 (only 0-125 meaningful — BC pads 126-132 with black, see
    /// <see cref="KeycapWireCount"/>), 0xRRGGBB per entry. <paramref name="brightness"/>
    /// is on a 0-100 scale (BC sends its brightness slider value here, e.g. 0x4B=75 —
    /// NOT 0-255; values above 100 are clamped).</summary>
    public static byte[][] BuildKeycapPackets(int[] wireColors, byte brightness = 100)
    {
        if (brightness > 100) brightness = 100;
        var packets = new byte[KeycapPageCount][];
        for (int chunk = 0; chunk < KeycapPageCount; chunk++)
        {
            var pkt = new byte[64];
            pkt[0] = 0x14; pkt[1] = 0x2C; pkt[2] = 0x00; pkt[3] = 0x01;
            pkt[4] = (byte)chunk; pkt[5] = brightness; pkt[6] = 0x00;

            int wireBase = chunk * 19;
            int n = 19;
            for (int i = 0; i < n; i++)
            {
                int idx = wireBase + i;
                int rgb = idx < wireColors.Length ? wireColors[idx] : 0;
                pkt[7 + i * 3] = (byte)((rgb >> 16) & 0xFF);
                pkt[7 + i * 3 + 1] = (byte)((rgb >> 8) & 0xFF);
                pkt[7 + i * 3 + 2] = (byte)(rgb & 0xFF);
            }
            packets[chunk] = pkt;
        }
        return packets;
    }

    private static readonly int[] ChunkCounts = { 19, 19, 7 };

    /// <summary>
    /// Builds the 3 chunk packets (<c>14 2D 0A 00 chunk FF 00 &lt;RGB...&gt;</c>) for
    /// the current side-LED state. <paramref name="wireColors"/> is indexed 0-44
    /// (wire index, NOT physical position — translate via <see cref="MainOrder"/>/
    /// <see cref="NumpadOrder"/> first), 0xRRGGBB per entry.
    /// </summary>
    public static byte[][] BuildSideLedPackets(int[] wireColors, byte brightness = 0xFF)
    {
        var packets = new byte[3][];
        int wireBase = 0;
        for (int chunk = 0; chunk < 3; chunk++)
        {
            var pkt = new byte[64];
            pkt[0] = 0x14; pkt[1] = 0x2D; pkt[2] = 0x0A; pkt[3] = 0x00;
            pkt[4] = (byte)chunk; pkt[5] = brightness; pkt[6] = 0x00;

            int n = ChunkCounts[chunk];
            for (int i = 0; i < n; i++)
            {
                int idx = wireBase + i;
                int rgb = idx < wireColors.Length ? wireColors[idx] : 0;
                pkt[7 + i * 3] = (byte)((rgb >> 16) & 0xFF);
                pkt[7 + i * 3 + 1] = (byte)((rgb >> 8) & 0xFF);
                pkt[7 + i * 3 + 2] = (byte)(rgb & 0xFF);
            }
            packets[chunk] = pkt;
            wireBase += n;
        }
        return packets;
    }

    /// <summary>Enable Custom mode / set overall brightness: <c>14 2C 0A 00 FF
    /// &lt;brightness&gt;</c>, rest of the 64-byte packet 0xFF-filled — matches the
    /// exact padding seen in the real capture (BaseCampLinux zero-pads this instead;
    /// copied the real bytes here rather than trust that). <paramref name="brightness"/>
    /// is 0-100 like the page packets (BC sends 0x64=100 here in every 2026-07-22
    /// capture; the earlier 0xFF default predates knowing the scale).</summary>
    public static byte[] BuildEnablePacket(byte brightness = 100)
    {
        if (brightness > 100) brightness = 100;
        var pkt = new byte[64];
        for (int i = 0; i < pkt.Length; i++) pkt[i] = 0xFF;
        pkt[0] = 0x14; pkt[1] = 0x2C; pkt[2] = 0x0A; pkt[3] = 0x00;
        pkt[4] = 0xFF; pkt[5] = brightness;
        return pkt;
    }

    /// <summary>Persist the current Custom lighting (keys + border) to flash slot 6:
    /// <c>13 55 00 00 06</c>.</summary>
    public static byte[] BuildPersistPacket()
    {
        var pkt = new byte[64];
        pkt[0] = 0x13; pkt[1] = 0x55; pkt[4] = 0x06;
        return pkt;
    }
}
