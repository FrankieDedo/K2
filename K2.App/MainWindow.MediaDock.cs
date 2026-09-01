// MainWindow.MediaDock.cs — partial: the Media Dock's live data feed.
//
// The dock's own pages (Volume, PC Info: CPU/GPU/HDD/Internet/RAM, APM) hold no
// data of their own — the host has to push a number into them, and only into the
// one the dock is currently showing. `FW_EXTEND_INFO.byMMDockMenuIndex` is what
// says which page that is.
//
// That field is READ-ONLY state, and this is the one place K2 reads it. Writing
// it is a dead end (Base Camp hardcodes it to 0 in every apply path — see
// MainWindow.DisplayDial.cs's header), which is why it looked useless for a long
// time. Reading it back tells you what the user is doing ON the keyboard:
//
//   33..37  Profile 1..5 page      49..57  Effect page (Static..Off)
//   65      Volume                 81      Brightness
//   97..101 PC Info CPU/GPU/HDD/Internet/RAM
//   113     APM                    130     (guard value in BC's macro path, unknown)
//
// Confirmed 2026-08-22 by decompiling BaseCamp.Service.exe: BaseCampService's
// `PcInfo_timer` (1000 ms) reads FW_EXTEND_INFO and calls SetPCInfo/SetVolumeInfo
// for the matching page only, and `Common._dicEffects` supplies the 49..57 names.
//
// Writing ONLY the visible page is not an optimisation, it is the constraint:
// every host write resets the dock's idle counter, so blanket per-second writes
// stop the screensaver from ever starting (the bug fixed the same day in
// MainWindow.DisplayDial.cs — a 1 Hz clock resync did exactly that).

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using K2.App.Services;

namespace K2.App;

public partial class MainWindow
{
    // Page codes as read back from byMMDockMenuIndex.
    private const byte DockPageProfileFirst = 33, DockPageProfileLast = 37;
    private const byte DockPageEffectFirst  = 49, DockPageEffectLast  = 57;
    private const byte DockPageVolume       = 65;
    private const byte DockPageBrightness   = 81;
    private const byte DockPageCpu          = 97;
    private const byte DockPageGpu          = 98;
    private const byte DockPageDisk         = 99;
    private const byte DockPageNet          = 100;
    private const byte DockPageRam          = 101;
    private const byte DockPageApm          = 113;

    // SetPCInfo's infoType, per Base Camp's own call sites.
    private const int PcInfoCpu = 0, PcInfoGpu = 1, PcInfoDisk = 2,
                      PcInfoNet = 3, PcInfoRam = 4, PcInfoApm = 5;

    private DispatcherTimer? _dockPollTimer;

    /// <summary>Last page seen, so a change can be acted on once instead of every tick.</summary>
    private int _dockLastPage = -1;

    /// <summary>When the dock last changed page — used only for the "[DOCK] page X -> Y
    /// after Ns" log line now. The feed back-off keys off <see cref="_dockFeedActiveSince"/>
    /// instead, which a Windows-side volume change also renews.</summary>
    private DateTime _dockPageSince = DateTime.UtcNow;

    /// <summary>When the dock feed was last "renewed" by real activity: a page change, or a
    /// volume change reported by Core Audio while the Volume page is up. <see
    /// cref="DockShouldStopFeeding"/> measures idleness from here, so a user who changes
    /// Windows volume keeps the dock's Volume page live for another timeout window —
    /// turning the physical volume roller already resets the firmware's own idle counter
    /// the same way.</summary>
    private DateTime _dockFeedActiveSince = DateTime.UtcNow;

    /// <summary>Last volume percent pushed to the dock, to skip redundant writes when the
    /// Core Audio event and the 1 Hz poll agree. -1 = nothing pushed yet.</summary>
    private int _dockLastVolumePushed = -1;

    /// <summary>Starts the 1 Hz dock feed. Same period as Base Camp's PcInfo_timer: the
    /// dock's pages are live readouts, and the tick is mostly a single read
    /// (GetExtendInfo) with a write only when a data page is actually on screen.</summary>
    private void InitMediaDockPanel()
    {
        if (_dockPollTimer is not null) return;
        _dockPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dockPollTimer.Tick += (_, _) => DockPollTick();
        _dockPollTimer.Start();

        // Immediate Volume-page updates (and feed-window renewal) on any Windows-side
        // volume change, instead of waiting up to a second for the poll.
        SystemMonitor.VolumeChanged += OnSystemVolumeChanged;
        SystemMonitor.StartVolumeNotifications();
    }

    private void CleanupMediaDock()
    {
        _dockPollTimer?.Stop();
        _dockPollTimer = null;
        SystemMonitor.VolumeChanged -= OnSystemVolumeChanged;
        SystemMonitor.StopVolumeNotifications();
    }

    /// <summary>Core Audio reported a volume change (any source). Fired on an audio worker
    /// thread — hop to the UI thread, then, if the dock is actually on its Volume page,
    /// push the new value now and renew the feed window so <see cref="DockShouldStopFeeding"/>
    /// keeps it live.</summary>
    private void OnSystemVolumeChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_everest is not { IsOpen: true }) return;
            if (_ndkUploadBusy) return;
            if (!_everest.TryGetExtendInfo(out var info)) return;
            if (info.byMMDockMenuIndex != DockPageVolume) return;

            _dockFeedActiveSince = DateTime.UtcNow;   // a real volume change is dock activity
            int vol = SystemMonitor.VolumePercent();
            if (vol == _dockLastVolumePushed) return;
            _everest.SetVolume(vol);
            _dockLastVolumePushed = vol;
        });
    }

    private void DockPollTick()
    {
        if (_everest is not { IsOpen: true }) return;
        // A flash write (icon/screensaver upload) makes the firmware unresponsive on
        // both transports — same guard as the accessory poll in MainWindow.Layout.cs.
        if (_ndkUploadBusy) return;
        if (!_everest.TryGetExtendInfo(out var info)) return;

        byte page = info.byMMDockMenuIndex;

        if (page != _dockLastPage)
        {
            // Deliberately App.WriteLog and not LogEverest: this is the only window K2 has
            // onto what the dock actually does, and it has to survive on a machine whose
            // log level is turned down (2026-08-23 — a dock screensaver that engages on one
            // keyboard and not on another, diagnosable only by comparing these lines).
            // Page codes are listed in this file's header; a page the menu cannot reach
            // (bitmask bit clear) means the firmware took the screen over by itself, i.e.
            // the screensaver engaged.
            App.WriteLog($"[DOCK] page {_dockLastPage} -> {page} after " +
                         $"{(DateTime.UtcNow - _dockPageSince).TotalSeconds:F0}s  " +
                         $"(menu=0x{info.byMMDockShowMenu:X2} screenSetup=0x{info.byMMDockScreenSetup:X2} " +
                         $"ss={info.wMMDockScreenSaver} off={info.wMMDockTurnOff})");
            _dockPageSince = DateTime.UtcNow;
            _dockFeedActiveSince = DateTime.UtcNow;
        }

        if (!DockShouldStopFeeding(info))
            FeedDockPage(page);

        // Normally only a page CHANGE is worth reacting to. Brightness is the exception:
        // its page code stays 81 while the user turns the dial through the whole range,
        // so there is no edge to catch and it has to be re-read while it is on screen.
        if (page != _dockLastPage || page == DockPageBrightness)
        {
            _dockLastPage = page;
            SyncUiFromDockPage(page);
        }
    }

    /// <summary>
    /// True once the dock has sat on the same page for at least as long as its own
    /// screensaver / turn-off timeout — at which point K2 stops writing to it entirely.
    /// <para>
    /// The firmware counts idle time to decide when to blank or take over the screen, and
    /// a host write is activity. Feeding a live number into a page nobody has touched for
    /// longer than the timeout is exactly the kind of write that can hold those timeouts
    /// off forever, and a stale reading on a screen nobody is looking at costs nothing.
    /// Base Camp has no such back-off (its PcInfo_timer writes to the visible page
    /// unconditionally); K2 does, because "the screensaver never starts" is the bug this
    /// whole area came from and this is the one way the app could still cause it.
    /// </para>
    /// <para>
    /// A page change resets the clock (<see cref="_dockFeedActiveSince"/>), and so does a
    /// Windows-side volume change while the Volume page is up, so normal use is unaffected:
    /// the numbers stay live while the user is moving through the dock's pages or actually
    /// changing the volume.
    /// </para>
    /// </summary>
    private bool DockShouldStopFeeding(EverestSdkNative.FW_EXTEND_INFO info)
    {
        // Low two bits of byMMDockScreenSetup: bit0 screensaver, bit1 turn-off (see
        // MainWindow.DisplayDial.cs's header).
        bool ssOn  = (info.byMMDockScreenSetup & 0x01) != 0;
        bool offOn = (info.byMMDockScreenSetup & 0x02) != 0;

        int timeout = int.MaxValue;
        if (ssOn  && info.wMMDockScreenSaver > 0) timeout = Math.Min(timeout, info.wMMDockScreenSaver);
        if (offOn && info.wMMDockTurnOff     > 0) timeout = Math.Min(timeout, info.wMMDockTurnOff);
        if (timeout == int.MaxValue) return false;   // neither timeout is armed

        return (DateTime.UtcNow - _dockFeedActiveSince).TotalSeconds >= timeout;
    }

    /// <summary>Pushes the current value of whichever page the dock is showing. Anything
    /// else (clock, profile list, lighting menu…) the firmware draws by itself and needs
    /// no host data, so those ticks write nothing at all.</summary>
    private void FeedDockPage(byte page)
    {
        switch (page)
        {
            case DockPageCpu:    _everest.SetPCInfo(PcInfoCpu,  SystemMonitor.CpuPercent());        break;
            case DockPageGpu:    _everest.SetPCInfo(PcInfoGpu,  SystemMonitor.GpuPercent());        break;
            case DockPageDisk:   _everest.SetPCInfo(PcInfoDisk, SystemMonitor.DiskPercent());       break;
            case DockPageNet:    _everest.SetPCInfo(PcInfoNet,  SystemMonitor.DownloadMbPerSec());  break;
            case DockPageRam:    _everest.SetPCInfo(PcInfoRam,  SystemMonitor.RamPercent());        break;
            case DockPageApm:    _everest.SetPCInfo(PcInfoApm,  ApmLastMinute());                   break;
            case DockPageVolume:
                int vol = SystemMonitor.VolumePercent();
                _everest.SetVolume(vol);
                _dockLastVolumePushed = vol;
                break;
        }
    }

    // ─────────────────────── Device → UI sync ───────────────────────

    /// <summary>
    /// Follows changes the user makes on the keyboard itself. The dial can switch
    /// profile, effect and brightness with no involvement from K2, which otherwise keeps
    /// showing the previous state until something else reloads it.
    /// <para>
    /// The state being read is never echoed back: the firmware has already switched, so
    /// re-pushing it would be pointless. That rules out reusing
    /// <c>EvActivateProfileSlot</c>, whose SwitchProfile call and display-key flash work
    /// are far too heavy to hang off a poll. Base Camp draws the same line — with its UI
    /// running, <c>Common.ChangeEverestProfile</c> only tells the UI to refresh and
    /// returns. What DOES follow is a profile's own K2-side settings: reloading a profile
    /// re-applies its Display Dial config and disabled keys, exactly as when the user
    /// picks it in the list, once per switch rather than per tick.
    /// </para>
    /// </summary>
    private void SyncUiFromDockPage(byte page)
    {
        try
        {
            if (page is >= DockPageProfileFirst and <= DockPageProfileLast)
                SyncUiProfileFromDevice();
            else if (page is >= DockPageEffectFirst and <= DockPageEffectLast or DockPageBrightness)
                SyncUiEffectFromDevice();
        }
        catch (Exception ex)
        {
            App.WriteLog("[DOCK] SyncUiFromDockPage threw: " + ex);
        }
    }

    /// <summary>Re-selects the profile the firmware reports as active and reloads its data
    /// into the panels. The page code says which profile page is on screen, but the
    /// firmware's own <c>currentlyProfileIndex</c> says which one is actually running —
    /// Base Camp reads the same field for the same reason.</summary>
    private void SyncUiProfileFromDevice()
    {
        int fwSlot = _everest.CurrentProfile();
        if (fwSlot is < 1 or > 5) return;
        if (fwSlot == EvCurrentProfile()) return;
        if (LstEvProfile.ItemsSource is not List<EvProfileItem> items) return;
        if (items.Find(x => x.Slot == fwSlot && !x.IsNew) is null)
        {
            // The keyboard is on a firmware slot K2 has no profile for — nothing sensible
            // to show, and creating one behind the user's back would be worse.
            LogEverest($"[DOCK] device switched to firmware profile {fwSlot}, which has no K2 profile");
            return;
        }

        LogEverest($"[DOCK] profile changed on the device -> slot {fwSlot}, following in the UI");
        _evStore.SetCurrentProfile(fwSlot);
        EvSelectProfileSlot(fwSlot);      // suppressed: won't re-enter the click path
        ReloadEverestProfile(applyRgb: false);
    }

    /// <summary>Follows an effect or brightness change made on the dial, by reading back
    /// what the firmware actually has stored for the active profile.</summary>
    private void SyncUiEffectFromDevice()
    {
        if (!_evRgbInitialized) return;
        int fwSlot = _everest.CurrentProfile();
        if (!_everest.TryGetCurrentEffect(fwSlot, out var eff)) return;

        int idx = -1;
        for (int i = 0; i < EvEffectList.Length; i++)
            if ((byte)EvEffectList[i].Eff == eff.byEffectIndex) { idx = i; break; }
        if (idx < 0)
        {
            LogEverest($"[DOCK] device reports effect 0x{eff.byEffectIndex:X2}, not in K2's list");
            return;
        }

        // byLightness is a BrightT, whose values ARE percentages (0/25/50/75/100), so it
        // drops straight into the slider.
        int bright = eff.byLightness is >= 0 and <= 100 ? eff.byLightness : (int)SldEvBrightness.Value;
        if (idx == CbEvEffect.SelectedIndex && bright == (int)SldEvBrightness.Value) return;

        LogEverest($"[DOCK] lighting changed on the device -> {EvEffectList[idx].Label} @ {bright}%");
        bool prev = _evRgbSuppress;
        _evRgbSuppress = true;
        try
        {
            // Selecting fires EvReapplySelectedEffect, which restores that effect's saved
            // parameters — brightness has to be written after it, not before.
            CbEvEffect.SelectedIndex = idx;
            SldEvBrightness.Value = bright;
        }
        finally { _evRgbSuppress = prev; }
        // Suppression also blocks the save inside ApplyCurrentEffect, so persist here:
        // the device's state is now K2's state and must survive a restart.
        SaveEverestRgbToStore();
    }

    // ─────────────────────── Dial events (SDKDLL messages) ───────────────────────

    /// <summary>
    /// Handles <c>WM_KEY_STATUS</c> from SDKDLL.dll. A key matrix of 0 marks a Display
    /// Dial action, with the action in wParam — codes from the decompiled Base Camp
    /// service (<c>BaseCamp.Service.Helpers/MessageHandler.cs</c>): 107 and 179 = effect
    /// changed on the dial, 171..175 = profile changed, 177/178 = brightness changed.
    /// Everything here is a shortcut for the 1 Hz poll above, which reaches the same
    /// state on its own within a second — so a dead message channel costs latency, not
    /// correctness. Whether SDKDLL.dll delivers these at all on real hardware is still
    /// unverified (K2 never gave it a window to post to until 2026-08-22 — see
    /// EverestSdkNative.OpenUSBDriver).
    /// </summary>
    private void HandleEverestDockMessage(IntPtr wParam, IntPtr lParam)
    {
        if (_everest is not { IsOpen: true }) return;
        if (lParam.ToInt32() != 0) return;          // a normal key, not a dial action

        int code = wParam.ToInt32();
        switch (code)
        {
            case 107:
            case 179:
                SyncUiEffectFromDevice();
                break;
            case >= 171 and <= 175:
                SyncUiProfileFromDevice();
                break;
            case 177:
            case 178:
                SyncUiEffectFromDevice();           // brightness lives in the effect data
                break;
            default:
                LogEverest($"[DOCK] dial action code {code} (no K2 handler)");
                break;
        }
    }

    // ─────────────────────────── APM ───────────────────────────

    /// <summary>Timestamps of the last minute's key presses, oldest first.</summary>
    private readonly Queue<DateTime> _apmPresses = new();

    /// <summary>Keys currently held, so auto-repeat counts as the single press it is.</summary>
    private readonly HashSet<ushort> _apmKeysDown = new();

    /// <summary>
    /// Fed from the WndProc's raw-input hook. Base Camp's APM counts entries its SDK
    /// message pump recorded over the last minute; K2 counts physical key presses from
    /// Raw Input instead, because SDKDLL.dll's event channel has never been observed
    /// working here (see RawKeyboardActivityWatcher's own remarks). The number means
    /// roughly the same thing and, unlike the SDK-based one, it is actually non-zero.
    /// Raw Input is system-wide, so a second keyboard would count too.
    /// </summary>
    private void RegisterApmKey(bool keyDown, ushort vKey)
    {
        if (!keyDown) { _apmKeysDown.Remove(vKey); return; }
        if (!_apmKeysDown.Add(vKey)) return;        // auto-repeat of a held key
        _apmPresses.Enqueue(DateTime.UtcNow);
        if (_apmPresses.Count > 4000) _apmPresses.Dequeue();   // pathological input flood
    }

    private int ApmLastMinute()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (_apmPresses.Count > 0 && _apmPresses.Peek() < cutoff)
            _apmPresses.Dequeue();
        return _apmPresses.Count;
    }
}
