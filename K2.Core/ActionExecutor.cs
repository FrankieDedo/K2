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
                Start("calc.exe"); log("[EXEC] oscmd -> calc"); break;
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
            case "OS Commands":      return ("oscmd", string.IsNullOrEmpty(s.SubFunctionType) ? s.FunctionValue : s.SubFunctionType, null);
            case "Media":            return ("media", string.IsNullOrEmpty(s.SubFunctionType) ? s.FunctionValue : s.SubFunctionType, null);
            case "Mouse":            return ("mouse", string.IsNullOrEmpty(s.SubFunctionType) ? s.FunctionValue : s.SubFunctionType, null);
            case "Profile":          return ("profile", s.FunctionValue, null);
            default:                 return (null, null, $"FunctionType \"{s.FunctionType}\" not handled");
        }
    }

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

    // ── WinAPI ─────────────────────────────────

    private static class User32
    {
        public const uint KEYEVENTF_KEYUP = 0x0002;
        [DllImport("user32.dll", SetLastError = true)]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();
    }

    private static class PowrProf
    {
        [DllImport("powrprof.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
