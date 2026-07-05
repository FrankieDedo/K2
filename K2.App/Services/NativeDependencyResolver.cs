using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Risolve a runtime le DLL native che <b>non sono ridistribuibili</b> perche'
/// componenti interni di Base Camp (oggi: <c>MacroPadSDK.dll</c>).
///
/// <para>
/// K2 viene distribuito <b>senza</b> queste DLL. Per farle trovare al loader
/// si registra un <see cref="DllImportResolver"/> sull'assembly di K2.App:
/// quando il CLR deve caricare una di queste librerie, la cerca — in ordine —
/// </para>
/// <list type="number">
///   <item>accanto a <c>K2.App.exe</c> (l'utente l'ha copiata li' a mano);</item>
///   <item>nella cartella indicata dalla variabile d'ambiente
///         <c>K2_BASECAMP_DIR</c> (override esplicito);</item>
///   <item>in una installazione di Base Camp individuata da registro di
///         sistema e percorsi d'installazione tipici.</item>
/// </list>
///
/// <para>
/// Cosi' K2 resta un pacchetto redistribuibile pulito: l'utente finale o copia
/// la DLL accanto all'eseguibile, oppure tiene installato Base Camp (scaricabile
/// liberamente) e K2 vi si "aggancia". Vedi <c>DISTRIBUTION.md</c>.
/// </para>
/// </summary>
internal static class NativeDependencyResolver
{
    /// <summary>
    /// DLL native NON ridistribuibili da cercare in una installazione di Base
    /// Camp.
    /// <list type="bullet">
    ///   <item><c>MacroPadSDK.dll</c> — MacroPad</item>
    ///   <item><c>SDKDLL.dll</c>      — tastiera Everest Max (wrapper
    ///         <c>BaseCamp.Service.Helpers.Everest</c>)</item>
    /// </list>
    /// <c>Everest360_USB.dll</c> (Everest 60) is <b>not</b> in the list: the module
    /// Everest di K2 non lo usa, vedi commento in <see cref="EverestSdkNative"/>.
    /// </summary>
    public static readonly string[] BaseCampNativeDlls = { "MacroPadSDK.dll", "SDKDLL.dll" };

    /// <summary>Variabile d'ambiente con cui forzare la cartella di Base Camp.</summary>
    public const string BaseCampDirEnvVar = "K2_BASECAMP_DIR";

    private static bool _installed;
    private static string[]? _baseCampDirsCache;

    /// <summary>
    /// Registra il resolver. Va chiamato una sola volta, all'avvio dell'app,
    /// prima di qualunque P/Invoke verso le DLL native di Base Camp.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(MacroPadSdkNative).Assembly, Resolve);
            App.WriteLog("[NativeResolver] DllImportResolver registrato");
        }
        catch (Exception ex)
        {
            // SetDllImportResolver lancia se gia' registrato: non e' fatale.
            App.WriteLog("[NativeResolver] Install ha lanciato: " + ex.Message);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Gestiamo solo le DLL Base Camp; tutto il resto al loader standard.
        if (!IsBaseCampDll(libraryName))
            return IntPtr.Zero;

        foreach (var candidate in CandidatePaths(libraryName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                App.WriteLog($"[NativeResolver] '{libraryName}' caricata da: {candidate}");
                return handle;
            }
        }

        App.WriteLog($"[NativeResolver] '{libraryName}' NON trovata. Percorsi provati:" +
                     Environment.NewLine + "  " +
                     string.Join(Environment.NewLine + "  ", CandidatePaths(libraryName)));
        // IntPtr.Zero -> il CLR proseguira' e lancera' DllNotFoundException,
        // gestita con un messaggio chiaro a monte (MainWindow/MacroPadService).
        return IntPtr.Zero;
    }

    /// <summary>True se il nome corrisponde a una DLL nativa di Base Camp.</summary>
    public static bool IsBaseCampDll(string libraryName) =>
        BaseCampNativeDlls.Any(d =>
            string.Equals(d, libraryName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(d),
                          Path.GetFileNameWithoutExtension(libraryName),
                          StringComparison.OrdinalIgnoreCase));

    /// <summary>True se la DLL e' raggiungibile in almeno un percorso candidato.</summary>
    public static bool IsResolvable(string libraryName) =>
        CandidatePaths(libraryName).Any(File.Exists);

    /// <summary>
    /// Percorsi in cui cercare <paramref name="dll"/>, in ordine di priorita'.
    /// Esposto anche per diagnostica (vedi <see cref="DescribeSearch"/>).
    /// </summary>
    public static IEnumerable<string> CandidatePaths(string dll)
    {
        // 1) accanto all'eseguibile
        yield return Path.Combine(AppContext.BaseDirectory, dll);

        // 2) override esplicito via variabile d'ambiente
        var env = Environment.GetEnvironmentVariable(BaseCampDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.Combine(env.Trim(), dll);

        // 3) installazioni di Base Camp individuate
        foreach (var dir in BaseCampDirectories())
            yield return Path.Combine(dir, dll);
    }

    /// <summary>Diagnostica leggibile: dove e' stata cercata e se e' stata trovata.</summary>
    public static string DescribeSearch(string dll)
    {
        var lines = new List<string> { $"Ricerca di '{dll}':" };
        bool any = false;
        foreach (var p in CandidatePaths(dll))
        {
            bool ok = File.Exists(p);
            any |= ok;
            lines.Add($"  [{(ok ? "TROVATA" : "assente")}] {p}");
        }
        lines.Add(any ? "Esito: risolvibile." : "Esito: NON risolvibile.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Cartelle di installazione di Base Camp individuate (con cache).</summary>
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

        // Percorsi d'installazione tipici (Base Camp e' un'app Electron).
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

        // Registro: chiavi di disinstallazione con DisplayName "Base Camp".
        foreach (var d in RegistryInstallLocations())
            Add(d);

        _baseCampDirsCache = dirs.ToArray();
        App.WriteLog($"[NativeResolver] installazioni Base Camp trovate: " +
                     (_baseCampDirsCache.Length == 0
                         ? "nessuna"
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
                    catch { /* chiave illeggibile: ignora */ }
                }
            }
            catch { /* hive/vista non accessibile: ignora */ }
        }
        return found;
    }
}
