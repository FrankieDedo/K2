using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// The list of everything Telemachus can answer, as the mod itself describes it (<c>a.api</c>):
/// the options the Kerbal Space Program pickers offer beyond K2's curated entries — in the key's
/// action browser and in the game studio's Telemachus readings.
///
/// <para><b>Cached on disk</b> (<c>telemachus-api.json</c> next to K2's other stores), because the
/// pickers are mostly opened with the game shut: the list is fetched once per run the first time
/// the mod answers (<see cref="TelemachusClient"/> asks for it), or on demand from the studio's
/// picker, and the last good copy is what every other moment works from.</para>
///
/// <para>The list is REPLACED, never mutated: <see cref="KspTelemachus"/> notices a new listing by
/// reference, and a picker iterating the old one must not see it change under it.</para>
/// </summary>
internal static class TelemachusApi
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "telemachus-api.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static IReadOnlyList<KspApiEntry>? _entries;
    private static readonly object _gate = new();
    private static int _fetchedThisRun;

    /// <summary>Hooks the listing into the Core catalogue. Called once at startup.</summary>
    internal static void Install() => KspTelemachus.ApiProvider = Entries;

    /// <summary>The last listing known — fetched this run, or loaded from the cache file. Empty
    /// before the mod has ever been reached on this PC.</summary>
    internal static IReadOnlyList<KspApiEntry> Entries()
    {
        lock (_gate)
        {
            _entries ??= Load();
            return _entries;
        }
    }

    /// <summary>Called when the mod starts answering: fetches the listing once per run, in the
    /// background. A Telemachus update between two sessions is picked up the next time the game is
    /// running, with nobody having to ask.</summary>
    internal static void RefreshOncePerRun()
    {
        if (Interlocked.Exchange(ref _fetchedThisRun, 1) == 1) return;
        _ = RefreshAsync();
    }

    /// <summary>Fetches the listing now. Returns the number of entries, or null when the mod did
    /// not answer — in which case the cached list stays as it was.</summary>
    internal static async Task<int?> RefreshAsync()
    {
        try
        {
            string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = "a.api" });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(
                $"http://127.0.0.1:{KspTelemachus.DefaultPort}/telemachus/datalink", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            var list = Parse(json, rootKey: "a");
            if (list.Count == 0) return null;

            lock (_gate) _entries = list;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            }
            catch (Exception ex) { App.WriteLog($"[KSP] could not cache the Telemachus API list: {ex.Message}"); }

            App.WriteLog($"[KSP] Telemachus API list refreshed: {list.Count} entries");
            return list.Count;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<KspApiEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize<List<KspApiEntry>>(File.ReadAllText(FilePath)) is { } cached)
                return cached;
        }
        catch { /* a broken cache is the same as no cache: the next answer from the mod rewrites it */ }
        return Array.Empty<KspApiEntry>();
    }

    /// <summary>The mod's answer (<c>{ "a": [ {apistring, name, category, units, returnType,
    /// isAction, params}, ... ] }</c>) as entries. Fields the mod leaves out read as empty.</summary>
    private static List<KspApiEntry> Parse(string json, string rootKey)
    {
        var list = new List<KspApiEntry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(rootKey, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string api = Str(e, "apistring");
            if (api.Length == 0) continue;
            list.Add(new KspApiEntry(
                api, Str(e, "name"), Str(e, "category"), Str(e, "units"), Str(e, "returnType"),
                e.TryGetProperty("isAction", out var a) && a.ValueKind == JsonValueKind.True,
                Str(e, "params")));
        }
        return list;

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
