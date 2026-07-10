using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: Everest 60 tab — raw-HID connectivity check + RGB
/// lighting (preset effects + side ring). See <see cref="Everest60HidNative"/>
/// for why this talks HID Feature Reports instead of the SDK, and its remarks
/// for what's NOT implemented yet (key remapping/macros: firmware protocol
/// not reverse-engineered by any known source).
///
/// State (effect + params + colors) lives only in memory for this first cut —
/// per-session persistence (like Everest Max's <c>rgb.*</c> Settings keys) is
/// a future step once the panel has proven itself on real hardware.
///
/// Reuses <c>ApplyColorButton</c> from MainWindow.Everest.cs (same helper,
/// same partial class).
/// </summary>
public partial class MainWindow
{
    // Field initializer: legal to reference an instance method group here
    // (it just builds a delegate bound to `this`, not invoked yet). By the
    // time LogEverest60 actually runs, InitializeComponent() has long since
    // assigned TxtEv60Log — see the guard at the bottom of this file.
    private Everest60Service _ev60 = null!;
    private DispatcherTimer? _ev60PollTimer;
    private bool _ev60Initialized;
    private bool _ev60Suppress;
    private bool _ev60Connected;

    private int _ev60Color1 = 0x900000;
    private int _ev60Color2 = 0x000000;
    private int _ev60SideColor = 0x900000;

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

    // ------------------------------------------------------------
    // Init
    // ------------------------------------------------------------

    /// <summary>Sets up the Effect/Direction combos, starts the connection
    /// poll timer. Called once from the MainWindow constructor.</summary>
    private void InitEverest60Module()
    {
        _ev60 = new Everest60Service(LogEverest60);
        _ev60Suppress = true;
        try
        {
            CbEv60Effect.ItemsSource = Ev60EffectList;
            CbEv60Effect.DisplayMemberPath = "Label";
            CbEv60Effect.SelectedIndex = 2; // Wave, mirrors Everest Max's default

            SldEv60Speed.Value = 50;
            SldEv60Brightness.Value = 100;

            UpdateEv60Capabilities();
            ApplyColorButton(BtnEv60Color1, _ev60Color1);
            ApplyColorButton(BtnEv60Color2, _ev60Color2);
            ApplyColorButton(BtnEv60SideColor, _ev60SideColor);
        }
        finally
        {
            _ev60Suppress = false;
        }
        _ev60Initialized = true;

        _ev60PollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _ev60PollTimer.Tick += (_, _) => Ev60RefreshStatus();
        _ev60PollTimer.Start();
        Ev60RefreshStatus();
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
            SldEv60Speed.IsEnabled = caps.Speed;

            if (caps.Directions.Length > 0)
            {
                CbEv60Direction.ItemsSource = caps.Directions;
                CbEv60Direction.DisplayMemberPath = "Label";
                CbEv60Direction.SelectedIndex = 0;
                CbEv60Direction.IsEnabled = true;
            }
            else
            {
                CbEv60Direction.ItemsSource = null;
                CbEv60Direction.IsEnabled = false;
            }

            CkEv60Rainbow.IsEnabled = caps.Rainbow;
            if (!caps.Rainbow) CkEv60Rainbow.IsChecked = false;

            PnlEv60Color2.Visibility = caps.MaxColors >= 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _ev60Suppress = prev;
        }
    }

    // ------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------

    private void Ev60RefreshStatus()
    {
        bool connected = _ev60.IsConnected(out string model);
        _ev60Connected = connected;
        LblEv60Status.Text = connected
            ? Loc.Get("ev60_status_connected", model)
            : Loc.Get("ev60_status_disconnected");
        LblEv60Status.Foreground = connected
            ? (Brush)FindResource("K2AccentBrush")
            : (Brush)FindResource("K2TextMutedBrush");
    }

    private void BtnEv60Refresh_Click(object sender, RoutedEventArgs e) => Ev60RefreshStatus();

    // ------------------------------------------------------------
    // Effect panel event handlers
    // ------------------------------------------------------------

    private void CbEv60Effect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEv60Capabilities();
        ApplyCurrentEv60Effect();
    }

    private void CbEv60EffectParam_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplyCurrentEv60Effect();

    private void SldEv60Param_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEv60Speed != null) LblEv60Speed.Text = $"{(int)SldEv60Speed.Value}%";
        if (LblEv60Brightness != null) LblEv60Brightness.Text = $"{(int)SldEv60Brightness.Value}%";
        ApplyCurrentEv60Effect();
    }

    private void CkEv60Rainbow_Click(object sender, RoutedEventArgs e) => ApplyCurrentEv60Effect();

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

    /// <summary>Reads the panel and sends the effect to the firmware. No-op
    /// while still initializing or while the device isn't connected.</summary>
    private void ApplyCurrentEv60Effect()
    {
        if (!_ev60Initialized || _ev60Suppress) return;
        if (CbEv60Effect.SelectedItem is not Ev60EffectChoice pick)
            return;
        if (!_ev60Connected)
        {
            LogEverest60("[RGB ] skip: Everest 60 not connected");
            return;
        }

        var caps = CapsFor(pick.Eff);
        int speedPct = (int)SldEv60Speed.Value;
        int brightPct = (int)SldEv60Brightness.Value;
        bool rainbow = caps.Rainbow && CkEv60Rainbow.IsChecked == true;

        byte direction = caps.Directions.Length > 0 && CbEv60Direction.SelectedItem is Ev60DirChoice dir
            ? dir.Code
            : (byte)0;

        (byte r, byte g, byte b) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        (byte, byte, byte)? secondary = caps.MaxColors >= 2 && !rainbow ? C(_ev60Color2) : null;

        LogEverest60($"[RGB ] apply eff={pick.Eff} speed={speedPct}% bright={brightPct}% " +
                     $"rainbow={rainbow} dir=0x{direction:X2} c1=#{_ev60Color1:X6}" +
                     (secondary.HasValue ? $" c2=#{_ev60Color2:X6}" : ""));
        bool ok = _ev60.SetEffect(pick.Eff, speedPct, brightPct, C(_ev60Color1), secondary, rainbow, direction);
        LogEverest60($"[RGB ] SetEffect -> {ok}");
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
        if (!_ev60Connected) { LogEverest60("[RGB ] skip: Everest 60 not connected"); return; }
        (byte r, byte g, byte b) c = ((byte)((_ev60SideColor >> 16) & 0xFF),
                                       (byte)((_ev60SideColor >> 8) & 0xFF),
                                       (byte)(_ev60SideColor & 0xFF));
        int brightPct = (int)SldEv60Brightness.Value;
        LogEverest60($"[RGB ] apply side ring color=#{_ev60SideColor:X6} bright={brightPct}%");
        bool ok = _ev60.SetSideRing(c, brightPct);
        LogEverest60($"[RGB ] SetSideRing -> {ok}");
    }

    // ------------------------------------------------------------
    // Log
    // ------------------------------------------------------------

    private void LogEverest60(string text)
    {
        if (AppSettings.LogLevel == K2LogLevel.Off) return;
        App.WriteLog("[Everest60] " + text);
        if (TxtEv60Log == null) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtEv60Log.AppendText(line + Environment.NewLine);
        TxtEv60Log.ScrollToEnd();
    }
}
