// UpdateInstaller.cs — downloads a release asset found by UpdateChecker and either
// launches the installer (installed copies) or hands the ZIP to the caller to save
// wherever the user picks (portable copies). See MainWindow.Settings.cs for the
// Settings-tab flow that drives this (download -> launch installer -> close K2, or
// download -> save-as ZIP).

using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

public static class UpdateInstaller
{
    // Large installers/zips over a possibly slow connection — generous timeout.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>Downloads an asset to <paramref name="destPath"/>, reporting 0..1
    /// progress if the response carries a Content-Length (falls back to the asset's
    /// known size from the GitHub API when it doesn't).</summary>
    public static async Task DownloadAsync(UpdateAsset asset, string destPath, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? asset.Size;

        await using var httpStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long readSoFar = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readSoFar += read;
            if (total > 0)
                progress?.Report((double)readSoFar / total);
        }
    }

    /// <summary>Downloads the installer asset into the temp folder, returning the
    /// local path. Caller launches it (<see cref="LaunchInstaller"/>) and then closes
    /// K2 — the installer needs to overwrite K2.App.exe, which stays locked while
    /// this process is still running.</summary>
    public static async Task<string> DownloadInstallerAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken ct = default)
    {
        string dest = Path.Combine(Path.GetTempPath(), asset.Name);
        await DownloadAsync(asset, dest, progress, ct).ConfigureAwait(false);
        return dest;
    }

    /// <summary>Runs the downloaded installer. ShellExecute (not plain CreateProcess)
    /// so it can trigger its own UAC elevation prompt — same reasoning as K2Setup.iss's
    /// [Run] section for K2.App.exe itself.</summary>
    public static void LaunchInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
