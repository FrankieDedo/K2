using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;
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

    /// <summary>Mapped-keys list for the Key Binding section (LvMpKeys) —
    /// unlike <see cref="_keys"/> (always all 12, needed for the grid), this
    /// only holds keys that HAVE an action, mirroring Everest Max's _evKeys
    /// list. Rebuilt (not diffed) on every mutation — 12 keys max, cheap.</summary>
    private readonly ObservableCollection<MacroPadKey> _mpMappedKeys = new();

    /// <summary>Maps <c>hardware matrix → key index</c> for the active device.</summary>
    private readonly Dictionary<int, int> _matrixToIndex = new();

    /// <summary>Index of the cell awaiting a press during remapping (-1 = inactive).</summary>
    private int _mapAwaitingIndex = -1;

    private bool _suppressProfileUpdate;

    /// <summary>Orientation in which the MacroPad is mounted (2×6 rotatable grid).</summary>
    private MacroPadRotation _rotation = MacroPadRotation.None;

    private bool _suppressRotationUpdate;

    // ---- Drag & drop (swap two keys' action) ----
    private const string MacroPadDragFormat = "K2.MacroPadKeyIndex";
    private Point _mpDragStartPoint;
    private MacroPadKey? _mpDragCandidate;

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
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = Brushes.White,
                FontSize      = 11,
                FontFamily    = new FontFamily("Segoe UI,system-ui,Arial,sans-serif"),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width         = KeySize - 16,
            };
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(MacroPadKey.KeyLabel)));

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
            btn.AllowDrop = true;
            btn.PreviewMouseLeftButtonDown += KeyButton_PreviewMouseLeftButtonDown;
            btn.PreviewMouseMove += KeyButton_PreviewMouseMove;
            btn.DragEnter += KeyButton_DragEnter;
            btn.DragLeave += KeyButton_DragLeave;
            btn.Drop += KeyButton_Drop;
            _keyButtons[key.Index] = btn;
        }

        // Rotation selector (populated here, bound in XAML).
        _suppressRotationUpdate = true;
        CbMacroRotation.ItemsSource = new[]
        {
            new RotationChoice(MacroPadRotation.None,  Loc.Get("pos_horizontal", 0)),
            new RotationChoice(MacroPadRotation.Cw90,  Loc.Get("pos_vertical", 90)),
            new RotationChoice(MacroPadRotation.Cw180, Loc.Get("pos_horizontal", 180)),
            new RotationChoice(MacroPadRotation.Cw270, Loc.Get("pos_vertical", 270)),
        };
        CbMacroRotation.DisplayMemberPath = nameof(RotationChoice.Label);
        CbMacroRotation.SelectedIndex = 0;
        _suppressRotationUpdate = false;

        RebuildKeyGrid();

        LvMpKeys.ItemsSource = _mpMappedKeys;
        LstMpProfile.ContextMenu = MpBuildProfileContextMenu();
        BtnMpProfileMenu.ContextMenu = MpBuildProfileMenuNoEdit();

        // LstMpProfile is populated by MpRefreshProfiles on device change

        LoadRotationFromStore();

        // Restore custom tab name
        var savedName = _store.GetSetting("device.name");
        if (!string.IsNullOrEmpty(savedName))
            TabMacroPad.Header = savedName;

        InitMpSectionNav();
    }

    private void BtnMpRename_Click(object sender, RoutedEventArgs e)
    {
        string current = TabMacroPad.Header as string ?? Loc.Get("tab_macropad");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabMacroPad.Header = name;
        _store.SetSetting("device.name", name);
    }

    /// <summary>Right-click menu for LstMpProfile rows — see DpBuildProfileContextMenu
    /// (MainWindow.DisplayPad.cs) for the shared pattern/rationale.</summary>
    private ContextMenu MpBuildProfileContextMenu()
    {
        var menu = new ContextMenu();
        var miRename = new MenuItem { Header = Loc.Get("rename_profile") };
        miRename.Click += BtnMpRenameProfile_Click;
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnMpImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnMpImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnMpExportProfiles_Click;
        var miDelete = new MenuItem { Header = Loc.Get("delete_profile") };
        miDelete.Click += BtnMpDeleteProfile_Click;
        menu.Items.Add(miRename);
        menu.Items.Add(new Separator());
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDelete);
        return menu;
    }

    /// <summary>Same items as <see cref="MpBuildProfileContextMenu"/> minus Rename/Delete —
    /// opened from the small "…" button in the Profile header (BtnMpProfileMenu_Click),
    /// which is not tied to a specific row so renaming/deleting a specific profile
    /// wouldn't make sense there.</summary>
    private ContextMenu MpBuildProfileMenuNoEdit()
    {
        var menu = new ContextMenu();
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnMpImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnMpImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnMpExportProfiles_Click;
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        return menu;
    }

    private void BtnMpProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = btn;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
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
        _store.DeleteProfile(id, slot);
        Log($"[UI ] MacroPad profile {slot} deleted.");
        MpRefreshProfiles(id);
        // LstMpProfile_SelectionChanged will reload the key grid automatically
    }

    /// <summary>Gear-icon popup for a MacroPad profile row (see ProfileGear_Click in
    /// MainWindow.xaml.cs): rename, delete (same guard as <see cref="BtnMpDeleteProfile_Click"/>),
    /// or link an executable whose launch auto-switches to this profile (see
    /// K2.Core.Services.ProfileLaunchWatcher, registered from <see cref="MpRefreshProfiles"/>).</summary>
    private void MpShowProfileGear(MpProfileItem pi)
    {
        if (CurrentDeviceId() is not int id) return;
        string currentName = _store.GetProfileName(id, pi.Slot) ?? Loc.Get("profile_n", pi.Slot);
        string currentExe = _store.GetSetting($"profile.{id}.{pi.Slot}.launchExe") ?? "";
        var dlg = new ProfileSettingsDialog(currentName, currentExe) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.DeleteRequested)
        {
            var existing = _store.GetExistingProfiles(id);
            if (existing.Count <= 1)
            {
                MessageBox.Show(Loc.Get("delete_profile_last"),
                    Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var res = MessageBox.Show(
                Loc.Get("delete_profile_confirm", currentName),
                Loc.Get("delete_profile"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
            _store.DeleteProfile(id, pi.Slot);
            Log($"[UI ] MacroPad profile {pi.Slot} deleted (gear).");
        }
        else
        {
            _store.SetProfileName(id, pi.Slot, dlg.ProfileName);
            _store.SetSetting($"profile.{id}.{pi.Slot}.launchExe", dlg.ExePath);
            Log($"[UI ] MacroPad profile {pi.Slot} settings updated (gear).");
        }
        MpRefreshProfiles(id);
    }

    /// <summary>Wipes EVERY profile of the selected MacroPad unit back to K2's defaults:
    /// other profiles are deleted outright (mirrors BtnMpDeleteProfile_Click), the current
    /// one keeps its name but has its key actions cleared and its LED lighting reset to
    /// the factory values (<see cref="MpResetLedToDefaults"/>) — LED lighting became
    /// per-profile 2026-07-22 (see MacroLedPrefix), so "restore defaults" needs to touch
    /// it too, unlike when this button was last documented as lighting being device-wide.
    /// User request 2026-07-29.</summary>
    private void BtnMpRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) return;
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_device_confirm", Loc.Get("tab_macropad")),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        int current = CurrentProfile();
        foreach (var slot in _store.GetExistingProfiles(id))
            if (slot != current)
                _store.DeleteProfile(id, slot);

        _store.ClearProfile(id, current);
        MpResetLedToDefaults();
        Log($"[UI ] MacroPad device {id} restored to factory defaults (all profiles, lighting and key bindings).");
        MpRefreshProfiles(id);
        ReloadCurrentProfile();
    }

    /// <summary>Rotation combo item.</summary>
    private sealed record RotationChoice(MacroPadRotation Rotation, string Label)
    {
        // Fallback for the closed ComboBox: when the control's ancestor is still
        // Visibility="Collapsed" at the time ItemsSource/DisplayMemberPath are
        // set (e.g. MacroPad tab not yet selected), WPF may render the closed
        // box via ToString() instead of DisplayMemberPath. Matching ToString()
        // to the label keeps it correct either way.
        public override string ToString() => Label;
    }

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

        // Counter-rotate each button so its border and label stay correctly oriented.
        // The TextBlock inside the button rotates along with it -> no extra transform needed.
        var counterTransform = _rotation == MacroPadRotation.None
            ? Transform.Identity
            : new RotateTransform(-(int)_rotation);
        foreach (var btn in _keyButtons)
        {
            btn.LayoutTransform = counterTransform;
            if (btn.Content is TextBlock lbl)
                lbl.LayoutTransform = Transform.Identity; // the button already handles the rotation
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
        // A running Wave direction is screen-relative (see MacroPhysicalDirIndex
        // in MainWindow.MacroLed.cs) — resend so it keeps pointing the same way
        // on screen under the new mounting.
        ApplyCurrentMacroEffect();
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
        if (sender is not Button { Tag: MacroPadKey key }) return;

        // Edit-individual-keycaps mode (Settings section): open the per-key color/image
        // customizer instead of the action-configuration dialog.
        if (_mpKeycapEditMode && IsMpAppearanceSectionActive)
        {
            OpenMpKeycapCustomizeDialog(key.Index, key.KeyLabel);
            return;
        }

        // Custom lighting paint mode (LED Lighting section, "Custom" effect selected):
        // color the key and consume the click — see MainWindow.MpCustomLighting.cs.
        if (TryMpCustomPaint(key.Index))
            return;

        ConfigureAction(key);
    }

    private void MnuConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        if (KeyFromContextMenu(sender) is MacroPadKey key)
            ConfigureAction(key);
    }

    private void ConfigureAction(MacroPadKey key)
    {
        // Key editing is only enabled while the "Key Binding" section is active.
        if (!IsMpKeyBindingSectionActive) return;
        if (CurrentDeviceId() is not int id) { Log("[WARN] Select a device first."); return; }

        var dlg = new ButtonActionDialog(key.Index, key.ActionType, key.ActionValue, this) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        key.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                          ? null : dlg.ActionType;
        key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;
        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), key.Index, key.ActionType, key.ActionValue));
        RefreshMpMappedKeys();
        Log($"[ACT ] key #{key.Index} <- type={key.ActionType ?? "none"} value=\"{key.ActionValue}\"");
    }

    private void MnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMpKeyBindingSectionActive) return;
        if (KeyFromContextMenu(sender) is not MacroPadKey key) return;
        if (CurrentDeviceId() is not int id) return;
        key.ActionType = null;
        key.ActionValue = null;
        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), key.Index, null, null));
        RefreshMpMappedKeys();
        Log($"[ACT ] key #{key.Index} action removed");
    }

    /// <summary>Rebuilds the Key Binding section's mapped-keys list (LvMpKeys)
    /// from <see cref="_keys"/> — called after every action mutation.</summary>
    private void RefreshMpMappedKeys()
    {
        _mpMappedKeys.Clear();
        foreach (var k in _keys)
            if (k.HasAction) _mpMappedKeys.Add(k);
    }

    /// <summary>Configure/Remove only make sense with a row selected — mirrors
    /// LvEvKeys_SelectionChanged (MainWindow.Everest.cs).</summary>
    private void LvMpKeys_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = LvMpKeys.SelectedItem is not null;
        BtnMpConfigure.IsEnabled = hasSelection;
        BtnMpRemoveAction.IsEnabled = hasSelection;
    }

    /// <summary>"Configure" button next to LvMpKeys — same action dialog as
    /// clicking the key on the grid, for the currently selected list row.</summary>
    private void BtnMpConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (LvMpKeys.SelectedItem is not MacroPadKey key)
        {
            Log("[WARN] select a key first");
            return;
        }
        ConfigureAction(key);
    }

    /// <summary>"Remove" button next to LvMpKeys, for the currently selected list row.</summary>
    private void BtnMpRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMpKeyBindingSectionActive) return;
        if (LvMpKeys.SelectedItem is not MacroPadKey key) return;
        if (CurrentDeviceId() is not int id) return;
        key.ActionType = null;
        key.ActionValue = null;
        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), key.Index, null, null));
        RefreshMpMappedKeys();
        Log($"[ACT ] key #{key.Index} action removed");
    }

    // ============================================================
    // Drag & drop — swap two keys' action (grid rearrangement)
    // ============================================================

    private void KeyButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mpDragStartPoint = e.GetPosition(null);
        _mpDragCandidate = (sender as Button)?.Tag as MacroPadKey;
    }

    private void KeyButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _mpDragCandidate is null) return;
        if (!IsMpKeyBindingSectionActive || !_mpDragCandidate.HasAction)
        {
            _mpDragCandidate = null;
            return;
        }
        if (!DragDropHelper.ExceedsDragThreshold(_mpDragStartPoint, e.GetPosition(null))) return;

        var key = _mpDragCandidate;
        _mpDragCandidate = null;
        DragDrop.DoDragDrop((Button)sender, new DataObject(MacroPadDragFormat, key.Index), DragDropEffects.Move);
    }

    private void KeyButton_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(MacroPadDragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, true);
    }

    private void KeyButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
    }

    private void KeyButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (!IsMpKeyBindingSectionActive) return;
        if (CurrentDeviceId() is not int id) return;
        if (sender is not Button { Tag: MacroPadKey targetKey }) return;
        if (!e.Data.GetDataPresent(MacroPadDragFormat)) return;

        int sourceIndex = (int)e.Data.GetData(MacroPadDragFormat);
        if (sourceIndex < 0 || sourceIndex >= _keys.Length) return;
        var sourceKey = _keys[sourceIndex];
        if (ReferenceEquals(sourceKey, targetKey)) return;

        (sourceKey.ActionType, targetKey.ActionType)   = (targetKey.ActionType, sourceKey.ActionType);
        (sourceKey.ActionValue, targetKey.ActionValue) = (targetKey.ActionValue, sourceKey.ActionValue);

        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), sourceKey.Index, sourceKey.ActionType, sourceKey.ActionValue));
        _store.SaveKey(new MacroKeyRecord(id, CurrentProfile(), targetKey.Index, targetKey.ActionType, targetKey.ActionValue));
        RefreshMpMappedKeys();
        Log($"[ACT ] swapped key #{sourceKey.Index} <-> #{targetKey.Index}");
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

    private void LstMpProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileUpdate) return;
        if (CurrentDeviceId() is not int id) return;
        if (LstMpProfile.SelectedItem is not MpProfileItem pi) return;
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
            if (existing.Count == 0)
            {
            // No profile at all — fresh install, hardware factory reset or the Settings
            // tab's "Restore all defaults": recreate one instead of only showing a
            // phantom slot 1 under the generic "Profile 1" label. Named "Default
            // profile" (localized, `default_profile_name`), the same name Base Camp
            // gives its own starting profile. User request 2026-08-21.
                if (_store.GetProfileName(deviceId, 1) is null)
                    _store.SetProfileName(deviceId, 1, Loc.Get("default_profile_name"));
                existing.Add(1);
            }
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

            LstMpProfile.ItemsSource = items;

            int current = _store.GetCurrentProfile(deviceId);
            var match = items.Find(x => x.Slot == current && !x.IsNew);
            LstMpProfile.SelectedItem = match ?? items[0];

            MpRegisterProfileLaunchWatchers(deviceId, existing);
        }
        finally { _suppressProfileUpdate = false; }
    }

    /// <summary>Registers this device's profiles with K2.Core.Services.ProfileLaunchWatcher
    /// — see DpRegisterProfileLaunchWatchers (MainWindow.DisplayPad.cs) for the shared
    /// pattern/rationale.</summary>
    private void MpRegisterProfileLaunchWatchers(int deviceId, List<int> existing)
    {
        string scope = $"Mp:{deviceId}:";
        var currentKeys = new HashSet<string>();
        foreach (var slot in existing)
        {
            string? exe = _store.GetSetting($"profile.{deviceId}.{slot}.launchExe");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string key = scope + slot;
            currentKeys.Add(key);
            int capturedSlot = slot;
            ProfileLaunchWatcher.Instance.UpdateRegistration(key, exe,
                () => MpSwitchProfile(deviceId, capturedSlot.ToString()));
        }
        foreach (var staleKey in ProfileLaunchWatcher.Instance.KeysWithPrefix(scope).Except(currentKeys))
            ProfileLaunchWatcher.Instance.RemoveRegistration(staleKey);
    }

    /// <summary>Selects a slot in the MacroPad profile combo (suppressing the event).</summary>
    private void MpSelectProfileSlot(int slot)
    {
        _suppressProfileUpdate = true;
        try
        {
            if (LstMpProfile.ItemsSource is List<MpProfileItem> items)
                LstMpProfile.SelectedItem = items.Find(x => x.Slot == slot && !x.IsNew) ?? items[0];
        }
        finally { _suppressProfileUpdate = false; }
    }

    /// <summary>Reloads from DB the actions for the current (device, profile) into the grid.</summary>
    private void ReloadCurrentProfile()
    {
        foreach (var k in _keys) { k.ActionType = null; k.ActionValue = null; }
        if (CurrentDeviceId() is not int id)
        {
            RefreshMpMappedKeys();
            ReloadMacroLedForProfileSwitch();
            InitMpSettingsPanel();
            return;
        }
        int profile = CurrentProfile();
        var rows = _store.LoadProfile(id, profile);
        foreach (var r in rows)
        {
            if (r.KeyIndex < 0 || r.KeyIndex >= _keys.Length) continue;
            _keys[r.KeyIndex].ActionType  = r.ActionType;
            _keys[r.KeyIndex].ActionValue = r.ActionValue;
        }
        RefreshMpMappedKeys();
        Log($"[DB  ] loaded {rows.Count} actions for device={id} profile={profile}");

        ReloadMacroLedForProfileSwitch();
        InitMpSettingsPanel(); // re-loads Keycap Appearance for this slot — user request 2026-07-25
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
        _macroAutoOffTimer?.RegisterActivity();

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
            if (e.Pressed)
                TryExecuteAction(_keys[hi]);
            else
                // Picks up whatever keycap-appearance write may have landed (and been
                // skipped) while this key's IsHighlighted trigger was active — see
                // ApplyMacroKeycapAppearanceToAllKeys's doc comment for the "stuck gray
                // after a tap" bug this closes.
                ApplyMacroKeycapAppearanceToKey(hi);
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

            string profileName = root.Element("ProfileName")?.Value
                                 ?? Path.GetFileNameWithoutExtension(dlg.FileName);

            // Real Base Camp XML carries MacroPad key bindings under the SAME
            // EverestKeyBindings/KeyboardBinding wrapper Everest Max uses (confirmed
            // 2026-07-26 against a real BaseCamp.db + a real BC XML export — the
            // MacroPad's 12 keys share Everest Max's EverestKeyBidings DB table,
            // KeyId 170-179/220/221 = M1-M12, same scheme DisplayPad uses). The old
            // flat <MakaluKeyBindings> shape (K2's own pre-2026-07-26 export format,
            // never real Base Camp data) is kept as a fallback so previously-exported
            // K2 XML files still import.
            var bindings = root.Descendants("KeyboardBinding").ToList();
            bool legacyShape = bindings.Count == 0;
            if (legacyShape)
                bindings = root.Descendants("MakaluKeyBindings").ToList();
            if (bindings.Count == 0)
            {
                Log("[IMP-XML] No KeyboardBinding/MakaluKeyBindings found in XML.");
                return;
            }

            // Always land in a FRESH slot — see BaseCampDbImporter.FindFreeSlot's doc comment.
            int slot = BaseCampDbImporter.FindFreeSlot(_store.GetExistingProfiles(id));
            if (slot == 0)
            {
                MessageBox.Show(this, Loc.Get("import_no_free_slot", profileName),
                    Loc.Get("dp_open_bc_profile"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _store.ClearProfile(id, slot);

            // Register the name BEFORE translating any binding — same fix already made
            // for Everest Max/Everest 60: MacroPadStore.GetExistingProfiles only sees a
            // slot that has at least one Keys row, so a profile whose bindings all fail
            // to translate (or that carries lighting only) silently disappeared after
            // import — and even a successful import kept the default "Profile N" name.
            _store.SetProfileName(id, slot, profileName);

            int imported = 0;

            // Existing K2 macro names, so "Run Macro" bindings resolve against the
            // user's macro library — same pattern as the BC-db import below.
            var macroNames = _macroStore?.GetAll()
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            foreach (var b in bindings)
            {
                if (!int.TryParse(b.Element("KeyId")?.Value, out int keyId)) continue;

                string? funcType  = b.Element("FunctionType")?.Value;
                string? funcValue = b.Element("FunctionValue")?.Value;

                int keyIndex;
                string? actionType, actionValue;
                if (legacyShape)
                {
                    // Old K2-only flat shape: KeyId is 1-12 directly, FunctionEnteredValue
                    // carries the literal ActionType when FunctionType="K2Action" (no
                    // SubFunctionType column in that schema).
                    if (keyId < 1 || keyId > 12) continue;
                    keyIndex = keyId - 1;
                    string? funcEntered = b.Element("FunctionEnteredValue")?.Value;
                    if (funcType == "K2Action")
                    {
                        actionType  = funcEntered;
                        actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;
                    }
                    else
                    {
                        (actionType, actionValue) = BaseCampDbImporter.TranslateMakaluAction(funcType, funcValue, macroNames);
                    }
                }
                else
                {
                    // Real Base Camp shape (and K2's own current export, MpProfileExporter.
                    // ExportK2): KeyId is 170-179/220/221 = M1-M12, same as DisplayPad.
                    if (!BaseCampDbImporter.KeyIdToIndex.TryGetValue(keyId, out keyIndex)) continue;
                    if (funcType == "K2Action")
                    {
                        actionType  = b.Element("SubFunctionType")?.Value;
                        actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;
                    }
                    else
                    {
                        string? subType = b.Element("SubFunctionType")?.Value;
                        string? customUrl = b.Element("CustomURL")?.Value;
                        (actionType, actionValue) = BaseCampDbImporter.TranslateAction(funcType, subType, funcValue, macroNames, customUrl);
                    }
                }

                if (actionType is null) continue;
                _store.SaveKey(new MacroKeyRecord(id, slot, keyIndex, actionType, actionValue));
                imported++;
            }

            // Lighting (EverestLightings/Lighting — shared with Everest Max, see
            // BaseCampDbImporter.ApplyLightingToStore's doc comment).
            var lightingRows = new List<BaseCampDbImporter.BcLightingRow>();
            BaseCampDbImporter.BcCustomLighting? importedCustom = null;
            foreach (var lt in root.Descendants("Lighting"))
            {
                string? name = lt.Element("EffIndex")?.Value;
                int speed = int.TryParse(lt.Element("Speed")?.Value, out var sp) ? sp : 50;
                int brightness = int.TryParse(lt.Element("Brightness")?.Value, out var br) ? br : 100;
                int direction = int.TryParse(lt.Element("Direction")?.Value, out var di) ? di : 0;
                bool active = string.Equals(lt.Element("IsActive")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                byte effByte = BaseCampDbImporter.ResolveLightingEffectByte(name, null);
                int c1 = BaseCampDbImporter.ParseBcColor(lt.Element("Color1")?.Value, 0x900000);
                int c2 = BaseCampDbImporter.ParseBcColor(lt.Element("Color2")?.Value, 0);
                int c3 = BaseCampDbImporter.ParseBcColor(lt.Element("Color3")?.Value, 0);
                // <Type> = color-type pill (0 single / 1 dual / 2 rainbow), same as Everest Max.
                int colorType = int.TryParse(lt.Element("Type")?.Value, out var ct) ? ct : 0;
                lightingRows.Add(new BaseCampDbImporter.BcLightingRow(effByte, speed, brightness, direction, c1, c2, c3, active, colorType));

                // Per-key paint state of the Custom effect (12 M-keys) — same payload
                // shape as Everest Max's, and gated on IsActive for the same reason
                // (Base Camp's XML exporter synthesizes a default all-black board when
                // the profile has no saved paint — see the matching comment in
                // MainWindow.Everest.cs's BtnEvImportXml_Click).
                if (effByte == (byte)MacroPadSdkNative.EffectIndex.Custom && active)
                {
                    importedCustom = BaseCampDbImporter.ParseKeyboardCustomLighting(
                        lt.Element("CustomLightings")?.Value, BaseCampDbImporter.MacroPadKeyCount);
                    if (importedCustom is not null)
                    {
                        BaseCampDbImporter.ApplyCustomLightingToStore(_store.SetSetting,
                            $"macroled.p{slot}.custom.keyColors", $"macroled.p{slot}.custom.keyEffects", importedCustom);
                        Log($"[IMP-XML] custom lighting: {importedCustom.Colors.Count} painted key(s), " +
                            $"{importedCustom.Effects.Count} dynamic-effect key(s)");
                    }
                }
            }
            if (lightingRows.Count > 0)
                BaseCampDbImporter.ApplyLightingToStore(_store.SetSetting, $"macroled.p{slot}.", lightingRows);

            // After the effect rows (they set macroled.p{slot}.effect from IsActive): a
            // really painted board wins — see BaseCampDbImporter.LooksPainted.
            if (importedCustom is not null
                && BaseCampDbImporter.LooksPainted(importedCustom, BaseCampDbImporter.MacroPadKeyCount))
            {
                _store.SetSetting($"macroled.p{slot}.effect",
                    ((int)MacroPadSdkNative.EffectIndex.Custom).ToString());
            }

            // K2-format extra: the whole per-profile Settings namespace (see
            // K2ProfileSettingsXml). Absent from Base Camp files and from K2 exports made
            // before 2026-08-22, in which case this is a no-op.
            int k2Settings = K2ProfileSettingsXml.Apply(
                root, _store.SetSetting, slot, K2ProfileSettingsXml.SettingsOnlyFamilies);
            if (k2Settings > 0) Log($"[IMP-XML] {k2Settings} K2 profile setting(s) restored");

            _store.SetCurrentProfile(id, slot);
            MpRefreshProfiles(id);
            MpSelectProfileSlot(slot);
            ReloadCurrentProfile();

            // Replay the effect-dropdown pick so the imported lighting reaches the pad
            // and the preview right away — see MpReapplySelectedEffect.
            MpReapplySelectedEffect();

            Log($"[IMP-XML] '{profileName}' -> device {id} slot {slot}: {imported} keys");
            LblStatus.Text = Loc.Get("dp_imported_xml", profileName, slot);
        }
        catch (Exception ex)
        {
            Log($"[ERR] import XML: {ex.Message}");
        }
    }

    // ============================================================
    // Export profiles — Base Camp-compatible XML / K2-only XML
    // ============================================================

    private void BtnMpExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceId() is not int id) { LblStatus.Text = Loc.Get("dp_export_no_profile"); return; }

        var profiles = _store.GetExistingProfiles(id)
            .Select(slot => (Slot: slot, Name: _store.GetProfileName(id, slot) ?? Loc.Get("profile_n", slot)))
            .ToList();
        int? currentSlot = LstMpProfile.SelectedItem is MpProfileItem pi && !pi.IsNew ? pi.Slot : null;
        string deviceLabel = _mpDeviceLabels.GetValueOrDefault((uint)id, $"MacroPad {id}");

        ExportProfileHelper.Run(
            owner: this,
            deviceLabel: deviceLabel,
            profiles: profiles,
            currentSlot: currentSlot,
            exportOne: (slot, name, bcCompatible, path) =>
            {
                var result = bcCompatible
                    ? MpProfileExporter.ExportBaseCamp(_store, id, slot, name, path)
                    : MpProfileExporter.ExportK2(_store, id, slot, name, path);
                return (result.Exported, result.SkippedActions, result.SkipReasons);
            },
            log: Log,
            setStatus: s => LblStatus.Text = s);
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

        string deviceLabel = TabMacroPad.Header as string ?? Loc.Get("tab_macropad");

        List<BaseCampDbImporter.BcProfile> profiles;
        if (bcDevices.Count == 1)
        {
            profiles = bcDevices.Values.First();
        }
        else
        {
            // Ask which BC device to import from, instead of requiring an exact BC/K2
            // DeviceId match (the old behavior silently skipped everything on a mismatch).
            var options = bcDevices.Select(kv => (
                BcDeviceId: kv.Key,
                Label: Loc.Get("bc_pick_device_label", kv.Key, kv.Value.Count,
                    string.Join(", ", kv.Value.Select(p => p.Name)))
            )).ToList();
            var picker = new BcDevicePickerDialog(deviceLabel, options) { Owner = this };
            if (picker.ShowDialog() != true) return;
            profiles = bcDevices[picker.SelectedBcDeviceId!.Value];
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Import {profiles.Count} profile(s) into \"{deviceLabel}\"?\n");
        foreach (var p in profiles)
            sb.AppendLine($"  {(p.IsSelected ? "[ACTIVE] " : "")}{p.Name}");
        sb.AppendLine();
        sb.AppendLine(Loc.Get("bc_import_will_wipe", deviceLabel));

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Pre-read every profile's bindings BEFORE wiping anything: this import is
        // destructive (replace, not append), so a corrupt/locked Base Camp DB must surface
        // while the existing K2 profiles are still intact — not after they're gone.
        try
        {
            foreach (var p in profiles)
                BaseCampDbImporter.ReadMacroPadKeyBindings(dbPath, p.ProfileId);
        }
        catch (Exception ex)
        {
            Log($"[IMP-BC] Pre-read failed, aborting before wipe: {ex.Message}");
            MessageBox.Show(this, Loc.Get("bc_import_read_failed", ex.Message),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Wipe: replace, don't append — ALL slots, not only the ones GetExistingProfiles
        // reports (2026-08-22, same change as the Everest Max import): a slot with no Keys
        // row is invisible to that query yet can still hold stale macroled./settings. rows
        // that would become the starting point of whatever the import puts there.
        for (int wipeSlot = 1; wipeSlot <= MacroPadService.ProfileCount; wipeSlot++)
            _store.DeleteProfile(k2DeviceId, wipeSlot);

        int totalKeys = 0;
        var usedSlots = new HashSet<int>();

        // Existing K2 macro names, so "Run Macro" bindings resolve against the user's
        // macro library — same pattern as the DisplayPad/Everest/Everest60 BC imports.
        var macroNames = _macroStore?.GetAll()
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        foreach (var profile in profiles)
        {
            try
            {
                int targetSlot = BaseCampDbImporter.FindFreeSlot(usedSlots);
                if (targetSlot == 0) continue; // sanity ceiling only (5 real firmware slots)
                usedSlots.Add(targetSlot);

                int n = BaseCampDbImporter.ImportMacroPadProfile(dbPath, profile, k2DeviceId, _store, targetSlot, macroNames);
                totalKeys += n;
                Log($"[IMP-BC] slot {profile.Slot} '{profile.Name}' -> K2 slot {targetSlot}: {n} keys");
            }
            catch (Exception ex)
            {
                Log($"[IMP-BC] Error slot {profile.Slot}: {ex.Message}");
            }
        }

        // Always land on the FIRST imported profile and force a reload — simpler and
        // safer than trying to restore whatever was active in Base Camp (user request:
        // a plain, predictable refresh after import beats guessing at BC's own state).
        int slotToShow = usedSlots.DefaultIfEmpty(0).Min();
        MpRefreshProfiles(k2DeviceId);
        if (slotToShow > 0) MpSelectProfileSlot(slotToShow);
        ReloadCurrentProfile();

        // Same post-import apply as the XML path — see MpReapplySelectedEffect.
        MpReapplySelectedEffect();

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
        PnlMpDebugGroup.Visibility  = vis;  // common actions: Debug group (Refresh)
        LblSdk.Visibility           = vis;  // toolbar: SDK/DLL info label
    }

    /// <summary>SDK ID of the active MacroPad (set by TcDevices_SelectionChanged in xaml.cs).</summary>
    internal int? _activeMpDeviceId;
    private int? CurrentDeviceId() => _activeMpDeviceId;

    /// <summary>Currently selected profile for editing (1..5).</summary>
    private int CurrentProfile()
        => LstMpProfile.SelectedItem is MpProfileItem pi ? pi.Slot : 1;

    // ================================================================
    // Profile switching (called by IActionHost.SwitchProfile when
    // a MacroPad button has action type "profile")
    // ================================================================

    /// <summary>
    /// Resolves "Next"/"Previous"/"N" and switches the MacroPad firmware profile.
    /// Cycles through existing profile slots only. <paramref name="deviceId"/> = null
    /// targets the currently active MacroPad (and updates the UI combo); an explicit id
    /// (cross-device "switch profile" action) switches that device's stored profile and
    /// repaints it without touching the UI unless it happens to be the active device.
    /// </summary>
    internal void MpSwitchProfile(int? deviceId, string target)
    {
        int? id = deviceId ?? CurrentDeviceId();
        if (id is not int devId) return;
        bool isActive = devId == CurrentDeviceId();
        int cur = isActive ? CurrentProfile() : _store.GetCurrentProfile(devId);
        var existing = _store.GetExistingProfiles(devId);
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
            // Named-profile target — see MainWindow.Everest.cs's EvSwitchProfile for the
            // rationale (Base Camp XML/DB can carry a destination profile NAME instead of
            // Next/Previous/a slot number).
            int? byName = null;
            foreach (var s in existing)
                if (string.Equals(_store.GetProfileName(devId, s), t, StringComparison.OrdinalIgnoreCase)) { byName = s; break; }
            if (byName is int found) next = found;
            else
            {
                Log($"[EXEC] profile: target \"{t}\" not resolved for MacroPad");
                return;
            }
        }
        if (next == cur) return;

        _macroPad.SwitchProfile((uint)devId, next);
        _store.SetCurrentProfile(devId, next);
        if (isActive)
        {
            MpSelectProfileSlot(next);
            ReloadCurrentProfile();
        }
        Log($"[EXEC] MacroPad profile -> {next} (device {devId})");
    }
}
