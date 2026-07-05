using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace K2.App.Services;

/// <summary>
/// Minimal pcap-ng parser (link_type USBPcap = 249).
/// C# port of <c>K2/_reference/tools/parse_usb_pcap.py</c>.
/// Filters USB OUT packets (host -> device) and returns the payloads.
/// </summary>
internal static class PcapParser
{
    /// <summary>Single USB OUT packet extracted from the pcapng.</summary>
    internal sealed record UsbPacket
    {
        public int    Index   { get; init; }
        public ushort Bus     { get; init; }
        public ushort Device  { get; init; }
        public byte   Endpoint{ get; init; }
        public byte   Transfer{ get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public long   TimestampUs { get; init; }
    }

    /// <summary>
    /// Extracts all USB OUT packets (interrupt/bulk, non-empty payload)
    /// from a pcap-ng file captured with USBPcap.
    /// </summary>
    public static List<UsbPacket> ParseOutPackets(string pcapngPath)
    {
        var data = File.ReadAllBytes(pcapngPath);
        var result = new List<UsbPacket>();
        int off = 0;
        int pktIdx = 0;
        // ushort linkType = 0; // unused after assignment

        while (off + 8 <= data.Length)
        {
            uint btype = BitConverter.ToUInt32(data, off);
            uint blen  = BitConverter.ToUInt32(data, off + 4);
            if (blen < 12 || off + (int)blen > data.Length) break;

            if (btype == 1) // IDB — Interface Description Block
            {
                // linkType = BitConverter.ToUInt16(data, off + 8);
            }
            else if (btype == 6) // EPB — Enhanced Packet Block
            {
                uint capLen = BitConverter.ToUInt32(data, off + 20);
                int pktStart = off + 28;
                if (pktStart + (int)capLen > off + (int)blen) { off += (int)blen; continue; }

                long tsHi = BitConverter.ToUInt32(data, off + 12);
                long tsLo = BitConverter.ToUInt32(data, off + 16);
                long ts = (tsHi << 32) | tsLo;

                var pkt = new ReadOnlySpan<byte>(data, pktStart, (int)capLen);
                pktIdx++;
                var u = ParseUsbPcapHeader(pkt);
                if (u != null)
                {
                    u = u with { Index = pktIdx, TimestampUs = ts };
                    result.Add(u);
                }
            }
            off += (int)blen;
        }
        return result;
    }

    private static UsbPacket? ParseUsbPcapHeader(ReadOnlySpan<byte> pkt)
    {
        if (pkt.Length < 27) return null;

        ushort hlen = BitConverter.ToUInt16(pkt);
        ushort bus  = BitConverter.ToUInt16(pkt.Slice(17));
        ushort dev  = BitConverter.ToUInt16(pkt.Slice(19));
        byte   ep   = pkt[21];
        byte   xfer = pkt[22];
        uint   dataLen = BitConverter.ToUInt32(pkt.Slice(23));

        // Only interrupt (1) or bulk (3), OUT direction (bit 7 = 0), non-empty payload
        if (xfer != 1 && xfer != 3) return null;
        if ((ep & 0x80) != 0) return null; // IN, skip
        if (dataLen == 0) return null;

        int payloadStart = hlen;
        int payloadLen = (int)Math.Min(dataLen, (uint)(pkt.Length - payloadStart));
        if (payloadLen <= 0) return null;

        return new UsbPacket
        {
            Bus      = bus,
            Device   = dev,
            Endpoint = ep,
            Transfer = xfer,
            Payload  = pkt.Slice(payloadStart, payloadLen).ToArray(),
        };
    }

    /// <summary>Formats a packet as a readable hex dump.</summary>
    public static string FormatPacket(UsbPacket p, int maxPayload = 128)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#{p.Index} OUT bus={p.Bus} dev={p.Device} ep=0x{p.Endpoint:X2} xfer={p.Transfer} len={p.Payload.Length}");
        HexDump(sb, p.Payload, maxPayload);
        return sb.ToString();
    }

    /// <summary>Formats all packets into a text report.</summary>
    public static string FormatAll(IReadOnlyList<UsbPacket> packets)
    {
        var sb = new StringBuilder();
        foreach (var p in packets)
        {
            sb.AppendLine(FormatPacket(p));
        }
        sb.AppendLine($"# Total OUT packets: {packets.Count}");
        return sb.ToString();
    }

    private static void HexDump(StringBuilder sb, byte[] data, int maxLen)
    {
        int len = Math.Min(data.Length, maxLen);
        for (int i = 0; i < len; i += 16)
        {
            int chunk = Math.Min(16, len - i);
            sb.Append($"  {i:X4}  ");
            for (int j = 0; j < 16; j++)
            {
                if (j < chunk) sb.Append($"{data[i + j]:X2} ");
                else sb.Append("   ");
            }
            sb.Append(" ");
            for (int j = 0; j < chunk; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
    }
}
