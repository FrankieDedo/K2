// MainWindow.DisplayDial.cs — partial class: "Display Dial" panel
// Controls the visible pages on the Everest Max rotary display
// and clock, screensaver, auto-off, menu color settings.
//
// FW_EXTEND_INFO ↔ Display Dial mapping (to verify with USB capture):
//   byMMDockShowMenu  = page bitmask (bit0=Clock … bit7=Custom)
//   byMMDockScreenSetup = clock format, 12h/24h (assumed — see DialClockTypeIndex)
//   wMMDockScreenSaver  = screensaver timeout in seconds (0=disabled)
//   wMMDockTurnOff      = auto-off timeout in seconds (0=disabled)
//   MMDockColor         = menu color
//
// Enable/disable checkboxes for screensaver and turn-off mirror Base Camp's
// own DB model (BaseCamp.Data.DisplayDial: EnableSecreenSaver/EnableTurnOff
// are separate bool columns from ScreenSaverTime/TurnOffTime) — when
// unchecked, K2 sends 0 (disabled) to the firmware but keeps the configured
// seconds value so it's not lost if re-enabled.
//
// Clock style (analog/digital) and "screensaver shows" (which page acts as
// the screensaver) are real Base Camp concepts — BaseCamp.Data.DisplayDial
// has ClockType/ScreenSaverType columns, and BaseCampLinux's raw protocol
// has a confirmed STYLE_ANALOG/STYLE_DIGITAL byte and a MAIN_DISPLAY_MODES
// menu-byte table — but neither has a confirmed byte mapping in SDKDLL's
// FW_EXTEND_INFO for THIS SDK (the existing byMMDockMenuIndex comment in
// EverestSdkNative.cs quotes different values than BaseCampLinux's table,
// so they are not directly interchangeable). Both combos are therefore
// UI + persisted-only for now; wiring them to a device field needs a USB
// capture first (see _reference/USB_SNIFF_GUIDE.md), consistent with the
// project's "don't guess the bit-layout" rule.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    // ── Flag to prevent re-entry during value loading ──
    private bool _dialLoading;

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

    // Screensaver-function combo entries, in the same order as the page
    // toggles above. Values are internal page names (persisted), not a
    // confirmed device byte — see file header.
    private static readonly (string Key, string Value)[] DialFunctions =
    {
        ("dial_clock",     "clock"),
        ("dial_profile",   "profile"),
        ("dial_lighting",  "lighting"),
        ("dial_volume",    "volume"),
        ("dial_brightness","brightness"),
        ("dial_pcinfo",    "pcinfo"),
        ("dial_apm",       "apm"),
        ("dial_custom",    "custom"),
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
        foreach (var (key, _) in DialFunctions)
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

        // Update only the fields controlled by the Display Dial panel
        info.byMMDockShowMenu = BuildPageByte();
        info.byMMDockScreenSetup = (byte)DialClockTypeIndex;  // 0=24h, 1=12h (to be confirmed)
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
                   $"clock={info.byMMDockScreenSetup} ss={info.wMMDockScreenSaver} " +
                   $"off={info.wMMDockTurnOff}");

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

            (info.byMMDockScreenSetup == 1 ? RbDialClock12h : RbDialClock24h).IsChecked = true;

            // The firmware only reports a timeout, not a separate enable flag
            // (Base Camp keeps that flag DB-side). 0 => disabled, keep the last
            // configured seconds value in the textbox instead of overwriting it.
            CkDialScreenSaverEnable.IsChecked = info.wMMDockScreenSaver != 0;
            if (info.wMMDockScreenSaver != 0) TxtDialScreenSaver.Text = info.wMMDockScreenSaver.ToString();

            CkDialTurnOffEnable.IsChecked = info.wMMDockTurnOff != 0;
            if (info.wMMDockTurnOff != 0) TxtDialTurnOff.Text = info.wMMDockTurnOff.ToString();

            var c = info.MMDockColor;
            BtnDialMenuColor.Background = new SolidColorBrush(
                Color.FromRgb(c.r, c.g, c.b));

            LogEverest($"[DIAL] Read from device: pages=0x{pages:X2} " +
                       $"clock={info.byMMDockScreenSetup} ss={info.wMMDockScreenSaver} " +
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
        if (_dialLoading) return;
        SaveDialSettings();
    }

    private void CbDialScreenSaverFunction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dialLoading) return;
        SaveDialSettings();
    }

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
