using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace K2.Core;

/// <summary>
/// Implementations of "non-trivial" action types that K2 can execute:
/// oscmd, media (media keys), mouse (mouse_event WinAPI),
/// multi (JSON sequence), createfolder, back.
/// The other types (url/exec/folder/browser/command/keys/text/profile) are
/// handled directly by <see cref="ButtonActionEngine"/>.
/// </summary>
public static class ActionExecutor
{
    // ── OS commands ──────────────────────────────────

    public static void RunOsCommand(string cmd, Action<string> log)
    {
        switch (cmd?.Trim().ToLowerInvariant() ?? "")
        {
            case "run task manager":
            case "task manager":
            case "taskmgr":
                Start("taskmgr.exe"); log("[EXEC] oscmd -> taskmgr"); break;
            case "calculator":
            case "calc":
                StartCalculator(log); break;
            case "run explorer":
            case "explorer":
                Start("explorer.exe"); log("[EXEC] oscmd -> explorer"); break;
            case "lock computer":
            case "lock":
                User32.LockWorkStation(); log("[EXEC] oscmd -> lock"); break;
            case "shutdown":
                Start("shutdown.exe", "/s /t 0"); log("[EXEC] oscmd -> shutdown"); break;
            case "restart":
                Start("shutdown.exe", "/r /t 0"); log("[EXEC] oscmd -> restart"); break;
            case "sleep":
                // SetSuspendState(false=sleep, false=don't force, false=no wake event)
                PowrProf.SetSuspendState(false, false, false); log("[EXEC] oscmd -> sleep"); break;
            case "hibernate":
                PowrProf.SetSuspendState(true, false, false); log("[EXEC] oscmd -> hibernate"); break;
            default:
                log($"[EXEC] oscmd: sub-command \"{cmd}\" not handled"); break;
        }
    }

    /// <summary>
    /// Launches the Windows Calculator, working around the fact that K2 runs elevated
    /// (<c>app.manifest</c>, <c>requestedExecutionLevel=requireAdministrator</c>) while
    /// Windows refuses to activate a packaged/Store app from an elevated process. The
    /// <c>System32\calc.exe</c> stub is exactly such an activation: <see cref="Start"/>
    /// returns perfectly happily and no Calculator ever appears — reported 2026-07-27
    /// with "[EXEC] oscmd -> calc" in the log and nothing on screen. Handing the
    /// AppsFolder entry to <c>explorer.exe</c> instead makes the desktop shell open it
    /// at its own (medium) integrity level, which works from an elevated caller.
    /// Non-elevated runs keep the plain stub — a direct launch that surfaces real
    /// failures as exceptions, unlike the explorer hand-off which always "succeeds".
    /// </summary>
    private static void StartCalculator(Action<string> log)
    {
        if (!IsProcessElevated())
        {
            Start("calc.exe");
            log("[EXEC] oscmd -> calc");
            return;
        }

        // Stable AUMID of the inbox Calculator package (verified via Get-StartApps).
        Start("explorer.exe", @"shell:appsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        log("[EXEC] oscmd -> calc (via explorer: K2 is elevated, direct UWP activation is blocked)");
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static void Start(string file, string args = "")
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = true
        });
    }

    // ── Media keys ────────────────────────────────

    public static void SendMediaKey(string key, Action<string> log)
    {
        // Shuffle has no standard VK code, so it goes through the Spotify SMTC
        // session instead of keybd_event (see SpotifyMediaService).
        if (string.Equals(key?.Trim(), "shuffle", StringComparison.OrdinalIgnoreCase))
        {
            _ = Services.SpotifyMediaService.Instance.ToggleShuffleAsync();
            log("[EXEC] media -> shuffle (Spotify)");
            return;
        }

        // Virtual-Key codes (winuser.h)
        const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        const byte VK_MEDIA_PREV_TRACK = 0xB1;
        const byte VK_MEDIA_STOP       = 0xB2;
        const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        const byte VK_VOLUME_MUTE      = 0xAD;
        const byte VK_VOLUME_DOWN      = 0xAE;
        const byte VK_VOLUME_UP        = 0xAF;

        byte vk = key?.Trim().ToLowerInvariant() switch
        {
            "play/pause" or "play-pause" or "playpause" => VK_MEDIA_PLAY_PAUSE,
            "stop"                                       => VK_MEDIA_STOP,
            "previous track" or "prev" or "previous"     => VK_MEDIA_PREV_TRACK,
            "next track" or "next"                       => VK_MEDIA_NEXT_TRACK,
            "volume up" or "vol up" or "volup"           => VK_VOLUME_UP,
            "volume down" or "vol down" or "voldown"     => VK_VOLUME_DOWN,
            "mute"                                       => VK_VOLUME_MUTE,
            _ => (byte)0
        };
        if (vk == 0)
        {
            log($"[EXEC] media: key \"{key}\" not handled");
            return;
        }
        User32.keybd_event(vk, 0, 0, UIntPtr.Zero);
        User32.keybd_event(vk, 0, User32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        log($"[EXEC] media -> {key}");
    }

    // ── Mouse ──────────────────────────────────

    public static void DoMouse(string action, Action<string> log)
    {
        const uint LEFTDOWN = 0x0002, LEFTUP   = 0x0004;
        const uint RIGHTDOWN= 0x0008, RIGHTUP  = 0x0010;
        const uint MIDDLEDOWN=0x0020, MIDDLEUP = 0x0040;
        const uint XDOWN   = 0x0080,  XUP      = 0x0100;
        const uint WHEEL   = 0x0800;
        const uint HWHEEL  = 0x01000;
        const uint XBUTTON1 = 0x0001;
        const uint XBUTTON2 = 0x0002;

        switch (action?.Trim().ToLowerInvariant() ?? "")
        {
            case "left button":   User32.mouse_event(LEFTDOWN, 0,0,0,UIntPtr.Zero);
                                  User32.mouse_event(LEFTUP,   0,0,0,UIntPtr.Zero); break;
            case "right button":  User32.mouse_event(RIGHTDOWN, 0,0,0,UIntPtr.Zero);
                                  User32.mouse_event(RIGHTUP,   0,0,0,UIntPtr.Zero); break;
            case "middle button": User32.mouse_event(MIDDLEDOWN,0,0,0,UIntPtr.Zero);
                                  User32.mouse_event(MIDDLEUP,  0,0,0,UIntPtr.Zero); break;
            case "forward":       User32.mouse_event(XDOWN, 0,0, XBUTTON2, UIntPtr.Zero);
                                  User32.mouse_event(XUP,   0,0, XBUTTON2, UIntPtr.Zero); break;
            case "backward":      User32.mouse_event(XDOWN, 0,0, XBUTTON1, UIntPtr.Zero);
                                  User32.mouse_event(XUP,   0,0, XBUTTON1, UIntPtr.Zero); break;
            case "scroll up":     User32.mouse_event(WHEEL,  0,0,  120, UIntPtr.Zero); break;
            case "scroll down":   User32.mouse_event(WHEEL,  0,0, unchecked((uint)-120), UIntPtr.Zero); break;
            case "scroll right":  User32.mouse_event(HWHEEL, 0,0,  120, UIntPtr.Zero); break;
            case "scroll left":   User32.mouse_event(HWHEEL, 0,0, unchecked((uint)-120), UIntPtr.Zero); break;
            default:
                log($"[EXEC] mouse: action \"{action}\" not handled"); return;
        }
        log($"[EXEC] mouse -> {action}");
    }

    // ── Adobe/DaVinci/Zoom special values ──────────

    /// <summary>Handles the two special non-modifier values real Base Camp uses for
    /// Adobe/DaVinci/Zoom shortcuts: "Alt + click"/"Ctrl + click" (hold the modifier, left-click
    /// the mouse) — <see cref="SendKeysTranslator"/> has no concept of a mouse click, so these
    /// bypass it entirely (mirrors the decompiled reference,
    /// <c>OtherDeviceOperations.CallKeyPressFunctionForOtherDevice</c>'s Adobe/DaVinci/Zoom arm).
    /// "Tab + Shift" is NOT special-cased here — it parses fine as Shift+Tab through the normal
    /// <see cref="SendKeysTranslator"/> path. Returns true if it handled a special value, false
    /// if the caller should fall through to the normal keys/SendKeys.SendWait path.</summary>
    public static bool TryRunAppShortcutSpecial(string value, Action<string> log)
    {
        const byte VK_CONTROL = 0x11, VK_MENU = 0x12;
        const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

        var v = (value ?? "").Trim();
        byte modVk = string.Equals(v, "Alt + click", StringComparison.OrdinalIgnoreCase) ? VK_MENU
            : string.Equals(v, "Ctrl + click", StringComparison.OrdinalIgnoreCase) ? VK_CONTROL
            : (byte)0;
        if (modVk == 0) return false;

        User32.keybd_event(modVk, 0, 0, UIntPtr.Zero);
        User32.mouse_event(LEFTDOWN, 0, 0, 0u, UIntPtr.Zero);
        User32.mouse_event(LEFTUP, 0, 0, 0u, UIntPtr.Zero);
        User32.keybd_event(modVk, 0, User32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        log($"[EXEC] {v} -> modifier+click");
        return true;
    }

    // ── Multi Action ──────────────────────────────

    public static void RunMultiAction(string jsonPayload, Action<string> log,
        Action<string, string> runSubAction)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            log("[EXEC] multi: empty payload"); return;
        }
        List<MultiStep> steps;
        try
        {
            steps = JsonSerializer.Deserialize<List<MultiStep>>(jsonPayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception ex)
        {
            log($"[EXEC] multi: parse fail: {ex.Message}"); return;
        }
        log($"[EXEC] multi: {steps.Count} step");
        foreach (var s in steps)
        {
            var (type, value, _) = MapSubAction(s);
            if (type is null) { log($"[EXEC] multi: step \"{s.FunctionType}\" not handled"); continue; }
            try { runSubAction(type, value ?? ""); }
            catch (Exception ex) { log($"[EXEC] multi: step \"{type}\" error: {ex.Message}"); }
            int delay = Math.Max(s.ActionDelay, 50);
            Thread.Sleep(delay);
        }
    }

    private static (string? Type, string? Value, string? Reason) MapSubAction(MultiStep s)
    {
        // Replicates the mapping from BaseCampProfileImporter.MapActionExt
        switch ((s.FunctionType ?? "").Trim())
        {
            case "Run Program":      return ImportExecOrBrowserAction(s.FunctionValue);
            case "Open Folder":      return ("folder", s.FunctionValue, null);
            case "Run browser":      return ImportBrowserAction();
            case "Adobe":
            case "DaVinci":
            case "Zoom":
            case "Keyboard Shortcuts":
                return ("keys", s.FunctionValue, null);
            case "OS Commands":      return ("oscmd", ActionTypeHelper.NormalizeOsCommand(string.IsNullOrEmpty(s.SubFunctionType) ? s.FunctionValue : s.SubFunctionType), null);
            case "Media":            return ("media", ActionTypeHelper.NormalizeMediaKey(string.IsNullOrEmpty(s.SubFunctionType) ? s.FunctionValue : s.SubFunctionType), null);
            case "Mouse":            return ("mouse", string.IsNullOrEmpty(s.SubFunctionType) ? s.FunctionValue : s.SubFunctionType, null);
            case "Profile":          return ("profile", s.FunctionValue, null);
            default:
                // Steps built natively by K2's own Multi Action editor (ButtonActionDialog.Multi.cs)
                // already store one of ButtonActionEngine.ExecuteSub's own tags in FunctionType
                // (e.g. "url", "keys") instead of a Base Camp label — pass those straight through
                // instead of rejecting them, so native and BC-imported Multi Action data share the
                // exact same execution path with no duplicated translation.
                return NativeSubActionTypes.Contains(s.FunctionType ?? "")
                    ? (s.FunctionType, s.FunctionValue, null)
                    : (null, null, $"FunctionType \"{s.FunctionType}\" not handled");
        }
    }

    /// <summary>The native K2 action tags <see cref="ButtonActionEngine"/>'s <c>ExecuteSub</c>
    /// recognizes directly — see <see cref="MapSubAction"/>'s default arm.</summary>
    private static readonly HashSet<string> NativeSubActionTypes = new(StringComparer.Ordinal)
    {
        "url", "exec", "folder", "browser", "profile", "keys", "text", "emoji", "oscmd", "media", "mouse",
    };

    /// <summary>Same "Run browser" -> native browser action mapping as
    /// BaseCampDbImporter/BaseCampProfileImporter — pre-selects the first detected browser
    /// instead of running with no browser chosen (OS default via ShellExecute).</summary>
    private static (string? Type, string? Value, string? Reason) ImportBrowserAction()
    {
        var installed = BrowserDetector.DetectInstalled();
        var payload = new BrowserActionPayload { Browser = installed.Count > 0 ? installed[0].Id : "other" };
        return ("browser", payload.ToJson(), null);
    }

    /// <summary>Same "Run Program" -> "exec" (or native "browser" if it targets a known browser
    /// executable) mapping as BaseCampDbImporter/BaseCampProfileImporter.</summary>
    private static (string? Type, string? Value, string? Reason) ImportExecOrBrowserAction(string? execPath)
    {
        string? browserId = BrowserDetector.TryIdentifyByExeName(execPath);
        if (browserId is null) return ("exec", execPath, null);

        var payload = new BrowserActionPayload { Browser = browserId };
        return ("browser", payload.ToJson(), null);
    }

    public sealed class MultiStep
    {
        public int    Id { get; set; }
        public string? FunctionType { get; set; }
        public string? SubFunctionType { get; set; }
        public string? FunctionValue { get; set; }
        public string? KeyAlternateName { get; set; }
        public int    KeyPressDelay { get; set; }
        public int    ActionDelay { get; set; }
    }

    // ── Create Folder / Back ──────────────────────────────

    public static void CreateFolderOnDesktop(string name, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(name)) { log("[EXEC] createfolder: empty name"); return; }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        try { Directory.CreateDirectory(dir); log($"[EXEC] createfolder -> {dir}"); }
        catch (Exception ex) { log($"[EXEC] createfolder error: {ex.Message}"); }
    }

    public static void GoBackBrowser(Action<string> log)
    {
        // Alt+Left
        User32.keybd_event(0x12, 0, 0, UIntPtr.Zero);          // Alt down
        User32.keybd_event(0x25, 0, 0, UIntPtr.Zero);          // Left down
        User32.keybd_event(0x25, 0, User32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        User32.keybd_event(0x12, 0, User32.KEYEVENTF_KEYUP, UIntPtr.Zero);
        log("[EXEC] back -> Alt+Left");
    }

    // ── Unicode text injection ─────────────────

    /// <summary>
    /// Types <paramref name="text"/> into the focused window as raw Unicode, one
    /// <c>KEYEVENTF_UNICODE</c> keystroke per UTF-16 code unit — the only way to send an
    /// emoji: <c>SendKeys</c> (what the "keys"/"text" actions use) goes through the keyboard
    /// layout and can't carry a surrogate pair, so a non-BMP character comes out as garbage
    /// or nothing at all. Surrogate pairs need no special handling here beyond being sent as
    /// two consecutive units, which is exactly what Windows expects.
    /// </summary>
    public static void SendUnicodeText(string text, Action<string> log)
    {
        if (string.IsNullOrEmpty(text)) { log("[EXEC] unicode text: empty"); return; }

        var inputs = new List<User32.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(UnicodeKey(c, down: true));
            inputs.Add(UnicodeKey(c, down: false));
        }

        uint sent = User32.SendInput((uint)inputs.Count, inputs.ToArray(),
                                     Marshal.SizeOf<User32.INPUT>());
        if (sent != inputs.Count)
            log($"[EXEC] unicode text: SendInput sent {sent}/{inputs.Count} (err {Marshal.GetLastWin32Error()})");
        else
            log($"[EXEC] unicode text -> \"{text}\"");
    }

    private static User32.INPUT UnicodeKey(char c, bool down) => new()
    {
        type = User32.INPUT_KEYBOARD,
        u = new User32.InputUnion
        {
            ki = new User32.KEYBDINPUT
            {
                wVk         = 0,          // must be 0 for KEYEVENTF_UNICODE
                wScan       = c,
                dwFlags     = User32.KEYEVENTF_UNICODE | (down ? 0 : User32.KEYEVENTF_KEYUP),
                time        = 0,
                dwExtraInfo = UIntPtr.Zero,
            }
        }
    };

    // ── WinAPI ─────────────────────────────────

    private static class User32
    {
        public const uint KEYEVENTF_KEYUP   = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint INPUT_KEYBOARD    = 1;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT    mi;
            [FieldOffset(0)] public KEYBDINPUT    ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }
    }

    private static class PowrProf
    {
        [DllImport("powrprof.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
