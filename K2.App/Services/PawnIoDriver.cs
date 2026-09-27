// PawnIoDriver.cs — the PawnIO kernel driver LibreHardwareMonitor needs for CPU sensors.
//
// Since LHM 0.9.5 every MSR/SMN read (AMD Tctl/CCD temps, clocks, package power; Intel core
// temps) goes through PawnIO, which LHM does NOT install. Without it the CPU node still shows
// up but reads 0 (first seen on a Ryzen 9950X3D: only "Core (Tctl/Tdie)=0"); GPU/motherboard
// sensors come through other paths and keep working. K2 ships the official signed installer
// (ThirdParty\PawnIO_setup.exe, copied next to the exe) and runs it silently: from the setup
// wizard, or from the "Install" button shown where a CPU temperature is configured. K2.App
// already runs elevated (app.manifest), so the silent install needs no extra UAC prompt.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace K2.App.Services;

internal static class PawnIoDriver
{
    private static string SetupPath =>
        Path.Combine(AppContext.BaseDirectory, "ThirdParty", "PawnIO_setup.exe");

    /// <summary>LHM's own check (the PawnIO uninstall key, 32- and 64-bit views). Read once per
    /// process by LHM's static ctor — after an install this stays false until restart, so
    /// <see cref="InstallAsync"/> tracks its own success.</summary>
    public static bool IsInstalled => _installedThisSession || LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;

    public static string VersionText =>
        LibreHardwareMonitor.PawnIo.PawnIo.Version?.ToString() ?? (_installedThisSession ? "installed" : "none");

    public static bool SetupAvailable => File.Exists(SetupPath);

    private static bool _installedThisSession;

    /// <summary>Runs the bundled installer silently and, on success, restarts the sensor
    /// backend so the CPU node is rebuilt with PawnIO available. Never throws.</summary>
    public static async Task<bool> InstallAsync()
    {
        if (!SetupAvailable)
        {
            App.WriteLog($"[PawnIO] installer missing at {SetupPath}");
            return false;
        }
        try
        {
            int code = await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo(SetupPath, "-install -silent")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p is null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            });
            App.WriteLog($"[PawnIO] silent install exit code {code}");
            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED
            if (code is not (0 or 3010)) return false;
            _installedThisSession = true;
            await Task.Run(() => { HardwareSensors.Stop(); HardwareSensors.ResetStats(); HardwareSensors.Start(); });
            return true;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PawnIO] install failed: {ex.Message}");
            return false;
        }
    }
}
