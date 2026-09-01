using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: Makalu 67/Max mouse tab shell — sidebar, device image
/// + clickable hotspots (plus the LED ring preview drawn over the wheel/DPI
/// button, software-only since this device has no HID readback), right
/// column, section navigation. Section CONTENT (Lighting+Settings+DPI,
/// Key Binding) lives in two UserControls (<see cref="MakaluRgbSettingsPanel"/>,
/// <see cref="MakaluDpiRemapPanel"/>) wired here as direct children of
/// MainWindow.xaml (not nested inside another custom control — see
/// MakaluDpiRemapPanel.xaml for why).
///
/// RbMkSecRemap.IsChecked is set here in <see cref="InitMkSectionNav"/>, NOT
/// via IsChecked="True" in XAML — that used to null-ref inside
/// MkSection_Changed, because WPF fires RadioButton.Checked synchronously
/// the instant BAML sets IsChecked="True", which happens mid-
/// InitializeComponent(), before MkRgbSettings/MkDpiRemap (declared later
/// in MainWindow.xaml) are assigned. Root-caused with WinDbg+SOS
/// 2026-07-10 — see CHANGELOG.md for the full session. This was never a
/// JIT/CLR bug.
/// </summary>
public partial class MainWindow
{
    private MakaluService _makalu = null!;
    private MakaluStore _mkStore = null!;
    private DispatcherTimer? _mkPollTimer;
    private bool _mkConnected;
    private MakaluService.DeviceInfo _mkInfo =
        new(MakaluService.Model.Makalu67, "Makalu 67", 6, MakaluProtocol.DpiMin67);
    private bool _mkSuppressProfile;

    /// <summary>Background reader for the DPI button's physical press (see
    /// <see cref="MakaluDpiButtonWatcher"/> — the mouse's other 5 buttons come through
    /// RawMouseActivityWatcher/OnMakaluRawButton instead, this one has no OS mouse-click
    /// identity at all).</summary>
    private MakaluDpiButtonWatcher? _mkDpiWatcher;

    /// <summary>One-shot flash timer for the DPI hotspot — that button has no distinct
    /// release edge (see MakaluDpiButtonWatcher's class doc), so its highlight is timed
    /// instead of press/release like the other 5.</summary>
    private DispatcherTimer? _mkDpiFlashTimer;

    /// <summary>Called once from the MainWindow constructor.</summary>
    private void InitMakaluModule()
    {
        _makalu = new MakaluService(LogMakalu);
        _mkStore = new MakaluStore();

        MkRgbSettings.Init(_makalu, LogMakalu, _mkStore, MkCurrentProfile);
        MkDpiRemap.Init(_makalu, LogMakalu, _mkStore, MkCurrentProfile);
        MkRgbSettings.PreviewChanged += MkUpdateLedRingPreview;
        BuildMkHotspots();
        InitMkSectionNav();

        // Hotspot dot outlines are painted once in BuildMkHotspots (FindResource, not a
        // live binding) — without this, a Settings > Accent color switch leaves them the
        // old color until the device image is rebuilt (reconnect/model change), which
        // reads as "doesn't recolor until I restart".
        AccentCatalog.Applied += RefreshMkHotspotAccentColors;

        // _log runs on the watcher's OWN background read thread (unlike _makalu/
        // MkRgbSettings/MkDpiRemap's LogMakalu above, only ever invoked from UI-thread
        // event handlers) — must marshal here too, same as DpiPressed below, or
        // TxtMkLog.AppendText crashes the whole app with a cross-thread
        // InvalidOperationException the moment the read loop logs anything (found
        // 2026-07-27 while smoke-testing against a real Makalu: every launch crashed
        // within ~1 minute, right as ReadLoop logged "read thread exiting").
        _mkDpiWatcher = new MakaluDpiButtonWatcher(msg => Dispatcher.BeginInvoke(() => LogMakalu(msg)));
        _mkDpiWatcher.DpiPressed += () => Dispatcher.BeginInvoke(OnMakaluDpiPressed);
        _mkDpiWatcher.ButtonEvent += (cat, btn) => Dispatcher.BeginInvoke(() => OnMakaluButtonEvent(cat, btn));

        LstMkProfile.ContextMenu = WithProfileGuide(MkBuildProfileContextMenu(), "makalu");
        BtnMkProfileMenu.ContextMenu = WithProfileGuide(MkBuildProfileMenuNoEdit(), "makalu");
        MkRefreshProfiles();
        MkReloadProfile(MkCurrentProfile());

        _mkPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mkPollTimer.Tick += (_, _) => MkRefreshStatus();
        _mkPollTimer.Start();
        MkRefreshStatus();
    }

    // ------------------------------------------------------------
    // Profile management — Makalu has no firmware profile concept (raw HID,
    // no SwitchProfile-equivalent, see architectural note in _PROJECT_MAP.md):
    // a "profile" is purely a K2-side slot (1..5, same count as every other
    // device), persisted in MakaluStore. Switching means re-sending the
    // stored lighting/DPI/remap/settings to the device — see
    // MakaluRgbSettingsPanel.MkReloadProfile / MakaluDpiRemapPanel.MkReloadRemap.
    // Mirrors LstEvProfile_SelectionChanged/EvRefreshProfiles/EvSelectProfileSlot
    // in MainWindow.Everest.cs.
    // ------------------------------------------------------------

    private sealed record MkProfileItem(int Slot, string Label)
    {
        // "+ New profile" row, same convention as Ev/MpProfileItem — user request
        // 2026-07-29: only list slots actually configured, not all 5 fixed ones
        // unconditionally (previously always real/never "new" here).
        public bool IsNew => Label.StartsWith("+");
        public bool IsRealProfile => !IsNew;
        public override string ToString() => Label;
    }

    private int MkCurrentProfile()
        => LstMkProfile.SelectedItem is MkProfileItem pi ? pi.Slot : 1;

    /// <summary>Populates the Makalu profile list with configured profiles + "New
    /// profile…" (5 fixed K2-side slots exist regardless — see MakaluStore's doc
    /// comment — but the UI only lists the ones actually in use, same as
    /// Everest/MacroPad/DisplayPad, since 2026-07-29).</summary>
    private void MkRefreshProfiles()
    {
        _mkSuppressProfile = true;
        try
        {
            var existing = _mkStore.GetExistingProfiles();
            if (existing.Count == 0)
            {
            // No profile at all — fresh install, hardware factory reset or the Settings
            // tab's "Restore all defaults": recreate one instead of only showing a
            // phantom slot 1 under the generic "Profile 1" label. Named "Default
            // profile" (localized, `default_profile_name`), the same name Base Camp
            // gives its own starting profile. User request 2026-08-21.
                _mkStore.SetProfileName(1, Loc.Get("default_profile_name"));
                _mkStore.MarkProfileExists(1);
                existing.Add(1);
            }
            var items = new List<MkProfileItem>();
            foreach (var slot in existing)
                items.Add(new MkProfileItem(slot, _mkStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot)));

            int nextFree = Enumerable.Range(1, 5).FirstOrDefault(s => !existing.Contains(s));
            if (nextFree > 0)
                items.Add(new MkProfileItem(nextFree, Loc.Get("new_profile")));

            LstMkProfile.ItemsSource = items;

            int current = _mkStore.GetCurrentProfile();
            var match = items.Find(x => x.Slot == current && !x.IsNew);
            LstMkProfile.SelectedItem = match ?? items[0];

            MkRegisterProfileLaunchWatchers(existing);
        }
        finally { _mkSuppressProfile = false; }
    }

    /// <summary>Registers this device's profiles with K2.Core.Services.ProfileLaunchWatcher
    /// — see DpRegisterProfileLaunchWatchers (MainWindow.DisplayPad.cs) for the shared
    /// pattern/rationale.</summary>
    private void MkRegisterProfileLaunchWatchers(List<int> existing)
    {
        const string scope = "Mk:";
        var currentKeys = new HashSet<string>();
        foreach (var slot in existing)
        {
            string kb = $"profile.{slot}";
            string? exe = _mkStore.GetSetting($"{kb}.launchExe");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string key = scope + slot;
            currentKeys.Add(key);
            int capturedSlot = slot;
            bool focusOnly = _mkStore.GetSetting($"{kb}.launchFocusOnly") == "1";
            bool restoreOnClose = _mkStore.GetSetting($"{kb}.launchRestoreOnClose") == "1";
            ProfileLaunchWatcher.Instance.UpdateRegistration(key, exe, focusOnly, restoreOnClose,
                capturedSlot.ToString(),
                () => _mkStore.GetCurrentProfile().ToString(),
                t => MkSwitchProfileTo(int.Parse(t)));
        }
        foreach (var staleKey in ProfileLaunchWatcher.Instance.KeysWithPrefix(scope).Except(currentKeys))
            ProfileLaunchWatcher.Instance.RemoveRegistration(staleKey);
    }

    private void MkSelectProfileSlot(int slot)
    {
        _mkSuppressProfile = true;
        try
        {
            if (LstMkProfile.ItemsSource is List<MkProfileItem> items)
                LstMkProfile.SelectedItem = items.Find(x => x.Slot == slot && !x.IsNew) ?? items[0];
        }
        finally { _mkSuppressProfile = false; }
    }

    /// <summary>Pushes the given profile's stored lighting/DPI/settings/remap
    /// into both panels and re-applies them to hardware (if connected).</summary>
    private void MkReloadProfile(int slot)
    {
        MkRgbSettings.MkReloadProfile(slot);
        MkDpiRemap.MkReloadRemap(slot);
    }

    private void LstMkProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mkSuppressProfile) return;
        if (LstMkProfile.SelectedItem is not MkProfileItem pi) return;

        if (pi.IsNew)
        {
            // Create empty profile (mirrors LstEvProfile_SelectionChanged) — no
            // placeholder Remap/Lighting rows needed, MkReloadProfile below already
            // falls back to defaults for a slot with no saved state.
            _mkStore.MarkProfileExists(pi.Slot);
            LogMakalu($"[UI ] New empty Makalu profile created: slot {pi.Slot}");
            MkRefreshProfiles();
            MkSelectProfileSlot(pi.Slot);
        }

        _mkStore.SetCurrentProfile(pi.Slot);
        LogMakalu($"[UI ] Makalu profile selected: {pi.Slot}");
        MkReloadProfile(pi.Slot);
    }

    /// <summary>Right-click menu for LstMkProfile rows — see DpBuildProfileContextMenu
    /// (MainWindow.DisplayPad.cs) for the shared pattern/rationale.</summary>
    private ContextMenu MkBuildProfileContextMenu()
    {
        var menu = new ContextMenu();
        var miConfigure = new MenuItem { Header = Loc.Get("configure_profile") };
        miConfigure.Click += (_, _) => { if (LstMkProfile.SelectedItem is MkProfileItem pi) MkShowProfileGear(pi); };
        var miRename = new MenuItem { Header = Loc.Get("rename_profile") };
        miRename.Click += BtnMkRenameProfile_Click;
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnMkImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnMkImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnMkExportProfiles_Click;
        var miDelete = new MenuItem { Header = Loc.Get("delete_profile") };
        miDelete.Click += BtnMkDeleteProfile_Click;
        menu.Items.Add(miConfigure);
        menu.Items.Add(new Separator());
        menu.Items.Add(miRename);
        menu.Items.Add(new Separator());
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDelete);
        return menu;
    }

    /// <summary>Same items as <see cref="MkBuildProfileContextMenu"/> minus Rename/Delete —
    /// opened from the small "…" button in the Profile header (BtnMkProfileMenu_Click),
    /// which is not tied to a specific row so renaming/deleting a specific profile
    /// wouldn't make sense there.</summary>
    private ContextMenu MkBuildProfileMenuNoEdit()
    {
        var menu = new ContextMenu();
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnMkImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnMkImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnMkExportProfiles_Click;
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        return menu;
    }

    private void BtnMkProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = btn;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void BtnMkRenameProfile_Click(object sender, RoutedEventArgs e)
    {
        int slot = MkCurrentProfile();
        string current = _mkStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        string? name = ShowRenameDialog(current,
            Loc.Get("rename_profile_title"),
            Loc.Get("rename_profile_prompt"));
        if (name is null) return;
        _mkStore.SetProfileName(slot, name);
        MkRefreshProfiles();
        MkSelectProfileSlot(slot);
        LogMakalu($"[UI ] Makalu profile {slot} renamed to \"{name}\"");
    }

    private void BtnMkDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        int slot = MkCurrentProfile();
        // Cannot delete the last real profile — same guard as Everest/DisplayPad,
        // needed now that the list only shows configured slots (2026-07-29): deleting
        // down to zero would leave nothing for MkRefreshProfiles's "ensure profile 1"
        // fallback to select.
        if (_mkStore.GetExistingProfiles().Count <= 1)
        {
            MessageBox.Show(Loc.Get("delete_profile_last"),
                Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string profileName = _mkStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("delete_profile_confirm", profileName),
            Loc.Get("delete_profile"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _mkStore.ClearProfile(slot);
        LogMakalu($"[UI ] Makalu profile {slot} deleted.");
        MkRefreshProfiles();
        int fallback = _mkStore.GetExistingProfiles().DefaultIfEmpty(1).First();
        MkSelectProfileSlot(fallback);
        MkReloadProfile(fallback);
    }

    /// <summary>Gear-icon popup for a Makalu profile row (see ProfileGear_Click in
    /// MainWindow.xaml.cs). Same "last profile" guard as <see cref="BtnMkDeleteProfile_Click"/>.
    /// Also links an executable whose launch auto-switches to this profile (see
    /// K2.Core.Services.ProfileLaunchWatcher, registered from <see cref="MkRefreshProfiles"/>).</summary>
    private void MkShowProfileGear(MkProfileItem pi)
    {
        string currentName = _mkStore.GetProfileName(pi.Slot) ?? Loc.Get("profile_n", pi.Slot);
        string keyBase = $"profile.{pi.Slot}";
        string currentExe = _mkStore.GetSetting($"{keyBase}.launchExe") ?? "";
        bool focusOnly = _mkStore.GetSetting($"{keyBase}.launchFocusOnly") == "1";
        bool restoreOnClose = _mkStore.GetSetting($"{keyBase}.launchRestoreOnClose") == "1";
        var dlg = new ProfileSettingsDialog(currentName, currentExe, focusOnly, restoreOnClose) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.DeleteRequested)
        {
            if (_mkStore.GetExistingProfiles().Count <= 1)
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
            _mkStore.ClearProfile(pi.Slot);
            _mkStore.SetSetting($"{keyBase}.launchExe", "");
            LogMakalu($"[UI ] Makalu profile {pi.Slot} deleted (gear).");
            MkRefreshProfiles();
            int fallback = _mkStore.GetExistingProfiles().DefaultIfEmpty(1).First();
            MkSelectProfileSlot(fallback);
            MkReloadProfile(fallback);
            return;
        }

        _mkStore.SetProfileName(pi.Slot, dlg.ProfileName);
        _mkStore.SetSetting($"{keyBase}.launchExe", dlg.ExePath);
        _mkStore.SetSetting($"{keyBase}.launchFocusOnly", dlg.FocusOnly ? "1" : "0");
        _mkStore.SetSetting($"{keyBase}.launchRestoreOnClose", dlg.RestoreOnClose ? "1" : "0");
        LogMakalu($"[UI ] Makalu profile {pi.Slot} settings updated (gear).");
        MkRefreshProfiles();
        MkSelectProfileSlot(pi.Slot);
    }

    /// <summary>Switches to the given Makalu profile slot outright — mirrors the other
    /// devices' SwitchProfile(target) but Makalu has no IActionHost/target-string action
    /// (see the architectural note atop this file), so this is a direct slot setter, used
    /// only by K2.Core.Services.ProfileLaunchWatcher's launch-detection callback.</summary>
    private void MkSwitchProfileTo(int slot)
    {
        _mkStore.SetCurrentProfile(slot);
        MkSelectProfileSlot(slot);
        MkReloadProfile(slot);
    }

    /// <summary>Wipes EVERY Makalu profile back to K2's defaults: other profiles are
    /// deleted outright (mirrors BtnMkDeleteProfile_Click, full wipe — remap/lighting/DPI/
    /// settings/name), the current one keeps its name but has its button remap, lighting,
    /// DPI and device settings reset to K2's defaults and re-applied to the mouse if
    /// connected (see MakaluRgbSettingsPanel.RestoreDefaults / MakaluDpiRemapPanel.
    /// MkReloadRemap, which falls back to MakaluRemapData.RemapDefaults once the stored
    /// remap rows are gone). User request 2026-07-29 (previously only reset the current
    /// profile).</summary>
    private void BtnMkRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_device_confirm", Loc.Get("tab_makalu")),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        int slot = MkCurrentProfile();
        foreach (var s in _mkStore.GetExistingProfiles())
            if (s != slot) _mkStore.ClearProfile(s);

        _mkStore.ResetKeyRemap(slot);
        MkRgbSettings.RestoreDefaults();
        MkDpiRemap.MkReloadRemap(slot);
        LogMakalu($"[UI ] Makalu restored to factory defaults (all profiles, lighting, DPI and key remap).");
        MkRefreshProfiles();
    }

    // ------------------------------------------------------------
    // Import from Base Camp DB — mirrors BtnEvImportBc_Click in
    // MainWindow.Everest.cs. See BaseCampDbImporter's Makalu section for the
    // schema/vocabulary caveats (no real Makalu profile has ever existed in
    // this dev's Base Camp install to verify against).
    // ------------------------------------------------------------

    private void BtnMkImportBc_Click(object sender, RoutedEventArgs e)
    {
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            LogMakalu("[IMP-BC] BaseCamp.db not found.");
            return;
        }
        LogMakalu($"[IMP-BC] DB: {dbPath}");

        Dictionary<int, List<BaseCampDbImporter.BcProfile>> bcDevices;
        try { bcDevices = BaseCampDbImporter.ReadMakaluProfiles(dbPath); }
        catch (Exception ex) { LogMakalu($"[IMP-BC] Read error: {ex.Message}"); return; }

        if (bcDevices.Count == 0)
        {
            LogMakalu("[IMP-BC] No Makalu profiles in DB.");
            return;
        }

        string deviceLabel = AppSettings.MakaluDeviceName ?? (TabMakalu.Header as string) ?? Loc.Get("tab_makalu");

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

        // Pre-read every profile's data BEFORE wiping anything: this import is
        // destructive (replace, not append), so a corrupt/locked Base Camp DB must surface
        // while the existing K2 profiles are still intact — not after they're gone.
        try
        {
            foreach (var p in allProfiles)
            {
                BaseCampDbImporter.ReadMakaluMouseKeyBindings(dbPath, p.ProfileId);
                BaseCampDbImporter.ReadMakaluMouseLighting(dbPath, p.ProfileId);
                BaseCampDbImporter.ReadMakaluMouseSettings(dbPath, p.ProfileId);
            }
        }
        catch (Exception ex)
        {
            LogMakalu($"[IMP-BC] Pre-read failed, aborting before wipe: {ex.Message}");
            MessageBox.Show(this, Loc.Get("bc_import_read_failed", ex.Message),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Wipe: replace, don't append. Makalu always has 5 fixed K2-side slots (no
        // firmware profile concept — see the "Profile management" doc comment above).
        for (int slot = 1; slot <= 5; slot++)
            _mkStore.ClearProfile(slot);

        int totalRemap = 0;
        var usedSlots = new HashSet<int>();
        foreach (var profile in allProfiles)
        {
            try
            {
                int targetSlot = BaseCampDbImporter.FindFreeSlot(usedSlots);
                if (targetSlot == 0) continue; // sanity ceiling only (5 fixed slots)
                usedSlots.Add(targetSlot);

                var (remap, lighting, settings) = BaseCampDbImporter.ImportMakaluProfile(dbPath, profile, _mkStore, targetSlot);
                totalRemap += remap;
                LogMakalu($"[IMP-BC] slot {profile.Slot} '{profile.Name}' -> K2 slot {targetSlot}: remap={remap} lighting={lighting} settings={settings}");
            }
            catch (Exception ex) { LogMakalu($"[IMP-BC] slot {profile.Slot} error: {ex.Message}"); }
        }

        // Always land on the FIRST imported profile and force a reload — simpler and
        // safer than trying to restore whatever was active in Base Camp (user request:
        // a plain, predictable refresh after import beats guessing at BC's own state).
        int finalSlot = usedSlots.DefaultIfEmpty(1).Min();
        _mkStore.SetCurrentProfile(finalSlot);
        MkRefreshProfiles();
        MkSelectProfileSlot(finalSlot);
        MkReloadProfile(finalSlot);
        LogMakalu(Loc.Get("mk_imported_bc", allProfiles.Count, totalRemap));
    }

    // ------------------------------------------------------------
    // Import K2-only XML (produced by MkProfileExporter.ExportK2) —
    // single-profile, no Base Camp vocabulary translation needed since the
    // function keys are already MakaluRemapData's own strings.
    // ------------------------------------------------------------

    private void BtnMkImportXml_Click(object sender, RoutedEventArgs e)
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
            int slot = BaseCampDbImporter.FindFreeSlot(_mkStore.GetExistingProfiles());
            if (slot == 0)
            {
                MessageBox.Show(this, Loc.Get("import_no_free_slot", profileName),
                    Loc.Get("dp_open_bc_profile"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Two possible shapes here (2026-07-29 fix — the old code only understood
            // #2 below, so a genuine Base Camp XML export silently imported nothing):
            //  1) Real Base Camp shape (confirmed via decompiled BaseCamp.Data classes,
            //     same wrapper/item-class-name convention as every other device):
            //     <MakaluKeyBindings><MakaluKeyBinding>.../<MakaluKeyBindings>,
            //     <MakaluLightings><MakaluLighting> (one row per effect slot, pick
            //     IsEffectSelected="true"), <MakaluSettings><MakaluSetting> with
            //     DPI nested inside as <lstDPI><DPILevel>.
            //  2) K2's own OLD flat shape (MkProfileExporter, pre-2026-07-29): the
            //     wrapper name IS the (single, repeated-at-root) item, no nesting,
            //     DPI as root-level <DPILevels> siblings. Kept as a fallback so
            //     previously-exported K2 files still import (same convention as
            //     the Everest/Everest 60 BC-XML fixes).
            int remapped = 0;
            var kbWrapper = root.Element("MakaluKeyBindings");
            var kbItems = kbWrapper?.Elements("MakaluKeyBinding").ToList() is { Count: > 0 } wrapped
                ? wrapped
                : root.Elements("MakaluKeyBindings").ToList();
            foreach (var b in kbItems)
            {
                if (!int.TryParse(b.Element("KeyId")?.Value, out int btnIdx)) continue;
                string? functionType = b.Element("FunctionType")?.Value;
                string? functionValue = b.Element("FunctionValue")?.Value;
                string? fn = functionType == "K2Remap"
                    ? functionValue
                    : BaseCampDbImporter.TranslateMakaluRemapFunction(
                        functionType, functionValue, b.Element("FunctionEnteredValue")?.Value);
                if (string.IsNullOrEmpty(fn)) continue;
                _mkStore.SaveRemapButton(slot, btnIdx, fn);
                remapped++;
            }

            var lightWrapper = root.Element("MakaluLightings");
            var lightItems = lightWrapper?.Elements("MakaluLighting").ToList();
            var lightingEl = lightItems is { Count: > 0 }
                ? (lightItems.FirstOrDefault(x => (x.Element("IsEffectSelected")?.Value ?? "")
                        .Equals("true", StringComparison.OrdinalIgnoreCase)) ?? lightItems[0])
                : lightWrapper; // old flat K2 shape: the wrapper IS the single item
            if (lightingEl is not null)
            {
                string effectName = lightingEl.Element("EffectName")?.Value ?? "Static";
                bool customActive = effectName.Equals("Custom", StringComparison.OrdinalIgnoreCase);
                bool isRainbowColorMode = (lightingEl.Element("ColorType")?.Value ?? "")
                    .Equals("RAINBOW", StringComparison.OrdinalIgnoreCase);
                var eff = BaseCampDbImporter.TranslateMakaluEffectName(effectName, isRainbowColorMode);
                int color1 = BaseCampDbImporter.ParseBcColor(lightingEl.Element("DualColor1")?.Value ?? lightingEl.Element("SingleColor")?.Value, 0x900000);
                int color2 = BaseCampDbImporter.ParseBcColor(lightingEl.Element("DualColor2")?.Value, 0);
                int speedIdx = int.TryParse(lightingEl.Element("Speed")?.Value, out var sp) ? sp : 1;
                int dirIdx = int.TryParse(lightingEl.Element("Direction")?.Value, out var di) ? di : 0;
                double bright = int.TryParse(lightingEl.Element("Brightness")?.Value, out var br) ? br : 100;
                // Per-LED Custom colors (8 LEDs) — the payload was dropped here entirely
                // until 2026-07-26, so a Custom profile imported from XML came in black.
                // Same element name Base Camp and MkProfileExporter both write.
                var customColors = BaseCampDbImporter.ParseMakaluCustomColors(
                    lightingEl.Element("CustomMakaluLightings")?.Value);
                _mkStore.SaveLighting(slot, new MakaluLightingRecord(
                    (int)eff, color1, color2, speedIdx, dirIdx, bright, customActive, customColors));
            }

            var settingsWrapper = root.Element("MakaluSettings");
            var innerSetting = settingsWrapper?.Element("MakaluSetting");
            var settingsEl = innerSetting ?? settingsWrapper; // old flat K2 shape: wrapper IS the item
            var dpiItems = innerSetting?.Element("lstDPI")?.Elements("DPILevel").ToList()
                ?? root.Elements("DPILevels").ToList(); // old flat K2 shape: root-level siblings
            if (settingsEl is not null)
            {
                int pollHz = BaseCampDbImporter.NormalizeMakaluPollingHz(
                    int.TryParse(settingsEl.Element("PollingRate")?.Value, out var ph) ? ph : 1000);
                int debMs = int.TryParse(settingsEl.Element("ButtonResponseTime")?.Value, out var dm) ? dm : 2;
                bool angleOn = settingsEl.Element("AngleSnapping")?.Value == "On";
                string liftOff = settingsEl.Element("LiftOffDistance")?.Value ?? "Low";
                bool liftHigh = liftOff.Equals("High", StringComparison.OrdinalIgnoreCase);
                bool liftCustom = liftOff.Equals("Custom", StringComparison.OrdinalIgnoreCase);
                int sensitivity = int.TryParse(settingsEl.Element("Sensitivity")?.Value, out var sv)
                    ? Math.Clamp(sv, MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax) : 10;
                int clickSpeed = int.TryParse(settingsEl.Element("ClickSpeed")?.Value, out var cs)
                    ? Math.Clamp(cs, MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax) : 0;
                _mkStore.SaveSettings(slot, new MakaluDeviceSettingsRecord(
                    pollHz, debMs, angleOn, liftHigh, liftCustom, Sensitivity: sensitivity, ClickSpeed: clickSpeed));
            }

            if (dpiItems.Count > 0)
            {
                // Exactly as many levels as the file defines (1-5) — NOT padded to 5,
                // same fix as the DB import path (user report 2026-07-29).
                int count = Math.Clamp(dpiItems.Count, MakaluProtocol.DpiLevelCountMin, MakaluProtocol.DpiLevelCountMax);
                // Real Base Camp shape marks the active level via the settings element's
                // own SelectedDPILevelId (matched against each item's DPILevelId) — its
                // <DPILevel> items have no per-item "IsSelected" at all. The old flat K2
                // shape's <DPILevels> items DO carry "IsSelected" directly (no separate
                // settings-level id to cross-reference) — try that first, then fall back
                // to the id cross-reference.
                int selectedDpiLevelId = int.TryParse(settingsEl?.Element("SelectedDPILevelId")?.Value, out var sdi) ? sdi : -1;
                var levels = new int[count];
                int active = 0;
                for (int i = 0; i < count; i++)
                {
                    levels[i] = int.TryParse(dpiItems[i].Element("DPI")?.Value, out var d) ? d : (i > 0 ? levels[i - 1] : 0);
                    bool isSelectedFlag = (dpiItems[i].Element("IsSelected")?.Value ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
                    bool isSelectedById = selectedDpiLevelId >= 0
                        && int.TryParse(dpiItems[i].Element("DPILevelId")?.Value, out var lvlId) && lvlId == selectedDpiLevelId;
                    if (isSelectedFlag || isSelectedById) active = i;
                }
                _mkStore.SaveDpi(slot, new MakaluDpiRecord(levels, active));
            }

            // K2-format extra: the whole per-profile Settings namespace (see
            // K2ProfileSettingsXml). Absent from Base Camp files and from K2 exports made
            // before 2026-08-22, in which case this is a no-op.
            int k2Settings = K2ProfileSettingsXml.Apply(
                root, _mkStore.SetSetting, slot, K2ProfileSettingsXml.SettingsOnlyFamilies);
            if (k2Settings > 0) LogMakalu($"[IMP-XML] {k2Settings} K2 profile setting(s) restored");

            _mkStore.SetProfileName(slot, profileName);
            _mkStore.SetCurrentProfile(slot);
            MkRefreshProfiles();
            MkSelectProfileSlot(slot);
            MkReloadProfile(slot);
            LogMakalu($"[IMP-XML] '{profileName}' -> slot {slot}: {remapped} button(s)");
        }
        catch (Exception ex)
        {
            LogMakalu($"[ERR] import XML: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // Export profiles — Base Camp-compatible XML / K2-only XML, same shared
    // helper as Everest Max/MacroPad/DisplayPad.
    // ------------------------------------------------------------

    private void BtnMkExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var profiles = Enumerable.Range(1, 5)
            .Select(slot => (Slot: slot, Name: _mkStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot)))
            .ToList();
        int? currentSlot = LstMkProfile.SelectedItem is MkProfileItem pi ? pi.Slot : null;

        // Real DeviceType string (confirmed 2026-07-29 against a real BaseCamp.db —
        // see BaseCampDbImporter.ReadMakaluProfiles's doc comment), so a BC-compatible
        // export at least tags the right model even though MakaluMax itself is still
        // an unverified same-convention guess.
        string deviceType = _mkInfo.Model == MakaluService.Model.MakaluMax ? "MakaluMax" : "Makalu67";

        ExportProfileHelper.Run(
            owner: this,
            deviceLabel: "Makalu",
            profiles: profiles,
            currentSlot: currentSlot,
            exportOne: (slot, name, bcCompatible, path) =>
            {
                var result = bcCompatible
                    ? MkProfileExporter.ExportBaseCamp(_mkStore, slot, name, path, deviceType)
                    : MkProfileExporter.ExportK2(_mkStore, slot, name, path, deviceType);
                return (result.Exported, result.SkippedActions, result.SkipReasons);
            },
            log: LogMakalu,
            setStatus: LogMakalu);
    }

    // ------------------------------------------------------------
    // Section navigation — toggles the section Grids nested inside
    // MkRgbSettings (SecRgb/SecSettings, the latter also hosting the DPI
    // levels list) and MkDpiRemap (SecRemap, "Key Binding" in the sidebar).
    // ------------------------------------------------------------

    private FrameworkElement? _activeMkSection;

    /// <summary>Sets the default section AFTER InitializeComponent() has fully
    /// run (called from InitMakaluModule, which runs after the ctor's
    /// InitializeComponent() call) — setting RbMkSecRemap.IsChecked here, not in
    /// XAML, is what avoids the null-ref: see the comment on RbMkSecRemap in
    /// MainWindow.xaml. Key Binding is the default, same as MacroPad/DisplayPad
    /// (InitMpSectionNav/InitDpSectionNav in MainWindow.SectionNav.cs) — unlike
    /// Everest 60, Makalu's remap path is raw HID already (no vendor SDK
    /// session to keep lazy), so there's no reason to default elsewhere.</summary>
    private void InitMkSectionNav() => RbMkSecRemap.IsChecked = true; // fires MkSection_Changed -> ShowMkSection

    private void MkSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbMkSecRemap)    => MkDpiRemap.SecRemap,
            nameof(RbMkSecRgb)      => MkRgbSettings.SecRgb,
            nameof(RbMkSecSettings) => MkRgbSettings.SecSettings,
            _                       => null
        };

        if (panel is not null)
            ShowMkSection(panel);

        MkUpdateMouseImage(isLighting: rb.Name == nameof(RbMkSecRgb));
        UpdateMkCustomSquaresVisibility();
    }

    /// <summary>Swaps the device image per user request (2026-07-13): the
    /// live LED ring preview only makes sense while configuring Lighting, so
    /// every other section shows Base Camp's own pre-rendered rainbow photo
    /// (makalu_mouse_rainbow.png — opaque, no cutout) instead of the plain
    /// makalu_mouse.png (transparent ring cutout) the preview needs to show
    /// through. The ring keeps animating behind the scenes either way (not
    /// worth the extra bookkeeping to pause it), it just isn't visible
    /// through an opaque image.</summary>
    private void MkUpdateMouseImage(bool isLighting)
    {
        if (ImgMkMouse is null) return;
        // A plain relative Uri ("Assets/foo.png", UriKind.Relative) constructed in
        // code — as opposed to a XAML Source="..." attribute, which WPF's markup
        // extension resolves for you — has no base to resolve against and silently
        // fails to load. The explicit "pack://application:,,,/" authority is the
        // reliable form for a Resource-build-action file inside this same assembly.
        // The 2026-07-14 "hide ring for now" detour (this used to ignore isLighting
        // and always show the opaque rainbow photo) is over: the Custom Lighting
        // squares added 2026-07-27 point leader lines at the ring, so it needs to
        // actually be visible again while the Lighting section is active.
        string file = isLighting ? "makalu_mouse.png" : "makalu_mouse_rainbow.png";
        ImgMkMouse.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{file}"));
    }

    private void ShowMkSection(FrameworkElement panel)
    {
        if (_activeMkSection is not null)
            _activeMkSection.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
        _activeMkSection = panel;
    }

    // ------------------------------------------------------------
    // Device image hotspots — click a button on the mouse image to jump to
    // the Remap section with that physical button pre-selected. MkHotspotPos67
    // is pixel-measured against Assets/makalu_mouse.png (grid overlay + crop
    // sampling, 2026-07-11) to match Mountain's own numbered reference diagram
    // for the Makalu 67 (1/2 top buttons, 3 wheel, 5 above 4 on the side, 6
    // DPI button below the wheel). MkHotspotPosMax (8 buttons, different
    // layout) is still hand-estimated — no equivalent reference diagram seen.
    // ------------------------------------------------------------

    private static readonly Dictionary<int, (double X, double Y)> MkHotspotPos67 = new()
    {
        [1] = (68, 100),  // left
        [2] = (134, 100), // right
        [3] = (101, 155),  // middle/wheel
        [4] = (15, 260),  // back
        [5] = (15, 209),  // forward
        [6] = (101, 238),  // dpi
    };
    private static readonly Dictionary<int, (double X, double Y)> MkHotspotPosMax = new()
    {
        [1] = (70, 90),    // left
        [2] = (120, 90),   // right
        [3] = (101, 75),    // middle/wheel
        [4] = (101, 137),   // dpi
        [5] = (175, 180),  // extra button 5
        [6] = (175, 230),  // extra button 6
        [7] = (15, 180),   // forward
        [8] = (15, 230),   // back
    };

    private Dictionary<int, (double X, double Y)> MkHotspotPos =>
        _mkInfo.Model == MakaluService.Model.MakaluMax ? MkHotspotPosMax : MkHotspotPos67;

    /// <summary>Hotspot Ellipses by button index — kept around (unlike the local-only
    /// <c>dot</c> in <see cref="BuildMkHotspots"/> before this) so a physical press
    /// (<see cref="MkHighlightHotspot"/>) can find and fill the right circle.</summary>
    private readonly Dictionary<int, Ellipse> _mkHotspotDots = new();

    /// <summary>Resting hotspot fill — also the "un-pressed" target for
    /// <see cref="MkHighlightHotspot"/>.</summary>
    private static readonly SolidColorBrush s_mkHotspotRestBrush = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    /// <summary>Press-flash fill — same red as the keyboard devices' press highlight.</summary>
    private static readonly SolidColorBrush s_mkHotspotPressBrush = new(Color.FromRgb(0x90, 0x00, 0x00));

    /// <summary>Standard mouse button (Left/Right/Middle/Back/Forward — the five Windows
    /// itself can identify, see RawMouseActivityWatcher) → this model's hotspot index.
    /// Back/Forward sit at different indices per model (MkHotspotPos67 vs MkHotspotPosMax).</summary>
    private int? MkHotspotIndexFor(RawMouseActivityWatcher.MouseButton btn)
    {
        bool max = _mkInfo.Model == MakaluService.Model.MakaluMax;
        return btn switch
        {
            RawMouseActivityWatcher.MouseButton.Left    => 1,
            RawMouseActivityWatcher.MouseButton.Right   => 2,
            RawMouseActivityWatcher.MouseButton.Middle  => 3,
            RawMouseActivityWatcher.MouseButton.Back    => max ? 8 : 4,
            RawMouseActivityWatcher.MouseButton.Forward => max ? 7 : 5,
            _ => null,
        };
    }

    /// <summary>Raw Input told us a genuine Makalu click happened — see
    /// RawMouseActivityWatcher's doc comment for why this is the only way to see a real
    /// press (no vendor HID readback exists). Wired from MainWindow.xaml.cs's WndProc.</summary>
    private void OnMakaluRawButton(RawMouseActivityWatcher.MouseButton btn, bool pressed)
    {
        if (MkHotspotIndexFor(btn) is int idx) MkHighlightHotspot(idx, pressed);
    }

    /// <summary>Fills (or restores) the on-screen hotspot circle for a physical Makalu
    /// button press — user request 2026-07-27, "fill the circle red" mirroring the keyboard
    /// devices' highlight. Plain direct Fill assignment is safe here: nothing else ever
    /// touches a hotspot Ellipse's Fill after BuildMkHotspots creates it.</summary>
    private void MkHighlightHotspot(int btnIdx, bool pressed)
    {
        if (!_mkHotspotDots.TryGetValue(btnIdx, out var dot)) return;
        dot.Fill = pressed ? s_mkHotspotPressBrush : s_mkHotspotRestBrush;
    }

    /// <summary>This model's DPI-button hotspot index (see MkHotspotPos67/MkHotspotPosMax).</summary>
    private int MkDpiHotspotIndex => _mkInfo.Model == MakaluService.Model.MakaluMax ? 4 : 6;

    /// <summary>Index MkDpiFlashTimer_Tick un-highlights — set right before the timer
    /// (re)starts in <see cref="OnMakaluDpiPressed"/>.</summary>
    private int _mkDpiFlashIndex;

    /// <summary>MakaluDpiButtonWatcher told us the DPI button fired — see that class's doc
    /// comment for why this is a timed flash rather than a press/release pair (the button's
    /// own HID report has no release edge).</summary>
    private void OnMakaluDpiPressed()
    {
        _mkDpiFlashIndex = MkDpiHotspotIndex;
        MkHighlightHotspot(_mkDpiFlashIndex, true);

        if (_mkDpiFlashTimer is null)
        {
            _mkDpiFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _mkDpiFlashTimer.Tick += MkDpiFlashTimer_Tick;
        }
        _mkDpiFlashTimer.Stop();
        _mkDpiFlashTimer.Start();
    }

    private void MkDpiFlashTimer_Tick(object? sender, EventArgs e)
    {
        _mkDpiFlashTimer!.Stop();
        MkHighlightHotspot(_mkDpiFlashIndex, false);
    }

    /// <summary>MakaluDpiButtonWatcher told us a button with a "software action" function
    /// fired (see MakaluProtocol's doc comment above CategoryRunProgramOrFolder for the full
    /// protocol). Only Run Program/Open Folder (category 0x23) is implemented — anything else
    /// is logged so a future session has real button/category pairs to work from instead of
    /// silently doing nothing.</summary>
    private void OnMakaluButtonEvent(byte category, int buttonIndex1Based)
    {
        if (category != MakaluProtocol.CategoryRunProgramOrFolder)
        {
            LogMakalu($"[Makalu] button {buttonIndex1Based}: software action category 0x{category:X2} not yet implemented");
            return;
        }
        System.Threading.Tasks.Task.Run(() => RunMakaluButtonAction(buttonIndex1Based));
    }

    /// <summary>Acks the notification, reads back the stored path, and launches it —
    /// off the UI thread (blocking HID round-trip + Process.Start). Opens its own
    /// short-lived handle on the config collection, same open-per-call pattern as
    /// MakaluService.WithDevice; doesn't touch the DPI-button watcher's own persistent
    /// handle (a different HID collection).</summary>
    private void RunMakaluButtonAction(int buttonIndex1Based)
    {
        void Log(string msg) => Dispatcher.BeginInvoke(() => LogMakalu(msg));

        var found = MakaluHidNative.FindDevice();
        if (found is null) { Log("[Makalu] button action: device not connected"); return; }
        using var h = MakaluHidNative.Open(found.Value.Path);
        if (h is null || h.IsInvalid) { Log("[Makalu] button action: open failed"); return; }

        if (!MakaluProtocol.AckButtonEvent(h, buttonIndex1Based))
        {
            Log($"[Makalu] button {buttonIndex1Based}: ack failed");
            return;
        }
        System.Threading.Thread.Sleep(20);

        string? path = MakaluProtocol.ReadButtonEventPayload(h);
        if (string.IsNullOrWhiteSpace(path))
        {
            Log($"[Makalu] button {buttonIndex1Based}: no payload read back");
            return;
        }

        Log($"[Makalu] button {buttonIndex1Based}: opening \"{path}\"");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"[Makalu] button {buttonIndex1Based}: failed to open \"{path}\": {ex.Message}");
        }
    }

    /// <summary>Re-resolves every hotspot dot's Stroke after a live accent theme switch
    /// — see the AccentCatalog.Applied subscription in InitMakaluModule.</summary>
    private void RefreshMkHotspotAccentColors()
    {
        var brush = (Brush)FindResource("K2AccentBrush");
        foreach (var dot in _mkHotspotDots.Values)
            dot.Stroke = brush;
    }

    private void BuildMkHotspots()
    {
        CvsMkHotspots.Children.Clear();
        _mkHotspotDots.Clear();
        BuildMkLedRing();
        var names = MakaluRemapData.BtnNames(_mkInfo.Model);
        foreach (var kv in MkHotspotPos)
        {
            int btnIdx = kv.Key;
            var (x, y) = kv.Value;
            var dot = new Ellipse
            {
                Width = 22, Height = 22,
                Fill = s_mkHotspotRestBrush,
                Stroke = (Brush)FindResource("K2AccentBrush"),
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                ToolTip = names.TryGetValue(btnIdx, out var key) ? Loc.Get(key) : $"#{btnIdx}",
            };
            dot.MouseLeftButtonUp += (_, _) => MkHotspotClicked(btnIdx);
            Canvas.SetLeft(dot, x - dot.Width / 2);
            Canvas.SetTop(dot, y - dot.Height / 2);
            CvsMkHotspots.Children.Add(dot);
            _mkHotspotDots[btnIdx] = dot;
        }
    }

    private void MkHotspotClicked(int btnIdx)
    {
        RbMkSecRemap.IsChecked = true; // fires MkSection_Changed -> ShowMkSection
        MkDpiRemap.SelectRemapButton(btnIdx);
    }

    // ------------------------------------------------------------
    // LED ring preview — a software-only rendering of the currently selected
    // lighting effect, drawn as a ring around the wheel/DPI button on the
    // device image (same spot Base Camp's own Makalu screens show it). The
    // Makalu has no HID readback (unlike Everest 60's GetColorData2 poll —
    // see Everest60LedColorPoller), so this can't be a live hardware
    // preview: it mirrors MkRgbSettings' own effect/color/speed/direction
    // state instead, same as how Base Camp itself only ever shows what the
    // user picked, not what the mouse is physically doing.
    //
    // The ring area in makalu_mouse.png is a genuinely TRANSPARENT cutout
    // (confirmed 2026-07-12 by the user directly inspecting the PNG in
    // Photoshop — NOT painted grey/white pixels, which is what an earlier
    // brightness-based pixel scan misread it as), so this Border is drawn
    // BEHIND the Image (CvsMkRingBack in MainWindow.xaml, added before
    // <Image> in the same Grid cell) and sized slightly larger than the
    // measured hole so it shows through the gap — same technique Base
    // Camp's own overlay presumably uses on the real backlit ring.
    // ------------------------------------------------------------

    private Canvas? _mkLedRingHost;
    private readonly Border?[] _mkLedCells = new Border?[8];

    /// <summary>Visual cell index (0..7, left column top→bottom then right
    /// column top→bottom) → physical LED index, per
    /// MakaluProtocol.SetLightingCustom's doc (LED0=top-left…LED3=bottom-left,
    /// LED4=bottom-right…LED7=top-right). Used both for Custom (real per-LED
    /// colors) and for Rainbow's chase sequence (phase offset by LED index,
    /// which is itself already a loop around the ring's perimeter).</summary>
    private static readonly int[] MkCellLed = { 0, 1, 2, 3, 7, 6, 5, 4 };

    /// <summary>Native-pixel measurements of the transparent ring cutout in
    /// Assets/makalu_mouse.png (364×809 source), given directly by the user
    /// after inspecting the PNG's alpha channel in Photoshop (2026-07-12):
    /// left=152, top=252, width=83, height=273, ring line width=13, corner
    /// radius=38. Converted below to the Canvas's 190×422 render space
    /// (scale = 190/364) with a small overscan margin, per the user's own
    /// advice ("rendi l'anello leggermente più grande") so it fully covers
    /// the gap despite any residual sub-pixel misalignment.
    /// NOT model-dependent: both Makalu 67 and Max show this same photo (Max
    /// has no reference image of its own, see MkHotspotPosMax's doc
    /// comment), so the ring aligns to the one image actually on screen
    /// rather than to a per-model formula.</summary>
    private const double MkRingImageScale = 190.0 / 364.0;
    private const double MkRingOverscan = 3.0; // extra canvas px on each side beyond the measured hole
    /// <summary>User-reported corrections (2026-07-13) against the measured
    /// values: top sat 14 native px too low, and the bottom cap needed to
    /// come up a few native px too (ring read as slightly too tall/low
    /// overall against the actual on-screen render).</summary>
    private const double MkRingTopAdjustNative = -14.0;
    private const double MkRingHeightAdjustNative = -8.0;
    private const double MkRingLeft = 152 * MkRingImageScale - MkRingOverscan;
    private const double MkRingTop = (252 + MkRingTopAdjustNative) * MkRingImageScale - MkRingOverscan;
    private const double MkRingWidth = 83 * MkRingImageScale + MkRingOverscan * 2;
    private const double MkRingHeight = (273 + MkRingHeightAdjustNative) * MkRingImageScale + MkRingOverscan * 2;

    /// <summary>Builds the ring as 8 FILLED discrete cells (not a hollow
    /// stroke, not a smooth gradient) — one per physical LED, 4 stacked down
    /// each side — since the ring only shows through the image's transparent
    /// cutout anyway (everything else is hidden behind opaque pixels), a
    /// full fill is exactly as visible as a stroke there. Going all the way
    /// to true discrete cells (rather than the 2-half gradient tried first) is
    /// what makes both Custom (true independent LED colors, no gradient math
    /// needed — the shared BlurEffect below softens the cell boundaries for
    /// free) and Rainbow (a real chase sequence across 8 positions, not one
    /// rotating gradient behind the image) possible — the latter per user
    /// feedback 2026-07-13 ("al momento è solo il ring che ruota sullo
    /// sfondo" — wanted a genuine per-LED sequence instead).</summary>
    private void BuildMkLedRing()
    {
        CvsMkRingBack.Children.Clear();

        double halfWidth = MkRingWidth / 2;
        double cellHeight = MkRingHeight / 4;
        double cap = MkRingWidth / 2; // same radius as the old single-Border stadium, so the outer silhouette is unchanged

        _mkLedRingHost = new Canvas
        {
            Width = MkRingWidth,
            Height = MkRingHeight,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
            // Softens the seams between adjacent cells (most visible at the
            // rounded top/bottom caps) — also reads as a more realistic LED
            // glow/bloom rather than a hard-edged shape.
            Effect = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian },
        };

        for (int c = 0; c < 8; c++)
        {
            bool left = c < 4;
            int rowInColumn = left ? c : c - 4; // 0=top row of that column .. 3=bottom row
            var radius = (left, rowInColumn) switch
            {
                (true, 0)  => new CornerRadius(cap, 0, 0, 0),   // LED0 top-left: outer top cap
                (true, 3)  => new CornerRadius(0, 0, 0, cap),   // LED3 bottom-left: outer bottom cap
                (false, 0) => new CornerRadius(0, cap, 0, 0),   // LED7 top-right: outer top cap
                (false, 3) => new CornerRadius(0, 0, cap, 0),   // LED4 bottom-right: outer bottom cap
                _          => new CornerRadius(0),              // middle cells: flat, between two others
            };

            var cell = new Border
            {
                Width = halfWidth,
                Height = cellHeight,
                CornerRadius = radius,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(cell, left ? 0 : halfWidth);
            Canvas.SetTop(cell, rowInColumn * cellHeight);
            _mkLedCells[c] = cell;
            _mkLedRingHost.Children.Add(cell);
        }

        Canvas.SetLeft(_mkLedRingHost, MkRingLeft);
        Canvas.SetTop(_mkLedRingHost, MkRingTop);
        CvsMkRingBack.Children.Add(_mkLedRingHost);

        BuildMkCustomSquares();
        MkUpdateLedRingPreview();
    }

    // ------------------------------------------------------------
    // Custom Lighting squares — 8 clickable swatches placed beside the ring on
    // the device image, each with a leader line to its physical LED's ring
    // sector (user request 2026-07-27, matches Base Camp's own reference photo:
    // colored squares flanking the ring, connected to their sector by a line).
    // Data (selection/colors/persistence/device-apply) is owned by
    // MkRgbSettings (MakaluRgbSettingsPanel); this file only owns the VISUAL
    // overlay + click routing — same split as Everest 60's border squares
    // (MainWindow.Everest60.cs) vs. its own RGB panel.
    // ------------------------------------------------------------

    /// <summary>CvsMkDeviceHost's own width (MainWindow.xaml) — wide enough to flank the
    /// 190px mouse image (offset by <see cref="MkDeviceImageOffsetX"/>) with squares on
    /// both sides.</summary>
    private const double MkDeviceHostWidth = 330;

    /// <summary>CvsMkRingBack/ImgMkMouse/CvsMkHotspots' shared Canvas.Left within
    /// CvsMkDeviceHost (MainWindow.xaml) — <see cref="MkRingLeft"/>/<see cref="MkRingTop"/>
    /// are in THEIR local space, so this offset converts a ring coordinate into
    /// CvsMkCustomSquares' host-wide space for the leader lines below.</summary>
    private const double MkDeviceImageOffsetX = 70;

    private const double MkCustomSquareSize = 26;
    private const double MkCustomSquareMarginX = 8; // gap from the host's own left/right edge to each square

    /// <summary>One square Button per physical LED (0-7), indexed by LED id.</summary>
    private readonly Button[] _mkCustomSquares = new Button[8];

    /// <summary>Builds the 8 squares + their leader lines into CvsMkCustomSquares, 4 on
    /// each side, in the same visual top-to-bottom order as the ring's own cells
    /// (<see cref="MkCellLed"/>) so both always agree. Rebuilt whenever the ring itself
    /// is (called from <see cref="BuildMkLedRing"/>) — cheap, and the geometry never
    /// actually changes per model (ring position isn't model-dependent, see
    /// MkRingLeft/Top's doc), so this is just "rebuild alongside", not a real need.</summary>
    private void BuildMkCustomSquares()
    {
        CvsMkCustomSquares.Children.Clear();
        for (int c = 0; c < 8; c++)
        {
            bool left = c < 4;
            int row = left ? c : c - 4; // 0=top row of that column .. 3=bottom row, matches MkCellLed
            BuildMkCustomSquareAndLine(led: MkCellLed[c], row: row, left: left);
        }
    }

    private void BuildMkCustomSquareAndLine(int led, int row, bool left)
    {
        double cellHeight = MkRingHeight / 4;
        double yCenter = MkRingTop + (row + 0.5) * cellHeight;
        double squareY = yCenter - MkCustomSquareSize / 2;
        double squareX = left
            ? MkCustomSquareMarginX
            : MkDeviceHostWidth - MkCustomSquareMarginX - MkCustomSquareSize;
        double ringEdgeX = MkDeviceImageOffsetX + (left ? MkRingLeft : MkRingLeft + MkRingWidth);
        double lineStartX = left ? squareX + MkCustomSquareSize : squareX;

        var line = new Line
        {
            X1 = lineStartX, Y1 = yCenter, X2 = ringEdgeX, Y2 = yCenter,
            Stroke = (Brush)FindResource("K2TextMutedBrush"),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        CvsMkCustomSquares.Children.Add(line);

        // Plain default Button style (CornerRadius 7, tuned for ~30px swatches) —
        // NOT K2ColorSquareButton, which is tuned for the ~12px border-LED squares
        // (see K2Theme.xaml's doc comment). Fixed muted border always — no
        // "selected" state to highlight any more (2026-07-27, user feedback: these
        // are plain click-to-paint targets, not a select-then-apply flow).
        var btn = new Button
        {
            Width = MkCustomSquareSize,
            Height = MkCustomSquareSize,
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4F)),
            Tag = led,
        };
        btn.Click += MkCustomSquare_Click;
        Canvas.SetLeft(btn, squareX);
        Canvas.SetTop(btn, squareY);
        CvsMkCustomSquares.Children.Add(btn);
        _mkCustomSquares[led] = btn;
    }

    /// <summary>Paints one LED with the settings panel's current brush color and commits
    /// it (persist + device apply) right away — a plain click, no selection step.</summary>
    private void MkCustomSquare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int led }) return;
        MkRgbSettings.PaintLed(led);
        MkRgbSettings.CommitCustomPaint();
    }

    /// <summary>Repaints one square's fill from MkRgbSettings' current custom colors.</summary>
    private void MkUpdateCustomSquareVisual(int led)
    {
        var btn = _mkCustomSquares[led];
        if (btn is null) return;
        var (r, g, b) = MkRgbSettings.GetPreviewState().CustomColors[led];
        btn.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void MkUpdateAllCustomSquareVisuals()
    {
        for (int i = 0; i < 8; i++) MkUpdateCustomSquareVisual(i);
    }

    /// <summary>Shows the square overlay only while Custom is the active Lighting effect
    /// AND the Lighting section itself is on screen (mirrors Everest 60's
    /// UpdateEv60BorderOverlayVisibility) — called from MkSection_Changed and from
    /// MkUpdateLedRingPreview (MkRgbSettings.PreviewChanged), so it stays correct across
    /// both a section switch and a color/effect change.</summary>
    private void UpdateMkCustomSquaresVisibility()
    {
        if (CvsMkCustomSquares is null || MkRgbSettings is null) return;
        bool show = MkRgbSettings.IsCustomActive && ReferenceEquals(_activeMkSection, MkRgbSettings.SecRgb);
        CvsMkCustomSquares.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) MkUpdateAllCustomSquareVisuals();
    }

    // ------------------------------------------------------------
    // Rubber-band multi-select over the squares — drag a rectangle across
    // several to paint every one it touches with the current brush color in one
    // go (user request 2026-07-27, "enable multi-selection"), mirrors Everest
    // Max/60's rubber-band paint (MainWindow.CustomLighting.cs's
    // EvDeviceBox_MouseDown/Move/Up). Wired to BdrMkDeviceBox (the outer,
    // opaque-background Border) rather than CvsMkCustomSquares itself: a Canvas
    // with no Background isn't hit-testable on its own empty area (only its
    // children are), so a drag starting between squares — not exactly on one —
    // never reached it. The Border's real Background makes the whole device box
    // hit-testable regardless of what's underneath at any given point.
    // ------------------------------------------------------------

    private Point _mkRubberStart;
    private bool _mkRubberTracking;
    private bool _mkRubberActive;

    private void BdrMkDeviceBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MkRgbSettings is null || !MkRgbSettings.IsCustomActive) return;
        _mkRubberStart = e.GetPosition(CvsMkCustomSquares);
        _mkRubberTracking = true;
        _mkRubberActive = false;
    }

    private void BdrMkDeviceBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mkRubberTracking) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelMkRubberBand();
            return;
        }
        var p = e.GetPosition(CvsMkCustomSquares);
        if (!_mkRubberActive)
        {
            if (Math.Abs(p.X - _mkRubberStart.X) < 5 && Math.Abs(p.Y - _mkRubberStart.Y) < 5) return;
            _mkRubberActive = true;
            RectMkRubberBand.Visibility = Visibility.Visible;
            // Steal capture from whatever square Button the drag started on, so it
            // neither clicks on release nor keeps eating our move events.
            CvsMkCustomSquares.CaptureMouse();
        }
        var r = new Rect(_mkRubberStart, p);
        Canvas.SetLeft(RectMkRubberBand, r.X);
        Canvas.SetTop(RectMkRubberBand, r.Y);
        RectMkRubberBand.Width = r.Width;
        RectMkRubberBand.Height = r.Height;
    }

    private void BdrMkDeviceBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mkRubberTracking) return;
        bool wasActive = _mkRubberActive;
        var rect = wasActive ? new Rect(_mkRubberStart, e.GetPosition(CvsMkCustomSquares)) : Rect.Empty;
        CancelMkRubberBand();
        if (!wasActive) return; // plain click: let the Button handle it normally
        e.Handled = true;       // suppress the click that would otherwise fire on release
        PaintMkSquaresInRect(rect);
    }

    private void CancelMkRubberBand()
    {
        _mkRubberTracking = false;
        _mkRubberActive = false;
        RectMkRubberBand.Visibility = Visibility.Collapsed;
        if (CvsMkCustomSquares.IsMouseCaptured) CvsMkCustomSquares.ReleaseMouseCapture();
    }

    /// <summary>Paints every square whose bounds intersect <paramref name="rect"/> (in
    /// CvsMkCustomSquares' own coordinate space) with the current brush color, then
    /// commits once for all of them — SetLightingCustom always sends all 8 LED colors
    /// in one packet anyway, so there's no benefit to committing per-square.</summary>
    private void PaintMkSquaresInRect(Rect rect)
    {
        bool any = false;
        foreach (var btn in _mkCustomSquares)
        {
            if (btn is null || btn.Tag is not int led) continue;
            var bounds = new Rect(Canvas.GetLeft(btn), Canvas.GetTop(btn), btn.Width, btn.Height);
            if (!rect.IntersectsWith(bounds)) continue;
            MkRgbSettings.PaintLed(led);
            any = true;
        }
        if (any) MkRgbSettings.CommitCustomPaint();
    }

    private static readonly double[] MkRingSpeedSeconds = { 2.6, 1.6, 0.9 }; // slow/medium/fast

    private static Color MkScaleColor(int rgb, double brightnessPct)
    {
        double f = Math.Clamp(brightnessPct, 0, 100) / 100.0;
        byte r = (byte)(((rgb >> 16) & 0xFF) * f);
        byte g = (byte)(((rgb >> 8) & 0xFF) * f);
        byte b = (byte)((rgb & 0xFF) * f);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>Hue-only HSV→RGB (full saturation/value, scaled by
    /// brightness at the end) — used to synthesize the Rainbow chase's
    /// per-cell colors analytically instead of keyframing 8 separate
    /// animations.</summary>
    private static Color MkHueColor(double hueDeg, double brightnessPct)
    {
        double h = ((hueDeg % 360) + 360) % 360;
        double x = 1 - Math.Abs(h / 60.0 % 2 - 1);
        var (r1, g1, b1) = h switch
        {
            < 60  => (1.0, x, 0.0),
            < 120 => (x, 1.0, 0.0),
            < 180 => (0.0, 1.0, x),
            < 240 => (0.0, x, 1.0),
            < 300 => (x, 0.0, 1.0),
            _     => (1.0, 0.0, x),
        };
        double f = Math.Clamp(brightnessPct, 0, 100) / 100.0;
        return Color.FromRgb((byte)(r1 * 255 * f), (byte)(g1 * 255 * f), (byte)(b1 * 255 * f));
    }

    /// <summary>Drives the Rainbow effect's per-cell chase — each of the 8
    /// cells shows a hue offset by its physical LED index (already a loop
    /// around the ring's perimeter, see <see cref="MkCellLed"/>), all
    /// rotating together over time. A plain DispatcherTimer recomputing all
    /// 8 colors analytically is simpler to reason about here than 8
    /// synchronized WPF ColorAnimations with hand-tuned keyframe offsets.</summary>
    private DispatcherTimer? _mkRainbowChaseTimer;
    private double _mkRainbowDegPerSec;
    private double _mkRainbowBrightness;

    private void StartMkRainbowChase()
    {
        StopMkRainbowChase();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _mkRainbowChaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _mkRainbowChaseTimer.Tick += (_, _) =>
        {
            double t = sw.Elapsed.TotalSeconds;
            for (int c = 0; c < 8; c++)
            {
                double hue = t * _mkRainbowDegPerSec + MkCellLed[c] * 45.0;
                _mkLedCells[c]!.Background = new SolidColorBrush(MkHueColor(hue, _mkRainbowBrightness));
            }
        };
        _mkRainbowChaseTimer.Start();
    }

    private void StopMkRainbowChase()
    {
        _mkRainbowChaseTimer?.Stop();
        _mkRainbowChaseTimer = null;
    }

    /// <summary>Single-color Breathing: the ring fades from black up to Color1 and back
    /// down to black, repeating — "colore scelto al nero" (user request 2026-07-27). A
    /// single WPF ColorAnimation with AutoReverse does the whole pulse for free, same
    /// technique the old Color1↔Color2 crossfade used, just with black as the other
    /// endpoint instead of a second user color (Breathing's Double option was dropped in
    /// the same request — see MakaluRgbSettingsPanel.CapsFor's doc).</summary>
    private void StartMkBreathingSingle(double dur, int color1, double brightnessPct)
    {
        var brush = new SolidColorBrush(Colors.Black);
        foreach (var cell in _mkLedCells) cell!.Background = brush;
        var anim = new ColorAnimation(Colors.Black, MkScaleColor(color1, brightnessPct), TimeSpan.FromSeconds(dur))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>Whether a <see cref="StartMkBreathingRainbow"/> chain is still supposed to
    /// keep going — checked by each animation's Completed handler before queuing the next
    /// one, since there's no RepeatBehavior.Forever equivalent when the target color
    /// changes every cycle (see that method's doc).</summary>
    private bool _mkBreathingRainbowActive;

    /// <summary>Rainbow Breathing (the old separate "RGB Breathing" effect, now reached via
    /// the Rainbow radio under Breathing — see MakaluRgbSettingsPanel.ResolveMkWireEffect):
    /// identical technique to <see cref="StartMkBreathingSingle"/> (a black↔peak
    /// ColorAnimation, AutoReverse) — user feedback 2026-07-27 that an earlier
    /// DispatcherTimer-driven version "didn't work very well" (visibly less smooth than the
    /// native animation). The only difference: each full cycle picks a NEW peak hue instead
    /// of repeating the same one forever, chained via the animation's Completed event since
    /// RepeatBehavior.Forever can't target an ever-changing color — "un colore unico al
    /// nero, a un altro colore, al nero, e così via".</summary>
    private void StartMkBreathingRainbow(double dur, double brightnessPct)
    {
        StopMkBreathingRainbow();
        _mkBreathingRainbowActive = true;
        var brush = new SolidColorBrush(Colors.Black);
        foreach (var cell in _mkLedCells) cell!.Background = brush;

        double hue = 0;
        void PlayNext()
        {
            if (!_mkBreathingRainbowActive) return;
            var peak = MkHueColor(hue, brightnessPct);
            var anim = new ColorAnimation(Colors.Black, peak, TimeSpan.FromSeconds(dur)) { AutoReverse = true };
            anim.Completed += (_, _) =>
            {
                // Golden-angle-ish step so consecutive breaths land on visually distinct
                // hues instead of a slow, barely-noticeable drift around the wheel.
                hue = (hue + 137.5) % 360;
                PlayNext();
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        PlayNext();
    }

    private void StopMkBreathingRainbow()
    {
        _mkBreathingRainbowActive = false;
    }

    /// <summary>Reapplies the 8 cells' brushes/animations from
    /// MkRgbSettings' current state — called on effect/color/speed/
    /// direction/brightness change (MkRgbSettings.PreviewChanged) and
    /// whenever the ring itself is (re)built (model change). Every effect
    /// except Custom/Rainbow shares ONE brush/animation instance across all
    /// 8 cells — a shared Freezable brush animates every cell referencing it
    /// in perfect sync for free.</summary>
    private void MkUpdateLedRingPreview()
    {
        if (_mkLedRingHost is null || _mkLedCells[0] is null || MkRgbSettings is null) return;
        var s = MkRgbSettings.GetPreviewState();

        UpdateMkCustomSquaresVisibility();
        StopMkRainbowChase();
        StopMkBreathingRainbow();
        _mkLedRingHost.BeginAnimation(UIElement.OpacityProperty, null);
        _mkLedRingHost.Opacity = 1;
        _mkLedRingHost.Visibility = Visibility.Visible;

        if (s.IsCustom) // Custom: true per-LED colors, no gradient needed — the shared BlurEffect blends the cell boundaries
        {
            var leds = s.CustomColors;
            for (int c = 0; c < 8; c++)
            {
                var (r, g, b) = leds[MkCellLed[c]];
                _mkLedCells[c]!.Background = new SolidColorBrush(MkScaleColor((r << 16) | (g << 8) | b, s.Brightness));
            }
            return;
        }

        if (s.Effect == MakaluProtocol.Effect.Off)
        {
            _mkLedRingHost.Visibility = Visibility.Collapsed;
            return;
        }

        double dur = MkRingSpeedSeconds[Math.Clamp(s.SpeedIdx, 0, MkRingSpeedSeconds.Length - 1)];

        // Breathing/RgbBreathing are handled explicitly here (not via the caps-based
        // dispatch below) since 2026-07-27's merge (RgbBreathing folded into Breathing's
        // own Rainbow radio) gave them bespoke black-based pulse animations — see
        // StartMkBreathingSingle/StartMkBreathingRainbow's doc comments.
        if (s.Effect == MakaluProtocol.Effect.Breathing)
        {
            StartMkBreathingSingle(dur, s.Color1, s.Brightness);
            return;
        }
        if (s.Effect == MakaluProtocol.Effect.RgbBreathing)
        {
            StartMkBreathingRainbow(dur, s.Brightness);
            return;
        }

        var caps = MakaluRgbSettingsPanel.CapsFor(s.Effect);

        if (caps.Direction) // Rainbow / Color Wave: chase sequence across the 8 discrete LEDs
        {
            _mkRainbowDegPerSec = 360.0 / (dur * 2) * (s.DirIdx == 0 ? -1 : 1);
            _mkRainbowBrightness = s.Brightness;
            StartMkRainbowChase();
        }
        else if (caps.Color2) // Yeti: all 8 cells pulse between the two colors in sync
        {
            var brush = new SolidColorBrush(MkScaleColor(s.Color1, s.Brightness));
            foreach (var cell in _mkLedCells) cell!.Background = brush;
            var anim = new ColorAnimation(MkScaleColor(s.Color1, s.Brightness), MkScaleColor(s.Color2, s.Brightness), TimeSpan.FromSeconds(dur))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else // Static / Responsive: solid color, no animation
        {
            var brush = new SolidColorBrush(MkScaleColor(s.Color1, s.Brightness));
            foreach (var cell in _mkLedCells) cell!.Background = brush;
        }
    }

    // ------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------

    private void MkRefreshStatus()
    {
        bool wasConnected = _mkConnected;
        bool connected = _makalu.IsConnected(out var info);
        _mkConnected = connected;

        // Retried every tick (cheap no-op once running) rather than only on the
        // disconnected->connected edge, so a background read thread that died from a
        // transient error (see MakaluDpiButtonWatcher.ReadLoop) gets restarted without
        // needing a full unplug/replug cycle.
        if (connected) _mkDpiWatcher?.Start();
        else _mkDpiWatcher?.Stop();

        // _mkInfo (and the tab header) must be current BEFORE SetDeviceTabVisible below,
        // since that call triggers RefreshHomeTiles() -> MkHomeImageFile(), which reads
        // _mkInfo.Model — otherwise the very first connect of a session would build the
        // Home tile from the stale default model (Makalu67) instead of the real one.
        if (connected && (!wasConnected || info.Model != _mkInfo.Model))
        {
            _mkInfo = info;
            MkRgbSettings.UpdateDeviceInfo(info);
            MkDpiRemap.UpdateDeviceInfo(info);
            BuildMkHotspots();
            // Reflect the actual connected model (Makalu Max vs 67 sit in the same tab
            // slot — only one is ever physically plugged in) unless the user renamed
            // the tab themselves (AppSettings.MakaluDeviceName).
            if (AppSettings.MakaluDeviceName is null)
                TabMakalu.Header = info.Label;
        }

        SetDeviceTabVisible(TabMakalu, connected);
        MkRgbSettings.SetConnected(connected);
        LblMkStatus.Text = connected
            ? Loc.Get("makalu_status_connected", info.Label)
            : Loc.Get("makalu_status_disconnected");
        LblMkStatus.Foreground = connected
            ? (Brush)FindResource("K2AccentBrush")
            : (Brush)FindResource("K2TextMutedBrush");

        // Freshly plugged in: push the currently selected profile so the
        // mouse reflects it even if it was switched while disconnected.
        if (connected && !wasConnected)
            MkReloadProfile(MkCurrentProfile());
    }

    private void BtnMkRefresh_Click(object sender, RoutedEventArgs e) => MkRefreshStatus();

    // ------------------------------------------------------------
    // Brightness — Slider lives in MainWindow's shared top-right bar
    // (BrMakalu), not in MkRgbSettings; same convention as Everest Max's
    // SldEvBrightness_ValueChanged.
    // ------------------------------------------------------------
    private void SldMkBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMkBrightness != null) LblMkBrightness.Text = $"{(int)e.NewValue}%";
        // Null-guard: SldMkBrightness lives in the shared top bar, declared in
        // MainWindow.xaml BEFORE MkRgbSettings (Makalu tab content further down
        // the same file). Its explicit Value="100" (default is 0) makes WPF fire
        // this handler synchronously during InitializeComponent(), before
        // MkRgbSettings has been constructed/assigned yet — same root cause as the
        // RbMkSecRgb/SldMkDpi crashes (see CHANGELOG 2026-07-10), just hit here via
        // a Slider.Value default-mismatch instead of RadioButton.IsChecked.
        MkRgbSettings?.SetBrightness(e.NewValue);
    }

    // ------------------------------------------------------------
    // Debug mode — driven centrally by the General Settings tab
    // (MainWindow.Settings.cs), see AppSettings.DebugMode. Mirrors
    // ApplyDebugMode (Everest)/ApplyMpDebugMode/ApplyDpDebugMode.
    // ------------------------------------------------------------
    private void ApplyMkDebugMode(bool debug)
    {
        var vis = debug ? Visibility.Visible : Visibility.Collapsed;

        // Common actions: Debug group (Connected status + Refresh)
        PnlMkDebugGroup.Visibility = vis;

        // Right column: log box (same gating as Everest's GbEvLog)
        PnlMkLog.Visibility = vis;
    }

    // ------------------------------------------------------------
    // Device rename (no per-device SQLite store for Makalu — see
    // AppSettings.MakaluDeviceName)
    // ------------------------------------------------------------

    private void BtnMkRename_Click(object sender, RoutedEventArgs e)
    {
        string current = AppSettings.MakaluDeviceName ?? (TabMakalu.Header as string) ?? Loc.Get("tab_makalu");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabMakalu.Header = name;
        AppSettings.SetMakaluDeviceName(name);
    }

    // ------------------------------------------------------------
    // Log
    // ------------------------------------------------------------

    private void LogMakalu(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        App.WriteLog("[Makalu] " + text);
        if (TxtMkLog == null) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtMkLog.AppendText(line + Environment.NewLine);
        TxtMkLog.ScrollToEnd();
    }
}
