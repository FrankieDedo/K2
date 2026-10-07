using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>A game profile's own icon, shown instead of the emoji glyph. Empty for the
        /// ordinary categories, which have no picture of their own.</summary>
        public string IconImageUri { get; init; } = "";

        public Visibility ImageVisibility => IconImageUri.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GlyphVisibility => IconImageUri.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The game behind a game-profile key: its catalogue id, the name to print on the
    /// top-level card, the PNG of the game's icon (null when the game isn't installed, in which
    /// case the card keeps the generic gamepad glyph). Everything else about the game — its art,
    /// accent and command families — is looked up from <see cref="GameProfileSpecs"/> by the id,
    /// so a new game is one entry there rather than more fields here.</summary>
    public sealed record GamePickerProfile(string Id, string Name, string? IconPath);

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
        // Backlight of any connected device. Both tags are dropped from CbType on a host with
        // no IActionHost.Lighting, which hides the card.
        ("lighting",   "💡", LightingActionTypes.All),
        ("live",       "📊", new[] { "dp_clock", "dp_sysmon", "dp_speedtest", "dp_screen", "dp_custom" }),
        // The Makalu's own firmware functions. Every tag is dropped from CbType on any other
        // host, which empties the category and hides its card — see ShowCategoryPicker.
        ("makalu",     "🖱", MakaluActionTypes.All),
        ("apps",       "🧩", new[] { "googlehome", "adobe", "davinci", "zoom", "obs", "twitch", "spotify", "discord", "youtube", "pyscript", ModLinkGames.StudioActionType }),
        // Game-specific commands — kept out of the generic categories above so a game's own
        // vocabulary (cockpit annunciators, in-game shortcuts) doesn't crowd the everyday
        // action list. dp_edstatus is the real "dynamic tile" action (its own sub-grid of
        // cockpit toggles); the dp_ed_* entries are NOT real action types — see
        // EdShortcutPresets — they are the "non-dynamic" plain shortcuts, one card each so a
        // commander picks "Frame Shift Drive" instead of having to know it's bound to J.
        ("games",      "🎮", new[] { "dp_edstatus", "dp_ed_fsd", "dp_ed_heatsink", "dp_ed_target",
                                     "dp_zcstatus", KspTelemachus.ActionType, ModLinkGames.ActionType }),
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
        ["dp_clock"] = "🕐", ["dp_sysmon"] = "📊", ["dp_speedtest"] = "🚀", ["dp_screen"] = "🔍",
        ["dp_custom"] = "🎨",
        [LightingActionTypes.Brightness] = "🔆", [LightingActionTypes.Effect] = "🌈",
        ["dp_edstatus"] = "🛸",
        ["dp_zcstatus"] = "🎖",
        [KspTelemachus.ActionType] = "🚀",
        [ModLinkGames.ActionType] = "🧩",
        [ModLinkGames.StudioActionType] = "🎛",
        ["dp_ed_fsd"] = "🌌", ["dp_ed_heatsink"] = "❄", ["dp_ed_target"] = "🎯",
        [MakaluActionTypes.Mouse] = "🖱", [MakaluActionTypes.Dpi] = "🎚", [MakaluActionTypes.Scroll] = "↕",
        [MakaluActionTypes.Sniper] = "🎯", [MakaluActionTypes.Profile] = "👤",
        [MakaluActionTypes.Lighting] = "💡", [MakaluActionTypes.Disable] = "🚫",
    };

    /// <summary>Elite Dangerous "quick command" presets: cards in the Games category that are
    /// NOT real CbType tags — picking one just fills in the ordinary "keys" action with the
    /// game's own default bind (see <see cref="ButtonActionDialog.Keys"/>'s LoadKeysSpec), so a
    /// commander gets a named card instead of having to know FSD is bound to "J". See
    /// <see cref="ActionCard_Click"/>.</summary>
    private static readonly Dictionary<string, string> EdShortcutPresets = new()
    {
        ["dp_ed_fsd"]      = "J",
        ["dp_ed_heatsink"] = "V",
        ["dp_ed_target"]   = "T",
    };

    /// <summary>Types whose value is picked from a fixed/dynamic list (CbComboValue) rather
    /// than typed free-hand — these get the 3rd breadcrumb crumb + sub-action grid. Matches
    /// the "combo" set UpdatePanels() already switches ComboPanel on for.</summary>
    private static readonly HashSet<string> ComboTags = new()
        { "oscmd", "media", "mouse", "macro", "googlehome", "obs", "twitch", "spotify", "discord", "audiodevice",
          "dp_clock", "dp_sysmon", "dp_speedtest", "dp_edstatus", "dp_zcstatus", KspTelemachus.ActionType, ModLinkGames.ActionType, ModLinkGames.StudioActionType, "dp_screen",
          CustomActionType.Tag,
          MakaluActionTypes.Mouse, MakaluActionTypes.Dpi, MakaluActionTypes.Scroll,
          MakaluActionTypes.Profile, MakaluActionTypes.Lighting };

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

    /// <summary>Categories that only make sense inside a GAME PROFILE's key configuration and are
    /// hidden everywhere else: their actions are meaningless on an ordinary profile (an Elite
    /// cockpit annunciator on a general-purpose page has no ship to mirror).</summary>
    private static readonly string[] GameOnlyCategories = { "games" };

    /// <summary>Tags that keep their place in <see cref="PickerCategories"/> — the breadcrumb
    /// takes an action's category from there — but are never offered as a CARD of their own.
    ///
    /// <para>A studio action belongs to the game it was written for and is picked under THAT
    /// game's card, filed in its family. Listing it a second time under "Live tiles" showed the
    /// same action twice in a game profile and, on an ordinary profile, opened a grid holding
    /// every game's actions at once — or, with no studio action defined, nothing at all
    /// (user report 2026-09-18).</para></summary>
    private static readonly HashSet<string> CardlessTags = new() { CustomActionType.Tag };

    /// <summary>The game a game-profile key belongs to. When set, the picker's generic "Game
    /// controls" category is replaced by one card carrying the GAME's own name and icon, and its
    /// second level lists the game's command FAMILIES (cockpit, gauges, flight, ...) instead of
    /// K2 action types — a pilot picks "Landing gear" out of "Cockpit toggles", never having to
    /// know it is delivered as a "dp_edstatus" tile. Null everywhere else, which keeps the
    /// ordinary category/action/sub-action tree exactly as it was.</summary>
    private GamePickerProfile? _gameProfile;

    /// <summary>Prefix marking a level-2 card as a command FAMILY rather than an action type, and
    /// a level-3 card as one of that family's commands. Both reuse the ordinary grids; the prefix
    /// is what tells the click handlers which world they are in.</summary>
    private const string GameFamilyPrefix = "gamefam:";
    private const string GameItemPrefix = "gameitem:";

    /// <summary>Level-3 cards for the family currently open, keyed by the token in their Tag.
    /// Rebuilt on every family open — a token only has to survive until its card is clicked.</summary>
    private readonly Dictionary<string, ActionTypeHelper.GameCommand> _gameItems = new();

    /// <summary>The family whose commands are on screen, so Back goes up one level instead of
    /// jumping to the categories.</summary>
    private string? _gameFamilyOpen;

    /// <summary>Set by the caller to restrict the picker to a few categories — a game profile
    /// offers Input and Game controls and nothing else. Null means "the ordinary set", which is
    /// everything EXCEPT <see cref="GameOnlyCategories"/>.</summary>
    private string[]? _allowedCategories;

    private bool CategoryAllowed(string key) =>
        _allowedCategories is { Length: > 0 }
            ? _allowedCategories.Contains(key)
            : !GameOnlyCategories.Contains(key);

    /// <summary>The display name of the Game-controls preset a plain "keys" action came from
    /// ("Frame Shift Drive" rather than the bare "J"), or null when the action is not one of them.
    /// A preset stores itself as an ordinary keystroke — see <see cref="EdShortcutPresets"/> — so
    /// its identity is only recoverable from the bind, which is what this does.</summary>
    public static string? GameShortcutName(string? actionType, string? actionValue)
    {
        if (actionType != "keys" || string.IsNullOrWhiteSpace(actionValue)) return null;
        foreach (var (tag, keys) in EdShortcutPresets)
            if (string.Equals(keys, actionValue!.Trim(), System.StringComparison.OrdinalIgnoreCase))
                return Loc.Get(LocKeyForTag(tag));
        return null;
    }

    /// <summary>Whether an action type belongs to one of <paramref name="categories"/> — the same
    /// grouping the picker shows. Lets a caller that restricted the picker (the game profile
    /// editor) apply the SAME rule to a paste, which arrives from the app-wide clipboard and
    /// never went through the picker at all.</summary>
    public static bool IsTypeInCategories(string? actionType, string[] categories) =>
        actionType is not null &&
        PickerCategories.Any(c => categories.Contains(c.Key) && c.Tags.Contains(actionType));

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
        // A game command's crumb 2 is its FAMILY, so it must reopen the family grid — the action
        // type it happens to use ("keys" for Frame Shift Drive) would land in Input instead.
        if (_gameProfile is { } gp && CurrentGameCommand(gp) is not null)
            ShowActionPicker("games");
        else
            ShowActionPicker(CategoryKeyOf(CurrentTag()));
        OpenOverlay();
    }

    private void BtnCrumbSubAction_Click(object sender, RoutedEventArgs e)
    {
        if (_gameProfile is { } gp && CurrentGameCommand(gp) is { } hit)
            ShowGameCommandPicker(hit.Family.LocKey);
        else
            ShowSubActionPicker();
        OpenOverlay();
    }

    /// <summary>The "saved in the mouse's memory" line under the picker title — on for the
    /// Makalu category's own grids, off everywhere else.</summary>
    private void SetPickerHint(string? categoryKey) =>
        LblPickerHint.Visibility = categoryKey == "makalu" ? Visibility.Visible : Visibility.Collapsed;

    private void ShowCategoryPicker()
    {
        SetPickerHint(null);
        // Only categories with at least one action still present in CbType.Items —
        // an allow-list (macro-step picker) or a page-less host can empty a whole
        // category, and an empty card that opens onto a blank grid is just a dead end.
        var availableTags = CbType.Items.OfType<ComboBoxItem>()
            .Select(i => (string?)i.Tag).ToHashSet();
        IcPickerCategories.ItemsSource = PickerCategories
            .Where(c => CategoryAllowed(c.Key))
            .Where(c => c.Tags.Any(t => !CardlessTags.Contains(t) &&
                                        (availableTags.Contains(t) || EdShortcutPresets.ContainsKey(t))))
            .Select(c => c.Key == "games" && _gameProfile is { } gp
                // The game's own identity, not a generic label: the card says "Elite Dangerous"
                // and wears the profile's icon, so the two top-level choices read as "keyboard
                // things" and "this game's things".
                ? new CategoryCard { Key = c.Key, Glyph = c.Glyph, Name = gp.Name, IconImageUri = gp.IconPath ?? "" }
                : new CategoryCard { Key = c.Key, Glyph = c.Glyph, Name = Loc.Get("cat_" + c.Key) })
            .ToList();

        LblPickerTitle.Text = Loc.Get("picker_pick_category");
        BtnPickerBack.Visibility = Visibility.Collapsed;
        IcPickerCategories.Visibility = Visibility.Visible;
        IcPickerActions.Visibility = Visibility.Collapsed;
        IcPickerSubActions.Visibility = Visibility.Collapsed;
    }

    /// <summary>Category whose action grid is currently shown — used by the Guide
    /// button to open the right "picker:cat:*" page while at level 2.</summary>
    private string _pickerCategoryKey = "system";

    private void ShowActionPicker(string categoryKey)
    {
        var category = PickerCategories.FirstOrDefault(c => c.Key == categoryKey);
        if (category.Tags is null) return;
        _pickerCategoryKey = categoryKey;
        SetPickerHint(categoryKey);

        if (categoryKey == "games" && _gameProfile is { } gp)
        {
            ShowGameFamilyPicker(gp);
            return;
        }

        var availableTags = CbType.Items.OfType<ComboBoxItem>().Select(i => (string?)i.Tag).ToHashSet();
        IcPickerActions.ItemsSource = category.Tags
            .Where(t => !CardlessTags.Contains(t))
            .Where(t => availableTags.Contains(t) || EdShortcutPresets.ContainsKey(t))
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

    /// <summary>Level 2 for a game profile: one card per command family. Replaces the
    /// action-type grid entirely — the types those commands use (dp_edstatus, keys) are an
    /// implementation detail the pilot never sees.</summary>
    private void ShowGameFamilyPicker(GamePickerProfile gp)
    {
        IcPickerActions.ItemsSource = FamiliesFor(gp)
            .Select(f => new ActionCard
            {
                Tag = GameFamilyPrefix + f.LocKey,
                Name = Loc.Get(f.LocKey),
                Glyph = f.Glyph,
            })
            .ToList();

        _gameFamilyOpen = null;
        LblPickerTitle.Text = gp.Name;
        BtnPickerBack.Visibility = Visibility.Visible;
        IcPickerCategories.Visibility = Visibility.Collapsed;
        IcPickerActions.Visibility = Visibility.Visible;
        IcPickerSubActions.Visibility = Visibility.Collapsed;
    }

    /// <summary>Level 3 for a game profile: the commands of one family, live status tiles and
    /// plain shortcuts side by side.</summary>
    private void ShowGameCommandPicker(string familyLocKey)
    {
        if (_gameProfile is not { } gp) return;

        var family = FamiliesFor(gp).FirstOrDefault(f => f.LocKey == familyLocKey);
        if (family.Items is null) return;

        _gameItems.Clear();
        var cards = new List<SubActionCard>();
        foreach (var cmd in family.Items)
        {
            string token = GameItemPrefix + _gameItems.Count.ToString(CultureInfo.InvariantCulture);
            _gameItems[token] = cmd;
            cards.Add(new SubActionCard { Value = token, Name = Loc.Get(cmd.LocKey) });
        }
        IcPickerSubActions.ItemsSource = cards;

        _gameFamilyOpen = familyLocKey;
        LblPickerTitle.Text = Loc.Get(familyLocKey);
        BtnPickerBack.Visibility = Visibility.Visible;
        IcPickerCategories.Visibility = Visibility.Collapsed;
        IcPickerActions.Visibility = Visibility.Collapsed;
        IcPickerSubActions.Visibility = Visibility.Visible;
    }

    /// <summary>Which family list a profile uses — its <see cref="GameProfileSpec"/>'s, or none.
    /// A game with no spec contributes no cards, and its category is then not offered at all.</summary>
    private static (string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items)[] FamiliesFor(
        GamePickerProfile gp) => GameProfileSpecs.FamiliesFor(gp.Id);

    /// <summary>Applies one picked game command: the action type plus its value, through the same
    /// code paths the ordinary picker uses, so a "keys" command lands in the Keys panel and a
    /// "dp_edstatus" one selects its card in CbComboValue.</summary>
    private void ApplyGameCommand(ActionTypeHelper.GameCommand cmd)
    {
        SetType(cmd.ActionType);

        if (cmd.ActionType == "keys")
        {
            LoadKeysSpec(cmd.ActionValue);
        }
        else
        {
            // LoadComboSpec, not a hand-made match against CbComboValue: setting the type only
            // repopulates the combo when the SELECTION actually changed, so picking a second
            // command of the same type left the old (or default) item selected — every pick came
            // out as "Landing gear", the first entry the live tile falls back to when the value
            // is empty.
            LoadComboSpec(cmd.ActionType, cmd.ActionValue);
            UpdateSubActionCrumb();
        }

        CloseOverlay();
    }

    /// <summary>Built straight from CbComboValue.Items (already populated by
    /// EnsureComboPanel/PopulateCombo for the current tag, including the dynamic macro/
    /// googlehome/audiodevice lists) rather than re-deriving them — one source of truth,
    /// no risk of the two lists drifting apart.</summary>
    private void ShowSubActionPicker()
    {
        _gameFamilyOpen = null;
        SetPickerHint(CategoryKeyOf(CurrentTag()));
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

        if (tag.StartsWith(GameFamilyPrefix, System.StringComparison.Ordinal))
        {
            ShowGameCommandPicker(tag[GameFamilyPrefix.Length..]);
            return;
        }

        // Elite Dangerous quick-command preset: not a real action type, just a named shortcut
        // to the ordinary "keys" action with the game's own default bind already filled in.
        if (EdShortcutPresets.TryGetValue(tag, out var keys))
        {
            SetType("keys");
            LoadKeysSpec(keys);
            CloseOverlay();
            return;
        }

        SetType(tag);

        if (ComboTags.Contains(tag)) ShowSubActionPicker();
        else CloseOverlay();
    }

    private void SubActionCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;

        if (value.StartsWith(GameItemPrefix, System.StringComparison.Ordinal))
        {
            if (_gameItems.TryGetValue(value, out var cmd)) ApplyGameCommand(cmd);
            return;
        }

        // "PC monitor" > "Sensor selection": open the hardware-sensor picker instead of just
        // selecting a card. It re-selects the card itself on a successful pick, or leaves the
        // current selection untouched on cancel.
        if (value == SensorPickTag)
        {
            CloseOverlay();
            OpenSensorPickerCard();
            return;
        }

        // "Screen reading" > "New probe…": same shape as the sensor card — the calibration window
        // is the picker for this type, so the card opens it instead of selecting a value.
        if (value == ScreenProbeNewTag)
        {
            CloseOverlay();
            OpenScreenProbeEditor(null);
            return;
        }

        // "Custom action" > "New action…": the studio is the picker for this type.
        if (value == CustomActionNewTag)
        {
            CloseOverlay();
            OpenCustomActionEditor(null);
            return;
        }

        var match = CbComboValue.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, value, System.StringComparison.Ordinal));
        if (match is not null) CbComboValue.SelectedItem = match;
        CloseOverlay();
    }

    private void BtnPickerBack_Click(object sender, RoutedEventArgs e)
    {
        // Back from a level-3 grid returns to the level 2 it was opened from — tracked
        // explicitly, since a game command's action type says nothing about its family.
        if (IcPickerSubActions.Visibility == Visibility.Visible)
            ShowActionPicker(_gameFamilyOpen is not null ? "games" : CategoryKeyOf(CurrentTag()));
        else
            ShowCategoryPicker();
    }

    private void BtnPickerClose_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    /// <summary>"Guide" button (bottom bar, always visible): opens GuideWindow at
    /// the page matching the current picker level — categories overview (L1), one
    /// category's actions (L2), or one action's sub-actions (L3). When the picker
    /// is closed (an action is being configured) it explains that action, plus
    /// its sub-action options for the "combo" types.</summary>
    private void BtnGuide_Click(object sender, RoutedEventArgs e)
    {
        string[] keys;
        string heading;

        if (PickerPanel.Visibility == Visibility.Visible &&
            IcPickerCategories.Visibility == Visibility.Visible)
        {
            keys = new[] { "picker:categories" };
            heading = Loc.Get("picker_pick_category");
        }
        else if (PickerPanel.Visibility == Visibility.Visible &&
                 IcPickerActions.Visibility == Visibility.Visible)
        {
            keys = new[] { "picker:cat:" + _pickerCategoryKey };
            heading = Loc.Get("cat_" + _pickerCategoryKey);
        }
        else
        {
            string tag = CurrentTag();
            if (tag == "none")
            {
                keys = new[] { "picker:categories" };
                heading = Loc.Get("picker_pick_category");
            }
            else if (PickerPanel.Visibility == Visibility.Visible &&
                     IcPickerSubActions.Visibility == Visibility.Visible)
            {
                // Level 3: the sub-action grid (combo types only).
                keys = new[] { "picker:sub:" + tag };
                heading = Loc.Get(LocKeyForTag(tag));
            }
            else
            {
                // Config panel for a chosen action — for combo types add the
                // sub-action reference after the action overview.
                keys = ComboTags.Contains(tag)
                    ? new[] { "picker:act:" + tag, "picker:sub:" + tag }
                    : new[] { "picker:act:" + tag };
                heading = Loc.Get(LocKeyForTag(tag));
            }
        }

        new GuideWindow(keys, heading) { Owner = this }.ShowDialog();
    }

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
        TxtCrumbCategoryName.Text = categoryKey == "games" && _gameProfile is { } gpc
            ? gpc.Name
            : Loc.Get("cat_" + categoryKey);

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

        ApplyGameCrumbs();

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
        ApplyGameCrumbs();
    }

    /// <summary>Rewrites the breadcrumb in the GAME's vocabulary once the chosen action turns out
    /// to be one of its catalogued commands: "Elite Dangerous > Cockpit toggles > Landing gear"
    /// rather than "Game controls > Elite Dangerous status > Landing gear". The crumbs are the
    /// only place the old action-type wording survived after the picker itself was reorganised
    /// into families, and having the two disagree is what made the picker look unchanged.
    ///
    /// <para>Derived from the action type + value rather than remembered from the click, so a key
    /// configured in an earlier session shows the same trail when the dialog is reopened.</para></summary>
    private void ApplyGameCrumbs()
    {
        if (_gameProfile is not { } gp) return;
        if (CurrentGameCommand(gp) is not { } hit) return;

        TxtCrumbCategoryName.Text = gp.Name;

        // The family replaces the action type in crumb 2 — always its emoji, never the action's
        // logo, so the vector/bitmap slots have to be cleared.
        TxtCrumbActionName.Text = Loc.Get(hit.Family.LocKey);
        TxtCrumbActionGlyph.Text = hit.Family.Glyph;
        TxtCrumbActionGlyph.Visibility = Visibility.Visible;
        PathCrumbActionIcon.Data = null;
        PathCrumbActionIcon.Visibility = Visibility.Collapsed;
        ImgCrumbActionIcon.Source = null;
        ImgCrumbActionIcon.Visibility = Visibility.Collapsed;

        // Crumb 3 is the command itself — shown even for a "keys" command, which in the ordinary
        // flow has no third level at all.
        TxtCrumbChevron2.Visibility = Visibility.Visible;
        BtnCrumbSubAction.Visibility = Visibility.Visible;
        TxtCrumbSubActionName.Text = Loc.Get(hit.Command.LocKey);
    }

    /// <summary>The catalogued command the dialog is currently showing, or null when the key is
    /// bound to something outside the game's catalogue (a user's own shortcut, say) — which then
    /// keeps the ordinary breadcrumb.</summary>
    private (( string LocKey, string Glyph, ActionTypeHelper.GameCommand[] Items) Family,
             ActionTypeHelper.GameCommand Command)? CurrentGameCommand(GamePickerProfile gp)
    {
        string tag = CurrentTag();
        string value = tag == "keys"
            ? SaveKeysSpec()
            : CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";

        // A status tile may carry its optional keystroke after a bar; the catalogue keys on the
        // state alone.
        if (tag == "dp_edstatus") value = ActionTypeHelper.SplitEdStatusValue(value).State;
        if (value.Length == 0) return null;

        foreach (var family in FamiliesFor(gp))
            foreach (var cmd in family.Items)
                if (cmd.ActionType == tag &&
                    string.Equals(cmd.ActionValue, value, System.StringComparison.OrdinalIgnoreCase))
                    return (family, cmd);

        return null;
    }
}
