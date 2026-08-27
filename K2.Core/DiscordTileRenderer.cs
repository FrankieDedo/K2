using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace K2.Core;

/// <summary>
/// The two picture tiles of the DisplayPad's Discord voice page: one participant circle (avatar,
/// speaking ring, muted/deafened badge) and the server icon.
///
/// <para>
/// Same split from <see cref="IconImageGenerator"/> as <see cref="LiveTileRenderer"/>: these
/// depend on state that changes while the key is on screen (who is talking, who muted
/// themselves), so they are re-rendered over a single per-slot file instead of being cached
/// under a fingerprinted name. The LOOK is borrowed from that class all the same — same rounded
/// black tile and same bottom-strip caption — so the voice page sits next to the profile's own
/// keys without looking foreign.
/// </para>
/// </summary>
public static class DiscordTileRenderer
{
    /// <summary>Discord's own "someone is talking" green, so the ring reads as the same signal
    /// as the one around the avatar in the client.</summary>
    private static readonly Color SpeakingGreen = Color.FromArgb(59, 165, 93);

    /// <summary>Discord's muted red, used for the mute/deafen badge.</summary>
    private static readonly Color MutedRed = Color.FromArgb(237, 66, 69);

    /// <summary>
    /// One participant tile: their avatar in a circle, the name under it, a green ring while
    /// they are talking and a red badge when they are muted (or deafened).
    /// </summary>
    /// <param name="avatarPath">Downloaded avatar, or null to fall back to an initials circle.</param>
    /// <param name="self">Local user — drawn with an accent-colored outline so it is obvious
    /// which circle is "you" even before reading the name.</param>
    public static bool TryRenderParticipant(string? avatarPath, string name, bool speaking, bool mute, bool deaf,
                                            bool self, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                // No caption under the circle (user request): the face — or the initials standing
                // in for it — IS the label, so the picture takes the whole tile instead of sharing
                // it with a name that was unreadable at 102 px anyway.
                float d = size * 0.72f;
                var circle = new RectangleF((size - d) / 2f, (size - d) / 2f, d, d);

                DrawAvatar(g, circle, avatarPath, name);

                // Speaking wins the ring; "you" keeps a thinner accent outline otherwise, so the
                // two never fight over the same pixels.
                if (speaking) Ring(g, circle, SpeakingGreen, size * 0.055f);
                else if (self) Ring(g, circle, IconImageGenerator.TileAccent, size * 0.030f);

                if (deaf || mute) DrawMutedBadge(g, circle, size, deaf);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>The server tile (key 1): the server's picture, and nothing else — no name under
    /// it and no initials standing in for a missing one (user request). Returns false when there
    /// is no picture to draw, which leaves the key blank rather than showing a placeholder.</summary>
    public static bool TryRenderServer(string? iconPath, int size, string outputPngPath)
    {
        if (iconPath is null || !File.Exists(iconPath)) return false;
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
                DrawAvatar(g, TileCircle(size), iconPath, "");
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Group picture for a call that has no server behind it — a DM or group call. Discord's RPC
    /// reports no icon for those channels (<c>GET_CHANNEL</c> carries no <c>icon</c> field, and
    /// there is no guild to ask), so the tile is BUILT from the members' avatars, the same way the
    /// client draws a group's picture: up to four faces packed into the circle, one big one when
    /// the call is just the two of you. Like the server tile, it carries no text.
    /// </summary>
    /// <param name="avatarPaths">Members' avatars, already downloaded; nulls are skipped.</param>
    public static bool TryRenderGroup(IReadOnlyList<string?> avatarPaths, int size, string outputPngPath)
    {
        var faces = avatarPaths.Where(p => p is not null && File.Exists(p)).Take(4).ToList();
        if (faces.Count == 0) return false;

        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                var area = TileCircle(size);
                float d = area.Width;

                // One face fills the circle; two sit side by side; three or four go on a 2×2 grid
                // (the last cell stays empty for three, which reads as "and one more" rather than
                // as a broken layout).
                float f = faces.Count == 1 ? d : d * 0.56f;
                var spots = faces.Count switch
                {
                    1 => new[] { new PointF(area.Left, area.Top) },
                    2 => new[] { new PointF(area.Left, area.Top + (d - f) / 2f),
                                 new PointF(area.Right - f, area.Top + (d - f) / 2f) },
                    _ => new[] { new PointF(area.Left, area.Top),
                                 new PointF(area.Right - f, area.Top),
                                 new PointF(area.Left, area.Bottom - f),
                                 new PointF(area.Right - f, area.Bottom - f) },
                };

                for (int i = 0; i < faces.Count && i < spots.Length; i++)
                {
                    var cell = new RectangleF(spots[i].X, spots[i].Y, f, f);
                    // A thin cut-out in the tile color separates faces that touch.
                    if (faces.Count > 1)
                    {
                        var halo = cell;
                        halo.Inflate(size * 0.012f, size * 0.012f);
                        using var back = new SolidBrush(IconImageGenerator.TileBackground);
                        g.FillEllipse(back, halo);
                    }
                    DrawAvatar(g, cell, faces[i], "");
                }
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// One of the voice page's control keys, drawn from the artwork shipped with K2
    /// (<c>Assets/DiscordIcons/*.png</c>, supplied by the user) rather than from a generated glyph:
    /// mic on/off, audio on/off, push-to-talk, webcam, disconnect. The pictures already carry their
    /// own colors — red for the "off"/hang-up states, gray for the neutral ones — so nothing is
    /// tinted here.
    /// </summary>
    /// <param name="iconName">File name without extension, e.g. <c>mic_off</c>.</param>
    /// <param name="caption">Label drawn under the icon, in the shared bottom strip every other
    /// DisplayPad tile uses. Empty for a tile with no text.</param>
    /// <param name="highlight">Paints the tile <see cref="PressGreen"/> instead of black — the
    /// push-to-talk key while it is held down.</param>
    public static bool TryRenderControl(string iconName, string caption, bool highlight, int size,
                                        string outputPngPath)
    {
        var art = LoadIcon(iconName);
        if (art is null) return false;
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size, highlight ? PressGreen : (Color?)null))
            {
                // With a caption the art sits in the upper part, same split as a captioned action
                // tile; without one it is simply centered.
                float d = size * (caption.Length > 0 ? 0.52f : 0.62f);
                float top = caption.Length > 0 ? size * 0.32f - d / 2f : (size - d) / 2f;
                g.DrawImage(art, new RectangleF((size - d) / 2f, top, d, d));
                if (caption.Length > 0) IconImageGenerator.DrawCaption(g, size, caption);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>Background of the push-to-talk key while it is held down.</summary>
    private static readonly Color PressGreen = Color.FromArgb(0x2D, 0xC7, 0x70);

    private static readonly Dictionary<string, Image?> _iconCache = new();

    /// <summary>Decodes one bundled icon (once) out of the assembly's WPF resources. Returns null
    /// when it can't be loaded — the caller then leaves the key blank rather than guessing.</summary>
    private static Image? LoadIcon(string name)
    {
        lock (_iconCache)
        {
            if (_iconCache.TryGetValue(name, out var cached)) return cached;

            Image? image = null;
            try
            {
                var uri = new Uri($"pack://application:,,,/K2.Core;component/Assets/DiscordIcons/{name}.png");
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info is not null)
                {
                    using var stream = info.Stream;
                    // Copied into memory first: Image.FromStream keeps the stream alive for the
                    // lifetime of the bitmap, and the resource stream is not seekable enough for
                    // the repeated draws this cache exists for.
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                }
            }
            catch { image = null; }

            _iconCache[name] = image;
            return image;
        }
    }

    // ---------------------------------------------------------------- pieces

    /// <summary>Picture clipped into the circle, or the first letters of the name on a dark
    /// disc when there is no picture (yet — the avatar download is asynchronous).</summary>
    private static void DrawAvatar(Graphics g, RectangleF circle, string? imagePath, string name)
    {
        if (imagePath is not null && File.Exists(imagePath))
        {
            try
            {
                using var src = Image.FromFile(imagePath);
                var saved = g.Save();
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(circle);
                    g.SetClip(clip);
                    g.DrawImage(src, circle);
                }
                g.Restore(saved);
                return;
            }
            catch (Exception ex)
            {
                // Falls through to the initials/gray disc. Logged because a picture that IS on disk
                // and still won't draw means a format System.Drawing can't read (Discord's CDN
                // hands out .webp unless asked otherwise — see DiscordAvatarCache.AsPng).
                Services.DiscordBridge.Log?.Invoke($"[Discord] tile picture unreadable: {ex.Message}");
            }
        }

        using var disc = new SolidBrush(Color.FromArgb(60, 63, 70));
        g.FillEllipse(disc, circle);
        string initials = Initials(name);
        if (initials.Length == 0) return;
        using var font = IconImageGenerator.CaptionFont(circle.Height * 0.42f);
        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(initials, font, brush, circle, format);
    }

    private static void Ring(Graphics g, RectangleF circle, Color color, float width)
    {
        using var pen = new Pen(color, width);
        // Inflated by half the stroke so the ring sits AROUND the picture instead of eating
        // into the face.
        var r = circle;
        r.Inflate(width / 2f, width / 2f);
        g.DrawEllipse(pen, r);
    }

    /// <summary>Red disc with a white slash at the bottom-right of the circle — muted, or
    /// deafened (drawn slightly larger, since it implies muted too).</summary>
    private static void DrawMutedBadge(Graphics g, RectangleF circle, int size, bool deaf)
    {
        float d = size * (deaf ? 0.30f : 0.26f);
        var badge = new RectangleF(circle.Right - d * 0.75f, circle.Bottom - d * 0.75f, d, d);

        using (var back = new SolidBrush(IconImageGenerator.TileBackground))
        {
            var halo = badge;
            halo.Inflate(size * 0.02f, size * 0.02f);
            g.FillEllipse(back, halo);
        }
        using (var brush = new SolidBrush(MutedRed))
            g.FillEllipse(brush, badge);

        using var pen = new Pen(Color.White, d * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float inset = d * 0.28f;
        g.DrawLine(pen, badge.Left + inset, badge.Bottom - inset, badge.Right - inset, badge.Top + inset);
    }

    /// <summary>Up to two letters standing in for a missing picture ("Fra Dedo" → "FD").</summary>
    private static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        string first = words[0][..1].ToUpperInvariant();
        return words.Length == 1 ? first : first + words[1][..1].ToUpperInvariant();
    }

    /// <summary>The picture area of a text-less tile: centered, as big as the rounded tile lets it
    /// be without touching the corners.</summary>
    private static RectangleF TileCircle(int size)
    {
        float d = size * 0.80f;
        return new RectangleF((size - d) / 2f, (size - d) / 2f, d, d);
    }

    private static Graphics NewGraphics(Bitmap canvas, int size, Color? background = null)
    {
        var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(IconImageGenerator.TileBackground);
        IconImageGenerator.ClipToRoundedTile(g, size);
        // A colored background is painted INSIDE the rounded clip, never with Clear(): the corners
        // must keep the tile color, or the key would show a green square with black corners.
        if (background is Color fill)
        {
            using var brush = new SolidBrush(fill);
            g.FillRectangle(brush, 0, 0, size, size);
        }
        return g;
    }

    private static bool Save(Bitmap canvas, string outputPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
        // The page overwrites the same per-slot file on every state change, and the previous
        // render may still be memory-mapped by a pending upload — write through a buffer.
        using var ms = new MemoryStream();
        canvas.Save(ms, ImageFormat.Png);
        File.WriteAllBytes(outputPngPath, ms.ToArray());
        return true;
    }
}
