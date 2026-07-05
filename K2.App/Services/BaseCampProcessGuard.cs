using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Keeps Base Camp's background processes away from the DisplayPad while K2's native
/// USB engine is active, and manages Base Camp's Windows-autostart entries.
///
/// Rationale: HID collections accept multiple concurrent writers. MountainDisplayPadWorker
/// autostarts with Windows, keeps running in the background, reacts to the pads' key
/// events and pushes its own icon/PC-info uploads — its pixel stream interleaves with
/// K2's on the display endpoint and icons corrupt at random (confirmed on hardware,
/// 2026-07-04: killing the worker eliminates the corruption).
/// </summary>
internal static class BaseCampProcessGuard
{
    // Substrings (lowercase) identifying Base Camp processes / autostart entries.
    private static readonly string[] Needles = { "displaypadworker", "basecamp", "base camp", "mountain" };

    private static bool IsBaseCamp(string s)
    {
        string l = s.ToLowerInvariant();
        if (l.Contains("k2")) return false;   // never touch ourselves
        return Needles.Any(l.Contains);
    }

    // ================================================================
    // Worker kill
    // ================================================================

    /// <summary>
    /// Kills any running MountainDisplayPadWorker (the process that fights the native
    /// engine over the display endpoint). Returns the number of processes killed.
    /// Only the DisplayPad worker is targeted — the Base Camp GUI is left alone so the
    /// user can still use it for the other devices if they want.
    /// </summary>
    public static int KillDisplayPadWorkers(Action<string>? log = null)
    {
        int killed = 0;
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                bool target;
                try { target = p.ProcessName.ToLowerInvariant().Contains("displaypadworker"); }
                catch { continue; }
                if (!target) continue;
                try
                {
                    log?.Invoke($"[DpNative] killing Base Camp worker: {p.ProcessName} (pid {p.Id})");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[DpNative] could not kill {p.ProcessName}: {ex.Message}");
                }
            }
        }
        catch { /* best-effort */ }
        return killed;
    }

    // ================================================================
    // Autostart management (StartupApproved — same mechanism Task Manager uses,
    // fully reversible: the Run entry itself is never deleted)
    // ================================================================

    public sealed record AutostartEntry(string Hive, string Name, string Command, bool Enabled);

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Finds Base Camp entries in the HKCU/HKLM Run keys with their current
    /// StartupApproved state (no flag value = enabled, first byte 0x02 = enabled).</summary>
    public static List<AutostartEntry> FindAutostartEntries()
    {
        var result = new List<AutostartEntry>();
        foreach (var (root, hive) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            try
            {
                using var run = root.OpenSubKey(RunKey);
                if (run is null) continue;
                using var approved = root.OpenSubKey(ApprovedKey);
                foreach (var name in run.GetValueNames())
                {
                    string cmd = run.GetValue(name)?.ToString() ?? "";
                    if (!IsBaseCamp(name) && !IsBaseCamp(cmd)) continue;
                    bool enabled = true;
                    if (approved?.GetValue(name) is byte[] flag && flag.Length > 0)
                        enabled = (flag[0] & 0x01) == 0;   // 0x02 = enabled, 0x03 = disabled
                    result.Add(new AutostartEntry(hive, name, cmd, enabled));
                }
            }
            catch { /* hive not readable — skip */ }
        }
        return result;
    }

    /// <summary>
    /// Enables/disables every Base Camp autostart entry by writing the StartupApproved
    /// flag (0x02 = enabled, 0x03 = disabled; 12-byte value like Task Manager writes).
    /// HKLM entries need admin rights — failures are reported via <paramref name="log"/>
    /// and the method returns how many entries were actually updated.
    /// </summary>
    public static int SetAutostartEnabled(bool enabled, Action<string>? log = null)
    {
        int changed = 0;
        var flag = new byte[12];
        flag[0] = (byte)(enabled ? 0x02 : 0x03);
        foreach (var entry in FindAutostartEntries())
        {
            try
            {
                var root = entry.Hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using var approved = root.CreateSubKey(ApprovedKey, writable: true);
                approved.SetValue(entry.Name, flag, RegistryValueKind.Binary);
                changed++;
                log?.Invoke($"[BC] autostart '{entry.Name}' ({entry.Hive}) -> {(enabled ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[BC] cannot change autostart '{entry.Name}' ({entry.Hive}): {ex.Message}" +
                            (entry.Hive == "HKLM" ? " — run K2 as administrator to change HKLM entries" : ""));
            }
        }
        return changed;
    }
}
