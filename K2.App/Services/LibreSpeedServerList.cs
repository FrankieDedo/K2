using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>One bundled LibreSpeed backend, already stitched into the two absolute URLs the
/// speed-test engine wants (see <see cref="LibreSpeedServerList"/>).</summary>
public sealed record LibreSpeedServer(string Name, string Sponsor, string DownUrl, string UpUrl)
{
    /// <summary>What the picker row shows: place name, plus the sponsor when there is one.</summary>
    public string Display => string.IsNullOrWhiteSpace(Sponsor) ? Name : $"{Name} — {Sponsor}";
}

/// <summary>
/// The bundled public LibreSpeed server list, shown as the "LibreSpeed server" picker in
/// <see cref="K2.App.SpeedTestConfigWindow"/>. Read from <c>librespeed-servers.json</c> next to
/// the executable (see K2.App.csproj) — a verbatim copy of <c>librespeed/speedtest</c>'s
/// <c>server-list.json</c>.
///
/// <para>Each upstream entry is <c>{ name, server, dlURL, ulURL, ... }</c> where <c>server</c> is
/// an origin (often protocol-relative, i.e. starting with <c>//</c>) and <c>dlURL</c>/<c>ulURL</c>
/// are paths relative to it. <see cref="Load"/> stitches them into
/// <c>{server}/{dlURL}?ckSize={mb}</c> for download and <c>{server}/{ulURL}</c> for upload — the
/// same shape as the manual LibreSpeed example the popup prefills. <c>{mb}</c> is left as a
/// literal for <see cref="SpeedTestConfig.BuildDownloadUrl"/> to substitute per run.</para>
///
/// <para>Missing or unparseable file ⇒ empty list; the popup then just shows the manual URL
/// boxes with no picker rows, exactly as before this list existed.</para>
/// </summary>
public static class LibreSpeedServerList
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "librespeed-servers.json");

    /// <summary>Parsed list ordered as in the file, or empty on any failure.</summary>
    public static IReadOnlyList<LibreSpeedServer> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<LibreSpeedServer>();
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<LibreSpeedServer>();

            var list = new List<LibreSpeedServer>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string origin = Normalize(Str(e, "server"));
                string dl = Str(e, "dlURL");
                string ul = Str(e, "ulURL");
                if (origin.Length == 0 || dl.Length == 0) continue;

                string down = origin + dl.TrimStart('/');
                down += (down.Contains('?') ? "&" : "?") + "ckSize={mb}";
                string up = ul.Length == 0 ? "" : origin + ul.TrimStart('/');

                list.Add(new LibreSpeedServer(Str(e, "name"), Str(e, "sponsorName"), down, up));
            }
            return list;
        }
        catch
        {
            return Array.Empty<LibreSpeedServer>();
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim() : "";

    /// <summary>Turn the upstream <c>server</c> value into an absolute origin ending with a
    /// single '/': add <c>https:</c> to a protocol-relative <c>//host</c>, leave an explicit
    /// scheme alone, and default a bare host to https.</summary>
    private static string Normalize(string server)
    {
        if (server.Length == 0) return "";
        if (server.StartsWith("//", StringComparison.Ordinal)) server = "https:" + server;
        else if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
        return server.EndsWith('/') ? server : server + "/";
    }
}
