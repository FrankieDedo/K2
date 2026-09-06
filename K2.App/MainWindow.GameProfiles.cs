using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// The Game profiles section — a per-game DisplayPad profile collection that sits alongside the
/// normal per-device profiles, browsed from its own top-level tab immediately left of Macro.
///
/// <para>
/// <b>The visibility gate has two halves and they are ANDed in exactly one place</b>
/// (<see cref="RefreshGameTabVisibility"/>): the master switch in Settings
/// (<see cref="AppSettings.GameProfilesEnabled"/>, on by default) AND at least one connected
/// DisplayPad. The second half is not cosmetic — a game profile has nothing to run on without a
/// pad, so with no DisplayPad the tab stays hidden even with the switch on. Turning the switch
/// off is stronger than hiding: <see cref="GameProfilesActive"/> is what any activation path must
/// consult, so a disabled feature never drives a device.
/// </para>
///
/// <para>
/// <b>Device assignment.</b> Each profile targets one DisplayPad. With a single pad connected the
/// choice is meaningless, so the config popup hides it entirely and the profile is silently bound
/// to that pad (<see cref="ResolveTargetDevice"/>) — the user never sees a one-item picker. With
/// several pads the popup shows the list. The stored target survives a pad being unplugged: it is
/// only re-resolved when the saved id is no longer among the connected devices.
/// </para>
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<GameProfileItem> _gameProfiles = new();
    private bool _gameTabActive;

    /// <summary>True when game profiles may actually drive a device: the master switch is on AND
    /// a DisplayPad is connected. Every activation path must gate on this, not just the tab.</summary>
    private bool GameProfilesActive =>
        AppSettings.GameProfilesEnabled && _dpDeviceIds.Count > 0;

    /// <summary>The single place the two halves of the gate meet. Called on startup, whenever the
    /// DisplayPad set changes (<c>DpRefreshDevices</c>) and when the master switch is toggled.</summary>
    private void RefreshGameTabVisibility()
    {
        bool show = GameProfilesActive;
        BtnGameTab.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // If the section is open when the gate closes under it (pad unplugged, or the switch
        // turned off from Settings), don't leave the user staring at a section whose tab is
        // gone — fall back to Home, the same landing page startup uses.
        if (!show && _gameTabActive) TcDevices.SelectedItem = TabHome;

        if (show) RefreshGameProfiles();
        LeaveGameProfilesOnStartup();
        RefreshGameLaunchRegistrations();
    }

    /// <summary>Once per run, moves any pad that came up ON a game profile back to an ordinary
    /// one.
    ///
    /// <para><b>Why a pad starts there at all.</b> The reserved slot is made current when the
    /// game starts and put back when it exits — but if K2 is closed (or crashes) while the game
    /// is running, that "put back" never happens and the stored current profile is still the game
    /// slot. Next launch the pad comes up showing a game page with no game behind it, which is
    /// what the user hit (report 2026-09-06).</para>
    ///
    /// <para><b>Why this does not fight the watcher.</b> A game profile must be earned at every
    /// startup, never inherited: this only clears the stale state, and
    /// <see cref="ProfileLaunchWatcher"/> then decides from scratch whether to activate — it
    /// polls for the process and applies the profile's own rules, so a game that IS running gets
    /// its page back within a poll, and one that is running without the foreground stays off
    /// until it is focused, exactly as configured.</para>
    ///
    /// <para>Runs once (<see cref="_gameStartupCleared"/>): after this, a pad sitting on a game
    /// profile is the watcher's doing and must be left alone.</para></summary>
    private void LeaveGameProfilesOnStartup()
    {
        if (_gameStartupCleared) return;
        _gameStartupCleared = true;

        foreach (int deviceId in _dpStore.GetKnownDeviceIds())
        {
            int current = _dpStore.GetCurrentProfile(deviceId);
            if (!DpIsGameProfileName(_dpStore.GetProfileName(deviceId, current))) continue;

            int fallback = _dpStore.GetExistingProfiles(deviceId)
                .FirstOrDefault(sl => sl != current &&
                                      !DpIsGameProfileName(_dpStore.GetProfileName(deviceId, sl)));

            GameSwitchTo(deviceId, fallback == 0 ? 1 : fallback);
            DpLog($"[GAME] device {deviceId}: started on a game profile (slot {current}) — " +
                  $"back to slot {(fallback == 0 ? 1 : fallback)}; the watcher decides from here");
        }

        // Pads were just moved behind the watcher's back. If it had already polled — the timer
        // runs from the first registration, which can be a call or two before this one — it may
        // have seen a pad sitting on the game profile, decided there was nothing to do and stood
        // down for the run. It has to look again.
        ProfileLaunchWatcher.Instance.Rearm("Game:");
    }

    /// <summary>See <see cref="LeaveGameProfilesOnStartup"/>: the cleanup is a startup action, not
    /// a rule, so it must not fire again when a pad is plugged in later.</summary>
    private bool _gameStartupCleared;

    /// <summary>Rebuilds the card list from the catalogue, re-resolving each profile's target
    /// DisplayPad against what is actually connected right now.
    ///
    /// <para>A profile whose game isn't installed on this machine is left OUT of the list by
    /// default: the catalogue is shared by everyone, so most of it is about games this PC doesn't
    /// have, and a card for one is a row the user can't act on. The "show all" checkbox brings
    /// them back — useful to pin an executable by hand for an install detection can't see. This
    /// is purely a VIEW filter: a hidden profile keeps its settings and, if it is enabled, still
    /// activates when its process appears.</para></summary>
    private void RefreshGameProfiles()
    {
        _gameProfiles.Clear();
        foreach (var def in GameProfileCatalog.All)
        {
            string? exe = ResolveGameExe(def);
            if (exe is null && !_gameShowAll) continue;

            int target = ResolveTargetDevice(def.Id);
            _gameProfiles.Add(new GameProfileItem
            {
                Id = def.Id,
                Name = def.Name,
                Enabled = LoadEnabled(def.Id),
                TargetDeviceId = target,
                TargetDeviceLabel = DescribeTarget(target),
                IconPath = Services.GameExeResolver.IconPngFor(exe),
                Installed = exe is not null,
            });
        }
        IcGameProfiles.ItemsSource = _gameProfiles;
        CkGameShowAll.IsChecked = _gameShowAll;
        TxtGameProfilesEmpty.Visibility =
            _gameProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether cards for games not found on this PC are shown too. Off by default —
    /// absence reads as false, the opposite of the per-profile flags above, because this one is
    /// opt-in. Read straight from the store so there is no second copy of it to keep in sync.</summary>
    private bool _gameShowAll => _dpStore.GetSetting(ShowAllKey) is "1";

    private const string ShowAllKey = "gameprofile.showall";

    private void CkGameShowAll_Click(object sender, RoutedEventArgs e)
    {
        _dpStore.SetSetting(ShowAllKey, (sender as CheckBox)?.IsChecked == true ? "1" : "0");
        RefreshGameProfiles();
    }

    /// <summary>Which DisplayPad this profile runs on. A saved target is honoured while that pad
    /// is still connected; otherwise it falls back to the first connected pad — which is also the
    /// single-pad case, where the user is never asked in the first place.</summary>
    private int ResolveTargetDevice(string profileId)
    {
        if (_dpDeviceIds.Count == 0) return -1;
        string? saved = _dpStore.GetSetting(TargetKey(profileId));
        if (int.TryParse(saved, out int id) && _dpDeviceIds.Contains(id)) return id;
        return _dpDeviceIds[0];
    }

    /// <summary>Label under a card's title: the pad's name, or nothing to say when there is only
    /// one pad (naming it would be noise the user can't act on).</summary>
    private string DescribeTarget(int deviceId)
    {
        if (deviceId < 0) return "";
        if (_dpDeviceIds.Count <= 1) return "";
        return _dpDeviceLabels.TryGetValue(deviceId, out var label) ? label : $"DisplayPad {deviceId}";
    }

    private static string EnabledKey(string profileId)    => $"gameprofile.{profileId}.enabled";
    private static string TargetKey(string profileId)     => $"gameprofile.{profileId}.device";
    private static string ReturnKey(string profileId)     => $"gameprofile.{profileId}.return";
    private static string ReturnSecKey(string profileId)  => $"gameprofile.{profileId}.returnsec";
    private static string ForegroundKey(string profileId) => $"gameprofile.{profileId}.foreground";
    private static string TileKey(string profileId, int page, int key) =>
        $"gameprofile.{profileId}.p{page}.k{key}";
    private static string ExePathKey(string profileId) => $"gameprofile.{profileId}.exepath";

    /// <summary>Where the USER pinned the game's executable, as opposed to <see cref="ExePathKey"/>
    /// which is only a cache of what auto-detection last found. Kept as two separate keys on
    /// purpose: auto-detection rewrites its own cache freely, and must never be able to clobber
    /// (or be clobbered by) a path the user chose deliberately.</summary>
    private static string ExeOverrideKey(string profileId) => $"gameprofile.{profileId}.exeuser";

    /// <summary>The executable this profile is pinned to, or null when it follows the catalogue's
    /// process name. Blank is stored on "back to auto", so it reads the same as absent.</summary>
    private string? LoadExeOverride(string profileId)
    {
        string? path = _dpStore.GetSetting(ExeOverrideKey(profileId));
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>What the launch watcher arms on. A pinned path wins over the catalogue's name —
    /// the watcher takes the file name off it itself — so a non-Steam, portable or renamed
    /// install follows the program the user actually runs.</summary>
    private string EffectiveExeTarget(GameProfileCatalog.Definition def) =>
        LoadExeOverride(def.Id) ?? def.ExeName;

    /// <summary>Where this profile's game lives on this machine, or null when it can't be found —
    /// which is also what decides whether the card is shown at all. Costs nothing on later
    /// refreshes: the path is stored and the icon PNG is cached.</summary>
    private string? ResolveGameExe(GameProfileCatalog.Definition def)
    {
        // A pinned executable is the whole answer: no detection, and no falling back to a guessed
        // install when the pinned file is missing — silently showing another program's icon would
        // hide the fact that the pin is broken.
        if (LoadExeOverride(def.Id) is { } pinned)
            return System.IO.File.Exists(pinned) ? pinned : null;

        string? remembered = _dpStore.GetSetting(ExePathKey(def.Id));
        string? exe = Services.GameExeResolver.Resolve(def.ExeName, def.SteamAppId, remembered);
        if (exe is not null && exe != remembered) _dpStore.SetSetting(ExePathKey(def.Id), exe);
        return exe;
    }

    // Every game-profile flag defaults ON, so an ABSENT setting must read as true — only an
    // explicit "0" turns one off. Reading absence as false would silently disable the feature
    // for everyone on first run.
    private bool LoadEnabled(string profileId)    => _dpStore.GetSetting(EnabledKey(profileId))    is not "0";
    private bool LoadReturn(string profileId)     => _dpStore.GetSetting(ReturnKey(profileId))     is not "0";
    private bool LoadForeground(string profileId) => _dpStore.GetSetting(ForegroundKey(profileId)) is not "0";

    private int LoadReturnSeconds(string profileId) =>
        int.TryParse(_dpStore.GetSetting(ReturnSecKey(profileId)), out int s) && s > 0
            ? s : GameProfileConfigDialog.DefaultReturnSeconds;

    // Tile overrides are stored one key per tile, "type\u001fvalue\u001fcaption". An absent
    // entry means "as shipped", so a profile the user never touched costs nothing in the store
    // and automatically picks up any change to the curated mapping.
    private const char TileSep = '\u001f';

    /// <summary>Pages this profile has RIGHT NOW: never fewer than the catalogue ships, plus any
    /// the user added in the config popup. Stored as a plain count because the tiles themselves
    /// already persist one key per slot.</summary>
    private static string PageCountKey(string profileId) => $"gameprofile.{profileId}.pages";

    private int LoadPageCount(GameProfileCatalog.Definition def) =>
        int.TryParse(_dpStore.GetSetting(PageCountKey(def.Id)), out int n)
            ? Math.Clamp(n, def.Pages.Count, 20)
            : def.Pages.Count;

    private IReadOnlyList<IReadOnlyList<GameProfileCatalog.Tile>> LoadPages(GameProfileCatalog.Definition def)
    {
        var pages = new List<IReadOnlyList<GameProfileCatalog.Tile>>();
        int pageCount = LoadPageCount(def);
        for (int p = 0; p < pageCount; p++)
        {
            // A page past the shipped ones has no curated content to fall back to: its slots start
            // blank and are filled in entirely from the stored overrides below.
            var shipped = p < def.Pages.Count ? def.Pages[p].Tiles : GameProfileCatalog.EmptyPage();
            var tiles = new List<GameProfileCatalog.Tile>();
            for (int k = 0; k < shipped.Count; k++)
            {
                string? raw = _dpStore.GetSetting(TileKey(def.Id, p, k));
                if (raw is null) { tiles.Add(shipped[k]); continue; }
                var parts = raw.Split(TileSep);
                if (parts.Length < 3) { tiles.Add(shipped[k]); continue; }
                // 4th field says whether the caption is a loc key; rows written before it existed
                // are user edits, whose wording is literal.
                bool isLocKey = parts.Length >= 4 && parts[3] == "1";
                // 5th field is the caption size in px; rows written before it existed simply have
                // no size, which is the shrink-to-fit default.
                double fontSize = parts.Length >= 5 &&
                    double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double fs)
                        ? fs : 0;
                tiles.Add(new GameProfileCatalog.Tile(parts[0], parts[1], parts[2], isLocKey, fontSize));
            }
            pages.Add(tiles);
        }
        return pages;
    }

    private void SavePages(string profileId, IReadOnlyList<IReadOnlyList<GameProfileCatalog.Tile>> pages)
    {
        _dpStore.SetSetting(PageCountKey(profileId), pages.Count.ToString());
        for (int p = 0; p < pages.Count; p++)
            for (int k = 0; k < pages[p].Count; k++)
            {
                var t = pages[p][k];
                _dpStore.SetSetting(TileKey(profileId, p, k),
                    $"{t.ActionType}{TileSep}{t.ActionValue}{TileSep}{t.Caption}" +
                    $"{TileSep}{(t.CaptionIsLocKey ? "1" : "0")}" +
                    $"{TileSep}{t.FontSize.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
    }

    private void ClearPageOverrides(GameProfileCatalog.Definition def)
    {
        // Clears the pages the user ADDED too, not just the shipped ones — otherwise "restore
        // default keys" would leave orphan pages behind that the catalogue knows nothing about.
        int pageCount = LoadPageCount(def);
        for (int p = 0; p < pageCount; p++)
            for (int k = 0; k < 12; k++)
                _dpStore.SetSetting(TileKey(def.Id, p, k), "");   // empty => treated as shipped
        _dpStore.SetSetting(PageCountKey(def.Id), def.Pages.Count.ToString());
    }

    // ─────────────────────────── Navigation ───────────────────────────

    /// <summary>Game profiles tab: a top-level section, same deselect-the-device-tabs pattern as
    /// <c>BtnMacroTab_Click</c>.</summary>
    private void BtnGameTab_Click(object sender, RoutedEventArgs e)
    {
        PnlGameProfiles.Visibility = Visibility.Visible;
        PnlMacro.Visibility      = Visibility.Collapsed;
        PnlSettings.Visibility   = Visibility.Collapsed;
        PnlHome.Visibility       = Visibility.Collapsed;
        PnlEverest.Visibility    = Visibility.Collapsed;
        PnlEverest60.Visibility  = Visibility.Collapsed;
        PnlMakalu.Visibility     = Visibility.Collapsed;
        PnlMacroPad.Visibility   = Visibility.Collapsed;
        PnlDisplayPad.Visibility = Visibility.Collapsed;

        BrEverest.Visibility    = Visibility.Collapsed;
        BrMacroPad.Visibility   = Visibility.Collapsed;
        BrDisplayPad.Visibility = Visibility.Collapsed;
        BrEverest60.Visibility  = Visibility.Collapsed;
        BrMakalu.Visibility     = Visibility.Collapsed;

        TcDevices.SelectedIndex = -1;
        StopEvAccessoryPoll();
        SetSettingsTabActive(false);
        SetMacroTabActive(false);
        SetGameTabActive(true);
        RefreshGameProfiles();
    }

    private void SetGameTabActive(bool active)
    {
        _gameTabActive = active;
        BtnGameTab.Tag = active ? "active" : null;
    }

    // ─────────────────────────── Handlers ───────────────────────────

    /// <summary>Settings &gt; Game profiles master switch.</summary>
    private void CkGameProfilesEnabled_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetGameProfilesEnabled(CkGameProfilesEnabled.IsChecked == true);
        RefreshGameTabVisibility();
    }

    private void CkGameProfileEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id }) return;
        var item = _gameProfiles.FirstOrDefault(p => p.Id == id);
        if (item is null) return;
        _dpStore.SetSetting(EnabledKey(id), item.Enabled ? "1" : "0");
        if (item.Enabled) EnableGameApi(id);
        RefreshGameLaunchRegistrations();
    }

    /// <summary>Switches on whatever the game needs to answer K2, for the profiles whose tiles
    /// read a live state through the game's own API. Today that is Zero Company alone: its
    /// remote-control server is present in the retail build but off until a line of config asks
    /// for it, so enabling the profile writes that line into the game's OWN user config (never
    /// into the install) and it takes effect at the game's next launch. Deleting that file undoes
    /// it; nothing here depends on it existing, so a profile whose config could not be written
    /// still works as a plain page with "unknown" tiles.</summary>
    private void EnableGameApi(string profileId)
    {
        if (profileId != GameProfileSpecs.ZeroCompanyId) return;
        Services.ZeroCompanyClient.EnsureRemoteControlEnabled(App.WriteLog);
    }

    private void BtnGameProfileConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) ShowGameProfileConfig(id);
    }

    /// <summary>Opens a game profile's settings popup. Reached from the card's gear in the Game
    /// section and from the gear on the pad's own game-profile row, which is the same profile seen
    /// from the device it runs on.</summary>
    private void ShowGameProfileConfig(string id)
    {
        // The card list is only built while the Game section is reachable, and the pad's row can
        // ask for a profile that isn't in it — so the row is fetched, and made on the spot from
        // the stored settings when it is missing.
        var item = _gameProfiles.FirstOrDefault(p => p.Id == id) ?? MakeGameProfileItem(id);
        if (item is null) return;

        // The device picker is offered ONLY when there is a real choice to make — see the class
        // remarks. With one pad the dialog simply doesn't show that row.
        var devices = _dpDeviceIds
            .Select(devId => new DpDeviceItem(devId,
                _dpDeviceLabels.TryGetValue(devId, out var l) ? l : $"DisplayPad {devId}"))
            .ToList();

        var def = GameProfileCatalog.ById(id);
        if (def is null) return;

        // The hint under the path box needs what AUTO would find, which is not necessarily what
        // the card is showing: with a pin set, the card's icon comes from the pinned file.
        string? autoExe = Services.GameExeResolver.Resolve(
            def.ExeName, def.SteamAppId, _dpStore.GetSetting(ExePathKey(id)));

        var dlg = new GameProfileConfigDialog(
            def.Id, item.Name, item.Enabled, item.TargetDeviceId, devices,
            LoadReturn(id), LoadReturnSeconds(id), LoadForeground(id),
            LoadPages(def), item.IconPath,
            LoadExeOverride(id), autoExe, def.ExeName)
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true) return;

        // Reset: drop the per-tile overrides AND re-materialise the slot straight away. Without
        // the refresh the pad kept whatever it was last given — the reset looked like it had
        // done nothing until the next restart or profile toggle.
        if (dlg.ResetRequested)
        {
            ClearPageOverrides(def);
            RefreshGameLaunchRegistrations();
            int resetDevice = ResolveTargetDevice(def.Id);
            if (resetDevice >= 0) DpRequestRepaint(resetDevice);
            return;
        }

        item.Enabled = dlg.ResultEnabled;
        _dpStore.SetSetting(EnabledKey(id), item.Enabled ? "1" : "0");
        if (item.Enabled) EnableGameApi(id);
        _dpStore.SetSetting(ReturnKey(id), dlg.ResultReturnEnabled ? "1" : "0");
        _dpStore.SetSetting(ReturnSecKey(id), dlg.ResultReturnSeconds.ToString());
        _dpStore.SetSetting(ForegroundKey(id), dlg.ResultForegroundOnly ? "1" : "0");
        SavePages(id, dlg.ResultPages);

        // Changing which executable the profile follows changes both the icon and what the
        // watcher arms on, so the whole card list is rebuilt rather than patched in place.
        string? previousExe = LoadExeOverride(id);
        _dpStore.SetSetting(ExeOverrideKey(id), dlg.ResultExePath ?? "");
        bool exeChanged = !string.Equals(previousExe, dlg.ResultExePath, StringComparison.OrdinalIgnoreCase);

        if (dlg.ResultDeviceId >= 0)
        {
            item.TargetDeviceId = dlg.ResultDeviceId;
            item.TargetDeviceLabel = DescribeTarget(dlg.ResultDeviceId);
            _dpStore.SetSetting(TargetKey(id), dlg.ResultDeviceId.ToString());
        }
        if (exeChanged) RefreshGameProfiles();
        RefreshGameLaunchRegistrations();

        // The call above already rewrote the reserved slot's twelve keys (EnsureGameSlot), but the
        // pad goes on showing the pictures it was last SENT: without an explicit repaint the new
        // icons only appeared at the next profile switch. That switch is what covers the
        // foreground-gated case — the profile is re-applied when the game takes focus — so the
        // repaint is only needed when the gate is off and the pad may be sitting on the game
        // profile right now with nothing about to move it. Same two steps the reset branch above
        // performs, for the same reason.
        if (!dlg.ResultForegroundOnly)
        {
            int saveDevice = ResolveTargetDevice(def.Id);
            if (saveDevice >= 0)
            {
                DpRequestRepaint(saveDevice);
                DpLog($"[GAME] device {saveDevice}: \"{def.Name}\" repainted after save (foreground gate off)");
            }
        }
    }

    /// <summary>The card row for a profile, built from the store — for the paths that need one
    /// without the Game section having been opened. Null for an id the catalogue doesn't
    /// know.</summary>
    private GameProfileItem? MakeGameProfileItem(string id)
    {
        var def = GameProfileCatalog.ById(id);
        if (def is null) return null;
        string? exe = ResolveGameExe(def);
        int target = ResolveTargetDevice(id);
        return new GameProfileItem
        {
            Id = def.Id,
            Name = def.Name,
            Enabled = LoadEnabled(id),
            TargetDeviceId = target,
            TargetDeviceLabel = DescribeTarget(target),
            IconPath = Services.GameExeResolver.IconPngFor(exe),
            Installed = exe is not null,
        };
    }

    // ──────────────── Materialisation on the pad + launch trigger ────────────────

    /// <summary>Prefix of the profile name a game profile reserves on its DisplayPad. Same trick
    /// the Spotify/Discord dedicated profiles use (<c>MainWindow.DisplayPad.Dedicated.cs</c>): the
    /// NAME is the marker, so a device "has" a game profile exactly when one of its slots carries
    /// this name — no extra table, and the keys are ordinary keys.</summary>
    private const string GameProfileNamePrefix = "Game: ";

    private static string GameSlotName(GameProfileCatalog.Definition def) =>
        GameProfileNamePrefix + def.Name;

    /// <summary>True for a reserved game-profile slot, i.e. one that belongs in the Game profiles
    /// tab and NOT in the device's normal profile list.</summary>
    private static bool DpIsGameProfileName(string? name) =>
        name is not null && name.StartsWith(GameProfileNamePrefix, StringComparison.Ordinal);

    // ──────────────── The pad's own "Game profiles" list ────────────────

    /// <summary>One row of the DisplayPad panel's game-profile list: the reserved slot, the game's
    /// name without the <c>"Game: "</c> marker, and the catalogue id its gear opens.</summary>
    private sealed record DpGameSlotItem(int Slot, string Label, string ProfileId)
    {
        /// <summary>Shows the shared row template's gear (see <c>K2ProfileItemTemplate</c>). It
        /// does not open the profile context menu that an ordinary row's gear does — a game
        /// profile is not renamed or deleted from here — but the game's own settings.</summary>
        public bool IsRealProfile => true;

        public override string ToString() => Label;
    }

    /// <summary>Guards the same re-entrancy the profile list does: selecting a row from code must
    /// not run the user-intent handler.</summary>
    private bool _dpSuppressGameSlot;

    /// <summary>Rebuilds the pad's game-profile list and hides it when the device has none.
    ///
    /// <para>The reserved slot is deliberately absent from the ordinary profile list — it belongs
    /// to the Game section — but that left it with NO way of being chosen by hand: the watcher put
    /// the pad on it and, once the user moved off, nothing could move back (user report
    /// 2026-09-06: "non riesce a tornare al profilo game"). This row is that way back, exactly as
    /// the dedicated profiles have one.</para></summary>
    private void DpRefreshGameSlots(int deviceId)
    {
        _dpSuppressGameSlot = true;
        try
        {
            var items = _dpStore.GetExistingProfiles(deviceId)
                .Select(slot => (Slot: slot, Name: _dpStore.GetProfileName(deviceId, slot)))
                .Where(x => DpIsGameProfileName(x.Name))
                .Select(x => new DpGameSlotItem(
                    x.Slot,
                    x.Name![GameProfileNamePrefix.Length..],
                    // The row is built from the slot NAME, which is the game's display name, so
                    // the catalogue is what turns it back into an id for the gear.
                    GameProfileCatalog.All
                        .FirstOrDefault(d => GameSlotName(d) == x.Name)?.Id ?? ""))
                .ToList();

            LstDpGameSlots.ItemsSource = items;
            LstDpGameSlots.SelectedItem = null;
            PnlDpGameSlots.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _dpSuppressGameSlot = false; }
    }

    /// <summary>Tells the launch watcher that the user picked <paramref name="slot"/> on
    /// <paramref name="deviceId"/> themselves, so its rules stop competing with that choice — see
    /// <see cref="ProfileLaunchWatcher.NotifyManualChoice"/>. Called from every manual path: this
    /// list, the ordinary profile list and the pad's own profile keys.</summary>
    private void NotifyManualProfileChoice(int deviceId, int slot) =>
        ProfileLaunchWatcher.Instance.NotifyManualChoice("Game:", ":" + deviceId, slot.ToString());

    /// <summary>Mirrors "the panel is on a game profile" into the list — null when an ordinary
    /// profile is showing. Never triggers the handler.</summary>
    private void DpSelectGameSlot(int? slot)
    {
        _dpSuppressGameSlot = true;
        try
        {
            LstDpGameSlots.SelectedItem =
                slot is null || LstDpGameSlots.ItemsSource is not List<DpGameSlotItem> items
                    ? null
                    : items.Find(x => x.Slot == slot.Value);
        }
        finally { _dpSuppressGameSlot = false; }
    }

    /// <summary>The game slot currently owning <paramref name="deviceId"/>'s panel, or null when
    /// an ordinary (or dedicated) profile is on it.</summary>
    private int? DpActiveGameSlot(int deviceId)
    {
        int current = _dpStore.GetCurrentProfile(deviceId);
        return DpIsGameProfileName(_dpStore.GetProfileName(deviceId, current)) ? current : null;
    }

    private void LstDpGameSlots_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dpSuppressGameSlot) return;
        if (DpSelectedDeviceId() is not int id) return;
        if (LstDpGameSlots.SelectedItem is not DpGameSlotItem item) return;

        // A plain profile switch — the slot holds ordinary keys. The watcher IS told, or its
        // focus rule would take the profile away again on the next poll: the user is looking at
        // K2, so the game is by definition not in front.
        GameSwitchTo(id, item.Slot);
        NotifyManualProfileChoice(id, item.Slot);
        DpLog($"[GAME] device {id}: profile \"{item.Label}\" chosen by hand (slot {item.Slot})");
    }

    /// <summary>Finds (or creates) this profile's reserved slot on a device and writes its twelve
    /// keys into it. Re-synced on every refresh so an edit made in the config popup — or a change
    /// to the curated mapping in a future K2 build — reaches the pad without the user doing
    /// anything.
    ///
    /// <para>Only page 0 is materialised today: the multi-page model exists for the arrow /
    /// sub-menu navigation still to be designed, and writing pages the pad has no way to reach
    /// yet would just be dead rows.</para></summary>
    /// <summary>Which Elite Dangerous control backs each curated tile. The control names are the
    /// game's own, taken from its shipped <c>ControlSchemes\*.binds</c> rather than from a
    /// community list — including the two that are not guessable from the caption:
    /// <c>ToggleButtonUpInput</c> is silent running, and the FSD key is
    /// <c>HyperSuperCombination</c> (the combined jump/supercruise control).
    ///
    /// <para>Status tiles are keyed by their state token; the plain-shortcut tiles have no token,
    /// so they are keyed by their caption LOC KEY — the catalogue's stable identity for a tile,
    /// which also means a tile the user re-captioned by hand stops matching and keeps whatever
    /// they chose.</para></summary>
    private static readonly Dictionary<string, string> EliteTileBinds = new(StringComparer.Ordinal)
    {
        ["gear"]            = "LandingGearToggle",
        ["scoop"]           = "ToggleCargoScoop",
        ["lights"]          = "ShipSpotLightToggle",
        ["hardpoints"]      = "DeployHardpointToggle",
        ["flightassist"]    = "ToggleFlightAssist",
        ["flightassist_on"] = "ToggleFlightAssist",
        ["silent"]          = "ToggleButtonUpInput",
        ["supercruise"]     = "HyperSuperCombination",
        ["gp_ed_fsd"]       = "HyperSuperCombination",
        ["gp_ed_heatsink"]  = "DeployHeatSink",
        ["gp_ed_target"]    = "SelectTarget",
    };

    /// <summary>The action value this tile should carry given the game's CURRENT bindings, or null
    /// to keep the catalogue's own (which encodes the game's shipped defaults).
    ///
    /// <para>Returns null — i.e. changes nothing — in every case where we would be guessing: a
    /// non-Elite profile, a pilot who never customised their controls, a control bound to a HOTAS
    /// instead of the keyboard, a key K2 has no way to press, and a tile whose value already
    /// carries an explicit <c>"state|shortcut"</c> override, which is the pilot pinning that key
    /// by hand and outranks the game.</para></summary>
    private static string? SyncedBind(GameProfileCatalog.Definition def, GameProfileCatalog.Tile t)
    {
        if (def.Id == "deadside") return DeadsideSyncedBind(t);
        if (def.Id != "elite_dangerous") return null;

        var binds = Services.EliteBindsReader.Snapshot();
        if (binds.Count == 0) return null;

        if (t.ActionType == "dp_edstatus")
        {
            if (t.ActionValue.Contains('|')) return null;         // hand-pinned shortcut wins
            string state = t.ActionValue.Trim();
            return EliteTileBinds.TryGetValue(state, out var control) &&
                   binds.TryGetValue(control, out var shortcut)
                ? $"{state}|{shortcut}"
                : null;
        }

        if (t.ActionType == "keys" && t.CaptionIsLocKey)
        {
            return EliteTileBinds.TryGetValue(t.Caption, out var control) &&
                   binds.TryGetValue(control, out var shortcut)
                ? shortcut
                : null;
        }

        return null;
    }

    /// <summary>Which Deadside action backs each curated tile. The names are the game's own
    /// <c>ActionMappings</c> identifiers, taken from the <c>DefaultInput.ini</c> packaged in the
    /// game's pak, so they match the player's <c>Input.ini</c> exactly.
    ///
    /// <para>Keyed by the tile's caption LOC KEY, the catalogue's stable identity for a tile —
    /// which also means a tile the user re-captioned by hand stops matching and keeps the
    /// shortcut they chose.</para></summary>
    private static readonly Dictionary<string, string> DeadsideTileBinds = new(StringComparer.Ordinal)
    {
        ["gp_ds_inventory"] = "Inventory",
        ["gp_ds_map"]       = "Map",
        ["gp_ds_quests"]    = "Quests",
        ["gp_ds_build"]     = "BaseCraft",
        ["gp_ds_reload"]    = "Reload",
        ["gp_ds_firemode"]  = "FireMode",
        ["gp_ds_aim"]       = "ToggleAiming",
        ["gp_ds_autorun"]   = "AutoRun",
        ["gp_ds_crouch"]    = "Crouch",
        ["gp_ds_prone"]     = "Prone",
        ["gp_ds_motions"]   = "Motions",
        ["gp_ds_hood"]      = "HoodOnHead",
    };

    /// <summary>The shortcut a Deadside tile should carry given the player's CURRENT bindings, or
    /// null to keep the catalogue's own (the game's shipped default). Null in every case where we
    /// would be guessing: a tile the user re-captioned or re-mapped by hand, a player who has
    /// never run the game, an action bound to the mouse or to a bare modifier, and any tile that
    /// isn't one of the curated twelve.</summary>
    private static string? DeadsideSyncedBind(GameProfileCatalog.Tile t)
    {
        if (t.ActionType != "keys" || !t.CaptionIsLocKey) return null;

        var binds = Services.DeadsideBindsReader.Snapshot();
        if (binds.Count == 0) return null;

        return DeadsideTileBinds.TryGetValue(t.Caption, out var action) &&
               binds.TryGetValue(action, out var shortcut)
            ? shortcut
            : null;
    }

    /// <summary>Deletes this profile's reserved slot from every pad EXCEPT the one it targets.
    ///
    /// <para><b>The bug this exists for.</b> <see cref="EnsureGameSlot"/> only ever creates, and
    /// the device it creates on is whatever <see cref="ResolveTargetDevice"/> answered at that
    /// moment — which falls back to the first connected pad while the real target is not (yet)
    /// among them, as happens on startup before every pad has enumerated. The slot made on the
    /// wrong pad then stayed there for good: the user saw their Zero Company page on pads 1 AND 2
    /// having only ever assigned it to 2, and could not get rid of it from the Game profiles tab
    /// because nothing in that tab deletes slots (report 2026-09-06).</para>
    ///
    /// <para>A pad currently SHOWING the stray slot is moved off it first — deleting the profile
    /// under it would leave the device pointing at a slot that no longer exists.</para></summary>
    private void PruneStrayGameSlots(GameProfileCatalog.Definition def, int keepDeviceId)
    {
        string reserved = GameSlotName(def);

        // Every id the store knows, not just the connected pads: a DisplayPad's id changes when
        // the set of plugged-in devices changes, so the stray slots pile up on ids that are no
        // longer in _dpDeviceIds — which is exactly why the first version of this pruning did
        // nothing for the user (report 2026-09-06: slots on devices 1 and 2 while the profile
        // targets 3).
        foreach (int deviceId in _dpStore.GetKnownDeviceIds())
        {
            if (deviceId == keepDeviceId) continue;

            foreach (int slot in _dpStore.GetExistingProfiles(deviceId))
            {
                if (_dpStore.GetProfileName(deviceId, slot) != reserved) continue;

                if (_dpStore.GetCurrentProfile(deviceId) == slot)
                {
                    int fallback = _dpStore.GetExistingProfiles(deviceId)
                        .FirstOrDefault(sl => sl != slot &&
                                              !DpIsGameProfileName(_dpStore.GetProfileName(deviceId, sl)));
                    GameSwitchTo(deviceId, fallback == 0 ? 1 : fallback);
                }

                _dpStore.DeleteProfile(deviceId, slot);
                DpLog($"[GAME] device {deviceId}: removed stray \"{def.Name}\" slot {slot} " +
                      $"(profile targets device {keepDeviceId})");
                DpRequestRepaint(deviceId);
            }
        }
    }

    private int EnsureGameSlot(int deviceId, GameProfileCatalog.Definition def)
    {
        string reserved = GameSlotName(def);
        var existing = _dpStore.GetExistingProfiles(deviceId);

        int slot = existing.FirstOrDefault(sl => _dpStore.GetProfileName(deviceId, sl) == reserved);
        if (slot == 0)
        {
            slot = existing.Count > 0 ? existing.Max() + 1 : 1;
            _dpStore.SetProfileName(deviceId, slot, reserved);
            DpLog($"[GAME] device {deviceId}: reserved slot {slot} for \"{def.Name}\"");
        }

        // Every page, not just the first: a page the user added in the config popup is written to
        // the pad under its own page id, so it is real data on the device rather than something
        // that only exists in the dialog. Reaching page 2+ still needs a navigation key on page 1
        // (an ordinary "Page" action), exactly like a hand-built profile.
        var pages = LoadPages(def);
        for (int p = 0; p < pages.Count; p++)
        {
            var tiles = pages[p];
            for (int i = 0; i < tiles.Count && i < 12; i++)
            {
                var t = tiles[i];
                // What the key PRESSES follows the game's own bindings when they can be read, so a
                // commander who rebound a control doesn't get a tile pressing the shipped default.
                string? actionValue = SyncedBind(def, t) ?? t.ActionValue;
                string? actionType = string.IsNullOrEmpty(t.ActionType) ? null : t.ActionType;

                bool eliteStyled = GameProfileCatalog.IsStyledTile(def.Id, t);
                var spec = new KeyIconSpec
                {
                    DefaultIcon = true,
                    ShowText = true,
                    Text = GameProfileCatalog.CaptionOf(t),
                    // Drives IconStyleScope.OverrideText, which is the colour every generated
                    // tile draws with — this is how the accent reaches the keys that are not
                    // Elite status tiles, so the page changes colour as one.
                    TextColor = eliteStyled
                        ? Services.GameProfileTheme.TextHexFor(def.Id, lit: true)
                        : Services.GameProfileTheme.AccentHex(def.Id),
                    // See KeyIconSpec.TextOnly. The status tiles override this baseline "lit"
                    // background with their own live on/off state
                    // (DpLiveTileService.ApplyEdStyle); everything else — the plain shortcuts
                    // and the user's own mappings — has no state of its own and stays lit.
                    TextOnly = eliteStyled,
                    BgImagePath = eliteStyled
                        ? Services.GameProfileTheme.BgImagePathFor(def.Id, lit: true)
                        : null,
                    // 0 = shrink-to-fit, i.e. exactly what every tile did before the size became
                    // editable. It rides in the stored spec, so the live repaint (which mutates
                    // that spec's colours, not its metrics) keeps it too.
                    FontSize = t.FontSize,
                };

                // The PICTURE has to be generated and stored here. Every upload path skips a
                // button whose stored image path is empty, so a key written with a null path
                // simply never reaches the panel — which is why only the dp_edstatus keys used to
                // show up: DpLiveTileService paints those itself and needs no stored picture.
                string? png = actionType is null
                    ? null
                    : Services.DpDefaultIconRenderer.Render(actionType, actionValue,
                        DpWithDefaultBg(deviceId, spec.Clone()), null, Services.DpHidNative.IconSize);

                _dpStore.SaveButton(deviceId, slot, p, i, png, actionType,
                    string.IsNullOrEmpty(actionValue) ? null : actionValue);
                _dpStore.SaveIconSpec(deviceId, slot, p, i, spec.ToJson());
            }
        }
        return slot;
    }

    /// <summary>Switches a device to a game profile's reserved slot. Cannot go through
    /// <c>DpSwitchProfile</c>: that one searches the ordinary profile list, which a reserved slot
    /// is filtered out of, so the switch would silently no-op — the exact bug the Spotify
    /// dedicated profile hit (see <c>DpSpotifySwitchTo</c>'s remarks).</summary>
    private void GameSwitchTo(int deviceId, int slot)
    {
        bool isActive = deviceId == DpSelectedDeviceId();
        _dpStore.SetCurrentProfile(deviceId, slot);
        if (isActive)
        {
            DpSelectProfileSlot(slot);
            ResetDpNavigation();
        }
        else
        {
            _dpBgPageId[deviceId] = 0;
            if (_dpBgPageHistory.TryGetValue(deviceId, out var hist)) hist.Clear();
        }
        DpRequestRepaint(deviceId);
        DeviceSyncOnProfileSwitched(SyncDeviceKind.DisplayPad, slot);
    }

    /// <summary>Arms every enabled game profile against its game's process, and drops the
    /// registrations of the ones that are off (or of the whole feature when the master switch is).
    /// Reuses <see cref="ProfileLaunchWatcher"/>, which already does the process polling, the
    /// foreground gate and the restore-on-exit that these profiles need.</summary>
    private void RefreshGameLaunchRegistrations()
    {
        const string scope = "Game:";
        var currentKeys = new List<string>();

        // The watcher lives in K2.Core and cannot see App.WriteLog; hand it the sink here, where
        // its first registration is made. "The profile didn't come up by itself" is reported as a
        // log file, and its decisions have to be in it.
        ProfileLaunchWatcher.Log ??= App.WriteLog;

        if (GameProfilesActive)
        {
            foreach (var def in GameProfileCatalog.All)
            {
                if (!LoadEnabled(def.Id))
                {
                    // Switched off: its reserved slot has no business sitting on any pad. The
                    // user's own edits live under gameprofile.{id}.* and are untouched, so
                    // turning the profile back on rebuilds the slot exactly as it was.
                    PruneStrayGameSlots(def, keepDeviceId: -1);
                    continue;
                }

                int deviceId = ResolveTargetDevice(def.Id);
                if (deviceId < 0)
                {
                    GameRegNote($"{def.Id}: enabled but no DisplayPad to put it on — not armed");
                    continue;
                }

                int slot = EnsureGameSlot(deviceId, def);
                PruneStrayGameSlots(def, keepDeviceId: deviceId);
                string key = $"{scope}{def.Id}:{deviceId}";
                currentKeys.Add(key);

                int capturedDevice = deviceId, capturedSlot = slot;
                ProfileLaunchWatcher.Instance.UpdateRegistration(
                    key, EffectiveExeTarget(def),
                    focusOnly: LoadForeground(def.Id),
                    restoreOnClose: true,
                    capturedSlot.ToString(),
                    () => _dpStore.GetCurrentProfile(capturedDevice).ToString(),
                    t => { if (int.TryParse(t, out int ts)) GameSwitchTo(capturedDevice, ts); },
                    // Armed for the whole time the game runs, not just at its launch: K2 opening
                    // with the game already up must still bring the profile in.
                    keepWhileRunning: true,
                    // "Bring this profile back after N seconds" — the profile's own setting, and
                    // the ONLY thing that overrides a profile the user picked by hand. Off = 0 =
                    // their choice stands until they change it.
                    reassertAfterSeconds: LoadReturn(def.Id) ? LoadReturnSeconds(def.Id) : 0);
            }
        }

        else GameRegNote("game profiles are off (master switch, or no DisplayPad) — none armed");

        foreach (var stale in ProfileLaunchWatcher.Instance.KeysWithPrefix(scope).Except(currentKeys))
            ProfileLaunchWatcher.Instance.RemoveRegistration(stale);

        if (currentKeys.Count > 0) StartGameAccentWatch(); else StopGameAccentWatch();
    }

    /// <summary>A one-off note about why a profile is NOT armed. This method runs on every tab
    /// switch, save and replug, so the same reason is only written once until it changes —
    /// but written it must be: an unarmed profile is exactly the case where the log otherwise
    /// says nothing at all.</summary>
    private void GameRegNote(string message)
    {
        if (_lastGameRegNote == message) return;
        _lastGameRegNote = message;
        DpLog("[GAME] " + message);
    }

    private string? _lastGameRegNote;

    // ─────────────────── Live re-colour on the ship's state ───────────────────

    private System.Windows.Threading.DispatcherTimer? _gameAccentTimer;
    private readonly Dictionary<string, int> _gameAccentLast = new();

    /// <summary>Watches for the accent changing under an ACTIVE game profile — for Elite that is
    /// the flight assist going on or off, which recolours the whole page.
    ///
    /// <para>A repaint is needed (rather than letting the tile timer handle it) because only the
    /// Elite status keys are live: the plain shortcut keys are static pictures drawn once from
    /// their stored icon spec, so their colour only changes when those specs are rewritten and
    /// the page is pushed again. That costs twelve icon uploads — ~150 ms of wire time — which is
    /// why it happens ONLY on an actual flip, not on every tick.</para></summary>
    private void StartGameAccentWatch()
    {
        if (_gameAccentTimer is not null) return;
        _gameAccentTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _gameAccentTimer.Tick += (_, _) => PollGameAccent();
        _gameAccentTimer.Start();
    }

    private void StopGameAccentWatch()
    {
        _gameAccentTimer?.Stop();
        _gameAccentTimer = null;
        _gameAccentLast.Clear();
    }

    private void PollGameAccent()
    {
        if (!GameProfilesActive) return;

        foreach (var def in GameProfileCatalog.All)
        {
            if (!LoadEnabled(def.Id)) continue;

            int accent = Services.GameProfileTheme.AccentFor(def.Id).ToArgb();
            if (_gameAccentLast.TryGetValue(def.Id, out int last) && last == accent) continue;

            // Only worth repainting the pad that is actually SHOWING this profile; for any other
            // device the new colour is picked up whenever the profile is next activated.
            //
            // NOTE the accent is recorded as "seen" only once we KNOW we repainted with it. It
            // used to be stamped before these guards, so a flip that happened while the pad was
            // on another profile was swallowed: coming back, last == accent and the page kept the
            // stale colour until the ship toggled assist twice more.
            int deviceId = ResolveTargetDevice(def.Id);
            if (deviceId < 0) continue;

            string reserved = GameSlotName(def);
            int current = _dpStore.GetCurrentProfile(deviceId);
            if (_dpStore.GetProfileName(deviceId, current) != reserved) continue;

            _gameAccentLast[def.Id] = accent;
            EnsureGameSlot(deviceId, def);        // rewrites the twelve icon specs with the accent
            DpRequestRepaint(deviceId);
            DpLog($"[GAME] device {deviceId}: \"{def.Name}\" re-coloured (accent changed)");
        }
    }
}

/// <summary>One row in the Game profiles list. Mutable + observable because the cards edit
/// Enabled in place (checkbox two-way binding) and the config popup rewrites the target label.</summary>
public sealed class GameProfileItem : INotifyPropertyChanged
{
    private bool _enabled;
    private string _targetDeviceLabel = "";

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>PNG of the game executable's icon, or null when the game isn't installed (or has
    /// never been run, for a non-Steam one). The card binds an Image to it and simply shows
    /// nothing when it is null.</summary>
    public string? IconPath { get; init; }

    /// <summary>Whether the game was actually found on this machine. False only ever reaches the
    /// list when "show all" is on — the card then says so under the title.</summary>
    public bool Installed { get; init; }
    public int TargetDeviceId { get; set; } = -1;

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnChanged(); } }
    }

    public string TargetDeviceLabel
    {
        get => _targetDeviceLabel;
        set { if (_targetDeviceLabel != value) { _targetDeviceLabel = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The set of game profiles K2 offers. Deliberately a single named list rather than something
/// scanned off disk: this is the seam the feature's data model lives behind, so wherever the
/// profiles end up coming from (shipped with K2, generated, or user-created), only this class
/// changes and neither the tab nor the config popup notices.
/// </summary>
public static class GameProfileCatalog
{
    /// <summary>One key of a game profile page. Icons are NOT shipped as images: the action
    /// itself is enough for K2's own default-icon renderer to draw the tile (and for
    /// <c>dp_edstatus</c> keys that renderer already draws the cockpit-style annunciator), so a
    /// profile stays a few lines of data instead of a folder of PNGs.</summary>
    /// <summary><paramref name="Caption"/> is a LOCALISATION KEY for every tile the catalogue
    /// ships (<paramref name="CaptionIsLocKey"/> true), so a curated profile reads in the user's
    /// language instead of whatever language it was authored in. A tile the user edited stores
    /// their literal text with the flag false — their wording is never sent through Loc.</summary>
    /// <summary><paramref name="FontSize"/> is the caption's size in pixels as picked in the key's
    /// "Edit icon" dialog, 0 = the renderer's own shrink-to-fit default. Stored per tile because
    /// a game page's label IS the tile (see <c>KeyIconSpec.TextOnly</c>): one long legend among
    /// short ones is the case the slider exists for.</summary>
    public sealed record Tile(string ActionType, string ActionValue, string Caption,
                              bool CaptionIsLocKey = true, double FontSize = 0);

    /// <summary>The text a tile actually shows.</summary>
    public static string CaptionOf(Tile t) =>
        t.CaptionIsLocKey ? Loc.Get(t.Caption) : t.Caption;

    /// <summary>Twelve tiles — one DisplayPad page. A profile can have several: the config
    /// window pages through them, and a profile that needs more than 12 keys navigates with
    /// arrow keys or sub-pages built out of these same slots.</summary>
    public sealed record Page(string Name, IReadOnlyList<Tile> Tiles);

    /// <summary><paramref name="ExeName"/> is the process the profile follows — no extension, no
    /// path, because that is what the launch watcher matches on. Verified against the real
    /// installs rather than guessed.</summary>
    /// <param name="SteamAppId">Only so the icon resolver can ask Steam where the game lives
    /// (see <c>GameExeResolver</c>); null for a game that isn't a Steam install.</param>
    public sealed record Definition(string Id, string Name, string ExeName, int? SteamAppId,
                                    IReadOnlyList<Page> Pages);

    private static Tile Keys(string k, string locKey) => new("keys", k, locKey);
    private static Tile Ed(string state, string locKey) => new("dp_edstatus", state, locKey);

    /// <summary>A slot the profile deliberately leaves unconfigured: no action type at all, so
    /// <c>EnsureGameSlot</c> stores a null action and the key stays blank instead of showing a
    /// "disabled" glyph. Keeps the cockpit tiles in the positions the commander already learned
    /// rather than reflowing the grid when a slot is dropped.</summary>
    private static Tile Empty() => new("", "", "", CaptionIsLocKey: false);

    /// <summary>Twelve blank slots — a page the user just added, before they map anything onto it.</summary>
    public static IReadOnlyList<Tile> EmptyPage() =>
        Enumerable.Range(0, 12).Select(_ => Empty()).ToList();

    /// <summary>Whether a tile gets the profile's own cockpit-frame styling (background art +
    /// caption instead of an action glyph — see <c>KeyIconSpec.TextOnly</c>).
    ///
    /// <para>The rule is per-game data (<c>GameProfileSpec.StyleAllTiles</c>), not a list of
    /// exceptions: for a game that sets it, EVERY mapped key wears the art, the ones the user
    /// added by hand included, because the page is meant to read as one panel and a key that
    /// opted out visually would look like a hole in it. An EMPTY slot is never styled — an
    /// unmapped key must stay dark rather than paint a lit frame around nothing.</para></summary>
    public static bool IsStyledTile(string profileId, Tile? tile) =>
        GameProfileSpecs.ById(profileId)?.StyleAllTiles == true &&
        !string.IsNullOrEmpty(tile?.ActionType);

    /// <summary>Elite Dangerous. The six toggles that the game reports back in its status file
    /// are <c>dp_edstatus</c> tiles — they light up with the real cockpit state AND still send
    /// the keystroke when pressed — while the rest are plain shortcuts. Binds are the game's own
    /// defaults, taken from its shipped <c>KeyboardMouseOnly.binds</c> preset.
    ///
    /// <para>The two profile arrows and the launcher were dropped on request (2026-09-05): the
    /// page is meant to read as a cockpit panel, and a pad-navigation key or a game icon sitting
    /// among the annunciators broke that. Their slots stay <see cref="Empty"/> rather than being
    /// reflowed.</para></summary>
    private static readonly Page ElitePage1 = new("Flight", new[]
    {
        Empty(),
        Ed("gear",         "gp_ed_gear"),
        Ed("scoop",        "gp_ed_scoop"),
        Ed("lights",       "gp_ed_lights"),
        Ed("hardpoints",   "gp_ed_hardpoints"),
        Empty(),
        Keys("J",          "gp_ed_fsd"),
        // "_on", not "flightassist": this key is captioned FLIGHT ASSIST, so it must be lit while
        // assist is ON. See DpLiveTileService.EdFlagMap for the two readings of the same bit.
        Ed("flightassist_on", "gp_ed_flightassist"),
        Ed("silent",       "gp_ed_silent"),
        Keys("V",          "gp_ed_heatsink"),
        Keys("T",          "gp_ed_target"),
        Empty(),
    });

    /// <summary>A Zero Company tile. It ships with NO caption on purpose: the wording — and
    /// whether it is one line or two — is decided by the painter
    /// (<c>DpLiveTileService.TileCaption</c>), which is also the only place that can put a live
    /// value in it, like the name of the soldier standing in a squad slot. A stored caption
    /// would outrank that (see <c>DpLiveTileService.Caption</c>), which is exactly what must not
    /// happen here — while the user's own wording from "Edit icon" still wins, as it should.</summary>
    private static Tile Zc(string state) => new("dp_zcstatus", state, "", CaptionIsLocKey: false);

    /// <summary>Star Wars: Zero Company — the squad, and what it can do.
    ///
    /// <para>Top row: the four soldiers. Each key is the whole operative — health dial, health
    /// number and heart on the left, action points as bars on the right — and pressing it selects
    /// them in the game for real, through the game's own API rather than a guessed keystroke.
    /// Below: the six ability slots of whoever is selected.</para>
    ///
    /// <para><b>The ability keys are known not to fire yet</b> (verified against a live mission,
    /// 2026-09-05): the game's <c>TryAndSelectAbility</c> accepts the index and does nothing
    /// while the in-game action wheel is closed, and no function that opens that wheel is exposed
    /// anywhere on the player controller. They are shipped anyway, at the user's request, because
    /// the row is where the abilities will live once there is a way to trigger them — but the
    /// honest reading of the page today is four working keys and six that light up correctly and
    /// press nothing.</para>
    ///
    /// <para>A squad smaller than four leaves the last key showing a dash: the slot is honest
    /// about having nobody in it rather than being reflowed per mission. The readings this page
    /// no longer shows — turn, round, the selected soldier's own health and AP — are all still in
    /// the action picker under the game's own card.</para></summary>
    private static readonly Page ZeroCompanyPage1 = new("Tactical", new[]
    {
        // Top row — the squad. Key 6 doubles as the UP arrow once the actions are scrolled,
        // which is why the sixth soldier sits there rather than somewhere it would be lost.
        Zc("unit1"),
        Zc("unit2"),
        Zc("unit3"),
        Zc("unit4"),
        Zc("unit5"),
        Zc("unit6"),

        // Bottom row — five actions and the DOWN arrow.
        Zc("act1"),
        Zc("act2"),
        Zc("act3"),
        Zc("act4"),
        Zc("act5"),
        Zc("scroll"),
    });

    /// <summary>Deadside. Twelve plain shortcuts — the game exposes no live state to light a tile
    /// with (no status file, no local API, and it runs under BattlEye, so nothing is read out of
    /// it), which makes this the "keyboard shortcuts only" shape the catalogue was built to
    /// allow.
    ///
    /// <para>The values here are the game's OWN shipped defaults, read out of the
    /// <c>DefaultInput.ini</c> packaged inside <c>Deadside-WindowsNoEditor.pak</c> rather than
    /// from a community list — and they are only the fallback: <see cref="MainWindow.SyncedBind"/>
    /// replaces each one with whatever the player actually bound (see
    /// <c>DeadsideBindsReader</c>).</para>
    ///
    /// <para>What is deliberately NOT here: movement and aim (they belong under the hand on the
    /// keyboard, not on a pad), sprint and walk (bound to bare modifiers, which K2's shortcut
    /// syntax cannot send on their own), and everything bound to the mouse. The picker's own
    /// families carry the rest — quick slots, vehicle, squad chat, scopes.</para></summary>
    private static readonly Page DeadsidePage1 = new("Field", new[]
    {
        Keys("Tab", "gp_ds_inventory"),
        Keys("M",   "gp_ds_map"),
        Keys("O",   "gp_ds_quests"),
        Keys("B",   "gp_ds_build"),

        Keys("R",        "gp_ds_reload"),
        Keys("X",        "gp_ds_firemode"),
        Keys("CapsLock", "gp_ds_aim"),
        Keys("V",        "gp_ds_autorun"),

        Keys("C", "gp_ds_crouch"),
        Keys("Z", "gp_ds_prone"),
        Keys("N", "gp_ds_motions"),
        Keys("H", "gp_ds_hood"),
    });

    public static IReadOnlyList<Definition> All { get; } = new List<Definition>
    {
        new("elite_dangerous", "Elite Dangerous", "EliteDangerous64", 359320, new[] { ElitePage1 }),
        new("zero_company", "Star Wars: Zero Company", "SWZeroCompany", null, new[] { ZeroCompanyPage1 }),
        // The process is the shipping binary several folders down, not the launcher at the install
        // root: BattlEye starts Deadside.exe, which starts this one and exits, so a watcher armed
        // on the launcher would fire once and then see the game "close" while it is still running.
        new("deadside", "Deadside", "Deadside-Win64-Shipping", 895400, new[] { DeadsidePage1 }),
    };

    public static Definition? ById(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
}
