using System;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace K2.UnityLink
{
    /// <summary>
    /// The actual server: periodic snapshot on Unity's main thread, HTTP listener on a background
    /// thread. Lives on its own dedicated, <c>DontDestroyOnLoad</c> GameObject (see
    /// <see cref="UnityLinkPlugin"/>'s class comment for why it is not on the plugin's own host
    /// object) so a game that recycles BepInEx's plugin GameObject cannot take the server down with
    /// it.
    ///
    /// <para><b>Two snapshots, two paces, see <c>k2want</c>.</b> K2's studio needs to see EVERYTHING
    /// while a person is picking a value that has never been read before; K2's background poll,
    /// once a reading is saved, only ever asks for the handful of paths it actually uses. Serving
    /// both from one cache would force a choice — walk everything every tick forever (defeats the
    /// point of ever telling this mod what is actually wanted), or narrow the ONE cache and make
    /// browsing for something new impossible once anything is saved. So there are two: a full one,
    /// refreshed slowly (nobody needs it fresher than a human can look at it), and a narrow one,
    /// refreshed at the normal fast pace, resolving only whatever the last <c>k2want</c> request
    /// named. A plain request (no <c>k2want</c> query string) gets the full one; one carrying
    /// <c>k2want=&lt;comma-separated paths&gt;</c> gets the narrow one, and its wanted set replaces
    /// whatever the narrow snapshot was tracking.
    /// </summary>
    internal sealed class UnityLinkRunner : MonoBehaviour
    {
        private ManualLogSource _log = null!;
        private ConfigEntry<int> _port = null!;
        private ConfigEntry<int> _refreshMs = null!;
        private ConfigEntry<int> _fullRefreshMs = null!;
        private ConfigEntry<int> _maxObjects = null!;
        private ConfigEntry<int> _maxDepth = null!;
        private ConfigEntry<int> _maxFieldsPerComponent = null!;
        private ConfigEntry<bool> _includeInactive = null!;
        private ConfigEntry<bool> _includePrivateFields = null!;
        private ConfigEntry<bool> _showPublicFields = null!;
        private ConfigEntry<int> _maxNestedDepth = null!;

        private HttpListener? _listener;
        private Thread? _serverThread;

        private const string EmptySnapshot = "{\"scenes\":[],\"objects\":{}}";
        private volatile string _fullSnapshotJson = EmptySnapshot;
        private volatile string _narrowSnapshotJson = EmptySnapshot;

        /// <summary>The most recently requested <c>k2want</c> set. Replaced (never merged) on every
        /// request that carries one — an old reading's path that stopped being wanted must stop
        /// costing anything, not linger forever because nobody explicitly asked to drop it.</summary>
        private volatile string[] _wantedPaths = Array.Empty<string>();

        private volatile bool _running;
        private float _nextNarrowRefreshAt;

        /// <summary>Raised by a request that needs the full dump, cleared by the frame that builds
        /// it. The full walk is the expensive one and it is NOT on a timer any more: running it every
        /// few seconds forever cost a visible stutter in-game (reported on Unturned 2026-09-14, at
        /// exactly the old refresh interval) to keep something fresh that only matters while a human
        /// is looking at a value picker.</summary>
        private volatile bool _fullRequested;
        private readonly ManualResetEventSlim _fullReady = new(false);
        private long _lastFullUtcTicks;

        /// <summary>Search term for the pending build, or null for "everything". A searched result is
        /// never cached (a different term is a different answer) and never replaces the full one.</summary>
        private volatile string? _pendingFind;
        private volatile string _findSnapshotJson = EmptySnapshot;

        /// <summary>Searches are serialised: two at once would race for the one pending slot, and the
        /// loser would be handed the other one's answer.</summary>
        private readonly object _findGate = new();

        internal void Init(ManualLogSource log, ConfigEntry<int> port, ConfigEntry<int> refreshMs,
                            ConfigEntry<int> fullRefreshMs, ConfigEntry<int> maxObjects, ConfigEntry<int> maxDepth,
                            ConfigEntry<int> maxFieldsPerComponent, ConfigEntry<bool> includeInactive,
                            ConfigEntry<bool> includePrivateFields, ConfigEntry<bool> showPublicFields,
                            ConfigEntry<int> maxNestedDepth)
        {
            _log = log;
            _port = port;
            _refreshMs = refreshMs;
            _fullRefreshMs = fullRefreshMs;
            _maxObjects = maxObjects;
            _maxDepth = maxDepth;
            _maxFieldsPerComponent = maxFieldsPerComponent;
            _includeInactive = includeInactive;
            _includePrivateFields = includePrivateFields;
            _showPublicFields = showPublicFields;
            _maxNestedDepth = maxNestedDepth;

            StartServer();
        }

        private SceneWalker.Options CurrentOptions() => new(
            _maxObjects.Value, _maxDepth.Value, _includeInactive.Value, _maxFieldsPerComponent.Value,
            _includePrivateFields.Value, _showPublicFields.Value, _maxNestedDepth.Value);

        private void Update()
        {
            float now = Time.unscaledTime;

            if (now >= _nextNarrowRefreshAt)
            {
                _nextNarrowRefreshAt = now + Mathf.Max(0.05f, _refreshMs.Value / 1000f);
                var wanted = _wantedPaths;
                if (wanted.Length > 0)
                {
                    try
                    {
                        _narrowSnapshotJson = MiniJsonWriter.Write(SceneWalker.SnapshotPaths(wanted, CurrentOptions()));
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"narrow snapshot walk failed: {ex.Message}");
                    }
                }
            }

            if (_fullRequested)
            {
                _fullRequested = false;
                string? find = _pendingFind;
                try
                {
                    // This object was DontDestroyOnLoad'd before being attached (see
                    // UnityLinkPlugin), so its own .scene IS the DontDestroyOnLoad pseudo-scene —
                    // the only way to get a handle to it, since SceneManager never lists it among
                    // loaded scenes.
                    string json = MiniJsonWriter.Write(
                        SceneWalker.Snapshot(CurrentOptions(), gameObject.scene, find));

                    if (find is null)
                    {
                        _fullSnapshotJson = json;
                        Interlocked.Exchange(ref _lastFullUtcTicks, DateTime.UtcNow.Ticks);
                    }
                    else _findSnapshotJson = json;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"full snapshot walk failed: {ex.Message}");
                }
                finally
                {
                    // Always, even on failure: a request waiting on this must not hang for its whole
                    // timeout because the walk threw.
                    _fullReady.Set();
                }
            }
        }

        /// <summary>Still logged, same reasoning as before this class existed: on its OWN dedicated
        /// object this should only ever fire on game quit, so if it fires earlier it is still worth
        /// knowing — it would mean even a standalone DontDestroyOnLoad object is not safe here.</summary>
        private void OnDestroy()
        {
            _log.LogInfo("host GameObject destroyed — HTTP listener stopping");
            StopServer();
        }

        // ───────────────────────────── HTTP server ─────────────────────────────

        /// <summary>One background thread accepting requests and answering with whatever the two
        /// periodic ticks above last computed — the listener never touches Unity state itself, only
        /// the cached strings, so it is safe off the main thread.</summary>
        private void StartServer()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port.Value}/");
                _listener.Start();
                _running = true;
                _serverThread = new Thread(ServerLoop) { IsBackground = true, Name = "K2UnityLink-HTTP" };
                _serverThread.Start();
                _log.LogInfo($"serving snapshot on http://127.0.0.1:{_port.Value}/state");
            }
            catch (Exception ex)
            {
                // Most common cause: the port is taken (another instance, another mod, the config
                // was changed but the old listener never released it). Loud in the log, but never
                // fatal to the game itself.
                _log.LogError($"could not start HTTP listener on port {_port.Value}: {ex.Message}");
            }
        }

        private void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* already gone */ }
            try { _listener?.Close(); } catch { /* already gone */ }
        }

        private void ServerLoop()
        {
            while (_running && _listener is { IsListening: true })
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (Exception ex)
                {
                    // A deliberate Stop()/Close() (game quitting, OnDestroy) also throws here — that
                    // case already has its own log line, so only the UNEXPECTED case (accept loop
                    // dying while the runner thinks it is still running) is worth another one; the
                    // process otherwise looks alive forever while nothing accepts new connections.
                    if (_running) _log.LogWarning($"HTTP accept loop stopped unexpectedly: {ex.Message}");
                    break;
                }

                try
                {
                    HandleRequest(ctx);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"request failed: {ex.Message}");
                }
            }
        }

        /// <summary>The full dump, rebuilt on demand — asked for from this background thread, built
        /// by the next <c>Update</c> (Unity state can only be touched there), waited for here.
        ///
        /// <para><c>FullRefreshMs</c> is a MAXIMUM AGE, not a period: an answer younger than that is
        /// served as-is, so clicking around a picker does not re-walk the scene on every click, and
        /// nothing walks anything at all while the game is simply being played. The wait is bounded
        /// because <c>Update</c> may not be running (game paused, loading, alt-tabbed): a caller gets
        /// the previous answer rather than a hung socket.</para></summary>
        private string FullSnapshot()
        {
            long age = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFullUtcTicks);
            if (age <= TimeSpan.FromMilliseconds(Math.Max(250, _fullRefreshMs.Value)).Ticks)
                return _fullSnapshotJson;

            lock (_findGate)
            {
                _pendingFind = null;
                _fullReady.Reset();
                _fullRequested = true;
                _fullReady.Wait(5000);
                return _fullSnapshotJson;
            }
        }

        /// <summary>Same handshake, restricted to objects matching a term — and never cached, since
        /// the next search will ask something else.</summary>
        private string FindSnapshot(string find)
        {
            lock (_findGate)
            {
                _pendingFind = find;
                _fullReady.Reset();
                _fullRequested = true;
                _fullReady.Wait(5000);
                _pendingFind = null;
                return _findSnapshotJson;
            }
        }

        /// <summary>Always closes the response, even on failure — a response left half-written (or
        /// never started) leaves the CLIENT hanging until ITS OWN timeout fires, which reads on the
        /// K2 side as "no answer in time" and points straight at K2/the network instead of here.</summary>
        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                // Three ways to ask, and only ONE of them may ever rebuild the expensive dump:
                //   k2want=<paths>  the background poll, naming what it reads — resolve just those
                //   k2full=1        somebody pressed Refresh / opened the picker — rebuild if stale
                //   neither         a plain reader: serve the last dump as it stands, build nothing
                // The third case is what keeps a poll on a link with no saved readings yet (freshly
                // created, or its readings deleted) from quietly re-walking the whole scene forever.
                string? want = ctx.Request.QueryString["k2want"];
                string json;
                if (want is not null)
                {
                    // Replace, not merge: a reading that was deleted or repointed to another link
                    // must stop being resolved on the very next request that no longer names it,
                    // not linger because an earlier request once asked for it.
                    _wantedPaths = want.Length == 0 ? Array.Empty<string>() : want.Split(',');
                    json = _narrowSnapshotJson;
                }
                else if (ctx.Request.QueryString["k2find"] is { Length: > 0 } find)
                {
                    json = FindSnapshot(find);
                }
                else if (ctx.Request.QueryString["k2full"] is not null)
                {
                    json = FullSnapshot();
                }
                else
                {
                    json = _fullSnapshotJson;
                }

                byte[] body = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"could not write response: {ex.Message}");
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { /* already gone */ }
            }
        }
    }
}
