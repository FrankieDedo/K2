using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// Per-game-profile configuration, modelled on <c>DiscordProfileConfigWindow</c> — the same two
/// behaviour flags the Discord voice page already has, because a game profile takes over the pad
/// in exactly the same way and should behave the same while it does:
/// <list type="bullet">
/// <item><b>Bring the page back after N seconds</b> — if you leave the game profile for a normal
/// one while the game is running, it returns on its own.</item>
/// <item><b>Show only while the game is in front</b> — the profile is up only while the game owns
/// the foreground window, and drops back the moment it loses focus.</item>
/// </list>
/// Both default ON.
///
/// <para>The device picker is shown ONLY when more than one DisplayPad is connected; with a single
/// pad the profile is bound to it silently (see <c>MainWindow.GameProfiles.cs</c>).</para>
///
/// <para>The tile editor shows one hardware page at a time — 6x2, the DisplayPad's real layout —
/// and hands a clicked key to the very same <see cref="DpKeyConfigDialog"/> the normal profiles
/// use, so a game profile's key is configured exactly like any other key. Edits are kept in this
/// dialog's own working copy and only committed on Save.</para>
/// </summary>
public partial class GameProfileConfigDialog : Window
{
    /// <summary>Default for the return timer, matching the Discord voice page's own default.</summary>
    public const int DefaultReturnSeconds = 10;

    private readonly List<List<GameProfileCatalog.Tile>> _pages;
    private readonly string _profileId;
    private readonly ButtonActionDialog.GamePickerProfile _pickerProfile;
    private int _pageIndex;

    public bool ResultEnabled { get; private set; }
    public int ResultDeviceId { get; private set; } = -1;
    public bool ResultReturnEnabled { get; private set; }
    public int ResultReturnSeconds { get; private set; } = DefaultReturnSeconds;
    public bool ResultForegroundOnly { get; private set; }

    /// <summary>True when the user chose "show the profile only when selected": the caller then
    /// keeps the reserved slot but does not arm the launch watcher for it. Only meaningful once
    /// the dialog returned true.</summary>
    public bool ResultActivationManual { get; private set; }

    /// <summary>The executable the user pinned this profile to, or null for "let K2 find it".
    /// Only meaningful once the dialog returned true.</summary>
    public string? ResultExePath { get; private set; }

    /// <summary>The edited pages, only meaningful once the dialog returned true.</summary>
    public IReadOnlyList<IReadOnlyList<GameProfileCatalog.Tile>> ResultPages => _pages;

    public GameProfileConfigDialog(string profileId,
                                   string profileName,
                                   bool enabled,
                                   int deviceId,
                                   IReadOnlyList<DpDeviceItem> devices,
                                   bool returnEnabled,
                                   int returnSeconds,
                                   bool foregroundOnly,
                                   IReadOnlyList<IReadOnlyList<GameProfileCatalog.Tile>> pages,
                                   string? gameIconPath = null,
                                   string? exePathOverride = null,
                                   string? autoResolvedExe = null,
                                   string? autoExeName = null,
                                   bool activationManual = false)
    {
        InitializeComponent();

        _profileId = profileId;
        // Identity for the action picker's top level: the game's own name and icon replace the
        // generic "Game controls" card there (see ButtonActionDialog.GamePickerProfile).
        _pickerProfile = new ButtonActionDialog.GamePickerProfile(profileId, profileName, gameIconPath);
        TxtTitle.Text = profileName;
        CkEnabled.IsChecked = enabled;
        CkReturn.IsChecked = returnEnabled;
        TxtReturnSec.Text = returnSeconds.ToString(CultureInfo.InvariantCulture);
        CkForeground.IsChecked = foregroundOnly;

        RbModeSelect.IsChecked = activationManual;
        RbModeLaunch.IsChecked = !activationManual;
        ApplyModeVisibility();

        _autoResolvedExe = autoResolvedExe;
        _autoExeName = autoExeName ?? "";
        TxtExePath.Text = exePathOverride ?? "";
        UpdateExeAutoHint();

        // Working copy: the dialog must be cancellable without having mutated the catalogue.
        _pages = pages.Select(p => p.ToList()).ToList();
        if (_pages.Count == 0) _pages.Add(new List<GameProfileCatalog.Tile>());

        // One pad => no choice to present. See the class remarks.
        if (devices.Count > 1)
        {
            CbDevice.ItemsSource = devices;
            CbDevice.SelectedItem = devices.FirstOrDefault(d => d.SdkId == deviceId) ?? devices[0];
        }
        else
        {
            PnlDevice.Visibility = Visibility.Collapsed;
            ResultDeviceId = devices.Count == 1 ? devices[0].SdkId : deviceId;
        }

        ShowPage(0);
    }

    // ─────────────────────────── Per-game guide ───────────────────────────

    /// <summary>Opens the shared <see cref="GuideWindow"/> on this game's own guide block
    /// (<c>gameprofile:&lt;id&gt;</c> in <c>Guides/guide.&lt;lang&gt;.md</c>) — what the keys do,
    /// how the tiles behave, the known limits, and anything to enable inside the game. Same button
    /// and same window as the Discord / Spotify config popups.</summary>
    private void BtnGuide_Click(object sender, RoutedEventArgs e) =>
        new GuideWindow("gameprofile:" + _profileId, TxtTitle.Text) { Owner = this }.ShowDialog();

    // ─────────────────────────── Activation mode ───────────────────────────

    private void Mode_Changed(object sender, RoutedEventArgs e) => ApplyModeVisibility();

    /// <summary>The executable, return-timer and foreground rows are sub-options of "show when the
    /// app opens" — they mean nothing for a profile that only ever shows when picked by hand, so
    /// they are hidden in that mode. The window is <c>SizeToContent="Height"</c>, so it reflows.</summary>
    private void ApplyModeVisibility()
    {
        var vis = RbModeSelect.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        PnlExeRow.Visibility = vis;
        PnlReturnRow.Visibility = vis;
        PnlForegroundRow.Visibility = vis;
    }

    // ─────────────────────────── Executable override ───────────────────────────

    /// <summary>What "auto" resolves to on this machine right now (null when nothing was found),
    /// and the catalogue's process name — shown under the box only while no override is set, so
    /// the user can tell whether overriding is even needed.</summary>
    private readonly string? _autoResolvedExe;
    private readonly string _autoExeName = "";

    private void UpdateExeAutoHint()
    {
        bool overridden = !string.IsNullOrWhiteSpace(TxtExePath.Text);
        TxtExeAuto.Visibility = overridden ? Visibility.Collapsed : Visibility.Visible;
        if (overridden) return;

        TxtExeAuto.Text = _autoResolvedExe is { Length: > 0 }
            ? string.Format(Loc.Get("game_profile_exe_auto_fmt"), _autoResolvedExe)
            : string.Format(Loc.Get("game_profile_exe_auto_none_fmt"), _autoExeName);
    }

    private void TxtExePath_TextChanged(object sender, TextChangedEventArgs e) => UpdateExeAutoHint();

    private void BtnExeBrowse_Click(object sender, RoutedEventArgs e)
    {
        // Opens where the current value points (or where auto found the game) rather than at the
        // last shell folder: a game's install directory is rarely somewhere you were just at.
        string? start = null;
        string current = TxtExePath.Text.Trim();
        string? seed = current.Length > 0 ? current : _autoResolvedExe;
        try { if (!string.IsNullOrEmpty(seed)) start = Path.GetDirectoryName(seed); } catch { }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("game_profile_exe_picker_title"),
            Filter = "Applications (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (start is not null && Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) != true) return;

        TxtExePath.Text = dlg.FileName;
    }

    /// <summary>Back to "let K2 find it" — an empty box IS the auto mode, there is no third state.</summary>
    private void BtnExeClear_Click(object sender, RoutedEventArgs e) => TxtExePath.Text = "";

    // Key geometry of the real device, mirrored from DpRebuildKeyGrid so the preview lines up
    // with the picture behind it. Kept as plain constants rather than shared: MainWindow's are
    // private to that partial, and duplicating four numbers beats widening its surface.
    private const double KeyW = 60, KeyH = 60, GapH = 8, GapV = 10;
    private const double AreaLeft = 55, AreaRight = 455, AreaTop = 130, AreaBottom = 330;

    private void ShowPage(int index)
    {
        _pageIndex = Math.Clamp(index, 0, _pages.Count - 1);
        var page = _pages[_pageIndex];

        CvsGameKeys.Children.Clear();

        const int rows = 2, cols = 6;
        double totalW = cols * KeyW + (cols - 1) * GapH;
        double totalH = rows * KeyH + (rows - 1) * GapV;
        double startX = AreaLeft + ((AreaRight - AreaLeft) - totalW) / 2;
        double startY = AreaTop + ((AreaBottom - AreaTop) - totalH) / 2;

        for (int i = 0; i < 12; i++)
        {
            var tile = i < page.Count ? page[i] : null;
            var btn = BuildKeyButton(i, tile);
            Canvas.SetLeft(btn, startX + (i % cols) * (KeyW + GapH));
            Canvas.SetTop(btn, startY + (i / cols) * (KeyH + GapV));
            CvsGameKeys.Children.Add(btn);
        }

        TxtPage.Text = string.Format(Loc.Get("game_profile_page_fmt"), _pageIndex + 1, _pages.Count);
        BtnPagePrev.IsEnabled = _pageIndex > 0;
        BtnPageNext.IsEnabled = _pageIndex < _pages.Count - 1;

        // Paging chrome only exists once there is something to page through.
        PnlPageNav.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        // A game whose layout is fixed (Zero Company's tactical grid) doesn't offer extra pages —
        // "Remove" still shows for any stale page left over from before that rule, so they can be
        // cleared.
        BtnPageAdd.Visibility = GameProfileSpecs.AllowsExtraPages(_profileId)
            ? Visibility.Visible : Visibility.Collapsed;
        // Only pages the USER added can be removed; the catalogue's own pages are the profile.
        BtnPageRemove.Visibility = _pageIndex >= ShippedPageCount ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How many pages this profile ships with — everything past them was added here and
    /// is the user's to delete again.</summary>
    private int ShippedPageCount =>
        GameProfileCatalog.ById(_profileId)?.Pages.Count ?? 1;

    private void BtnPageAdd_Click(object sender, RoutedEventArgs e)
    {
        _pages.Add(GameProfileCatalog.EmptyPage().ToList());
        ShowPage(_pages.Count - 1);
    }

    private void BtnPageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex < ShippedPageCount) return;
        _pages.RemoveAt(_pageIndex);
        ShowPage(Math.Min(_pageIndex, _pages.Count - 1));
    }

    /// <summary>One key of the preview: the very picture the pad will show, drawn by the same
    /// default-icon renderer the hardware path uses, with the caption underneath as a fallback
    /// for an action that has no icon to generate (an unset key, say).</summary>
    private Button BuildKeyButton(int index, GameProfileCatalog.Tile? tile)
    {
        var content = new Grid();

        // Same caption resolution and same TextOnly/background rules EnsureGameSlot uses, so the
        // preview is a preview and not an approximation — right down to the square annunciator
        // being gone for the styled tiles. The accent is deliberately fixed to the resting
        // orange rather than the live ship state (LiveTileRenderer.EdAccentOverride /
        // GameProfileTheme's *ForPreview members): editing the profile should look the same
        // whether or not Elite happens to be running with assist on at that moment.
        LiveTileRenderer.EdAccentOverride = Services.GameProfileTheme.EliteOrange;
        bool eliteStyled = GameProfileCatalog.IsStyledTile(_profileId, tile);

        string? png = tile is null || string.IsNullOrEmpty(tile.ActionType)
            ? null
            : Services.DpDefaultIconRenderer.Render(
                tile.ActionType, tile.ActionValue,
                new KeyIconSpec
                {
                    DefaultIcon = true,
                    ShowText = true,
                    Text = GameProfileCatalog.CaptionOf(tile),
                    TextColor = eliteStyled
                        ? Services.GameProfileTheme.TextHexForPreview(_profileId, lit: true)
                        : Services.GameProfileTheme.AccentHex(_profileId),
                    TextOnly = eliteStyled,
                    BgImagePath = eliteStyled
                        ? Services.GameProfileTheme.BgImagePathForPreview(_profileId, lit: true)
                        : null,
                    // The caption size the user picked in "Edit icon" — the preview renders at the
                    // pad's own 102 px, so the stored pixel size means the same thing here.
                    FontSize = tile?.FontSize ?? 0,
                },
                pageName: null, iconSize: 102);

        if (png is not null && File.Exists(png))
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            // OnLoad + a cloned stream: the file is regenerated as the user edits keys, and the
            // default (delayed, file-locking) load would both pin it and show a stale frame.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(png);
            bmp.EndInit();
            img.Source = bmp;
            content.Children.Add(img);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = tile is null ? "" : GameProfileCatalog.CaptionOf(tile),
                Foreground = Brushes.White,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var btn = new Button
        {
            Width = KeyW,
            Height = KeyH,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x10)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = index,
            Content = content,
            ToolTip = tile is null || string.IsNullOrEmpty(tile.ActionType)
                ? null
                : ActionTypeHelper.Summary(tile.ActionType, tile.ActionValue),
        };
        // The keys are built in code, so the handler has to be attached here — the XAML never sees
        // them. Without this the whole grid was inert: BtnTile_Click existed but nothing raised it.
        btn.Click += BtnTile_Click;

        // Same gestures as the DisplayPad's own key grid: drag a key onto another to swap them,
        // right-click for copy/cut/paste (through the app-wide ActionClipboard, so a key can be
        // carried between a game profile and an ordinary one).
        btn.AllowDrop = true;
        btn.PreviewMouseLeftButtonDown += Tile_PreviewMouseLeftButtonDown;
        btn.PreviewMouseMove += Tile_PreviewMouseMove;
        btn.DragEnter += Tile_DragEnter;
        btn.DragLeave += Tile_DragLeave;
        btn.Drop += Tile_Drop;
        btn.ContextMenu = BuildTileMenu(index);
        return btn;
    }

    // ── Drag & drop + clipboard, mirroring MainWindow.DisplayPad's key grid ──────────────

    private const string TileDragFormat = "K2.GameProfileTile";
    private static readonly GameProfileCatalog.Tile BlankTile = new("", "", "", CaptionIsLocKey: false);

    private Point _dragStart;
    private int _dragIndex = -1;

    /// <summary>The tile at that slot on the current page, or null when the slot is past the end
    /// (a page shorter than twelve) — the grid always draws twelve buttons regardless.</summary>
    private GameProfileCatalog.Tile? TileAt(int index)
    {
        var page = _pages[_pageIndex];
        return index >= 0 && index < page.Count ? page[index] : null;
    }

    private static bool IsMapped(GameProfileCatalog.Tile? t) => !string.IsNullOrEmpty(t?.ActionType);

    /// <summary>The label a freshly chosen action writes on its own tile — these keys carry no
    /// glyph, so the name IS the icon and it has to read like the shipped ones rather than like a
    /// debug summary.
    ///
    /// <para>A Game-controls preset knows its own name ("Frame Shift Drive" instead of the bare
    /// "J" its keystroke would give), and a live tile uses the pad's own short cockpit wording
    /// ("GEAR", "FA ON") — the same text the renderer falls back to, so a key picked from the
    /// browser looks like one that shipped with the profile. Anything else falls back to the
    /// ordinary action summary.</para></summary>
    private static string DefaultCaptionFor(string? actionType, string? actionValue)
    {
        if (string.IsNullOrEmpty(actionType)) return "";

        if (ButtonActionDialog.GameShortcutName(actionType, actionValue) is { } presetName)
            return presetName;

        if (Services.DpLiveTileService.IsLiveType(actionType))
        {
            string tile = Services.DpLiveTileService.TileCaption(actionType, actionValue);
            if (!string.IsNullOrWhiteSpace(tile)) return tile;
        }

        return ActionTypeHelper.Summary(actionType, actionValue);
    }

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragIndex = (sender as Button)?.Tag as int? ?? -1;
    }

    private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragIndex < 0) return;
        // An empty slot has nothing to carry; dragging one would just be a click that missed.
        if (!IsMapped(TileAt(_dragIndex))) { _dragIndex = -1; return; }
        if (!DragDropHelper.ExceedsDragThreshold(_dragStart, e.GetPosition(null))) return;

        int source = _dragIndex;
        _dragIndex = -1;
        DragDrop.DoDragDrop((Button)sender, new DataObject(TileDragFormat, source), DragDropEffects.Move);
    }

    private void Tile_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(TileDragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, true);
    }

    private void Tile_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
    }

    private void Tile_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (sender is not Button { Tag: int target }) return;
        if (!e.Data.GetDataPresent(TileDragFormat)) return;
        if (e.Data.GetData(TileDragFormat) is not int source || source == target) return;

        // Deferred for the same reason the DisplayPad grid defers its swap: this handler runs
        // inside DoDragDrop's own nested message pump, and rebuilding the canvas from in there
        // doesn't reliably get a clean repaint before the pump unwinds.
        Dispatcher.BeginInvoke(() => SwapTiles(source, target));
    }

    private void SwapTiles(int a, int b)
    {
        var page = _pages[_pageIndex];
        while (page.Count <= Math.Max(a, b)) page.Add(BlankTile);
        (page[a], page[b]) = (page[b], page[a]);
        ShowPage(_pageIndex);
    }

    private ContextMenu BuildTileMenu(int index)
    {
        var copy   = new MenuItem { Header = Loc.Get("act_copy_action") };
        var cut    = new MenuItem { Header = Loc.Get("act_cut_action") };
        var paste  = new MenuItem { Header = Loc.Get("act_paste_action") };
        var remove = new MenuItem { Header = Loc.Get("dp_remove_action") };

        copy.Click   += (_, _) => CopyTile(index, cut: false);
        cut.Click    += (_, _) => CopyTile(index, cut: true);
        paste.Click  += (_, _) => PasteTile(index);
        remove.Click += (_, _) => ClearTile(index);

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(cut);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        // Resolved on open rather than at build time: the grid is rebuilt on every ShowPage, but
        // the clipboard can be filled from another window in between.
        menu.Opened += (_, _) =>
        {
            bool mapped = IsMapped(TileAt(index));
            copy.IsEnabled = cut.IsEnabled = remove.IsEnabled = mapped;
            paste.IsEnabled = ActionClipboard.HasContent;
        };
        return menu;
    }

    /// <summary>Copy (or cut) a tile to the app-wide clipboard. The label rides along inside the
    /// icon spec, so pasting keeps the wording instead of falling back to the action's summary.</summary>
    private void CopyTile(int index, bool cut)
    {
        if (TileAt(index) is not { } t || !IsMapped(t)) return;

        ActionClipboard.Copy(t.ActionType, t.ActionValue, null,
            new KeyIconSpec
            {
                DefaultIcon = true,
                ShowText = true,
                Text = GameProfileCatalog.CaptionOf(t),
            }.ToJson());

        if (!cut) return;
        ClearTile(index);
    }

    /// <summary>Empties a slot: the key goes back to dark and unmapped, the same state a page
    /// starts in. Shared by "Cut" and "Remove action".</summary>
    private void ClearTile(int index)
    {
        var page = _pages[_pageIndex];
        if (index < 0 || index >= page.Count) return;
        page[index] = BlankTile;
        ShowPage(_pageIndex);
    }

    private void PasteTile(int index)
    {
        if (!ActionClipboard.HasContent) return;

        // The clipboard bypasses the picker entirely, so the game profile's own restriction has
        // to be re-applied here — otherwise a key copied from an ordinary profile could smuggle
        // in an action type this editor deliberately doesn't offer.
        if (!ButtonActionDialog.IsTypeInCategories(ActionClipboard.ActionType, GameProfileActionCategories))
        {
            MessageBox.Show(this, Loc.Get("game_profile_paste_unsupported"),
                            Loc.Get("act_paste_action"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var page = _pages[_pageIndex];
        while (page.Count <= index) page.Add(BlankTile);

        string? label = KeyIconSpec.FromJson(ActionClipboard.IconSpecJson)?.Text;
        string caption = !string.IsNullOrWhiteSpace(label)
            ? label!.Trim()
            : DefaultCaptionFor(ActionClipboard.ActionType, ActionClipboard.ActionValue);

        page[index] = new GameProfileCatalog.Tile(
            ActionClipboard.ActionType ?? "", ActionClipboard.ActionValue ?? "",
            caption, CaptionIsLocKey: false,
            FontSize: KeyIconSpec.FromJson(ActionClipboard.IconSpecJson)?.FontSize ?? 0);
        ShowPage(_pageIndex);
    }

    private void BtnPagePrev_Click(object sender, RoutedEventArgs e) => ShowPage(_pageIndex - 1);
    private void BtnPageNext_Click(object sender, RoutedEventArgs e) => ShowPage(_pageIndex + 1);

    /// <summary>What a game profile's key may be mapped to: ordinary input (keystrokes, macros,
    /// media), the game-specific commands, and the live tiles. Everything else — launching
    /// programs, opening folders, Spotify — belongs to a normal profile, not to a page that takes
    /// over the pad while a game is in the foreground.
    ///
    /// <para>"live" is here for the SCREEN READINGS (<c>dp_screen</c>): a game that reports
    /// nothing back can still have real gauges on the pad, read off its own HUD, and a game page
    /// is exactly where those belong. The clock and the PC-monitor tiles come along with the
    /// category, which is no loss — a CPU temperature next to the game's own keys is a reasonable
    /// thing to want while playing.</para></summary>
    private static readonly string[] GameProfileActionCategories = { "input", "games", "live" };

    private void BtnTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;

        var page = _pages[_pageIndex];
        while (page.Count <= index) page.Add(new GameProfileCatalog.Tile("", "", ""));
        var current = page[index];

        // Seed the key dialog's own caption field with what the tile shows today, so its "Edit
        // icon" text box IS the tile's label editor — the user renames the key where they are
        // already standing instead of in a second prompt of our own.
        //
        // The styling rides along (TextOnly + the frame art + the accent), otherwise that dialog's
        // icon preview would show a generic glyph tile for a key the pad draws as a backlit label.
        string shownCaption = GameProfileCatalog.CaptionOf(current);
        bool styled = GameProfileCatalog.IsStyledTile(_profileId, current);
        string seedSpec = new KeyIconSpec
        {
            DefaultIcon = true,
            ShowText = true,
            Text = shownCaption,
            TextOnly = styled,
            TextColor = styled
                ? Services.GameProfileTheme.TextHexForPreview(_profileId, lit: true)
                : null,
            BgImagePath = styled
                ? Services.GameProfileTheme.BgImagePathForPreview(_profileId, lit: true)
                : null,
            // So "Edit icon" opens on the size this tile already wears instead of back on auto.
            FontSize = current.FontSize,
        }.ToJson();

        var dlg = new DpKeyConfigDialog(index, null, current.ActionType, current.ActionValue, seedSpec,
                                        GameProfileActionCategories, _pickerProfile)
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true) return;

        // "No action" is how a slot goes back to being empty: an unmapped key must end up truly
        // blank (dark, no lit frame), not carrying a "none" action that still reads as mapped.
        if (string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none")
        {
            page[index] = new GameProfileCatalog.Tile("", "", "", CaptionIsLocKey: false);
            ShowPage(_pageIndex);
            return;
        }

        // A text-only tile IS its label, so the wording matters more here than on an ordinary key.
        // Order of precedence: what the user typed wins; otherwise a NEW action names itself (the
        // old label would be a leftover describing something the key no longer does); otherwise
        // the existing wording stands.
        var editedSpec = KeyIconSpec.FromJson(dlg.IconSpecJson);
        string? edited = editedSpec?.Text?.Trim();
        bool renamed = !string.IsNullOrWhiteSpace(edited) && edited != shownCaption;
        bool actionChanged = current.ActionType != dlg.ActionType
                          || current.ActionValue != (dlg.ActionValue ?? "");

        string caption =
            renamed                                    ? edited!
          : actionChanged                              ? DefaultCaptionFor(dlg.ActionType, dlg.ActionValue)
          : !string.IsNullOrWhiteSpace(shownCaption)   ? shownCaption
          : DefaultCaptionFor(dlg.ActionType, dlg.ActionValue);

        page[index] = current with
        {
            ActionType = dlg.ActionType,
            ActionValue = dlg.ActionValue ?? "",
            // It is now the user's own tile: freeze the resolved wording instead of a loc key that
            // would re-translate under them.
            Caption = caption,
            CaptionIsLocKey = false,
            // Caption size from the same "Edit icon" pass; 0 (auto) when the dialog was left on
            // shrink-to-fit, which is what a tile that was never touched has too.
            FontSize = editedSpec?.FontSize ?? 0,
        };
        ShowPage(_pageIndex);
    }

    /// <summary>Puts every page back to what the catalogue ships — the escape hatch after an
    /// experiment, since a game profile's default mapping is curated rather than user-built.</summary>
    private void BtnResetTiles_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.Get("game_profile_reset_tiles_confirm"),
                            Loc.Get("game_profile_reset_tiles"),
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        ResetRequested = true;
        DialogResult = true;
    }

    /// <summary>Set when the user asked for the shipped mapping back; the caller drops its saved
    /// tile overrides instead of writing <see cref="ResultPages"/>.</summary>
    public bool ResetRequested { get; private set; }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ResultEnabled = CkEnabled.IsChecked == true;
        ResultReturnEnabled = CkReturn.IsChecked == true;
        ResultForegroundOnly = CkForeground.IsChecked == true;
        ResultActivationManual = RbModeSelect.IsChecked == true;

        ResultReturnSeconds =
            int.TryParse(TxtReturnSec.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
            && s > 0 ? s : DefaultReturnSeconds;

        if (CbDevice.SelectedItem is DpDeviceItem picked) ResultDeviceId = picked.SdkId;

        // A path that no longer exists is worse than no path: the watcher would arm on a process
        // name nothing produces and the card would lose its icon, with nothing on screen saying
        // why. Refuse the save and leave the user in the dialog to fix it.
        string exe = TxtExePath.Text.Trim().Trim('"');
        // In "show only when selected" mode the executable is not used to arm anything, so a stale
        // path there must not block the save — it is kept as-is for if the user switches back.
        if (!ResultActivationManual && exe.Length > 0 && !File.Exists(exe))
        {
            MessageBox.Show(this, string.Format(Loc.Get("file_not_found_fmt"), exe),
                            Loc.Get("game_profile_config_title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtExePath.Focus();
            return;
        }
        ResultExePath = exe.Length > 0 ? exe : null;

        DialogResult = true;
    }

}
