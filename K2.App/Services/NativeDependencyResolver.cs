using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Resolves at runtime the native DLLs that are <b>not redistributable</b> because
/// they are internal Base Camp components (today: <c>MacroPadSDK.dll</c>).
///
/// <para>
/// K2 is distributed <b>without</b> these DLLs. To make the loader find them,
/// a <see cref="DllImportResolver"/> is registered on the K2.App assembly:
/// when the CLR needs to load one of these libraries, it looks — in order —
/// </para>
/// <list type="number">
///   <item>next to <c>K2.App.exe</c> (the user copied it there manually);</item>
///   <item>in the folder indicated by the <c>K2_BASECAMP_DIR</c> environment
///         variable (explicit override);</item>
///   <item>in a Base Camp installation found via the system registry
///         and typical install paths.</item>
/// </list>
///
/// <para>
/// This way K2 remains a clean redistributable package: the end user either copies
/// the DLL next to the executable, or keeps Base Camp installed (freely
/// downloadable) and K2 "hooks into" it. See <c>DISTRIBUTION.md</c>.
/// </para>
/// </summary>
internal static class NativeDependencyResolver
{
    /// <summary>
    /// Non-redistributable native DLLs to look for in a Base Camp
    /// installation.
    /// <list type="bullet">
    ///   <item><c>MacroPadSDK.dll</c> — MacroPad</item>
    ///   <item><c>SDKDLL.dll</c>      — Everest Max keyboard (wrapper
    ///         <c>BaseCamp.Service.Helpers.Everest</c>)</item>
    ///   <item><c>Everest360_USB.dll</c> — Everest 60 keyboard, Key Binding +
    ///         numpad detection only (wrapper
    ///         <c>BaseCamp.Service.Helpers.Everest60</c> — see
    ///         <see cref="Everest60SdkNative"/>). Lighting stays on raw HID
    ///         and doesn't need this DLL. Added 2026-07-11 alongside
    ///         <c>Everest60SdkNative</c>/<c>Everest60SdkService</c> — this
    ///         list wasn't updated at the time, which meant
    ///         <c>OpenUSBDriver</c> always threw <c>DllNotFoundException</c>
    ///         (root cause of a "numpad not detected" report — the CLR
    ///         never even looked in a Base Camp install, it just tried the
    ///         default OS search path and gave up).</item>
    /// </list>
    /// </summary>
    public static readonly string[] BaseCampNativeDlls = { "MacroPadSDK.dll", "SDKDLL.dll", "Everest360_USB.dll" };

    /// <summary>Environment variable used to force the Base Camp folder.</summary>
    public const string BaseCampDirEnvVar = "K2_BASECAMP_DIR";

    private static bool _installed;
    private static string[]? _baseCampDirsCache;

    /// <summary>
    /// Registers the resolver. Must be called exactly once, at app startup,
    /// before any P/Invoke to Base Camp's native DLLs.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(MacroPadSdkNative).Assembly, Resolve);
            App.WriteLog("[NativeResolver] DllImportResolver registered");
        }
        catch (Exception ex)
        {
            // SetDllImportResolver throws if already registered: not fatal.
            App.WriteLog("[NativeResolver] Install threw: " + ex.Message);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only handle Base Camp DLLs; everything else goes to the standard loader.
        if (!IsBaseCampDll(libraryName))
            return IntPtr.Zero;

        foreach (var candidate in CandidatePaths(libraryName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                App.WriteLog($"[NativeResolver] '{libraryName}' loaded from: {candidate}");
                return handle;
            }
        }

        App.WriteLog($"[NativeResolver] '{libraryName}' NOT found. Paths tried:" +
                     Environment.NewLine + "  " +
                     string.Join(Environment.NewLine + "  ", CandidatePaths(libraryName)));
        // IntPtr.Zero -> the CLR will proceed and throw DllNotFoundException,
        // handled upstream with a clear message (MainWindow/MacroPadService).
        return IntPtr.Zero;
    }

    /// <summary>True if the name matches a Base Camp native DLL.</summary>
    public static bool IsBaseCampDll(string libraryName) =>
        BaseCampNativeDlls.Any(d =>
            string.Equals(d, libraryName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(d),
                          Path.GetFileNameWithoutExtension(libraryName),
                          StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the DLL is reachable at at least one candidate path.</summary>
    public static bool IsResolvable(string libraryName) =>
        CandidatePaths(libraryName).Any(File.Exists);

    /// <summary>
    /// Paths to search for <paramref name="dll"/>, in priority order.
    /// Also exposed for diagnostics (see <see cref="DescribeSearch"/>).
    /// </summary>
    public static IEnumerable<string> CandidatePaths(string dll)
    {
        // 1) next to the executable
        yield return Path.Combine(AppContext.BaseDirectory, dll);

        // 2) explicit override via environment variable
        var env = Environment.GetEnvironmentVariable(BaseCampDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.Combine(env.Trim(), dll);

        // 3) detected Base Camp installations
        foreach (var dir in BaseCampDirectories())
            yield return Path.Combine(dir, dll);
    }

    /// <summary>Readable diagnostics: where it was searched and whether it was found.</summary>
    public static string DescribeSearch(string dll)
    {
        var lines = new List<string> { $"Searching for '{dll}':" };
        bool any = false;
        foreach (var p in CandidatePaths(dll))
        {
            bool ok = File.Exists(p);
            any |= ok;
            lines.Add($"  [{(ok ? "FOUND" : "missing")}] {p}");
        }
        lines.Add(any ? "Result: resolvable." : "Result: NOT resolvable.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Detected Base Camp installation folders (cached).</summary>
    public static string[] BaseCampDirectories()
    {
        if (_baseCampDirsCache != null) return _baseCampDirsCache;

        var dirs = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            d = d.Trim();
            if (Directory.Exists(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d);
        }

        // Typical installation paths (Base Camp is an Electron app).
        string?[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            Add(Path.Combine(root, "Mountain", "Base Camp"));
            Add(Path.Combine(root, "Base Camp"));
            Add(Path.Combine(root, "Programs", "Base Camp"));
            Add(Path.Combine(root, "Programs", "base-camp"));
        }

        // Registry: uninstall keys with DisplayName "Base Camp".
        foreach (var d in RegistryInstallLocations())
            Add(d);

        _baseCampDirsCache = dirs.ToArray();
        App.WriteLog($"[NativeResolver] Base Camp installations found: " +
                     (_baseCampDirsCache.Length == 0
                         ? "none"
                         : string.Join(" | ", _baseCampDirsCache)));
        return _baseCampDirsCache;
    }

    private static IEnumerable<string> RegistryInstallLocations()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var probes = new (RegistryHive hive, RegistryView view)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser,  RegistryView.Registry64),
        };

        var found = new List<string>();
        foreach (var (hive, view) in probes)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var unins = baseKey.OpenSubKey(uninstall);
                if (unins is null) continue;

                foreach (var subName in unins.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = unins.OpenSubKey(subName);
                        if (sub?.GetValue("DisplayName") is not string name) continue;
                        if (name.IndexOf("Base Camp", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (sub.GetValue("InstallLocation") is string loc &&
                            !string.IsNullOrWhiteSpace(loc))
                            found.Add(loc);
                    }
                    catch { /* unreadable key: ignore */ }
                }
            }
            catch { /* hive/view not accessible: ignore */ }
        }
        return found;
    }
}
