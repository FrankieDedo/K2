// MainWindow.Layout.cs — partial class: dynamic keyboard layout.
//
// Media Dock / Display Dial: image (keytop.png) overlaid by half on the
// top edge of the keyboard body, horizontally centered.
//
// Numpad: positioned to the left or right of the keyboard+dock column.
//   byNumpadPlug: 0=hidden, 1=right, 2=left
//   byMMDockPlug: 0=hidden, ≥1=connected

using System.Windows;
using System.Windows.Controls;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>
    /// Detects dock and numpad positions and updates visibility/order.
    /// Called after SDK connection.
    /// </summary>
    private void UpdateKeyboardLayout()
    {
        if (_everest is null) return;

        byte dockPos   = _everest.MMDockPlugPosition();
        byte numpadPos = _everest.NumpadPlugPosition();

        LogEverest($"[LAYOUT] dockPos={dockPos} numpadPos={numpadPos}");

        // ---- Dock (overlaid on the top edge of the keyboard) ----
        if (dockPos != 0)
        {
            ImgEvDock.Visibility = Visibility.Visible;
            // Align to the side where it is physically connected: 1=right, 2=left
            ImgEvDock.HorizontalAlignment = dockPos == 2
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }
        else
        {
            ImgEvDock.Visibility = Visibility.Collapsed;
        }

        // ---- Numpad (left or right of the keyboard column) ----
        if (numpadPos == 0)
        {
            CvsEvNumpad.Visibility = Visibility.Collapsed;
        }
        else
        {
            CvsEvNumpad.Visibility = Visibility.Visible;

            // Reorder SpEvLayout children: [GrdEvKeyColumn] and [CvsEvNumpad]
            SpEvLayout.Children.Clear();

            if (numpadPos == 2) // left
            {
                CvsEvNumpad.Margin = new Thickness(0, 0, 6, 0);
                SpEvLayout.Children.Add(CvsEvNumpad);
                SpEvLayout.Children.Add(GrdEvKeyColumn);
            }
            else // 1 or other = right (default)
            {
                CvsEvNumpad.Margin = new Thickness(6, 0, 0, 0);
                SpEvLayout.Children.Add(GrdEvKeyColumn);
                SpEvLayout.Children.Add(CvsEvNumpad);
            }
        }
    }
}
