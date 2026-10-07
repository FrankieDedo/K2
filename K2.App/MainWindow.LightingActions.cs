using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: the "Lighting" action category (<see cref="LightingActionTypes"/>) —
/// brightness step and effect switch from a key, on any device of this process. Shared by
/// every action-host adapter through <see cref="IActionHost.Lighting"/>, like
/// <see cref="SwitchProfileByKey"/> is for "switch profile".
///
/// Each device is driven through the SAME controls its Lighting section uses (the shared
/// top-bar brightness slider, the effect combo): their handlers already apply to the device
/// and persist per profile, so a key press and a click in the UI cannot drift apart.
///
/// <para><b>Presses are summed, not queued.</b> A lighting write is slow (on the Everest Max
/// it holds the UI thread for the whole firmware round trip), so a burst of presses sent one
/// write each stacked seconds of work and froze the window. A press only moves a pending
/// target; the device is written ONCE, <see cref="LightingSettle"/> after the last press,
/// straight to where the burst ended.</para>
/// </summary>
public partial class MainWindow : ILightingController
{
    private const string MakaluLightingKey = "makalu:1";

    /// <summary>Quiet time after the last press before the device is written.</summary>
    private static readonly TimeSpan LightingSettle = TimeSpan.FromMilliseconds(350);

    /// <summary>Where a burst of presses has got to, per target key, until its timer fires.</summary>
    private readonly Dictionary<string, int> _lightPendingBrightness = new();
    private readonly Dictionary<string, int> _lightPendingEffect = new();
    private readonly Dictionary<string, DispatcherTimer> _lightTimers = new();

    private static IReadOnlyList<LightingEffectChoice> NoLightingEffects => Array.Empty<LightingEffectChoice>();

    IReadOnlyList<LightingTargetOption> ILightingController.ListLightingTargets()
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => ((ILightingController)this).ListLightingTargets());

        var list = new List<LightingTargetOption>
        {
            new("everest:1", TabEverest.Header as string ?? Loc.Get("tab_everest"), _everest.IsOpen,
                EvEffectList.Select(e => new LightingEffectChoice(e.Eff.ToString(), e.Label)).ToList()),
            new("everest60:1", TabEverest60.Header as string ?? Loc.Get("tab_everest60"), _ev60Connected,
                Ev60RgbPanel.LightingEffects),
            new(MakaluLightingKey, TabMakalu.Header as string ?? Loc.Get("tab_makalu"), _mkConnected,
                MkRgbSettings.LightingEffects),
        };

        // Brightness only: a DisplayPad has a screen backlight, no effects.
        foreach (var (id, label) in _dpDeviceLabels)
            list.Add(new LightingTargetOption($"displaypad:{id}", label, _dpClient.IsPlugged(id), NoLightingEffects));

        if (_activeMpDeviceId is int mpId)
            list.Add(new LightingTargetOption($"macropad:{mpId}",
                TabMacroPad.Header as string ?? Loc.Get("tab_macropad"), true,
                MacroEffectList.Select(e => new LightingEffectChoice(e.Eff.ToString(), e.Label)).ToList()));

        // Same order as the device tabs at the top of the window.
        var tabOrder = TcDevices.Items.OfType<TabItem>()
            .Select((tab, index) => (Tag: tab.Tag as string ?? "", index))
            .Where(t => t.Tag.Length > 0)
            .GroupBy(t => t.Tag).ToDictionary(g => g.Key, g => g.First().index);
        return list.OrderBy(t => tabOrder.GetValueOrDefault(LightingTabTag(t.Key), int.MaxValue)).ToList();
    }

    /// <summary>The <c>TabItem.Tag</c> of a target key's device tab.</summary>
    private static string LightingTabTag(string targetKey)
    {
        var parts = targetKey.Split(':', 2);
        return parts[0] == "displaypad" && parts.Length == 2 ? $"dp_{parts[1]}" : parts[0];
    }

    void ILightingController.StepBrightness(string targetKey, string mode)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ((ILightingController)this).StepBrightness(targetKey, mode));
            return;
        }
        if (!TryParseLightingKey(targetKey, out string kind, out int id)) return;

        int current;
        if (_lightPendingBrightness.TryGetValue(targetKey, out int pending)) current = pending;
        else if (kind == "displaypad")
        {
            if (!_dpClient.IsPlugged(id)) { Log($"[EXEC] brightness: {targetKey} not connected"); return; }
            current = DpBrightnessOf(id);
        }
        else if (LightingSliderFor(kind, id, targetKey) is { } slider) current = (int)slider.Value;
        else return;

        _lightPendingBrightness[targetKey] = BrightnessActionPayload.Next(current, mode);
        LightingRestartTimer("b|" + targetKey, () => ApplyPendingBrightness(targetKey, kind, id));
    }

    private void ApplyPendingBrightness(string targetKey, string kind, int id)
    {
        if (!_lightPendingBrightness.Remove(targetKey, out int level)) return;

        if (kind == "displaypad")
        {
            // Not through SldDpBrightness: that slider belongs to the pad currently SHOWN.
            if (!_dpClient.IsPlugged(id)) return;
            _dpClient.SetBrightness(id, level);
            _dpStore.SetBrightness(id, level);
            if (DpSelectedDeviceId() == id)
            {
                _dpSuppressBrightness = true;
                try { SldDpBrightness.Value = level; LblDpBrightness.Text = $"{level}%"; }
                finally { _dpSuppressBrightness = false; }
            }
        }
        else
        {
            if (LightingSliderFor(kind, id, targetKey) is not { } slider) return;
            slider.Value = level;   // its ValueChanged handler applies + saves
        }
        Log($"[EXEC] brightness: {targetKey} -> {level}%");
    }

    void ILightingController.SwitchEffect(string targetKey, string target)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ((ILightingController)this).SwitchEffect(targetKey, target));
            return;
        }
        if (!TryParseLightingKey(targetKey, out string kind, out int id)) return;
        if (LightingEffectAccess(kind) is not { } fx) { Log($"[EXEC] effect: {targetKey} has no lighting effects"); return; }
        if (LightingSliderFor(kind, id, targetKey) is null) return;   // connection check + log

        int current = _lightPendingEffect.TryGetValue(targetKey, out int pending) ? pending : fx.Get();
        int idx = LightEffectPayload.ResolveIndex(fx.Ids, current, target);
        if (idx < 0) { Log($"[EXEC] effect: {targetKey} has no effect \"{target}\""); return; }

        _lightPendingEffect[targetKey] = idx;
        LightingRestartTimer("e|" + targetKey, () =>
        {
            if (!_lightPendingEffect.Remove(targetKey, out int final)) return;
            if (LightingSliderFor(kind, id, targetKey) is null) return;
            fx.Set(final);   // the combo's SelectionChanged handler applies + saves
            Log($"[EXEC] effect: {targetKey} -> {fx.Ids[final]}");
        });
    }

    /// <summary>A device's effect list and its effect combo's index, by device kind. Null for
    /// a kind with no effects (DisplayPad).</summary>
    private (IReadOnlyList<string> Ids, Func<int> Get, Action<int> Set)? LightingEffectAccess(string kind) => kind switch
    {
        "everest"   => (EvEffectList.Select(e => e.Eff.ToString()).ToList(),
                        () => CbEvEffect.SelectedIndex, i => CbEvEffect.SelectedIndex = i),
        "macropad"  => (MacroEffectList.Select(e => e.Eff.ToString()).ToList(),
                        () => CbMacroEffect.SelectedIndex, i => CbMacroEffect.SelectedIndex = i),
        "everest60" => (Ev60RgbPanel.LightingEffects.Select(e => e.Id).ToList(),
                        () => Ev60RgbPanel.LightingEffectIndex, i => Ev60RgbPanel.LightingEffectIndex = i),
        "makalu"    => (MkRgbSettings.LightingEffects.Select(e => e.Id).ToList(),
                        () => MkRgbSettings.LightingEffectIndex, i => MkRgbSettings.LightingEffectIndex = i),
        _           => null,
    };

    /// <summary>(Re)starts the settle timer of one pending write: every press pushes the write
    /// back, so it happens once, after the burst.</summary>
    private void LightingRestartTimer(string timerKey, Action apply)
    {
        if (_lightTimers.Remove(timerKey, out var old)) old.Stop();
        var timer = new DispatcherTimer { Interval = LightingSettle };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _lightTimers.Remove(timerKey);
            try { apply(); }
            catch (Exception ex) { Log($"[EXEC] lighting: {timerKey} error: {ex.Message}"); }
        };
        _lightTimers[timerKey] = timer;
        timer.Start();
    }

    /// <summary>The top-bar brightness slider of a connected device, or null (logged) when the
    /// device is not connected / not the one its section is showing.</summary>
    private Slider? LightingSliderFor(string kind, int id, string targetKey)
    {
        (Slider? slider, bool connected) = kind switch
        {
            // The MacroPad section (slider included) only ever shows the active pad.
            "macropad"  => (SldMacroBrightness, _activeMpDeviceId == id && _macroLedInitialized),
            "everest"   => (SldEvBrightness, _everest.IsOpen && _evRgbInitialized),
            "everest60" => (SldEv60Brightness, _ev60Connected),
            "makalu"    => (SldMkBrightness, _mkConnected),
            _           => ((Slider?)null, false),
        };
        if (slider is null) { Log($"[EXEC] lighting: unknown device kind \"{kind}\""); return null; }
        if (!connected) { Log($"[EXEC] lighting: {targetKey} not connected"); return null; }
        return slider;
    }

    private bool TryParseLightingKey(string targetKey, out string kind, out int id)
    {
        var parts = targetKey.Split(':', 2);
        kind = parts[0];
        id = 0;
        if (parts.Length == 2 && int.TryParse(parts[1], out id)) return true;
        Log($"[EXEC] lighting: bad target key \"{targetKey}\"");
        return false;
    }
}
