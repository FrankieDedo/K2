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
        // enough: some input consumers (Windows' own Alt+Numpad composer
        // among them) expect the key to keep being reasserted the way a
        // real held key auto-repeats, not just go down once and stay
        // silent until the up. See <see cref="HoldRepeat"/>.
        var heldKeys = new HashSet<ushort>();

        try
        {
            for (int i = 0; i < iterations && !ct.IsCancellationRequested; i++)
            {
                heldKeys.Clear();
                foreach (var input in macro.Inputs)
                {
                    if (ct.IsCancellationRequested) break;

                    // Delay
                    int delay = macro.DelayOption switch
                    {
                        MacroDelay.NoDelay  => 0,
                        MacroDelay.Custom   => macro.CustomDelayMs,
                        _                   => input.DelayMs
                    };
                    if (delay > 0)
                        HoldRepeat(delay, heldKeys, ct);

                    ExecuteInput(input, heldKeys);
                }
                // A macro missing a keyup (truncated recording, edited by
                // hand) must not leave a modifier stuck down system-wide.
                foreach (var vk in heldKeys)
                    SendKeyInput(vk, true);
            }
        }
        finally
        {
            PlaybackStopped?.Invoke();
        }
    }

    private const int HoldRepeatIntervalMs = 30;

    /// <summary>Sleeps <paramref name="totalMs"/> in small slices, resending
    /// a keydown for every currently-held MODIFIER key on each slice —
    /// mirrors the OS's own key auto-repeat for a physically held modifier
    /// instead of a single fire-and-forget keydown.
    /// Only modifiers: resending a non-modifier key (e.g. a numpad digit
    /// between its recorded down and up) types it again on every slice,
    /// which corrupts Alt+Numpad codes (Alt+0233 became Alt+02223333…)
    /// and duplicates any character held across a recorded delay.</summary>
    private static void HoldRepeat(int totalMs, HashSet<ushort> heldKeys, CancellationToken ct)
    {
        int elapsed = 0;
        while (elapsed < totalMs && !ct.IsCancellationRequested)
        {
            int chunk = Math.Min(HoldRepeatIntervalMs, totalMs - elapsed);
            Thread.Sleep(chunk);
            elapsed += chunk;
            foreach (var vk in heldKeys)
                if (IsModifierKey(vk))
                    SendKeyInput(vk, false);
        }
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

    private static void ExecuteInput(MacroInput input, HashSet<ushort> heldKeys)
    {
        switch (input.Type)
        {
            case "keydown":
                heldKeys.Add((ushort)input.Key);
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
        if (button == 1) // left
            input.U.mi.dwFlags = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN;
        else if (button == 2) // right
            input.U.mi.dwFlags = up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN;
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
    private const uint MOUSEEVENTF_ABSOLUTE  = 0x8000;
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

    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
