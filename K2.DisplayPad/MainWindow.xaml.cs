using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using K2.Core;
using K2.DisplayPad.Dialogs;
using K2.DisplayPad.Models;
using K2.DisplayPad.Services;

namespace K2.DisplayPad;

public partial class MainWindow : Window
{
    private readonly DisplayPadService _service = new();
    private readonly StateStore        _store   = new();
    private readonly ObservableCollection<DeviceRow> _devices  = new();
    private readonly ObservableCollection<int>       _deviceIds = new();
    private readonly ButtonCell[] _cells;

    /// <summary>Button controls indexed by PHYSICAL button index
    /// (0..11). Rotation rearranges them in the grid, it doesn't recreate them.</summary>
    private readonly Button[] _cellButtons;

    /// <summary>Rotation of the currently selected device.</summary>
    private DisplayRotation _rotation = DisplayRotation.None;
    private RotationOption[] _rotationOptions = Array.Empty<RotationOption>();
    private bool _suppressRotationUpdate;

    private readonly Dictionary<int, int> _matrixToIndex = new();
    private int _mapAwaitingIndex = -1;

    private static readonly (int Index, int Matrix)[] DefaultKeyMap = new[]
    {
        (0,  0x08), (1,  0x11), (2,  0x1A), (3,  0x23),
        (4,  0x2C), (5,  0x35), (6,  0x3E), (7,  0x47),
        (8,  0x50), (9,  0x59), (10, 0x62), (11, 0x7D),
    };

    private bool _suppressBrightnessUpdate;
    private bool _suppressProfileUpdate;

    public MainWindow()
    {
        InitializeComponent();

        _cells = Enumerable.Range(0, DisplayPadService.ButtonCount)
                           .Select(i => new ButtonCell(i))
                           .ToArray();
        _cellButtons = new Button[_cells.Length];
        foreach (var cell in _cells)
        {
            var btn = new Button
            {
                Style       = (Style)Resources["ButtonCellStyle"],
                DataContext = cell,
                Tag         = cell
            };
            btn.Click += BtnCell_Click;
            btn.ContextMenu = BuildCellContextMenu();
            _cellButtons[cell.Index] = btn;
        }
        LayoutGrid(_rotation);

        ApplyDefaultKeyMap();

        LvDevices.ItemsSource = _devices;
        CbDevice.ItemsSource  = _deviceIds;
        CbProfile.ItemsSource = Enumerable.Range(1, DisplayPadService.ProfileCount).ToArray();

        _rotationOptions = new[]
        {
            new RotationOption(Loc.Get("pos_horizontal", 0),   DisplayRotation.None),
            new RotationOption(Loc.Get("pos_vertical", 90),    DisplayRotation.Cw90),
            new RotationOption(Loc.Get("pos_horizontal", 180), DisplayRotation.Cw180),
            new RotationOption(Loc.Get("pos_vertical", 270),   DisplayRotation.Cw270),
        };
        _suppressRotationUpdate = true;
        CbRotation.ItemsSource   = _rotationOptions;
        CbRotation.SelectedIndex = 0;
        _suppressRotationUpdate  = false;

        _service.DevicePlug       += OnDevicePlug;
        _service.KeyEvent         += OnKeyEvent;
        _service.FirmwareProgress += OnFirmwareProgress;

        // Shared action engine (K2.Core) + Python bridge.
        // Initialized BEFORE the Dispose handler below so that, on
        // shutdown, scripts get terminated before the SDK is torn down.
        InitActionEngine();

        Closed += (_, _) =>
        {
            _service.Dispose();
            _store.Dispose();
        };
        Loaded += (_, _) => LblSdk.Text = $"SDK DLL v{_service.SdkVersion()}";
    }

    // ---- toolbar ----

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Log($"OpenUSBDriver(hwnd=0x{hwnd.ToInt64():X})");
            bool ok = _service.Open(hwnd);
            LblStatus.Text = ok ? "Driver aperto." : "Apertura driver fallita.";
            Log($"  -> {(ok ? "OK" : "FAIL")}");
            RefreshDevices();
        }
        catch (Exception ex)
        {
            Log("[ERR ] BtnOpen_Click: " + ex);
            LblStatus.Text = "Eccezione durante l'apertura — vedi log.";
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _service.Close();
        LblStatus.Text = "Driver chiuso.";
        Log("CloseUSBDriver()");
        _devices.Clear();
        _deviceIds.Clear();
    }

    private void CbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbDevice.SelectedItem is not int id) return;
        Log($"[UI] Active device: {id}");

        _suppressBrightnessUpdate = true;
        try
        {
            int b = SafeCall(() => _service.GetBrightness(id), -1);
            if (b >= 0) { SldBrightness.Value = b; LblBrightness.Text = $"{b}%"; }
        }
        finally { _suppressBrightnessUpdate = false; }

        _suppressProfileUpdate = true;
        try { CbProfile.SelectedItem = _store.GetCurrentProfile(id); }
        finally { _suppressProfileUpdate = false; }

        // Rotation: each device has its own, saved to the DB.
        _suppressRotationUpdate = true;
        try
        {
            _rotation = _store.GetRotation(id);
            CbRotation.SelectedItem =
                _rotationOptions.FirstOrDefault(o => o.Value == _rotation) ?? _rotationOptions[0];
        }
        finally { _suppressRotationUpdate = false; }
        LayoutGrid(_rotation);

        ReloadCurrentProfile();
    }

    private void ChkApEnable_Toggled(object sender, RoutedEventArgs e)
    {
        if (CbDevice.SelectedItem is not int id) return;
        bool enable = ChkApEnable.IsChecked == true;
        try { Log($"APEnable({id}, {enable}) -> {_service.APEnable(id, enable)}"); }
        catch (Exception ex) { Log($"[ERR ] APEnable: {ex.Message}"); }
    }

    private void BtnResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (CbDevice.SelectedItem is not int id) { Log("[WARN] No device selected."); return; }
        int profile = CurrentProfile();
        try
        {
            Log($"ResetAllPictures({id}) -> {_service.ResetAllPictures(id)}");
            _store.ClearProfile(id, profile);
            foreach (var c in _cells) { c.ImagePath = null; c.ActionType = null; c.ActionValue = null; }
        }
        catch (Exception ex) { Log($"[ERR ] Reset: {ex.Message}"); }
    }

    private void SldBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressBrightnessUpdate) return;
        if (CbDevice.SelectedItem is not int id) return;
        int level = (int)Math.Round(e.NewValue / 25.0) * 25;
        LblBrightness.Text = $"{level}%";
        try { Log($"SetBrightness({id}, {level}) -> {_service.SetBrightness(id, level)}"); }
        catch (Exception ex) { Log($"[ERR ] SetBrightness: {ex.Message}"); }
    }

    private void CbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileUpdate) return;
        if (CbDevice.SelectedItem is not int id) return;
        if (CbProfile.SelectedItem is not int profile) return;
        try
        {
            Log($"SwitchProfile({id}, {profile}) -> {_service.SwitchProfile(id, profile)}");
            _store.SetCurrentProfile(id, profile);
            ReloadCurrentProfile();
        }
        catch (Exception ex) { Log($"[ERR ] SwitchProfile: {ex.Message}"); }
    }

    // ---- rotation ----

    private void CbRotation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRotationUpdate) return;
        if (CbRotation.SelectedItem is not RotationOption opt) return;

        _rotation = opt.Value;
        LayoutGrid(_rotation);

        if (CbDevice.SelectedItem is not int id) return;
        _store.SetRotation(id, _rotation);
        Log($"[ROT ] device {id} -> {DisplayPadLayout.Label(_rotation)}");
        // Already-uploaded icons need to be re-uploaded with the new rotation.
        ReloadCurrentProfile();
    }

    /// <summary>Rearranges the button controls in <c>GridButtons</c> according
    /// to the rotation: 2x6 native, 6x2 at 90/270. The model (ButtonCell.Index)
    /// stays in physical indices, only the on-screen positions change.</summary>
    private void LayoutGrid(DisplayRotation rotation)
    {
        var (rows, cols)  = DisplayPadLayout.VisualGrid(rotation);
        var physForVisual = DisplayPadLayout.PhysicalForVisual(rotation);

        GridButtons.Children.Clear();
        GridButtons.Rows    = rows;
        GridButtons.Columns = cols;
        foreach (int phys in physForVisual)
            GridButtons.Children.Add(_cellButtons[phys]);
    }

    // ---- button grid ----

    private void BtnCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) { Log("[WARN] Select a device first."); return; }

        var dlg = new CellConfigDialog(cell.Index, cell.ImagePath, cell.ActionType, cell.ActionValue) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        cell.ActionType  = dlg.ActionType;
        cell.ActionValue = dlg.ActionValue;

        if (dlg.ImageChanged)
        {
            if (!string.IsNullOrEmpty(dlg.NewImagePath) && System.IO.File.Exists(dlg.NewImagePath))
            {
                UploadAndPersist(id, CurrentProfile(), cell, dlg.NewImagePath);
            }
            else if (dlg.NewImagePath is null)
            {
                cell.ImagePath = null;
                _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, null, cell.ActionType, cell.ActionValue));
                Log($"[IMG ] cell #{cell.Index} image removed");
            }
        }
        else
        {
            _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, cell.ImagePath, cell.ActionType, cell.ActionValue));
            Log($"[ACT ] cell #{cell.Index} <- type={cell.ActionType ?? "none"}");
        }
    }

    private void UploadAndPersist(int id, int profile, ButtonCell cell, string path)
    {
        var rotation = _rotation;
        try
        {
            // Upload uses the pre-rotated image; the store keeps the
            // ORIGINAL path, so the preview is upright and can be re-rotated
            // if the device's rotation changes in the future.
            string up = IconRotator.ResolveForUpload(path, rotation);
            string rotNote = up != path ? $" [rot {DisplayPadLayout.Label(rotation)}]" : "";
            Log($"UploadImageToProfile(id={id}, slot={profile}, btn={cell.Index}, path=\"{path}\"{rotNote})");
            bool ok = _service.UploadImageToProfile(id, up, cell.Index, profile);
            if (!ok)
            {
                Log("  -> FAIL (persistent), trying live upload");
                ok = _service.UploadImage(id, up, cell.Index);
                Log($"  -> live = {(ok ? "OK" : "FAIL")}");
                if (!ok) { LblStatus.Text = "Upload fallito (FW storage pieno?)."; return; }
            }
            else { Log("  -> OK"); }
            cell.ImagePath = path;
            _store.SaveButton(new ButtonRecord(id, profile, cell.Index, path, cell.ActionType, cell.ActionValue));
        }
        catch (Exception ex) { Log($"[ERR ] Upload: {ex}"); }
    }

    // ---- context menu (in code-behind to avoid connection-id collisions) ----

    private ContextMenu BuildCellContextMenu()
    {
        var menu = new ContextMenu();
        var miCfg = new MenuItem { Header = "Configura azione…" }; miCfg.Click += MnuConfigureAction_Click;
        var miRa  = new MenuItem { Header = "Rimuovi azione"   }; miRa.Click  += MnuRemoveAction_Click;
        var miRi  = new MenuItem { Header = "Rimuovi immagine" }; miRi.Click  += MnuRemoveImage_Click;
        menu.Items.Add(miCfg); menu.Items.Add(miRa);
        menu.Items.Add(new Separator()); menu.Items.Add(miRi);
        return menu;
    }

    private static ButtonCell? CellFromContextMenu(object sender) =>
        sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is ButtonCell cell ? cell : null;

    private void MnuConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (CellFromContextMenu(sender) is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) return;
        var dlg = new ButtonActionDialog(cell.Index, cell.ActionType, cell.ActionValue, this) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            cell.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none" ? null : dlg.ActionType;
            cell.ActionValue = cell.ActionType is null ? null : dlg.ActionValue;
            _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, cell.ImagePath, cell.ActionType, cell.ActionValue));
            Log($"[ACT ] cell #{cell.Index} <- type={cell.ActionType ?? "none"} value=\"{cell.ActionValue}\"");
        }
    }

    private void MnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (CellFromContextMenu(sender) is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) return;
        cell.ActionType = null; cell.ActionValue = null;
        _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, cell.ImagePath, null, null));
        Log($"[ACT ] cell #{cell.Index} action removed");
    }

    private void MnuRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (CellFromContextMenu(sender) is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) return;
        cell.ImagePath = null;
        _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, null, cell.ActionType, cell.ActionValue));
        Log($"[IMG ] cell #{cell.Index} image removed");
    }

    // ---- refresh / persistence ----

    private void RefreshDevices()
    {
        var previousSelection = CbDevice.SelectedItem as int?;
        _devices.Clear(); _deviceIds.Clear();

        var ids = _service.DeviceIds();
        var snap = _service.ListDeviceIdSnapshot();
        Log($"Devices IDs (real) -> [{string.Join(", ", ids)}]");
        Log($"Devices IDs (lstDeviceID snapshot) -> [{string.Join(", ", snap)}]");

        foreach (var id in ids)
        {
            bool   plugged    = SafeCall(() => _service.IsPlugged(id), false);
            string fw         = SafeCall(() => _service.FirmwareVersion(id), "");
            int    brightness = SafeCall(() => _service.GetBrightness(id), -1);
            _devices.Add(new DeviceRow
            {
                Id              = id,
                Plugged         = plugged ? "yes" : "no",
                FirmwareVersion = string.IsNullOrEmpty(fw) ? "—" : fw,
                Brightness      = brightness < 0 ? "—" : $"{brightness}%"
            });
            _deviceIds.Add(id);
        }
        if (_deviceIds.Count > 0)
            CbDevice.SelectedItem = previousSelection is int prev && _deviceIds.Contains(prev) ? prev : _deviceIds[0];
    }

    private int CurrentProfile() => CbProfile.SelectedItem is int p && p >= 1 ? p : 1;

    private void ReloadCurrentProfile()
    {
        if (CbDevice.SelectedItem is not int id) return;
        int profile = CurrentProfile();
        foreach (var c in _cells) { c.ImagePath = null; c.ActionType = null; c.ActionValue = null; }
        var rows = _store.LoadProfile(id, profile);
        Log($"[DB  ] loaded {rows.Count} records for device={id} profile={profile}");
        foreach (var r in rows)
        {
            if (r.ButtonIndex < 0 || r.ButtonIndex >= _cells.Length) continue;
            var cell = _cells[r.ButtonIndex];
            cell.ActionType = r.ActionType; cell.ActionValue = r.ActionValue;
            if (!string.IsNullOrEmpty(r.ImagePath) && System.IO.File.Exists(r.ImagePath))
            {
                cell.ImagePath = r.ImagePath;
                try
                {
                    // Re-upload to the FW profile to make sure icons stay
                    // persistent even after a device restart, rotated
                    // according to the device's current mounting.
                    string up = IconRotator.ResolveForUpload(r.ImagePath, _rotation);
                    bool ok = _service.UploadImageToProfile(id, up, r.ButtonIndex, profile);
                    if (!ok)
                    {
                        Log($"[DB  ] persistent upload cell #{r.ButtonIndex} FAIL, falling back to live");
                        _service.UploadImage(id, up, r.ButtonIndex);
                    }
                }
                catch (Exception ex) { Log($"[ERR ] reload upload cell #{r.ButtonIndex}: {ex.Message}"); }
            }
        }
    }

    // ---- device events ----

    private void OnDevicePlug(object? sender, DevicePlugEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            Log($"[PLUG] arg0={e.Arg0} arg1={e.Arg1} status={e.Status}");
            RefreshDevices();
        });
    }

    private void OnKeyEvent(object? sender, DisplayPadKeyEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Pressed && _mapAwaitingIndex >= 0 && _mapAwaitingIndex < _cells.Length)
            {
                int idx = _mapAwaitingIndex;
                _matrixToIndex[e.KeyMatrix] = idx;
                _cells[idx].KeyMatrix = e.KeyMatrix;
                Log($"[MAP ] cell #{idx} <- matrix 0x{e.KeyMatrix:X2}");
                _mapAwaitingIndex++;
                if (_mapAwaitingIndex >= _cells.Length)
                {
                    _mapAwaitingIndex = -1;
                    LblStatus.Text = "Mappatura tasti completata.";
                    LblMapKeysText.Text = "Rimappa tasti";
                }
                else LblStatus.Text = $"Mappatura: premi il tasto fisico per la cella #{_mapAwaitingIndex}…";
                return;
            }

            string label = _matrixToIndex.TryGetValue(e.KeyMatrix, out int cellIdx)
                ? $"cell #{cellIdx}" : "unmapped";
            Log($"[KEY ] id={e.DeviceId} matrix=0x{e.KeyMatrix:X2} {(e.Pressed ? "DOWN" : "UP")}  -> {label}");

            if (_matrixToIndex.TryGetValue(e.KeyMatrix, out int hi) && hi < _cells.Length)
            {
                _cells[hi].IsHighlighted = e.Pressed;
                if (e.Pressed) TryExecuteAction(_cells[hi]);
            }
        });
    }

    private void OnFirmwareProgress(object? sender, FirmwareProgressEventArgs e) =>
        Dispatcher.Invoke(() => Log($"[FW  ] {(e.Failed ? "FAILED" : $"{e.Percent}%")}"));

    // ---- utility ----

    private void Log(string message)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
    }

    private static T SafeCall<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    private sealed class DeviceRow
    {
        public int    Id              { get; set; }
        public string Plugged         { get; set; } = "";
        public string FirmwareVersion { get; set; } = "";
        public string Brightness      { get; set; } = "";
    }

    /// <summary>Entry of the rotation ComboBox. <see cref="ToString"/> is
    /// what the ComboBox displays on screen.</summary>
    private sealed record RotationOption(string Label, DisplayRotation Value)
    {
        public override string ToString() => Label;
    }
}
