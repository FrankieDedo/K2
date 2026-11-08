using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace K2.Core;

/// <summary>
/// ButtonActionEngine partial: <see cref="TriggerPayload"/> keys — per-modifier actions, long
/// press and hold-to-repeat (issue #31). Runs entirely on the UI thread (DispatcherTimer), like
/// the rest of the engine.
/// </summary>
public sealed partial class ButtonActionEngine
{
    private sealed class PendingTrigger
    {
        public required TriggerPayload Payload { get; init; }
        public required DispatcherTimer Timer { get; init; }
        public DateTime Since { get; } = DateTime.UtcNow;
        /// <summary>The long action already ran: the up edge must not run the base one too.</summary>
        public bool Fired { get; set; }
        /// <summary>Set while an up edge that came too soon is waiting to be confirmed (see
        /// <see cref="GlitchWindow"/>).</summary>
        public DispatcherTimer? ReleaseConfirm { get; set; }
    }

    private readonly Dictionary<int, PendingTrigger> _pendingTriggers = new();

    /// <summary>An up edge this soon after the down edge is held back for <see cref="GlitchConfirm"/>
    /// before it counts: an Everest Max key claimed by the host reports ONE press as down/up/down a
    /// few ms apart (see MainWindow.Everest's ExecuteEverestKeyDeduped), which would otherwise end
    /// every long press as a short one. A genuine tap only pays the confirm delay.</summary>
    private static readonly TimeSpan GlitchWindow = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan GlitchConfirm = TimeSpan.FromMilliseconds(80);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True while the named modifier is physically held on any keyboard.</summary>
    public static bool IsModifierHeld(string modifier) => modifier switch
    {
        "ctrl"  => IsDown(0x11),
        "shift" => IsDown(0x10),
        "alt"   => IsDown(0x12),
        "win"   => IsDown(0x5B) || IsDown(0x5C),
        _       => false,
    };

    private void PressTrigger(string value, int buttonIndex, bool momentary, bool releaseFollows)
    {
        if (_pendingTriggers.TryGetValue(buttonIndex, out var bouncing) && bouncing.ReleaseConfirm is not null)
        {
            ResumeHold(buttonIndex);
            return;
        }
        var p = TriggerPayload.Parse(value);
        if (p is null) { _host.Log($"[EXEC] button #{buttonIndex}: invalid trigger payload"); return; }

        foreach (var mod in TriggerPayload.Modifiers)
        {
            if (p.ModAction(mod) is not { } m || !IsModifierHeld(mod)) continue;
            _host.Log($"[EXEC] button #{buttonIndex}: {mod} variant");
            Dispatch(m.Type, m.Value ?? "", buttonIndex, momentary);
            return;
        }

        bool timed = buttonIndex >= 0 && releaseFollows
                     && (p.Hold == TriggerPayload.HoldMode.Repeat
                         || (p.Hold == TriggerPayload.HoldMode.Long && !string.IsNullOrEmpty(p.LongType) && p.LongType != "none"));
        if (!timed)
        {
            Dispatch(p.Type, p.Value ?? "", buttonIndex, momentary);
            return;
        }

        CancelTrigger(buttonIndex);   // a stale one whose up edge never came
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(p.HoldMs, 100, 5000)) };
        var pending = new PendingTrigger { Payload = p, Timer = timer };
        _pendingTriggers[buttonIndex] = pending;

        if (p.Hold == TriggerPayload.HoldMode.Repeat)
        {
            // First run right away, then again every RepeatMs once the hold delay has passed.
            // Repeats never hold keys down: a held shortcut can't be pressed again on top of itself.
            Dispatch(p.Type, p.Value ?? "", buttonIndex, momentary: false);
            timer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow - pending.Since > HeldShortcutMaxAge) { CancelTrigger(buttonIndex); return; }
                timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(p.RepeatMs, 20, 5000));
                Dispatch(p.Type, p.Value ?? "", buttonIndex, momentary: false);
            };
        }
        else
        {
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                pending.Fired = true;
                _host.Log($"[EXEC] button #{buttonIndex}: long press");
                Dispatch(p.LongType!, p.LongValue ?? "", buttonIndex, momentary: false);
            };
        }
        timer.Start();
    }

    /// <summary>Up edge of a timed trigger: ends a repeat, or — when the long press threshold
    /// was not reached — runs the base action now.</summary>
    private void ReleaseTrigger(int buttonIndex)
    {
        if (!_pendingTriggers.TryGetValue(buttonIndex, out var pending)) return;
        if (pending.ReleaseConfirm is not null) return;

        if (DateTime.UtcNow - pending.Since < GlitchWindow)
        {
            var confirm = new DispatcherTimer { Interval = GlitchConfirm };
            confirm.Tick += (_, _) =>
            {
                confirm.Stop();
                pending.ReleaseConfirm = null;
                if (_pendingTriggers.TryGetValue(buttonIndex, out var cur) && cur == pending)
                    FinishTrigger(buttonIndex, pending);
            };
            pending.ReleaseConfirm = confirm;
            confirm.Start();
            return;
        }
        FinishTrigger(buttonIndex, pending);
    }

    private void FinishTrigger(int buttonIndex, PendingTrigger pending)
    {
        pending.Timer.Stop();
        _pendingTriggers.Remove(buttonIndex);
        var p = pending.Payload;
        if (p.Hold == TriggerPayload.HoldMode.Long && !pending.Fired)
            Dispatch(p.Type, p.Value ?? "", buttonIndex, momentary: false);
    }

    /// <summary>A second down edge arrived for a key whose up edge is still being confirmed (the
    /// Everest Max glitch described at <see cref="GlitchWindow"/>): the key is still held, carry on.
    /// Devices that drop the duplicate down edge themselves call this instead of
    /// <see cref="Execute"/>.</summary>
    public void ResumeHold(int buttonIndex)
    {
        if (!_pendingTriggers.TryGetValue(buttonIndex, out var pending) || pending.ReleaseConfirm is null) return;
        pending.ReleaseConfirm.Stop();
        pending.ReleaseConfirm = null;
    }

    private void CancelTrigger(int buttonIndex)
    {
        if (!_pendingTriggers.TryGetValue(buttonIndex, out var pending)) return;
        pending.Timer.Stop();
        pending.ReleaseConfirm?.Stop();
        _pendingTriggers.Remove(buttonIndex);
    }

    private void CancelAllTriggers()
    {
        foreach (var p in _pendingTriggers.Values) { p.Timer.Stop(); p.ReleaseConfirm?.Stop(); }
        _pendingTriggers.Clear();
    }
}
