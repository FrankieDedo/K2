using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace K2.Core.Services;

/// <summary>
/// Drives home.google.com through a real, persistent WebView2 (Chromium/Edge) session so
/// K2 can trigger a Google Home device the same way a user clicking the page would — there
/// is no public API for a third-party app to control arbitrary devices in a user's Google
/// Home structure. Built against the CURRENT page layout by design (user decision): if
/// Google reshuffles the page, a binding stops matching and gets re-captured via
/// <see cref="GoogleHomeSetupWindow"/>, same "fix on break, via an update" philosophy as
/// K2's other reverse-engineered protocols.
///
/// One shared <see cref="CoreWebView2Environment"/> (same user-data folder, so the Google
/// login session persists across app restarts) backs two WebView2 controls: a hidden one
/// here used only to fire actions, and the visible one in <see cref="GoogleHomeSetupWindow"/>
/// used to log in and capture bindings — both must share the SAME environment instance
/// (not just the same folder path) to avoid the profile being locked against itself.
/// </summary>
public sealed class GoogleHomeBridge
{
    private sealed class ClickedInfo
    {
        public string? Tag { get; set; }
        public string? Role { get; set; }
        public string? AriaLabel { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }
    }

    private sealed class TriggerResult
    {
        public string Status { get; set; } = "notfound";
        public string? ScopeTag { get; set; }
        public string? ScopeRole { get; set; }
        public int ScopeButtons { get; set; }
        public ClickedInfo? Clicked { get; set; }
    }

    /// <summary>Outcome of a Foyer replay, posted back from the page — see
    /// <see cref="GoogleHomeFoyer"/> for why it arrives by message rather than as the script's
    /// return value.</summary>
    private sealed class FoyerResult
    {
        public string? Type { get; set; }
        public string? Nonce { get; set; }
        public string Status { get; set; } = "error";
        public int Code { get; set; }
        public string? Detail { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static GoogleHomeBridge Instance { get; } = new();

    private GoogleHomeBridge() { }

    private CoreWebView2Environment? _environment;
    private Task<CoreWebView2Environment>? _environmentTask;

    private Window? _hiddenWindow;
    private WebView2? _triggerView;
    private string? _currentPath;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<FoyerResult>> _pendingReplays = new();

    private static string ProfileFolder => Path.Combine(K2Paths.Root, "GoogleHome");

    /// <summary>Shared environment used by both the hidden trigger view here and
    /// <see cref="GoogleHomeSetupWindow"/>'s visible one.</summary>
    public Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        return _environmentTask ??= CreateEnvironmentAsync();
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        Directory.CreateDirectory(ProfileFolder);
        _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfileFolder);
        return _environment;
    }

    /// <summary>Fire-and-forget wrapper for <see cref="ButtonActionEngine"/>'s synchronous
    /// dispatch — errors are caught and logged internally rather than thrown.</summary>
    public void Trigger(string bindingId, Action<string> log)
    {
        _ = TriggerAsync(bindingId, log);
    }

    public async Task TriggerAsync(string bindingId, Action<string> log)
    {
        var binding = GoogleHomeStore.Find(bindingId);
        if (binding is null)
        {
            log($"[EXEC] googlehome: binding not found (id \"{bindingId}\") — was it deleted?");
            return;
        }

        // Account-wide flag (see GoogleHomeStore.Disconnect/ReconcileScan): a "Disconnetti"
        // leaves every binding in place but refuses to trigger any of them — dom or foyer —
        // until a fresh login re-finds the same devices, rather than silently attempting a
        // request against a signed-out session.
        if (!GoogleHomeStore.IsConnected)
        {
            log($"[EXEC] googlehome -> {binding.Name}: Google Home disconnected — reopen \"Manage Google Home devices\" and sign in again");
            return;
        }

        try
        {
            var view = await EnsureTriggerViewAsync();

            if (string.Equals(binding.Kind, "foyer", StringComparison.Ordinal))
            {
                await TriggerFoyerAsync(view, binding, log);
                return;
            }

            if (!string.Equals(_currentPath, binding.PagePath, StringComparison.Ordinal))
            {
                await NavigateAsync(view, "https://home.google.com" + binding.PagePath);
                _currentPath = binding.PagePath;
                // NavigationCompleted only means the top-level document loaded — this is an
                // Angular SPA that fetches the automations/devices list asynchronously
                // afterward, so the very first trigger right after a fresh navigation would
                // otherwise query an empty/half-rendered page (same reasoning as
                // GoogleHomeSetupWindow's post-login auto-scan delay).
                await Task.Delay(1500);
            }

            // Two-level match: cardText identifies the specific routine/device card (repeated
            // cards whose own control shares one generic accessible name across every card of
            // that kind — see GoogleHomeJs), controlLabel identifies which control inside that
            // card to click. Empty cardText means the click was on a one-off page control with
            // no repeated-card ancestor: match page-wide instead.
            string js = $$"""
                (function() {
                    var cardText = {{JsonSerializer.Serialize(binding.CardText)}};
                    var controlLabel = {{JsonSerializer.Serialize(binding.ControlLabel)}};
                    var scope = document;
                    if (cardText) {
                        scope = window.__k2gh.findCard(cardText);
                        if (!scope) return JSON.stringify({ status: 'cardnotfound' });
                    }
                    var el = window.__k2gh.findControlLike(scope, controlLabel);
                    if (!el) {
                        var isEl = scope && scope.nodeType === 1;
                        return JSON.stringify({
                            status: 'notfound',
                            scopeTag: isEl ? scope.tagName : null,
                            scopeRole: isEl && scope.getAttribute ? scope.getAttribute('role') : null,
                            scopeButtons: isEl ? scope.querySelectorAll('button, [role="button"]').length : 0
                        });
                    }
                    var clicked = window.__k2gh.describe(el);
                    window.__k2gh.simulateClick(el);
                    return JSON.stringify({ status: 'ok', clicked: clicked });
                })();
                """;

            string rawResult = await view.CoreWebView2.ExecuteScriptAsync(js);
            string json = JsonSerializer.Deserialize<string>(rawResult) ?? "{}";
            var outcome = JsonSerializer.Deserialize<TriggerResult>(json, JsonOpts) ?? new TriggerResult();

            if (outcome.Status == "ok")
            {
                var c = outcome.Clicked;
                log(c is null
                    ? $"[EXEC] googlehome -> {binding.Name}"
                    : $"[EXEC] googlehome -> {binding.Name} (clicked <{c.Tag} role={c.Role} aria-label=\"{c.AriaLabel}\" title=\"{c.Title}\" text=\"{c.Text}\">)");
            }
            else if (outcome.Status == "cardnotfound")
                log($"[EXEC] googlehome -> {binding.Name}: card \"{binding.CardText}\" not found — renamed or removed on the page? try re-capturing this binding");
            else
                log($"[EXEC] googlehome -> {binding.Name}: control not found in card (scope=<{outcome.ScopeTag} role={outcome.ScopeRole} buttonsInside={outcome.ScopeButtons}>) — Google may have changed the page, try re-capturing this binding");
        }
        catch (Exception ex)
        {
            log($"[EXEC] googlehome -> {binding.Name}: {ex.Message}");
        }
    }

    /// <summary>Foyer mode: replays the recorded RPC (see <see cref="GoogleHomeFoyer"/>). No
    /// navigation to the binding's page, no card matching, no Angular settle delay — the only
    /// requirement is that the view sits on a loaded home.google.com document, since the
    /// endpoint is CORS-restricted to that origin and needs its cookies.</summary>
    private async Task TriggerFoyerAsync(WebView2 view, GoogleHomeBinding binding, Action<string> log)
    {
        if (_currentPath is null)
        {
            await NavigateAsync(view, "https://home.google.com/");
            _currentPath = "/";
        }

        // An expired login redirects the view to accounts.google.com, where the replay would
        // fail on CORS with a message that says nothing about the real cause.
        if (!Uri.TryCreate(view.CoreWebView2.Source, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("home.google.com", StringComparison.OrdinalIgnoreCase))
        {
            log($"[EXEC] googlehome -> {binding.Name}: not signed in (the session landed on \"{uri?.Host}\") — reopen \"Manage Google Home devices\" and sign in again");
            return;
        }

        // Alternating binding: send whichever body is due, then flip — see
        // GoogleHomeBinding.FoyerBodyAlt.
        bool alternating = binding.FoyerBodyAlt.Length > 0;
        string body = alternating && binding.AltNext ? binding.FoyerBodyAlt : binding.FoyerBody;

        string nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<FoyerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReplays[nonce] = tcs;

        try
        {
            string js = "window.__k2ghf.replay("
                + JsonSerializer.Serialize(nonce) + ", "
                + JsonSerializer.Serialize(binding.FoyerUrl) + ", "
                + JsonSerializer.Serialize(body) + ", "
                + JsonSerializer.Serialize(binding.FoyerApiKey) + ", "
                + JsonSerializer.Serialize(binding.FoyerAuthUser) + ");";
            await view.CoreWebView2.ExecuteScriptAsync(js);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != tcs.Task)
            {
                log($"[EXEC] googlehome -> {binding.Name}: no response from Google within 10s");
                return;
            }

            var result = await tcs.Task;
            switch (result.Status)
            {
                case "ok":
                    if (alternating) GoogleHomeStore.SetAltNext(binding.Id, !binding.AltNext);
                    log($"[EXEC] googlehome -> {binding.Name}");
                    break;
                case "noauth":
                    log($"[EXEC] googlehome -> {binding.Name}: SAPISID cookie missing — the Google session expired, sign in again from \"Manage Google Home devices\"");
                    break;
                case "http":
                    log($"[EXEC] googlehome -> {binding.Name}: Google returned HTTP {result.Code} ({result.Detail}) — if this persists, re-record the action");
                    break;
                default:
                    log($"[EXEC] googlehome -> {binding.Name}: {result.Detail}");
                    break;
            }
        }
        finally
        {
            _pendingReplays.TryRemove(nonce, out _);
        }
    }

    private void OnTriggerMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(json)) return;

        FoyerResult? result;
        try { result = JsonSerializer.Deserialize<FoyerResult>(json, JsonOpts); }
        catch { return; }

        if (result?.Type != "foyerResult" || result.Nonce is null) return;
        if (_pendingReplays.TryGetValue(result.Nonce, out var tcs)) tcs.TrySetResult(result);
    }

    private async Task<WebView2> EnsureTriggerViewAsync()
    {
        if (_triggerView is not null) return _triggerView;

        // Offscreen, never shown: WebView2 still needs a real HWND to host the browser
        // process, so this is a real (positioned off the visible desktop) window rather
        // than a hidden one — Show() is what actually creates the HWND. Sized like a real
        // desktop viewport (NOT 1x1, an earlier version of this code): home.google.com is a
        // responsive Angular app, and a 1x1 viewport falls below every real breakpoint —
        // Angular's own responsive layout can render a materially different (or partially
        // unmounted/non-interactive) DOM in that state than the normal desktop layout the
        // page was captured against in the visible setup window, which would explain a
        // click "succeeding" (a real element found and clicked) without the device actually
        // responding.
        _hiddenWindow = new Window
        {
            Width = 1280,
            Height = 900,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
        };

        _triggerView = new WebView2();
        _hiddenWindow.Content = _triggerView;
        _hiddenWindow.Show();

        var env = await GetSharedEnvironmentAsync();
        await _triggerView.EnsureCoreWebView2Async(env);
        await _triggerView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GoogleHomeJs.Helpers);
        await _triggerView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GoogleHomeFoyer.Helpers);
        _triggerView.CoreWebView2.WebMessageReceived += OnTriggerMessageReceived;
        return _triggerView;
    }

    private static Task NavigateAsync(WebView2 view, string url)
    {
        var tcs = new TaskCompletionSource();
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            tcs.TrySetResult();
        }
        view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        view.CoreWebView2.Navigate(url);
        return tcs.Task;
    }
}
