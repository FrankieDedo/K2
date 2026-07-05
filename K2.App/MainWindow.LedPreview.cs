using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Models;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: real-time LED color preview across all connected devices
/// (Everest + MacroPad). Replicates Base Camp's behavior where a semi-transparent
/// overlay on each key shows the current LED color, updated via periodic SDK polling.
///
/// Implementation: instead of a Canvas Rectangle overlay (which would render above the
/// text label), we use the named "LedTint" Border inside each button's ControlTemplate.
/// That Border sits below the ContentPresenter, so text is always above the LED color.
///
/// MAPPING NOTE:
///   Button.Tag contains VK codes (used for SDK callbacks), but GetColorData
///   returns an array indexed by LIGHTING matrixId (different from VK!).
///   The VK→LED index translation is in LedMatrixMapping.
/// </summary>
public partial class MainWindow
{
    private LedColorPoller? _ledPoller;

    /// <summary>
    /// Number of SDKDLL.dll crashes recovered since app start.
    /// After 2+ recoveries the LED preview is NOT restarted to avoid crash loops.
    /// </summary>
    private int _everestCrashCount;

    /// <summary>Maps ledIndex (GetColorData) → LedTint border for the Everest preview.</summary>
    private readonly Dictionary<int, Border> _evKeyTints = new();

    /// <summary>Maps key index (0..11) → LedTint border for the MacroPad preview.</summary>
    private readonly Dictionary<int, Border> _mpKeyTints = new();

    private int _evColorLogCount;  // log only the first N ticks for diagnostics


    /// <summary>
    /// Initializes the poller and LED overlays. Must be called AFTER InitKeysModule and
    /// InitEverestModule (which build the Canvas and button maps).
    /// </summary>
    private void InitLedPreview()
    {
        _ledPoller = new LedColorPoller(_everest, _macroPad);
        _ledPoller.EverestColorsUpdated += OnEverestColorsUpdated;
        _ledPoller.MacroPadColorsUpdated += OnMacroPadColorsUpdated;
        _ledPoller.SdkCrashDetected += OnSdkCrashDetected;

        // Capture LedTint borders from Everest buttons
        BuildEverestLedTints(CvsEvKeyboard, LedMatrixMapping.EverestKeyboard);
        BuildEverestLedTints(CvsEvNumpad, LedMatrixMapping.EverestNumpad);

        // Capture LedTint borders from MacroPad buttons
        BuildMacroPadLedTints();

        App.WriteLog($"[LED] LedTint borders  Everest: {_evKeyTints.Count}  MacroPad: {_mpKeyTints.Count}");
    }

    /// <summary>Starts LED polling (call when the lighting tab is visible).</summary>
    private void StartLedPreview()
    {
        if (_ledPoller == null) return;
        _ledPoller.MacroPadEnabled = _macroPad.IsOpen;
        if (CurrentDeviceId() is int id)
            _ledPoller.MacroPadSlot = (uint)id;

        // EVEREST LED PREVIEW — re-enabled after VEH instruction-skip fix (App.xaml.cs).
        //
        // The crash at SDKDLL.dll+0x5133 (MOV [ESP+0x14], EDX — DLL internal thread,
        // stack-top write fault) is handled by a targeted instruction skip: the VEH
        // advances EIP past the failed store without modifying registers, then returns
        // EXCEPTION_CONTINUE_EXECUTION. The DLL thread survives — no recovery needed.
        //
        // Investigation proved the crash is independent of color streaming (same RVA and
        // timing even with EverestEnabled=false). Disabling GetColorData was unnecessary.
        if (_everest.IsOpen)
        {
            _everest.SetSyncEffect(false, 50);
            _everest.SetSyncEffect(true,  50);
            _everest.EnableColorStream(10);
            _ledPoller.EverestEnabled = true;
            _everest.SetBacklight(true);
        }

        // MacroPad: same Everest sequence (GetFWLayout + SetSyncEffect + AP + backlight)
        if (_macroPad.IsOpen && CurrentDeviceId() is int mpId)
        {
            uint uid = (uint)mpId;

            try
            {
                int mpLayout = 0;
                bool fl = MacroPadSdkNative.GetFWLayout(ref mpLayout, uid);
                App.WriteLog($"[LED] MP GetFWLayout({uid}) -> {fl}  layout={mpLayout}");
            }
            catch (Exception ex) { App.WriteLog("[LED] MP GetFWLayout threw: " + ex); }

            _macroPad.APEnable(uid, true);

            try
            {
                bool sf = MacroPadSdkNative.SetSyncEffect(false, 50, uid);
                bool st = MacroPadSdkNative.SetSyncEffect(true, 50, uid);
                App.WriteLog($"[LED] MP SetSyncEffect({uid}) (false,50)->{sf}  (true,50)->{st}");
            }
            catch (Exception ex) { App.WriteLog("[LED] MP SetSyncEffect threw: " + ex); }

            _macroPad.SetBacklight(uid, true);
        }

        _ledPoller.Start();
        App.WriteLog($"[LED] StartLedPreview  ev={_ledPoller.EverestEnabled} mp={_ledPoller.MacroPadEnabled}  tints={_evKeyTints.Count}+{_mpKeyTints.Count}");
    }

    /// <summary>Stops polling (when leaving the tab or closing).</summary>
    private void StopLedPreview()
    {
        _ledPoller?.Stop();
    }

    // ==================================================================
    // LedTint collection — Everest
    // ==================================================================

    private void BuildEverestLedTints(Canvas canvas, Dictionary<int, int> vkToLedMap)
    {
        foreach (UIElement child in canvas.Children)
        {
            if (child is not Button btn || btn.Tag is not int vk) continue;
            if (!vkToLedMap.TryGetValue(vk, out int ledIndex)) continue;

            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedTint", btn) is Border tint)
                _evKeyTints[ledIndex] = tint; // last VK wins if multiple map to same ledIndex
        }
    }

    // ==================================================================
    // LedTint collection — MacroPad
    // ==================================================================

    private void BuildMacroPadLedTints()
    {
        for (int i = 0; i < _keyButtons.Length; i++)
        {
            var btn = _keyButtons[i];
            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedTint", btn) is Border tint)
                _mpKeyTints[i] = tint;
        }
    }

    // ==================================================================
    // Color update handlers
    // ==================================================================

    private void OnEverestColorsUpdated(EverestSdkNative.FWColor[] colors)
    {
        if (_evColorLogCount < 3)
        {
            _evColorLogCount++;
            int nonZero = 0;
            for (int i = 0; i < colors.Length; i++)
                if (colors[i].r != 0 || colors[i].g != 0 || colors[i].b != 0) nonZero++;
            App.WriteLog($"[LED] EverestColors tick#{_evColorLogCount}: len={colors.Length} nonZero={nonZero} tints={_evKeyTints.Count}");
        }
        foreach (var kv in _evKeyTints)
        {
            int matrixId = kv.Key;
            var tint = kv.Value;
            if (matrixId < 0 || matrixId >= colors.Length) continue;

            var c = colors[matrixId];
            tint.Background = (c.r == 0 && c.g == 0 && c.b == 0)
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(128, c.r, c.g, c.b));
        }
    }

    private void OnMacroPadColorsUpdated(MacroPadSdkNative.FWColor[] colors)
    {
        // _matrixToIndex maps wMatrix (SDK callback) → button index (0..11).
        // To read the correct color from GetColorData, translate
        // wMatrix to LED index via LedMatrixMapping.MacroPad.
        foreach (var kv in _matrixToIndex)
        {
            int wMatrix  = kv.Key;    // code from SDK callback (170-179, 220, 221)
            int btnIndex = kv.Value;  // index in _keyButtons (0..11)

            if (!LedMatrixMapping.MacroPad.TryGetValue(wMatrix, out int ledIndex)) continue;
            if (ledIndex < 0 || ledIndex >= colors.Length) continue;
            if (!_mpKeyTints.TryGetValue(btnIndex, out var tint)) continue;

            var c = colors[ledIndex];
            tint.Background = (c.r == 0 && c.g == 0 && c.b == 0)
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(128, c.r, c.g, c.b));
        }
    }

    // ==================================================================
    // Utility
    // ==================================================================

    private void OnSdkCrashDetected()
    {
        App.WriteLog("[UI] SDKDLL.dll crash detected — scheduling auto-recovery in 3s");

        // Clear residual LED colors
        foreach (var kv in _evKeyTints)
            kv.Value.Background = Brushes.Transparent;
        foreach (var kv in _mpKeyTints)
            kv.Value.Background = Brushes.Transparent;

        LblStatus.Text = Loc.Get("ev_crash_recovering");

        // Auto-recovery: wait 3 s, then re-open the driver and restart LED preview.
        // A one-shot DispatcherTimer keeps us on the UI thread.
        var recovery = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        recovery.Tick += (_, _) =>
        {
            recovery.Stop();
            TryEverestCrashRecovery();
        };
        recovery.Start();
    }

    /// <summary>
    /// Attempts to re-open the Everest driver after an SDKDLL.dll crash and
    /// restart the LED preview. Called on the UI thread 3 s after crash.
    /// </summary>
    private void TryEverestCrashRecovery()
    {
        _everestCrashCount++;
        App.WriteLog($"[UI] TryEverestCrashRecovery #{_everestCrashCount}");

        // DLL timer thread killed via ExitThread (ESP drift limit hit). Skip Close/Open
        // (ntdll crash risk from corrupted critical sections). GetColorData may still work
        // because it executes on the calling thread; try to restart polling without re-open.
        if (App.SdkRateLimitedExitThread)
        {
            App.WriteLog("[UI] rate-limit ExitThread — skipping Close/Open; restarting LED polling without re-open");
            App.SdkCrashRecoveryNeeded = false;
            LblStatus.Text = Loc.Get("ev_crash_recovered");

            if (_ledPoller != null && _everest.IsOpen)
            {
                try { _everest.SetSyncEffect(false, 50); } catch { }
                try { _everest.SetSyncEffect(true,  50); } catch { }
                try { _everest.EnableColorStream(10);    } catch { }
                _ledPoller.EverestEnabled = true;  // if it crashes again, SdkCrashDetected fires
                App.WriteLog("[UI] LED polling re-enabled after ExitThread recovery");
            }
            else
            {
                App.WriteLog("[UI] LED polling NOT re-enabled (IsOpen=false or no poller)");
                LblStatus.Text = Loc.Get("ev_crash_recovery_failed");
            }
            return;
        }

        try { _everest.Close(); } catch { /* best-effort */ }

        // Reset the crash flag so IsOpen works again
        App.SdkCrashRecoveryNeeded = false;

        bool ok = false;
        try { ok = _everest.Open(); }
        catch (Exception ex) { App.WriteLog("[UI] recovery Open() threw: " + ex.Message); }

        App.WriteLog($"[UI] recovery Open() -> {ok}");

        if (ok)
        {
            LblStatus.Text = Loc.Get("ev_crash_recovered");

            // Restart LED preview only for the first 2 recoveries.
            // If crashes keep happening despite the APEnable fix, give up on
            // LED preview for this session to avoid an infinite crash loop.
            if (_everestCrashCount <= 2)
            {
                App.WriteLog("[UI] recovery: restarting LED preview");
                StartLedPreview();
            }
            else
            {
                App.WriteLog("[UI] recovery: LED preview disabled after repeated crashes (crash #" + _everestCrashCount + ")");
                // Note: driver is open, key callbacks and effects still work — only LED preview is off.
            }
        }
        else
        {
            App.SdkCrashRecoveryNeeded = true; // re-set: driver still unavailable
            LblStatus.Text = Loc.Get("ev_crash_recovery_failed");
            App.WriteLog("[UI] crash recovery failed — driver could not be re-opened");
        }
    }
}
