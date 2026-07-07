using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Native USB-HID DisplayPad backend — same surface as <see cref="DisplayPadSatelliteClient"/>
/// but talks to the hardware directly through <see cref="DpHidNative"/> (no SDK DLL, no
/// satellite process). Differences vs. the SDK path:
/// <list type="bullet">
/// <item>Device IDs are assigned 1..N ordered by the (stable) USB parent instance ID, so
///   they may differ from the SDK's numbering on multi-pad setups.</item>
/// <item><see cref="UploadImageToProfile"/> performs a LIVE upload only — the raw protocol
///   has no known firmware-profile persistence command. K2 re-uploads from its DB on
///   startup/switch, so on-screen behavior is unchanged while the host is running.</item>
/// <item><see cref="SwitchProfile"/>/<see cref="APEnable"/> are no-ops (BC never uses the
///   firmware profile switch for the DisplayPad either; profiles are host-side).</item>
/// <item><see cref="GetBrightness"/> returns the last value set this session (no known
///   read-back command); -1 before the first set.</item>
/// </list>
/// Icon uploads are handshake-confirmed by the device (READY/DONE responses) instead of
/// relying on fixed settle delays, and rotation happens fully in memory — both classes of
/// the historical icon-corruption bug (overlapping transfers, torn cache files) are gone
/// by construction.
/// </summary>
public sealed class DisplayPadNativeClient : IDisplayPadClient
{
    private readonly object _lock = new();
    private readonly Dictionary<int, DpHidNative.Pad> _pads = new();          // id → open pad
    private readonly Dictionary<int, string> _groupKeys = new();              // id → group key
    private readonly Dictionary<int, int> _brightness = new();                // id → last set
    private Timer? _pollTimer;
    private bool _connected;
    private int _pollBusy;   // 1 while a refresh is running (init/upload can take seconds)
    private bool _enumLogged; // HID enumeration details logged once (or always at Verbose)

    // SDK keyMatrix codes for K1..K12 — same values DisplayPadSDK.dll reports, so the
    // existing mapping/actions in MainWindow.DisplayPad.cs work unchanged.
    private static readonly int[] MatrixByIndex =
        { 0x08, 0x11, 0x1A, 0x23, 0x2C, 0x35, 0x3E, 0x47, 0x50, 0x59, 0x62, 0x7D };

    private static readonly JsonSerializerOptions JOpts = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public event EventHandler<JsonElement>? PlugEvent;
    public event EventHandler<JsonElement>? KeyEvent;
    // Interface compatibility only: the raw protocol has no upload-progress callback
    // (transfers are short and handshake-confirmed), so this event is never raised.
#pragma warning disable CS0067
    public event EventHandler<JsonElement>? ProgressEvent;
#pragma warning restore CS0067
    public event EventHandler<string>? SatelliteLog;

    public bool IsConnected => _connected;

    // ================================================================
    // Lifecycle
    // ================================================================

    /// <summary>
    /// Non-blocking: device discovery and the INIT handshake run on the thread pool,
    /// never on the caller's (UI) thread — USB I/O at startup must not delay
    /// AutoOpenDrivers. Pads announce themselves via <see cref="PlugEvent"/> once
    /// initialized (MainWindow already refreshes its device tabs on that event).
    /// </summary>
    public bool Connect(int timeoutMs = 8000)
    {
        if (_connected) return true;
        Log("[DpNative] engine starting (raw USB-HID, no SDK)");
        if (AppSettings.KillBaseCampWorker)
            BaseCampProcessGuard.KillDisplayPadWorkers(Log);
        WarnIfBaseCampRunning();
        _connected = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            SafePoll();
            if (_connected && _pollTimer is null)
                _pollTimer = new Timer(__ => SafePoll(), null, 2000, 2000);
        });
        return true;
    }

    public void Disconnect()
    {
        _connected = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        lock (_lock)
        {
            foreach (var p in _pads.Values) p.Dispose();
            _pads.Clear();
            _groupKeys.Clear();
        }
    }

    public void Dispose() => Disconnect();

    public JsonElement? Open()
    {
        if (!_connected) Connect();
        return JsonSerializer.SerializeToElement(new { ok = true, native = true }, JOpts);
    }

    public JsonElement? Close()
    {
        Disconnect();
        return JsonSerializer.SerializeToElement(new { ok = true }, JOpts);
    }

    // ================================================================
    // Device discovery / hotplug
    // ================================================================

    private void SafePoll()
    {
        // Skip overlapping polls: a refresh can take seconds (INIT retries) and the
        // timer would otherwise re-enter while the previous one is still opening pads.
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        // Standing guard: Base Camp can respawn its DisplayPad worker at any time
        // (e.g. the user opens the BC GUI) — re-kill it as soon as it reappears.
        try { if (AppSettings.KillBaseCampWorker) BaseCampProcessGuard.KillDisplayPadWorkers(Log); }
        catch { }
        try { RefreshPads(raiseEvents: true); }
        catch (Exception ex) { Log($"[DpNative] poll error: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _pollBusy, 0); }
    }

    private void RefreshPads(bool raiseEvents)
    {
        // Sorted by GroupKey → stable IDs. Enumeration lines land in the DisplayPad
        // event log (via SatelliteLog) so report lengths can be verified on hardware —
        // but only on the first pass (or at Verbose), not on every 2s poll.
        bool logEnum = !_enumLogged || AppSettings.LogLevel == K2LogLevel.Verbose;
        var found = DpHidNative.Enumerate(logEnum ? Log : null);
        _enumLogged = true;
        lock (_lock)
        {
            var foundKeys = found.Select(f => f.GroupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Removals (an ID may be reserved but not opened yet — see OpenPad)
            foreach (var id in _groupKeys.Where(kv => !foundKeys.Contains(kv.Value))
                                         .Select(kv => kv.Key).ToList())
            {
                if (_pads.TryGetValue(id, out var pad))
                {
                    Log($"[DpNative] pad #{id} unplugged");
                    pad.Dispose();
                    _pads.Remove(id);
                    if (raiseEvents) RaisePlug();
                }
                _groupKeys.Remove(id);
            }

            // Additions — assign the lowest free ID (found list is sorted, so IDs are
            // deterministic), then open each pad on its OWN thread-pool task: a pad
            // that needs the INIT/flush recovery path (~10 s) must not delay the
            // others nor the whole refresh cycle. The ID is reserved up front; on
            // failure the reservation is released so the next 2 s poll retries.
            foreach (var f in found)
            {
                if (_groupKeys.ContainsValue(f.GroupKey)) continue;
                int id = Enumerable.Range(1, 16).First(i => !_groupKeys.ContainsKey(i));
                _groupKeys[id] = f.GroupKey;   // reserve (not in _pads yet → not visible)
                var info = f;
                ThreadPool.QueueUserWorkItem(_ => OpenPad(id, info, raiseEvents));
            }
        }
    }

    private void OpenPad(int id, DpHidNative.PadInterfaces info, bool raiseEvents)
    {
        var pad = new DpHidNative.Pad(info, Log);
        try
        {
            pad.Open();
        }
        catch (Exception ex)
        {
            Log($"[DpNative] open failed for {info.GroupKey}: {ex.Message} (will retry)");
            pad.Dispose();
            lock (_lock)
            {
                // Release the reservation only if it still points at this device.
                if (_groupKeys.TryGetValue(id, out var k) && k == info.GroupKey && !_pads.ContainsKey(id))
                    _groupKeys.Remove(id);
            }
            return;
        }
        lock (_lock)
        {
            if (!_connected)   // disconnected while we were opening
            {
                pad.Dispose();
                return;
            }
            pad.KeyChanged += (k, pressed) => OnPadKey(id, k, pressed);
            _pads[id] = pad;
        }
        Log($"[DpNative] pad #{id} = {info.GroupKey}");
        if (raiseEvents) RaisePlug();
    }

    private void OnPadKey(int deviceId, int keyIndex, bool pressed)
    {
        if (keyIndex < 0 || keyIndex >= MatrixByIndex.Length) return;
        KeyEvent?.Invoke(this, JsonSerializer.SerializeToElement(
            new { deviceId, keyMatrix = MatrixByIndex[keyIndex], pressed }, JOpts));
    }

    private void RaisePlug() =>
        PlugEvent?.Invoke(this, JsonSerializer.SerializeToElement(new { arg0 = 0, arg1 = 0 }, JOpts));

    private void Log(string msg) => SatelliteLog?.Invoke(this, msg);

    /// <summary>
    /// HID collections allow MULTIPLE concurrent writers: if Base Camp (or its
    /// DisplayPadWorker, which autostarts with Windows and keeps running in the
    /// background reacting to key events and pushing PC-info/clock icon updates)
    /// is alive while the native engine uploads, the two pixel streams interleave
    /// on the display endpoint and icons corrupt randomly. Detect and warn loudly.
    /// </summary>
    private void WarnIfBaseCampRunning()
    {
        try
        {
            var offenders = System.Diagnostics.Process.GetProcesses()
                .Where(p =>
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    return n.Contains("basecamp") || n.Contains("base camp") ||
                           (n.Contains("mountain") && !n.Contains("k2")) ||
                           n.Contains("displaypadworker");
                })
                .Select(p => $"{p.ProcessName} (pid {p.Id})")
                .ToList();
            if (offenders.Count > 0)
                Log("[DpNative] *** WARNING: Base Camp processes are running and WILL corrupt " +
                    "native uploads (concurrent HID writers): " + string.Join(", ", offenders) +
                    " — close Base Camp completely (including the tray icon / worker) ***");
        }
        catch { /* best-effort diagnostics */ }
    }

    // ================================================================
    // Queries
    // ================================================================

    public int SdkVersion() => 0;   // native engine — no SDK DLL involved

    public List<int> DeviceIds()
    {
        lock (_lock) return _pads.Keys.OrderBy(k => k).ToList();
    }

    public bool IsPlugged(int id) { lock (_lock) return _pads.ContainsKey(id); }

    public string FirmwareVersion(int id) => "native";

    public int GetBrightness(int id)
    {
        lock (_lock) return _brightness.TryGetValue(id, out int b) ? b : -1;
    }

    public bool SetBrightness(int id, int level)
    {
        DpHidNative.Pad? pad;
        lock (_lock) _pads.TryGetValue(id, out pad);
        if (pad is null) return false;
        bool ok = pad.SetBrightness(level);
        if (ok) lock (_lock) _brightness[id] = level;
        return ok;
    }

    // No-ops: profiles are host-side concepts for the DisplayPad (BC never calls the
    // firmware SwitchProfile either) and AP mode is an SDK-internal notion.
    public bool SwitchProfile(int id, int profile) => true;
    public bool APEnable(int id, bool enable) => true;

    public bool Ping() => _connected;

    // ================================================================
    // Icon uploads
    // ================================================================

    /// <summary>
    /// Full repaint preamble, exactly as Base Camp does on every profile change
    /// (verified via USB sniff): re-INIT the command interface, then upload a black
    /// full-panel image (SetPanelImage/UploadLogo equivalent). This resets whatever
    /// transition state the firmware is in (e.g. after a physical next/prev-profile
    /// key press it repaints from flash on its own) before K2 re-uploads the icons —
    /// uploading icons without this preamble races the firmware's own repaint and
    /// produces the random persistent icon corruption. BC closes the sequence with a
    /// brightness write, so the last set value is re-sent here too.
    /// </summary>
    public bool ResetPictures(int id)
    {
        DpHidNative.Pad? pad;
        int brightness;
        lock (_lock)
        {
            _pads.TryGetValue(id, out pad);
            _brightness.TryGetValue(id, out brightness);
        }
        if (pad is null) return false;
        try
        {
            pad.Reinit();
            bool ok = pad.UploadPanel(null);
            if (brightness > 0) pad.SetBrightness(brightness);
            return ok;
        }
        catch (Exception ex) { Log($"[DpNative] panel blank failed: {ex.Message}"); return false; }
    }

    public bool UploadImage(int id, string path, int btn, int rotation = 0, bool pressed = false)
    {
        if (btn is < 0 or > 11) return false;
        DpHidNative.Pad? pad;
        lock (_lock) _pads.TryGetValue(id, out pad);
        if (pad is null) return false;
        try
        {
            byte[] bgr = LoadBgr(path, rotation, pressed);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = pad.UploadIcon(btn, bgr);
            if (AppSettings.LogLevel == K2LogLevel.Verbose)
                Log($"[DpNative] upload dev={id} btn={btn} pressed={pressed} ms={sw.ElapsedMilliseconds} ok={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log($"[DpNative] upload dev={id} btn={btn} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Live upload only — see class remarks. The raw protocol has no known
    /// command to persist an icon into a firmware profile slot.</summary>
    public bool UploadImageToProfile(int id, string path, int btn, int profile, int rotation = 0)
        => UploadImage(id, path, btn, rotation);

    /// <summary>
    /// Animation fast path: the caller (DpGifAnimator/DpFullscreenAnimator) already
    /// decoded, sized and device-rotated the buffer once at cache-build time — skip
    /// straight to the wire, no GDI+ involved at all on this call.
    /// </summary>
    public bool TryUploadRawBgr(int id, byte[] bgr, int btn)
    {
        if (btn is < 0 or > 11) return false;
        DpHidNative.Pad? pad;
        lock (_lock) _pads.TryGetValue(id, out pad);
        if (pad is null) return false;
        try { return pad.UploadIcon(btn, bgr); }
        catch (Exception ex) { Log($"[DpNative] raw upload dev={id} btn={btn} failed: {ex.Message}"); return false; }
    }

    public bool SupportsRawPanel => true;

    /// <summary>Single-transfer full-panel upload — see DpFullscreenAnimator.BuildPanelBgr
    /// for how the 800×240 buffer is composed/rotated before reaching this call.</summary>
    public bool TryUploadRawPanel(int id, byte[] bgr)
    {
        DpHidNative.Pad? pad;
        lock (_lock) _pads.TryGetValue(id, out pad);
        if (pad is null) return false;
        try { return pad.UploadPanel(bgr); }
        catch (Exception ex) { Log($"[DpNative] raw panel upload dev={id} failed: {ex.Message}"); return false; }
    }

    // ================================================================
    // Image conversion (in-memory: resize → counter-rotate → BGR)
    // ================================================================

    /// <summary>
    /// Loads an image and converts it to the device's 102×102 BGR format.
    /// <paramref name="deviceRotation"/> is the panel's physical rotation (0/90/270):
    /// the image is counter-rotated the same way the satellite's ResolveForUpload did
    /// (device 90° → rotate image 270°, device 270° → rotate 90°) — but entirely in
    /// memory, so there is no rotation-cache file to race on.
    /// <paramref name="pressed"/> reproduces BC's hardware press-bounce: the icon is drawn
    /// at 80×80 centered on an otherwise-black 102×102 canvas (11px margin all around,
    /// same as <c>DisplayPadOperations.UploadImage</c>'s <c>IsBtnPressed</c> branch) instead
    /// of filling the whole tile.
    /// </summary>
    private static byte[] LoadBgr(string path, int deviceRotation, bool pressed = false)
    {
        byte[] fileBytes = File.ReadAllBytes(path);   // avoid GDI+ file locks
        using var ms = new MemoryStream(fileBytes);
        using var src = new Bitmap(ms);
        using var bmp = new Bitmap(DpHidNative.IconSize, DpHidNative.IconSize,
                                   PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (pressed)
            {
                const int inner = 80;
                int off = (DpHidNative.IconSize - inner) / 2;
                g.Clear(Color.Black);
                g.DrawImage(src, off, off, inner, inner);
            }
            else
            {
                g.DrawImage(src, 0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
            }
        }
        switch (deviceRotation)
        {
            case 90: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            case 180: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 270: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        }

        var rect = new Rectangle(0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = DpHidNative.IconSize * 3;          // 306
            var bgr = new byte[DpHidNative.IconBytes];
            for (int y = 0; y < DpHidNative.IconSize; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, bgr, y * rowBytes, rowBytes);
            return bgr;   // GDI+ 24bpp memory layout is already B,G,R
        }
        finally { bmp.UnlockBits(data); }
    }
}
