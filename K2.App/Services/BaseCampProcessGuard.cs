using System;
using System.Collections.Generic;
using System.IO;
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
    private static readonly string[] Needles =
        { "displaypadworker", "basecamp", "base camp", "mountain", "makalu" };

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
                    // NO tree kill: apps launched from a pad key while Base Camp was in
                    // control (e.g. a "Run Program" Steam key) are children of the worker
                    // and must survive it. Every real Base Camp process is matched by name
                    // in this same loop, so nothing of Base Camp's is left behind.
                    p.Kill();
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
    // Full stop (all processes + Windows service) — AppSettings.AutoStopBaseCamp
    // ================================================================

    /// <summary>
    /// Stops the "BaseCampService" Windows service and kills every running Base Camp
    /// process (GUI, service, workers, Makalu monitor — anything matching <see cref="Needles"/>,
    /// except K2 itself). Equivalent to <c>stop-basecamp.bat</c> but run in-process at K2
    /// startup, so K2 replaces Base Camp instead of both fighting over the same USB
    /// devices. Best-effort throughout: the service stop needs admin rights and silently
    /// no-ops without them, same as the .bat. Returns the number of processes killed.
    /// </summary>
    public static int KillAllBaseCampProcesses(Action<string>? log = null)
    {
        StopBaseCampService(log);

        int killed = 0;
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                string name;
                try { name = p.ProcessName; } catch { continue; }
                if (!IsBaseCamp(name)) continue;
                try
                {
                    log?.Invoke($"[AutoStop] killing Base Camp process: {name} (pid {p.Id})");
                    // NO tree kill (confirmed 2026-07-19: it was closing Steam). Apps the
                    // user launched from a pad key while Base Camp was in control are
                    // children of these processes; killing the tree took them down too.
                    // All actual Base Camp processes match Needles by name and are killed
                    // individually by this loop anyway.
                    p.Kill();
                    p.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[AutoStop] could not kill {name}: {ex.Message}");
                }
            }
        }
        catch { /* best-effort */ }
        return killed;
    }

    /// <summary>Stops the Base Camp Windows service via <c>sc.exe</c> (avoids pulling in
    /// the System.ServiceProcess NuGet package for a single best-effort call). Requires
    /// admin rights; fails silently (logged, not thrown) otherwise.</summary>
    private static void StopBaseCampService(Action<string>? log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "stop BaseCampService")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var sc = System.Diagnostics.Process.Start(psi);
            sc?.WaitForExit(3000);
            log?.Invoke(sc != null && sc.ExitCode == 0
                ? "[AutoStop] BaseCampService stopped."
                : "[AutoStop] BaseCampService not stopped (not installed, already stopped, or admin rights needed).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[AutoStop] could not stop BaseCampService: {ex.Message}");
        }
    }

    /// <summary>Starts the Base Camp Windows service via <c>sc.exe</c> (mirror of
    /// <see cref="StopBaseCampService"/>). Requires admin rights; fails silently
    /// (logged, not thrown) otherwise.</summary>
    private static void StartBaseCampService(Action<string>? log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "start BaseCampService")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var sc = System.Diagnostics.Process.Start(psi);
            sc?.WaitForExit(3000);
            log?.Invoke(sc != null && sc.ExitCode == 0
                ? "[Restart] BaseCampService started."
                : "[Restart] BaseCampService not started (not installed, already running, or admin rights needed).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Restart] could not start BaseCampService: {ex.Message}");
        }
    }

    // ================================================================
    // Restart on K2 close — AppSettings.RestartBaseCampOnClose
    // ================================================================

    /// <summary>
    /// Puts Base Camp back the way it normally starts: the Windows service, plus every
    /// enabled Base Camp Windows-autostart entry (usually just "Base Camp.exe", which
    /// spawns its own worker processes the same way it would after a fresh boot), plus
    /// MountainDisplayPadWorker directly (it has no autostart entry of its own — Base
    /// Camp normally launches it internally, but relaunching it here doesn't depend on
    /// that). Called on K2 close when <see cref="AppSettings.RestartBaseCampOnClose"/> is
    /// enabled.
    ///
    /// Deliberately NOT based on "what K2 personally killed this run": if Base Camp was
    /// already stopped before K2 even started (e.g. the previous K2 session already shut
    /// it down and it never came back), there would be nothing recorded to relaunch even
    /// though the user still wants it running again — confirmed on hardware 2026-07-15.
    /// Each executable is skipped if a process with that name is already running, so this
    /// is safe to call even when Base Camp never got auto-stopped this session.
    /// Returns the number of processes actually launched.
    /// </summary>
    public static int RestartKilledProcesses(Action<string>? log = null)
    {
        StartBaseCampService(log);

        int started = 0;

        var entries = FindAutostartEntries().Where(e => e.Enabled).ToList();
        if (entries.Count == 0)
            log?.Invoke("[Restart] no enabled Base Camp autostart entry found in the registry.");
        foreach (var entry in entries)
        {
            if (LaunchCommand(entry.Command, log)) started++;
        }

        if (!IsRunning("MountainDisplayPadWorker"))
        {
            string[] installDirs = NativeDependencyResolver.BaseCampDirectories();
            string? worker = ResolveExePath(installDirs, "MountainDisplayPadWorker");
            if (worker is null)
            {
                log?.Invoke(installDirs.Length == 0
                    ? "[Restart] no Base Camp installation folder found — cannot locate MountainDisplayPadWorker.exe."
                    : $"[Restart] MountainDisplayPadWorker.exe not found under {string.Join(" | ", installDirs)}.");
            }
            else
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(worker)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true, // background worker, no UI of its own
                        WorkingDirectory = Path.GetDirectoryName(worker) ?? "",
                    };
                    System.Diagnostics.Process.Start(psi);
                    started++;
                    log?.Invoke($"[Restart] relaunched silently: {worker}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[Restart] could not relaunch {worker}: {ex.Message}");
                }
            }
        }

        return started;
    }

    private static bool IsRunning(string processName)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

    /// <summary>Splits a registry Run-key command (quoted path, or an unquoted path that
    /// may itself contain spaces followed by arguments — e.g.
    /// <c>C:\Program Files (x86)\Mountain Base Camp\Base Camp.exe --hidden</c>) into an
    /// executable path and its argument string, then launches it exactly as Windows
    /// autostart would — skipped if a process with the exe's name is already running.
    /// Returns true if a new process was started.</summary>
    private static bool LaunchCommand(string command, Action<string>? log)
    {
        var (exe, args) = SplitCommandLine(command);
        if (exe is null || !File.Exists(exe))
        {
            log?.Invoke($"[Restart] could not resolve executable from autostart command: {command}");
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(exe);
        if (IsRunning(name))
        {
            log?.Invoke($"[Restart] {name} already running, skipping.");
            return false;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            System.Diagnostics.Process.Start(psi);
            log?.Invoke($"[Restart] relaunched: {exe} {args}".TrimEnd());
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Restart] could not relaunch {exe}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Mirrors how CreateProcess resolves an unquoted lpCommandLine: a quoted
    /// path is taken as-is; otherwise, walk forward to each ".exe" occurrence and accept
    /// the first prefix that actually exists on disk (handles "Base Camp.exe" being an
    /// unquoted path that itself contains a space, followed by arguments).</summary>
    private static (string? exe, string args) SplitCommandLine(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return (null, "");

        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            if (end > 0)
                return (command.Substring(1, end - 1), command.Substring(end + 1).Trim());
        }

        int searchFrom = 0;
        while (true)
        {
            int exeIdx = command.IndexOf(".exe", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (exeIdx < 0) return (command, ""); // no ".exe" found: best-effort, treat whole string as the path
            int end = exeIdx + 4;
            string candidate = command.Substring(0, end);
            if (File.Exists(candidate))
                return (candidate, command.Substring(end).Trim());
            searchFrom = end;
        }
    }

    /// <summary>Finds "{processName}.exe" under any of the given install folders (recursive
    /// — Base Camp's Electron GUI keeps some executables under resources\bin). Returns the
    /// first match, or null if not found in any of them.</summary>
    private static string? ResolveExePath(string[] installDirs, string processName)
    {
        foreach (var dir in installDirs)
        {
            try
            {
                var match = Directory.EnumerateFiles(dir, processName + ".exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (match is not null) return match;
            }
            catch { /* unreadable subfolder: skip */ }
        }
        return null;
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
