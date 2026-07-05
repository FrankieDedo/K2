// MainWindow.SectionNav.cs — partial class: Everest section navigation.
//
// Manages:
//  - Sidebar RadioButton selection → shows the matching bottom panel
//  - Debug mode toggle → reveals AP controls, USB Recorder section, SDK log
//
// Section panels (all in the same Grid cell, one Visible at a time):
//   PnlSecKeyMapping  — mapped key list + capture/remap/configure
//   PnlSecRgb         — RGB preset effects + per-key custom lighting
//   PnlSecDial        — Display Dial page/clock/screensaver settings
//   PnlSecDock        — Dock + numpad display key actions
//   PnlSecMacros      — Keyboard macro CRUD / record / play
//   PnlSecUsb         — USB Recorder (debug only)

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
            nameof(RbSecDock)       => PnlSecDock,
            nameof(RbSecMacros)     => PnlSecMacros,
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
    }

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

        // If USB section was selected while we hide it, fall back to Key Mapping
        if (!debug && RbSecUsb.IsChecked == true)
        {
            RbSecKeyMapping.IsChecked = true;
            // Checked event fires automatically and calls ShowEvSection
        }
    }
}
