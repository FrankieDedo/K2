// MainWindow.NumpadDisplayKeys.cs — partial class: 4 Everest numpad display keys.
// Unified interface (matches DisplayPad): a single click opens NdkKeyConfigDialog,
// which combines image + action in one window. Right-click keeps quick "remove"
// shortcuts only. Images are uploaded via EverestImageUploader (72×72 RGB565) and
// actions are saved in EverestStore as "ndk.{profile}.{keyIndex}.imagePath"/"actionType"
// etc. — PER PROFILE (see UploadNdkImage's doc comment: confirmed via USB capture
// 2026-07-16 that the firmware itself stores each profile's 4 NDK pictures separately,
// which is also why switching the active firmware profile is instant on real hardware —
// no image re-transfer needed, just EverestService.SwitchProfile).

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Number of display keys on the numpad.</summary>
    private const int NdkCount = 4;

    /// <summary>Square size of each key in the canvas (logical px, = 1U).</summary>
    private const double NdkBtnSize = 30;

    /// <summary>UI buttons for the 4 display keys.</summary>
    private readonly Button[] _ndkButtons = new Button[NdkCount];

    /// <summary>Current image path for each display key.</summary>
    private readonly string?[] _ndkImagePaths = new string?[NdkCount];

    /// <summary>Action associated with each display key (type, value).</summary>
    private readonly (string? Type, string? Value)[] _ndkActions = new (string?, string?)[NdkCount];

    // ---- Drag & drop (swap two display keys' action + image) ----
    private const string NdkDragFormat = "K2.NdkIndex";
    private Point _ndkDragStartPoint;
    private int? _ndkDragCandidate;

    // ─────────────────────── Init ───────────────────────

    /// <summary>
    /// Creates the 4 display key buttons and positions them at the top of
    /// the numpad Canvas (above the regular keys).
    /// </summary>
    private void InitNumpadDisplayKeys()
    {
        // Aligned to the numpad grid (same constants as KeyboardLayout):
        // npL=20, U=30, G=2. NumLock row is at y≈85, display keys sit
        // in the row above (y=53), same size as regular keys.
        const double startX = 20;
        const double startY = 27;
        const double gap = 2;

        var bgBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
        var borderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0xBE, 0xC3)); // teal accent

        for (int i = 0; i < NdkCount; i++)
        {
            int keyIndex = i; // capture

            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = $"D{i + 1}",
                Foreground = Brushes.White,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0.5,
            };

            var grid = new Grid();
            grid.Children.Add(img);
            grid.Children.Add(label);

            var btn = new Button
            {
                Width = NdkBtnSize,
                Height = NdkBtnSize,
                Content = grid,
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Cursor = Cursors.Hand,
                Tag = keyIndex,
                ToolTip = $"Display Key {i + 1} (click: configure image + action)",
                ContextMenu = BuildNdkContextMenu(),
            };

            btn.Click += NdkButton_Click;
            btn.AllowDrop = true;
            btn.PreviewMouseLeftButtonDown += NdkButton_PreviewMouseLeftButtonDown;
            btn.PreviewMouseMove += NdkButton_PreviewMouseMove;
            btn.DragEnter += NdkButton_DragEnter;
            btn.DragLeave += NdkButton_DragLeave;
            btn.Drop += NdkButton_Drop;

            Canvas.SetLeft(btn, startX + i * (NdkBtnSize + gap));
            Canvas.SetTop(btn, startY);
            CvsEvNumpad.Children.Add(btn);
            _ndkButtons[i] = btn;
        }

        // Load saved state
        LoadNdkState();
    }

    // ─────────────────────── Persistence ───────────────────────

    /// <summary>(Re)loads the 4 NDK buttons' thumbnails/actions for the CURRENT Everest
    /// profile (<see cref="EvCurrentProfile"/>) — called at startup and every time
    /// <see cref="ReloadEverestProfile"/> switches profile. No hardware I/O: the pictures
    /// were already pushed to that profile's firmware slot whenever they were assigned
    /// (see <see cref="UploadNdkImage"/>), so a profile switch only needs to refresh what
    /// the on-screen buttons display, matching what's actually resident on the device.
    /// Guarded for the one call before <see cref="InitNumpadDisplayKeys"/> has built the
    /// buttons yet (InitEverestModule's ReloadEverestProfile runs first).</summary>
    private void LoadNdkState()
    {
        if (_ndkButtons[0] is null) return;
        int profile = EvCurrentProfile();
        for (int i = 0; i < NdkCount; i++)
        {
            _ndkImagePaths[i] = _evStore.GetSetting($"ndk.{profile}.{i}.imagePath");
            _ndkActions[i] = (
                _evStore.GetSetting($"ndk.{profile}.{i}.actionType"),
                _evStore.GetSetting($"ndk.{profile}.{i}.actionValue")
            );

            if (!string.IsNullOrEmpty(_ndkImagePaths[i]) && File.Exists(_ndkImagePaths[i]))
                NdkSetThumbnail(i, _ndkImagePaths[i]!);
            else
                NdkClearThumbnail(i);
        }
    }

    /// <summary>Persists display key <paramref name="index"/>'s image/action for the
    /// CURRENT Everest profile.</summary>
    private void SaveNdkKey(int index)
    {
        int profile = EvCurrentProfile();
        _evStore.SetSetting($"ndk.{profile}.{index}.imagePath", _ndkImagePaths[index] ?? "");
        _evStore.SetSetting($"ndk.{profile}.{index}.actionType", _ndkActions[index].Type ?? "");
        _evStore.SetSetting($"ndk.{profile}.{index}.actionValue", _ndkActions[index].Value ?? "");
    }

    // ─────────────────────── Thumbnail ───────────────────────

    private void NdkSetThumbnail(int index, string imagePath)
    {
        try
        {
            // Load from bytes into a MemoryStream, NOT BitmapImage.UriSource: every NDK
            // slot always reuses the same fixed filename (ndk_{index}.png, whether written
            // by XML import, BC-DB import, or NdkApplyImage), and WPF's imaging pipeline
            // caches a UriSource-loaded BitmapImage by that URI — re-importing/overwriting
            // the same file left the canvas thumbnail showing the stale (or, after a prior
            // failed load, blank) cached bitmap instead of the new content, even though
            // NdkKeyConfigDialog's own preview (RefreshImagePreview, same byte-stream
            // approach) showed the correct new icon from the very same path. Mirrors that
            // dialog's loading code exactly so both stay in sync.
            byte[] bytes = File.ReadAllBytes(imagePath);
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = ms;
            bi.DecodePixelWidth = 72;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();

            if (_ndkButtons[index].Content is Grid g && g.Children[0] is Image img)
            {
                img.Source = bi;
                // Hide the "D1" label
                if (g.Children[1] is TextBlock tb)
                    tb.Visibility = Visibility.Collapsed;
            }
        }
        catch { /* invalid image */ }
    }

    private void NdkClearThumbnail(int index)
    {
        if (_ndkButtons[index].Content is Grid g && g.Children[0] is Image img)
        {
            img.Source = null;
            if (g.Children[1] is TextBlock tb)
            {
                tb.Visibility = Visibility.Visible;
                tb.Text = $"D{index + 1}";
            }
        }
    }

    // ─────────────────────── Click: unified image + action dialog ───────────────────────

    private void NdkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int keyIndex }) return;
        ConfigureNdkKey(keyIndex);
    }

    /// <summary>
    /// Opens the unified image+action dialog for display key <paramref name="keyIndex"/>
    /// and applies the result. Shared by the canvas display-key button (above) and by
    /// LvEvKeys's "Configure" button once an NDK entry appears in that same mapped-keys
    /// list (see MainWindow.Everest.cs's EvAddNdkEntriesToKeyList/BtnEvConfig_Click) —
    /// both surfaces edit the same underlying state, so both must refresh the list.
    /// </summary>
    private void ConfigureNdkKey(int keyIndex)
    {
        var dlg = new NdkKeyConfigDialog(
            keyIndex, _ndkImagePaths[keyIndex], _ndkActions[keyIndex].Type, _ndkActions[keyIndex].Value, _evActionHost)
        { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _ndkActions[keyIndex] = (dlg.ActionType, dlg.ActionValue);

        if (dlg.ImageChanged)
        {
            if (!string.IsNullOrEmpty(dlg.NewImagePath) && File.Exists(dlg.NewImagePath))
            {
                NdkApplyImage(keyIndex, dlg.NewImagePath!);
            }
            else if (dlg.NewImagePath is null)
            {
                _ndkImagePaths[keyIndex] = null;
                NdkClearThumbnail(keyIndex);
            }
        }

        SaveNdkKey(keyIndex);
        EvRefreshNdkInKeyList();
        LogEverest($"[NDK] key={keyIndex} <- action={dlg.ActionType ?? "none"}, image {(dlg.ImageChanged ? "changed" : "unchanged")}");
    }

    // ─────────────────────── Drag & drop: swap two display keys ───────────────────────

    private void NdkButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ndkDragStartPoint = e.GetPosition(null);
        _ndkDragCandidate = (sender as Button)?.Tag as int?;
    }

    private void NdkButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _ndkDragCandidate is not int keyIndex) return;
        bool hasContent = _ndkActions[keyIndex].Type is not null || !string.IsNullOrEmpty(_ndkImagePaths[keyIndex]);
        if (!hasContent)
        {
            _ndkDragCandidate = null;
            return;
        }
        if (!DragDropHelper.ExceedsDragThreshold(_ndkDragStartPoint, e.GetPosition(null))) return;

        _ndkDragCandidate = null;
        DragDrop.DoDragDrop((Button)sender, new DataObject(NdkDragFormat, keyIndex), DragDropEffects.Move);
    }

    private void NdkButton_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(NdkDragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, true);
    }

    private void NdkButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
    }

    private void NdkButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (sender is not Button { Tag: int targetIndex }) return;
        if (!e.Data.GetDataPresent(NdkDragFormat)) return;

        int sourceIndex = (int)e.Data.GetData(NdkDragFormat);
        if (sourceIndex == targetIndex) return;

        (_ndkActions[sourceIndex], _ndkActions[targetIndex])       = (_ndkActions[targetIndex], _ndkActions[sourceIndex]);
        (_ndkImagePaths[sourceIndex], _ndkImagePaths[targetIndex]) = (_ndkImagePaths[targetIndex], _ndkImagePaths[sourceIndex]);

        NdkRefreshAfterSwap(sourceIndex);
        NdkRefreshAfterSwap(targetIndex);
        EvRefreshNdkInKeyList();

        LogEverest($"[NDK] swapped key={sourceIndex} <-> key={targetIndex}");
    }

    /// <summary>Refreshes the on-screen thumbnail and persists a display key after its
    /// entry in <see cref="_ndkImagePaths"/>/<see cref="_ndkActions"/> was swapped
    /// locally. Re-uploads the image to hardware (if any and the device is connected)
    /// since each display key's picture actually lives in the keyboard's firmware,
    /// keyed by key index — a local-only swap would leave the physical device showing
    /// the pre-swap pictures.</summary>
    private void NdkRefreshAfterSwap(int index)
    {
        string? path = _ndkImagePaths[index];
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            if (_everest.IsOpen)
                NdkApplyImage(index, path); // re-uploads, sets thumbnail + persists on success
            else
            {
                NdkSetThumbnail(index, path);
                SaveNdkKey(index);
            }
        }
        else
        {
            NdkClearThumbnail(index);
            SaveNdkKey(index);
        }
    }

    /// <summary>
    /// Uploads <paramref name="imagePath"/> (already 72×72 — either user-cropped or
    /// auto-generated, both via <see cref="NdkKeyConfigDialog"/>) to display key
    /// <paramref name="keyIndex"/> of the CURRENT Everest profile, persists it, and
    /// refreshes the thumbnail. <see cref="EverestSdkNative.StartPicUpdate"/> is
    /// synchronous and takes ~2s (confirmed via USB capture, K2/_reference/usb_dumps,
    /// 2026-07-16), so a full-window "please wait" overlay (<see cref="ShowHwBusy"/>) is
    /// shown for the duration — mirrors Base Camp, which blocks its own UI the same way
    /// while pushing NDK/Display Dial pictures.
    /// </summary>
    private void NdkApplyImage(int keyIndex, string imagePath)
    {
        if (_everest is null || !_everest.IsOpen)
        {
            LogEverest("[NDK] Everest not connected.");
            return;
        }

        NdkSetButtonsEnabled(false);
        _ndkUploadBusy = true;
        ShowHwBusy(Loc.Get("hw_busy_uploading_image"));
        try
        {
            bool ok = UploadNdkImage(keyIndex, imagePath, EvCurrentProfile());
            if (ok)
            {
                _ndkImagePaths[keyIndex] = imagePath;
                NdkSetThumbnail(keyIndex, imagePath);
                SaveNdkKey(keyIndex);

                // Update SetDisplayKeyPic to show the new image
                NdkRefreshDevicePicSlots();
            }
        }
        finally
        {
            HideHwBusy();
            NdkSetButtonsEnabled(true);
            // The firmware reports the numpad/dock as unplugged (byNumpadPlug/byMMDockPlug
            // == 0) while it's busy writing the new NDK picture to flash — UpdateKeyboardLayout
            // (MainWindow.Layout.cs) was suppressed for the duration (_ndkUploadBusy), so it
            // never acted on that transient reading. Run it once now that the write settled to
            // pick up the real state (also catches an actual unplug that happened meanwhile).
            _ndkUploadBusy = false;
            UpdateKeyboardLayout();
        }
    }

    /// <summary>
    /// Uploads <paramref name="imagePath"/> to display key <paramref name="keyIndex"/> of
    /// firmware profile <paramref name="profile"/> (1..5). Wire mapping confirmed via USB
    /// capture while sniffing real Base Camp (K2/_reference/usb_dumps/evicone.pcapng,
    /// 2026-07-16): the SDK's <c>StartPicUpdate</c> header encodes
    /// <c>byTargetPic = firmware profile number</c>, NOT the key index as originally
    /// guessed — a single manual D1 upload used targetPic=01 (profile 1 was active), while
    /// loading profile 2's 4 icons used targetPic=02 constant with byTargetSubItem=0..3 as
    /// the key index. This is also why switching the active firmware profile is instant on
    /// real hardware: each profile's 4 pictures live in its own flash slot, so nothing needs
    /// re-transferring on switch — only on an actual edit, which is what this method is for.
    /// Shared by <see cref="NdkApplyImage"/> (single-key edit, current profile) and
    /// <see cref="EvUploadNdkImages"/> (re-sync on fresh device connect, see
    /// MainWindow.Everest.cs). Does not touch <see cref="_ndkImagePaths"/>/UI state; the
    /// caller decides what to do with the result.
    /// </summary>
    private bool UploadNdkImage(int keyIndex, string imagePath, int profile)
    {
        bool ok = _everest.UploadNumpadImage(imagePath, keyIndex, picSlot: (byte)profile);
        LogEverest($"[NDK] Upload profile={profile} key={keyIndex} -> {(ok ? "OK" : "FAIL")}");
        return ok;
    }

    /// <summary>Set while <see cref="NdkApplyImage"/> is writing a picture to the device —
    /// <see cref="UpdateKeyboardLayout"/> (MainWindow.Layout.cs) skips its poll-driven
    /// numpad/dock visibility update during this window, since the firmware transiently
    /// reports both as unplugged while busy with the flash write, which would otherwise
    /// flicker them out of the UI until the next successful poll.</summary>
    private bool _ndkUploadBusy;

    /// <summary>Enables/disables all 4 NDK buttons while a hardware reload is in flight,
    /// so the user can't queue up another upload before the previous one (and its
    /// device-side pic-slot refresh) has actually settled.</summary>
    private void NdkSetButtonsEnabled(bool enabled)
    {
        foreach (var btn in _ndkButtons)
            if (btn is not null) btn.IsEnabled = enabled;
    }

    /// <summary>Sends SetDisplayKeyPic with the current slots.</summary>
    private void NdkRefreshDevicePicSlots()
    {
        if (_everest is null || !_everest.IsOpen) return;
        // Simple slot mapping: key index = pic index
        _everest.SetDisplayKeyPic(0, 1, 2, 3);
    }

    // ─────────────────────── Context menu ───────────────────────

    private ContextMenu BuildNdkContextMenu()
    {
        var menu = new ContextMenu();

        var miRa = new MenuItem { Header = Loc.Get("dp_remove_action") };
        miRa.Click += NdkMnuRemoveAction_Click;

        menu.Items.Add(miRa);
        return menu;
    }

    private static int NdkIndexFromMenu(object sender) =>
        sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is Button { Tag: int idx } ? idx : -1;

    /// <summary>Removing the action also clears the key's picture — same behavior as
    /// the unified config dialog's "Remove action" button.</summary>
    private void NdkMnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        int idx = NdkIndexFromMenu(sender);
        if (idx < 0) return;
        ClearNdkKey(idx);
    }

    /// <summary>Clears display key <paramref name="idx"/>'s action and icon. Shared by
    /// the canvas context menu (above) and LvEvKeys's "Remove" button for an NDK entry
    /// (see BtnEvRemove_Click in MainWindow.Everest.cs).</summary>
    private void ClearNdkKey(int idx)
    {
        _ndkActions[idx] = (null, null);
        _ndkImagePaths[idx] = null;
        NdkClearThumbnail(idx);
        SaveNdkKey(idx);
        EvRefreshNdkInKeyList();
        LogEverest($"[NDK] key={idx} action removed");
    }

    // ─────────────────────── Display key action execution ───────────────────────

    /// <summary>
    /// Called from the SDK callback when a numpad display key is pressed.
    /// The matrixId for display keys is typically 0xF0-0xF3 (to be verified).
    /// </summary>
    internal void HandleNumpadDisplayKeyPress(int keyIndex)
    {
        if (keyIndex < 0 || keyIndex >= NdkCount) return;

        var (aType, aValue) = _ndkActions[keyIndex];
        if (aType is null) return;

        // Execute via the same action engine used for keyboard/DisplayPad
        if (_evEngine is not null)
        {
            LogEverest($"[NDK] executing action key={keyIndex}: {aType}");
            _evEngine.Execute(aType, aValue, buttonIndex: keyIndex);
        }
    }
}
