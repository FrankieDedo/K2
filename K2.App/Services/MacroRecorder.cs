// Services/MacroRecorder.cs — records key sequences via global low-level hooks
// (WH_KEYBOARD_LL / optional WH_MOUSE_LL).
//
// The hooks run on a DEDICATED background thread with its own Win32 message
// loop — never on the WPF UI thread. A low-level hook callback is invoked on
// the thread that installed the hook, and that thread must service it within
// LowLevelHooksTimeout (~300 ms) or Windows silently skips (and eventually
// drops) the hook. If the hook lives on the UI thread, then whenever K2 itself
// has keyboard focus the UI thread is busy in WPF's own input pipeline and the
// callback is starved — recording captures nothing while K2 is focused, works
// only once focus moves away. Its own pump thread has nothing else to do, so
// the callback is always serviced in time.
//
// The callback stays cheap: it appends to _inputs (under a lock) and posts the
// InputRecorded event to the UI thread asynchronously.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using K2.App.Models;

namespace K2.App.Services;

public sealed class MacroRecorder : IDisposable
{
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly List<MacroInput> _inputs = new();
    private readonly object _inputsLock = new();
    private readonly Stopwatch _sw = new();
    private volatile bool _recording;
    private volatile bool _recordMouse;
    private volatile bool _recordMouseMovement;
    private volatile bool _recordKeyboard;
    private IntPtr _ownerHwnd;

    // Win32 hook delegates (must stay alive to prevent GC).
    private readonly LowLevelKeyboardProc _kbProc;
    private readonly LowLevelMouseProc _mouseProc;

    // Marshals InputRecorded back to whichever thread built this instance
    // (the WPF UI thread). Captured in the ctor.
    private readonly SynchronizationContext? _uiCtx;

    // Dedicated hook-pump thread.
    private Thread? _hookThread;
    private volatile uint _hookThreadId;   // written by the pump thread, read by Stop()
    private readonly ManualResetEventSlim _hookReady = new(false);

    private long _kbRawHits;
    private long _mouseRawHits;
    private bool _kbCallbackSeen;

    private readonly object _swLock = new();

    // Cross-path de-dupe: the same key press can reach us twice, once from the
    // global WH_KEYBOARD_LL hook and once from InjectKey (the Everest NKRO path),
    // whenever the keyboard happens to emit BOTH. Only a repeat from the *other*
    // source counts as a duplicate — two events from the same source are always
    // two real events, so key auto-repeat and fast double-taps survive intact.
    private readonly object _dedupeLock = new();
    private int _lastVk = -1;
    private bool _lastDown;
    private KeySource _lastSource = KeySource.None;
    private long _lastKeyAt;

    private enum KeySource { None, Hook, Injected }

    private int NextDelayMs()
    {
        lock (_swLock) { int ms = (int)_sw.ElapsedMilliseconds; _sw.Restart(); return ms; }
    }

    private bool IsDuplicateKey(int vk, bool down, KeySource source)
    {
        long now = Environment.TickCount64;
        lock (_dedupeLock)
        {
            bool dup = _lastVk == vk
                       && _lastDown == down
                       && _lastSource != source          // same source => a real repeat
                       && _lastSource != KeySource.None
                       && now - _lastKeyAt < 30;
            _lastVk = vk; _lastDown = down; _lastSource = source; _lastKeyAt = now;
            return dup;
        }
    }

    /// <summary>Feed a key transition that did NOT come through the global hook
    /// (currently: the Everest keyboard, which stops emitting standard keyboard
    /// input while K2 is the foreground window). <paramref name="vk"/> is a
    /// Win32 virtual-key code. No-op unless a keyboard recording is running.</summary>
    public void InjectKey(int vk, bool down)
    {
        if (vk == 0) return;
        // A single-key re-record is armed: the Everest is exactly the case that
        // needs this path, since it stops emitting standard keyboard input
        // while K2 is focused — and the capture is always armed from a click
        // inside K2, so its WH_KEYBOARD_LL hook alone would never see the key.
        if (_capturing)
        {
            if (down) CompleteSingleKeyCapture(vk);
            return;
        }
        if (!_recording || !_recordKeyboard) return;
        if (IsDuplicateKey(vk, down, KeySource.Injected)) return;
        var input = new MacroInput
        {
            Type = down ? "keydown" : "keyup",
            Key = vk,
            DelayMs = NextDelayMs()
        };
        Emit(input);
    }

    public bool IsRecording => _recording;

    /// <summary>Snapshot of what has been captured so far — safe to read from
    /// the UI thread while the hook thread keeps appending.</summary>
    public IReadOnlyList<MacroInput> Inputs
    {
        get { lock (_inputsLock) return _inputs.ToArray(); }
    }

    public event Action<MacroInput>? InputRecorded;

    public MacroRecorder()
    {
        _kbProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        _captureProc = CaptureHookCallback;
        _uiCtx = SynchronizationContext.Current;
    }

    /// <summary>K2's own main window handle — clicks landing inside its
    /// bounds (e.g. the "Stop" button) are excluded from the recording.
    /// Call before <see cref="Start"/> with the caller's up-to-date HWND.</summary>
    public void SetOwnerWindow(IntPtr hwnd) => _ownerHwnd = hwnd;

    public void Start(bool recordMouse = false, bool recordKeyboard = true, bool recordMouseMovement = false)
    {
        if (_recording) return;
        _recordMouse = recordMouse;
        _recordKeyboard = recordKeyboard;
        _recordMouseMovement = recordMouseMovement;
        lock (_inputsLock) _inputs.Clear();
        _kbCallbackSeen = false;
        _kbRawHits = 0;
        _mouseRawHits = 0;
        _lastNotifyAt = 0;
        lock (_swLock) _sw.Restart();
        _recording = true;

        _hookReady.Reset();
        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "K2 MacroRecorder hooks"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        // Wait until the hooks are actually installed (or the attempt failed).
        _hookReady.Wait(3000);
    }

    public List<MacroInput> Stop()
    {
        _recording = false;

        // Ask the pump thread to unhook and exit, then wait for it.
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread?.Join(3000);
        _hookThread = null;
        _hookThreadId = 0;

        lock (_swLock) _sw.Stop();

        // The one and only mouse event to discard is the click that ENDS the
        // recording (the Stop button). Nothing is trimmed from the head: Stop is
        // reached from a WPF Click handler, which fires on mouse-UP, so by the
        // time Start() installed the hook the starting click was already over
        // and was never captured in the first place. An earlier version trimmed
        // the head too and ate the user's own first click when it happened to
        // land on K2's window.
        //
        // The stop click is pinned down exactly: it is the trailing run of mouse
        // button events that (a) landed on a K2 window — decided per event at
        // capture time with a pixel-accurate hit test — and (b) sit at the
        // cursor's position right now, since that is where the user just clicked
        // Stop. A legitimate earlier click on K2 somewhere else does not match.
        // The cap of one click (down + up) is a final backstop.
        GetCursorPos(out POINT stopAt);

        int trimmed, count;
        lock (_inputsLock)
        {
            int before = _inputs.Count;

            for (int n = 0; n < MaxOwnClickEvents && _inputs.Count > 0; n++)
            {
                var last = _inputs[^1];
                if (!IsMouseButton(last) || !last.OnOwnWindow) break;
                if (Math.Abs(last.X - stopAt.x) > StopClickSlopPx ||
                    Math.Abs(last.Y - stopAt.y) > StopClickSlopPx) break;
                _inputs.RemoveAt(_inputs.Count - 1);
            }

            count = _inputs.Count;
            trimmed = before - count;
        }

        App.WriteLog($"[MacroRecorder] Stop -> kbRawHits={_kbRawHits} mouseRawHits={_mouseRawHits} " +
                     $"captured={count} (trimmed {trimmed} stop-click event(s) at {stopAt.x},{stopAt.y})");
        lock (_inputsLock) return new List<MacroInput>(_inputs);
    }

    /// <summary>One click = one down + one up. Never trim more than that.</summary>
    private const int MaxOwnClickEvents = 2;

    /// <summary>How far a recorded click may sit from the cursor's position at
    /// Stop() and still count as "the click that pressed Stop" (the pointer can
    /// drift a pixel or two between the button-down and the handler running).</summary>
    private const int StopClickSlopPx = 4;

    private static bool IsMouseButton(MacroInput e) => e.Type is "mousedown" or "mouseup";

    /// <summary>Pixel-accurate "did this land on a K2 window?" test, run once per
    /// mouse button event (never on moves — it must stay cheap on the hook
    /// thread). Unlike a bounding-rect test it cannot false-positive when K2 is
    /// maximised: a click on another app's window on top of K2 resolves to that
    /// app's HWND, not K2's.</summary>
    private bool HitTestOwnWindow(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { x = x, y = y });
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == _ownerHwnd) return true;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ─────────────────────── Dedicated hook pump ───────────────────────

    private void HookThreadProc()
    {
        _hookThreadId = GetCurrentThreadId();

        IntPtr hMod = GetModuleHandle(null);

        if (_recordKeyboard)
        {
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                App.WriteLog($"[MacroRecorder] keyboard SetWindowsHookEx failed err={err}");
            }
        }
        if (_recordMouse)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                App.WriteLog($"[MacroRecorder] mouse SetWindowsHookEx failed err={err}");
            }
        }

        bool elevated = false;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            elevated = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { /* ignore */ }

        App.WriteLog($"[MacroRecorder] Start kbd={_recordKeyboard} mouse={_recordMouse} " +
                     $"move={_recordMouseMovement} -> kbdHook=0x{_keyboardHook.ToInt64():X} " +
                     $"mouseHook=0x{_mouseHook.ToInt64():X} pumpTid={_hookThreadId} elevated={elevated}");

        _hookReady.Set();

        // Pump until Stop() posts WM_QUIT to this thread.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

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
    }

    private long _lastNotifyAt;

    private void Emit(MacroInput input)
    {
        lock (_inputsLock) _inputs.Add(input);

        var handler = InputRecorded;
        if (handler is null) return;
        if (_uiCtx is null) { handler(input); return; }

        // Coalesce UI notifications: a mouse-move recording fires this callback
        // hundreds of times a second — posting one dispatcher item each would
        // flood the UI thread and freeze the app. The handler resyncs the whole
        // preview from the Inputs snapshot, so dropping intermediate notifies is
        // harmless; Stop() does a final rebuild regardless.
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastNotifyAt) < 75) return;
        Interlocked.Exchange(ref _lastNotifyAt, now);
        _uiCtx.Post(_ => handler(input), null);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Increment(ref _kbRawHits);
        if (nCode >= 0 && _recording)
        {
            if (!_kbCallbackSeen)
            {
                _kbCallbackSeen = true;
                App.WriteLog($"[MacroRecorder] keyboard hook callback alive (wParam=0x{((int)wParam):X})");
            }
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
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                if (!IsDuplicateKey(vkCode, down, KeySource.Hook))
                {
                    Emit(new MacroInput
                    {
                        Type = down ? "keydown" : "keyup",
                        Key = vkCode,
                        DelayMs = NextDelayMs()
                    });
                }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Increment(ref _mouseRawHits);
        if (nCode >= 0 && _recording && _recordMouse)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            string? type = msg switch
            {
                WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN => "mousedown",
                WM_LBUTTONUP   or WM_RBUTTONUP   or WM_MBUTTONUP   or WM_XBUTTONUP   => "mouseup",
                WM_MOUSEWHEEL  => "mousewheel",
                WM_MOUSEMOVE   => _recordMouseMovement ? "mousemove" : null,
                _ => null
            };
            // NB: no per-event "is this on K2's window" test here — it must be
            // cheap enough to run on every mouse-move. The only clicks that need
            // dropping are the ones that start/stop the recording, and Stop()
            // trims those from the head/tail afterwards.

            if (type != null)
            {
                // MSLLHOOKSTRUCT.mouseData carries the X-button id (XBUTTON1/2)
                // and the wheel delta in its HIGH word — the delta is signed
                // (±WHEEL_DELTA multiples), so it must be read as a short.
                short hiWord = (short)((hookStruct.mouseData >> 16) & 0xFFFF);

                int key = msg switch
                {
                    WM_LBUTTONDOWN or WM_LBUTTONUP => MouseLeft,
                    WM_RBUTTONDOWN or WM_RBUTTONUP => MouseRight,
                    WM_MBUTTONDOWN or WM_MBUTTONUP => MouseMiddle,      // wheel click
                    WM_XBUTTONDOWN or WM_XBUTTONUP => hiWord == XBUTTON2 ? MouseX2 : MouseX1,
                    WM_MOUSEWHEEL                  => hiWord,           // scroll delta
                    _ => 0
                };
                bool isButton = type is "mousedown" or "mouseup";

                Emit(new MacroInput
                {
                    Type = type,
                    Key = key,
                    X = hookStruct.pt.x,
                    Y = hookStruct.pt.y,
                    DelayMs = NextDelayMs(),
                    // Only meaningful for buttons — Stop() trims at most one
                    // click off each end using this. Skipped for moves/wheel so
                    // the hook callback stays cheap under a movement recording.
                    OnOwnWindow = isButton && HitTestOwnWindow(hookStruct.pt.x, hookStruct.pt.y)
                });
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        CancelCaptureSingleKey();
        if (_recording) Stop();
        _hookReady.Dispose();
        _captureReady.Dispose();
    }

    // ─────────────────────── One-shot single-key capture ───────────────────────
    // Re-records ONE macro row's key: install a temporary WH_KEYBOARD_LL hook,
    // grab the first real key-down's VK code, unhook, hand it back. The captured
    // keystroke is swallowed so it doesn't leak into whatever app has focus.
    // Independent of Start/Stop — never used while a full recording runs. Runs on
    // its own pump thread for the same reason the main hooks do: it is armed
    // from a button click while K2 has focus, the exact case a UI-thread hook
    // would be starved in.

    private IntPtr _captureHook;
    private readonly LowLevelKeyboardProc _captureProc;
    private Action<int>? _captureCallback;
    private Thread? _captureThread;
    private uint _captureThreadId;
    private readonly ManualResetEventSlim _captureReady = new(false);
    private volatile bool _capturing;

    public bool IsCapturingSingleKey => _capturing;

    /// <summary>Begins capturing the next key-down. <paramref name="onCaptured"/>
    /// is invoked with its VK code on the hook thread (the caller marshals to the
    /// UI thread). Returns false if a recording or another capture is in progress.</summary>
    public bool BeginCaptureSingleKey(Action<int> onCaptured)
    {
        if (_recording || _capturing) return false;
        _captureCallback = onCaptured;
        _capturing = true;

        _captureReady.Reset();
        _captureThread = new Thread(CaptureThreadProc)
        {
            IsBackground = true,
            Name = "K2 MacroRecorder key-capture"
        };
        _captureThread.SetApartmentState(ApartmentState.STA);
        _captureThread.Start();
        _captureReady.Wait(3000);

        if (_captureHook == IntPtr.Zero)
        {
            _capturing = false;
            _captureCallback = null;
            return false;
        }
        return true;
    }

    /// <summary>Delivers the captured VK to the armed callback and tears the
    /// capture pump down. Reached from either source — the temporary
    /// WH_KEYBOARD_LL hook (on its own pump thread) or
    /// <see cref="InjectKey"/> (the Everest NKRO path, on the UI thread).
    /// <see cref="_capturing"/> is cleared first, so whichever source wins the
    /// race the other one is a no-op.</summary>
    private void CompleteSingleKeyCapture(int vk)
    {
        var cb = _captureCallback;
        if (!_capturing || cb is null) return;
        _capturing = false;
        _captureCallback = null;
        // Tear the pump down from outside the hook callback.
        if (_captureThreadId != 0)
            PostThreadMessage(_captureThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_uiCtx != null) _uiCtx.Post(_ => cb(vk), null);
        else cb(vk);
    }

    public void CancelCaptureSingleKey()
    {
        if (_captureThreadId != 0)
            PostThreadMessage(_captureThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _captureThread?.Join(3000);
        _captureThread = null;
        _captureThreadId = 0;
        _capturing = false;
        _captureCallback = null;
    }

    private void CaptureThreadProc()
    {
        _captureThreadId = GetCurrentThreadId();
        _captureHook = SetWindowsHookEx(WH_KEYBOARD_LL, _captureProc, GetModuleHandle(null), 0);
        _captureReady.Set();
        if (_captureHook == IntPtr.Zero) return;

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_captureHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_captureHook);
            _captureHook = IntPtr.Zero;
        }
    }

    private IntPtr CaptureHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _capturing && _captureHook != IntPtr.Zero)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var hs = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool isFakeAltGrCtrl = hs.vkCode == VK_LCONTROL
                    && hs.scanCode == ALTGR_FAKE_LCONTROL_SCANCODE;
                if (!isFakeAltGrCtrl)
                {
                    CompleteSingleKeyCapture(hs.vkCode);
                    return (IntPtr)1; // swallow — don't leak the keystroke
                }
            }
        }
        return CallNextHookEx(_captureHook, nCode, wParam, lParam);
    }

    // ─────────────────────── Win32 ───────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;   // wheel click
    private const int WM_MBUTTONUP   = 0x0208;
    private const int WM_MOUSEWHEEL  = 0x020A;   // wheel scroll
    private const int WM_XBUTTONDOWN = 0x020B;   // side buttons
    private const int WM_XBUTTONUP   = 0x020C;
    private const int XBUTTON2       = 0x0002;

    // MacroInput.Key values for "mousedown"/"mouseup". 1 and 2 are historical
    // (persisted in existing macros and in BaseCamp imports) — only append here.
    internal const int MouseLeft   = 1;
    internal const int MouseRight  = 2;
    internal const int MouseMiddle = 3;
    internal const int MouseX1     = 4;
    internal const int MouseX2     = 5;
    private const int WM_QUIT        = 0x0012;
    private const int VK_LCONTROL    = 0xA2;
    private const int ALTGR_FAKE_LCONTROL_SCANCODE = 0x21D;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }


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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    // The callback parameter MUST be typed as the concrete delegate, not
    // System.Delegate — see the file header note about the pump thread; a
    // System.Delegate parameter also builds a per-call thunk whose lifetime is
    // not tied to the managed delegate field.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
