using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: DisplayPad "icons with modifiers". While Ctrl / Shift / Alt / Win is held
/// on the keyboard, every key of the page on the panel that has a picture for that modifier
/// (<see cref="KeyIconSpec.ModifierImages"/>, set in DpKeyConfigDialog) shows it instead of its
/// own; releasing the modifier puts the page's pictures back. Purely visual — what a press does
/// with the modifier held is the action's business (<see cref="TriggerPayload"/>, or the game
/// receiving Ctrl+key).
///
/// The modifier state is polled (cheap: four GetAsyncKeyState calls) rather than hooked, and a
/// modifier must stay down <see cref="ModIconSettle"/> before the icons change, so typing a
/// capital letter doesn't flash the pad. Release is applied at once. The first held modifier in
/// <see cref="TriggerPayload.Modifiers"/> order wins — the same rule the engine uses to pick a
/// modifier action, so icon and action always agree.
///
/// Uploads ride the per-device <c>_dpUploadChain</c> like every other icon write. Keys painted by
/// a live overlay (clock/monitor tiles, Discord, Spotify cover) or looping a GIF are left alone:
/// those services would paint over the swap on their next tick. Takeovers (emoji browser,
/// Discord voice page, action wheel, fullscreen/screensaver) suspend the swap.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan ModIconSettle = TimeSpan.FromMilliseconds(120);

    private DispatcherTimer? _dpModIconTimer;
    /// <summary>Modifier whose pictures are currently applied (null = none).</summary>
    private string? _dpModHeld;
    private string? _dpModCandidate;
    private DateTime _dpModCandidateSince;
    /// <summary>Per device: key → modifier picture currently on the panel in place of the page's.</summary>
    private readonly Dictionary<int, Dictionary<int, string>> _dpModShown = new();

    private void DpInitModIcons()
    {
        _dpModIconTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _dpModIconTimer.Tick += (_, _) => DpModIconTick();
        _dpModIconTimer.Start();
    }

    private static string? DpHeldModifier()
    {
        foreach (var m in TriggerPayload.Modifiers)
            if (ButtonActionEngine.IsModifierHeld(m)) return m;
        return null;
    }

    private void DpModIconTick()
    {
        if (_dpDeviceIds.Count == 0) return;
        string? now = DpHeldModifier();
        if (now != _dpModCandidate) { _dpModCandidate = now; _dpModCandidateSince = DateTime.UtcNow; }
        string? target = now is null ? null
                       : DateTime.UtcNow - _dpModCandidateSince >= ModIconSettle ? now : _dpModHeld;
        if (target == _dpModHeld) return;
        _dpModHeld = target;
        foreach (int id in _dpDeviceIds.ToList()) DpApplyModIcons(id);
    }

    /// <summary>The key's picture to bounce on a press — the modifier one while it is shown.</summary>
    private string? DpModIconShownFor(int id, int btnIndex) =>
        _dpModShown.TryGetValue(id, out var shown) && shown.TryGetValue(btnIndex, out var p) ? p : null;

    /// <summary>Called by every repaint of <paramref name="id"/>'s page: the repaint puts the page's
    /// own pictures back, so forget what was swapped and, if a modifier is still held, swap again
    /// once the repaint's uploads are queued (BeginInvoke runs after the caller has chained them).</summary>
    private void DpModIconsAfterRepaint(int id)
    {
        _dpModShown.Remove(id);
        if (_dpModHeld is not null)
            Dispatcher.BeginInvoke(() => DpApplyModIcons(id), DispatcherPriority.Background);
    }

    private bool DpPanelTakenOver(int id) =>
        DpEmojiBrowserActive(id) || DpDiscordRoomActive(id) || DpWheelActive(id)
        || _dpScreensaverShowing.Contains(id)
        || (_dpFullscreenByDevice.TryGetValue(id, out bool fs) && fs);

    private void DpApplyModIcons(int id)
    {
        if (DpPanelTakenOver(id)) return;

        int profile = DpSelectedDeviceId() == id ? DpCurrentProfile() : _dpStore.GetCurrentProfile(id);
        var rows = _dpStore.LoadPage(id, profile, DpCurrentPageIdFor(id));
        var want = new Dictionary<int, string>();
        if (_dpModHeld is string mod)
        {
            foreach (var r in rows)
            {
                if (r.IconSpec is null || !r.IconSpec.Contains("ModifierImages")) continue;
                if (KeyIconSpec.FromJson(r.IconSpec)?.ModifierImages is not { } imgs
                    || !imgs.TryGetValue(mod, out var path) || !File.Exists(path)) continue;
                if (DpKeyOwnedByOverlay(id, r.ButtonIndex, r.ImagePath)) continue;
                want[r.ButtonIndex] = path;
            }
        }

        var shown = _dpModShown.TryGetValue(id, out var s) ? s : new Dictionary<int, string>();
        if (want.Count == 0 && shown.Count == 0) return;

        var restore = shown.Keys.Where(k => !want.ContainsKey(k))
                                .Select(k => (k, rows.FirstOrDefault(r => r.ButtonIndex == k)?.ImagePath))
                                .ToList();
        var paint = want.Where(kv => !shown.TryGetValue(kv.Key, out var cur) || cur != kv.Value).ToList();
        _dpModShown[id] = want;

        int rotation = _dpStore.GetRotation(id);
        var previous = _dpUploadChain.TryGetValue(id, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[id] = previous.ContinueWith(_ =>
        {
            foreach (var (btn, path) in paint) _dpClient.UploadImage(id, path, btn, rotation);
            foreach (var (btn, original) in restore)
            {
                if (!string.IsNullOrEmpty(original) && File.Exists(original)) _dpClient.UploadImage(id, original, btn, rotation);
                else DpClearKeyOnDevice(id, btn);
            }
        }, TaskScheduler.Default);
    }

    private bool DpKeyOwnedByOverlay(int id, int btn, string? imagePath) =>
        DpLiveTileService.Owns(id, btn) || DiscordVoiceKeyService.Owns(id, btn)
        || DpSpotifyCoverKeyService.Owns(id, btn) || SpotifyCoverService.Owns(id, btn)
        || DpGifAnimator.IsAnimatedGif(imagePath);
}
