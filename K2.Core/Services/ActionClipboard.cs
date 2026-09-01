using System;
using System.Windows;

namespace K2.Core.Services;

/// <summary>
/// In-memory, app-wide clipboard for a single key's action (ActionType/ActionValue) — lets
/// the user copy/cut an action from any key on any device and paste it onto any other key on
/// any device (cross-device: DisplayPad, MacroPad, Everest Max incl. its numpad display keys,
/// Everest 60).
/// For display keys (DisplayPad tile / Everest numpad display key) it ALSO carries the source
/// key's picture (<see cref="ImagePath"/> + <see cref="IconSpecJson"/>): the user reasonably
/// expects "copy action" on a key that shows an emoji-browser / exec / custom picture to bring
/// that picture along too. A paste target with no picture of its own adopts a COPY of it (each
/// device's *PasteAction method), and only falls back to generating the action's
/// <see cref="ActionIconFallback"/> default when the clipboard has no usable picture — which
/// also covers page-only types like <c>dp_emojibrowser</c> that <see cref="ActionIconFallback"/>
/// can't render at all. Non-display keys (regular Everest Max / MacroPad / Everest 60) have no
/// picture concept and simply ignore these two fields on both copy and paste.
/// Static/process-wide by design: K2.App hosts every device module in one process, so this is
/// effectively "the app's clipboard", not per-window state — pasting a DisplayPad key's action
/// onto an Everest Max key (or vice versa) is the point.
/// </summary>
public static class ActionClipboard
{
    public static string? ActionType  { get; private set; }
    public static string? ActionValue { get; private set; }

    /// <summary>Absolute path to the source display key's picture at copy time (custom image,
    /// animated GIF, cropped-GIF sidecar, or a previously auto-generated default icon), or null
    /// when the source had none / isn't a display key. See <see cref="HasImage"/>.</summary>
    public static string? ImagePath { get; private set; }

    /// <summary>The source display key's <c>KeyIconSpec</c> JSON (style the picture was built
    /// with), carried alongside <see cref="ImagePath"/> so "Edit icon" on the paste target
    /// resumes from the same choices. Null when unknown.</summary>
    public static string? IconSpecJson { get; private set; }

    /// <summary>False for an empty clipboard OR a stored "none"/empty type — mirrors every
    /// device key's own <c>HasAction</c> so "nothing to paste" and "action removed" agree.</summary>
    public static bool HasContent => !string.IsNullOrEmpty(ActionType) && ActionType != "none";

    /// <summary>True when <see cref="Copy"/> captured a source picture that still exists on
    /// disk — a display-key paste target with no picture of its own should adopt a copy of it
    /// rather than only regenerating the action's default icon.</summary>
    public static bool HasImage => !string.IsNullOrEmpty(ImagePath) && System.IO.File.Exists(ImagePath);

    public static void Copy(string? actionType, string? actionValue,
                            string? imagePath = null, string? iconSpecJson = null)
    {
        ActionType  = string.IsNullOrEmpty(actionType) || actionType == "none" ? null : actionType;
        ActionValue = ActionType is null ? null : actionValue;
        ImagePath    = ActionType is null || string.IsNullOrEmpty(imagePath) ? null : imagePath;
        IconSpecJson = ActionType is null || string.IsNullOrEmpty(iconSpecJson) ? null : iconSpecJson;
    }

    /// <summary>
    /// True when the clipboard's action can be pasted onto a key belonging to
    /// <paramref name="host"/> — false only for a DisplayPad-page action
    /// (<see cref="ActionTypeHelper.PageOnlyActionTypes"/>) pasted onto a host with no page
    /// concept (MacroPad, Everest Max/60, the Everest numpad display keys — all
    /// <c>IActionHost.SupportsPages == false</c>). Callers show
    /// <see cref="ShowPasteUnsupportedError"/> when this returns false.
    /// </summary>
    public static bool CanPasteOn(IActionHost? host)
    {
        if (!HasContent) return false;
        if (host?.SupportsPages == true) return true;
        return Array.IndexOf(ActionTypeHelper.PageOnlyActionTypes, ActionType) < 0;
    }

    /// <summary>Shared error popup for a paste rejected by <see cref="CanPasteOn"/> — same
    /// wording/style everywhere it can happen (DisplayPad, MacroPad, Everest Max/60, numpad
    /// display keys).</summary>
    public static void ShowPasteUnsupportedError(Window? owner) =>
        MessageBox.Show(owner!, Loc.Get("paste_unsupported_message"), Loc.Get("paste_unsupported_title"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
}
