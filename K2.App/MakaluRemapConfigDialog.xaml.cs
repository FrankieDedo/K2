using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Small modal to pick a Makalu button's remap category/function (and sniper
/// DPI, when applicable) — see MakaluRemapConfigDialog.xaml for why this
/// exists instead of the shared ButtonActionDialog. Content mirrors what used
/// to be inline WrapPanel controls directly under the (now removed) button
/// grid in MakaluDpiRemapPanel, just moved into a popup so the panel itself
/// can show a plain list + Configure/Remove buttons like every other device.
/// </summary>
public partial class MakaluRemapConfigDialog : Window
{
    /// <summary>Raw assignment string on OK ("left", "dpi+", "sniper:{dpi}", ...).</summary>
    public string ResultAssignment { get; private set; }

    private readonly int _dpiMin;
    private bool _suppress = true;

    public MakaluRemapConfigDialog(string buttonLabel, string currentAssignment, int dpiMin)
    {
        InitializeComponent();
        _dpiMin = dpiMin;
        ResultAssignment = currentAssignment;
        LblHeader.Text = Loc.Get("makalu_remap_configure_title", buttonLabel);

        CbCategory.ItemsSource = MakaluRemapData.RemapCategories.Keys.Select(MakaluRemapData.CatLabel).ToArray();
        SyncFromAssignment(currentAssignment);
        _suppress = false;
    }

    private void SyncFromAssignment(string raw)
    {
        string fnKey = raw.StartsWith("sniper:") ? "sniper" : raw;
        int dpi = raw.StartsWith("sniper:") && int.TryParse(raw.Split(':')[1], out int d) ? d : _dpiMin;
        string catKey = MakaluRemapData.RemapCategories.FirstOrDefault(kv => kv.Value.Contains(fnKey)).Key ?? "Mouse";

        _suppress = true;
        CbCategory.SelectedItem = MakaluRemapData.CatLabel(catKey);
        var fns = MakaluRemapData.RemapCategories[catKey];
        CbFunction.ItemsSource = fns.Select(MakaluRemapData.FnLabel).ToArray();
        CbFunction.SelectedItem = MakaluRemapData.FnLabel(fnKey);
        SldSniperDpi.Minimum = _dpiMin;
        SldSniperDpi.Value = Math.Clamp(dpi, _dpiMin, MakaluProtocol.DpiMax);
        TxtSniperDpi.Text = ((int)SldSniperDpi.Value).ToString();
        PnlSniper.Visibility = catKey == "Sniper" ? Visibility.Visible : Visibility.Collapsed;
        _suppress = false;
    }

    private void CbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        string label = CbCategory.SelectedItem as string ?? "";
        string catKey = MakaluRemapData.RemapCategories.Keys.FirstOrDefault(k => MakaluRemapData.CatLabel(k) == label) ?? "Mouse";
        var fns = MakaluRemapData.RemapCategories[catKey];
        CbFunction.ItemsSource = fns.Select(MakaluRemapData.FnLabel).ToArray();
        CbFunction.SelectedIndex = 0;
        PnlSniper.Visibility = catKey == "Sniper" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SldSniperDpi_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        int dpi = MakaluProtocol.QuantizeDpiTiered((int)e.NewValue);
        TxtSniperDpi.Text = dpi.ToString();
    }

    private void TxtSniperDpi_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitSniperEntry();
    }

    private void TxtSniperDpi_LostFocus(object sender, RoutedEventArgs e) => CommitSniperEntry();

    private void CommitSniperEntry()
    {
        if (!int.TryParse(TxtSniperDpi.Text, out int dpi)) dpi = (int)SldSniperDpi.Value;
        dpi = Math.Clamp(MakaluProtocol.QuantizeDpiTiered(dpi), _dpiMin, MakaluProtocol.DpiMax);
        TxtSniperDpi.Text = dpi.ToString();
        SldSniperDpi.Value = dpi;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        string label = CbCategory.SelectedItem as string ?? "";
        string catKey = MakaluRemapData.RemapCategories.Keys.FirstOrDefault(k => MakaluRemapData.CatLabel(k) == label) ?? "Mouse";
        string fnLabel = CbFunction.SelectedItem as string ?? "";
        var fns = MakaluRemapData.RemapCategories[catKey];
        int fi = fns.Select(MakaluRemapData.FnLabel).ToList().IndexOf(fnLabel);
        if (fi < 0) { DialogResult = false; return; }

        string fnKey = fns[fi];
        if (fnKey == "sniper")
        {
            CommitSniperEntry();
            ResultAssignment = $"sniper:{(int)SldSniperDpi.Value}";
        }
        else
        {
            ResultAssignment = fnKey;
        }
        DialogResult = true;
    }
}
