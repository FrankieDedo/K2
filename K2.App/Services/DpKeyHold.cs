using System.Collections.Generic;

namespace K2.App.Services;

/// <summary>
/// DisplayPad keys temporarily owned by something drawn on top of the page (the action wheel):
/// the live painters (live tiles, GIF loops, Spotify/Discord overlays) skip a held key instead of
/// being stopped for the whole pad, so every key outside the wheel keeps running untouched.
/// Thread-safe: the painters run on their own threads.
/// </summary>
internal static class DpKeyHold
{
    private static readonly object _gate = new();
    private static readonly HashSet<(int Dev, int Btn)> _held = new();

    public static bool IsHeld(int deviceId, int btn)
    {
        lock (_gate) return _held.Contains((deviceId, btn));
    }

    public static void Hold(int deviceId, IEnumerable<int> buttons)
    {
        lock (_gate) foreach (int b in buttons) _held.Add((deviceId, b));
    }

    public static void Release(int deviceId)
    {
        lock (_gate) _held.RemoveWhere(k => k.Dev == deviceId);
    }
}
