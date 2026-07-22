// MainWindow.Layout.cs — partial class: dynamic keyboard layout.
//
// Media Dock / Display Dial: image (keytop.png) overlaid by half on the
// top edge of the keyboard body, horizontally centered.
//
// Numpad: positioned to the left or right of the keyboard+dock column.
//   byNumpadPlug: 0=hidden, 1=right, 2=left
//   byMMDockPlug: 0=hidden, ≥1=connected
//
// Tab auto-naming: both accessories -> "Everest Max", neither -> "Everest
// Core", only one -> plain "Everest". Skipped if the user manually renamed
// the tab (device.name setting present) via BtnEvRename_Click.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>
    /// Poll timer for dock/numpad attach detection, active only while the
    /// Everest tab is the selected top-level tab (started/stopped from
    /// TcDevices_SelectionChanged) — unlike Everest 60's poller (see
    /// MainWindow.Everest60.cs), which runs unconditionally, so Everest Max
    /// stays consistent with the narrower "K2 startup + tab open + poll while
    /// visible" trigger set requested for this device.
    /// </summary>
    private DispatcherTimer? _evAccessoryPollTimer;

    /// <summary>Last known dock/numpad attach state — cached here (not just passed
    /// through locally) so the Home tab's tile (MainWindow.Home.cs, EvHomeImageFile)
    /// can pick the right artwork without re-querying the SDK: UpdateKeyboardLayout
    /// only runs at Everest open + every 3s while the Everest tab itself is selected
    /// (see StartEvAccessoryPoll), so this is the freshest info available while the
    /// user is elsewhere, e.g. on Home.</summary>
    private bool _evDockConnected;
    private bool _evNumpadConnected;

    /// <summary>Last numpad side (0=hidden, 1=right, 2=left) — cached so
    /// <see cref="ApplyNumpadGap"/> can be called from MainWindow.CustomLighting.cs's
    /// border-overlay toggle too, without re-querying the SDK.</summary>
    private int _evNumpadPos = 1;

    /// <summary>Whether a Media Dock/Display Dial accessory is physically attached
    /// (dockPos != 0 in the last poll) — separate from GrdEvDock's actual Visibility,
    /// which also depends on Custom Lighting's paint mode (see UpdateDockVisibility).</summary>
    private bool _evDockPhysicallyConnected;

    /// <summary>
    /// Starts the 3s dock/numpad poll (same cadence as Ev60RefreshStatus).
    /// Idempotent — safe to call every time the Everest tab is (re)selected.
    /// </summary>
    private void StartEvAccessoryPoll()
    {
        if (_evAccessoryPollTimer != null) return;
        _evAccessoryPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _evAccessoryPollTimer.Tick += (_, _) => UpdateKeyboardLayout();
        _evAccessoryPollTimer.Start();
    }

    /// <summary>Stops the poll timer once the Everest tab is no longer active.</summary>
    private void StopEvAccessoryPoll()
    {
        _evAccessoryPollTimer?.Stop();
        _evAccessoryPollTimer = null;
    }

    /// <summary>
    /// Detects dock and numpad positions and updates visibility/order.
    /// Called after SDK connection.
    /// </summary>
    private void UpdateKeyboardLayout()
    {
        if (_everest is null) return;
        // Skip while an NDK picture upload is in flight — the firmware transiently reports
        // both accessories unplugged while it's busy writing to flash (see _ndkUploadBusy in
        // MainWindow.NumpadDisplayKeys.cs), which would otherwise flicker them out of the UI
        // until the next successful poll. NdkApplyImage re-runs this once the write settles.
        if (_ndkUploadBusy) return;

        byte dockPos   = _everest.MMDockPlugPosition();
        byte numpadPos = _everest.NumpadPlugPosition();

        LogEverest($"[LAYOUT] dockPos={dockPos} numpadPos={numpadPos}");

        UpdateEverestAutoName(dockPos != 0, numpadPos != 0);

        bool dockConnected = dockPos != 0;
        bool numpadConnected = numpadPos != 0;
        if (dockConnected != _evDockConnected || numpadConnected != _evNumpadConnected)
        {
            _evDockConnected = dockConnected;
            _evNumpadConnected = numpadConnected;
            RefreshHomeTiles();
        }

        // ---- Dock (overlaid on the top edge of the keyboard) ----
        // GrdEvDock carries both the artwork (ImgEvDock) and the clickable
        // hotspots (CvsEvDock: media buttons + crown rotation buttons), so they
        // move together when the dock's physical side changes.
        _evDockPhysicallyConnected = dockPos != 0;
        PnlHwCaptureBar.Visibility = _evDockPhysicallyConnected ? Visibility.Visible : Visibility.Collapsed;
        if (_evDockPhysicallyConnected)
        {
            // Align to the side where it is physically connected: 1=right, 2=left
            GrdEvDock.HorizontalAlignment = dockPos == 2
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }
        UpdateDockVisibility();

        // ---- Numpad (left or right of the keyboard column) ----
        if (numpadPos == 0)
        {
            CvsEvNumpad.Visibility = Visibility.Collapsed;
            // Keep the Custom Lighting border overlay in sync: its numpad squares are
            // a separate canvas from CvsEvNumpad and would otherwise keep floating
            // where the numpad used to be (see UpdateBorderOverlayVisibility).
            UpdateBorderOverlayVisibility();
        }
        else
        {
            CvsEvNumpad.Visibility = Visibility.Visible;

            // Reorder SpEvLayout children: [GrdEvKeyColumn] and [GrdEvNumpadColumn]
            // (the latter wraps CvsEvNumpad — see MainWindow.xaml — so it, not
            // CvsEvNumpad itself, is SpEvLayout's actual direct child).
            SpEvLayout.Children.Clear();
            _evNumpadPos = numpadPos;

            if (numpadPos == 2) // left
            {
                SpEvLayout.Children.Add(GrdEvNumpadColumn);
                SpEvLayout.Children.Add(GrdEvKeyColumn);
            }
            else // 1 or other = right (default)
            {
                SpEvLayout.Children.Add(GrdEvKeyColumn);
                SpEvLayout.Children.Add(GrdEvNumpadColumn);
            }
            // Also re-syncs the Custom Lighting border overlay (its numpad squares
            // follow attach/detach) and the gap — UpdateBorderOverlayVisibility ends
            // with ApplyNumpadGap itself.
            UpdateBorderOverlayVisibility();
        }
    }

    /// <summary>
    /// Sets GrdEvNumpadColumn's margin — the gap between it and GrdEvKeyColumn — based
    /// on <see cref="_evNumpadPos"/> (which side the numpad sits on) and whether the
    /// Custom Lighting border overlay is currently showing (needs a wider gap so its
    /// squares, which extend past each canvas's edge, don't overlap — see
    /// MainWindow.CustomLighting.cs's UpdateBorderOverlayVisibility, the other caller).
    /// Centralized here because <see cref="UpdateKeyboardLayout"/> runs on a 3s poll
    /// timer and used to unconditionally stomp a hardcoded margin over whatever
    /// UpdateBorderOverlayVisibility had just set (user-reported bug 2026-07-22:
    /// "quando allargo la finestra, numpad e tastiera si riavvicinano" — really any
    /// poll tick, not specifically resize, just noticed then).
    /// </summary>
    /// <summary>
    /// GrdEvDock (Media Dock/Display Dial artwork + its media/crown hotspots) is only
    /// shown when the accessory is physically attached AND Custom Lighting's paint mode
    /// isn't active — the dock sits right above the keyboard body where the border
    /// overlay's top-row squares also live, so the two visually clash (user request
    /// 2026-07-22: "quando attivi la modalità custom... nascondi anche media dock e
    /// relativi tasti"). Called from UpdateKeyboardLayout (accessory poll) and from
    /// MainWindow.CustomLighting.cs's SetCustomPaintModeActive (paint-mode toggle) —
    /// same "two callers, one source of truth" pattern as ApplyNumpadGap below.
    /// </summary>
    private void UpdateDockVisibility()
    {
        GrdEvDock.Visibility = (_evDockPhysicallyConnected && !_customPaintMode)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyNumpadGap()
    {
        bool wide = CvsEvBorderMain.Visibility == Visibility.Visible;
        double gap = wide ? 36 : 6;
        GrdEvNumpadColumn.Margin = _evNumpadPos == 2
            ? new Thickness(0, 0, gap, 0)   // numpad on the left: extra gap on its right edge
            : new Thickness(gap, 0, 0, 0);  // numpad on the right (default): extra gap on its left edge
    }

    /// <summary>
    /// Auto-renames the Everest tab based on the connected accessories,
    /// unless the user has already set a custom name via "Rename".
    /// </summary>
    private void UpdateEverestAutoName(bool dockConnected, bool numpadConnected)
    {
        if (!string.IsNullOrEmpty(_evStore.GetSetting("device.name"))) return;

        string autoName = (dockConnected, numpadConnected) switch
        {
            (true, true)   => Loc.Get("tab_everest_max"),
            (false, false) => Loc.Get("tab_everest_core"),
            _              => Loc.Get("tab_everest"),
        };

        TabEverest.Header = autoName;
    }
}
