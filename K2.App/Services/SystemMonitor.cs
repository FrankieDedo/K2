// SystemMonitor.cs — PC metrics for the Everest Max Media Dock's "PC Info" pages.
//
// Base Camp's own numbers (ground truth, decompiled 2026-08-22 from
// BaseCamp.ResourceMonitorHelper/PCResourceMonitorHelper.cs and
// BaseCamp.LibreHardwareMonitor/LibreHWMonitorHelper.cs — the values its
// BaseCampService.PcInfo_timer feeds to Everest.SetPCInfo):
//
//   CPU  (SetPCInfo type 0) = "\Processor(_Total)\% Processor Time",  ceil -> int %
//   GPU  (type 1)           = LibreHardwareMonitor "GPU Core" Load sensor -> int %
//   Disk (type 2)           = "\PhysicalDisk(_Total)\% Disk Time",    ceil -> int
//   Net  (type 3)           = LHM "Download Speed" (bytes/s) / 1e6    -> int MB/s
//   RAM  (type 4)           = (Total - Free) / Total * 100            -> int %
//   Volume (SetVolumeInfo)  = master volume scalar * 100              -> int %
//
// K2 reproduces those numbers WITHOUT taking LibreHardwareMonitor as a
// dependency: CPU comes from GetSystemTimes, RAM from GlobalMemoryStatusEx,
// disk and GPU from PDH (the same performance counters BC reads through the
// PerformanceCounter class, minus the System.Diagnostics.PerformanceCounter
// package), download speed from NetworkInterface statistics, and volume from
// Core Audio. PDH counters are added with PdhAddEnglishCounter, not
// PdhAddCounter: counter paths are localized on a non-English Windows and the
// English form is the only one that works everywhere.
//
// Every getter is best-effort and returns 0 on any failure — a dead metric must
// never take down the dock poller (MainWindow.MediaDock.cs) that calls it.

using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace K2.App.Services;

internal static class SystemMonitor
{
    // ───────────────────── Sampling cache ─────────────────────
    //
    // Two independent consumers now read these numbers: the Media Dock poller
    // (MainWindow.MediaDock.cs, 1 Hz) and the DisplayPad live tiles (DpLiveTileService,
    // up to 1 Hz per key). The delta-based metrics — CPU and the two network rates —
    // CONSUME their baseline on every call: two callers a few milliseconds apart would
    // leave the second one dividing by a near-zero interval and reading nonsense (a CPU
    // key showing 0% or 100% at random while the dock read fine). So every getter goes
    // through this cache: the first caller in a MinSampleMs window takes a real sample,
    // everyone else gets the same value back. It also keeps the PDH/COM work down to
    // one pass per second no matter how many keys are on screen.

    private const int MinSampleMs = 700;

    private static readonly object _cacheGate = new();
    private static readonly Dictionary<string, (DateTime At, int Value)> _cache = new();

    /// <summary>Returns <paramref name="sample"/>'s value, re-sampling at most once every
    /// <see cref="MinSampleMs"/> per metric key.</summary>
    private static int Cached(string key, Func<int> sample)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var hit)
                && (DateTime.UtcNow - hit.At).TotalMilliseconds < MinSampleMs)
                return hit.Value;

            int value = sample();
            _cache[key] = (DateTime.UtcNow, value);
            return value;
        }
    }

    // ─────────────────────────── CPU ───────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static long _prevIdle, _prevKernel, _prevUser;

    /// <summary>Total CPU load, 0..100. Derived from GetSystemTimes deltas between
    /// calls (kernel time already includes idle time), so the first call after
    /// startup has no baseline and reports 0.</summary>
    public static int CpuPercent() => Cached("cpu", CpuPercentSample);

    private static int CpuPercentSample()
    {
        try
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
            long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            long total = dKernel + dUser;
            if (total <= 0) return 0;
            return Clamp((int)Math.Ceiling((total - dIdle) * 100.0 / total));
        }
        catch { return 0; }
    }

    // ─────────────────────────── RAM ───────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>Physical memory in use, 0..100 — <c>dwMemoryLoad</c> is exactly the
    /// (total-free)/total ratio Base Camp computes from Win32_OperatingSystem, without
    /// the WMI query.</summary>
    public static int RamPercent() => Cached("ram", RamPercentSample);

    private static int RamPercentSample()
    {
        try
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref st) ? Clamp((int)st.dwMemoryLoad) : 0;
        }
        catch { return 0; }
    }

    // ─────────────────────── Network download ───────────────────────

    private static long _prevRxBytes, _prevTxBytes;
    private static DateTime _prevRxAt, _prevTxAt;

    /// <summary>Download speed in whole MB/s across all interfaces that are up — the unit
    /// Base Camp sends (LHM's bytes/s "Download Speed" divided by 1e6 and rounded).
    /// Loopback and tunnels are excluded, as in any real throughput reading.</summary>
    public static int DownloadMbPerSec() => (int)Math.Round(DownloadBytesPerSec() / 1_000_000.0);

    /// <summary>Upload counterpart of <see cref="DownloadMbPerSec"/> — same interfaces, same
    /// unit. Base Camp never sends this one to the dock (its PC Info pages have no upload
    /// page); it exists for the DisplayPad "net_up" tile.</summary>
    public static int UploadMbPerSec() => (int)Math.Round(UploadBytesPerSec() / 1_000_000.0);

    /// <summary>Raw throughput in bytes/s — what the DisplayPad tiles format themselves
    /// (a key showing "0 MB/s" for anything under half a megabyte reads as broken, so they
    /// scale the unit down to KB/s instead of rounding to whole MB like the dock page).</summary>
    public static int DownloadBytesPerSec() => Cached("net_down", () => NetBytesPerSecSample(upload: false));

    /// <inheritdoc cref="DownloadBytesPerSec"/>
    public static int UploadBytesPerSec() => Cached("net_up", () => NetBytesPerSecSample(upload: true));

    private static int NetBytesPerSecSample(bool upload)
    {
        try
        {
            long total = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var stats = ni.GetIPv4Statistics();
                total += upload ? stats.BytesSent : stats.BytesReceived;
            }
            var now = DateTime.UtcNow;
            long prev = upload ? _prevTxBytes : _prevRxBytes;
            var prevAt = upload ? _prevTxAt : _prevRxAt;
            if (upload) { _prevTxBytes = total; _prevTxAt = now; }
            else        { _prevRxBytes = total; _prevRxAt = now; }
            if (prevAt == default) return 0;             // no baseline yet
            double secs = (now - prevAt).TotalSeconds;
            if (secs <= 0 || total < prev) return 0;     // clock jump / counter reset
            return Clamp((int)Math.Round((total - prev) / secs), 0, int.MaxValue);
        }
        catch { return 0; }
    }

    // ─────────────────────────── PDH ───────────────────────────
    //
    // Disk and GPU come from performance counters. PDH lives in pdh.dll and ships with
    // every supported Windows, so this needs no NuGet package.

    private const uint PDH_FMT_DOUBLE   = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const int  PDH_MORE_DATA    = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath,
                                                    IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format,
                                                          out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
                                                           ref uint bufferSize, out uint itemCount,
                                                           IntPtr itemBuffer);

    // PDH_FMT_COUNTERVALUE = { DWORD CStatus; union { ... double doubleValue; ... }; }
    // The union is 8-byte aligned (a double is its widest member), so the value sits at
    // offset 8 and the struct is 16 bytes on x86 as well as x64 — hence explicit offsets
    // rather than a sequential layout, which would place it at 4 on x86.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public int CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    // PDH_FMT_COUNTERVALUE_ITEM = { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; }
    // Same alignment story: the embedded value starts at 8 even on x86, where the name
    // pointer is only 4 bytes wide.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        [FieldOffset(0)] public IntPtr szName;
        [FieldOffset(8)] public PDH_FMT_COUNTERVALUE FmtValue;
    }

    /// <summary>One PDH query + counter pair, opened lazily and never reopened after a
    /// failure (a counter set that isn't there stays missing for the process's life).</summary>
    private sealed class PdhCounter
    {
        private readonly string _path;
        private IntPtr _query, _counter;
        private bool _tried, _ok;

        public PdhCounter(string path) => _path = path;

        public bool Ready
        {
            get
            {
                if (_tried) return _ok;
                _tried = true;
                try
                {
                    if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return false;
                    if (PdhAddEnglishCounterW(_query, _path, IntPtr.Zero, out _counter) != 0) return false;
                    PdhCollectQueryData(_query);   // rate counters need a first sample
                    _ok = true;
                }
                catch { _ok = false; }
                return _ok;
            }
        }

        public IntPtr Handle => _counter;

        public bool Collect() => Ready && PdhCollectQueryData(_query) == 0;
    }

    private static readonly PdhCounter _diskCounter = new(@"\PhysicalDisk(_Total)\% Disk Time");

    /// <summary>Disk activity, 0..100. "% Disk Time" can read above 100 on a busy
    /// multi-queue disk (Base Camp doesn't clamp it either), but the dock draws a
    /// percentage, so it is capped here.</summary>
    public static int DiskPercent() => Cached("disk", DiskPercentSample);

    private static int DiskPercentSample()
    {
        try
        {
            if (!_diskCounter.Collect()) return 0;
            if (PdhGetFormattedCounterValue(_diskCounter.Handle, PDH_FMT_DOUBLE, out _, out var v) != 0)
                return 0;
            return Clamp((int)Math.Ceiling(v.doubleValue));
        }
        catch { return 0; }
    }

    // Windows exposes GPU load per engine instance, e.g.
    //   pid_1234_luid_0x00000000_0x0000C7B4_phys_0_eng_0_engtype_3D
    // There is no _Total instance, so this is a wildcard counter whose 3D-engine
    // instances get summed — the closest equivalent to the LibreHardwareMonitor
    // "GPU Core" load that Base Camp reads.
    private static readonly PdhCounter _gpuCounter = new(@"\GPU Engine(*)\Utilization Percentage");

    /// <summary>GPU 3D-engine load, 0..100 (summed across processes).</summary>
    public static int GpuPercent() => Cached("gpu", GpuPercentSample);

    private static int GpuPercentSample()
    {
        try
        {
            if (!_gpuCounter.Collect()) return 0;

            uint size = 0;
            int rc = PdhGetFormattedCounterArrayW(_gpuCounter.Handle,
                PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return 0;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArrayW(_gpuCounter.Handle,
                    PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buf);
                if (rc != 0) return 0;

                double total = 0;
                int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * stride);
                    if (item.FmtValue.CStatus != 0) continue;
                    string name = Marshal.PtrToStringUni(item.szName) ?? "";
                    if (!name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    total += item.FmtValue.doubleValue;
                }
                return Clamp((int)Math.Round(total));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return 0; }
    }

    // ────────────────────────── Volume ──────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    // Only the slots up to GetMasterVolumeLevelScalar are used, but every earlier vtable
    // entry still has to be declared, in order, for the offsets to line up.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
        int UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float leveldB, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float leveldB);
        int GetMasterVolumeLevelScalar(out float level);
    }

    // Core Audio's change-notification sink. The audio service calls OnNotify on its own
    // thread whenever the endpoint's master volume/mute changes — from ANY source (the
    // Windows tray slider, a media key, another app, the Everest volume roller). The
    // notification struct is ignored here: consumers just re-read VolumePercent().
    // No [ComImport]: this interface is implemented by a managed class (VolumeCallback) and
    // handed to Core Audio as a COM-callable wrapper. [Guid] + InterfaceIsIUnknown give the
    // CCW the exact IUnknown+OnNotify vtable RegisterControlChangeNotify expects.
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotifyData);
    }

    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr pNotifyData)
        {
            try { VolumeChanged?.Invoke(); } catch { /* a dead subscriber must not break the sink */ }
            return 0;   // S_OK
        }
    }

    private const int ERender = 0, EMultimedia = 1, ClsCtxAll = 23;

    // ── Volume change notifications ──
    //
    // Raised (on a Core Audio worker thread — marshal before touching UI) whenever the
    // default render endpoint's volume changes. Lets the Media Dock push the new value to
    // the keyboard immediately instead of waiting for its 1 Hz poll, and lets it treat a
    // Windows-side volume change as dock activity (see MainWindow.MediaDock.cs).
    //
    // Bound to whatever endpoint was default at StartVolumeNotifications() time; a later
    // default-device switch silently stops the events until restart, which the poll still
    // covers. Keeping the endpoint COM object referenced is what keeps the registration
    // alive, so it is held here, not released like VolumePercentSample's throwaway one.
    public static event Action? VolumeChanged;

    private static readonly object _volNotifyGate = new();
    private static IAudioEndpointVolume? _volNotifyEndpoint;
    private static VolumeCallback? _volNotifyCallback;

    /// <summary>Registers a Core Audio volume-change callback on the current default
    /// render endpoint. Idempotent; best-effort (logs nothing, swallows failure — the
    /// 1 Hz dock poll is the fallback).</summary>
    public static void StartVolumeNotifications()
    {
        lock (_volNotifyGate)
        {
            if (_volNotifyEndpoint is not null) return;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0 || device is null)
                    return;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volObj) != 0) return;
                if (volObj is not IAudioEndpointVolume vol) return;

                var cb = new VolumeCallback();
                if (vol.RegisterControlChangeNotify(cb) != 0)
                {
                    if (Marshal.IsComObject(vol)) Marshal.ReleaseComObject(vol);
                    return;
                }
                _volNotifyEndpoint = vol;
                _volNotifyCallback = cb;
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Unregisters the callback and releases the held endpoint. Safe to call
    /// when never started.</summary>
    public static void StopVolumeNotifications()
    {
        lock (_volNotifyGate)
        {
            try
            {
                if (_volNotifyEndpoint is not null && _volNotifyCallback is not null)
                    _volNotifyEndpoint.UnregisterControlChangeNotify(_volNotifyCallback);
            }
            catch { /* ignore */ }
            finally
            {
                try
                {
                    if (_volNotifyEndpoint is not null && Marshal.IsComObject(_volNotifyEndpoint))
                        Marshal.ReleaseComObject(_volNotifyEndpoint);
                }
                catch { /* ignore */ }
                _volNotifyEndpoint = null;
                _volNotifyCallback = null;
            }
        }
    }

    /// <summary>Master output volume, 0..100 — the value Base Camp's PcInfo_timer sends
    /// with SetVolumeInfo while the dock shows its Volume page.</summary>
    public static int VolumePercent() => Cached("volume", VolumePercentSample);

    private static int VolumePercentSample()
    {
        object? volObj = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0 || device is null)
                return 0;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out volObj) != 0) return 0;
            if (volObj is not IAudioEndpointVolume vol) return 0;
            if (vol.GetMasterVolumeLevelScalar(out float level) != 0) return 0;
            return Clamp((int)Math.Round(level * 100f));
        }
        catch { return 0; }
        finally
        {
            if (volObj is not null && Marshal.IsComObject(volObj))
                Marshal.ReleaseComObject(volObj);
        }
    }

    private static int Clamp(int v, int min = 0, int max = 100)
        => v < min ? min : v > max ? max : v;
}
