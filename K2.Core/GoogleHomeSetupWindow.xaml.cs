using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// "Manage Google Home devices" window: a real, visible WebView2 session on
/// home.google.com (same shared profile as <see cref="GoogleHomeBridge"/>, so logging in
/// here is what makes triggering work later). Once signed in, every device on the page is
/// discovered and imported automatically (see <see cref="RunAutoImportAsync"/> and
/// <see cref="GoogleHomeStore.ReconcileScan"/>) — the browser and its manual buttons hide
/// behind the persistent, checkbox-driven bindings list, which is the only surface the user
/// normally needs, and the window shrinks to just that list (see <see cref="UpdateStateUi"/>).
/// The app-wide Debug mode (<see cref="AppSettings.DebugMode"/> — Settings tab, not a
/// separate toggle here) re-exposes the older Foyer recording flow (a real backend RPC
/// captured from a live click — see <see cref="GoogleHomeFoyer"/>) for the rare case a
/// simulated DOM click isn't enough (dimmers, colours, scenes…). Opened from
/// <see cref="ButtonActionDialog"/>'s "googlehome" combo panel (see ButtonActionDialog.Simple.cs).
/// </summary>
public partial class GoogleHomeSetupWindow : Window
{
    /// <summary>A recorded Foyer RPC (see <see cref="GoogleHomeFoyer"/>) — the body is stored
    /// and replayed opaquely, K2 never parses it.</summary>
    private sealed class FoyerPayload
    {
        public string? Type { get; set; }
        /// <summary>Echoes back what <c>arm()</c> was given: the id of the binding this
        /// recording should become the OPPOSITE action of, or empty for a brand-new binding.</summary>
        public string Tag { get; set; } = "";
        /// <summary>The "Room / Device" label of the tile the user touched while recording, used
        /// only to pre-fill the name field.</summary>
        public string CardName { get; set; } = "";
        /// <summary>The tile's &lt;mat-icon&gt; ligature name, used for the key's default
        /// picture — see <see cref="GoogleHomeIconCatalog"/>.</summary>
        public string IconName { get; set; } = "";
        public string Url { get; set; } = "";
        public string Body { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string AuthUser { get; set; } = "";
    }

    /// <summary>Internal DTO for one <c>window.__k2gh.scanCards()</c> result — never shown to
    /// the user directly, just fed into <see cref="GoogleHomeStore.ReconcileScan"/>.</summary>
    private sealed class ScanResultItem
    {
        public string CardText { get; set; } = "";
        public string ControlLabel { get; set; } = "";
        public string IconName { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private FoyerPayload? _pendingFoyer;
    private string? _renamingId;
    private bool _autoScanDone;

    public GoogleHomeSetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        // Both can change while this window is open from somewhere else entirely: Debug mode
        // from the main window's Settings tab, IsConnected from... well, only this window
        // changes it today, but keeping the two paths symmetric costs nothing and avoids a
        // stale UI if that ever stops being true.
        AppSettings.Changed += UpdateStateUi;
        GoogleHomeStore.ConnectionChanged += UpdateStateUi;
        Closed += (_, _) =>
        {
            AppSettings.Changed -= UpdateStateUi;
            GoogleHomeStore.ConnectionChanged -= UpdateStateUi;
        };
        RefreshBindingsList();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        var env = await GoogleHomeBridge.Instance.GetSharedEnvironmentAsync();
        await WebGh.EnsureCoreWebView2Async(env);
        await WebGh.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GoogleHomeJs.Helpers);
        await WebGh.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GoogleHomeFoyer.Helpers);
        WebGh.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WebGh.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        // Navigating straight to home.google.com already drops an unauthenticated session
        // directly into Google's own login page (an in-place redirect, not a popup) — there
        // is no separate "login window" to close here, it's the same embedded browser.
        WebGh.CoreWebView2.Navigate("https://home.google.com/");
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_autoScanDone) return;
        if (!e.IsSuccess) return;
        if (!Uri.TryCreate(WebGh.CoreWebView2.Source, UriKind.Absolute, out var uri)) return;
        if (!uri.Host.EndsWith("home.google.com", StringComparison.OrdinalIgnoreCase)) return;

        // First time we're actually on home.google.com (not still on accounts.google.com's
        // login page): log in is done. Also re-armed after "Disconnetti" navigates back here —
        // see BtnDisconnect_Click.
        _autoScanDone = true;
        await RunAutoImportAsync();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(json)) return;

        FoyerPayload? foyer;
        try { foyer = JsonSerializer.Deserialize<FoyerPayload>(json, JsonOpts); }
        catch { return; }
        if (foyer?.Type == "foyer") OnFoyerRecorded(foyer);
    }

    /// <summary>Arms the Foyer recorder and lets the user drive the page normally: whatever
    /// action they perform (turn on, dim, change colour, run a scene) makes home.google.com fire
    /// exactly one UpdateTraits RPC, which K2 captures and can replay forever after. Unlike a
    /// simulated DOM click, this click is NOT swallowed — the page must really act, otherwise
    /// there is no request to record. "Avanzate (debug)" only.</summary>
    private void BtnRecord_Click(object sender, RoutedEventArgs e) => ArmRecorder(tag: "");

    /// <summary>Records the OPPOSITE action into the selected binding, turning it into an
    /// alternating one (one key press flips between the two) — a recorded RPC is a fixed
    /// command, so an "on" recording alone can never turn the device back off.</summary>
    private void BtnRecordAlt_Click(object sender, RoutedEventArgs e)
    {
        if (LstBindings.SelectedItem is not GoogleHomeBinding binding) return;
        ArmRecorder(tag: binding.Id);
    }

    private void ArmRecorder(string tag)
    {
        BtnRecord.IsEnabled = false;
        BtnRecordAlt.IsEnabled = false;
        LblCaptureHint.Text = Loc.Get(tag.Length == 0 ? "gh_record_armed" : "gh_record_alt_armed");
        _ = WebGh.CoreWebView2.ExecuteScriptAsync($"window.__k2ghf.arm({JsonSerializer.Serialize(tag)})");
    }

    private void OnFoyerRecorded(FoyerPayload payload)
    {
        LblCaptureHint.Text = Loc.Get("gh_record_hint");
        BtnRecord.IsEnabled = true;
        UpdateRecordAltEnabled();

        if (payload.Tag.Length > 0)
        {
            // Opposite action for an existing binding: nothing to name, just attach it.
            GoogleHomeStore.SetFoyerAlt(payload.Tag, payload.Body);
            RefreshBindingsList();
            return;
        }

        // Fire-and-forget: the glyph is only needed later, when a key gets bound to this
        // device, and the user shouldn't wait on it to finish naming the binding.
        _ = CacheIconsAsync(new[] { payload.IconName });

        _pendingFoyer = payload;
        _renamingId = null;
        TxtCaptureName.Text = payload.CardName;
        PnlCaptureName.Visibility = Visibility.Visible;
        TxtCaptureName.SelectAll();
        TxtCaptureName.Focus();
    }

    private void LstBindings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateRecordAltEnabled();

    /// <summary>"Record opposite action" only makes sense for exactly one selected Foyer
    /// binding — a DOM binding clicks a tile the page itself toggles, so it needs no opposite.</summary>
    private void UpdateRecordAltEnabled()
    {
        BtnRecordAlt.IsEnabled = LstBindings.SelectedItems.Count == 1
            && LstBindings.SelectedItem is GoogleHomeBinding b
            && b.Kind == "foyer";
    }

    private async void BtnForceRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnForceRefresh.IsEnabled = false;
        try
        {
            if (WebGh.CoreWebView2 is null) return;
            // If the shared view has wandered off home.google.com (e.g. left mid-debug on a
            // device's own sub-page), bring it back before scanning — the card list only
            // exists on the main Devices/Automations pages.
            if (!Uri.TryCreate(WebGh.CoreWebView2.Source, UriKind.Absolute, out var uri)
                || !uri.Host.EndsWith("home.google.com", StringComparison.OrdinalIgnoreCase))
            {
                await NavigateWebAsync("https://home.google.com/");
            }
            await RunAutoImportAsync();
        }
        finally
        {
            BtnForceRefresh.IsEnabled = true;
        }
    }

    private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        BtnDisconnect.IsEnabled = false;
        try
        {
            if (WebGh.CoreWebView2 is not null)
                await NavigateWebAsync("https://accounts.google.com/Logout");

            GoogleHomeStore.Disconnect();
            // Re-arm the post-login auto-import for whenever the user signs back in — see
            // OnNavigationCompleted.
            _autoScanDone = false;
            RefreshBindingsList();

            // Lands back on Google's login page (home.google.com redirects there in-place when
            // signed out) so the user can reconnect without closing/reopening this window.
            if (WebGh.CoreWebView2 is not null)
                await NavigateWebAsync("https://home.google.com/");
        }
        finally
        {
            BtnDisconnect.IsEnabled = true;
        }
    }

    private void ChkBindingEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: GoogleHomeBinding binding } cb) return;
        GoogleHomeStore.SetEnabled(binding.Id, cb.IsChecked == true);
    }

    /// <summary>
    /// Rescans the current page and reconciles the result into the store (see
    /// <see cref="GoogleHomeStore.ReconcileScan"/>): known devices are refreshed in place,
    /// new ones are added pre-selected, vanished ones are dropped. Run once automatically
    /// after every successful sign-in (<see cref="OnNavigationCompleted"/>) and again on demand
    /// from "Forza aggiornamento". The delay gives the Angular SPA time to render its
    /// asynchronously-fetched device list — scanning immediately would see an empty/half-drawn
    /// page (same reasoning previously applied to the old manual "Scan this page" button).
    /// </summary>
    private async System.Threading.Tasks.Task RunAutoImportAsync()
    {
        await System.Threading.Tasks.Task.Delay(1500);

        string rawResult;
        try { rawResult = await WebGh.CoreWebView2.ExecuteScriptAsync("window.__k2gh.scanCards()"); }
        catch { return; }

        string json;
        try { json = JsonSerializer.Deserialize<string>(rawResult) ?? "[]"; }
        catch { json = "[]"; }

        ScanResultItem[]? items;
        try { items = JsonSerializer.Deserialize<ScanResultItem[]>(json, JsonOpts); }
        catch { items = null; }
        items ??= Array.Empty<ScanResultItem>();

        string path = "";
        if (WebGh.CoreWebView2 is not null && Uri.TryCreate(WebGh.CoreWebView2.Source, UriKind.Absolute, out var uri))
            path = uri.PathAndQuery + uri.Fragment;

        GoogleHomeStore.ReconcileScan(items.Select(i => (i.CardText, i.ControlLabel, path, i.IconName)).ToList());

        await CacheIconsAsync(items.Select(i => i.IconName));

        RefreshBindingsList();
    }

    /// <summary>
    /// Rasterizes the Material glyphs for <paramref name="iconNames"/> from the live page and
    /// caches them, so keys bound to these devices get the device's own icon (see
    /// <see cref="GoogleHomeIconCatalog"/>). Only ever renders what isn't cached yet, and only
    /// while a home.google.com page is open — the cache is filled opportunistically because a
    /// missing glyph is never fatal, it just means a caption-only tile.
    /// </summary>
    private async System.Threading.Tasks.Task CacheIconsAsync(System.Collections.Generic.IEnumerable<string> iconNames)
    {
        var missing = iconNames
            .Where(n => !string.IsNullOrEmpty(n) && GoogleHomeIconCatalog.TryGetCachedPng(n) is null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missing.Count == 0) return;

        string rawResult;
        try
        {
            string js = $"window.__k2gh.renderIcons({JsonSerializer.Serialize(missing)}, {GoogleHomeIconCatalog.RenderSize})";
            rawResult = await WebGh.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch { return; }

        System.Collections.Generic.Dictionary<string, string>? rendered;
        try
        {
            string json = JsonSerializer.Deserialize<string>(rawResult) ?? "{}";
            rendered = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);
        }
        catch { return; }
        if (rendered is null) return;

        foreach (var (name, dataUrl) in rendered)
            GoogleHomeIconCatalog.SaveFromDataUrl(name, dataUrl);
    }

    private void BtnCaptureSave_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtCaptureName.Text?.Trim() ?? "";
        if (name.Length == 0) return;

        if (_renamingId is not null)
        {
            GoogleHomeStore.Rename(_renamingId, name);
        }
        else if (_pendingFoyer is not null)
        {
            GoogleHomeStore.AddFoyer(name, _pendingFoyer.Url, _pendingFoyer.Body,
                _pendingFoyer.ApiKey, _pendingFoyer.AuthUser, _pendingFoyer.IconName);
        }

        CancelCapturePanel();
        RefreshBindingsList();
    }

    private void BtnCaptureCancel_Click(object sender, RoutedEventArgs e) => CancelCapturePanel();

    private void CancelCapturePanel()
    {
        _pendingFoyer = null;
        _renamingId = null;
        TxtCaptureName.Text = "";
        PnlCaptureName.Visibility = Visibility.Collapsed;
        BtnRecord.IsEnabled = true;
        LblCaptureHint.Text = Loc.Get("gh_record_hint");
        // The recorder disarms itself once it captures a request, but an abandoned recording
        // (user pressed Record then changed their mind) would otherwise stay armed and swallow
        // the next unrelated action they perform on the page.
        _ = WebGh.CoreWebView2?.ExecuteScriptAsync("window.__k2ghf.disarm()");
        UpdateRecordAltEnabled();
    }

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (LstBindings.SelectedItem is not GoogleHomeBinding binding) return;
        _pendingFoyer = null;
        _renamingId = binding.Id;
        TxtCaptureName.Text = binding.Name;
        PnlCaptureName.Visibility = Visibility.Visible;
        TxtCaptureName.Focus();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        // SelectedItems is a snapshot-safe copy target: removing from the store while it's
        // still backing LstBindings.ItemsSource would mutate the collection out from under
        // the enumeration.
        var selected = LstBindings.SelectedItems.OfType<GoogleHomeBinding>().ToList();
        if (selected.Count == 0) return;
        foreach (var binding in selected)
            GoogleHomeStore.Remove(binding.Id);
        RefreshBindingsList();
    }

    private void RefreshBindingsList()
    {
        var bindings = GoogleHomeStore.List();
        LstBindings.ItemsSource = null;
        LstBindings.ItemsSource = bindings;
        LblNoBindings.Visibility = bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateUi();
    }

    /// <summary>Centralizes every Visibility/size toggle driven by connection state and the
    /// app-wide Debug mode (<see cref="AppSettings.DebugMode"/> — the same flag the Settings
    /// tab's checkbox controls, not a separate per-window one: the browser and its Foyer
    /// Record buttons only reappear once connected if Debug mode is on). Once connected and
    /// out of debug mode there is nothing left on the left side, so it collapses and the whole
    /// window shrinks instead of leaving a big empty gap.</summary>
    private void UpdateStateUi()
    {
        bool connected = GoogleHomeStore.IsConnected;
        bool debug = AppSettings.DebugMode;
        bool showWeb = !connected || debug;

        BorderWeb.Visibility = showWeb ? Visibility.Visible : Visibility.Collapsed;
        LblLoginHint.Visibility = !connected ? Visibility.Visible : Visibility.Collapsed;
        PnlAdvanced.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
        PnlConnectedButtons.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        LblAdvancedHint.Visibility = connected && debug ? Visibility.Visible : Visibility.Collapsed;
        LblConnStatus.Text = Loc.Get(connected ? "gh_status_connected" : "gh_status_disconnected");

        ColWeb.Width = showWeb ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColGap.Width = showWeb ? new GridLength(12) : new GridLength(0);
        Width = showWeb ? 960 : 340;
        MinWidth = showWeb ? 720 : 300;
    }

    private System.Threading.Tasks.Task NavigateWebAsync(string url)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource();
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs ev)
        {
            WebGh.CoreWebView2.NavigationCompleted -= OnCompleted;
            tcs.TrySetResult();
        }
        WebGh.CoreWebView2.NavigationCompleted += OnCompleted;
        WebGh.CoreWebView2.Navigate(url);
        return tcs.Task;
    }
}
