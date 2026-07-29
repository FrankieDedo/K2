namespace K2.Core;

/// <summary>
/// Real default keyboard shortcuts backing the "adobe"/"davinci"/"zoom" action pickers
/// (<c>ButtonActionDialog.AppShortcut.cs</c>). Action names sourced from Base Camp's own
/// shipped icon filenames (<c>Mountain Base Camp/resources/bin/wwwroot/images/{illustrator,
/// photoshop,premiere,davinci}-profile/</c>); shortcuts verified against each vendor's own
/// published default-keyboard-shortcut reference (Adobe helpx.com pages/PDF for Illustrator/
/// Photoshop/Premiere Pro, Blackmagic Design's DaVinci Resolve keyboard shortcuts reference).
///
/// Every entry works exactly like <see cref="ZoomShortcuts"/> already did: picking one
/// autofills the shortcut editor below (still overridable, "Custom" skips it). A handful of
/// actions have NO fixed default in the vendor's own stock install (only reachable via a
/// mouse modifier, a hold-behavior rather than a togglable shortcut, or genuinely unassigned
/// out of the box) — those carry an empty shortcut so picking them leaves the editor
/// untouched, same as "Custom".
/// </summary>
public static class AppShortcutCatalog
{
    public static readonly string[] AdobeApps = { "Illustrator", "Photoshop", "Premiere Pro" };

    /// <summary>All 13 shortcuts here are Adobe Illustrator (Windows) primary-source
    /// verified: https://helpx.adobe.com/illustrator/using/default-keyboard-shortcuts.html</summary>
    public static readonly (string Shortcut, string Label)[] IllustratorActions =
    {
        ("Ctrl + Z",               "Undo"),
        ("Ctrl + Shift + Z",       "Redo"),
        ("Ctrl + X",               "Cut"),
        ("Ctrl + C",               "Copy"),
        ("Ctrl + V",               "Paste"),
        ("Ctrl + F",               "Paste in front"),
        ("Ctrl + B",               "Paste at back"),
        ("Ctrl + Shift + V",       "Paste in place"),
        ("Ctrl + Shift + Alt + V", "Paste on all"),
        ("Ctrl + I",               "Check spelling"),
        ("Ctrl + Shift + K",       "Color Settings"),
        ("Ctrl + Shift + Alt + K", "Keyboard Shortcuts"),
        ("Ctrl + K",               "Preferences"),
    };

    /// <summary>Photoshop (Windows). Free Transform/New layer copy/New layer cut confirmed
    /// against Adobe's official shortcut PDF; brush-tip rotation and Shift+Tab field
    /// navigation are real documented Photoshop behavior but Adobe's own shortcut table was
    /// unreachable to double-check at research time (retired page, no accessible mirror) —
    /// corroborated instead by multiple independent tutorials. "Close open documents except
    /// selected" (right-click-tab-only command), "Delete brush swatch" (Alt+click on the
    /// brush thumbnail — a mouse modifier, not a keystroke) and "Move Tool Auto-select
    /// ON/OFF" (Ctrl-held-down override, not an assignable toggle) have no real keyboard
    /// default, hence the empty shortcut.</summary>
    public static readonly (string Shortcut, string Label)[] PhotoshopActions =
    {
        ("Ctrl + T",         "Free Transform"),
        ("Shift + Right",    "Rotate brush tip +15°"),
        ("Shift + Left",     "Rotate brush tip -15°"),
        ("Ctrl + J",         "New layer copy"),
        ("Ctrl + Shift + J", "New layer cut"),
        ("",                 "Close open documents except selected"),
        ("Shift + Tab",      "Navigate fields backwards"),
        ("",                 "Delete brush swatch"),
        ("",                 "Move Tool Auto-select ON/OFF"),
    };

    /// <summary>Premiere Pro (Windows), primary-source verified against Adobe's official
    /// default-keyboard-shortcuts page. Five actions ship with no default binding in stock
    /// Premiere Pro — "Title" (Legacy Titler shortcut was dropped from the default set),
    /// "Browse in Adobe Bridge" (removed from the File menu in CC 2018+), "Paste Insert"
    /// (command exists, Adobe's own shortcut table lists no binding for it), "Track Select
    /// Backward" (a Tools-panel tool, outside Adobe's documented menu-shortcut table) and
    /// "Selection Properties" (no command by that exact name in Adobe's list) — all carry an
    /// empty shortcut rather than a guess.</summary>
    public static readonly (string Shortcut, string Label)[] PremiereActions =
    {
        ("Ctrl + Alt + N",   "New Project"),
        ("Ctrl + N",         "New Sequence"),
        ("Ctrl + B",         "New Bin"),
        ("",                 "Title"),
        ("Ctrl + O",         "Open Project"),
        ("",                 "Browse in Adobe Bridge"),
        ("Ctrl + Shift + W", "Close Project"),
        ("Ctrl + W",         "Close"),
        ("Ctrl + S",         "Save"),
        ("Ctrl + Shift + S", "Save As"),
        ("Ctrl + Alt + S",   "Save a Copy"),
        ("Ctrl + Z",         "Undo"),
        ("Ctrl + Shift + Z", "Redo"),
        ("Ctrl + X",         "Cut"),
        ("Ctrl + C",         "Copy"),
        ("Ctrl + V",         "Paste"),
        ("",                 "Paste Insert"),
        ("Ctrl + Alt + V",   "Paste Attributes"),
        ("Ctrl + Shift + /", "Duplicate"),
        ("Ctrl + Shift + X", "Clear In & Out"),
        ("Shift + I",        "Go to In Point"),
        ("Shift + O",        "Go to Out Point"),
        ("Ctrl + F",         "Find"),
        ("Ctrl + Shift + A", "Deselect All"),
        ("Ctrl + A",         "Select All"),
        ("Ctrl + E",         "Edit Original"),
        ("Ctrl + I",         "Import Media"),
        ("Ctrl + Alt + I",   "Import from Media Browser"),
        ("Ctrl + M",         "Export Media"),
        ("Shift + Delete",   "Ripple Delete"),
        ("",                 "Track Select Backward"),
        ("",                 "Selection Properties"),
        ("Ctrl + R",         "Speed/Duration"),
        ("Ctrl + Q",         "Exit Program"),
    };

    /// <summary>DaVinci Resolve (Windows) Edit page shortcuts, verified against Blackmagic
    /// Design's official keyboard-shortcuts reference and cross-checked against independent
    /// shortcut databases. "Reset Retime" and "Select Clips Backward" ship unassigned out of
    /// the box (must be mapped manually in Keyboard Customization), hence the empty
    /// shortcut.</summary>
    public static readonly (string Shortcut, string Label)[] DaVinciActions =
    {
        ("Ctrl + Z",         "Undo"),
        ("Ctrl + Shift + Z", "Redo"),
        ("Ctrl + C",         "Copy"),
        ("Ctrl + V",         "Paste"),
        ("Ctrl + M",         "Add & Modify Marker"),
        ("Ctrl + [",         "Add Keyframe"),
        ("Ctrl + ]",         "Add Static Keyframe"),
        ("Ctrl + T",         "Add Transition"),
        ("Shift + F12",      "Append At End"),
        ("Alt + I",          "Clear In"),
        ("Alt + O",          "Clear Out"),
        ("Ctrl + Alt + L",   "Clip Link"),
        ("Alt + ]",          "Delete Keyframe"),
        ("Alt + M",          "Delete Marker"),
        ("Delete",           "Delete with Ripple"),
        ("Ctrl + Shift + A", "Deselect All"),
        ("Ctrl + E",         "Export Project"),
        ("Shift + L",        "Fast Forward"),
        ("Shift + J",        "Fast Reverse"),
        ("Shift + F11",      "Fit To Fill"),
        ("Ctrl + I",         "Import Project"),
        ("Alt + \\",         "Join Clips"),
        ("Ctrl + Shift + L", "Linked Selection"),
        ("Ctrl + /",         "Loop/Unloop"),
        ("Shift + M",        "Modify Marker"),
        ("Ctrl + Shift + N", "New Bin"),
        ("Ctrl + N",         "New Timeline"),
        ("Alt + V",          "Paste Attribute"),
        ("Alt + /",          "Play In To Out"),
        ("Ctrl + Alt + /",   "Play To Out"),
        ("Ctrl + B",         "Razor"),
        ("",                 "Reset Retime"),
        ("Ctrl + R",         "Retime Controls"),
        ("Ctrl + S",         "Save Project"),
        ("Ctrl + Shift + S", "Save Project As…"),
        ("Ctrl + A",         "Select All"),
        ("",                 "Select Clips Backward"),
        ("Shift + V",        "Select Nearest Clip/Gap"),
        ("Ctrl + \\",        "Split Clip"),
    };

    public static (string Shortcut, string Label)[] ActionsForAdobeApp(string app) => app switch
    {
        "Illustrator"   => IllustratorActions,
        "Photoshop"     => PhotoshopActions,
        "Premiere Pro"  => PremiereActions,
        _               => System.Array.Empty<(string, string)>(),
    };

    /// <summary>Zoom's real default Windows keyboard shortcuts, shortcut -> what it does.</summary>
    public static readonly (string Shortcut, string Label)[] ZoomShortcuts =
    {
        ("Alt + F1",              "Switch to active speaker view"),
        ("Alt + F2",              "Switch to gallery view"),
        ("Alt + A",               "Mute/unmute audio"),
        ("Alt + V",               "Start/stop video"),
        ("Alt + M",               "Mute audio for everyone (host)"),
        ("Alt + S",               "Start screen share"),
        ("Alt + Shift + S",       "Stop screen share"),
        ("Alt + T",               "Pause or resume screen share"),
        ("Alt + R",               "Start/stop local recording"),
        ("Alt + C",               "Start/stop cloud recording"),
        ("Alt + P",               "Pause or resume recording"),
        ("Alt + N",               "Switch camera"),
        ("Alt + F",               "Enter or exit full screen"),
        ("Alt + H",               "Show/hide chat panel"),
        ("Alt + U",               "Show/hide participants panel"),
        ("Alt + I",               "Open invite window"),
        ("Alt + Y",               "Raise/lower hand"),
        ("Alt + Shift + R",       "Gain remote control"),
        ("Alt + Shift + G",       "Stop remote control"),
        ("Ctrl + Alt + Shift + H","Show/hide meeting controls"),
        ("Alt + Q",               "End meeting"),
    };
}
