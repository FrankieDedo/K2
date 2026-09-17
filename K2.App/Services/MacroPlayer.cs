// Services/MacroPlayer.cs — plays back recorded macros
// Uses SendInput (Win32) to simulate keydown/keyup and mouse events.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using K2.App.Models;

namespace K2.App.Services;

public sealed class MacroPlayer
{
    private CancellationTokenSource? _cts;
    private Task? _playTask;
    public bool IsPlaying => _playTask is { IsCompleted: false };

    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;

    /// <summary>Raised for a <c>k2action</c> step (args: K2 action type, payload).
    /// The host marshals to the UI thread and runs it through
    /// <c>ButtonActionEngine</c> — playback does not wait for it to finish.</summary>
    public event Action<string, string>? K2ActionRequested;

    public void Play(MacroDefinition macro)
    {
        if (IsPlaying) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _playTask = Task.Run(() => PlayInternal(macro, token), token);
        PlaybackStarted?.Invoke();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void PlayInternal(MacroDefinition macro, CancellationToken ct)
    {
        int iterations = macro.PlaybackOption switch
        {
            MacroPlayback.RepeatN => macro.RepeatCount,
            MacroPlayback.WhileHeld or MacroPlayback.Toggle => int.MaxValue,
            _ => 1
        };

        // Keys whose keydown has been sent but whose matching keyup hasn't
        // played yet — i.e. keys the macro is "holding" (typically a
        // modifier like Alt held across several other keys, e.g. an
        // Alt+Numpad Unicode code). A single keydown sent once isn't
        // enough: a really held key keeps emitting — modifiers must be
        // reasserted for consumers that expect it (Windows' own Alt+Numpad
        // composer among them), and an ordinary key auto-repeats.
        // See <see cref="HoldRepeat"/>.
        var heldKeys = new Dictionary<ushort, HeldKey>();

        try
        {
            for (int i = 0; i < iterations && !ct.IsCancellationRequested; i++)
            {
                heldKeys.Clear();
                for (int idx = 0; idx < macro.Inputs.Count; idx++)
                {
                    if (ct.IsCancellationRequested) break;
                    var input = macro.Inputs[idx];

                    if (input.Type == "k2action")
                    {
                        K2ActionRequested?.Invoke(input.K2Type ?? "", input.Text ?? "");
                    }
                    // Alt+Numpad code (Alt held, numpad digits, Alt released — e.g.
                    // Alt+0192 = "À"): compose the character ourselves and inject it as
                    // a single Unicode keystroke instead of replaying the raw keys.
                    // Windows' own Alt+Numpad composer has proven unreliable with
                    // injected input on this machine — a clean SendInput stream (with
                    // scan codes, with/without the HoldRepeat modifier re-assert)
                    // produced empty text in a dedicated standalone harness, root cause
                    // never isolated (see CHANGELOG 2026-07-14). Composing the group
                    // deterministically removes every timing/composer dependency.
                    // Only when no other key is currently held by the macro: a group
                    // played under e.g. a held Ctrl isn't a plain Alt code.
                    else if (heldKeys.Count == 0
                             && TryComposeAltCode(macro.Inputs, idx, out char altChar, out int groupEnd))
                    {
                        SendCharInput(altChar);
                        idx = groupEnd;          // skip the whole group, intra-group delays included
                        input = macro.Inputs[idx];   // the group's closing Alt-up carries the trailing delay
                    }
                    else
                    {
                        ExecuteInput(input, heldKeys);
                    }

                    // Delay AFTER the step just played — the in-memory convention
                    // (see MacroInput.ShiftToDelayAfter). On a keydown that is how
                    // long the key stays held down (HoldRepeat keeps it alive for
                    // the whole stretch); on a keyup it is the pause before the
                    // next step.
                    int delay = macro.DelayOption switch
                    {
                        MacroDelay.NoDelay  => 0,
                        MacroDelay.Custom   => macro.CustomDelayMs,
                        _                   => input.DelayMs
                    };
                    if (delay > 0)
                        HoldRepeat(delay, heldKeys, ct);
                }
                // A macro missing a keyup (truncated recording, edited by
                // hand) must not leave a modifier stuck down system-wide.
                foreach (var vk in heldKeys.Keys)
                    SendKeyInput(vk, true);
            }
        }
        finally
        {
            PlaybackStopped?.Invoke();
        }
    }

    private const int HoldRepeatIntervalMs = 15;

    /// <summary>State kept for a key the macro is currently holding down.</summary>
    private sealed class HeldKey
    {
        /// <summary><see cref="Environment.TickCount64"/> at which this key's next
        /// auto-repeat keydown is due. Seeded one typematic delay after the
        /// original keydown — only non-modifier keys use it.</summary>
        public long RepeatAtTick;
    }

    /// <summary>Sleeps <paramref name="totalMs"/> in small slices, keeping every
    /// currently-held key alive the way a physically held key stays alive:
    /// <list type="bullet">
    /// <item>MODIFIERS are reasserted on every slice — some consumers (Windows'
    /// own Alt+Numpad composer among them) expect a steady stream rather than a
    /// single fire-and-forget keydown, and a repeated modifier emits nothing.</item>
    /// <item>ORDINARY keys auto-repeat on the system's own typematic schedule
    /// (Control Panel repeat delay, then repeat rate), so a recording where "A"
    /// was held for two seconds plays back as "aaaaaaa…" rather than a single
    /// "a". The initial delay is what keeps this from corrupting fast
    /// sequences: normal typing leaves a key down for far less than it, so
    /// Alt+Numpad digits and quick keystrokes still fire exactly once.</item>
    /// </list></summary>
    private static void HoldRepeat(int totalMs, Dictionary<ushort, HeldKey> heldKeys, CancellationToken ct)
    {
        int elapsed = 0;
        while (elapsed < totalMs && !ct.IsCancellationRequested)
        {
            int chunk = Math.Min(HoldRepeatIntervalMs, totalMs - elapsed);
            Thread.Sleep(chunk);
            elapsed += chunk;
            long now = Environment.TickCount64;
            foreach (var pair in heldKeys)
            {
                if (IsModifierKey(pair.Key)) { SendKeyInput(pair.Key, false); continue; }
                if (now < pair.Value.RepeatAtTick) continue;
                SendKeyInput(pair.Key, false);
                pair.Value.RepeatAtTick = now + TypematicRepeatMs;
            }
        }
    }

    /// <summary>Windows' keyboard repeat DELAY (SPI_GETKEYBOARDDELAY, 0-3 →
    /// 250/500/750/1000 ms) — how long a key must stay down before it starts
    /// repeating. Read once: changing it mid-session is not worth a re-read.</summary>
    private static readonly int TypematicDelayMs = ReadTypematicDelayMs();

    private static int ReadTypematicDelayMs() =>
        SystemParametersInfo(SPI_GETKEYBOARDDELAY, 0, out int v, 0) && v is >= 0 and <= 3
            ? 250 * (v + 1)
            : 500;

    /// <summary>Windows' keyboard repeat RATE (SPI_GETKEYBOARDSPEED, 0-31 →
    /// ~2.5 to ~30 repeats per second), as a period in ms.</summary>
    private static readonly int TypematicRepeatMs = ReadTypematicRepeatMs();

    private static int ReadTypematicRepeatMs() =>
        SystemParametersInfo(SPI_GETKEYBOARDSPEED, 0, out int v, 0) && v is >= 0 and <= 31
            ? (int)Math.Round(1000.0 / (2.5 + 27.5 * v / 31.0))
            : 33;

    /// <summary>
    /// Detects an Alt+Numpad compose group starting at <paramref name="start"/>:
    /// a keydown of plain Alt (VK_MENU/VK_LMENU — not AltGr, which is Ctrl+Alt and
    /// never a plain Alt code) followed exclusively by numpad-digit keydown/keyups
    /// (VK_NUMPAD0-9) up to the matching Alt keyup. On success returns the composed
    /// character and the index of the closing Alt keyup. Decoding follows Windows'
    /// own legacy rule: a leading 0 selects the active ANSI code page (e.g. Alt+0192
    /// → cp1252 "À"), no leading 0 the OEM code page — resolved via
    /// MultiByteToWideChar so the result matches this system's exact code pages
    /// without any encoding-provider dependency. Any other event inside the group
    /// (another key, a mouse event) means "not an Alt code" — caller falls back to
    /// normal key playback.
    /// </summary>
    private static bool TryComposeAltCode(
        IReadOnlyList<MacroInput> inputs, int start, out char ch, out int end)
    {
        ch = '\0';
        end = start;
        if (inputs[start].Type != "keydown") return false;
        ushort altVk = (ushort)inputs[start].Key;
        if (altVk is not (0x12 or 0xA4)) return false; // VK_MENU, VK_LMENU

        var digits = new System.Text.StringBuilder();
        for (int j = start + 1; j < inputs.Count; j++)
        {
            var ev = inputs[j];
            ushort vk = (ushort)ev.Key;

            if (ev.Type == "keyup" && (vk == altVk || vk == 0x12))
            {
                if (digits.Length == 0 || !TryDecodeAltCode(digits.ToString(), out ch))
                    return false;
                end = j;
                return true;
            }

            if (ev.Type is not ("keydown" or "keyup")) return false;
            if (vk is < 0x60 or > 0x69) return false; // numpad digits only
            if (ev.Type == "keydown")
                digits.Append((char)('0' + (vk - 0x60)));
            if (digits.Length > 10) return false; // runaway/garbage recording
        }
        return false; // Alt never released within the macro
    }

    private static bool TryDecodeAltCode(string digits, out char ch)
    {
        ch = '\0';
        if (!int.TryParse(digits, out int value) || value <= 0) return false;
        // Windows' legacy composer uses the low byte of the accumulated number.
        var bytes = new[] { (byte)(value & 0xFF) };
        uint codePage = digits[0] == '0' ? CP_ACP : CP_OEMCP;
        var buf = new char[2];
        int n = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, buf, buf.Length);
        if (n <= 0) return false;
        ch = buf[0];
        return true;
    }

    private static bool IsModifierKey(ushort vk) => vk switch
    {
        0x10 or 0x11 or 0x12          // VK_SHIFT, VK_CONTROL, VK_MENU
            or 0xA0 or 0xA1           // VK_LSHIFT, VK_RSHIFT
            or 0xA2 or 0xA3           // VK_LCONTROL, VK_RCONTROL
            or 0xA4 or 0xA5           // VK_LMENU, VK_RMENU (AltGr)
            or 0x5B or 0x5C           // VK_LWIN, VK_RWIN
            => true,
        _ => false
    };

    private static void ExecuteInput(MacroInput input, Dictionary<ushort, HeldKey> heldKeys)
    {
        switch (input.Type)
        {
            case "keydown":
                heldKeys[(ushort)input.Key] =
                    new HeldKey { RepeatAtTick = Environment.TickCount64 + TypematicDelayMs };
                SendKeyInput((ushort)input.Key, false);
                break;
            case "keyup":
                heldKeys.Remove((ushort)input.Key);
                SendKeyInput((ushort)input.Key, true);
                break;
            case "mousedown":
                SendMouseClick(input.X, input.Y, input.Key, false);
                break;
            case "mouseup":
                SendMouseClick(input.X, input.Y, input.Key, true);
                break;
            case "mousewheel":
                SendMouseWheel(input.X, input.Y, input.Key);
                break;
            case "mousemove":
                SendMouseMove(input.X, input.Y);
                break;
            case "text":
                if (input.Text != null)
                    foreach (char c in input.Text)
                        SendCharInput(c);
                break;
        }
    }

    // ─────────────────────── Win32 SendInput ───────────────────────

    private static void SendKeyInput(ushort vk, bool keyUp)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki.wVk = vk;
        // Real keystrokes always carry a scan code; leaving wScan at 0 makes
        // some consumers (games reading scan codes, parts of the Alt+Numpad
        // composer pipeline) drop the injected event.
        input.U.ki.wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        input.U.ki.dwFlags = flags;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Keys Windows requires KEYEVENTF_EXTENDEDKEY for (per SendInput docs):
    /// right Ctrl/Alt, the arrow/nav cluster, and Num Lock. Without this flag
    /// SendInput derives the scan code from the VK alone, which collapses
    /// Right Alt (AltGr) to the same non-extended scan code as Left Alt —
    /// so a recorded AltGr macro silently plays back as plain Alt.
    /// </summary>
    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0xA5 or 0xA3        // VK_RMENU (AltGr), VK_RCONTROL
            or 0x2D or 0x2E // Insert, Delete
            or 0x24 or 0x23 // Home, End
            or 0x21 or 0x22 // Page Up, Page Down
            or 0x25 or 0x26 or 0x27 or 0x28 // arrows
            or 0x90         // Num Lock
            => true,
        _ => false
    };

    private static void SendCharInput(char c)
    {
        var down = new INPUT { type = INPUT_KEYBOARD };
        down.U.ki.wScan = c;
        down.U.ki.dwFlags = KEYEVENTF_UNICODE;

        var up = new INPUT { type = INPUT_KEYBOARD };
        up.U.ki.wScan = c;
        up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseMove(int x, int y)
    {
        // Normalize to absolute coordinates (0-65535)
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        int normX = (int)((x * 65536.0) / screenW);
        int normY = (int)((y * 65536.0) / screenH);

        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dx = normX;
        input.U.mi.dy = normY;
        input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseClick(int x, int y, int button, bool up)
    {
        // x/y == -1 means "no recorded position" (e.g. imported from BaseCamp
        // click-repeat macros) — click wherever the cursor already is instead
        // of warping it to (-1,-1)/(0,0).
        if (x >= 0 && y >= 0)
            SendMouseMove(x, y);

        var input = new INPUT { type = INPUT_MOUSE };
        switch (button)
        {
            case MacroRecorder.MouseLeft:
                input.U.mi.dwFlags = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN;
                break;
            case MacroRecorder.MouseRight:
                input.U.mi.dwFlags = up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN;
                break;
            case MacroRecorder.MouseMiddle:
                input.U.mi.dwFlags = up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN;
                break;
            case MacroRecorder.MouseX1:
            case MacroRecorder.MouseX2:
                input.U.mi.dwFlags = up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN;
                input.U.mi.mouseData = button == MacroRecorder.MouseX2 ? XBUTTON2 : XBUTTON1;
                break;
            default:
                return;                       // unknown button — nothing to send
        }
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Replays a wheel scroll. <paramref name="delta"/> is the recorded
    /// signed WHEEL_DELTA multiple (positive = away from the user).</summary>
    private static void SendMouseWheel(int x, int y, int delta)
    {
        if (delta == 0) return;
        if (x >= 0 && y >= 0)
            SendMouseMove(x, y);

        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
        input.U.mi.mouseData = unchecked((uint)delta);
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ─────────────────────── Constants & structures ───────────────────────

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_MOVE      = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN  = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP    = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP   = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN= 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP  = 0x0040;
    private const uint MOUSEEVENTF_XDOWN     = 0x0080;
    private const uint MOUSEEVENTF_XUP       = 0x0100;
    private const uint MOUSEEVENTF_WHEEL     = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE  = 0x8000;
    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint SPI_GETKEYBOARDSPEED = 0x000A;
    private const uint SPI_GETKEYBOARDDELAY = 0x0016;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint CP_ACP   = 0; // active ANSI code page
    private const uint CP_OEMCP = 1; // active OEM code page

    [DllImport("kernel32.dll")]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[] lpMultiByteStr, int cbMultiByte,
        [Out] char[] lpWideCharStr, int cchWideChar);
}
