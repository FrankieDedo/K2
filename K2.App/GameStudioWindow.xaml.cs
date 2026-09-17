using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// The game studio: where the user BUILDS a game profile and the tiles that go on it, instead of
/// only configuring one K2 shipped.
///
/// <para>
/// <b>A tile is three layers.</b> The ACTION is its identity (name, profile, family) and the
/// background it is drawn on. Its READINGS are what it knows how to measure — one probe each, and
/// a tile can have several. Its ELEMENTS are what is actually drawn: pictures, indicators,
/// readings and labels, any number of each, every one placed and coloured on its own and pointing
/// at a reading when it shows one. The three lists in the Actions tab are exactly those layers.
/// </para>
///
/// <para>
/// <b>Shipped profiles are here too.</b> Elite Dangerous and the rest appear in the same list with
/// their identity locked — name, process and Steam id are part of K2, not user data. Everything
/// else about them is fair game: their keys (written through the very store the Configure popup
/// uses, so the two never disagree), their tile colours and frame art, and the user's own actions,
/// which can be filed into the game's own command families.
/// </para>
///
/// <para>
/// <b>Everything saves as you go.</b> There is no OK/Cancel: an edit is written as it is made, the
/// way a document editor works. The one thing that IS a decision — deleting a profile, an action or
/// a reading — asks first.
/// </para>
/// </summary>
public partial class GameStudioWindow : Window
{
    /// <summary>Id of the action the caller should bind a key to: the one last saved here, "" when
    /// it was deleted, or null when nothing was touched. Only the key-dialog path reads it.</summary>
    public string? SavedActionId { get; private set; }

    /// <summary>Blocks the field handlers while the window populates its own controls — every one
    /// of them writes to the store, and loading a profile into the form would otherwise save it
    /// back field by field.</summary>
    private bool _loading = true;

    /// <summary>One row of the profile list. A shipped profile is named with a badge so the list
    /// says at a glance which half of it is the user's.</summary>
    private sealed record ProfileRow(string Id, string Name, bool IsCustom)
    {
        public override string ToString() =>
            IsCustom ? Name : $"{Name} ({Loc.Get("studio_builtin")})";
    }

    /// <summary>One row of the reading list.</summary>
    private sealed record ReadingRow(TileReading Reading, string Label);

    /// <summary>What a row of the navigation stands for.</summary>
    public enum NavKind { Header, Profile, Action, NewAction, NewProfile }

    /// <summary>
    /// One row of the navigation — the studio's ONLY list of profiles and actions. It is flat on
    /// purpose: group headings are rows (so the whole thing scrolls as one), and a profile's actions
    /// are listed under it only while that profile is the open one. That is what makes the profile
    /// the context of everything on the right, instead of the separate "belongs to / show all"
    /// filter the actions used to have.
    /// </summary>
    /// <param name="Id">Profile id for Profile/NewAction rows, action id for Action rows.</param>
    public sealed record NavRow(NavKind Kind, string Id, string Label, string Glyph, int Level)
    {
        public bool IsHeader => Kind == NavKind.Header;
        public bool IsCommand => Kind is NavKind.NewAction or NavKind.NewProfile;
        public Thickness Indent => new(Level * 16, 0, 0, 0);
        public double GlyphWidth => Glyph.Length > 0 ? 20 : 0;
    }

    private const string GlyphGame = "", GlyphLock = "", GlyphAction = "", GlyphAdd = "";

    /// <summary>One row of the element list: what the piece is, what it shows, and the colour its
    /// outline wears on the preview. Everything else about the piece is edited in the panel under
    /// the list, for the selected one only.</summary>
    public sealed class ElementRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private string _kind = "", _detail = "";
        private Brush _colour = Brushes.Gray;

        public string Kind { get => _kind; set => Set(ref _kind, value); }
        public string Detail { get => _detail; set => Set(ref _detail, value); }
        public Brush Colour { get => _colour; set => Set(ref _colour, value); }
    }

    private readonly ObservableCollection<ProfileRow> _rows = new();
    private readonly ObservableCollection<NavRow> _nav = new();
    private readonly ObservableCollection<ReadingRow> _readingRows = new();
    private readonly ObservableCollection<ElementRow> _elementRows = new();

    /// <summary>What the selected element's two combos offer. The readings follow the tile's own
    /// (so renaming one relabels them), the shapes never change.</summary>
    private readonly ObservableCollection<Item<string>> _readingOptions = new();
    private readonly ObservableCollection<Item<CustomIndicatorShape>> _shapeOptions = new();
    private readonly ObservableCollection<CustomActionState> _states = new();

    private ProfileRow? _row;

    /// <summary>The selected profile when it is the user's own; null on a shipped one.</summary>
    private CustomGameProfile? _profile;

    /// <summary>The selected profile when it is one K2 ships; null on a custom one.</summary>
    private GameProfileDefinition? _builtin;

    /// <summary>A shipped profile's pages as they stand on this machine (catalogue + the user's
    /// per-key overrides). Loaded through <see cref="MainWindow.LoadPages"/> and written back with
    /// <see cref="MainWindow.SavePages"/>, which is where the Configure popup keeps them too.</summary>
    private List<List<GameProfileTile>>? _builtinPages;

    private CustomGameAction? _action;

    /// <summary>Working copies of the selected tile's two lists. Everything the user does edits
    /// these, and <see cref="SaveAction"/> writes them back as a whole.</summary>
    private List<TileReading> _readings = new();
    private List<TileElement> _elements = new();

    private int _readingIndex = -1;
    private int _elementIndex = -1;

    private DispatcherTimer? _previewTimer;
    private readonly string _previewPath =
        Path.Combine(Path.GetTempPath(), "K2.Studio", $"preview_{Guid.NewGuid():N}.png");

    private int _pageIndex;

    /// <summary>The main window, which owns the DisplayPad store a shipped profile's keys live in.
    /// Null in the odd case where the studio outlives it — the shipped profiles are then listed but
    /// their keys cannot be edited, which the hint under the pad says.</summary>
    private static MainWindow? Host =>
        Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();

    public GameStudioWindow() : this(null) { }

    /// <param name="focusActionId">Action to open on. Empty/"" means "create a new one" — the key
    /// dialog's "New action…" card comes in that way, and landing on a form the user still has to
    /// find the New button for would be one step too many.</param>
    /// <param name="focusProfileId">Profile to open on (custom or shipped). Ignored when
    /// <paramref name="focusActionId"/> asks for something, since that opens its own profile.</param>
    public GameStudioWindow(string? focusActionId, string? focusProfileId = null)
    {
        InitializeComponent();

        LstNav.ItemsSource = _nav;
        LstReadings.ItemsSource = _readingRows;
        LstElements.ItemsSource = _elementRows;
        LstStates.ItemsSource = _states;
        CbElReading.ItemsSource = _readingOptions;
        CbElShape.ItemsSource = _shapeOptions;

        CvsHandles.SizeChanged += (_, _) => RefreshHandles();
        // Enter inside a field commits just that field (the handlers listen to LostFocus) and
        // keeps the studio open — Close is deliberately not IsDefault.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || e.OriginalSource is not TextBox { AcceptsReturn: false } tb) return;
            tb.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, tb));
            tb.SelectAll();
            e.Handled = true;
        };
        FillStaticCombos();
        SetAdvancedOpen(false);
        ReloadProfiles();

        // A caller that asked for a specific action wins over the profile selection: OpenAction
        // opens the profile that owns it too.
        if (focusActionId is { Length: > 0 } &&
            CustomGameStore.ActionById(focusActionId) is { } wanted)
        {
            OpenAction(wanted.Id);
        }
        else if (focusActionId is not null)
        {
            // "" — the key dialog's "New action…" card.
            Dispatcher.BeginInvoke(new Action(() => NewAction(_row?.Id ?? "")), DispatcherPriority.Loaded);
        }
        else if (focusProfileId is { Length: > 0 })
        {
            OpenProfile(focusProfileId);
        }

        _loading = false;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _previewTimer.Tick += (_, _) =>
        {
            RefreshPreview();
            if (PnlScale.IsVisible) UpdateScaleEnds();
        };
        _previewTimer.Start();

        Closed += (_, _) =>
        {
            _previewTimer?.Stop();
            _previewTimer = null;
            try { if (File.Exists(_previewPath)) File.Delete(_previewPath); } catch { /* temp file */ }
        };
    }

    // ─────────────────────────── combos ───────────────────────────

    /// <summary>A combo entry that carries a value and prints a label — the shape every combo in
    /// this window uses, so the handlers can all read <c>SelectedItem</c> the same way.</summary>
    public sealed record Item<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private void FillStaticCombos()
    {
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.Value, Loc.Get("studio_kind_value")));
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.Range, Loc.Get("studio_kind_range")));
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.Number, Loc.Get("studio_kind_number")));
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.Text, Loc.Get("studio_kind_text")));
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.OnOff, Loc.Get("studio_kind_onoff")));
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.MultiState, Loc.Get("studio_kind_multi")));
        CbKind.Items.Add(new Item<CustomValueKind>(CustomValueKind.Mirror, Loc.Get("studio_kind_mirror")));

        CbSource.Items.Add(new Item<CustomSource>(CustomSource.Screen, Loc.Get("studio_source_screen")));
        CbSource.Items.Add(new Item<CustomSource>(CustomSource.Link, Loc.Get("studio_source_link")));
        CbSource.Items.Add(new Item<CustomSource>(CustomSource.ModLink, Loc.Get("studio_source_modlink")));

        HintKind.ToolTip = ChoicesTip(Loc.Get("screen_probe_mode_types"),
            CbKind.Items.OfType<Item<CustomValueKind>>().Select(i => (i.Label, Loc.Get(KindHintKey(i.Value)))));
        RefreshSourceTip();

        CbMultiMode.Items.Add(new Item<CustomMultiMode>(CustomMultiMode.Colors, Loc.Get("studio_multi_colors")));
        CbMultiMode.Items.Add(new Item<CustomMultiMode>(CustomMultiMode.Rects, Loc.Get("studio_multi_rects")));

        _shapeOptions.Add(new Item<CustomIndicatorShape>(CustomIndicatorShape.LinearHorizontal, Loc.Get("studio_shape_h")));
        _shapeOptions.Add(new Item<CustomIndicatorShape>(CustomIndicatorShape.LinearVertical, Loc.Get("studio_shape_v")));
        _shapeOptions.Add(new Item<CustomIndicatorShape>(CustomIndicatorShape.Circular, Loc.Get("studio_shape_circle")));
        _shapeOptions.Add(new Item<CustomIndicatorShape>(CustomIndicatorShape.None, Loc.Get("studio_shape_none")));

        // Families installed on THIS PC: the tile is drawn with System.Drawing, which can only use a
        // face Windows actually has — offering K2's own embedded UI fonts here would list names the
        // renderer then silently substitutes.
        CbFont.Items.Add(new Item<string>("", Loc.Get("studio_font_default")));
        foreach (string f in Fonts.SystemFontFamilies
                     .Select(f => f.Source)
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct()
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            CbFont.Items.Add(new Item<string>(f, f));
    }

    /// <summary>Process the action's profile watches, without extension — the spelling probes store
    /// in <see cref="ScreenProbe.Process"/>. Empty when the action belongs to no profile, or when
    /// the profile has no executable set yet.</summary>
    private string ActionProfileProcess()
    {
        string id = _action?.ProfileId ?? "";
        if (id.Length == 0) return "";
        if (CustomGameStore.ProfileById(id) is { } custom) return (custom.ExeName ?? "").Trim();
        return (GameProfileCatalog.ById(id)?.ExeName ?? "").Trim();
    }

    /// <summary>
    /// Refills the probe combo. Called on load and after every trip to the calibration window,
    /// since that is where probes are created and renamed.
    ///
    /// <para>
    /// The list is FILTERED to the readings taken from the profile's own process. The probe library
    /// is one flat list shared by every game, so on a machine with a few games set up an unfiltered
    /// combo is mostly entries that cannot apply to the tile being edited. Two things keep the
    /// filter from ever hiding something the user needs: the currently assigned probe is always
    /// listed, whatever its process (otherwise opening a reading would silently clear its source),
    /// and Browse — <see cref="BtnProbeBrowse_Click"/> — reaches the whole library. A profile with
    /// no process yet filters nothing, since there would be nothing to filter on.
    /// </para>
    /// </summary>
    private void FillProbes(string? select)
    {
        string process = ActionProfileProcess();

        CbProbe.Items.Clear();
        foreach (var p in ScreenProbeStore.All()
                     .Where(p => process.Length == 0
                                 || p.Id == select
                                 || string.Equals(p.Process, process, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            CbProbe.Items.Add(new Item<string>(p.Id, ProbeLabel(p)));

        CbProbe.SelectedItem = CbProbe.Items.OfType<Item<string>>().FirstOrDefault(i => i.Value == select);
        BtnProbeEdit.IsEnabled = BtnProbeDelete.IsEnabled = CbProbe.SelectedItem is not null;
    }

    /// <summary>"name - process": which window a reading is taken from is half its identity, and two
    /// games' HUDs very often carry the same reading name ("Health", "Ammo").</summary>
    private static string ProbeLabel(ScreenProbe p) =>
        string.IsNullOrWhiteSpace(p.Process) ? p.Name : Loc.Get("studio_probe_fmt", p.Name, p.Process);

    /// <summary>Offers the Telemachus source only where it belongs: on an action of the Kerbal Space
    /// Program profile — or on a reading that already uses it, which must never be shown with its
    /// source silently gone just because the action was moved to another profile.</summary>
    private void SyncTelemachusSource(bool readingUsesIt)
    {
        bool want = readingUsesIt ||
                    _action?.ProfileId == GameProfileSpecs.KspId;
        var existing = CbSource.Items.OfType<Item<CustomSource>>()
            .FirstOrDefault(i => i.Value == CustomSource.Telemachus);

        if (want && existing is null)
            CbSource.Items.Add(new Item<CustomSource>(CustomSource.Telemachus, Loc.Get("studio_source_telemachus")));
        else if (!want && existing is not null)
            CbSource.Items.Remove(existing);
        RefreshSourceTip();
    }

    /// <summary>The source "?" lists exactly the sources the combo offers — Telemachus only where
    /// it is in the list.</summary>
    private void RefreshSourceTip() =>
        HintSource.ToolTip = ChoicesTip(Loc.Get("studio_source_hint"),
            CbSource.Items.OfType<Item<CustomSource>>().Select(i => (i.Label, Loc.Get(i.Value switch
            {
                CustomSource.Link => "studio_source_link_hint",
                CustomSource.ModLink => "studio_source_modlink_hint",
                CustomSource.Telemachus => "studio_source_telemachus_hint",
                _ => "studio_source_screen_hint",
            }))));

    private static string KindHintKey(CustomValueKind kind) => kind switch
    {
        CustomValueKind.Range => "studio_kind_range_hint",
        CustomValueKind.Value => "studio_kind_value_hint",
        CustomValueKind.OnOff => "studio_kind_onoff_hint",
        CustomValueKind.Number => "studio_kind_number_hint",
        CustomValueKind.Text => "studio_kind_text_hint",
        CustomValueKind.Mirror => "studio_kind_mirror_hint",
        _ => "studio_kind_multi_hint",
    };

    /// <summary>A "?" tooltip naming every choice of a combo with what it does, same look as the
    /// screen probe's reading types.</summary>
    private static ToolTip ChoicesTip(string heading, IEnumerable<(string Name, string Hint)> choices)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var (name, hint) in choices)
        {
            panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var desc = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 8) };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "K2TextMutedBrush");
            panel.Children.Add(desc);
        }
        if (panel.Children[^1] is TextBlock last) last.Margin = new Thickness(0, 1, 0, 0);
        return new ToolTip { MaxWidth = 380, Content = panel };
    }

    /// <summary>The source the reading editor is on right now.</summary>
    private CustomSource CurrentSource() =>
        (CbSource.SelectedItem as Item<CustomSource>)?.Value ?? CustomSource.Screen;

    /// <summary>Which kind of link the chosen source asks for.</summary>
    private GameLinkKind CurrentLinkKind() =>
        CurrentSource().WantsMod() ? GameLinkKind.Mod : GameLinkKind.Native;

    /// <summary>Refills the link combo with the links OF THE CHOSEN KIND. Filtered rather than
    /// labelled: the source above already says whether a mod is involved, so a list mixing the two
    /// would be offering an answer to a question the user has already answered.
    ///
    /// <para>The link named by <paramref name="select"/> is listed whatever its kind — otherwise
    /// opening a reading could silently drop its source.</para></summary>
    private void FillLinks(string? select)
    {
        var want = CurrentLinkKind();
        bool kspAction = _action?.ProfileId == GameProfileSpecs.KspId;

        CbLink.Items.Clear();
        foreach (var l in GameLinkStore.All()
                     .Where(l => l.Kind == want || l.Id == select)
                     // A Telemachus link is Kerbal Space Program's alone: listed on that profile's
                     // actions, hidden everywhere else — unless a reading already uses it, which
                     // must not lose its source just by being opened.
                     .Where(l => !l.IsTelemachus || kspAction || l.Id == select)
                     .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
            CbLink.Items.Add(new Item<string>(l.Id, l.Name));

        CbLink.SelectedItem = CbLink.Items.OfType<Item<string>>().FirstOrDefault(i => i.Value == select);
        BtnLinkEdit.IsEnabled = BtnLinkDelete.IsEnabled = CbLink.SelectedItem is not null;
        UpdateLinkModButton();
    }

    /// <summary>Shows the "get the mod" button for a mod link that carries a page.</summary>
    private void UpdateLinkModButton()
    {
        var link = GameLinkStore.ById((CbLink.SelectedItem as Item<string>)?.Value);
        BtnLinkMod.Visibility = CurrentSource().WantsMod() && link is { ModUrl.Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnLinkMod_Click(object sender, RoutedEventArgs e)
    {
        var link = GameLinkStore.ById((CbLink.SelectedItem as Item<string>)?.Value);
        if (link is null || !GameLinkMod.Open(link.ModUrl))
            LblLinkStatus.Text = Loc.Get("link_mod_bad_url");
    }

    /// <summary>The line under the value box: what the link is saying right now, or why it is
    /// saying nothing. The value matters more than the path — a path that resolves to the wrong
    /// number looks perfectly right until you see the number.</summary>
    private void UpdateLinkStatus()
    {
        if (CurrentSource() == CustomSource.Telemachus)
        {
            UpdateTelemachusStatus(retry: true);
            return;
        }

        string linkId = (CbLink.SelectedItem as Item<string>)?.Value ?? "";
        if (linkId.Length == 0)
        {
            LblLinkStatus.Text = Loc.Get("studio_link_none");
            return;
        }

        var snapshot = GameLinkReader.Snapshot(linkId);
        if (!snapshot.Known)
        {
            // For a mod link "the game is not answering" is only half the story: the other half is
            // that the mod may never have been installed, or was wiped by the game's last update.
            bool mod = CurrentSource().WantsMod();
            LblLinkStatus.Text = snapshot.Error is { Length: > 0 } why
                ? Loc.Get(mod ? "studio_link_down_mod_fmt" : "studio_link_down_fmt", why)
                : Loc.Get(mod ? "studio_link_down_mod" : "studio_link_down");
            return;
        }

        string path = TxtValuePath.Text.Trim();
        LblLinkStatus.Text = path.Length == 0
            ? Loc.Get("studio_link_up_fmt", snapshot.Values.Count)
            : snapshot.Values.TryGetValue(path, out string? value)
                ? Loc.Get("studio_link_now_fmt", value)
                : Loc.Get("studio_link_missing");
    }

    /// <summary>The status line for a Telemachus reading. Asking for an entry only puts it in the
    /// next request, so the first look usually has nothing yet: <paramref name="retry"/> looks once
    /// more after a poll instead of leaving "no answer" on screen for a value that is on its way.</summary>
    private async void UpdateTelemachusStatus(bool retry)
    {
        string path = TxtValuePath.Text.Trim();
        var status = TelemachusClient.Want(path.Length > 0 ? new[] { path } : Array.Empty<string>());
        string? value = TelemachusClient.Text(status.Get(path));

        if (retry && (!status.Alive || (path.Length > 0 && value is null)))
        {
            await System.Threading.Tasks.Task.Delay(900);
            if (CurrentSource() == CustomSource.Telemachus) UpdateTelemachusStatus(retry: false);
            return;
        }

        LblLinkStatus.Text =
            !status.Alive ? Loc.Get("studio_tm_down")
            : path.Length == 0 ? Loc.Get("studio_tm_up")
            : value is not null ? Loc.Get("studio_link_now_fmt", value)
            : status.Paused != 0 && KspTelemachus.NeedsAntenna(path) ? Loc.Get("studio_tm_no_antenna")
            : Loc.Get("studio_link_missing");
    }

    /// <summary>The categories the action can be filed under: the studio's "Generic", the GAME's
    /// own (so a custom reading lands among its commands — Deadside's "Movement", Elite's
    /// "Gauges"), then the ones the user added in the profile's settings.</summary>
    private void FillCategories(string? profileId, string? select)
    {
        CbCategory.Items.Clear();
        foreach (var c in CategoriesOf(profileId))
            CbCategory.Items.Add(new Item<string>(c.Key, c.Name));

        CbCategory.SelectedItem = CbCategory.Items.OfType<Item<string>>()
            .FirstOrDefault(i => i.Value == (select ?? "")) ?? CbCategory.Items[0];
    }

    /// <summary>Every category a profile offers, in the order the pickers list them. Only the user's
    /// own are <c>Deletable</c>: Generic is where everything else falls back to, and a shipped
    /// game's categories are part of its action browser.</summary>
    private static IEnumerable<(string Key, string Name, bool Deletable)> CategoriesOf(string? profileId)
    {
        yield return ("", Loc.Get("studio_category_generic"), false);
        foreach (var fam in GameProfileSpecs.ById(profileId)?.CommandFamilies
                            ?? Array.Empty<(string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)>())
            yield return (fam.LocKey, Loc.Get(fam.LocKey), false);
        foreach (string name in CustomGameStore.CategoriesFor(profileId))
            yield return (CustomGameStore.CategoryKey(name), name, true);
    }

    /// <summary>The profile settings' category chips: a lock on the fixed ones, a delete button on
    /// the user's.</summary>
    private void RefreshCategories()
    {
        PnlCategories.Children.Clear();
        if (_row is null) return;

        foreach (var c in CategoriesOf(_row.Id))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(new TextBlock
            {
                Text = c.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("K2TextBrush"),
            });
            if (c.Deletable)
            {
                string name = c.Name;
                var delete = new Button
                {
                    Content = "",
                    Style = (Style)FindResource("StudioGlyphButton"),
                    Width = 20, Height = 18, FontSize = 9,
                    Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = Loc.Get("studio_category_delete"),
                };
                delete.Click += (_, _) => DeleteCategory(name);
                chip.Children.Add(delete);
            }
            else
            {
                chip.Children.Add(new TextBlock
                {
                    Text = "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("K2TextMutedBrush"),
                    ToolTip = Loc.Get("studio_category_locked"),
                });
            }

            PnlCategories.Children.Add(new Border
            {
                Child = chip,
                CornerRadius = new CornerRadius(12),
                Background = (Brush)FindResource("K2SurfaceAltBrush"),
                BorderBrush = (Brush)FindResource("K2BorderBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 3, c.Deletable ? 4 : 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
                MinHeight = 26,
            });
        }
    }

    private void TxtNewCategory_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddCategory();
        e.Handled = true;
    }

    private void BtnCategoryAdd_Click(object sender, RoutedEventArgs e) => AddCategory();

    private void AddCategory()
    {
        if (_row is null) return;
        string name = TxtNewCategory.Text.Trim();
        if (name.Length == 0) return;

        // Two headings with the same name would read as one in the action browser.
        if (CategoriesOf(_row.Id).Any(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            MessageBox.Show(this, string.Format(Loc.Get("studio_category_exists"), name), Loc.Get("studio_title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CustomGameStore.AddCategory(_row.Id, name);
        TxtNewCategory.Clear();
        RefreshCategories();
    }

    private void DeleteCategory(string name)
    {
        if (_row is null) return;
        int used = CustomGameStore.ActionsFor(_row.Id)
            .Count(a => a.Category == CustomGameStore.CategoryKey(name));
        if (used > 0 &&
            MessageBox.Show(this, string.Format(Loc.Get("studio_category_delete_confirm"), name, used),
                            Loc.Get("studio_title"), MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        CustomGameStore.DeleteCategory(_row.Id, name);
        RefreshCategories();
    }

    /// <summary>The readings an element can point at — the tile's own, plus a "none" for a picture
    /// that just wears its PNG.</summary>
    private void FillElementReadings()
    {
        _readingOptions.Clear();
        _readingOptions.Add(new Item<string>("", Loc.Get("studio_el_reading_none")));
        foreach (var r in _readings)
            _readingOptions.Add(new Item<string>(r.Id, r.Name));
    }

    // ─────────────────────────── profiles ───────────────────────────

    private void ReloadProfiles()
    {
        string? keep = _row?.Id;
        _rows.Clear();
        foreach (var p in CustomGameStore.Profiles())
            _rows.Add(new ProfileRow(p.Id, p.Name, IsCustom: true));
        // The shipped catalogue, in its own order, after the user's own: this window is about what
        // the user makes, and the built-ins are here to be extended, not to headline the list.
        foreach (var def in GameProfileCatalog.All.Where(d => !CustomGameProfile.IsCustomId(d.Id)))
            _rows.Add(new ProfileRow(def.Id, def.Name, IsCustom: false));

        _row = null;
        OpenProfile(keep ?? _rows.FirstOrDefault()?.Id);
    }

    // ─────────────────────────── navigation ───────────────────────────

    private enum StudioView { None, Profile, Action }
    private StudioView _view;

    /// <summary>Set while <see cref="ReloadNav"/> rebuilds the list, so the selection events that
    /// causes are not taken for the user picking a row.</summary>
    private bool _navRefresh;

    /// <summary>
    /// Rebuilds the navigation around what is open: every profile, the open one's actions (plus a
    /// "+ New action" row) under it, and — only when there are some — the actions that belong to no
    /// profile at all, which have nowhere else to be found. Then re-selects the row of whatever the
    /// right side is showing. Cheap enough to call after every rename.
    /// </summary>
    private void ReloadNav()
    {
        var actions = CustomGameStore.Actions();

        _navRefresh = true;
        _nav.Clear();

        void AddProfiles(IEnumerable<ProfileRow> rows, string glyph)
        {
            foreach (var r in rows)
            {
                _nav.Add(new NavRow(NavKind.Profile, r.Id, r.Name, glyph, 0));
                if (r.Id != _row?.Id) continue;
                foreach (var a in actions.Where(a => a.ProfileId == r.Id)
                                         .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                    _nav.Add(new NavRow(NavKind.Action, a.Id, a.Name, GlyphAction, 1));
                _nav.Add(new NavRow(NavKind.NewAction, r.Id, Loc.Get("studio_nav_new_action"), GlyphAdd, 1));
            }
        }

        _nav.Add(new NavRow(NavKind.Header, "", Loc.Get("studio_profiles"), "", 0));
        AddProfiles(_rows.Where(r => r.IsCustom), GlyphGame);
        _nav.Add(new NavRow(NavKind.NewProfile, "", Loc.Get("studio_nav_new_profile"), GlyphAdd, 0));

        _nav.Add(new NavRow(NavKind.Header, "", Loc.Get("studio_group_builtin"), "", 0));
        AddProfiles(_rows.Where(r => !r.IsCustom), GlyphLock);

        // Unassigned, or pointing at a profile that no longer exists: both are offered nowhere, so
        // this is the only place they can be reached.
        var loose = actions.Where(a => a.ProfileId.Length == 0 || _rows.All(r => r.Id != a.ProfileId))
                           .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();
        if (loose.Count > 0)
        {
            _nav.Add(new NavRow(NavKind.Header, "", Loc.Get("studio_group_none"), "", 0));
            foreach (var a in loose)
                _nav.Add(new NavRow(NavKind.Action, a.Id, a.Name, GlyphAction, 0));
        }

        LstNav.SelectedItem = _view == StudioView.Action && _action is not null
            ? _nav.FirstOrDefault(n => n.Kind == NavKind.Action && n.Id == _action.Id)
            : _nav.FirstOrDefault(n => n.Kind == NavKind.Profile && n.Id == _row?.Id);
        if (LstNav.SelectedItem is not null) LstNav.ScrollIntoView(LstNav.SelectedItem);
        _navRefresh = false;
    }

    private void LstNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navRefresh || LstNav.SelectedItem is not NavRow n) return;
        switch (n.Kind)
        {
            case NavKind.Profile: OpenProfile(n.Id); break;
            case NavKind.Action: OpenAction(n.Id); break;
            case NavKind.NewAction: NewAction(n.Id); break;
            case NavKind.NewProfile: BtnProfileNew_Click(this, new RoutedEventArgs()); break;
        }
    }

    /// <summary>Shows a profile: its pad in the middle, its settings folded above. Opening the
    /// profile that is already open only redraws its keys — an action edited a moment ago may have
    /// changed how one of them looks.</summary>
    private void OpenProfile(string? id)
    {
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            LoadProfile(null);
            LoadAction(null);
            _view = StudioView.None;
        }
        else
        {
            if (row.Id != _row?.Id) LoadProfile(row);
            else BuildKeyCanvas();
            LoadAction(null);
            _view = StudioView.Profile;
        }
        ShowView();
        ReloadNav();
    }

    /// <summary>Shows an action in its editor, opening the profile it belongs to first so the
    /// navigation expands around it and "back" leads somewhere sensible.</summary>
    private void OpenAction(string id)
    {
        if (CustomGameStore.ActionById(id) is not { } action) { ReloadNav(); return; }

        if (_rows.FirstOrDefault(r => r.Id == action.ProfileId) is { } owner && owner.Id != _row?.Id)
            LoadProfile(owner);

        LoadAction(action);
        _view = StudioView.Action;
        ShowView();
        ReloadNav();
    }

    private void ShowView()
    {
        PnlProfileView.Visibility = _view == StudioView.Profile ? Visibility.Visible : Visibility.Collapsed;
        PnlActionView.Visibility = _view == StudioView.Action ? Visibility.Visible : Visibility.Collapsed;
        PnlEmpty.Visibility = _view == StudioView.None ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnCrumbProfile_Click(object sender, RoutedEventArgs e)
    {
        string? owner = _action?.ProfileId;
        OpenProfile(_rows.Any(r => r.Id == owner) ? owner : _row?.Id);
    }

    private void BtnProfileSettings_Click(object sender, RoutedEventArgs e) =>
        PnlProfileSettings.Visibility = PnlProfileSettings.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void LoadProfile(ProfileRow? row)
    {
        bool wasLoading = _loading;
        _loading = true;

        _row = row;
        _profile = row is { IsCustom: true } ? CustomGameStore.ProfileById(row.Id) : null;
        _builtin = row is { IsCustom: false } ? GameProfileCatalog.ById(row.Id) : null;
        _builtinPages = _builtin is not null && Host is { } host
            ? host.LoadPages(_builtin).Select(p => p.ToList()).ToList()
            : null;

        bool custom = _profile is not null;
        bool anything = custom || _builtin is not null;

        PnlProfile.IsEnabled = anything;
        PnlBuiltinNote.Visibility = _builtin is not null ? Visibility.Visible : Visibility.Collapsed;
        // A shipped profile's IDENTITY is part of K2 and stays locked; its LOOK is not, so the style
        // block below stays live for it (that is where its frame art is replaced).
        TxtProfileName.IsReadOnly = TxtProfileExe.IsReadOnly = TxtProfileAppId.IsReadOnly = !custom;
        BtnExeRunning.IsEnabled = BtnExeBrowse.IsEnabled = custom;
        BtnAccent.IsEnabled = custom;
        GrdProfileStyle.IsEnabled = anything;
        BtnProfileDelete.IsEnabled = BtnProfileExport.IsEnabled = custom;

        var style = anything ? CustomGameStore.EffectiveStyleFor(row!.Id) : new CustomTileStyle();

        if (custom)
        {
            var p = _profile!;
            TxtProfileName.Text = p.Name;
            TxtProfileExe.Text = p.ExeName;
            TxtProfileAppId.Text = p.SteamAppId?.ToString() ?? "";
            SetSwatch(SwAccent, p.Accent);
        }
        else
        {
            var d = _builtin;
            TxtProfileName.Text = d?.Name ?? "";
            TxtProfileExe.Text = d?.ExeName ?? "";
            TxtProfileAppId.Text = d?.SteamAppId?.ToString() ?? "";
            // The shipped accent is the game's own HUD colour, from its spec.
            SetSwatch(SwAccent, CustomTileRenderer.ToHex(
                GameProfileSpecs.ById(d?.Id)?.Accent ?? System.Drawing.Color.Gray));
        }

        SetSwatch(SwBgOn, style.BackgroundOn);
        SetSwatch(SwTextOn, style.TextOn);
        SetSwatch(SwBgOff, style.BackgroundOff ?? style.BackgroundOn);
        SetSwatch(SwTextOff, style.TextOff ?? style.TextOn);
        ChkProfileOff.IsChecked = style.BackgroundOff is not null || style.TextOff is not null;
        ShowProfileBackground(style.BackgroundImageOn);
        UpdateOffEnabled();
        UpdateProfileHeader();
        RefreshCategories();
        TxtNewCategory.Clear();

        _pageIndex = 0;
        _selectedKey = -1;
        ReloadPages();

        _loading = wasLoading;
    }

    /// <summary>The profile's title line: its name, the process it follows (said in warning
    /// colours when there is none — such a profile never switches on by itself), and the lock of a
    /// shipped one.</summary>
    private void UpdateProfileHeader()
    {
        TxtProfileTitle.Text = TxtProfileName.Text;
        string exe = TxtProfileExe.Text.Trim();
        bool none = exe.Length == 0 && _row is not null;
        TxtExeChip.Text = none ? Loc.Get("studio_no_process") : exe;
        TxtExeChip.Foreground = none
            ? new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27))
            : (Brush)FindResource("K2TextMutedBrush");
        BdExeChip.Visibility = _row is null ? Visibility.Collapsed : Visibility.Visible;
        BdBuiltinBadge.Visibility = _builtin is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the frame the tiles are wearing — the user's picture, or the game's own art
    /// when it has not been replaced. Seeing it is the point: without it, "change the background" is
    /// a guess about what is being changed.</summary>
    private void ShowProfileBackground(string? path)
    {
        LblProfileBgImage.Text = path is null or ""
            ? Loc.Get("studio_no_image")
            : Path.GetFileName(path) + (IsShippedArt(path) ? "  ·  " + Loc.Get("studio_builtin") : "");
        ImgProfileBg.Source = LoadBitmap(path);
    }

    /// <summary>True for a picture that came out of K2's own assets rather than the studio's folder
    /// — i.e. the game's shipped frame.</summary>
    private static bool IsShippedArt(string? path) =>
        path is not null &&
        !path.StartsWith(CustomGameStore.AssetsDir, StringComparison.OrdinalIgnoreCase);

    private void ProfileField_Changed(object sender, RoutedEventArgs e) => SaveProfile();

    private void ChkProfileOff_Click(object sender, RoutedEventArgs e)
    {
        UpdateOffEnabled();
        SaveProfile();
    }

    private void UpdateOffEnabled() =>
        PnlProfileOff.Visibility = ChkProfileOff.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Writes the profile form back. Every field handler ends here — the form IS the
    /// record, so there is one place that reads it and one shape it can be in. A shipped profile has
    /// no record of its own: only its STYLE is stored, keyed by catalogue id.</summary>
    private void SaveProfile()
    {
        if (_loading || _row is null) return;

        bool withOff = ChkProfileOff.IsChecked == true;
        var style = CustomGameStore.EffectiveStyleFor(_row.Id) with
        {
            BackgroundOn = SwatchHex(SwBgOn),
            TextOn = SwatchHex(SwTextOn),
            // Unticking "different colours when off" clears the second half rather than leaving it
            // stored: a hidden colour that comes back on the next tick is the kind of state that
            // makes a tile change for no visible reason.
            BackgroundOff = withOff ? SwatchHex(SwBgOff) : null,
            TextOff = withOff ? SwatchHex(SwTextOff) : null,
        };

        if (_profile is not null)
        {
            var updated = _profile with
            {
                Name = TxtProfileName.Text.Trim().Length > 0
                    ? TxtProfileName.Text.Trim() : Loc.Get("studio_profile_untitled"),
                ExeName = TxtProfileExe.Text.Trim(),
                SteamAppId = int.TryParse(TxtProfileAppId.Text.Trim(), out int appId) ? appId : null,
                Accent = SwatchHex(SwAccent),
                DefaultStyle = style,
            };
            CustomGameStore.SaveProfile(updated);
            _profile = updated;
            RenameRow(updated.Id, updated.Name);
        }
        else if (_builtin is not null)
        {
            CustomGameStore.SaveShippedStyle(_builtin.Id, style);
        }

        UpdateProfileHeader();
        BuildKeyCanvas();
        RefreshPreview();
    }

    /// <summary>Keeps the profile list, and the navigation drawn from it, in step with a renamed
    /// profile.</summary>
    private void RenameRow(string id, string name)
    {
        int i = _rows.ToList().FindIndex(r => r.Id == id);
        if (i < 0 || _rows[i].Name == name) return;
        _rows[i] = _rows[i] with { Name = name };
        _row = _rows[i];
        ReloadNav();
    }

    private void BtnProfileNew_Click(object sender, RoutedEventArgs e)
    {
        var profile = new CustomGameProfile
        {
            Id = CustomGameStore.NewProfileId(),
            Name = Loc.Get("studio_profile_untitled"),
            Pages = new[] { new CustomGamePage { Name = Loc.Get("studio_page_default") } },
        };
        CustomGameStore.SaveProfile(profile);

        var row = new ProfileRow(profile.Id, profile.Name, IsCustom: true);
        // In front of the shipped ones, which are always the tail of the list.
        _rows.Insert(Math.Max(CustomGameStore.Profiles().Count - 1, 0), row);
        OpenProfile(profile.Id);

        // A brand new profile has nothing else to say yet: its settings are what the user came for,
        // so they are open rather than folded away.
        PnlProfileSettings.Visibility = Visibility.Visible;
        TxtProfileName.Focus();
        TxtProfileName.SelectAll();
    }

    private void BtnProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;   // a shipped profile is not the user's to delete
        if (MessageBox.Show(this, string.Format(Loc.Get("studio_delete_profile_confirm"), _profile.Name),
                            Loc.Get("studio_title"), MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        // The pads keep what they were given: the reserved "Game: <name>" slot and the profile's
        // settings rows outlive the catalogue entry unless someone takes them away. Done BEFORE
        // the store forgets the profile — the slot is found by the NAME the catalogue still has.
        if (GameProfileCatalog.ById(_profile.Id) is { } def) Host?.ForgetGameProfile(def);

        CustomGameStore.DeleteProfile(_profile.Id);
        _profile = null;
        _row = null;
        ReloadProfiles();
    }

    private void BtnProfileExport_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.Get("studio_package_filter"),
            FileName = Sanitize(_profile.Name) + ".k2game.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (!CustomGameStore.Export(_profile.Id, dlg.FileName))
            MessageBox.Show(this, Loc.Get("studio_export_failed"), Loc.Get("studio_title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnProfileImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("studio_package_filter") };
        if (dlg.ShowDialog(this) != true) return;

        string? id = CustomGameStore.Import(dlg.FileName);
        if (id is null)
        {
            MessageBox.Show(this, Loc.Get("studio_import_failed"), Loc.Get("studio_title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _row = null;
        ReloadProfiles();
        OpenProfile(id);
    }

    /// <summary>"From a running app…": the same picker the ordinary profiles use. What is stored is
    /// the process NAME, which is what the launch watcher matches on.</summary>
    private void BtnExeRunning_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RunningProcessDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetExeFromPath(dlg.SelectedPath);
    }

    private void BtnExeBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("studio_exe_filter") };
        if (dlg.ShowDialog(this) != true) return;
        SetExeFromPath(dlg.FileName);
    }

    private void SetExeFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        TxtProfileExe.Text = Path.GetFileNameWithoutExtension(path);
        SaveProfile();
    }

    // ─────────────────────────── tiles ───────────────────────────

    private string ProfileName(string profileId) =>
        _rows.FirstOrDefault(r => r.Id == profileId)?.Name ?? "";

    private void LoadAction(CustomGameAction? action)
    {
        bool wasLoading = _loading;
        _loading = true;
        _action = action;

        PnlAction.IsEnabled = action is not null;
        BtnActionDelete.IsEnabled = BtnActionCopy.IsEnabled =
            BtnActionDuplicate.IsEnabled = action is not null;

        var a = action ?? new CustomGameAction();
        TxtActionName.Text = a.Name;
        TxtActionTitle.Text = a.Name;
        TxtCrumbProfile.Text = ProfileName(a.ProfileId) is { Length: > 0 } owner
            ? owner : Loc.Get("studio_group_none");
        FillCategories(a.ProfileId, a.Category);

        if (CbActKeyValue.Items.Count == 0) KeyCombo.Populate(CbActKeyValue);
        KeyCombo.Load(a.Keys, ChkActKeyCtrl, ChkActKeyShift, ChkActKeyAlt, ChkActKeyWin, CbActKeyValue);

        _readings = a.Readings.ToList();
        _elements = a.Elements.ToList();
        _readingIndex = _readings.Count > 0 ? 0 : -1;
        _elementIndex = _elements.Count > 0 ? 0 : -1;

        // What the tile inherits: the style of the profile IT belongs to, which is not necessarily
        // the profile currently selected in the list.
        var effective = CustomGameStore.EffectiveStyleFor(a.ProfileId).MergedWith(a.Style);
        ChkActionOverride.IsChecked = a.Style is not null;
        GrdActionStyle.Visibility = a.Style is not null ? Visibility.Visible : Visibility.Collapsed;
        SetSwatch(SwABgOn, effective.BackgroundOn);
        SetSwatch(SwATextOn, effective.TextOn);
        SetSwatch(SwABgOff, effective.BackgroundOff ?? effective.BackgroundOn);
        SetSwatch(SwATextOff, effective.TextOff ?? effective.TextOn);
        LblActionBgImage.Text = ShortPath(effective.BackgroundImageOn);

        RefreshReadingList();
        RefreshElementList();
        RebuildHandles();

        // Which steps are open is decided ONCE per tile, when it is opened: re-deciding it on
        // every save would fold a step away under the user's hands as they filled it in.
        if (action is not null && action.Id != _openedActionId)
        {
            _openedActionId = action.Id;
            SetStep("read", _readings.Count == 0 || _readings.Any(r => NeedsSource(r)));
            SetStep("look", true);
            SetStep("colours", false);
        }

        _loading = wasLoading;
        UpdateStepSummaries();
        RefreshPreview();
    }

    /// <summary>Which tile the steps were last opened for — see <see cref="LoadAction"/>.</summary>
    private string _openedActionId = "";

    /// <summary>True for a reading that cannot answer anything yet: no rectangle on the screen, no
    /// link and value, or — for a multi-state one — no states.
    ///
    /// <para>A LINK is judged on what is stored, not on whether the game happens to be running: a
    /// tile authored with the game shut down is finished, and colouring its summary amber would be
    /// telling the user to fix something that is not broken.</para></summary>
    private static bool NeedsSource(TileReading r) =>
        r.ValueKind == CustomValueKind.MultiState
            ? r.States.Count == 0
            : r.Source == CustomSource.Telemachus
                ? r.ValuePath.Length == 0
            : r.Source.IsLink()
                ? r.LinkId.Length == 0 || r.ValuePath.Length == 0 || GameLinkStore.ById(r.LinkId) is null
                : r.ProbeId.Length == 0 || ScreenProbeStore.ById(r.ProbeId) is null;

    private void ActionField_Changed(object sender, RoutedEventArgs e) => SaveAction();

    /// <summary>Clears the keystroke in one gesture — unticking four boxes and emptying a combo to
    /// say "presses nothing" is four chances to leave half of it behind.</summary>
    private void BtnActKeyClear_Click(object sender, RoutedEventArgs e)
    {
        KeyCombo.Load("", ChkActKeyCtrl, ChkActKeyShift, ChkActKeyAlt, ChkActKeyWin, CbActKeyValue);
        SaveAction();
    }

    private void ChkActionOverride_Click(object sender, RoutedEventArgs e)
    {
        GrdActionStyle.Visibility = ChkActionOverride.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        SaveAction();
    }

    // ─────────────────────────── the three steps ───────────────────────────

    private (Border Header, Border Body, Border Number, TextBlock Chevron) Step(string which) => which switch
    {
        "read" => (StepRead, StepReadBody, BdStepReadNum, ChevRead),
        "look" => (StepLook, StepLookBody, BdStepLookNum, ChevLook),
        _ => (StepColours, StepColoursBody, BdStepColoursNum, ChevColours),
    };

    private void Step_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string which }) return;
        SetStep(which, Step(which).Body.Visibility != Visibility.Visible);
    }

    /// <summary>Opens or folds one step, and says so twice: the chevron, and the step's number disc,
    /// which wears the accent while its step is open.</summary>
    private void SetStep(string which, bool open)
    {
        var (_, body, number, chevron) = Step(which);
        body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        chevron.Text = open ? "" : "";
        number.Background = open
            ? (Brush)FindResource("K2AccentBrush")
            : (Brush)FindResource("K2SurfaceDarkBrush");
    }

    /// <summary>What each folded step says about itself — enough to know whether it needs opening.
    /// The first one turns amber when a reading has no rectangle yet: that tile can draw nothing
    /// until it gets one.</summary>
    private void UpdateStepSummaries()
    {
        if (_action is null) return;

        var first = _readings.FirstOrDefault();
        bool missing = _readings.Any(NeedsSource);
        string read;
        if (first is null)
        {
            read = Loc.Get("studio_sum_no_reading");
        }
        else
        {
            string kind = CbKind.Items.OfType<Item<CustomValueKind>>()
                .FirstOrDefault(i => i.Value == first.ValueKind)?.Label ?? "";
            string source = first.ValueKind == CustomValueKind.MultiState
                ? Loc.Get("studio_sum_states", first.States.Count)
                : first.Source.IsValueSource()
                    // The PATH is what the user recognises here — the link's name is the game, and
                    // the summary already sits under that game's profile.
                    ? first.ValuePath.Length > 0 ? first.ValuePath : Loc.Get("studio_sum_no_value")
                    : ScreenProbeStore.ById(first.ProbeId) is { } probe
                        ? probe.Name
                        : Loc.Get("studio_sum_no_area");
            read = $"{first.Name} · {kind} · {source}";
            if (_readings.Count > 1) read += "   " + Loc.Get("studio_sum_more", _readings.Count - 1);
        }

        TxtStepReadSum.Text = read;
        TxtStepReadSum.Foreground = missing
            ? new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27))
            : (Brush)FindResource("K2TextMutedBrush");

        TxtStepLookSum.Text = Loc.Get("studio_sum_elements", _elements.Count);
        TxtStepColoursSum.Text = Loc.Get(ChkActionOverride.IsChecked == true
            ? "studio_sum_colours_own" : "studio_sum_colours_profile");
    }

    /// <summary>Writes the tile back to the store — identity, style, readings and elements, all of
    /// it. Every editor in this tab ends here, so there is one place that builds the record.</summary>
    private void SaveAction()
    {
        if (_loading || _action is null) return;

        bool over = ChkActionOverride.IsChecked == true;

        // The profile is not edited here: a tile belongs to the profile it sits under.
        var updated = _action with
        {
            Name = TxtActionName.Text.Trim().Length > 0
                ? TxtActionName.Text.Trim() : Loc.Get("studio_action_untitled"),
            Category = (CbCategory.SelectedItem as Item<string>)?.Value ?? "",
            Readings = _readings.ToList(),
            Elements = _elements.ToList(),
            Keys = KeyCombo.Save(ChkActKeyCtrl, ChkActKeyShift, ChkActKeyAlt, ChkActKeyWin, CbActKeyValue),
            // Not overriding stores NOTHING, not a copy of the profile's colours: a tile that
            // inherits must keep following the profile when the profile changes later.
            Style = over
                ? new CustomTileOverride
                {
                    BackgroundOn = SwatchHex(SwABgOn),
                    TextOn = SwatchHex(SwATextOn),
                    BackgroundOff = SwatchHex(SwABgOff),
                    TextOff = SwatchHex(SwATextOff),
                    BackgroundImageOn = _action.Style?.BackgroundImageOn,
                    BackgroundImageOff = _action.Style?.BackgroundImageOff,
                }
                : null,
        };

        CustomGameStore.SaveAction(updated);
        _action = updated;
        SavedActionId = updated.Id;

        UpdateActionRow(updated);

        UpdateStepSummaries();
        RefreshPreview();
    }

    /// <summary>Keeps the title and the navigation row in step with an edited tile.</summary>
    private void UpdateActionRow(CustomGameAction updated)
    {
        TxtActionTitle.Text = updated.Name;
        if (_nav.FirstOrDefault(n => n.Kind == NavKind.Action && n.Id == updated.Id) is { } row &&
            row.Label != updated.Name)
            ReloadNav();
    }

    /// <summary>
    /// A new tile, from a MODEL rather than from nothing. Composing a tile out of a reading and a
    /// handful of elements is the studio's whole vocabulary, and having to learn all of it before
    /// the first tile shows anything is what made this window feel like a form rather than a tool:
    /// every model lands a reading and the elements that make it readable, already pointed at each
    /// other, so the only thing left is to show K2 where to look on the screen — which is why the
    /// calibration window opens straight after one.
    /// </summary>
    private void NewAction(string profileId)
    {
        var dlg = new GameStudioTemplateDialog(ProfileName(profileId)) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Choice is not { } template)
        {
            // Cancelled: the "+ New action" row is selected, and nothing was made — put the
            // selection back on whatever is actually open.
            ReloadNav();
            return;
        }

        var action = BuildFromTemplate(template, profileId);
        CustomGameStore.SaveAction(action);
        SavedActionId = action.Id;
        OpenAction(action.Id);

        if (template == StudioTemplate.Blank)
        {
            TxtActionName.Focus();
            TxtActionName.SelectAll();
            return;
        }

        // After the window has drawn itself: the calibration dialog is modal, and opening it from
        // inside the click would show it over a half-painted editor. A Telemachus reading has no
        // rectangle to calibrate — what is left to choose is the entry, so that is what opens.
        if (action.Readings.FirstOrDefault()?.Source == CustomSource.Telemachus)
            Dispatcher.BeginInvoke(new Action(PickTelemachusForReading), DispatcherPriority.Loaded);
        else
            Dispatcher.BeginInvoke(new Action(() => OpenProbe(null)), DispatcherPriority.Loaded);
    }

    /// <summary>Right after a Kerbal Space Program tile is made from a model: choose what it reads.</summary>
    private void PickTelemachusForReading()
    {
        if (SelectedReading is null || CurrentSource() != CustomSource.Telemachus) return;
        if (PickTelemachus(TxtValuePath.Text) is not { } api) return;
        TxtValuePath.Text = api;
        ReadingField_Changed(this, new RoutedEventArgs());
    }

    /// <summary>Where a new reading of this profile gets its value by default: Telemachus on the
    /// Kerbal Space Program profile — the reason to build a KSP tile in K2 at all — the screen
    /// everywhere else.</summary>
    private static CustomSource DefaultSourceFor(string? profileId) =>
        profileId == GameProfileSpecs.KspId ? CustomSource.Telemachus : CustomSource.Screen;

    private static CustomGameAction BuildFromTemplate(StudioTemplate template, string profileId)
    {
        string readingId = CustomGameStore.NewPartId();
        TileElement Piece(TileElementKind kind, TileAnchor anchor, string? readsId = null,
                          CustomIndicatorShape shape = CustomIndicatorShape.LinearHorizontal,
                          TileBox? box = null) =>
            new()
            {
                Id = CustomGameStore.NewPartId(),
                Kind = kind,
                ReadingId = readsId ?? "",
                Anchor = anchor,
                Shape = shape,
                Box = box,
            };

        var (nameKey, kind, elements) = template switch
        {
            StudioTemplate.Bar => ("studio_tpl_bar_name", CustomValueKind.Range, new[]
            {
                Piece(TileElementKind.Indicator, TileAnchor.BottomCenter, readingId),
                Piece(TileElementKind.Value, TileAnchor.Center, readingId),
                Piece(TileElementKind.Label, TileAnchor.TopCenter),
            }),
            StudioTemplate.Number => ("studio_tpl_number_name", CustomValueKind.Number, new[]
            {
                Piece(TileElementKind.Value, TileAnchor.Center, readingId),
                Piece(TileElementKind.Label, TileAnchor.TopCenter),
            }),
            StudioTemplate.OnOff => ("studio_tpl_onoff_name", CustomValueKind.OnOff, new[]
            {
                Piece(TileElementKind.Label, TileAnchor.Center),
            }),
            StudioTemplate.Mirror => ("studio_tpl_mirror_name", CustomValueKind.Mirror, new[]
            {
                // A capture is the whole point of the tile, so it starts nearly full size rather
                // than at the small default a picture beside a reading would want.
                Piece(TileElementKind.Icon, TileAnchor.Center, readingId, box: new TileBox(0.86, 0.86)),
            }),
            _ => ("studio_action_untitled", CustomValueKind.Range, new[]
            {
                Piece(TileElementKind.Value, TileAnchor.TopCenter, readingId),
                Piece(TileElementKind.Label, TileAnchor.TopCenter),
            }),
        };

        string name = Loc.Get(nameKey);
        return new CustomGameAction
        {
            Id = CustomGameStore.NewActionId(),
            Name = name,
            ProfileId = profileId,
            Readings = new[]
            {
                new TileReading
                {
                    Id = readingId, Name = name, ValueKind = kind,
                    // A mirror copies pixels: it is a screen reading whatever the game.
                    Source = template == StudioTemplate.Mirror ? CustomSource.Screen : DefaultSourceFor(profileId),
                },
            },
            Elements = elements,
        };
    }

    /// <summary>"Copy": duplicates the tile onto another profile. A menu rather than a dialog —
    /// the whole question is "which profile", and the answer is one click.</summary>
    private void BtnActionCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_action is null || sender is not Button button) return;

        ShowMenu(BuildCopyMenu(_action), button);
    }

    /// <summary>"Duplicate": a second copy of the tile on the SAME profile, named "… Copy". What it
    /// is for is building a variant — same readings, same layout, one colour or one rectangle
    /// different — without laying the whole tile out again.</summary>
    private void BtnActionDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_action is not null) DuplicateAction(_action);
    }

    private void DuplicateAction(CustomGameAction action)
    {
        string? id = CustomGameStore.CopyActionTo(action.Id, action.ProfileId,
                                                 UniqueCopyName(action.Name, action.ProfileId));
        App.WriteLog($"[STUDIO] duplicate action {action.Id} on {action.ProfileId}: {id ?? "failed"}");
        if (id is not null) OpenAction(id);
    }

    /// <summary>"&lt;name&gt; Copy", then "&lt;name&gt; Copy 2"… — the studio lists actions by name, and
    /// two rows reading the same thing is a list nobody can use.</summary>
    private static string UniqueCopyName(string name, string profileId)
    {
        var taken = CustomGameStore.ActionsFor(profileId).Select(a => a.Name)
                                   .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        string suffix = Loc.Get("studio_action_copy_suffix");
        string candidate = $"{name} {suffix}";
        for (int n = 2; taken.Contains(candidate); n++) candidate = $"{name} {suffix} {n}";
        return candidate;
    }

    /// <summary>Right-clicking an action in the list offers the same copy, without having to open
    /// it first: spreading one reading across several games is then a right-click and a click per
    /// game, instead of a round trip through every action's editor.</summary>
    private void LstNav_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(LstNav, e.OriginalSource as DependencyObject)
                is not ListBoxItem container ||
            container.DataContext is not NavRow nav || nav.Kind != NavKind.Action ||
            CustomGameStore.ActionById(nav.Id) is not { } action) return;

        var copy = new MenuItem { Header = Loc.Get("studio_action_copy") };
        FillCopyTargets(copy.Items, action);
        copy.IsEnabled = copy.Items.Count > 0;

        var duplicate = new MenuItem { Header = Loc.Get("studio_action_duplicate") };
        duplicate.Click += (_, _) => DuplicateAction(action);

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(duplicate);
        ShowMenu(menu, container);
        e.Handled = true;
    }

    private ContextMenu BuildCopyMenu(CustomGameAction action)
    {
        var menu = new ContextMenu();
        FillCopyTargets(menu.Items, action);
        return menu;
    }

    /// <summary>Opens a menu built in code, and the two things that make one actually appear.
    ///
    /// <para>It is hung on the target's own <see cref="FrameworkElement.ContextMenu"/>: a LOOSE
    /// ContextMenu (just PlacementTarget and IsOpen) is in nobody's tree — see the same warning in
    /// <c>MainWindow.DisplayPad.Dedicated.cs</c>.</para>
    ///
    /// <para>And it is opened only once the gesture that asked for it is OVER. Opening a menu
    /// inside a Click handler opens it while the button still holds the mouse capture, and the
    /// button's own mouse-up then reads as a click outside the menu, which shuts it in the same
    /// frame — the menu "does nothing" because it opened and closed before it could be drawn. The
    /// one menu in this app that always worked (<c>DpShowDedicatedGear</c>) is opened from
    /// PreviewMouseDown, i.e. before the capture, which is the same rule seen from the other
    /// side.</para></summary>
    private static void ShowMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        target.ContextMenu = menu;
        App.WriteLog($"[STUDIO] menu on {target.Name}: {menu.Items.Count} items");
        target.Dispatcher.BeginInvoke(new Action(() => menu.IsOpen = true),
                                      DispatcherPriority.Background);
    }

    /// <summary>One item per profile the action is not already on — the user's own and the shipped
    /// ones alike, since a custom reading is assignable to both.</summary>
    private void FillCopyTargets(ItemCollection items, CustomGameAction action)
    {
        items.Clear();
        foreach (var row in _rows.Where(r => r.Id != action.ProfileId))
        {
            var item = new MenuItem { Header = row.ToString() };
            string actionId = action.Id, target = row.Id;
            item.Click += (_, _) => CopyActionTo(actionId, target);
            items.Add(item);
        }
    }

    private void CopyActionTo(string actionId, string profileId)
    {
        string? id = CustomGameStore.CopyActionTo(actionId, profileId);
        App.WriteLog($"[STUDIO] copy action {actionId} -> {profileId}: {id ?? "failed"}");
        if (id is null) return;

        // Straight onto the copy — which means opening the profile it was copied to, so the user
        // can see where it went.
        OpenAction(id);
    }

    private void BtnActionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_action is null) return;
        if (MessageBox.Show(this, string.Format(Loc.Get("studio_delete_action_confirm"), _action.Name),
                            Loc.Get("studio_title"), MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        CustomGameStore.DeleteAction(_action.Id);
        // The caller (a key's action dialog) has to learn the action is gone, or it would bind the
        // key to an id that no longer exists.
        if (SavedActionId == _action.Id) SavedActionId = "";
        _action = null;
        // Back to the profile it was on: there is nothing left to show in the action editor.
        OpenProfile(_row?.Id);
    }

    // ─────────────────────────── readings ───────────────────────────

    private TileReading? SelectedReading =>
        _readingIndex >= 0 && _readingIndex < _readings.Count ? _readings[_readingIndex] : null;

    private void RefreshReadingList()
    {
        bool wasLoading = _loading;
        _loading = true;

        _readingRows.Clear();
        foreach (var r in _readings)
            _readingRows.Add(new ReadingRow(r, ReadingLabel(r)));

        if (_readingIndex >= _readingRows.Count) _readingIndex = _readingRows.Count - 1;
        LstReadings.SelectedIndex = _readingIndex;

        _loading = wasLoading;
        LoadReading();
    }

    /// <summary>"name — what it reads": the kind is what tells two readings of the same HUD apart
    /// at a glance ("Health, number" against "Health, bar").</summary>
    private string ReadingLabel(TileReading r) =>
        $"{r.Name} — {(CbKind.Items.OfType<Item<CustomValueKind>>().FirstOrDefault(i => i.Value == r.ValueKind)?.Label ?? "")}";

    private void LstReadings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _readingIndex = LstReadings.SelectedIndex;
        LoadReading();
    }

    /// <summary>Puts the selected reading into its editor.</summary>
    private void LoadReading()
    {
        bool wasLoading = _loading;
        _loading = true;

        var r = SelectedReading;
        PnlReading.IsEnabled = r is not null;
        BtnReadingDelete.IsEnabled = r is not null;

        var reading = r ?? new TileReading();
        TxtReadingName.Text = reading.Name;
        CbKind.SelectedItem = CbKind.Items.OfType<Item<CustomValueKind>>()
            .FirstOrDefault(i => i.Value == reading.ValueKind);
        CbMultiMode.SelectedItem = CbMultiMode.Items.OfType<Item<CustomMultiMode>>()
            .FirstOrDefault(i => i.Value == reading.MultiMode);
        // A reading whose link turns out to be a mod is a MOD link reading, whatever it was saved
        // as: the two lists are separate, so a stale source would leave its own link unlisted.
        var source = reading.Source == CustomSource.Link &&
                     GameLinkStore.ById(reading.LinkId)?.Kind == GameLinkKind.Mod
            ? CustomSource.ModLink
            : reading.Source;
        SyncTelemachusSource(source == CustomSource.Telemachus);
        CbSource.SelectedItem = CbSource.Items.OfType<Item<CustomSource>>()
            .FirstOrDefault(i => i.Value == source);
        FillProbes(reading.ProbeId);
        FillLinks(reading.LinkId);
        TxtValuePath.Text = reading.ValuePath;
        TxtMinPath.Text = reading.MinPath;
        TxtMaxPath.Text = reading.MaxPath;
        TxtMin.Text = reading.MinPath.Length > 0 ? "" : Num(reading.Min);
        TxtMax.Text = reading.MaxPath.Length > 0 ? "" : Num(reading.Max);
        TxtMin.IsReadOnly = reading.MinPath.Length > 0;
        TxtMax.IsReadOnly = reading.MaxPath.Length > 0;

        _states.Clear();
        foreach (var s in reading.States) _states.Add(s);
        LstStates.SelectedItem = _states.FirstOrDefault();
        LoadState(_states.FirstOrDefault());

        UpdateKindPanels();
        _loading = wasLoading;
    }

    private void ReadingField_Changed(object sender, RoutedEventArgs e)
    {
        UpdateKindPanels();
        SaveReading();
    }

    /// <summary>Writes the reading editor back into the tile's list.</summary>
    private void SaveReading()
    {
        if (_loading || SelectedReading is not { } current) return;

        _readings[_readingIndex] = current with
        {
            Name = TxtReadingName.Text.Trim().Length > 0
                ? TxtReadingName.Text.Trim() : Loc.Get("studio_reading_untitled"),
            ValueKind = (CbKind.SelectedItem as Item<CustomValueKind>)?.Value ?? CustomValueKind.Range,
            Source = (CbSource.SelectedItem as Item<CustomSource>)?.Value ?? CustomSource.Screen,
            ProbeId = (CbProbe.SelectedItem as Item<string>)?.Value ?? "",
            LinkId = (CbLink.SelectedItem as Item<string>)?.Value ?? "",
            ValuePath = TxtValuePath.Text.Trim(),
            MultiMode = (CbMultiMode.SelectedItem as Item<CustomMultiMode>)?.Value ?? CustomMultiMode.Colors,
            States = _states.ToList(),
            // A path wins: the number box then only shows what the path reads right now.
            MinPath = TxtMinPath.Text.Trim(),
            MaxPath = TxtMaxPath.Text.Trim(),
            Min = TxtMinPath.Text.Trim().Length > 0 ? null : ParseNum(TxtMin.Text),
            Max = TxtMaxPath.Text.Trim().Length > 0 ? null : ParseNum(TxtMax.Text),
        };

        SaveAction();
        // The list shows the name and the kind, and the element table names readings in its rows.
        RefreshReadingRow();
        RefreshElementList();
    }

    private void RefreshReadingRow()
    {
        if (SelectedReading is not { } r || _readingIndex >= _readingRows.Count) return;
        bool wasLoading = _loading;
        _loading = true;
        _readingRows[_readingIndex] = new ReadingRow(r, ReadingLabel(r));
        LstReadings.SelectedIndex = _readingIndex;
        _loading = wasLoading;
    }

    private void BtnReadingNew_Click(object sender, RoutedEventArgs e)
    {
        if (_action is null) return;

        _readings.Add(new TileReading
        {
            Id = CustomGameStore.NewPartId(),
            Name = Loc.Get("studio_reading_untitled"),
            Source = DefaultSourceFor(_action.ProfileId),
        });
        _readingIndex = _readings.Count - 1;
        SaveAction();
        RefreshReadingList();
        TxtReadingName.Focus();
        TxtReadingName.SelectAll();
    }

    /// <summary>Removes a reading, and with it the pointer of every element that showed it — those
    /// elements stay (with their placement) but stop showing a value, which is the same state a
    /// newly added one is in.</summary>
    private void BtnReadingDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedReading is not { } reading) return;
        if (MessageBox.Show(this, Loc.Get("studio_delete_reading_confirm", reading.Name),
                            Loc.Get("studio_title"), MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _readings.RemoveAt(_readingIndex);
        for (int i = 0; i < _elements.Count; i++)
            if (_elements[i].ReadingId == reading.Id)
                _elements[i] = _elements[i] with { ReadingId = "" };

        _readingIndex = Math.Min(_readingIndex, _readings.Count - 1);
        SaveAction();
        RefreshReadingList();
        RefreshElementList();
    }

    /// <summary>Shows only the fields the chosen kind actually has: a number has the two ends of its
    /// scale, a multi-state has a list of states and no probe of its own.</summary>
    private void UpdateKindPanels()
    {
        var kind = (CbKind.SelectedItem as Item<CustomValueKind>)?.Value ?? CustomValueKind.Range;
        bool multi = kind == CustomValueKind.MultiState;

        // "link" here means any source whose value is a NAMED entry — the two link kinds and
        // Telemachus share the value box, the states and the scale. Only a real link has a link
        // to choose; Telemachus' address is the mod's own.
        bool link = CurrentSource().IsValueSource();
        bool realLink = CurrentSource().IsLink();

        PnlSingleProbe.Visibility = !link && !multi ? Visibility.Visible : Visibility.Collapsed;
        PnlLink.Visibility = link && !multi ? Visibility.Visible : Visibility.Collapsed;
        PnlLinkLabel.Visibility = GrdLinkPick.Visibility =
            realLink ? Visibility.Visible : Visibility.Collapsed;
        PnlStates.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;

        // A state of a LINK reading is a value and the text it has to equal; a state of a screen
        // reading is a rectangle. Never both at once — they answer the same question twice.
        PnlStateLink.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
        BtnStateProbe.Visibility = link ? Visibility.Collapsed : Visibility.Visible;

        // A number always has a scale to set; a RANGE has one only when it comes from a link, since
        // a probe measures its own 0..100% off the pixels and has nothing to be told.
        PnlScale.Visibility = kind == CustomValueKind.Number || (link && kind == CustomValueKind.Range)
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateScaleEnds();

        if (link) UpdateLinkStatus();

        // A missing OCR language is not an explanation, it is something the user has to go and fix,
        // so it stays on screen instead of hiding in the tooltip.
        bool needsOcr = !link && kind is CustomValueKind.Number or CustomValueKind.Text;

        // A mirror is drawn by a PICTURE element and by nothing else, so a mirror nothing points at
        // is a reading that will never appear on the tile — said on screen rather than left for the
        // user to work out from an empty preview.
        bool mirrorUnshown = kind == CustomValueKind.Mirror && SelectedReading is { } shown &&
            !_elements.Any(e => e.Kind == TileElementKind.Icon && e.ReadingId == shown.Id);

        LblKindWarning.Visibility = (needsOcr && !ScreenTextReader.Available) || mirrorUnshown
            ? Visibility.Visible : Visibility.Collapsed;
        LblKindWarning.Text = Loc.Get(mirrorUnshown ? "studio_kind_mirror_no_icon" : "studio_kind_ocr_missing");

        HintMulti.ToolTip = Loc.Get(
            (CbMultiMode.SelectedItem as Item<CustomMultiMode>)?.Value == CustomMultiMode.Rects
                ? "studio_multi_rects_hint" : "studio_multi_colors_hint");

        BtnStateRemove.IsEnabled = BtnStateProbe.IsEnabled = BtnStateIcon.IsEnabled =
            LstStates.SelectedItem is not null;

        // Here and not only in FillProbes: picking another reading from the combo does not refill
        // it, so the two buttons stayed disabled from whenever the reading had no source yet.
        BtnProbeEdit.IsEnabled = BtnProbeDelete.IsEnabled = CbProbe.SelectedItem is not null;
    }

    // ─────────────────────────── the source (screen probes) ───────────────────────────

    private void BtnProbeEdit_Click(object sender, RoutedEventArgs e) =>
        OpenProbe((CbProbe.SelectedItem as Item<string>)?.Value);

    private void BtnProbeNew_Click(object sender, RoutedEventArgs e) => OpenProbe(null);

    /// <summary>Removes the selected probe from the shared library. Asks first, and says what it
    /// costs: probes are shared, so this can leave a reading in ANOTHER tile without a source (that
    /// tile then draws its labels and no value — the same state a new reading is in, not a
    /// crash).</summary>
    private void BtnProbeDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((CbProbe.SelectedItem as Item<string>)?.Value is not { Length: > 0 } id) return;
        if (ScreenProbeStore.ById(id) is not { } probe) return;

        if (MessageBox.Show(this, Loc.Get("studio_probe_delete_confirm", probe.Name),
                            Loc.Get("studio_title"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        ScreenProbeStore.Delete(id);
        FillProbes(null);
        SaveReading();
    }

    /// <summary>The whole library, past the combo's process filter — see <see cref="FillProbes"/>
    /// for why the combo is filtered in the first place. A probe picked here is passed to FillProbes
    /// as the selection, which is what makes the combo list it even when it belongs to another
    /// game's process.</summary>
    private void BtnProbeBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ScreenProbePickerDialog(ActionProfileProcess(),
                                              (CbProbe.SelectedItem as Item<string>)?.Value) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedProbeId is not { Length: > 0 } picked) return;

        FillProbes(picked);
        SaveReading();
    }

    /// <summary>Which bucket the calibration window should remember the chosen program in: the
    /// open tile's profile, or a bucket of its own for the tiles that belong to no profile — those
    /// are a scratch pile, and a scratch pile is still not the same game as somebody else's.</summary>
    private string ProbeProfileKey() =>
        _action?.ProfileId is { Length: > 0 } id ? id : "(no-profile)";

    private void OpenProbe(string? probeId)
    {
        // A new probe for an OCR reading opens already in that mode: the rectangle is drawn around
        // TEXT, and calibrating it as a colour fill first would be a wasted round trip.
        var kind = (CbKind.SelectedItem as Item<CustomValueKind>)?.Value ?? CustomValueKind.Range;
        ProbeMode? seed = kind switch
        {
            CustomValueKind.Number => ProbeMode.Number,
            CustomValueKind.Text => ProbeMode.Text,
            CustomValueKind.Mirror => ProbeMode.Capture,
            _ => null,
        };

        var dlg = new ScreenProbeDialog(probeId, seed, ProbeProfileKey(), ActionProfileProcess())
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;

        // "" comes back from a delete: the reading is left with no source rather than pointing at a
        // probe that is gone.
        string? saved = dlg.SavedProbeId;
        FillProbes(string.IsNullOrEmpty(saved) ? null : saved);
        SaveReading();
    }

    // ─────────────────────────── the source (game links) ───────────────────────────

    /// <summary>Switching source rewrites which half of the editor applies, and saves — a reading
    /// that says "link" while still holding a probe id would draw from neither.</summary>
    private void CbSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        // Game link and mod link keep separate lists, so the combo has to be rebuilt: what was
        // selected under the other source is not on offer under this one.
        FillLinks(SelectedLinkId());
        UpdateKindPanels();
        SaveReading();
    }

    private string? SelectedLinkId() => (CbLink.SelectedItem as Item<string>)?.Value;

    private void CbLink_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnLinkEdit.IsEnabled = BtnLinkDelete.IsEnabled = CbLink.SelectedItem is not null;
        UpdateLinkModButton();
        if (_loading) return;

        // Another link means another vocabulary of paths: the list has to follow, or the box would
        // offer the previous game's values — including whatever a refresh pulled back for the link
        // being left behind. Saved first: the refresh asks for the paths the saved readings use.
        SaveReading();
        RefreshLinkNow();
    }

    private void BtnLinkNew_Click(object sender, RoutedEventArgs e) => OpenLink(null);

    private void BtnLinkEdit_Click(object sender, RoutedEventArgs e) =>
        OpenLink((CbLink.SelectedItem as Item<string>)?.Value);

    /// <summary>Removes a link from the shared library. Like a probe it can be used by readings in
    /// other tiles, which then show a dash — said out loud rather than discovered on the pad.</summary>
    private void BtnLinkDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((CbLink.SelectedItem as Item<string>)?.Value is not { Length: > 0 } id) return;
        if (GameLinkStore.ById(id) is not { } link) return;

        if (MessageBox.Show(this, Loc.Get("studio_link_delete_confirm", link.Name),
                            Loc.Get("studio_title"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        GameLinkStore.Delete(id);
        FillLinks(null);
        UpdateLinkStatus();
        SaveReading();
    }

    /// <summary>Re-reads what the link is carrying. Manual rather than automatic because the answer
    /// only changes when the GAME changes state — and because the list is long enough that
    /// rebuilding it under the user's cursor while they scroll it would be hostile.</summary>
    /// <summary>Re-reads what the link says for the path in the box, without waiting for the next
    /// poll. It no longer fetches the link's whole payload: there is no list left to fill here, and
    /// on a link that dumps a running game building that answer is what costs the game a stutter —
    /// the tree browser asks for it when a person actually goes looking.</summary>
    private void BtnLinkRefresh_Click(object sender, RoutedEventArgs e) => RefreshLinkNow();

    /// <summary>Asks the link now rather than showing whatever the poll last had — which, for a link
    /// just picked, is nothing yet, and read as "the game is not answering". Looks twice: a mod
    /// that resolves only the values it is told about (K2 Unity Link) answers the first request
    /// with its previous set and has the new one ready a moment later.</summary>
    private async void RefreshLinkNow()
    {
        if (!CurrentSource().IsLink() || SelectedLinkId() is not { Length: > 0 } linkId)
        {
            UpdateLinkStatus();
            return;
        }

        LblLinkStatus.Text = Loc.Get("link_tree_loading");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await System.Threading.Tasks.Task.Delay(800);
            var snapshot = await GameLinkReader.RefreshAsync(linkId);
            if (SelectedLinkId() != linkId) return;   // another link was picked meanwhile

            string path = TxtValuePath.Text.Trim();
            if (!snapshot.Known || path.Length == 0 || snapshot.Values.ContainsKey(path)) break;
        }
        UpdateLinkStatus();
        UpdateScaleEnds();
    }

    /// <summary>Opens the tree browser for the reading's own value path — for a link with real
    /// hierarchy (a generic Unity scene dump can easily be a few hundred paths deep), a flat filtered
    /// combo is not something a human can orient in.</summary>
    private void BtnValuePathTree_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSource() == CustomSource.Telemachus)
        {
            if (PickTelemachus(TxtValuePath.Text) is not { } api) return;
            TxtValuePath.Text = api;
            ReadingField_Changed(this, new RoutedEventArgs());
            return;
        }

        string linkId = (CbLink.SelectedItem as Item<string>)?.Value ?? "";
        var dlg = new GameLinkTreeDialog(linkId) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        TxtValuePath.Text = dlg.SelectedPath;
        ReadingField_Changed(this, new RoutedEventArgs());
    }

    /// <summary>Both scale ends: the path box and its X exist only while a path is set, and then the
    /// number box is read-only and shows the value the path reads right now. Called again every
    /// preview tick so that value stays live.</summary>
    private void UpdateScaleEnds()
    {
        // A screen reading has no named values to read an end from.
        bool link = CurrentSource().IsValueSource();
        UpdateScaleEnd(TxtMin, BtnMinTree, TxtMinPath, BtnMinClear, link);
        UpdateScaleEnd(TxtMax, BtnMaxTree, TxtMaxPath, BtnMaxClear, link);
    }

    private void UpdateScaleEnd(TextBox number, Button lens, TextBox path, Button clear, bool link)
    {
        string p = path.Text.Trim();
        lens.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
        path.Visibility = clear.Visibility = p.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (p.Length > 0)
        {
            number.IsReadOnly = true;
            number.Text = LiveValue(p) ?? "—";
        }
        else if (number.IsReadOnly)
        {
            // The path just went away: back to an empty box to type into, not the last live value.
            number.IsReadOnly = false;
            number.Text = "";
        }
    }

    /// <summary>What <paramref name="path"/> reads from the reading's source right now, null when
    /// nothing answers for it.</summary>
    private string? LiveValue(string path)
    {
        string? text = null;
        if (CurrentSource() == CustomSource.Telemachus)
            text = TelemachusClient.Text(TelemachusClient.Want(new[] { path }).Get(path));
        else if (CurrentSource().IsLink() && (CbLink.SelectedItem as Item<string>)?.Value is { Length: > 0 } linkId)
        {
            var snapshot = GameLinkReader.Snapshot(linkId);
            if (snapshot.Known && snapshot.Values.TryGetValue(path, out string? value)) text = value;
        }
        return text is null ? null : ParseNum(text) is { } n ? Num(n) : text;
    }

    private void BtnScaleClear_Click(object sender, RoutedEventArgs e)
    {
        (sender == BtnMinClear ? TxtMinPath : TxtMaxPath).Text = "";
        ReadingField_Changed(this, new RoutedEventArgs());
    }

    /// <summary>Picks a scale end from the game — same browsers as the value path.</summary>
    private void BtnScaleTree_Click(object sender, RoutedEventArgs e)
    {
        var box = sender == BtnMinTree ? TxtMinPath : TxtMaxPath;
        string? picked;
        if (CurrentSource() == CustomSource.Telemachus)
            picked = PickTelemachus(box.Text);
        else
        {
            var dlg = new GameLinkTreeDialog((CbLink.SelectedItem as Item<string>)?.Value ?? "") { Owner = this };
            picked = dlg.ShowDialog() == true ? dlg.SelectedPath : null;
        }
        if (picked is null) return;
        box.Text = picked;
        ReadingField_Changed(this, new RoutedEventArgs());
    }

    private void BtnStateValuePathTree_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSource() == CustomSource.Telemachus)
        {
            if (PickTelemachus(TxtStateValuePath.Text) is not { } api) return;
            TxtStateValuePath.Text = api;
            StateField_Changed(this, new RoutedEventArgs());
            return;
        }

        string linkId = (CbLink.SelectedItem as Item<string>)?.Value ?? "";
        var dlg = new GameLinkTreeDialog(linkId) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        TxtStateValuePath.Text = dlg.SelectedPath;
        StateField_Changed(this, new RoutedEventArgs());
    }

    /// <summary>The Telemachus entry browser, opened on <paramref name="current"/>. Null when
    /// cancelled.</summary>
    private string? PickTelemachus(string current)
    {
        var dlg = new TelemachusPickerDialog(current) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.SelectedApi : null;
    }

    private void OpenLink(string? linkId)
    {
        var dlg = new GameLinkDialog(linkId, CurrentLinkKind()) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        FillLinks(dlg.SavedLinkId);
        UpdateLinkStatus();
        SaveReading();
    }

    private void LstStates_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadState(LstStates.SelectedItem as CustomActionState);
        BtnStateRemove.IsEnabled = BtnStateProbe.IsEnabled = BtnStateIcon.IsEnabled =
            LstStates.SelectedItem is not null;
    }

    private void LoadState(CustomActionState? state)
    {
        bool wasLoading = _loading;
        _loading = true;
        TxtStateName.Text = state?.Name ?? "";
        TxtStateValuePath.Text = state?.ValuePath ?? "";
        TxtStateMatch.Text = state?.Match ?? "";
        _loading = wasLoading;
    }

    private void StateField_Changed(object sender, RoutedEventArgs e) => SaveState(s => s with
    {
        Name = TxtStateName.Text.Trim().Length > 0 ? TxtStateName.Text.Trim() : Loc.Get("studio_state_untitled"),
        ValuePath = TxtStateValuePath.Text.Trim(),
        Match = TxtStateMatch.Text.Trim(),
    });

    /// <summary>Applies a change to the selected state and saves the reading. States are a list
    /// inside the reading, so there is no "save state" of its own.</summary>
    private void SaveState(Func<CustomActionState, CustomActionState> change)
    {
        if (_loading || LstStates.SelectedItem is not CustomActionState current) return;
        int i = _states.IndexOf(current);
        if (i < 0) return;

        _states[i] = change(current);
        LstStates.SelectedItem = _states[i];
        SaveReading();
    }

    private void BtnStateAdd_Click(object sender, RoutedEventArgs e)
    {
        // In "one rectangle, one colour per state" mode a new state starts on the SAME rectangle as
        // the first one: that is what the mode means, and making the user redraw the identical box
        // for every state is how two of them end up a pixel apart.
        string? seed = null;
        if ((CbMultiMode.SelectedItem as Item<CustomMultiMode>)?.Value == CustomMultiMode.Colors &&
            _states.FirstOrDefault() is { } first &&
            ScreenProbeStore.ById(first.ProbeId) is { } model)
        {
            var copy = model with { Id = ScreenProbeStore.NewId(), Name = Loc.Get("studio_state_untitled") };
            ScreenProbeStore.Save(copy);
            seed = copy.Id;
        }

        _states.Add(new CustomActionState { Name = Loc.Get("studio_state_untitled"), ProbeId = seed ?? "" });
        LstStates.SelectedItem = _states[^1];
        SaveReading();
        TxtStateName.Focus();
        TxtStateName.SelectAll();
    }

    private void BtnStateRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LstStates.SelectedItem is not CustomActionState state) return;
        _states.Remove(state);
        LstStates.SelectedItem = _states.FirstOrDefault();
        SaveReading();
    }

    private void BtnStateProbe_Click(object sender, RoutedEventArgs e)
    {
        if (LstStates.SelectedItem is not CustomActionState state) return;
        var dlg = new ScreenProbeDialog(string.IsNullOrEmpty(state.ProbeId) ? null : state.ProbeId,
                                        null, ProbeProfileKey(), ActionProfileProcess())
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true) return;
        SaveState(s => s with { ProbeId = dlg.SavedProbeId ?? "" });
    }

    private void BtnStateIcon_Click(object sender, RoutedEventArgs e)
    {
        if (PickImage() is not { } path) return;
        SaveState(s => s with { IconPath = path });
    }

    // ─────────────────────────── elements ───────────────────────────

    private TileElement? SelectedElement =>
        _elementIndex >= 0 && _elementIndex < _elements.Count ? _elements[_elementIndex] : null;

    private static readonly Dictionary<TileElementKind, Color> ElementColours = new()
    {
        [TileElementKind.Icon] = Color.FromRgb(0xC0, 0x8C, 0xF0),
        [TileElementKind.Indicator] = Color.FromRgb(0x5E, 0xBE, 0xEB),
        [TileElementKind.Value] = Color.FromRgb(0xFF, 0x9A, 0x3C),
        [TileElementKind.Label] = Color.FromRgb(0x9B, 0xE1, 0x5D),
    };

    private static string KindName(TileElementKind kind) => Loc.Get(kind switch
    {
        TileElementKind.Icon => "studio_el_icon",
        TileElementKind.Indicator => "studio_el_indicator",
        TileElementKind.Value => "studio_el_value",
        _ => "studio_el_caption",
    });

    /// <summary>The second line of an element's row: what it actually shows. A value or an
    /// indicator names its reading, a label quotes its words, a picture its file.</summary>
    private string ElementDetail(TileElement element) => element.Kind switch
    {
        // The reading first when there is one: a picture on a mirror (or on a multi-state reading)
        // takes its image from the reading, and its own PNG is only the fallback.
        TileElementKind.Icon => element.ReadingId is { Length: > 0 }
            ? ReadingName(element.ReadingId, Loc.Get("studio_no_image"))
            : element.IconPath is { Length: > 0 } p ? Path.GetFileName(p) : Loc.Get("studio_no_image"),
        TileElementKind.Label => element.Text.Length > 0 ? element.Text : Loc.Get("studio_el_text_key"),
        _ => ReadingName(element.ReadingId, Loc.Get("studio_el_reading_none")),
    };

    private string ReadingName(string? id, string fallback) =>
        _readings.FirstOrDefault(r => r.Id == id)?.Name ?? fallback;

    private void RefreshElementList()
    {
        bool wasLoading = _loading;
        _loading = true;

        // The readings go first: the selected piece's own combo is filled from this list, and a
        // combo set from a list that no longer holds its reading would come up on "none".
        FillElementReadings();
        _elementRows.Clear();
        foreach (var e in _elements)
        {
            var row = new ElementRow();
            FillElementRow(row, e);
            _elementRows.Add(row);
        }

        if (_elementIndex >= _elementRows.Count) _elementIndex = _elementRows.Count - 1;
        LstElements.SelectedIndex = _elementIndex;

        _loading = wasLoading;
        LoadElement();
    }

    /// <summary>Puts a piece into its row of the layer list: what it is, what it shows, and the
    /// colour it wears as an outline on the preview — everything else is edited in the panel under
    /// the list, for the selected piece only.</summary>
    private void FillElementRow(ElementRow row, TileElement element)
    {
        row.Kind = KindName(element.Kind);
        row.Detail = ElementDetail(element);
        row.Colour = new SolidColorBrush(ElementColours.GetValueOrDefault(element.Kind, Colors.Gray));
    }

    /// <summary>The colour a piece wears when it has none of its own: the profile's accent for an
    /// indicator, the tile's lettering for everything else.</summary>
    private string InheritedColour(TileElement element) => CustomTileRenderer.ToHex(
        element.Kind == TileElementKind.Indicator
            ? CustomGameStore.AccentFor(_action?.ProfileId)
            : CustomTileRenderer.ParseColor(
                CustomGameStore.EffectiveStyleFor(_action?.ProfileId).MergedWith(_action?.Style).TextOn,
                System.Drawing.Color.White));

    private void LstElements_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _elementIndex = LstElements.SelectedIndex;
        LoadElement();
    }

    /// <summary>
    /// The panel under the layer list: everything about the SELECTED piece, and only the rows its
    /// kind actually has — a label types words where the others point at a reading, only an
    /// indicator has a shape, only a picture has a PNG, only text has a face.
    /// </summary>
    private void LoadElement()
    {
        bool wasLoading = _loading;
        _loading = true;

        var element = SelectedElement;
        PnlElement.Visibility = element is not null ? Visibility.Visible : Visibility.Collapsed;
        TxtElPropsEmpty.Visibility = element is not null ? Visibility.Collapsed : Visibility.Visible;
        BtnElementDelete.IsEnabled = element is not null;
        BtnElementUp.IsEnabled = element is not null && _elementIndex > 0;
        BtnElementDown.IsEnabled = element is not null && _elementIndex < _elements.Count - 1;

        var el = element ?? new TileElement();
        bool isLabel = el.Kind == TileElementKind.Label;
        bool isIcon = el.Kind == TileElementKind.Icon;
        bool isText = el.Kind is TileElementKind.Label or TileElementKind.Value;
        bool isIndicator = el.Kind == TileElementKind.Indicator;

        // One row, two faces: a piece that shows a reading picks one, a label types its words.
        LblElMain.Text = Loc.Get(isLabel ? "studio_el_text" : "studio_el_reading");
        HintElMain.ToolTip = Loc.Get(isLabel ? "studio_el_text_hint" : "studio_el_reading_hint");
        CbElReading.Visibility = isLabel ? Visibility.Collapsed : Visibility.Visible;
        TxtElText.Visibility = isLabel ? Visibility.Visible : Visibility.Collapsed;
        CbElReading.SelectedValue = el.ReadingId ?? "";
        TxtElText.Text = el.Text;

        PnlElShapeLabel.Visibility = CbElShape.Visibility =
            isIndicator ? Visibility.Visible : Visibility.Collapsed;
        CbElShape.SelectedValue = el.Shape;

        var c = CustomTileRenderer.ParseColor(el.Colour ?? InheritedColour(el), System.Drawing.Color.White);
        BtnElColour.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        // Faded when inherited, so "no colour of its own" still reads as a colour and not as black.
        BtnElColour.Opacity = el.Colour is null ? 0.45 : 1;
        ChkElInherit.IsChecked = el.Colour is null;

        PnlElIconLabel.Visibility = PnlElIcon.Visibility = isIcon ? Visibility.Visible : Visibility.Collapsed;
        PnlElFontLabel.Visibility = PnlElFont.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;

        // Auto is shown as what it prints: the percentage for a range, the number for anything else.
        PnlElValueLabelLabel.Visibility = PnlElValueLabel.Visibility =
            el.Kind == TileElementKind.Value ? Visibility.Visible : Visibility.Collapsed;
        var shownLabel = el.ValueLabel != CustomValueLabel.Auto ? el.ValueLabel
            : _readings.FirstOrDefault(r => r.Id == el.ReadingId)?.ValueKind == CustomValueKind.Range
                ? CustomValueLabel.Percent : CustomValueLabel.Number;
        RbElPercent.IsChecked = shownLabel == CustomValueLabel.Percent;
        RbElNumber.IsChecked = shownLabel == CustomValueLabel.Number;
        RbElValueOfMax.IsChecked = shownLabel == CustomValueLabel.ValueOfMax;

        CbFont.SelectedItem = CbFont.Items.OfType<Item<string>>()
            .FirstOrDefault(i => i.Value == (el.FontFamily ?? "")) ?? CbFont.Items[0];
        ChkValueAuto.IsChecked = el.FontSize <= 0;
        SldValueSize.Value = el.FontSize > 0 ? el.FontSize : 24;
        UpdateFontSizeLabel();

        ChkElSnap.IsChecked = el.Snap;
        TxtElEdge.Text = Math.Round(el.Margin * 100).ToString(CultureInfo.InvariantCulture);

        ShowElementIcon(el.IconPath);

        _loading = wasLoading;
    }

    private void ElAdvancedToggle_Click(object sender, MouseButtonEventArgs e) =>
        SetAdvancedOpen(PnlElAdvanced.Visibility != Visibility.Visible);

    /// <summary>Snapping and the edge margin: real settings, but ones a tile is finished without —
    /// so they are a click away rather than two more controls on every row.</summary>
    private void SetAdvancedOpen(bool open)
    {
        PnlElAdvanced.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        TxtElAdvancedToggle.Text = (open ? "▾  " : "▸  ") + Loc.Get("studio_advanced");
    }

    private void ShowElementIcon(string? path)
    {
        LblActionIcon.Text = ShortPath(path);
        ImgActionIcon.Source = LoadBitmap(path);
    }

    private void ElementField_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFontSizeLabel();
        SaveElement();
    }

    private void ElementSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFontSizeLabel();
        SaveElement();
    }

    private void UpdateFontSizeLabel()
    {
        if (LblValueSize is null || ChkValueAuto is null || SldValueSize is null) return;
        SldValueSize.IsEnabled = ChkValueAuto.IsChecked != true;
        LblValueSize.Text = ChkValueAuto.IsChecked == true
            ? "" : ((int)Math.Round(SldValueSize.Value)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Writes the strip under the table back into the selected piece.</summary>
    private void SaveElement()
    {
        if (_loading || SelectedElement is not { } current) return;

        var next = current with
        {
            FontFamily = (CbFont.SelectedItem as Item<string>)?.Value is { Length: > 0 } f ? f : null,
            FontSize = ChkValueAuto.IsChecked == true ? 0 : Math.Round(SldValueSize.Value),
        };
        if (next == current) return;

        _elements[_elementIndex] = next;
        SaveAction();
        RefreshElementRow();
        RefreshPreview();
    }

    /// <summary>An edit from the properties panel, applied to the selected piece. One place, so
    /// every control down there saves, relabels its row and redraws the preview the same way.</summary>
    private void EditSelectedElement(Func<TileElement, TileElement> change)
    {
        if (_loading || SelectedElement is not { } current) return;

        var next = change(current);
        if (next == current) return;

        _elements[_elementIndex] = next;
        SaveAction();
        RefreshElementRow();
        LoadElement();
        RefreshPreview();
    }

    private void ElReading_Changed(object sender, SelectionChangedEventArgs e) =>
        // Null is not "none" — that is the empty id — but a combo whose value has not been bound
        // yet, or one whose list was just rebuilt: neither is the user choosing anything.
        EditSelectedElement(el => CbElReading.SelectedValue is string id ? el with { ReadingId = id } : el);

    private void ElShape_Changed(object sender, SelectionChangedEventArgs e) =>
        EditSelectedElement(el => CbElShape.SelectedValue is CustomIndicatorShape shape
            ? el with { Shape = shape } : el);

    private void ElValueLabel_Click(object sender, RoutedEventArgs e) =>
        EditSelectedElement(el => el with
        {
            ValueLabel = RbElPercent.IsChecked == true ? CustomValueLabel.Percent
                       : RbElValueOfMax.IsChecked == true ? CustomValueLabel.ValueOfMax
                       : CustomValueLabel.Number,
        });

    private void ElText_Changed(object sender, RoutedEventArgs e) =>
        EditSelectedElement(el => el with { Text = TxtElText.Text });

    private void ElSnap_Click(object sender, RoutedEventArgs e) =>
        EditSelectedElement(el => el with { Snap = ChkElSnap.IsChecked == true });

    private void ElEdge_Changed(object sender, RoutedEventArgs e) =>
        EditSelectedElement(el => el with
        {
            Margin = Math.Clamp(ParseNum(TxtElEdge.Text) ?? 4, 0, 45) / 100d,
        });

    private void ElColour_Click(object sender, RoutedEventArgs e) =>
        EditSelectedElement(el =>
        {
            int current = CustomTileRenderer.ParseColor(el.Colour ?? InheritedColour(el),
                                                       System.Drawing.Color.White).ToArgb() & 0xFFFFFF;
            return ColorPickerDialog.Pick(this, current) is { } rgb ? el with { Colour = $"#{rgb:X6}" } : el;
        });

    /// <summary>Ticked, the piece follows the tile's colour instead of holding a copy of whatever
    /// it was when it was set; unticked, it keeps the colour it was inheriting and owns it from
    /// there — so turning it off does not change how the tile looks, only what it obeys.</summary>
    private void ElInherit_Click(object sender, RoutedEventArgs e) =>
        EditSelectedElement(el => ChkElInherit.IsChecked == true
            ? el with { Colour = null }
            : el.Colour is null ? el with { Colour = InheritedColour(el) } : el);

    private void RefreshElementRow()
    {
        if (SelectedElement is not { } el || _elementIndex >= _elementRows.Count) return;
        FillElementRow(_elementRows[_elementIndex], el);
    }

    /// <summary>"Add element": a menu of the four kinds. A new piece lands in the middle of the tile
    /// with the first reading already attached, so it draws something immediately instead of being
    /// an invisible entry in a list.</summary>
    private void BtnElementAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_action is null || sender is not Button button) return;

        var menu = new ContextMenu();
        foreach (var kind in new[] { TileElementKind.Value, TileElementKind.Label,
                                     TileElementKind.Indicator, TileElementKind.Icon })
        {
            var item = new MenuItem { Header = KindName(kind) };
            var captured = kind;
            item.Click += (_, _) => AddElement(captured);
            menu.Items.Add(item);
        }
        ShowMenu(menu, button);
    }

    private void AddElement(TileElementKind kind)
    {
        _elements.Add(new TileElement
        {
            Id = CustomGameStore.NewPartId(),
            Kind = kind,
            // A picture starts on a MIRROR reading when the tile has one: that is the only reading
            // a picture can show by itself, and it is what the user just built it for.
            ReadingId = kind switch
            {
                TileElementKind.Value or TileElementKind.Indicator => _readings.FirstOrDefault()?.Id ?? "",
                TileElementKind.Icon => _readings.FirstOrDefault(r => r.ValueKind == CustomValueKind.Mirror)?.Id ?? "",
                _ => "",
            },
            Anchor = TileAnchor.Center,
        });
        _elementIndex = _elements.Count - 1;

        SaveAction();
        RefreshElementList();
        RebuildHandles();
        RefreshPreview();
    }

    private void BtnElementDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedElement is null) return;
        _elements.RemoveAt(_elementIndex);
        _elementIndex = Math.Min(_elementIndex, _elements.Count - 1);

        SaveAction();
        RefreshElementList();
        RebuildHandles();
        RefreshPreview();
    }

    private void BtnElementUp_Click(object sender, RoutedEventArgs e) => MoveElement(-1);
    private void BtnElementDown_Click(object sender, RoutedEventArgs e) => MoveElement(+1);

    /// <summary>Moves a piece in the drawing order. The list IS the stack: later means on top, so
    /// "down" in the list is "in front" on the tile.</summary>
    private void MoveElement(int delta)
    {
        int index = _elementIndex, target = index + delta;
        if (index < 0 || target < 0 || target >= _elements.Count) return;

        (_elements[index], _elements[target]) = (_elements[target], _elements[index]);
        _elementIndex = target;

        SaveAction();
        RefreshElementList();
        RebuildHandles();
        RefreshPreview();
    }

    // ─────────────────────────── colours and pictures ───────────────────────────

    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which }) return;
        var swatch = SwatchFor(which);
        if (swatch is null) return;

        int current = CustomTileRenderer.ParseColor(SwatchHex(swatch), System.Drawing.Color.White).ToArgb() & 0xFFFFFF;
        if (ColorPickerDialog.Pick(this, current) is not { } rgb) return;

        string hex = $"#{rgb:X6}";
        SetSwatch(swatch, hex);

        if (which.StartsWith("a", StringComparison.Ordinal)) SaveAction();
        else SaveProfile();
    }

    private Border? SwatchFor(string which) => which switch
    {
        "accent" => SwAccent,
        "dbgon" => SwBgOn,
        "dtexton" => SwTextOn,
        "dbgoff" => SwBgOff,
        "dtextoff" => SwTextOff,
        "abgon" => SwABgOn,
        "atexton" => SwATextOn,
        "abgoff" => SwABgOff,
        "atextoff" => SwATextOff,
        _ => null,
    };

    private void BtnPickImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which }) return;
        if (PickImage() is not { } path) return;
        ApplyImage(which, path);
    }

    private void BtnClearImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which }) return;
        ApplyImage(which, null);
    }

    private void ApplyImage(string which, string? path)
    {
        switch (which)
        {
            case "dbgimgon":
            {
                if (_row is null) return;
                // Clearing a SHIPPED profile's picture means "back to the game's own art", not "no
                // picture": that is the only sensible undo for a frame the user replaced.
                var style = CustomGameStore.EffectiveStyleFor(_row.Id);
                string? next = path ?? (_builtin is not null
                    ? CustomGameStore.ShippedStyleFor(_builtin.Id).BackgroundImageOn
                    : null);
                string? nextOff = path ?? (_builtin is not null
                    ? CustomGameStore.ShippedStyleFor(_builtin.Id).BackgroundImageOff
                    : null);

                var updated = style with { BackgroundImageOn = next, BackgroundImageOff = nextOff };
                if (_profile is not null)
                {
                    _profile = _profile with { DefaultStyle = updated };
                    CustomGameStore.SaveProfile(_profile);
                }
                else if (_builtin is not null)
                {
                    CustomGameStore.SaveShippedStyle(_builtin.Id, updated);
                }
                ShowProfileBackground(updated.BackgroundImageOn);
                BuildKeyCanvas();
                break;
            }

            case "abgimgon":
                if (_action is null) return;
                _action = _action with
                {
                    Style = (_action.Style ?? new CustomTileOverride()) with { BackgroundImageOn = path }
                };
                ChkActionOverride.IsChecked = true;
                GrdActionStyle.Visibility = Visibility.Visible;
                CustomGameStore.SaveAction(_action);
                UpdateActionRow(_action);
                LblActionBgImage.Text = ShortPath(path);
                break;

            case "aicon":
                // The PICTURE of the selected element — a tile can hold several, so this is never
                // "the tile's icon".
                if (SelectedElement is not { } element) return;
                _elements[_elementIndex] = element with { IconPath = path };
                SaveAction();
                ShowElementIcon(path);
                RefreshElementRow();
                RebuildHandles();
                break;
        }
        RefreshPreview();
    }

    /// <summary>Asks for a picture and copies it into the studio's own folder, so the profile keeps
    /// working when the original is moved. Null when the user cancelled or the copy failed.</summary>
    private string? PickImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("studio_image_filter") };
        if (dlg.ShowDialog(this) != true) return null;
        return CustomGameStore.ImportImage(dlg.FileName);
    }

    // ─────────────────────────── pages and keys ───────────────────────────

    /// <summary>Key geometry of the pad picture, in the canvas' own device coordinates — the same
    /// numbers the DisplayPad section and the game-profile popup draw with, so the three show the
    /// keys in the same place.</summary>
    private const double KeyW = 60, KeyH = 60, GapH = 8, GapV = 10;
    private const double AreaLeft = 55, AreaRight = 455, AreaTop = 130, AreaBottom = 330;

    /// <summary>The key the bar under the pad is talking about, or -1 for none.</summary>
    private int _selectedKey = -1;

    /// <summary>The picture each key was last drawn with, so the bar can show the selected one
    /// without rendering the tile a second time.</summary>
    private readonly string?[] _keyPngs = new string?[12];

    /// <summary>How many pages the selected profile has, whichever kind it is.</summary>
    private int PageCount =>
        _profile is not null ? Math.Max(_profile.Pages.Count, 1)
        : _builtinPages is not null ? Math.Max(_builtinPages.Count, 1)
        : 0;

    /// <summary>Whether the keys on screen can be edited: a custom profile always, a shipped one
    /// only while the main window (and with it the pad's store) is around.</summary>
    private bool KeysEditable => _profile is not null || (_builtin is not null && Host is not null);

    private void ReloadPages()
    {
        int count = Math.Max(PageCount, 1);
        _pageIndex = Math.Clamp(_pageIndex, 0, count - 1);

        TxtPage.Text = string.Format(Loc.Get("game_profile_page_fmt"), _pageIndex + 1, count);
        BtnPagePrev.IsEnabled = _pageIndex > 0;
        BtnPageNext.IsEnabled = _pageIndex < count - 1;
        // Paging chrome only exists once there is something to page through.
        PnlPageNav.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;

        // A shipped profile whose layout is fixed doesn't offer extra pages (Zero Company's tactical
        // grid), and only pages the USER added can be removed from one.
        BtnPageAdd.IsEnabled = KeysEditable &&
            (_profile is not null || GameProfileSpecs.AllowsExtraPages(_builtin?.Id));
        BtnPageRemove.IsEnabled = KeysEditable && _pageIndex >= ShippedPageCount && count > 1;

        HintKeys.ToolTip = Loc.Get(
            _builtin is not null && Host is null ? "studio_keys_readonly" : "studio_layout_hint");

        BuildKeyCanvas();
    }

    /// <summary>Pages that are not the user's to delete: all of a shipped profile's catalogue pages,
    /// none of a custom profile's.</summary>
    private int ShippedPageCount => _builtin?.Pages.Count ?? 0;

    private void BtnPagePrev_Click(object sender, RoutedEventArgs e) { _pageIndex--; ReloadPages(); }
    private void BtnPageNext_Click(object sender, RoutedEventArgs e) { _pageIndex++; ReloadPages(); }

    /// <summary>The twelve slots of the current page, whichever kind of profile is selected. A
    /// shipped profile's tiles are translated into the same shape as a custom one's — with their
    /// captions already resolved through <see cref="Loc"/> — so everything below this point draws
    /// and edits one thing.</summary>
    private IReadOnlyList<CustomGameTile> CurrentTiles()
    {
        if (_profile is not null)
        {
            var pages = _profile.Pages;
            var tiles = _pageIndex < pages.Count ? pages[_pageIndex].Tiles : Array.Empty<CustomGameTile>();
            return Enumerable.Range(0, 12)
                .Select(i => i < tiles.Count ? tiles[i] : new CustomGameTile("", "", ""))
                .ToList();
        }

        if (_builtinPages is not null && _pageIndex < _builtinPages.Count)
        {
            var page = _builtinPages[_pageIndex];
            return Enumerable.Range(0, 12)
                .Select(i => i < page.Count
                    ? new CustomGameTile(page[i].ActionType, page[i].ActionValue,
                                         GameProfileCatalog.CaptionOf(page[i]))
                    : new CustomGameTile("", "", ""))
                .ToList();
        }

        return Enumerable.Range(0, 12).Select(_ => new CustomGameTile("", "", "")).ToList();
    }

    /// <summary>Draws the twelve keys of the current page ON the pad picture, each showing the very
    /// PNG the hardware would get (the same default-icon renderer the device path uses, which for a
    /// custom action is this studio's own painter). Rebuilt whole on every change: twelve buttons
    /// cost nothing to build, and a partial update is where a stale tile comes from.</summary>
    private void BuildKeyCanvas()
    {
        CvsKeys.Children.Clear();
        Array.Clear(_keyPngs, 0, _keyPngs.Length);
        if (_row is null) { UpdateKeyBar(); return; }

        const int rows = 2, cols = 6;
        double totalW = cols * KeyW + (cols - 1) * GapH;
        double totalH = rows * KeyH + (rows - 1) * GapV;
        double startX = AreaLeft + ((AreaRight - AreaLeft) - totalW) / 2;
        double startY = AreaTop + ((AreaBottom - AreaTop) - totalH) / 2;

        var tiles = CurrentTiles();
        for (int i = 0; i < 12; i++)
        {
            var btn = BuildKeyButton(i, tiles[i]);
            Canvas.SetLeft(btn, startX + (i % cols) * (KeyW + GapH));
            Canvas.SetTop(btn, startY + (i / cols) * (KeyH + GapV));
            CvsKeys.Children.Add(btn);
        }

        HighlightSelectedKey();
        UpdateKeyBar();
    }

    /// <summary>The accent frame around the key the bar is showing. Drawn by re-colouring the
    /// buttons rather than rebuilding them: a rebuild on every click would destroy the very button
    /// the second click of a double-click has to land on.</summary>
    private void HighlightSelectedKey()
    {
        foreach (var btn in CvsKeys.Children.OfType<Button>())
        {
            bool on = btn.Tag is int i && i == _selectedKey;
            btn.BorderThickness = new Thickness(on ? 2 : 1);
            btn.BorderBrush = on
                ? (Brush)FindResource("K2AccentBrush")
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        }
    }

    /// <summary>The bar under the pad: what the selected key does, and the three things that can be
    /// done to it. With no key selected it says how to pick one instead.</summary>
    private void UpdateKeyBar()
    {
        var tiles = _row is null ? null : CurrentTiles();
        var tile = tiles is not null && _selectedKey >= 0 && _selectedKey < tiles.Count
            ? tiles[_selectedKey] : null;

        PnlKeyActions.Visibility = tile is not null ? Visibility.Visible : Visibility.Collapsed;
        BdKeyThumb.Visibility = tile is not null ? Visibility.Visible : Visibility.Collapsed;

        if (tile is null)
        {
            TxtKeyTitle.Text = Loc.Get("studio_key_pick_hint");
            TxtKeyDesc.Text = "";
            ImgKeyBar.Source = null;
            return;
        }

        bool mapped = tile.ActionType.Length > 0;
        TxtKeyTitle.Text = Loc.Get("studio_key_fmt", _selectedKey + 1) +
                           (mapped && tile.Caption.Length > 0 ? "  ·  " + tile.Caption : "");
        TxtKeyDesc.Text = mapped
            ? ActionTypeHelper.Summary(tile.ActionType, tile.ActionValue)
            : Loc.Get("studio_key_empty");
        ImgKeyBar.Source = LoadBitmap(_keyPngs[_selectedKey]);

        // "Edit action" only means something for a tile this studio owns.
        BtnKeyEditAction.Visibility = CustomActionIdOf(tile) is not null
            ? Visibility.Visible : Visibility.Collapsed;
        BtnKeyChange.IsEnabled = KeysEditable;
        BtnKeyClear.IsEnabled = KeysEditable && mapped;
    }

    /// <summary>The studio action a key is bound to, or null when the key holds something else.</summary>
    private static string? CustomActionIdOf(CustomGameTile tile) =>
        tile.ActionType == CustomActionType.Tag &&
        CustomActionType.Parse(tile.ActionValue) is { } parsed && parsed.Id.Length > 0
            ? parsed.Id : null;

    private void BtnKeyEditAction_Click(object sender, RoutedEventArgs e)
    {
        if (_row is null || _selectedKey < 0) return;
        if (CustomActionIdOf(CurrentTiles()[_selectedKey]) is { } id) OpenAction(id);
    }

    private void BtnKeyChange_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKey >= 0) AssignKey(_selectedKey);
    }

    private void BtnKeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKey >= 0) WriteTile(_selectedKey, new CustomGameTile("", "", ""));
    }

    private Button BuildKeyButton(int index, CustomGameTile tile)
    {
        var content = new Grid();
        bool mapped = tile.ActionType.Length > 0;

        // The key grid is a preview: a live source is read even with K2 in front, or every custom
        // tile on it would show a dash.
        string? png = !mapped ? null : CustomActionTile.InPreview(() =>
            Services.DpDefaultIconRenderer.Render(
                tile.ActionType, tile.ActionValue, KeySpecFor(tile), pageName: null, iconSize: 102));
        if (index >= 0 && index < _keyPngs.Length) _keyPngs[index] = png;

        if (png is not null && File.Exists(png))
        {
            content.Children.Add(new Image { Source = LoadBitmap(png), Stretch = Stretch.UniformToFill });
        }
        else if (mapped)
        {
            content.Children.Add(new TextBlock
            {
                Text = tile.Caption,
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
            Cursor = Cursors.Hand,
            Tag = index,
            Content = content,
            IsEnabled = KeysEditable,
            ToolTip = mapped ? ActionTypeHelper.Summary(tile.ActionType, tile.ActionValue) : null,
        };
        btn.Click += KeyTile_Click;
        btn.MouseDoubleClick += KeyTile_DoubleClick;
        btn.ContextMenu = BuildKeyMenu(index);
        return btn;
    }

    /// <summary>The icon spec a tile is drawn with. A shipped profile's tiles wear that game's own
    /// frame art and caption colour (the same <c>TextOnly</c> rule <c>EnsureGameSlot</c> applies), so
    /// the preview here is the pad's page and not an approximation of it.</summary>
    private KeyIconSpec KeySpecFor(CustomGameTile tile)
    {
        var spec = new KeyIconSpec { DefaultIcon = true, ShowText = true, Text = tile.Caption };
        if (_builtin is null) return spec;

        var catalogTile = new GameProfileTile(tile.ActionType, tile.ActionValue, tile.Caption,
                                                      CaptionIsLocKey: false);
        if (!GameProfileCatalog.IsStyledTile(_builtin.Id, catalogTile)) return spec;

        // Fixed at the resting colour rather than the live state: editing a profile should look the
        // same whether or not the game happens to be running right now.
        LiveTileRenderer.EdAccentOverride = GameProfileTheme.AccentFor(_builtin.Id);
        spec.TextOnly = true;
        spec.TextColor = GameProfileTheme.TextHexForPreview(_builtin.Id, lit: true);
        spec.BgImagePath = GameProfileTheme.BgImagePathForPreview(_builtin.Id, lit: true);
        return spec;
    }

    /// <summary>Right-click on a key: the one thing the click itself cannot do, which is empty the
    /// slot without walking through the action dialog to pick "No action".</summary>
    private ContextMenu BuildKeyMenu(int index)
    {
        var remove = new MenuItem { Header = Loc.Get("dp_remove_action") };
        remove.Click += (_, _) => WriteTile(index, new CustomGameTile("", "", ""));
        var menu = new ContextMenu();
        menu.Items.Add(remove);
        menu.Opened += (_, _) => remove.IsEnabled = KeysEditable && CurrentTiles()[index].ActionType.Length > 0;
        return menu;
    }

    /// <summary>A click SELECTS a key — the bar under the pad then says what it does. An empty key
    /// has nothing to say, so it goes straight to the picker instead of showing a bar that only
    /// reads "empty".</summary>
    private void KeyTile_Click(object sender, RoutedEventArgs e)
    {
        if (_row is null || sender is not Button { Tag: int index }) return;

        bool empty = CurrentTiles()[index].ActionType.Length == 0;
        _selectedKey = index;
        HighlightSelectedKey();
        UpdateKeyBar();

        if (empty && KeysEditable) AssignKey(index);
    }

    private void KeyTile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (KeysEditable && sender is Button { Tag: int index }) AssignKey(index);
    }

    /// <summary>Asks what the key should do, and writes the answer.</summary>
    private void AssignKey(int index)
    {
        if (_row is null || !KeysEditable) return;
        var current = CurrentTiles()[index];

        // The same key dialog the profile popup uses, restricted to the same categories — so a page
        // built here and one edited there offer exactly the same choices, and the profile's own
        // actions show up under the game's card.
        var seed = KeySpecFor(current);
        var picker = new ButtonActionDialog.GamePickerProfile(_row.Id, _row.Name, null);
        var dlg = new DpKeyConfigDialog(index, null, current.ActionType, current.ActionValue, seed.ToJson(),
                                        new[] { "input", "games", "live" }, picker)
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true) return;

        if (string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none")
        {
            WriteTile(index, new CustomGameTile("", "", ""));
            return;
        }

        string? typed = KeyIconSpec.FromJson(dlg.IconSpecJson)?.Text?.Trim();
        string caption = !string.IsNullOrWhiteSpace(typed)
            ? typed!
            : ActionTypeHelper.Summary(dlg.ActionType, dlg.ActionValue);
        WriteTile(index, new CustomGameTile(dlg.ActionType!, dlg.ActionValue ?? "", caption));
    }

    /// <summary>Puts one tile on the current page and saves it — to the studio's store for a custom
    /// profile, to the DisplayPad store's per-key overrides for a shipped one, which is exactly
    /// where the Configure popup reads and writes them.</summary>
    private void WriteTile(int index, CustomGameTile tile)
    {
        if (!KeysEditable) return;

        if (_profile is not null)
        {
            var tiles = CurrentTiles().ToList();
            tiles[index] = tile;

            var pages = _profile.Pages.ToList();
            while (pages.Count <= _pageIndex)
                pages.Add(new CustomGamePage { Name = string.Format(Loc.Get("studio_page_fmt"), pages.Count + 1) });
            pages[_pageIndex] = pages[_pageIndex] with { Tiles = tiles };

            _profile = _profile with { Pages = pages };
            CustomGameStore.SaveProfile(_profile);
        }
        else if (_builtin is not null && _builtinPages is not null && Host is { } host)
        {
            while (_builtinPages.Count <= _pageIndex)
                _builtinPages.Add(GameProfileCatalog.EmptyPage().ToList());
            var page = _builtinPages[_pageIndex];
            while (page.Count <= index) page.Add(new GameProfileTile("", "", "", CaptionIsLocKey: false));

            // The user's own wording, so the caption stops being a loc key from here on.
            page[index] = new GameProfileTile(tile.ActionType, tile.ActionValue, tile.Caption,
                                                      CaptionIsLocKey: false);
            host.SavePages(_builtin.Id, _builtinPages);
        }

        BuildKeyCanvas();
    }

    private void BtnPageAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is not null)
        {
            var pages = _profile.Pages.ToList();
            pages.Add(new CustomGamePage
            {
                Name = string.Format(Loc.Get("studio_page_fmt"), pages.Count + 1),
                Tiles = Enumerable.Range(0, 12).Select(_ => new CustomGameTile("", "", "")).ToList(),
            });
            _profile = _profile with { Pages = pages };
            CustomGameStore.SaveProfile(_profile);
            _pageIndex = pages.Count - 1;
        }
        else if (_builtin is not null && _builtinPages is not null && Host is { } host)
        {
            _builtinPages.Add(GameProfileCatalog.EmptyPage().ToList());
            host.SavePages(_builtin.Id, _builtinPages);
            _pageIndex = _builtinPages.Count - 1;
        }
        ReloadPages();
    }

    private void BtnPageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PageCount <= 1 || _pageIndex < ShippedPageCount) return;
        if (MessageBox.Show(this, Loc.Get("studio_delete_page_confirm"), Loc.Get("studio_title"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (_profile is not null)
        {
            var pages = _profile.Pages.ToList();
            pages.RemoveAt(Math.Clamp(_pageIndex, 0, pages.Count - 1));
            _profile = _profile with { Pages = pages };
            CustomGameStore.SaveProfile(_profile);
        }
        else if (_builtin is not null && _builtinPages is not null && Host is { } host)
        {
            _builtinPages.RemoveAt(Math.Clamp(_pageIndex, 0, _builtinPages.Count - 1));
            host.SavePages(_builtin.Id, _builtinPages);
        }

        _pageIndex = 0;
        ReloadPages();
    }

    // ─────────────────────── preview, and dragging what is on it ───────────────────────

    /// <summary>Size the preview tile is rendered at. The outlines are laid out in these same units
    /// and then scaled to the control, so the box the user grabs is the space the piece really
    /// occupies on the key.</summary>
    private const int PreviewTileSize = DpHidNative.IconSize * 2;

    /// <summary>Outline and corner grip for each element, by its index in <see cref="_elements"/>.
    ///
    /// <para><b>The grips live on their own layer, above every outline.</b> Pieces overlap — that is
    /// the point of being able to stack them — and with the grip inside its own outline the one
    /// underneath could never be grabbed: whichever box was on top swallowed the click. Adding all
    /// the outlines first and all the grips after puts every grip above every body.</para></summary>
    private readonly List<Border> _handleBodies = new();
    private readonly List<Rectangle> _handleGrips = new();

    private int _dragIndex = -1;
    private bool _resizing;

    /// <summary>The last picture the preview was drawn from, so the outlines can be laid out without
    /// reading the sources again on every mouse move.</summary>
    private CustomTilePaint? _lastPaint;

    /// <summary>Which step of the demo sweep the indicators are showing while there is no real
    /// reading — see <see cref="DemoFractions"/>.</summary>
    private int _demoStep;

    /// <summary>The sweep an indicator runs through when its source says nothing: empty, a third,
    /// half, two thirds, full. Without it a new indicator shows an empty track and there is no way
    /// to see (or aim at) what the bar will actually look like.</summary>
    private static readonly double[] DemoFractions = { 0, 1d / 3, 0.5, 2d / 3, 1 };

    /// <summary>Rebuilds one outline and one grip per element. Called whenever the LIST changes
    /// (another tile loaded, element added, order changed) — a plain move or resize only needs
    /// <see cref="RefreshHandles"/>.</summary>
    private void RebuildHandles()
    {
        CvsHandles.Children.Clear();
        _handleBodies.Clear();
        _handleGrips.Clear();

        for (int i = 0; i < _elements.Count; i++)
        {
            var colour = ElementColours.GetValueOrDefault(_elements[i].Kind, Colors.Gray);
            var body = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(colour),
                CornerRadius = new CornerRadius(3),
                // Very nearly transparent, but NOT null: a brush is what makes the outline's whole
                // area catch the mouse, so the piece can be grabbed anywhere inside it.
                Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                Cursor = Cursors.SizeAll,
                ToolTip = $"{KindName(_elements[i].Kind)} — {Loc.Get("studio_move_hint")}",
                Tag = i,
                Visibility = Visibility.Collapsed,
            };
            body.MouseLeftButtonDown += Handle_MouseDown;
            _handleBodies.Add(body);
            CvsHandles.Children.Add(body);
        }

        // Second pass: every grip goes above every outline (see the field's remarks).
        for (int i = 0; i < _elements.Count; i++)
        {
            var colour = ElementColours.GetValueOrDefault(_elements[i].Kind, Colors.Gray);
            var grip = new Rectangle
            {
                Width = 11,
                Height = 11,
                Fill = new SolidColorBrush(colour),
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Cursor = Cursors.SizeNWSE,
                ToolTip = Loc.Get("studio_resize_hint"),
                Tag = i,
                Visibility = Visibility.Collapsed,
            };
            grip.MouseLeftButtonDown += Grip_MouseDown;
            _handleGrips.Add(grip);
            CvsHandles.Children.Add(grip);
        }

        RefreshHandles();
    }

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_action is null || sender is not Border { Tag: int index }) return;
        _dragIndex = index;
        _resizing = false;
        SelectElement(index);
        // Keyboard focus on the preview, so the arrows nudge the piece just grabbed.
        CvsHandles.Focus();
        CvsHandles.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_action is null || sender is not Rectangle { Tag: int index }) return;
        _dragIndex = index;
        _resizing = true;
        SelectElement(index);
        CvsHandles.Focus();
        CvsHandles.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Grabbing a piece on the preview also selects it in the list — the editor under it
    /// then shows what was just grabbed, instead of some other element's settings.</summary>
    private void SelectElement(int index)
    {
        if (index < 0 || index >= _elements.Count || index == _elementIndex) return;
        _elementIndex = index;
        LstElements.SelectedIndex = index;
        LoadElement();
    }

    private void Handles_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragIndex >= _handleBodies.Count) return;

        var body = _handleBodies[_dragIndex];
        var p = e.GetPosition(CvsHandles);

        if (_resizing)
        {
            // The top-left corner stays put and the box grows towards the cursor — the way every
            // resize grip in every editor behaves.
            body.Width = Math.Max(14, p.X - Canvas.GetLeft(body));
            body.Height = Math.Max(14, p.Y - Canvas.GetTop(body));
        }
        else
        {
            Canvas.SetLeft(body, p.X - body.Width / 2);
            Canvas.SetTop(body, p.Y - body.Height / 2);
        }
        PositionGrip(_dragIndex);
    }

    /// <summary>Drop. A RESIZE stores the box's new size as a fraction of the tile; a MOVE either
    /// snaps the piece to whichever of the nine cells its centre was let go over, or — with snapping
    /// off for that piece — keeps the exact spot it was dropped on.</summary>
    private void Handles_MouseUp(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0) return;
        int index = _dragIndex;
        bool resized = _resizing;
        _dragIndex = -1;
        _resizing = false;
        CvsHandles.ReleaseMouseCapture();

        double w = CvsHandles.ActualWidth, h = CvsHandles.ActualHeight;
        if (w <= 0 || h <= 0 || index >= _elements.Count || index >= _handleBodies.Count)
        {
            RefreshHandles();
            return;
        }

        var body = _handleBodies[index];
        var element = _elements[index];

        if (resized)
        {
            element = element with { Box = new TileBox(body.Width / w, body.Height / h) };
        }
        else
        {
            double cx = Canvas.GetLeft(body) + body.Width / 2, cy = Canvas.GetTop(body) + body.Height / 2;
            if (element.Snap)
            {
                int col = Math.Clamp((int)(cx / (w / 3)), 0, 2);
                int row = Math.Clamp((int)(cy / (h / 3)), 0, 2);
                element = element with { Anchor = (TileAnchor)(row * 3 + col) };
            }
            else
            {
                element = element with { X = Math.Clamp(cx / w, 0, 1), Y = Math.Clamp(cy / h, 0, 1) };
            }
        }

        _elements[index] = element;
        SaveAction();
        RefreshPreview();
    }

    /// <summary>A nudge not yet written to the store: holding an arrow repeats KeyDown dozens of
    /// times a second, so the moves only redraw and the save waits for the key to come up.</summary>
    private bool _nudgePending;

    /// <summary>Arrows move the selected piece by ONE pixel of the real key (not of the enlarged
    /// preview). A snapped piece is freed first, at the exact centre it is drawn on, so the first
    /// press moves it one pixel instead of jumping it off its snap point.</summary>
    private void Handles_KeyDown(object sender, KeyEventArgs e)
    {
        int dx = e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 };
        int dy = e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 };
        if (dx == 0 && dy == 0) return;
        // Handled even when nothing moves: otherwise the arrow walks the focus off the preview.
        e.Handled = true;
        if (_action is null || _dragIndex >= 0 || _lastPaint is null || SelectedElement is not { } element) return;

        bool freed = element.Snap;
        if (freed)
        {
            var placed = CustomTileRenderer.Layout(_lastPaint, PreviewTileSize)
                .FirstOrDefault(x => x.Piece.Element.Id == element.Id);
            if (placed.Piece is null) return;
            element = element with
            {
                Snap = false,
                X = (placed.Rect.X + placed.Rect.Width / 2) / PreviewTileSize,
                Y = (placed.Rect.Y + placed.Rect.Height / 2) / PreviewTileSize,
            };
        }

        double step = 1d / DpHidNative.IconSize;
        _elements[_elementIndex] = element with
        {
            X = Math.Clamp(element.X + dx * step, 0, 1),
            Y = Math.Clamp(element.Y + dy * step, 0, 1),
        };
        _nudgePending = true;

        if (freed) LoadElement(); // the snap box in the panel below must untick
        RefreshPreview();
    }

    private void Handles_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) CommitNudge();
    }

    private void Handles_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitNudge();

    private void CommitNudge()
    {
        if (!_nudgePending) return;
        _nudgePending = false;
        SaveAction();
    }

    /// <summary>Puts every piece back where the renderer would have put it: default positions and
    /// sizes, snapping on, default margins. One button, because undoing a layout piece by piece is
    /// worse than starting it again.</summary>
    private void BtnResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_action is null) return;

        for (int i = 0; i < _elements.Count; i++)
            _elements[i] = _elements[i] with
            {
                Anchor = _elements[i].Kind switch
                {
                    TileElementKind.Indicator => TileAnchor.BottomCenter,
                    TileElementKind.Value => TileAnchor.TopCenter,
                    TileElementKind.Label => TileAnchor.TopCenter,
                    _ => TileAnchor.Center,
                },
                Snap = true,
                Margin = 0.04,
                Box = null,
                X = 0.5,
                Y = 0.5,
            };

        SaveAction();
        LoadElement();
        RefreshPreview();
    }

    /// <summary>Lays the outlines over the preview, each on the rectangle the renderer says its
    /// piece occupies. A piece with nothing to show has no box.</summary>
    private void RefreshHandles()
    {
        double w = CvsHandles.ActualWidth, h = CvsHandles.ActualHeight;
        if (w <= 0 || h <= 0) return;

        foreach (var body in _handleBodies) body.Visibility = Visibility.Collapsed;
        foreach (var grip in _handleGrips) grip.Visibility = Visibility.Collapsed;
        if (_action is null || _lastPaint is null) return;

        var layout = CustomTileRenderer.Layout(_lastPaint, PreviewTileSize);
        double scale = w / PreviewTileSize;

        for (int i = 0; i < _elements.Count && i < _handleBodies.Count; i++)
        {
            // Matched by the element's ID: the paint carries copies, and two pieces of the same kind
            // must not swap outlines.
            var placed = layout.FirstOrDefault(x => x.Piece.Element.Id == _elements[i].Id);
            if (placed.Piece is null) continue;

            var body = _handleBodies[i];
            body.Visibility = Visibility.Visible;
            body.Width = Math.Max(14, placed.Rect.Width * scale);
            body.Height = Math.Max(14, placed.Rect.Height * scale);
            Canvas.SetLeft(body, placed.Rect.X * scale);
            Canvas.SetTop(body, placed.Rect.Y * scale);
            PositionGrip(i);
        }
    }

    /// <summary>Parks one grip on the bottom-right corner of its outline.</summary>
    private void PositionGrip(int index)
    {
        if (index < 0 || index >= _handleGrips.Count || index >= _handleBodies.Count) return;
        var body = _handleBodies[index];
        var grip = _handleGrips[index];

        grip.Visibility = body.Visibility;
        Canvas.SetLeft(grip, Canvas.GetLeft(body) + body.Width - grip.Width / 2);
        Canvas.SetTop(grip, Canvas.GetTop(body) + body.Height - grip.Height / 2);
    }

    /// <summary>Redraws the preview with the tile the pad would show right now — through the very
    /// same painter the hardware uses, so what is on screen here is not an approximation.</summary>
    private void RefreshPreview()
    {
        if (_action is null)
        {
            ImgPreview.Source = null;
            LblPreviewCaption.Text = "";
            _lastPaint = null;
            RefreshHandles();
            return;
        }

        try
        {
            // The preview draws from the WORKING lists, so a piece being dragged is already in its
            // new place before the tile is saved.
            var editing = _action with { Readings = _readings.ToList(), Elements = _elements.ToList() };

            // A preview reads its sources even though K2 — not the game — is the window in front;
            // otherwise every reading here would be a dash and nothing could be calibrated.
            var paint = CustomActionTile.InPreview(() => CustomActionTile.BuildPaint(editing, editing.Name));

            // Nothing to report: sweep the indicators through the demo values instead of leaving an
            // empty track. Otherwise a brand new indicator — or one whose game isn't running — gives
            // no idea of what the bar will look like, and no filled shape to aim at while dragging.
            double demo = DemoFractions[_demoStep % DemoFractions.Length];
            _demoStep++;
            paint = paint with
            {
                Pieces = paint.Pieces
                    .Select(p => p.Element.Kind == TileElementKind.Indicator && p.Fraction is null
                        ? p with { Fraction = demo }
                        : p)
                    .ToList(),
            };

            _lastPaint = paint;
            if (CustomTileRenderer.TryRender(paint, PreviewTileSize, _previewPath))
                ImgPreview.Source = LoadBitmap(_previewPath);

            // What the tile's headline reading says right now — the same line the key list shows.
            var headline = _readings.FirstOrDefault();
            string reading = headline is null
                ? ""
                : CustomActionTile.InPreview(() => CustomActionTile.Read(headline)).Text;
            LblPreviewCaption.Text = reading.Length > 0
                ? string.Format(Loc.Get("studio_preview_fmt"), reading) : "";
        }
        catch (Exception ex)
        {
            App.WriteLog($"[STUDIO] preview failed: {ex.Message}");
        }
        RefreshHandles();
    }

    // ─────────────────────────── small shared helpers ───────────────────────────

    /// <summary>Loads a PNG into a frozen bitmap that does NOT keep the file locked: these files are
    /// regenerated as the user edits, and the default (delayed, file-locking) load would both pin
    /// them and show a stale frame.</summary>
    private static BitmapImage? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // OnLoad reads the file NOW and lets go of it; IgnoreImageCache keeps WPF from handing
            // back the previous frame for the same path. The two must go with a UriSource: paired
            // with a StreamSource instead, WPF throws "Value cannot be null. (Parameter 'key')".
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[STUDIO] cannot load \"{path}\": {ex.Message}");
            return null;
        }
    }

    private static void SetSwatch(Border swatch, string? hex)
    {
        var c = CustomTileRenderer.ParseColor(hex, System.Drawing.Color.Black);
        swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        swatch.Tag = CustomTileRenderer.ToHex(c);
    }

    private static string SwatchHex(Border swatch) => swatch.Tag as string ?? "#000000";

    /// <summary>A scale end as typed, or null for an empty box — which is what "this number has no
    /// scale" looks like, and the reason the two fields are not spin boxes with a default.</summary>
    private static double? ParseNum(string? text) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double v) ? v : null;

    private static string Num(double? value) =>
        value is null ? "" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ShortPath(string? path) =>
        string.IsNullOrEmpty(path) ? Loc.Get("studio_no_image") : Path.GetFileName(path);

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().Length == 0 ? "profile" : name.Trim();
    }

    /// <summary>Opens the shared <see cref="GuideWindow"/> on the studio's own blocks
    /// (<c>gamestudio*</c> in <c>Guides/guide.&lt;lang&gt;.md</c>), read one after the other so the
    /// popup is the whole tour: what the studio is for, the profile view, the action editor and
    /// the two sources a reading can come from. The hint dots answer "what is this field?"; this
    /// answers "how do I build one?", which is why it is a button and not another tooltip.</summary>
    private void BtnGuide_Click(object sender, RoutedEventArgs e) =>
        new GuideWindow(new[] { "gamestudio", "gamestudio:profile", "gamestudio:action", "gamestudio:sources" },
                        Loc.Get("studio_title")) { Owner = this }.ShowDialog();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
