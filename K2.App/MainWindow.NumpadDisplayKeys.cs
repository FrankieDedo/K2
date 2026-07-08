// MainWindow.NumpadDisplayKeys.cs — partial class: 4 Everest numpad display keys.
// Unified interface (matches DisplayPad): a single click opens NdkKeyConfigDialog,
// which combines image + action in one window. Right-click keeps quick "remove"
// shortcuts only. Images are uploaded via EverestImageUploader (72×72 RGB565) and
// actions are saved in EverestStore as "ndk.{keyIndex}.imagePath"/"actionType" etc.

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

            Canvas.SetLeft(btn, startX + i * (NdkBtnSize + gap));
            Canvas.SetTop(btn, startY);
            CvsEvNumpad.Children.Add(btn);
            _ndkButtons[i] = btn;
        }

        // Load saved state
        LoadNdkState();
    }

    // ─────────────────────── Persistence ───────────────────────

    private void LoadNdkState()
    {
        for (int i = 0; i < NdkCount; i++)
        {
            _ndkImagePaths[i] = _evStore.GetSetting($"ndk.{i}.imagePath");
            _ndkActions[i] = (
                _evStore.GetSetting($"ndk.{i}.actionType"),
                _evStore.GetSetting($"ndk.{i}.actionValue")
            );

            // Show thumbnail if the file exists
            if (!string.IsNullOrEmpty(_ndkImagePaths[i]) && File.Exists(_ndkImagePaths[i]))
                NdkSetThumbnail(i, _ndkImagePaths[i]!);
        }
    }

    private void SaveNdkKey(int index)
    {
        _evStore.SetSetting($"ndk.{index}.imagePath", _ndkImagePaths[index] ?? "");
        _evStore.SetSetting($"ndk.{index}.actionType", _ndkActions[index].Type ?? "");
        _evStore.SetSetting($"ndk.{index}.actionValue", _ndkActions[index].Value ?? "");
    }

    // ─────────────────────── Thumbnail ───────────────────────

    private void NdkSetThumbnail(int index, string imagePath)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(imagePath, UriKind.Absolute);
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
        LogEverest($"[NDK] key={keyIndex} <- action={dlg.ActionType ?? "none"}, image {(dlg.ImageChanged ? "changed" : "unchanged")}");
    }

    /// <summary>
    /// Uploads <paramref name="imagePath"/> (already 72×72 — either user-cropped or
    /// auto-generated, both via <see cref="NdkKeyConfigDialog"/>) to display key
    /// <paramref name="keyIndex"/>, persists it, and refreshes the thumbnail.
    /// </summary>
    private void NdkApplyImage(int keyIndex, string imagePath)
    {
        if (_everest is null || !_everest.IsOpen)
        {
            LogEverest("[NDK] Everest not connected.");
            return;
        }

        // Upload to device — try parameter combinations for debugging.
        // SDK: targetDev=1, targetPic=picSlot, targetSubItem=keyIndex.
        // Try different (picSlot, keyIndex) combinations to find which
        // mapping works for all 4 display keys.
        bool ok = false;

        // Attempt 1: picSlot=0, subItem=keyIndex (all on the same slot)
        ok = _everest.UploadNumpadImage(imagePath, keyIndex, picSlot: 0);
        LogEverest($"[NDK] Upload key={keyIndex} try1(pic=0,sub={keyIndex}) -> {(ok ? "OK" : "FAIL")}");

        if (!ok)
        {
            // Attempt 2: picSlot=keyIndex, subItem=keyIndex
            ok = _everest.UploadNumpadImage(imagePath, keyIndex, picSlot: (byte)keyIndex);
            LogEverest($"[NDK] Upload key={keyIndex} try2(pic={keyIndex},sub={keyIndex}) -> {(ok ? "OK" : "FAIL")}");
        }

        if (!ok)
        {
            // Attempt 3: picSlot=keyIndex+1, subItem=keyIndex
            ok = _everest.UploadNumpadImage(imagePath, keyIndex, picSlot: (byte)(keyIndex + 1));
            LogEverest($"[NDK] Upload key={keyIndex} try3(pic={keyIndex + 1},sub={keyIndex}) -> {(ok ? "OK" : "FAIL")}");
        }

        if (!ok)
        {
            // Attempt 4: strip mode (targetDev=0, targetPic=2+keyIndex)
            ok = _everest.UploadNumpadImageStrip(imagePath, keyIndex, picSlot: (byte)keyIndex);
            LogEverest($"[NDK] Upload key={keyIndex} try4-strip(pic={keyIndex},sub={keyIndex}) -> {(ok ? "OK" : "FAIL")}");
        }

        if (ok)
        {
            _ndkImagePaths[keyIndex] = imagePath;
            NdkSetThumbnail(keyIndex, imagePath);
            SaveNdkKey(keyIndex);

            // Update SetDisplayKeyPic to show the new image
            NdkRefreshDevicePicSlots();
        }
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
        _ndkActions[idx] = (null, null);
        _ndkImagePaths[idx] = null;
        NdkClearThumbnail(idx);
        SaveNdkKey(idx);
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
