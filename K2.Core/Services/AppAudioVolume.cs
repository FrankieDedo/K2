using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace K2.Core.Services;

/// <summary>
/// Per-application volume, the thing Windows' own Volume Mixer shows: the Core Audio
/// <c>ISimpleAudioVolume</c> of every audio session owned by a given process (Spotify.exe).
///
/// Why this exists: the Spotify dedicated profile's Volume/Mute keys used to have exactly two
/// implementations, and neither one touched Spotify alone —
/// <list type="bullet">
/// <item><b>Source = Web API</b> drives <c>me/player/volume</c> (<see cref="SpotifyBridge"/>),
///   which needs Premium AND an active Connect device.</item>
/// <item><b>Source = Local</b> sent plain system media keys, i.e. the MASTER volume: every other
///   app on the machine moved with it.</item>
/// </list>
/// The audio session is the missing third option: no account, no Premium, no Connect device —
/// just the running desktop app — and it moves Spotify's slider only (user request 2026-09-08).
/// Written against the same hand-rolled Core Audio interop style as
/// <c>K2.App/Services/SystemMonitor.cs</c>'s endpoint-volume code (no NAudio dependency).
///
/// Everything here is best-effort: with the app closed, muted by policy, or simply not rendering
/// audio yet there is no session at all, and every call returns false / null so the caller can
/// fall back (ButtonActionEngine falls back to the system media key).
/// </summary>
public static class AppAudioVolume
{
    /// <summary>Default step for one key press, as a fraction of full scale.</summary>
    public const float DefaultStep = 0.05f;

    /// <summary>Adds <paramref name="delta"/> (fraction of full scale, may be negative) to every
    /// audio session of <paramref name="processName"/>, clamped to 0..1. Un-mutes on the way up:
    /// raising the volume of a muted app is otherwise a no-op the user can't see.</summary>
    /// <returns>True when at least one session was found and changed.</returns>
    public static bool TryStep(string processName, float delta, Action<string>? log = null)
        => ForEachSession(processName, vol =>
        {
            if (vol.GetMasterVolume(out float current) != 0) return false;
            float target = Math.Clamp(current + delta, 0f, 1f);
            var ctx = Guid.Empty;
            if (vol.SetMasterVolume(target, ref ctx) != 0) return false;
            if (delta > 0) vol.SetMute(false, ref ctx);
            return true;
        }, log, $"step {delta:+0.00;-0.00}");

    /// <summary>Mutes/un-mutes every audio session of <paramref name="processName"/>. The new
    /// state is the opposite of the FIRST session's, so several sessions can't end up split.</summary>
    public static bool TryToggleMute(string processName, Action<string>? log = null)
    {
        bool? target = null;
        return ForEachSession(processName, vol =>
        {
            if (target is null)
            {
                if (vol.GetMute(out bool muted) != 0) return false;
                target = !muted;
            }
            var ctx = Guid.Empty;
            return vol.SetMute(target.Value, ref ctx) == 0;
        }, log, "mute toggle");
    }

    /// <summary>Current volume (0..1) of the process's first audio session, or null when it has
    /// none — the caller decides what "no session" means (usually: fall back).</summary>
    public static float? TryGetVolume(string processName)
    {
        float? result = null;
        ForEachSession(processName, vol =>
        {
            if (result is not null) return true;                 // first session wins
            if (vol.GetMasterVolume(out float v) != 0) return false;
            result = v;
            return true;
        }, null, "read");
        return result;
    }

    /// <summary>Mute state of the process's first audio session, or null when it has none.</summary>
    public static bool? TryGetMute(string processName)
    {
        bool? result = null;
        ForEachSession(processName, vol =>
        {
            if (result is not null) return true;
            if (vol.GetMute(out bool m) != 0) return false;
            result = m;
            return true;
        }, null, "read");
        return result;
    }

    /// <summary>True while <paramref name="processName"/> owns at least one audio session on the
    /// default render endpoint — i.e. while this class can do anything at all for it.</summary>
    public static bool HasSession(string processName) => TryGetVolume(processName) is not null;

    // ────────────────────────────── enumeration ──────────────────────────────

    /// <summary>Runs <paramref name="action"/> against the <c>ISimpleAudioVolume</c> of every
    /// session belonging to <paramref name="processName"/> (compared without the ".exe", case
    /// insensitively). Returns true if at least one session accepted it.</summary>
    private static bool ForEachSession(string processName, Func<ISimpleAudioVolume, bool> action,
                                       Action<string>? log, string what)
    {
        string want = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName;

        int hits = 0;
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(typeof(MMDeviceEnumerator).GUID, throwOnError: true)!)!;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != 0
                || device is null)
                return false;

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object mgrObj) != 0
                || mgrObj is not IAudioSessionManager2 mgr)
                return false;

            if (mgr.GetSessionEnumerator(out var sessions) != 0 || sessions is null) return false;
            if (sessions.GetCount(out int count) != 0) return false;

            // One Process lookup per distinct pid: a busy machine can have dozens of sessions and
            // Process.GetProcessById is not cheap.
            var names = new Dictionary<int, string>();
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var ctl) != 0 || ctl is null) continue;
                if (ctl is not IAudioSessionControl2 ctl2) continue;
                if (ctl2.GetProcessId(out int pid) != 0 || pid == 0) continue;
                if (!names.TryGetValue(pid, out string? name))
                {
                    try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
                    catch { name = ""; }
                    names[pid] = name;
                }
                if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) continue;
                if (ctl is not ISimpleAudioVolume vol) continue;
                if (action(vol)) hits++;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[AppVolume] {want}: {what} failed: {ex.Message}");
            return false;
        }

        if (hits == 0) log?.Invoke($"[AppVolume] {want}: no audio session ({what} skipped)");
        return hits > 0;
    }

    // ────────────────────────────── Core Audio interop ──────────────────────────────

    private const int DataFlowRender = 0;    // eRender
    private const int RoleMultimedia = 1;    // eMultimedia
    private const int ClsCtxAll = 23;        // INPROC | INPROC_HANDLER | LOCAL | REMOTE

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // IAudioSessionManager's own two methods come first (vtable order), then the "2" additions.
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        // Remaining notification methods are unused — declared so the vtable stays complete for
        // anything that might be added later, as IntPtr to avoid dragging in more interfaces.
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification(IntPtr sessionId, IntPtr duckNotification);
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionCount, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    // IAudioSessionControl's 9 methods, then IAudioSessionControl2's 5. Only GetProcessId is
    // called; the rest are IntPtr placeholders purely to keep the vtable offsets right.
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid overrideValue, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr newNotifications);
        int UnregisterAudioSessionNotification(IntPtr newNotifications);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetProcessId(out int retVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
    }
}
