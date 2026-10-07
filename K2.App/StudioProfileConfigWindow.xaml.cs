using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>How Fender Studio Pro's dedicated profile takes and gives back the pad. One set for
/// the PC, not per pad: there is one Studio Pro and one MIDI link to it.</summary>
/// <param name="ReturnEnabled">Come back on its own after <paramref name="ReturnSeconds"/> away
/// (the back key, or a profile picked by hand) while the program is still running.</param>
/// <param name="ForegroundOnly">On the pad only while Studio Pro is the window in front.</param>
/// <param name="BackArrow">Draw the arrow on the back key. The key works either way.</param>
public readonly record struct StudioProfileConfig(bool ReturnEnabled, int ReturnSeconds,
                                                  bool ForegroundOnly, bool BackArrow)
{
    public const int DefaultReturnSeconds = 5;
}

/// <summary>
/// Configuration of the Fender Studio Pro dedicated profile — the same three behaviours as the
/// Spotify profile's popup (return timer, foreground-only, back arrow), plus the one thing this
/// profile needs that Spotify's does not: how to set up the MIDI link, and whether it is up.
/// Opened from the dedicated row's gear and from a <c>dp_studio</c> key's own dialog.
/// </summary>
public partial class StudioProfileConfigWindow : Window
{
    private const string LoopMidiUrl = "https://www.tobias-erichsen.de/software/loopmidi.html";
    private static readonly Regex NonDigit = new("[^0-9]");

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public StudioProfileConfig Result { get; private set; }

    public StudioProfileConfigWindow(StudioProfileConfig cfg)
    {
        InitializeComponent();
        Result = cfg;
        CkReturn.IsChecked = cfg.ReturnEnabled;
        TxtReturnSec.Text = Math.Max(1, cfg.ReturnSeconds).ToString();
        CkForeground.IsChecked = cfg.ForegroundOnly;
        CkBackArrow.IsChecked = cfg.BackArrow;
        TxtReturnSec.IsEnabled = cfg.ReturnEnabled;

        // Live, so the user watches the link come up as they follow the steps below.
        UpdateStatus();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
    }

    private void UpdateStatus()
    {
        var st = ModLinkClient.Want(ModLinkGames.StudioPro);
        TxtStatus.Text = !st.Alive ? Loc.Get("dedicated_studio_midi_missing")
                       : st.InGame ? Loc.Get("dedicated_studio_midi_ok")
                       : Loc.Get("dedicated_studio_midi_wait");
    }

    private void ReturnEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtReturnSec is null) return;   // fires during InitializeComponent
        TxtReturnSec.IsEnabled = CkReturn.IsChecked == true;
    }

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = NonDigit.IsMatch(e.Text);

    private void LnkLoopMidi_Click(object sender, RoutedEventArgs e) => GameLinkMod.Open(LoopMidiUrl);

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        int seconds = int.TryParse(TxtReturnSec.Text, out int s) && s > 0
            ? Math.Min(s, 3600) : StudioProfileConfig.DefaultReturnSeconds;
        Result = new StudioProfileConfig(CkReturn.IsChecked == true, seconds,
                                         CkForeground.IsChecked == true, CkBackArrow.IsChecked == true);
        DialogResult = true;
    }
}
