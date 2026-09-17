// GameProfileModuleLoader.cs — finds the game-profile module on disk and takes it into the process.
//
// See K2.Core/GameProfileModule.cs for what a module is and why. This file is only the "where from
// and how", and its whole job is to be unable to break K2: every failure here — no module, a module
// built against a newer contract, a corrupt DLL — ends in a log line and an app that starts with
// whatever profiles it already had.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using K2.Core;

namespace K2.App.Services;

public static class GameProfileModuleLoader
{
    /// <summary>The module assembly's file name, in every location it can be found.</summary>
    public const string ModuleFileName = "K2.GameProfiles.dll";

    /// <summary>Points the loader at a module build output instead of an installed copy, so the
    /// module can be worked on without packaging it. Development only; unset on a user's machine.</summary>
    private const string DevPathVariable = "K2_GAMEPROFILES_DIR";

    private const string ModulesFolder = "Modules";
    private const string GameProfilesFolder = "GameProfiles";

    /// <summary>Where a downloaded module is staged. Under LocalAppData, not next to the exe: an
    /// installed K2 lives in Program Files, which K2 cannot write to without elevation — the same
    /// reason <see cref="SelfUpdate"/> stages there.</summary>
    public static string StagingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", ModulesFolder, GameProfilesFolder);

    /// <summary>The copy the installer ships, used until an update replaces it.</summary>
    private static string BundledDir => Path.Combine(
        AppContext.BaseDirectory, ModulesFolder, GameProfilesFolder);

    /// <summary>Loads the newest module available and publishes what it contributes. Called once at
    /// startup, before the catalogue is first read.</summary>
    public static void LoadAll(IGameProfileHost host, Action<string> log)
    {
        try
        {
            string? dll = SelectModule(log);
            if (dll is null) { log("game profiles: no module found"); return; }

            var asm = new GameProfileLoadContext(dll).LoadFromAssemblyPath(dll);

            var types = asm.GetTypes().Where(t =>
                typeof(IGameProfileModule).IsAssignableFrom(t) &&
                !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null).ToList();

            if (types.Count == 0) { log($"game profiles: no IGameProfileModule in {dll}"); return; }

            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IGameProfileModule module) continue;

                if (!GameProfileModules.Register(module, Path.GetDirectoryName(dll)!))
                {
                    log($"game profiles: refused {type.FullName} " +
                        $"(api {module.ApiVersion} > {GameProfileModules.HostApiVersion}, or duplicate id)");
                    continue;
                }

                module.Initialize(host);
                Loc.AddStrings(module.GetStrings(Loc.CurrentLang));
                log($"game profiles: loaded {module.Id} " +
                    $"({module.Definitions.Count} profiles) from {dll}");
            }
        }
        catch (Exception ex)
        {
            log($"game profiles: module load failed — {ex.Message}");
        }
    }

    /// <summary>The module to load: a developer's build output if one is pointed at, otherwise the
    /// highest-versioned of the staged copies and the bundled one.</summary>
    private static string? SelectModule(Action<string> log)
    {
        string? dev = Environment.GetEnvironmentVariable(DevPathVariable);
        if (!string.IsNullOrWhiteSpace(dev))
        {
            string devDll = Path.Combine(dev, ModuleFileName);
            if (File.Exists(devDll)) { log($"game profiles: using dev module {devDll}"); return devDll; }
            log($"game profiles: {DevPathVariable} set but no {ModuleFileName} under it");
        }

        var candidates = new List<string>();
        if (Directory.Exists(StagingRoot))
            candidates.AddRange(Directory.GetDirectories(StagingRoot)
                                         .Select(d => Path.Combine(d, ModuleFileName)));
        candidates.Add(Path.Combine(BundledDir, ModuleFileName));

        return candidates.Where(File.Exists)
                         .OrderByDescending(VersionOf)
                         .FirstOrDefault();
    }

    /// <summary>An assembly's version read from its metadata, without loading it into the process —
    /// the point is to CHOOSE between copies, and loading one to find out would already have
    /// committed to it. Unreadable (truncated download, not a .NET assembly) sorts last.</summary>
    private static Version VersionOf(string dllPath)
    {
        try { return AssemblyName.GetAssemblyName(dllPath).Version ?? new Version(0, 0, 0); }
        catch { return new Version(0, 0, 0); }
    }
}

/// <summary>The context a module is loaded into: its own, so it can ship its own dependencies
/// without colliding with K2's.
///
/// <para><b>K2.Core is deliberately NOT resolved here.</b> The contract types travel through it, and
/// a second copy loaded beside the app's own would be a different type of the same name — the module
/// would load and then fail to cast, which is a far worse failure than not loading. Returning null
/// hands those names back to the default context, where the app's copy already is.</para></summary>
internal sealed class GameProfileLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public GameProfileLoadContext(string modulePath) : base("K2.GameProfiles", isCollectible: false) =>
        _resolver = new AssemblyDependencyResolver(modulePath);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is null) return null;
        if (name.Name is "K2.Core" ||
            name.Name.StartsWith("System.", StringComparison.Ordinal) ||
            name.Name.StartsWith("Microsoft.", StringComparison.Ordinal))
            return null;

        string? path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
