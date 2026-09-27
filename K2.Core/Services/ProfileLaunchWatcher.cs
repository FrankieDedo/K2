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
///   • + keep-while-running: the switch stays ARMED while the exe runs instead of firing only
///     on the launch edge, so an app that was already running when K2 started still gets its
///     profile. It fires once per run and then stands down — a profile the user picks
///     afterwards is left alone. When several such apps run at once only the most recently
///     started one may fire, so they can't fight over the device.
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

    /// <summary>Where this class writes its diagnostics — set by the app to its own log sink
    /// (K2.Core cannot see App.WriteLog). Null until then, and the calls are no-ops.
    ///
    /// <para>Deliberately on the always-on log, not a debug channel: "the profile sometimes
    /// doesn't come up by itself" is a report that arrives as a log file, and without a line per
    /// decision there is no telling a registration that was never made from one that fired and
    /// was undone by something else.</para></summary>
    public static Action<string>? Log;

    /// <summary>One line per CHANGE, never per poll — this runs once a second for the life of the
    /// process.</summary>
    private static void Say(string message) => Log?.Invoke("[WATCH] " + message);

    private sealed class Reg
    {
        /// <summary>The registration key, carried so the log can name who did what.</summary>
        public required string Key;
        public required string ExeName;
        /// <summary>When set, the process only counts while one of its windows is titled with
        /// this prefix — for a game that runs inside a generic host process (Minecraft: Java
        /// Edition is <c>javaw</c>, like every other Java program).</summary>
        public string? TitlePrefix;
        public required bool FocusOnly;
        public required bool RestoreOnClose;
        public required bool KeepWhileRunning;
        /// <summary>Seconds the profile waits before coming back after the user switched away,
        /// or 0 for "never — their choice stands". See <see cref="PollLaunch"/>.</summary>
        public required int ReassertAfterSeconds;
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
        public long RunSince;        // launch order stamp, newest keep-while-running wins
        public DateTime? AwaySince;  // when the device was first seen off this profile
    }

    private readonly Dictionary<string, Reg> _regs = new();
    private long _runSeq;
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
    /// <param name="keepWhileRunning">(launch mode only) keep the switch ARMED for as long as
    /// the exe runs rather than only on the launch edge, so an app already running when this
    /// process started still gets its profile.</param>
    /// <param name="reassertAfterSeconds">(with <paramref name="keepWhileRunning"/>) how long a
    /// device may stay on ANOTHER profile before this one is put back. 0 leaves it alone: the
    /// user switched away on purpose and only they switch back.</param>
    /// <param name="targetProfile">token identifying this registration's profile, as
    /// understood by <paramref name="switchToProfile"/> / returned by
    /// <paramref name="getCurrentProfile"/> (each device uses its slot number as string).</param>
    /// <param name="getCurrentProfile">the device's currently-active profile token.</param>
    /// <param name="switchToProfile">switch the device to the given profile token.</param>
    public void UpdateRegistration(string key, string? exePath,
        bool focusOnly, bool restoreOnClose, string targetProfile,
        Func<string?> getCurrentProfile, Action<string> switchToProfile,
        bool keepWhileRunning = false, int reassertAfterSeconds = 0, string? windowTitlePrefix = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _regs.Remove(key);
            return;
        }
        string exeName = Path.GetFileNameWithoutExtension(exePath);
        _regs.TryGetValue(key, out var existing);
        // Only a registration that is new or has actually changed is worth a line: every profile
        // refresh (a tab switch, a save, a pad replug) re-registers all of them unchanged.
        if (existing is null || existing.ExeName != exeName ||
            existing.TargetProfile != targetProfile || existing.FocusOnly != focusOnly ||
            existing.KeepWhileRunning != keepWhileRunning)
            Say($"{key}: watching \"{exeName}\" -> profile {targetProfile} " +
                $"(focusOnly={focusOnly}, keepWhileRunning={keepWhileRunning}, " +
                $"restoreOnClose={restoreOnClose}){(existing is null ? "" : " [updated]")}");
        _regs[key] = new Reg
        {
            Key = key,
            ExeName = exeName,
            TitlePrefix = string.IsNullOrWhiteSpace(windowTitlePrefix) ? null : windowTitlePrefix,
            FocusOnly = focusOnly,
            RestoreOnClose = restoreOnClose,
            KeepWhileRunning = keepWhileRunning,
            ReassertAfterSeconds = reassertAfterSeconds,
            TargetProfile = targetProfile,
            GetCurrentProfile = getCurrentProfile,
            SwitchToProfile = switchToProfile,
            WasRunning = existing?.WasRunning ?? false,
            WasForeground = existing?.WasForeground ?? false,
            Active = existing?.Active ?? false,
            PrevProfile = existing?.PrevProfile,
            RunSince = existing?.RunSince ?? 0,
            AwaySince = existing?.AwaySince,
        };
    }

    /// <summary>Forgets what every registration under <paramref name="prefix"/> concluded, so
    /// each one decides again from scratch on the next poll: not active, not seen running, nothing
    /// to restore.
    ///
    /// <para>For the caller that moves devices OFF a profile behind the watcher's back — the game
    /// section's startup cleanup. Without this the watcher can have already decided, half a second
    /// earlier, that a pad was on the right profile and stood down; the cleanup then moves it away
    /// and nothing ever brings it back, which is the "sometimes the profile doesn't come up by
    /// itself" case. Re-arming is safe: a registration that has nothing to do simply does
    /// nothing.</para></summary>
    public void Rearm(string prefix)
    {
        foreach (var reg in _regs.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                                 .Select(kv => kv.Value))
        {
            reg.Active = false;
            reg.WasRunning = false;
            reg.WasForeground = false;
            reg.PrevProfile = null;
            reg.AwaySince = null;
            Say($"{reg.Key}: re-armed");
        }
    }

    /// <summary>The user chose a profile themselves on a device — told to the watcher so its
    /// automation stops treating that device as its own.
    ///
    /// <para>Picking the watched profile BY HAND makes it stick: nothing is remembered to restore,
    /// so neither losing focus nor the app exiting takes it away again. Picking another one is
    /// just as deliberate: the focus rule stands down, and the launch rule starts its "bring this
    /// profile back after N seconds" clock from now.</para>
    ///
    /// <para><paramref name="keyPrefix"/> and <paramref name="keySuffix"/> select the
    /// registrations of one device — keys are "&lt;scope&gt;:&lt;id&gt;:&lt;device&gt;".</para></summary>
    public void NotifyManualChoice(string keyPrefix, string keySuffix, string profileToken)
    {
        foreach (var reg in _regs.Where(kv => kv.Key.StartsWith(keyPrefix, StringComparison.Ordinal) &&
                                              kv.Key.EndsWith(keySuffix, StringComparison.Ordinal))
                                 .Select(kv => kv.Value))
        {
            if (SameProfile(profileToken, reg.TargetProfile))
            {
                reg.Active = true;
                reg.PrevProfile = null;
                reg.AwaySince = null;
                Say($"{reg.Key}: profile {profileToken} chosen by hand — it stays");
            }
            else
            {
                if (reg.FocusOnly) reg.Active = false;
                reg.PrevProfile = null;
                reg.AwaySince = DateTime.UtcNow;
                Say($"{reg.Key}: user moved to profile {profileToken}" +
                    (reg.ReassertAfterSeconds > 0
                        ? $" — back in {reg.ReassertAfterSeconds}s"
                        : " — staying out of the way"));
            }
        }
    }

    public void RemoveRegistration(string key)
    {
        if (_regs.Remove(key)) Say($"{key}: registration dropped");
    }

    /// <summary>All currently-registered keys starting with <paramref name="prefix"/> —
    /// used by each device's XxRefreshProfiles to find and remove stale registrations
    /// (deleted profiles, or profiles whose link was cleared) after re-adding the current
    /// set via <see cref="UpdateRegistration"/>.</summary>
    public IEnumerable<string> KeysWithPrefix(string prefix) =>
        _regs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    /// <summary>Process name that currently owns the foreground window, or null. Exposed so
    /// callers that need the same "only while X is in front" check without a full profile
    /// registration (the Discord voice page's foreground-only gate) can reuse it.</summary>
    public static string? CurrentForegroundProcessName() => ForegroundProcessName();

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

    private static string? ForegroundWindowTitle()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            var sb = new System.Text.StringBuilder(256);
            return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    private static bool HasWindowTitled(string exeName, string prefix)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(exeName))
                using (p)
                {
                    try
                    {
                        if (p.MainWindowTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { /* exited or access denied — try the next one */ }
                }
        }
        catch { }
        return false;
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
        string? fgTitle = needForeground && _regs.Values.Any(r => r.TitlePrefix is not null)
            ? ForegroundWindowTitle() : null;

        // A title-qualified registration narrows "running" to a process that has such a window.
        // Only processes of that name are inspected, and only once per poll per (name, prefix).
        var titled = new Dictionary<(string, string), bool>();
        bool IsRunning(Reg r)
        {
            if (!running.Contains(r.ExeName)) return false;
            if (r.TitlePrefix is null) return true;
            var k = (r.ExeName, r.TitlePrefix);
            if (!titled.TryGetValue(k, out bool hit))
                titled[k] = hit = HasWindowTitled(r.ExeName, r.TitlePrefix);
            return hit;
        }

        // Stamp launch order first, then let only the newest running keep-while-running
        // registration re-assert its profile: two of them running at once would otherwise
        // switch the device back and forth once a second.
        long bestRun = 0;
        foreach (var r in _regs.Values)
        {
            if (r.FocusOnly || !IsRunning(r)) continue;
            if (!r.WasRunning) r.RunSince = ++_runSeq;
            if (r.KeepWhileRunning && r.RunSince > bestRun) bestRun = r.RunSince;
        }

        foreach (var key in _regs.Keys.ToList())
        {
            if (!_regs.TryGetValue(key, out var reg)) continue;

            bool isRunning = IsRunning(reg);
            bool isForeground = fgName is not null &&
                string.Equals(fgName, reg.ExeName, StringComparison.OrdinalIgnoreCase) &&
                (reg.TitlePrefix is null ||
                 (fgTitle?.StartsWith(reg.TitlePrefix, StringComparison.OrdinalIgnoreCase) ?? false));

            try
            {
                if (reg.FocusOnly)
                    PollFocus(reg, isForeground);
                else
                    PollLaunch(reg, isRunning, reg.KeepWhileRunning && reg.RunSince == bestRun);
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
                Say($"{reg.Key}: \"{reg.ExeName}\" in front — {cur ?? "-"} -> {reg.TargetProfile}");
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
            // PrevProfile null = the profile is on this device because the USER put it there
            // (see NotifyManualChoice), not because focus did. Losing focus then means nothing:
            // taking it away would undo a choice nobody asked us to review — which is exactly
            // what the pad did a second after every manual pick (user report 2026-09-06: "appena
            // ci clicco sopra mi riporta al profilo 1").
            if (reg.PrevProfile is not null && SameProfile(reg.GetCurrentProfile(), reg.TargetProfile))
            {
                Say($"{reg.Key}: \"{reg.ExeName}\" lost focus — back to {reg.PrevProfile}");
                reg.SwitchToProfile(reg.PrevProfile);
            }
            reg.Active = false;
            reg.PrevProfile = null;
            reg.AwaySince = null;
        }
    }

    private static void PollLaunch(Reg reg, bool isRunning, bool mayReassert)
    {
        // The first clause is the launch EDGE (the game just appeared); the second is
        // keep-while-running, and it deliberately does NOT require the registration to be active
        // already.
        //
        // It used to (`mayReassert && reg.Active`), and that left the profile unreachable in a
        // very ordinary case: K2 starting while the game is already up. On the first poll the pad
        // could still be showing the game's own profile from the previous session, so the switch
        // was skipped as unnecessary and Active stayed false — but WasRunning became true, so the
        // launch edge never came round again. Moving the pad off that profile (which K2 now does
        // at startup by design) then had no way back, for the whole life of the process (user
        // report 2026-09-06: "non parte col gioco avviato, non c'è modo di tirarlo su").
        //
        // The re-assert is therefore armed until it LANDS, once per run — `!reg.Active` — and
        // not for the whole life of the process. Re-asserting every poll made the device
        // unusable while the game ran: any profile the user picked on the pad was undone within
        // the second, and there was no way to get off the game's page at all (user report
        // 2026-09-06: "non potevo cambiare profilo sul displaypad, mi riportava sempre allo
        // stesso"). Switching away from a profile the game put there is a decision, not a
        // glitch to repair.
        if (isRunning != reg.WasRunning)
            Say($"{reg.Key}: \"{reg.ExeName}\" {(isRunning ? "started" : "exited")} " +
                $"(active={reg.Active}, current profile={reg.GetCurrentProfile() ?? "-"}, " +
                $"target={reg.TargetProfile})");

        if (isRunning && (!reg.WasRunning || (mayReassert && !reg.Active)))
        {
            string? cur = reg.GetCurrentProfile();
            if (!SameProfile(cur, reg.TargetProfile))
            {
                Say($"{reg.Key}: switching {cur ?? "-"} -> {reg.TargetProfile}");
                // Only the first switch of this run records what to restore afterwards: a
                // later re-assert must not remember the profile we're overriding.
                if (!reg.Active) reg.PrevProfile = cur;
                reg.SwitchToProfile(reg.TargetProfile);
            }
            else Say($"{reg.Key}: already on profile {reg.TargetProfile} — nothing to do");
            // Marked active even when the pad was ALREADY on the profile: the registration has
            // done its job for this run either way, and leaving it armed would let it override
            // the user's next manual switch.
            reg.Active = true;
            reg.AwaySince = null;
        }
        // The COME-BACK. Once the switch has landed, the user is free to take the device
        // elsewhere; this brings the profile back only after it has stayed away for the
        // configured time ("Bring this profile back after N seconds"), and never at all when that
        // is off. Anything shorter than a deliberate wait would be the every-poll re-assert again,
        // which is what made the pad unusable while the game ran.
        else if (isRunning && mayReassert && reg.Active && reg.ReassertAfterSeconds > 0)
        {
            string? cur = reg.GetCurrentProfile();
            if (SameProfile(cur, reg.TargetProfile))
            {
                reg.AwaySince = null;   // back on it — by their hand or ours
            }
            else
            {
                var now = DateTime.UtcNow;
                reg.AwaySince ??= now;
                if ((now - reg.AwaySince.Value).TotalSeconds >= reg.ReassertAfterSeconds)
                {
                    Say($"{reg.Key}: away for {reg.ReassertAfterSeconds}s — back to " +
                        $"profile {reg.TargetProfile}");
                    reg.SwitchToProfile(reg.TargetProfile);
                    reg.AwaySince = null;
                }
            }
        }
        else if (!isRunning && reg.WasRunning && reg.RestoreOnClose && reg.Active)
        {
            // Same reading of a null PrevProfile as in PollFocus: nothing to put back means the
            // device is on this profile by the user's own choice, so the app closing leaves it
            // exactly where they left it.
            if (reg.PrevProfile is not null && SameProfile(reg.GetCurrentProfile(), reg.TargetProfile))
            {
                Say($"{reg.Key}: \"{reg.ExeName}\" closed — back to {reg.PrevProfile}");
                reg.SwitchToProfile(reg.PrevProfile);
            }
            reg.Active = false;
            reg.PrevProfile = null;
            reg.AwaySince = null;
        }
    }
}
