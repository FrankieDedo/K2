// UpdateChecker.cs — asks GitHub's Releases API whether a newer K2 build exists.
//
// Compared against the running process's own AssemblyVersion (see K2.App.csproj's
// <Version> — real releases stamp it via build-installer.bat's
// "dotnet publish -p:Version=X.Y.Z", matching the "vX.Y.Z-beta" git tag/GitHub
// release that build-installer.bat also produces (K2-Setup-X.Y.Z.exe / K2-X.Y.Z.zip
// assets — see Installer/build-installer.bat and K2Setup.iss). Whether the running
// copy is an installed vs. portable build is a separate concern, see InstallDetector.

using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

public sealed record UpdateAsset(string Name, string Url, long Size);

public sealed class UpdateCheckResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ReleaseUrl { get; init; }
    public UpdateAsset? InstallerAsset { get; init; }
    public UpdateAsset? PortableZipAsset { get; init; }
}

public static class UpdateChecker
{
    private const string RepoOwner = "FrankieDedo";
    private const string RepoName = "K2";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The running process's own version, as embedded by the build
    /// (see K2.App.csproj's &lt;Version&gt;). Falls back to 0.0.0 if somehow absent.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            // GitHub's API rejects requests with no User-Agent.
            req.Headers.UserAgent.ParseAdd("K2-UpdateChecker");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckResult { Success = false, Error = $"HTTP {(int)resp.StatusCode}" };

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string? latestVersionStr = ExtractVersion(tag);
            if (latestVersionStr is null || !Version.TryParse(latestVersionStr, out var latestVersion))
                return new UpdateCheckResult { Success = false, Error = $"Unrecognized release tag: '{tag}'" };

            UpdateAsset? installerAsset = null;
            UpdateAsset? zipAsset = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        installerAsset = new UpdateAsset(name, url, size);
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        zipAsset = new UpdateAsset(name, url, size);
                }
            }

            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            return new UpdateCheckResult
            {
                Success = true,
                UpdateAvailable = latestVersion > CurrentVersion,
                LatestVersion = latestVersionStr,
                ReleaseNotes = notes,
                ReleaseUrl = htmlUrl,
                InstallerAsset = installerAsset,
                PortableZipAsset = zipAsset,
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>Pulls "1.0.3" out of tags like "v1.0.3-beta" or a bare "1.0.3".</summary>
    private static string? ExtractVersion(string tag)
    {
        var m = Regex.Match(tag, @"\d+\.\d+\.\d+");
        return m.Success ? m.Value : null;
    }
}
