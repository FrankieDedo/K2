using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: the DisplayPad ACTION WHEEL (action type <c>dp_wheel</c>, value = page id).
///
/// A wheel is a variant of a page: its keys are stored like any sub-page's (same table, its own
/// page id, shown in the Pages tab), but only the keys AROUND the key that opens it can hold
/// something — the physical neighbours on the 2×6 panel, diagonals included (3 for a corner key,
/// 5 for a middle one). Opening it does not leave the current page: those neighbours are
/// overwritten with the wheel's icons (zooming out of the pressed key), the opening key keeps its
/// own icon with a "back" badge and becomes the back key, and every other key is left alone (not
/// re-uploaded, its live painters keep running). The wheel's area (its icons plus the opening key)
/// gets a coloured OUTLINE — each tile draws only its edges that face outside the area — in the
/// page's own colour or the app's accent, composed at upload time only: the icon files the user
/// picked never carry it. Nothing is persisted; closing plays the zoom backwards and puts back
/// only the keys the wheel used.
///
/// Open from: the key's own action (<c>dp_wheel</c>), or a long-press / modifier variant of
/// another action (<see cref="TriggerPayload"/>, routed here by
/// <see cref="DpRunPageAction"/> through <see cref="IActionHost.TryRunHostAction"/>).
///
/// While a wheel is up, the live painters (live tiles, GIF loops, album-cover and Discord
/// overlays) skip the wheel's keys (<see cref="DpKeyHold"/>) and go on everywhere else;
/// automatic profile switches (program launch watchers, the Spotify return timer, game profiles)
/// and repaint requests are queued and run on close, the screensaver/auto-off timeouts are
/// skipped. A page showing a fullscreen image is the exception: its animation is stopped and
/// closing repaints the whole panel, as the image spans every key.
///
/// Key handling (down edges only — the up edge of the press that opened the wheel must not close
/// it): the back key or any other non-wheel key closes it; a wheel key closes it and runs its action.
/// Device-agnostic (keyed by device id), so it works on the visible tab and on a background pad.
/// </summary>
public partial class MainWindow
{
    private const int DpCols = 6, DpRows = 2;

    private sealed class WheelState
    {
        public required int Anchor;
        public required int PageId;
        public required int Rotation;
        public required Dictionary<int, DpButtonRecord> Items;
        /// <summary>Keys the wheel drew on (opening key + wheel icons) — the only ones closing restores.</summary>
        public required int[] Touched;
        /// <summary>Keys that zoomed out of the opening key: (button, original icon, direction).</summary>
        public required List<(int Btn, string Path, int DirX, int DirY)> Zoom;
        /// <summary>The page had a fullscreen image: closing repaints the whole panel.</summary>
        public bool Fullscreen;
        /// <summary>Picture on each key while the wheel is up (null = untouched) — for the press bounce.</summary>
        public string?[] Tiles = new string?[12];
        /// <summary>Rotation each of <see cref="Tiles"/> is uploaded with (0 = composed already rotated).</summary>
        public int[] TileRot = new int[12];
        /// <summary>Automatic switches/repaints that arrived while the wheel was up.</summary>
        public readonly List<Action> Deferred = new();
        /// <summary>Set when the wheel closes (read off the UI thread by the upload chain).</summary>
        public volatile bool Closed;
    }

    /// <summary>Which edges of a tile belong to the wheel's outline (physical panel directions).</summary>
    [Flags]
    internal enum WheelSides { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    /// <summary>Edges of <paramref name="key"/> that face outside <paramref name="area"/> — the
    /// piece of the area's outline this tile draws.</summary>
    internal static WheelSides DpWheelSides(int key, ICollection<int> area)
    {
        int r = key / DpCols, c = key % DpCols;
        bool In(int r2, int c2) => r2 is >= 0 and < DpRows && c2 is >= 0 and < DpCols && area.Contains(r2 * DpCols + c2);
        var sides = WheelSides.None;
        if (!In(r, c - 1)) sides |= WheelSides.Left;
        if (!In(r - 1, c)) sides |= WheelSides.Top;
        if (!In(r, c + 1)) sides |= WheelSides.Right;
        if (!In(r + 1, c)) sides |= WheelSides.Bottom;
        return sides;
    }

    private readonly Dictionary<int, WheelState> _dpWheels = new();

    private bool DpWheelActive(int devId) => _dpWheels.ContainsKey(devId);

    /// <summary>Physical neighbours of a key on the 2×6 panel, diagonals included. Rotation does
    /// not change adjacency, so this is the same however the pad is mounted.</summary>
    internal static int[] DpWheelNeighbours(int anchor)
    {
        int r = anchor / DpCols, c = anchor % DpCols;
        var list = new List<int>();
        for (int dr = -1; dr <= 1; dr++)
        for (int dc = -1; dc <= 1; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            int r2 = r + dr, c2 = c + dc;
            if (r2 is >= 0 and < DpRows && c2 is >= 0 and < DpCols) list.Add(r2 * DpCols + c2);
        }
        return list.ToArray();
    }

    /// <summary>The key a wheel page opens from (the one holding the <c>dp_wheel</c> action), or
    /// null when no key points at it.</summary>
    private int? DpWheelAnchor(int devId, int profile, int wheelPageId) =>
        _dpStore.WheelAnchor(devId, profile, wheelPageId);

    /// <summary>A wheel page's border colour: its own (page config) or the app's accent.</summary>
    internal System.Windows.Media.Color DpWheelColor(int pageId)
    {
        if (_dpStore.GetWheelColor(pageId) is int rgb)
            return System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return K2.Core.Services.AccentCatalog.Resolve(AppSettings.AccentTheme).Accent;
    }

    private bool DpIsWheelPage(int devId, int profile, int pageId) =>
        pageId != 0 && DpWheelAnchor(devId, profile, pageId) is not null;

    /// <summary>Queues <paramref name="action"/> until the wheel on <paramref name="devId"/> closes.
    /// False (nothing queued) when no wheel is up — the caller then runs it itself.</summary>
    private bool DpWheelDefer(int devId, Action action)
    {
        if (!_dpWheels.TryGetValue(devId, out var st)) return false;
        st.Deferred.Add(action);
        DpLog($"[WHEEL] device {devId}: automatic switch/repaint deferred until the wheel closes");
        return true;
    }

    // ================================================================
    // Page actions run from a long-press / modifier variant
    // ================================================================

    /// <summary><see cref="IActionHost.TryRunHostAction"/> for both DisplayPad hosts: the page
    /// actions the DisplayPad normally dispatches itself, reached from a trigger sub-action.</summary>
    internal bool DpRunPageAction(int devId, string type, string value, int btnIndex)
    {
        bool fg = DpSelectedDeviceId() == devId;
        switch (type)
        {
            case "dp_wheel" when int.TryParse(value, out int wheelPage) && btnIndex is >= 0 and < 12:
                DpWheelOpen(devId, btnIndex, wheelPage);
                return true;
            case "dp_folder" when int.TryParse(value, out int page):
                if (fg) DpNavigateToPage(page, _dpStore.GetFolderName(page)); else DpBgNavigateToPage(devId, page);
                return true;
            case "dp_back":
                if (fg) DpNavigateBack(); else DpBgNavigateBack(devId);
                return true;
            default:
                return false;
        }
    }

    // ================================================================
    // Open / close
    // ================================================================

    private void DpWheelOpen(int devId, int anchor, int pageId)
    {
        if (_dpWheels.ContainsKey(devId)) return;
        if (DpEmojiBrowserActive(devId) || DpDiscordRoomActive(devId)) return;

        int profile = DpSelectedDeviceId() == devId ? DpCurrentProfile() : _dpStore.GetCurrentProfile(devId);
        int rotation = _dpStore.GetRotation(devId);
        var neighbours = DpWheelNeighbours(anchor);
        var items = _dpStore.LoadPage(devId, profile, pageId)
            .Where(r => Array.IndexOf(neighbours, r.ButtonIndex) >= 0
                        && (!string.IsNullOrEmpty(r.ActionType) || !string.IsNullOrEmpty(r.ImagePath)))
            .ToDictionary(r => r.ButtonIndex);

        // What the opening key shows RIGHT NOW (live overlay or modifier icon included): it is
        // what gets the back badge.
        string? anchorNow = DiscordVoiceKeyService.CurrentIconPath(devId, anchor)
                            ?? DpLiveTileService.CurrentIconPath(devId, anchor)
                            ?? DpSpotifyCoverKeyService.CurrentIconPath(devId, anchor)
                            ?? DpModIconShownFor(devId, anchor)
                            ?? _dpStore.LoadPage(devId, profile, DpCurrentPageIdFor(devId))
                                       .FirstOrDefault(r => r.ButtonIndex == anchor)?.ImagePath;
        var wc = DpWheelColor(pageId);
        var color = Color.FromArgb(wc.R, wc.G, wc.B);

        // The wheel's area: the opening key plus every wheel key that has an icon to show.
        // Live keys (clock, sensors…) have no picture file of their own: they are rendered now
        // and then kept up to date by DpLiveTileService's wheel layer.
        var liveRows = items.Values.Where(r => DpLiveTileService.IsLiveType(r.ActionType)).ToList();
        string? defaultBg = _dpStore.GetDefaultBgImage(devId);
        var liveFirst = new Dictionary<int, string>();
        foreach (var r in liveRows)
            if (DpLiveTileService.RenderWheelNow(devId, r, defaultBg) is string first) liveFirst[r.ButtonIndex] = first;
        var area = new HashSet<int>(items.Where(kv => WheelUsable(kv.Value.ImagePath) || liveFirst.ContainsKey(kv.Key))
                                         .Select(kv => kv.Key)) { anchor };
        var touched = area.ToArray();

        bool fullscreen = _dpFullscreenByDevice.TryGetValue(devId, out bool fs) && fs;
        if (fullscreen) DpFullscreenAnimator.Stop(devId);
        // The live painters skip these keys from now on; everything else keeps running.
        DpKeyHold.Hold(devId, touched);
        if (_dpModShown.TryGetValue(devId, out var modShown))
            foreach (int k in touched) modShown.Remove(k);   // repainted by DpApplyModIcons on close

        var st = new WheelState
        {
            Anchor = anchor, PageId = pageId, Rotation = rotation, Items = items,
            Touched = touched, Zoom = new(), Fullscreen = fullscreen,
        };
        _dpWheels[devId] = st;
        DpLog($"[WHEEL] device {devId}: wheel page {pageId} opened from key #{anchor} ({items.Count} item(s))");

        // Edge tiles are composed already rotated for the mounting (uploaded at 0), so the
        // outline sits on the physical edges however the pad is mounted.
        // The opening key draws no area outline (the area opens toward it, as if the wheel popped
        // out of it); while the wheel is open it gets its own thicker rounded ring instead.
        st.Tiles[anchor] = WheelEdgeTile(anchorNow, badge: true, WheelSides.None, color, rotation);
        foreach (var (i, item) in items)
        {
            if (!area.Contains(i)) continue;
            if (liveFirst.TryGetValue(i, out string? livePng))
            {
                // Live: no zoom (its picture changes), composed into the key's fixed wheel file.
                st.Tiles[i] = WheelLiveTile(devId, i, livePng, DpWheelSides(i, area), color,
                                            DpLiveTileService.WheelSkipsRotation(item) ? 0 : rotation);
                continue;
            }
            if (CroppedGifRef.IsCropRef(item.ImagePath!))
            {
                st.Tiles[i] = item.ImagePath; st.TileRot[i] = rotation;   // no outline on a cropped GIF
                continue;
            }
            st.Tiles[i] = WheelEdgeTile(item.ImagePath, badge: false, DpWheelSides(i, area), color, rotation);
            if (st.Tiles[i] is null) { st.Tiles[i] = item.ImagePath; st.TileRot[i] = rotation; continue; }
            st.Zoom.Add((i, item.ImagePath!, Math.Sign(anchor % DpCols - i % DpCols), Math.Sign(anchor / DpCols - i / DpCols)));
        }
        var tiles = st.Tiles;
        var tileRot = st.TileRot;

        // One in-between frame only: every upload is a serialized, settle-delayed USB write, so
        // each extra frame costs a full pass over the wheel keys and made the animation stutter
        // (user report). Frames are rendered here, before the chain, so nothing but uploads sits
        // between them on the wire (they are cached on disk after the first opening anyway).
        var frames = st.Zoom.Select(z => (z.Btn, Frame: WheelZoomFrame(z.Path, 0.5 * WheelIconScale, z.DirX, z.DirY, rotation))).ToList();

        var previous = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[devId] = previous.ContinueWith(_ =>
        {
            // The wheel keys first (half size, then full) — they are what the user is waiting
            // for — then the back key and the rest of the area. Nothing else is re-uploaded.
            foreach (var (btn, frame) in frames)
                if (frame is not null) _dpClient.UploadImage(devId, frame, btn, 0);
            foreach (var (btn, _) in frames)
                if (tiles[btn] is string t) _dpClient.UploadImage(devId, t, btn, tileRot[btn]);
            foreach (int i in touched)
            {
                if (frames.Any(f => f.Btn == i)) continue;
                if (tiles[i] is string t) _dpClient.UploadImage(devId, t, i, tileRot[i]);
            }
            // From here on the live keys of the wheel tick on their own (still this wheel?).
            if (liveFirst.Count > 0 && !st.Closed)
            {
                var sides = liveFirst.Keys.ToDictionary(k => k, k => DpWheelSides(k, area));
                DpLiveTileService.SetWheel(_dpClient, DpLogAsync, devId, liveRows.Where(r => liveFirst.ContainsKey(r.ButtonIndex)),
                    defaultBg, (png, btn, skipRot) => WheelLiveTile(devId, btn, png, sides[btn], color, skipRot ? 0 : rotation));
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Keeps a wheel page's keys around its opening key after that key moved (dragged
    /// to another spot on the parent page): every key keeps its place RELATIVE to the opening key
    /// when it still fits, otherwise it takes the nearest free neighbour; the back key goes onto
    /// the opening key (a second back is dropped). <paramref name="oldAnchor"/> null = unknown
    /// (only the nearest-free rule applies). A key with no free spot left stays where it is.</summary>
    private void DpWheelRelayout(int id, int profile, int pageId, int anchor, int? oldAnchor)
    {
        var rows = _dpStore.LoadPage(id, profile, pageId);
        var nb = DpWheelNeighbours(anchor);
        bool Fits(int i) => Array.IndexOf(nb, i) >= 0;
        bool IsBack(DpButtonRecord r) => r.ActionType == "dp_back";

        var strays = rows.Where(r => !Fits(r.ButtonIndex) && !(r.ButtonIndex == anchor && IsBack(r))).ToList();
        if (strays.Count == 0) return;
        var strayIdx = strays.Select(r => r.ButtonIndex).ToHashSet();
        var taken = rows.Where(r => !strayIdx.Contains(r.ButtonIndex)).Select(r => r.ButtonIndex).ToHashSet();
        var free = nb.Where(i => !taken.Contains(i)).ToList();

        int? Shifted(int from)
        {
            if (oldAnchor is not int o) return null;
            int r = from / DpCols + anchor / DpCols - o / DpCols, c = from % DpCols + anchor % DpCols - o % DpCols;
            return r is >= 0 and < DpRows && c is >= 0 and < DpCols ? r * DpCols + c : null;
        }
        static int Dist(int x, int y) => Math.Max(Math.Abs(x / DpCols - y / DpCols), Math.Abs(x % DpCols - y % DpCols));

        var moves = new List<(int From, int To)>();
        var deletes = new List<int>();
        bool anchorTaken = taken.Contains(anchor);
        // Backs first: one onto the opening key, any other is redundant.
        foreach (var r in strays.Where(IsBack))
        {
            if (!anchorTaken) { moves.Add((r.ButtonIndex, anchor)); anchorTaken = true; }
            else deletes.Add(r.ButtonIndex);
        }
        foreach (var r in strays.Where(r => !IsBack(r)))
        {
            int want = Shifted(r.ButtonIndex) ?? r.ButtonIndex;
            int? to = free.Contains(want) ? want : free.Count > 0 ? free.OrderBy(f => Dist(f, want)).First() : null;
            if (to is not int t) { DpLog($"[WHEEL] page {pageId}: no free spot around key #{anchor} for key #{r.ButtonIndex}"); continue; }
            free.Remove(t);
            moves.Add((r.ButtonIndex, t));
        }
        foreach (int d in deletes) _dpStore.DeleteButton(id, profile, pageId, d);
        _dpStore.MoveButtons(id, profile, pageId, moves);
        DpLog($"[WHEEL] page {pageId}: keys follow opening key #{oldAnchor?.ToString() ?? "?"} -> #{anchor}: "
              + string.Join(", ", moves.Select(m => $"#{m.From}->#{m.To}"))
              + (deletes.Count > 0 ? $" (extra back dropped: {string.Join(",", deletes)})" : ""));
    }

    private static bool WheelUsable(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <summary>Closes the wheel: the zoom plays backwards (the wheel's icons shrink into the
    /// opening key) and only the keys the wheel used get the page back; then whatever was
    /// deferred runs.</summary>
    private void DpWheelClose(int devId)
    {
        if (!_dpWheels.Remove(devId, out var st)) return;
        st.Closed = true;
        DpLiveTileService.ClearWheel(devId);
        DpLog($"[WHEEL] device {devId}: wheel closed — restoring {st.Touched.Length} key(s)");

        if (st.Fullscreen)
        {
            DpKeyHold.Release(devId);
            DpRequestRepaint(devId);
            foreach (var a in st.Deferred) a();
            return;
        }

        int profile = DpSelectedDeviceId() == devId ? DpCurrentProfile() : _dpStore.GetCurrentProfile(devId);
        var rows = _dpStore.LoadPage(devId, profile, DpCurrentPageIdFor(devId));
        int rotation = st.Rotation;
        var frames = st.Zoom.Select(z => (z.Btn, Frame: WheelZoomFrame(z.Path, 0.5 * WheelIconScale, z.DirX, z.DirY, rotation))).ToList();

        // What goes back on each key: a live painter repaints its own key once released; a GIF
        // loop simply resumes; anything else gets its page picture (or a blank).
        var painted = new List<(int Btn, string? Path)>();
        var live = new List<int>();
        foreach (int i in st.Touched)
        {
            string? path = rows.FirstOrDefault(r => r.ButtonIndex == i)?.ImagePath;
            if (DpLiveTileService.Owns(devId, i) || DiscordVoiceKeyService.Owns(devId, i)
                || DpSpotifyCoverKeyService.Owns(devId, i) || SpotifyCoverService.Owns(devId, i)
                || (WheelUsable(path) && DpGifAnimator.IsAnimatedGif(path)))
                live.Add(i);
            else
                painted.Add((i, WheelUsable(path) ? path : null));
        }

        var previous = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[devId] = previous.ContinueWith(_ =>
        {
            DpLiveTileService.ClearWheel(devId);   // in case the opening chain registered it after the close
            foreach (var (btn, frame) in frames)
                if (frame is not null) _dpClient.UploadImage(devId, frame, btn, 0);
            DpKeyHold.Release(devId);
            foreach (var (btn, path) in painted)
            {
                if (path is not null) _dpClient.UploadImage(devId, path, btn, rotation);
                else DpClearKeyOnDevice(devId, btn);
            }
            foreach (int btn in live)
            {
                DpLiveTileService.RepaintKey(devId, btn);
                DiscordVoiceKeyService.Repaint(devId, btn);
                DpSpotifyCoverKeyService.Repaint(devId, btn);
                if (SpotifyCoverService.Owns(devId, btn)) SpotifyCoverService.Repaint(devId);
            }
        }, TaskScheduler.Default);

        // A modifier held meanwhile: put its icons back on the restored keys (chained after).
        if (_dpModHeld is not null)
            Dispatcher.BeginInvoke(() => DpApplyModIcons(devId), DispatcherPriority.Background);
        foreach (var a in st.Deferred) a();
    }

    /// <summary>Forgets the wheel WITHOUT repainting — for callers about to repaint themselves
    /// (same contract as <see cref="DpEmojiBrowserAbandon"/>). Deferred work still runs.</summary>
    private void DpWheelAbandon(int devId)
    {
        if (!_dpWheels.Remove(devId, out var st)) return;
        st.Closed = true;
        DpLiveTileService.ClearWheel(devId);
        DpKeyHold.Release(devId);
        DpLog($"[WHEEL] device {devId}: wheel dropped (panel repainted elsewhere)");
        if (st.Deferred.Count > 0)
            Dispatcher.BeginInvoke(() => { foreach (var a in st.Deferred) a(); }, DispatcherPriority.Background);
    }

    // ================================================================
    // Keys
    // ================================================================

    /// <summary>One physical key event while the wheel owns <paramref name="devId"/>'s panel —
    /// called from <c>OnDpKey</c> before the normal dispatch.</summary>
    private void DpWheelKey(int devId, int btnIndex, bool pressed)
    {
        if (!_dpWheels.TryGetValue(devId, out var st)) return;

        if (st.Tiles[btnIndex] is string tile && File.Exists(tile))
        {
            int rot = st.TileRot[btnIndex];
            var prev = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
            _dpUploadChain[devId] = prev.ContinueWith(
                _ => _dpClient.UploadImage(devId, tile, btnIndex, rot, pressed), TaskScheduler.Default);
        }
        if (!pressed) return;

        if (btnIndex != st.Anchor && st.Items.TryGetValue(btnIndex, out var item) && !string.IsNullOrEmpty(item.ActionType))
        {
            DpWheelClose(devId);
            DpLog($"[WHEEL] device {devId}: key #{btnIndex} -> {item.ActionType}");
            DpRunWheelItem(devId, btnIndex, item);
            return;
        }
        DpWheelClose(devId);   // back key, or any key outside the wheel
    }

    private void DpRunWheelItem(int devId, int btnIndex, DpButtonRecord item)
    {
        string type = item.ActionType!;
        string value = item.ActionValue ?? "";
        if (type == "dp_back") return;   // the wheel's own back: closing was all it had to do
        if (type == "dp_emojibrowser") { DpEmojiBrowserOpen(devId); return; }
        if (DpLiveTileService.IsLiveType(type)) { DpLiveTileService.HandleWheelPress(devId, item, DpLogAsync); return; }
        if (DpRunPageAction(devId, type, value, btnIndex)) return;
        var engine = DpSelectedDeviceId() == devId ? _dpEngine : DpEngineFor(devId);
        engine?.Execute(type, value, btnIndex);
    }

    // ================================================================
    // Editing: on a wheel page only the anchor's neighbours can be mapped
    // ================================================================

    /// <summary>Wheel page on the foreground grid: (opening key, outline brush), null otherwise.</summary>
    private (int Anchor, System.Windows.Media.Brush Brush)? _dpWheelEdit;

    /// <summary>The foreground grid shows an action wheel page (see IActionHost.OnWheelPage).</summary>
    internal bool DpEditingWheelPage => _dpWheelEdit is not null;

    /// <summary>Locks every grid key of the foreground page that a wheel can't use (anything but
    /// the anchor's neighbours — the anchor itself is the wheel's back key, generated, not
    /// editable) and draws the wheel's outline in the preview.</summary>
    private void DpApplyWheelEditLock(int id, int profile, int pageId)
    {
        int? anchor = pageId == 0 ? null : DpWheelAnchor(id, profile, pageId);
        var allowed = anchor is int a ? DpWheelNeighbours(a) : null;
        _dpWheelEdit = null;
        if (anchor is int an)
        {
            var brush = new System.Windows.Media.SolidColorBrush(DpWheelColor(pageId));
            brush.Freeze();
            _dpWheelEdit = (an, brush);
        }
        for (int i = 0; i < _dpButtons.Length; i++)
        {
            bool locked = allowed is not null && Array.IndexOf(allowed, i) < 0;
            var btn = _dpButtons[i];
            if (locked && i == anchor)
            {
                // The back key stays clickable (it goes back to the previous page, see
                // DpKeyButton_Click) and fully visible: it is part of the wheel's outlined area.
                btn.ClearValue(UIElement.IsEnabledProperty);
                btn.ClearValue(UIElement.OpacityProperty);
                btn.ToolTip = Loc.Get("dp_wheel_anchor_key");
            }
            else if (locked)
            {
                btn.IsEnabled = false;
                btn.Opacity = 0.3;
                btn.ToolTip = Loc.Get("dp_wheel_locked_key");
                System.Windows.Controls.ToolTipService.SetShowOnDisabled(btn, true);
            }
            else
            {
                btn.ClearValue(UIElement.IsEnabledProperty);
                btn.ClearValue(UIElement.OpacityProperty);
                btn.ClearValue(FrameworkElement.ToolTipProperty);
            }
        }
        DpRefreshWheelOutline();
    }

    /// <summary>Preview outline of the wheel's area (opening key + neighbours that have an icon),
    /// same rule as on the device. Re-run when a key's icon comes or goes.</summary>
    private void DpRefreshWheelOutline()
    {
        if (_dpWheelEdit is not var (anchor, brush))
        {
            foreach (var k in _dpKeys) { k.WheelFrame = null; k.WheelIconScale = 1.0; k.IsWheelAnchor = false; }
            return;
        }
        var area = new HashSet<int>(DpWheelNeighbours(anchor).Where(i => i < _dpKeys.Length && _dpKeys[i].HasImage)) { anchor };
        const double w = 3, rad = 13;   // rad: the key's own corner (9 on the 52 px icon) pushed out to the gap
        for (int i = 0; i < _dpKeys.Length; i++)
        {
            var key = _dpKeys[i];
            key.IsWheelAnchor = i == anchor;
            if (i == anchor)
            {
                // The opening key: its own ring all round, 30% thicker, icon shrunk — as on the
                // device while the wheel is open. Only here (wheel page on screen); on the parent
                // page the key looks normal.
                key.WheelFrameSides = new Thickness(w * 1.3);
                key.WheelFrameCorners = new CornerRadius(rad);
                key.WheelIconScale = WheelIconScale;
                key.WheelFrame = brush;
                continue;
            }
            if (!area.Contains(i))
            {
                key.WheelFrame = null;
                key.WheelIconScale = 1.0;
                continue;
            }
            var s = DpWheelSides(i, area);
            bool l = s.HasFlag(WheelSides.Left), t = s.HasFlag(WheelSides.Top),
                 r = s.HasFlag(WheelSides.Right), b = s.HasFlag(WheelSides.Bottom);
            key.WheelFrameSides = new Thickness(l ? w : 0, t ? w : 0, r ? w : 0, b ? w : 0);
            // Only a convex corner (both of its sides drawn) is rounded.
            key.WheelFrameCorners = new CornerRadius(t && l ? rad : 0, t && r ? rad : 0, b && r ? rad : 0, b && l ? rad : 0);
            key.WheelIconScale = WheelIconScale;
            key.WheelFrame = brush;
        }
    }

    // ================================================================
    // Tiles (cached under the shared auto-icon folder)
    // ================================================================

    private static string Stamp(string? path) =>
        WheelUsable(path) ? $"{path}|{File.GetLastWriteTimeUtc(path!).Ticks}" : "none";

    private static Bitmap? WheelLoad(string? path)
    {
        if (!WheelUsable(path) || CroppedGifRef.IsCropRef(path!)) return null;
        try
        {
            using var fs = File.OpenRead(path!);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    private static Bitmap WheelCanvas() => new(DpHidNative.IconSize, DpHidNative.IconSize, PixelFormat.Format32bppArgb);

    /// <summary>Thickness of the wheel outline on the 102 px device tile (the editor preview
    /// draws it at about the same proportion — see DpRefreshWheelOutline).</summary>
    internal const int WheelFrameWidth = 6;

    /// <summary>Corner radius of the outline on the 102 px tile — the key's own rounding
    /// (9 on the app's 52 px icon) at device scale.</summary>
    private const float WheelFrameRadius = 18f;

    /// <summary>Wheel icons are drawn this much smaller (device and editor preview only — the
    /// icon files are untouched), so the outline has room around them.</summary>
    internal const double WheelIconScale = 0.85;

    /// <summary>The opening key's own ring (device only, while the wheel is open): 30% thicker
    /// than the area outline.</summary>
    private const float WheelAnchorFrameWidth = WheelFrameWidth * 1.3f;

    /// <summary>A key of the wheel's area: its icon (plus the "back" badge on the opening key,
    /// alone on black when that key has no picture), rotated for the mounting, with the outline
    /// on <paramref name="sides"/> (physical edges). Uploaded at rotation 0. Cached.</summary>
    private static string? WheelEdgeTile(string? source, bool badge, WheelSides sides, Color color, int rotation)
    {
        string dest = DpAutoIconCachePath("wheeledge4", $"{Stamp(source)}|{badge}|{(int)sides}|{color.ToArgb():X8}|{rotation}");
        if (File.Exists(dest)) return dest;
        return ComposeWheelTile(source, badge, sides, color, rotation, dest);
    }

    /// <summary>A live key of the wheel: same look as <see cref="WheelEdgeTile"/>, but NOT cached
    /// by content (it changes every minute/second) — always rewritten into one file per key.</summary>
    private static string? WheelLiveTile(int devId, int btn, string source, WheelSides sides, Color color, int rotation) =>
        ComposeWheelTile(source, badge: false, sides, color, rotation,
                         Path.Combine(DpAutoIconDir, $"wheel_live_dev{devId}_btn{btn}.png"));

    private static string? ComposeWheelTile(string? source, bool badge, WheelSides sides, Color color, int rotation, string dest)
    {
        using var src = WheelLoad(source);
        if (src is null && !badge) return null;
        // The icon at full size (the opening key gets its "back" badge on it), then shrunk inside
        // the outline / ring.
        using var icon = WheelCanvas();
        using (var g = Graphics.FromImage(icon))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (src is not null) g.DrawImage(src, 0, 0, icon.Width, icon.Height);
        }
        if (badge) DiscordTileRenderer.StampBackBadge(icon);
        using var bmp = WheelCanvas();
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int size = (int)Math.Round(bmp.Width * WheelIconScale), off = (bmp.Width - size) / 2;
            g.DrawImage(icon, off, off, size, size);
        }
        switch (rotation)   // same convention as WheelZoomFrame / DisplayPadNativeClient
        {
            case 90:  bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            case 180: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 270: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        }
        if (badge) DrawWheelOutline(bmp, WheelSides.Left | WheelSides.Top | WheelSides.Right | WheelSides.Bottom, color, WheelAnchorFrameWidth);
        else if (sides != WheelSides.None) DrawWheelOutline(bmp, sides, color, WheelFrameWidth);
        return Save(bmp, dest);
    }

    /// <summary>The tile's piece of the outline, filled as ONE shape (outer rounded edge minus
    /// inner rounded edge) so straight runs and corners have no seams. Drawn sides get a band
    /// <paramref name="width"/> thick at the tile's edge; a convex corner (both sides drawn) is
    /// rounded; undrawn sides are pushed off the tile so the band runs out to meet the
    /// neighbouring tile's piece.</summary>
    private static void DrawWheelOutline(Bitmap bmp, WheelSides sides, Color color, float width)
    {
        bool l = sides.HasFlag(WheelSides.Left), t = sides.HasFlag(WheelSides.Top),
             r = sides.HasFlag(WheelSides.Right), b = sides.HasFlag(WheelSides.Bottom);
        float n = bmp.Width, far = width * 2, near = width;
        float ro = WheelFrameRadius, ri = Math.Max(0f, WheelFrameRadius - width);
        using var path = new GraphicsPath(FillMode.Alternate);
        AddWheelRoundRect(path, l ? 0 : -far, t ? 0 : -far, r ? n : n + far, b ? n : n + far,
            t && l ? ro : 0, t && r ? ro : 0, b && r ? ro : 0, b && l ? ro : 0);
        AddWheelRoundRect(path, l ? width : -near, t ? width : -near, r ? n - width : n + near, b ? n - width : n + near,
            t && l ? ri : 0, t && r ? ri : 0, b && r ? ri : 0, b && l ? ri : 0);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Closed rectangle with its own radius per corner (0 = square), clockwise from top-left.</summary>
    private static void AddWheelRoundRect(GraphicsPath p, float x0, float y0, float x1, float y1,
                                          float tl, float tr, float br, float bl)
    {
        p.StartFigure();
        if (tl > 0) p.AddArc(x0, y0, tl * 2, tl * 2, 180, 90); else p.AddLine(x0, y0, x0, y0);
        if (tr > 0) p.AddArc(x1 - tr * 2, y0, tr * 2, tr * 2, 270, 90); else p.AddLine(x1, y0, x1, y0);
        if (br > 0) p.AddArc(x1 - br * 2, y1 - br * 2, br * 2, br * 2, 0, 90); else p.AddLine(x1, y1, x1, y1);
        if (bl > 0) p.AddArc(x0, y1 - bl * 2, bl * 2, bl * 2, 90, 90); else p.AddLine(x0, y1, x0, y1);
        p.CloseFigure();
    }

    /// <summary>One frame of a wheel key growing out of the pressed key: the icon at
    /// <paramref name="scale"/>, pushed toward the pressed key's side (<paramref name="dirX"/>/
    /// <paramref name="dirY"/>, physical panel directions). Composed already counter-rotated
    /// for the mounting (same convention as DisplayPadNativeClient) and uploaded at rotation 0,
    /// so "toward the pressed key" holds however the pad is mounted.</summary>
    private static string? WheelZoomFrame(string source, double scale, int dirX, int dirY, int rotation)
    {
        string dest = DpAutoIconCachePath("wheelzoom", $"{Stamp(source)}|{scale}|{dirX}|{dirY}|{rotation}");
        if (File.Exists(dest)) return dest;
        using var src = WheelLoad(source);
        if (src is null) return null;
        switch (rotation)
        {
            case 90:  src.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            case 180: src.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 270: src.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        }
        using var bmp = WheelCanvas();
        int size = bmp.Width, s = Math.Max(1, (int)Math.Round(size * scale)), free = size - s;
        int x = free / 2 + dirX * free / 2, y = free / 2 + dirY * free / 2;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, x, y, s, s);
        }
        return Save(bmp, dest);
    }

    private static string? Save(Bitmap bmp, string dest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string tmp = dest + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            bmp.Save(tmp, ImageFormat.Png);
            File.Move(tmp, dest, overwrite: true);
            return dest;
        }
        catch { return File.Exists(dest) ? dest : null; }
    }
}
