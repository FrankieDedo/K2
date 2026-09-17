using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Where a game link is created and tried out: a name, an address, and a TEST that says what the
/// game is answering right now.
///
/// <para>The test is not a nicety. A link is the one part of a custom tile whose correctness cannot
/// be seen on the tile — a wrong port and a game that simply is not running look identical from the
/// pad (a dash), so without a button that says "eleven values read" the user has no way to tell
/// which of the two they are looking at.</para>
///
/// <para>The presets exist for the same reason. Every address here was verified against a real
/// installation (2026-09-13); a user who types <c>8080</c> for War Thunder instead of
/// <c>8111</c> has no way of knowing which of the two is wrong, and a list of known-good starting
/// points costs nothing.</para>
/// </summary>
public partial class GameLinkDialog : Window
{
    /// <summary>The link that was saved, once the dialog closed with OK.</summary>
    public string? SavedLinkId { get; private set; }

    private readonly string _id;

    /// <summary>What kind of link this window is editing. Comes from the caller — the reading's
    /// Source already said it — and never changes while the window is open.</summary>
    private readonly GameLinkKind _kind;

    /// <summary>The mod's page as it stands. Held here rather than read off a box, because the box
    /// only exists while the user is actually changing it.</summary>
    private string _modUrl = "";

    /// <summary>A known-good starting point: what to call it, where to fetch it, whether the game
    /// serves it by itself, and — when it does not — where the mod that does lives.</summary>
    /// <param name="HintKey">Loc key for an extra one-liner shown right under the preset picker —
    /// for the one preset where "get the mod" alone would leave out a prerequisite (K2 Unity Link
    /// needs BepInEx installed FIRST, the mod page alone does not say so loudly enough). A KEY, not
    /// resolved text: this array is built once as a static field, before there is any guarantee
    /// Loc's language is settled yet.</param>
    private sealed record Preset(string Name, string Url, GameLinkKind Kind,
                                 string ModUrl = "", bool Insecure = false, string HintKey = "");

    private static readonly Preset[] Presets =
    {
        // Native: nothing installed, the game answers as shipped.
        new("War Thunder", "http://127.0.0.1:8111/state", GameLinkKind.Native),
        new("War Thunder (indicators)", "http://127.0.0.1:8111/indicators", GameLinkKind.Native),
        new("League of Legends", "https://127.0.0.1:2999/liveclientdata/allgamedata",
            GameLinkKind.Native, Insecure: true),

        // Mods: the address only answers once these are installed in the game.
        new("Satisfactory (Ficsit Remote Monitoring)", "http://127.0.0.1:8080/getPlayer",
            GameLinkKind.Mod, "https://docs.ficsit.app/ficsitremotemonitoring/latest/index.html"),
        // No Telemachus (Kerbal Space Program) preset on purpose: that mod lives in the built-in
        // Kerbal Space Program profile, where its entries are picked by name and K2 composes the
        // request (KspTelemachus / TelemachusClient) instead of a hand-written datalink URL.
        // Not a third party: K2's own generic mod (K2/K2.UnityLink) — works on any Mono-backend
        // Unity game (IL2CPP is out of scope, see the mod's own README), reads whatever the game's
        // scripts hold rather than one hand-picked endpoint per game.
        new("Unity (mod K2 Unity Link)", "http://127.0.0.1:34873/state",
            GameLinkKind.Mod, "https://github.com/FrankieDedo/K2/tree/master/K2.UnityLink",
            HintKey: "link_preset_unitylink_hint"),
    };

    /// <param name="kind">Native or mod — decided by the Source the reading is on, so this window
    /// states it instead of asking again. It also decides which presets are worth listing: a mod's
    /// starting point offered under "the game serves it by itself" would be a contradiction.</param>
    public GameLinkDialog(string? linkId, GameLinkKind kind)
    {
        InitializeComponent();

        var existing = GameLinkStore.ById(linkId);
        _id = existing?.Id ?? GameLinkStore.NewId();
        _kind = kind;

        CbPreset.Items.Add(Loc.Get("link_preset_none"));
        foreach (var p in PresetsFor(kind)) CbPreset.Items.Add(p.Name);
        CbPreset.SelectedIndex = 0;

        Title = Loc.Get(kind == GameLinkKind.Mod ? "link_title_mod" : "link_title");
        LblKindHint.Text = Loc.Get(kind == GameLinkKind.Mod ? "link_kind_mod_hint"
                                                            : "link_kind_native_hint");
        PnlMod.Visibility = kind == GameLinkKind.Mod ? Visibility.Visible : Visibility.Collapsed;

        TxtName.Text = existing?.Name ?? "";
        TxtUrl.Text = existing?.Url ?? "";
        TxtPoll.Text = (existing?.PollMs ?? 1000).ToString(CultureInfo.InvariantCulture);
        ChkInsecure.IsChecked = existing?.AllowInvalidCertificate ?? false;
        ShowModUrl(existing?.ModUrl ?? "");
    }

    private static Preset[] PresetsFor(GameLinkKind kind) =>
        Presets.Where(p => p.Kind == kind).ToArray();

    // ───────────────────────────── the mod's page ─────────────────────────────

    /// <summary>Puts the address up as something to CLICK. An empty one says so in words instead
    /// of showing a link that goes nowhere. Display-only — the mod page comes from the Source's
    /// preset, never typed by hand, so there is nothing here to edit.</summary>
    private void ShowModUrl(string url)
    {
        _modUrl = url.Trim();
        RunModUrl.Text = _modUrl.Length > 0 ? _modUrl : Loc.Get("link_mod_none");
        LnkModUrl.IsEnabled = _modUrl.Length > 0;
    }

    private void LnkModUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!GameLinkMod.Open(_modUrl)) LblTest.Text = Loc.Get("link_mod_bad_url");
    }

    private void CbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbPreset.SelectedIndex <= 0)
        {
            LblPresetHint.Visibility = Visibility.Collapsed;
            return;
        }
        var preset = PresetsFor(_kind)[CbPreset.SelectedIndex - 1];

        // A preset fills the boxes and stops there: everything stays editable, because a game on a
        // second machine or a mod on a moved port is exactly the case a preset cannot know about.
        TxtName.Text = preset.Name;
        TxtUrl.Text = preset.Url;
        ChkInsecure.IsChecked = preset.Insecure;
        ShowModUrl(preset.ModUrl);

        LblPresetHint.Text = preset.HintKey.Length > 0 ? Loc.Get(preset.HintKey) : "";
        LblPresetHint.Visibility = preset.HintKey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        LstValues.Items.Clear();
        LblTest.Text = Loc.Get("link_testing");
        BtnTest.IsEnabled = false;
        try
        {
            var snapshot = await GameLinkReader.FetchAsync(GameLinkReader.ExplicitFor(Current()));

            if (!snapshot.Known)
            {
                LblTest.Text = Loc.Get("link_test_fail_fmt", snapshot.Error ?? "");
                return;
            }

            LblTest.Text = Loc.Get("link_test_ok_fmt", snapshot.Values.Count);
            foreach (var pair in snapshot.Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
                LstValues.Items.Add($"{pair.Key} = {pair.Value}");
        }
        finally
        {
            BtnTest.IsEnabled = true;
        }
    }

    /// <summary>The definition as the boxes currently read — what the test tries and what OK
    /// saves.</summary>
    private GameLinkDef Current() => new()
    {
        Id = _id,
        Name = TxtName.Text.Trim().Length > 0 ? TxtName.Text.Trim() : Loc.Get("link_untitled"),
        Url = TxtUrl.Text.Trim(),
        PollMs = int.TryParse(TxtPoll.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int ms)
            ? Math.Clamp(ms, GameLinkReader.MinPollMs, GameLinkReader.MaxPollMs)
            : 1000,
        AllowInvalidCertificate = ChkInsecure.IsChecked == true,
        Kind = _kind,
        ModUrl = _modUrl,
    };

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var link = Current();
        if (link.Url.Length == 0)
        {
            LblTest.Text = Loc.Get("link_need_url");
            TxtUrl.Focus();
            return;
        }

        GameLinkStore.Save(link);
        SavedLinkId = link.Id;
        DialogResult = true;
    }
}
