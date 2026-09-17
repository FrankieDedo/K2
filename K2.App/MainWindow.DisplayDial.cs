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
//   byMMDockMenuIndex = write-only-as-zero, READ-ONLY as state. Base Camp's apply
//                      logic always hardcodes it to 0 (`stfld byMMDockMenuIndex`
//                      right after `initobj`, no DisplayDial field feeds it), so
//                      there is nothing to send here — but that is only half the
//                      story, and an earlier version of this comment ("confirmed
//                      dead end; not used at all") wrote the field off entirely.
//                      READ BACK, it reports the page the dock is currently
//                      showing: 33..37 profile 1..5, 49..57 effect, 65 volume,
//                      81 brightness, 97..101 PC info CPU/GPU/HDD/Internet/RAM,
//                      113 APM. That is how Base Camp feeds the dock's live pages
//                      and how it notices profile/effect changes made on the
//                      keyboard (BaseCampService.PcInfo_timer, Common._dicEffects
//                      — decompiled 2026-08-22). K2 reads it in
//                      MainWindow.MediaDock.cs; do not delete it as unused.
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
// Clock STYLE (analog/digital) is NOT in FW_EXTEND_INFO — an earlier version of
// this comment concluded from that that Base Camp never sends it at all, and so
// K2 left the setting UI+persisted-only. That was wrong: it rides on the clock
// call instead. CONFIRMED 2026-09-09 by decompiling BaseCamp.Service.exe
// (ilspycmd): the real export is
//   SetClockInfo(iMonth, iDay, iHour, iMin, iSec, bool isDigitalClock, bool is12hr)
// and BaseCampLinux's raw packet agrees (`11 84 00 01 00 00 M D h m s <style>`,
// style 0=analog / 1=digital — devices/everest_max/controller.py). K2's P/Invoke
// had those two bools named `clockEnabled`/`format24h` and passed `clockEnabled:
// true`, i.e. it forced the DIGITAL face on every clock push — which is why the
// "Analog" button never did anything. Both names and the hardcode are fixed now.

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

    /// <summary>False until <see cref="InitDisplayDialPanel"/> has run once — guards
    /// <see cref="ReloadEverestDialForProfileSwitch"/> against the very first
    /// <c>ReloadEverestProfile</c> call, which happens before this panel's own Init
    /// (MainWindow.Everest.cs calls ReloadEverestProfile before InitDisplayDialPanel —
    /// see the call order there). Mirrors <c>_evRgbInitialized</c>'s exact role for the
    /// RGB panel. User request 2026-07-25.</summary>
    private bool _dialInitialized;

    /// <summary>
    /// Re-syncs the Media Dock clock (<c>EverestService.UpdateClock</c>) — see that
    /// method's remarks (real Base Camp carries the 12h/24h format on the clock call
    /// itself, not via SetExtendInfo). Runs for the app's lifetime; the Tick handler
    /// no-ops if the driver isn't open, same tolerance as other pollers in this
    /// codebase. Always carries <see cref="_dialAppliedDigital"/> + <see cref="_dialApplied12h"/>, which are
    /// refreshed by <see cref="SaveAndApplyDial"/> whenever a Display Dial control
    /// changes (every field applies on change since 2026-08-28).
    /// <para>
    /// <b>Interval = 30 minutes, NOT 1 second (2026-08-22 bug fix).</b> The dock has
    /// an on-board RTC: <c>SetClockInfo</c> sets the time, the firmware ticks it on
    /// its own. Ticking this every second was a K2 invention, and it kept the dock's
    /// idle counter permanently reset — user report: "the screensaver never starts".
    /// Confirmed against the real thing by decompiling <c>BaseCamp.Service.exe</c>
    /// (<c>BaseCampService.Clock_timer</c>): <c>Interval = 1800000.0</c> ms, handler
    /// <c>Clock_timer_Elapsed</c> → <c>Common.SetClockInfoInHW()</c>. Base Camp's own
    /// 1-second timer (<c>PcInfo_timer</c>) only ever <i>reads</i> FW_EXTEND_INFO and
    /// writes exclusively to the page the dock is currently showing — it never writes
    /// the clock periodically. Extra clock pushes still happen exactly where Base Camp
    /// does them: at session logon/unlock and when the Display Dial data is applied
    /// (see <see cref="ApplyDialToDevice"/> and the one-shot sync in
    /// <see cref="InitDisplayDialPanel"/>).
    /// </para>
    /// </summary>
    private DispatcherTimer? _dialClockTimer;

    /// <summary>Clock format last pushed to the device (on any Display Dial change,
    /// or loaded at startup) — see <see cref="_dialClockTimer"/>.</summary>
    private bool _dialApplied12h = true;

    /// <summary>Clock face last applied: true = digital, false = analog. Carried on every
    /// clock push for the same reason as <see cref="_dialApplied12h"/> — the style is a
    /// parameter of SetClockInfo, so a push that guessed it would silently overwrite the
    /// user's choice (K2 hardcoded it to digital until 2026-09-09; see the file header).</summary>
    private bool _dialAppliedDigital = true;

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

    /// <summary>Resting values for the two timeout fields, and the floor below which they
    /// are never sent.
    ///
    /// <para>A ZERO turn-off timeout kills the screensaver too. The firmware runs both
    /// timeouts off one idle state machine, and <c>wMMDockTurnOff = 0</c> puts the whole
    /// thing out of action — the screensaver enable bit is set, the seconds value is
    /// right, <c>SetExtendInfo</c> returns true, and the dial simply never blanks (user
    /// report 2026-09-10: "se il turn-off timer è a zero non funziona nemmeno il timer
    /// screensaver"). Base Camp never hits this because it never writes a zero: every one
    /// of its exported profiles carries <c>&lt;IsTurnOffAfter&gt;false&lt;/IsTurnOffAfter&gt;</c>
    /// next to <c>&lt;TurnOffAfter&gt;0:0:10&lt;/TurnOffAfter&gt;</c> — "disabled" is carried by
    /// the enable bit alone (see this file's header), and the seconds field always keeps a
    /// plausible resting value. K2 used to default the box to "0", so a user who never
    /// touched turn-off had the screensaver silently dead.</para></summary>
    private const ushort DefaultTurnOffSeconds = 10;
    private const ushort DefaultScreenSaverSeconds = 30;

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
        // The 2026-07-16 hardware test that found this argument "inverted" was reading a
        // wrong parameter name: it is `is12hr`, not `format24h` (see the file header), so
        // "true when the UI says 12h" was right all along and is simply correctly named now.
        _dialApplied12h = DialClockTypeIndex == 1;
        _dialAppliedDigital = DialClockStyleIndex == 0;

        if (_dialClockTimer is null)
        {
            // 30 min, same as Base Camp's own Clock_timer — see _dialClockTimer's docs
            // for why a 1s tick broke the dock screensaver.
            _dialClockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _dialClockTimer.Tick += (_, _) => PushDialClock("timer");
            _dialClockTimer.Start();
            // First sync now: with a 30-minute period the first Tick is far too late
            // to put the right time on the dock at startup (Base Camp does the same
            // one-shot push when the service starts / the Display Dial page opens).
            // This one usually no-ops — the driver opens later, in EvAutoOpen, which
            // does its own push right after Open() succeeds; keep it for the case where
            // the panel is re-initialised with the driver already up.
            PushDialClock("startup");

            // Base Camp also re-pushes the clock on session logon/unlock; the dock's RTC
            // can drift or lose the time across a long lock or a suspend/resume, and with
            // a 30-minute timer it would stay wrong until the next tick. SystemEvents
            // fires on its own thread — hop to the dispatcher in PushDialClock's callers.
            SystemEvents.SessionSwitch += OnDialSessionSwitch;
            SystemEvents.PowerModeChanged += OnDialPowerModeChanged;
        }
        _dialInitialized = true;
    }

    /// <summary>Pushes the current wall-clock time to the Media Dock (no-op if the driver
    /// isn't open). Always carries <see cref="_dialAppliedDigital"/> and
    /// <see cref="_dialApplied12h"/>: both are SetClockInfo parameters, so a push that
    /// left them at a default would undo the user's Display Dial choice every 30 minutes.
    /// (Base Camp solves the same problem the other way round — its resync calls
    /// GetClockInfo first and echoes back whatever the firmware reports.)</summary>
    private void PushDialClock(string reason = "")
    {
        // Logged even when the driver is shut: a silent no-op here and a timer that never
        // ticked look identical in the log otherwise, and that is exactly the pair a
        // "dock clock is drifting" report has to tell apart (user report 2026-09-10).
        if (_everest is { IsOpen: true })
        {
            App.WriteLog($"[Dial.PushDialClock] resync ({reason})");
            _everest.UpdateClock(isDigitalClock: _dialAppliedDigital, is12hr: _dialApplied12h);
        }
        else
            App.WriteLog($"[Dial.PushDialClock] skipped ({reason}): Everest driver not open");
    }

    private void OnDialSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
                     or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Dispatcher.BeginInvoke(new Action(() => PushDialClock("session-switch")));
    }

    private void OnDialPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.BeginInvoke(new Action(() => PushDialClock("resume")));
    }

    /// <summary>Detaches the SystemEvents handlers — those are rooted by a process-wide
    /// static, so leaving them attached would leak the window. Called from MainWindow's
    /// Closed handler alongside CleanupMediaDock.</summary>
    private void CleanupDisplayDial()
    {
        _dialClockTimer?.Stop();
        _dialClockTimer = null;
        SystemEvents.SessionSwitch -= OnDialSessionSwitch;
        SystemEvents.PowerModeChanged -= OnDialPowerModeChanged;
    }

    /// <summary>
    /// Re-loads the Display Dial panel for the profile that just became active and
    /// pushes it to the device (mirrors <c>ReloadEverestRgbForProfileSwitch</c>) —
    /// called from <c>ReloadEverestProfile</c> (MainWindow.Everest.cs). ApplyDialToDevice
    /// itself is a no-op when the driver isn't open (logs and returns). User request
    /// 2026-07-25.
    /// </summary>
    private void ReloadEverestDialForProfileSwitch()
    {
        if (!_dialInitialized || _evStore is null) return;
        bool prev = _dialLoading;
        _dialLoading = true;
        try { LoadDialSettings(); }
        finally { _dialLoading = prev; }
        ApplyDialToDevice();
    }

    // ─────────────────────── Load / Save Settings ───────────────────────

    /// <summary>Fetches a Display Dial key under the profile-scoped (or shared, if
    /// synced) namespace given by <see cref="EvDialPrefix"/>, falling back to the
    /// legacy always-global "dial.*" key — one-time seeding for existing installs/
    /// profiles that never had their own per-profile value saved yet (same pattern
    /// as EvRgbPrefix/EvSettingsPrefix). User request 2026-07-25.</summary>
    private string? GetDialSetting(string key) =>
        _evStore?.GetSetting(EvDialPrefix() + key) ?? _evStore?.GetSetting("dial." + key);

    private void LoadDialSettings()
    {
        // Own flag since 2026-08-28 (K2-side only); one-time migration falls back to the
        // old shared rgb.sync so "sync on" users keep Display Dial synced too.
        CkDialSync.IsChecked =
            (_evStore?.GetSetting("dial.sync") ?? _evStore?.GetSetting("rgb.sync")) == "1";

        byte pages = ParseByte(GetDialSetting("pages"), (byte)DialPage.All);
        CkDialClock.IsChecked    = (pages & (byte)DialPage.Clock) != 0;
        CkDialProfile.IsChecked  = (pages & (byte)DialPage.Profile) != 0;
        CkDialVolume.IsChecked   = (pages & (byte)DialPage.Volume) != 0;
        CkDialBright.IsChecked   = (pages & (byte)DialPage.Brightness) != 0;
        CkDialLighting.IsChecked = (pages & (byte)DialPage.Lighting) != 0;
        CkDialPCInfo.IsChecked   = (pages & (byte)DialPage.PCInfo) != 0;
        CkDialAPM.IsChecked      = (pages & (byte)DialPage.APM) != 0;
        CkDialCustom.IsChecked   = (pages & (byte)DialPage.Custom) != 0;

        int clockType = ParseInt(GetDialSetting("clockType"), 0);
        (clockType == 1 ? RbDialClock12h : RbDialClock24h).IsChecked = true;

        int clockStyle = ParseInt(GetDialSetting("clockStyle"), 0);
        (clockStyle == 1 ? RbDialClockAnalog : RbDialClockDigital).IsChecked = true;
        UpdateDialClockFormatVisibility();

        string ssFunction = GetDialSetting("screenSaverFunction") ?? DialFunctions[0].Value;
        int ssIndex = Array.FindIndex(DialFunctions, f => f.Value == ssFunction);
        CbDialScreenSaverFunction.SelectedIndex = ssIndex >= 0 ? ssIndex : 0;

        CkDialScreenSaverEnable.IsChecked = ParseBool(GetDialSetting("screenSaverEnable"), true);
        CkDialTurnOffEnable.IsChecked     = ParseBool(GetDialSetting("turnOffEnable"), false);
        // Defaults match what Base Camp leaves in its own profiles — in particular turn-off
        // rests at 10, never 0 (see DefaultTurnOffSeconds). A stored 0 is healed to the
        // resting value rather than merely clamped on the wire, so the box never shows a
        // number the device is not actually being given.
        TxtDialScreenSaver.Text = DialTimeout(GetDialSetting("screenSaver"), DefaultScreenSaverSeconds).ToString();
        TxtDialTurnOff.Text     = DialTimeout(GetDialSetting("turnOff"), DefaultTurnOffSeconds).ToString();

        string menuColor = GetDialSetting("menuColor") ?? "#F3CC23";
        try
        {
            BtnDialMenuColor.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(menuColor));
        }
        catch { /* fallback: keep XAML default */ }

        CkEvSyncBrightness.IsChecked = ParseBool(GetDialSetting("syncBrightness"), false);
        UpdateDisplayBrightnessSyncUi();
        SetDisplayBrightnessUi(ParseInt(GetDialSetting("displayBrightness"), DefaultDisplayBrightness));
    }

    /// <summary>Display brightness default: what an untouched Base Camp profile holds
    /// (<c>KeyboardSettings.DisplayBrightness</c> = 100, 0xE4 on the wire).</summary>
    private const int DefaultDisplayBrightness = 100;

    /// <summary>Wire format of the dial screen / numpad display keys brightness:
    /// <c>0x80 | percent</c> — bit 7 marks the value as set, the low 7 bits are 0..100
    /// (Base Camp capture, see <c>EverestSdkNative.FW_EXTEND_BRIGHTNESS</c>).</summary>
    private static byte EncodeDisplayBrightness(int pct) => (byte)(0x80 | Math.Clamp(pct, 0, 100));

    /// <summary>Inverse of <see cref="EncodeDisplayBrightness"/>; null when that firmware
    /// profile never had a value (bit 7 clear — the 0 of a profile Base Camp never set).</summary>
    private static int? DecodeDisplayBrightness(byte raw) =>
        (raw & 0x80) != 0 ? Math.Min(raw & 0x7F, 100) : null;

    /// <summary>Sets slider and label together: ValueChanged doesn't fire when the value
    /// is unchanged, so the label can't be left to the handler.</summary>
    private void SetDisplayBrightnessUi(int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        SldDialDisplayBrightness.Value = pct;
        LblDialDisplayBrightness.Text = $"{pct}%";
    }

    private void SldDialDisplayBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblDialDisplayBrightness != null) LblDialDisplayBrightness.Text = $"{(int)e.NewValue}%";
        SaveAndApplyDial();
    }

    /// <summary>True while <c>ReloadEverestProfile</c> brings the panels up to date with a
    /// new profile — see <see cref="MirrorDisplayBrightness"/>.</summary>
    private bool _evProfileReloading;

    /// <summary>"Sync brightness" (Everest Max, per profile like Base Camp's
    /// <c>KeyboardSettings.IsSyncDisplay</c>): the display slider follows the keyboard's
    /// and can't be moved on its own.</summary>
    private void UpdateDisplayBrightnessSyncUi() =>
        SldDialDisplayBrightness.IsEnabled = CkEvSyncBrightness.IsChecked != true;

    private void CkEvSyncBrightness_Click(object sender, RoutedEventArgs e)
    {
        UpdateDisplayBrightnessSyncUi();
        if (CkEvSyncBrightness.IsChecked == true &&
            (int)SldDialDisplayBrightness.Value != (int)SldEvBrightness.Value)
            SldDialDisplayBrightness.Value = SldEvBrightness.Value;   // its handler saves + applies
        else
            SaveAndApplyDial();                                       // persist the flag
    }

    /// <summary>
    /// With "sync brightness" on, copies the keyboard's lighting brightness onto the display
    /// brightness; the slider's own handler then saves and writes it. Called from
    /// <c>SldEvBrightness_ValueChanged</c>, which also runs when the Display Dial's knob
    /// changed the brightness (SyncUiEffectFromDevice sets that slider). Null-safe because
    /// SldEvBrightness's XAML Value fires it mid-InitializeComponent, before these controls
    /// exist. Skipped during a profile reload (<see cref="_evProfileReloading"/>).
    /// </summary>
    private void MirrorDisplayBrightness(double value)
    {
        if (_evProfileReloading) return;
        if (CkEvSyncBrightness?.IsChecked != true || SldDialDisplayBrightness is null) return;
        SldDialDisplayBrightness.Value = value;
    }

    /// <summary>Analog clocks have no 12h/24h digit format — hide the format
    /// segmented control while "Analog" is selected. (Purely cosmetic: the style itself
    /// reaches the device through <see cref="PushDialClock"/>.)</summary>
    private void UpdateDialClockFormatVisibility()
    {
        PnlDialClockFormat.Visibility = RbDialClockAnalog.IsChecked == true
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveDialSettings()
    {
        if (_evStore is null) return;
        string prefix = EvDialPrefix();
        _evStore.SetSetting(prefix + "pages", BuildPageByte().ToString());
        _evStore.SetSetting(prefix + "clockType", DialClockTypeIndex.ToString());
        _evStore.SetSetting(prefix + "clockStyle", DialClockStyleIndex.ToString());
        _evStore.SetSetting(prefix + "screenSaverFunction", DialFunctions[CbDialScreenSaverFunction.SelectedIndex >= 0
            ? CbDialScreenSaverFunction.SelectedIndex : 0].Value);
        _evStore.SetSetting(prefix + "screenSaverEnable", (CkDialScreenSaverEnable.IsChecked == true) ? "1" : "0");
        _evStore.SetSetting(prefix + "turnOffEnable", (CkDialTurnOffEnable.IsChecked == true) ? "1" : "0");
        _evStore.SetSetting(prefix + "screenSaver", TxtDialScreenSaver.Text.Trim());
        _evStore.SetSetting(prefix + "turnOff", TxtDialTurnOff.Text.Trim());
        _evStore.SetSetting(prefix + "menuColor", FormatColor(BtnDialMenuColor));
        _evStore.SetSetting(prefix + "displayBrightness", ((int)SldDialDisplayBrightness.Value).ToString());
        _evStore.SetSetting(prefix + "syncBrightness", CkEvSyncBrightness.IsChecked == true ? "1" : "0");
    }

    /// <summary>
    /// The DISPLAY DIAL section's own "sync across profiles" flag (<c>dial.sync</c>),
    /// independent of the Lighting/Settings flags since 2026-08-28 and K2-side only —
    /// Base Camp deliberately keeps Display Dial out of its device sync flag, so this
    /// never calls <c>SetSyncAcrossProfiles</c>. Re-saves the on-screen dial config under
    /// the switched namespace (<see cref="EvDialPrefix"/>) and, on the rising edge,
    /// replays it into every profile slot (mirrors <c>CkEvSync_Click</c>).
    /// </summary>
    private void CkDialSync_Click(object sender, RoutedEventArgs e)
    {
        if (_dialLoading) return;
        _evStore?.SetSetting("dial.sync", CkDialSync.IsChecked == true ? "1" : "0");
        SaveDialSettings();
        if (CkDialSync.IsChecked == true) ReplayEverestSectionToAllProfiles(EvSyncSection.Dial);
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
        info.wMMDockScreenSaver = DialTimeout(TxtDialScreenSaver.Text, DefaultScreenSaverSeconds);
        info.wMMDockTurnOff = DialTimeout(TxtDialTurnOff.Text, DefaultTurnOffSeconds);

        // Menu color → FWColor
        try
        {
            var c = ((SolidColorBrush)BtnDialMenuColor.Background).Color;
            info.MMDockColor = new EverestSdkNative.FWColor(c.R, c.G, c.B);
        }
        catch { /* keep the color read from device */ }

        // Display brightness (dial screen + numpad display keys) lives in the active
        // firmware profile's exBrightness pair, both bytes with the same value — exactly
        // what Base Camp writes (see EncodeDisplayBrightness).
        int fwProfile = _everest.CurrentProfile();
        if (fwProfile is >= 1 and <= 5 && info.exBrightness is { Length: 5 })
        {
            byte raw = EncodeDisplayBrightness((int)SldDialDisplayBrightness.Value);
            info.exBrightness[fwProfile - 1].byMMDockBrightness = raw;
            info.exBrightness[fwProfile - 1].byNumpadBrightness = raw;
            LogEverest($"[DIAL] display brightness p{fwProfile} = 0x{raw:X2}");
        }

        bool ok = _everest.SetExtendInfo(info);
        LogEverest($"[DIAL] SetExtendInfo -> {ok}  pages=0x{info.byMMDockShowMenu:X2} " +
                   $"screenSetup=0x{info.byMMDockScreenSetup:X2} " +
                   $"ss={info.wMMDockScreenSaver} off={info.wMMDockTurnOff}");

        // Clock face AND digit format don't live in FW_EXTEND_INFO (see file header) —
        // both are SetClockInfo parameters, so push them separately on the same
        // "Apply to device" trigger as everything else.
        _dialApplied12h = DialClockTypeIndex == 1;
        _dialAppliedDigital = DialClockStyleIndex == 0;
        App.WriteLog($"[DIAL] UpdateClock(digital={_dialAppliedDigital}, 12h={_dialApplied12h}) -> " +
                     $"{_everest.UpdateClock(_dialAppliedDigital, _dialApplied12h)}");

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
            // Same zero-healing as LoadDialSettings: a device that still holds the 0 an
            // older K2 wrote must not put it back into the box (see DefaultTurnOffSeconds).
            TxtDialScreenSaver.Text = (info.wMMDockScreenSaver == 0
                ? DefaultScreenSaverSeconds : info.wMMDockScreenSaver).ToString();
            TxtDialTurnOff.Text     = (info.wMMDockTurnOff == 0
                ? DefaultTurnOffSeconds : info.wMMDockTurnOff).ToString();

            // Display brightness of the active firmware profile — only when that profile
            // actually has one (bit 7 set); an unset 0 keeps K2's own value.
            int fwProfile = _everest.CurrentProfile();
            if (fwProfile is >= 1 and <= 5 && info.exBrightness is { Length: 5 } &&
                DecodeDisplayBrightness(info.exBrightness[fwProfile - 1].byMMDockBrightness) is int dispPct)
                SetDisplayBrightnessUi(dispPct);

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

    /// <summary>Persists the on-screen Display Dial state and immediately pushes it
    /// to the device. Since 2026-08-28 every Display Dial control applies on change
    /// (user request) — the old explicit "Apply to device" button is gone.
    /// <see cref="ApplyDialToDevice"/> is a no-op when the driver isn't open, and
    /// <see cref="SaveDialSettings"/> runs first so the choice is still persisted
    /// when no device is connected.</summary>
    private void SaveAndApplyDial()
    {
        if (_dialLoading) return;
        SaveDialSettings();
        ApplyDialToDevice();
    }

    private void CkDial_Click(object sender, RoutedEventArgs e)
    {
        SaveAndApplyDial();
    }

    private void RbDialClockType_Checked(object sender, RoutedEventArgs e)
    {
        SaveAndApplyDial();
    }

    private void RbDialClockStyle_Checked(object sender, RoutedEventArgs e)
    {
        UpdateDialClockFormatVisibility();
        SaveAndApplyDial();
    }

    private void CbDialScreenSaverFunction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveAndApplyDial();
    }

    private void CkDialScreenSaverEnable_Click(object sender, RoutedEventArgs e)
    {
        SaveAndApplyDial();
    }

    private void CkDialTurnOffEnable_Click(object sender, RoutedEventArgs e)
    {
        SaveAndApplyDial();
    }

    private void TxtDialScreenSaver_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveAndApplyDial();
    }

    private void TxtDialTurnOff_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveAndApplyDial();
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
            SaveAndApplyDial();
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

        string? cropped = ImageCropDialog.Show(this, dlg.FileName, W, H, Loc.Get("crop_title", W, H));
        if (cropped is null) return;
        string picked = cropped;

        if (_everest is null) return;
        // StartPicUpdate (the SDK's picture-upload export) is synchronous and takes ~2s —
        // same blocking contract as the Everest numpad display keys, see NdkApplyImage's
        // doc comment (MainWindow.NumpadDisplayKeys.cs).
        bool ok = RunHwBusy(Loc.Get("hw_busy_uploading_image"), () => _everest.UploadMMDockScreensaver(picked));
        LogEverest($"[DIAL] UploadMMDockScreensaver -> {ok}");
        // Same flash-write side effect as the numpad display key icons — see
        // EvReArmColorStreamAfterFlashWrite's doc comment (MainWindow.LedPreview.cs).
        EvReArmColorStreamAfterFlashWrite();
    }

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

    /// <summary>Parses one of the two Display Dial timeout boxes into the seconds value to
    /// put on the wire, NEVER letting a zero through — see
    /// <see cref="DefaultTurnOffSeconds"/>.</summary>
    private static ushort DialTimeout(string? text, ushort fallback)
    {
        ushort v = ParseUshort(text, fallback);
        return v == 0 ? fallback : v;
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
