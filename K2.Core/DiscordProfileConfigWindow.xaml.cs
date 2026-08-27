using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Configuration popup for the DisplayPad's <b>Discord dedicated profile</b> (the live voice page),
/// opened from that profile's gear ▸ Configure. It holds only the knobs that belong to the voice
/// page itself:
/// <list type="bullet">
/// <item>the webcam key's shortcut (<see cref="DiscordStore.WebcamHotkey"/>),</item>
/// <item>the push-to-talk key's shortcut (<see cref="DiscordStore.PushToTalkHotkey"/>), and</item>
/// <item>the screensaver-style delay after which the page reopens on its own once the user has
/// left it for a normal profile mid-call (<see cref="DiscordStore.VoicePageReturnEnabled"/>/
/// <see cref="DiscordStore.VoicePageReturnSeconds"/>).</item>
/// </list>
/// The Discord <i>account</i> (Client ID/Secret, webhook, Connect) lives in the separate
/// <see cref="DiscordSettingsWindow"/> — the same one the "discord" action combo opens — reached
/// from here through a button, not duplicated.
/// </summary>
public partial class DiscordProfileConfigWindow : Window
{
    public DiscordProfileConfigWindow()
    {
        InitializeComponent();
        TxtDiscordWebcamHotkey.Text = DiscordStore.WebcamHotkey;
        TxtDiscordPttHotkey.Text = DiscordStore.PushToTalkHotkey;
        CkDiscordReturn.IsChecked = DiscordStore.VoicePageReturnEnabled;
        TxtDiscordReturnSec.Text = DiscordStore.VoicePageReturnSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void BtnDiscordAccount_Click(object sender, RoutedEventArgs e)
    {
        new DiscordSettingsWindow { Owner = this }.ShowDialog();
    }

    // ---------------------------------------------------------------- hotkey recorder
    // Two boxes (webcam, push-to-talk) share one recorder: the Record button that was clicked sets
    // _recordTarget, and OnPreviewKeyDown writes the captured combination there.

    /// <summary>True between "Record" and the first non-modifier key: every keystroke is captured
    /// instead of reaching the window.</summary>
    private bool _recordingHotkey;

    /// <summary>Box the current recording writes into.</summary>
    private TextBox? _recordTarget;

    /// <summary>Text shown in the box while recording, remembered so Esc can put back whatever was
    /// there before.</summary>
    private string _hotkeyBeforeRecording = "";

    private void BtnDiscordRecordWebcam_Click(object sender, RoutedEventArgs e) => BeginRecording(TxtDiscordWebcamHotkey);
    private void BtnDiscordRecordPtt_Click(object sender, RoutedEventArgs e) => BeginRecording(TxtDiscordPttHotkey);

    private void BeginRecording(TextBox target)
    {
        _recordingHotkey = true;
        _recordTarget = target;
        _hotkeyBeforeRecording = target.Text;
        target.Text = Loc.Get("hotkey_recording");
        // Keyboard focus has to leave the button, or Space/Enter would "press" it again instead
        // of being recorded.
        Keyboard.ClearFocus();
        Focus();
    }

    /// <summary>
    /// Captures the combination while recording. Modifier-only presses are ignored (they are read
    /// from <see cref="Keyboard.Modifiers"/> when the real key lands), Esc cancels, and the result
    /// is written in the same "Ctrl+Shift+V" notation <see cref="SendKeysTranslator"/> parses.
    ///
    /// <para>Handled at the WINDOW level rather than on the box itself: the box is not focusable
    /// (it must never be typed into), so the keystrokes never reach it.</para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recordingHotkey || _recordTarget is null) { base.OnPreviewKeyDown(e); return; }

        // Alt-combinations arrive as Key.System, with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        _recordingHotkey = false;

        if (key == Key.Escape)
        {
            _recordTarget.Text = _hotkeyBeforeRecording;
            _recordTarget = null;
            return;
        }

        var mods = Keyboard.Modifiers;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(HotkeyKeyName(key));
        _recordTarget.Text = string.Join("+", parts);
        _recordTarget = null;
    }

    /// <summary>WPF key → the name <see cref="SendKeysTranslator"/> understands. Digits come back
    /// as <c>D4</c>/<c>NumPad4</c> from <see cref="Key"/>, which that translator would send as a
    /// literal word instead of a digit.</summary>
    private static string HotkeyKeyName(Key key)
    {
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7) return name[6].ToString();
        return key switch
        {
            Key.Return => "Enter",
            Key.Next => "PgDn",
            Key.Prior => "PgUp",
            Key.Back => "Backspace",
            Key.Capital => "CapsLock",
            _ => name,
        };
    }

    // ---------------------------------------------------------------- return timer

    private static readonly Regex NonDigit = new("[^0-9]");

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = NonDigit.IsMatch(e.Text);

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DiscordStore.WebcamHotkey = TxtDiscordWebcamHotkey.Text.Trim();
        DiscordStore.PushToTalkHotkey = TxtDiscordPttHotkey.Text.Trim();
        DiscordStore.VoicePageReturnEnabled = CkDiscordReturn.IsChecked == true;
        if (int.TryParse(TxtDiscordReturnSec.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
            DiscordStore.VoicePageReturnSeconds = sec;   // setter clamps to a sane range
        Close();
    }
}
