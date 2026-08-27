using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: Everest 60 tab shell — sidebar, device image, right
/// column, section navigation. Section CONTENT lives in two siblings toggled
/// by <see cref="ShowEv60Section"/>: <see cref="Everest60RgbPanel"/> (Lighting
/// — preset effect + side ring + per-key custom lighting, merged into one
/// section) and <c>PnlEv60Settings</c> (Keycap Appearance, cosmetic; Layout,
/// disabled pending investigation). See <see cref="Everest60HidNative"/> for
/// why lighting talks HID Feature Reports instead of the SDK. Key
/// remapping/macros: the vendor SDK (<c>Everest360_USB.dll</c>, wrapped by
/// <c>BaseCamp.Service.Helpers.Everest60</c>) turned out to expose plain-int
/// <c>ChangeKey(int,int)</c>/<c>ChangeFnKey</c>/<c>ChangeShortcutKey</c> calls
/// (not opaque structs like the lighting exports) — under active investigation
/// (2026-07-11, see CHANGELOG) to determine the exact keyId/functionCode
/// encoding before committing to an implementation.
///
/// RbEv60SecLighting.IsChecked is set in <see cref="InitEv60SectionNav"/>, NOT
/// via IsChecked="True" in XAML — see the identical note on RbMkSecRgb in
/// MainWindow.Makalu.cs: WPF fires RadioButton.Checked synchronously the
/// instant BAML sets IsChecked="True", mid-InitializeComponent(), before
/// later-declared elements (Ev60RgbPanel here) are assigned. Root-caused
/// with WinDbg+SOS 2026-07-10 on the Makalu tab — see CHANGELOG.md.
///
/// State (effect + params + colors) lives only in memory for this first cut —
/// per-session persistence (like Everest Max's <c>rgb.*</c> Settings keys) is
/// a future step once the panel has proven itself on real hardware.
/// </summary>
public partial class MainWindow
{
    private Everest60Service _ev60 = null!;
    /// <summary>SDK path (Everest360_USB.dll), used for Key Binding AND
    /// numpad-position detection (GetSubDeviceInfo — see
    /// Ev60RefreshStatus/ApplyEv60NumpadPosition). Opened eagerly at startup
    /// (2026-07-11, changed from lazy-on-Key-Binding-visit after a
    /// real-hardware report that lazy per-poll open/close never actually
    /// detected the numpad — matches Base Camp's own EV60MessageHandler,
    /// which keeps the driver open continuously rather than reopening per
    /// check). See Everest60SdkService's remarks for the still-unverified
    /// coexistence-with-raw-HID caveat.</summary>
    private readonly Everest60SdkService _ev60Sdk = new();
    private DispatcherTimer? _ev60PollTimer;
    private bool _ev60Connected;
    private Everest60Store _ev60Store = null!;
    private bool _ev60SuppressProfile;

    /// <summary>Backlight-off-when-idle timer (device setting, global across
    /// profiles). Owned here (not by Ev60RgbPanel) — same split as
    /// _ev60ActionHost/_ev60Sdk — because it needs the panel's
    /// SetBacklightForcedOff, not the other way around.</summary>
    private BacklightIdleTimer? _ev60AutoOffTimer;

    /// <summary>Locale legend selection for the 64-key overlay (Settings' "Layout"
    /// combo) — see <see cref="Everest60KeyboardLayout.GetMainBoard"/>'s doc comment:
    /// same fixed physical board for every value, only printed legends change.</summary>
    private KeyboardLayoutType _ev60LayoutType = KeyboardLayoutType.AnsiUs;

    /// <summary>Live LED-color readback poller (raw HID, Everest60Service.TryGetColorData
    /// — see Everest60Protocol.ReadColorData's doc comment for why not the
    /// vendor SDK) — started/stopped by <see cref="UpdateEv60LedPreviewActive"/>
    /// whenever the Lighting section becomes visible/hidden, same gating
    /// pattern as Everest Max/MacroPad in MainWindow.LedPreview.cs. See
    /// Everest60LedColorPoller's doc comment for why 300ms (not the other
    /// devices' 120ms).</summary>
    private Everest60LedColorPoller? _ev60LedPoller;

    /// <summary>Button + LedHalo border per main-board key (keyed by LED
    /// index) and per numpad key (no meaningful index — a plain list), for
    /// Keycap Appearance's style-blend rendering (reuses the
    /// <c>KeyVisual</c> record from MainWindow.KeycapAppearance.cs).</summary>
    private readonly Dictionary<int, KeyVisual> _ev60KeyVisuals = new();

    /// <summary>Each main-board key's original legend TextBlock, captured at build time — see
    /// _evOriginalKeyContent (MainWindow.LedPreview.cs) for the full doc; used to restore the
    /// legend when a per-key custom-image override is cleared.</summary>
    private readonly Dictionary<int, FrameworkElement> _ev60OriginalKeyContent = new();
    private readonly List<KeyVisual> _ev60NumpadVisuals = new();

    /// <summary>Border-square Button per wire index (0-43, see
    /// <see cref="Everest60Protocol.SideLedIndex"/>) — built once by
    /// <see cref="BuildEv60BorderSquares"/>, mirrors Everest Max's
    /// _customSideButtons (MainWindow.CustomLighting.cs).</summary>
    private readonly Dictionary<int, Button> _ev60BorderButtons = new();

    /// <summary>Number of K2-side profile slots for Everest 60 — see the
    /// "Profile management" doc comment below (no firmware profile concept).</summary>
    private const int Ev60ProfileCount = 5;

    private Ev60ActionHost? _ev60ActionHost;
    private ButtonActionEngine? _ev60Engine;

    /// <summary>
    /// The Everest 60's SDK key callback reports a key by its <b>DLLKeyId</b> — settled
    /// on real hardware 2026-07-27 by two independent presses logged with their identity
    /// known in advance: pressing "2" (DLLKeyId 3, DLLMatrixIndex 2) reported wMatrix
    /// 0x03, and pressing Numpad 5 (DLLKeyId 97, DLLMatrixIndex 110) reported 0x61=97.
    ///
    /// <para>An earlier pass this same day switched this to DLLMatrixIndex, reasoning
    /// from Base Camp's own <c>OtherDeviceOperations</c> (which does look bindings up by
    /// <c>DLLMatrixIndex == wMatrix</c>) — but that code path is the Everest MAX's, and
    /// the two devices don't agree. The switch didn't fail loudly either: the spaces
    /// overlap in the low integers, so pressing "2" fired the action bound to "3". The
    /// original reading was right; what actually broke key bindings back then was
    /// <c>EnableKeyFunc</c> returning false (see Everest60SdkService.DoOpenAndInit).</para>
    ///
    /// <para>Translation goes through <see cref="BaseCampDbImporter.Everest60LedIndexFromDllKeyId"/>
    /// rather than a local table, because that one also resolves the accessory numpad's
    /// DLLKeyIds — which the callback reports too, and which the main-board-only table
    /// used to drop.</para>
    /// </summary>
    private readonly Dictionary<int, int> _ev60DllKeyIdToLedIndex = new();

    /// <summary>Called once from the MainWindow constructor.</summary>
    private void InitEverest60Module()
    {
        _ev60 = new Everest60Service(LogEverest60);
        _ev60Store = new Everest60Store();

        Ev60RgbPanel.CustomKeysCleared += ApplyEv60KeycapAppearanceToAllKeys;
        // Custom Lighting (per-key + border ring + numpad) repaint bridge —
        // ApplyEv60KeycapAppearanceToAllKeys already repaints key/numpad Buttons
        // from Ev60RgbPanel's paint state and now also calls Ev60ReapplyBorderOverlays.
        Ev60RgbPanel.RequestReapplyOverlays += ApplyEv60KeycapAppearanceToAllKeys;
        Ev60RgbPanel.PaintModeChanged += _ => UpdateEv60BorderOverlayVisibility();
        _ev60AutoOffTimer = new BacklightIdleTimer(Dispatcher,
            () => Ev60RgbPanel.SetBacklightForcedOff(true),
            () => Ev60RgbPanel.SetBacklightForcedOff(false));
        Ev60RgbPanel.BacklightManuallyToggled += () => _ev60AutoOffTimer?.RegisterActivity();
        // Init() raises AutoOffConfigChanged once with the loaded value — must be
        // subscribed before Init() runs so that first push isn't missed (see
        // Everest60RgbPanel.Init's doc comment on the event).
        Ev60RgbPanel.AutoOffConfigChanged += (enabled, seconds) => _ev60AutoOffTimer?.Configure(enabled, seconds);
        Ev60RgbPanel.Init(_ev60, LogEverest60, _ev60Store, Ev60CurrentProfile);
        Ev60KeyBindingPanel.Init(_ev60Store, Ev60CurrentProfile, LogEverest60, () => _ev60LayoutType);
        InitEv60SectionNav();

        _ev60LayoutType = LoadPersistedEv60KeyboardLayout();
        BuildEverest60KeyboardOverlay();
        BuildEv60BorderSquares();
        BuildEv60NumpadBorderSquares();
        ApplyEv60NumpadPosition(Ev60NumpadPosition.None); // until the first poll completes
        InitEv60SettingsPanel();
        InitEv60KeyboardLayoutSelector();

        // DllKeyId -> ledIndex reverse map (main board), for translating the SDK key
        // callback — see _ev60DllKeyIdToLedIndex's doc comment.
        var keyIdTable = Everest60RemapData.LedIndexToDllKeyIdArray;
        for (int led = 0; led < keyIdTable.Length; led++)
            _ev60DllKeyIdToLedIndex[keyIdTable[led]] = led;

        _ev60Sdk.KeyEvent += OnEv60Key;

        _ev60ActionHost = new Ev60ActionHost(
            dispatcher:            Dispatcher,
            log:                   LogEverest60,
            currentProfile:        Ev60CurrentProfile,
            profileCount:          () => Ev60ProfileCount,
            sdkVersion:            () => { try { return Everest60SdkNative.GetDLLVersion(); } catch { return 0; } },
            getButtons:            Ev60GetButtons,
            pressButton:           Ev60PressButton,
            switchProfile:         Ev60SwitchProfile,
            configuredPythonPath:  () => _ev60Store.GetSetting("python.exePath"),
            listAllProfileTargets: ListAllProfileTargets,
            switchProfileByKey:    SwitchProfileByKey,
            listMacroNames:        ListAllMacroNames,
            playMacro:             PlayMacroByName);
        Ev60KeyBindingPanel.SetActionHost(_ev60ActionHost);
        Ev60KeyBindingPanel.SetMainBoardDisablePush(PushEv60DisabledKeysToDevice);
        Ev60KeyBindingPanel.SetNumpadDevicePush(
            writeBinding:     (dllKeyId, label) => { _ev60.WriteNumpadKeyBinding(dllKeyId, label); StartEv60NumpadPresenceGrace(); },
            unassignBinding:  dllKeyId => { _ev60.UnassignNumpadKey(dllKeyId); StartEv60NumpadPresenceGrace(); });

        _ev60Engine = new ButtonActionEngine(_ev60ActionHost);
        _ev60Engine.Start();

        LstEv60Profile.ContextMenu = Ev60BuildProfileContextMenu();
        BtnEv60ProfileMenu.ContextMenu = Ev60BuildProfileMenuNoEdit();
        Ev60RefreshProfiles();
        Ev60ReloadProfile(Ev60CurrentProfile());

        _ev60LedPoller = new Everest60LedColorPoller(_ev60);
        _ev60LedPoller.ColorsUpdated += OnEv60ColorsUpdated;

        // Everest60NumpadKeyPoller (Feature-Report polling, 100ms interval) is NO LONGER
        // started here — 2026-07-28, user question "why is the numpad on a slow poll when
        // typing on it is instant?" was the right challenge: the numpad speaks the exact
        // same standard boot-keyboard reports as the main board (same USB device), so it
        // now goes through the same instant, event-driven Raw Input path — see
        // Everest60KeyboardLayout.ScanCodeToLedIndex's doc comment for the numpad scan
        // codes and HandleEv60KeyFromHid for the shared handler. The poller class itself
        // is left in place (unused) rather than deleted, in case Raw Input ever turns out
        // to miss numpad events Feature-Report polling wouldn't have.

        Closed += (_, _) =>
        {
            try { RestoreEv60DisabledKeysOnExit(); } catch { /* ignore */ }
            try { _ev60LedPoller?.Dispose(); } catch { /* ignore */ }
            try { _ev60Engine?.Dispose(); } catch { /* ignore */ }
            try { _ev60Sdk.Dispose(); } catch { /* ignore */ }
        };

        // Eager open moved to Ev60AutoOpen(), called from AutoOpenDrivers()
        // once _hWnd is a real handle (see its doc comment) — this
        // constructor runs before OnSourceInitialized, so _hWnd is still
        // IntPtr.Zero here.
        _ev60PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _ev60PollTimer.Tick += (_, _) => { Ev60RefreshStatus(); Ev60ClearStaleHighlights(); };
        _ev60PollTimer.Start();
        Ev60RefreshStatus();
    }

    // ------------------------------------------------------------
    // Profile management — Everest 60 has no firmware profile concept
    // either (remap writes straight to firmware with no onboard slots,
    // lighting is raw HID — see architectural note in _PROJECT_MAP.md): a
    // "profile" is purely a K2-side slot (1..5), persisted in Everest60Store.
    // Switching re-sends the stored lighting state and rewrites the stored
    // 64-key binding table LIVE to firmware — it does NOT call SaveFlash
    // automatically (stays behind the manual "Save" button in
    // Everest60KeyBindingPanel, to avoid wearing the keyboard's flash on
    // every switch). Mirrors MainWindow.Makalu.cs's Mk* profile methods.
    // ------------------------------------------------------------

    private sealed record Ev60ProfileItem(int Slot, string Label)
    {
        // Same "+ New profile" placeholder convention as EvProfileItem/DpProfileItem/
        // MpProfileItem — see Ev60RefreshProfiles.
        public bool IsNew => Label.StartsWith("+");
        public bool IsRealProfile => !IsNew;
        public override string ToString() => Label;
    }

    private int Ev60CurrentProfile()
        => LstEv60Profile.SelectedItem is Ev60ProfileItem pi ? pi.Slot : 1;

    /// <summary>Populates the Everest 60 profile combo with configured profiles + "New
    /// profile…" — mirrors EvRefreshProfiles (MainWindow.Everest.cs). K2 always has 5
    /// fixed slots for this device (no firmware profile concept), but the UI only lists
    /// the ones actually in use, same as every other module.</summary>
    private void Ev60RefreshProfiles()
    {
        _ev60SuppressProfile = true;
        try
        {
            var existing = _ev60Store.GetExistingProfiles();
            if (existing.Count == 0)
            {
            // No profile at all — fresh install, hardware factory reset or the Settings
            // tab's "Restore all defaults": recreate one instead of only showing a
            // phantom slot 1 under the generic "Profile 1" label. Named "Default
            // profile" (localized, `default_profile_name`), the same name Base Camp
            // gives its own starting profile. User request 2026-08-21.
                _ev60Store.SetProfileName(1, Loc.Get("default_profile_name"));
                _ev60Store.MarkProfileExists(1);
                existing.Add(1);
            }
            var items = new List<Ev60ProfileItem>();
            foreach (var slot in existing)
            {
                string name = _ev60Store.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
                items.Add(new Ev60ProfileItem(slot, name));
            }
            int nextFree = Enumerable.Range(1, Ev60ProfileCount)
                .FirstOrDefault(s => !existing.Contains(s));
            if (nextFree > 0)
                items.Add(new Ev60ProfileItem(nextFree, Loc.Get("new_profile")));

            LstEv60Profile.ItemsSource = items;

            int current = _ev60Store.GetCurrentProfile();
            LstEv60Profile.SelectedItem = items.Find(x => x.Slot == current && !x.IsNew) ?? items[0];

            Ev60RegisterProfileLaunchWatchers();
        }
        finally { _ev60SuppressProfile = false; }
    }

    /// <summary>Registers this device's profiles with K2.Core.Services.ProfileLaunchWatcher
    /// — see DpRegisterProfileLaunchWatchers (MainWindow.DisplayPad.cs) for the shared
    /// pattern/rationale. Loops all 5 fixed slots directly (not just the "existing"
    /// ones shown in the combo) since a launch-exe link can outlive the profile being
    /// otherwise emptied out.</summary>
    private void Ev60RegisterProfileLaunchWatchers()
    {
        const string scope = "Ev60:";
        var currentKeys = new HashSet<string>();
        for (int slot = 1; slot <= Ev60ProfileCount; slot++)
        {
            string? exe = _ev60Store.GetSetting($"profile.{slot}.launchExe");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string key = scope + slot;
            currentKeys.Add(key);
            int capturedSlot = slot;
            ProfileLaunchWatcher.Instance.UpdateRegistration(key, exe,
                () => Ev60SwitchProfile(capturedSlot.ToString()));
        }
        foreach (var staleKey in ProfileLaunchWatcher.Instance.KeysWithPrefix(scope).Except(currentKeys))
            ProfileLaunchWatcher.Instance.RemoveRegistration(staleKey);
    }

    private void Ev60SelectProfileSlot(int slot)
    {
        _ev60SuppressProfile = true;
        try
        {
            if (LstEv60Profile.ItemsSource is List<Ev60ProfileItem> items)
                LstEv60Profile.SelectedItem = items.Find(x => x.Slot == slot && !x.IsNew) ?? items[0];
        }
        finally { _ev60SuppressProfile = false; }
    }

    /// <summary>Pushes the given profile's stored lighting/key bindings into
    /// both panels and re-applies them to hardware (if connected/open).</summary>
    private void Ev60ReloadProfile(int slot)
    {
        Ev60RgbPanel.Ev60ReloadProfile(slot);
        Ev60KeyBindingPanel.Ev60ReloadKeyBindings(slot);   // reconciles disabled keys itself
        PushNumpadKeyBindingsToDevice();
        InitEv60SettingsPanel(); // re-loads Keycap Appearance for this slot — user request 2026-07-25
    }

    /// <summary>Re-writes every numpad key currently bound in this profile to
    /// the physical device — the firmware doesn't persist a binding across a
    /// replug/profile switch (same reason lighting needs re-applying via
    /// Ev60RgbPanel.Ev60ReloadProfile above), unlike the main board's
    /// bindings which live purely in K2 software and need no device push at
    /// all.</summary>
    private void PushNumpadKeyBindingsToDevice()
    {
        bool wroteAny = false;
        foreach (var key in Ev60KeyBindingPanel.Keys)
            if (key.NumpadIndex is int npi)
            {
                _ev60.WriteNumpadKeyBinding(Everest60RemapData.NumpadDllKeyId[npi], key.Label);
                wroteAny = true;
            }
        if (wroteAny) StartEv60NumpadPresenceGrace();
    }

    /// <summary>
    /// Resolves "Next"/"Previous"/"1..N" and switches the Everest 60's K2-side
    /// profile slot — mirrors MainWindow.Everest.cs's EvSwitchProfile, but
    /// there is no firmware call to make (see the "Profile management" doc
    /// comment above): switching just reloads the slot's stored keys/lighting,
    /// same as picking it from LstEv60Profile.
    /// </summary>
    private void Ev60SwitchProfile(string target)
    {
        int cur = Ev60CurrentProfile();
        int next = cur;
        var t = (target ?? "").Trim();
        if (t.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Next Profile", StringComparison.OrdinalIgnoreCase))
            next = cur == Ev60ProfileCount ? 1 : cur + 1;
        else if (t.Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("Previous Profile", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("prev", StringComparison.OrdinalIgnoreCase))
            next = cur == 1 ? Ev60ProfileCount : cur - 1;
        else if (int.TryParse(t, out var n) && n >= 1 && n <= Ev60ProfileCount)
            next = n;
        else
        {
            // Named-profile target — see EvSwitchProfile's identical fallback for the
            // rationale (Base Camp XML/DB can carry a destination profile NAME).
            int? byName = null;
            for (int s = 1; s <= Ev60ProfileCount; s++)
                if (string.Equals(_ev60Store.GetProfileName(s), t, StringComparison.OrdinalIgnoreCase)) { byName = s; break; }
            if (byName is int found) next = found;
            else
            {
                LogEverest60($"[EXEC] profile: target \"{t}\" not resolved");
                return;
            }
        }
        if (next == cur) { LogEverest60($"[EXEC] profile: already on {cur}"); return; }

        _ev60Store.SetCurrentProfile(next);
        Ev60SelectProfileSlot(next);
        Ev60ReloadProfile(next);
        LogEverest60($"[EXEC] profile -> {next}");
    }

    private void LstEv60Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ev60SuppressProfile) return;
        if (LstEv60Profile.SelectedItem is not Ev60ProfileItem pi) return;
        int slot = pi.Slot;

        if (pi.IsNew)
        {
            // Create empty profile (see Everest60Store.MarkProfileExists for why this
            // doesn't use a placeholder Keys row like MacroPad/DisplayPad do) — mirrors
            // LstEvProfile_SelectionChanged (MainWindow.Everest.cs).
            _ev60Store.MarkProfileExists(slot);
            LogEverest60($"[UI ] New empty Everest 60 profile created: slot {slot}");
            Ev60RefreshProfiles();
            Ev60SelectProfileSlot(slot);
        }

        _ev60Store.SetCurrentProfile(slot);
        LogEverest60($"[UI ] Everest 60 profile selected: {slot}");
        Ev60ReloadProfile(slot);
    }

    /// <summary>Right-click menu for LstEv60Profile rows — see DpBuildProfileContextMenu
    /// (MainWindow.DisplayPad.cs) for the shared pattern/rationale.</summary>
    private ContextMenu Ev60BuildProfileContextMenu()
    {
        var menu = new ContextMenu();
        var miRename = new MenuItem { Header = Loc.Get("rename_profile") };
        miRename.Click += BtnEv60RenameProfile_Click;
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnEv60ImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnEv60ImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnEv60ExportProfiles_Click;
        var miDelete = new MenuItem { Header = Loc.Get("delete_profile") };
        miDelete.Click += BtnEv60DeleteProfile_Click;
        menu.Items.Add(miRename);
        menu.Items.Add(new Separator());
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDelete);
        return menu;
    }

    /// <summary>Same items as <see cref="Ev60BuildProfileContextMenu"/> minus Rename/Delete —
    /// opened from the small "…" button in the Profile header (BtnEv60ProfileMenu_Click),
    /// which is not tied to a specific row so renaming/deleting a specific profile
    /// wouldn't make sense there.</summary>
    private ContextMenu Ev60BuildProfileMenuNoEdit()
    {
        var menu = new ContextMenu();
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnEv60ImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnEv60ImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnEv60ExportProfiles_Click;
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        return menu;
    }

    private void BtnEv60ProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = btn;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void BtnEv60RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        int slot = Ev60CurrentProfile();
        string current = _ev60Store.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        string? name = ShowRenameDialog(current,
            Loc.Get("rename_profile_title"),
            Loc.Get("rename_profile_prompt"));
        if (name is null) return;
        _ev60Store.SetProfileName(slot, name);
        Ev60RefreshProfiles();
        Ev60SelectProfileSlot(slot);
        LogEverest60($"[UI ] Everest 60 profile {slot} renamed to \"{name}\"");
    }

    private void BtnEv60DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        int slot = Ev60CurrentProfile();
        // Cannot delete the last real profile — mirrors BtnEvDeleteProfile_Click
        // (MainWindow.Everest.cs), now that empty slots are hidden from the combo.
        if (_ev60Store.GetExistingProfiles().Count <= 1)
        {
            MessageBox.Show(Loc.Get("delete_profile_last"),
                Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string profileName = _ev60Store.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("delete_profile_confirm", profileName),
            Loc.Get("delete_profile"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _ev60Store.ClearProfile(slot);
        LogEverest60($"[UI ] Everest 60 profile {slot} deleted.");
        Ev60RefreshProfiles();
        // Land on a SURVIVING slot, not the just-deleted (now hidden) one — same
        // "phantom click" fix as BtnEvDeleteProfile_Click's identical fallback.
        int fallback = _ev60Store.GetExistingProfiles().DefaultIfEmpty(1).First();
        Ev60SelectProfileSlot(fallback);
        _ev60Store.SetCurrentProfile(fallback);
        Ev60ReloadProfile(fallback);
    }

    /// <summary>Gear-icon popup for an Everest 60 profile row (see ProfileGear_Click in
    /// MainWindow.xaml.cs): rename, delete (same guard as
    /// <see cref="BtnEv60DeleteProfile_Click"/>), or link an executable whose launch
    /// auto-switches to this profile (see K2.Core.Services.ProfileLaunchWatcher,
    /// registered from <see cref="Ev60RefreshProfiles"/>).</summary>
    private void Ev60ShowProfileGear(Ev60ProfileItem pi)
    {
        string currentName = _ev60Store.GetProfileName(pi.Slot) ?? Loc.Get("profile_n", pi.Slot);
        string currentExe = _ev60Store.GetSetting($"profile.{pi.Slot}.launchExe") ?? "";
        var dlg = new ProfileSettingsDialog(currentName, currentExe) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.DeleteRequested)
        {
            if (_ev60Store.GetExistingProfiles().Count <= 1)
            {
                MessageBox.Show(Loc.Get("delete_profile_last"),
                    Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var res = MessageBox.Show(
                Loc.Get("delete_profile_confirm", currentName),
                Loc.Get("delete_profile"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
            _ev60Store.ClearProfile(pi.Slot);
            _ev60Store.SetSetting($"profile.{pi.Slot}.launchExe", "");
            LogEverest60($"[UI ] Everest 60 profile {pi.Slot} deleted (gear).");
            Ev60RefreshProfiles();
            int fallback = _ev60Store.GetExistingProfiles().DefaultIfEmpty(1).First();
            Ev60SelectProfileSlot(fallback);
            _ev60Store.SetCurrentProfile(fallback);
            Ev60ReloadProfile(fallback);
            return;
        }

        _ev60Store.SetProfileName(pi.Slot, dlg.ProfileName);
        _ev60Store.SetSetting($"profile.{pi.Slot}.launchExe", dlg.ExePath);
        LogEverest60($"[UI ] Everest 60 profile {pi.Slot} settings updated (gear).");
        Ev60RefreshProfiles();
        Ev60SelectProfileSlot(pi.Slot);
    }

    /// <summary>Wipes EVERY Everest 60 profile back to K2's defaults: other profiles are
    /// deleted outright (mirrors BtnEv60DeleteProfile_Click), the current one keeps its
    /// name but has its lighting and key bindings reset to K2's defaults (see
    /// Everest60RgbPanel.RestoreDefaults / Everest60KeyBindingPanel.RestoreDefaults) and
    /// re-applied to the keyboard if connected. User request 2026-07-29 (previously only
    /// reset the current profile).</summary>
    private void BtnEv60RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_device_confirm", Loc.Get("tab_everest60")),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        int current = Ev60CurrentProfile();
        foreach (var slot in _ev60Store.GetExistingProfiles())
            if (slot != current) _ev60Store.ClearProfile(slot);

        Ev60RgbPanel.RestoreDefaults();
        Ev60KeyBindingPanel.RestoreDefaults();
        LogEverest60($"[UI ] Everest 60 restored to factory defaults (all profiles, lighting and key bindings).");
        Ev60RefreshProfiles();
    }

    // ------------------------------------------------------------
    // Import from Base Camp DB — mirrors BtnEvImportBc_Click in
    // MainWindow.Everest.cs. See BaseCampDbImporter's Everest 60 section for
    // the lighting-vs-key-binding confidence caveat (only one real profile
    // ever seen, factory default — its Fn-layer legends aren't real
    // user remaps, so Key Binding import is necessarily best-effort).
    // ------------------------------------------------------------

    private void BtnEv60ImportBc_Click(object sender, RoutedEventArgs e)
    {
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            LogEverest60("[IMP-BC] BaseCamp.db not found.");
            return;
        }
        LogEverest60($"[IMP-BC] DB: {dbPath}");

        Dictionary<int, List<BaseCampDbImporter.BcProfile>> bcDevices;
        try { bcDevices = BaseCampDbImporter.ReadEverest60Profiles(dbPath); }
        catch (Exception ex) { LogEverest60($"[IMP-BC] Read error: {ex.Message}"); return; }

        if (bcDevices.Count == 0)
        {
            LogEverest60("[IMP-BC] No Everest 60 profiles in DB.");
            return;
        }

        string deviceLabel = AppSettings.Everest60DeviceName ?? (TabEverest60.Header as string) ?? Loc.Get("tab_everest60");

        List<BaseCampDbImporter.BcProfile> allProfiles;
        if (bcDevices.Count == 1)
        {
            allProfiles = bcDevices.Values.First().OrderBy(p => p.Slot).ToList();
        }
        else
        {
            var options = bcDevices.Select(kv => (
                BcDeviceId: kv.Key,
                Label: Loc.Get("bc_pick_device_label", kv.Key, kv.Value.Count,
                    string.Join(", ", kv.Value.Select(p => p.Name)))
            )).ToList();
            var picker = new BcDevicePickerDialog(deviceLabel, options) { Owner = this };
            if (picker.ShowDialog() != true) return;
            allProfiles = bcDevices[picker.SelectedBcDeviceId!.Value].OrderBy(p => p.Slot).ToList();
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Import {allProfiles.Count} profile(s) into \"{deviceLabel}\"?\n");
        foreach (var p in allProfiles)
            sb.AppendLine($"  {(p.IsSelected ? "[ACTIVE] " : "")}{p.Name}");
        sb.AppendLine();
        sb.AppendLine(Loc.Get("bc_import_will_wipe", deviceLabel));

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Pre-read every profile's bindings+lighting BEFORE wiping anything: this import is
        // destructive (replace, not append), so a corrupt/locked Base Camp DB must surface
        // while the existing K2 profiles are still intact — not after they're gone.
        try
        {
            foreach (var p in allProfiles)
            {
                BaseCampDbImporter.ReadEverest60KeyBindingsRaw(dbPath, p.ProfileId);
                BaseCampDbImporter.ReadEverest60LightingRaw(dbPath, p.ProfileId);
            }
        }
        catch (Exception ex)
        {
            LogEverest60($"[IMP-BC] Pre-read failed, aborting before wipe: {ex.Message}");
            MessageBox.Show(this, Loc.Get("bc_import_read_failed", ex.Message),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Wipe: replace, don't append. Everest 60 always has 5 fixed K2-side slots (no
        // firmware profile concept — see the "Profile management" doc comment above).
        for (int slot = 1; slot <= Ev60ProfileCount; slot++)
            _ev60Store.ClearProfile(slot);

        int totalKeys = 0, skipped = 0;
        var usedSlots = new HashSet<int>();

        // Existing K2 macro names, used by TranslateAction to auto-match a Base Camp
        // named-macro reference ("Default" FunctionType) against the user's own macro
        // library — same lookup the DisplayPad/Everest import paths use (BaseCampDbImporter.
        // TranslateDefaultAction's doc comment).
        var macroNames = _macroStore?.GetAll()
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        foreach (var profile in allProfiles)
        {
            try
            {
                int targetSlot = BaseCampDbImporter.FindFreeSlot(usedSlots);
                if (targetSlot == 0) { skipped++; continue; } // more BC profiles than the 5 fixed slots allow
                usedSlots.Add(targetSlot);

                int keys = BaseCampDbImporter.ImportEverest60Profile(dbPath, profile, _ev60Store, targetSlot, macroNames);
                totalKeys += keys;
                LogEverest60($"[IMP-BC] slot {profile.Slot} '{profile.Name}' -> K2 slot {targetSlot}: keys={keys}");
            }
            catch (Exception ex) { LogEverest60($"[IMP-BC] slot {profile.Slot} error: {ex.Message}"); }
        }

        // Always land on the FIRST imported profile and force a reload — simpler and
        // safer than trying to restore whatever was active in Base Camp (user request:
        // a plain, predictable refresh after import beats guessing at BC's own state).
        int finalSlot = usedSlots.DefaultIfEmpty(1).Min();
        _ev60Store.SetCurrentProfile(finalSlot);
        Ev60RefreshProfiles();
        Ev60SelectProfileSlot(finalSlot);
        Ev60ReloadProfile(finalSlot);
        LogEverest60(Loc.Get("ev60_imported_bc", allProfiles.Count, totalKeys));

        if (skipped > 0)
        {
            LogEverest60($"[IMP-BC] {skipped} profile(s) skipped: no free slot left.");
            MessageBox.Show(this, Loc.Get("import_some_skipped_no_slot", skipped),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------
    // Import K2-only XML (produced by Ev60ProfileExporter.ExportK2).
    // ------------------------------------------------------------

    private void BtnEv60ImportXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = Loc.Get("dp_open_bc_profile"),
            Filter = Loc.Get("dp_filter_bc_xml"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(dlg.FileName);
            var root = doc.Root;
            if (root is null) return;

            string profileName = root.Element("ProfileName")?.Value
                                  ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            // Always land in a FRESH slot — see BaseCampDbImporter.FindFreeSlot's doc comment.
            int slot = BaseCampDbImporter.FindFreeSlot(_ev60Store.GetExistingProfiles());
            if (slot == 0)
            {
                MessageBox.Show(this, Loc.Get("import_no_free_slot", profileName),
                    Loc.Get("dp_open_bc_profile"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Real Base Camp XML carries Everest 60 key bindings under an
            // Everest60KeyBindings/Everest60KeyBinding wrapper (correct spelling,
            // confirmed 2026-07-26 against a real BC XML export — the typo'd flat
            // <Everest60KeyBidings> shape below was K2's own pre-2026-07-26 export
            // format, never real Base Camp data, kept as a fallback for old K2 files).
            var keyEls = root.Descendants("Everest60KeyBinding").ToList();
            bool legacyShape = keyEls.Count == 0;
            if (legacyShape)
                keyEls = root.Descendants("Everest60KeyBidings").ToList();

            var macroNames = _macroStore?.GetAll()
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            // Same fresh-slot reset the DB import path does — without it, leftovers from a
            // previously deleted profile in the same slot survive under the new one.
            _ev60Store.ClearProfile(slot);

            int imported = 0, skippedKeys = 0;
            foreach (var b in keyEls)
            {
                string? funcType  = b.Element("FunctionType")?.Value;
                string? funcValue = b.Element("FunctionValue")?.Value;
                string? actionType, actionValue;
                int ledIndex;

                if (legacyShape)
                {
                    // K2's own OLD export: FunctionType="K2Action", SubFunctionType=ActionType,
                    // FunctionValue=ActionValue verbatim — no native BC vocabulary to translate,
                    // and DLLMatrixIndex genuinely IS K2's LED index there (K2 wrote it itself).
                    if (funcType != "K2Action") continue;
                    if (!int.TryParse(b.Element("DLLMatrixIndex")?.Value, out ledIndex)) continue;
                    actionType  = b.Element("SubFunctionType")?.Value;
                    actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;
                }
                else
                {
                    // Real Base Camp data: only the base layer is a real remap — see
                    // BaseCampDbImporter's class-level doc comment on Everest 60's
                    // LayerType=3 factory Fn-legend rows.
                    int layerType = int.TryParse(b.Element("LayerType")?.Value, out var lt) ? lt : 1;
                    bool isAssigned = string.Equals(b.Element("IsKeyAssigned")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                    if (layerType != 1 || !isAssigned) continue;

                    // LED index comes from DLLKeyId, NOT DLLMatrixIndex — see
                    // BaseCampDbImporter.Everest60LedIndexFromDllKeyId's doc comment
                    // (this used to read DLLMatrixIndex verbatim, which put 24 of the 64
                    // physical keys on the wrong LED and wrote out-of-range indices for
                    // every catalog row this board doesn't have).
                    if (!int.TryParse(b.Element("DLLKeyId")?.Value, out int dllKeyId)) continue;
                    ledIndex = BaseCampDbImporter.Everest60LedIndexFromDllKeyId(dllKeyId);
                    if (ledIndex < 0) { skippedKeys++; continue; }

                    if (funcType == "K2Action")
                    {
                        actionType  = b.Element("SubFunctionType")?.Value;
                        actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;
                    }
                    else
                    {
                        string? subType = b.Element("SubFunctionType")?.Value;
                        string? customUrl = b.Element("CustomURL")?.Value;
                        (actionType, actionValue) = BaseCampDbImporter.TranslateAction(funcType, subType, funcValue, macroNames, customUrl);
                    }
                }

                if (string.IsNullOrEmpty(actionType)) continue;
                _ev60Store.SaveKey(new Ev60KeyRecord(slot, ledIndex, null, actionType, actionValue));
                imported++;
            }

            // Lighting: real Base Camp XML wraps multiple <Everest60Lighting> rows
            // (one per effect, string EffIndex names) — the old flat single-element
            // shape (numeric EffIndex directly under <Everest60Lightings>) is K2's own
            // legacy export format, kept as a fallback.
            var lightingItems = root.Descendants("Everest60Lighting").ToList();
            System.Xml.Linq.XElement? activeLighting = lightingItems.Count > 0
                ? (lightingItems.FirstOrDefault(l => string.Equals(l.Element("IsActive")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                   ?? lightingItems[0])
                : root.Element("Everest60Lightings"); // legacy flat shape (numeric EffIndex directly under the wrapper)

            if (activeLighting is not null)
            {
                string? effName = activeLighting.Element("EffIndex")?.Value;
                int effIndex = int.TryParse(effName, out var ein) ? ein : (effName ?? "").Trim().ToLowerInvariant() switch
                {
                    "static" => 1, "colorwave" or "color wave" => 2, "tornado" => 3,
                    "breathing" => 4, "reactive" or "reactivea" => 5, "matrix" => 6,
                    "custom" => 7, "yeti" or "yeti mode" => 8, "off" => 9, _ => 1,
                };
                var eff = effIndex switch
                {
                    1 => Everest60Protocol.Effect.Static, 2 => Everest60Protocol.Effect.Wave,
                    3 => Everest60Protocol.Effect.Tornado, 4 => Everest60Protocol.Effect.Breathing,
                    5 => Everest60Protocol.Effect.Reactive,
                    // 7 = Custom is a real effect here too — see the matching arm in
                    // BaseCampDbImporter.ReadEverest60LightingRaw for why it can't fall
                    // through to Static (the dropdown showed "Static" on every imported
                    // Custom profile).
                    7 => Everest60Protocol.Effect.Custom,
                    8 => Everest60Protocol.Effect.Yeti,
                    9 => Everest60Protocol.Effect.Off, _ => Everest60Protocol.Effect.Static,
                };
                string activeMode = effIndex == 7 ? "custom" : "preset";
                int color1 = BaseCampDbImporter.ParseBcColor(activeLighting.Element("Color1")?.Value, 0x900000);
                int color2 = BaseCampDbImporter.ParseBcColor(activeLighting.Element("Color2")?.Value, 0);
                int speedPct = int.TryParse(activeLighting.Element("Speed")?.Value, out var sp) ? sp : 50;
                int rawDir = int.TryParse(activeLighting.Element("Direction")?.Value, out var di) ? di : 0;
                int dirIdx = BaseCampDbImporter.Everest60DirIndexFor(eff, rawDir);
                double bright = int.TryParse(activeLighting.Element("Brightness")?.Value, out var br) ? br : 100;
                // <Type> = Base Camp's color-type pill (0 single / 1 dual / 2 rainbow) —
                // see BaseCampDbImporter.ApplyLightingToStore for how that was established.
                int colorType = int.TryParse(activeLighting.Element("Type")?.Value, out var ct) ? ct : 0;
                // Per-key Custom colors: same [{Ids,KeyCode,ColorHex}] payload the DB path
                // parses — previously dropped here entirely. Read from the Custom ROW,
                // not from the active one, and regardless of which effect is active: the
                // paint belongs to the profile and Base Camp keeps it there either way
                // (verified 2026-07-26 on two exports of the same Everest 60 profile,
                // one with Custom active and one on Color Wave — byte-identical payload).
                var customEl = lightingItems.FirstOrDefault(l =>
                    string.Equals(l.Element("EffIndex")?.Value, "Custom", StringComparison.OrdinalIgnoreCase))
                    ?? activeLighting;
                // The board is imported whatever the active effect is, but it does NOT
                // decide the active effect: on this device Base Camp keeps the full
                // 192-address board forever, so its presence says nothing about whether
                // Custom is in use — only the Custom row's own IsActive does (already
                // folded into activeMode above). See BaseCampDbImporter.LooksPainted's
                // doc comment for why Everest Max/MacroPad can afford the opposite rule.
                var custom = BaseCampDbImporter.ParseEverest60Custom(
                    customEl.Element("CustomLightings")?.Value);
                _ev60Store.SaveLighting(slot, new Ev60LightingRecord(
                    (int)eff, color1, color2, speedPct, dirIdx, colorType == 2, bright, bright, activeMode,
                    custom.KeyColors, colorType == 1, custom.SideColors, custom.NumpadRingColors));
                LogEverest60($"[IMP-XML] custom lighting: {custom.KeyColors.Count} key LED(s), " +
                             $"{custom.SideColors.Count} side, {custom.NumpadRingColors.Count} numpad ring");
            }

            // Settings (Everest60Settings/Everest60Setting) — Game Mode/Core LED, same
            // fields BaseCampDbImporter.ReadEverest60Settings reads from the DB.
            var settingsEl = root.Descendants("Everest60Setting").FirstOrDefault();
            if (settingsEl is not null)
            {
                bool B(string name) => string.Equals(settingsEl.Element(name)?.Value, "true", StringComparison.OrdinalIgnoreCase);
                int mode = (B("DisableShift") ? 0x1 : 0) | (B("DisableAltF4") ? 0x2 : 0)
                         | (B("DisableWin") ? 0x4 : 0) | (B("DisableAltTab") ? 0x8 : 0);
                string sp2 = $"settings.p{slot}.";
                _ev60Store.SetSetting(sp2 + "game_mode", mode.ToString());
                _ev60Store.SetSetting(sp2 + "indicator_led", B("EnableCoreLED") ? "1" : "0");

                // Keycap legends — see the Everest Max XML import for the
                // IsLayoutConfigured gate and why this key is not per-slot.
                if (B("IsLayoutConfigured")
                    && EverestKeyboardLayout.ParseStorageString(
                           settingsEl.Element("KeyboardLayout")?.Value) is { } impLayout)
                {
                    _ev60Store.SetSetting(EverestKeyboardLayout.LayoutSettingKey,
                                          EverestKeyboardLayout.ToStorageString(impLayout));
                    _ev60LayoutType = impLayout;
                    CbEv60KeyboardLayout.SelectedItem = (CbEv60KeyboardLayout.ItemsSource as Ev60LayoutChoice[])
                        ?.FirstOrDefault(x => x.Layout == impLayout) ?? CbEv60KeyboardLayout.SelectedItem;
                    BuildEverest60KeyboardOverlay();
                    ApplyEv60KeycapAppearanceToAllKeys();
                }
            }

            // K2-format extra: the whole per-profile Settings namespace (see
            // K2ProfileSettingsXml). Absent from Base Camp files and from K2 exports made
            // before 2026-08-22, in which case this is a no-op.
            int k2Settings = K2ProfileSettingsXml.Apply(
                root, _ev60Store.SetSetting, slot, K2ProfileSettingsXml.SettingsOnlyFamilies);
            if (k2Settings > 0) LogEverest60($"[IMP-XML] {k2Settings} K2 profile setting(s) restored");

            _ev60Store.SetProfileName(slot, profileName);
            _ev60Store.SetCurrentProfile(slot);
            Ev60RefreshProfiles();
            Ev60SelectProfileSlot(slot);
            Ev60ReloadProfile(slot);
            LogEverest60($"[IMP-XML] '{profileName}' -> slot {slot}: {imported} key(s)" +
                         (skippedKeys > 0 ? $", {skippedKeys} skipped (key not on this board)" : ""));
        }
        catch (Exception ex)
        {
            LogEverest60($"[ERR] import XML: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // Export profiles — Base Camp-compatible XML / K2-only XML, same shared
    // helper as Everest Max/MacroPad/DisplayPad/Makalu.
    // ------------------------------------------------------------

    private void BtnEv60ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var profiles = Enumerable.Range(1, 5)
            .Select(slot => (Slot: slot, Name: _ev60Store.GetProfileName(slot) ?? Loc.Get("profile_n", slot)))
            .ToList();
        int? currentSlot = LstEv60Profile.SelectedItem is Ev60ProfileItem pi ? pi.Slot : null;

        ExportProfileHelper.Run(
            owner: this,
            deviceLabel: "Everest60",
            profiles: profiles,
            currentSlot: currentSlot,
            exportOne: (slot, name, bcCompatible, path) =>
            {
                var result = bcCompatible
                    ? Ev60ProfileExporter.ExportBaseCamp(_ev60Store, slot, name, path)
                    : Ev60ProfileExporter.ExportK2(_ev60Store, slot, name, path);
                return (result.Exported, result.SkippedActions, result.SkipReasons);
            },
            log: LogEverest60,
            setStatus: LogEverest60);
    }

    /// <summary>
    /// Eagerly opens the Everest 60 SDK session with the real window handle
    /// and keeps it open across every 3s poll tick (2026-07-11, after a
    /// real-hardware report that numpad detection never worked), matching
    /// how Base Camp's own EV60MessageHandler keeps the driver open
    /// continuously rather than opening/closing per check. QueryNumpadPosition
    /// still falls back to its own brief open/close if this fails (e.g.
    /// device plugged in after startup) — see its doc comment. The same
    /// persistent session also backs the LED color poller (GetColorData2).
    /// <para>
    /// Called from <see cref="AutoOpenDrivers"/> (after <c>_hWnd</c> is a real
    /// handle from <see cref="OnSourceInitialized"/>), NOT from
    /// <see cref="InitEverest60Module"/> — 2026-07-12, real-hardware log showed
    /// OpenUSBDriver(IntPtr.Zero) called from the constructor (before the
    /// window has a real HWND) intermittently returns true, but APEnable/
    /// EnableKeyFunc/GetSubDeviceInfo return false on every single call
    /// afterwards. See Everest60SdkService.Open's doc comment for the full
    /// reasoning (SDK likely needs a real HWND to finish initializing its
    /// internal message pump, same reason MacroPad/DisplayPad pass their own
    /// real _hWnd to their OpenUSBDriver).
    /// </para>
    /// </summary>
    private void Ev60AutoOpen()
    {
        bool opened = false;
        try { opened = _ev60Sdk.Open(_hWnd, LogEverest60); } catch (Exception ex) { LogEverest60("[KeyBind] eager Open threw: " + ex); }
        UpdateEv60LedPreviewActive(ReferenceEquals(_activeEv60Section, Ev60RgbPanel));
        if (opened) Ev60KeyBindingPanel.Ev60ReloadKeyBindings(Ev60CurrentProfile());
    }

    // ------------------------------------------------------------
    // Interactive keyboard overlay (64 main-board keys, paintable) +
    // decorative-only numpad accessory (no known LED/remap protocol — see
    // Everest60KeyboardLayout.Numpad).
    // ------------------------------------------------------------

    private void BuildEverest60KeyboardOverlay()
    {
        CvsEv60Keyboard.Children.Clear();
        CvsEv60Numpad.Children.Clear();
        _ev60KeyVisuals.Clear();
        _ev60NumpadVisuals.Clear();
        _ev60OriginalKeyContent.Clear();
        var keyStyle = (Style)FindResource("EverestKeyStyle");

        foreach (var kd in Everest60KeyboardLayout.GetMainBoard(_ev60LayoutType))
        {
            var btn = new Button
            {
                Width = kd.W, Height = kd.H, Style = keyStyle,
                Content = BuildEv60KeyContent(kd),
                Tag = kd.MatrixId, // LED index 0-63
            };
            btn.Click += Ev60KeyboardButton_Click;
            btn.AllowDrop = true;
            btn.PreviewMouseLeftButtonDown += Ev60KeyboardButton_PreviewMouseLeftButtonDown;
            btn.PreviewMouseMove += Ev60KeyboardButton_PreviewMouseMove;
            btn.DragEnter += Ev60KeyButton_DragEnter;
            btn.DragLeave += Ev60KeyButton_DragLeave;
            btn.Drop += Ev60KeyboardButton_Drop;
            Canvas.SetLeft(btn, kd.X);
            Canvas.SetTop(btn, kd.Y);
            CvsEv60Keyboard.Children.Add(btn);

            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedHalo", btn) is Border halo)
            {
                _ev60KeyVisuals[kd.MatrixId] = new KeyVisual(btn, halo);
                if (btn.Content is FrameworkElement original)
                    _ev60OriginalKeyContent[kd.MatrixId] = original;
            }
        }

        foreach (var kd in Everest60KeyboardLayout.Numpad)
        {
            var btn = new Button
            {
                Width = kd.W, Height = kd.H, Style = keyStyle,
                Content = new TextBlock
                {
                    Text = kd.Label, Foreground = Brushes.White, FontSize = kd.W < 30 ? 6 : 8,
                    FontFamily = _evKeyFont,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
                Tag = kd.NumpadIndex, // 0-16, see KeyDef.NumpadIndex
            };
            // Click routes to Key Binding (SelectNumpadKey) or the per-key
            // keycap customizer (OpenEv60KeycapCustomizeDialog) depending on
            // active section — see Ev60NumpadButton_Click.
            btn.Click += Ev60NumpadButton_Click;
            btn.AllowDrop = true;
            btn.PreviewMouseLeftButtonDown += Ev60NumpadButton_PreviewMouseLeftButtonDown;
            btn.PreviewMouseMove += Ev60NumpadButton_PreviewMouseMove;
            btn.DragEnter += Ev60KeyButton_DragEnter;
            btn.DragLeave += Ev60KeyButton_DragLeave;
            btn.Drop += Ev60NumpadButton_Drop;
            Canvas.SetLeft(btn, kd.X);
            Canvas.SetTop(btn, kd.Y);
            CvsEv60Numpad.Children.Add(btn);

            // Numpad gets full Keycap Appearance now (base/text color + style
            // baseline + per-key color/image override) — see
            // ApplyEv60KeycapAppearanceToAllKeys. _ev60NumpadVisuals'
            // insertion order matches KeyDef.NumpadIndex 1:1 (same order as
            // Everest60Protocol.NumpadLedIndex, already relied on by the LED
            // preview below) so list index doubles as identity; original
            // content is captured under the SAME NumpadLedIndexBase-offset
            // key used for its keycap override row (Everest60Store's
            // KeycapOverrides table, shared with the main board's 0-63 —
            // same reuse pattern as the numpad's Key Binding LedIndex offset).
            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedHalo", btn) is Border halo)
            {
                _ev60NumpadVisuals.Add(new KeyVisual(btn, halo));
                if (btn.Content is FrameworkElement original)
                    _ev60OriginalKeyContent[Everest60Protocol.NumpadLedIndexBase + kd.NumpadIndex] = original;
            }
        }
    }

    // ------------------------------------------------------------
    // Border (side LED) squares — Custom Lighting system ported from Everest
    // Max 2026-07-24 (MainWindow.CustomLighting.cs's BuildBorderSquares/PlaceEdge
    // is the pattern this mirrors). Unlike that board, the 44 wire indices here
    // (Everest60Protocol.SideLedIndex) are ALREADY in physical clockwise order
    // starting above Esc (per that array's own doc comment) — no separate
    // "MainOrder" reorder table is needed, wire index == placement order.
    // Per-edge counts (16 top / 6 right / 16 bottom / 6 left) are a first-pass
    // proportional placement matching the board's aspect ratio (504x186 canvas),
    // the SAME caveat Everest Max's own BuildBorderSquares doc comment already
    // carries: total count (44) and starting point/direction are confirmed from
    // Everest60Protocol's doc comment, but the exact per-edge split has never
    // been individually verified against a physical capture (neither by K2 nor
    // by BaseCampLinux, which only ever drives this ring as one uniform color —
    // see its panel.py's "Side perimeter ring (44 LEDs) — single colour for
    // now" comment). Refine with a real per-square USB capture if this turns
    // out visually wrong on hardware.
    // ------------------------------------------------------------

    private const double Ev60BorderSz = 12, Ev60BorderGap = 2;

    private void BuildEv60BorderSquares()
    {
        CvsEv60BorderMain.Children.Clear();
        _ev60BorderButtons.Clear();

        const double bw = 504, bh = 186;
        double topY = -Ev60BorderGap - Ev60BorderSz, bottomY = bh + Ev60BorderGap;
        double leftX = -Ev60BorderGap - Ev60BorderSz, rightX = bw + Ev60BorderGap;
        int wire = 0;
        wire = PlaceEv60BorderEdge(CvsEv60BorderMain, _ev60BorderButtons, Ev60BorderSquare_Click,
            wire, 16, new Point(0, topY), new Point(bw - Ev60BorderSz, topY));
        wire = PlaceEv60BorderEdge(CvsEv60BorderMain, _ev60BorderButtons, Ev60BorderSquare_Click,
            wire, 6, new Point(rightX, 0), new Point(rightX, bh - Ev60BorderSz));
        wire = PlaceEv60BorderEdge(CvsEv60BorderMain, _ev60BorderButtons, Ev60BorderSquare_Click,
            wire, 16, new Point(bw - Ev60BorderSz, bottomY), new Point(0, bottomY));
        PlaceEv60BorderEdge(CvsEv60BorderMain, _ev60BorderButtons, Ev60BorderSquare_Click,
            wire, 6, new Point(leftX, bh - Ev60BorderSz), new Point(leftX, 0));
    }

    /// <summary>Border-square Button per wire index (0-21) for the numpad
    /// accessory's OWN perimeter ring — see
    /// <see cref="Everest60Protocol.NumpadSideLedIndex"/>'s doc comment for how
    /// the 22-LED count/clockwise-from-top-left order were confirmed
    /// 2026-07-24. Mirrors <see cref="_ev60BorderButtons"/>.</summary>
    private readonly Dictionary<int, Button> _ev60NumpadBorderButtons = new();

    /// <summary>Builds the 22 numpad-ring border squares (5 top / 6 right / 5
    /// bottom / 6 left — first-pass proportional placement for the 154x186
    /// numpad canvas, taller than wide, same caveat as
    /// <see cref="BuildEv60BorderSquares"/>'s own split). Rebuilt whenever the
    /// numpad's presence/side is (re)detected is NOT needed — the squares are
    /// positioned in the canvas's own local coordinate space, which doesn't
    /// change when <see cref="ApplyEv60NumpadPosition"/> moves/mirrors
    /// <see cref="GrdEv60NumpadColumn"/> — so this only needs to run once,
    /// same as <see cref="BuildEv60BorderSquares"/>.</summary>
    private void BuildEv60NumpadBorderSquares()
    {
        CvsEv60BorderNumpad.Children.Clear();
        _ev60NumpadBorderButtons.Clear();

        const double nw = 154, nh = 186;
        double topY = -Ev60BorderGap - Ev60BorderSz, bottomY = nh + Ev60BorderGap;
        double leftX = -Ev60BorderGap - Ev60BorderSz, rightX = nw + Ev60BorderGap;
        int wire = 0;
        wire = PlaceEv60BorderEdge(CvsEv60BorderNumpad, _ev60NumpadBorderButtons, Ev60NumpadBorderSquare_Click,
            wire, 5, new Point(0, topY), new Point(nw - Ev60BorderSz, topY));
        wire = PlaceEv60BorderEdge(CvsEv60BorderNumpad, _ev60NumpadBorderButtons, Ev60NumpadBorderSquare_Click,
            wire, 6, new Point(rightX, 0), new Point(rightX, nh - Ev60BorderSz));
        wire = PlaceEv60BorderEdge(CvsEv60BorderNumpad, _ev60NumpadBorderButtons, Ev60NumpadBorderSquare_Click,
            wire, 5, new Point(nw - Ev60BorderSz, bottomY), new Point(0, bottomY));
        PlaceEv60BorderEdge(CvsEv60BorderNumpad, _ev60NumpadBorderButtons, Ev60NumpadBorderSquare_Click,
            wire, 6, new Point(leftX, nh - Ev60BorderSz), new Point(leftX, 0));
    }

    /// <summary>Places <paramref name="count"/> squares evenly between
    /// <paramref name="p0"/> (first) and <paramref name="p1"/> (last), inclusive,
    /// starting at wire index <paramref name="startWire"/>, into
    /// <paramref name="target"/> (adding each Button to <paramref name="store"/>
    /// keyed by wire index). Shared by <see cref="BuildEv60BorderSquares"/>
    /// (main board ring) and <see cref="BuildEv60NumpadBorderSquares"/> (numpad
    /// ring). Returns the next unused wire index.</summary>
    private int PlaceEv60BorderEdge(Canvas target, Dictionary<int, Button> store, RoutedEventHandler clickHandler,
        int startWire, int count, Point p0, Point p1)
    {
        var squareStyle = (Style)FindResource("K2ColorSquareButton");
        for (int i = 0; i < count; i++)
        {
            double t = count > 1 ? (double)i / (count - 1) : 0;
            double x = p0.X + t * (p1.X - p0.X);
            double y = p0.Y + t * (p1.Y - p0.Y);
            int wireIdx = startWire + i;

            var btn = new Button
            {
                Width = Ev60BorderSz,
                Height = Ev60BorderSz,
                Style = squareStyle,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4F)),
                Background = Brushes.Transparent,
                Tag = wireIdx,
            };
            btn.Click += clickHandler;
            Canvas.SetLeft(btn, x);
            Canvas.SetTop(btn, y);
            target.Children.Add(btn);
            store[wireIdx] = btn;
        }
        return startWire + count;
    }

    private void Ev60BorderSquare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int wireIdx } btn) return;
        if (Ev60RgbPanel.TryPaintSide(wireIdx, out var color))
            btn.Background = new SolidColorBrush(color);
    }

    private void Ev60NumpadBorderSquare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int wireIdx } btn) return;
        if (Ev60RgbPanel.TryPaintNumpadRing(wireIdx, out var color))
            btn.Background = new SolidColorBrush(color);
    }

    /// <summary>Repaints every border square from Ev60RgbPanel's current paint
    /// state — called by ApplyEv60KeycapAppearanceToAllKeys (same "one place,
    /// many callers" pattern as the key/numpad repaint it already does). Only
    /// shows painted colors while Custom is active (see that method's
    /// IsPaintModeActive gate) — otherwise clears every square, since Custom
    /// mode owns this overlay exclusively.</summary>
    private void Ev60ReapplyBorderOverlays()
    {
        bool painting = Ev60RgbPanel.IsPaintModeActive;
        foreach (var (wireIdx, btn) in _ev60BorderButtons)
        {
            btn.Background = painting && Ev60RgbPanel.TryGetSideColor(wireIdx, out var c)
                ? new SolidColorBrush(c)
                : Brushes.Transparent;
        }
    }

    /// <summary>Repaints every numpad-ring border square from Ev60RgbPanel's
    /// current paint state — mirrors <see cref="Ev60ReapplyBorderOverlays"/>.</summary>
    private void Ev60ReapplyNumpadBorderOverlays()
    {
        bool painting = Ev60RgbPanel.IsPaintModeActive;
        foreach (var (wireIdx, btn) in _ev60NumpadBorderButtons)
        {
            btn.Background = painting && Ev60RgbPanel.TryGetNumpadRingColor(wireIdx, out var c)
                ? new SolidColorBrush(c)
                : Brushes.Transparent;
        }
    }

    /// <summary>Shows/hides the border-square overlay: visible only while the
    /// Lighting section is active AND Key Lighting's paint mode is checked —
    /// mirrors Everest Max's UpdateBorderOverlayVisibility. Also re-asserts the
    /// numpad gap (ApplyEv60NumpadGap), same "two callers, one source of truth"
    /// reasoning as Everest Max's ApplyNumpadGap (the 3s accessory poll would
    /// otherwise stomp a margin this sets).</summary>
    private void UpdateEv60BorderOverlayVisibility()
    {
        bool show = Ev60RgbPanel.IsPaintModeActive && ReferenceEquals(_activeEv60Section, Ev60RgbPanel);
        CvsEv60BorderMain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The numpad ring's 22 squares only make sense with a numpad accessory to
        // draw them around — gate on presence too, same as Everest Max's
        // CvsEvBorderNumpad/_evNumpadConnected. ApplyEv60NumpadPosition re-calls us
        // on attach/detach (see UpdateKeyboardLayout's Everest Max equivalent).
        CvsEv60BorderNumpad.Visibility = show && _ev60NumpadPosition != Ev60NumpadPosition.None
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyEv60NumpadGap();
    }

    /// <summary>Widens the gap between the keyboard and numpad canvases while
    /// the border overlay is showing (its squares extend Ev60BorderSz+Ev60BorderGap
    /// past the keyboard's right edge) — mirrors Everest Max's ApplyNumpadGap.
    /// Centralized here (not folded into ApplyEv60NumpadPosition) so both that
    /// method AND UpdateEv60BorderOverlayVisibility can call it without either
    /// one stomping the other's margin.</summary>
    private void ApplyEv60NumpadGap()
    {
        bool wide = CvsEv60BorderMain.Visibility == Visibility.Visible;
        double gap = wide ? 36 : 6; // wide = 24 + 50% (user request 2026-07-25)
        GrdEv60NumpadColumn.Margin = _ev60NumpadPosition == Ev60NumpadPosition.Left
            ? new Thickness(0, 0, gap, 0)
            : new Thickness(gap, 0, 0, 0);
    }

    /// <summary>
    /// Builds a main-board key's legend content — same rendering as Everest Max's
    /// board (<see cref="BuildEverestKeyboardOverlay"/> in MainWindow.Everest.cs:
    /// shared <c>_evKeyFont</c>/<c>_evBaseBrush</c>/<c>_evShiftBrush</c>/
    /// <c>_evAltGrBrush</c>/<see cref="BuildCornerLegend"/>/<see cref="BuildWinIcon"/>,
    /// all private members of this same partial class), bumped up one step in font
    /// size to read better on this board's slightly bigger single-legend keys (2026-07-19,
    /// user request: "increase the font a bit and use the same legends as Everest Max").
    /// AltGr/Shift corner legends come from the SAME <see cref="KeyLabelMap"/> Everest
    /// Max uses, keyed by VK code via <see cref="Everest60KeyboardLayout.LedIndexToVk"/>
    /// (this board's own MatrixId is the LED index, not a VK code — see that table's
    /// doc comment) — modifier/nav keys have no VK entry, so they always fall through
    /// to the plain single-legend case below, same as before.
    /// </summary>
    private FrameworkElement BuildEv60KeyContent(KeyDef kd)
    {
        double fs      = kd.W < 30 ? 7 : 9;   // single legend (Everest Max: 6/8)
        double fsMulti = kd.W < 30 ? 6 : 8;   // multi-legend  (Everest Max: 6/7)
        double fsBig   = fs + 1;

        if (kd.MatrixId == 56) // Win key ("⊞" placeholder in BuildMainBoard)
            return BuildWinIcon();

        if (kd.MatrixId == 14) // Tab: word + arrow, same as Everest Max
        {
            var tabSp = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            tabSp.Children.Add(new TextBlock
            {
                Text = "TAB", Foreground = Brushes.White, FontSize = fsMulti, FontFamily = _evKeyFont,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            tabSp.Children.Add(new TextBlock
            {
                Text = "⇆", Foreground = Brushes.White, FontSize = fsMulti + 1, FontFamily = _evKeyFont,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            return tabSp;
        }

        string? altLbl = null, altGrLbl = null, sAltGrLbl = null;
        if (Everest60KeyboardLayout.LedIndexToVk.TryGetValue(kd.MatrixId, out int vk))
        {
            altLbl    = KeyLabelMap.AltLabel(_ev60LayoutType, vk);
            altGrLbl  = KeyLabelMap.AltGrLabel(_ev60LayoutType, vk);
            sAltGrLbl = KeyLabelMap.ShiftAltGrLabel(_ev60LayoutType, vk);
        }

        if (altGrLbl is not null && (altLbl is not null || sAltGrLbl is not null))
            return BuildCornerLegend(kd.Label, altLbl, altGrLbl, sAltGrLbl, fsBig, fsBig);

        if (altGrLbl is not null)
        {
            var sp = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            sp.Children.Add(new TextBlock
            {
                Text = kd.Label, Foreground = Brushes.White, FontSize = fsBig, FontFamily = _evKeyFont,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = altGrLbl, Foreground = _evAltGrBrush, FontSize = fsMulti + 1, FontFamily = _evKeyFont,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            return sp;
        }

        if (altLbl is not null)
        {
            var sp = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            sp.Children.Add(new TextBlock
            {
                Text = altLbl, Foreground = _evShiftBrush, FontSize = fsMulti, FontFamily = _evKeyFont,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = kd.Label, Foreground = Brushes.White, FontSize = fs, FontFamily = _evKeyFont,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            });
            return sp;
        }

        return new TextBlock
        {
            Text = kd.Label, Foreground = Brushes.White, FontSize = fs, FontFamily = _evKeyFont,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ---- Layout selector (Settings' "Layout" combo) ------------------------

    private sealed record Ev60LayoutChoice(KeyboardLayoutType Layout, string Label)
    {
        // See EverestKeyboardLayout's LayoutChoice (MainWindow.Everest.cs) for
        // why ToString() mirrors Label: closed-ComboBox rendering fallback
        // when the ancestor is still Collapsed at ItemsSource-assignment time.
        public override string ToString() => Label;
    }

    private void InitEv60KeyboardLayoutSelector()
    {
        var choices = new[]
        {
            new Ev60LayoutChoice(KeyboardLayoutType.AnsiUs,    "English (US) — ANSI"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoUk,     "English (UK)"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoIt,     "Italian"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoDe,     "German (QWERTZ)"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoFr,     "French (AZERTY)"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoEs,     "Spanish"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoNordic, "Norwegian / Nordic"),
            new Ev60LayoutChoice(KeyboardLayoutType.IsoPt,     "Portuguese"),
        };
        CbEv60KeyboardLayout.ItemsSource       = choices;
        CbEv60KeyboardLayout.DisplayMemberPath = nameof(Ev60LayoutChoice.Label);
        CbEv60KeyboardLayout.SelectedItem      =
            System.Array.Find(choices, c => c.Layout == _ev60LayoutType) ?? choices[0];

        CbEv60KeyboardLayout.SelectionChanged += OnEv60KeyboardLayoutChanged;
    }

    /// <summary>Everest 60 twin of <see cref="LoadPersistedKeyboardLayout"/> — persisted
    /// choice first, Windows-locale guess as the fallback. Base Camp keeps this device's
    /// layout in <c>Everest60Settings.KeyboardLayout</c> (same vocabulary as the Max's
    /// <c>KeyboardSettings</c> row), host-side only: nothing about the layout is sent to
    /// the keyboard on either device — see the Max helper's doc comment for the two
    /// byte-identical BC captures that establish it.</summary>
    private KeyboardLayoutType LoadPersistedEv60KeyboardLayout()
    {
        try
        {
            if (EverestKeyboardLayout.ParseStorageString(
                    _ev60Store.GetSetting(EverestKeyboardLayout.LayoutSettingKey)) is { } stored)
                return stored;
        }
        catch (Exception ex) { LogEverest60("[Ev60] LoadPersistedEv60KeyboardLayout failed: " + ex); }
        return EverestKeyboardLayout.DetectLayout();
    }

    private void OnEv60KeyboardLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbEv60KeyboardLayout.SelectedItem is not Ev60LayoutChoice c) return;
        if (c.Layout == _ev60LayoutType) return;
        _ev60LayoutType = c.Layout;
        try
        {
            _ev60Store.SetSetting(EverestKeyboardLayout.LayoutSettingKey,
                                  EverestKeyboardLayout.ToStorageString(c.Layout));
        }
        catch (Exception ex) { LogEverest60("[Ev60] Saving keyboard layout failed: " + ex); }
        BuildEverest60KeyboardOverlay();
        ApplyEv60KeycapAppearanceToAllKeys();
    }

    /// <summary>Key click on the 64-key overlay: opens the Key Binding
    /// configure popup if that section is active (see below), paints the key
    /// if the Key Lighting section's paint mode is active (bridged via
    /// Ev60RgbPanel.TryPaintKey), no-op otherwise.</summary>
    private void Ev60KeyboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int ledIndex } btn) return;

        // Edit-individual-keycaps mode (Settings section): open the per-key color/image
        // customizer instead of anything else this click would normally do.
        if (_ev60KeycapEditMode && IsEv60AppearanceSectionActive)
        {
            string editLabel = (btn.Content as TextBlock)?.Text ?? $"#{ledIndex}";
            OpenEv60KeycapCustomizeDialog(ledIndex, editLabel);
            return;
        }

        // Key Binding section active: clicking a key selects it as the remap
        // source (Everest60KeyBindingPanel.SelectKey), instead of painting it.
        if (ReferenceEquals(_activeEv60Section, Ev60KeyBindingPanel))
        {
            string label = (btn.Content as TextBlock)?.Text ?? $"#{ledIndex}";
            Ev60KeyBindingPanel.SelectKey(ledIndex, label);
            return;
        }

        // Painted color is the "live" signal Keycap Appearance's style blends
        // with (see ApplyEv60KeyOverlay) — same role Everest Max's polled LED
        // tick plays, just discrete (on click) instead of continuous.
        if (Ev60RgbPanel.TryPaintKey(ledIndex, out var color) && _ev60KeyVisuals.TryGetValue(ledIndex, out var v))
            ApplyEv60KeyOverlay(v, color);
    }

    /// <summary>Key click on the 17-key numpad accessory overlay: opens the
    /// per-key keycap customizer (color/image override, same dialog as the
    /// main board — 2026-07-22) if "Edit individual keycaps" is active, the
    /// Key Binding remap source if that section is active, otherwise paints
    /// the key if Key Lighting's paint mode is on (2026-07-24 — the numpad's
    /// 17 keys share the same hardware address space
    /// Everest60Protocol.ReadColorData already reads live colors from, so
    /// writing them via the Custom-mode wire command is not a new guess, see
    /// Everest60Protocol.SendCustom's numpadColors doc comment).</summary>
    private void Ev60NumpadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int numpadIndex } btn) return;
        string label = (btn.Content as TextBlock)?.Text ?? $"#{numpadIndex}";

        if (_ev60KeycapEditMode && IsEv60AppearanceSectionActive)
        {
            OpenEv60KeycapCustomizeDialog(Everest60Protocol.NumpadLedIndexBase + numpadIndex, label);
            return;
        }

        if (ReferenceEquals(_activeEv60Section, Ev60KeyBindingPanel))
        {
            Ev60KeyBindingPanel.SelectNumpadKey(numpadIndex, label);
            return;
        }

        // Key Lighting paint mode: numpad keys are paintable too (Custom Lighting
        // port from Everest Max, 2026-07-24) — same offset-into-one-dictionary
        // convention as Key Binding's NumpadLedIndexBase reuse, see
        // Ev60RgbPanel._ev60CustomKeyColors' doc comment.
        int keyId = Everest60Protocol.NumpadLedIndexBase + numpadIndex;
        if (Ev60RgbPanel.TryPaintKey(keyId, out var color) &&
            numpadIndex >= 0 && numpadIndex < _ev60NumpadVisuals.Count)
            ApplyEv60KeyOverlay(_ev60NumpadVisuals[numpadIndex], color);
    }

    // ------------------------------------------------------------
    // Drag & drop — swap two keys' action (Key Binding section only), across
    // the main board AND the numpad accessory since both share one LedIndex
    // space in Everest60KeyBindingPanel/Everest60Store. Mirrors MainWindow.
    // Keys.cs's KeyButton_* (MacroPad), adapted for the sparse key dictionary
    // (see Everest60KeyBindingPanel.SwapKeys's doc comment) and for having two
    // physically distinct overlays (board Tag = LedIndex 0-63, numpad Tag =
    // NumpadIndex 0-16) feed the same drag payload.
    // ------------------------------------------------------------

    private readonly record struct Ev60DragPayload(int LedIndex, string Label);
    private const string Ev60KeyDragFormat = "K2.Ev60LedIndex";
    private Point _ev60DragStartPoint;
    private int? _ev60DragLed;
    private string? _ev60DragLabel;

    private void Ev60KeyboardButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: int ledIndex } btn) return;
        _ev60DragStartPoint = e.GetPosition(null);
        _ev60DragLed = ledIndex;
        _ev60DragLabel = (btn.Content as TextBlock)?.Text ?? $"#{ledIndex}";
    }

    private void Ev60NumpadButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: int numpadIndex } btn) return;
        _ev60DragStartPoint = e.GetPosition(null);
        _ev60DragLed = Everest60Protocol.NumpadLedIndexBase + numpadIndex;
        _ev60DragLabel = (btn.Content as TextBlock)?.Text ?? $"#{numpadIndex}";
    }

    private void Ev60KeyboardButton_PreviewMouseMove(object sender, MouseEventArgs e) => Ev60TryStartKeyDrag(sender, e);
    private void Ev60NumpadButton_PreviewMouseMove(object sender, MouseEventArgs e) => Ev60TryStartKeyDrag(sender, e);

    private void Ev60TryStartKeyDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _ev60DragLed is not int ledIndex) return;
        if (!ReferenceEquals(_activeEv60Section, Ev60KeyBindingPanel) ||
            !(Ev60KeyBindingPanel.ByLed(ledIndex)?.HasAction ?? false))
        {
            _ev60DragLed = null;
            return;
        }
        if (!DragDropHelper.ExceedsDragThreshold(_ev60DragStartPoint, e.GetPosition(null))) return;

        var payload = new Ev60DragPayload(ledIndex, _ev60DragLabel ?? $"#{ledIndex}");
        _ev60DragLed = null;
        DragDrop.DoDragDrop((Button)sender, new DataObject(Ev60KeyDragFormat, payload), DragDropEffects.Move);
    }

    private void Ev60KeyButton_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(Ev60KeyDragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, true);
    }

    private void Ev60KeyButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
    }

    private void Ev60KeyboardButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (!ReferenceEquals(_activeEv60Section, Ev60KeyBindingPanel)) return;
        if (sender is not Button { Tag: int ledIndex } targetBtn) return;
        if (e.Data.GetData(Ev60KeyDragFormat) is not Ev60DragPayload src) return;

        string targetLabel = (targetBtn.Content as TextBlock)?.Text ?? $"#{ledIndex}";
        Ev60KeyBindingPanel.SwapKeys(src.LedIndex, src.Label, ledIndex, targetLabel);
    }

    private void Ev60NumpadButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (!ReferenceEquals(_activeEv60Section, Ev60KeyBindingPanel)) return;
        if (sender is not Button { Tag: int numpadIndex } targetBtn) return;
        if (e.Data.GetData(Ev60KeyDragFormat) is not Ev60DragPayload src) return;

        int targetLed = Everest60Protocol.NumpadLedIndexBase + numpadIndex;
        string targetLabel = (targetBtn.Content as TextBlock)?.Text ?? $"#{numpadIndex}";
        Ev60KeyBindingPanel.SwapKeys(src.LedIndex, src.Label, targetLed, targetLabel);
    }

    // ─────────────────── Rectangular multi-LED selection ───────────────────
    // Drag a rubber-band square anywhere over the device box (main-board keys,
    // numpad keys, border squares, numpad-ring squares) to paint every LED it
    // touches with the brush color — ported from Everest Max's identical
    // feature (MainWindow.CustomLighting.cs's EvDeviceBox_*/PaintLedsInRect,
    // user request 2026-07-22 there), user request 2026-07-25 here. Wired to
    // BdrEv60DeviceBox's Preview mouse events (MainWindow.xaml) so the drag can
    // start on top of a key Button; a plain click (below the 5px threshold)
    // falls through to the normal single-key paint. Only engages while Key
    // Lighting's paint mode is active (Ev60RgbPanel.IsPaintModeActive) — same
    // gate BtnEv60Color/paint-click handlers implicitly rely on, so it can
    // never interfere with the Key Binding section's SelectKey click. It also
    // engages during Settings' "Edit individual keycaps" mode (user request
    // 2026-07-26): the same drag gesture instead collects every key the
    // rectangle touches and opens ONE KeycapCustomizeDialog applied to all of
    // them — see Ev60OpenKeycapDialogForRect. The two modes are mutually
    // exclusive (Lighting vs. Settings section), so only one gate is ever true.

    private Point _ev60RubberStart;
    private bool _ev60RubberTracking; // mouse down seen, watching for drag threshold
    private bool _ev60RubberActive;   // threshold passed, rubber band visible

    private void Ev60DeviceBox_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Ev60RgbPanel.IsPaintModeActive && !(_ev60KeycapEditMode && IsEv60AppearanceSectionActive)) return;
        _ev60RubberStart = e.GetPosition(CvsEv60RubberBand);
        _ev60RubberTracking = true;
        _ev60RubberActive = false;
    }

    private void Ev60DeviceBox_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_ev60RubberTracking) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            CancelEv60RubberBand();
            return;
        }
        var p = e.GetPosition(CvsEv60RubberBand);
        if (!_ev60RubberActive)
        {
            if (Math.Abs(p.X - _ev60RubberStart.X) < 5 && Math.Abs(p.Y - _ev60RubberStart.Y) < 5) return;
            _ev60RubberActive = true;
            RectEv60RubberBand.Visibility = Visibility.Visible;
            // Steal capture from whatever key Button the drag started on, so it
            // neither clicks on release nor keeps eating our move events.
            BdrEv60DeviceBox.CaptureMouse();
        }
        var r = new Rect(_ev60RubberStart, p);
        Canvas.SetLeft(RectEv60RubberBand, r.X);
        Canvas.SetTop(RectEv60RubberBand, r.Y);
        RectEv60RubberBand.Width  = r.Width;
        RectEv60RubberBand.Height = r.Height;
    }

    private void Ev60DeviceBox_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_ev60RubberTracking) return;
        bool wasActive = _ev60RubberActive;
        var rect = wasActive ? new Rect(_ev60RubberStart, e.GetPosition(CvsEv60RubberBand)) : Rect.Empty;
        CancelEv60RubberBand();
        if (!wasActive) return; // plain click: let the Button handle it normally
        e.Handled = true;       // suppress the click that would otherwise fire on release
        if (Ev60RgbPanel.IsPaintModeActive)
            Ev60PaintLedsInRect(rect);
        else if (_ev60KeycapEditMode && IsEv60AppearanceSectionActive)
            Ev60OpenKeycapDialogForRect(rect);
    }

    private void CancelEv60RubberBand()
    {
        _ev60RubberTracking = false;
        _ev60RubberActive = false;
        RectEv60RubberBand.Visibility = Visibility.Collapsed;
        if (BdrEv60DeviceBox.IsMouseCaptured) BdrEv60DeviceBox.ReleaseMouseCapture();
    }

    /// <summary>Paints every key Button (main board + numpad) and border/numpad-ring
    /// square whose on-screen bounds intersect <paramref name="rect"/>
    /// (CvsEv60RubberBand coordinate space, which spans the whole device box) —
    /// mirrors Everest Max's PaintLedsInRect, minus the per-effect Static-only
    /// gate on border squares (Ev60's Custom Lighting has no dynamic per-key
    /// effects, so its border squares are always paintable in paint mode).</summary>
    private void Ev60PaintLedsInRect(Rect rect)
    {
        int painted = 0;

        void TryPaintButton(Button btn, Action paint)
        {
            if (!btn.IsVisible) return;
            var bounds = btn.TransformToVisual(CvsEv60RubberBand)
                .TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));
            if (!rect.IntersectsWith(bounds)) return;
            paint();
            painted++;
        }

        foreach (var btn in CvsEv60Keyboard.Children.OfType<Button>())
        {
            if (btn.Tag is not int ledIndex) continue;
            TryPaintButton(btn, () =>
            {
                if (Ev60RgbPanel.TryPaintKey(ledIndex, out var color) && _ev60KeyVisuals.TryGetValue(ledIndex, out var v))
                    ApplyEv60KeyOverlay(v, color);
            });
        }

        foreach (var btn in CvsEv60Numpad.Children.OfType<Button>())
        {
            if (btn.Tag is not int numpadIndex) continue;
            TryPaintButton(btn, () =>
            {
                int keyId = Everest60Protocol.NumpadLedIndexBase + numpadIndex;
                if (Ev60RgbPanel.TryPaintKey(keyId, out var color) &&
                    numpadIndex >= 0 && numpadIndex < _ev60NumpadVisuals.Count)
                    ApplyEv60KeyOverlay(_ev60NumpadVisuals[numpadIndex], color);
            });
        }

        foreach (var kvp in _ev60BorderButtons)
        {
            var btn = kvp.Value;
            TryPaintButton(btn, () =>
            {
                if (Ev60RgbPanel.TryPaintSide(kvp.Key, out var color))
                    btn.Background = new SolidColorBrush(color);
            });
        }

        foreach (var kvp in _ev60NumpadBorderButtons)
        {
            var btn = kvp.Value;
            TryPaintButton(btn, () =>
            {
                if (Ev60RgbPanel.TryPaintNumpadRing(kvp.Key, out var color))
                    btn.Background = new SolidColorBrush(color);
            });
        }

        LogEverest60($"[CUSTOM] Rubber-band selection painted {painted} LED(s)");
    }

    // ============================================================
    // IActionHost adapter (delegates passed to Ev60ActionHost) +
    // physical key-press execution
    // ============================================================

    private IReadOnlyList<HostButton> Ev60GetButtons()
    {
        var keys = Ev60KeyBindingPanel.Keys;
        var list = new List<HostButton>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            list.Add(new HostButton(
                Index: i, KeyMatrix: k.LedIndex, HasImage: false, ImagePath: null,
                ActionType: k.ActionType, ActionValue: k.ActionValue));
        }
        return list;
    }

    private void Ev60PressButton(int index)
    {
        var keys = Ev60KeyBindingPanel.Keys;
        if (index >= 0 && index < keys.Count) ExecuteEv60Key(keys[index]);
    }

    private void ExecuteEv60Key(Ev60Key k) =>
        _ev60Engine?.Execute(k.ActionType, k.ActionValue, Ev60KeyBindingPanel.IndexOf(k));

    /// <summary>Physical key press/release, reported by the vendor SDK's
    /// callback (Everest60SdkService.KeyEvent) — mirrors MainWindow.Everest.cs's
    /// OnEverestKey/HandleEverestKey. See <see cref="_ev60WMatrixToLedIndex"/> for the
    /// wMatrix->ledIndex space this depends on.</summary>
    private void OnEv60Key(object? sender, (ushort WMatrix, bool Pressed, uint Id) e) =>
        Dispatcher.BeginInvoke(() => HandleEv60Key(e.WMatrix, e.Pressed));

    private void HandleEv60Key(ushort wMatrix, bool pressed)
    {
        if (AppSettings.LogLevel == K2LogLevel.Verbose)
            LogEverest60($"[KEY ] wMatrix=0x{wMatrix:X2} {(pressed ? "down" : "up")}");

        // Covers the accessory numpad too, not just the 64 main-board keys — the
        // callback reports both (see _ev60DllKeyIdToLedIndex's doc comment).
        int ledIndex = BaseCampDbImporter.Everest60LedIndexFromDllKeyId(wMatrix);
        if (ledIndex < 0)
        {
            if (pressed)
                LogEverest60($"[KEY ] wMatrix=0x{wMatrix:X2} is not a known DLLKeyId — press ignored");
            return;
        }
        HandleEv60KeyByLed(ledIndex, pressed);
    }

    /// <summary>Physical key press/release for the 64 main-board keys AND the 17-key
    /// numpad accessory, reported by <see cref="Services.RawEv60KeyWatcher"/> (Windows
    /// Raw Input, filtered to this keyboard's VID/PID) — see that class's doc comment for
    /// why this replaced the vendor SDK's KEY_CALLBACK as the actual working source:
    /// 2026-07-28, the callback never fired on real hardware even with APEnable/
    /// EnableKeyFunc both True, and a follow-up attempt to read the board's own raw HID
    /// reports directly hit ACCESS_DENIED (Windows reserves that collection for its own
    /// kbdhid driver) — Raw Input is called from MainWindow.xaml.cs's WndProc (already on
    /// the UI thread, no Dispatcher.BeginInvoke needed unlike a background-thread reader).
    /// The numpad accessory used to go through <see cref="Everest60NumpadKeyPoller"/>'s
    /// separate, much slower (100ms) Feature-Report polling instead — retired the same
    /// day once it became clear the accessory speaks the exact same standard
    /// boot-keyboard reports as the main board (user's own observation: typing on it is
    /// instant, so polling was never actually necessary), see
    /// <see cref="Everest60KeyboardLayout.ScanCodeToLedIndex"/>'s doc comment for the
    /// numpad's own scan codes.
    /// <paramref name="scanCode"/> is the PS/2 scan code (not VKey — see
    /// RawEv60KeyWatcher's doc comment for why: an ITA-layout key lighting up the wrong
    /// key on screen, user report 2026-07-28, was VKey's OS-translated locale-dependence
    /// leaking through).</summary>
    private void HandleEv60KeyFromHid(int scanCode, bool pressed)
    {
        if (AppSettings.LogLevel == K2LogLevel.Verbose)
            LogEverest60($"[KEY ] scanCode=0x{scanCode:X3} {(pressed ? "down" : "up")}");

        if (!Everest60KeyboardLayout.ScanCodeToLedIndex.TryGetValue(scanCode, out int ledIndex))
        {
            if (pressed)
                LogEverest60($"[KEY ] scanCode=0x{scanCode:X3} is not a known main-board key — press ignored");
            return;
        }
        HandleEv60KeyByLed(ledIndex, pressed);
    }

    /// <summary>Shared tail of <see cref="HandleEv60Key"/> (SDK wMatrix, currently dead —
    /// kept in case the callback ever does fire) and <see cref="HandleEv60KeyFromHid"/>
    /// (Raw Input, the verified-working path) once each has translated its own key
    /// identity down to a LED index.</summary>
    private void HandleEv60KeyByLed(int ledIndex, bool pressed)
    {
        _ev60AutoOffTimer?.RegisterActivity();

        // Physical-press highlight — re-enabled 2026-07-27, same reasoning as Everest
        // Max's EvHighlightKeyboardButton call (MainWindow.Everest.cs): the Tint overlay
        // never touches Background/BorderBrush, so it can't inherit MacroPad's "stuck
        // gray after release" bug (fixed the same day, see
        // ApplyMacroKeycapAppearanceToAllKeys in MainWindow.MacroKeycapAppearance.cs).
        // That reasoning only covers Background/BorderBrush though — SetLegendForeground
        // is written unconditionally by ApplyEv60KeyBaseline too (user report 2026-07-27:
        // "only the key's legend turns black, inconsistently"), so IsHighlighted below
        // gets the same race guard MacroPad's legend/background baseline already has
        // (ApplyEv60KeycapAppearanceToAllKeys's skip + this method's catch-up on release).
        Ev60HighlightKeyboardButton(ledIndex, pressed);

        if (pressed) _ev60PressedSince[ledIndex] = DateTime.UtcNow;
        else _ev60PressedSince.Remove(ledIndex);

        var key = Ev60KeyBindingPanel.ByLed(ledIndex);
        if (key is not null) key.IsHighlighted = pressed;

        if (pressed)
        {
            if (key is not null) ExecuteEv60KeyDeduped(key);
        }
        else
            // Picks up whatever keycap-appearance write may have landed (and been
            // skipped) while this key's IsHighlighted trigger was active — same
            // "stuck gray"-class fix as MacroPad's HandleKeyEvent (MainWindow.Keys.cs).
            // Unconditional even when this LED has no bound action at all (most keys,
            // on a fresh profile): user report 2026-07-28, "legend goes black after any
            // press, even with translucent legends on" — the old code returned early
            // right above for an unbound key, so this catch-up (the only thing that
            // ever wrote the correct translucent-aware color back) never ran, leaving
            // Ev60HighlightKeyboardButton's plain ResolveEv60KeycapTextColor() write
            // stuck forever instead of just for the instant before this call.
            ApplyEv60KeycapAppearanceToAllKeys();
    }

    /// <summary>Down-edge moment (UTC) of every key currently shown highlighted, keyed by
    /// ledIndex — watched by <see cref="Ev60ClearStaleHighlights"/> so a key-up the SDK
    /// callback fails to deliver (user report 2026-07-27: keys "remain as if pressed")
    /// can't leave a key's red tint stuck forever; nothing else clears it.</summary>
    private readonly Dictionary<int, DateTime> _ev60PressedSince = new();

    /// <summary>Force-releases the highlight of any key that's been "down" longer than a
    /// real press/hold ever plausibly lasts — called off the existing 3s status-poll tick.
    /// Cheap mitigation for a callback that can drop a key-up (can't be fixed host-side,
    /// same philosophy as the numpad presence grace period), not a real fix for the drop
    /// itself.</summary>
    private static readonly TimeSpan Ev60StaleHighlightTimeout = TimeSpan.FromSeconds(5);

    private void Ev60ClearStaleHighlights()
    {
        if (_ev60PressedSince.Count == 0) return;
        var now = DateTime.UtcNow;
        List<int>? stale = null;
        foreach (var (ledIndex, since) in _ev60PressedSince)
            if (now - since > Ev60StaleHighlightTimeout)
                (stale ??= new List<int>()).Add(ledIndex);
        if (stale is null) return;

        foreach (int ledIndex in stale)
        {
            LogEverest60($"[KEY ] led={ledIndex} highlight force-cleared — no key-up seen for {Ev60StaleHighlightTimeout.TotalSeconds:0}s");
            _ev60PressedSince.Remove(ledIndex);
            Ev60HighlightKeyboardButton(ledIndex, false);
            if (Ev60KeyBindingPanel.ByLed(ledIndex) is { } key) key.IsHighlighted = false;
        }
        ApplyEv60KeycapAppearanceToAllKeys();
    }

    /// <summary>Last (LED index, moment) actually executed, for
    /// <see cref="ExecuteEv60KeyDeduped"/>.</summary>
    private (int Led, DateTime At) _ev60LastExecuted = (-1, DateTime.MinValue);

    /// <summary>
    /// Runs a key's action at most once per physical press. Raw Input is now the only
    /// route for both main-board AND numpad keys (see
    /// <see cref="Everest60KeyboardLayout.ScanCodeToLedIndex"/>'s doc comment — the SDK
    /// callback never fires on real hardware, and <see cref="Everest60NumpadKeyPoller"/>
    /// was retired 2026-07-28), but the 400ms window stays: this device family has a
    /// confirmed firmware quirk where a single physical press can arrive as two distinct
    /// edges "a few milliseconds apart" instead of one clean one (same quirk
    /// <see cref="ExecuteEverestKeyDeduped"/> guards against on the Everest Max, and the
    /// numpad's own doc-commented "counter bumps twice per tap" behavior).
    /// </summary>
    private void ExecuteEv60KeyDeduped(Ev60Key key)
    {
        var now = DateTime.UtcNow;
        if (_ev60LastExecuted.Led == key.LedIndex
            && (now - _ev60LastExecuted.At) < TimeSpan.FromMilliseconds(400))
        {
            LogEverest60($"[KEY ] led={key.LedIndex} duplicate press ignored (other route already ran it)");
            return;
        }
        _ev60LastExecuted = (key.LedIndex, now);
        ExecuteEv60Key(key);
    }

    // ============================================================
    // "Disabled key" — the one binding that has to reach the firmware
    // ============================================================

    /// <summary>LED indices K2 currently holds switched off in the keyboard's firmware
    /// (main board only — a numpad key is already silenced by its own binding write).
    /// Tracked because a disable must be UNDONE when the key loses that binding or the
    /// profile changes, and the device won't tell us what we set.</summary>
    private readonly HashSet<int> _ev60FirmwareDisabledKeys = new();

    /// <summary>
    /// Reconciles the firmware state of the main-board keys with the current profile —
    /// the Everest 60 twin of <c>PushEvDisabledKeysToDevice</c> in MainWindow.Everest.cs,
    /// same reasoning, different protocol (see
    /// <see cref="Everest60Protocol.MainKeyBinding"/>). Numpad keys are skipped: any
    /// binding at all already stops them emitting.
    ///
    /// <para><b>Keys with an ordinary action are switched off too</b>, not just the ones
    /// bound to "disable" — otherwise pressing them runs the K2 action AND types the
    /// character (user report 2026-07-27). Unlike the Everest Max, which has a distinct
    /// "claimed by the host" value (0xC3) captured from Base Camp, no such value is known
    /// here: the Ev60 captures only ever showed Base Camp's Disable and Default writes,
    /// and inventing an action code for cmd 0x29 would be guessing the protocol. So this
    /// reuses the one confirmed silencing command and relies on the SDK key callback
    /// still reporting a firmware-disabled key — which is what makes the action run.
    /// <b>That last part is the unverified step</b>: if the callback goes quiet for
    /// disabled keys, such a key would neither type nor act, and the fix is a capture of
    /// Base Camp binding an action to an Ev60 main-board key (its cmd 0x29 action code is
    /// the missing piece). Nothing here is flash-persisted and removing the binding
    /// restores the key, so the experiment is cheap to undo.</para>
    /// </summary>
    private void PushEv60DisabledKeysToDevice()
    {
        var table = Everest60RemapData.LedIndexToDllKeyIdArray;
        var wanted = Ev60KeyBindingPanel.Keys
            .Where(k => k.NumpadIndex is null
                        && k.HasAction
                        && k.LedIndex >= 0 && k.LedIndex < table.Length)
            .Select(k => k.LedIndex)
            .ToHashSet();

        foreach (int led in _ev60FirmwareDisabledKeys.Except(wanted).ToList())
        {
            bool ok = _ev60.SetMainKeyDisabled(table[led], disabled: false);
            LogEverest60($"[KeyBind] key led={led} back to factory -> {ok}");
        }

        // Rewritten every time, not just when newly added — see the twin method's doc
        // comment in MainWindow.Everest.cs for why the set can't be trusted as device state.
        foreach (int led in wanted)
        {
            bool ok = _ev60.SetMainKeyDisabled(table[led], disabled: true);
            LogEverest60($"[KeyBind] key led={led} silenced in firmware -> {ok} " +
                         "(the action still runs off the SDK key callback)");
        }

        _ev60FirmwareDisabledKeys.Clear();
        _ev60FirmwareDisabledKeys.UnionWith(wanted);
    }

    /// <summary>Re-enables every main-board key K2 switched off, on shutdown — same
    /// reasoning as RestoreEvDisabledKeysOnExit (MainWindow.Everest.cs).</summary>
    private void RestoreEv60DisabledKeysOnExit()
    {
        var table = Everest60RemapData.LedIndexToDllKeyIdArray;
        foreach (int led in _ev60FirmwareDisabledKeys.ToList())
            try { _ev60.SetMainKeyDisabled(table[led], disabled: false); } catch { /* shutting down */ }
        _ev60FirmwareDisabledKeys.Clear();
    }

    /// <summary>Same "Tint" overlay approach as Everest Max's
    /// EvHighlightKeyboardButton (MainWindow.Everest.cs) — SetKeyTint/
    /// SetLegendForeground live in MainWindow.KeycapAppearance.cs and own
    /// Background/BorderBrush for the keycap-appearance system, so a plain
    /// assignment here would fight with custom color/live LED tint.</summary>
    private void Ev60HighlightKeyboardButton(int ledIndex, bool pressed)
    {
        KeyVisual v;
        // Numpad accessory keys live in a SEPARATE List<KeyVisual> indexed 0-16 (see
        // _ev60NumpadVisuals's doc comment), not in _ev60KeyVisuals (main board only,
        // keyed 0-63) — a plain _ev60KeyVisuals lookup for a NumpadLedIndexBase-offset
        // ledIndex always missed, silently breaking the numpad's highlight (user report
        // 2026-07-28, "il flash su numpad ancora non funziona").
        if (ledIndex >= Everest60Protocol.NumpadLedIndexBase)
        {
            int numpadIndex = ledIndex - Everest60Protocol.NumpadLedIndexBase;
            if (numpadIndex < 0 || numpadIndex >= _ev60NumpadVisuals.Count) return;
            v = _ev60NumpadVisuals[numpadIndex];
        }
        else if (!_ev60KeyVisuals.TryGetValue(ledIndex, out v!)) return;

        // Same red as MacroPad/Everest Max's press flash (user request 2026-07-27).
        SetKeyTint(v.Button, pressed ? new SolidColorBrush(Color.FromRgb(0x90, 0x00, 0x00)) : Brushes.Transparent);
        SetLegendForeground(v.Button, pressed ? Brushes.White : new SolidColorBrush(ResolveEv60KeycapTextColor()));
    }

    /// <summary>Current numpad position, updated by Ev60RefreshStatus's poll
    /// — auto-detected, no manual toggle. Position comes from raw HID
    /// (Everest60Service.TryGetNumpadPosition, 2026-07-25, replacing the SDK's
    /// GetSubDeviceInfo after it was found to reliably fail with a Makalu
    /// also connected — see Everest60Protocol.ReadNumpadPosition's doc
    /// comment); side (left/right) IS re-derived every poll straight from
    /// the wire, no fallback/assumption needed any more.</summary>
    private Ev60NumpadPosition _ev60NumpadPosition = Ev60NumpadPosition.None;

    /// <summary>Consecutive "not present" readings from <see cref="Everest60Service.TryGetNumpadPosition"/>.
    /// Debounced (hide only after 2 in a row) so a single blip doesn't
    /// flicker the UI — but see <see cref="_ev60NumpadPresenceGraceUntil"/>
    /// for the real fix: diagnostic logging on real hardware (2026-07-22,
    /// see CHANGELOG) confirmed this ISN'T a brief host-side race at all —
    /// after a Key Binding write, <c>cmd 0x20</c> genuinely, repeatedly reads
    /// an all-zero "not present" buffer for **~20+ consecutive seconds**
    /// (5+ clean, fast, correctly-echoed reads in a row, all "empty") before
    /// self-recovering, with the write's own <c>CommitKeyBinding</c> (cmd
    /// 0x2C) never getting a clean ack either (3 retries, stale echo every
    /// time). This is the firmware itself — likely the same long-standing,
    /// previously-reported "Ev60 numpad sometimes disappears, replug fixes
    /// it" hardware quirk, now confirmed to correlate with a Key Binding
    /// write specifically — nothing on the host side to fix. A 2-tick
    /// debounce alone doesn't cover 20+ seconds; presence coming back is
    /// never debounced — reconnecting (real or after the firmware settles)
    /// should still feel instant. Presence now comes from cmd 0x08 (see
    /// Everest60Protocol.ReadNumpadPosition), not the cmd 0x20 this account
    /// describes — whether 0x08 shares the same post-write stall is not yet
    /// confirmed, so this mitigation stays in place regardless.</summary>
    private int _ev60NumpadAbsentStreak;

    /// <summary>Suppresses hiding the numpad (but never suppresses showing
    /// it) until this time — set by <see cref="StartEv60NumpadPresenceGrace"/>
    /// right after any numpad Key Binding write, to ride out the firmware's
    /// own "not present" window documented on <see cref="_ev60NumpadAbsentStreak"/>
    /// instead of hiding the accessory for real. Each new write RENEWS this
    /// (confirmed working via a 2026-07-22 log: three writes ~4-14s apart
    /// each pushed the deadline out, keeping <c>grace=True</c> the whole
    /// time) — but a single 30s window turned out too short for that same
    /// back-to-back-writes case: the numpad was still reading absent 33s
    /// after the LAST write when the grace expired and it got hidden, with
    /// no sign of recovery yet in that log. Widened to 60s (evidence-based
    /// margin, not a guess pulled from nowhere: covers the single-write
    /// ~23s case with room to spare, and the observed 45s-and-still-absent
    /// multi-write case) — widen further if a future log still shows it
    /// hidden before recovering.</summary>
    private DateTime _ev60NumpadPresenceGraceUntil = DateTime.MinValue;

    private void StartEv60NumpadPresenceGrace() =>
        _ev60NumpadPresenceGraceUntil = DateTime.UtcNow.AddSeconds(60);

    /// <summary>Moves/mirrors/shows-or-hides CvsEv60Numpad for the given
    /// position. No separate right-side art exists (Base Camp itself only
    /// ships EV60_NumpadLeft.png) — "right" reuses the same flat panel image,
    /// mirrored via BrushEv60NumpadBg.RelativeTransform (flips only the
    /// image fill, not the Canvas or its child buttons — those would render
    /// backwards text if the whole Canvas were mirrored).</summary>
    private void ApplyEv60NumpadPosition(Ev60NumpadPosition position)
    {
        if (position == _ev60NumpadPosition) return;
        _ev60NumpadPosition = position;
        RefreshHomeTiles(); // Home tile artwork depends on numpad presence/side — see EvHomeImageFile's Ev60 counterpart

        if (position == Ev60NumpadPosition.None)
        {
            CvsEv60Numpad.Visibility = Visibility.Collapsed;
            UpdateEv60BorderOverlayVisibility(); // hides CvsEv60BorderNumpad too (gated on presence)
            return;
        }

        CvsEv60Numpad.Visibility = Visibility.Visible;
        BrushEv60NumpadBg.RelativeTransform = position == Ev60NumpadPosition.Right
            ? new ScaleTransform(-1, 1, 0.5, 0.5)
            : Transform.Identity;

        // Reorder within SpEv60Layout: numpad before the keyboard column for
        // "left", after it for "right". GrdEv60NumpadColumn/GrdEv60KeyColumn
        // (not the bare Canvases directly) are SpEv60Layout's actual children
        // now that both canvases share their Grid cell with a border-square
        // overlay — same indirection as Everest Max's GrdEvKeyColumn/GrdEvNumpadColumn.
        SpEv60Layout.Children.Remove(GrdEv60NumpadColumn);
        int keyboardIdx = SpEv60Layout.Children.IndexOf(GrdEv60KeyColumn);
        if (position == Ev60NumpadPosition.Left)
            SpEv60Layout.Children.Insert(keyboardIdx, GrdEv60NumpadColumn);
        else // Right
            SpEv60Layout.Children.Insert(keyboardIdx + 1, GrdEv60NumpadColumn);

        // Re-syncs the numpad-ring border overlay (attach/detach) and the gap —
        // UpdateEv60BorderOverlayVisibility ends with ApplyEv60NumpadGap itself.
        UpdateEv60BorderOverlayVisibility();
    }

    // ------------------------------------------------------------
    // Section navigation — toggles SecRgb/SecSideRing inside Ev60RgbPanel.
    // ------------------------------------------------------------

    private FrameworkElement? _activeEv60Section;

    /// <summary>Sets the default section AFTER InitializeComponent() has
    /// fully run — see the class doc comment for why this isn't
    /// IsChecked="True" in XAML. Key Binding (2026-07-13, user request —
    /// was Lighting until now, deliberately, to avoid eagerly opening the
    /// not-yet-hardware-verified SDK session; now confirmed working on
    /// real hardware, so this matches the Everest Max/MacroPad/Makalu
    /// convention of defaulting to Key Binding).</summary>
    private void InitEv60SectionNav() => RbEv60SecKeyBinding.IsChecked = true; // fires Ev60Section_Changed -> ShowEv60Section

    private void Ev60Section_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        // Ev60RgbPanel (preset effect + side ring + per-key custom lighting,
        // merged into one "Lighting" section — see Everest60RgbPanel.xaml),
        // PnlEv60Settings and Ev60KeyBindingPanel are siblings in the same
        // ScrollViewer; only one is visible at a time.
        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbEv60SecLighting)    => Ev60RgbPanel,
            nameof(RbEv60SecKeyBinding)  => Ev60KeyBindingPanel,
            nameof(RbEv60SecAppearance)  => PnlEv60Appearance,
            nameof(RbEv60SecSettings)    => PnlEv60Settings,
            _                            => null
        };

        if (panel is not null)
            ShowEv60Section(panel);

        // Key Binding needs the SDK DLL path (Everest360_USB.dll), NOT loaded
        // for the other two sections — opened lazily on first visit rather
        // than eagerly at startup (see _ev60Sdk's doc comment).
        if (rb.Name == nameof(RbEv60SecKeyBinding) && !_ev60Sdk.IsOpen)
        {
            bool ok = _ev60Sdk.Open(_hWnd, LogEverest60);
            LogEverest60($"[KeyBind] Everest60SdkService.Open -> {ok}");
            if (ok) Ev60KeyBindingPanel.Ev60ReloadKeyBindings(Ev60CurrentProfile());
        }

        // LED preview only makes sense while looking at Lighting — same
        // gating as Everest Max/MacroPad (MainWindow.LedPreview.cs).
        UpdateEv60LedPreviewActive(rb.Name == nameof(RbEv60SecLighting));

        // Border-square overlay (Key Lighting paint mode) only makes sense while
        // Lighting is the active section — leaving it hides the overlay/collapses
        // the numpad gap regardless of the paint-mode checkbox's own state, same
        // as Everest Max's ResetCustomLightingViewState.
        UpdateEv60BorderOverlayVisibility();
    }

    /// <summary>Starts/stops the live LED-color poller and, when deactivating,
    /// reverts every key to its painted/baseline appearance (no leftover live
    /// colors) — mirrors UpdateEverestLedPreviewActive/UpdateMpLedPreviewActive
    /// in MainWindow.LedPreview.cs. Unlike the old SDK-backed poller, this no
    /// longer needs an open SDK session (2026-07-13: color readback moved to
    /// raw HID, see Everest60LedColorPoller's doc comment) — the poller's own
    /// find-open-send-close cycle (Everest60Service.TryGetColorData) handles
    /// "device not connected yet" by itself, same as every other raw-HID call
    /// on this device.</summary>
    private void UpdateEv60LedPreviewActive(bool active)
    {
        if (_ev60LedPoller == null) return;
        if (active)
            _ev60LedPoller.Start();
        else
        {
            _ev60LedPoller.Stop();
            ApplyEv60KeycapAppearanceToAllKeys();
        }
    }

    /// <summary>Applies a live-polled LED color tick to every visible main-
    /// board key. <paramref name="colors"/> is indexed by firmware LED
    /// hardware address (see Everest60SdkNative.GetColorData2's doc comment),
    /// so each logical key (0-63, <c>_ev60KeyVisuals</c>'s key) is translated
    /// via <c>Everest60Protocol.LedIndex</c> — same indirection the write path
    /// (Everest60Protocol.SendCustom) already uses in reverse.
    /// <para>
    /// While Key Lighting's paint-mode checkbox is on, a key the user just
    /// painted but hasn't hit "Apply" for yet keeps showing its unsaved paint
    /// color instead of being immediately overwritten by the (still-old)
    /// hardware color on the next 300ms tick — same reasoning as MacroPad's
    /// IsHighlighted skip in MainWindow.LedPreview.cs, just for a paint
    /// preview instead of a physical key-press flash.
    /// </para></summary>
    private void OnEv60ColorsUpdated(EverestSdkNative.FWColor[] colors)
    {
        bool painting = Ev60RgbPanel.IsPaintModeActive;
        // "Off" is a distinct selectable effect (not just "no section active") —
        // the poll keeps running while the Lighting section is open regardless
        // of the selected effect, so without this the preview could still show
        // a stale/residual readback (previous effect's colors, or the firmware
        // not zeroing every address on "Off") even though the user picked "Off".
        bool effectOff = Ev60RgbPanel.IsEffectOff;
        foreach (var (ledIndex, v) in _ev60KeyVisuals)
        {
            if (painting && Ev60RgbPanel.TryGetPaintedColor(ledIndex, out var paintedColor))
            {
                ApplyEv60KeyOverlay(v, paintedColor);
                continue;
            }

            if (effectOff) { ApplyEv60KeyOverlay(v, null); continue; }

            if (ledIndex < 0 || ledIndex >= Everest60Protocol.LedIndex.Length) continue;
            int hwAddr = Everest60Protocol.LedIndex[ledIndex];
            if (hwAddr >= colors.Length) continue;

            var c = colors[hwAddr];
            // All-zero = LED off, same convention as Everest Max's
            // ApplyEverestLedColor (r/g/b all 0 rather than a "black lit" color).
            Color? live = c.r != 0 || c.g != 0 || c.b != 0 ? Color.FromRgb(c.r, c.g, c.b) : null;
            ApplyEv60KeyOverlay(v, live);
        }

        // Numpad accessory: live preview via Everest60Protocol.NumpadLedIndex
        // (reverse-engineered 2026-07-12 from a real USBPcap capture, see its doc
        // comment) — _ev60NumpadVisuals is built in the same order as
        // Everest60KeyboardLayout.Numpad, which NumpadLedIndex mirrors, so the two
        // lists are index-aligned. Paintable since the Custom Lighting port
        // (2026-07-24) — same unsaved-paint-preview precedence as the main board
        // above, keyed by Everest60Protocol.NumpadLedIndexBase + i.
        for (int i = 0; i < _ev60NumpadVisuals.Count && i < Everest60Protocol.NumpadLedIndex.Length; i++)
        {
            if (painting && Ev60RgbPanel.TryGetPaintedColor(Everest60Protocol.NumpadLedIndexBase + i, out var paintedNumpadColor))
            {
                ApplyEv60KeyOverlay(_ev60NumpadVisuals[i], paintedNumpadColor);
                continue;
            }

            if (effectOff) { ApplyEv60KeyOverlay(_ev60NumpadVisuals[i], null); continue; }

            int hwAddr = Everest60Protocol.NumpadLedIndex[i];
            if (hwAddr >= colors.Length) continue;
            var c = colors[hwAddr];
            Color? live = c.r != 0 || c.g != 0 || c.b != 0 ? Color.FromRgb(c.r, c.g, c.b) : null;
            ApplyEv60KeyOverlay(_ev60NumpadVisuals[i], live);
        }

        LogUnknownEv60LedAddresses(colors);
    }

    /// <summary>
    /// Diagnostic (2026-07-12): the numpad accessory has no known LED protocol
    /// (see Everest60KeyboardLayout's doc comment — never reverse-engineered,
    /// unlike the 64 main keys/side ring, which are covered by
    /// Everest60Protocol.KnownLedAddresses). Rather than guess a hardware
    /// address range for it (against CLAUDE.md's "don't guess the bit layout"
    /// rule), this logs any NON-zero color at an address the main
    /// board/side-ring don't claim — if the numpad's LEDs are visible in this
    /// same GetColorData2 readback at all, this reveals their real addresses
    /// from actual hardware instead of a guess. Logs at most once per second
    /// to stay readable.</summary>
    private DateTime _lastUnknownEv60LedLog = DateTime.MinValue;
    private void LogUnknownEv60LedAddresses(EverestSdkNative.FWColor[] colors)
    {
        if (DateTime.UtcNow - _lastUnknownEv60LedLog < TimeSpan.FromSeconds(1)) return;

        var hits = new List<string>();
        for (int addr = 0; addr < colors.Length; addr++)
        {
            if (Everest60Protocol.KnownLedAddresses.Contains((byte)addr)) continue;
            var c = colors[addr];
            if (c.r != 0 || c.g != 0 || c.b != 0)
                hits.Add($"{addr}=#{c.r:X2}{c.g:X2}{c.b:X2}");
        }
        if (hits.Count > 0)
        {
            _lastUnknownEv60LedLog = DateTime.UtcNow;
            LogEverest60($"[Ev60-DIAG] non-zero colors at UNKNOWN addresses (not main board/side ring): {string.Join(' ', hits)}");
        }
    }

    private void ShowEv60Section(FrameworkElement panel)
    {
        if (_activeEv60Section is not null)
            _activeEv60Section.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
        _activeEv60Section = panel;
    }

    // ------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------

    private void Ev60RefreshStatus()
    {
        bool wasConnected = _ev60Connected;
        bool connected = _ev60.IsConnected(out string model);
        _ev60Connected = connected;
        SetDeviceTabVisible(TabEverest60, connected);
        Ev60RgbPanel.SetConnected(connected);

        // Freshly plugged in: push the currently selected profile's lighting
        // so the keyboard reflects it even if it was switched while
        // disconnected (mirrors MainWindow.Makalu.cs's MkRefreshStatus).
        if (connected && !wasConnected)
            Ev60RgbPanel.Ev60ReloadProfile(Ev60CurrentProfile());
        LblEv60Status.Text = connected
            ? Loc.Get("ev60_status_connected", model)
            : Loc.Get("ev60_status_disconnected");
        LblEv60Status.Foreground = connected
            ? (Brush)FindResource("K2AccentBrush")
            : (Brush)FindResource("K2TextMutedBrush");

        // Retry the persistent SDK session if Ev60AutoOpen()'s single attempt
        // didn't land (2026-07-12 real-hardware log: OpenUSBDriver failed the
        // first 1-2 tries after a fresh connect/AutoStop-BaseCamp cycle, then
        // started succeeding reliably on later tries). Only needed for Key
        // Binding now — LED preview and numpad detection moved to raw HID
        // 2026-07-13 (see Everest60Protocol.ReadColorData/ReadNumpadPosition's
        // doc comments), so this retry no longer gates either of those.
        if (connected && !_ev60Sdk.IsOpen)
        {
            bool opened = _ev60Sdk.Open(_hWnd, LogEverest60);
            if (opened)
                Ev60KeyBindingPanel.Ev60ReloadKeyBindings(Ev60CurrentProfile());
        }
        // EnableKeyFunc (what actually makes the firmware report key presses) can still be
        // false even though OpenUSBDriver/IsOpen is true — Open() only attempts it once, and
        // a real-hardware log (2026-07-27) showed it losing that race for the WHOLE session
        // (main-board key presses never fired at all) when Everest Max/MacroPad/Ev60 all open
        // together at K2 startup. Keep retrying every tick until it lands — see
        // Everest60SdkService.EnsureKeyFuncEnabled's doc comment.
        else if (connected && _ev60Sdk.IsOpen && !_ev60Sdk.KeyFuncEnabled)
        {
            _ev60Sdk.EnsureKeyFuncEnabled(LogEverest60);
        }

        // Numpad auto-detect (2026-07-25, raw HID cmd 0x08 — see
        // Everest60Protocol.ReadNumpadPosition's doc comment): unlike the
        // opcode 0x20 presence check this replaced, the wire tells us the
        // side directly (None/Left/Right), confirmed against a real 8-step
        // attach/detach sequence — no more "assume Right" fallback needed.
        Ev60NumpadPosition numpadPos;
        if (!connected)
        {
            numpadPos = Ev60NumpadPosition.None;
            _ev60NumpadAbsentStreak = 0;
        }
        else
        {
            Ev60NumpadPosition? detected = _ev60.TryGetNumpadPosition();
            if (detected is Ev60NumpadPosition.Left or Ev60NumpadPosition.Right)
            {
                _ev60NumpadAbsentStreak = 0;
                numpadPos = detected.Value;
            }
            else
            {
                _ev60NumpadAbsentStreak++;
                bool inGrace = DateTime.UtcNow < _ev60NumpadPresenceGraceUntil;
                // Debounced (2-tick) normally; during the post-write grace
                // window (see _ev60NumpadPresenceGraceUntil) never hide at
                // all — the firmware's own ~20+s "not present" spell after a
                // binding write is expected, not a real disconnect. Not yet
                // confirmed whether cmd 0x08 even suffers that stall (see
                // ReadNumpadPosition's doc comment) — keep this mitigation
                // until a binding-write capture proves otherwise.
                numpadPos = (!inGrace && _ev60NumpadAbsentStreak >= 2) ? Ev60NumpadPosition.None : _ev60NumpadPosition;
            }
            LogEverest60($"[Ev60-NumpadTick] detected={detected?.ToString() ?? "null"} " +
                         $"streak={_ev60NumpadAbsentStreak} grace={DateTime.UtcNow < _ev60NumpadPresenceGraceUntil} -> pos={numpadPos}");
        }
        ApplyEv60NumpadPosition(numpadPos);
    }

    private void BtnEv60Refresh_Click(object sender, RoutedEventArgs e) => Ev60RefreshStatus();

    // ------------------------------------------------------------
    // Brightness — Slider lives in MainWindow's shared top-right bar
    // (BrEverest60), not in Ev60RgbPanel; same convention as Everest Max's
    // SldEvBrightness_ValueChanged.
    // ------------------------------------------------------------
    private void SldEv60Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEv60Brightness != null) LblEv60Brightness.Text = $"{(int)e.NewValue}%";
        // Null-guard: SldEv60Brightness lives in the shared top bar, declared in
        // MainWindow.xaml BEFORE Ev60RgbPanel (Everest 60 tab content further down
        // the same file). Its explicit Value="100" (default is 0) makes WPF fire
        // this handler synchronously during InitializeComponent(), before
        // Ev60RgbPanel has been constructed/assigned yet — same root cause as the
        // RbMkSecRgb/SldMkDpi crashes (see CHANGELOG 2026-07-10), just hit here via
        // a Slider.Value default-mismatch instead of RadioButton.IsChecked.
        Ev60RgbPanel?.SetBrightness(e.NewValue);
    }

    // ------------------------------------------------------------
    // Debug mode — driven centrally by the General Settings tab
    // (MainWindow.Settings.cs), see AppSettings.DebugMode. Mirrors
    // ApplyDebugMode (Everest)/ApplyMpDebugMode/ApplyDpDebugMode.
    // ------------------------------------------------------------
    private void ApplyEv60DebugMode(bool debug)
    {
        // Common actions: Debug group (Connected status + Refresh)
        PnlEv60DebugGroup.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------
    // Device rename (no per-device SQLite store for Everest 60 — see
    // AppSettings.Everest60DeviceName)
    // ------------------------------------------------------------

    private void BtnEv60Rename_Click(object sender, RoutedEventArgs e)
    {
        string current = AppSettings.Everest60DeviceName ?? (TabEverest60.Header as string) ?? Loc.Get("tab_everest60");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabEverest60.Header = name;
        AppSettings.SetEverest60DeviceName(name);
    }

    // ------------------------------------------------------------
    // Settings — Keycap Appearance (on-screen overlay only, cosmetic; see
    // MainWindow.KeycapAppearance.cs for the Everest Max equivalent, whose
    // KeycapStyleChoices/KeycapStyle/KeycapColorMode types are reused as-is
    // here). Imported from Everest Max 2026-07-11 (on request), including
    // KeycapStyle: the "live" color each style blends with is either the
    // polled hardware LED color (Everest60LedColorPoller, while the Lighting
    // section is visible — GetColorData2 found via decompile 2026-07-11) or,
    // as a fallback when the poll isn't running, the Key Lighting section's
    // painted per-key color (Ev60RgbPanel.TryGetPaintedColor) — see
    // ApplyEv60KeyOverlay/OnEv60ColorsUpdated. Applies to the numpad too
    // (always the "off" baseline there, since it's never painted or polled).
    // Persisted in Everest60Store's Settings k/v table (2026-07-13, moved from AppSettings for
    // consistency with Everest Max/MacroPad, which always used their own per-device store for
    // this — same key names as EverestStore/MacroPadStore's settings.keycap_* so the pattern is
    // identical across all 3 devices; only the per-key KeycapOverrides table predates this move).
    // ------------------------------------------------------------

    private bool _ev60SettingsSuppress = true; // default true — see Everest60RgbPanel's _ev60Suppress doc comment
    private KeycapColorMode _ev60KeycapColorMode = KeycapColorMode.Black;
    private string _ev60KeycapCustomHex = "#404040";
    private KeycapColorMode _ev60KeycapTextColorMode = KeycapColorMode.White;
    private string _ev60KeycapTextCustomHex = "#FFFFFF";
    private KeycapStyle _ev60KeycapStyleValue = KeycapStyle.Normal;

    /// <summary>"Translucent legends" checkbox — see the Everest Max equivalent
    /// (_evKeycapTranslucentLegend in MainWindow.KeycapAppearance.cs) for the full doc.</summary>
    private bool _ev60KeycapTranslucentLegend;

    /// <summary>Per-key color/image overrides — main board KeyId = LED index
    /// (same identity as _ev60KeyVisuals; Esc = EscKeyId = 0), see the
    /// Everest Max equivalent (_evKeycapOverrides in
    /// MainWindow.KeycapAppearance.cs) for the full doc. Also covers the
    /// numpad accessory (2026-07-22) at KeyId =
    /// <c>Everest60Protocol.NumpadLedIndexBase + NumpadIndex</c> — same
    /// disjoint-offset reuse of one shared table as the numpad's Key Binding
    /// LedIndex (Everest60Store's KeycapOverrides has no board discriminator
    /// column either).</summary>
    private readonly Dictionary<int, KeycapOverrideRecord> _ev60KeycapOverrides = new();

    /// <summary>"Edit individual keycaps" checkbox — see the Everest Max equivalent
    /// (_evKeycapEditMode in MainWindow.KeycapAppearance.cs) for the full doc.</summary>
    private bool _ev60KeycapEditMode;

    private void CkEv60KeycapEditMode_Click(object sender, RoutedEventArgs e) =>
        _ev60KeycapEditMode = CkEv60KeycapEditMode.IsChecked == true;

    /// <summary>True while the Everest 60 "Settings" section is active — gates whether clicking
    /// a key opens KeycapCustomizeDialog (only when "Edit individual keycaps" is also checked).</summary>
    private bool IsEv60AppearanceSectionActive => ReferenceEquals(_activeEv60Section, PnlEv60Appearance);

    /// <summary>Opens KeycapCustomizeDialog for the given key (KeyId = LED index) — see the
    /// Everest Max equivalent (OpenEvKeycapCustomizeDialog) for the full doc.</summary>
    private void OpenEv60KeycapCustomizeDialog(int keyId, string label)
    {
        _ev60KeycapOverrides.TryGetValue(keyId, out var current);
        var dlg = new KeycapCustomizeDialog(label, keyId == EscKeyId, current?.ColorHex, current?.ImagePath) { Owner = this };
        dlg.Changed += () =>
        {
            int profile = Ev60CurrentProfile();
            if (dlg.ColorHex is null && dlg.ImagePath is null)
            {
                _ev60Store.ClearKeycapOverride(profile, keyId);
                _ev60KeycapOverrides.Remove(keyId);
            }
            else
            {
                _ev60Store.SetKeycapOverride(profile, keyId, dlg.ColorHex, dlg.ImagePath);
                _ev60KeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
            }
            ApplyEv60KeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    /// <summary>Collects every main-board + numpad key Button whose on-screen bounds
    /// intersect <paramref name="rect"/> (CvsEv60RubberBand coordinate space) and opens
    /// ONE batch KeycapCustomizeDialog for them — called from Ev60DeviceBox_MouseUp when
    /// a rubber-band drag finishes while "Edit individual keycaps" is active (user request
    /// 2026-07-26). Iterates _ev60KeyVisuals/_ev60NumpadVisuals directly, same KeyId space
    /// Ev60KeyboardButton_Click/Ev60NumpadButton_Click already use for a single click.</summary>
    private void Ev60OpenKeycapDialogForRect(Rect rect)
    {
        var matches = new List<(int KeyId, string Label)>();

        bool Intersects(Button btn, out Rect bounds)
        {
            bounds = btn.TransformToVisual(CvsEv60RubberBand)
                .TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));
            return btn.IsVisible && rect.IntersectsWith(bounds);
        }

        foreach (var (ledIndex, v) in _ev60KeyVisuals)
        {
            if (!Intersects(v.Button, out _)) continue;
            matches.Add((ledIndex, (v.Button.Content as TextBlock)?.Text ?? $"#{ledIndex}"));
        }
        for (int i = 0; i < _ev60NumpadVisuals.Count; i++)
        {
            var v = _ev60NumpadVisuals[i];
            if (!Intersects(v.Button, out _)) continue;
            matches.Add((Everest60Protocol.NumpadLedIndexBase + i, (v.Button.Content as TextBlock)?.Text ?? $"#{i}"));
        }

        OpenEv60KeycapCustomizeDialogBatch(matches);
    }

    /// <summary>Opens a single key's dialog unchanged for a 1-key selection (identical to
    /// a plain click); for 2+ keys, opens ONE dialog (blank starting color/image) and
    /// applies whatever the user picks to every key in the selection — mirrors
    /// <see cref="OpenEv60KeycapCustomizeDialog"/>'s persistence, just looped.</summary>
    private void OpenEv60KeycapCustomizeDialogBatch(IReadOnlyList<(int KeyId, string Label)> keys)
    {
        if (keys.Count == 0) return;
        if (keys.Count == 1) { OpenEv60KeycapCustomizeDialog(keys[0].KeyId, keys[0].Label); return; }

        string label = Loc.Get("settings_keycap_edit_multi_label", keys.Count);
        var dlg = new KeycapCustomizeDialog(label, isEscKey: false, currentColorHex: null, currentImagePath: null) { Owner = this };
        dlg.Changed += () =>
        {
            int profile = Ev60CurrentProfile();
            foreach (var (keyId, _) in keys)
            {
                if (dlg.ColorHex is null && dlg.ImagePath is null)
                {
                    _ev60Store.ClearKeycapOverride(profile, keyId);
                    _ev60KeycapOverrides.Remove(keyId);
                }
                else
                {
                    _ev60Store.SetKeycapOverride(profile, keyId, dlg.ColorHex, dlg.ImagePath);
                    _ev60KeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
                }
            }
            ApplyEv60KeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    /// <summary>
    /// Key namespace for the Settings section — unconditionally per-profile (unlike
    /// Everest Max, Ev60 has no "sync across profiles" concept at all: lighting is
    /// already unconditionally per-profile here, see the class doc comment, so
    /// Settings follows the same philosophy rather than introducing a new toggle).
    /// User request 2026-07-25.
    /// </summary>
    private string Ev60SettingsPrefix() => $"settings.p{Ev60CurrentProfile()}.";

    private void InitEv60SettingsPanel()
    {
        _ev60SettingsSuppress = true;
        try
        {
            CbEv60KeycapStyle.ItemsSource  = KeycapStyleChoices;
            CbEv60KeycapStyle.ItemTemplate = (DataTemplate)FindResource("KeycapStyleItemTemplate");

            // Keycap Appearance is a cosmetic, device-wide preference, not per-profile
            // (user request 2026-08-22: split into its own Appearance section) — always the
            // fixed global "settings.keycap_*" namespace. Game Mode/Indicator LED below stay
            // per-profile via Ev60SettingsPrefix — this Get() only covers the keycap_* keys.
            string? Get(string key) => _ev60Store.GetSetting("settings." + key);

            _ev60KeycapColorMode = ParseKeycapColorMode(Get("keycap_color_mode"), KeycapColorMode.Black);
            _ev60KeycapCustomHex = Get("keycap_custom_hex") is { Length: > 0 } hex ? hex : "#404040";
            _ev60KeycapTextColorMode = ParseKeycapColorMode(Get("keycap_text_color_mode"), KeycapColorMode.White);
            _ev60KeycapTextCustomHex = Get("keycap_text_custom_hex") is { Length: > 0 } txt ? txt : "#FFFFFF";

            // Migration — see the Everest Max equivalent in LoadKeycapAppearanceFromStore
            // (MainWindow.KeycapAppearance.cs) for the full explanation of the old 4-value scheme.
            int rawStyle = int.TryParse(Get("keycap_style"), out var s) ? s : 0;
            if (Get("keycap_translucent_legend") is not { } translucentRaw)
            {
                _ev60KeycapTranslucentLegend = rawStyle == 1; // old Translucent
                _ev60KeycapStyleValue = rawStyle switch
                {
                    2 => KeycapStyle.Pudding,
                    3 => KeycapStyle.ReversePudding,
                    _ => KeycapStyle.Normal,
                };
                _ev60Store.SetSetting("settings.keycap_style", ((int)_ev60KeycapStyleValue).ToString());
                _ev60Store.SetSetting("settings.keycap_translucent_legend", _ev60KeycapTranslucentLegend ? "1" : "0");
            }
            else
            {
                _ev60KeycapTranslucentLegend = translucentRaw == "1";
                _ev60KeycapStyleValue = rawStyle is >= 0 and <= 2 ? (KeycapStyle)rawStyle : KeycapStyle.Normal;
            }
            CkEv60KeycapTranslucentLegend.IsChecked = _ev60KeycapTranslucentLegend;

            _ev60KeycapOverrides.Clear();
            foreach (var (keyId, rec) in _ev60Store.LoadAllKeycapOverrides(Ev60CurrentProfile()))
                _ev60KeycapOverrides[keyId] = rec;

            switch (_ev60KeycapColorMode)
            {
                case KeycapColorMode.White:  RbEv60KeycapWhite.IsChecked  = true; break;
                case KeycapColorMode.Custom: RbEv60KeycapCustom.IsChecked = true; break;
                default:                     RbEv60KeycapBlack.IsChecked = true; break;
            }
            BtnEv60KeycapCustomColor.IsEnabled = _ev60KeycapColorMode == KeycapColorMode.Custom;
            if (TryParseKeycapHexColor(_ev60KeycapCustomHex, out var custom))
                BtnEv60KeycapCustomColor.Background = new SolidColorBrush(custom);

            switch (_ev60KeycapTextColorMode)
            {
                case KeycapColorMode.Black:  RbEv60KeycapTextBlack.IsChecked  = true; break;
                case KeycapColorMode.Custom: RbEv60KeycapTextCustom.IsChecked = true; break;
                default:                     RbEv60KeycapTextWhite.IsChecked = true; break;
            }
            BtnEv60KeycapTextColor.IsEnabled = _ev60KeycapTextColorMode == KeycapColorMode.Custom;
            if (TryParseKeycapHexColor(_ev60KeycapTextCustomHex, out var textCustom))
                BtnEv60KeycapTextColor.Background = new SolidColorBrush(textCustom);

            int idx = (int)_ev60KeycapStyleValue;
            CbEv60KeycapStyle.SelectedIndex = idx >= 0 && idx < KeycapStyleChoices.Length ? idx : 0;

            // Game Mode / Core Indicator LED — ported from Everest Max, see the
            // XAML comment above these controls for why there's no ApplyToDevice call.
            // Still per-profile (unlike Keycap Appearance above) via Ev60SettingsPrefix, with
            // the same legacy-global fallback the combined Get() used to provide.
            string settingsPrefix = Ev60SettingsPrefix();
            string? GetSettings(string key) => _ev60Store.GetSetting(settingsPrefix + key) ?? _ev60Store.GetSetting("settings." + key);
            int mode = int.TryParse(GetSettings("game_mode"), out var m) ? m : 0;
            CkEv60GameModeShiftTab.IsChecked = (mode & 0x1) != 0;
            CkEv60GameModeAltF4.IsChecked    = (mode & 0x2) != 0;
            CkEv60GameModeWinKey.IsChecked   = (mode & 0x4) != 0;
            CkEv60GameModeAltTab.IsChecked   = (mode & 0x8) != 0;
            CkEv60CoreIndicatorLed.IsChecked = GetSettings("indicator_led") == "1";
        }
        finally { _ev60SettingsSuppress = false; }

        ApplyEv60KeycapAppearanceToAllKeys();
    }

    private void CbEv60KeycapStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ev60SettingsSuppress) return;
        if (CbEv60KeycapStyle.SelectedItem is not KeycapStyleChoice pick) return;
        _ev60KeycapStyleValue = pick.Style;
        _ev60Store.SetSetting("settings.keycap_style", ((int)pick.Style).ToString());
        ApplyEv60KeycapAppearanceToAllKeys();
    }

    /// <summary>Bit layout matches Everest Max's EvGameModeBitmask (MainWindow.Everest.cs):
    /// 0x1=DisableShift, 0x2=DisableAltF4, 0x4=DisableWin, 0x8=DisableAltTab.</summary>
    private int Ev60GameModeBitmask() =>
        (CkEv60GameModeShiftTab.IsChecked == true ? 0x1 : 0) |
        (CkEv60GameModeAltF4.IsChecked    == true ? 0x2 : 0) |
        (CkEv60GameModeWinKey.IsChecked   == true ? 0x4 : 0) |
        (CkEv60GameModeAltTab.IsChecked   == true ? 0x8 : 0);

    private void CkEv60GameMode_Click(object sender, RoutedEventArgs e)
    {
        if (_ev60SettingsSuppress) return;
        _ev60Store.SetSetting(Ev60SettingsPrefix() + "game_mode", Ev60GameModeBitmask().ToString());
    }

    private void CkEv60CoreIndicatorLed_Click(object sender, RoutedEventArgs e)
    {
        if (_ev60SettingsSuppress) return;
        _ev60Store.SetSetting(Ev60SettingsPrefix() + "indicator_led", CkEv60CoreIndicatorLed.IsChecked == true ? "1" : "0");
    }

    private void CkEv60KeycapTranslucentLegend_Click(object sender, RoutedEventArgs e)
    {
        if (_ev60SettingsSuppress) return;
        _ev60KeycapTranslucentLegend = CkEv60KeycapTranslucentLegend.IsChecked == true;
        _ev60Store.SetSetting("settings.keycap_translucent_legend", _ev60KeycapTranslucentLegend ? "1" : "0");
        ApplyEv60KeycapAppearanceToAllKeys();
    }

    private static KeycapColorMode ParseKeycapColorMode(string? stored, KeycapColorMode fallback) => stored switch
    {
        "black"  => KeycapColorMode.Black,
        "white"  => KeycapColorMode.White,
        "custom" => KeycapColorMode.Custom,
        _        => fallback,
    };

    private static string KeycapColorModeToString(KeycapColorMode mode) => mode switch
    {
        KeycapColorMode.White  => "white",
        KeycapColorMode.Custom => "custom",
        _                      => "black",
    };

    private static bool TryParseKeycapHexColor(string hex, out Color color)
    {
        try { color = (Color)ColorConverter.ConvertFromString(hex)!; return true; }
        catch { color = Colors.Transparent; return false; }
    }

    private void RbEv60KeycapColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_ev60SettingsSuppress) return;
        _ev60KeycapColorMode = sender == RbEv60KeycapWhite  ? KeycapColorMode.White
                              : sender == RbEv60KeycapCustom ? KeycapColorMode.Custom
                              :                                 KeycapColorMode.Black;
        _ev60Store.SetSetting("settings.keycap_color_mode", KeycapColorModeToString(_ev60KeycapColorMode));
        BtnEv60KeycapCustomColor.IsEnabled = _ev60KeycapColorMode == KeycapColorMode.Custom;
        ApplyEv60KeycapAppearanceToAllKeys();
    }

    private void RbEv60KeycapTextColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_ev60SettingsSuppress) return;
        _ev60KeycapTextColorMode = sender == RbEv60KeycapTextBlack  ? KeycapColorMode.Black
                                   : sender == RbEv60KeycapTextCustom ? KeycapColorMode.Custom
                                   :                                    KeycapColorMode.White;
        _ev60Store.SetSetting("settings.keycap_text_color_mode", KeycapColorModeToString(_ev60KeycapTextColorMode));
        BtnEv60KeycapTextColor.IsEnabled = _ev60KeycapTextColorMode == KeycapColorMode.Custom;
        ApplyEv60KeycapAppearanceToAllKeys();
    }

    private void BtnEv60KeycapCustomColor_Click(object sender, RoutedEventArgs e)
    {
        TryParseKeycapHexColor(_ev60KeycapCustomHex, out var current);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true, AnyColor = true, SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _ev60KeycapCustomHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        _ev60Store.SetSetting("settings.keycap_custom_hex", _ev60KeycapCustomHex);
        BtnEv60KeycapCustomColor.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));

        if (RbEv60KeycapCustom.IsChecked != true)
            RbEv60KeycapCustom.IsChecked = true; // RbEv60KeycapColor_Checked above calls ApplyEv60KeycapAppearanceToAllKeys
        else
            ApplyEv60KeycapAppearanceToAllKeys();
    }

    private void BtnEv60KeycapTextColor_Click(object sender, RoutedEventArgs e)
    {
        TryParseKeycapHexColor(_ev60KeycapTextCustomHex, out var current);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true, AnyColor = true, SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _ev60KeycapTextCustomHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        _ev60Store.SetSetting("settings.keycap_text_custom_hex", _ev60KeycapTextCustomHex);
        BtnEv60KeycapTextColor.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));

        if (RbEv60KeycapTextCustom.IsChecked != true)
            RbEv60KeycapTextCustom.IsChecked = true; // RbEv60KeycapTextColor_Checked above calls ApplyEv60KeycapAppearanceToAllKeys
        else
            ApplyEv60KeycapAppearanceToAllKeys();
    }

    private Color ResolveEv60KeycapColor() => _ev60KeycapColorMode switch
    {
        KeycapColorMode.White  => Color.FromRgb(0xE4, 0xE4, 0xE4),
        KeycapColorMode.Custom => TryParseKeycapHexColor(_ev60KeycapCustomHex, out var c) ? c : Color.FromRgb(0x40, 0x40, 0x40),
        _                      => Color.FromRgb(0x15, 0x15, 0x15),
    };

    private Color ResolveEv60KeycapTextColor() => _ev60KeycapTextColorMode switch
    {
        KeycapColorMode.Black  => Colors.Black,
        KeycapColorMode.Custom => TryParseKeycapHexColor(_ev60KeycapTextCustomHex, out var c) ? c : Colors.White,
        _                      => Colors.White,
    };

    /// <summary>
    /// Re-applies Keycap Appearance to every main-board AND numpad key: the
    /// static base/text color baseline (ApplyEv60KeyBaseline), then the
    /// style-dependent "live" overlay (ApplyEv60KeyOverlay) using each key's
    /// currently painted color if any — numpad keys are paintable too since
    /// the Custom Lighting port from Everest Max (2026-07-24, see
    /// Everest60Protocol.NumpadLedIndexBase's reuse in Ev60RgbPanel's
    /// _ev60CustomKeyColors). Mirrors Everest Max's two-phase
    /// ApplyKeycapAppearanceToAllKeys/ApplyEverestLedColor split
    /// (MainWindow.KeycapAppearance.cs), just triggered on-demand (paint
    /// click, settings change) instead of a continuous poll tick.
    /// </summary>
    private void ApplyEv60KeycapAppearanceToAllKeys()
    {
        var defaultKeycapBrush = new SolidColorBrush(ResolveEv60KeycapColor());
        var textBrush          = new SolidColorBrush(ResolveEv60KeycapTextColor());

        // Painted overlay only applies while Custom is the active effect
        // (2026-07-24: Custom is now mutually exclusive with the preset
        // effects, mirrors Everest Max) AND the Lighting section is the one
        // currently visible — CvsEv60Keyboard/CvsEv60Numpad are shared across
        // all three Ev60 sections (Key Binding/Lighting/Settings, see
        // MainWindow.xaml's BdrEv60DeviceBox), so without the section check
        // the simulated Custom-paint preview kept showing even after
        // switching away to Key Binding (user report 2026-07-27) — the live
        // LED-poll tick (OnEv60ColorsUpdated) already has this same section
        // gating via UpdateEv60LedPreviewActive starting/stopping the poller.
        bool lightingActive = ReferenceEquals(_activeEv60Section, Ev60RgbPanel);

        foreach (var (ledIndex, v) in _ev60KeyVisuals)
        {
            // A key currently mid-physical-press owns its legend color via
            // Ev60HighlightKeyboardButton; ApplyEv60KeyBaseline below writes
            // SetLegendForeground unconditionally, so without this skip a repaint
            // landing mid-press (settings change, profile switch, ...) resets the legend
            // back to the resting color while the red tint (a separate Background/Border
            // layer) stays untouched — user report 2026-07-27, "only the legend turns
            // black, inconsistently". Same "stuck gray"-class race MacroPad's
            // ApplyMacroKeycapAppearanceToAllKeys already guards against. Skipped keys are
            // caught up the instant they're released, see HandleEv60KeyByLed.
            // Keyed off _ev60PressedSince (every currently-pressed LED, bound or not) rather
            // than Ev60KeyBindingPanel.ByLed(...)?.IsHighlighted — that dictionary only ever
            // tracks keys with an assigned action, so an unbound key (most of them, on a
            // fresh profile) was never skipped here and could still race the same way.
            if (_ev60PressedSince.ContainsKey(ledIndex)) continue;

            _ev60KeycapOverrides.TryGetValue(ledIndex, out var ov);
            var keycapBrush = ov?.ColorHex is { Length: > 0 } hex && TryParseKeycapHexColor(hex, out var c2)
                ? new SolidColorBrush(c2)
                : defaultKeycapBrush;

            ApplyEv60KeyBaseline(v, keycapBrush, textBrush);
            Color? painted = lightingActive && Ev60RgbPanel.IsPaintModeActive &&
                Ev60RgbPanel.TryGetPaintedColor(ledIndex, out var c) ? c : null;
            ApplyEv60KeyOverlay(v, painted);

            _ev60OriginalKeyContent.TryGetValue(ledIndex, out var original);
            ApplyKeycapImageOverride(v.Button, original, ov?.ImagePath);
        }
        for (int npi = 0; npi < _ev60NumpadVisuals.Count; npi++)
        {
            int keyId = Everest60Protocol.NumpadLedIndexBase + npi;
            // Same skip as the main-board loop above — the numpad accessory shares the
            // same Ev60Key/highlight machinery (HandleEv60Key/Ev60HighlightKeyboardButton
            // key both main-board and numpad presses by this same ledIndex space).
            if (_ev60PressedSince.ContainsKey(keyId)) continue;

            var v = _ev60NumpadVisuals[npi];
            _ev60KeycapOverrides.TryGetValue(keyId, out var nov);
            var numpadKeycapBrush = nov?.ColorHex is { Length: > 0 } nhex && TryParseKeycapHexColor(nhex, out var nc)
                ? new SolidColorBrush(nc)
                : defaultKeycapBrush;

            ApplyEv60KeyBaseline(v, numpadKeycapBrush, textBrush);
            Color? numpadPainted = lightingActive && Ev60RgbPanel.IsPaintModeActive &&
                Ev60RgbPanel.TryGetPaintedColor(keyId, out var npc) ? npc : null;
            ApplyEv60KeyOverlay(v, numpadPainted);

            _ev60OriginalKeyContent.TryGetValue(keyId, out var noriginal);
            ApplyKeycapImageOverride(v.Button, noriginal, nov?.ImagePath);
        }

        Ev60ReapplyBorderOverlays();
        Ev60ReapplyNumpadBorderOverlays();
    }

    /// <summary>Sets the static (non-painted) part of a key's appearance:
    /// Background/BorderBrush per KeycapStyle (Mount mirrors BorderBrush via
    /// TemplateBinding) and legend color — same layout as Everest Max's
    /// ApplyKeycapAppearanceToAllKeys inner switch.</summary>
    private void ApplyEv60KeyBaseline(KeyVisual v, Brush keycapBrush, Brush textBrush)
    {
        var ledOffBrush = new SolidColorBrush(LedOffColor);
        switch (_ev60KeycapStyleValue)
        {
            case KeycapStyle.Pudding:
                SetKeyBackground(v.Button, keycapBrush);
                SetKeyBorderBrush(v.Button, ledOffBrush);
                break;
            case KeycapStyle.ReversePudding:
                SetKeyBackground(v.Button, ledOffBrush);
                SetKeyBorderBrush(v.Button, keycapBrush);
                break;
            default: // Normal
                SetKeyBackground(v.Button, keycapBrush);
                SetKeyBorderBrush(v.Button, keycapBrush);
                break;
        }
        v.Halo.Background = Brushes.Transparent;
        SetLegendForeground(v.Button, _ev60KeycapTranslucentLegend ? Brushes.White : textBrush);
    }

    /// <summary>Applies the "live" overlay for a single key — the painted
    /// custom-lighting color if any, routed to the visual element that
    /// matches the current KeycapStyle (same routing as Everest Max's
    /// ApplyEverestLedColor, "painted" standing in for "live LED tick").</summary>
    private void ApplyEv60KeyOverlay(KeyVisual v, Color? painted)
    {
        bool lit = painted.HasValue;
        var paintBrush = lit ? new SolidColorBrush(painted!.Value) : null;

        switch (_ev60KeycapStyleValue)
        {
            case KeycapStyle.Pudding:
                SetKeyBorderBrush(v.Button, paintBrush ?? new SolidColorBrush(LedOffColor));
                break;
            case KeycapStyle.ReversePudding:
                SetKeyBackground(v.Button, paintBrush ?? new SolidColorBrush(LedOffColor));
                break;
            default: // Normal — Pudding/ReversePudding already visualize the color via border/center.
                v.Halo.Background = lit ? new SolidColorBrush(Color.FromArgb(160, painted!.Value.R, painted.Value.G, painted.Value.B)) : Brushes.Transparent;
                break;
        }

        if (_ev60KeycapTranslucentLegend)
            SetLegendForeground(v.Button, paintBrush ?? Brushes.White);
    }

    // ------------------------------------------------------------
    // Log
    // ------------------------------------------------------------

    private void LogEverest60(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        App.WriteLog("[Everest60] " + text);
    }
}
