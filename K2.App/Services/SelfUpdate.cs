// SelfUpdate.cs — in-place update of an existing K2 install, without running the
// Inno Setup wizard again.
//
// Why this can work at all: the installer does nothing but copy the published tree
// into {app} (see Installer/K2Setup.iss — no services, no redists, no registry
// beyond Inno's own uninstall key) and seed app_settings.json on a FIRST install
// only. So "updating" is literally "overwrite the files with a newer tree", which
// K2 can do by itself: K2.App.exe is manifested requireAdministrator, and a child
// process inherits that elevated token, so even a Program Files install is
// writable with no extra UAC prompt.
//
// Flow (see MainWindow.Settings.cs for the UI half):
//   1. running K2  : download K2-X.Y.Z.zip -> %LocalAppData%\K2\update\, extract
//                    it into ...\update\staging\
//   2. running K2  : launch  staging\K2.App.exe --apply-update "<installDir>" <pid>
//                    and close itself
//   3. staged copy : wait for every K2 process under installDir to exit, mirror
//                    staging -> installDir (only the files that actually differ,
//                    old ones moved to ...\update\backup\ so a failure can roll
//                    back), refresh Inno's DisplayVersion, relaunch installDir's
//                    K2.App.exe, exit
//   4. new K2      : CleanupAfterUpdate() deletes staging/backup/zip (the updater
//                    could not delete the tree it was running from)
//
// The copy is additive: nothing in installDir is ever deleted, so Inno's
// unins000.exe/.dat — and any file the user dropped in there — survive. The
// trade-off is that a file REMOVED in a newer release lingers until the next
// full installer run; releases that need one can say so in the notes.
//
// Local testing without a GitHub release: see --test-update in TryRunEarlySwitches
// and Installer\make-test-update.bat.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace K2.App.Services;

public static class SelfUpdate
{
    /// <summary>Applies an already-staged tree. Passed to the STAGED copy of
    /// K2.App.exe: <c>--apply-update "&lt;installDir&gt;" &lt;pid of the old K2&gt;</c>.</summary>
    public const string ApplySwitch = "--apply-update";

    /// <summary>Dev/QA shortcut: <c>--test-update "&lt;zip or folder&gt;"</c> runs the whole
    /// self-update against a local build instead of a GitHub release.</summary>
    public const string TestSwitch = "--test-update";

    // Inno's AppId from K2Setup.iss — used to refresh DisplayVersion after an update.
    private const string InnoAppId = "{5C6A9E2A-6C6F-4C7B-9C64-1B7B6C7C7A21}_is1";

    private static readonly string[] K2ExeNames =
        { "K2.App.exe", "K2.DisplayPad.exe", "K2.DisplayPad.Satellite.exe" };

    public static string UpdateRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K2", "update");

    public static string StagingDir => Path.Combine(UpdateRoot, "staging");
    public static string BackupDir => Path.Combine(UpdateRoot, "backup");
    public static string LogPath => Path.Combine(UpdateRoot, "update.log");

    /// <summary>Folder the running copy lives in — the tree an update overwrites.</summary>
    public static string InstallDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    // ─────────────────────────────────────────────────────────────────────
    //  Entry point — called first thing in App's constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Handles the update-only command-line switches. Returns true when the
    /// process did updater work and must exit immediately: this runs BEFORE the
    /// single-instance mutex, the log reset and any UI, because in --apply-update mode
    /// the real K2 is still running and must not be disturbed.</summary>
    public static bool TryRunEarlySwitches(string[] argv)
    {
        var args = argv.Skip(1).ToArray();

        int i = Array.FindIndex(args, a => string.Equals(a, ApplySwitch, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            string installDir = i + 1 < args.Length ? args[i + 1].Trim('"') : "";
            int pid = i + 2 < args.Length && int.TryParse(args[i + 2], out int p) ? p : 0;
            RunApplyMode(installDir, pid);
            return true;
        }

        int t = Array.FindIndex(args, a => string.Equals(a, TestSwitch, StringComparison.OrdinalIgnoreCase));
        if (t >= 0)
        {
            string source = t + 1 < args.Length ? args[t + 1].Trim('"') : "";
            RunTestMode(source);
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Step 1-2: staging + handing over to the staged copy (called from the UI)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Extracts a downloaded release ZIP into <see cref="StagingDir"/> and
    /// returns the staged version. Throws if the archive doesn't look like a K2 tree.</summary>
    public static Version StageZip(string zipPath)
    {
        Log($"staging '{zipPath}' -> '{StagingDir}'");
        DeleteDirectory(StagingDir);
        Directory.CreateDirectory(StagingDir);
        ZipFile.ExtractToDirectory(zipPath, StagingDir, overwriteFiles: true);
        return VerifyStagedTree(StagingDir);
    }

    /// <summary>Sanity-checks a staged tree (ours or, in test mode, a publish folder)
    /// and returns the version of its K2.App.exe.</summary>
    public static Version VerifyStagedTree(string dir)
    {
        string exe = Path.Combine(dir, "K2.App.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException($"'{dir}' is not a K2 package (no K2.App.exe).");
        var v = FileVersion(exe);
        Log($"staged tree ok: {exe} v{v}");
        return v;
    }

    /// <summary>Starts the staged copy in --apply-update mode. The caller must close K2
    /// right after: the updater waits for this process to exit before touching files.</summary>
    public static void LaunchApply(string stagedDir, string installDir)
    {
        string exe = Path.Combine(stagedDir, "K2.App.exe");
        int pid = Environment.ProcessId;
        Log($"launching updater: \"{exe}\" {ApplySwitch} \"{installDir}\" {pid}");

        // UseShellExecute=false: the staged exe is also requireAdministrator, and we
        // are already elevated, so the child inherits the token with no second UAC
        // prompt (ShellExecute would raise one instead).
        Process.Start(new ProcessStartInfo(exe)
        {
            Arguments = $"{ApplySwitch} \"{installDir}\" {pid}",
            UseShellExecute = false,
            WorkingDirectory = stagedDir,
        });
    }

    /// <summary>Best-effort removal of the staging/backup leftovers, called on a normal
    /// startup: the updater can't delete the tree it is running from, so the freshly
    /// updated K2 does it instead. Runs off the UI thread and never throws.</summary>
    public static void CleanupAfterUpdate()
    {
        if (!Directory.Exists(UpdateRoot)) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Give a just-relaunched updater a moment to exit and release its files.
            Thread.Sleep(5000);
            try
            {
                DeleteDirectory(StagingDir);
                DeleteDirectory(BackupDir);
                foreach (var zip in Directory.EnumerateFiles(UpdateRoot, "*.zip"))
                    TryDelete(zip);
            }
            catch { /* leftovers are harmless — the next run tries again */ }
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Step 3: the updater itself (runs from the staged tree)
    // ─────────────────────────────────────────────────────────────────────

    private static void RunApplyMode(string installDir, int pid)
    {
        int code = 0;
        try
        {
            string source = InstallDir; // we ARE the staged tree
            Log($"=== apply-update start {DateTime.Now:O} pid={Environment.ProcessId} ===");
            Log($"source='{source}' target='{installDir}' waiting for pid={pid}");

            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                throw new InvalidOperationException($"target folder '{installDir}' does not exist");
            if (PathsEqual(source, installDir))
                throw new InvalidOperationException("source and target are the same folder — refusing to update in place");

            WaitForK2Exit(installDir, pid);
            MirrorTree(source, installDir);
            UpdateInnoDisplayVersion(FileVersion(Path.Combine(source, "K2.App.exe")));
            Log("apply-update done");
        }
        catch (Exception ex)
        {
            code = 1;
            Log($"apply-update FAILED: {ex}");
        }

        Relaunch(installDir);
        Environment.Exit(code);
    }

    /// <summary>Waits for the old K2 (and its helper processes) to let go of the install
    /// tree. The helpers are killed if they outstay the grace period: they are
    /// restarted by K2 on demand, and a lingering one would lock its own exe.</summary>
    private static void WaitForK2Exit(string installDir, int pid)
    {
        if (pid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.WaitForExit(120_000))
                    Log($"pid {pid} still alive after 120s — continuing anyway");
            }
            catch (ArgumentException) { /* already gone */ }
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var stragglers = K2ProcessesIn(installDir);
            if (stragglers.Count == 0) break;

            if (DateTime.UtcNow > deadline)
            {
                foreach (var p in stragglers)
                {
                    Log($"killing straggler {p.ProcessName} pid={p.Id}");
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
                    p.Dispose();
                }
                break;
            }

            foreach (var p in stragglers) p.Dispose();
            Thread.Sleep(500);
        }
    }

    private static List<Process> K2ProcessesIn(string installDir)
    {
        var found = new List<Process>();
        foreach (string exeName in K2ExeNames)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                if (p.Id == Environment.ProcessId) { p.Dispose(); continue; }
                string? path = TryGetProcessPath(p);
                if (path is not null && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    found.Add(p);
                else
                    p.Dispose();
            }
        }
        return found;
    }

    private static string? TryGetProcessPath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    /// <summary>Copies every file that differs from <paramref name="source"/> into
    /// <paramref name="target"/>, keeping the replaced originals in <see cref="BackupDir"/>
    /// so a mid-way failure can be rolled back. Nothing is ever deleted from the target
    /// (see the file header). Identical files — in practice the whole .NET runtime, i.e.
    /// most of the ~250 MB tree — are skipped, so a normal update copies a few MB.</summary>
    private static void MirrorTree(string source, string target)
    {
        DeleteDirectory(BackupDir);
        Directory.CreateDirectory(BackupDir);

        var copied = new List<(string Rel, bool HadBackup)>();
        int skipped = 0;

        try
        {
            foreach (string src in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(source, src);
                string dst = Path.Combine(target, rel);

                if (File.Exists(dst) && SameContent(src, dst)) { skipped++; continue; }

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                bool hadBackup = false;
                if (File.Exists(dst))
                {
                    string bak = Path.Combine(BackupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    File.Move(dst, bak, overwrite: true);
                    hadBackup = true;
                }

                CopyWithRetry(src, dst);
                copied.Add((rel, hadBackup));
            }

            Log($"mirror done: {copied.Count} file(s) updated, {skipped} unchanged");
        }
        catch (Exception ex)
        {
            Log($"mirror failed ({ex.Message}) — rolling back {copied.Count} file(s)");
            foreach (var (rel, hadBackup) in copied)
            {
                try
                {
                    string dst = Path.Combine(target, rel);
                    if (hadBackup)
                        File.Move(Path.Combine(BackupDir, rel), dst, overwrite: true);
                    else
                        TryDelete(dst);
                }
                catch (Exception rex) { Log($"  rollback of '{rel}' failed: {rex.Message}"); }
            }
            throw;
        }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Copy(src, dst, overwrite: true); return; }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500 * attempt);
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;

            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
        }
        catch { return false; }
    }

    /// <summary>Keeps "Apps &amp; features" honest after an in-place update. Best effort:
    /// a portable copy has no such key, and neither does a per-user install seen from
    /// the wrong hive — both are fine, nothing else depends on this value.</summary>
    private static void UpdateInnoDisplayVersion(Version v)
    {
        string display = $"{v.Major}.{v.Minor}.{v.Build}";
        foreach (var (root, path) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId),
            (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + InnoAppId),
        })
        {
            try
            {
                using var key = root.OpenSubKey(path, writable: true);
                if (key is null) continue;
                key.SetValue("DisplayVersion", display);
                Log($"DisplayVersion -> {display} in {root.Name}\\{path}");
            }
            catch (Exception ex) { Log($"DisplayVersion update skipped: {ex.Message}"); }
        }
    }

    private static void Relaunch(string installDir)
    {
        try
        {
            string exe = Path.Combine(installDir, "K2.App.exe");
            if (!File.Exists(exe)) { Log($"cannot relaunch: '{exe}' missing"); return; }
            Log($"relaunching '{exe}'");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDir });
        }
        catch (Exception ex) { Log($"relaunch failed: {ex.Message}"); }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Local test mode
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>--test-update &lt;zip|folder&gt;: runs the real update path against a local
    /// build instead of a GitHub release. A folder (e.g. Installer\publish\K2.App) is
    /// used as the staged tree as-is — no zip, no download, so the test loop is just
    /// "publish, then run this". The target is always the folder this exe runs from.</summary>
    private static void RunTestMode(string source)
    {
        try
        {
            Log($"=== test-update start {DateTime.Now:O} source='{source}' target='{InstallDir}' ===");
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidOperationException($"usage: K2.App.exe {TestSwitch} \"<zip or folder>\"");

            string staged;
            if (Directory.Exists(source))
            {
                staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
                VerifyStagedTree(staged);
            }
            else if (File.Exists(source))
            {
                StageZip(source);
                staged = StagingDir;
            }
            else throw new FileNotFoundException($"'{source}' not found");

            LaunchApply(staged, InstallDir);
            Log("test-update: updater launched, this process exits now");
        }
        catch (Exception ex)
        {
            Log($"test-update FAILED: {ex}");
            Environment.Exit(1);
        }

        Environment.Exit(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    public static Version FileVersion(string exePath)
    {
        var fvi = FileVersionInfo.GetVersionInfo(exePath);
        return Version.TryParse(fvi.FileVersion, out var v) ? v : new Version(0, 0, 0);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    private static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Log($"could not delete '{dir}': {ex.Message}"); }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    /// <summary>Own log file, deliberately NOT K2.App.log: in --apply-update mode the
    /// old K2 may still be running and holding that one, and the update trail must
    /// survive the restart that follows.</summary>
    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(UpdateRoot);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
    }
}
