using System;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Manages K2.App's own Windows-autostart entry (HKCU Run key) — separate from
/// <see cref="BaseCampProcessGuard"/>'s Base Camp autostart management, which only
/// enables/disables existing entries and never touches K2's own. HKCU-only by design:
/// no admin rights required to write the entry itself.
///
/// CAVEAT: K2.App now carries a requireAdministrator manifest (see app.manifest — needed
/// to control BaseCampService, which runs as LocalSystem). Windows does NOT auto-elevate
/// Run-key entries at logon, so an app manifested requireAdministrator launched this way
/// can fail to start silently. See settings_k2_autostart_hint in the Settings tab.
/// </summary>
internal static class K2AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "K2.App";

    /// <summary>True if a K2.App entry currently exists in HKCU\...\Run.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    /// <summary>Adds/removes the HKCU Run entry pointing at the current executable.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (run is null) return;
            if (enabled)
            {
                string exePath = Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                run.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort persistence — e.g. HKCU unexpectedly not writable */ }
    }
}
