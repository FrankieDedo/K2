using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace K2.Core.Services;

/// <summary>One active Windows playback (render) endpoint: its persistent device id
/// (survives reboots, but can change if the same physical device re-enumerates on a
/// different USB port) and its current friendly name.</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Lists Windows playback devices and switches the system default output — via the Core
/// Audio API (<c>MMDeviceEnumerator</c>) for enumeration and the undocumented but widely
/// relied-upon <c>IPolicyConfig</c> COM interface for <c>SetDefaultEndpoint</c> (no public
/// .NET/Win32 API exists for changing the default device; this is the same interface every
/// third-party "default audio device switcher" tool uses). No NuGet dependency — plain
/// COM interop, same style as <see cref="User32"/>/<see cref="PowrProf"/> in <c>ActionExecutor</c>.
/// </summary>
public static class AudioDeviceService
{
    private const int CLSCTX_ALL = 23;

    /// <summary>Active playback (render) devices, sorted by name. Returns empty (never
    /// throws) if the Core Audio API is unavailable for any reason.</summary>
    public static AudioDeviceInfo[] ListPlaybackDevices()
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
            collection.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                string? id = GetDeviceId(device);
                string? name = GetFriendlyName(device);
                if (id is not null) result.Add(new AudioDeviceInfo(id, name ?? id));
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result.ToArray();
    }

    /// <summary>Resolves a saved <see cref="AudioDevicePayload"/> against the devices
    /// currently plugged in: exact id match first, else a name match (case-insensitive) —
    /// the fallback that keeps a binding working after the physical device (e.g. a USB
    /// headset) is unplugged and reconnected, which can hand it a new device id. Returns
    /// null when neither matches (device genuinely not present right now).</summary>
    public static string? TryResolveDeviceId(AudioDevicePayload payload)
    {
        var devices = ListPlaybackDevices();
        foreach (var d in devices)
            if (string.Equals(d.Id, payload.Id, StringComparison.OrdinalIgnoreCase))
                return d.Id;
        if (!string.IsNullOrEmpty(payload.Name))
            foreach (var d in devices)
                if (string.Equals(d.Name, payload.Name, StringComparison.OrdinalIgnoreCase))
                    return d.Id;
        return null;
    }

    /// <summary>Sets the default playback device for all three roles (Console/Multimedia/
    /// Communications) — matching what the Windows Sound settings page does for a single
    /// "Output device" pick, rather than leaving old-role defaults stale.</summary>
    public static bool SetDefaultPlaybackDevice(string deviceId)
    {
        try
        {
            var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
            int hr1 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
            int hr2 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            int hr3 = policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            return hr1 == 0 && hr2 == 0 && hr3 == 0;
        }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
    }

    private static string? GetDeviceId(IMMDevice device)
    {
        device.GetId(out string id);
        return id;
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out var store);
            var key = PropertyKeys.PKEY_Device_FriendlyName;
            store.GetValue(ref key, out var pv);
            try { return pv.GetString(); }
            finally { PropVariantNative.PropVariantClear(ref pv); }
        }
        catch (COMException) { return null; }
    }

    // ── Core Audio API COM interop ───────────────────────────────────

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [Flags]
    private enum DeviceState : uint { Active = 0x1, Disabled = 0x2, NotPresent = 0x4, Unplugged = 0x8, All = 0xF }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int pcDevices);
        int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out DeviceState pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int cProps);
        int GetAt(int iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
        int SetValue(ref PropertyKey key, ref PropVariant propvar);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    private static class PropertyKeys
    {
        public static PropertyKey PKEY_Device_FriendlyName = new()
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14,
        };
    }

    /// <summary>Minimal <c>PROPVARIANT</c> — only reads the <c>VT_LPWSTR</c> case, the only
    /// one this file ever asks for (a device's friendly name).</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        private const short VT_LPWSTR = 31;
        public string? GetString() => vt == VT_LPWSTR ? Marshal.PtrToStringUni(pointerValue) : null;
    }

    private static class PropVariantNative
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);
    }

    // ── IPolicyConfig (undocumented; SetDefaultEndpoint has no public replacement) ───

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bDefault, out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long hnsDefaultDevicePeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bVisible);
    }
}
