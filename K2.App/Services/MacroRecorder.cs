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

    public void Start(bool recordMouse = false)
    {
        if (_recording) return;
        _recordMouse = recordMouse;
        _inputs.Clear();
        _sw.Restart();

        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule!;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc,
            GetModuleHandle(mod.ModuleName), 0);

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
            int vkCode = Marshal.ReadInt32(lParam);
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
                WM_MOUSEMOVE   => "mousemove",
                _ => null
            };
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

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

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
}
