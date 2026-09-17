using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// One node of a link's payload, shaped back into a tree from the dotted paths
/// <see cref="Services.GameLinkReader"/> hands out flat. Public (not nested/private): XAML's
/// <c>HierarchicalDataTemplate DataType</c> has to name the type.
///
/// <para>Rebuilding a tree from already-flattened paths, rather than re-parsing the link's raw JSON,
/// keeps this picker working off the exact same cache every other part of the studio already reads
/// (<see cref="Services.GameLinkReader.Snapshot"/>) — no second fetch, no risk of showing a tree that
/// disagrees with the flat list the combo box still falls back to.</para>
/// </summary>
public sealed class GameLinkPathNode : INotifyPropertyChanged
{
    public string Name { get; init; } = "";

    /// <summary>The dotted path this node stands for — what a reading actually stores. Only
    /// meaningful to SELECT when <see cref="IsLeaf"/>; a branch's path is just where its children
    /// happen to live.</summary>
    public string FullPath { get; init; } = "";

    /// <summary>Null for a branch (an object/array grouping more paths under it), the game's own
    /// text for a leaf — the same value <see cref="Services.GameLinkReader.Value"/> would return for
    /// this path right now.</summary>
    public string? Value { get; set; }

    public ObservableCollection<GameLinkPathNode> Children { get; } = new();

    /// <summary>Set while the tree is built. What "one level up" walks — a search answers with a
    /// single branch, and seeing what sits ALONGSIDE the hit (food and water next to health) means
    /// climbing back out of it.</summary>
    public GameLinkPathNode? Parent { get; set; }

    public bool IsLeaf => Value is not null;

    /// <summary>Forced open by the search filter when a descendant matches; otherwise left for the
    /// user to expand by hand — a fully-expanded tree of a few hundred GameObjects would defeat the
    /// point of a browser.
    ///
    /// <para>Raises a change so "expand all" is possible at all: without it, setting this on nodes
    /// already on screen would change the model and leave the tree exactly as it was.</para></summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayText => IsLeaf ? $"{Name} = {Value}" : Name;
}

/// <summary>
/// Tree browser for a link's value path, offered next to the flat filtered combo box
/// (<c>GameStudioWindow</c>'s <c>CbValuePath</c>/<c>CbStateValuePath</c>) for exactly the case that
/// combo is bad at: a link with hundreds of paths and real hierarchy — a generic Unity scene dump
/// from <c>K2.UnityLink</c> being the reason this exists, but useful for any link with enough shape
/// to it (Satisfactory's factory dump, KSP's vessel tree).
/// </summary>
public partial class GameLinkTreeDialog : Window
{
    /// <summary>The path the dialog closed with, or null when cancelled.</summary>
    public string? SelectedPath { get; private set; }

    private readonly GameLinkPathNode _root = new() { Name = "", FullPath = "" };

    /// <summary>Which link this window is showing — kept because exporting re-fetches rather than
    /// serialising the tree back: the file is meant to be the game's own document, not this app's
    /// reconstruction of it.</summary>
    private readonly string? _linkId;

    /// <summary>The term the tree currently holds the answer to, or empty when it holds everything.</summary>
    private string _term = "";

    /// <summary>Which load is the current one. A load is started and awaited without blocking, so a
    /// slow one can land after a later, faster one — the whole-payload fetch takes seconds while a
    /// search comes back in milliseconds, and the stale arrival would wipe the fresh answer off the
    /// screen. Anything that is not the newest generation throws its result away.</summary>
    private int _generation;

    public GameLinkTreeDialog(string? linkId)
    {
        InitializeComponent();
        _linkId = linkId;

        // Nothing is fetched on open, on purpose: a link can answer with a whole running game, and
        // pulling megabytes to show a tree nobody can read is a cost paid for nothing. The window
        // opens ready to be ASKED something — or to be told to show everything, by the checkbox.
        LblSelected.Text = Loc.Get("link_tree_start");

        // ...unless it was open on this link before: then it comes back the way it was left. The
        // browser is typically reopened to pick the NEXT value beside the one just picked (the
        // maximum after the current value), and starting over from an empty search there was a
        // round trip every time.
        if (linkId is { Length: > 0 } && _memory.TryGetValue(linkId, out var remembered)) Restore(remembered);
        Closed += (_, _) => Remember();
    }

    /// <summary>What the window looked like when it was last closed on a link: the search, the
    /// branches shown in full, which rows were open, and — when it is small enough to keep — the
    /// answer itself, so reopening costs no fetch. For this run of K2 only.</summary>
    private sealed record TreeMemory(string FilterText, string Term, bool ShowAll,
                                     Dictionary<string, string>? Values,
                                     HashSet<string> ShownAll, Dictionary<string, bool> Expanded);

    private static readonly Dictionary<string, TreeMemory> _memory = new(StringComparer.Ordinal);

    /// <summary>Past this the answer is not kept between openings — a whole scene dump held for the
    /// rest of the session in a 32-bit process — and is fetched again instead.</summary>
    private const int MaxRememberedValues = 20_000;

    private void Remember()
    {
        if (string.IsNullOrEmpty(_linkId)) return;
        CaptureExpanded();

        Dictionary<string, string>? values = null;
        if (_root.Children.Count > 0)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in _byPath.Values)
                if (node.Value is { } v) values[node.FullPath] = v;
            if (values.Count > MaxRememberedValues) values = null;
        }

        _memory[_linkId] = new TreeMemory(TxtFilter.Text, _term, ChkShowAll.IsChecked == true, values,
                                          new HashSet<string>(_shownAll, StringComparer.Ordinal),
                                          new Dictionary<string, bool>(_expanded, StringComparer.Ordinal));
    }

    private void Restore(TreeMemory memory)
    {
        TxtFilter.Text = memory.FilterText;
        // Before the window is loaded, so the checkbox handler does not start a fetch of its own.
        ChkShowAll.IsChecked = memory.ShowAll;
        foreach (var path in memory.ShownAll) _shownAll.Add(path);
        foreach (var pair in memory.Expanded) _expanded[pair.Key] = pair.Value;

        if (memory.Values is not null)
        {
            _term = memory.Term;
            BuildInto(_root, memory.Values);
            int shown = Refill(_term);
            LblSelected.Text = shown == 0 ? Loc.Get("link_tree_no_match") : Loc.Get("link_tree_loaded_fmt", shown);
            return;
        }

        // Too big to have been kept (or nothing was loaded): ask again, then put the branches shown
        // in full back — those came from their own fetches.
        if (memory.Term.Length == 0 && !memory.ShowAll) return;
        Loaded += async (_, _) =>
        {
            LblSelected.Text = Loc.Get("link_tree_loading");
            await LoadAsync(memory.Term.Length > 0 ? memory.Term : null, keepView: true);
            foreach (var path in _shownAll.ToList()) await LoadContainerAsync(path);
        };
    }

    /// <summary>How many values this will shape into a tree at all.
    ///
    /// <para>Not a display nicety — a hard safety limit. A generic Unity scene dump measured a
    /// quarter of a million values; turning that into nodes, each with its own collection, is more
    /// than a 32-BIT process (which K2.App is) can be asked to do, and it has already taken the app
    /// down once. Past this the window says how many arrived and asks for a search, which comes back
    /// small.</para>
    ///
    /// <para>The ceiling sits well above the first guess of 25k because what actually hurt was
    /// DRAWING the tree, not holding it: the TreeView now virtualises, so only the visible rows get
    /// a container, and a scene answering with tens of thousands of values — 42k measured on a real
    /// one — is a few tens of MB of nodes that open collapsed. Refusing those was refusing the
    /// ordinary case.</para></summary>
    private const int MaxTreeValues = 120_000;

    /// <param name="keepView">True when restoring: the branches shown in full and the open rows
    /// are the ones remembered, not leftovers of a previous answer to be cleared.</param>
    private async Task LoadAsync(string? searchTerm, bool keepView = false)
    {
        var def = GameLinkStore.ById(_linkId);
        if (def is null)
        {
            LblSelected.Text = Loc.Get("link_tree_empty");
            return;
        }

        int generation = ++_generation;
        BtnSearch.IsEnabled = false;
        try
        {
            var request = searchTerm is { Length: > 0 }
                ? GameLinkReader.SearchFor(GameLinkReader.ExplicitFor(def), searchTerm)
                : GameLinkReader.ExplicitFor(def);
            var snapshot = await GameLinkReader.FetchAsync(request);
            if (generation != _generation) return;   // a newer request already answered

            _root.Children.Clear();
            Tree.ItemsSource = null;
            _term = searchTerm ?? "";
            if (!keepView)
            {
                // A new answer: what was opened or shown in full belonged to the previous one.
                _shownAll.Clear();
                _expanded.Clear();
            }

            if (!snapshot.Known)
            {
                LblSelected.Text = Loc.Get("link_tree_empty");
                return;
            }

            if (snapshot.Values.Count > MaxTreeValues)
            {
                // Deliberately build NOTHING here. Half a tree would be worse than none: it would
                // look like the answer, while the value being hunted for is as likely as not to sit
                // in the half that was dropped.
                LblSelected.Text = Loc.Get("link_tree_too_many_fmt", snapshot.Values.Count);
                return;
            }

            BuildInto(_root, snapshot.Values);

            // Filtered by the same term the game already searched for, which is not redundant: a
            // match on one field brings its whole object back, and showing only the branches that
            // match is what keeps "health" from unfolding every script the player carries. An
            // endpoint that ignored the term entirely is filtered here too, and correctly.
            int shown = Refill(_term);

            // Counted from what is ON SCREEN, never from what arrived. A no-match answer is not
            // empty: the Unity mod still sends its scene list and its diag block, so "life" finding
            // nothing came back as 12 housekeeping values — reporting THOSE said "Showing 12 values"
            // over an empty tree, which reads as the window being broken rather than as the game
            // having nothing to offer.
            string status = snapshot.Truncated ? Loc.Get("link_tree_truncated")
                          : shown == 0 ? Loc.Get("link_tree_no_match")
                          : Loc.Get("link_tree_loaded_fmt", shown);

            // Two different ceilings can bite, and until now only K2's own said so.
            if (SourceCapNote(snapshot.Values) is { } note) status = status + " " + note;
            LblSelected.Text = status;
        }
        finally
        {
            if (generation == _generation) BtnSearch.IsEnabled = true;
        }
    }

    /// <summary>What to add when the ANSWER was already cut short before it left the game.
    ///
    /// <para>The K2 Unity Link mod caps how many objects it will describe, and says so in its own
    /// <c>diag</c> block — which until now K2 showed nowhere: a dump stopping at 6000 of 25308
    /// scripted objects looked exactly like a complete one, so "my value isn't in here" had no
    /// explanation on screen. The note goes next to the count rather than replacing it, since both
    /// numbers are true.</para>
    ///
    /// <para>Read out of the payload rather than out of a mod-specific type, and only when all three
    /// keys are there and the flag really says so: any endpoint is free to answer however it likes,
    /// and a link that knows nothing about this simply never trips it.</para></summary>
    private static string? SourceCapNote(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("diag.truncated", out var flag) ||
            !flag.Equals("true", StringComparison.OrdinalIgnoreCase)) return null;
        if (!values.TryGetValue("diag.objectsListed", out var listed) ||
            !values.TryGetValue("diag.objectsWithScripts", out var total)) return null;
        return Loc.Get("link_tree_source_capped_fmt", listed, total);
    }

    private void ChkShowAll_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ChkShowAll.IsChecked == true)
        {
            LblSelected.Text = Loc.Get("link_tree_loading");
            _ = LoadAsync(null);
        }
        else
        {
            _generation++;                 // abandon a full load still on its way
            _root.Children.Clear();
            Tree.ItemsSource = null;
            _term = "";
            _shownAll.Clear();
            _expanded.Clear();
            LblSelected.Text = Loc.Get("link_tree_start");
        }
    }


    /// <summary>Searching asks the GAME to search, rather than filtering what was already
    /// downloaded — and only when asked, never as you type.
    ///
    /// <para>Both halves matter. A link answering with a whole running game sends more than the
    /// reader keeps, so the value being hunted for can be missing from the local copy no matter how
    /// it is filtered; and filtering something that size per keystroke is what made this window
    /// unusable (and crashed the app) before. An endpoint that does not understand the search term
    /// simply answers in full, and the same term is then applied to what came back.</para></summary>
    private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await RunSearch();

    private async void TxtFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunSearch();
    }

    private async Task RunSearch()
    {
        string term = TxtFilter.Text.Trim();
        LblSelected.Text = term.Length > 0
            ? Loc.Get("link_tree_searching_fmt", term)
            : Loc.Get("link_tree_loading");
        await LoadAsync(term.Length > 0 ? term : null);
    }

    /// <summary>Saves the link's whole answer as a .json file.
    ///
    /// <para>The point is what happens NEXT to that file: a generic scene dump names things the way
    /// the game's own programmers named them, so "which of these four hundred numbers is my health"
    /// is a reading-comprehension problem, not a K2 problem — and one an assistant handed the file
    /// answers in seconds. So this exports the RAW document (see
    /// <see cref="GameLinkReader.FetchRawAsync"/>), nesting intact and nothing capped: the thing to
    /// paste somewhere else, not the flattened view this window happens to draw.</para></summary>
    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var def = GameLinkStore.ById(_linkId);
        if (def is null) return;

        BtnExport.IsEnabled = false;
        LblSelected.Text = Loc.Get("link_tree_loading");
        try
        {
            var (json, error) = await GameLinkReader.FetchRawAsync(GameLinkReader.ExplicitFor(def));
            if (json is null)
            {
                LblSelected.Text = Loc.Get("link_test_fail_fmt", error ?? "");
                return;
            }

            var save = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Loc.Get("link_tree_export_filter"),
                FileName = $"{SafeName(def.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            };
            if (save.ShowDialog(this) != true) { LblSelected.Text = ""; return; }

            System.IO.File.WriteAllText(save.FileName, json);
            LblSelected.Text = Loc.Get("link_tree_exported_fmt", json.Length);
        }
        catch (Exception ex)
        {
            LblSelected.Text = ex.Message;
        }
        finally
        {
            BtnExport.IsEnabled = true;
        }
    }

    /// <summary>A link's name is free text and ends up in a FILE name here.</summary>
    private static string SafeName(string name)
    {
        var cleaned = name.Trim();
        foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(bad, '_');
        return cleaned.Length > 0 ? cleaned : "link";
    }

    /// <summary>Turns the flat <c>a.b[0].c</c> paths back into a tree, by segment: each dotted (or
    /// bracketed) segment is one level, and a segment shared by several paths — every field under
    /// the same GameObject, say — becomes one branch node they all hang off, not one per path.</summary>
    private void BuildInto(GameLinkPathNode root, IReadOnlyDictionary<string, string> values)
    {
        _byPath.Clear();
        _byPath[""] = root;
        MergeInto(root, values);
    }

    /// <summary>Adds paths to the tree already built, without dropping anything: what a branch's own
    /// fetch brings back is layered onto the answer on screen. A path already present only has its
    /// value refreshed; new children are appended after the ones a branch already had.</summary>
    private void MergeInto(GameLinkPathNode root, IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var pair in values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            string prefix = "";
            GameLinkPathNode parent = root;
            foreach (string segment in pair.Key.Split('.'))
            {
                prefix = prefix.Length == 0 ? segment : prefix + "." + segment;
                if (!_byPath.TryGetValue(prefix, out var node))
                {
                    node = new GameLinkPathNode { Name = segment, FullPath = prefix, Parent = parent };
                    _byPath[prefix] = node;
                    parent.Children.Add(node);
                }
                parent = node;
            }
            parent.Value = pair.Value; // the last segment of THIS path is where it terminates
        }
    }

    /// <summary>Every node of the UNFILTERED tree, by path. What the filtered view shows are clones,
    /// so "show me around this" needs a way back to the real node — the one that still has all its
    /// siblings attached.</summary>
    private readonly Dictionary<string, GameLinkPathNode> _byPath = new(StringComparer.Ordinal);

    /// <summary>Branches the user asked to see in full, by path. A search shows only what matched;
    /// these stay whole on top of it, and every later "show all elements" ADDS one — nothing
    /// already opened is taken away.</summary>
    private readonly HashSet<string> _shownAll = new(StringComparer.Ordinal);

    /// <summary>Open/closed rows by path, carried across a rebuild of the view (and across
    /// reopening the window). The filtered view is made of clones, so the state has to live
    /// somewhere that outlasts them.</summary>
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);

    /// <summary>Shows in full whatever holds the selected row — the row itself when it is a branch,
    /// the object it belongs to when it is a value — IN PLACE: the rest of the tree stays exactly as
    /// it was, open rows included.
    ///
    /// <para>This is where a search naturally leads: the search narrows to the branches that match,
    /// which is what makes a hit findable, but a hit is rarely the end of the question — finding
    /// <c>health</c> is how you discover there is also <c>food</c> and <c>water</c> next to it.
    /// What is already in hand is shown at once; for a Unity Link object the rest of it is then
    /// fetched (see <see cref="LoadContainerAsync"/>), since a search answers with pruned
    /// objects.</para></summary>
    private async void ShowAllElements(GameLinkPathNode? shown)
    {
        if (shown is null || !_byPath.TryGetValue(shown.FullPath, out var real)) return;
        var container = real.IsLeaf ? real.Parent : real;
        if (container is null || ReferenceEquals(container, _root)) return;

        _shownAll.Add(container.FullPath);
        SyncShown(container.FullPath);
        LblSelected.Text = container.FullPath;
        await LoadContainerAsync(container.FullPath);
    }

    /// <summary>Fetches the whole GameObject a Unity Link path lives in (<c>objects.&lt;object&gt;.…</c>)
    /// and merges it into the tree. Any other link answers a search with its full document anyway,
    /// so everything it has is already here and nothing is fetched.</summary>
    private async Task LoadContainerAsync(string path)
    {
        const string prefix = "objects.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return;
        string rest = path.Substring(prefix.Length);
        int dot = rest.IndexOf('.');
        string objectKey = dot < 0 ? rest : rest.Substring(0, dot);
        // "#2" is the mod's name for a second object on the same path — the game only knows the path.
        int hash = objectKey.LastIndexOf('#');
        string term = hash > 0 ? objectKey.Substring(0, hash) : objectKey;
        if (term.Length == 0 || GameLinkStore.ById(_linkId) is not { } def) return;

        int generation = _generation;
        LblSelected.Text = Loc.Get("link_tree_loading");
        try
        {
            var snapshot = await GameLinkReader.FetchAsync(
                GameLinkReader.SearchFor(GameLinkReader.ExplicitFor(def), term));
            if (generation != _generation) return;   // the tree was replaced meanwhile

            // Only that object: a path search also matches every child object below it.
            string own = prefix + objectKey + ".";
            var extra = snapshot.Known
                ? snapshot.Values.Where(v => v.Key.StartsWith(own, StringComparison.Ordinal)).ToList()
                : new List<KeyValuePair<string, string>>();
            if (extra.Count > 0 && _byPath.Count + extra.Count <= MaxTreeValues)
            {
                MergeInto(_root, extra);
                foreach (var shownPath in _shownAll.ToList()) SyncShown(shownPath);
            }
        }
        catch
        {
            // Nothing more to show than what was already there.
        }
        if (generation == _generation) LblSelected.Text = path;
    }

    /// <summary>Brings the row for <paramref name="path"/> on screen up to the full set of its
    /// children, inserting the missing ones where they belong and leaving the ones already shown
    /// (and their open/closed state) alone.</summary>
    private void SyncShown(string path)
    {
        if (!_byPath.TryGetValue(path, out var real)) return;

        var visible = FindVisible(path);
        if (visible is null)
        {
            // Not on screen at all (a remembered branch the new answer does not reach): redraw,
            // keeping every row as it is.
            CaptureExpanded();
            Refill(_term);
            return;
        }

        if (!ReferenceEquals(visible, real))
        {
            int at = 0;
            foreach (var child in real.Children)
            {
                int existing = -1;
                for (int i = at; i < visible.Children.Count; i++)
                    if (visible.Children[i].FullPath == child.FullPath) { existing = i; break; }

                if (existing < 0)
                    visible.Children.Insert(at, _term.Length > 0 ? FilterNode(child, _term) ?? child : child);
                else if (existing != at)
                    visible.Children.Move(existing, at);
                at++;
            }
        }
        visible.IsExpanded = true;
        _expanded[path] = true;
    }

    /// <summary>The row on screen standing for <paramref name="path"/>, found by walking down the
    /// branches whose path leads to it.</summary>
    private GameLinkPathNode? FindVisible(string path)
    {
        var level = Tree.ItemsSource as IEnumerable<GameLinkPathNode>;
        while (level is not null)
        {
            GameLinkPathNode? next = null;
            foreach (var node in level)
            {
                if (node.FullPath == path) return node;
                if (path.StartsWith(node.FullPath + ".", StringComparison.Ordinal)) { next = node; break; }
            }
            level = next?.Children;
        }
        return null;
    }

    /// <summary>Writes the open/closed state of what is on screen into <see cref="_expanded"/>.
    /// Only walks into open rows: what sits under a closed one is not visible either way.</summary>
    private void CaptureExpanded()
    {
        if (Tree.ItemsSource is not IEnumerable<GameLinkPathNode> roots) return;
        var stack = new Stack<GameLinkPathNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Children.Count == 0) continue;
            _expanded[node.FullPath] = node.IsExpanded;
            if (node.IsExpanded) foreach (var child in node.Children) stack.Push(child);
        }
    }

    /// <summary>Opens or closes everything currently on screen.
    ///
    /// <para>Expanding is capped, and says so rather than trying: a TreeView realises a container per
    /// visible row, so opening a tree of tens of thousands of nodes at once is a freeze at best in a
    /// 32-bit process. Collapsing has no such cost and is never refused.</para></summary>
    private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
    {
        var roots = (Tree.ItemsSource as IEnumerable<GameLinkPathNode>)?.ToList();
        if (roots is null) return;

        int count = roots.Sum(CountNodes);
        if (count > MaxExpandNodes)
        {
            LblSelected.Text = Loc.Get("link_tree_expand_too_many_fmt", count);
            return;
        }

        foreach (var node in roots) SetExpanded(node, true);
    }

    private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.ItemsSource is not IEnumerable<GameLinkPathNode> roots) return;
        foreach (var node in roots.ToList()) SetExpanded(node, false);
    }

    private const int MaxExpandNodes = 3000;

    private static int CountNodes(GameLinkPathNode node) => 1 + node.Children.Sum(CountNodes);

    private static void SetExpanded(GameLinkPathNode node, bool expanded)
    {
        if (node.Children.Count > 0) node.IsExpanded = expanded;
        foreach (var child in node.Children) SetExpanded(child, expanded);
    }

    private void MnuShowAll_Click(object sender, RoutedEventArgs e) =>
        ShowAllElements(Tree.SelectedItem as GameLinkPathNode);

    /// <summary>WPF does not select a tree item on right-click, so a context menu would otherwise act
    /// on whatever was selected BEFORE — i.e. not the row being pointed at.</summary>
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Tree, (DependencyObject)e.OriginalSource) is TreeViewItem item)
            item.IsSelected = true;
    }

    /// <summary>Puts the (optionally filtered) tree on screen and answers with how many actual
    /// VALUES ended up visible — the only number worth telling the user about.</summary>
    private int Refill(string filter)
    {
        // The real nodes carry their own state; the clones below read it from the same map.
        foreach (var pair in _expanded)
            if (_byPath.TryGetValue(pair.Key, out var node)) node.IsExpanded = pair.Value;

        // Observable, so "show all elements" can add rows to a top-level branch in place.
        var items = new ObservableCollection<GameLinkPathNode>(filter.Length == 0
            ? _root.Children
            : _root.Children.Select(c => FilterNode(c, filter)).OfType<GameLinkPathNode>());
        Tree.ItemsSource = items;
        return items.Sum(CountLeaves);
    }

    /// <summary>Leaves only: branches are the shape the paths were given, not things the game holds a
    /// value for, and counting them would inflate every number this window reports.</summary>
    private static int CountLeaves(GameLinkPathNode node) =>
        (node.IsLeaf ? 1 : 0) + node.Children.Sum(CountLeaves);

    /// <summary>A node survives filtering if IT matches, or anything under it does — an ancestor of
    /// a match has to stay visible for the match to be reachable at all, even if the ancestor's own
    /// name says nothing about the search term. Matched branches come back pre-expanded; the rest
    /// starts collapsed so a hit doesn't get buried under everything else that happened to share an
    /// early path segment. A branch in <see cref="_shownAll"/> keeps ALL its children, the matching
    /// ones still filtered so they open onto their hit.</summary>
    private GameLinkPathNode? FilterNode(GameLinkPathNode node, string filter)
    {
        bool all = _shownAll.Contains(node.FullPath);
        var children = new List<GameLinkPathNode>();
        bool anyKept = false;
        foreach (var child in node.Children)
        {
            var filtered = FilterNode(child, filter);
            if (filtered is not null) { children.Add(filtered); anyKept = true; }
            else if (all) children.Add(child);
        }

        bool selfMatches = node.FullPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (node.Value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        if (!selfMatches && !anyKept && !all) return null;

        var clone = new GameLinkPathNode
        {
            Name = node.Name, FullPath = node.FullPath, Value = node.Value,
            IsExpanded = _expanded.TryGetValue(node.FullPath, out bool open) ? open : anyKept || all,
        };
        foreach (var child in children) clone.Children.Add(child);
        return clone;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var node = e.NewValue as GameLinkPathNode;
        BtnOk.IsEnabled = node?.IsLeaf == true;
        // Branches show their path too rather than blanking the line: OK stays disabled for them, so
        // there is nothing to mislead, and a blank line here used to wipe the load/refusal message
        // the moment anything was clicked.
        if (node is not null) LblSelected.Text = node.FullPath;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((Tree.SelectedItem as GameLinkPathNode)?.IsLeaf == true) Commit();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (Tree.SelectedItem is not GameLinkPathNode { IsLeaf: true } node) return;
        SelectedPath = node.FullPath;
        DialogResult = true;
    }
}
