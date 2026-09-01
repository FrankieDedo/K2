using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace K2.Core;

/// <summary>
/// Shared renderer for the "how to set this up" guides shown in the service settings
/// windows (Discord / Twitch / Spotify / YouTube / OBS). Turns a plain localized,
/// multi-line string into a read-only <see cref="FlowDocument"/> whose text is
/// selectable/copyable and whose external web addresses become clickable links — a
/// plain WPF <c>TextBlock</c> can do neither. Host the document in a transparent,
/// borderless, read-only <c>RichTextBox</c> so it still reads as static help text.
/// </summary>
public static class SetupGuide
{
    /// <summary>External web addresses inside a guide (with or without a scheme).
    /// Loopback callback addresses (<c>localhost</c> / <c>127.0.0.1</c>) are deliberately
    /// NOT matched: those are values to paste into a developer portal, not pages to open —
    /// a link there would only send the user to a dead page.</summary>
    private static readonly Regex LinkPattern = new(
        @"(?:https?://)?(?![\w.-]*(?:localhost|127\.0\.0\.1))(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}(?:/[^\s""'<>)]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly char[] TrailingPunctuation = { '.', ',', ';', ':', ')' };

    /// <summary>Builds the guide document: one paragraph per line, every external address
    /// turned into a clickable <see cref="Hyperlink"/> that opens in the default browser.
    /// <paramref name="themeSource"/> only supplies the accent brush for the link colour;
    /// a missing brush falls back to a plain blue rather than taking the window down.</summary>
    public static FlowDocument BuildDocument(string text, FrameworkElement themeSource)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        var linkBrush = themeSource?.TryFindResource("K2AccentBrush") as Brush ?? Brushes.CornflowerBlue;

        foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            int last = 0;

            foreach (Match match in LinkPattern.Matches(line))
            {
                string shown = match.Value.TrimEnd(TrailingPunctuation);
                if (shown.Length == 0) continue;
                int start = match.Index;

                if (start > last) paragraph.Inlines.Add(new Run(line[last..start]));

                string url = shown.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? shown : "https://" + shown;
                var link = new Hyperlink(new Run(shown)) { NavigateUri = new Uri(url), Foreground = linkBrush };
                link.RequestNavigate += OnRequestNavigate;
                paragraph.Inlines.Add(link);
                last = start + shown.Length;
            }

            if (last < line.Length) paragraph.Inlines.Add(new Run(line[last..]));
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); }
        catch { /* no default browser — nothing useful to do here */ }
        e.Handled = true;
    }
}
