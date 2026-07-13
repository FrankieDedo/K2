using System.Windows.Controls;

namespace K2.App.Models;

/// <summary>One card in the Home tab's device grid (see MainWindow.Home.cs).
/// <see cref="Target"/> is the top-level TabItem to select on click.</summary>
public sealed class HomeDeviceTile(string name, string imagePath, TabItem target)
{
    public string Name { get; } = name;
    public string ImagePath { get; } = imagePath;
    public TabItem Target { get; } = target;
}
