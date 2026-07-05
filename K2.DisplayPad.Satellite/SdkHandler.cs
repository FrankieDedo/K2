using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using DisplayPad.SDK;

namespace K2.DisplayPad.Satellite;

/// <summary>
/// Handles the JSON commands coming from the pipe and drives the DisplayPad SDK.
/// SDK events (plug, key, progress) are relayed as JSON via
/// <see cref="EventRaised"/>.
/// </summary>
internal sealed class SdkHandler : IDisposable
{
    private readonly DisplayPadHelper _helper = new();
    private readonly Dispatcher _dispatcher;
    private bool _opened;

    private Delegate? _plugHandler;
    private Delegate? _keyHandler;
    private Delegate? _progressHandler;

    private static readonly JsonSerializerOptions JOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>JSON push event: the string is a complete JSON object (one line).</summary>
    public event EventHandler<string>? EventRaised;

    public SdkHandler(Dispatcher dispatcher) => _dispatcher = dispatcher;

    // ================================================================
    // Command dispatch
    // ================================================================

    public object Handle(string cmd, JsonElement root)
    {
        try
        {
            return cmd switch
            {
                "open"              => CmdOpen(root),
                "close"             => CmdClose(),
                "sdkVersion"        => new { ok = true, version = _helper.DisplayPadDllVersion() },
                "deviceIds"         => CmdDeviceIds(),
                "isPlugged"         => new { ok = true, plugged = _helper.DisplayPadIsDevicePlug(Int(root, "deviceId")) },
                "firmwareVersion"   => new { ok = true, fw = _helper.DisplayPadGetDevAppVer(Int(root, "deviceId")) ?? "" },
                "getBrightness"     => new { ok = true, brightness = _helper.DisplayPadGetMainBrightness(Int(root, "deviceId")) },
                "setBrightness"     => new { ok = true, result = _helper.DisplayPadSetMainBrightness(Int(root, "level"), Int(root, "deviceId")) },
                "switchProfile"     => CmdSwitchProfile(root),
                "apEnable"          => CmdApEnable(root),
                "resetPictures"     => CmdResetPictures(root),
                "uploadImage"       => CmdUploadImage(root),
                "uploadImageToProfile" => CmdUploadImageToProfile(root),
                "ping"              => new { ok = true, pong = true },
                "exit"              => new { ok = true },
                _                   => new { ok = false, error = $"unknown cmd: {cmd}" },
            };
        }
        catch (Exception ex)
        {
            Program.Log($"[Handle] cmd={cmd} -> {ex.Message}");
            return new { ok = false, error = ex.Message };
        }
    }

    // ================================================================
    // Specific commands
    // ================================================================

    private object CmdOpen(JsonElement root)
    {
        if (_opened) return new { ok = true, already = true };

        // The SDK needs an HWND to post WM_DEVICE_PLUG.
        // We create a hidden window on the WPF thread.
        IntPtr hwnd = IntPtr.Zero;
        _dispatcher.Invoke(() =>
        {
            var win = new System.Windows.Window
            {
                Width = 0, Height = 0,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            win.Show();
            win.Hide();
            hwnd = new WindowInteropHelper(win).Handle;
        });

        AttachStaticEvent("DisplayPadPlugCallBack",     nameof(OnPlug),     out _plugHandler);
        AttachStaticEvent("DisplayPadKeyCallBack",      nameof(OnKey),      out _keyHandler);
        AttachStaticEvent("DisplayPadProgressCallBack", nameof(OnProgress), out _progressHandler);

        var hStr = hwnd.ToInt64().ToString();
        Program.Log($"[Open] DisplayPadOpenUSBDriver(\"{hStr}\")");
        bool ok = _helper.DisplayPadOpenUSBDriver(hStr);
        Program.Log($"[Open] -> {ok}");
        _opened = ok;
        return new { ok };
    }

    private object CmdClose()
    {
        if (!_opened) return new { ok = true };
        try { _helper.DisplayPadCloseUSBDriver(); } catch { /* swallow */ }
        _opened = false;
        DetachStaticEvent("DisplayPadPlugCallBack",     _plugHandler);
        DetachStaticEvent("DisplayPadKeyCallBack",      _keyHandler);
        DetachStaticEvent("DisplayPadProgressCallBack", _progressHandler);
        _plugHandler = _keyHandler = _progressHandler = null;
        return new { ok = true };
    }

    private object CmdResetPictures(JsonElement root)
    {
        int id = Int(root, "deviceId");
        bool ok;
        lock (_sdkLock)
        {
            ok = _helper.DisplayPadResetPicture(id);
        }
        return new { ok = true, result = ok };
    }

    private static readonly HashSet<int> ValidBrightness = new() { 0, 25, 50, 75, 100 };

    private object CmdDeviceIds()
    {
        // Primary: use the SDK's own lstDeviceID field (authoritative list of connected devices).
        // Only trust it if non-empty — it may be empty immediately after Open() before the SDK
        // has had time to populate it.
        var fromSdk = TryGetSdkDeviceList();
        if (fromSdk is { Count: > 0 })
            return new { ok = true, ids = fromSdk };

        // Fallback: iterate slots and use DisplayPadGetDeviceInfo as the phantom discriminant.
        // IsDevicePlug and GetDevAppVer are unreliable (return true/non-empty for phantom slots).
        // GetDeviceInfo returns a DevInfo struct: phantom slots should have VID == 0.
        var found = new List<int>();
        for (int id = 1; id <= 10; id++)
        {
            try
            {
                if (!_helper.DisplayPadIsDevicePlug(id)) continue;
                // DisplayPadGetDeviceInfo returns a DevInfo struct (not bool).
                // Check the VID field via reflection — phantom slots have VID == 0.
                object devInfo = _helper.DisplayPadGetDeviceInfo(id);
                if (devInfo is null) continue;
                var vidField = devInfo.GetType().GetField("vid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? devInfo.GetType().GetField("VID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (vidField is not null)
                {
                    int vid = Convert.ToInt32(vidField.GetValue(devInfo));
                    if (vid == 0) continue;  // phantom slot
                }
                found.Add(id);
            }
            catch { continue; }
        }
        return new { ok = true, ids = found };
    }

    /// <summary>
    /// Reads <c>DisplayPadSDK.lstDeviceID</c> via reflection — the SDK's own authoritative
    /// list of connected device IDs. Returns null if unavailable (falls back to polling).
    /// </summary>
    private static List<int>? TryGetSdkDeviceList()
    {
        try
        {
            var sdkType = typeof(DisplayPadHelper).Assembly
                          .GetType("DisplayPad.SDK.DisplayPadSDK");
            if (sdkType is null) return null;

            var listField = sdkType.GetField("lstDeviceID",
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance);
            if (listField is null) return null;

            object? listValue;
            if (listField.IsStatic)
            {
                listValue = listField.GetValue(null);
            }
            else
            {
                // Locate the SDK singleton (field named "DisplayPad_instance" or similar)
                var instField = sdkType.GetField("DisplayPad_instance",
                                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? sdkType.GetField("_instance",
                                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? sdkType.GetField("instance",
                                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var inst = instField?.GetValue(null);
                if (inst is null) return null;
                listValue = listField.GetValue(inst);
            }

            if (listValue is not System.Collections.IEnumerable rawList) return null;

            var result = new List<int>();
            foreach (var item in rawList)
            {
                if (item is null) continue;
                try { result.Add(Convert.ToInt32(item)); } catch { }
            }
            return result; // may be empty — caller should treat empty as "SDK not ready yet"
        }
        catch { return null; }
    }

    /// <summary>
    /// Switches the firmware profile, then waits briefly before returning.
    /// The firmware needs time to complete its own profile-load transition (flash
    /// read + internal icon-buffer swap). K2 immediately follows a profile switch
    /// with a burst of uploadImage/uploadImageToProfile calls (root page + folder
    /// sub-pages); sending those while the firmware is still mid-transition races
    /// with its own buffer swap and corrupts the on-screen icons. Same category of
    /// fix as the retry/backoff already used in <see cref="CmdApEnable"/>.
    /// </summary>
    private object CmdSwitchProfile(JsonElement root)
    {
        int id = Int(root, "deviceId");
        int profile = Int(root, "profile");
        bool ok = _helper.DisplayPadSwitchProfile(profile.ToString(), id);
        if (ok) Thread.Sleep(300);
        return new { ok = true, result = ok };
    }

    private object CmdApEnable(JsonElement root)
    {
        int id = Int(root, "deviceId");
        bool enable = root.GetProperty("enable").GetBoolean();
        for (int attempt = 0; attempt <= 10; attempt++)
        {
            try
            {
                if (_helper.DisplayPadAPEnable(enable ? "1" : "0", id))
                    return new { ok = true, result = true };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = ex.Message };
            }
            if (attempt < 10) Thread.Sleep(150);
        }
        return new { ok = true, result = false };
    }

    /// <summary>
    /// Serializes every icon transfer, plus a short settle delay after each one.
    /// Reference (decompiled BaseCamp, DisplayPadOperations.UploadImage) always sends
    /// icon packets inside a lock (`_objlockTask`) around the raw SetIconPacket call,
    /// and never issues two image transfers back-to-back without one finishing first.
    /// K2 previously called the higher-level UploadImage/UploadImageBySetIconPic
    /// wrapper repeatedly with no serialization or settle time between calls — a burst
    /// of these (e.g. on profile switch, one call per button) can have transfers
    /// overlap on the wire, corrupting icons. This lock + delay replicates BC's
    /// discipline without needing the raw packet API.
    /// </summary>
    private static readonly object _sdkLock = new();

    /// <summary>
    /// Settle delay after each icon transfer. Kept at 400ms even after switching to the native
    /// SetIconPacket path below — the wrapper turned out to be the real suspect (corruption
    /// persisted through 100ms and 400ms alike while still going through UploadImage/
    /// UploadImageBySetIconPic), but a small settle margin after a raw 31-packet USB transfer is
    /// still cheap insurance and matches BC's own delays elsewhere.
    /// </summary>
    private const int IconSettleDelayMs = 400;

    private object CmdUploadImage(JsonElement root)
    {
        int id = Int(root, "deviceId");
        int btn = Int(root, "buttonIndex");
        string path = Str(root, "imagePath");
        int rotation = OptInt(root, "rotation", 0);
        bool ok;
        // ResolveForUpload (rotation + cache file write) must be INSIDE the lock too: it used to
        // run before acquiring it, so two concurrent uploads of the same source image (e.g. two
        // DisplayPads reloading at once, or an overlapping profile-switch + single-key edit) could
        // both miss the cache, rotate the same file, and write the same cache path at the same
        // time — a torn/partial PNG on disk that then gets uploaded as a corrupted icon.
        lock (_sdkLock)
        {
            string resolved = ResolveForUpload(path, rotation);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // NativeIconUploader talks to SetIconPacket directly (same native call BC itself
            // uses), bypassing DisplayPadHelper.UploadImage — see NativeIconUploader.cs for why.
            ok = NativeIconUploader.Upload(resolved, btn, (uint)id);
            sw.Stop();
            Program.Log($"[UploadImage/native] dev={id} btn={btn} nativeCallMs={sw.ElapsedMilliseconds} ok={ok} path={resolved}");
            Thread.Sleep(IconSettleDelayMs);
        }
        return new { ok, result = ok };
    }

    private object CmdUploadImageToProfile(JsonElement root)
    {
        int id = Int(root, "deviceId");
        int btn = Int(root, "buttonIndex");
        int profile = Int(root, "profile");
        string path = Str(root, "imagePath");
        int rotation = OptInt(root, "rotation", 0);
        bool ok;
        lock (_sdkLock)
        {
            string resolved = ResolveForUpload(path, rotation);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Same native path as CmdUploadImage. BC has no per-profile firmware icon storage for
            // the DisplayPad at all (see project_displaypad_profile_corruption memory) — "profile"
            // is only ever a K2/BC-side DB concept, so there is nothing profile-specific to write
            // to the device beyond the live icon itself. The `profile` parameter is accepted for
            // API compatibility with existing callers but no longer changes device-side behavior.
            ok = NativeIconUploader.Upload(resolved, btn, (uint)id);
            sw.Stop();
            Program.Log($"[UploadImageToProfile/native] dev={id} btn={btn} profile={profile} nativeCallMs={sw.ElapsedMilliseconds} ok={ok} path={resolved}");
            Thread.Sleep(IconSettleDelayMs);
        }
        return new { ok, result = ok };
    }

    // ================================================================
    // SDK callbacks → push JSON events
    // ================================================================

    private void OnPlug(int a, int b) =>
        PushEvent("plug", new { arg0 = a, arg1 = b });

    private void OnKey(int keyMatrix, int isPressed, int deviceId) =>
        PushEvent("key", new { deviceId, keyMatrix, pressed = isPressed == 1 });

    private void OnProgress(int percent) =>
        PushEvent("progress", new { percent, failed = percent == -1 });

    private void PushEvent(string evtName, object data)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(data, JOpts), JOpts)
            ?? new Dictionary<string, object?>();
        dict["id"] = 0;
        dict["evt"] = evtName;
        string json = JsonSerializer.Serialize(dict, JOpts);
        EventRaised?.Invoke(this, json);
    }

    // ================================================================
    // Icon rotation (same logic as K2.DisplayPad IconRotator)
    // ================================================================

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2.DisplayPad", "rotated");

    private static string ResolveForUpload(string path, int rotationDegrees)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;
        int angle = rotationDegrees switch { 90 => 270, 270 => 90, _ => 0 };
        if (angle == 0) return path;

        try
        {
            Directory.CreateDirectory(CacheDir);
            long mtime = 0;
            try { mtime = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{mtime}|{angle}"));
            string cached = Path.Combine(CacheDir,
                Convert.ToHexString(hash).ToLowerInvariant() + $"_r{angle}.png");
            if (File.Exists(cached)) return cached;

            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var src = new Bitmap(ms);
            using var bmp = new Bitmap(src);
            bmp.RotateFlip(angle == 90
                ? RotateFlipType.Rotate90FlipNone
                : RotateFlipType.Rotate270FlipNone);
            bmp.Save(cached, ImageFormat.Png);
            return cached;
        }
        catch { return path; }
    }

    // ================================================================
    // Reflection helpers (SDK static events)
    // ================================================================

    private void AttachStaticEvent(string eventName, string handlerName, out Delegate? created)
    {
        created = null;
        var evt = typeof(DisplayPadHelper).GetEvent(eventName,
            BindingFlags.Static | BindingFlags.Public);
        if (evt is null) return;
        var handlerType = evt.EventHandlerType!;
        var method = typeof(SdkHandler).GetMethod(handlerName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null) return;
        created = Delegate.CreateDelegate(handlerType, this, method);
        evt.AddEventHandler(null, created);
    }

    private static void DetachStaticEvent(string eventName, Delegate? handler)
    {
        if (handler is null) return;
        typeof(DisplayPadHelper).GetEvent(eventName,
            BindingFlags.Static | BindingFlags.Public)
            ?.RemoveEventHandler(null, handler);
    }

    // ================================================================
    // JSON helpers
    // ================================================================

    private static int Int(JsonElement e, string prop) => e.GetProperty(prop).GetInt32();
    private static string Str(JsonElement e, string prop) => e.GetProperty(prop).GetString() ?? "";
    private static int OptInt(JsonElement e, string prop, int def) =>
        e.TryGetProperty(prop, out var v) ? v.GetInt32() : def;

    public void Dispose()
    {
        if (_opened) CmdClose();
    }
}
