// MainWindow.DisplayDial.cs — partial class: "Display Dial" panel
// Controls the visible pages on the Everest Max rotary display
// and clock, screensaver, auto-off, menu color settings.
//
// FW_EXTEND_INFO ↔ Display Dial mapping:
//   byMMDockShowMenu  = page bitmask (bit0=Clock … bit7=Custom)
//   byMMDockScreenSetup = NOT clock format, NOT written by K2 anymore. A real
//                         Base Camp USB capture (_reference/usb_dumps/
//                         evclock.pcapng, 2026-07-15) proved it stays constant
//                         while toggling 12h/24h in Base Camp's own UI — clock
//                         format is instead carried on every periodic
//                         SetClockInfo call (see EverestService.UpdateClock,
//                         _dialClockTimer below). What byMMDockScreenSetup
//                         actually does remains unknown; left untouched.
//   byMMDockMenuIndex = NOT WRITTEN by K2 (2026-07-15, real hardware test via
//                       K2 itself, not just packet captures): SetExtendInfo
//                       returned True after writing menuIndex=67 (0x43,
//                       clock content + both enable bits packed in, per the
//                       real Base Camp USB captures' byte layout), but the
//                       immediately following GetExtendInfo read back
//                       menuIndex=0 — the SDK/firmware silently discards this
//                       field via this call even though the API reports
//                       success. wMMDockScreenSaver/wMMDockTurnOff round-
//                       tripped correctly in the same test (ss=2, off=30
//                       both came back as written), so this is specific to
//                       byMMDockMenuIndex, not a general SetExtendInfo
//                       failure. Base Camp's own USB traffic DOES show this
//                       byte changing and sticking within its session, so it
//                       must be set through some other command (plausibly the
//                       same mechanism as EverestSdkNative.SetPCInfo, whose
//                       doc comment says it drives byMMDockMenuIndex into the
//                       97-101/113 range as a side effect) — not yet
//                       identified. Read-only for now: K2 shows whatever the
//                       device reports but the "what the screensaver shows"
//                       combo has NO device effect until that command is
//                       found (would need a fresh raw-HID capture, not just
//                       SetExtendInfo's blob).
//   wMMDockScreenSaver  = screensaver timeout in seconds — CONFIRMED, including
//                         0=disabled: real hardware test round-tripped ss=2
//                         correctly through SetExtendInfo/GetExtendInfo.
//   wMMDockTurnOff      = auto-off timeout in seconds — CONFIRMED, same as above
//                         (off=30 round-tripped in the same real hardware test).
//   MMDockColor         = menu color
//
// Enable/disable checkboxes for screensaver and turn-off mirror Base Camp's
// own DB model (BaseCamp.Data.DisplayDial: EnableSecreenSaver/EnableTurnOff
// are separate bool columns from ScreenSaverTime/TurnOffTime) — K2 has no
// confirmed separate device-side enable flag (see byMMDockMenuIndex above:
// the bits that looked like enable flags in the packet captures live in a
// field K2 can't actually write), so it falls back to zeroing
// wMMDockScreenSaver/wMMDockTurnOff for "disabled" — confirmed round-tripping
// correctly (see above), unlike the byMMDockMenuIndex approach it replaces.
//
// Clock STYLE (analog/digital) is still NOT confirmed against a device field:
// both real USB captures show ambiguous/inconclusive byte changes around the
// clock-type UI actions (multiple candidate bytes moving together, and most
// clicks producing byte-identical packets — possibly Base Camp itself doesn't
// wire analog/digital to firmware, only 12h/24h). Left UI + persisted-only,
// per the project's "don't guess the bit-layout" rule, until a cleaner
// isolated capture (one click at a time) resolves it.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    // ── Flag to prevent re-entry during value loading ──
    private bool _dialLoading;

    /// <summary>
    /// Ticks the Media Dock clock every second (<c>EverestService.UpdateClock</c>) —
    /// see that method's remarks (real Base Camp sends the format on every
    /// periodic clock call, not via SetExtendInfo). Runs for the app's lifetime;
    /// the Tick handler no-ops if the driver isn't open, same tolerance as other
    /// pollers in this codebase. Unlike the RbDialClockType radio buttons
    /// themselves, this always carries <see cref="_dialAppliedFormat24h"/> — the
    /// clock must keep ticking continuously (it's not a one-shot setting write),
    /// but which format it uses only changes on "Apply to device", same as
    /// every other Display Dial field.
    /// </summary>
    private DispatcherTimer? _dialClockTimer;

    /// <summary>Clock format last pushed via "Apply to device" (or loaded at
    /// startup) — see <see cref="_dialClockTimer"/>.</summary>
    private bool _dialAppliedFormat24h = true;

    // ── Bit mapping for byMMDockShowMenu (to be confirmed with USB capture) ──
    [Flags]
    private enum DialPage : byte
    {
        Clock      = 0x01,
        Profile    = 0x02,
        Volume     = 0x04,
        Brightness = 0x08,
        Lighting   = 0x10,
        PCInfo     = 0x20,
        APM        = 0x40,
        Custom     = 0x80,
        All        = 0xFF
    }

    // Screensaver-function combo entries: what the screensaver shows.
    // Code layout confirmed against two independent real Base Camp USB
    // captures (_reference/usb_dumps/evsettings.pcapng + evsettings2.pcapng,
    // 2026-07-15). NOT the same list as the 8 page-visibility checkboxes
    // above (byMMDockShowMenu, a bitmask, unrelated field): Base Camp's real
    // combo here is image/clock/timer/stopwatch/volume/brightness/pcinfo/apm,
    // not clock/profile/lighting/volume/brightness/pcinfo/apm/custom as
    // previously guessed. NOT WRITTEN to the device — see file header
    // (byMMDockMenuIndex): kept only so "Read from device" can show what the
    // device currently reports, and so the combo/persisted choice survive a
    // future fix once the real write command is found.
    private static readonly (string Key, string Value, byte Code, bool IsSpecialRaw)[] DialFunctions =
    {
        ("dial_image",     "image",      2,   false),
        ("dial_timer",     "timer",      3,   false),
        ("dial_clock",     "clock",      4,   false),
        ("dial_stopwatch", "stopwatch",  7,   false),
        ("dial_volume",    "volume",     8,   false),
        ("dial_brightness","brightness", 9,   false),
        ("dial_pcinfo",    "pcinfo",     14,  false),
        ("dial_apm",       "apm",        113, true),
    };

    // ─────────────────────── Init ───────────────────────

    /// <summary>0=24h/Digital, 1=12h/Analog — mirrors what CbDialClockType/
    /// CbDialClockStyle.SelectedIndex used to provide before those became
    /// RbDialClock*/RbDialClockStyle* segmented button groups.</summary>
    private int DialClockTypeIndex => RbDialClock12h.IsChecked == true ? 1 : 0;
    private int DialClockStyleIndex => RbDialClockAnalog.IsChecked == true ? 1 : 0;

    private void InitDisplayDialPanel()
    {
        // Populate screensaver-function combo (which page shows as screensaver)
        CbDialScreenSaverFunction.Items.Clear();
        foreach (var (key, _, _, _) in DialFunctions)
            CbDialScreenSaverFunction.Items.Add(Loc.Get(key));

        // Load saved settings (or defaults)
        _dialLoading = true;
        try
        {
            LoadDialSettings();
        }
        finally
        {
            _dialLoading = false;
        }
        _dialAppliedFormat24h = DialClockTypeIndex == 0;

        if (_dialClockTimer is null)
        {
            _dialClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _dialClockTimer.Tick += (_, _) =>
            {
                if (_everest is { IsOpen: true })
                    _everest.UpdateClock(format24h: _dialAppliedFormat24h);
            };
            _dialClockTimer.Start();
        }
    }

    // ─────────────────────── Load / Save Settings ───────────────────────

    private void LoadDialSettings()
    {
        byte pages = ParseByte(_evStore?.GetSetting("dial.pages"), (byte)DialPage.All);
        CkDialClock.IsChecked    = (pages & (byte)DialPage.Clock) != 0;
        CkDialProfile.IsChecked  = (pages & (byte)DialPage.Profile) != 0;
        CkDialVolume.IsChecked   = (pages & (byte)DialPage.Volume) != 0;
        CkDialBright.IsChecked   = (pages & (byte)DialPage.Brightness) != 0;
        CkDialLighting.IsChecked = (pages & (byte)DialPage.Lighting) != 0;
        CkDialPCInfo.IsChecked   = (pages & (byte)DialPage.PCInfo) != 0;
        CkDialAPM.IsChecked      = (pages & (byte)DialPage.APM) != 0;
        CkDialCustom.IsChecked   = (pages & (byte)DialPage.Custom) != 0;

        int clockType = ParseInt(_evStore?.GetSetting("dial.clockType"), 0);
        (clockType == 1 ? RbDialClock12h : RbDialClock24h).IsChecked = true;

        int clockStyle = ParseInt(_evStore?.GetSetting("dial.clockStyle"), 0);
        (clockStyle == 1 ? RbDialClockAnalog : RbDialClockDigital).IsChecked = true;
        UpdateDialClockFormatVisibility();

        string ssFunction = _evStore?.GetSetting("dial.screenSaverFunction") ?? DialFunctions[0].Value;
        int ssIndex = Array.FindIndex(DialFunctions, f => f.Value == ssFunction);
        CbDialScreenSaverFunction.SelectedIndex = ssIndex >= 0 ? ssIndex : 0;

        CkDialScreenSaverEnable.IsChecked = ParseBool(_evStore?.GetSetting("dial.screenSaverEnable"), true);
        CkDialTurnOffEnable.IsChecked     = ParseBool(_evStore?.GetSetting("dial.turnOffEnable"), false);
        TxtDialScreenSaver.Text = _evStore?.GetSetting("dial.screenSaver") ?? "30";
        TxtDialTurnOff.Text     = _evStore?.GetSetting("dial.turnOff") ?? "0";

        string menuColor = _evStore?.GetSetting("dial.menuColor") ?? "#F3CC23";
        try
        {
            BtnDialMenuColor.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(menuColor));
        }
        catch { /* fallback: keep XAML default */ }
    }

    /// <summary>Analog clocks have no 12h/24h digit format — hide the format
    /// segmented control while "Analog" is selected.</summary>
    private void UpdateDialClockFormatVisibility()
    {
        PnlDialClockFormat.Visibility = RbDialClockAnalog.IsChecked == true
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveDialSettings()
    {
        if (_evStore is null) return;
        _evStore.SetSetting("dial.pages", BuildPageByte().ToString());
        _evStore.SetSetting("dial.clockType", DialClockTypeIndex.ToString());
        _evStore.SetSetting("dial.clockStyle", DialClockStyleIndex.ToString());
        _evStore.SetSetting("dial.screenSaverFunction", DialFunctions[CbDialScreenSaverFunction.SelectedIndex >= 0
            ? CbDialScreenSaverFunction.SelectedIndex : 0].Value);
        _evStore.SetSetting("dial.screenSaverEnable", (CkDialScreenSaverEnable.IsChecked == true) ? "1" : "0");
        _evStore.SetSetting("dial.turnOffEnable", (CkDialTurnOffEnable.IsChecked == true) ? "1" : "0");
        _evStore.SetSetting("dial.screenSaver", TxtDialScreenSaver.Text.Trim());
        _evStore.SetSetting("dial.turnOff", TxtDialTurnOff.Text.Trim());
        _evStore.SetSetting("dial.menuColor", FormatColor(BtnDialMenuColor));
    }

    // ─────────────────────── Build / parse byte ───────────────────────

    private byte BuildPageByte()
    {
        byte b = 0;
        if (CkDialClock.IsChecked == true)    b |= (byte)DialPage.Clock;
        if (CkDialProfile.IsChecked == true)  b |= (byte)DialPage.Profile;
        if (CkDialVolume.IsChecked == true)   b |= (byte)DialPage.Volume;
        if (CkDialBright.IsChecked == true)   b |= (byte)DialPage.Brightness;
        if (CkDialLighting.IsChecked == true) b |= (byte)DialPage.Lighting;
        if (CkDialPCInfo.IsChecked == true)   b |= (byte)DialPage.PCInfo;
        if (CkDialAPM.IsChecked == true)      b |= (byte)DialPage.APM;
        if (CkDialCustom.IsChecked == true)   b |= (byte)DialPage.Custom;
        return b;
    }

    // ─────────────────────── Apply to device ───────────────────────

    /// <summary>Builds a FW_EXTEND_INFO from the UI controls and sends it to the device.</summary>
    private void ApplyDialToDevice()
    {
        if (_everest is null) return;

        // Read current state from device to avoid overwriting unknown fields
        // (this also preserves byPixelShiftTime, which K2 no longer exposes in the UI).
        if (!_everest.TryGetExtendInfo(out var info))
        {
            LogEverest("[DIAL] Cannot read ExtendInfo from device.");
            return;
        }

        // Update only the fields controlled by the Display Dial panel.
        // byMMDockScreenSetup and byMMDockMenuIndex are NOT written here — see
        // file header for why (clock format goes through SetClockInfo instead;
        // menuIndex writes are silently discarded by the device, confirmed on
        // real hardware). Writing a value that's confirmed not to stick would
        // just violate the project's "don't guess the bit-layout" rule for no
        // benefit.
        info.byMMDockShowMenu = BuildPageByte();
        info.wMMDockScreenSaver = CkDialScreenSaverEnable.IsChecked == true
            ? ParseUshort(TxtDialScreenSaver.Text, 30) : (ushort)0;
        info.wMMDockTurnOff = CkDialTurnOffEnable.IsChecked == true
            ? ParseUshort(TxtDialTurnOff.Text, 0) : (ushort)0;

        // Menu color → FWColor
        try
        {
            var c = ((SolidColorBrush)BtnDialMenuColor.Background).Color;
            info.MMDockColor = new EverestSdkNative.FWColor(c.R, c.G, c.B);
        }
        catch { /* keep the color read from device */ }

        bool ok = _everest.SetExtendInfo(info);
        LogEverest($"[DIAL] SetExtendInfo -> {ok}  pages=0x{info.byMMDockShowMenu:X2} " +
                   $"menuIndex={info.byMMDockMenuIndex} " +
                   $"ss={info.wMMDockScreenSaver} off={info.wMMDockTurnOff}");

        // Clock format doesn't live in FW_EXTEND_INFO (see file header) — push
        // it separately, on the same "Apply to device" trigger as everything else.
        _dialAppliedFormat24h = DialClockTypeIndex == 0;
        LogEverest($"[DIAL] UpdateClock(format24h={_dialAppliedFormat24h}) -> " +
                   $"{_everest.UpdateClock(_dialAppliedFormat24h)}");

        SaveDialSettings();
    }

    /// <summary>Reads FW_EXTEND_INFO from the device and populates the UI controls.</summary>
    private void ReadDialFromDevice()
    {
        if (_everest is null) return;
        if (!_everest.TryGetExtendInfo(out var info))
        {
            LogEverest("[DIAL] Cannot read ExtendInfo from device.");
            return;
        }

        _dialLoading = true;
        try
        {
            byte pages = info.byMMDockShowMenu;
            CkDialClock.IsChecked    = (pages & (byte)DialPage.Clock) != 0;
            CkDialProfile.IsChecked  = (pages & (byte)DialPage.Profile) != 0;
            CkDialVolume.IsChecked   = (pages & (byte)DialPage.Volume) != 0;
            CkDialBright.IsChecked   = (pages & (byte)DialPage.Brightness) != 0;
            CkDialLighting.IsChecked = (pages & (byte)DialPage.Lighting) != 0;
            CkDialPCInfo.IsChecked   = (pages & (byte)DialPage.PCInfo) != 0;
            CkDialAPM.IsChecked      = (pages & (byte)DialPage.APM) != 0;
            CkDialCustom.IsChecked   = (pages & (byte)DialPage.Custom) != 0;

            // Clock format (12h/24h) is intentionally left untouched here:
            // byMMDockScreenSetup doesn't carry it (see ApplyDialToDevice's
            // comment) — K2's own persisted choice (_dialClockTimer/
            // RbDialClockType_Checked) is the source of truth, not the device.

            // byMMDockMenuIndex is a PACKED byte (contentNibble<<4 | enableBits)
            // for every content type except the special-raw "apm" — see file
            // header. Check special-raw entries first (exact match) before
            // falling back to nibble matching, so apm's 113 (0x71) can't be
            // mistaken for a nibble-7 ("stopwatch") + enableBits=1 combination.
            // Display-only: K2 never writes this field (see file header), so
            // it just reflects whatever Base Camp or the firmware itself last
            // set it to — selecting a different option in the combo has no
            // effect on the device.
            byte menuIndex = info.byMMDockMenuIndex;
            int fnIndex = Array.FindIndex(DialFunctions, f => f.IsSpecialRaw && f.Code == menuIndex);
            if (fnIndex < 0)
                fnIndex = Array.FindIndex(DialFunctions, f => !f.IsSpecialRaw && f.Code == (menuIndex >> 4));
            CbDialScreenSaverFunction.SelectedIndex = fnIndex >= 0 ? fnIndex : 0;

            // The firmware only reports a timeout, not a separate enable flag
            // (Base Camp keeps that flag DB-side, and K2's own attempt at a
            // device-side flag via byMMDockMenuIndex was confirmed not to
            // stick — see file header). 0 => disabled, keep the last
            // configured seconds value in the textbox instead of overwriting it.
            CkDialScreenSaverEnable.IsChecked = info.wMMDockScreenSaver != 0;
            if (info.wMMDockScreenSaver != 0) TxtDialScreenSaver.Text = info.wMMDockScreenSaver.ToString();

            CkDialTurnOffEnable.IsChecked = info.wMMDockTurnOff != 0;
            if (info.wMMDockTurnOff != 0) TxtDialTurnOff.Text = info.wMMDockTurnOff.ToString();

            var c = info.MMDockColor;
            BtnDialMenuColor.Background = new SolidColorBrush(
                Color.FromRgb(c.r, c.g, c.b));

            LogEverest($"[DIAL] Read from device: pages=0x{pages:X2} " +
                       $"menuIndex={info.byMMDockMenuIndex} ss={info.wMMDockScreenSaver} " +
                       $"off={info.wMMDockTurnOff} color=({c.r},{c.g},{c.b})");

            SaveDialSettings();
        }
        finally
        {
            _dialLoading = false;
        }
    }

    // ─────────────────────── Event handlers ───────────────────────

    private void CkDial_Click(object sender, RoutedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void RbDialClockType_Checked(object sender, RoutedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void RbDialClockStyle_Checked(object sender, RoutedEventArgs e)
    {
        UpdateDialClockFormatVisibility();
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void CbDialScreenSaverFunction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    // Every Display Dial field — including screensaver/turn-off enable+timeout
    // and clock format — only reaches the device on an explicit "Apply to
    // device" click (BtnDialApply_Click). Edits here only update the local
    // UI/persisted settings; see ApplyDialToDevice for the one place that
    // actually talks to the firmware.
    private void CkDialScreenSaverEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void CkDialTurnOffEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void TxtDialScreenSaver_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void TxtDialTurnOff_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void BtnDialMenuColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog();
        try
        {
            var cur = ((SolidColorBrush)BtnDialMenuColor.Background).Color;
            dlg.Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B);
        }
        catch { }

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            BtnDialMenuColor.Background = new SolidColorBrush(
                Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            SaveDialSettings();
        }
    }

    private void BtnDialApply_Click(object sender, RoutedEventArgs e) => ApplyDialToDevice();
    private void BtnDialRead_Click(object sender, RoutedEventArgs e) => ReadDialFromDevice();

    private void BtnDialReset_Click(object sender, RoutedEventArgs e)
    {
        if (_everest is null) return;
        bool ok = _everest.ResetMMDock();
        LogEverest($"[DIAL] ResetMMDock -> {ok}");
        if (ok) ReadDialFromDevice();
    }

    // ─────────────────────── Helper ───────────────────────

    private static byte ParseByte(string? s, byte fallback)
    {
        return byte.TryParse(s, out var v) ? v : fallback;
    }

    private static ushort ParseUshort(string? s, ushort fallback)
    {
        return ushort.TryParse(s?.Trim(), out var v) ? v : fallback;
    }

    private static int ParseInt(string? s, int fallback)
    {
        return int.TryParse(s, out var v) ? v : fallback;
    }

    private static bool ParseBool(string? s, bool fallback)
    {
        return s switch { "1" => true, "0" => false, _ => fallback };
    }

    private static string FormatColor(Button btn)
    {
        try
        {
            var c = ((SolidColorBrush)btn.Background).Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch { return "#F3CC23"; }
    }
}
