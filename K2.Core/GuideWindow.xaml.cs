// GuideWindow.xaml.cs — popup showing the guide for whatever was open when the
// "Guide" button was pressed (a device section, or a level of the action
// picker). Content comes from GuideContent, rendered here from a small markdown
// subset: "# "/"## " headings, "- " bullets (indented continuation), **bold**,
// and a whole-line image "![caption](file.png)" resolved against
// K2.Core Assets/Guides/ (cropped UI screenshots — see that folder's README).

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.Core;

public partial class GuideWindow : Window
{
    /// <summary>Opens the guide identified by <paramref name="guideKey"/> (e.g.
    /// "everest:keybinding" or "picker:act:keys"). <paramref name="fallbackHeading"/>
    /// is used as the window heading when the guide body has no leading "# " title
    /// (or no guide exists for the key yet).</summary>
    public GuideWindow(string guideKey, string fallbackHeading)
        : this(new[] { guideKey }, fallbackHeading) { }

    /// <summary>Opens a guide built from several blocks concatenated in order —
    /// used where a base guide gets a device-specific addendum (e.g. profiles +
    /// DisplayPad dedicated profiles). Missing blocks are skipped; the first
    /// block's leading "# " title becomes the window heading, later "# " lines
    /// render as in-body headings.</summary>
    public GuideWindow(string[] guideKeys, string fallbackHeading)
    {
        InitializeComponent();

        var parts = guideKeys.Select(GuideContent.Get).Where(b => b is not null).ToList();
        string? body = parts.Count > 0 ? string.Join("\n\n", parts) : null;
        string heading = fallbackHeading;

        if (body is null)
        {
            PnlBody.Children.Add(MakeParagraph(Loc.Get("guide_none")));
        }
        else
        {
            var lines = body.Replace("\r\n", "\n").Split('\n');
            int start = 0;
            if (lines.Length > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal))
            {
                heading = lines[0][2..].Trim();
                start = 1;
            }
            Render(lines, start);
        }

        TxtHeading.Text = heading;
        Title = Loc.Get("guide_title") + " — " + heading;
    }

    // ── markdown-subset renderer ─────────────────────────────────────────
    private void Render(string[] lines, int start)
    {
        var para = new System.Collections.Generic.List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            PnlBody.Children.Add(MakeParagraph(string.Join(" ", para)));
            para.Clear();
        }

        for (int i = start; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();

            if (line.Length == 0) { FlushPara(); continue; }

            if (line.StartsWith("![", StringComparison.Ordinal) && line.EndsWith(")", StringComparison.Ordinal)
                && line.Contains("](", StringComparison.Ordinal))
            {
                FlushPara();
                int bar = line.IndexOf("](", StringComparison.Ordinal);
                string alt = line[2..bar];
                string name = line[(bar + 2)..^1].Trim();
                PnlBody.Children.Add(MakeImage(alt, name));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushPara();
                PnlBody.Children.Add(MakeHeading(line[3..].Trim()));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushPara();
                PnlBody.Children.Add(MakeHeading(line[2..].Trim()));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal))
            {
                // "- " starts a bullet; a following indented line continues it.
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    FlushPara();
                    PnlBody.Children.Add(MakeBullet(line[2..].Trim()));
                }
                else if (PnlBody.Children.Count > 0 && PnlBody.Children[^1] is Grid g
                         && g.Children.Count == 2 && g.Children[1] is TextBlock tb)
                {
                    tb.Inlines.Add(" ");
                    AppendInlines(tb, line.Trim());
                }
                else
                {
                    para.Add(line.Trim());
                }
            }
            else
            {
                para.Add(line.Trim());
            }
        }
        FlushPara();
    }

    /// <summary>A whole-line "![caption](file.png)" — a cropped UI screenshot from
    /// K2.Core Assets/Guides/. Degrades to the caption text if the asset is
    /// missing so a not-yet-captured image never breaks the guide.</summary>
    private static UIElement MakeImage(string alt, string name)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri($"pack://application:,,,/K2.Core;component/Assets/Guides/{name}", UriKind.Absolute);
            bmp.EndInit();

            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = 540,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var border = new Border
            {
                Child = img,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                SnapsToDevicePixels = true,
            };
            if (Application.Current?.TryFindResource("K2BorderBrush") is Brush bb) border.BorderBrush = bb;
            panel.Children.Add(border);

            if (!string.IsNullOrWhiteSpace(alt))
            {
                var cap = new TextBlock
                {
                    Text = alt,
                    FontSize = 11,
                    Margin = new Thickness(2, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                if (Application.Current?.TryFindResource("K2TextMutedBrush") is Brush mb) cap.Foreground = mb;
                panel.Children.Add(cap);
            }
        }
        catch
        {
            panel.Children.Add(MakeParagraph(string.IsNullOrWhiteSpace(alt) ? $"[{name}]" : alt));
        }
        return panel;
    }

    private static TextBlock MakeHeading(string text) => new()
    {
        Text = text,
        FontSize = 13.5,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 14, 0, 6),
    };

    private static TextBlock MakeParagraph(string text)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            LineHeight = 19,
        };
        AppendInlines(tb, text);
        return tb;
    }

    private static Grid MakeBullet(string text)
    {
        var g = new Grid { Margin = new Thickness(2, 0, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new TextBlock { Text = "•" };
        if (Application.Current?.TryFindResource("K2TextMutedBrush") is Brush b) dot.Foreground = b;
        Grid.SetColumn(dot, 0);

        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        AppendInlines(tb, text);
        Grid.SetColumn(tb, 1);

        g.Children.Add(dot);
        g.Children.Add(tb);
        return g;
    }

    /// <summary>Appends <paramref name="text"/> to <paramref name="tb"/>, turning
    /// **…** spans into bold runs.</summary>
    private static void AppendInlines(TextBlock tb, string text)
    {
        int idx = 0;
        while (idx < text.Length)
        {
            int open = text.IndexOf("**", idx, StringComparison.Ordinal);
            if (open < 0)
            {
                tb.Inlines.Add(new Run(text[idx..]));
                break;
            }
            if (open > idx) tb.Inlines.Add(new Run(text[idx..open]));

            int close = text.IndexOf("**", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                tb.Inlines.Add(new Run(text[open..]));
                break;
            }
            tb.Inlines.Add(new Run(text[(open + 2)..close]) { FontWeight = FontWeights.SemiBold });
            idx = close + 2;
        }
    }
}
