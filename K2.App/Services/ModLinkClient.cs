using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Reads the state K2's own game mods serve — the Minecraft Fabric mod and the Space Engineers
/// client plugin (<c>K2/Mods/</c>) — for the DisplayPad's <c>dp_modlink</c> tiles.
///
/// <para><b>Protocol.</b> Each mod publishes ONE flat JSON object:
/// <c>{"k2":1, "inGame":true, "health":18, ...}</c> — the Minecraft mod answers
/// <c>GET http://127.0.0.1:{port}/state</c>; the Space Engineers mod, sandboxed with no sockets,
/// rewrites <c>%AppData%\SpaceEngineers\Storage\&lt;mod&gt;_K2Link\state.json</c> a few times a
/// second (see <see cref="ReadSpaceEngineersFile"/>). The whole state is a few hundred
/// bytes, so there is no per-field request like Telemachus' — the poller just takes all of it.
/// <c>inGame</c> false (main menu, loading) means every reading is not known.</para>
///
/// <para><b>Polled only while wanted</b>, same pact as <see cref="TelemachusClient"/>: a tile asks
/// on every repaint (<see cref="Want"/>), a game nobody asked about for a few seconds stops being
/// polled.</para>
/// </summary>
internal static class ModLinkClient
{
    private const int PollMs = 250;
    private static readonly TimeSpan WantTtl = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    /// <summary>One reading. <see cref="Alive"/> false while nothing answers on the port (game
    /// closed, mod missing); <see cref="InGame"/> false while the mod answers from a menu.</summary>
    internal sealed record Status(bool Alive, bool InGame, IReadOnlyDictionary<string, JsonElement> Values)
    {
        public static readonly Status Offline =
            new(false, false, new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        public JsonElement? Get(string? field)
        {
            if (!Alive || !InGame || field is not { Length: > 0 }) return null;
            return Values.TryGetValue(field, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? v : null;
        }
    }

    private sealed class Channel
    {
        public required ModLinkGame Game;
        public Status Last = Status.Offline;
        public DateTime WantedUtc;
        public Task? Poller;
        public volatile bool PollNow;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

    /// <summary>Marks the game as on a pad right now and returns its latest reading. Never blocks.</summary>
    internal static Status Want(ModLinkGame game)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(game.ProfileId, out var ch))
                _channels[game.ProfileId] = ch = new Channel { Game = game };
            ch.WantedUtc = DateTime.UtcNow;
            if (ch.Poller is not { IsCompleted: false }) ch.Poller = Task.Run(() => PollLoop(ch));
            return ch.Last;
        }
    }

    /// <summary>Asks for a fresh reading right away — after a key press, so a toggle lights up
    /// when pressed rather than a quarter second later.</summary>
    internal static void PollSoon(ModLinkGame game)
    {
        lock (_gate)
            if (_channels.TryGetValue(game.ProfileId, out var ch)) ch.PollNow = true;
    }

    private static async Task PollLoop(Channel ch)
    {
        string url = $"http://127.0.0.1:{ch.Game.Port}/state";
        string where = ch.Game.Port > 0 ? $":{ch.Game.Port}" : "its state file";
        bool? wasAlive = null;
        while (true)
        {
            lock (_gate)
                if (DateTime.UtcNow - ch.WantedUtc > WantTtl)
                {
                    ch.Last = Status.Offline;
                    ch.Poller = null;
                    return;
                }

            Status next;
            try
            {
                string? json = ch.Game.Port > 0
                    ? await Http.GetStringAsync(url).ConfigureAwait(false)
                    : ReadSpaceEngineersFile();
                next = json is null ? Status.Offline : Parse(json, ch.Game);
            }
            catch { next = Status.Offline; }

            lock (_gate) ch.Last = next;
            if (wasAlive != next.Alive)
            {
                App.WriteLog(next.Alive
                    ? $"[MODLINK] {ch.Game.ModName} answering on {where}"
                    : $"[MODLINK] {ch.Game.ModName} not answering on {where}");
                wasAlive = next.Alive;
            }

            for (int waited = 0; waited < PollMs && !ch.PollNow; waited += 50)
                await Task.Delay(50).ConfigureAwait(false);
            ch.PollNow = false;
        }
    }

    private static readonly string IconDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K2", "K2.App", "modlink-icons");

    /// <summary>Icon path → (revision on disk, file). Survives only the run: the files are a cache.</summary>
    private static readonly Dictionary<string, (int Rev, string File)> _icons = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _iconFetching = new(StringComparer.Ordinal);

    /// <summary>The picture a mod serves at <paramref name="path"/> (Minecraft's <c>hotbar/3</c>) as
    /// a PNG on disk, for revision <paramref name="rev"/>. Never blocks: a new revision is fetched in
    /// the background and, until it lands, the previous picture is returned — a slot that changes
    /// shows the old item for one poll rather than an empty key. Null for revision 0 (nothing to
    /// show) and before the first picture arrives.</summary>
    internal static string? IconFile(ModLinkGame game, string path, int rev)
    {
        string key = game.ProfileId + "/" + path;
        lock (_gate)
        {
            _icons.TryGetValue(key, out var have);
            if (rev <= 0) return null;
            if (have.Rev == rev || game.Port <= 0 || !_iconFetching.Add(key))
                return have.File;
        }

        string url = $"http://127.0.0.1:{game.Port}/{path}";
        string file = System.IO.Path.Combine(IconDir, $"{game.ProfileId}_{path.Replace('/', '_')}_{rev}.png");
        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                string? stored = null;
                if (resp.IsSuccessStatusCode)
                {
                    System.IO.Directory.CreateDirectory(IconDir);
                    await System.IO.File.WriteAllBytesAsync(
                        file, await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    stored = file;
                }
                string? old;
                lock (_gate)
                {
                    old = _icons.TryGetValue(key, out var prev) ? prev.File : null;
                    _icons[key] = (rev, stored!);
                }
                if (old is not null && old != stored) try { System.IO.File.Delete(old); } catch { }
            }
            catch { /* next poll asks again */ }
            finally
            {
                lock (_gate) _iconFetching.Remove(key);
                PollSoon(game);
            }
        });
        return _icons.TryGetValue(key, out var current) ? current.File : null;
    }

    /// <summary>How old the Space Engineers file may be before it counts as a game that is no
    /// longer running (closed, crashed): the mod rewrites it four times a second, with a timestamp
    /// inside so the content always changes.</summary>
    private static readonly TimeSpan SeFileStale = TimeSpan.FromSeconds(4);

    /// <summary>The newest <c>state.json</c> in any <c>Storage\*K2Link*</c> folder. The folder name
    /// is the game's, built from the mod's id and script folder (a local copy and the Workshop copy
    /// differ), so it is found by pattern rather than spelled out. Null when there is none, or the
    /// newest is stale.</summary>
    private static string? ReadSpaceEngineersFile()
    {
        string storage = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "Storage");
        if (!System.IO.Directory.Exists(storage)) return null;

        System.IO.FileInfo? newest = null;
        foreach (string dir in System.IO.Directory.EnumerateDirectories(storage, "*K2Link*"))
        {
            var f = new System.IO.FileInfo(System.IO.Path.Combine(dir, "state.json"));
            if (f.Exists && (newest is null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc)) newest = f;
        }
        if (newest is null || DateTime.UtcNow - newest.LastWriteTimeUtc > SeFileStale) return null;

        // Shared read: the game may be rewriting it right now. A torn read fails to parse and
        // counts as one missed poll.
        using var fs = new System.IO.FileStream(newest.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        using var sr = new System.IO.StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static Status Parse(string json, ModLinkGame game)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Status.Offline;

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject()) values[p.Name] = p.Value.Clone();

        bool inGame = values.TryGetValue("inGame", out var ig) && ig.ValueKind == JsonValueKind.True;
        if (game.ProfileId == ModLinkGames.MinecraftId &&
            values.TryGetValue("gameDir", out var dir) && dir.ValueKind == JsonValueKind.String)
            ModLinkBinds.MinecraftGameDir = dir.GetString();

        return new Status(true, inGame, values);
    }
}
