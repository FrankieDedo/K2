using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Finds a game's executable on THIS machine and extracts its icon, so a game profile card can
/// show the game rather than a generic placeholder.
///
/// <para>
/// The catalogue deliberately carries only a process NAME (what the launch watcher matches on),
/// never a path: an install location is per-machine, and hard-coding one would be wrong for
/// everybody but the person it was written on. So the path is resolved at runtime, cheapest and
/// most certain source first:
/// <list type="number">
/// <item>a path already resolved once and remembered;</item>
/// <item>the running process itself — exact, and free when the game is up;</item>
/// <item>Steam's own metadata, for a profile that names an AppID: the library list from Steam's
/// registered path, then that app's manifest for its install folder.</item>
/// </list>
/// A profile whose game is installed by neither route simply has no icon until the game is run
/// once, which is when route 2 catches it.
/// </para>
/// </summary>
internal static class GameExeResolver
{
    private static readonly string IconCache =
        Path.Combine(Path.GetTempPath(), "K2.GameIcons");

    /// <summary>Full path of the game's executable, or null when it can't be found on this
    /// machine. <paramref name="remembered"/> is whatever a previous call stored (the caller owns
    /// persistence); when this returns a path the caller should remember it.</summary>
    public static string? Resolve(string exeName, int? steamAppId, string? remembered)
    {
        if (!string.IsNullOrEmpty(remembered) && File.Exists(remembered)) return remembered;

        if (FromRunningProcess(exeName) is { } running) return running;
        if (steamAppId is int id && FromSteam(id, exeName) is { } steam) return steam;
        return null;
    }

    /// <summary>The executable of the game if it happens to be running. Reading another process's
    /// MainModule can be denied (elevation mismatch, a protected process) — that is expected, not
    /// an error worth surfacing.</summary>
    private static string? FromRunningProcess(string exeName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(exeName))
                using (p)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                    }
                    catch { /* access denied for this one; try the next */ }
                }
        }
        catch { }
        return null;
    }

    /// <summary>Walks Steam's own metadata rather than scanning drives: the registered Steam path,
    /// every library folder it lists, and the app's manifest for the folder it installed into.</summary>
    private static string? FromSteam(int appId, string exeName)
    {
        try
        {
            string? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                                ?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steam)) return null;
            steam = steam.Replace('/', '\\');

            foreach (string lib in SteamLibraries(steam))
            {
                string manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(manifest)) continue;

                string? installDir = AcfValue(File.ReadAllText(manifest), "installdir");
                if (string.IsNullOrEmpty(installDir)) continue;

                string root = Path.Combine(lib, "steamapps", "common", installDir);
                if (!Directory.Exists(root)) continue;

                // The launcher often sits at the root and the real binary several folders down
                // (Elite's is under Products\...\); a bounded search finds either.
                return FindExe(root, exeName, depth: 5);
            }
        }
        catch { }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> SteamLibraries(string steamPath)
    {
        yield return steamPath;

        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        // The file is Valve's KeyValues, not JSON; every library appears as a "path" entry and
        // that is the only field needed here, so a targeted scan beats a full VDF parser.
        foreach (var line in text.Split('\n'))
        {
            string t = line.Trim();
            if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = t.Split('"').Where(x => x.Trim().Length > 0).ToArray();
            if (parts.Length >= 2)
            {
                string p = parts[^1].Replace("\\\\", "\\");
                if (Directory.Exists(p)) yield return p;
            }
        }
    }

    private static string? AcfValue(string acf, string key)
    {
        foreach (var line in acf.Split('\n'))
        {
            string t = line.Trim();
            if (!t.StartsWith($"\"{key}\"", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = t.Split('"').Where(x => x.Trim().Length > 0).ToArray();
            if (parts.Length >= 2) return parts[^1];
        }
        return null;
    }

    private static string? FindExe(string root, string exeName, int depth)
    {
        try
        {
            string direct = Path.Combine(root, exeName + ".exe");
            if (File.Exists(direct)) return direct;

            if (depth <= 0) return null;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (FindExe(dir, exeName, depth - 1) is { } hit) return hit;
        }
        catch { }
        return null;
    }

    /// <summary>Extracts the executable's icon to a PNG and returns its path (cached per
    /// executable+timestamp), or null when there is nothing to extract. WPF cannot bind an
    /// <see cref="Icon"/> directly, and re-extracting on every list refresh would hit the disk
    /// for each card.</summary>
    public static string? IconPngFor(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
        try
        {
            var info = new FileInfo(exePath);
            string stamp = $"{Path.GetFileNameWithoutExtension(exePath)}_{info.LastWriteTimeUtc.Ticks}";
            string outPath = Path.Combine(IconCache, stamp + ".png");
            if (File.Exists(outPath)) return outPath;

            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;

            Directory.CreateDirectory(IconCache);
            using var bmp = icon.ToBitmap();
            bmp.Save(outPath, ImageFormat.Png);
            return outPath;
        }
        catch { return null; }
    }
}
