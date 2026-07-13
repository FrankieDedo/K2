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
        if (dockPos != 0)
        {
            GrdEvDock.Visibility = Visibility.Visible;
            // Align to the side where it is physically connected: 1=right, 2=left
            GrdEvDock.HorizontalAlignment = dockPos == 2
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }
        else
        {
            GrdEvDock.Visibility = Visibility.Collapsed;
        }

        // ---- Numpad (left or right of the keyboard column) ----
        if (numpadPos == 0)
        {
            CvsEvNumpad.Visibility = Visibility.Collapsed;
        }
        else
        {
            CvsEvNumpad.Visibility = Visibility.Visible;

            // Reorder SpEvLayout children: [GrdEvKeyColumn] and [CvsEvNumpad]
            SpEvLayout.Children.Clear();

            if (numpadPos == 2) // left
            {
                CvsEvNumpad.Margin = new Thickness(0, 0, 6, 0);
                SpEvLayout.Children.Add(CvsEvNumpad);
                SpEvLayout.Children.Add(GrdEvKeyColumn);
            }
            else // 1 or other = right (default)
            {
                CvsEvNumpad.Margin = new Thickness(6, 0, 0, 0);
                SpEvLayout.Children.Add(GrdEvKeyColumn);
                SpEvLayout.Children.Add(CvsEvNumpad);
            }
        }
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
