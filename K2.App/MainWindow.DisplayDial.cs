// MainWindow.DisplayDial.cs — partial class: "Display Dial" panel
// Controls the visible pages on the Everest Max rotary display
// and clock, screensaver, auto-off, menu color settings.
//
// FW_EXTEND_INFO ↔ Display Dial mapping (to verify with USB capture):
//   byMMDockShowMenu  = page bitmask (bit0=Clock … bit7=Custom)
//   byMMDockScreenSetup = clock type (0=24h, 1=12h — assumed)
//   wMMDockScreenSaver  = screensaver timeout in seconds (0=disabled)
//   wMMDockTurnOff      = auto-off timeout in seconds (0=disabled)
//   MMDockColor         = menu color
//   byPixelShiftTime    = pixel shift in minutes

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;

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

    // ─────────────────────── Init ───────────────────────

    private void InitDisplayDialPanel()
    {
        // Populate clock type combo
        CbDialClockType.Items.Clear();
        CbDialClockType.Items.Add("24h");
        CbDialClockType.Items.Add("12h");

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
        CbDialClockType.SelectedIndex = clockType < CbDialClockType.Items.Count ? clockType : 0;

        TxtDialScreenSaver.Text = _evStore?.GetSetting("dial.screenSaver") ?? "30";
        TxtDialTurnOff.Text     = _evStore?.GetSetting("dial.turnOff") ?? "0";
        TxtDialPixelShift.Text  = _evStore?.GetSetting("dial.pixelShift") ?? "0";

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
        _evStore.SetSetting("dial.clockType", CbDialClockType.SelectedIndex.ToString());
        _evStore.SetSetting("dial.screenSaver", TxtDialScreenSaver.Text.Trim());
        _evStore.SetSetting("dial.turnOff", TxtDialTurnOff.Text.Trim());
        _evStore.SetSetting("dial.pixelShift", TxtDialPixelShift.Text.Trim());
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
        if (!_everest.TryGetExtendInfo(out var info))
        {
            LogEverest("[DIAL] Cannot read ExtendInfo from device.");
            return;
        }

        // Update only the fields controlled by the Display Dial panel
        info.byMMDockShowMenu = BuildPageByte();
        info.byMMDockScreenSetup = (byte)CbDialClockType.SelectedIndex;  // 0=24h, 1=12h (to be confirmed)
        info.wMMDockScreenSaver = ParseUshort(TxtDialScreenSaver.Text, 30);
        info.wMMDockTurnOff = ParseUshort(TxtDialTurnOff.Text, 0);
        info.byPixelShiftTime = ParseByte(TxtDialPixelShift.Text, 0);

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
                   $"off={info.wMMDockTurnOff} pxShift={info.byPixelShiftTime}");

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

            CbDialClockType.SelectedIndex = info.byMMDockScreenSetup < 2
                ? info.byMMDockScreenSetup : 0;
            TxtDialScreenSaver.Text = info.wMMDockScreenSaver.ToString();
            TxtDialTurnOff.Text = info.wMMDockTurnOff.ToString();
            TxtDialPixelShift.Text = info.byPixelShiftTime.ToString();

            var c = info.MMDockColor;
            BtnDialMenuColor.Background = new SolidColorBrush(
                Color.FromRgb(c.r, c.g, c.b));

            LogEverest($"[DIAL] Read from device: pages=0x{pages:X2} " +
                       $"clock={info.byMMDockScreenSetup} ss={info.wMMDockScreenSaver} " +
                       $"off={info.wMMDockTurnOff} pxShift={info.byPixelShiftTime} " +
                       $"color=({c.r},{c.g},{c.b})");

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

    private void CbDialClockType_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    private void TxtDialPixelShift_TextChanged(object sender, TextChangedEventArgs e)
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
