using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace K2.Core;

/// <summary>
/// Replays a shortcut like <c>"Ctrl+Shift+V"</c> as real keyboard input, for the cases where the
/// target listens for a GLOBAL hotkey rather than for text in its window.
///
/// <para>
/// Why not <see cref="System.Windows.Forms.SendKeys"/> (which the <c>keys</c> action uses): that
/// API drives the <b>foreground window's</b> message queue through a journal hook. Applications
/// that register their shortcuts globally — Discord's Keybinds are the case this was written for,
/// and the same is true of most overlays and voice apps — watch the input stream with a low-level
/// keyboard hook instead, which journal-injected keystrokes never reach. The symptom is exactly
/// "the shortcut is right, Discord just never reacts". <c>SendInput</c> injects into the real
/// input stream, so those hooks see it.
/// </para>
///
/// <para>
/// Each key is sent with both its virtual-key code and its scan code (no <c>KEYEVENTF_SCANCODE</c>:
/// consumers that read the VK and consumers that read the scan code then both get what they
/// expect), modifiers are pressed before the key and released after it in reverse order, and the
/// whole thing is spaced out by a few milliseconds — a burst delivered in one tick is dropped by
/// some listeners.
/// </para>
/// </summary>
public static class HotkeySender
{
    /// <summary>Sends <paramref name="hotkey"/> ("Ctrl+Shift+V", "Alt+F4", "F13", …). Returns false
    /// (with the reason in <paramref name="error"/>) when the combination can't be resolved on the
    /// current keyboard layout. Blocks for a few ms — call it off the UI thread.</summary>
    public static bool TrySend(string? hotkey, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(hotkey)) { error = "empty shortcut"; return false; }

        var mods = new List<ushort>();
        ushort key = 0;
        foreach (var raw in hotkey.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mods.Add(VK_CONTROL); break;
                case "SHIFT": mods.Add(VK_SHIFT); break;
                case "ALT": mods.Add(VK_MENU); break;
                case "WIN": case "GUI": case "META": case "CMD": mods.Add(VK_LWIN); break;
                default:
                    if (!TryResolveKey(part, out key)) { error = $"unknown key \"{part}\""; return false; }
                    break;
            }
        }
        if (key == 0) { error = "no key in the shortcut"; return false; }

        foreach (var m in mods) { Send(m, down: true); Thread.Sleep(5); }
        Send(key, down: true);
        Thread.Sleep(30);
        Send(key, down: false);
        for (int i = mods.Count - 1; i >= 0; i--) { Thread.Sleep(5); Send(mods[i], down: false); }
        return true;
    }

    /// <summary>Virtual-key code for one key name. Letters/digits go through the CURRENT keyboard
    /// layout (<c>VkKeyScan</c>), so a shortcut recorded on an Italian layout is replayed as the
    /// same physical key, not as whatever US position that character happens to have.</summary>
    private static bool TryResolveKey(string name, out ushort vk)
    {
        vk = 0;
        if (name.Length == 1)
        {
            short scan = VkKeyScan(char.ToUpperInvariant(name[0]));
            if (scan == -1) return false;
            vk = (ushort)(scan & 0xFF);
            return true;
        }
        if ((name[0] is 'F' or 'f') && int.TryParse(name[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + fn - 1);
            return true;
        }
        if (Specials.TryGetValue(name, out var special)) { vk = special; return true; }
        return false;
    }

    private static readonly Dictionary<string, ushort> Specials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Tab"] = 0x09, ["Space"] = 0x20, ["Backspace"] = 0x08, ["BS"] = 0x08,
        ["Del"] = 0x2E, ["Delete"] = 0x2E, ["Ins"] = 0x2D, ["Insert"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23, ["PgUp"] = 0x21, ["PageUp"] = 0x21,
        ["PgDn"] = 0x22, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28,
        ["Left"] = 0x25, ["Right"] = 0x27, ["CapsLock"] = 0x14, ["NumLock"] = 0x90,
        ["ScrollLock"] = 0x91, ["PrtSc"] = 0x2C, ["Pause"] = 0x13,
    };

    /// <summary>Keys on the extended half of the keyboard need <c>KEYEVENTF_EXTENDEDKEY</c>, or the
    /// listener sees the numpad twin of the arrow/navigation key instead.</summary>
    private static bool IsExtended(ushort vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
           or 0x2D or 0x2E or 0x90 or 0x5B or 0x5C or 0x2C;

    private static void Send(ushort vk, bool down)
    {
        uint flags = down ? 0 : KEYEVENTF_KEYUP;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---------------------------------------------------------------- WinAPI

    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        /// <summary>Never written: it only pads the union out to the size a MOUSEINPUT would take,
        /// which is what <c>SendInput</c>'s cbSize must match.</summary>
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }
}
