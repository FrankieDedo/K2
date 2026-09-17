using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace K2.App.Services;

/// <summary>
/// Holds back an Everest firmware command that keeps the keyboard busy until no key is
/// held down, so the stall never falls between a key's press and its release.
///
/// <para><b>Why.</b> The Everest Max has one MCU: while it works on a command from K2
/// (effect apply, AP toggle, SaveFlash, settings write, clock) it stops servicing its
/// keyboard endpoint. USB keyboards don't auto-repeat — Windows does, from the moment it
/// sees the press until it sees the release. So a key pressed just before a stall has its
/// release reported only when the stall ends, and Windows fills the gap with repeats:
/// "typing while K2 starts, it freezes and then types the last key many times" (user
/// report 2026-09-11; the same mechanism was already root-caused for the backlight wake
/// on 2026-08-30). A key pressed DURING a stall is harmless — press and release come out
/// together afterwards — so all it takes is not to start a stall while a key is down.</para>
///
/// <para><b>How.</b> <see cref="Enter"/> waits (bounded) while any non-modifier key is down
/// according to <c>GetAsyncKeyState</c>, which is global and updated by the input thread,
/// so it works whichever thread calls it and whatever window has focus. Dispose marks the
/// end of the busy command: the next <see cref="Enter"/> first waits <see cref="SettleMs"/>
/// so a press the firmware queued during the stall has reached Windows before the check —
/// otherwise back-to-back commands would slip the next stall under that very press.</para>
///
/// <para>Modifiers are ignored (a repeating Shift/Ctrl types nothing, and they are often
/// held while clicking in K2). A key held longer than the wait cap is a deliberate hold —
/// it repeats anyway — so after a timeout the gate stands aside for a short while instead
/// of making every following command wait the full cap again.</para>
/// </summary>
internal static class KeyboardQuietGate
{
    /// <summary>Time after a busy command before the next check (see class remarks).</summary>
    private const int SettleMs = 25;
    private const int PollMs = 10;
    /// <summary>After a wait timed out, don't wait again for this long.</summary>
    private const int BypassAfterTimeoutMs = 2000;

    private static long _lastBusyEndTicks = long.MinValue / 2;
    private static long _bypassUntilTicks;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Waits until no key is held (at most <paramref name="maxWaitMs"/>), then
    /// returns a scope whose Dispose marks the end of the busy command.</summary>
    public static Scope Enter(string op, int maxWaitMs = 600)
    {
        Wait(op, maxWaitMs);
        return new Scope(true);
    }

    /// <summary>The waiting half of <see cref="Enter"/>, without marking a busy command —
    /// for a caller that wants to wait for a gap BEFORE taking a lock others need.</summary>
    public static void Wait(string op, int maxWaitMs)
    {
        long sinceBusy = Environment.TickCount64 - Interlocked.Read(ref _lastBusyEndTicks);
        if (sinceBusy >= 0 && sinceBusy < SettleMs) Thread.Sleep((int)(SettleMs - sinceBusy));

        if (Environment.TickCount64 < Interlocked.Read(ref _bypassUntilTicks)) return;
        if (!AnyKeyHeld(out int vk)) return;

        long start = Environment.TickCount64;
        while (Environment.TickCount64 - start < maxWaitMs)
        {
            Thread.Sleep(PollMs);
            if (!AnyKeyHeld(out _))
            {
                App.WriteLog($"[KbdGate] {op}: waited {Environment.TickCount64 - start}ms for key release (vk=0x{vk:X2})");
                return;
            }
        }
        Interlocked.Exchange(ref _bypassUntilTicks, Environment.TickCount64 + BypassAfterTimeoutMs);
        App.WriteLog($"[KbdGate] {op}: key still held after {maxWaitMs}ms (vk=0x{vk:X2}) — going ahead");
    }

    private static bool AnyKeyHeld(out int heldVk)
    {
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (IsIgnored(vk)) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) { heldVk = vk; return true; }
        }
        heldVk = 0;
        return false;
    }

    private static bool IsIgnored(int vk) => vk is
        0x10 or 0x11 or 0x12 or        // Shift / Ctrl / Alt (generic)
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or   // L/R Shift, Ctrl, Alt
        0x5B or 0x5C or                // L/R Win
        0x14 or 0x90 or 0x91;          // Caps / Num / Scroll Lock (toggles, never type)

    /// <summary>End-of-busy marker; <c>default(Scope)</c> is a no-op.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _active;
        internal Scope(bool active) => _active = active;
        public void Dispose()
        {
            if (_active) Interlocked.Exchange(ref _lastBusyEndTicks, Environment.TickCount64);
        }
    }
}
