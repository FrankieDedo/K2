using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace K2.Core.Services;

/// <summary>
/// Shared singleton that polls running processes (same name-matching approach as
/// K2.App's BaseCampProcessGuard) plus the foreground window, and drives a device's
/// profile from the linked application's state — used to auto-switch a device's profile
/// when the user launches / focuses an app it's linked to (see each device's
/// XxShowProfileGear/ProfileSettingsDialog for how a profile gets linked, and each
/// device's XxRefreshProfiles for registration).
///
/// Three behaviours per registration, chosen from the gear popup:
///   • launch-switch (default): the instant the linked exe starts running, switch to
///     its profile.
///   • + restore-on-close: when that exe later exits, switch back to whatever profile
///     was active before — but only if we're still sitting on the app's profile (the
///     user may have changed it manually meanwhile).
///   • focus-only: the app's profile is active *only* while that exe owns the foreground
///     window; losing focus restores the previous profile (same "only if still on the
///     app's profile" guard). Supersedes the two launch behaviours for that registration.
///
/// One instance, one DispatcherTimer, for the whole process — mirrors
/// BacklightIdleTimer's per-purpose-timer pattern but shared rather than per-device,
/// since polling Process.GetProcesses() once for all registrations is cheaper than
/// once per device.
/// </summary>
public sealed class ProfileLaunchWatcher
{
    public static ProfileLaunchWatcher Instance { get; } = new();

    private sealed class Reg
    {
        public required string ExeName;
        public required bool FocusOnly;
        public required bool RestoreOnClose;
        public required string TargetProfile;
        public required Func<string?> GetCurrentProfile;
        public required Action<string> SwitchToProfile;

        // Live state, carried across UpdateRegistration so an unrelated refresh
        // (renaming another profile, a tab activation) neither re-triggers a switch
        // for an app that was already running nor forgets the profile to restore.
        public bool WasRunning;
        public bool WasForeground;
        public bool Active;          // we performed an auto-switch we may still undo
        public string? PrevProfile;  // profile to restore when deactivating
    }

    private readonly Dictionary<string, Reg> _regs = new();
    private readonly DispatcherTimer _timer;

    private ProfileLaunchWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>Registers/updates the executable linked to a given key (one key per
    /// device-profile-slot, e.g. "Dp:3:2" for device 3 slot 2 — see each device's
    /// XxRefreshProfiles). A null/blank <paramref name="exePath"/> removes the
    /// registration.</summary>
    /// <param name="focusOnly">profile is active only while the exe owns the foreground
    /// window; losing focus restores the previous profile.</param>
    /// <param name="restoreOnClose">(launch mode only) restore the previous profile when
    /// the exe exits, if still on the app's profile.</param>
    /// <param name="targetProfile">token identifying this registration's profile, as
    /// understood by <paramref name="switchToProfile"/> / returned by
    /// <paramref name="getCurrentProfile"/> (each device uses its slot number as string).</param>
    /// <param name="getCurrentProfile">the device's currently-active profile token.</param>
    /// <param name="switchToProfile">switch the device to the given profile token.</param>
    public void UpdateRegistration(string key, string? exePath,
        bool focusOnly, bool restoreOnClose, string targetProfile,
        Func<string?> getCurrentProfile, Action<string> switchToProfile)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _regs.Remove(key);
            return;
        }
        string exeName = Path.GetFileNameWithoutExtension(exePath);
        _regs.TryGetValue(key, out var existing);
        _regs[key] = new Reg
        {
            ExeName = exeName,
            FocusOnly = focusOnly,
            RestoreOnClose = restoreOnClose,
            TargetProfile = targetProfile,
            GetCurrentProfile = getCurrentProfile,
            SwitchToProfile = switchToProfile,
            WasRunning = existing?.WasRunning ?? false,
            WasForeground = existing?.WasForeground ?? false,
            Active = existing?.Active ?? false,
            PrevProfile = existing?.PrevProfile,
        };
    }

    public void RemoveRegistration(string key) => _regs.Remove(key);

    /// <summary>All currently-registered keys starting with <paramref name="prefix"/> —
    /// used by each device's XxRefreshProfiles to find and remove stale registrations
    /// (deleted profiles, or profiles whose link was cleared) after re-adding the current
    /// set via <see cref="UpdateRegistration"/>.</summary>
    public IEnumerable<string> KeysWithPrefix(string prefix) =>
        _regs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    private static string? ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(h, out int pid);
            if (pid <= 0) return null;
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    private void Poll()
    {
        if (_regs.Count == 0) return;

        HashSet<string> running;
        try
        {
            running = new HashSet<string>(
                Process.GetProcesses().Select(p => { try { return p.ProcessName; } catch { return ""; } }),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { return; } // best-effort, same as BaseCampProcessGuard

        bool needForeground = _regs.Values.Any(r => r.FocusOnly);
        string? fgName = needForeground ? ForegroundProcessName() : null;

        foreach (var key in _regs.Keys.ToList())
        {
            if (!_regs.TryGetValue(key, out var reg)) continue;

            bool isRunning = running.Contains(reg.ExeName);
            bool isForeground = fgName is not null &&
                string.Equals(fgName, reg.ExeName, StringComparison.OrdinalIgnoreCase);

            try
            {
                if (reg.FocusOnly)
                    PollFocus(reg, isForeground);
                else
                    PollLaunch(reg, isRunning);
            }
            catch { /* best-effort: a bad callback must not kill the shared timer */ }

            // The callback above may have replaced this key (a profile switch triggers
            // XxRefreshProfiles -> UpdateRegistration); only stamp state onto the reg we
            // actually polled if it's still the live one.
            if (_regs.TryGetValue(key, out var current) && ReferenceEquals(current, reg))
            {
                reg.WasRunning = isRunning;
                reg.WasForeground = isForeground;
            }
        }
    }

    private static bool SameProfile(string? a, string? b) =>
        string.Equals(a, b, StringComparison.Ordinal);

    private static void PollFocus(Reg reg, bool isForeground)
    {
        if (isForeground && !reg.Active)
        {
            string? cur = reg.GetCurrentProfile();
            if (!SameProfile(cur, reg.TargetProfile))
            {
                reg.PrevProfile = cur;
                reg.SwitchToProfile(reg.TargetProfile);
            }
            else
            {
                reg.PrevProfile = null; // already on it — nothing to restore later
            }
            reg.Active = true;
        }
        else if (!isForeground && reg.Active)
        {
            if (reg.PrevProfile is not null && SameProfile(reg.GetCurrentProfile(), reg.TargetProfile))
                reg.SwitchToProfile(reg.PrevProfile);
            reg.Active = false;
            reg.PrevProfile = null;
        }
    }

    private static void PollLaunch(Reg reg, bool isRunning)
    {
        if (isRunning && !reg.WasRunning)
        {
            string? cur = reg.GetCurrentProfile();
            if (!SameProfile(cur, reg.TargetProfile))
            {
                reg.PrevProfile = cur;
                reg.SwitchToProfile(reg.TargetProfile);
                reg.Active = true;
            }
        }
        else if (!isRunning && reg.WasRunning && reg.RestoreOnClose && reg.Active)
        {
            if (reg.PrevProfile is not null && SameProfile(reg.GetCurrentProfile(), reg.TargetProfile))
                reg.SwitchToProfile(reg.PrevProfile);
            reg.Active = false;
            reg.PrevProfile = null;
        }
    }
}
