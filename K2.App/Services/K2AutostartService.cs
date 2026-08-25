using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Manages K2.App's own Windows-autostart entry — separate from
/// <see cref="BaseCampProcessGuard"/>'s Base Camp autostart management, which only
/// enables/disables existing entries and never touches K2's own.
///
/// Backed by a per-user Scheduled Task ("K2.App Autostart", ONLOGON trigger, /RL HIGHEST)
/// rather than the HKCU Run key. K2.App carries a requireAdministrator manifest (see
/// app.manifest — needed to control BaseCampService, which runs as LocalSystem), and
/// Windows does NOT auto-elevate Run-key entries at logon: an app manifested
/// requireAdministrator launched that way fails to start silently, with no UAC prompt
/// and no error. A Scheduled Task with /RL HIGHEST is the standard way around that.
/// Creating/deleting the task itself requires elevation, which K2.App already has by
/// virtue of its own manifest, so this runs without an extra prompt.
/// </summary>
internal static class K2AutostartService
{
    private const string TaskName = "K2.App Autostart";

    // Legacy HKCU Run key, kept only so SetEnabled can clean up entries left by
    // older K2 versions that used it (see git history for the prior implementation).
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "K2.App";

    /// <summary>True if the K2.App Autostart scheduled task currently exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            return RunSchtasks($"/query /tn \"{TaskName}\"") == 0;
        }
        catch { return false; }
    }

    /// <summary>Creates/deletes the K2.App Autostart scheduled task (ONLOGON, current
    /// user, highest privileges). Also removes any legacy HKCU Run key entry.</summary>
    public static void SetEnabled(bool enabled)
    {
        RemoveLegacyRunKeyEntry();

        try
        {
            if (enabled)
            {
                string exePath = Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                string user = $@"{Environment.UserDomainName}\{Environment.UserName}";
                RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" " +
                            $"/sc onlogon /ru \"{user}\" /rl highest /f");
            }
            else
            {
                RunSchtasks($"/delete /tn \"{TaskName}\" /f");
            }
        }
        catch { /* best-effort persistence — e.g. Task Scheduler unexpectedly unavailable */ }
    }

    private static void RemoveLegacyRunKeyEntry()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            run?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort cleanup */ }
    }

    private static int RunSchtasks(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        proc!.WaitForExit();
        return proc.ExitCode;
    }
}
