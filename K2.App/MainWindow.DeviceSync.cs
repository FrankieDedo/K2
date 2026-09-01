using System;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Cross-device sync coordinator for the two GLOBAL flags (Settings tab, persisted in
/// <see cref="AppSettings"/>):
///
/// <list type="bullet">
///   <item><b>Sync across devices</b> (<see cref="AppSettings.SyncAcrossDevices"/>) —
///   switching profile on any connected Mountain device switches every other connected
///   device to the same slot number.</item>
///   <item><b>Sync lighting across devices</b>
///   (<see cref="AppSettings.SyncLightingAcrossDevices"/>) — changing the lighting
///   effect/colour on any device mirrors it to the others, nearest-effect fallback where a
///   device can't reproduce the source effect.</item>
/// </list>
///
/// Base Camp has no firmware flag for either (verified against everest_flags.pcapng — the
/// per-profile "sync" it does is itself just host-side replay), so this is a pure host-side
/// mediator that reuses each device module's already-verified primitives. A single
/// re-entrancy guard (<see cref="_deviceSyncBusy"/>) stops the fan-out from looping back.
///
/// <para><b>Not yet hardware-verified</b> (needs ≥2 Mountain devices connected at once,
/// unavailable in the sessions that built this) — user request 2026-08-28.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Which device a sync notification originated from — the coordinator skips
    /// this one when fanning out.</summary>
    internal enum SyncDeviceKind { Everest, Everest60, MacroPad, DisplayPad }

    /// <summary>Device-neutral lighting state. <see cref="EffectName"/> uses the
    /// Everest Max / MacroPad enum vocabulary as canonical
    /// (Static/Breath/Wave/ReactiveA/ReactiveB/ReactiveC/Yeti/Tornado/Matrix/Custom/Off);
    /// per-device builders/appliers translate to/from it.</summary>
    internal sealed record LightingSnapshot(
        string EffectName, int Color1, int Color2, int Color3,
        int SpeedPct, int BrightnessPct, int DirIndex, bool Rainbow, bool ColorDouble);

    /// <summary>True while a sync fan-out is in progress — every device-module hook checks
    /// this so a sync-driven profile/effect change doesn't re-trigger the coordinator.</summary>
    private bool _deviceSyncBusy;

    // ───────────────────────────── Profile sync ─────────────────────────────

    /// <summary>Called from each device's profile-list selection handler after it has
    /// applied the switch locally. Mirrors the slot to every OTHER connected device when
    /// <see cref="AppSettings.SyncAcrossDevices"/> is on.</summary>
    internal void DeviceSyncOnProfileSwitched(SyncDeviceKind from, int slot)
    {
        if (_deviceSyncBusy || !AppSettings.SyncAcrossDevices) return;
        _deviceSyncBusy = true;
        try
        {
            string s = slot.ToString();
            if (from != SyncDeviceKind.Everest)   TrySync(() => EvSwitchProfile(s));
            if (from != SyncDeviceKind.Everest60) TrySync(() => Ev60SwitchProfile(s));
            if (from != SyncDeviceKind.MacroPad)  TrySync(() => MpSwitchProfile(null, s));
            if (from != SyncDeviceKind.DisplayPad) TrySync(() =>
            {
                foreach (var (id, _) in _dpDeviceLabels) DpSwitchProfile(id, s);
            });
            Log($"[DEVSYNC] profile {slot} mirrored from {from} to the other connected devices");
        }
        finally { _deviceSyncBusy = false; }
    }

    // ───────────────────────────── Lighting sync ────────────────────────────

    /// <summary>Called from each device's "apply current effect" choke point after it has
    /// pushed the effect locally. Mirrors it to every OTHER connected RGB device when
    /// <see cref="AppSettings.SyncLightingAcrossDevices"/> is on. DisplayPad has no RGB
    /// effect model and is skipped.</summary>
    internal void DeviceSyncOnLightingChanged(SyncDeviceKind from, LightingSnapshot snap)
    {
        if (_deviceSyncBusy || !AppSettings.SyncLightingAcrossDevices) return;
        _deviceSyncBusy = true;
        try
        {
            if (from != SyncDeviceKind.Everest)   TrySync(() => EvApplyLightingSnapshot(snap));
            if (from != SyncDeviceKind.MacroPad)  TrySync(() => MpApplyLightingSnapshot(snap));
            if (from != SyncDeviceKind.Everest60) TrySync(() => Ev60RgbPanel.ApplyLightingSnapshot(
                snap.EffectName, snap.Color1, snap.Color2, snap.SpeedPct, snap.BrightnessPct,
                snap.DirIndex, snap.Rainbow, snap.ColorDouble));
            Log($"[DEVSYNC] lighting '{snap.EffectName}' mirrored from {from} to the other connected devices");
        }
        finally { _deviceSyncBusy = false; }
    }

    private void TrySync(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log($"[DEVSYNC] mirror step failed: {ex.Message}"); }
    }

    // ─────────────── Per-device lighting snapshot build / apply ──────────────

    /// <summary>Everest Max ← / → snapshot. Everest Max and MacroPad share the exact same
    /// effect enum names, so the canonical <see cref="LightingSnapshot.EffectName"/> is
    /// just <c>EverestService.Effect.ToString()</c>.</summary>
    private LightingSnapshot? EvBuildLightingSnapshot()
    {
        if (!_evRgbInitialized || CbEvEffect.SelectedItem is not EvEffectChoice pick) return null;
        return new LightingSnapshot(
            pick.Eff.ToString(), _evColor1, _evColor2, _evColor3,
            (int)SldEvSpeed.Value, (int)SldEvBrightness.Value, _evDirIndex,
            RbEvRainbow.IsChecked == true, RbEvColorDouble.IsChecked == true);
    }

    private void EvApplyLightingSnapshot(LightingSnapshot snap)
    {
        if (!_evRgbInitialized) return;
        if (!Enum.TryParse<EverestService.Effect>(MapEffectName(snap.EffectName, forEv60: false), out var eff))
            return;
        int idx = Array.FindIndex(EvEffectList, x => x.Eff == eff);
        if (idx < 0) return;

        bool prev = _evRgbSuppress;
        _evRgbSuppress = true;
        try
        {
            CbEvEffect.SelectedIndex = idx;   // fires the combo handler; apply is suppressed
            SldEvSpeed.Value      = Math.Clamp(snap.SpeedPct, 0, 100);
            SldEvBrightness.Value = Math.Clamp(snap.BrightnessPct, 0, 100);
            _evColor1 = snap.Color1 & 0xFFFFFF;
            _evColor2 = snap.Color2 & 0xFFFFFF;
            _evColor3 = snap.Color3 & 0xFFFFFF;
            ApplyColorButton(BtnEvColor1, _evColor1);
            ApplyColorButton(BtnEvColor2, _evColor2);
            ApplyColorButton(BtnEvColor3, _evColor3);
            _evDirIndex = Math.Max(0, snap.DirIndex);
            RbEvRainbow.IsChecked     = snap.Rainbow;
            RbEvColorDouble.IsChecked = !snap.Rainbow && snap.ColorDouble;
            RbEvColorSingle.IsChecked = !snap.Rainbow && !snap.ColorDouble;
        }
        finally { _evRgbSuppress = prev; }
        UpdateEvCapabilities();
        ApplyCurrentEffect();
    }

    private LightingSnapshot? MpBuildLightingSnapshot()
    {
        if (!_macroLedInitialized || CbMacroEffect.SelectedItem is not MacroEffectChoice pick) return null;
        return new LightingSnapshot(
            pick.Eff.ToString(), _macroColor1, _macroColor2, _macroColor3,
            (int)SldMacroSpeed.Value, (int)SldMacroBrightness.Value, _macroDirIndex,
            RbMacroRainbow.IsChecked == true, RbMacroColorDouble.IsChecked == true);
    }

    private void MpApplyLightingSnapshot(LightingSnapshot snap)
    {
        if (!_macroLedInitialized) return;
        if (!Enum.TryParse<MacroPadService.Effect>(MapEffectName(snap.EffectName, forEv60: false), out var eff))
            return;
        int idx = Array.FindIndex(MacroEffectList, x => x.Eff == eff);
        if (idx < 0) return;

        bool prev = _macroLedSuppress;
        _macroLedSuppress = true;
        try
        {
            CbMacroEffect.SelectedIndex = idx;
            SldMacroSpeed.Value      = Math.Clamp(snap.SpeedPct, 0, 100);
            SldMacroBrightness.Value = Math.Clamp(snap.BrightnessPct, 0, 100);
            _macroColor1 = snap.Color1 & 0xFFFFFF;
            _macroColor2 = snap.Color2 & 0xFFFFFF;
            _macroColor3 = snap.Color3 & 0xFFFFFF;   // no BtnMacroColor3 in the UI — field only
            ApplyColorButton(BtnMacroColor1, _macroColor1);
            ApplyColorButton(BtnMacroColor2, _macroColor2);
            _macroDirIndex = Math.Max(0, snap.DirIndex);
            RbMacroRainbow.IsChecked     = snap.Rainbow;
            RbMacroColorDouble.IsChecked = !snap.Rainbow && snap.ColorDouble;
            RbMacroColorSingle.IsChecked = !snap.Rainbow && !snap.ColorDouble;
        }
        finally { _macroLedSuppress = prev; }
        UpdateMpCapabilities();
        ApplyCurrentMacroEffect();
    }

    /// <summary>Translates a canonical (Everest/MacroPad) effect name to the closest name
    /// the target device understands. <paramref name="forEv60"/> narrows to Everest 60's
    /// smaller set; otherwise it is a pass-through (Everest ↔ MacroPad share names).</summary>
    internal static string MapEffectName(string canonical, bool forEv60)
    {
        if (!forEv60)
        {
            // Everest-only names MacroPad lacks → nearest MacroPad effect.
            return canonical switch
            {
                "Matrix2" or "DiagonalWave" => "Wave",
                _ => canonical,
            };
        }
        return canonical switch
        {
            "Breath"                              => "Breathing",
            "ReactiveA" or "ReactiveB" or "ReactiveC" => "Reactive",
            "Matrix" or "Matrix2" or "DiagonalWave"  => "Wave",
            "Static" or "Wave" or "Tornado" or "Yeti" or "Custom" or "Off" => canonical,
            _ => "Static",
        };
    }
}
