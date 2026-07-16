using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Models;
using K2.App.Services;
using K2.Core;

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

    /// <summary>Number of K2-side profile slots for Everest 60 — see the
    /// "Profile management" doc comment below (no firmware profile concept).</summary>
    private const int Ev60ProfileCount = 5;

    private Ev60ActionHost? _ev60ActionHost;
    private ButtonActionEngine? _ev60Engine;

    /// <summary>Reverse of <see cref="Everest60RemapData.LedIndexToDllKeyIdArray"/>
    /// (DllKeyId -> ledIndex), built once in <see cref="InitEverest60Module"/> —
    /// used to translate <see cref="Everest60SdkService.KeyEvent"/>'s wMatrix into
    /// the board's LED index. <b>Unverified on real hardware</b>: assumes the
    /// SDK's key-press callback reports the physical key by the same DllKeyId
    /// space used by the (now-retired) ChangeKey/ChangeFnKey remap exports —
    /// reasonable given both speak "DLLKeyId" consistently in Base Camp's own
    /// decompiled code, but never confirmed against a live callback. If this
    /// turns out false on real hardware, Everest Max's guided-remap capture
    /// flow (BtnEvMapKeys/_evWMatrixToLayout in MainWindow.Everest.cs) is the
    /// fallback pattern to port.</summary>
    private readonly Dictionary<int, int> _ev60DllKeyIdToLedIndex = new();

    /// <summary>Called once from the MainWindow constructor.</summary>
    private void InitEverest60Module()
    {
        _ev60 = new Everest60Service(LogEverest60);
        _ev60Store = new Everest60Store();

        Ev60RgbPanel.Init(_ev60, LogEverest60, _ev60Store, Ev60CurrentProfile);
        Ev60RgbPanel.CustomKeysCleared += ApplyEv60KeycapAppearanceToAllKeys;
        Ev60KeyBindingPanel.Init(_ev60Store, Ev60CurrentProfile, LogEverest60, () => _ev60LayoutType);
        InitEv60SectionNav();

        _ev60LayoutType = EverestKeyboardLayout.DetectLayout();
        BuildEverest60KeyboardOverlay();
        ApplyEv60NumpadPosition(Ev60NumpadPosition.None); // until the first poll completes
        InitEv60SettingsPanel();
        InitEv60KeyboardLayoutSelector();

        // DllKeyId -> ledIndex reverse map, for translating the SDK key
        // callback (see _ev60DllKeyIdToLedIndex's doc comment).
        var table = Everest60RemapData.LedIndexToDllKeyIdArray;
        for (int led = 0; led < table.Length; led++)
            _ev60DllKeyIdToLedIndex[table[led]] = led;

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

        _ev60Engine = new ButtonActionEngine(_ev60ActionHost);
        _ev60Engine.Start();

        Ev60RefreshProfiles();
        Ev60ReloadProfile(Ev60CurrentProfile());

        _ev60LedPoller = new Everest60LedColorPoller(_ev60);
        _ev60LedPoller.ColorsUpdated += OnEv60ColorsUpdated;

        Closed += (_, _) =>
        {
            try { _ev60LedPoller?.Dispose(); } catch { /* ignore */ }
            try { _ev60Engine?.Dispose(); } catch { /* ignore */ }
            try { _ev60Sdk.Dispose(); } catch { /* ignore */ }
        };

        // Eager open moved to Ev60AutoOpen(), called from AutoOpenDrivers()
        // once _hWnd is a real handle (see its doc comment) — this
        // constructor runs before OnSourceInitialized, so _hWnd is still
        // IntPtr.Zero here.
        _ev60PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _ev60PollTimer.Tick += (_, _) => Ev60RefreshStatus();
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
        public override string ToString() => Label;
    }

    private int Ev60CurrentProfile()
        => CbEv60Profile.SelectedItem is Ev60ProfileItem pi ? pi.Slot : 1;

    private void Ev60RefreshProfiles()
    {
        _ev60SuppressProfile = true;
        try
        {
            var items = Enumerable.Range(1, Ev60ProfileCount)
                .Select(s => new Ev60ProfileItem(s, _ev60Store.GetProfileName(s) ?? Loc.Get("profile_n", s)))
                .ToList();
            CbEv60Profile.DisplayMemberPath = nameof(Ev60ProfileItem.Label);
            CbEv60Profile.ItemsSource = items;

            int current = _ev60Store.GetCurrentProfile();
            CbEv60Profile.SelectedItem = items.Find(x => x.Slot == current) ?? items[0];
        }
        finally { _ev60SuppressProfile = false; }
    }

    private void Ev60SelectProfileSlot(int slot)
    {
        _ev60SuppressProfile = true;
        try
        {
            if (CbEv60Profile.ItemsSource is List<Ev60ProfileItem> items)
                CbEv60Profile.SelectedItem = items.Find(x => x.Slot == slot) ?? items[0];
        }
        finally { _ev60SuppressProfile = false; }
    }

    /// <summary>Pushes the given profile's stored lighting/key bindings into
    /// both panels and re-applies them to hardware (if connected/open).</summary>
    private void Ev60ReloadProfile(int slot)
    {
        Ev60RgbPanel.Ev60ReloadProfile(slot);
        Ev60KeyBindingPanel.Ev60ReloadKeyBindings(slot);
    }

    /// <summary>
    /// Resolves "Next"/"Previous"/"1..N" and switches the Everest 60's K2-side
    /// profile slot — mirrors MainWindow.Everest.cs's EvSwitchProfile, but
    /// there is no firmware call to make (see the "Profile management" doc
    /// comment above): switching just reloads the slot's stored keys/lighting,
    /// same as picking it from CbEv60Profile.
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
            LogEverest60($"[EXEC] profile: target \"{t}\" not resolved");
            return;
        }
        if (next == cur) { LogEverest60($"[EXEC] profile: already on {cur}"); return; }

        _ev60Store.SetCurrentProfile(next);
        Ev60SelectProfileSlot(next);
        Ev60ReloadProfile(next);
        LogEverest60($"[EXEC] profile -> {next}");
    }

    private void CbEv60Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ev60SuppressProfile) return;
        if (CbEv60Profile.SelectedItem is not Ev60ProfileItem pi) return;
        _ev60Store.SetCurrentProfile(pi.Slot);
        LogEverest60($"[UI ] Everest 60 profile selected: {pi.Slot}");
        Ev60ReloadProfile(pi.Slot);
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
        Ev60SelectProfileSlot(slot);
        Ev60ReloadProfile(slot);
    }

    /// <summary>Resets the currently selected profile's lighting and key bindings back to
    /// K2's defaults (see Everest60RgbPanel.RestoreDefaults / Everest60KeyBindingPanel.
    /// RestoreDefaults) and re-applies them to the keyboard if connected.</summary>
    private void BtnEv60RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        int slot = Ev60CurrentProfile();
        string profileName = _ev60Store.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_profile_confirm", profileName),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        Ev60RgbPanel.RestoreDefaults();
        Ev60KeyBindingPanel.RestoreDefaults();
        LogEverest60($"[UI ] Everest 60 profile {slot} restored to defaults.");
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

        var allProfiles = bcDevices.Values.SelectMany(x => x).OrderBy(p => p.Slot).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Base Camp -> K2 Everest 60 import:\n");
        foreach (var p in allProfiles)
            sb.AppendLine($"  Slot {p.Slot}: {p.Name}{(p.IsSelected ? " [ACTIVE]" : "")}");
        sb.AppendLine($"\nImport {allProfiles.Count} profile(s)?");

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int totalKeys = 0;
        int activeSlot = -1;
        int skippedNoSlot = 0;
        var usedSlots = new HashSet<int>(_ev60Store.GetExistingProfiles());
        foreach (var profile in allProfiles)
        {
            try
            {
                int targetSlot = BaseCampDbImporter.FindFreeSlot(usedSlots);
                if (targetSlot == 0) { skippedNoSlot++; continue; }
                usedSlots.Add(targetSlot);

                int keys = BaseCampDbImporter.ImportEverest60Profile(dbPath, profile, _ev60Store, targetSlot);
                totalKeys += keys;
                if (profile.IsSelected) activeSlot = targetSlot;
                LogEverest60($"[IMP-BC] slot {profile.Slot} '{profile.Name}' -> K2 slot {targetSlot}: keys={keys}");
            }
            catch (Exception ex) { LogEverest60($"[IMP-BC] slot {profile.Slot} error: {ex.Message}"); }
        }

        if (skippedNoSlot > 0)
            MessageBox.Show(this, Loc.Get("import_some_skipped_no_slot", skippedNoSlot),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (activeSlot > 0) _ev60Store.SetCurrentProfile(activeSlot);
        Ev60RefreshProfiles();
        int finalSlot = activeSlot > 0 ? activeSlot : Ev60CurrentProfile();
        Ev60SelectProfileSlot(finalSlot);
        Ev60ReloadProfile(finalSlot);
        LogEverest60(Loc.Get("ev60_imported_bc", allProfiles.Count, totalKeys));
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

            int imported = 0;
            foreach (var b in root.Descendants("Everest60KeyBidings"))
            {
                if (!int.TryParse(b.Element("DLLMatrixIndex")?.Value, out int ledIndex)) continue;

                // K2-only export (see the method doc comment): Ev60ProfileExporter.ExportK2
                // always writes FunctionType="K2Action", SubFunctionType=ActionType,
                // FunctionValue=ActionValue verbatim (same lossless round-trip as
                // EvProfileExporter's own K2Action branch, see MainWindow.Everest.cs's
                // BtnEvImportXml_Click) — no native Base Camp vocabulary to translate here.
                if (b.Element("FunctionType")?.Value != "K2Action") continue;
                string? actionType = b.Element("SubFunctionType")?.Value;
                if (string.IsNullOrEmpty(actionType)) continue;
                string? actionValue = b.Element("FunctionValue")?.Value;

                _ev60Store.SaveKey(new Ev60KeyRecord(slot, ledIndex, null, actionType, actionValue));
                imported++;
            }

            var lightingEl = root.Element("Everest60Lightings");
            if (lightingEl is not null)
            {
                int effIndex = int.TryParse(lightingEl.Element("EffIndex")?.Value, out var ei) ? ei : 1;
                var eff = effIndex switch
                {
                    1 => Everest60Protocol.Effect.Static, 2 => Everest60Protocol.Effect.Wave,
                    3 => Everest60Protocol.Effect.Tornado, 4 => Everest60Protocol.Effect.Breathing,
                    5 => Everest60Protocol.Effect.Reactive, 8 => Everest60Protocol.Effect.Yeti,
                    9 => Everest60Protocol.Effect.Off, _ => Everest60Protocol.Effect.Static,
                };
                string activeMode = effIndex == 7 ? "custom" : "preset";
                int color1 = BaseCampDbImporter.ParseBcColor(lightingEl.Element("Color1")?.Value, 0x900000);
                int color2 = BaseCampDbImporter.ParseBcColor(lightingEl.Element("Color2")?.Value, 0);
                int sideColor = BaseCampDbImporter.ParseBcColor(lightingEl.Element("Color3")?.Value, 0x900000);
                int speedPct = int.TryParse(lightingEl.Element("Speed")?.Value, out var sp) ? sp : 50;
                int dirIdx = int.TryParse(lightingEl.Element("Direction")?.Value, out var di) ? di : 0;
                double bright = int.TryParse(lightingEl.Element("Brightness")?.Value, out var br) ? br : 100;
                _ev60Store.SaveLighting(slot, new Ev60LightingRecord(
                    (int)eff, color1, color2, speedPct, dirIdx, false, bright, sideColor, bright, activeMode, new Dictionary<int, int>()));
            }

            _ev60Store.SetProfileName(slot, profileName);
            _ev60Store.SetCurrentProfile(slot);
            Ev60RefreshProfiles();
            Ev60SelectProfileSlot(slot);
            Ev60ReloadProfile(slot);
            LogEverest60($"[IMP-XML] '{profileName}' -> slot {slot}: {imported} key(s)");
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
        int? currentSlot = CbEv60Profile.SelectedItem is Ev60ProfileItem pi ? pi.Slot : null;

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
                Content = new TextBlock
                {
                    Text = kd.Label, Foreground = Brushes.White, FontSize = kd.W < 30 ? 6 : 7,
                    FontFamily = new FontFamily("Segoe UI,system-ui,Arial,sans-serif"),
                    TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
                Tag = kd.MatrixId, // LED index 0-63
                ToolTip = $"{kd.Label}  (LED {kd.MatrixId})",
            };
            btn.Click += Ev60KeyboardButton_Click;
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
                    Text = kd.Label, Foreground = Brushes.White, FontSize = kd.W < 30 ? 6 : 7,
                    FontFamily = new FontFamily("Segoe UI,system-ui,Arial,sans-serif"),
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
                IsHitTestVisible = false, // decorative only — no protocol for this accessory
                ToolTip = Loc.Get("ev60_numpad_decorative_tip"),
            };
            Canvas.SetLeft(btn, kd.X);
            Canvas.SetTop(btn, kd.Y);
            CvsEv60Numpad.Children.Add(btn);

            // Numpad gets Keycap Appearance too (base/text color + style
            // baseline) even though it's never painted — see
            // ApplyEv60KeycapAppearanceToAllKeys.
            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedHalo", btn) is Border halo)
                _ev60NumpadVisuals.Add(new KeyVisual(btn, halo));
        }
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

    private void OnEv60KeyboardLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbEv60KeyboardLayout.SelectedItem is not Ev60LayoutChoice c) return;
        if (c.Layout == _ev60LayoutType) return;
        _ev60LayoutType = c.Layout;
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
        if (_ev60KeycapEditMode && IsEv60SettingsSectionActive)
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
    /// OnEverestKey/HandleEverestKey. <b>Unverified on real hardware</b>: see
    /// _ev60DllKeyIdToLedIndex's doc comment for the wMatrix->ledIndex
    /// assumption this depends on.</summary>
    private void OnEv60Key(object? sender, (ushort WMatrix, bool Pressed, uint Id) e) =>
        Dispatcher.BeginInvoke(() => HandleEv60Key(e.WMatrix, e.Pressed));

    private void HandleEv60Key(ushort wMatrix, bool pressed)
    {
        if (AppSettings.LogLevel == K2LogLevel.Verbose)
            LogEverest60($"[KEY ] wMatrix=0x{wMatrix:X2} {(pressed ? "down" : "up")}");

        if (!_ev60DllKeyIdToLedIndex.TryGetValue(wMatrix, out int ledIndex)) return;

        Ev60HighlightKeyboardButton(ledIndex, pressed);

        var key = Ev60KeyBindingPanel.ByLed(ledIndex);
        if (key is null) return;

        key.IsHighlighted = pressed;
        if (pressed) ExecuteEv60Key(key);
    }

    /// <summary>Same "Tint" overlay approach as Everest Max's
    /// EvHighlightKeyboardButton (MainWindow.Everest.cs) — SetKeyTint/
    /// SetLegendForeground live in MainWindow.KeycapAppearance.cs and own
    /// Background/BorderBrush for the keycap-appearance system, so a plain
    /// assignment here would fight with custom color/live LED tint.</summary>
    private void Ev60HighlightKeyboardButton(int ledIndex, bool pressed)
    {
        if (!_ev60KeyVisuals.TryGetValue(ledIndex, out var v)) return;

        SetKeyTint(v.Button, pressed ? new SolidColorBrush(Color.FromRgb(0x5B, 0xBE, 0xC3)) : Brushes.Transparent);
        SetLegendForeground(v.Button, pressed ? Brushes.Black : new SolidColorBrush(ResolveEv60KeycapTextColor()));
    }

    /// <summary>Current numpad position, updated by Ev60RefreshStatus's poll
    /// — auto-detected, no manual toggle. Presence comes from raw HID
    /// (Everest60Service.TryGetNumpadPresent, 2026-07-13, replacing the SDK's
    /// GetSubDeviceInfo after it was found to reliably fail with a Makalu
    /// also connected — see Everest60Protocol.ReadNumpadPresent's doc
    /// comment); side (left/right) is NOT re-derived each poll, only
    /// presence is — see Ev60RefreshStatus for why.</summary>
    private Ev60NumpadPosition _ev60NumpadPosition = Ev60NumpadPosition.None;

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
            return;
        }

        CvsEv60Numpad.Visibility = Visibility.Visible;
        BrushEv60NumpadBg.RelativeTransform = position == Ev60NumpadPosition.Right
            ? new ScaleTransform(-1, 1, 0.5, 0.5)
            : Transform.Identity;

        // Reorder within SpEv60Layout: numpad before the keyboard for
        // "left", after it for "right".
        SpEv60Layout.Children.Remove(CvsEv60Numpad);
        int keyboardIdx = SpEv60Layout.Children.IndexOf(CvsEv60Keyboard);
        if (position == Ev60NumpadPosition.Left)
        {
            SpEv60Layout.Children.Insert(keyboardIdx, CvsEv60Numpad);
            CvsEv60Numpad.Margin = new Thickness(0, 0, 6, 0);
        }
        else // Right
        {
            SpEv60Layout.Children.Insert(keyboardIdx + 1, CvsEv60Numpad);
            CvsEv60Numpad.Margin = new Thickness(6, 0, 0, 0);
        }
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
        foreach (var (ledIndex, v) in _ev60KeyVisuals)
        {
            if (painting && Ev60RgbPanel.TryGetPaintedColor(ledIndex, out var paintedColor))
            {
                ApplyEv60KeyOverlay(v, paintedColor);
                continue;
            }

            if (ledIndex < 0 || ledIndex >= Everest60Protocol.LedIndex.Length) continue;
            int hwAddr = Everest60Protocol.LedIndex[ledIndex];
            if (hwAddr >= colors.Length) continue;

            var c = colors[hwAddr];
            // All-zero = LED off, same convention as Everest Max's
            // ApplyEverestLedColor (r/g/b all 0 rather than a "black lit" color).
            Color? live = c.r != 0 || c.g != 0 || c.b != 0 ? Color.FromRgb(c.r, c.g, c.b) : null;
            ApplyEv60KeyOverlay(v, live);
        }

        // Numpad accessory: read-only live preview via Everest60Protocol.NumpadLedIndex
        // (reverse-engineered 2026-07-12 from a real USBPcap capture, see its doc
        // comment) — _ev60NumpadVisuals is built in the same order as
        // Everest60KeyboardLayout.Numpad, which NumpadLedIndex mirrors, so the two
        // lists are index-aligned. No paint mode here: the numpad has no write
        // path yet (MatrixId=-1, not hit-testable), this is readback only.
        for (int i = 0; i < _ev60NumpadVisuals.Count && i < Everest60Protocol.NumpadLedIndex.Length; i++)
        {
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
        // 2026-07-13 (see Everest60Protocol.ReadColorData/ReadNumpadPresent's
        // doc comments), so this retry no longer gates either of those.
        if (connected && !_ev60Sdk.IsOpen)
        {
            bool opened = _ev60Sdk.Open(_hWnd, LogEverest60);
            if (opened)
                Ev60KeyBindingPanel.Ev60ReloadKeyBindings(Ev60CurrentProfile());
        }

        // Numpad auto-detect (2026-07-13, raw HID — see
        // Everest60Protocol.ReadNumpadPresent's doc comment): the SDK's
        // GetSubDeviceInfo was found to reliably fail whenever a Makalu is
        // also connected, real Base Camp captures showed this isn't a
        // separate SDK code path at all, just HID Feature Reports on the
        // SAME interface already used for lighting. Only presence is
        // confirmed from the captures available so far (both sessions that
        // captured this had the accessory on the right) — side is NOT
        // re-derived from the wire, it just keeps whatever side was already
        // known and defaults to Right the first time presence flips on, same
        // as the only physical unit seen in every capture/log so far.
        Ev60NumpadPosition numpadPos;
        if (!connected)
            numpadPos = Ev60NumpadPosition.None;
        else
        {
            bool? present = _ev60.TryGetNumpadPresent();
            numpadPos = present == true
                ? (_ev60NumpadPosition == Ev60NumpadPosition.None ? Ev60NumpadPosition.Right : _ev60NumpadPosition)
                : Ev60NumpadPosition.None;
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

    /// <summary>Per-key color/image overrides (KeyId = LED index, same identity as
    /// _ev60KeyVisuals; Esc = EscKeyId = 0) — see the Everest Max equivalent
    /// (_evKeycapOverrides in MainWindow.KeycapAppearance.cs) for the full doc. Main board only,
    /// not the decorative numpad (_ev60NumpadVisuals has no per-key identity).</summary>
    private readonly Dictionary<int, KeycapOverrideRecord> _ev60KeycapOverrides = new();

    /// <summary>"Edit individual keycaps" checkbox — see the Everest Max equivalent
    /// (_evKeycapEditMode in MainWindow.KeycapAppearance.cs) for the full doc.</summary>
    private bool _ev60KeycapEditMode;

    private void CkEv60KeycapEditMode_Click(object sender, RoutedEventArgs e) =>
        _ev60KeycapEditMode = CkEv60KeycapEditMode.IsChecked == true;

    /// <summary>True while the Everest 60 "Settings" section is active — gates whether clicking
    /// a key opens KeycapCustomizeDialog (only when "Edit individual keycaps" is also checked).</summary>
    private bool IsEv60SettingsSectionActive => ReferenceEquals(_activeEv60Section, PnlEv60Settings);

    /// <summary>Opens KeycapCustomizeDialog for the given key (KeyId = LED index) — see the
    /// Everest Max equivalent (OpenEvKeycapCustomizeDialog) for the full doc.</summary>
    private void OpenEv60KeycapCustomizeDialog(int keyId, string label)
    {
        _ev60KeycapOverrides.TryGetValue(keyId, out var current);
        var dlg = new KeycapCustomizeDialog(label, keyId == EscKeyId, current?.ColorHex, current?.ImagePath) { Owner = this };
        dlg.Changed += () =>
        {
            if (dlg.ColorHex is null && dlg.ImagePath is null)
            {
                _ev60Store.ClearKeycapOverride(keyId);
                _ev60KeycapOverrides.Remove(keyId);
            }
            else
            {
                _ev60Store.SetKeycapOverride(keyId, dlg.ColorHex, dlg.ImagePath);
                _ev60KeycapOverrides[keyId] = new KeycapOverrideRecord(keyId, dlg.ColorHex, dlg.ImagePath);
            }
            ApplyEv60KeycapAppearanceToAllKeys();
        };
        dlg.ShowDialog();
    }

    private void InitEv60SettingsPanel()
    {
        _ev60SettingsSuppress = true;
        try
        {
            CbEv60KeycapStyle.ItemsSource       = KeycapStyleChoices;
            CbEv60KeycapStyle.DisplayMemberPath = "Label";

            _ev60KeycapColorMode = ParseKeycapColorMode(_ev60Store.GetSetting("settings.keycap_color_mode"), KeycapColorMode.Black);
            _ev60KeycapCustomHex = _ev60Store.GetSetting("settings.keycap_custom_hex") is { Length: > 0 } hex ? hex : "#404040";
            _ev60KeycapTextColorMode = ParseKeycapColorMode(_ev60Store.GetSetting("settings.keycap_text_color_mode"), KeycapColorMode.White);
            _ev60KeycapTextCustomHex = _ev60Store.GetSetting("settings.keycap_text_custom_hex") is { Length: > 0 } txt ? txt : "#FFFFFF";

            // Migration — see the Everest Max equivalent in LoadKeycapAppearanceFromStore
            // (MainWindow.KeycapAppearance.cs) for the full explanation of the old 4-value scheme.
            int rawStyle = int.TryParse(_ev60Store.GetSetting("settings.keycap_style"), out var s) ? s : 0;
            if (_ev60Store.GetSetting("settings.keycap_translucent_legend") is not { } translucentRaw)
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
            foreach (var (keyId, rec) in _ev60Store.LoadAllKeycapOverrides())
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
    /// style-dependent "live" overlay (ApplyEv60KeyOverlay) using each main-
    /// board key's currently painted color if any (numpad keys are never
    /// painted, so they always render the style's "off" state). Mirrors
    /// Everest Max's two-phase ApplyKeycapAppearanceToAllKeys/ApplyEverestLedColor
    /// split (MainWindow.KeycapAppearance.cs), just triggered on-demand
    /// (paint click, settings change) instead of a continuous poll tick.
    /// </summary>
    private void ApplyEv60KeycapAppearanceToAllKeys()
    {
        var defaultKeycapBrush = new SolidColorBrush(ResolveEv60KeycapColor());
        var textBrush          = new SolidColorBrush(ResolveEv60KeycapTextColor());

        foreach (var (ledIndex, v) in _ev60KeyVisuals)
        {
            _ev60KeycapOverrides.TryGetValue(ledIndex, out var ov);
            var keycapBrush = ov?.ColorHex is { Length: > 0 } hex && TryParseKeycapHexColor(hex, out var c2)
                ? new SolidColorBrush(c2)
                : defaultKeycapBrush;

            ApplyEv60KeyBaseline(v, keycapBrush, textBrush);
            Color? painted = Ev60RgbPanel.TryGetPaintedColor(ledIndex, out var c) ? c : null;
            ApplyEv60KeyOverlay(v, painted);

            _ev60OriginalKeyContent.TryGetValue(ledIndex, out var original);
            ApplyKeycapImageOverride(v.Button, original, ov?.ImagePath);
        }
        foreach (var v in _ev60NumpadVisuals)
        {
            ApplyEv60KeyBaseline(v, defaultKeycapBrush, textBrush);
            ApplyEv60KeyOverlay(v, null);
        }
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
