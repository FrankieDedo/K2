using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: the DisplayPad's <b>"Dedicated profiles"</b> section — the list that
/// appears under the normal profile list once the device has one.
///
/// <para>
/// A dedicated profile is not a profile you edit key by key: it is an ALTERNATIVE panel that
/// takes the pad over on its own trigger and hands it back to the normal profile afterwards,
/// the way a screensaver does. Two exist today:
/// <list type="bullet">
/// <item><b>Spotify</b> — a reserved slot whose 2×2 cover block (left by default, configurable
/// to center/right) is painted live by
/// <c>SpotifyCoverService</c>;</item>
/// <item><b>Discord</b> — the live voice page, which takes the panel over while the user is in a
/// voice call (see <c>MainWindow.DisplayPad.DiscordRoom.cs</c>). Its slot holds no keys at all:
/// the page is transient and painted from the call itself, so the slot exists purely to say
/// "THIS pad has the Discord profile" — which is also why it never becomes the current profile.
/// </item>
/// </list>
/// </para>
///
/// <para>
/// Both are created the same way as any other profile — "+ New profile" ▸ Dedicated — and both
/// are <b>per device</b>: the reserved slot lives in that pad's own profile table, so a dedicated
/// profile created on one DisplayPad never arms itself on another. Being reserved slots, they are
/// filtered out of the normal profile list (<c>DpRefreshProfiles</c>) and shown only here; the
/// section stays collapsed while the device has none.
/// </para>
///
/// <para>
/// Selecting a row ARMS/opens that takeover; selecting anything in the profile list above leaves
/// it and gives the panel back — temporarily for Discord (the page returns on the next call, or
/// right away from a <c>discord ▸ voice page</c> key), permanently for Spotify, which is a plain
/// profile switch. The gear opens that profile's own configuration (for Discord: the account it
/// connects with) and can delete it.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Ids of the dedicated profile types, as offered by
    /// <see cref="NewDisplayPadProfileDialog"/>, with the reserved profile name each one takes on
    /// the device and the loc key of its row label. The reserved NAME is the marker: a device
    /// "has" a dedicated profile exactly when one of its slots carries that name.</summary>
    private static readonly (string Id, string ReservedName, string LocKey)[] DpDedicatedCatalog =
    {
        ("Spotify", SpotifyProfileName, "dedicated_spotify"),
        ("Discord", DiscordProfileName, "dedicated_discord"),
        (StudioDedicatedId, StudioProfileName, "dedicated_studio"),
    };

    /// <summary>Fender Studio Pro's dedicated profile: an ordinary page of <c>dp_studio</c> keys
    /// (see <see cref="ModLinkGames.StudioPro"/>) in a reserved slot, which takes the pad while
    /// the program runs and hands it back when it closes (see <c>DpSyncLaunchRegistrations</c>'s
    /// synthesized registration for this slot).</summary>
    private const string StudioDedicatedId = "Studio Pro";
    private const string StudioProfileName = "Fender Studio Pro";
    private const string StudioExeName = "Studio Pro.exe";

    /// <summary>The keys a new Studio Pro profile starts with, in pad order.</summary>
    private static readonly string[] StudioSeedKeys =
    {
        "sp.record", "sp.play", "sp.stop", "sp.rewind", "sp.forward", "sp.loop",
        "sp.click", "sp.marker", "sp.undo", "sp.redo", "sp.save", "sp.anysolo",
    };

    /// <summary>Reserved profile name of the Discord dedicated profile — see
    /// <see cref="SpotifyProfileName"/>, same role.</summary>
    private const string DiscordProfileName = "Discord";

    /// <summary>Guards the same re-entrancy <c>_dpSuppressProfile</c> guards for the profile
    /// list: setting <c>SelectedItem</c> from code must not run the user-intent handler.</summary>
    private bool _dpSuppressDedicated;

    /// <summary>True when <paramref name="name"/> is a reserved dedicated-profile name, i.e. that
    /// slot belongs in this section and NOT in the normal profile list.</summary>
    private static bool DpIsDedicatedName(string? name) =>
        name is not null && Array.Exists(DpDedicatedCatalog, d => d.ReservedName == name);

    /// <summary>Whether <paramref name="deviceId"/> has the dedicated profile <paramref name="id"/>
    /// ("Spotify"/"Discord"). This is the per-device condition every takeover is gated on.</summary>
    private bool DpHasDedicated(int deviceId, string id)
    {
        int i = Array.FindIndex(DpDedicatedCatalog, d => d.Id == id);
        if (i < 0) return false;
        string reserved = DpDedicatedCatalog[i].ReservedName;
        return _dpStore.GetExistingProfiles(deviceId)
            .Any(slot => _dpStore.GetProfileName(deviceId, slot) == reserved);
    }

    // ================================================================
    // The list
    // ================================================================

    /// <summary>Rebuilds the section from the device's reserved slots and collapses it when there
    /// are none. Called by <c>DpRefreshProfiles</c>, which walks the same slot list.</summary>
    private void DpRefreshDedicated(int deviceId)
    {
        // The Studio Pro back key is pressed inside a static service: hand it the way out from
        // here, the first place that runs once a pad and its store are up.
        DpLiveTileService.StudioBackRequested ??= id => Dispatcher.BeginInvoke(() => DpStudioLeave(id));

        _dpSuppressDedicated = true;
        try
        {
            var names = _dpStore.GetExistingProfiles(deviceId)
                .Select(slot => (Slot: slot, Name: _dpStore.GetProfileName(deviceId, slot)))
                .Where(x => DpIsDedicatedName(x.Name))
                .ToList();

            var items = new List<DpDedicatedItem>();
            foreach (var (id, reserved, locKey) in DpDedicatedCatalog)
            {
                var match = names.FirstOrDefault(x => x.Name == reserved);
                if (match.Name is null) continue;
                items.Add(new DpDedicatedItem(id, Loc.Get(locKey), match.Slot));
            }

            LstDpDedicated.ItemsSource = items;
            LstDpDedicated.SelectedItem = null;
            PnlDpDedicated.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _dpSuppressDedicated = false; }
    }

    private void LstDpDedicated_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dpSuppressDedicated) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (LstDpDedicated.SelectedItem is not DpDedicatedItem item) return;

        switch (item.Id)
        {
            case "Spotify":
                // Self-contained: switches to the reserved slot and repaints. The profile list's
                // own selection is cleared by DpRefreshProfiles, which skips reserved names.
                DpCreateOrSwitchSpotifyProfile();
                break;

            case "Discord":
                // Nothing to switch to — the panel is taken over by the call, not by the slot.
                DvpReopen(id);
                break;

            case StudioDedicatedId:
                DpStudioEnter(id, item.Slot);
                break;
        }
    }

    /// <summary>Mirrors "what owns the panel" into the list — call with null when a normal
    /// profile is showing. Never triggers the handler above.</summary>
    private void DpSelectDedicated(string? id)
    {
        _dpSuppressDedicated = true;
        try
        {
            LstDpDedicated.SelectedItem = id is null || LstDpDedicated.ItemsSource is not List<DpDedicatedItem> items
                ? null
                : items.Find(x => x.Id == id);
        }
        finally { _dpSuppressDedicated = false; }
    }

    /// <summary>Which dedicated profile currently owns <paramref name="deviceId"/>'s panel, or
    /// null for a normal profile.</summary>
    private string? DpActiveDedicated(int deviceId)
    {
        if (DpDiscordRoomActive(deviceId)) return "Discord";
        string? current = _dpStore.GetProfileName(deviceId, _dpStore.GetCurrentProfile(deviceId));
        return current == SpotifyProfileName ? "Spotify"
             : current == StudioProfileName ? StudioDedicatedId : null;
    }

    /// <summary>The user picked a normal profile: the panel goes back to it. For Discord that is
    /// a TEMPORARY exit (the page returns on the next call), which is the whole point of keeping
    /// the two lists side by side.</summary>
    private void DpLeaveDedicatedForProfile(int deviceId)
    {
        if (DpDiscordRoomActive(deviceId)) DvpDismiss(deviceId);
        DpSelectDedicated(null);
    }

    // ================================================================
    // Create / configure / delete
    // ================================================================

    /// <summary>Creates the dedicated profile picked in the "+ New profile" dialog on the selected
    /// device (or just switches to it when it is already there).</summary>
    internal void DpCreateDedicatedProfile(string type)
    {
        if (DpSelectedDeviceId() is not int id) return;

        switch (type)
        {
            case "Spotify":
                DpCreateOrSwitchSpotifyProfile();
                return;

            case "Discord":
            {
                if (!DpHasDedicated(id, "Discord"))
                {
                    var existing = _dpStore.GetExistingProfiles(id);
                    int slot = BaseCampDbImporter.FindFreeSlot(existing, maxSlots: 999);
                    _dpStore.ClearProfile(id, slot);
                    _dpStore.SetProfileName(id, slot, DiscordProfileName);
                    // Materializes the slot so it counts as "existing" without seeding any key:
                    // the voice page paints all 12 itself.
                    _dpStore.SaveButton(id, slot, 0, null, null, null);
                    DpLog($"[UI] Discord dedicated profile created: slot {slot} (device {id})");
                }
                DpRefreshProfiles(id);
                // The current profile deliberately stays where it was: the Discord page shows up
                // when a call starts, and the pad must keep working normally until then.
                DvpReopen(id);
                // No account connected yet — send the user straight to the account window (the
                // config popup only links to it, one extra click that first-run doesn't need).
                if (!DiscordStore.IsConnected) new DiscordSettingsWindow { Owner = this }.ShowDialog();
                return;
            }

            case StudioDedicatedId:
            {
                int slot = _dpStore.GetExistingProfiles(id)
                    .FirstOrDefault(s => _dpStore.GetProfileName(id, s) == StudioProfileName);
                if (slot == 0)
                {
                    slot = BaseCampDbImporter.FindFreeSlot(_dpStore.GetExistingProfiles(id), maxSlots: 999);
                    _dpStore.ClearProfile(id, slot);
                    _dpStore.SetProfileName(id, slot, StudioProfileName);
                    for (int btn = 0; btn < StudioSeedKeys.Length; btn++)
                        DpSeedSpotifyKey(id, slot, btn, ModLinkGames.StudioActionType, StudioSeedKeys[btn]);
                    DpLog($"[UI] Studio Pro dedicated profile created: slot {slot} (device {id})");
                }
                DpStudioApplyBackKey(id);
                DpRefreshProfiles(id);
                DpStudioEnter(id, slot);
                return;
            }
        }
    }

    /// <summary>Gear popup of a dedicated row: its own configuration panel, or delete. Reached
    /// from the shared <c>ProfileGear_PreviewMouseDown</c>, which type-switches on the row.
    /// <paramref name="target"/> (the gear button itself) is required as the menu's
    /// PlacementTarget — a loose ContextMenu with IsOpen set directly and no PlacementTarget
    /// has nothing to anchor to and never actually renders.</summary>
    private void DpShowDedicatedGear(DpDedicatedItem item, UIElement target)
    {
        if (DpSelectedDeviceId() is not int id) return;

        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        var configure = new MenuItem { Header = Loc.Get("configure_profile") };
        configure.Click += (_, _) => DpShowDedicatedConfig(item.Id);
        menu.Items.Add(configure);
        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = Loc.Get("delete_profile") };
        delete.Click += (_, _) => DpDeleteDedicated(id, item);
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    /// <summary>The dedicated profile's own configuration. Discord opens a small popup with the
    /// voice-page knobs (webcam shortcut, screensaver-style return timer) and a button through to
    /// the account window; Spotify opens its own popup (data source, cover layout, text mode —
    /// see <see cref="SpotifyProfileConfigWindow"/>).</summary>
    private void DpShowDedicatedConfig(string id)
    {
        switch (id)
        {
            case "Discord":
                new DiscordProfileConfigWindow { Owner = this }.ShowDialog();
                break;
            case "Spotify":
                DpShowSpotifyProfileConfig();
                break;
            case StudioDedicatedId:
                DpShowStudioConfig(this);
                break;
            default:
                MessageBox.Show(Loc.Get("dedicated_no_config"), Loc.Get("dedicated_configure"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    /// <summary>Opens the Spotify dedicated profile's configuration for the selected pad and,
    /// on save, persists it and re-arms the cover overlay + repaints so the change shows at
    /// once.</summary>
    private void DpShowSpotifyProfileConfig()
    {
        if (DpSelectedDeviceId() is not int id) return;

        var oldCfg = DpReadSpotifyCoverConfig(id);
        var dlg = new SpotifyProfileConfigWindow(oldCfg) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Saved) return;

        DpWriteSpotifyCoverConfig(id, dlg.Result);
        DpLog($"[UI] Spotify profile config saved (device {id}): {dlg.Result.SourceToken}/{dlg.Result.LayoutToken}/{dlg.Result.TextModeToken}/{dlg.Result.PositionToken}");

        // Reseed the 8 control keys whenever Source, Position or the target Device changed —
        // user request 2026-09-01 ("quando seleziono web api o local account, aggiorna le azioni
        // dei pulsanti", extended to Position/Device since both also change what gets written
        // into the same physical keys — see DpReseedSpotifyControlButtons, whose `repositioned`
        // flag additionally blanks the new block keys' stale action/image on a Position change).
        bool sourceChanged = dlg.Result.Source != oldCfg.Source;
        bool positionChanged = dlg.Result.Position != oldCfg.Position;
        bool deviceChanged = dlg.Result.Device != oldCfg.Device;
        int slot = DpSpotifySlot(id);
        if ((sourceChanged || positionChanged || deviceChanged) && slot != 0)
            DpReseedSpotifyControlButtons(id, slot, dlg.Result.Source, dlg.Result.Position, positionChanged, dlg.Result.Device);

        DpSyncSpotifyCoverService(id);
        // Picks up (or drops) the focus-only watcher registration for the new flag, and re-reads
        // the block/control keys just reseeded above.
        DpRefreshProfiles(id);
        DpRequestRepaint(id);
    }

    // ================================================================
    // Fender Studio Pro
    // ================================================================

    /// <summary>Where each pad was before the Studio Pro profile took it — what its back key
    /// returns to.</summary>
    private readonly Dictionary<int, int> _dpStudioPrevProfile = new();

    private StudioProfileConfig DpReadStudioConfig() => new(
        _dpStore.GetSetting("studio.returnOn") == "1",
        int.TryParse(_dpStore.GetSetting("studio.returnSec"), out int s) && s > 0
            ? s : StudioProfileConfig.DefaultReturnSeconds,
        _dpStore.GetSetting("studio.fgOnly") == "1",
        _dpStore.GetSetting("studio.backArrow") != "0");      // absent = shown

    /// <summary>The Studio Pro profile's configuration popup. Reached from the dedicated row's
    /// gear and from a <c>dp_studio</c> key's own dialog (<see cref="IActionHost.OpenStudioConfig"/>).</summary>
    internal void DpShowStudioConfig(Window owner)
    {
        var dlg = new StudioProfileConfigWindow(DpReadStudioConfig()) { Owner = owner };
        if (dlg.ShowDialog() != true) return;

        var cfg = dlg.Result;
        _dpStore.SetSetting("studio.returnOn", cfg.ReturnEnabled ? "1" : "0");
        _dpStore.SetSetting("studio.returnSec", cfg.ReturnSeconds.ToString());
        _dpStore.SetSetting("studio.fgOnly", cfg.ForegroundOnly ? "1" : "0");
        _dpStore.SetSetting("studio.backArrow", cfg.BackArrow ? "1" : "0");

        // Picks up the new foreground / return settings in the launch watcher, and the back key.
        // DpRefreshProfiles rebuilds the profile LIST of the tab, so it is only for the pad the tab
        // is showing: calling it for every pad left the list on the last one's profiles (user
        // report 2026-10-06). The others get their watcher registration refreshed directly.
        int? selected = DpSelectedDeviceId();
        foreach (int id in _dpDeviceIds.ToList())
        {
            DpStudioApplyBackKey(id);
            if (id == selected) DpRefreshProfiles(id);
            else DpRegisterProfileLaunchWatchers(id, _dpStore.GetExistingProfiles(id));
            DpRequestRepaint(id);
        }
    }

    /// <summary>Pad position of the back key: key 7, the first of the bottom row.</summary>
    private const int StudioBackButton = 6;

    /// <summary>Puts the back key on key 7 while "Show back arrow" is on, and gives that key its
    /// stock function back when it is off. Turning it on overwrites whatever key 7 held — that is
    /// what the option asks for; turning it off only touches the key if it still is the back key.</summary>
    private void DpStudioApplyBackKey(int id)
    {
        int slot = _dpStore.GetExistingProfiles(id)
            .FirstOrDefault(s => _dpStore.GetProfileName(id, s) == StudioProfileName);
        if (slot == 0) return;

        // A profile made before the back key moved to key 7 still has it on key 12.
        const int legacyButton = 11;
        var legacy = _dpStore.LoadPage(id, slot, 0).FirstOrDefault(b => b.ButtonIndex == legacyButton);
        if (legacy?.ActionType == ModLinkGames.StudioActionType && legacy.ActionValue == "sp.back")
            DpSeedSpotifyKey(id, slot, legacyButton, ModLinkGames.StudioActionType, StudioSeedKeys[legacyButton]);

        var key = _dpStore.LoadPage(id, slot, 0).FirstOrDefault(b => b.ButtonIndex == StudioBackButton);
        bool isBack = key?.ActionType == ModLinkGames.StudioActionType && key.ActionValue == "sp.back";
        bool want = DpReadStudioConfig().BackArrow;
        if (want == isBack) return;

        DpSeedSpotifyKey(id, slot, StudioBackButton, ModLinkGames.StudioActionType,
                         want ? "sp.back" : StudioSeedKeys[StudioBackButton]);
        DpLog($"[STUDIO] device {id}: key 7 -> {(want ? "back" : StudioSeedKeys[StudioBackButton])}");
    }

    /// <summary>Switches <paramref name="id"/> to its Studio Pro slot, remembering where it came
    /// from. Every way in goes through here — the row, the watcher, a "switch profile" key.</summary>
    private void DpStudioEnter(int id, int slot)
    {
        DpStudioCancelReturnTimer(id);
        int leaving = _dpStore.GetCurrentProfile(id);
        if (leaving != slot) _dpStudioPrevProfile[id] = leaving;
        DpSpotifySwitchTo(id, slot);
    }

    /// <summary>Pending "return to this profile after N s" countdowns, one per pad.</summary>
    private readonly Dictionary<int, System.Windows.Threading.DispatcherTimer> _dpStudioReturnTimers = new();

    private void DpStudioCancelReturnTimer(int id)
    {
        if (!_dpStudioReturnTimers.Remove(id, out var t)) return;
        t.Stop();
    }

    /// <summary>Starts the countdown that brings the Studio Pro profile back after the back key.
    /// A timer of our own, like Spotify's, and NOT the launch watcher's re-assert: that one only
    /// counts while the program is running and not in foreground-only mode, so the profile
    /// never came back when it had been opened by hand (user report 2026-10-06).</summary>
    private void DpStudioArmReturnTimer(int id)
    {
        DpStudioCancelReturnTimer(id);
        var cfg = DpReadStudioConfig();
        if (!cfg.ReturnEnabled) return;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, cfg.ReturnSeconds)),
        };
        timer.Tick += (_, _) =>
        {
            DpStudioCancelReturnTimer(id);
            int slot = _dpStore.GetExistingProfiles(id)
                .FirstOrDefault(s => _dpStore.GetProfileName(id, s) == StudioProfileName);
            if (slot == 0 || _dpStore.GetCurrentProfile(id) == slot) return;
            DpLog($"[STUDIO] device {id}: return timer -> profile {slot}");
            DpStudioEnter(id, slot);
        };
        _dpStudioReturnTimers[id] = timer;
        timer.Start();
        DpLog($"[STUDIO] device {id}: back in {cfg.ReturnSeconds}s");
    }

    /// <summary>The back key: hands the pad to the profile it was on, and starts the return
    /// countdown when the profile is set to come back.</summary>
    internal void DpStudioLeave(int id)
    {
        var ordinary = DpOrdinaryProfiles(id);
        if (ordinary.Count == 0) { DpLog($"[STUDIO] device {id}: nothing to go back to"); return; }
        int target = _dpStudioPrevProfile.TryGetValue(id, out int prev) && ordinary.Contains(prev)
                     ? prev : ordinary[0];
        DpSpotifySwitchTo(id, target);
        DpLog($"[STUDIO] device {id}: back key -> profile {target}");
        DpStudioArmReturnTimer(id);
    }

    private void DpDeleteDedicated(int deviceId, DpDedicatedItem item)
    {
        var res = MessageBox.Show(
            Loc.Get("delete_profile_confirm", item.Label),
            Loc.Get("delete_profile"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        // Whatever it was doing to the panel stops with it.
        if (item.Id == "Discord") DvpExit(deviceId);
        else SpotifyCoverService.Stop(deviceId);

        bool wasCurrent = _dpStore.GetCurrentProfile(deviceId) == item.Slot;
        DpDeleteProfileSlot(deviceId, item.Slot);
        DpLog($"[UI] dedicated profile \"{item.Id}\" deleted (device {deviceId}, slot {item.Slot})");

        if (wasCurrent)
        {
            int fallback = _dpStore.GetExistingProfiles(deviceId).FirstOrDefault(s => s != item.Slot);
            if (fallback > 0) _dpStore.SetCurrentProfile(deviceId, fallback);
        }
        DpRefreshProfiles(deviceId);
        DpSelectProfileSlot(_dpStore.GetCurrentProfile(deviceId));
        DpRequestRepaint(deviceId);
    }
}

/// <summary>One row of the "Dedicated profiles" list. Deliberately NOT a
/// <see cref="DpProfileItem"/>: the shared gear/context menu of a normal profile (rename, link an
/// executable, export…) makes no sense for a takeover, so the shared handlers type-switch on this
/// type and route to <c>DpShowDedicatedGear</c> instead.</summary>
/// <param name="slot">Reserved profile slot backing it on that device.</param>
public sealed class DpDedicatedItem(string id, string label, int slot)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public int Slot { get; } = slot;

    /// <summary>The shared item template shows the gear only for a "real" row (the "+ New profile"
    /// placeholder has none) — a dedicated row has one too, it just opens a different menu.</summary>
    public bool IsRealProfile => true;

    public override string ToString() => Label;
}
