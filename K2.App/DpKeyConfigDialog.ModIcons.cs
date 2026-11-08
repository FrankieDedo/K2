using System.Collections.Generic;
using System.Windows;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// DpKeyConfigDialog partial: the "Modifiers" button — the shared <see cref="ModifiersWindow"/>
/// (also reachable from the action dialog), editing in one place what the key does while Ctrl /
/// Shift / Alt / Win is held (the modifier actions inside its <see cref="TriggerPayload"/>) and
/// the picture it shows meanwhile (<see cref="KeyIconSpec.ModifierImages"/>, painted at runtime
/// by MainWindow.DisplayPad.ModIcons.cs).
/// </summary>
public partial class DpKeyConfigDialog
{
    private void UpdateModifierIconsButton()
    {
        int n = _spec.ModifierImages?.Count ?? 0;
        var trig = ActionType == TriggerPayload.Tag ? TriggerPayload.Parse(ActionValue) : null;
        bool set = n > 0 || trig?.Mods.Count > 0;
        BtnModifierIcons.Content = Loc.Get("dp_mod_icons") + (set ? "  ●" : "");
    }

    /// <summary>The key's modifier pictures in the form the shared popups edit.</summary>
    internal ModifierImageContext ModifierImageContextForKey() => new()
    {
        Images = _spec.ModifierImages is null ? new Dictionary<string, string>() : new Dictionary<string, string>(_spec.ModifierImages),
        IconSize = DpHidNative.IconSize,
        OutputDir = MainWindow.DpAutoIconDir,
        RenderActionIcon = (t, v) => DpDefaultIconRenderer.Render(t, v, new KeyIconSpec(), null, DpHidNative.IconSize),
    };

    /// <summary>Writes back pictures edited in a shared popup.</summary>
    internal void ApplyModifierImages(ModifierImageContext? ctx)
    {
        if (ctx is null) return;
        _spec.ModifierImages = ctx.Images.Count == 0 ? null : new Dictionary<string, string>(ctx.Images);
        UpdateModifierIconsButton();
    }

    private void BtnModifierIcons_Click(object sender, RoutedEventArgs e)
    {
        var (baseType, baseValue) = TriggerPayload.Unwrap(ActionType, ActionValue);
        bool canWrap = TriggerPayload.CanWrap(baseType);
        TriggerPayload? work = null;
        if (canWrap)
            work = (ActionType == TriggerPayload.Tag ? TriggerPayload.Parse(ActionValue) : null) ?? new TriggerPayload();

        var images = ModifierImageContextForKey();
        var host = (Owner as MainWindow)?._dpActionHost
                   ?? (Application.Current?.MainWindow as MainWindow)?._dpActionHost;
        var win = new ModifiersWindow(work, images, host) { Owner = this };
        if (win.ShowDialog() != true) return;

        if (work is not null)
        {
            var (t, v) = TriggerPayload.Wrap(baseType!, baseValue ?? "", work);
            ActionType = t;
            ActionValue = v;
            RefreshActionSummary();
        }
        ApplyModifierImages(images);
    }
}
