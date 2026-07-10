using System;
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

    internal void Init(Everest60Service service, Action<string> log)
    {
        _ev60 = service;
        _log = log;
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
    }

    internal void SetConnected(bool connected) => _ev60Connected = connected;

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
            _log("[RGB ] skip: Everest 60 not connected");
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

        _log($"[RGB ] apply eff={pick.Eff} speed={speedPct}% bright={brightPct}% " +
             $"rainbow={rainbow} dir=0x{direction:X2} c1=#{_ev60Color1:X6}" +
             (secondary.HasValue ? $" c2=#{_ev60Color2:X6}" : ""));
        bool ok = _ev60.SetEffect(pick.Eff, speedPct, brightPct, C(_ev60Color1), secondary, rainbow, direction);
        _log($"[RGB ] SetEffect -> {ok}");
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
        if (!_ev60Connected) { _log("[RGB ] skip: Everest 60 not connected"); return; }
        (byte r, byte g, byte b) c = ((byte)((_ev60SideColor >> 16) & 0xFF),
                                       (byte)((_ev60SideColor >> 8) & 0xFF),
                                       (byte)(_ev60SideColor & 0xFF));
        int brightPct = (int)SldEv60Brightness.Value;
        _log($"[RGB ] apply side ring color=#{_ev60SideColor:X6} bright={brightPct}%");
        bool ok = _ev60.SetSideRing(c, brightPct);
        _log($"[RGB ] SetSideRing -> {ok}");
    }
}
