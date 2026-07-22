// MainWindow.DisplayDial.cs — partial class: "Display Dial" panel
// Controls the visible pages on the Everest Max rotary display
// and clock, screensaver, auto-off, menu color settings.
//
// FW_EXTEND_INFO ↔ Display Dial mapping — CONFIRMED 2026-07-16 by decompiling
// the real BaseCamp.UI.dll (EverestOperations.SetDispalyDialDatatoHW/GetSStype,
// via _reference/tools/dotnet_method_calls.py — no guessing, exact IL read):
//
//   byMMDockShowMenu = page bitmask, built by Base Camp as a binary string
//                      "CustomMode APMCounter PCInfo Brightness Volume
//                      LightingMode Profile Clock" (MSB→LSB) parsed base-2:
//                      bit0=Clock, bit1=Profile, bit2=Lighting, bit3=Volume,
//                      bit4=Brightness, bit5=PCInfo, bit6=APM, bit7=Custom.
//                      An earlier version of this file had Volume/Brightness/
//                      Lighting on the WRONG bits (0x04/0x08/0x10 swapped) —
//                      fixed now that the real bit order is confirmed.
//   byMMDockScreenSetup = PACKED byte: (screensaverTypeNibble << 4) | 0b00 |
//                      (EnableTurnOff << 1) | EnableSecreenSaver. This is the
//                      field that actually carries screensaver content type
//                      AND enable/disable — NOT byMMDockMenuIndex (see below).
//                      Type nibble (GetSStype, exact decompiled table):
//                      Image=0, Clock=1(12h)/2(24h — depends on ClockType),
//                      Stopwatch=3, Timer=4, Volume=7, Brightness=8,
//                      PC Info-CPU=9, GPU=10, HDD=11, Internet=12, RAM=13,
//                      APM=14. An earlier version of this file guessed this
//                      exact table but attached it to the wrong struct field
//                      (byMMDockMenuIndex) and used raw HID packet captures'
//                      byte offset instead of the real field name — the wire
//                      offset empirically lines up with byMMDockScreenSetup,
//                      not the naive struct-offset arithmetic (Base Camp's
//                      actual wire framing has a few more header bytes than
//                      assumed; doesn't matter now that the field identity is
//                      confirmed from source, not inferred from offsets).
//   byMMDockMenuIndex = ALWAYS hardcoded to 0 by Base Camp's own apply logic
//                      (`stfld byMMDockMenuIndex` right after `initobj`, no
//                      DisplayDial field feeds it) — not writable through this
//                      path, full stop. Confirmed dead end; not used at all.
//   wMMDockScreenSaver / wMMDockTurnOff = timeout in seconds, ALWAYS sent as
//                      the real configured value (Base Camp does NOT zero
//                      these to represent "disabled" — that's carried
//                      entirely by byMMDockScreenSetup's low 2 bits instead).
//                      An earlier version of this file zeroed these fields to
//                      express disabled state, which is why turn-off (whose
//                      real enable bit was never touched) never engaged on
//                      real hardware even though the seconds value round-
//                      tripped fine.
//   MMDockColor       = menu color
//
// Clock STYLE (analog/digital) is still NOT confirmed against a device field:
// GetSStype/SetDispalyDialDatatoHW never reference ClockStyle/analog/digital
// at all — Base Camp's own decompiled apply logic simply doesn't send it
// anywhere. Left UI + persisted-only, per the project's "don't guess the
// bit-layout" rule (there's nothing to guess here: it's confirmed unsent).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

public partial class MainWindow
{
    // ── Flag to prevent re-entry during value loading ──
    // Defaults to true, not false: TxtDialScreenSaver/TxtDialTurnOff's
    // TextChanged (and the other handlers guarded by this flag) can fire
    // synchronously during InitializeComponent() itself — before
    // InitDisplayDialPanel() has run and before later-declared fields like
    // CbDialScreenSaverFunction are assigned — same root cause and same fix
    // as Everest60RgbPanel._ev60Suppress's doc comment. Confirmed crashing
    // 2026-07-21: SaveDialSettings() -> CbDialScreenSaverFunction.SelectedIndex
    // on a still-null field, a NullReferenceException during XAML load that a
    // separately-diagnosed VEH interaction (App.xaml.cs) turned into the app
    // not starting at all instead of a recoverable error dialog.
    private bool _dialLoading = true;

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

    // Bit mapping for byMMDockShowMenu — confirmed order (see file header):
    // Clock/Profile/Lighting/Volume/Brightness/PCInfo/APM/Custom.
    [Flags]
    private enum DialPage : byte
    {
        Clock      = 0x01,
        Profile    = 0x02,
        Lighting   = 0x04,
        Volume     = 0x08,
        Brightness = 0x10,
        PCInfo     = 0x20,
        APM        = 0x40,
        Custom     = 0x80,
        All        = 0xFF
    }

    // Screensaver-function combo entries: what the screensaver shows, encoded
    // as byMMDockScreenSetup's high nibble — see file header for the exact
    // decompiled table (EverestOperations.GetSStype). "clock"'s Code (2) is
    // the 24h default; BuildScreenSetupByte overrides it to 1 when 12h is
    // selected — GetSStype's own switch depends on ClockType for this one item.
    private static readonly (string Key, string Value, byte Code)[] DialFunctions =
    {
        ("dial_image",           "image",           0),
        ("dial_clock",           "clock",            2),
        ("dial_stopwatch",       "stopwatch",        3),
        ("dial_timer",           "timer",            4),
        ("dial_volume",          "volume",           7),
        ("dial_brightness",      "brightness",       8),
        ("dial_pcinfo_cpu",      "pcinfo_cpu",       9),
        ("dial_pcinfo_gpu",      "pcinfo_gpu",      10),
        ("dial_pcinfo_hdd",      "pcinfo_hdd",      11),
        ("dial_pcinfo_internet", "pcinfo_internet", 12),
        ("dial_pcinfo_ram",      "pcinfo_ram",      13),
        ("dial_apm",             "apm",             14),
    };

    /// <summary>Packs the selected screensaver-content code with the
    /// screensaver/turn-off enable bits into the byte Base Camp actually
    /// sends as <c>byMMDockScreenSetup</c> — see file header.</summary>
    private byte BuildScreenSetupByte()
    {
        var fn = DialFunctions[CbDialScreenSaverFunction.SelectedIndex >= 0
            ? CbDialScreenSaverFunction.SelectedIndex : 0];
        byte typeCode = fn.Value == "clock" && DialClockTypeIndex == 1 ? (byte)1 : fn.Code;

        byte enableBits = 0;
        if (CkDialTurnOffEnable.IsChecked == true)     enableBits |= 0x02;
        if (CkDialScreenSaverEnable.IsChecked == true) enableBits |= 0x01;
        return (byte)((typeCode << 4) | enableBits);
    }

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
        foreach (var (key, _, _) in DialFunctions)
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
        // Inverted on purpose: real hardware test (2026-07-16) showed
        // SetClockInfo's format24h parameter behaves opposite to its name —
        // the "24h" button only produces a 24-hour clock on the device when
        // format24h is sent as false (DialClockTypeIndex==1, i.e. what the UI
        // calls "12h"). Trusting the hardware result over the SDK's own
        // parameter name.
        _dialAppliedFormat24h = DialClockTypeIndex == 1;

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
        // byMMDockMenuIndex is NOT written — Base Camp itself always hardcodes
        // it to 0 (see file header). Enable/disable lives in
        // byMMDockScreenSetup's low bits now, not in zeroed seconds fields.
        info.byMMDockShowMenu = BuildPageByte();
        info.byMMDockScreenSetup = BuildScreenSetupByte();
        info.wMMDockScreenSaver = ParseUshort(TxtDialScreenSaver.Text, 30);
        info.wMMDockTurnOff = ParseUshort(TxtDialTurnOff.Text, 0);

        // Menu color → FWColor
        try
        {
            var c = ((SolidColorBrush)BtnDialMenuColor.Background).Color;
            info.MMDockColor = new EverestSdkNative.FWColor(c.R, c.G, c.B);
        }
        catch { /* keep the color read from device */ }

        bool ok = _everest.SetExtendInfo(info);
        LogEverest($"[DIAL] SetExtendInfo -> {ok}  pages=0x{info.byMMDockShowMenu:X2} " +
                   $"screenSetup=0x{info.byMMDockScreenSetup:X2} " +
                   $"ss={info.wMMDockScreenSaver} off={info.wMMDockTurnOff}");

        // Clock format doesn't live in FW_EXTEND_INFO (see file header) — push
        // it separately, on the same "Apply to device" trigger as everything else.
        // Inverted on purpose: real hardware test (2026-07-16) showed
        // SetClockInfo's format24h parameter behaves opposite to its name —
        // the "24h" button only produces a 24-hour clock on the device when
        // format24h is sent as false (DialClockTypeIndex==1, i.e. what the UI
        // calls "12h"). Trusting the hardware result over the SDK's own
        // parameter name.
        _dialAppliedFormat24h = DialClockTypeIndex == 1;
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
            // byMMDockScreenSetup's type nibble only reflects 12h/24h when
            // content=Clock is selected, and clock format itself goes through
            // SetClockInfo, not this struct — K2's own persisted choice
            // (_dialClockTimer/RbDialClockType_Checked) is the source of
            // truth, not the device.
            byte screenSetup = info.byMMDockScreenSetup;
            byte typeCode = (byte)(screenSetup >> 4);
            int fnIndex = Array.FindIndex(DialFunctions,
                f => f.Code == typeCode || (f.Value == "clock" && typeCode == 1));
            CbDialScreenSaverFunction.SelectedIndex = fnIndex >= 0 ? fnIndex : 0;

            byte enableBits = (byte)(screenSetup & 0x03);
            CkDialScreenSaverEnable.IsChecked = (enableBits & 0x01) != 0;
            CkDialTurnOffEnable.IsChecked     = (enableBits & 0x02) != 0;
            TxtDialScreenSaver.Text = info.wMMDockScreenSaver.ToString();
            TxtDialTurnOff.Text     = info.wMMDockTurnOff.ToString();

            var c = info.MMDockColor;
            BtnDialMenuColor.Background = new SolidColorBrush(
                Color.FromRgb(c.r, c.g, c.b));

            LogEverest($"[DIAL] Read from device: pages=0x{pages:X2} " +
                       $"screenSetup=0x{screenSetup:X2} ss={info.wMMDockScreenSaver} " +
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

    /// <summary>Loads/crops a 240×204 image and uploads it as the Media Dock
    /// screensaver picture — mirrors NdkKeyConfigDialog.BtnLoadImage_Click
    /// (Everest numpad display keys), same OpenFileDialog + ImageCropDialog flow.</summary>
    private void BtnDialLoadImage_Click(object sender, RoutedEventArgs e)
    {
        const int W = 240, H = 204;
        var dlg = new OpenFileDialog
        {
            Title  = Loc.Get("dial_load_image_title"),
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        string picked = dlg.FileName;
        string? cropped = ImageCropDialog.Show(this, picked, W, H, Loc.Get("crop_title", W, H));
        if (cropped is not null) picked = cropped;

        if (_everest is null) return;
        // StartPicUpdate (the SDK's picture-upload export) is synchronous and takes ~2s —
        // same blocking contract as the Everest numpad display keys, see NdkApplyImage's
        // doc comment (MainWindow.NumpadDisplayKeys.cs).
        bool ok = RunHwBusy(Loc.Get("hw_busy_uploading_image"), () => _everest.UploadMMDockScreensaver(picked));
        LogEverest($"[DIAL] UploadMMDockScreensaver -> {ok}");
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
