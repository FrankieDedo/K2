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
/// "Key Binding" (button remap + sniper) section content for the Makalu tab —
/// see MakaluDpiRemapPanel.xaml for why this is kept as its own small
/// UserControl, and for why DPI no longer lives here despite the class name.
/// Owns no MakaluService of its own; the parent (MainWindow) passes one in
/// via <see cref="Init"/> and keeps it in sync via
/// <see cref="UpdateDeviceInfo"/> whenever the detected model/connection
/// state changes.
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

    /// <summary>Profile persistence — set once from Init, same pattern as
    /// MakaluRgbSettingsPanel._mkStore/_mkSlot.</summary>
    private MakaluStore? _mkStore;
    private Func<int>? _mkSlot;
    private int CurrentSlot => _mkSlot?.Invoke() ?? 1;

    public MakaluDpiRemapPanel()
    {
        InitializeComponent();
    }

    internal void Init(MakaluService service, Action<string> log, MakaluStore store, Func<int> currentSlot)
    {
        _makalu = service;
        _log = log;
        _mkStore = store;
        _mkSlot = currentSlot;
        BuildMkRemapButtons();
        _mkSuppress = false;
    }

    /// <summary>Called by the parent whenever the detected model/connection
    /// state changes — rebuilds the remap button set for the new model.</summary>
    internal void UpdateDeviceInfo(MakaluService.DeviceInfo info)
    {
        _mkInfo = info;
        BuildMkRemapButtons();
    }

    /// <summary>Selects the given physical button as the active one for the
    /// Remap section — called from MakaluTabPanel when a hotspot on the
    /// device image is clicked.</summary>
    internal void SelectRemapButton(int btnIdx) => MkRemapSelectButton(btnIdx);

    // ------------------------------------------------------------
    // Button remap + sniper
    // ------------------------------------------------------------

    private readonly Dictionary<int, Button> _mkRemapButtons = new();
    private Dictionary<int, string> _mkRemapAssignments = new();
    private int _mkRemapActiveButton = 1;
    private string _mkRemapCatKey = "Mouse";

    /// <summary>Merges the current profile's saved remap rows (if any) over
    /// this model's defaults — a button with no saved row yet (never applied
    /// in this profile) falls back to the model default, same as a brand new
    /// installation before any Apply has ever been pressed.</summary>
    private Dictionary<int, string> MkLoadAssignments()
    {
        var result = new Dictionary<int, string>(MakaluRemapData.RemapDefaults(_mkInfo.Model));
        if (_mkStore is not null)
            foreach (var kv in _mkStore.LoadRemap(CurrentSlot))
                result[kv.Key] = kv.Value;
        return result;
    }

    private void BuildMkRemapButtons()
    {
        PnlMkRemapButtons.Children.Clear();
        _mkRemapButtons.Clear();
        var names = MakaluRemapData.BtnNames(_mkInfo.Model);
        _mkRemapAssignments = MkLoadAssignments();

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
        int dpi = MakaluProtocol.QuantizeDpiTiered((int)e.NewValue);
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
        dpi = Math.Clamp(MakaluProtocol.QuantizeDpiTiered(dpi), _mkInfo.DpiMin, MakaluProtocol.DpiMax);
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
        _mkStore?.SaveRemapButton(CurrentSlot, btnIdx, newAssignment);

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
            _mkStore?.SaveRemapButton(CurrentSlot, btnIdx, oldFn);
            MkRemapSyncDropdowns(btnIdx);
        }
        LblMkRemapStatus.Text = ok ? Loc.Get("makalu_remap_reverted") : Loc.Get("makalu_failed");
        LblMkRemapStatus.Foreground = ok ? (Brush)FindResource("K2TextMutedBrush") : (Brush)FindResource("K2DangerBrush");
    }

    // ------------------------------------------------------------
    // Profile switch: push the stored slot's button assignments into this
    // panel and re-send every one of them to firmware (if connected). Called
    // by MainWindow.Makalu.cs alongside MakaluRgbSettingsPanel.MkReloadProfile.
    // ------------------------------------------------------------

    internal void MkReloadRemap(int slot)
    {
        _mkRemapAssignments = MkLoadAssignments();
        foreach (var kv in _mkRemapAssignments)
            if (_mkRemapButtons.TryGetValue(kv.Key, out var btn))
                btn.Content = MakaluRemapData.RemapBtnText(Loc.Get(MakaluRemapData.BtnNames(_mkInfo.Model)[kv.Key]), kv.Value);
        MkRemapSyncDropdowns(_mkRemapActiveButton);
        MkUpdateRemapButtonHighlight();

        // Not connected: UI reflects the profile, hardware catches up on reconnect
        // (MainWindow.Makalu.cs calls this again on the disconnected->connected
        // poll transition).
        bool anyConnected = false;
        foreach (var kv in _mkRemapAssignments)
        {
            bool ok = kv.Value.StartsWith("sniper:") && int.TryParse(kv.Value.Split(':')[1], out int dpi)
                ? _makalu.SetButtonSniper(kv.Key, dpi, _mkInfo.DpiMin)
                : _makalu.SetButtonRemap(kv.Key, kv.Value);
            anyConnected |= ok;
        }
        _log($"[PROFILE] reload remap slot={slot}: {_mkRemapAssignments.Count} button(s), hw ok={anyConnected}");
    }
}
