// Services/MacroRecorder.cs — records key sequences via a global hook
// Uses SetWindowsHookEx(WH_KEYBOARD_LL) to capture all keydown/keyup events.
// Mouse recording is optional (WH_MOUSE_LL) — enabled via RecordMouse.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using K2.App.Models;

namespace K2.App.Services;

public sealed class MacroRecorder : IDisposable
{
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly List<MacroInput> _inputs = new();
    private readonly Stopwatch _sw = new();
    private bool _recording;
    private bool _recordMouse;
    private bool _recordMouseMovement;
    private IntPtr _ownerHwnd;

    // Win32 hook delegates (must stay alive to prevent GC)
    private readonly LowLevelKeyboardProc _kbProc;
    private readonly LowLevelMouseProc _mouseProc;

    public bool IsRecording => _recording;
    public IReadOnlyList<MacroInput> Inputs => _inputs;

    public event Action<MacroInput>? InputRecorded;

    public MacroRecorder()
    {
        _kbProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    /// <summary>K2's own main window handle — clicks landing inside its
    /// bounds (e.g. the "Stop" button) are excluded from the recording.
    /// Call before <see cref="Start"/> with the caller's up-to-date HWND.</summary>
    public void SetOwnerWindow(IntPtr hwnd) => _ownerHwnd = hwnd;

    public void Start(bool recordMouse = false, bool recordKeyboard = true, bool recordMouseMovement = false)
    {
        if (_recording) return;
        _recordMouse = recordMouse;
        _recordMouseMovement = recordMouseMovement;
        _inputs.Clear();
        _sw.Restart();

        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule!;

        if (recordKeyboard)
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc,
                GetModuleHandle(mod.ModuleName), 0);
        }

        if (recordMouse)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc,
                GetModuleHandle(mod.ModuleName), 0);
        }

        _recording = true;
    }

    public List<MacroInput> Stop()
    {
        _recording = false;
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _sw.Stop();
        return new List<MacroInput>(_inputs);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _recording)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // AltGr on ISO/international keyboard layouts is delivered by
            // Windows as a synthetic Left-Ctrl keydown/keyup immediately
            // around the real Right-Alt one — a driver-level artifact, not
            // an actual keystroke. Windows tags it with this specific scan
            // code so it can be told apart; recording it verbatim turns a
            // single AltGr press into a bogus "Ctrl+AltGr" combo on playback.
            bool isFakeAltGrCtrl = hookStruct.vkCode == VK_LCONTROL
                && hookStruct.scanCode == ALTGR_FAKE_LCONTROL_SCANCODE;

            if (!isFakeAltGrCtrl)
            {
                int vkCode = hookStruct.vkCode;
                int msg = (int)wParam;
                string type = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) ? "keydown" : "keyup";

                var input = new MacroInput
                {
                    Type = type,
                    Key = vkCode,
                    DelayMs = (int)_sw.ElapsedMilliseconds
                };
                _sw.Restart();
                _inputs.Add(input);
                InputRecorded?.Invoke(input);
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _recording && _recordMouse)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            string? type = msg switch
            {
                WM_LBUTTONDOWN => "mousedown",
                WM_LBUTTONUP   => "mouseup",
                WM_RBUTTONDOWN => "mousedown",
                WM_RBUTTONUP   => "mouseup",
                WM_MOUSEMOVE   => _recordMouseMovement ? "mousemove" : null,
                _ => null
            };
            // Clicks on K2's own window (e.g. the "Stop" button that ends the
            // recording) must not end up inside the macro — only capture
            // clicks aimed at other applications.
            if (type != null && IsOverOwnWindow(hookStruct.pt.x, hookStruct.pt.y))
                type = null;
            if (type != null)
            {
                int button = msg switch
                {
                    WM_LBUTTONDOWN or WM_LBUTTONUP => 1,  // left
                    WM_RBUTTONDOWN or WM_RBUTTONUP => 2,  // right
                    _ => 0
                };
                var input = new MacroInput
                {
                    Type = type,
                    Key = button,
                    X = hookStruct.pt.x,
                    Y = hookStruct.pt.y,
                    DelayMs = (int)_sw.ElapsedMilliseconds
                };
                _sw.Restart();
                _inputs.Add(input);
                InputRecorded?.Invoke(input);
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    /// <summary>True if the given screen point belongs to a window owned by
    /// this process (i.e. K2's own UI) — used to keep clicks on the recorder
    /// panel itself (Stop button included) out of the recorded macro.
    /// Checks two ways and ORs them: a direct bounding-rect test against
    /// <see cref="_ownerHwnd"/> (reliable even for a custom-chrome/layered
    /// WPF window, where Z-order hit-testing via WindowFromPoint can miss)
    /// plus a WindowFromPoint + owning-process check as a second opinion.</summary>
    private bool IsOverOwnWindow(int x, int y)
    {
        if (_ownerHwnd != IntPtr.Zero &&
            GetWindowRect(_ownerHwnd, out RECT r) &&
            x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
        {
            return true;
        }

        IntPtr hwnd = WindowFromPoint(new POINT { x = x, y = y });
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ─────────────────────── Win32 ───────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int VK_LCONTROL    = 0xA2;
    private const int ALTGR_FAKE_LCONTROL_SCANCODE = 0x21D;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
