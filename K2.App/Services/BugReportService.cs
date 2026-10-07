// BugReportService.cs — builds a bug-report zip (user description + system info + logs)
// and POSTs it to a Google Apps Script web app that forwards it by mail
// (script source: K2/tools/BugReportRelay.gs). No mail credentials live in K2: the
// script runs under the maintainer's Google account and sends the mail itself.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

public sealed class BugReportFile
{
    public string Path { get; init; } = "";
    public string EntryName { get; init; } = "";
    /// <summary>Path inside the zip when it differs from <see cref="EntryName"/>.</summary>
    public string? ZipPath { get; init; }
    public long Size { get; init; }
    public bool IsSelected { get; set; }
    public string Display => $"{EntryName}  ({Math.Max(1, Size / 1024)} KB)";
}

public static class BugReportService
{
    /// <summary>Apps Script web-app URL ("…/exec"). Empty until the relay is deployed.</summary>
    public const string Endpoint = "https://script.google.com/macros/s/AKfycbwrp6y_6lnTGKZ4x5PclhZ_xLBY_dYnFcda7rpvjPYd-ciPvhvqgYcMFHstaPdTSXVkeA/exec";

    /// <summary>Shared with the script's TOKEN constant — only keeps random scanners out.</summary>
    private const string Token = "f53baeed75251351c0f418ae380253ed";

    private const long MaxZipBytes = 15L * 1024 * 1024;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static bool IsConfigured => Endpoint.Length > 0;

    /// <summary>Every diagnostic file that could be attached. Pre-selected: the current log, the
    /// crash log and the latest crash dump (the dump only if it fits the size cap on its own).</summary>
    public static List<BugReportFile> CollectFiles()
    {
        var list = new List<BugReportFile>();
        void Add(string path, bool selected)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                    list.Add(new BugReportFile { Path = path, EntryName = fi.Name, Size = fi.Length, IsSelected = selected });
            }
            catch { /* unreadable — skip */ }
        }

        Add(App.LogPath, true);
        Add(App.CrashLogPath, true);
        var dir = Path.GetDirectoryName(App.LogPath);
        if (dir != null && Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "K2.App_*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(5)) Add(f, false);
            var dumps = Directory.GetFiles(dir, "K2.App_*.dmp")
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(3).ToList();
            for (int i = 0; i < dumps.Count; i++)
                Add(dumps[i], i == 0 && new FileInfo(dumps[i]).Length < MaxZipBytes / 2);
        }
        return list;
    }

    /// <summary>Profile stores to attach: K2's per-device databases (K2 keeps profiles in
    /// SQLite, not XML) and Base Camp's own BaseCamp.db, when found.</summary>
    public static List<BugReportFile> CollectProfileFiles()
    {
        var list = new List<BugReportFile>();
        void Add(string path, string zipPath)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 0)
                    list.Add(new BugReportFile { Path = path, EntryName = fi.Name, ZipPath = zipPath, Size = fi.Length });
            }
            catch { /* unreadable — skip */ }
        }

        var k2App = K2.Core.K2Paths.For("K2.App");
        foreach (var db in new[] { "everest.db", "everest60.db", "macropad.db", "makalu.db" })
            Add(Path.Combine(k2App, db), "profiles/k2/" + db);
        Add(Path.Combine(K2.Core.K2Paths.For("K2.DisplayPad.AppSide"), "state.db"), "profiles/k2/displaypad_state.db");

        var bc = BaseCampDbImporter.FindBaseCampDb();
        if (bc != null) Add(bc, "profiles/basecamp/BaseCamp.db");
        return list;
    }

    /// <summary>Anonymous system summary — deliberately no machine name, user name,
    /// serials or network identifiers.</summary>
    public static string BuildSystemInfo()
    {
        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var sb = new StringBuilder();
        sb.AppendLine($"K2 version: {UpdateChecker.CurrentVersion}");
        sb.AppendLine($"OS: {OsName(Reg(nt, "ProductName"))} {Reg(nt, "DisplayVersion")} " +
                      $"(build {Environment.OSVersion.Version.Build}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"Language: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"CPU: {Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")} ({Environment.ProcessorCount} threads)");
        sb.AppendLine($"Log level: {K2.Core.AppSettings.LogLevel}");
        try { sb.AppendLine($"K2 uptime: {(DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss")}"); } catch { }
        return sb.ToString();
    }

    /// <summary>Windows 11 still reports "Windows 10 …" in ProductName; the build number tells them apart.</summary>
    private static string OsName(string productName) =>
        Environment.OSVersion.Version.Build >= 22000 ? productName.Replace("Windows 10", "Windows 11") : productName;

    // 64-bit view on purpose: K2 is an x86 process, and the WOW64 view of this key can hold stale values.
    private static string Reg(string subKey, string name)
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var k = hklm.OpenSubKey(subKey);
            return (k?.GetValue(name) as string)?.Trim() ?? "?";
        }
        catch { return "?"; }
    }

    /// <summary>Zips description + system info + the given files; returns null if over the size cap.</summary>
    public static byte[]? BuildZip(string description, string contact, IEnumerable<BugReportFile> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("report.txt", CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
            {
                w.WriteLine("Contact: " + (string.IsNullOrWhiteSpace(contact) ? "(none)" : contact));
                w.WriteLine();
                w.WriteLine(BuildSystemInfo());
                w.WriteLine("Description:");
                w.WriteLine(description);
            }
            foreach (var f in files)
            {
                try
                {
                    var entry = zip.CreateEntry(f.ZipPath ?? f.EntryName, CompressionLevel.Optimal);
                    using var dst = entry.Open();
                    // The live log is held open by the writer — share read access.
                    using var src = new FileStream(f.Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    src.CopyTo(dst);
                }
                catch { /* one unreadable file must not sink the report */ }
            }
        }
        return ms.Length > MaxZipBytes ? null : ms.ToArray();
    }

    public static async Task<(bool Ok, string? Error)> SendAsync(
        string description, string contact, byte[] zip, CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "not configured");
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                token = Token,
                version = UpdateChecker.CurrentVersion.ToString(),
                contact,
                description,
                filename = $"K2-bugreport-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                zipBase64 = Convert.ToBase64String(zip),
            });
            // text/plain keeps Apps Script from rejecting the body; it parses it itself.
            using var content = new StringContent(payload, Encoding.UTF8, "text/plain");
            using var resp = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return (true, null);
            return (false, doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : "unknown");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
