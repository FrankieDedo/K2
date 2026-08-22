using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace K2.Core;

/// <summary>
/// Emoji browser: search by name, filter by category, click to pick. Shared by every device
/// — an "emoji" action can be assigned to any key (it types the emoji into the focused app),
/// and on a DISPLAY key (DisplayPad tile / Everest Max numpad display key) the chosen emoji
/// additionally becomes the key's picture, auto-generated via
/// <see cref="EmojiGlyphRenderer.TryGenerateEmojiIcon"/>.
/// </summary>
public partial class EmojiPickerDialog : Window
{
    /// <summary>The emoji the user picked (valid only when <c>ShowDialog</c> returned true).</summary>
    public string? SelectedEmoji { get; private set; }

    /// <summary>Cells per row — fixed rather than measured, because the rows live inside a
    /// virtualizing ListBox (see the XAML note) and the window is non-resizable, so a
    /// reflow-on-resize case doesn't exist.</summary>
    private const int Columns = 11;

    /// <summary>One picker cell. <see cref="Image"/> is a frozen vector
    /// <see cref="DrawingImage"/> built once per emoji and cached by
    /// <see cref="EmojiGlyphRenderer"/>, so re-filtering doesn't re-render anything.</summary>
    private sealed record Cell(string Emoji, string Name, ImageSource? Image);

    private static readonly Dictionary<string, Cell> CellCache = new(StringComparer.Ordinal);

    /// <summary>Guards <see cref="Filter_Changed"/> while the constructor populates the
    /// category combo — the resulting SelectionChanged would otherwise re-run the filter
    /// before the dialog is fully built.</summary>
    private bool _ready;

    public EmojiPickerDialog(string? currentEmoji = null)
    {
        InitializeComponent();

        CbCategory.Items.Add(new ComboBoxItem { Content = Loc.Get("emoji_category_all"), Tag = "" });
        foreach (var group in EmojiCatalog.Groups)
            CbCategory.Items.Add(new ComboBoxItem { Content = EmojiCatalog.LocalizedGroup(group), Tag = group });
        CbCategory.SelectedIndex = 0;

        _ready = true;
        ApplyFilter();
        Select(currentEmoji);

        Loaded += (_, _) => TxtSearch.Focus();
    }

    // ---- filtering ------------------------------------------------------

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    private void ApplyFilter()
    {
        string group = CbCategory.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";
        var matches = EmojiCatalog.Search(group, TxtSearch.Text).ToList();

        var rows = new List<List<Cell>>();
        for (int i = 0; i < matches.Count; i += Columns)
            rows.Add(matches.Skip(i).Take(Columns).Select(ToCell).ToList());

        LstRows.ItemsSource   = rows;
        LblNoResults.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Cell ToCell(EmojiCatalog.EmojiEntry entry)
    {
        if (CellCache.TryGetValue(entry.Emoji, out var cached)) return cached;
        var cell = new Cell(entry.Emoji, entry.Name, EmojiGlyphRenderer.TryGetImage(entry.Emoji));
        CellCache[entry.Emoji] = cell;
        return cell;
    }

    // ---- selection ------------------------------------------------------

    private void EmojiCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Cell cell }) Select(cell.Emoji);
    }

    private void Select(string? emoji)
    {
        var entry = EmojiCatalog.Find(emoji);
        if (entry is null)
        {
            SelectedEmoji     = null;
            ImgSelected.Source = null;
            LblSelected.Text   = Loc.Get("emoji_none_selected");
            BtnOk.IsEnabled    = false;
            return;
        }

        SelectedEmoji      = entry.Emoji;
        ImgSelected.Source = EmojiGlyphRenderer.TryGetImage(entry.Emoji);
        LblSelected.Text   = entry.Name;
        BtnOk.IsEnabled    = true;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEmoji is null) return;
        DialogResult = true;
        Close();
    }
}
