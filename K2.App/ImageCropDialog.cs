using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.Core;

namespace K2.App;

/// <summary>
/// Modal popup wrapper around <see cref="CropEditor"/> — kept for the Everest NDK flow
/// (<c>MainWindow.NumpadDisplayKeys.NdkButton_Click</c>), which has no "load and rotate"
/// host dialog of its own to embed the editor into (see <see cref="CropEditor"/> remarks;
/// DisplayPad icon/fullscreen dialogs embed <see cref="CropEditor"/> directly instead, since
/// 2026-07-05, per user request to keep crop/zoom in the SAME window used to load/rotate).
/// </summary>
internal static class ImageCropDialog
{
    /// <summary>
    /// Shows the crop dialog for <paramref name="sourcePath"/>. Returns the path to a
    /// cropped+resized (or, if the user ticks "no crop", plain-stretched) PNG cached under
    /// <c>%LOCALAPPDATA%\K2\cropped\</c>, sized EXACTLY <paramref name="targetW"/>×
    /// <paramref name="targetH"/>. Returns null if the user cancelled or the file couldn't
    /// be read.
    /// </summary>
    /// <param name="bakeRoundedCorners">Pass true when the target is a single rounded-bezel
    /// key screen (e.g. Everest NDK) — see <see cref="CropEditor"/>'s constructor doc.</param>
    public static string? Show(Window owner, string sourcePath, int targetW, int targetH, string title,
        bool bakeRoundedCorners = false)
    {
        var editor = new CropEditor(targetW, targetH, bakeRoundedCorners: bakeRoundedCorners);
        if (!editor.Load(sourcePath)) return null;

        string? resultPath = null;

        var btnOk = new Button
        {
            Content = Loc.Get("ok"), IsDefault = true, Width = 80,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4),
        };
        var btnCancel = new Button
        {
            Content = Loc.Get("cancel"), IsCancel = true, Width = 80,
            Padding = new Thickness(8, 4, 8, 4),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(btnOk);
        buttons.Children.Add(btnCancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = title, Foreground = Brushes.White, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(editor.ViewportBorder);
        root.Children.Add(editor.ControlsPanel);
        root.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
        };

        btnOk.Click += (_, _) => { resultPath = editor.GetResultPath(); dlg.Close(); };
        btnCancel.Click += (_, _) => dlg.Close();

        dlg.ShowDialog();
        return resultPath;
    }
}
