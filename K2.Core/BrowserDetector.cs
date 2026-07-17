using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace K2.Core;

/// <summary>One browser found on this machine (id/name are stable, path may change on update/reinstall).</summary>
public sealed record InstalledBrowser(string Id, string Name, string ExePath);

/// <summary>
/// Detects the well-known browsers K2 offers as one-click "Open browser" targets, via the
/// standard Windows App Paths registry key (same mechanism Explorer/Run use to resolve a
/// bare exe name): HKLM/HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}.
/// The key's default value holds the full path to the executable.
/// </summary>
public static class BrowserDetector
{
    private static readonly (string Id, string Name, string Exe)[] Known =
    {
        ("chrome",  "Google Chrome", "chrome.exe"),
        ("edge",    "Microsoft Edge", "msedge.exe"),
        ("firefox", "Mozilla Firefox", "firefox.exe"),
        ("opera",   "Opera", "opera.exe"),
        ("brave",   "Brave", "brave.exe"),
    };

    /// <summary>Returns only the browsers actually found on this machine.</summary>
    public static IReadOnlyList<InstalledBrowser> DetectInstalled()
    {
        var found = new List<InstalledBrowser>();
        foreach (var (id, name, exe) in Known)
        {
            var path = ResolveAppPath(exe);
            if (!string.IsNullOrEmpty(path))
                found.Add(new InstalledBrowser(id, name, path!));
        }
        return found;
    }

    /// <summary>Re-resolves a known browser id's current exe path (survives updates/reinstalls); null if not found.</summary>
    public static string? ResolveById(string id)
    {
        foreach (var (kid, _, exe) in Known)
            if (string.Equals(kid, id, StringComparison.OrdinalIgnoreCase))
                return ResolveAppPath(exe);
        return null;
    }

    /// <summary>Returns the known browser id whose executable filename matches <paramref name="execPath"/>
    /// (e.g. "C:\...\chrome.exe" → "chrome"), or null if it's not one of the well-known browsers this
    /// class tracks. Used when importing a generic "run program" action that happens to point at a
    /// browser executable, so it becomes K2's native "browser" action instead of a plain exec.</summary>
    public static string? TryIdentifyByExeName(string? execPath)
    {
        if (string.IsNullOrWhiteSpace(execPath)) return null;
        string fileName = Path.GetFileName(execPath);
        foreach (var (id, _, exe) in Known)
            if (string.Equals(exe, fileName, StringComparison.OrdinalIgnoreCase))
                return id;
        return null;
    }

    private static string? ResolveAppPath(string exeName)
    {
        const string keyBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(keyBase + exeName);
            if (hklm?.GetValue(null) is string p1 && !string.IsNullOrWhiteSpace(p1)) return p1;
            using var hkcu = Registry.CurrentUser.OpenSubKey(keyBase + exeName);
            if (hkcu?.GetValue(null) is string p2 && !string.IsNullOrWhiteSpace(p2)) return p2;
        }
        catch { /* registry access issue: treat as "not installed" */ }
        return null;
    }
}
