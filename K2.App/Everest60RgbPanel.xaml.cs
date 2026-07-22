using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// RGB Lighting + Side Ring section content for the Everest 60 tab — see
/// Everest60RgbPanel.xaml for why this is its own UserControl.
/// </summary>
public partial class Everest60RgbPanel : UserControl
{
    private Everest60Service _ev60 = null!;
    private Action<string> _log = _ => { };
    private bool _ev60Initialized;
    /// <summary>Defaults to true — see the identical doc comment on
    /// MakaluDpiRemapPanel._mkSuppress (root-caused via WinDbg+SOS
    /// 2026-07-10: any XAML-wired handler can fire synchronously during
    /// InitializeComponent(), before later-declared elements or Init() have
    /// run — defaulting this guard true instead of false makes that a
    /// no-op instead of a null-ref). Not currently known to be hit here
    /// (both Sliders' XAML-literal Minimum matches their default Value=0,
    /// so no coercion/ValueChanged fires during load), but costs nothing.</summary>
    private bool _ev60Suppress = true;
    private bool _ev60Connected;

    private int _ev60Color1 = 0x900000;
    private int _ev60Color2 = 0x000000;
    private int _ev60SideColor = 0x900000;

    /// <summary>Backs the main effect/side-ring brightness — the Slider itself
    /// lives in MainWindow's shared top-right bar (BrEverest60), not in this
    /// panel (see MainWindow.SectionNav.cs). Updated via <see cref="SetBrightness"/>.</summary>
    internal double Brightness { get; private set; } = 100;

    /// <summary>Profile persistence — set once from Init, same pattern as
    /// MakaluRgbSettingsPanel._mkStore/_mkSlot.</summary>
    private Everest60Store? _ev60Store;
    private Func<int>? _ev60Slot;
    private int CurrentSlot => _ev60Slot?.Invoke() ?? 1;

    /// <summary>Which of the three mutually-exclusive lighting modes was last
    /// sent to hardware (mirrors Ev60PersistLighting's activeMode tag) — used
    /// by <see cref="SetBacklightForcedOff"/> to know what to resend on wake.</summary>
    private string _ev60ActiveMode = "preset";
    private bool _ev60BacklightForcedOff;

    public Everest60RgbPanel()
    {
        InitializeComponent();
    }

    private static void ApplyColorButton(Button btn, int rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >> 8) & 0xFF);
        byte b = (byte)(rgb & 0xFF);
        btn.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        btn.ToolTip = $"#{rgb:X6}";
    }

    private sealed record Ev60EffectChoice(Everest60Protocol.Effect Eff, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record Ev60DirChoice(string Label, byte Code)
    {
        public override string ToString() => Label;
    }

    private static readonly Ev60EffectChoice[] Ev60EffectList =
    {
        new(Everest60Protocol.Effect.Static,    "Static"),
        new(Everest60Protocol.Effect.Breathing, "Breathing"),
        new(Everest60Protocol.Effect.Wave,      "Wave"),
        new(Everest60Protocol.Effect.Tornado,   "Tornado"),
        new(Everest60Protocol.Effect.Reactive,  "Reactive"),
        new(Everest60Protocol.Effect.Yeti,      "Yeti"),
        new(Everest60Protocol.Effect.Off,       "Off"),
    };

    private sealed record Ev60Caps(int MaxColors, bool Rainbow, bool Speed, Ev60DirChoice[] Directions);

    /// <summary>Backs GridEv60Direction's segmented buttons — mirrors what
    /// CbEv60Direction.SelectedItem used to provide before the direction
    /// ComboBox became a dynamically-rebuilt RadioButton row (see
    /// SegmentedButtonGroup).</summary>
    private int _ev60DirIndex;

    private static readonly Ev60DirChoice[] NoDirections = Array.Empty<Ev60DirChoice>();
    private static readonly Ev60DirChoice[] WaveDirChoices =
        Everest60Protocol.WaveDirections.Select(d => new Ev60DirChoice(d.Label, d.Code)).ToArray();
    private static readonly Ev60DirChoice[] TornadoDirChoices =
        Everest60Protocol.TornadoDirections.Select(d => new Ev60DirChoice(d.Label, d.Code)).ToArray();

    private static Ev60Caps CapsFor(Everest60Protocol.Effect e) => e switch
    {
        Everest60Protocol.Effect.Static    => new(1, false, false, NoDirections),
        Everest60Protocol.Effect.Breathing => new(2, true,  true,  NoDirections),
        Everest60Protocol.Effect.Wave      => new(2, true,  true,  WaveDirChoices),
        Everest60Protocol.Effect.Tornado   => new(1, true,  true,  TornadoDirChoices),
        Everest60Protocol.Effect.Reactive  => new(2, false, true,  NoDirections),
        Everest60Protocol.Effect.Yeti      => new(2, false, true,  NoDirections),
        _                                   => new(1, false, false, NoDirections), // Off
    };

    internal void Init(Everest60Service service, Action<string> log, Everest60Store store, Func<int> currentSlot)
    {
        _ev60 = service;
        _log = log;
        _ev60Store = store;
        _ev60Slot = currentSlot;
        _ev60Suppress = true;
        try
        {
            CbEv60Effect.ItemsSource = Ev60EffectList;
            CbEv60Effect.DisplayMemberPath = "Label";
            CbEv60Effect.SelectedIndex = 2; // Wave, mirrors Everest Max's default

            SldEv60Speed.Value = 50;
            RbEv60ColorSingle.IsChecked = true; // default, overridden by Ev60ReloadProfile if persisted

            UpdateEv60Capabilities();
            ApplyColorButton(BtnEv60Color1, _ev60Color1);
            ApplyColorButton(BtnEv60Color2, _ev60Color2);
            ApplyColorButton(BtnEv60SideColor, _ev60SideColor);

            // Backlight auto-off — moved here from MainWindow's Settings panel
            // (user request 2026-07-21), same "settings.*" keys as before so
            // existing saved values keep working.
            CkEv60AutoOffEnable.IsChecked = _ev60Store.GetSetting("settings.autoOffEnable") == "1";
            TxtEv60AutoOffSeconds.Text = int.TryParse(_ev60Store.GetSetting("settings.autoOffSeconds"), out var aoS) ? aoS.ToString() : "60";
        }
        finally
        {
            _ev60Suppress = false;
        }
        _ev60Initialized = true;
        RaiseAutoOffConfigChanged();
    }

    internal void SetConnected(bool connected) => _ev60Connected = connected;

    /// <summary>Called by MainWindow's shared top-right brightness Slider on
    /// change: updates the stored value and re-applies whatever's currently
    /// configured (preset effect), same "always live" behavior as Everest
    /// Max's SldEvBrightness_ValueChanged.</summary>
    internal void SetBrightness(double value)
    {
        Brightness = value;
        ApplyCurrentEv60Effect();
    }

    /// <summary>Aligns Speed/Direction/Rainbow/2nd-color controls to the
    /// selected effect's capabilities. Suppresses events during repopulation.</summary>
    private void UpdateEv60Capabilities()
    {
        if (CbEv60Effect.SelectedItem is not Ev60EffectChoice pick) return;
        var caps = CapsFor(pick.Eff);

        bool prev = _ev60Suppress;
        _ev60Suppress = true;
        try
        {
            PnlEv60Speed.Visibility = caps.Speed ? Visibility.Visible : Visibility.Collapsed;

            if (caps.Directions.Length > 0)
            {
                _ev60DirIndex = 0;
                SegmentedButtonGroup.Rebuild(GridEv60Direction, "Ev60Direction",
                    caps.Directions.Select(d => d.Label).ToArray(), RbEv60Direction_Checked, 0);
                PnlEv60Direction.Visibility = Visibility.Visible;
            }
            else
            {
                GridEv60Direction.Children.Clear();
                PnlEv60Direction.Visibility = Visibility.Collapsed;
            }

            // Color mode: Single/Double/Rainbow are one mutually-exclusive radio
            // group now (GroupName="Ev60ColorMode") — WPF's RadioButton group
            // handles the mutual exclusion, no manual uncheck logic needed.
            // Rainbow/Double are only selectable when the effect supports them;
            // falls back to Single otherwise (same pattern as the
            // Direction/Speed Collapsed-when-unsupported gating above).
            RbEv60Rainbow.IsEnabled = caps.Rainbow;
            RbEv60Rainbow.Visibility = caps.Rainbow ? Visibility.Visible : Visibility.Collapsed;
            if (!caps.Rainbow && RbEv60Rainbow.IsChecked == true)
                RbEv60ColorSingle.IsChecked = true;

            RbEv60ColorDouble.IsEnabled = caps.MaxColors >= 2;
            if (caps.MaxColors < 2 && RbEv60ColorDouble.IsChecked == true)
                RbEv60ColorSingle.IsChecked = true;

            UpdateEv60ColorRowVisibility();
        }
        finally
        {
            _ev60Suppress = prev;
        }
    }

    // ------------------------------------------------------------
    // Effect panel event handlers
    // ------------------------------------------------------------

    private void CbEv60Effect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEv60Capabilities();
        ApplyCurrentEv60Effect();
    }

    private void RbEv60Direction_Checked(object sender, RoutedEventArgs e)
    {
        _ev60DirIndex = (int)((RadioButton)sender).Tag;
        ApplyCurrentEv60Effect();
    }

    private void SldEv60Speed_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEv60Speed != null) LblEv60Speed.Text = $"{(int)SldEv60Speed.Value}%";
        ApplyCurrentEv60Effect();
    }

    /// <summary>Manual backlight switch — reuses the same mechanism as the
    /// auto-off idle timer (SetBacklightForcedOff): resends the current
    /// mode's "Off" or active state directly, no real firmware on/off
    /// toggle exists on this device.</summary>
    private void CkEv60Backlight_Click(object sender, RoutedEventArgs e)
    {
        SetBacklightForcedOff(CkEv60Backlight.IsChecked != true);
        BacklightManuallyToggled?.Invoke();
    }

    /// <summary>Single/Double/Rainbow color mode — one mutually-exclusive radio
    /// group (GroupName="Ev60ColorMode"), so no manual uncheck logic is needed.</summary>
    private void RbEv60ColorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_ev60Suppress) return;
        UpdateEv60ColorRowVisibility();
        ApplyCurrentEv60Effect();
    }

    /// <summary>Swatch rows follow the selected color mode: hidden entirely
    /// under Rainbow (colors are ignored), primary-only under Single, both
    /// under Double.</summary>
    private void UpdateEv60ColorRowVisibility()
    {
        bool rainbow = RbEv60Rainbow.IsChecked == true;
        PnlEv60Color1.Visibility = rainbow ? Visibility.Collapsed : Visibility.Visible;
        PnlEv60Color2.Visibility = !rainbow && RbEv60ColorDouble.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnEv60Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        int current = tag == "1" ? _ev60Color1 : _ev60Color2;

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb((current >> 16) & 0xFF, (current >> 8) & 0xFF, current & 0xFF),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        int rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
        if (tag == "1") _ev60Color1 = rgb; else _ev60Color2 = rgb;
        ApplyColorButton(btn, rgb);
        ApplyCurrentEv60Effect();
    }

    /// <summary>Snapshots ALL lighting state (preset effect + side ring color +
    /// per-key custom colors) into the current profile slot in one combined
    /// record, tagging which of the three mutually-exclusive modes was the one
    /// just sent to hardware. Called unconditionally (even while disconnected)
    /// so a profile edited with the keyboard unplugged is still saved.</summary>
    private void Ev60PersistLighting(string activeMode)
    {
        _ev60ActiveMode = activeMode;
        if (_ev60Store is null) return;
        var eff = CbEv60Effect.SelectedItem is Ev60EffectChoice pick ? pick.Eff : Everest60Protocol.Effect.Off;
        bool rainbow = RbEv60Rainbow.IsChecked == true;
        int speedPct = (int)SldEv60Speed.Value;
        int customBrightPct = (int)SldEv60CustomBrightness.Value;
        var customDict = new Dictionary<int, int>();
        foreach (var kv in _ev60CustomKeyColors)
            customDict[kv.Key] = (kv.Value.R << 16) | (kv.Value.G << 8) | kv.Value.B;

        _ev60Store.SaveLighting(CurrentSlot, new Ev60LightingRecord(
            (int)eff, _ev60Color1, _ev60Color2, speedPct, _ev60DirIndex, rainbow,
            Brightness, _ev60SideColor, customBrightPct, activeMode, customDict,
            ColorDouble: RbEv60ColorDouble.IsChecked == true));
    }

    /// <summary>Reads the panel and sends the effect to the firmware. No-op
    /// while still initializing or while the device isn't connected.</summary>
    private void ApplyCurrentEv60Effect()
    {
        if (!_ev60Initialized || _ev60Suppress) return;
        if (CbEv60Effect.SelectedItem is not Ev60EffectChoice pick)
            return;
        Ev60PersistLighting(activeMode: "preset");
        if (!_ev60Connected)
        {
            _log("[RGB ] skip: Everest 60 not connected");
            return;
        }

        var caps = CapsFor(pick.Eff);
        int speedPct = (int)SldEv60Speed.Value;
        int brightPct = (int)Brightness;
        bool rainbow = caps.Rainbow && RbEv60Rainbow.IsChecked == true;

        byte direction = caps.Directions.Length > 0
            ? caps.Directions[Math.Clamp(_ev60DirIndex, 0, caps.Directions.Length - 1)].Code
            : (byte)0;

        (byte r, byte g, byte b) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        bool useDouble = !rainbow && caps.MaxColors >= 2 && RbEv60ColorDouble.IsChecked == true;
        (byte, byte, byte)? secondary = useDouble ? C(_ev60Color2) : null;

        _log($"[RGB ] apply eff={pick.Eff} speed={speedPct}% bright={brightPct}% " +
             $"rainbow={rainbow} dir=0x{direction:X2} c1=#{_ev60Color1:X6}" +
             (secondary.HasValue ? $" c2=#{_ev60Color2:X6}" : ""));
        bool ok = _ev60.SetEffect(pick.Eff, speedPct, brightPct, C(_ev60Color1), secondary, rainbow, direction);
        _log($"[RGB ] SetEffect -> {ok}");
    }

    /// <summary>Backlight-off-when-idle timer callback (see MainWindow.Everest60.cs's
    /// BacklightIdleTimer). Sends the "Off" preset directly to hardware without
    /// touching persisted state or <see cref="_ev60ActiveMode"/> (so whichever of
    /// preset/side/custom was actually active survives); waking resends that
    /// same mode via the exact dispatch <see cref="Ev60ReloadProfile"/> uses.</summary>
    internal void SetBacklightForcedOff(bool off)
    {
        if (off == _ev60BacklightForcedOff) return;
        _ev60BacklightForcedOff = off;
        CkEv60Backlight.IsChecked = !off;
        if (!_ev60Connected) return;

        if (off)
        {
            bool ok = _ev60.SetEffect(Everest60Protocol.Effect.Off, 0, 0, (0, 0, 0), null, false, 0);
            _log($"[RGB ] auto-off: SetEffect(Off) -> {ok}");
            return;
        }

        _log($"[RGB ] auto-off wake: resend active mode -> {_ev60ActiveMode}");
        switch (_ev60ActiveMode)
        {
            case "side":   BtnEv60SideApply_Click(this, new RoutedEventArgs()); break;
            case "custom": BtnEv60CustomApply_Click(this, new RoutedEventArgs()); break;
            default:       ApplyCurrentEv60Effect(); break;
        }
    }

    // ------------------------------------------------------------
    // Side ring
    // ------------------------------------------------------------

    private void BtnEv60SideColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb((_ev60SideColor >> 16) & 0xFF, (_ev60SideColor >> 8) & 0xFF, _ev60SideColor & 0xFF),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _ev60SideColor = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
        ApplyColorButton(BtnEv60SideColor, _ev60SideColor);
    }

    private void BtnEv60SideApply_Click(object sender, RoutedEventArgs e)
    {
        Ev60PersistLighting(activeMode: "side");
        if (!_ev60Connected) { _log("[RGB ] skip: Everest 60 not connected"); return; }
        (byte r, byte g, byte b) c = ((byte)((_ev60SideColor >> 16) & 0xFF),
                                       (byte)((_ev60SideColor >> 8) & 0xFF),
                                       (byte)(_ev60SideColor & 0xFF));
        int brightPct = (int)Brightness;
        _log($"[RGB ] apply side ring color=#{_ev60SideColor:X6} bright={brightPct}%");
        bool ok = _ev60.SetSideRing(c, brightPct);
        _log($"[RGB ] SetSideRing -> {ok}");
    }

    // ------------------------------------------------------------
    // Key Lighting — per-key custom paint editor (64 main-board keys).
    //
    // The keyboard overlay itself (CvsEv60Keyboard, the actual Buttons) lives
    // in MainWindow.xaml/MainWindow.Everest60.cs — this panel only owns the
    // paint state + device Apply/Clear, and bridges to MainWindow via
    // TryPaintKey() (called on every key click) and the CustomKeysCleared
    // event (so MainWindow can reset its Button visuals on Clear). No live
    // readback exists for this raw-HID board, so there is nothing to
    // "reapply on reconnect" — a painted key keeps its Background for the
    // Canvas's lifetime, same as Everest Max's Custom Lighting overlay.
    // ------------------------------------------------------------

    private bool _ev60PaintMode;
    private Color _ev60BrushColor = Color.FromRgb(0x5B, 0xBE, 0xC3); // teal accent
    private readonly Dictionary<int, Color> _ev60CustomKeyColors = new();

    /// <summary>Raised when "Clear" is pressed, so MainWindow can reset the
    /// on-screen key Buttons it owns.</summary>
    internal event Action? CustomKeysCleared;

    /// <summary>Raised on every manual click of the backlight switch, so
    /// MainWindow can keep its BacklightIdleTimer's countdown/forced-off state
    /// in sync (owned there, not here — see InitEverest60Module's doc comment
    /// on _ev60AutoOffTimer). Without this, re-enabling the backlight here
    /// after an auto-off never restarts the timer, so it would never auto-off
    /// a second time.</summary>
    internal event Action? BacklightManuallyToggled;

    /// <summary>Raised whenever the backlight auto-off checkbox/seconds change
    /// (including once from <see cref="Init"/>), so MainWindow can push the
    /// new config into its own <c>_ev60AutoOffTimer</c> (owned there, not
    /// here — same split as <see cref="BacklightManuallyToggled"/>).</summary>
    internal event Action<bool, int>? AutoOffConfigChanged;

    private void RaiseAutoOffConfigChanged()
    {
        bool enabled = CkEv60AutoOffEnable.IsChecked == true;
        int seconds = int.TryParse(TxtEv60AutoOffSeconds.Text, out int s) ? s : 0;
        AutoOffConfigChanged?.Invoke(enabled, seconds);
    }

    private void CkEv60AutoOffEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_ev60Suppress) return;
        _ev60Store?.SetSetting("settings.autoOffEnable", CkEv60AutoOffEnable.IsChecked == true ? "1" : "0");
        RaiseAutoOffConfigChanged();
    }

    private void TxtEv60AutoOffSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_ev60Suppress) return;
        if (!int.TryParse(TxtEv60AutoOffSeconds.Text, out int seconds) || seconds < 0)
        {
            seconds = 60;
            TxtEv60AutoOffSeconds.Text = seconds.ToString();
        }
        _ev60Store?.SetSetting("settings.autoOffSeconds", seconds.ToString());
        RaiseAutoOffConfigChanged();
    }

    /// <summary>Called by MainWindow's Everest 60 keyboard-key click handler.
    /// Returns true (and the applied color) if paint mode is active.</summary>
    internal bool TryPaintKey(int ledIndex, out Color color)
    {
        color = _ev60BrushColor;
        if (!_ev60PaintMode || ledIndex < 0) return false;
        _ev60CustomKeyColors[ledIndex] = _ev60BrushColor;
        return true;
    }

    /// <summary>Read-only lookup of a key's current painted color, for
    /// MainWindow's Keycap Appearance system (ApplyEv60KeycapAppearanceToAllKeys)
    /// to use as the baseline "live" signal each KeycapStyle blends with when
    /// the LED preview poll isn't running (or hasn't ticked yet) — e.g. right
    /// after a paint click, or while a non-Lighting section is active. While
    /// the poll IS running, MainWindow.OnEv60ColorsUpdated feeds the actual
    /// polled hardware color instead (see Everest60SdkNative.GetColorData2:
    /// live readback DOES exist for this board, found via decompile
    /// 2026-07-11 — this comment previously said otherwise).</summary>
    internal bool TryGetPaintedColor(int ledIndex, out Color color) =>
        _ev60CustomKeyColors.TryGetValue(ledIndex, out color);

    /// <summary>Whether the Key Lighting paint-mode checkbox is currently
    /// checked — used by MainWindow's live LED-color poll (OnEv60ColorsUpdated)
    /// to avoid overwriting an unsaved paint preview with the hardware's actual
    /// (pre-Apply) colors while the user is actively painting keys.</summary>
    internal bool IsPaintModeActive => _ev60PaintMode;

    private void CkEv60CustomPaint_Checked(object sender, RoutedEventArgs e) =>
        _ev60PaintMode = CkEv60CustomPaint.IsChecked == true;

    private void BtnEv60CustomBrushColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(_ev60BrushColor.R, _ev60BrushColor.G, _ev60BrushColor.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _ev60BrushColor = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        BtnEv60CustomBrushColor.Background = new SolidColorBrush(_ev60BrushColor);
    }

    private void SldEv60CustomBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEv60CustomBrightness != null) LblEv60CustomBrightness.Text = $"{(int)SldEv60CustomBrightness.Value}%";
    }

    private void BtnEv60CustomApply_Click(object sender, RoutedEventArgs e)
    {
        Ev60PersistLighting(activeMode: "custom");
        if (!_ev60Connected) { _log("[KEYS] skip: Everest 60 not connected"); return; }

        var colors = new (byte r, byte g, byte b)[Everest60Protocol.NumKeys];
        foreach (var kv in _ev60CustomKeyColors)
        {
            if (kv.Key < 0 || kv.Key >= colors.Length) continue;
            colors[kv.Key] = (kv.Value.R, kv.Value.G, kv.Value.B);
        }

        int brightPct = (int)SldEv60CustomBrightness.Value;
        _log($"[KEYS] apply {_ev60CustomKeyColors.Count} painted key(s) bright={brightPct}%");
        bool ok = _ev60.SetCustomKeys(colors, brightPct);
        _log($"[KEYS] SetCustomKeys -> {ok}");
    }

    private void BtnEv60CustomClear_Click(object sender, RoutedEventArgs e)
    {
        _ev60CustomKeyColors.Clear();
        CustomKeysCleared?.Invoke();
    }

    // ------------------------------------------------------------
    // Profile switch: push a stored slot's lighting into this panel's
    // controls, then re-apply whichever of the three modes (preset/side/
    // custom) was active for that profile. Called by
    // MainWindow.Everest60.cs on combo switch, module init, and the
    // disconnected->connected poll transition.
    // ------------------------------------------------------------

    /// <summary>Resets this profile's lighting (preset effect + side ring + any painted
    /// per-key colors) to K2's factory defaults — the same values Init() sets up for a
    /// brand-new profile — and re-applies them to the keyboard if connected. Seeds the
    /// store with an explicit default record rather than clearing it, since
    /// <see cref="Ev60ReloadProfile"/> no-ops on a missing (null) record. Called by
    /// MainWindow.Everest60.cs's "Restore defaults" button.</summary>
    internal void RestoreDefaults()
    {
        if (_ev60Store is null) return;
        _ev60Store.SaveLighting(CurrentSlot, new Ev60LightingRecord(
            (int)Everest60Protocol.Effect.Wave, 0x900000, 0x000000, 50, 0, false,
            100, 0x900000, 100, "preset", new Dictionary<int, int>()));
        Ev60ReloadProfile(CurrentSlot);
        CustomKeysCleared?.Invoke();
    }

    internal void Ev60ReloadProfile(int slot)
    {
        if (_ev60Store is null) return;
        var lighting = _ev60Store.LoadLighting(slot);
        if (lighting is null) return;

        bool wasSuppress = _ev60Suppress;
        _ev60Suppress = true;
        try
        {
            var eff = (Everest60Protocol.Effect)lighting.Effect;
            int idx = Array.FindIndex(Ev60EffectList, x => x.Eff == eff);
            CbEv60Effect.SelectedIndex = idx >= 0 ? idx : 0;
            UpdateEv60Capabilities(); // rebuilds direction row for this effect, resets _ev60DirIndex to 0

            if (GridEv60Direction.Children.Count > 0 && lighting.DirIndex >= 0 &&
                lighting.DirIndex < GridEv60Direction.Children.Count)
                ((RadioButton)GridEv60Direction.Children[lighting.DirIndex]).IsChecked = true;

            _ev60Color1 = lighting.Color1;
            _ev60Color2 = lighting.Color2;
            _ev60SideColor = lighting.SideColor;
            ApplyColorButton(BtnEv60Color1, _ev60Color1);
            ApplyColorButton(BtnEv60Color2, _ev60Color2);
            ApplyColorButton(BtnEv60SideColor, _ev60SideColor);

            SldEv60Speed.Value = lighting.SpeedPct;
            if (LblEv60Speed != null) LblEv60Speed.Text = $"{lighting.SpeedPct}%";

            // Single/Double/Rainbow are one mutually-exclusive radio group —
            // Rainbow wins if both were somehow persisted true.
            if (RbEv60Rainbow.IsEnabled && lighting.Rainbow) RbEv60Rainbow.IsChecked = true;
            else if (RbEv60ColorDouble.IsEnabled && lighting.ColorDouble) RbEv60ColorDouble.IsChecked = true;
            else RbEv60ColorSingle.IsChecked = true;
            UpdateEv60ColorRowVisibility();

            Brightness = lighting.Brightness;

            SldEv60CustomBrightness.Value = lighting.CustomBrightness;
            if (LblEv60CustomBrightness != null)
                LblEv60CustomBrightness.Text = $"{(int)lighting.CustomBrightness}%";

            _ev60CustomKeyColors.Clear();
            foreach (var kv in lighting.CustomKeyColors)
                _ev60CustomKeyColors[kv.Key] = Color.FromRgb(
                    (byte)((kv.Value >> 16) & 0xFF), (byte)((kv.Value >> 8) & 0xFF), (byte)(kv.Value & 0xFF));
        }
        finally { _ev60Suppress = wasSuppress; }

        if (!_ev60Connected)
        {
            _log("[PROFILE] reload: device not connected, UI updated only");
            return;
        }

        switch (lighting.ActiveMode)
        {
            case "side":
                BtnEv60SideApply_Click(this, new RoutedEventArgs());
                break;
            case "custom":
                BtnEv60CustomApply_Click(this, new RoutedEventArgs());
                break;
            default:
                ApplyCurrentEv60Effect();
                break;
        }
    }
}
