// GameProfileModuleHost.cs — K2.App's side of the module contract.
//
// Everything a loaded module is allowed to ask of the app goes through here, which is the point:
// the surface a module can reach is this file, not "whatever is public in K2.App".

using System;
using System.IO;
using System.Linq;
using K2.Core;

namespace K2.App.Services;

internal sealed class GameProfileModuleHost : IGameProfileHost
{
    public void Log(string message) => App.WriteLog($"[gameprofiles] {message}");

    /// <summary>Resolves through the same <see cref="GameExeResolver"/> the profile cards use, so a
    /// module finds the game exactly where K2 already found it. The Steam app id is not the module's
    /// to pass — it is already in the catalogue entry for that executable, so it is looked up here
    /// rather than asked for and possibly disagreed about.</summary>
    public string? ResolveGameInstallDir(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return null;

        int? steamAppId = GameProfileCatalog.All
            .FirstOrDefault(d => string.Equals(d.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
            ?.SteamAppId;

        string? exe = GameExeResolver.Resolve(exeName, steamAppId, remembered: null);
        return exe is null ? null : Path.GetDirectoryName(exe);
    }
}
