namespace K2.Core.Services;

/// <summary>
/// "Foyer" mode: instead of finding and clicking a button in home.google.com's DOM (the
/// original approach — see <see cref="GoogleHomeJs"/>), K2 records the actual backend RPC the
/// page fires when the user performs an action, and later replays that request verbatim.
///
/// The endpoint is Google's internal Home API — <c>POST https://googlehomefoyer-pa.clients6.
/// google.com/$rpc/google.internal.home.foyer.v1.HomeControlService/UpdateTraits</c>, gRPC-web
/// with <c>content-type: application/json+protobuf</c> (positional arrays, protobuf fields by
/// index). A confirmed real on/off body looks like:
/// <code>
/// [[[ ["&lt;device-guid&gt;", ["&lt;agent-id&gt;", "&lt;agent-device-id&gt;"]],
///     [["onOff", [["onOff", [null,null,null,1]]]]] ]]]
/// </code>
/// device reference (Home Graph guid + the third-party agent's own ids), then the traits to
/// write; the trailing <c>1</c>/<c>0</c> is the bool (confirmed by the user: flipping it turns
/// the physical light on/off).
///
/// **K2 deliberately does NOT parse any of that.** The body is stored and replayed as an opaque
/// string, which is what makes this generic: brightness, colour, volume, scenes — any action the
/// web UI can perform produces its own UpdateTraits body, and recording it is all K2 needs. It
/// also means a schema change on Google's side only breaks bindings that need re-recording,
/// never K2's code.
///
/// Why this beats the DOM approach: no card matching, no page navigation, no waiting for
/// Angular to render, no synthetic pointer gymnastics (see the 12th-14th pass in CHANGELOG),
/// and the device reference is a stable id rather than a display name that changes with the
/// page layout or the room the device sits in.
///
/// The one thing that CANNOT be replayed verbatim is the <c>authorization</c> header:
/// <c>SAPISIDHASH &lt;ts&gt;_&lt;sha1(ts + " " + SAPISID + " " + origin)&gt;</c> embeds a
/// timestamp and expires, so <c>auth()</c> recomputes it per request from the SAPISID cookie
/// (readable from JS on .google.com — that is how Google's own client-side auth works; the
/// 1P/3P variants carry the same value in practice, confirmed against a real request where all
/// three hashes were identical). Everything else is either a constant or covered by
/// <c>credentials: 'include'</c>. The headers Chrome adds on its own (<c>x-browser-validation</c>,
/// <c>x-server-token</c>, <c>x-foyer-client-environment</c>) were confirmed NOT required.
///
/// Must run from a document on the home.google.com origin — the endpoint is CORS-restricted to
/// it and the request needs that origin's cookies.
/// </summary>
internal static class GoogleHomeFoyer
{
    /// <summary>Public web API key home.google.com itself sends. Recorded per-binding as well
    /// (see <c>GoogleHomeBinding.FoyerApiKey</c>) so a rotation only needs a re-record; this is
    /// the fallback for bindings captured before that field existed.</summary>
    public const string DefaultApiKey = "AIzaSyCMqap8NH88PrhvoBwY1W8ChRUJRjIOJXM";

    /// <summary>Injected via <c>AddScriptToExecuteOnDocumentCreatedAsync</c> into both WebView2
    /// instances, alongside <see cref="GoogleHomeJs.Helpers"/>. Installs the recorder (setup
    /// window) and exposes the replay entry point (trigger view).</summary>
    public const string Helpers = """
        window.__k2ghf = {
            // Recording is explicitly armed by the setup window and disarms itself after one
            // captured request, so ordinary browsing never posts anything back to K2.
            armed: false,
            recordTag: '',
            lastCardName: '',
            lastIconName: '',
            endpoint: '/google.internal.home.foyer.v1.HomeControlService/UpdateTraits',
            apiKey: 'AIzaSyCMqap8NH88PrhvoBwY1W8ChRUJRjIOJXM',
            rawFetch: null,

            arm: function(tag) {
                window.__k2ghf.armed = true;
                window.__k2ghf.recordTag = tag || '';
                window.__k2ghf.lastCardName = '';
                window.__k2ghf.lastIconName = '';
            },
            disarm: function() {
                window.__k2ghf.armed = false;
            },

            // SAPISIDHASH: sha1(unixSeconds + " " + SAPISID + " " + origin), emitted three
            // times for the plain/1P/3P cookie variants (same cookie value in practice — a
            // real captured request had all three hashes identical). Recomputed per call
            // because the timestamp inside it expires.
            auth: async function() {
                var jar = {};
                document.cookie.split('; ').forEach(function(entry) {
                    var i = entry.indexOf('=');
                    if (i > 0) jar[entry.slice(0, i)] = entry.slice(i + 1);
                });
                var sapisid = jar['SAPISID'] || jar['__Secure-3PAPISID'] || jar['__Secure-1PAPISID'];
                if (!sapisid) return '';
                var ts = Math.floor(Date.now() / 1000);
                var digest = await crypto.subtle.digest('SHA-1', new TextEncoder().encode(ts + ' ' + sapisid + ' https://home.google.com'));
                var hex = Array.prototype.map.call(new Uint8Array(digest), function(b) {
                    return ('0' + b.toString(16)).slice(-2);
                }).join('');
                return 'SAPISIDHASH ' + ts + '_' + hex
                    + ' SAPISID1PHASH ' + ts + '_' + hex
                    + ' SAPISID3PHASH ' + ts + '_' + hex;
            },

            // ExecuteScriptAsync cannot await a promise (it would serialize the Promise object
            // itself), so the outcome comes back through postMessage keyed by the caller's
            // nonce instead of through the script's return value.
            replay: function(nonce, url, body, apiKey, authUser) {
                function reply(payload) {
                    payload.type = 'foyerResult';
                    payload.nonce = nonce;
                    try { window.chrome.webview.postMessage(JSON.stringify(payload)); } catch (e) {}
                }
                window.__k2ghf.auth().then(function(auth) {
                    if (!auth) { reply({ status: 'noauth' }); return; }
                    var send = window.__k2ghf.rawFetch || window.fetch;
                    return send.call(window, url, {
                        method: 'POST', mode: 'cors', credentials: 'include',
                        headers: {
                            'authorization': auth,
                            'content-type': 'application/json+protobuf',
                            'x-goog-api-key': apiKey || window.__k2ghf.apiKey,
                            'x-goog-authuser': authUser || '0',
                            'x-user-agent': 'grpc-web-javascript/0.1'
                        },
                        body: body
                    }).then(function(res) {
                        return res.text().then(function(text) {
                            reply({ status: res.ok ? 'ok' : 'http', code: res.status, detail: text.slice(0, 200) });
                        });
                    });
                }).catch(function(err) {
                    reply({ status: 'error', detail: String((err && err.message) || err) });
                });
            },

            report: function(url, body, headerLookup) {
                window.__k2ghf.armed = false;
                try {
                    window.chrome.webview.postMessage(JSON.stringify({
                        type: 'foyer',
                        tag: window.__k2ghf.recordTag,
                        cardName: window.__k2ghf.lastCardName,
                        iconName: window.__k2ghf.lastIconName,
                        url: url,
                        body: body,
                        apiKey: headerLookup('x-goog-api-key'),
                        authUser: headerLookup('x-goog-authuser')
                    }));
                } catch (e) {}
            }
        };

        (function() {
            // Both transports are wrapped: grpc-web's JS client goes through XMLHttpRequest,
            // but DevTools' "Copy as fetch" renders every request as a fetch regardless of how
            // it was actually issued, so which one home.google.com uses cannot be told from a
            // copied request alone. Wrapping both costs nothing and removes the guess.
            var originalFetch = window.fetch;
            window.__k2ghf.rawFetch = originalFetch;

            window.fetch = function(input, init) {
                try {
                    var url = (typeof input === 'string') ? input : ((input && input.url) || '');
                    var body = (init && typeof init.body === 'string') ? init.body : '';
                    if (window.__k2ghf.armed && body && url.indexOf(window.__k2ghf.endpoint) >= 0) {
                        var headers = (init && init.headers) || {};
                        window.__k2ghf.report(url, body, function(name) {
                            if (typeof headers.get === 'function') return headers.get(name) || '';
                            var keys = Object.keys(headers);
                            for (var i = 0; i < keys.length; i++) {
                                if (keys[i].toLowerCase() === name) return headers[keys[i]];
                            }
                            return '';
                        });
                    }
                } catch (e) {}
                return originalFetch.apply(this, arguments);
            };

            var originalOpen = XMLHttpRequest.prototype.open;
            var originalSetHeader = XMLHttpRequest.prototype.setRequestHeader;
            var originalSend = XMLHttpRequest.prototype.send;

            XMLHttpRequest.prototype.open = function(method, url) {
                try { this.__k2url = String(url || ''); } catch (e) {}
                return originalOpen.apply(this, arguments);
            };
            XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
                try {
                    if (!this.__k2headers) this.__k2headers = {};
                    this.__k2headers[String(name).toLowerCase()] = value;
                } catch (e) {}
                return originalSetHeader.apply(this, arguments);
            };
            XMLHttpRequest.prototype.send = function(body) {
                try {
                    var self = this;
                    if (window.__k2ghf.armed && typeof body === 'string' && body
                        && self.__k2url && self.__k2url.indexOf(window.__k2ghf.endpoint) >= 0) {
                        window.__k2ghf.report(self.__k2url, body, function(name) {
                            return (self.__k2headers && self.__k2headers[name]) || '';
                        });
                    }
                } catch (e) {}
                return originalSend.apply(this, arguments);
            };

            // Passive (never preventDefault'd — the click MUST reach the page, that is what
            // makes it fire the request we are recording): remembers which device tile was
            // touched last, so the setup window can pre-fill the binding name with the device's
            // own "Room / Device" label and give the key the device's own Material icon instead
            // of leaving the user to type and pick both.
            document.addEventListener('click', function(e) {
                try {
                    if (!window.__k2ghf.armed) return;
                    var path = e.composedPath ? e.composedPath() : [e.target];
                    var origin = path.length > 0 ? path[0] : e.target;
                    var control = window.__k2gh.nearestControl(origin);
                    var card = window.__k2gh.findCardFor(control);
                    if (!card) return;
                    window.__k2ghf.lastCardName = window.__k2gh.displayNameFor(card);
                    window.__k2ghf.lastIconName = window.__k2gh.iconNameFor(card);
                } catch (err) {}
            }, true);
        })();
        """;
}
