using System.Windows;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Youtube" panel — just a chat-message textbox (the only
/// real action, see <see cref="Services.YouTubeBridge"/>'s remarks) plus a "Manage" button
/// opening <see cref="YouTubeSettingsWindow"/>. Load/save is inlined directly in
/// <c>ButtonActionDialog.xaml.cs</c> (a plain textbox needs no dedicated Load/Save helpers).
/// </summary>
public partial class ButtonActionDialog
{
    private void BtnYoutubeSettings_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new YouTubeSettingsWindow { Owner = this };
        wnd.ShowDialog();
    }
}
