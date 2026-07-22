using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace K2.Core.Services;

/// <summary>
/// Shared singleton that polls running processes (same name-matching approach as
/// K2.App's BaseCampProcessGuard) and fires a callback the moment a linked executable
/// starts running — used to auto-switch a device's profile when the user launches an
/// app it's linked to (see each device's XxShowProfileGear/ProfileSettingsDialog for how
/// a profile gets linked, and each device's XxRefreshProfiles for registration).
///
/// One instance, one DispatcherTimer, for the whole process — mirrors
/// BacklightIdleTimer's per-purpose-timer pattern but shared rather than per-device,
/// since polling Process.GetProcesses() once for all registrations is cheaper than
/// once per device.
/// </summary>
public sealed class ProfileLaunchWatcher
{
    public static ProfileLaunchWatcher Instance { get; } = new();

    private sealed record Reg(string ExeName, bool WasRunning, Action OnLaunch);

    private readonly Dictionary<string, Reg> _regs = new();
    private readonly DispatcherTimer _timer;

    private ProfileLaunchWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>Registers/updates the executable linked to a given key (one key per
    /// device-profile-slot, e.g. "Dp:3:2" for device 3 slot 2 — see each device's
    /// XxRefreshProfiles). A null/blank <paramref name="exePath"/> removes the
    /// registration. Preserves "was it running last poll" across updates so an unrelated
    /// refresh (e.g. renaming a different profile) doesn't cause a spurious re-trigger for
    /// an app that was already running before this call.</summary>
    public void UpdateRegistration(string key, string? exePath, Action onLaunch)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _regs.Remove(key);
            return;
        }
        string exeName = Path.GetFileNameWithoutExtension(exePath);
        bool wasRunning = _regs.TryGetValue(key, out var existing) && existing.WasRunning;
        _regs[key] = new Reg(exeName, wasRunning, onLaunch);
    }

    public void RemoveRegistration(string key) => _regs.Remove(key);

    /// <summary>All currently-registered keys starting with <paramref name="prefix"/> —
    /// used by each device's XxRefreshProfiles to find and remove stale registrations
    /// (deleted profiles, or profiles whose link was cleared) after re-adding the current
    /// set via <see cref="UpdateRegistration"/>.</summary>
    public IEnumerable<string> KeysWithPrefix(string prefix) =>
        _regs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

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

        foreach (var key in _regs.Keys.ToList())
        {
            var reg = _regs[key];
            bool now = running.Contains(reg.ExeName);
            if (now && !reg.WasRunning)
            {
                try { reg.OnLaunch(); }
                catch { /* best-effort: a bad callback must not kill the shared timer */ }
            }
            if (_regs.ContainsKey(key)) // the callback above may itself remove/replace this key
                _regs[key] = reg with { WasRunning = now };
        }
    }
}
