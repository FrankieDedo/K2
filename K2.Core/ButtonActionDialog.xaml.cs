using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace K2.Core;

public partial class ButtonActionDialog : Window
{
    public string ActionType  { get; private set; } = "none";
    public string ActionValue { get; private set; } = "";

    /// <summary>
    /// The device host the button being configured belongs to (null in the few call
    /// sites that don't have one handy — the "switch profile" cross-device picker then
    /// degrades to self-target only, since it has no host to enumerate other devices).
    /// </summary>
    private readonly IActionHost? _host;

    /// <param name="allowedCategories">Restricts the action PICKER to these categories (see
    /// <c>ButtonActionDialog.TypePicker</c>). Used by the game profile editor, which offers Input
    /// and Game controls only. Null keeps the ordinary set.</param>
    public ButtonActionDialog(int buttonIndex, string? currentType, string? currentValue, IActionHost? host = null,
        string[]? allowedTypes = null, string[]? allowedCategories = null,
        GamePickerProfile? gameProfile = null)
    {
        InitializeComponent();
        _host = host;
        _allowedCategories = allowedCategories;
        _gameProfile = gameProfile;
        LblHeader.Text = Loc.Get("dlg_button_label").Replace("#?", $"#{buttonIndex}");
        Closed += (_, _) => { _livePreviewTimer?.Stop(); _livePreviewTimer = null; };

        // Restrict the type list to an explicit allow-list (used by the macro-step
        // picker, which only offers action types that have a real write effect —
        // no "none"/"disable" and no DisplayPad-visual-only types).
        if (allowedTypes is { Length: > 0 })
        {
            foreach (var item in CbType.Items.OfType<ComboBoxItem>()
                         .Where(i => Array.IndexOf(allowedTypes, (string?)i.Tag) < 0).ToList())
                CbType.Items.Remove(item);
        }

        // Hide the "Page" type entirely (not just non-functional/empty like "macro") on
        // hosts with no DisplayPad-page concept — a MacroPad/Everest key can never
        // meaningfully navigate a DisplayPad page.
        if (_host?.SupportsPages != true)
        {
            foreach (var dpOnly in CbType.Items.OfType<ComboBoxItem>()
                         .Where(i => Array.IndexOf(ActionTypeHelper.PageOnlyActionTypes, (string?)i.Tag) >= 0).ToList())
                CbType.Items.Remove(dpOnly);
        }

        // Two types whose values are things the USER defines. On a host with no store behind
        // them the card would open a picker that can only answer "none", so the type is dropped
        // from the list entirely and the picker skips it with it.
        if (_host?.SupportsScreenProbes != true) RemoveType("dp_screen");
        if (_host?.SupportsCustomActions != true) RemoveType(CustomActionType.Tag);

        // Set BEFORE SetType(): assigning CbType.SelectedItem below fires
        // CbType_SelectionChanged synchronously, which cascades into UpdatePanels() ->
        // EnsurePagePanel() — that needs _originalPageId already resolved to pre-select
        // the right page in the combo.
        if (currentType == "dp_folder")
            _originalPageId = int.TryParse(currentValue, out int pid) ? pid : (int?)null;

        SetType(currentType ?? "none");

        if (currentType == "pyscript")
        {
            LoadPySpec(PyScriptPayload.Parse(currentValue) ?? new PyScriptPayload());
        }
        else if (currentType == "exec")
        {
            var (execPath, execConsole) = ExecActionPayload.Split(currentValue);
            TxtExecPath.Text = execPath;
            // Set after the Text (TextChanged toggles the panel's visibility) and never from
            // XAML: an IsChecked="True" attribute fires Checked mid-InitializeComponent.
            RbExecBatchConsole.IsChecked = execConsole;
            RbExecBatchHidden.IsChecked  = !execConsole;
        }
        else if (currentType == "folder")
        {
            TxtFolderPath.Text = currentValue ?? "";
        }
        else if (currentType == "browser")
        {
            LoadBrowserSpec(BrowserActionPayload.Parse(currentValue)
                ?? new BrowserActionPayload { Browser = "other", Url = currentValue ?? "" });
        }
        else if (currentType == "profile")
        {
            LoadProfileSpec(ProfileTargetPayload.Parse(currentValue)
                ?? LegacyProfileSpec(currentValue));
        }
        // "dp_screen"/"dp_custom" are in this list too although their value list is built from a
        // host store rather than a fixed enum: without the load, CbComboValue stayed EMPTY on a
        // dialog opened on an existing key — the 3rd crumb showed nothing, the sub-action grid
        // opened blank, a game profile could not recognise the key as one of the game's own
        // commands (so the breadcrumb read "Live tiles > Generic" instead of the game's family),
        // and saving wrote the value back as "" (user report 2026-09-18).
        else if (currentType is "oscmd" or "media" or "mouse" or "macro" or "googlehome" or "obs" or "twitch" or "spotify" or "discord" or "audiodevice"
                 or "dp_clock" or "dp_sysmon" or "dp_speedtest" or "dp_edstatus" or "dp_zcstatus"
                 or KspTelemachus.ActionType or ModLinkGames.ActionType or "dp_screen" or CustomActionType.Tag)
        {
            LoadComboSpec(currentType, currentValue ?? "");
        }
        else if (currentType == "keys")
        {
            LoadKeysSpec(currentValue ?? "");
        }
        else if (currentType == "hotkeyswitch")
        {
            LoadHotkeySwitchSpec(currentValue ?? "");
        }
        else if (currentType == "multi")
        {
            LoadMultiSpec(currentValue ?? "");
        }
        else if (currentType is "adobe" or "davinci" or "zoom")
        {
            LoadAppShortcutSpec(currentType, currentValue ?? "");
        }
        else if (currentType == "youtube")
        {
            TxtYoutubeMessage.Text = currentValue ?? "";
        }
        else if (currentType == "emoji")
        {
            LoadEmojiSpec(currentValue ?? "");
        }
        else
        {
            TxtPayload.Text  = currentValue ?? "";
            RbFile.IsChecked = true;          // sensible default for the pyscript panel
        }

        UpdatePanels();
    }

    private void LoadPySpec(PyScriptPayload spec)
    {
        TxtPath.Text    = spec.Path;
        TxtCode.Text    = spec.Code;
        TxtArgs.Text    = spec.Args;
        TxtTimeout.Text = spec.TimeoutSeconds.ToString();
        RbInline.IsChecked = spec.Inline;
        RbFile.IsChecked   = !spec.Inline;
        UpdatePyMode();
    }

    // ---- action type ------------------------------------------------

    private void SetType(string tag)
    {
        foreach (var item in CbType.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag == tag)
            {
                CbType.SelectedItem = item;
                return;
            }
        }
        CbType.SelectedIndex = 0;
    }

    private string CurrentTag()
        => CbType.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "none" : "none";

    /// <summary>Drops one action type from the list. Used for the types a host cannot back;
    /// silently does nothing when the tag isn't there, since an allow-list may have removed it
    /// already.</summary>
    private void RemoveType(string tag)
    {
        foreach (var item in CbType.Items.OfType<ComboBoxItem>()
                     .Where(i => (string?)i.Tag == tag).ToList())
            CbType.Items.Remove(item);
    }

    private void CbType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbType.SelectedItem is not ComboBoxItem ci) return;
        var tag = (string?)ci.Tag ?? "none";

        var (label, hint) = tag switch
        {
            "url"      => ("URL to open:",                 "https://example.com"),
            "exec"     => ("Executable / file path:",      @"C:\Program Files\App\app.exe"),
            "folder"   => ("Folder path:",                 @"C:\Users\Francesco\Documents"),
            "dp_folder"=> ("Page:",                        ""),
            "browser"  => ("Initial URL (optional):",      "https://duckduckgo.com"),
            "profile"  => ("Target profile:",              "Next | Previous | 1..5"),
            "oscmd"    => ("System command:",               "Calculator | Task Manager | Lock | Sleep | Hibernate | Shutdown"),
            "media"    => ("Media key:",                   "Play/Pause | Stop | Previous | Next | Volume Up | Volume Down | Mute"),
            "mouse"    => ("Mouse action:",                "Left Button | Right Button | Middle Button | Forward | Backward | Scroll Up/Down/Left/Right"),
            "keys"     => ("Shortcut (human syntax):",     "Ctrl + Shift + A   |   Ctrl + F4"),
            "hotkeyswitch" => ("Two alternating shortcuts:", ""),
            "multi"    => ("Chain of steps:",              ""),
            "adobe"    => ("Adobe shortcut:",               ""),
            "davinci"  => ("DaVinci Resolve shortcut:",     ""),
            "zoom"     => ("Zoom shortcut:",                ""),
            "command"  => ("Command line:",                "cmd /c echo hello"),
            "text"     => ("Text to paste:",               "hello world"),
            "emoji"    => ("Emoji:",                        ""),
            "macro"    => ("Macro:",                        ""),
            "googlehome" => ("Google Home action:",         ""),
            "obs"        => ("OBS Studio command:",         ""),
            "twitch"     => ("Twitch action:",              ""),
            "discord"    => ("Discord action:",             ""),
            "spotify"    => ("Spotify action:",             ""),
            "audiodevice" => ("Windows audio device:",      ""),
            "youtube"    => ("YouTube live chat message:",  ""),
            "pyscript" => ("Python Script",                ""),
            "dp_emojibrowser" => ("Emoji browser (no payload)", ""),
            "disable"  => ("Key disabled (no payload)",    ""),
            _          => ("No action",                    "")
        };
        LblPayload.Text      = label;
        TxtPayload.IsEnabled = tag is not ("none" or "disable" or "dp_emojibrowser");
        if (string.IsNullOrEmpty(TxtPayload.Text)) TxtPayload.Tag = hint;

        UpdateBreadcrumb(tag);
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (PyPanel is null || StandardPanel is null) return;
        var tag = CurrentTag();
        bool py      = tag == "pyscript";
        bool exec    = tag == "exec";
        bool folder  = tag == "folder";
        bool page    = tag == "dp_folder";
        bool browser = tag == "browser";
        bool profile = tag == "profile";
        // "dp_screen"/"dp_custom" belong here as well: their value lives in CbComboValue like every
        // other combo type, and ComboPanel is where their "Edit reading…"/"Edit action…" button and
        // their live readout sit. Leaving them out is what kept the combo unpopulated — see the
        // constructor's LoadComboSpec branch.
        bool combo   = tag is "oscmd" or "media" or "mouse" or "macro" or "googlehome" or "obs" or "twitch" or "spotify" or "discord" or "audiodevice"
                              or "dp_clock" or "dp_sysmon" or "dp_speedtest" or "dp_edstatus"
                              or "dp_zcstatus" or KspTelemachus.ActionType or ModLinkGames.ActionType
                              or "dp_screen" or CustomActionType.Tag;
        bool sysmon  = tag == "dp_sysmon";
        bool keys    = tag == "keys";
        bool hotkeyswitch = tag == "hotkeyswitch";
        bool multi   = tag == "multi";
        bool appShortcut = tag is "adobe" or "davinci" or "zoom";
        bool youtube = tag == "youtube";
        bool emoji   = tag == "emoji";
        bool emojiBrowser = tag == "dp_emojibrowser";
        // A custom action is CHOSEN in the picker and has nothing to type — the breadcrumb above
        // says which action the key carries, which is the whole of it (user request 2026-09-08).
        // That is already the case now that it counts as a "combo" type: ComboPanel's label and
        // list are permanently Collapsed in XAML, so only the "Edit action…" button and the live
        // readout show, and it never falls through to the standard value box.
        bool std     = !py && !exec && !folder && !page && !browser && !profile && !combo && !keys && !hotkeyswitch && !multi && !appShortcut && !youtube && !emoji && !emojiBrowser;

        PyPanel.Visibility       = py      ? Visibility.Visible : Visibility.Collapsed;
        ExecPanel.Visibility     = exec    ? Visibility.Visible : Visibility.Collapsed;
        FolderPanel.Visibility   = folder  ? Visibility.Visible : Visibility.Collapsed;
        PagePanel.Visibility     = page    ? Visibility.Visible : Visibility.Collapsed;
        BrowserPanel.Visibility  = browser ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility  = profile ? Visibility.Visible : Visibility.Collapsed;
        ComboPanel.Visibility    = combo   ? Visibility.Visible : Visibility.Collapsed;
        SysMonPanel.Visibility   = sysmon  ? Visibility.Visible : Visibility.Collapsed;
        KeysPanel.Visibility     = keys    ? Visibility.Visible : Visibility.Collapsed;
        HotkeySwitchPanel.Visibility = hotkeyswitch ? Visibility.Visible : Visibility.Collapsed;
        MultiPanel.Visibility    = multi   ? Visibility.Visible : Visibility.Collapsed;
        AppShortcutPanel.Visibility = appShortcut ? Visibility.Visible : Visibility.Collapsed;
        YoutubePanel.Visibility  = youtube ? Visibility.Visible : Visibility.Collapsed;
        EmojiPanel.Visibility    = emoji   ? Visibility.Visible : Visibility.Collapsed;
        StandardPanel.Visibility = std     ? Visibility.Visible : Visibility.Collapsed;

        if (exec) RefreshExecPanel();
        if (folder) RefreshFolderPanel();
        if (page) EnsurePagePanel();
        if (browser) EnsureBrowserChoicesPopulated();
        if (profile) EnsureProfileRows();
        if (multi) EnsureMultiPanel();
        if (combo) EnsureComboPanel(tag);
        if (sysmon) RefreshSysMonPanel();
        UpdateLivePreview(tag);
        if (keys) EnsureKeysPanel();
        if (hotkeyswitch) EnsureHotkeySwitchPanel();
        if (appShortcut) EnsureAppShortcutPanel(tag);
        if (emoji) RefreshEmojiPreview();
    }

    // ---- Python Script panel ----------------------------------------

    private void PyMode_Changed(object sender, RoutedEventArgs e) => UpdatePyMode();

    private void UpdatePyMode()
    {
        if (PathRow is null || TxtCode is null) return;
        bool inline = RbInline.IsChecked == true;
        PathRow.Visibility = inline ? Visibility.Collapsed : Visibility.Visible;
        TxtCode.Visibility = inline ? Visibility.Visible   : Visibility.Collapsed;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.Get("py_browse"),
            Filter = "Python Script (*.py;*.pyw)|*.py;*.pyw|All files|*.*"
        };
        try
        {
            var dir = Path.GetDirectoryName(TxtPath.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch { /* invalid path: ignore */ }

        if (dlg.ShowDialog(this) == true)
            TxtPath.Text = dlg.FileName;
    }

    // ---- save / cancel ----------------------------------------------

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var tag = CurrentTag();
        ActionType = tag;

        if (tag == "pyscript")
        {
            int timeout = 60;
            var t = TxtTimeout.Text?.Trim() ?? "";
            if (t.Length > 0 && int.TryParse(t, out var parsed))
                timeout = parsed < 0 ? 0 : parsed;

            var spec = new PyScriptPayload
            {
                Inline         = RbInline.IsChecked == true,
                Path           = TxtPath.Text?.Trim() ?? "",
                Code           = TxtCode.Text ?? "",
                Args           = TxtArgs.Text?.Trim() ?? "",
                TimeoutSeconds = timeout,
            };
            ActionValue = spec.ToJson();
        }
        else if (tag == "exec")
        {
            var execPath = TxtExecPath.Text?.Trim() ?? "";
            ActionValue = ExecActionPayload.Build(execPath, RbExecBatchConsole.IsChecked == true);
            // The recent list stores bare paths only — the terminal flag is per-binding.
            if (execPath.Length > 0) AppSettings.AddRecentExecPath(execPath);
        }
        else if (tag == "folder")
        {
            ActionValue = TxtFolderPath.Text?.Trim() ?? "";
            if (ActionValue.Length > 0) AppSettings.AddRecentFolderPath(ActionValue);
        }
        else if (tag == "dp_folder")
        {
            ActionValue = SavePageSpec();
        }
        else if (tag == "browser")
        {
            ActionValue = SaveBrowserSpec().ToJson();
        }
        else if (tag == "profile")
        {
            ActionValue = SaveProfileSpec().ToJson();
        }
        else if (tag is "oscmd" or "media" or "mouse" or "macro" or "googlehome" or "obs" or "twitch" or "spotify" or "discord" or "audiodevice"
                 or "dp_clock" or "dp_speedtest" or "dp_edstatus" or "dp_zcstatus" or KspTelemachus.ActionType or ModLinkGames.ActionType)
        {
            ActionValue = SaveComboSpec();
        }
        else if (tag == "dp_sysmon")
        {
            ActionValue = SaveSysMonSpec();
        }
        else if (tag == "dp_screen")
        {
            ActionValue = SaveScreenProbeSpec();
        }
        else if (tag == CustomActionType.Tag)
        {
            ActionValue = SaveCustomActionSpec();
        }
        else if (tag == "keys")
        {
            ActionValue = SaveKeysSpec();
        }
        else if (tag == "hotkeyswitch")
        {
            ActionValue = SaveHotkeySwitchSpec();
        }
        else if (tag == "multi")
        {
            ActionValue = SaveMultiSpec();
        }
        else if (tag is "adobe" or "davinci" or "zoom")
        {
            ActionValue = SaveAppShortcutSpec();
        }
        else if (tag == "youtube")
        {
            ActionValue = TxtYoutubeMessage.Text?.Trim() ?? "";
        }
        else if (tag == "emoji")
        {
            ActionValue = SaveEmojiSpec();
        }
        else if (tag == "dp_emojibrowser")
        {
            // The whole browser lives in MainWindow.DisplayPad.EmojiBrowser.cs; the key
            // binding is just the "open it" marker, with nothing to configure.
            ActionValue = "";
        }
        else if (tag == "disable")
        {
            // Payload-less by definition — never carry over whatever the (disabled)
            // standard text box happened to still show from the previous type.
            ActionValue = "";
        }
        else
        {
            ActionValue = TxtPayload.Text ?? "";
        }

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
