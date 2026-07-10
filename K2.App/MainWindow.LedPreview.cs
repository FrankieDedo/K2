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
/// Both devices use the keycap-appearance-aware path (MainWindow.KeycapAppearance.cs for
/// Everest, MainWindow.MacroKeycapAppearance.cs for MacroPad): depending on the selected
/// keycap style, the live color drives the LedHalo glow layer, the legend color, or the
/// Face's Background/BorderBrush (Pudding/Reverse Pudding) — see
/// ApplyEverestLedColor/ApplyMacroPadLedColor.
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

    /// <summary>Maps ledIndex (GetColorData) → the Button + LedHalo border for the Everest preview.</summary>
    private readonly Dictionary<int, KeyVisual> _evKeyVisuals = new();

    /// <summary>Maps key index (0..11) → the Button + LedHalo border for the MacroPad preview.</summary>
    private readonly Dictionary<int, KeyVisual> _mpKeyVisuals = new();

    private int _evColorLogCount;  // log only the first N ticks for diagnostics
    private int _mpColorLogCount;  // log only the first N ticks for diagnostics


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

        // Capture Button + LedHalo border from Everest buttons
        BuildEverestKeyVisuals(CvsEvKeyboard, LedMatrixMapping.EverestKeyboard);
        BuildEverestKeyVisuals(CvsEvNumpad, LedMatrixMapping.EverestNumpad);
        ApplyKeycapAppearanceToAllKeys();

        // Capture Button + LedHalo border from MacroPad buttons
        BuildMacroPadKeyVisuals();
        ApplyMacroKeycapAppearanceToAllKeys();

        App.WriteLog($"[LED] Everest key visuals: {_evKeyVisuals.Count}  MacroPad key visuals: {_mpKeyVisuals.Count}");
    }

    /// <summary>Starts LED polling (call when the lighting tab is visible).</summary>
    private void StartLedPreview()
    {
        if (_ledPoller == null) return;
        // Gated to the "LED Lighting" section, same as Everest (UpdateMpLedPreviewActive) —
        // see that method's doc comment for why unconditional polling caused keys to look
        // stuck gray after a physical press.
        _ledPoller.MacroPadEnabled = _activeMpSection == PnlMpSecLed && _macroPad.IsOpen;
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
        //
        // The device-side setup below (sync/backlight) always runs so the physical
        // keyboard's effect keeps working; only the on-screen preview polling
        // (_ledPoller.EverestEnabled) is gated to the "RGB & Lighting" section —
        // see IsEvKeyBindingSectionActive/UpdateEverestLedPreviewActive.
        if (_everest.IsOpen)
        {
            _everest.SetSyncEffect(false, 50);
            _everest.SetSyncEffect(true,  50);
            _everest.EnableColorStream(10);
            _ledPoller.EverestEnabled = _activeEvSection == PnlSecRgb;
            _everest.SetBacklight(true);
        }

        // MacroPad: matches Base Camp's actual sequence, reverse-engineered from
        // BaseCamp.UI.dll (MacroPadOperations.getDefaultLighting / MacroPadController.
        // GetColorData — extracted 2026-07-09 from the Electron/.NET UI bundle, see
        // K2/_reference/BaseCamp_decompiled_UI/). BC calls SetSyncEffect(id, true, 50)
        // ONCE when the lighting page loads, then polls GetColorData in a plain loop
        // (300ms sleep) with NO other priming call. In particular BC never calls
        // GetFWLayout, APEnable or SetBacklight for MacroPad color streaming — those
        // were copied here from the Everest flow (which genuinely needs them) and are
        // suspected to be why the on-screen preview never lit up: APEnable especially
        // toggles the device into a different (AP/firmware-update) mode that likely
        // stops normal HID color reporting. SetBacklight was also removed because BC
        // doesn't force it here either — forcing it on every StartLedPreview call was
        // clobbering the user's own backlight ON/OFF setting (BtnMacroLightOn/Off).
        if (_macroPad.IsOpen && CurrentDeviceId() is int mpId)
        {
            uint uid = (uint)mpId;

            // Same per-slot init Everest does once after connect (GetFWInfo/GetFWLayout/
            // EnableKeyFunc/APEnable(false)) — see MacroPadService.EnsureSlotInitialized.
            // Idempotent: a no-op if SetEffect (or a previous preview start) already ran it.
            _macroPad.EnsureSlotInitialized(uid);

            try
            {
                bool st = MacroPadSdkNative.SetSyncEffect(true, 50, uid);
                App.WriteLog($"[LED] MP SetSyncEffect({uid}) (true,50)->{st}");
            }
            catch (Exception ex) { App.WriteLog("[LED] MP SetSyncEffect threw: " + ex); }
        }

        _ledPoller.Start();
        App.WriteLog($"[LED] StartLedPreview  ev={_ledPoller.EverestEnabled} mp={_ledPoller.MacroPadEnabled}  visuals={_evKeyVisuals.Count}+{_mpKeyVisuals.Count}");
    }

    /// <summary>Stops polling (when leaving the tab or closing).</summary>
    private void StopLedPreview()
    {
        _ledPoller?.Stop();
    }

    /// <summary>
    /// Enables/disables just the Everest half of the LED color preview. Called from
    /// <see cref="ShowEvSection"/> whenever the active Everest section changes: the
    /// preview is only meaningful while looking at "RGB &amp; Lighting", so polling
    /// stays off (and tints are cleared) on every other section. MacroPad polling is
    /// untouched — <see cref="LedColorPoller"/> tracks the two independently.
    /// </summary>
    private void UpdateEverestLedPreviewActive(bool active)
    {
        if (_ledPoller == null) return;
        _ledPoller.EverestEnabled = active && _everest.IsOpen;
        if (!active)
            foreach (var kv in _evKeyVisuals)
                ResetEverestKeyToOff(kv.Value);
    }

    /// <summary>
    /// Enables/disables the MacroPad half of the LED color preview, mirroring
    /// <see cref="UpdateEverestLedPreviewActive"/>. Called from
    /// <see cref="ShowMpSection"/> whenever the active MacroPad section changes.
    /// <para>
    /// Added 2026-07-09: previously MacroPad polling ran unconditionally
    /// whenever the device was open, regardless of which section was visible.
    /// That polling writes the "LED off" (gray) color to a key's Background/
    /// BorderBrush via <c>SetCurrentValue</c> when the effect isn't actually
    /// lighting it (see <see cref="ApplyMacroPadLedColor"/> for Pudding/Reverse
    /// Pudding styles) — if a 120ms poll tick landed WHILE a key's
    /// <c>IsHighlighted</c> style trigger was active (physical key press), the
    /// gray value became the new "current value" once the trigger deactivated
    /// on release, so the key visibly stayed gray after being pressed. Gating
    /// to the LED Lighting section shrinks that window to only while the user
    /// is actually looking at the panel; <see cref="OnMacroPadColorsUpdated"/>
    /// additionally skips highlighted keys as a second line of defense.
    /// </para>
    /// </summary>
    private void UpdateMpLedPreviewActive(bool active)
    {
        if (_ledPoller == null) return;
        _ledPoller.MacroPadEnabled = active && _macroPad.IsOpen;
        if (!active)
            foreach (var kv in _mpKeyVisuals)
                ResetMacroPadKeyToOff(kv.Value);
    }

    // ==================================================================
    // Key visual collection — Everest
    // ==================================================================

    private void BuildEverestKeyVisuals(Canvas canvas, Dictionary<int, int> vkToLedMap)
    {
        foreach (UIElement child in canvas.Children)
        {
            if (child is not Button btn || btn.Tag is not int vk) continue;
            if (!vkToLedMap.TryGetValue(vk, out int ledIndex)) continue;

            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedHalo", btn) is Border halo)
                _evKeyVisuals[ledIndex] = new KeyVisual(btn, halo); // last VK wins if multiple map to same ledIndex
        }
    }

    // ==================================================================
    // Key visual collection — MacroPad
    // ==================================================================

    private void BuildMacroPadKeyVisuals()
    {
        for (int i = 0; i < _keyButtons.Length; i++)
        {
            var btn = _keyButtons[i];
            btn.ApplyTemplate();
            if (btn.Template?.FindName("LedHalo", btn) is Border halo)
                _mpKeyVisuals[i] = new KeyVisual(btn, halo);
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
            App.WriteLog($"[LED] EverestColors tick#{_evColorLogCount}: len={colors.Length} nonZero={nonZero} tints={_evKeyVisuals.Count}");
        }
        foreach (var kv in _evKeyVisuals)
        {
            int matrixId = kv.Key;
            if (matrixId < 0 || matrixId >= colors.Length) continue;

            var c = colors[matrixId];
            ApplyEverestLedColor(kv.Value, c.r, c.g, c.b);
        }
    }

    private void OnMacroPadColorsUpdated(MacroPadSdkNative.FWColor[] colors)
    {
        if (_mpColorLogCount < 3)
        {
            _mpColorLogCount++;
            int nonZero = 0;
            for (int i = 0; i < colors.Length; i++)
                if (colors[i].r != 0 || colors[i].g != 0 || colors[i].b != 0) nonZero++;
            App.WriteLog($"[LED] MacroPadColors tick#{_mpColorLogCount}: len={colors.Length} nonZero={nonZero} " +
                         $"matrixToIndex={_matrixToIndex.Count} visuals={_mpKeyVisuals.Count}");
        }

        // _matrixToIndex maps wMatrix (SDK callback) → button index (0..11).
        // 2026-07-10: confirmed via real device capture (user's saved
        // device.<id>.keymap in macropad.db: {"8":0,"17":1,"26":2,...,"125":11})
        // that the firmware's wMatrix IS DIRECTLY the GetColorData LED index —
        // there is no separate translation table. The previous
        // LedMatrixMapping.MacroPad dictionary assumed wMatrix was 170-179/220/221
        // (copied by analogy from the DB "DLLMatrixIndex" scheme used for
        // BaseCamp profile import, a completely different numbering domain) and
        // never matched anything real, which is why the on-screen LED preview
        // never lit up (mappedToLed was always 0).
        foreach (var kv in _matrixToIndex)
        {
            int ledIndex = kv.Key;    // code from SDK callback == GetColorData index
            int btnIndex = kv.Value;  // index in _keyButtons (0..11)

            if (ledIndex < 0 || ledIndex >= colors.Length) continue;
            if (!_mpKeyVisuals.TryGetValue(btnIndex, out var v)) continue;

            // Skip a key that's currently mid-physical-press: the IsHighlighted
            // style trigger (MacroKeyStyle) is active right now and outranks
            // whatever we'd write via SetCurrentValue, but the value we DO write
            // becomes the new baseline the trigger reverts to on release — a
            // poll landing here would otherwise leave the key looking "stuck"
            // in the LED-off/gray color after the press ends (see
            // UpdateMpLedPreviewActive for the full explanation).
            if (btnIndex >= 0 && btnIndex < _keys.Length && _keys[btnIndex].IsHighlighted)
                continue;

            var c = colors[ledIndex];
            ApplyMacroPadLedColor(v, c.r, c.g, c.b);
        }
    }

    // ==================================================================
    // Utility
    // ==================================================================

    private void OnSdkCrashDetected()
    {
        App.WriteLog("[UI] SDKDLL.dll crash detected — scheduling auto-recovery in 3s");

        // Clear residual LED colors
        foreach (var kv in _evKeyVisuals)
            ResetEverestKeyToOff(kv.Value);
        foreach (var kv in _mpKeyVisuals)
            ResetMacroPadKeyToOff(kv.Value);

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
