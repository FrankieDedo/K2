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

    /// <summary>Controlli Button indicizzati per indice FISICO del tasto
    /// (0..11). La rotazione li ridispone nella griglia, non li ricrea.</summary>
    private readonly Button[] _cellButtons;

    /// <summary>Rotazione del device attualmente selezionato.</summary>
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
            new RotationOption("0°",   DisplayRotation.None),
            new RotationOption("90°",  DisplayRotation.Cw90),
            new RotationOption("270°", DisplayRotation.Cw270),
        };
        _suppressRotationUpdate = true;
        CbRotation.ItemsSource   = _rotationOptions;
        CbRotation.SelectedIndex = 0;
        _suppressRotationUpdate  = false;

        _service.DevicePlug       += OnDevicePlug;
        _service.KeyEvent         += OnKeyEvent;
        _service.FirmwareProgress += OnFirmwareProgress;

        // Motore azioni condiviso (K2.Core) + ponte Python.
        // Inizializzato PRIMA dell'handler di Dispose qui sotto cosi' che, alla
        // chiusura, gli script vengano terminati prima di smontare l'SDK.
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
        Log($"[UI] Device attivo: {id}");

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

        // Rotazione: ogni device ha la sua, salvata su DB.
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
        if (CbDevice.SelectedItem is not int id) { Log("[WARN] Nessun device selezionato."); return; }
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

    // ---- rotazione ----

    private void CbRotation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRotationUpdate) return;
        if (CbRotation.SelectedItem is not RotationOption opt) return;

        _rotation = opt.Value;
        LayoutGrid(_rotation);

        if (CbDevice.SelectedItem is not int id) return;
        _store.SetRotation(id, _rotation);
        Log($"[ROT ] device {id} -> {DisplayPadLayout.Label(_rotation)}");
        // Le icone gia' caricate vanno ri-uploadate con la nuova rotazione.
        ReloadCurrentProfile();
    }

    /// <summary>Ridispone i controlli tasto nella <c>GridButtons</c> secondo
    /// la rotazione: 2x6 nativo, 6x2 a 90/270. Il modello (ButtonCell.Index)
    /// resta in indici fisici, cambiano solo le posizioni a schermo.</summary>
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

    // ---- griglia tasti ----

    private void BtnCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) { Log("[WARN] Seleziona prima un device."); return; }

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
                Log($"[IMG ] cella #{cell.Index} immagine rimossa");
            }
        }
        else
        {
            _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, cell.ImagePath, cell.ActionType, cell.ActionValue));
            Log($"[ACT ] cella #{cell.Index} <- type={cell.ActionType ?? "none"}");
        }
    }

    private void UploadAndPersist(int id, int profile, ButtonCell cell, string path)
    {
        try
        {
            // L'upload usa l'immagine pre-ruotata; nello store resta il path
            // ORIGINALE, cosi' l'anteprima e' dritta e si puo' ri-ruotare
            // se in futuro la rotazione del device cambia.
            string up = IconRotator.ResolveForUpload(path, _rotation);
            string rotNote = up != path ? $" [rot {DisplayPadLayout.Label(_rotation)}]" : "";
            Log($"UploadImageToProfile(id={id}, slot={profile}, btn={cell.Index}, path=\"{path}\"{rotNote})");
            bool ok = _service.UploadImageToProfile(id, up, cell.Index, profile);
            if (!ok)
            {
                Log("  -> FAIL (persistent), provo upload live");
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

    // ---- context menu (in code-behind per evitare collisioni connection-id) ----

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
        var dlg = new ButtonActionDialog(cell.Index, cell.ActionType, cell.ActionValue) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            cell.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none" ? null : dlg.ActionType;
            cell.ActionValue = cell.ActionType is null ? null : dlg.ActionValue;
            _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, cell.ImagePath, cell.ActionType, cell.ActionValue));
            Log($"[ACT ] cella #{cell.Index} <- type={cell.ActionType ?? "none"} value=\"{cell.ActionValue}\"");
        }
    }

    private void MnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (CellFromContextMenu(sender) is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) return;
        cell.ActionType = null; cell.ActionValue = null;
        _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, cell.ImagePath, null, null));
        Log($"[ACT ] cella #{cell.Index} azione rimossa");
    }

    private void MnuRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (CellFromContextMenu(sender) is not ButtonCell cell) return;
        if (CbDevice.SelectedItem is not int id) return;
        cell.ImagePath = null;
        _store.SaveButton(new ButtonRecord(id, CurrentProfile(), cell.Index, null, cell.ActionType, cell.ActionValue));
        Log($"[IMG ] cella #{cell.Index} immagine rimossa");
    }

    // ---- refresh / persistenza ----

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
        Log($"[DB  ] caricati {rows.Count} record per device={id} profilo={profile}");
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
                    // Re-uploadiamo nel profilo FW per assicurarci che le icone
                    // siano persistenti anche dopo riavvio device, ruotate
                    // secondo il montaggio corrente del device.
                    string up = IconRotator.ResolveForUpload(r.ImagePath, _rotation);
                    bool ok = _service.UploadImageToProfile(id, up, r.ButtonIndex, profile);
                    if (!ok)
                    {
                        Log($"[DB  ] upload persistente cella #{r.ButtonIndex} FAIL, fallback live");
                        _service.UploadImage(id, up, r.ButtonIndex);
                    }
                }
                catch (Exception ex) { Log($"[ERR ] reload upload cella #{r.ButtonIndex}: {ex.Message}"); }
            }
        }
    }

    // ---- eventi device ----

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
                Log($"[MAP ] cella #{idx} <- matrix 0x{e.KeyMatrix:X2}");
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
                ? $"cella #{cellIdx}" : "non mappato";
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

    /// <summary>Voce della ComboBox rotazione. <see cref="ToString"/> e'
    /// quello che la ComboBox mostra a video.</summary>
    private sealed record RotationOption(string Label, DisplayRotation Value)
    {
        public override string ToString() => Label;
    }
}
