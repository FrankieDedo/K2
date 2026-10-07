using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Lighting" category's two panels — brightness step
/// (<see cref="LightingActionTypes.Brightness"/>) and effect switch
/// (<see cref="LightingActionTypes.Effect"/>). Devices come from the host's
/// <see cref="IActionHost.Lighting"/>, in the order of the device tabs: the list is the same
/// whichever device the key belongs to, only the default selection (that device) changes.
/// </summary>
public partial class ButtonActionDialog
{
    private IReadOnlyList<LightingTargetOption>? _lightingTargets;

    private IReadOnlyList<LightingTargetOption> LightingTargets =>
        _lightingTargets ??= _host?.Lighting?.ListLightingTargets()
                             ?? (IReadOnlyList<LightingTargetOption>)new List<LightingTargetOption>();

    private string LightingSelfKey => _host?.SelfTargetKey ?? "";

    /// <summary>A saved target key as the device list knows it: an empty key (a
    /// never-configured action) means the key's own device.</summary>
    private string LightingKeyOf(string savedKey) => savedKey.Length > 0 ? savedKey : LightingSelfKey;

    /// <summary>The devices a lighting action can be pointed at: the connected ones, the key's
    /// own, and whatever <paramref name="savedKeys"/> already point at — a target must not
    /// vanish from a saved action just because its device is unplugged right now.</summary>
    private List<LightingTargetOption> LightingDeviceChoices(IEnumerable<string> savedKeys, bool effectsOnly)
    {
        var keep = savedKeys.Append(LightingSelfKey).ToHashSet();
        return LightingTargets
            .Where(t => !effectsOnly || t.Effects.Count > 0)
            .Where(t => t.Connected || keep.Contains(t.Key))
            .ToList();
    }

    // ---- Brightness ---------------------------------------------------

    private void EnsureBrightnessPanel()
    {
        if (CbBrightMode.Items.Count == 0) LoadBrightnessSpec(new BrightnessActionPayload());
    }

    private void LoadBrightnessSpec(BrightnessActionPayload spec)
    {
        CbBrightMode.Items.Clear();
        foreach (var (mode, locKey) in new[]
                 {
                     (BrightnessActionPayload.Cycle, "light_mode_cycle"),
                     (BrightnessActionPayload.Up,    "light_mode_up"),
                     (BrightnessActionPayload.Down,  "light_mode_down"),
                 })
            CbBrightMode.Items.Add(new ComboBoxItem { Content = Loc.Get(locKey), Tag = mode });
        CbBrightMode.SelectedItem = CbBrightMode.Items.OfType<ComboBoxItem>()
            .First(i => (string?)i.Tag == spec.Mode);

        var targets = spec.Targets.Select(LightingKeyOf).ToHashSet();
        PnlBrightTargets.Children.Clear();
        foreach (var device in LightingDeviceChoices(targets, effectsOnly: false))
            PnlBrightTargets.Children.Add(new CheckBox
            {
                Content = device.Label,
                Tag = device.Key,
                IsChecked = targets.Contains(device.Key),
                Margin = new Thickness(0, 0, 0, 4),
            });
    }

    private BrightnessActionPayload SaveBrightnessSpec()
    {
        var targets = PnlBrightTargets.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string?)c.Tag ?? "")
            .ToList();
        // Nothing ticked would save a key that does nothing: fall back to the key's own device.
        if (targets.Count == 0) targets.Add(LightingSelfKey);

        return new BrightnessActionPayload
        {
            Mode = CbBrightMode.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? BrightnessActionPayload.Cycle
                                                                : BrightnessActionPayload.Cycle,
            Targets = targets,
        };
    }

    // ---- Effect -------------------------------------------------------

    private void EnsureLightEffectPanel()
    {
        if (CbFxDevice.ItemsSource is null) LoadLightEffectSpec(new LightEffectPayload());
    }

    private void LoadLightEffectSpec(LightEffectPayload spec)
    {
        string key = LightingKeyOf(spec.Key);
        var devices = LightingDeviceChoices(new[] { key }, effectsOnly: true);
        CbFxDevice.ItemsSource = devices;
        LblFxNoDevice.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (devices.Count == 0) { CbFxEffect.Items.Clear(); return; }

        // The key's own device may have no effects at all (a DisplayPad): the first one that does.
        var device = devices.FirstOrDefault(d => d.Key == key) ?? devices[0];
        CbFxDevice.SelectedItem = device;   // fires CbFxDevice_SelectionChanged
        PopulateFxEffects(device, spec.Target);
    }

    private void CbFxDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbFxDevice.SelectedItem is not LightingTargetOption device) return;
        // Keep the choice across a device change when the new device has it too (Next/Previous
        // always do).
        string? current = CbFxEffect.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag : null;
        PopulateFxEffects(device, current);
    }

    private void PopulateFxEffects(LightingTargetOption device, string? selectTag)
    {
        CbFxEffect.Items.Clear();
        CbFxEffect.Items.Add(new ComboBoxItem { Content = Loc.Get("light_effect_next"), Tag = LightEffectPayload.NextEffect });
        CbFxEffect.Items.Add(new ComboBoxItem { Content = Loc.Get("light_effect_previous"), Tag = LightEffectPayload.PreviousEffect });
        foreach (var fx in device.Effects)
            CbFxEffect.Items.Add(new ComboBoxItem { Content = fx.Name, Tag = fx.Id });

        var match = CbFxEffect.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == selectTag);
        CbFxEffect.SelectedItem = match ?? CbFxEffect.Items[0];
    }

    private LightEffectPayload SaveLightEffectSpec()
    {
        var device = CbFxDevice.SelectedItem as LightingTargetOption;
        var effect = CbFxEffect.SelectedItem as ComboBoxItem;
        string target = (string?)effect?.Tag ?? LightEffectPayload.NextEffect;
        return new LightEffectPayload
        {
            Key = device?.Key ?? LightingSelfKey,
            Target = target,
            Name = target is LightEffectPayload.NextEffect or LightEffectPayload.PreviousEffect
                ? "" : effect?.Content as string ?? "",
            Device = device?.Label ?? "",
        };
    }
}
