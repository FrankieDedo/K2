using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// A source of values the GAME ITSELF publishes, as opposed to a screen probe, which measures the
/// pixels the game happens to be drawing.
///
/// <para><b>Why this is one generic thing and not one connector per game.</b> Surveying what is
/// actually reachable (2026-09-13) the answer was not "Unity" or "Unreal" — the engine turned out
/// to be irrelevant, since of fourteen Unreal games installed exactly one shipped Epic's
/// WebRemoteControl plugin. What repeats is the TRANSPORT: a local HTTP endpoint answering JSON.
/// Elite, Zero Company (UE WebRemoteControl on :30010), Kerbal Space Program (Telemachus on
/// :8085), Satisfactory (Ficsit Remote Monitoring on :8080), War Thunder (:8111, no mod at all)
/// and League of Legends (:2999) are all the same shape wearing different port numbers. So K2
/// learns the SHAPE once, and every game — present and future, modded or not — is a row in a JSON
/// file instead of a new C# file.</para>
///
/// <para><b>What a link deliberately is not.</b> It is not a command channel (a link only reads),
/// it is not per-device, and it holds no knowledge of any particular game: the payload is
/// flattened to dotted paths and the user picks the paths they want. A game that renames a field
/// in an update breaks a READING, which then says "—", and never the app.</para>
/// </summary>
public sealed record GameLinkDef
{
    public string Id { get; init; } = "";

    /// <summary>What the user calls it — usually the game's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The endpoint to fetch, e.g. <c>http://127.0.0.1:8111/state</c>. One URL per link:
    /// a game serving two endpoints (War Thunder's <c>/state</c> and <c>/indicators</c>) is two
    /// links, which costs one extra row and saves every reading from having to say which of the
    /// two it meant.</summary>
    public string Url { get; init; } = "";

    /// <summary>How often to re-fetch while a tile is actually watching, in milliseconds. Clamped
    /// to <see cref="GameLinkReader.MinPollMs"/>…<see cref="GameLinkReader.MaxPollMs"/> — the pad
    /// itself only repaints about once a second, so faster than that buys nothing and only costs
    /// the game.</summary>
    public int PollMs { get; init; } = 1000;

    /// <summary>Whether the game serves this by itself or only with a mod installed — see
    /// <see cref="GameLinkKind"/>.</summary>
    public GameLinkKind Kind { get; init; } = GameLinkKind.Native;

    /// <summary>Where to get the mod, for a <see cref="GameLinkKind.Mod"/> link. A page to open in
    /// a browser, never a file to fetch: K2 does not download or install anything into a game, and
    /// whoever is about to mod their game should see whose page they are landing on.</summary>
    public string ModUrl { get; init; } = "";

    /// <summary>True for a link pointed at Telemachus (Kerbal Space Program) — recognised by the
    /// mod's own path rather than by name, which the user may have changed. Telemachus belongs to
    /// the built-in KSP profile, so such a link (typically one saved from the old studio preset) is
    /// only offered on that profile's actions.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTelemachus => Url.Contains("/telemachus/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accept a TLS certificate that does not validate. Needed by exactly one shape of
    /// endpoint and worth the ugly name: a game serving HTTPS on loopback with a self-signed
    /// certificate (League of Legends' Live Client Data API). Off by default, and the user has to
    /// tick it themselves.</summary>
    public bool AllowInvalidCertificate { get; init; }
}

/// <summary>Whether the game answers on its own, or only after something was installed into it.
///
/// <para>The distinction is the user's, not the code's — both kinds are fetched identically. It
/// exists because the two FAIL differently: a native link that says nothing means the game is shut
/// down, while a mod link that says nothing may well mean the mod was never installed, or was
/// wiped by the game's last update. Telling them apart is what turns "it doesn't work" into
/// "install this".</para></summary>
public enum GameLinkKind
{
    /// <summary>The game serves this by itself, with nothing added: War Thunder on :8111, League of
    /// Legends on :2999, an Unreal build that shipped WebRemoteControl.</summary>
    Native,

    /// <summary>A mod publishes it: Ficsit Remote Monitoring in Satisfactory, K2 Unity Link.
    /// <see cref="GameLinkDef.ModUrl"/> is where to get it.</summary>
    Mod,
}

/// <summary>
/// The links the user has defined, persisted as one JSON file next to K2's log — the same shape,
/// and for the same reasons, as <see cref="ScreenProbeStore"/>: read by static services that have
/// no store instance, not per-device, small enough to rewrite whole on every change.
/// </summary>
internal static class GameLinkStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "gamelinks.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object _gate = new();
    private static List<GameLinkDef>? _cache;

    public static IReadOnlyList<GameLinkDef> All()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<GameLinkDef>>(File.ReadAllText(FilePath), Json)
                      ?? new List<GameLinkDef>()
                    : new List<GameLinkDef>();
            }
            catch (Exception ex)
            {
                // Same rule as the probes: a corrupt file is a log line and an empty list, never a
                // crash and never a silent overwrite.
                App.WriteLog($"[LINK] cannot read \"{FilePath}\": {ex.Message}");
                _cache = new List<GameLinkDef>();
            }
            return _cache;
        }
    }

    public static GameLinkDef? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All().FirstOrDefault(l => l.Id == id);

    /// <summary>Adds a link, or replaces the one with the same <see cref="GameLinkDef.Id"/>.</summary>
    public static void Save(GameLinkDef link)
    {
        lock (_gate)
        {
            var list = All().ToList();
            int i = list.FindIndex(l => l.Id == link.Id);
            if (i >= 0) list[i] = link; else list.Add(link);
            Write(list);
        }
        // The definition changed under a running poller (a new URL, a new interval): drop what was
        // cached for it so the next reading cannot answer with the old endpoint's values.
        GameLinkReader.Forget(link.Id);
    }

    public static void Delete(string id)
    {
        lock (_gate)
        {
            Write(All().Where(l => l.Id != id).ToList());
        }
        GameLinkReader.Forget(id);
    }

    /// <summary>A fresh id. Time-based rather than a GUID so the file stays readable by hand.</summary>
    public static string NewId() => "g" + DateTime.UtcNow.Ticks.ToString("x");

    private static void Write(List<GameLinkDef> list)
    {
        _cache = list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
        }
        catch (Exception ex)
        {
            App.WriteLog($"[LINK] cannot write \"{FilePath}\": {ex.Message}");
        }
    }
}

/// <summary>
/// Fetches game links and hands out the values they carry.
///
/// <para><b>Cadence.</b> Reading a value never waits on a socket: <see cref="Snapshot"/> is a pure
/// cache read, and a background poller per link refreshes it. The poller starts on the first read
/// and stops itself a few seconds after the last one, so a link is polled only while something is
/// actually showing it — the pattern <c>ZeroCompanyClient</c> already uses, for the same reason: a
/// tile that moved off the pad must not keep talking to the game.</para>
///
/// <para><b>Why the payload is flattened.</b> The reading stores a PATH, not a query, so the
/// question "what can I read here" has to be answerable as a list — that is what the studio's
/// value picker shows. Flattening the whole document to <c>a.b[0].c</c> keys makes discovery, the
/// picker and the lookup one mechanism instead of three, and keeps the reading's stored form
/// something a human can check by eye.</para>
/// </summary>
internal static class GameLinkReader
{
    internal const int MinPollMs = 200;
    internal const int MaxPollMs = 10_000;

    /// <summary>How long after the last read a poller keeps going before it gives up and stops.</summary>
    private static readonly TimeSpan Interest = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the values kept from one payload, and the reason a snapshot can be
    /// INCOMPLETE — see <see cref="LinkSnapshot.Truncated"/>.
    ///
    /// <para>Was 4000, which silently hid the thing the user was looking for: a generic Unity scene
    /// dump measured 3269 objects and 5.5 MB, with the player's own values arriving after roughly
    /// 5900 — so the reader stopped short of them and the picker simply did not list health at all,
    /// with nothing anywhere saying why. Raised well past that, and the cap now announces itself
    /// when it bites instead of quietly shortening the answer.</para></summary>
    private const int MaxValues = 60_000;

    /// <summary>Ceiling on nesting, so a pathological document cannot recurse the flattener to
    /// death.</summary>
    private const int MaxDepth = 12;

    /// <summary>Ceiling on the response body. Anything larger is not a tile's business.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    /// <summary>A second client for the self-signed-certificate case. Separate rather than a
    /// per-request flag because the validation callback lives on the HANDLER, and a client that
    /// accepts anything must never become the one every other link uses.</summary>
    private static readonly Lazy<HttpClient> HttpLax = new(() => new HttpClient(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
    { Timeout = TimeSpan.FromMilliseconds(1500) });

    /// <summary>What one link says right now.</summary>
    /// <param name="Known">False when the link has never answered, or stopped answering. A tile
    /// reading an unknown link shows "—" rather than a stale number.</param>
    /// <param name="Values">Flattened payload, by dotted path.</param>
    /// <param name="Error">Why the last fetch failed, for the studio to show. Null when fine.</param>
    /// <param name="Truncated">The payload had more values than <see cref="MaxValues"/> and the rest
    /// were dropped. Worth surfacing rather than swallowing: a picker that is missing the one value
    /// somebody is hunting for, with no sign that anything is missing, is how an afternoon gets
    /// spent looking in the wrong place.</param>
    internal readonly record struct LinkSnapshot(
        bool Known, IReadOnlyDictionary<string, string> Values, string? Error, bool Truncated = false)
    {
        internal static readonly LinkSnapshot Unknown =
            new(false, Empty, null);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private sealed class State
    {
        public LinkSnapshot Snapshot = LinkSnapshot.Unknown;
        public DateTime LastWantedUtc;
        public Task? Poller;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, State> _links = new(StringComparer.Ordinal);

    /// <summary>Latest values for a link, and a note that somebody wants them — which is what keeps
    /// its poller alive. Never blocks.</summary>
    internal static LinkSnapshot Snapshot(string? linkId)
    {
        if (string.IsNullOrEmpty(linkId)) return LinkSnapshot.Unknown;
        var def = GameLinkStore.ById(linkId);
        if (def is null || string.IsNullOrWhiteSpace(def.Url)) return LinkSnapshot.Unknown;

        State state;
        lock (_gate)
        {
            if (!_links.TryGetValue(linkId, out state!)) _links[linkId] = state = new State();
            state.LastWantedUtc = DateTime.UtcNow;
            if (state.Poller is not { IsCompleted: false }) state.Poller = Task.Run(() => PollLoop(linkId));
            return state.Snapshot;
        }
    }

    /// <summary>One value out of a link, as text, or null when the link or the path is not there.</summary>
    internal static string? Value(string? linkId, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var snap = Snapshot(linkId);
        return snap.Known && snap.Values.TryGetValue(path!, out var v) ? v : null;
    }

    /// <summary>Drops everything cached for a link, so a changed definition cannot be answered
    /// with the previous endpoint's values. The poller notices on its next turn.</summary>
    internal static void Forget(string? linkId)
    {
        if (string.IsNullOrEmpty(linkId)) return;
        lock (_gate) { _links.Remove(linkId!); }
    }

    /// <summary>Runs the background poll's own request NOW and stores the answer, instead of waiting
    /// for the next turn — for the studio, the moment a link is picked. Same narrowed request as the
    /// poll, so it costs the game nothing the poll would not.</summary>
    internal static async Task<LinkSnapshot> RefreshAsync(string? linkId)
    {
        Snapshot(linkId);   // registers interest, so the poller is (still) running afterwards
        var def = GameLinkStore.ById(linkId);
        if (def is null || string.IsNullOrWhiteSpace(def.Url)) return LinkSnapshot.Unknown;

        var snap = await FetchAsync(NarrowedFor(def)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_links.TryGetValue(linkId!, out var s)) s.Snapshot = snap;
        }
        return snap;
    }

    /// <summary>Fetches a link ONCE, off the poll cycle. For the studio: a "test" button has to
    /// answer now, and has to answer for a definition the user has not saved yet.</summary>
    internal static async Task<LinkSnapshot> FetchAsync(GameLinkDef def)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(def.Url)) return LinkSnapshot.Unknown;

            var client = def.AllowInvalidCertificate ? HttpLax.Value : Http;
            using var response = await client.GetAsync(def.Url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new LinkSnapshot(false, Empty, $"HTTP {(int)response.StatusCode}");

            // Some game endpoints answer with text/plain or a bare text/html, so the content type
            // is not a gate — what matters is whether the body parses as JSON.
            byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (body.Length > MaxBytes) return new LinkSnapshot(false, Empty, "payload too large");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            Flatten(doc.RootElement, "", values, 0);
            return new LinkSnapshot(true, values, null, Truncated: values.Count >= MaxValues);
        }
        catch (JsonException ex)
        {
            return new LinkSnapshot(false, Empty, Loc.Get("link_not_json", ex.Message));
        }
        catch (OperationCanceledException)
        {
            // What HttpClient throws when ITS timeout fires, and the message it carries ("a task
            // was canceled") tells the user nothing about their game.
            return new LinkSnapshot(false, Empty, Loc.Get("link_timeout"));
        }
        catch (Exception ex)
        {
            // A game that is not running is the NORMAL case here, not an error worth a log line
            // every second: the message travels in the snapshot for the studio to show.
            return new LinkSnapshot(false, Empty, Short(ex));
        }
    }

    /// <summary>The link's answer as the game actually wrote it, unparsed and unflattened — what the
    /// studio's "export" hands the user as a file.
    ///
    /// <para>Deliberately NOT the flattened <see cref="LinkSnapshot.Values"/> every other reader
    /// here works from: that view is capped (<see cref="MaxValues"/>) and already interpreted, while
    /// the point of exporting is to hand the WHOLE document to something else — an assistant being
    /// asked "which of these is the health value", most likely — which is a job that wants the
    /// original shape and the nesting, not this app's opinion of it.</para></summary>
    internal static async Task<(string? Json, string? Error)> FetchRawAsync(GameLinkDef def)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(def.Url)) return (null, Loc.Get("link_need_url"));

            var client = def.AllowInvalidCertificate ? HttpLax.Value : Http;
            using var response = await client.GetAsync(def.Url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");

            byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (body.Length > MaxBytes) return (null, "payload too large");
            return (System.Text.Encoding.UTF8.GetString(body), null);
        }
        catch (OperationCanceledException)
        {
            return (null, Loc.Get("link_timeout"));
        }
        catch (Exception ex)
        {
            return (null, Short(ex));
        }
    }

    /// <summary>Appends the reading paths actually configured for this link (across every profile,
    /// not just whatever the studio happens to have open) as a <c>k2want</c> query parameter, so a
    /// link whose payload comes from reflecting a whole running program — <c>K2.UnityLink</c>'s
    /// generic Unity mod is the reason this exists — can stop walking everything on every poll once
    /// it knows which handful of values anyone actually reads.
    ///
    /// <para><b>Only the BACKGROUND poll narrows.</b> A one-off <see cref="FetchAsync"/> — the
    /// studio's "Test" button, the value-path TREE browser — calls the plain, un-narrowed method
    /// directly and never goes through here, on purpose: those exist specifically to discover a
    /// value NOBODY has configured yet, and narrowing them to what is already saved would make
    /// exactly that impossible.</para>
    ///
    /// <para>Harmless for every other kind of link (War Thunder, Satisfactory, KSP/Telemachus...):
    /// an extra, unrecognized query parameter on a plain REST endpoint is simply ignored by servers
    /// that were never written to look for it, so this is safe to apply unconditionally rather than
    /// only for links known to be a <c>K2.UnityLink</c> instance.</para></summary>
    private static GameLinkDef NarrowedFor(GameLinkDef def)
    {
        var wanted = CustomGameStore.WantedPathsForLink(def.Id);
        if (wanted.Count == 0) return def;
        return WithQuery(def, "k2want=" + Uri.EscapeDataString(string.Join(",", wanted)));
    }

    /// <summary>Marks a fetch as one a PERSON asked for — the studio's "Test", the value-path tree
    /// browser, the JSON export — as opposed to the background poll, which is the same HTTP request
    /// to the same address and otherwise indistinguishable.
    ///
    /// <para>It matters because of what answering can cost on the other side: <c>K2.UnityLink</c>
    /// rebuilds its whole-scene dump only for a request carrying this, and serves the last one it
    /// already had to everybody else. Without the distinction a link with no saved readings yet —
    /// exactly the state a link is in WHILE being set up — would have its background poll rebuilding
    /// that dump around the clock, which on a real game is a visible stutter every few seconds.
    /// Ignored, like <c>k2want</c>, by every endpoint that was not written to look for it.</para></summary>
    internal static GameLinkDef ExplicitFor(GameLinkDef def) => WithQuery(def, "k2full=1");

    /// <summary>An explicit fetch narrowed to what a person just typed, searched AT THE SOURCE.
    ///
    /// <para>A link can answer with more than any picker can show — a whole game's scripted objects
    /// runs to megabytes — and filtering after the fact cannot help when the cut happens before the
    /// interesting part is even read. Sending the term instead lets the endpoint answer with the few
    /// objects that match. Endpoints that never heard of <c>k2find</c> ignore it and answer in full,
    /// exactly as they do today.</para></summary>
    internal static GameLinkDef SearchFor(GameLinkDef def, string term) =>
        WithQuery(def, "k2find=" + Uri.EscapeDataString(term));

    private static GameLinkDef WithQuery(GameLinkDef def, string query) =>
        def with { Url = def.Url.Contains('?') ? def.Url + "&" + query : def.Url + "?" + query };

    private static async Task PollLoop(string linkId)
    {
        while (true)
        {
            GameLinkDef? def;
            lock (_gate)
            {
                if (!_links.TryGetValue(linkId, out var s)) return;          // forgotten
                if (DateTime.UtcNow - s.LastWantedUtc > Interest)            // nobody watching
                {
                    s.Poller = null;
                    return;
                }
                def = GameLinkStore.ById(linkId);
                if (def is null) { _links.Remove(linkId); return; }
            }

            var snap = await FetchAsync(NarrowedFor(def)).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_links.TryGetValue(linkId, out var s)) return;
                s.Snapshot = snap;
            }

            await Task.Delay(Math.Clamp(def.PollMs, MinPollMs, MaxPollMs)).ConfigureAwait(false);
        }
    }

    // ─────────────────────────── Flattening ───────────────────────────

    /// <summary>Walks a JSON document into <c>path -> text</c> pairs. Objects join with a dot,
    /// arrays with <c>[i]</c>, and every array also gets a <c>.count</c> of its own — "how many
    /// squad members" is exactly the kind of number a tile wants and no game publishes it
    /// separately.</summary>
    private static void Flatten(JsonElement element, string prefix,
                                Dictionary<string, string> into, int depth)
    {
        if (into.Count >= MaxValues || depth > MaxDepth) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (into.Count >= MaxValues) return;
                    Flatten(prop.Value, prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name,
                            into, depth + 1);
                }
                break;

            case JsonValueKind.Array:
            {
                int i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (into.Count >= MaxValues) return;
                    Flatten(item, prefix + "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                            into, depth + 1);
                    i++;
                }
                if (prefix.Length > 0) into[prefix + ".count"] = i.ToString(CultureInfo.InvariantCulture);
                break;
            }

            case JsonValueKind.Null or JsonValueKind.Undefined:
                if (prefix.Length > 0) into[prefix] = "";
                break;

            default:
                // Numbers keep the game's own spelling (GetRawText, not a round-trip through
                // double): a value is printed on a tile far more often than it is measured, and
                // "1.0" turning into "1" is the game's decision to make, not ours.
                if (prefix.Length > 0)
                    into[prefix] = element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? ""
                        : element.GetRawText();
                break;
        }
    }

    /// <summary>The short form of an exception, for a tooltip: the innermost message, which for a
    /// socket error is the one that says what actually went wrong.</summary>
    private static string Short(Exception ex)
    {
        var e = ex;
        while (e.InnerException is { } inner) e = inner;
        return e.Message;
    }
}

/// <summary>Opening a mod's page. Small enough to be one method, separate enough to be worth
/// naming: it is the only place in the link machinery that starts a program.</summary>
internal static class GameLinkMod
{
    /// <summary>Opens a mod page in the user's browser. Returns false when the address is not one
    /// this will open.
    ///
    /// <para><b>http and https only, deliberately.</b> A link definition can arrive from an
    /// IMPORTED profile package — that is, from someone else — and <c>ShellExecute</c> on an
    /// arbitrary string is how a shared profile would get to run a program on this machine. A mod
    /// page is a web page, so anything that is not one is refused rather than handed to the
    /// shell.</para></summary>
    internal static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[LINK] cannot open \"{uri.AbsoluteUri}\": {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Reading a link's value AS a value: the small amount of interpretation that sits between "the
/// game published this text" and "the tile shows this".
///
/// <para>Kept apart from <see cref="GameLinkReader"/> because it is the only part with an opinion.
/// What counts as true, what counts as a number when the game writes <c>"87%"</c>, how a state
/// matches — those are judgement calls, and they belong in one place where they can be read and
/// argued with rather than scattered through the tile painter.</para>
/// </summary>
internal static class GameLinkValue
{
    /// <summary>The value as a number, if it reads as one. Digits are pulled out of text so a game
    /// that publishes <c>"87%"</c>, <c>"12 / 30"</c> or <c>"1,234"</c> still feeds an indicator —
    /// the first number in the text wins, which is the one a human would read too.</summary>
    internal static double? AsNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return d;

        // "87%", "12 / 30", "1 234 m/s": take the leading run that looks numeric.
        int i = 0, n = text!.Length;
        while (i < n && !(char.IsDigit(text[i]) || text[i] == '-' || text[i] == '+')) i++;
        int start = i;
        if (i < n && (text[i] == '-' || text[i] == '+')) i++;
        bool dot = false;
        while (i < n && (char.IsDigit(text[i]) || (text[i] == '.' && !dot)))
        {
            if (text[i] == '.') dot = true;
            i++;
        }
        return i > start &&
               double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out d)
            ? d
            : null;
    }

    /// <summary>Whether the value reads as ON. Booleans and numbers answer for themselves; a word
    /// is false only when it is one of the words that MEAN false — anything else the game bothered
    /// to publish counts as something being there.</summary>
    internal static bool IsOn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text!.Trim();

        if (bool.TryParse(t, out bool b)) return b;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return Math.Abs(d) > double.Epsilon;

        return !t.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !t.Equals("off", StringComparison.OrdinalIgnoreCase)
            && !t.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !t.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !t.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a value satisfies a state's <c>Match</c>. Numeric on both sides compares as
    /// numbers ("3" matches "3.0"), otherwise it is a case-insensitive text match; an empty Match
    /// means "whatever reads as true", which is how a state watching its own flag works.</summary>
    internal static bool Matches(string? value, string? match)
    {
        if (string.IsNullOrEmpty(match)) return IsOn(value);
        if (value is null) return false;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
            double.TryParse(match, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            return Math.Abs(a - b) < 1e-9;

        return value.Trim().Equals(match!.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
