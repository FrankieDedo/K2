using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace K2.App.Services;

/// <summary>
/// Keeps the resources of an overlapped HID transfer that could not be reaped alive
/// forever, instead of freeing them under a still-live kernel I/O.
///
/// <para><b>Why this exists.</b> <c>EverestHidNative.Transfer</c> /
/// <c>DpHidNative.Transfer</c> issue an overlapped ReadFile/WriteFile, wait on an event
/// with a hard timeout and, on timeout, <c>CancelIoEx</c>. Cancellation is itself
/// asynchronous: until the event signals, the driver still owns (a) the pinned data
/// buffer and (b) the OVERLAPPED block. Freeing them at that point — which is what the
/// original <c>finally</c> did unconditionally — unpins a GC-heap array and releases the
/// HGlobal while the kernel is still holding pointers to both. When the transfer finally
/// completes the kernel writes the report into whatever now occupies those addresses,
/// corrupting the managed heap. The CLR notices at the next GC and aborts the process
/// with <c>0x80131506</c> (COR_E_EXECUTIONENGINE, "internal error in the .NET Runtime")
/// in <c>coreclr.dll</c> — no managed exception, no crash-log entry, no ProcessExit.
/// Observed 2026-09-10 00:36 during AutoOpen's Everest→Everest60 stagger, while the
/// reader loop and SDKDLL.dll were both hammering MI_03.</para>
///
/// <para>Abandoning leaks a pinned ~65-byte buffer, a NativeOverlapped and an event
/// handle per occurrence. That is the deliberate trade: a bounded, diagnosable leak
/// instead of an unrecoverable heap corruption. It should be rare — every occurrence is
/// logged via <see cref="App.WriteLog"/>, so a growing count in the log means the real
/// problem is a device/driver that stopped reaping cancellations.</para>
/// </summary>
internal static class AbandonedHidIo
{
    private static readonly List<Entry> _live = new();

    private sealed record Entry(GCHandle Pin, IntPtr Overlapped, ManualResetEvent Event);

    /// <summary>Number of transfers abandoned so far this session.</summary>
    internal static int Count { get { lock (_live) return _live.Count; } }

    /// <summary>
    /// Permanently retains <paramref name="pin"/> (never <c>Free</c>d, so the buffer stays
    /// pinned at the address the driver holds), <paramref name="ovl"/> (never
    /// <c>FreeHGlobal</c>ed) and <paramref name="evt"/> (kept strongly reachable so the
    /// finalizer never closes the handle the OVERLAPPED points at).
    /// </summary>
    internal static void Keep(string who, GCHandle pin, IntPtr ovl, ManualResetEvent evt)
    {
        int n;
        lock (_live)
        {
            _live.Add(new Entry(pin, ovl, evt));
            n = _live.Count;
        }
        App.WriteLog($"[HID] {who}: I/O not reaped after CancelIoEx — buffer+OVERLAPPED " +
                     $"abandoned to avoid heap corruption (abandoned so far: {n})");
    }
}
