using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// K2 as a <b>Mackie Control</b> surface for Fender Studio Pro, over two virtual MIDI ports
/// (loopMIDI or Windows MIDI Services loopback). The transport behind the <c>dp_modlink</c> games
/// whose <see cref="ModLinkGame.Midi"/> is set: <see cref="StateJson"/> stands in for the mod's
/// <c>/state</c> JSON, <see cref="Press"/> for the key bind.
///
/// <para><b>Ports.</b> One loopback port is one-way, so there are two, named so that nobody has to
/// guess which is which: <see cref="ToProgramPort"/> carries K2's button presses to the program
/// (Studio Pro's "Receive From"), <see cref="FromProgramPort"/> carries its LED feedback back (its
/// "Send To"). Matched by name, case-insensitively and as a substring, because WinMM truncates
/// names to 31 characters and some drivers append an index.</para>
///
/// <para><b>Protocol.</b> Mackie Control: a button is Note On (velocity 127 pressed, 0 released) on
/// channel 1; the host lights its LED with the same note (127 on, 1 blinking, 0 off). The host
/// opens with a device query SysEx (<c>F0 00 00 66 14 00 F7</c>); answering with a Device Online
/// message is all a host needs to start talking. State is "connected" once the host has sent
/// anything at all, because MCU has no explicit "I am here" other than that traffic.</para>
///
/// <para>Ports are opened lazily on the first press or poll and kept for the run — a closed port
/// would drop the program's connection every time the last tile left the screen.</para>
/// </summary>
internal static class McuClient
{
    internal const string ToProgramPort = "K2 to Studio";
    internal const string FromProgramPort = "Studio to K2";

    private static readonly object _gate = new();
    private static readonly byte[] _led = new byte[128];
    private static IntPtr _in, _out;
    private static volatile bool _hostSeen;
    private static DateTime _nextOpenTry;
    private static string _openError = "";

    // The callback delegate and the sysex buffer must outlive the port: WinMM keeps raw pointers.
    private static MidiInProc? _inProc;
    private static IntPtr _inHdr, _inBuf;
    private const int SysexBuf = 512;

    /// <summary>Why the ports are not open, for the config dialog's status line; empty when they are.</summary>
    internal static string LastError { get { lock (_gate) return _openError; } }

    /// <summary>The current feedback as the flat JSON every mod-link game answers, or null while the
    /// ports cannot be opened (virtual ports missing). Field names are the items' <c>Read</c>.</summary>
    internal static string? StateJson()
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return null;
            var sb = new StringBuilder("{\"k2\":1,\"inGame\":").Append(_hostSeen ? "true" : "false");
            foreach (var item in ModLinkGames.StudioPro.Items)
                if (item.Read is { } field && item.Mcu is >= 0 and var note)
                    sb.Append(",\"").Append(field).Append("\":")
                      .Append(_led[note & 0x7F] > 1 ? "true" : "false");
            return sb.Append('}').ToString();
        }
    }

    /// <summary>Presses and releases one Mackie Control button; <see cref="ModLinkGames.McuShift"/>
    /// in <paramref name="mcu"/> holds the MCU Shift button around it.</summary>
    internal static bool Press(int mcu)
    {
        lock (_gate)
        {
            if (!EnsureOpen()) return false;
            int note = mcu & 0x7F;
            bool shift = (mcu & ModLinkGames.McuShift) != 0;
            if (shift) Note(0x46, 127);
            Note(note, 127);
            Note(note, 0);
            if (shift) Note(0x46, 0);
            return true;
        }
    }

    private static void Note(int note, int velocity)
    {
        uint msg = (uint)(0x90 | (note << 8) | (velocity << 16));
        if (midiOutShortMsg(_out, msg) != 0) Close("MIDI output failed");
    }

    // ------------------------------------------------------------------ ports

    private static bool EnsureOpen()
    {
        if (_in != IntPtr.Zero && _out != IntPtr.Zero) return true;
        if (DateTime.UtcNow < _nextOpenTry) return false;
        _nextOpenTry = DateTime.UtcNow.AddSeconds(2);

        int outId = FindOut(ToProgramPort), inId = FindIn(FromProgramPort);
        if (outId < 0 || inId < 0)
        {
            string why = $"MIDI ports \"{ToProgramPort}\" / \"{FromProgramPort}\" not found";
            if (why != _openError) App.WriteLog("[MCU] " + why);
            _openError = why;
            return false;
        }

        try
        {
            if (midiOutOpen(out _out, (uint)outId, IntPtr.Zero, IntPtr.Zero, 0) != 0) throw new InvalidOperationException("midiOutOpen");

            _inProc = OnMidiIn;
            if (midiInOpen(out _in, (uint)inId, _inProc, IntPtr.Zero, CALLBACK_FUNCTION) != 0) throw new InvalidOperationException("midiInOpen");

            _inBuf = Marshal.AllocHGlobal(SysexBuf);
            _inHdr = Marshal.AllocHGlobal(Marshal.SizeOf<MIDIHDR>());
            var hdr = new MIDIHDR { lpData = _inBuf, dwBufferLength = SysexBuf };
            Marshal.StructureToPtr(hdr, _inHdr, false);
            midiInPrepareHeader(_in, _inHdr, (uint)Marshal.SizeOf<MIDIHDR>());
            midiInAddBuffer(_in, _inHdr, (uint)Marshal.SizeOf<MIDIHDR>());
            midiInStart(_in);
        }
        catch (Exception ex)
        {
            Close("open failed: " + ex.Message);
            return false;
        }

        _openError = "";
        _hostSeen = false;
        Array.Clear(_led);
        App.WriteLog("[MCU] ports open — announcing as Mackie Control");
        SendDeviceOnline();
        return true;
    }

    private static void Close(string why)
    {
        App.WriteLog("[MCU] closing ports: " + why);
        _openError = why;
        if (_in != IntPtr.Zero)
        {
            midiInReset(_in);
            if (_inHdr != IntPtr.Zero) midiInUnprepareHeader(_in, _inHdr, (uint)Marshal.SizeOf<MIDIHDR>());
            midiInClose(_in);
        }
        if (_out != IntPtr.Zero) { midiOutReset(_out); midiOutClose(_out); }
        if (_inHdr != IntPtr.Zero) Marshal.FreeHGlobal(_inHdr);
        if (_inBuf != IntPtr.Zero) Marshal.FreeHGlobal(_inBuf);
        _in = _out = _inHdr = _inBuf = IntPtr.Zero;
        _hostSeen = false;
    }

    /// <summary>MCU "Device Online": manufacturer 00 00 66, device 14 (Mackie Control), command 01,
    /// a 7-byte serial and a 4-byte challenge. The host's answer (02) is not checked — nothing here
    /// needs the connection to be authenticated.</summary>
    private static void SendDeviceOnline() =>
        SendSysex(new byte[]
        {
            0xF0, 0x00, 0x00, 0x66, 0x14, 0x01,
            (byte)'K', (byte)'2', (byte)'M', (byte)'C', (byte)'U', (byte)'0', (byte)'1',
            0x01, 0x02, 0x03, 0x04, 0xF7,
        });

    private static void SendSysex(byte[] data)
    {
        if (_out == IntPtr.Zero) return;
        int size = Marshal.SizeOf<MIDIHDR>();
        IntPtr buf = Marshal.AllocHGlobal(data.Length), hdrPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(data, 0, buf, data.Length);
            Marshal.StructureToPtr(new MIDIHDR { lpData = buf, dwBufferLength = (uint)data.Length,
                                                 dwBytesRecorded = (uint)data.Length }, hdrPtr, false);
            if (midiOutPrepareHeader(_out, hdrPtr, (uint)size) != 0) return;
            if (midiOutLongMsg(_out, hdrPtr, (uint)size) == 0)
            {
                // MHDR_DONE: the driver is finished with the buffer. Bounded wait, then give up.
                for (int i = 0; i < 40; i++)
                {
                    if ((Marshal.PtrToStructure<MIDIHDR>(hdrPtr).dwFlags & MHDR_DONE) != 0) break;
                    System.Threading.Thread.Sleep(5);
                }
            }
            midiOutUnprepareHeader(_out, hdrPtr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(hdrPtr); Marshal.FreeHGlobal(buf); }
    }

    // ------------------------------------------------------------------ input

    private static void OnMidiIn(IntPtr hMidiIn, uint msg, IntPtr instance, IntPtr p1, IntPtr p2)
    {
        try
        {
            if (msg == MIM_DATA)
            {
                uint m = (uint)p1.ToInt64();
                int status = (int)(m & 0xF0), d1 = (int)((m >> 8) & 0x7F), d2 = (int)((m >> 16) & 0x7F);
                // No lock here: Close() holds _gate while it resets the port, and WinMM may deliver
                // the returned buffers on that very call. Single-byte writes need none.
                _hostSeen = true;
                if (status == 0x90) _led[d1] = (byte)d2;       // Note On: LED level (127 on, 1 blink, 0 off)
                else if (status == 0x80) _led[d1] = 0;          // Note Off
            }
            else if (msg == MIM_LONGDATA)
            {
                var hdr = Marshal.PtrToStructure<MIDIHDR>(p1);
                if (hdr.dwBytesRecorded == 0) return;           // buffer handed back by a reset
                var bytes = new byte[hdr.dwBytesRecorded];
                Marshal.Copy(hdr.lpData, bytes, 0, bytes.Length);

                _hostSeen = true;
                midiInAddBuffer(hMidiIn, p1, (uint)Marshal.SizeOf<MIDIHDR>());   // allowed from the callback
                // Host's device query (F0 00 00 66 14 00 F7): answer with Device Online — off this
                // thread, because preparing an output header is not allowed inside a callback.
                bool query = bytes.Length >= 6 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x66 && bytes[5] == 0x00;
                if (query)
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => { lock (_gate) SendDeviceOnline(); });
            }
        }
        catch { /* a malformed message must never take the callback thread down */ }
    }

    // ------------------------------------------------------------------ enumeration

    private static int FindOut(string name)
    {
        uint n = midiOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIOUTCAPS();
            if (midiOutGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<MIDIOUTCAPS>()) == 0 &&
                caps.szPname.Contains(name, StringComparison.OrdinalIgnoreCase)) return (int)i;
        }
        return -1;
    }

    private static int FindIn(string name)
    {
        uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            var caps = new MIDIINCAPS();
            if (midiInGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<MIDIINCAPS>()) == 0 &&
                caps.szPname.Contains(name, StringComparison.OrdinalIgnoreCase)) return (int)i;
        }
        return -1;
    }

    // ------------------------------------------------------------------ WinMM

    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const uint MIM_DATA = 0x3C3, MIM_LONGDATA = 0x3C4;
    private const uint MHDR_DONE = 0x1;

    private delegate void MidiInProc(IntPtr hMidiIn, uint wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIDIHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext, reserved;
        public uint dwOffset;
        public IntPtr dwReserved0, dwReserved1, dwReserved2, dwReserved3;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MIDIINCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MIDIOUTCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public ushort wTechnology, wVoices, wNotes, wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCapsW(UIntPtr id, ref MIDIINCAPS caps, uint size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetDevCapsW(UIntPtr id, ref MIDIOUTCAPS caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out IntPtr h, uint id, MidiInProc cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInClose(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiInPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInAddBuffer(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutOpen(out IntPtr h, uint id, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiOutClose(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiOutReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(IntPtr h, uint msg);
    [DllImport("winmm.dll")] private static extern uint midiOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutLongMsg(IntPtr h, IntPtr hdr, uint size);
}
