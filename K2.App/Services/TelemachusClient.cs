using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Talks to Telemachus Reborn — the Kerbal Space Program mod that serves the game's telemetry on
/// <c>http://127.0.0.1:8085/telemachus/datalink</c> — for the DisplayPad's <c>dp_ksp</c> tiles.
///
/// <para><b>Nobody writes a request.</b> The datalink endpoint answers exactly the entries it is
/// asked for, as <c>{ label: api-string }</c> pairs. The tiles say which entries they need
/// (<see cref="Want"/>, called from the live-tile tick), and every poll is ONE request carrying
/// the union of what is on the pads right now plus <c>p.paused</c>. An entry nobody has asked
/// about for a few seconds drops out of the request, and with nothing wanted the poller stops —
/// the same pattern as <see cref="ZeroCompanyClient"/>: a game nobody is watching is not polled.</para>
///
/// <para><b>POST with a JSON body, not a query string.</b> Parameterised entries carry brackets
/// (<c>r.resource[LiquidFuel]</c>, <c>f.setSASMode[Prograde]</c>) and a body keeps them out of URL
/// escaping entirely. Telemachus accepts both forms for the same endpoint.</para>
///
/// <para><b>Commands.</b> A key press sends its entry through the same endpoint; the mod runs it on
/// the next game tick. No keystroke is involved, so the game does not need focus and the player's
/// own key bindings do not matter.</para>
///
/// <para><b>What an answer can mean.</b> A value that is <c>null</c>, missing, listed under
/// <c>unknown</c>/<c>errors</c>, or belongs to an antenna-gated family while <c>p.paused</c> is
/// not 0 (see <see cref="KspTelemachus.NeedsAntenna"/>) is reported as NOT KNOWN — never as zero,
/// which on a gear lamp or a fuel dial would be a confident lie.</para>
/// </summary>
internal static class TelemachusClient
{
    private static readonly string Endpoint =
        $"http://127.0.0.1:{KspTelemachus.DefaultPort}/telemachus/datalink";

    private const int PollMs = 500;
    private static readonly TimeSpan WantTtl = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(900) };
    private static readonly object _gate = new();

    /// <summary>API string → last time a tile asked for it.</summary>
    private static readonly Dictionary<string, DateTime> _wanted = new(StringComparer.Ordinal);

    private static Status _last = Status.Offline;
    private static Task? _poller;

    /// <summary>Set by <see cref="Send"/> so the reading right after a press does not wait out the
    /// rest of the poll interval — a SAS key should light up when it is pressed, not half a second
    /// later.</summary>
    private static volatile bool _pollNow;

    /// <summary>One reading. <see cref="Alive"/> is false while nothing answers on the port (game
    /// closed, mod missing, or still loading). <see cref="Paused"/> is Telemachus' own
    /// <c>p.paused</c>: 0 in flight with a working antenna, 1 paused, 2 no power, 3 antenna off,
    /// 4 no antenna, 5 not in the flight scene.</summary>
    internal sealed record Status(bool Alive, int Paused, IReadOnlyDictionary<string, JsonElement> Values)
    {
        public static readonly Status Offline =
            new(false, -1, new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        /// <summary>The value of one entry, or null when it is not known — see the class remarks
        /// for everything that counts as not known.</summary>
        public JsonElement? Get(string? api)
        {
            if (!Alive || api is not { Length: > 0 }) return null;
            if (Paused != 0 && KspTelemachus.NeedsAntenna(api)) return null;
            return Values.TryGetValue(api, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? v : null;
        }
    }

    /// <summary>Tells the poller these entries are on a pad right now, and returns the latest
    /// reading. Never blocks: the request runs on its own task, so the first call for a new entry
    /// answers "not known" and the next tick has the value.</summary>
    internal static Status Want(IEnumerable<string> apis)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (string api in apis) _wanted[api] = now;
            EnsurePollerLocked();
            return _last;
        }
    }

    /// <summary>A value as the text a game link would carry for it — numbers in invariant culture,
    /// booleans as true/false — so studio readings can treat Telemachus exactly like a link. Null
    /// for no value, and for vectors/objects, which no reading can show.</summary>
    internal static string? Text(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    /// <summary>Runs a Telemachus command (<c>f.stage</c>, <c>f.setSASMode[Prograde]</c>, ...).
    /// Fire and forget: the pad's press handler must not wait on a socket.</summary>
    internal static void Send(string api, Action<string> log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string json = await PostAsync(new Dictionary<string, string> { ["cmd"] = api, ["p"] = "p.paused" })
                    .ConfigureAwait(false);
                int paused = TryPaused(json);
                log(paused is 0
                    ? $"[KSP] press -> {api}"
                    // The mod refuses flight commands without a powered antenna and says so only
                    // through this code — worth a line in the log, since the key simply did nothing.
                    : $"[KSP] press -> {api} (p.paused={paused}: Telemachus may ignore it without a powered antenna)");
            }
            catch (Exception ex)
            {
                log($"[KSP] press -> {api} failed: {ex.Message}");
            }
            _pollNow = true;
        });
    }

    private static void EnsurePollerLocked()
    {
        if (_poller is { IsCompleted: false }) return;
        _poller = Task.Run(PollLoop);
    }

    private static async Task PollLoop()
    {
        bool? wasAlive = null;
        while (true)
        {
            string[] apis;
            lock (_gate)
            {
                var cutoff = DateTime.UtcNow - WantTtl;
                foreach (string stale in _wanted.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _wanted.Remove(stale);
                if (_wanted.Count == 0) { _poller = null; _last = Status.Offline; return; }
                apis = _wanted.Keys.ToArray();
            }

            Status next;
            try { next = await ReadAsync(apis).ConfigureAwait(false); }
            catch { next = Status.Offline; }

            lock (_gate) _last = next;
            if (next.Alive) TelemachusApi.RefreshOncePerRun();
            if (wasAlive != next.Alive)
            {
                App.WriteLog(next.Alive
                    ? $"[KSP] Telemachus answering on :{KspTelemachus.DefaultPort} (p.paused={next.Paused})"
                    : $"[KSP] Telemachus not answering on :{KspTelemachus.DefaultPort}");
                wasAlive = next.Alive;
            }

            for (int waited = 0; waited < PollMs && !_pollNow; waited += 50)
                await Task.Delay(50).ConfigureAwait(false);
            _pollNow = false;
        }
    }

    private static async Task<Status> ReadAsync(string[] apis)
    {
        // Labels are positional ("k0", "k1", ...): an API string is not a safe JSON key to echo
        // back, and the answer only has to be mapped to what was asked.
        var body = new Dictionary<string, string>(StringComparer.Ordinal) { ["p"] = "p.paused" };
        for (int i = 0; i < apis.Length; i++) body["k" + i] = apis[i];

        string json = await PostAsync(body).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Status.Offline;

        int paused = root.TryGetProperty("p", out var p) && p.TryGetInt32(out int pv) ? pv : -1;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (int i = 0; i < apis.Length; i++)
            if (root.TryGetProperty("k" + i, out var v))
                values[apis[i]] = v.Clone();

        return new Status(true, paused, values);
    }

    private static async Task<string> PostAsync(Dictionary<string, string> body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(Endpoint, content).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static int TryPaused(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("p", out var p) && p.TryGetInt32(out int v) ? v : -1;
        }
        catch { return -1; }
    }
}
