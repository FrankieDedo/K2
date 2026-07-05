using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using DisplayPad.SDK;

namespace K2.DisplayPad.Services;

/// <summary>
/// Facade over <c>DisplayPad.SDK</c>.
///
/// Actual SDK map (verified by reading the DLL's ECMA-335 metadata):
///   - <c>DisplayPad.SDK.DisplayPadSDK</c>: the methods are <em>internal static</em>,
///     so they are NOT usable directly from outside the assembly.
///     The only thing reachable from outside via reflection is the static field
///     <c>lstDeviceID</c> (the collection of known device IDs).
///   - <c>DisplayPad.SDK.DisplayPadHelper</c>: public class with public
///     <em>instance</em> methods (DisplayPadOpenUSBDriver, DisplayPadIsDevicePlug,
///     UploadImage, ...). The events <c>DisplayPadPlugCallBack</c>,
///     <c>DisplayPadKeyCallBack</c>, <c>DisplayPadProgressCallBack</c> are instead
///     <em>static</em> on the class.
///
/// This facade instantiates a single <c>DisplayPadHelper</c>, hooks the three
/// static events via reflection (target signatures: <c>void(int,int,int)</c> per
/// DisplayPad.SDK.xml) and re-exposes everything as typed .NET events.
/// </summary>
public sealed class DisplayPadService : IDisposable
{
    private readonly DisplayPadHelper _helper = new();
    private bool _opened;
    private Delegate? _plugHandler;
    private Delegate? _keyHandler;
    private Delegate? _progressHandler;

    /// <summary>Plug / Unplug / Suspend of the device.</summary>
    public event EventHandler<DevicePlugEventArgs>? DevicePlug;

    /// <summary>DisplayPad button pressed/released.</summary>
    public event EventHandler<DisplayPadKeyEventArgs>? KeyEvent;

    /// <summary>Firmware update progress (0..100, -1 = fail).</summary>
    public event EventHandler<FirmwareProgressEventArgs>? FirmwareProgress;

    /// <summary>Maximum number of devices handled by the SDK (MAX_DEV_COUNT = 10).</summary>
    public const int MaxDeviceCount = 10;

    /// <summary>Opens the USB driver. <paramref name="hWnd"/> is the HWND of the
    /// main window: needed because the SDK posts WM_DEVICE_PLUG /
    /// WM_FW_PROGRESS to its message pump.
    /// The DisplayPadHelper API accepts the handle as a <em>string</em> (decimal).</summary>
    public bool Open(IntPtr hWnd)
    {
        if (_opened) return true;

        AttachStaticEvent("DisplayPadPlugCallBack",     nameof(OnPlug),     out _plugHandler);
        AttachStaticEvent("DisplayPadKeyCallBack",      nameof(OnKey),      out _keyHandler);
        AttachStaticEvent("DisplayPadProgressCallBack", nameof(OnProgress), out _progressHandler);

        var hStr = hWnd.ToInt64().ToString();
        App.WriteLog($"[Open] DisplayPadOpenUSBDriver(\"{hStr}\")");
        bool ok = _helper.DisplayPadOpenUSBDriver(hStr);
        App.WriteLog($"[Open] -> {ok}");
        _opened = ok;
        return ok;
    }

    /// <summary>Closes the driver and unsubscribes the events.</summary>
    public void Close()
    {
        if (!_opened) return;
        try { _helper.DisplayPadCloseUSBDriver(); } catch { /* swallow */ }
        _opened = false;
        DetachStaticEvent("DisplayPadPlugCallBack",     _plugHandler);
        DetachStaticEvent("DisplayPadKeyCallBack",      _keyHandler);
        DetachStaticEvent("DisplayPadProgressCallBack", _progressHandler);
        _plugHandler = _keyHandler = _progressHandler = null;
    }

    /// <summary>Version of the managed SDK DLL.</summary>
    public int SdkVersion() => _helper.DisplayPadDllVersion();

    // The SDK only allows the values 0/25/50/75/100 for brightness.
    // We use this as a discriminant: phantom slots return
    // out-of-range values, while real devices always return one
    // of these five steps.
    private static readonly HashSet<int> ValidBrightness = new() { 0, 25, 50, 75, 100 };

    /// <summary>IDs of the devices ACTUALLY connected.
    /// <c>DisplayPadIsDevicePlug</c> is unreliable (it can return true
    /// even for phantom slots). We filter by verifying that the read
    /// brightness is one of the five steps allowed by the SDK.</summary>
    public IReadOnlyList<int> DeviceIds()
    {
        var found = new List<int>();
        for (int id = 1; id <= MaxDeviceCount; id++)
        {
            bool plugged;
            try { plugged = _helper.DisplayPadIsDevicePlug(id); }
            catch (Exception ex)
            {
                App.WriteLog($"[DeviceIds] IsDevicePlug({id}) threw: {ex.Message}");
                continue;
            }
            if (!plugged) continue;

            // Phantom slots return empty firmware version; real devices always have one.
            string fw;
            try { fw = _helper.DisplayPadGetDevAppVer(id) ?? ""; }
            catch { continue; }
            if (string.IsNullOrEmpty(fw))
            {
                App.WriteLog($"[DeviceIds] id={id} skipped (empty fw version -> phantom)");
                continue;
            }

            int brightness;
            try { brightness = _helper.DisplayPadGetMainBrightness(id); }
            catch { continue; }

            if (!ValidBrightness.Contains(brightness))
            {
                App.WriteLog($"[DeviceIds] id={id} skipped (brightness={brightness} invalid -> phantom)");
                continue;
            }
            found.Add(id);
        }
        App.WriteLog($"[DeviceIds] real devices -> [{string.Join(", ", found)}]");
        return found;
    }

    /// <summary>Snapshot of the SDK's <c>lstDeviceID</c> field (can be
    /// empty right after Open until the driver is queried).
    /// Useful for diagnostic purposes.</summary>
    public IReadOnlyList<int> ListDeviceIdSnapshot()
    {
        var sdkType = typeof(DisplayPadHelper).Assembly
            .GetType("DisplayPad.SDK.DisplayPadSDK");
        if (sdkType is null) return Array.Empty<int>();
        var field = sdkType.GetField("lstDeviceID",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (field?.GetValue(null) is not System.Collections.IEnumerable raw)
            return Array.Empty<int>();
        var result = new List<int>();
        foreach (var item in raw)
        {
            if (item is null) continue;
            try { result.Add(Convert.ToInt32(item)); } catch { }
        }
        return result;
    }

    /// <summary>Number of connected devices.</summary>
    public int DeviceCount() => DeviceIds().Count;

    /// <summary>True if the device is physically plugged.</summary>
    public bool IsPlugged(int id) => _helper.DisplayPadIsDevicePlug(id);

    /// <summary>Device FW version.
    /// The SDK API returns the version as a <see cref="string"/> (e.g. "1.0.6"). </summary>
    public string FirmwareVersion(int id) => _helper.DisplayPadGetDevAppVer(id) ?? "";

    /// <summary>Current device brightness (0/25/50/75/100).</summary>
    public int GetBrightness(int id) => _helper.DisplayPadGetMainBrightness(id);

    /// <summary>Sets the brightness (0/25/50/75/100).</summary>
    public bool SetBrightness(int id, int level) =>
        _helper.DisplayPadSetMainBrightness(level, id);

    /// <summary>Switches the active profile (1..5).</summary>
    public bool SwitchProfile(int id, int profile) =>
        _helper.DisplayPadSwitchProfile(profile.ToString(), id);

    /// <summary>Number of DisplayPad buttons (FW_NUM_KEY = 12).</summary>
    public const int ButtonCount = 12;

    /// <summary>Maximum number of profiles (FW_NUM_PROFILE = 5).</summary>
    public const int ProfileCount = 5;

    /// <summary>Enables / disables SW control of the device.
    /// When true, the host manages button images in real time
    /// (UploadImage uses SetIconPacket); when false, images live in the
    /// profile stored on the firmware.
    ///
    /// The original MountainDisplayPadWorker.exe retries up to ~6 times
    /// when APEnable returns false: we replicate the same pattern.</summary>
    public bool APEnable(int id, bool enable, int retries = 10)
    {
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            bool ok;
            try { ok = _helper.DisplayPadAPEnable(enable ? "1" : "0", id); }
            catch (Exception ex)
            {
                App.WriteLog($"[APEnable] id={id} enable={enable} threw on attempt {attempt}: {ex.Message}");
                return false;
            }
            if (ok)
            {
                App.WriteLog($"[APEnable] id={id} enable={enable} OK (attempt {attempt})");
                return true;
            }
            if (attempt < retries) Thread.Sleep(150);
        }
        App.WriteLog($"[APEnable] id={id} enable={enable} FAIL after {retries} retries");
        return false;
    }

    /// <summary>Probes the device: calls <c>DisplayPadGetFWInfo</c> and returns
    /// a diagnostic string with the struct's fields. Useful for distinguishing
    /// "real" devices from phantom slots populated by <c>IsDevicePlug</c>.</summary>
    public string ProbeFirmwareInfo(int id)
    {
        try
        {
            object? info = _helper.DisplayPadGetFWInfo(id);
            if (info is null) return "<null>";
            // We extract the public fields via reflection: the FWInfo struct is
            // not referenceable in C# without defining it explicitly.
            var t = info.GetType();
            var parts = new List<string>();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                parts.Add($"{f.Name}={f.GetValue(info)}");
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                try { parts.Add($"{p.Name}={p.GetValue(info)}"); } catch { }
            }
            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    /// <summary>Resets ALL button images of the current profile.</summary>
    public bool ResetAllPictures(int id) =>
        _helper.DisplayPadResetPicture(id);

    /// <summary>Uploads an image to the button in "live" mode via SetIconPacket
    /// (requires AP enabled). It's fast but <b>not persistent</b>: the firmware
    /// redraws the icon from its stored profile as soon as it receives a
    /// redraw event. Useful only for a quick preview.</summary>
    public bool UploadImage(int id, string imagePath, int buttonIndex) =>
        _helper.UploadImage(id, imagePath, buttonIndex);

    /// <summary>Uploads an image and <b>saves</b> it in the given profile
    /// slot (1..5) of the firmware via SetIconPic. Preferred over <see cref="UploadImage"/>
    /// when we want the icon to survive presses/redraws/profile changes.
    /// The <c>isAPEnable</c> parameter passed to the SDK is false: this tells the
    /// firmware "write to my storage, not just to the display buffer".</summary>
    public bool UploadImageToProfile(int id, string imagePath, int buttonIndex, int profileIndex) =>
        _helper.UploadImageBySetIconPic(id, imagePath, buttonIndex, false, profileIndex);

    public void Dispose() => Close();

    // ---------- internal handlers (reflection targets) ----------
    //
    // SDK delegate signatures (verified from the DLL's metadata):
    //   DisplayPadStatus.Invoke         (int, int)            -> Plug
    //   DisplayPadKeyStatus.Invoke      (int, int, int)       -> Key
    //   DisplayPadProgressStatus.Invoke (int)                 -> Progress

    // Plug: the two ints (a, b) are not explicitly documented in the SDK's
    // XML doc. Empirically one should be the device ID and the
    // other the status (0=remove, 1=plug, 2=suspend). We report
    // both and call them "a" / "b" until we see live
    // what they are; whoever consumes the event can use them directly.
    private void OnPlug(int a, int b)
    {
        DevicePlug?.Invoke(this, new DevicePlugEventArgs(
            arg0: a,
            arg1: b,
            status: b switch
            {
                0 => DevicePlugStatus.Removed,
                1 => DevicePlugStatus.Plugged,
                2 => DevicePlugStatus.Suspended,
                _ => DevicePlugStatus.Unknown
            }));
    }

    private void OnKey(int keyMatrix, int isPressed, int deviceId)
    {
        KeyEvent?.Invoke(this, new DisplayPadKeyEventArgs(
            deviceId: deviceId,
            keyMatrix: keyMatrix,
            pressed: isPressed == 1));
    }

    private void OnProgress(int percent)
    {
        FirmwareProgress?.Invoke(this, new FirmwareProgressEventArgs(
            percent: percent,
            failed: percent == -1));
    }

    // ---------- reflection helpers ----------

    // The events are static on the DisplayPadHelper class.
    private void AttachStaticEvent(string eventName, string handlerName, out Delegate? created)
    {
        created = null;
        var evt = typeof(DisplayPadHelper).GetEvent(
            eventName, BindingFlags.Static | BindingFlags.Public);
        if (evt is null)
        {
            App.WriteLog($"[AttachStaticEvent] event '{eventName}' not found");
            return;
        }
        var handlerType = evt.EventHandlerType
            ?? throw new InvalidOperationException($"{eventName}: missing EventHandlerType");
        var method = typeof(DisplayPadService).GetMethod(
            handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
        {
            App.WriteLog($"[AttachStaticEvent] handler '{handlerName}' not found");
            return;
        }
        try
        {
            created = Delegate.CreateDelegate(handlerType, this, method);
            evt.AddEventHandler(target: null, created);
            App.WriteLog($"[AttachStaticEvent] {eventName} <- {handlerName} ({handlerType.Name})");
        }
        catch (Exception ex)
        {
            App.WriteLog($"[AttachStaticEvent] FAIL {eventName} <- {handlerName}: {ex}");
            throw;
        }
    }

    private void DetachStaticEvent(string eventName, Delegate? handler)
    {
        if (handler is null) return;
        var evt = typeof(DisplayPadHelper).GetEvent(
            eventName, BindingFlags.Static | BindingFlags.Public);
        evt?.RemoveEventHandler(target: null, handler);
    }
}

public enum DevicePlugStatus { Unknown, Removed, Plugged, Suspended }

public sealed class DevicePlugEventArgs : EventArgs
{
    public DevicePlugEventArgs(int arg0, int arg1, DevicePlugStatus status)
    {
        Arg0 = arg0;
        Arg1 = arg1;
        Status = status;
    }
    /// <summary>First argument of the SDK delegate (deviceId or status, TBD).</summary>
    public int Arg0 { get; }
    /// <summary>Second argument of the SDK delegate.</summary>
    public int Arg1 { get; }
    /// <summary>Interpretation of <see cref="Arg1"/> assuming it's the status.</summary>
    public DevicePlugStatus Status { get; }
}

public sealed class DisplayPadKeyEventArgs : EventArgs
{
    public DisplayPadKeyEventArgs(int deviceId, int keyMatrix, bool pressed)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
    }
    public int DeviceId { get; }
    public int KeyMatrix { get; }
    public bool Pressed { get; }
}

public sealed class FirmwareProgressEventArgs : EventArgs
{
    public FirmwareProgressEventArgs(int percent, bool failed)
    {
        Percent = percent;
        Failed = failed;
    }
    public int Percent { get; }
    public bool Failed { get; }
}
