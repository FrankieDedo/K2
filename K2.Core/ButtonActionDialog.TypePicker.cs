using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.Core;

/// <summary>
/// Card-based replacement for CbType/CbComboValue's native dropdowns: a category grid ->
/// action grid -> (for "combo" types only) sub-action grid, all 4 columns, same card style.
/// A 2-3 crumb breadcrumb mirrors the current selection tree; clicking any crumb reopens the
/// overlay at that level so a step can be changed without re-walking the whole tree, and
/// picking a category/action auto-advances to the next level (cascading). CbType/CbComboValue
/// stay the single source of truth (Collapsed in the tree) — every panel/handler in
/// ButtonActionDialog.xaml.cs / .Simple.cs keeps working unchanged. An action with no type
/// yet (a freshly-added key) auto-opens straight into the category grid on dialog-open —
/// there's nothing useful to show below an empty selection anyway.
/// </summary>
public partial class ButtonActionDialog
{
    private sealed class CategoryCard
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Glyph { get; init; } = "";
    }

    /// <summary>One action card. Exactly one of the three visuals shows: a bitmap logo
    /// (<see cref="IconImageUri"/>, the two multi-color PNGs), a single-path vector logo
    /// (<see cref="IconPathData"/>), or the emoji fallback (<see cref="Glyph"/>).</summary>
    private sealed class ActionCard
    {
        public string Tag { get; init; } = "";
        public string Name { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string IconPathData { get; init; } = "";
        public string IconColor { get; init; } = "";
        public string IconImageUri { get; init; } = "";
        public double IconWidth { get; init; } = 20;
        public double IconHeight { get; init; } = 20;

        public Visibility ImageVisibility => IconImageUri.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IconVisibility => IconImageUri.Length == 0 && IconPathData.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GlyphVisibility => IconImageUri.Length == 0 && IconPathData.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class SubActionCard
    {
        public string Value { get; init; } = "";
        public string Name { get; init; } = "";
        public string Glyph { get; init; } = "";
    }

    /// <summary>Category key -> ordered action tags. Tags not present in CbType.Items at
    /// runtime (e.g. dp_folder/dp_emojibrowser on a host with no page concept) are skipped
    /// when the action grid is built.</summary>
    private static readonly (string Key, string Glyph, string[] Tags)[] PickerCategories =
    {
        ("system",     "🖥",  new[] { "none", "disable", "url", "exec", "folder", "oscmd", "command", "audiodevice" }),
        ("navigation", "🧭",  new[] { "dp_folder", "profile", "browser" }),
        ("input",      "⌨",  new[] { "keys", "hotkeyswitch", "mouse", "media", "multi", "macro" }),
        ("content",    "📝", new[] { "text", "emoji", "dp_emojibrowser" }),
        ("live",       "📊", new[] { "dp_clock", "dp_sysmon", "dp_speedtest" }),
        ("apps",       "🧩", new[] { "googlehome", "adobe", "davinci", "zoom", "obs", "twitch", "spotify", "discord", "youtube", "pyscript" }),
    };

    /// <summary>Per-type emoji fallback for tags with no real vector logo in
    /// <see cref="ButtonActionDialogIcons"/> — shown on the action card + breadcrumb action
    /// crumb whenever <see cref="ButtonActionDialogIcons.IconsByTag"/> has no entry for the tag.</summary>
    private static readonly Dictionary<string, string> ActionGlyphs = new()
    {
        ["disable"] = "🚫", ["url"] = "🔗", ["exec"] = "💻", ["folder"] = "📁",
        ["oscmd"] = "🖥",
        ["profile"] = "👤", ["browser"] = "🌐",
        ["keys"] = "⌨", ["hotkeyswitch"] = "🔁", ["mouse"] = "🖱", ["media"] = "🎵", ["multi"] = "📋", ["macro"] = "⏱",
        ["text"] = "📝", ["emoji"] = "😀", ["dp_emojibrowser"] = "🙂",
        ["dp_clock"] = "🕐", ["dp_sysmon"] = "📊", ["dp_speedtest"] = "🚀",
    };

    /// <summary>Types whose value is picked from a fixed/dynamic list (CbComboValue) rather
    /// than typed free-hand — these get the 3rd breadcrumb crumb + sub-action grid. Matches
    /// the "combo" set UpdatePanels() already switches ComboPanel on for.</summary>
    private static readonly HashSet<string> ComboTags = new()
        { "oscmd", "media", "mouse", "macro", "googlehome", "obs", "twitch", "spotify", "discord", "audiodevice",
          "dp_clock", "dp_sysmon", "dp_speedtest" };

    /// <summary>The two CbType tags whose loc key doesn't follow the plain "act_"+tag
    /// pattern the rest of the list uses (see the ComboBoxItem list in ButtonActionDialog.xaml) —
    /// missing this mapping is what made these two cards show their raw "[act_dp_folder]"/
    /// "[act_dp_emojibrowser]" loc-miss placeholder instead of a real name.</summary>
    private static string LocKeyForTag(string tag) => tag switch
    {
        "dp_folder" => "act_page",
        "dp_emojibrowser" => "act_emojibrowser",
        _ => "act_" + tag,
    };

    private static string CategoryKeyOf(string tag) =>
        PickerCategories.FirstOrDefault(c => c.Tags.Contains(tag)).Key ?? "system";

    /// <summary>The card visual for a tag: a bitmap logo, a vector logo, or the emoji
    /// fallback (exactly one is non-empty). dp_folder is left with an empty Color in
    /// ButtonActionDialogIcons because the actual DisplayPad page-key tile (IconImageGenerator.cs)
    /// tracks the live accent color, but this picker card is not a display-key icon — it stays
    /// a fixed white here regardless of Settings > Accent color.</summary>
    private static (string Glyph, string PathData, string Color, string ImageUri, double W, double H) IconFor(string tag)
    {
        if (ButtonActionDialogIcons.ImagesByTag.TryGetValue(tag, out var img))
            return ("", "", "", img.PackUri, img.Width, img.Height);

        if (ButtonActionDialogIcons.IconsByTag.TryGetValue(tag, out var icon))
        {
            string color = icon.Color.Length > 0 ? icon.Color : "#FFFFFF";
            return ("", icon.PathData, color, "", icon.Width, icon.Height);
        }

        return (ActionGlyphs.TryGetValue(tag, out var g) ? g : "⚙", "", "", "", 20, 20);
    }

    /// <summary>Swaps the dialog's content row between the config panels and the inline
    /// picker — the picker is NOT a second popup, it takes over the same area (breadcrumb
    /// above and Save/Cancel below stay put).</summary>
    private void OpenOverlay()
    {
        LblActionType.Visibility = Visibility.Collapsed;
        PnlBreadcrumb.Visibility = Visibility.Collapsed;
        ConfigPanels.Visibility = Visibility.Collapsed;
        PickerPanel.Visibility = Visibility.Visible;
    }

    private void CloseOverlay()
    {
        PickerPanel.Visibility = Visibility.Collapsed;
        LblActionType.Visibility = Visibility.Visible;
        PnlBreadcrumb.Visibility = Visibility.Visible;
        ConfigPanels.Visibility = Visibility.Visible;
    }

    private void BtnCrumbCategory_Click(object sender, RoutedEventArgs e)
    {
        ShowCategoryPicker();
        OpenOverlay();
    }

    private void BtnCrumbAction_Click(object sender, RoutedEventArgs e)
    {
        ShowActionPicker(CategoryKeyOf(CurrentTag()));
        OpenOverlay();
    }

    private void BtnCrumbSubAction_Click(object sender, RoutedEventArgs e)
    {
        ShowSubActionPicker();
        OpenOverlay();
    }

    private void ShowCategoryPicker()
    {
        if (IcPickerCategories.ItemsSource is null)
        {
            IcPickerCategories.ItemsSource = PickerCategories
                .Select(c => new CategoryCard { Key = c.Key, Glyph = c.Glyph, Name = Loc.Get("cat_" + c.Key) })
                .ToList();
        }

        LblPickerTitle.Text = Loc.Get("picker_pick_category");
        BtnPickerBack.Visibility = Visibility.Collapsed;
        IcPickerCategories.Visibility = Visibility.Visible;
        IcPickerActions.Visibility = Visibility.Collapsed;
        IcPickerSubActions.Visibility = Visibility.Collapsed;
    }

    private void ShowActionPicker(string categoryKey)
    {
        var category = PickerCategories.FirstOrDefault(c => c.Key == categoryKey);
        if (category.Tags is null) return;

        var availableTags = CbType.Items.OfType<ComboBoxItem>().Select(i => (string?)i.Tag).ToHashSet();
        IcPickerActions.ItemsSource = category.Tags
            .Where(availableTags.Contains)
            .Select(tag =>
            {
                var (glyph, path, color, imageUri, w, h) = IconFor(tag);
                return new ActionCard
                {
                    Tag = tag,
                    Name = Loc.Get(LocKeyForTag(tag)),
                    Glyph = glyph,
                    IconPathData = path,
                    IconColor = color,
                    IconImageUri = imageUri,
                    IconWidth = w,
                    IconHeight = h,
                };
            })
            .ToList();

        LblPickerTitle.Text = Loc.Get("cat_" + categoryKey);
        BtnPickerBack.Visibility = Visibility.Visible;
        IcPickerCategories.Visibility = Visibility.Collapsed;
        IcPickerActions.Visibility = Visibility.Visible;
        IcPickerSubActions.Visibility = Visibility.Collapsed;
    }

    /// <summary>Built straight from CbComboValue.Items (already populated by
    /// EnsureComboPanel/PopulateCombo for the current tag, including the dynamic macro/
    /// googlehome/audiodevice lists) rather than re-deriving them — one source of truth,
    /// no risk of the two lists drifting apart.</summary>
    private void ShowSubActionPicker()
    {
        IcPickerSubActions.ItemsSource = CbComboValue.Items.OfType<ComboBoxItem>()
            .Select(i => new SubActionCard { Value = (string?)i.Tag ?? "", Name = (string?)i.Content ?? "" })
            .ToList();

        LblPickerTitle.Text = Loc.Get(LocKeyForTag(CurrentTag()));
        BtnPickerBack.Visibility = Visibility.Visible;
        IcPickerCategories.Visibility = Visibility.Collapsed;
        IcPickerActions.Visibility = Visibility.Collapsed;
        IcPickerSubActions.Visibility = Visibility.Visible;
    }

    private void CategoryCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        ShowActionPicker(key);
    }

    private void ActionCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        SetType(tag);

        if (ComboTags.Contains(tag)) ShowSubActionPicker();
        else CloseOverlay();
    }

    private void SubActionCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        var match = CbComboValue.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, value, System.StringComparison.Ordinal));
        if (match is not null) CbComboValue.SelectedItem = match;
        CloseOverlay();
    }

    private void BtnPickerBack_Click(object sender, RoutedEventArgs e)
    {
        if (IcPickerSubActions.Visibility == Visibility.Visible)
            ShowActionPicker(CategoryKeyOf(CurrentTag()));
        else
            ShowCategoryPicker();
    }

    private void BtnPickerClose_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    /// <summary>Refreshes the breadcrumb's category + action crumbs (and the sub-action
    /// crumb's visibility) — called whenever CbType's selection changes, including
    /// programmatically from SetType. Also auto-opens the category picker the very first time
    /// it's called with an unset ("none") action, straight into the same dialog/popup —
    /// nothing useful to configure below an empty selection anyway.</summary>
    private void UpdateBreadcrumb(string tag)
    {
        string categoryKey = CategoryKeyOf(tag);
        var category = PickerCategories.FirstOrDefault(c => c.Key == categoryKey);
        TxtCrumbCategoryGlyph.Text = category.Glyph ?? "";
        TxtCrumbCategoryName.Text = Loc.Get("cat_" + categoryKey);

        var (glyph, path, color, imageUri, w, h) = IconFor(tag);
        bool hasImage = imageUri.Length > 0;
        bool hasPath = !hasImage && path.Length > 0;

        TxtCrumbActionGlyph.Text = glyph;
        TxtCrumbActionGlyph.Visibility = hasImage || hasPath ? Visibility.Collapsed : Visibility.Visible;

        // The crumb is ~40 px tall, so every icon is drawn at 0.8x its card size — a
        // wide wordmark (Zoom) keeps its aspect ratio instead of being squeezed square.
        PathCrumbActionIcon.Data = hasPath ? Geometry.Parse(path) : null;
        PathCrumbActionIcon.Fill = hasPath ? (Brush)new BrushConverter().ConvertFromString(color)! : null;
        PathCrumbActionIcon.Width = w * 0.8;
        PathCrumbActionIcon.Height = h * 0.8;
        PathCrumbActionIcon.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;

        ImgCrumbActionIcon.Source = hasImage ? new BitmapImage(new System.Uri(imageUri)) : null;
        ImgCrumbActionIcon.Width = w * 0.8;
        ImgCrumbActionIcon.Height = h * 0.8;
        ImgCrumbActionIcon.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        TxtCrumbActionName.Text = Loc.Get(LocKeyForTag(tag));

        bool needsSubAction = ComboTags.Contains(tag);
        TxtCrumbChevron2.Visibility = needsSubAction ? Visibility.Visible : Visibility.Collapsed;
        BtnCrumbSubAction.Visibility = needsSubAction ? Visibility.Visible : Visibility.Collapsed;
        if (!needsSubAction) TxtCrumbSubActionName.Text = "";

        if (tag == "none" && PickerPanel.Visibility != Visibility.Visible)
        {
            ShowCategoryPicker();
            OpenOverlay();
        }
    }

    /// <summary>Keeps the 3rd breadcrumb crumb's text in sync with CbComboValue's selection —
    /// called from CbComboValue_SelectionChanged (ButtonActionDialog.Simple.cs), so it updates
    /// both from card picks and from PopulateCombo's own default-selection.</summary>
    private void UpdateSubActionCrumb()
    {
        TxtCrumbSubActionName.Text = CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Content ?? "" : "";
    }
}
