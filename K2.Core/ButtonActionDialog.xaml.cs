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

    public ButtonActionDialog(int buttonIndex, string? currentType, string? currentValue)
    {
        InitializeComponent();
        LblHeader.Text = Loc.Get("dlg_button_label").Replace("#?", $"#{buttonIndex}");

        SetType(currentType ?? "none");

        if (currentType == "pyscript")
        {
            LoadPySpec(PyScriptPayload.Parse(currentValue) ?? new PyScriptPayload());
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

    private void CbType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbType.SelectedItem is not ComboBoxItem ci) return;
        var tag = (string?)ci.Tag ?? "none";

        var (label, hint) = tag switch
        {
            "url"      => ("URL to open:",                 "https://example.com"),
            "exec"     => ("Executable / file path:",      @"C:\Program Files\App\app.exe"),
            "folder"   => ("Folder path:",                 @"C:\Users\Francesco\Documents"),
            "browser"  => ("Initial URL (optional):",      "https://duckduckgo.com"),
            "profile"  => ("Target profile:",              "Next | Previous | 1..5"),
            "oscmd"    => ("System command:",               "Calculator | Task Manager | Lock | Sleep | Hibernate | Shutdown"),
            "media"    => ("Media key:",                   "Play/Pause | Stop | Previous | Next | Volume Up | Volume Down | Mute"),
            "mouse"    => ("Mouse action:",                "Left Button | Right Button | Middle Button | Forward | Backward | Scroll Up/Down/Left/Right"),
            "keys"     => ("Shortcut (human syntax):",     "Ctrl + Shift + A   |   Ctrl + F4"),
            "command"  => ("Command line:",                "cmd /c echo hello"),
            "text"     => ("Text to paste:",               "hello world"),
            "pyscript" => ("Python Script",                ""),
            _          => ("No action",                    "")
        };
        LblPayload.Text      = label;
        TxtPayload.IsEnabled = tag != "none";
        if (string.IsNullOrEmpty(TxtPayload.Text)) TxtPayload.Tag = hint;

        UpdatePanels();
    }

    private void UpdatePanels()
    {
        if (PyPanel is null || StandardPanel is null) return;
        bool py = CurrentTag() == "pyscript";
        PyPanel.Visibility       = py ? Visibility.Visible    : Visibility.Collapsed;
        StandardPanel.Visibility = py ? Visibility.Collapsed  : Visibility.Visible;
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
