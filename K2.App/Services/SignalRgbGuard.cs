using System;
using System.IO;
using System.Linq;
using System.Threading;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Coexistence layer between K2 and SignalRGB.
///
/// <para>SignalRGB ships its own reverse-engineered plugins for the Mountain gear
/// (<c>Signal-x64\Plugins\Mountain\*.js</c>) and they drive exactly the same raw-HID
/// interfaces K2's native engines use — Everest Max <c>MI_03</c>, MacroPad <c>MI_02</c>,
/// Everest 60 <c>MI_00</c>, Makalu 67 <c>MI_01</c>. HID collections accept multiple
/// concurrent writers, so with both programs running the two lighting streams interleave
/// and the LEDs flicker between K2's effect and SignalRGB's canvas (same class of problem
/// as Base Camp's DisplayPad worker — see <see cref="BaseCampProcessGuard"/>).</para>
///
/// <para>Unlike Base Camp, SignalRGB is NOT a competitor to be killed: users run it on
/// purpose to sync their whole rig. So the default is <see cref="SignalRgbMode.Yield"/> —
/// while the SignalRGB engine is up, K2 keeps doing everything that isn't lighting (keys,
/// macros, display keys, Media Dock, profiles) and simply stops writing colors. When
/// SignalRGB exits, K2 reapplies its own lighting via <see cref="LightingReclaimed"/>.</para>
///
/// <para>Detection is by process name: the engine that owns the USB devices is
/// <c>SignalRgb.exe</c>. <c>SignalRgbService.exe</c> is the always-on elevated helper and
/// <c>SignalRgbLauncher.exe</c> only bootstraps the update — neither one drives LEDs, so
/// matching them would make K2 yield permanently. Hence exact-name matching, not
/// "starts with".</para>
/// </summary>
internal static class SignalRgbGuard
{
    /// <summary>Process name (no extension) of the engine that actually owns the USB devices.</summary>
    private const string EngineProcess = "SignalRgb";

    /// <summary>Processes killed by <see cref="SignalRgbMode.Stop"/>: the engine plus the
    /// launcher (which would immediately bring the engine back up).</summary>
    private static readonly string[] StopTargets = { "SignalRgb", "SignalRgbLauncher" };

    private static Timer? _poll;
    private static bool _lastRunning;
    private static Action<string>? _log;

    /// <summary>Raised when the SignalRGB engine starts or stops. Argument: true = running.</summary>
    public static event Action<bool>? StateChanged;

    /// <summary>Raised when K2 gets the lighting back (SignalRGB closed, or the user turned
    /// coexistence off). Device modules subscribe to reapply their own effect.</summary>
    public static event Action? LightingReclaimed;

    // ================================================================
    // State
    // ================================================================

    /// <summary>True while the SignalRGB engine process is up.</summary>
    public static bool IsEngineRunning
    {
        get
        {
            try
            {
                return System.Diagnostics.Process.GetProcessesByName(EngineProcess).Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// The single flag every lighting write in K2 checks. True only in
    /// <see cref="SignalRgbMode.Yield"/> mode while SignalRGB is actually running — cached
    /// by the poll timer so hot paths (per-key color streams) don't enumerate processes.
    /// </summary>
    public static bool LightingYielded { get; private set; }

    /// <summary>Convenience guard for device services: logs once per blocked call and returns
    /// true when the caller must skip its lighting write.</summary>
    public static bool BlockLighting(string caller)
    {
        if (!LightingYielded) return false;
        App.WriteLog($"[SignalRGB] lighting write skipped ({caller}) — SignalRGB owns the device.");
        return true;
    }

    // ================================================================
    // Polling
    // ================================================================

    /// <summary>Starts (or restarts) the 2s poll that tracks the SignalRGB engine. Safe to
    /// call repeatedly — e.g. after the user changes the mode in Settings.</summary>
    public static void Start(Action<string>? log = null)
    {
        _log = log ?? App.WriteLog;
        Stop();

        if (AppSettings.SignalRgbMode != SignalRgbMode.Yield)
        {
            // Not yielding: make sure a previously-set flag doesn't stay latched.
            if (LightingYielded)
            {
                LightingYielded = false;
                _lastRunning = false;
                LightingReclaimed?.Invoke();
            }
            return;
        }

        _lastRunning = IsEngineRunning;
        LightingYielded = _lastRunning;
        _log?.Invoke($"[SignalRGB] coexistence armed — engine {(_lastRunning ? "running (K2 yields lighting)" : "not running")}.");
        if (_lastRunning) StateChanged?.Invoke(true);

        _poll = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Stops the poll timer (does not change <see cref="LightingYielded"/>).</summary>
    public static void Stop()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private static void Tick()
    {
        bool running;
        try { running = IsEngineRunning; }
        catch { return; }

        if (running == _lastRunning) return;
        _lastRunning = running;
        LightingYielded = running;
        _log?.Invoke($"[SignalRGB] engine {(running ? "started — K2 hands over the lighting" : "closed — K2 takes the lighting back")}.");

        try { StateChanged?.Invoke(running); } catch { /* UI handler blew up: keep polling */ }
        if (!running)
        {
            try { LightingReclaimed?.Invoke(); } catch { }
        }
    }

    // ================================================================
    // Stop mode
    // ================================================================

    /// <summary>Kills the SignalRGB engine and its launcher (best-effort, mirrors
    /// <see cref="BaseCampProcessGuard.KillAllBaseCampProcesses"/>). The Windows service is
    /// deliberately left alone: it doesn't drive LEDs and killing it needs admin rights.
    /// Returns how many processes were killed.</summary>
    public static int KillSignalRgb(Action<string>? log = null)
    {
        log ??= App.WriteLog;
        int killed = 0;
        foreach (var target in StopTargets)
        {
            System.Diagnostics.Process[] procs;
            try { procs = System.Diagnostics.Process.GetProcessesByName(target); }
            catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    log($"[SignalRGB] killing {target} (pid {p.Id})");
                    p.Kill();
                    p.WaitForExit(3000);
                    killed++;
                }
                catch (Exception ex) { log($"[SignalRGB] could not kill {target}: {ex.Message}"); }
            }
        }
        return killed;
    }

    // ================================================================
    // Installation / plugin folders
    // ================================================================

    /// <summary>Newest <c>%LOCALAPPDATA%\VortxEngine\app-*\Signal-x64</c>, or null when
    /// SignalRGB isn't installed. (Squirrel keeps one folder per version side by side; the
    /// MSIX package under WindowsApps is only the Dynamic Lighting shim, it holds no plugins.)</summary>
    public static string? InstallDirectory()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VortxEngine");
            if (!Directory.Exists(root)) return null;

            return Directory.EnumerateDirectories(root, "app-*")
                .Select(d => Path.Combine(d, "Signal-x64"))
                .Where(Directory.Exists)
                .OrderByDescending(d => VersionOf(Path.GetFileName(Path.GetDirectoryName(d)!)))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static Version VersionOf(string appFolderName)
    {
        // "app-2.5.74" -> 2.5.74; anything unparseable sorts last.
        return Version.TryParse(appFolderName.Replace("app-", ""), out var v) ? v : new Version(0, 0);
    }

    /// <summary>True when a SignalRGB installation was found on this PC.</summary>
    public static bool IsInstalled => InstallDirectory() is not null;

    /// <summary>SignalRGB's own bundled Mountain plugins (read-only reference — an app update
    /// overwrites this folder, which is why K2's own plugins go to <see cref="UserPluginDirectory"/>).</summary>
    public static string? BundledMountainPluginDirectory()
    {
        string? install = InstallDirectory();
        if (install is null) return null;
        string dir = Path.Combine(install, "Plugins", "Mountain");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The user plugin folder SignalRGB scans on top of its bundled ones —
    /// <c>Documents\WhirlwindFX\Plugins</c>. Plugins dropped here survive app updates and
    /// take precedence when they declare the same VID/PID.</summary>
    public static string UserPluginDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WhirlwindFX", "Plugins");

    /// <summary>K2's own plugin sources, shipped next to the executable
    /// (<c>SignalRGB\Plugins</c> — see the K2.App .csproj content items).</summary>
    public static string K2PluginSourceDirectory => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "SignalRGB", "Plugins");

    /// <summary>
    /// Copies K2's Mountain plugins into <see cref="UserPluginDirectory"/>, overwriting any
    /// previous copy. Returns the number of files installed (0 = nothing to install).
    /// SignalRGB picks them up on its next start.
    /// </summary>
    public static int InstallK2Plugins(Action<string>? log = null)
    {
        log ??= App.WriteLog;
        string src = K2PluginSourceDirectory;
        if (!Directory.Exists(src))
        {
            log($"[SignalRGB] plugin source folder missing: {src}");
            return 0;
        }

        int copied = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(src, "*.js", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, file);
                string dst = Path.Combine(UserPluginDirectory, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
                copied++;
                log($"[SignalRGB] installed plugin: {rel}");
            }
        }
        catch (Exception ex)
        {
            log($"[SignalRGB] plugin install failed: {ex.Message}");
        }
        return copied;
    }

    /// <summary>Removes the plugins <see cref="InstallK2Plugins"/> wrote (matched by the same
    /// relative paths, so unrelated user plugins are never touched). Returns files removed.</summary>
    public static int RemoveK2Plugins(Action<string>? log = null)
    {
        log ??= App.WriteLog;
        string src = K2PluginSourceDirectory;
        if (!Directory.Exists(src)) return 0;

        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(src, "*.js", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(UserPluginDirectory, Path.GetRelativePath(src, file));
                if (!File.Exists(dst)) continue;
                File.Delete(dst);
                removed++;
            }
        }
        catch (Exception ex)
        {
            log($"[SignalRGB] plugin removal failed: {ex.Message}");
        }
        return removed;
    }

    /// <summary>True when K2's plugins are currently present in the user plugin folder.</summary>
    public static bool K2PluginsInstalled()
    {
        try
        {
            string src = K2PluginSourceDirectory;
            if (!Directory.Exists(src)) return false;
            var files = Directory.EnumerateFiles(src, "*.js", SearchOption.AllDirectories).ToList();
            return files.Count > 0 && files.All(f =>
                File.Exists(Path.Combine(UserPluginDirectory, Path.GetRelativePath(src, f))));
        }
        catch { return false; }
    }
}
