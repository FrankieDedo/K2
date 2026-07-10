using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// DPI + Remap section content for the Makalu tab — see MakaluDpiRemapPanel.xaml
/// for why this is kept as its own small UserControl. Owns no MakaluService of
/// its own; the parent (MakaluTabPanel) passes one in via <see cref="Init"/> and
/// keeps it in sync via <see cref="UpdateDeviceInfo"/> whenever the detected
/// model/connection state changes.
/// </summary>
public partial class MakaluDpiRemapPanel : UserControl
{
    private MakaluService _makalu = null!;
    private Action<string> _log = _ => { };
    private MakaluService.DeviceInfo _mkInfo =
        new(MakaluService.Model.Makalu67, "Makalu 67", 6, MakaluProtocol.DpiMin67);
    /// <summary>Defaults to true (not false) so any event that WPF fires
    /// synchronously WHILE InitializeComponent() is still parsing this
    /// control's own BAML (e.g. SldMkDpi's Minimum="50" coercing Value up
    /// from its default 0, which fires SldMkDpi_ValueChanged before
    /// TxtMkDpi — declared later in the XAML — has been assigned) is a
    /// no-op instead of null-refing. Root-caused via WinDbg+SOS 2026-07-10,
    /// see CHANGELOG.md — this was the actual crash, not a JIT/CLR bug.
    /// Cleared at the end of Init().</summary>
    private bool _mkSuppress = true;

    public MakaluDpiRemapPanel()
    {
        InitializeComponent();
    }

    internal void Init(MakaluService service, Action<string> log)
    {
        _makalu = service;
        _log = log;
        BuildMkDpiLevelButtons();
        BuildMkRemapButtons();
        _mkSuppress = false;
    }

    /// <summary>Called by the parent whenever the detected model/connection
    /// state changes — rebuilds the DPI/remap button sets for the new model
    /// and re-syncs values from the device.</summary>
    internal void UpdateDeviceInfo(MakaluService.DeviceInfo info)
    {
        _mkInfo = info;
        BuildMkDpiLevelButtons();
        BuildMkRemapButtons();
        MkDpiRefreshFromDevice();
    }

    /// <summary>Selects the given physical button as the active one for the
    /// Remap section — called from MakaluTabPanel when a hotspot on the
    /// device image is clicked.</summary>
    internal void SelectRemapButton(int btnIdx) => MkRemapSelectButton(btnIdx);

    // ------------------------------------------------------------
    // DPI
    // ------------------------------------------------------------

    private readonly List<Button> _mkDpiLevelButtons = new();
    private int[] _mkDpiValues = { 400, 800, 1600, 3200, 6400 };
    private int _mkDpiActive;

    private static int QuantizeDpi(int dpi) => (int)Math.Round(dpi / (double)MakaluProtocol.DpiStep) * MakaluProtocol.DpiStep;

    private void BuildMkDpiLevelButtons()
    {
        PnlMkDpiLevels.Children.Clear();
        _mkDpiLevelButtons.Clear();
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Width = 54, Height = 40, Margin = new Thickness(0, 0, 4, 4),
                Content = $"L{i + 1}\n{_mkDpiValues[i]}",
            };
            btn.Click += (_, _) => MkDpiSelectLevel(idx);
            PnlMkDpiLevels.Children.Add(btn);
            _mkDpiLevelButtons.Add(btn);
        }
        SldMkDpi.Minimum = _mkInfo.DpiMin;
        MkUpdateDpiButtonLabels();
        SldMkDpi.Value = _mkDpiValues[_mkDpiActive];
        TxtMkDpi.Text = _mkDpiValues[_mkDpiActive].ToString();
    }

    private void MkUpdateDpiButtonLabels()
    {
        for (int i = 0; i < _mkDpiLevelButtons.Count; i++)
        {
            _mkDpiLevelButtons[i].Content = $"L{i + 1}\n{_mkDpiValues[i]}";
            _mkDpiLevelButtons[i].Background = i == _mkDpiActive
                ? (Brush)FindResource("K2AccentBrush")
                : (Brush)FindResource("K2HoverBrush");
        }
    }

    private void MkDpiSelectLevel(int idx)
    {
        _mkDpiActive = idx;
        SldMkDpi.Value = _mkDpiValues[idx];
        TxtMkDpi.Text = _mkDpiValues[idx].ToString();
        MkUpdateDpiButtonLabels();
    }

    private void SldMkDpi_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mkSuppress) return;
        int dpi = QuantizeDpi((int)e.NewValue);
        _mkDpiValues[_mkDpiActive] = dpi;
        TxtMkDpi.Text = dpi.ToString();
        MkUpdateDpiButtonLabels();
    }

    private void TxtMkDpi_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MkCommitDpiEntry();
    }

    private void TxtMkDpi_LostFocus(object sender, RoutedEventArgs e) => MkCommitDpiEntry();

    private void MkCommitDpiEntry()
    {
        if (!int.TryParse(TxtMkDpi.Text, out int dpi)) dpi = _mkDpiValues[_mkDpiActive];
        dpi = Math.Clamp(QuantizeDpi(dpi), _mkInfo.DpiMin, MakaluProtocol.DpiMax);
        _mkDpiValues[_mkDpiActive] = dpi;
        TxtMkDpi.Text = dpi.ToString();
        _mkSuppress = true;
        try { SldMkDpi.Value = dpi; } finally { _mkSuppress = false; }
        MkUpdateDpiButtonLabels();
    }

    private void BtnMkDpiApply_Click(object sender, RoutedEventArgs e)
    {
        MkCommitDpiEntry();
        LblMkDpiStatus.Text = "...";
        bool ok = _makalu.SetAllDpi(_mkDpiValues, _mkDpiActive + 1, _mkInfo.DpiMin);
        _log($"[DPI ] SetAllDpi([{string.Join(",", _mkDpiValues)}], active={_mkDpiActive + 1}) -> {ok}");
        LblMkDpiStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkDpiStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void BtnMkDpiRefresh_Click(object sender, RoutedEventArgs e) => MkDpiRefreshFromDevice();

    private void MkDpiRefreshFromDevice()
    {
        var result = _makalu.GetDpi(_mkInfo.DpiMin);
        if (result is null) { _log("[DPI ] GetDpi -> not connected/failed"); return; }
        _mkDpiValues = result.Value.Levels;
        _mkDpiActive = result.Value.Active;
        MkUpdateDpiButtonLabels();
        _mkSuppress = true;
        try { SldMkDpi.Value = _mkDpiValues[_mkDpiActive]; } finally { _mkSuppress = false; }
        TxtMkDpi.Text = _mkDpiValues[_mkDpiActive].ToString();
        _log($"[DPI ] GetDpi -> levels=[{string.Join(",", _mkDpiValues)}] active={_mkDpiActive}");
    }

    // ------------------------------------------------------------
    // Button remap + sniper
    // ------------------------------------------------------------

    private readonly Dictionary<int, Button> _mkRemapButtons = new();
    private Dictionary<int, string> _mkRemapAssignments = new();
    private int _mkRemapActiveButton = 1;
    private string _mkRemapCatKey = "Mouse";

    private void BuildMkRemapButtons()
    {
        PnlMkRemapButtons.Children.Clear();
        _mkRemapButtons.Clear();
        var names = MakaluRemapData.BtnNames(_mkInfo.Model);
        _mkRemapAssignments = new Dictionary<int, string>(MakaluRemapData.RemapDefaults(_mkInfo.Model));

        foreach (var kv in names.OrderBy(k => k.Key))
        {
            int btnIdx = kv.Key;
            var btn = new Button
            {
                Width = 96, Height = 46, Margin = new Thickness(0, 0, 4, 4),
                Content = MakaluRemapData.RemapBtnText(Loc.Get(kv.Value), _mkRemapAssignments[btnIdx]),
            };
            btn.Click += (_, _) => MkRemapSelectButton(btnIdx);
            PnlMkRemapButtons.Children.Add(btn);
            _mkRemapButtons[btnIdx] = btn;
        }
        _mkRemapActiveButton = names.Keys.Min();

        _mkSuppress = true;
        try
        {
            CbMkRemapCategory.ItemsSource = MakaluRemapData.RemapCategories.Keys.Select(MakaluRemapData.CatLabel).ToArray();
            CbMkRemapCategory.SelectedIndex = 0;
        }
        finally { _mkSuppress = false; }

        MkRemapSyncDropdowns(_mkRemapActiveButton);
        MkUpdateRemapButtonHighlight();
    }

    private void MkUpdateRemapButtonHighlight()
    {
        foreach (var kv in _mkRemapButtons)
            kv.Value.Background = kv.Key == _mkRemapActiveButton
                ? (Brush)FindResource("K2AccentBrush")
                : (Brush)FindResource("K2HoverBrush");
    }

    private void MkRemapSelectButton(int idx)
    {
        _mkRemapActiveButton = idx;
        MkUpdateRemapButtonHighlight();
        MkRemapSyncDropdowns(idx);
    }

    /// <summary>Aligns the category/function dropdowns (and sniper row) to the
    /// current assignment of the given physical button.</summary>
    private void MkRemapSyncDropdowns(int btnIdx)
    {
        string raw = _mkRemapAssignments.GetValueOrDefault(btnIdx, "left");
        string fnKey = raw.StartsWith("sniper:") ? "sniper" : raw;
        if (raw.StartsWith("sniper:") && int.TryParse(raw.Split(':')[1], out int dpi))
        {
            SldMkSniperDpi.Value = dpi;
            TxtMkSniperDpi.Text = dpi.ToString();
        }

        string catKey = MakaluRemapData.RemapCategories.FirstOrDefault(kv => kv.Value.Contains(fnKey)).Key ?? "Mouse";
        _mkRemapCatKey = catKey;

        bool prev = _mkSuppress;
        _mkSuppress = true;
        try
        {
            CbMkRemapCategory.SelectedItem = MakaluRemapData.CatLabel(catKey);
            var fns = MakaluRemapData.RemapCategories[catKey];
            CbMkRemapFunction.ItemsSource = fns.Select(MakaluRemapData.FnLabel).ToArray();
            CbMkRemapFunction.SelectedItem = MakaluRemapData.FnLabel(fnKey);
        }
        finally { _mkSuppress = prev; }

        PnlMkSniper.Visibility = catKey == "Sniper" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CbMkRemapCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mkSuppress) return;
        string label = CbMkRemapCategory.SelectedItem as string ?? "";
        string catKey = MakaluRemapData.RemapCategories.Keys.FirstOrDefault(k => MakaluRemapData.CatLabel(k) == label) ?? "Mouse";
        _mkRemapCatKey = catKey;
        var fns = MakaluRemapData.RemapCategories[catKey];
        CbMkRemapFunction.ItemsSource = fns.Select(MakaluRemapData.FnLabel).ToArray();
        CbMkRemapFunction.SelectedIndex = 0;
        PnlMkSniper.Visibility = catKey == "Sniper" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SldMkSniperDpi_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mkSuppress) return; // see _mkSuppress doc comment — SldMkSniperDpi's Minimum="50" hits the same load-order issue as SldMkDpi
        int dpi = QuantizeDpi((int)e.NewValue);
        TxtMkSniperDpi.Text = dpi.ToString();
    }

    private void TxtMkSniperDpi_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MkCommitSniperEntry();
    }

    private void TxtMkSniperDpi_LostFocus(object sender, RoutedEventArgs e) => MkCommitSniperEntry();

    private void MkCommitSniperEntry()
    {
        if (!int.TryParse(TxtMkSniperDpi.Text, out int dpi)) dpi = (int)SldMkSniperDpi.Value;
        dpi = Math.Clamp(QuantizeDpi(dpi), _mkInfo.DpiMin, MakaluProtocol.DpiMax);
        TxtMkSniperDpi.Text = dpi.ToString();
        SldMkSniperDpi.Value = dpi;
    }

    private void BtnMkRemapApply_Click(object sender, RoutedEventArgs e)
    {
        int btnIdx = _mkRemapActiveButton;
        string fnLabel = CbMkRemapFunction.SelectedItem as string ?? "";
        var fns = MakaluRemapData.RemapCategories[_mkRemapCatKey];
        int fi = fns.Select(MakaluRemapData.FnLabel).ToList().IndexOf(fnLabel);
        if (fi < 0) return;
        string fnKey = fns[fi];
        string oldRaw = _mkRemapAssignments.GetValueOrDefault(btnIdx, "left");

        LblMkRemapStatus.Text = "...";
        bool ok;
        string newAssignment;
        if (fnKey == "sniper")
        {
            MkCommitSniperEntry();
            int dpi = (int)SldMkSniperDpi.Value;
            newAssignment = $"sniper:{dpi}";
            ok = _makalu.SetButtonSniper(btnIdx, dpi, _mkInfo.DpiMin);
        }
        else
        {
            newAssignment = fnKey;
            ok = _makalu.SetButtonRemap(btnIdx, fnKey);
        }
        _log($"[REMAP] button={btnIdx} fn={newAssignment} -> {ok}");

        if (!ok)
        {
            LblMkRemapStatus.Text = Loc.Get("makalu_failed");
            LblMkRemapStatus.Foreground = (Brush)FindResource("K2DangerBrush");
            return;
        }

        _mkRemapAssignments[btnIdx] = newAssignment;
        _mkRemapButtons[btnIdx].Content = MakaluRemapData.RemapBtnText(Loc.Get(MakaluRemapData.BtnNames(_mkInfo.Model)[btnIdx]), newAssignment);

        // Safety: remapping the LEFT button risks locking the user out of clicking —
        // show a countdown confirm/auto-revert, same as controller.py's UI reference.
        if (btnIdx == 1 && fnKey != "left")
            MkShowRemapConfirm(btnIdx, oldRaw);
        else
        {
            LblMkRemapStatus.Text = Loc.Get("makalu_remap_applied");
            LblMkRemapStatus.Foreground = (Brush)FindResource("K2AccentBrush");
        }
    }

    // ------------------------------------------------------------
    // Left-button remap safety confirm/auto-revert overlay
    // ------------------------------------------------------------

    private DispatcherTimer? _mkConfirmTimer;
    private int _mkConfirmSeconds;
    private int _mkConfirmButton;
    private string _mkConfirmOldFn = "left";

    private void MkShowRemapConfirm(int btnIdx, string oldRaw)
    {
        _mkConfirmButton = btnIdx;
        _mkConfirmOldFn = oldRaw;
        _mkConfirmSeconds = 10;
        LblMkRemapConfirmText.Text = Loc.Get("makalu_remap_keep_text", _mkConfirmSeconds);
        PnlMkRemapConfirm.Visibility = Visibility.Visible;

        _mkConfirmTimer?.Stop();
        _mkConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mkConfirmTimer.Tick += (_, _) =>
        {
            _mkConfirmSeconds--;
            if (_mkConfirmSeconds <= 0) { MkRemapRevert(); return; }
            LblMkRemapConfirmText.Text = Loc.Get("makalu_remap_keep_text", _mkConfirmSeconds);
        };
        _mkConfirmTimer.Start();
    }

    private void BtnMkRemapKeep_Click(object sender, RoutedEventArgs e)
    {
        _mkConfirmTimer?.Stop();
        PnlMkRemapConfirm.Visibility = Visibility.Collapsed;
        LblMkRemapStatus.Text = Loc.Get("makalu_remap_applied");
        LblMkRemapStatus.Foreground = (Brush)FindResource("K2AccentBrush");
    }

    private void BtnMkRemapRevert_Click(object sender, RoutedEventArgs e) => MkRemapRevert();

    private void MkRemapRevert()
    {
        _mkConfirmTimer?.Stop();
        PnlMkRemapConfirm.Visibility = Visibility.Collapsed;
        int btnIdx = _mkConfirmButton;
        string oldFn = _mkConfirmOldFn;

        bool ok = oldFn.StartsWith("sniper:") && int.TryParse(oldFn.Split(':')[1], out int dpi)
            ? _makalu.SetButtonSniper(btnIdx, dpi, _mkInfo.DpiMin)
            : _makalu.SetButtonRemap(btnIdx, oldFn);
        _log($"[REMAP] revert button={btnIdx} -> {oldFn} ok={ok}");

        if (ok)
        {
            _mkRemapAssignments[btnIdx] = oldFn;
            _mkRemapButtons[btnIdx].Content = MakaluRemapData.RemapBtnText(Loc.Get(MakaluRemapData.BtnNames(_mkInfo.Model)[btnIdx]), oldFn);
            MkRemapSyncDropdowns(btnIdx);
        }
        LblMkRemapStatus.Text = ok ? Loc.Get("makalu_remap_reverted") : Loc.Get("makalu_failed");
        LblMkRemapStatus.Foreground = ok ? (Brush)FindResource("K2TextMutedBrush") : (Brush)FindResource("K2DangerBrush");
    }
}
