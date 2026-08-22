using System.Windows;

namespace K2.Core;

/// <summary>
/// "Emoji" action panel: pick an emoji from <see cref="EmojiPickerDialog"/> and the key types
/// it into the focused app on press (Unicode injection, see
/// <c>ActionExecutor.SendUnicodeText</c> — SendKeys, which every other text-ish action uses,
/// can't carry a surrogate pair). Available on EVERY device; a display key
/// (DisplayPad tile / Everest Max numpad display key) additionally gets the emoji itself as
/// its picture, auto-generated where the other auto-icons are (see
/// <c>DpKeyConfigDialog.TryAutoGenerateKeyImage</c> / <c>NdkKeyConfigDialog.TryAutoGenerateImage</c>).
///
/// The action value is simply the emoji string itself — no payload wrapper, so it stays
/// readable in the stores and prints as-is in every key-list summary.
/// </summary>
public partial class ButtonActionDialog
{
    private string _emojiValue = "";

    private void LoadEmojiSpec(string value)
    {
        _emojiValue = value ?? "";
        RefreshEmojiPreview();
    }

    private string SaveEmojiSpec() => _emojiValue;

    private void BtnPickEmoji_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EmojiPickerDialog(_emojiValue) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedEmoji is null) return;

        _emojiValue = dlg.SelectedEmoji;
        RefreshEmojiPreview();
    }

    private void RefreshEmojiPreview()
    {
        if (ImgEmojiPreview is null) return;

        ImgEmojiPreview.Source = EmojiGlyphRenderer.TryGetImage(_emojiValue);
        var entry = EmojiCatalog.Find(_emojiValue);
        LblEmojiName.Text = entry?.Name
            ?? (string.IsNullOrEmpty(_emojiValue) ? Loc.Get("emoji_none_selected") : _emojiValue);
    }
}
