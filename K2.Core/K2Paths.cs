using System;
using System.IO;

namespace K2.Core;

/// <summary>
/// Single top-level data root for every K2 process (user request 2026-07-22:
/// K2.App, K2.DisplayPad and K2.DisplayPad.AppSide used to each live as their
/// own sibling folder directly under %LocalAppData%, alongside the shared "K2"
/// folder used by <see cref="AppSettings"/>/<see cref="Loc"/> — now all of them
/// nest under that one "K2" folder instead.
/// </summary>
public static class K2Paths
{
    /// <summary>%LocalAppData%\K2</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K2");

    /// <summary>
    /// %LocalAppData%\K2\<paramref name="name"/> — e.g. <c>For("K2.App")</c>,
    /// <c>For("K2.DisplayPad")</c>. Also migrates a legacy top-level
    /// %LocalAppData%\<paramref name="name"/> folder into it the first time it's
    /// asked for, best-effort. Multiple K2 processes can share the same
    /// <paramref name="name"/> (K2.App, K2.DisplayPad.exe and the Satellite all
    /// write under "K2.DisplayPad") and may race to migrate it concurrently —
    /// a losing race just no-ops against a legacy folder the winner already moved.
    /// </summary>
    public static string For(string name)
    {
        string dest = Path.Combine(Root, name);
        MigrateLegacy(name, dest);
        return dest;
    }

    private static void MigrateLegacy(string name, string dest)
    {
        try
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
            if (!Directory.Exists(legacy)) return;

            Directory.CreateDirectory(Root);
            if (!Directory.Exists(dest))
            {
                Directory.Move(legacy, dest);
                return;
            }
            MergeInto(legacy, dest);
        }
        catch { /* best-effort — a locked file or a losing race just leaves the
                   legacy folder behind for the next launch to retry */ }
    }

    /// <summary>Destination already has content (partial migration, or another
    /// process recreated it) — merge file-by-file/dir-by-dir instead of a
    /// whole-folder move, then remove the now-empty source.</summary>
    private static void MergeInto(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(dir));
            if (Directory.Exists(target)) MergeInto(dir, target);
            else Directory.Move(dir, target);
        }
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(file));
            if (!File.Exists(target)) File.Move(file, target);
        }
        Directory.Delete(sourceDir, recursive: true);
    }
}
