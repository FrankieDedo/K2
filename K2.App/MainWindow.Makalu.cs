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
    // Mirrors CbEvProfile_SelectionChanged/EvRefreshProfiles/EvSelectProfileSlot
    // in MainWindow.Everest.cs.
    // ------------------------------------------------------------

    private sealed record MkProfileItem(int Slot, string Label)
    {
        public override string ToString() => Label;
    }

    private int MkCurrentProfile()
        => CbMkProfile.SelectedItem is MkProfileItem pi ? pi.Slot : 1;

    private void MkRefreshProfiles()
    {
        _mkSuppressProfile = true;
        try
        {
            var items = Enumerable.Range(1, 5)
                .Select(s => new MkProfileItem(s, _mkStore.GetProfileName(s) ?? Loc.Get("profile_n", s)))
                .ToList();
            CbMkProfile.DisplayMemberPath = nameof(MkProfileItem.Label);
            CbMkProfile.ItemsSource = items;

            int current = _mkStore.GetCurrentProfile();
            CbMkProfile.SelectedItem = items.Find(x => x.Slot == current) ?? items[0];
        }
        finally { _mkSuppressProfile = false; }
    }

    private void MkSelectProfileSlot(int slot)
    {
        _mkSuppressProfile = true;
        try
        {
            if (CbMkProfile.ItemsSource is List<MkProfileItem> items)
                CbMkProfile.SelectedItem = items.Find(x => x.Slot == slot) ?? items[0];
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

    private void CbMkProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mkSuppressProfile) return;
        if (CbMkProfile.SelectedItem is not MkProfileItem pi) return;
        _mkStore.SetCurrentProfile(pi.Slot);
        LogMakalu($"[UI ] Makalu profile selected: {pi.Slot}");
        MkReloadProfile(pi.Slot);
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
        MkSelectProfileSlot(slot);
        MkReloadProfile(slot);
    }

    /// <summary>Resets the currently selected profile's button remap, lighting, DPI and
    /// device settings back to K2's defaults and re-applies them to the mouse if
    /// connected (see MakaluRgbSettingsPanel.RestoreDefaults / MakaluDpiRemapPanel.
    /// MkReloadRemap, which falls back to MakaluRemapData.RemapDefaults once the stored
    /// remap rows are gone).</summary>
    private void BtnMkRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        int slot = MkCurrentProfile();
        string profileName = _mkStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_profile_confirm", profileName),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _mkStore.ResetKeyRemap(slot);
        MkRgbSettings.RestoreDefaults();
        MkDpiRemap.MkReloadRemap(slot);
        LogMakalu($"[UI ] Makalu profile {slot} restored to defaults.");
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

        var allProfiles = bcDevices.Values.SelectMany(x => x).OrderBy(p => p.Slot).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Base Camp -> K2 Makalu import:\n");
        foreach (var p in allProfiles)
            sb.AppendLine($"  Slot {p.Slot}: {p.Name}{(p.IsSelected ? " [ACTIVE]" : "")}");
        sb.AppendLine($"\nImport {allProfiles.Count} profile(s)?");

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int totalRemap = 0;
        int activeSlot = -1;
        foreach (var profile in allProfiles)
        {
            try
            {
                var (remap, lighting, settings) = BaseCampDbImporter.ImportMakaluProfile(dbPath, profile, _mkStore);
                totalRemap += remap;
                if (profile.IsSelected) activeSlot = profile.Slot;
                LogMakalu($"[IMP-BC] slot {profile.Slot} '{profile.Name}': remap={remap} lighting={lighting} settings={settings}");
            }
            catch (Exception ex) { LogMakalu($"[IMP-BC] slot {profile.Slot} error: {ex.Message}"); }
        }

        if (activeSlot > 0) _mkStore.SetCurrentProfile(activeSlot);
        MkRefreshProfiles();
        int finalSlot = activeSlot > 0 ? activeSlot : MkCurrentProfile();
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

            int slot = 1;
            if (int.TryParse(root.Element("Id")?.Value, out var n) && n >= 1 && n <= 5) slot = n;
            string profileName = root.Element("ProfileName")?.Value
                                  ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            int remapped = 0;
            foreach (var b in root.Descendants("MakaluKeyBindings"))
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

            var lightingEl = root.Element("MakaluLightings");
            if (lightingEl is not null)
            {
                string effectName = lightingEl.Element("EffectName")?.Value ?? "Static";
                bool customActive = effectName.Equals("Custom", StringComparison.OrdinalIgnoreCase);
                var eff = Enum.TryParse<MakaluProtocol.Effect>(effectName, true, out var e2) ? e2 : MakaluProtocol.Effect.Static;
                int color1 = BaseCampDbImporter.ParseBcColor(lightingEl.Element("DualColor1")?.Value ?? lightingEl.Element("SingleColor")?.Value, 0x900000);
                int color2 = BaseCampDbImporter.ParseBcColor(lightingEl.Element("DualColor2")?.Value, 0);
                int speedIdx = int.TryParse(lightingEl.Element("Speed")?.Value, out var sp) ? sp : 1;
                int dirIdx = int.TryParse(lightingEl.Element("Direction")?.Value, out var di) ? di : 0;
                double bright = int.TryParse(lightingEl.Element("Brightness")?.Value, out var br) ? br : 100;
                _mkStore.SaveLighting(slot, new MakaluLightingRecord(
                    (int)eff, color1, color2, speedIdx, dirIdx, bright, customActive, new int[8]));
            }

            var settingsEl = root.Element("MakaluSettings");
            if (settingsEl is not null)
            {
                int pollHz = int.TryParse(settingsEl.Element("PollingRate")?.Value, out var ph) ? ph : 1000;
                int debMs = int.TryParse(settingsEl.Element("ButtonResponseTime")?.Value, out var dm) ? dm : 2;
                bool angleOn = settingsEl.Element("AngleSnapping")?.Value == "On";
                bool liftHigh = settingsEl.Element("LiftOffDistance")?.Value == "High";
                _mkStore.SaveSettings(slot, new MakaluDeviceSettingsRecord(pollHz, debMs, angleOn, liftHigh));
            }

            var dpiEls = root.Elements("DPILevels").ToList();
            if (dpiEls.Count > 0)
            {
                var levels = new int[5];
                int active = 0;
                for (int i = 0; i < dpiEls.Count && i < 5; i++)
                {
                    levels[i] = int.TryParse(dpiEls[i].Element("DPI")?.Value, out var d) ? d : levels[Math.Max(0, i - 1)];
                    if (dpiEls[i].Element("IsSelected")?.Value == "true") active = i;
                }
                _mkStore.SaveDpi(slot, new MakaluDpiRecord(levels, active));
            }

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
        int? currentSlot = CbMkProfile.SelectedItem is MkProfileItem pi ? pi.Slot : null;

        ExportProfileHelper.Run(
            owner: this,
            deviceLabel: "Makalu",
            profiles: profiles,
            currentSlot: currentSlot,
            exportOne: (slot, name, bcCompatible, path) =>
            {
                var result = bcCompatible
                    ? MkProfileExporter.ExportBaseCamp(_mkStore, slot, name, path)
                    : MkProfileExporter.ExportK2(_mkStore, slot, name, path);
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
        // Ring section hidden for now (2026-07-14, user request) — always use the
        // opaque rainbow image so the transparent ring cutout is never exposed;
        // the ring still renders behind it (BuildMkLedRing), just not visible.
        string file = "makalu_mouse_rainbow.png";
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

    private void BuildMkHotspots()
    {
        CvsMkHotspots.Children.Clear();
        BuildMkLedRing();
        var names = MakaluRemapData.BtnNames(_mkInfo.Model);
        foreach (var kv in MkHotspotPos)
        {
            int btnIdx = kv.Key;
            var (x, y) = kv.Value;
            var dot = new Ellipse
            {
                Width = 22, Height = 22,
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Stroke = (Brush)FindResource("K2AccentBrush"),
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                ToolTip = names.TryGetValue(btnIdx, out var key) ? Loc.Get(key) : $"#{btnIdx}",
            };
            dot.MouseLeftButtonUp += (_, _) => MkHotspotClicked(btnIdx);
            Canvas.SetLeft(dot, x - dot.Width / 2);
            Canvas.SetTop(dot, y - dot.Height / 2);
            CvsMkHotspots.Children.Add(dot);
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

        MkUpdateLedRingPreview();
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

        StopMkRainbowChase();
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

        var caps = MakaluRgbSettingsPanel.CapsFor(s.Effect);
        double dur = MkRingSpeedSeconds[Math.Clamp(s.SpeedIdx, 0, MkRingSpeedSeconds.Length - 1)];

        if (caps.Direction) // Rainbow / Color Wave: chase sequence across the 8 discrete LEDs
        {
            _mkRainbowDegPerSec = 360.0 / (dur * 2) * (s.DirIdx == 0 ? -1 : 1);
            _mkRainbowBrightness = s.Brightness;
            StartMkRainbowChase();
        }
        else if (caps.Color2) // Breathing / Yeti: all 8 cells pulse between the two colors in sync
        {
            var brush = new SolidColorBrush(MkScaleColor(s.Color1, s.Brightness));
            foreach (var cell in _mkLedCells) cell!.Background = brush;
            var anim = new ColorAnimation(MkScaleColor(s.Color1, s.Brightness), MkScaleColor(s.Color2, s.Brightness), TimeSpan.FromSeconds(dur))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else if (caps.Speed) // RGB Breathing: fixed rainbow spread across the 8 LEDs, whole ring breathing in/out
        {
            for (int c = 0; c < 8; c++)
                _mkLedCells[c]!.Background = new SolidColorBrush(MkHueColor(MkCellLed[c] * 45.0, s.Brightness));
            var anim = new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(dur))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            _mkLedRingHost.BeginAnimation(UIElement.OpacityProperty, anim);
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
