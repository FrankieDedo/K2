using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// MainWindow partial: MacroPad 12-key grid, action configuration,
/// hardware-matrix remapping and persistence.
/// </summary>
public partial class MainWindow
{
    private readonly MacroPadStore _store = new();

    private MacroPadKey[] _keys = Array.Empty<MacroPadKey>();
    private Button[] _keyButtons = Array.Empty<Button>();

    /// <summary>Maps <c>hardware matrix → key index</c> for the active device.</summary>
    private readonly Dictionary<int, int> _matrixToIndex = new();

    /// <summary>Index of the cell awaiting a press during remapping (-1 = inactive).</summary>
    private int _mapAwaitingIndex = -1;

    private bool _suppressProfileUpdate;

    /// <summary>Orientation in which the MacroPad is mounted (2×6 rotatable grid).</summary>
    private MacroPadRotation _rotation = MacroPadRotation.None;

    private bool _suppressRotationUpdate;

    // ============================================================
    // Grid construction
    // ============================================================

    // Key layout constants in the Canvas (mkd_bg.png coordinates at 510×370)
    private const double KeySize   = 55;
    private const double KeyGapH   = 8;
    private const double KeyGapV   = 10;
    // Key area centred within the device "screen"
    private const double KeyStartX = 70;
    private const double KeyStartY = 140;

    /// <summary>Builds the key grid and profile selector. Called from constructor.</summary>
    private void InitKeysModule()
    {
        _keys = Enumerable.Range(0, MacroPadService.ButtonCount)
                          .Select(i => new MacroPadKey(i))
                          .ToArray();
        _keyButtons = new Button[_keys.Length];

        // Buttons are created once, indexed by PHYSICAL index.
        // Rotation only changes the ORDER and POSITION in which they are
        // placed in the Canvas (see RebuildKeyGrid), not the model/actions.
        foreach (var key in _keys)
        {
            var label = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                Foreground    = Brushes.White,
                FontSize      = 8,
                FontFamily    = new FontFamily("Segoe UI,system-ui,Arial,sans-serif"),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(MacroPadKey.Display)));

            var btn = new Button
            {
                Width       = KeySize,
                Height      = KeySize,
                DataContext = key,
                Tag         = key,
                Content     = label,
                ContextMenu = BuildKeyContextMenu(),
                Style       = (Style)FindResource("MacroKeyStyle"),
            };
            btn.Click += KeyButton_Click;
            _keyButtons[key.Index] = btn;
        }

        // Rotation selector (populated here, bound in XAML).
        _suppressRotationUpdate = true;
        CbMacroRotation.ItemsSource = new[]
        {
            new RotationChoice(MacroPadRotation.None,  "Horizontal (0°)"),
            new RotationChoice(MacroPadRotation.Cw90,  "Vertical 90°"),
            new RotationChoice(MacroPadRotation.Cw270, "Vertical 270°"),
        };
        CbMacroRotation.DisplayMemberPath = nameof(RotationChoice.Label);
        CbMacroRotation.SelectedIndex = 0;
        _suppressRotationUpdate = false;

        RebuildKeyGrid();

        // CbProfile is populated by MpRefreshProfiles on device change

        LoadRotationFromStore();

        // Restore custom tab name
        var savedName = _store.GetSetting("device.name");
        if (!string.IsNullOrEmpty(savedName))
            TabMacroPad.Header = savedName;
    }

    private void BtnMpRename_Click(object sender, RoutedEventArgs e)
    {
        string current = TabMacroPad.Header as string ?? Loc.Get("tab_macropad");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabMacroPad.Header = name;
        _store.SetSetting("device.name", name);
    }

    private void BtnMpRenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) return;
        int slot = CurrentProfile();
        string current = _store.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot);
        string? name = ShowRenameDialog(current,
            Loc.Get("rename_profile_title"),
            Loc.Get("rename_profile_prompt"));
        if (name is null) return;
        _store.SetProfileName(id, slot, name);
        MpRefreshProfiles(id);
        MpSelectProfileSlot(slot);
        Log($"[UI ] MacroPad profile {slot} renamed to \"{name}\"");
    }

    private void BtnMpDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) return;
        int slot = CurrentProfile();
        // Cannot delete the last real profile
        var existing = _store.GetExistingProfiles(id);
        if (existing.Count <= 1)
        {
            MessageBox.Show(Loc.Get("delete_profile_last"),
                Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string profileName = _store.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("delete_profile_confirm", profileName),
            Loc.Get("delete_profile"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _store.ClearProfile(id, slot);
        _store.SetSetting($"profile.{id}.{slot}.name", "");
        Log($"[UI ] MacroPad profile {slot} deleted.");
        MpRefreshProfiles(id);
        // CbProfile_SelectionChanged will reload the key grid automatically
    }

    /// <summary>Rotation combo item.</summary>
    private sealed record RotationChoice(MacroPadRotation Rotation, string Label);

    /// <summary>
    /// Places buttons in the <see cref="Canvas"/> <c>CvsMacroKeys</c> always
    /// in the physical 2×6 layout. Visual rotation (background + keys together)
    /// is handled by a <see cref="System.Windows.Media.RotateTransform"/> as the
    /// Canvas LayoutTransform; the parent <see cref="System.Windows.Controls.Viewbox"/>
    /// rescales.
    /// </summary>
    private void RebuildKeyGrid()
    {
        CvsMacroKeys.Children.Clear();

        // Always physical layout 2×6
        const int rows = 2, cols = 6;
        double totalW = cols * KeySize + (cols - 1) * KeyGapH;
        double totalH = rows * KeySize + (rows - 1) * KeyGapV;

        // "Screen" area in mkd_bg.png
        double areaLeft   = 55;
        double areaRight  = 455;
        double areaTop    = 130;
        double areaBottom = 330;
        double areaW = areaRight - areaLeft;
        double areaH = areaBottom - areaTop;

        double startX = areaLeft + (areaW - totalW) / 2;
        double startY = areaTop  + (areaH - totalH) / 2;

        for (int i = 0; i < _keyButtons.Length; i++)
        {
            int r = i / cols;
            int c = i % cols;
            double x = startX + c * (KeySize + KeyGapH);
            double y = startY + r * (KeySize + KeyGapV);

            var btn = _keyButtons[i];
            btn.Width  = KeySize;
            btn.Height = KeySize;
            Canvas.SetLeft(btn, x);
            Canvas.SetTop(btn, y);
            CvsMacroKeys.Children.Add(btn);
        }

        // LayoutTransform rotates background + keys together
        CvsMacroKeys.LayoutTransform = _rotation == MacroPadRotation.None
            ? Transform.Identity
            : new RotateTransform((int)_rotation);

        // Counter-rotate each button so bordi e mount restano orientati correttamente.
        // Il TextBlock dentro il bottone ruota con esso → nessun transform aggiuntivo.
        var counterTransform = _rotation == MacroPadRotation.None
            ? Transform.Identity
            : new RotateTransform(-(int)_rotation);
        foreach (var btn in _keyButtons)
        {
            btn.LayoutTransform = counterTransform;
            if (btn.Content is TextBlock lbl)
                lbl.LayoutTransform = Transform.Identity; // il bottone gestisce già la rotazione
        }
    }

    /// <summary>Orientation changed from combo: rebuild grid and save.</summary>
    private void CbMacroRotation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRotationUpdate) return;
        if (CbMacroRotation.SelectedItem is not RotationChoice choice) return;
        _rotation = choice.Rotation;
        RebuildKeyGrid();
        _store.SetSetting("macropad.rotation", ((int)_rotation).ToString());
        Log($"[UI] MacroPad orientation: {MacroPadLayout.Label(_rotation)}");
    }

    /// <summary>Restores the saved orientation (called at startup).</summary>
    private void LoadRotationFromStore()
    {
        var saved = MacroPadLayout.Parse(_store.GetSetting("macropad.rotation"));
        if (saved == _rotation) return;
        _suppressRotationUpdate = true;
        try
        {
            _rotation = saved;
            if (CbMacroRotation.ItemsSource is IEnumerable<RotationChoice> items)
            {
                int i = 0;
                foreach (var c in items) { if (c.Rotation == saved) { CbMacroRotation.SelectedIndex = i; break; } i++; }
            }
            RebuildKeyGrid();
        }
        finally { _suppressRotationUpdate = false; }
    }

    private ContextMenu BuildKeyContextMenu()
    {
        var menu = new ContextMenu();
        var miCfg = new MenuItem { Header = Loc.Get("dp_configure_action") };
        miCfg.Click += MnuConfigureAction_Click;
        var miRem = new MenuItem { Header = Loc.Get("dp_remove_action") };
        miRem.Click += MnuRemoveAction_Click;
        menu.Items.Add(miCfg);
        menu.Items.Add(miRem);
        return menu;
    }

    private static MacroPadKey? KeyFromContextMenu(object sender) =>
        sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is MacroPadKey key ? key : null;

    // ============================================================
    // Action configuration
    // ============================================================

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MacroPadKey key })
            ConfigureAction(key);
    }

    private void MnuConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (KeyFromContextMenu(sender) is MacroPadKey key)
            ConfigureAction(key);
    }

    private void ConfigureAction(MacroPadKey key)
    {
        if (CurrentDeviceId() is not int id) { Log("[WARN] Select a device first."); return; }

        var dlg = new ButtonActionDialog(key.Index, key.ActionType, key.ActionValue) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        key.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                          ? null : dlg.ActionType;
        key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;
        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), key.Index, key.ActionType, key.ActionValue));
        Log($"[ACT ] key #{key.Index} <- type={key.ActionType ?? "none"} value=\"{key.ActionValue}\"");
    }

    private void MnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (KeyFromContextMenu(sender) is not MacroPadKey key) return;
        if (CurrentDeviceId() is not int id) return;
        key.ActionType = null;
        key.ActionValue = null;
        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), key.Index, null, null));
        Log($"[ACT ] key #{key.Index} action removed");
    }

    // ============================================================
    // Device / profile selection
    // ============================================================

    // ── Device selection (driven by top-level TcDevices) ──────────
    private void CbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) return;
        Log($"[UI] Active MacroPad device: {id}");

        // Load the saved matrix→key map for this device.
        _matrixToIndex.Clear();
        foreach (var k in _keys) k.KeyMatrix = null;
        foreach (var (matrix, index) in _store.GetKeyMap(id))
        {
            _matrixToIndex[matrix] = index;
            if (index >= 0 && index < _keys.Length) _keys[index].KeyMatrix = matrix;
        }

        MpRefreshProfiles(id);
        ReloadCurrentProfile();
    }

    private void CbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileUpdate) return;
        if (CurrentDeviceId() is not int id) return;
        if (CbProfile.SelectedItem is not MpProfileItem pi) return;
        int profile = pi.Slot;

        if (pi.IsNew)
        {
            // Create empty profile
            _store.ClearProfile(id, profile);
            _store.SaveKey(new MacroKeyRecord(id, profile, 0, null, null));
            Log($"[UI] New empty MacroPad profile created: slot {profile}");
            MpRefreshProfiles(id);
            MpSelectProfileSlot(profile);
        }

        _store.SetCurrentProfile(id, profile);
        Log($"[UI] MacroPad profile in edit: {profile}");
        ReloadCurrentProfile();
    }

    /// <summary>Populates the MacroPad profile combo with existing profiles + "New profile…".</summary>
    private void MpRefreshProfiles(int deviceId)
    {
        _suppressProfileUpdate = true;
        try
        {
            var existing = _store.GetExistingProfiles(deviceId);
            if (existing.Count == 0) existing.Add(1);
            var items = new List<MpProfileItem>();
            foreach (var slot in existing)
            {
                string name = _store.GetProfileName(deviceId, slot) ?? Loc.Get("profile_n", slot);
                items.Add(new MpProfileItem(slot, name));
            }
            int nextFree = Enumerable.Range(1, MacroPadService.ProfileCount)
                .FirstOrDefault(s => !existing.Contains(s));
            if (nextFree > 0)
                items.Add(new MpProfileItem(nextFree, Loc.Get("new_profile")));

            CbProfile.DisplayMemberPath = nameof(MpProfileItem.Label);
            CbProfile.ItemsSource = items;

            int current = _store.GetCurrentProfile(deviceId);
            var match = items.Find(x => x.Slot == current && !x.IsNew);
            CbProfile.SelectedItem = match ?? items[0];
        }
        finally { _suppressProfileUpdate = false; }
    }

    /// <summary>Selects a slot in the MacroPad profile combo (suppressing the event).</summary>
    private void MpSelectProfileSlot(int slot)
    {
        _suppressProfileUpdate = true;
        try
        {
            if (CbProfile.ItemsSource is List<MpProfileItem> items)
                CbProfile.SelectedItem = items.Find(x => x.Slot == slot && !x.IsNew) ?? items[0];
        }
        finally { _suppressProfileUpdate = false; }
    }

    /// <summary>Reloads from DB the actions for the current (device, profile) into the grid.</summary>
    private void ReloadCurrentProfile()
    {
        foreach (var k in _keys) { k.ActionType = null; k.ActionValue = null; }
        if (CurrentDeviceId() is not int id) return;
        int profile = CurrentProfile();
        var rows = _store.LoadProfile(id, profile);
        foreach (var r in rows)
        {
            if (r.KeyIndex < 0 || r.KeyIndex >= _keys.Length) continue;
            _keys[r.KeyIndex].ActionType  = r.ActionType;
            _keys[r.KeyIndex].ActionValue = r.ActionValue;
        }
        Log($"[DB  ] loaded {rows.Count} actions for device={id} profile={profile}");
    }

    // ============================================================
    // Key remapping
    // ============================================================

    private void BtnMapKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_mapAwaitingIndex >= 0)                       // cancel
        {
            _mapAwaitingIndex = -1;
            BtnMapKeys.Content = Loc.Get("remap_keys");
            LblStatus.Text = Loc.Get("mapping_cancelled");
            return;
        }
        if (CurrentDeviceId() is null) { Log("[WARN] Select a device first."); return; }

        _matrixToIndex.Clear();
        foreach (var k in _keys) k.KeyMatrix = null;
        _mapAwaitingIndex = 0;
        BtnMapKeys.Content = Loc.Get("cancel_remap");
        LblStatus.Text = Loc.Get("dp_mapping_prompt", 0);
        Log("[MAP ] MacroPad key remapping started");
    }

    /// <summary>Handles an SDK key event (remapping, highlighting, execution).</summary>
    private void HandleKeyEvent(MacroPadKeyEventArgs e)
    {
        int matrix = e.KeyMatrix;

        // Remapping in progress: assign the matrix to the awaited cell.
        if (e.Pressed && _mapAwaitingIndex >= 0 && _mapAwaitingIndex < _keys.Length)
        {
            int idx = _mapAwaitingIndex;
            _matrixToIndex[matrix] = idx;
            _keys[idx].KeyMatrix = matrix;
            Log($"[MAP ] cell #{idx} <- matrix 0x{matrix:X2}");

            _mapAwaitingIndex++;
            if (_mapAwaitingIndex >= _keys.Length)
            {
                _mapAwaitingIndex = -1;
                BtnMapKeys.Content = Loc.Get("remap_keys");
                LblStatus.Text = "Key mapping complete.";
                if (CurrentDeviceId() is int devId)
                    _store.SetKeyMap(devId, _matrixToIndex);
            }
            else
            {
                LblStatus.Text = Loc.Get("dp_mapping_prompt", _mapAwaitingIndex);
            }
            return;
        }

        // Per-key-press log: noisy in normal use, so it only fires at LogLevel.Verbose
        // (see General Settings tab / AppSettings.LogLevel).
        if (AppSettings.LogLevel == K2LogLevel.Verbose)
            Log($"[KEY ] matrix 0x{matrix:X2} {(e.Pressed ? "down" : "up")}");

        if (_matrixToIndex.TryGetValue(matrix, out int hi) && hi >= 0 && hi < _keys.Length)
        {
            _keys[hi].IsHighlighted = e.Pressed;
            if (e.Pressed) TryExecuteAction(_keys[hi]);
        }
    }

    /// <summary>Executes the key's action by delegating to the shared engine (K2.Core).</summary>
    private void TryExecuteAction(MacroPadKey key)
        => _engine?.Execute(key.ActionType, key.ActionValue, key.Index);

    // ============================================================
    // Import XML (Base Camp-compatible or K2-only, same schema)
    // ============================================================

    private void BtnMpImportXml_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { LblStatus.Text = Loc.Get("select_device_first"); return; }

        var dlg = new OpenFileDialog
        {
            Title  = Loc.Get("dp_open_bc_profile"),
            Filter = Loc.Get("dp_filter_bc_xml"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var doc  = System.Xml.Linq.XDocument.Load(dlg.FileName);
            var root = doc.Root;
            if (root is null) return;

            int slot = 1;
            if (int.TryParse(root.Element("Id")?.Value, out var n) && n >= 1 && n <= 5)
                slot = n;
            string profileName = root.Element("ProfileName")?.Value
                                 ?? Path.GetFileNameWithoutExtension(dlg.FileName);

            var bindings = root.Descendants("MakaluKeyBindings").ToList();
            if (bindings.Count == 0)
            {
                Log("[IMP-XML] No MakaluKeyBindings found in XML.");
                return;
            }

            _store.ClearProfile(id, slot);
            int imported = 0;

            foreach (var b in bindings)
            {
                if (!int.TryParse(b.Element("KeyId")?.Value, out int keyId) || keyId < 1 || keyId > 12) continue;
                int keyIndex = keyId - 1;

                string? funcType    = b.Element("FunctionType")?.Value;
                string? funcValue   = b.Element("FunctionValue")?.Value;
                string? funcEntered = b.Element("FunctionEnteredValue")?.Value;

                string? actionType, actionValue;
                if (funcType == "K2Action")
                {
                    // Sentinel scritto da MpProfileExporter.ExportK2: FunctionEnteredValue/
                    // FunctionValue portano ActionType/ActionValue K2 letterali (lo schema
                    // MakaluKeyBindings non ha SubFunctionType, quindi si riusa
                    // FunctionEnteredValue per il round-trip senza perdite).
                    actionType  = funcEntered;
                    actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;
                }
                else
                {
                    (actionType, actionValue) = BaseCampDbImporter.TranslateMakaluAction(funcType, funcValue);
                }

                if (actionType is null) continue;
                _store.SaveKey(new MacroKeyRecord(id, slot, keyIndex, actionType, actionValue));
                imported++;
            }

            _store.SetCurrentProfile(id, slot);
            MpRefreshProfiles(id);
            MpSelectProfileSlot(slot);
            ReloadCurrentProfile();

            Log($"[IMP-XML] '{profileName}' -> device {id} slot {slot}: {imported} keys");
            LblStatus.Text = Loc.Get("dp_imported_xml", profileName, slot);
        }
        catch (Exception ex)
        {
            Log($"[ERR] import XML: {ex.Message}");
        }
    }

    // ============================================================
    // Export profile — Base Camp-compatible XML / K2-only XML
    // ============================================================

    private void BtnMpExportBc_Click(object sender, RoutedEventArgs e) => MpExportProfile(bcCompatible: true);
    private void BtnMpExportK2_Click(object sender, RoutedEventArgs e) => MpExportProfile(bcCompatible: false);

    private void MpExportProfile(bool bcCompatible)
    {
        if (CurrentDeviceId() is not int id) { LblStatus.Text = Loc.Get("dp_export_no_profile"); return; }
        if (CbProfile.SelectedItem is not MpProfileItem pi || pi.IsNew) { LblStatus.Text = Loc.Get("dp_export_no_profile"); return; }
        int slot = pi.Slot;
        string profileName = _store.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot);

        var dlg = new SaveFileDialog
        {
            Title    = bcCompatible ? Loc.Get("dp_save_bc_profile") : Loc.Get("dp_save_k2_profile"),
            Filter   = Loc.Get("dp_filter_bc_xml"),
            FileName = $"{profileName}.xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var result = bcCompatible
                ? MpProfileExporter.ExportBaseCamp(_store, id, slot, profileName, dlg.FileName)
                : MpProfileExporter.ExportK2(_store, id, slot, profileName, dlg.FileName);

            if (bcCompatible)
            {
                LblStatus.Text = Loc.Get("dp_exported_bc", profileName, result.Exported, result.SkippedActions);
                Log($"[EXP-BC] '{profileName}' -> {dlg.FileName}: {result.Exported} actions, {result.SkippedActions} skipped");
                foreach (var reason in result.SkipReasons) Log($"[EXP-BC] skip: {reason}");
            }
            else
            {
                LblStatus.Text = Loc.Get("dp_exported_k2", profileName, result.Exported);
                Log($"[EXP-K2] '{profileName}' -> {dlg.FileName}: {result.Exported} actions");
            }
        }
        catch (Exception ex)
        {
            Log($"[ERR] export XML: {ex.Message}");
        }
    }

    // ============================================================
    // Import from Base Camp DB
    // ============================================================

    private void BtnMpImportBc_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int k2DeviceId)
        {
            LblStatus.Text = Loc.Get("select_device_first");
            return;
        }

        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            Log("[IMP-BC] BaseCamp.db not found.");
            LblStatus.Text = Loc.Get("dp_bc_db_not_found");
            return;
        }
        Log($"[IMP-BC] DB: {dbPath}");

        Dictionary<int, List<BaseCampDbImporter.BcProfile>> bcDevices;
        try { bcDevices = BaseCampDbImporter.ReadMacroPadProfiles(dbPath); }
        catch (Exception ex) { Log($"[IMP-BC] Read error: {ex.Message}"); return; }

        if (bcDevices.Count == 0)
        {
            Log("[IMP-BC] No MacroPad profiles in DB.");
            LblStatus.Text = Loc.Get("mp_no_profiles_in_bc");
            return;
        }

        // Auto-mapping: BC DeviceId == K2 SDK DeviceId
        if (!bcDevices.TryGetValue(k2DeviceId, out var profiles) || profiles.Count == 0)
        {
            Log($"[IMP-BC] No BC profiles for device #{k2DeviceId}.");
            LblStatus.Text = Loc.Get("dp_no_profiles_in_bc");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Base Camp → K2 MacroPad import (device #{k2DeviceId}):\n");
        foreach (var p in profiles)
            sb.AppendLine($"  Slot {p.Slot}: {p.Name}{(p.IsSelected ? " [ACTIVE]" : "")}");
        sb.AppendLine($"\nImport {profiles.Count} profile(s)?");

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int totalKeys = 0;
        int activeSlot = -1;

        foreach (var profile in profiles)
        {
            try
            {
                int n = BaseCampDbImporter.ImportMacroPadProfile(dbPath, profile, k2DeviceId, _store);
                totalKeys += n;
                Log($"[IMP-BC] slot {profile.Slot} '{profile.Name}': {n} keys");
                if (profile.IsSelected) activeSlot = profile.Slot;
            }
            catch (Exception ex)
            {
                Log($"[IMP-BC] Error slot {profile.Slot}: {ex.Message}");
            }
        }

        // Switch to active BC profile and reload UI
        int slotToShow = activeSlot > 0 ? activeSlot : CurrentProfile();
        MpRefreshProfiles(k2DeviceId);
        MpSelectProfileSlot(slotToShow);
        ReloadCurrentProfile();

        Log($"[IMP-BC] Done: {totalKeys} keys across {profiles.Count} profiles");
        LblStatus.Text = Loc.Get("mp_imported_bc", profiles.Count, totalKeys);
    }

    // ============================================================
    // MacroPad debug mode
    // ============================================================

    // Driven centrally by the General Settings tab (MainWindow.Settings.cs) —
    // see AppSettings.DebugMode. No longer has its own per-device checkbox.
    private void ApplyMpDebugMode(bool debug)
    {
        var vis = debug ? Visibility.Visible : Visibility.Collapsed;
        BtnOpen.Visibility          = vis;
        BtnClose.Visibility         = vis;
        BtnApOn.Visibility          = vis;
        BtnApOff.Visibility         = vis;
        SepMpApDbg.Visibility       = vis;
        BtnMapKeys.Visibility       = vis;  // remap keys: debug-only, see project rule
        PnlMpDebugRight.Visibility  = vis;
    }

    /// <summary>SDK ID of the active MacroPad (set by TcDevices_SelectionChanged in xaml.cs).</summary>
    internal int? _activeMpDeviceId;
    private int? CurrentDeviceId() => _activeMpDeviceId;

    /// <summary>Currently selected profile for editing (1..5).</summary>
    private int CurrentProfile()
        => CbProfile.SelectedItem is MpProfileItem pi ? pi.Slot : 1;

    // ================================================================
    // Profile switching (called by IActionHost.SwitchProfile when
    // a MacroPad button has action type "profile")
    // ================================================================

    /// <summary>
    /// Resolves "Next"/"Previous"/"N" and switches the MacroPad firmware profile.
    /// Cycles through existing profile slots only; also updates the UI combo.
    /// </summary>
    internal void MpSwitchProfile(string target)
    {
        if (CurrentDeviceId() is not int id) return;
        int cur = CurrentProfile();
        var existing = _store.GetExistingProfiles(id);
        if (existing.Count == 0) return;

        var t = (target ?? "").Trim();
        int next;
        if (t.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Next Profile", StringComparison.OrdinalIgnoreCase))
        {
            int idx = existing.IndexOf(cur);
            next = existing[(idx + 1) % existing.Count];
        }
        else if (t.Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("Previous Profile", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("prev", StringComparison.OrdinalIgnoreCase))
        {
            int idx = existing.IndexOf(cur);
            next = existing[(idx - 1 + existing.Count) % existing.Count];
        }
        else if (int.TryParse(t, out var n) && n >= 1 && n <= MacroPadService.ProfileCount)
            next = n;
        else
        {
            Log($"[EXEC] profile: target \"{t}\" not resolved for MacroPad");
            return;
        }
        if (next == cur) return;

        _macroPad.SwitchProfile((uint)(uint)id, next);
        _store.SetCurrentProfile(id, next);
        MpSelectProfileSlot(next);
        ReloadCurrentProfile();
        Log($"[EXEC] MacroPad profile -> {next}");
    }
}
