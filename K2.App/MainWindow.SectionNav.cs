// MainWindow.SectionNav.cs — partial class: per-device section navigation
// (Everest, MacroPad, DisplayPad).
//
// Manages:
//  - Sidebar RadioButton selection → shows the matching bottom panel
//  - Debug mode toggle → reveals AP controls, USB Recorder section, SDK log
//
// Everest section panels (all in the same Grid cell, one Visible at a time):
//   PnlSecKeyMapping  — "Key Binding": mapped key list + capture/remap/configure,
//                       plus the generic HW-key capture utility (dock/crown/display
//                       keys are configured directly on the device graphic — see
//                       MainWindow.DockActions.cs and MainWindow.NumpadDisplayKeys.cs)
//   PnlSecRgb         — RGB preset effects + per-key custom lighting
//   PnlSecDial        — Display Dial page/clock/screensaver settings
//   PnlSecSettings    — general Everest settings: sync-across-profiles, Game Mode
//                       key-lock checkboxes, Core indicator LEDs, keyboard layout
//                       selector, factory reset (see MainWindow.Everest.cs)
//   PnlSecUsb         — USB Recorder (debug only)
//
// Keyboard macro CRUD/record/play used to live here (PnlSecMacros) but is now
// its own top-level section (PnlMacro), reachable via the dedicated Macro
// icon button next to Settings — see BtnMacroTab_Click in MainWindow.xaml.cs.
//
// MacroPad section panels: PnlMpSecKeyBinding (keys configured on the grid above),
//   PnlMpSecOrientation (rotation), PnlMpSecLed (RGB lighting), PnlMpSecSettings
//   (keycap appearance — same color/style controls as Everest's PnlSecSettings,
//   see MainWindow.MacroKeycapAppearance.cs)
// DisplayPad section panels: PnlDpSecKeyBinding (keys configured on the grid above),
//   PnlDpSecRotation (rotation + icon rotate)
//
// Brightness sliders (SldEvBrightness/SldMacroBrightness/SldDpBrightness/
// SldEv60Brightness/SldMkBrightness) live in the shared top-right brightness
// bar (BrEverest/BrMacroPad/BrDisplayPad/BrEverest60/BrMakalu in
// MainWindow.xaml), not in these per-device sections — see MainWindow.xaml.cs
// TcDevices_SelectionChanged for how that bar switches with the active tab.
// Everest 60/Makalu's sliders live in MainWindow (this partial class's
// siblings MainWindow.Everest60.cs/MainWindow.Makalu.cs) same as the other
// three, but the effect-apply logic they trigger stays inside those devices'
// own UserControls (Ev60RgbPanel/MkRgbSettings) via an internal
// Brightness/SetBrightness() property+method, since (unlike Everest/MacroPad/
// DisplayPad, which have no separate UserControl) that's where the rest of
// each device's RGB state already lives.
//
// Key-editing gate: clicking a key on the on-screen grid/keyboard only opens the
// action-configuration dialog while that device's Key Binding section is the
// active one (see EvKeyboardButton_Click, KeyButton_Click/ConfigureAction,
// DpKeyButton_Click and their context-menu equivalents). Elsewhere the click is
// a no-op (or, for DisplayPad folders, still just navigates).
//
// LED preview gate: the real-time LED color overlay (MainWindow.LedPreview.cs) is only
// meaningful while looking at "RGB & Lighting" (Everest) / "LED Lighting" (MacroPad) —
// ShowEvSection/ShowMpSection toggle _ledPoller.EverestEnabled/MacroPadEnabled accordingly
// so it isn't polled/painted elsewhere. For MacroPad this also avoids a key looking stuck
// gray after a physical press (see UpdateMpLedPreviewActive for the full explanation).

using System.Windows;
using System.Windows.Controls;

namespace K2.App;

public partial class MainWindow
{
    // ── Active section panel ──────────────────────────────────────────
    private FrameworkElement? _activeEvSection;

    // ── Called once by InitEverestModule ─────────────────────────────
    private void InitSectionNav()
    {
        // Show the default section (Key Mapping is IsChecked=True in XAML)
        ShowEvSection(PnlSecKeyMapping);
    }

    // ── RadioButton.Checked handler (all EvSections group) ───────────
    private void EvSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbSecKeyMapping) => PnlSecKeyMapping,
            nameof(RbSecRgb)        => PnlSecRgb,
            nameof(RbSecDial)       => PnlSecDial,
            nameof(RbSecSettings)   => PnlSecSettings,
            nameof(RbSecUsb)        => PnlSecUsb,
            _                       => null
        };

        if (panel is not null)
            ShowEvSection(panel);
    }

    private void ShowEvSection(FrameworkElement panel)
    {
        // Collapse previous section
        if (_activeEvSection is not null)
            _activeEvSection.Visibility = Visibility.Collapsed;

        // Show new section
        panel.Visibility  = Visibility.Visible;
        _activeEvSection  = panel;

        // LED color preview is only useful (and only polled) while looking at RGB & Lighting.
        UpdateEverestLedPreviewActive(panel == PnlSecRgb);
    }

    /// <summary>True while the Everest "Key Binding" section is the active one —
    /// gates whether clicking a key on the keyboard overlay opens the action dialog.</summary>
    private bool IsEvKeyBindingSectionActive => _activeEvSection == PnlSecKeyMapping;

    // ── MacroPad sidebar (Key Binding / Orientation / LED Lighting) ───
    private FrameworkElement? _activeMpSection;

    private void InitMpSectionNav() => ShowMpSection(PnlMpSecKeyBinding);

    private void MpSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbMpSecKeyBinding)  => PnlMpSecKeyBinding,
            nameof(RbMpSecOrientation) => PnlMpSecOrientation,
            nameof(RbMpSecLed)         => PnlMpSecLed,
            nameof(RbMpSecSettings)    => PnlMpSecSettings,
            _                          => null
        };

        if (panel is not null)
            ShowMpSection(panel);
    }

    private void ShowMpSection(FrameworkElement panel)
    {
        if (_activeMpSection is not null)
            _activeMpSection.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
        _activeMpSection = panel;

        // LED color preview is only useful (and only polled) while looking at LED Lighting —
        // mirrors ShowEvSection/UpdateEverestLedPreviewActive.
        UpdateMpLedPreviewActive(panel == PnlMpSecLed);
    }

    /// <summary>True while the MacroPad "Key Binding" section is the active one —
    /// gates whether clicking a key on the grid opens the action dialog.</summary>
    private bool IsMpKeyBindingSectionActive => _activeMpSection == PnlMpSecKeyBinding;

    // ── DisplayPad sidebar (Key Binding / Rotation) ───────────────────
    private FrameworkElement? _activeDpSection;

    private void InitDpSectionNav() => ShowDpSection(PnlDpSecKeyBinding);

    private void DpSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;

        // Null-guard: the default RadioButton's IsChecked="True" fires this Checked
        // event synchronously during InitializeComponent, before the panel fields
        // (declared later in the same XAML file) have been constructed/assigned yet.
        FrameworkElement? panel = rb.Name switch
        {
            nameof(RbDpSecKeyBinding) => PnlDpSecKeyBinding,
            nameof(RbDpSecRotation)   => PnlDpSecRotation,
            nameof(RbDpSecPages)      => PnlDpSecPages,
            _                         => null
        };

        if (panel is not null)
            ShowDpSection(panel);
    }

    private void ShowDpSection(FrameworkElement panel)
    {
        if (_activeDpSection is not null)
            _activeDpSection.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
        _activeDpSection = panel;

        // The page list can go stale (created/renamed from the "Page" action type in
        // ButtonActionDialog, which has no direct handle back to this list) — cheap enough
        // to just recompute it every time this section becomes the active one.
        if (panel == PnlDpSecPages)
            RefreshDpPagesList();
    }

    /// <summary>True while the DisplayPad "Key Binding" section is the active one —
    /// gates whether clicking a key on the grid opens the action dialog.</summary>
    private bool IsDpKeyBindingSectionActive => _activeDpSection == PnlDpSecKeyBinding;

    // ── Debug mode ─────────────────────────────────────────────────────
    // Driven centrally by the General Settings tab (MainWindow.Settings.cs) —
    // see AppSettings.DebugMode. No longer has its own per-device checkbox.
    private void ApplyDebugMode(bool debug)
    {
        var vis = debug ? Visibility.Visible : Visibility.Collapsed;

        // Toolbar: open/close driver + AP controls
        BtnEvOpen.Visibility    = vis;
        BtnEvClose.Visibility   = vis;
        SepEvApDbg.Visibility   = vis;
        BtnEvApOn.Visibility    = vis;
        BtnEvApOff.Visibility   = vis;

        // Key Mapping section: remap keys button (debug-only, see project rule)
        BtnEvMapKeys.Visibility    = vis;
        SepEvMapKeysDbg.Visibility = vis;

        // Sidebar: USB Recorder section
        SepSectionDbg.Visibility = vis;
        RbSecUsb.Visibility      = vis;

        // Settings panel: SDK log
        GbEvLog.Visibility = vis;

        // Common actions: Debug group (Refresh)
        PnlEvDebugGroup.Visibility = vis;

        // Toolbar: SDK/DLL info label
        SepEvSdkDbg.Visibility = vis;
        LblEvSdk.Visibility = vis;

        // If USB section was selected while we hide it, fall back to Key Mapping
        if (!debug && RbSecUsb.IsChecked == true)
        {
            RbSecKeyMapping.IsChecked = true;
            // Checked event fires automatically and calls ShowEvSection
        }
    }
}
