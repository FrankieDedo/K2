// Services/MacroPlayer.cs — riproduce macro registrate
// Usa SendInput (Win32) per simulare keydown/keyup e mouse events.

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

        try
        {
            for (int i = 0; i < iterations && !ct.IsCancellationRequested; i++)
            {
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
                        Thread.Sleep(delay);

                    ExecuteInput(input);
                }
            }
        }
        finally
        {
            PlaybackStopped?.Invoke();
        }
    }

    private static void ExecuteInput(MacroInput input)
    {
        switch (input.Type)
        {
            case "keydown":
                SendKeyInput((ushort)input.Key, false);
                break;
            case "keyup":
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
        input.U.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

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
        // Normalizza a coordinate assolute (0-65535)
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
        SendMouseMove(x, y);
        var input = new INPUT { type = INPUT_MOUSE };
        if (button == 1) // left
            input.U.mi.dwFlags = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN;
        else if (button == 2) // right
            input.U.mi.dwFlags = up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ─────────────────────── Costanti & strutture ───────────────────────

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;
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
}
