using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.Core;

/// <summary>
/// What the "Modifiers" popup needs to offer per-modifier PICTURES — DisplayPad only (a key
/// with a screen). Null on every other device: the popup then shows the actions alone.
/// <see cref="Images"/> is edited in place (modifier → ready-to-upload PNG).
/// </summary>
public sealed class ModifierImageContext
{
    public Dictionary<string, string> Images { get; set; } = new();
    /// <summary>Pixel size of a key picture.</summary>
    public required int IconSize { get; init; }
    /// <summary>Folder (absolute) where pictures made from a file are written.</summary>
    public required string OutputDir { get; init; }
    /// <summary>Renders an action's default key icon (type, value → PNG path), used to give a
    /// modifier a picture as soon as it gets an action and has none of its own yet.</summary>
    public Func<string, string?, string?>? RenderActionIcon { get; init; }

    public ModifierImageContext Clone() => new()
    {
        Images = new Dictionary<string, string>(Images), IconSize = IconSize, OutputDir = OutputDir,
        RenderActionIcon = RenderActionIcon,
    };
}

/// <summary>
/// The "Modifiers" popup, shared by the action dialog (<see cref="ButtonActionDialog"/>) and the
/// DisplayPad key popup: one row per keyboard modifier (Ctrl / Shift / Alt / Win) with a checkbox
/// that enables it, the action the key runs while that modifier is held (stored in the key's
/// <see cref="TriggerPayload"/>) and — DisplayPad only — the picture the key shows meanwhile
/// (<see cref="KeyIconSpec.ModifierImages"/>). An unchecked row is cleared on OK.
/// Edits the objects it is given in place; callers pass copies so Cancel changes nothing.
/// Fn is not offered: on both Everest keyboards it is a firmware-only layer switch the host
/// never sees.
/// </summary>
public sealed class ModifiersWindow : Window
{
    private readonly TriggerPayload? _trigger;
    private readonly ModifierImageContext? _images;
    private readonly IActionHost? _host;
    private readonly Dictionary<string, CheckBox> _enabled = new();
    /// <summary>Modifiers whose picture was made from their action in this session (and so
    /// follows it when the action changes, instead of being kept as a user choice).</summary>
    private readonly HashSet<string> _autoImage = new();

    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2));

    /// <param name="trigger">The key's trigger payload, or null when its action can't carry
    /// modifier actions (no action, a page, a live tile…) — the Action buttons are then off.</param>
    public ModifiersWindow(TriggerPayload? trigger, ModifierImageContext? images, IActionHost? host)
    {
        _trigger = trigger;
        _images = images;
        _host = host;
        Title = Loc.Get("dp_mod_icons");
        Width = images is null ? 560 : 820;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (TryFindResource("K2WindowStyle") is Style st) Style = st;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = Loc.Get(images is null ? "dp_mod_hint_actions" : "dp_mod_icons_hint"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = HintBrush,
        });
        foreach (var mod in TriggerPayload.Modifiers) root.Children.Add(BuildRow(mod));
        if (trigger is null)
            root.Children.Add(new TextBlock
            {
                Text = Loc.Get("dp_mod_no_actions"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0), Foreground = HintBrush,
            });
        else
            root.Children.Add(new TextBlock
            {
                Text = Loc.Get("trig_mods_note"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0), Foreground = HintBrush,
            });

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = Loc.Get("ok"), MinWidth = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { ClearDisabledRows(); DialogResult = true; Close(); };
        footer.Children.Add(ok);
        footer.Children.Add(new Button { Content = Loc.Get("cancel"), MinWidth = 72, IsCancel = true });
        root.Children.Add(footer);
        Content = root;
    }

    private void ClearDisabledRows()
    {
        foreach (var (mod, chk) in _enabled)
        {
            if (chk.IsChecked == true) continue;
            _trigger?.Mods.RemoveAll(m => string.Equals(m.Modifier, mod, StringComparison.OrdinalIgnoreCase));
            _images?.Images.Remove(mod);
        }
    }

    private UIElement BuildRow(string mod)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        if (_images is not null) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chk = new CheckBox { Content = Loc.Get("trig_mod_" + mod), VerticalAlignment = VerticalAlignment.Center };
        _enabled[mod] = chk;

        Image? img = null;
        Border? thumb = null;
        if (_images is not null)
        {
            img = new Image { Stretch = Stretch.Uniform };
            thumb = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(6), Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3A)), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, Child = img,
            };
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Btn(string text, double left = 6) =>
            new() { Content = text, Padding = new Thickness(10, 2, 10, 2), Height = 26, Margin = new Thickness(left, 0, 0, 0) };

        var action = new Button
        {
            Width = 200, Height = 26, Margin = new Thickness(0), Padding = new Thickness(8, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        var actionText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        action.Content = actionText;
        buttons.Children.Add(action);
        Button? pick = null, emoji = null;
        if (_images is not null)
        {
            pick = Btn(Loc.Get("dp_load_image"));
            emoji = Btn(Loc.Get("dp_emoji_image"));
            buttons.Children.Add(pick);
            buttons.Children.Add(emoji);
        }
        var clear = Btn(Loc.Get("dp_mod_icons_clear"));
        buttons.Children.Add(clear);

        TriggerModAction? Current() => _trigger?.ModAction(mod);

        void Show()
        {
            var a = Current();
            actionText.Text = a is null ? Loc.Get("dp_mod_action") : ActionTypeHelper.ListSummary(a.Type, a.Value);
            action.ToolTip = a is null ? (_trigger is null ? Loc.Get("dp_mod_no_actions") : null) : actionText.Text;
            if (img is not null) img.Source = _images!.Images.TryGetValue(mod, out var p) ? LoadThumb(p) : null;
            bool on = chk.IsChecked == true;
            action.IsEnabled = on && _trigger is not null;
            if (pick is not null) pick.IsEnabled = on;
            if (emoji is not null) emoji.IsEnabled = on;
            clear.IsEnabled = on && (a is not null || _images?.Images.ContainsKey(mod) == true);
            if (thumb is not null) thumb.Opacity = on ? 1.0 : 0.4;
        }

        void SetImage(string? path, bool auto)
        {
            if (_images is null) return;
            if (path is null) _images.Images.Remove(mod); else _images.Images[mod] = path;
            if (auto) _autoImage.Add(mod); else _autoImage.Remove(mod);
        }

        chk.IsChecked = Current() is not null || _images?.Images.ContainsKey(mod) == true;
        chk.Checked += (_, _) => Show();
        chk.Unchecked += (_, _) => Show();

        action.Click += (_, _) =>
        {
            if (_trigger is null) return;
            var cur = Current();
            var dlg = new ButtonActionDialog(0, cur?.Type, cur?.Value, _host, allowTriggers: false) { Owner = this };
            dlg.LblHeader.Text = Loc.Get("trig_mod_" + mod);
            if (dlg.ShowDialog() != true) return;
            bool pageAction = _host?.SupportsPages == true && dlg.ActionType is "dp_folder" or "dp_wheel";
            if (dlg.ActionType != "none" && !TriggerPayload.CanWrap(dlg.ActionType) && !pageAction)
            {
                MessageBox.Show(this, Loc.Get("trig_type_unsupported"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _trigger.Mods.RemoveAll(m => string.Equals(m.Modifier, mod, StringComparison.OrdinalIgnoreCase));
            bool has = !string.IsNullOrEmpty(dlg.ActionType) && dlg.ActionType != "none";
            if (has) _trigger.Mods.Add(new TriggerModAction { Modifier = mod, Type = dlg.ActionType, Value = dlg.ActionValue });

            // No picture of its own yet (or one made from the previous action): show the action's.
            if (_images is not null && (!_images.Images.ContainsKey(mod) || _autoImage.Contains(mod)))
                SetImage(has ? _images.RenderActionIcon?.Invoke(dlg.ActionType, dlg.ActionValue) : null, auto: true);
            Show();
        };
        if (pick is not null)
            pick.Click += (_, _) =>
            {
                var ofd = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("dp_mod_icons_filter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
                if (ofd.ShowDialog(this) != true) return;
                Directory.CreateDirectory(_images!.OutputDir);
                string dest = Path.Combine(_images.OutputDir, $"modicon_{Guid.NewGuid():N}.png");
                if (IconImageGenerator.TryGenerateGalleryIcon(ofd.FileName, _images.IconSize, dest, tintToAccent: false))
                    SetImage(dest, auto: false);
                Show();
            };
        if (emoji is not null)
            emoji.Click += (_, _) =>
            {
                var picker = new EmojiPickerDialog { Owner = this };
                if (picker.ShowDialog() != true || picker.SelectedEmoji is null) return;
                if (EmojiGlyphRenderer.RenderToCache(picker.SelectedEmoji, _images!.IconSize, "K2.DisplayPad\\emoji_icons") is string png)
                    SetImage(png, auto: false);
                Show();
            };
        clear.Click += (_, _) =>
        {
            _trigger?.Mods.RemoveAll(m => string.Equals(m.Modifier, mod, StringComparison.OrdinalIgnoreCase));
            SetImage(null, auto: false);
            Show();
        };
        Show();

        int col = 0;
        Grid.SetColumn(chk, col++); row.Children.Add(chk);
        if (thumb is not null) { Grid.SetColumn(thumb, col++); row.Children.Add(thumb); }
        Grid.SetColumn(buttons, col); row.Children.Add(buttons);
        return row;
    }

    private static ImageSource? LoadThumb(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
