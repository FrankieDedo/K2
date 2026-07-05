using System;
using System.Collections.Generic;
using System.IO;

namespace K2.Core;

/// <summary>
/// Locates at runtime the embeddable Python interpreter and the
/// <c>k2_runner.py</c> bootstrap used to execute button-bound scripts.
///
/// Interpreter search order:
///   1. path configured in settings (DB);
///   2. <c>K2_PYTHON_DIR</c> environment variable;
///   3. <c>python-embed\python.exe</c> next to the executable (distributed package);
///   4. <c>lib\python-embed\python.exe</c> walking up parent directories (repo layout).
/// </summary>
public static class PythonRuntimeLocator
{
    /// <summary>Returns the path to <c>python.exe</c> or null.</summary>
    public static string? FindPythonExe(string? configured)
    {
        var fromConfig = NormalizeExe(configured);
        if (fromConfig != null) return fromConfig;

        var fromEnv = NormalizeExe(Environment.GetEnvironmentVariable("K2_PYTHON_DIR"));
        if (fromEnv != null) return fromEnv;

        foreach (var dir in CandidateBaseDirs())
        {
            var direct = Path.Combine(dir, "python-embed", "python.exe");
            if (File.Exists(direct)) return direct;

            var underLib = Path.Combine(dir, "lib", "python-embed", "python.exe");
            if (File.Exists(underLib)) return underLib;
        }
        return null;
    }

    /// <summary>Returns the path to <c>k2_runner.py</c> or null.</summary>
    public static string? FindRunnerScript()
    {
        foreach (var dir in CandidateBaseDirs())
        {
            var direct = Path.Combine(dir, "pybridge", "k2_runner.py");
            if (File.Exists(direct)) return direct;

            var underLib = Path.Combine(dir, "lib", "pybridge", "k2_runner.py");
            if (File.Exists(underLib)) return underLib;
        }
        return null;
    }

    /// <summary>True if both the interpreter and the runner are available.</summary>
    public static bool IsReady(string? configuredPython)
        => FindPythonExe(configuredPython) != null && FindRunnerScript() != null;

    // ------------------------------------------------------------------

    /// <summary>Accepts either the direct path to python.exe or a directory containing it.</summary>
    private static string? NormalizeExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');

        if (File.Exists(path) &&
            string.Equals(Path.GetFileName(path), "python.exe", StringComparison.OrdinalIgnoreCase))
            return path;

        if (Directory.Exists(path))
        {
            var exe = Path.Combine(path, "python.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Exe directory and its ancestors (to find the repo layout).</summary>
    private static IEnumerable<string> CandidateBaseDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir  = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar,
                                                    Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 9 && !string.IsNullOrEmpty(dir); i++)
        {
            if (seen.Add(dir)) yield return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
    }
}
