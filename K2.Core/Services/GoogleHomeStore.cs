using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core.Services;

/// <summary>One "button" on home.google.com captured via <see cref="GoogleHomeSetupWindow"/>.
/// Two-level match, not a single selector: home.google.com renders every automation/device as
/// a repeated card (Angular Material <c>role="listitem"</c>/<c>.device-tile</c>) whose action
/// button (e.g. the routine "run" FAB) carries the SAME generic aria-label on every card
/// ("Avvia automazione" for every routine, confirmed against a real saved page) — matching on
/// the control alone would always hit whichever card happens to be first in the page. So a
/// binding records the card's own identifying name (<see cref="CardText"/>, room-qualified
/// where the page groups by room — see <c>GoogleHomeJs.displayNameFor</c>) plus which control
/// inside that specific card to click (<see cref="ControlLabel"/>).</summary>
public sealed class GoogleHomeBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"dom" (default, and what every binding captured before Foyer mode existed is)
    /// clicks a control on the page — see the rest of this class. "foyer" replays a recorded
    /// backend RPC instead and ignores every DOM-related field below; see
    /// <see cref="GoogleHomeFoyer"/> for why that is strictly better where available.</summary>
    public string Kind { get; set; } = "dom";
    /// <summary>Foyer mode: the recorded RPC endpoint (absolute URL).</summary>
    public string FoyerUrl { get; set; } = "";
    /// <summary>Foyer mode: the recorded request body, replayed verbatim as an opaque string —
    /// K2 never parses it, which is what makes any trait (brightness, colour, volume, scenes…)
    /// work without protocol knowledge. See <see cref="GoogleHomeFoyer"/>.</summary>
    public string FoyerBody { get; set; } = "";
    /// <summary>Foyer mode, optional: the recorded body of the OPPOSITE action (e.g. "off" when
    /// <see cref="FoyerBody"/> is "on"). When set, one key press alternates between the two
    /// instead of always sending the same command — a recorded body is a fixed command, unlike
    /// clicking a tile which the page itself toggles. Empty means "always send
    /// <see cref="FoyerBody"/>".</summary>
    public string FoyerBodyAlt { get; set; } = "";
    /// <summary>Which of the two bodies the NEXT press should send; only meaningful when
    /// <see cref="FoyerBodyAlt"/> is set. Persisted so alternation survives an app restart.
    /// Can drift out of step if the device is changed from elsewhere (K2 tracks what it sent,
    /// it does not read the device's real state) — one extra press resyncs it.</summary>
    public bool AltNext { get; set; }
    /// <summary>Foyer mode: the <c>x-goog-api-key</c>/<c>x-goog-authuser</c> observed on the
    /// recorded request, so a key rotation or a multi-account setup only needs a re-record
    /// rather than a code change. Empty falls back to
    /// <see cref="GoogleHomeFoyer.DefaultApiKey"/> / "0".</summary>
    public string FoyerApiKey { get; set; } = "";
    public string FoyerAuthUser { get; set; } = "";
    /// <summary>Identifying text of the enclosing card — "Room / Device" when the page groups
    /// by room (home.google.com's Devices page), just the device/automation name otherwise.
    /// Empty when the clicked control wasn't inside a repeated card (a one-off page button), in
    /// which case matching falls back to <see cref="ControlLabel"/> alone, page-wide. This is
    /// the actual MATCH key (not just a label) — see <c>GoogleHomeJs.findCard</c>.</summary>
    public string CardText { get; set; } = "";
    /// <summary>The clicked control's aria-label (falls back to its own text content if it had
    /// none) — identifies WHICH control inside the card (e.g. "Avvia automazione"), not WHICH
    /// card; combined with <see cref="CardText"/> to disambiguate repeated cards.</summary>
    public string ControlLabel { get; set; } = "";
    /// <summary>location.pathname+search+hash on home.google.com the element was captured on
    /// (the site is a single-page app, and may keep view state outside the path alone; K2
    /// navigates back here before clicking).</summary>
    public string PagePath { get; set; } = "";
    /// <summary>The card's Material icon ligature name (e.g. "lightbulb", "outlet") if any —
    /// see <see cref="GoogleHomeIconCatalog.LabelFor"/> for a friendly label; empty when the
    /// card had no icon or none was captured.</summary>
    public string IconName { get; set; } = "";
    /// <summary>Whether this binding shows up in K2's action pickers (the "googlehome" combo
    /// in <c>ButtonActionDialog</c>). Unchecking a device only hides it from being picked for
    /// NEW key assignments — a key already bound to this <see cref="Id"/> keeps working, same
    /// as a binding that no longer matches anything (see <c>PopulateCombo</c>'s "dynamicList"
    /// fallback). Defaults true so newly discovered devices are pre-selected.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Name plus the device kind read off its icon ("Studio / Lampada · Lampadina") —
    /// shown in the setup window's list so identical-looking names can be told apart by what the
    /// device actually IS. Computed, never persisted (hence <see cref="JsonIgnoreAttribute"/>):
    /// it is derived from <see cref="IconName"/> and would go stale in the file.</summary>
    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            string kind = GoogleHomeIconCatalog.LabelFor(IconName);
            string suffix = FoyerBodyAlt.Length > 0 ? " ⇄" : "";
            return kind.Length > 0 ? $"{Name} · {kind}{suffix}" : Name + suffix;
        }
    }
}

/// <summary>
/// Host-agnostic store for the "Google Home" action's bindings (id/name/selector/page), shared by
/// every K2 device module the same way the "browser"/"profile" action payloads are — no
/// <see cref="IActionHost"/> involvement, unlike the macro library which is owned by the host app.
/// Persisted as a small JSON file, same convention as <see cref="AppSettings"/>.
/// </summary>
public static class GoogleHomeStore
{
    private sealed class Data
    {
        public List<GoogleHomeBinding> Bindings { get; set; } = new();
        /// <summary>Account-wide (not per-device) connection flag — see <see cref="IsConnected"/>.</summary>
        public bool IsConnected { get; set; }
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    private static string StorePath => Path.Combine(K2Paths.Root, "googlehome_bindings.json");

    /// <summary>Whether the Google account is currently signed in, per the last successful
    /// <see cref="ReconcileScan"/> or explicit <see cref="Disconnect"/>. Account-wide, not
    /// per-device: gates every trigger (see <see cref="GoogleHomeBridge"/>) and drives the
    /// DisplayPad's disconnected warning triangle (see <c>DisplayPadKey.IsGoogleHomeDisconnected</c>
    /// in K2.App).</summary>
    public static bool IsConnected
    {
        get { EnsureLoaded(); return _data.IsConnected; }
    }

    /// <summary>Fires whenever <see cref="IsConnected"/> changes — never on a no-op
    /// reconcile/disconnect that leaves it as-is.</summary>
    public static event Action? ConnectionChanged;

    public static IReadOnlyList<GoogleHomeBinding> List()
    {
        EnsureLoaded();
        return _data.Bindings;
    }

    public static GoogleHomeBinding? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLoaded();
        return _data.Bindings.FirstOrDefault(b => b.Id == id);
    }

    public static GoogleHomeBinding Add(string name, string cardText, string controlLabel, string pagePath, string iconName = "")
    {
        EnsureLoaded();
        var binding = new GoogleHomeBinding { Name = name, CardText = cardText, ControlLabel = controlLabel, PagePath = pagePath, IconName = iconName };
        lock (_lock)
        {
            _data.Bindings.Add(binding);
            Save();
        }
        return binding;
    }

    /// <summary>Bulk auto-import: reconciles the store against a fresh
    /// <c>window.__k2gh.scanCards()</c> result (see <see cref="GoogleHomeSetupWindow"/>'s
    /// auto-import flow, run after login/on window open and on "Force refresh"). Matches
    /// existing "dom" bindings by <see cref="GoogleHomeBinding.CardText"/> (case-insensitive,
    /// already room-qualified — see that property's doc; no stable device id exists in the
    /// page's DOM, confirmed against a real captured tile) and, for a match, refreshes
    /// <see cref="GoogleHomeBinding.ControlLabel"/>/<see cref="GoogleHomeBinding.PagePath"/>/
    /// <see cref="GoogleHomeBinding.IconName"/> in place — <see cref="GoogleHomeBinding.Id"/>,
    /// <see cref="GoogleHomeBinding.Name"/> and <see cref="GoogleHomeBinding.IsEnabled"/> are
    /// left untouched, so a rename/checkbox choice and any key already bound to it survive.
    /// A card with no matching binding is added new (pre-selected, <c>IsEnabled = true</c> —
    /// "device added" case). An existing "dom" binding whose <c>CardText</c> is absent from
    /// <paramref name="found"/> is removed outright — "device removed from Google Home" case.
    /// "foyer" bindings are never touched here: they are explicit per-action recordings, not
    /// tied 1:1 to a scanned card. Also marks the account as connected (a scan only runs
    /// against a live, signed-in home.google.com session).</summary>
    public static (int Added, int Updated, int Removed) ReconcileScan(
        IReadOnlyList<(string CardText, string ControlLabel, string PagePath, string IconName)> found)
    {
        EnsureLoaded();
        int added = 0, updated = 0, removed;
        bool wasConnected;
        lock (_lock)
        {
            var foundKeys = new HashSet<string>(found.Select(f => f.CardText), StringComparer.OrdinalIgnoreCase);
            removed = _data.Bindings.RemoveAll(b =>
                string.Equals(b.Kind, "dom", StringComparison.Ordinal) && !foundKeys.Contains(b.CardText));

            foreach (var item in found)
            {
                var existing = _data.Bindings.FirstOrDefault(b =>
                    string.Equals(b.Kind, "dom", StringComparison.Ordinal)
                    && string.Equals(b.CardText, item.CardText, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.ControlLabel = item.ControlLabel;
                    existing.PagePath = item.PagePath;
                    existing.IconName = item.IconName;
                    updated++;
                }
                else
                {
                    _data.Bindings.Add(new GoogleHomeBinding
                    {
                        Name = item.CardText,
                        CardText = item.CardText,
                        ControlLabel = item.ControlLabel,
                        PagePath = item.PagePath,
                        IconName = item.IconName,
                    });
                    added++;
                }
            }

            wasConnected = _data.IsConnected;
            _data.IsConnected = true;
            Save();
        }
        if (!wasConnected) ConnectionChanged?.Invoke();
        return (added, updated, removed);
    }

    /// <summary>Toggles whether a binding shows up in K2's action pickers — see
    /// <see cref="GoogleHomeBinding.IsEnabled"/>.</summary>
    public static void SetEnabled(string id, bool enabled)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var binding = _data.Bindings.FirstOrDefault(b => b.Id == id);
            if (binding is null || binding.IsEnabled == enabled) return;
            binding.IsEnabled = enabled;
            Save();
        }
    }

    /// <summary>"Disconnetti": marks the account as signed out account-wide. Bindings are left
    /// untouched — they stay assigned to whatever key uses them, just refuse to trigger (see
    /// <see cref="GoogleHomeBridge.TriggerAsync"/>) until a fresh login's <see cref="ReconcileScan"/>
    /// finds the same devices again and flips <see cref="IsConnected"/> back on.</summary>
    public static void Disconnect()
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_data.IsConnected) return;
            _data.IsConnected = false;
            Save();
        }
        ConnectionChanged?.Invoke();
    }

    /// <summary>Foyer mode (see <see cref="GoogleHomeFoyer"/>): stores one recorded RPC as a new
    /// binding. Unlike <see cref="ReconcileScan"/> there is no dedup key — the same
    /// device legitimately yields several distinct bindings (on, off, 40% brightness, a colour…),
    /// each its own recorded body.</summary>
    public static GoogleHomeBinding AddFoyer(string name, string url, string body, string apiKey, string authUser, string iconName = "")
    {
        EnsureLoaded();
        var binding = new GoogleHomeBinding
        {
            Name = name,
            Kind = "foyer",
            FoyerUrl = url,
            FoyerBody = body,
            FoyerApiKey = apiKey,
            FoyerAuthUser = authUser,
            IconName = iconName,
        };
        lock (_lock)
        {
            _data.Bindings.Add(binding);
            Save();
        }
        return binding;
    }

    /// <summary>Attaches the opposite action to an existing Foyer binding, turning it into an
    /// alternating one — see <see cref="GoogleHomeBinding.FoyerBodyAlt"/>.</summary>
    public static void SetFoyerAlt(string id, string body)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var binding = _data.Bindings.FirstOrDefault(b => b.Id == id);
            if (binding is null) return;
            binding.FoyerBodyAlt = body;
            binding.AltNext = false;
            Save();
        }
    }

    /// <summary>Records which body the next press of an alternating binding should send.</summary>
    public static void SetAltNext(string id, bool altNext)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var binding = _data.Bindings.FirstOrDefault(b => b.Id == id);
            if (binding is null || binding.AltNext == altNext) return;
            binding.AltNext = altNext;
            Save();
        }
    }

    public static void Rename(string id, string name)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var binding = _data.Bindings.FirstOrDefault(b => b.Id == id);
            if (binding is null || binding.Name == name) return;
            binding.Name = name;
            Save();
        }
    }

    public static void Remove(string id)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.Bindings.RemoveAll(b => b.Id == id) > 0)
                Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }
    }

    private static void Load()
    {
        try
        {
            string path = StorePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Data>(json);
                if (data is not null) _data = data;
            }
        }
        catch
        {
            // Corrupt/missing store file: fall back to an empty list.
            _data = new Data();
        }
    }

    private static void Save()
    {
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort persistence; a failed write just means the binding
            // won't survive a restart, which is not worth crashing over.
        }
    }
}
