using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: the DisplayPad "Emoji browser" (action type <c>dp_emojibrowser</c>).
///
/// Pressing a key bound to that action turns the whole 2×6 panel into a transient emoji
/// picker painted straight onto the hardware — it is NOT a stored page/profile, nothing is
/// persisted, and the on-screen key grid is left alone (same treatment as the screensaver
/// takeover, see <c>DpScreensaverTimeout</c>). Leaving it repaints whatever the device was
/// showing before.
///
/// The layout is defined in the user's VISUAL frame and mapped to physical keys through the
/// pad's mounting rotation (<see cref="EmbPhysicalForVisual"/>), so it reads the same however
/// the pad is mounted — 8 content tiles, then back/close, then the two scroll keys:
/// <code>
///   rotation 0/180 (2 rows × 6 columns)   rotation 90/270 (6 rows × 2 columns)
///   [ c ][ c ][ c ][ c ][back ][prev▲]    [  c  ][  c  ]
///   [ c ][ c ][ c ][ c ][close][next▼]    [  c  ][  c  ]
///                                         [  c  ][  c  ]
///                                         [  c  ][  c  ]
///                                         [back ][close]
///                                         [prev◀][next▶]
/// </code>
/// On a rotated pad the two scroll keys end up side by side rather than stacked, so they
/// become left/right instead of up/down: forward is the RIGHT one, back the LEFT one, and the
/// triangles are drawn pointing that way instead of being rotated up/down arrows.
///
/// The browser opens on the CATEGORY screen (Unicode's own emoji groups, one tile each);
/// picking a category jumps into the emoji list at that category's first entry. That list is
/// one flat run over <see cref="EmojiCatalog.All"/> (which is already ordered by category),
/// not a per-category slice: scrolling past the end of a category therefore continues
/// straight into the next one, which is exactly the "you can still get from one list to
/// another with the arrows" behaviour asked for, with no extra state.
///
/// Back/close semantics:
/// <list type="bullet">
/// <item>emoji screen: <b>back</b> returns to the category screen, <b>close</b> exits to the profile;</item>
/// <item>category screen: both <b>back</b> and <b>close</b> exit to the profile.</item>
/// </list>
///
/// Everything here is device-agnostic (keyed by device id, never touching the
/// foreground-only <c>_dpKeys</c>/<c>_currentDpPageId</c>), so it works the same on the
/// visible tab and on a background pad — see <c>OnDpKey</c>/<c>DpHandleBackgroundKey</c>.
/// </summary>
public partial class MainWindow
{
    private const int EmbPageSize = 8;

    /// <summary>Which physical key plays which role, for one mounting rotation.</summary>
    /// <param name="Content">The 8 emoji/category tiles, in the user's reading order.</param>
    /// <param name="Horizontal">True when the two scroll keys sit side by side (rotated pad)
    /// rather than stacked, i.e. when "next" means right instead of down.</param>
    private readonly record struct EmbLayout(
        int[] Content, int Back, int Close, int Prev, int Next, bool Horizontal);

    /// <summary>Open browsers, keyed by device id. Absent = the device shows its normal page.</summary>
    private readonly Dictionary<int, EmbState> _dpEmojiBrowser = new();

    private sealed class EmbState
    {
        /// <summary>True while the category ("chapters") screen is shown.</summary>
        public bool Categories = true;
        /// <summary>Index of the first item shown, into <see cref="EmojiCatalog.Groups"/> or
        /// <see cref="EmojiCatalog.All"/> depending on <see cref="Categories"/>.</summary>
        public int Offset;
        /// <summary>Rotation and key roles captured when the browser opened, so painting and
        /// key handling can never disagree even if the setting changes underneath (it can't
        /// in practice: changing it repaints, which drops the browser).</summary>
        public required int Rotation;
        public required EmbLayout Layout;
        /// <summary>Tile image currently painted on each of the 12 keys (null = blank) —
        /// kept so the press-bounce can re-upload the right picture.</summary>
        public string?[] Tiles = new string?[12];
    }

    /// <summary>True while <paramref name="devId"/>'s panel is owned by the emoji browser.</summary>
    private bool DpEmojiBrowserActive(int devId) => _dpEmojiBrowser.ContainsKey(devId);

    // ================================================================
    // Layout
    // ================================================================

    /// <summary>
    /// Physical key index for each visual slot, in the on-screen reading order of the rotated
    /// grid — a local mirror of <c>K2.DisplayPad</c>'s <c>DisplayPadLayout.PhysicalForVisual</c>
    /// (that one lives in the standalone project; K2.App's own DisplayPad tab rotates its grid
    /// with a WPF <c>LayoutTransform</c> instead and so never needed the permutation).
    /// At 90°/270° the 2×6 strip reads as 6 rows × 2 columns.
    /// </summary>
    private static int[] EmbPhysicalForVisual(int rotation)
    {
        const int rows = 2, cols = 6;
        int vCols = rotation is 90 or 270 ? rows : cols;
        var map = new int[12];
        for (int pr = 0; pr < rows; pr++)
        for (int pc = 0; pc < cols; pc++)
        {
            int phys = pr * cols + pc;
            var (vr, vc) = rotation switch
            {
                90  => (pc, rows - 1 - pr),
                270 => (cols - 1 - pc, pr),
                180 => (rows - 1 - pr, cols - 1 - pc),
                _   => (pr, pc),
            };
            map[vr * vCols + vc] = phys;
        }
        return map;
    }

    private static EmbLayout EmbLayoutFor(int rotation)
    {
        var v2p = EmbPhysicalForVisual(rotation);
        bool tall = rotation is 90 or 270;

        // Visual slots, in reading order of the rotated grid: content first, then the
        // back/close pair, then the two scroll keys.
        int[] contentSlots = tall
            ? new[] { 0, 1, 2, 3, 4, 5, 6, 7 }          // 6×2: the first four rows
            : new[] { 0, 1, 2, 3, 6, 7, 8, 9 };         // 2×6: the first four columns
        var content = contentSlots.Select(v => v2p[v]).ToArray();

        return tall
            ? new EmbLayout(content, v2p[8], v2p[9], v2p[10], v2p[11], Horizontal: true)
            : new EmbLayout(content, v2p[4], v2p[10], v2p[5], v2p[11], Horizontal: false);
    }

    // ================================================================
    // Open / close
    // ================================================================

    /// <summary>Takes the panel over and paints the category screen. Re-opening while
    /// already open is a no-op (a stray double press shouldn't reset the position).</summary>
    private void DpEmojiBrowserOpen(int devId)
    {
        if (_dpEmojiBrowser.ContainsKey(devId)) return;

        // Per-key GIF loops would keep repainting their own tiles over the browser, exactly
        // as they would over the screensaver — see DpScreensaverTimeout.
        DpGifAnimator.StopAllForDevice(devId);
        DpFullscreenAnimator.Stop(devId);
        DpLiveTileService.Stop(devId);   // idem for the clock/monitor tiles
        DpSpotifyCoverKeyService.Stop(devId);   // idem for album-cover keys

        int rotation = _dpStore.GetRotation(devId);
        _dpEmojiBrowser[devId] = new EmbState { Rotation = rotation, Layout = EmbLayoutFor(rotation) };
        DpLog($"[EMB] device {devId}: emoji browser opened (rotation {rotation}°)");
        DpEmojiBrowserPaint(devId);
    }

    /// <summary>Drops the browser and repaints the device's real page. No-op when it isn't
    /// open, so every "something else is repainting this pad" call site can call it blindly.</summary>
    private void DpEmojiBrowserExit(int devId)
    {
        if (!_dpEmojiBrowser.Remove(devId)) return;
        DpLog($"[EMB] device {devId}: emoji browser closed — restoring page icons");
        DpRequestRepaint(devId);
    }

    /// <summary>Forgets the browser WITHOUT repainting — for the call sites that are
    /// themselves about to repaint the device (profile switch, page navigation, tab
    /// change), where <see cref="DpEmojiBrowserExit"/> would queue a second, redundant
    /// full repaint on top of theirs.</summary>
    private void DpEmojiBrowserAbandon(int devId)
    {
        if (_dpEmojiBrowser.Remove(devId))
            DpLog($"[EMB] device {devId}: emoji browser dropped (panel repainted elsewhere)");
    }

    // ================================================================
    // Key handling
    // ================================================================

    /// <summary>
    /// Handles one physical key event while the browser owns <paramref name="devId"/>'s panel.
    /// Called from <c>OnDpKey</c> BEFORE the normal foreground/background dispatch, so no
    /// stored binding of the underlying page can fire while the browser is up.
    /// </summary>
    private void DpEmojiBrowserKey(int devId, int btnIndex, bool pressed)
    {
        if (!_dpEmojiBrowser.TryGetValue(devId, out var st)) return;

        // Same shrink-on-press feedback the normal pages get (DpUploadPressVisualForDevice
        // can't be reused: it reads the stored page's image and bails out on the fullscreen
        // flag, neither of which describes a browser tile).
        if (st.Tiles[btnIndex] is string tile && File.Exists(tile))
        {
            var prev = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
            _dpUploadChain[devId] = prev.ContinueWith(
                _ => _dpClient.UploadImage(devId, tile, btnIndex, st.Rotation, pressed), TaskScheduler.Default);
        }

        if (!pressed) return;

        var layout = st.Layout;

        if (btnIndex == layout.Close)
        {
            DpEmojiBrowserExit(devId);
            return;
        }

        if (btnIndex == layout.Back)
        {
            // Emoji screen -> category screen; category screen -> out (same as close).
            if (st.Categories) DpEmojiBrowserExit(devId);
            else { st.Categories = true; st.Offset = 0; DpEmojiBrowserPaint(devId); }
            return;
        }

        if (btnIndex == layout.Prev || btnIndex == layout.Next)
        {
            int count = st.Categories ? EmojiCatalog.Groups.Count : EmojiCatalog.All.Count;
            int last = Math.Max(0, (count - 1) / EmbPageSize) * EmbPageSize;
            int wanted = st.Offset + (btnIndex == layout.Next ? EmbPageSize : -EmbPageSize);
            int clamped = Math.Clamp(wanted, 0, last);
            if (clamped == st.Offset) return;   // already at the end: nothing to repaint
            st.Offset = clamped;
            DpEmojiBrowserPaint(devId);
            return;
        }

        int slot = Array.IndexOf(layout.Content, btnIndex);
        if (slot < 0) return;                   // not one of ours (remapped pad)
        int item = st.Offset + slot;

        if (st.Categories)
        {
            var groups = EmojiCatalog.Groups;
            if (item >= groups.Count) return;   // empty tile on the last category screen
            // Jump into the flat emoji run at this category's first entry, aligned to a
            // screen boundary so the picked category starts at the top-left tile.
            int first = IndexOfFirstInGroup(groups[item]);
            if (first < 0) return;
            st.Categories = false;
            st.Offset = first - (first % EmbPageSize);
            DpLog($"[EMB] device {devId}: category \"{groups[item]}\" -> offset {st.Offset}");
            DpEmojiBrowserPaint(devId);
            return;
        }

        var all = EmojiCatalog.All;
        if (item >= all.Count) return;          // empty tile on the last emoji screen
        DpLog($"[EMB] device {devId}: type {all[item].Name}");
        ActionExecutor.SendUnicodeText(all[item].Emoji, DpLog);
    }

    private static int IndexOfFirstInGroup(string group)
    {
        var all = EmojiCatalog.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Group == group) return i;
        return -1;
    }

    // ================================================================
    // Painting
    // ================================================================

    /// <summary>Renders the current screen's 12 tiles and uploads them, chained onto the same
    /// per-device upload chain as every other icon write so it can never race a repaint.</summary>
    private void DpEmojiBrowserPaint(int devId)
    {
        if (!_dpEmojiBrowser.TryGetValue(devId, out var st)) return;
        var layout = st.Layout;

        var tiles = new string?[12];
        for (int s = 0; s < layout.Content.Length; s++)
        {
            int item = st.Offset + s;
            tiles[layout.Content[s]] = st.Categories
                ? (item < EmojiCatalog.Groups.Count ? EmbCategoryTile(EmojiCatalog.Groups[item]) : null)
                : (item < EmojiCatalog.All.Count ? EmbEmojiTile(EmojiCatalog.All[item].Emoji) : null);
        }

        tiles[layout.Back]  = EmbNavTile(IconImageGenerator.NavShape.Back,  Loc.Get("dp_back"));
        tiles[layout.Close] = EmbNavTile(IconImageGenerator.NavShape.Close, Loc.Get("emb_close"));
        // Rotated pad: the scroll keys are side by side, so "next" points right, not down.
        tiles[layout.Prev]  = EmbNavTile(layout.Horizontal
            ? IconImageGenerator.NavShape.Left : IconImageGenerator.NavShape.Up, "");
        tiles[layout.Next]  = EmbNavTile(layout.Horizontal
            ? IconImageGenerator.NavShape.Right : IconImageGenerator.NavShape.Down, "");

        st.Tiles = tiles;

        int rotation = st.Rotation;
        var previous = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[devId] = previous.ContinueWith(_ =>
        {
            for (int i = 0; i < 12; i++)
            {
                if (tiles[i] is string path) _dpClient.UploadImage(devId, path, i, rotation);
                else DpClearKeyOnDevice(devId, i);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Tile for one emoji — cached on disk under the shared auto-icon folder, so a
    /// given emoji is only ever rasterized once per install.</summary>
    private static string? EmbEmojiTile(string emoji)
    {
        string dest = DpAutoIconCachePath("embemoji", emoji);
        if (File.Exists(dest)) return dest;
        return EmojiGlyphRenderer.TryGenerateEmojiIcon(emoji, DpHidNative.IconSize, dest) ? dest : null;
    }

    /// <summary>
    /// One emoji standing in for each Unicode category, plus the suffix of the SHORT caption
    /// loc key (<c>emb_grp_*</c>) drawn under it. Deliberately not
    /// <see cref="EmojiCatalog.LocalizedGroup"/>'s full name: "Smileys &amp; Emotion" and
    /// friends get ellipsized to "Smileys &amp;..." at 102 px, which reads worse than the
    /// one-word form. Picked by hand rather than "first entry of the group" because that
    /// happens to be a grinning face / waving hand / grinning cat and so on — recognizable for
    /// some groups, meaningless for others. Anything the installed font can't draw falls back
    /// to the group's first catalog entry, and then to a caption-only tile.
    /// </summary>
    private static readonly Dictionary<string, (string Emoji, string LocSuffix)> EmbCategoryTiles = new()
    {
        ["Smileys & Emotion"] = ("\U0001F600", "smileys"),     // grinning face
        ["People & Body"]     = ("\U0001F44B", "people"),      // waving hand
        ["Animals & Nature"]  = ("\U0001F43B", "animals"),     // bear
        ["Food & Drink"]      = ("\U0001F354", "food"),        // hamburger
        ["Travel & Places"]   = ("\U0001F697", "travel"),      // car
        ["Activities"]        = ("\U000026BD", "activities"),  // soccer ball
        ["Objects"]           = ("\U0001F4A1", "objects"),     // light bulb
        ["Symbols"]           = ("\U0001F523", "symbols"),     // input symbols
        ["Flags"]             = ("\U0001F3C1", "flags"),       // chequered flag
    };

    /// <summary>Category tile: representative emoji + the category's short name below it.</summary>
    private static string? EmbCategoryTile(string group)
    {
        bool known = EmbCategoryTiles.TryGetValue(group, out var meta);
        // A Unicode revision adding a group falls back to that group's first emoji and its
        // full localized name (LocalizedGroup itself falls back to the English one there).
        string caption = known ? Loc.Get("emb_grp_" + meta.LocSuffix) : EmojiCatalog.LocalizedGroup(group);
        string emoji = known ? meta.Emoji
            : EmojiCatalog.All.FirstOrDefault(x => x.Group == group)?.Emoji ?? "";

        // Emoji art, accent-independent; caption is language-dependent — both in the key.
        string dest = DpAutoIconCachePath("embcat", $"{emoji}|{caption}");
        if (File.Exists(dest)) return dest;
        if (EmojiGlyphRenderer.TryGenerateEmojiIcon(emoji, DpHidNative.IconSize, dest, caption)) return dest;
        return IconImageGenerator.TryGenerateCaptionIcon(caption, DpHidNative.IconSize, dest) ? dest : null;
    }

    /// <summary>Navigation tile (back / close / scroll).</summary>
    private static string? EmbNavTile(IconImageGenerator.NavShape shape, string caption)
    {
        // The shapes are painted in the icon color (Settings > Accent color > Icon color,
        // defaults to the accent theme — see IconImageGenerator.ResolveIconColor), so both
        // settings go in the cache key or a switch of one without the other would keep
        // serving a stale-colored cached PNG.
        string dest = DpAutoIconCachePath("embnav", $"{AppSettings.AccentTheme}|{AppSettings.IconColorTheme}|{shape}|{caption}");
        if (File.Exists(dest)) return dest;
        return IconImageGenerator.TryGenerateNavIcon(shape, caption, DpHidNative.IconSize, dest) ? dest : null;
    }
}
