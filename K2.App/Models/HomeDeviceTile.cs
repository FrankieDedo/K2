using System.Windows.Controls;

namespace K2.App.Models;

/// <summary>One card in the Home tab's device grid (see MainWindow.Home.cs).
/// <see cref="Target"/> is the top-level TabItem to select on click.
/// <see cref="ImageWidth"/>/<see cref="ImageHeight"/> default to the card artwork's
/// normal size (286x195); per-device callers may pass a larger size (same aspect
/// ratio) to make that tile's artwork stand out.</summary>
public sealed class HomeDeviceTile(string name, string imagePath, TabItem target,
    double imageWidth = 286, double imageHeight = 195)
{
    public string Name { get; } = name;
    public string ImagePath { get; } = imagePath;
    public TabItem Target { get; } = target;
    public double ImageWidth { get; } = imageWidth;
    public double ImageHeight { get; } = imageHeight;
}
