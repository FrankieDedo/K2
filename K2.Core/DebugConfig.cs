using System;
using System.Collections.Generic;
using System.IO;

namespace K2.Core;

/// <summary>
/// Developer-only flags read from a PLAIN TEXT config file in
/// <c>%LOCALAPPDATA%\K2\k2_debug.cfg</c> — deliberately NOT part of
/// <see cref="AppSettings"/>'s JSON and NOT exposed in the Settings UI
/// (the old "Debug mode" checkbox in the Danger Zone was removed 2026-08-27).
///
/// <para>Rationale: debug mode gates diagnostics and experimental features that
/// must never be reachable by accident from a normal install. Everything here
/// defaults to OFF; enabling it means opening a text file by hand.</para>
///
/// <para>Format — one <c>key=value</c> per line, <c>#</c> or <c>;</c> starts a
/// comment, keys are case-insensitive, truthy values are <c>1/true/yes/on</c>:</para>
/// <code>
/// # K2 debug configuration
/// debug=0
/// </code>
///
/// <para>Read ONCE per process (first access) and cached: changing the file
/// requires restarting K2. The file is created with a commented template the
/// first time it is missing, so it is discoverable without documentation.</para>
/// </summary>
public static class DebugConfig
{
    private const string FileName = "k2_debug.cfg";

    private static readonly object _lock = new();
    private static Dictionary<string, string>? _values;

    /// <summary>Full path of the config file (created on first read if missing).</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", FileName);

    /// <summary>Master debug switch (<c>debug=1</c>). Default <c>false</c>.</summary>
    public static bool Enabled => GetBool("debug");

    /// <summary>Re-reads the file from disk (nothing in K2 calls this yet — the flag is
    /// read at startup; kept so a future "reload debug config" action is a one-liner).</summary>
    public static void Reload()
    {
        lock (_lock) _values = null;
    }

    /// <summary>Generic accessor for any other key that may be added to the file later.</summary>
    public static bool GetBool(string key, bool fallback = false)
    {
        var map = EnsureLoaded();
        if (!map.TryGetValue(key, out var v)) return fallback;
        v = v.Trim();
        return v is "1" or "true" or "yes" or "on" or "True" or "TRUE";
    }

    private static Dictionary<string, string> EnsureLoaded()
    {
        lock (_lock)
        {
            if (_values is not null) return _values;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path;
                if (!File.Exists(path))
                {
                    WriteTemplate(path);
                }
                else
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                    }
                }
            }
            catch
            {
                // Unreadable/locked file: every flag simply stays at its default (off).
            }

            return _values = map;
        }
    }

    private static void WriteTemplate(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "# K2 debug configuration" + Environment.NewLine +
                "# Developer-only flags. Restart K2 after editing this file." + Environment.NewLine +
                "#" + Environment.NewLine +
                "# debug=1 enables debug UI and diagnostics on every device" + Environment.NewLine +
                "# (LED indices on the key overlays, extra diagnostic panels, ...)." + Environment.NewLine +
                "debug=0" + Environment.NewLine);
        }
        catch
        {
            // Read-only profile folder: not fatal, the defaults still apply.
        }
    }
}
