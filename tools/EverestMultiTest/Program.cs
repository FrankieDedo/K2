using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// ============================================================================
// EverestMultiTest — throwaway diagnostic tool, K2 project "multi-Everest Max"
// investigation, Fase 0.2 + 0.3 of the plan (see
// C:\Users\Francesco\.claude\plans\snoopy-baking-zephyr.md).
//
// Two independent experiments, run back to back:
//
//   STEP 1 — raw-HID enumeration: lists EVERY MI_03 command-interface path
//   found for VID 0x3282 / PID 0x0001 (K2's EverestHidNative.FindCommandInterfacePath
//   only keeps the LAST match today — "best wins" — this tool keeps them all).
//   With N physical Everest Max plugged in, expect N distinct paths if Windows
//   really does expose each physical unit separately (it should — device
//   instance paths are per-USB-port, VID/PID identity has no bearing on it).
//
//   STEP 2 — SDKDLL.dll "copy trick": SDKDLL.dll is a closed-source, PER-PROCESS
//   SINGLETON — none of its ~45 exports take a device handle/index/serial (see
//   K2.App/Services/EverestSdkNative.cs). OpenUSBDriver() grabs "a" Everest Max
//   via its own internal (closed) enumeration, no way to steer it. This step
//   copies SDKDLL.dll to 2 more files (SDKDLL_2.dll / SDKDLL_3.dll) and loads
//   all 3 as independent P/Invoke targets in the SAME process, to see empirically
//   whether each copy ends up bound to a DIFFERENT physical keyboard (decisive:
//   if it works, K2 does NOT need to reverse-engineer RGB/key-matrix/Media Dock
//   over raw HID to get multi-device — it can just open N copies of the DLL).
//
// KNOWN RISK: SDKDLL.dll has a documented chronic internal-thread stack-write
// bug (see K2.App/App.xaml.cs's VEH crash-survival machinery for the full
// story) — this tool has NONE of that safety net. A crash here just kills this
// console process; it cannot take down K2.App or corrupt anything since it's a
// separate process. That is the point of testing here first.
// ============================================================================

Console.WriteLine("=== EverestMultiTest — K2 multi-Everest Max diagnostic ===");
Console.WriteLine();

// ---------------------------------------------------------------------------
// STEP 1 — enumerate ALL MI_03 HID paths (VID 0x3282 / PID 0x0001)
// ---------------------------------------------------------------------------
Console.WriteLine("--- STEP 1: raw-HID MI_03 enumeration ---");
var paths = HidEnum.FindAllCommandInterfacePaths();
if (paths.Count == 0)
{
    Console.WriteLine("Nessuna Everest Max trovata (MI_03 non presente). Collega almeno una tastiera e rilancia.");
}
else
{
    Console.WriteLine($"Trovate {paths.Count} interfacce MI_03:");
    for (int i = 0; i < paths.Count; i++)
        Console.WriteLine($"  [{i + 1}] {paths[i]}");
}
Console.WriteLine();
Console.WriteLine("Nota: rilancia questo tool dopo scollegare/collegare/riavviare per verificare");
Console.WriteLine("se questi path restano STABILI (serve per usarli come identificativo persistente).");
Console.WriteLine();

// ---------------------------------------------------------------------------
// STEP 2 — SDKDLL.dll copy-trick
// ---------------------------------------------------------------------------
Console.WriteLine("--- STEP 2: SDKDLL.dll copy-trick (fino a 3 istanze) ---");

string? sourceDll = args.Length > 0 ? args[0] : DllLocator.FindSdkDll();
if (sourceDll is null)
{
    Console.WriteLine("SDKDLL.dll non trovato automaticamente.");
    Console.WriteLine("Rilancia passando il percorso completo come argomento, es.:");
    Console.WriteLine(@"  dotnet run -- ""C:\...\Mountain Base Camp\SDKDLL.dll""");
    return;
}
Console.WriteLine($"Sorgente SDKDLL.dll: {sourceDll}");

string outDir = AppContext.BaseDirectory;
string pathA = Path.Combine(outDir, "SDKDLL.dll");
string pathB = Path.Combine(outDir, "SDKDLL_2.dll");
string pathC = Path.Combine(outDir, "SDKDLL_3.dll");
File.Copy(sourceDll, pathA, overwrite: true);
File.Copy(sourceDll, pathB, overwrite: true);
File.Copy(sourceDll, pathC, overwrite: true);
Console.WriteLine($"Copiata in:\n  {pathA}\n  {pathB}\n  {pathC}");
Console.WriteLine();

var log = new ConcurrentQueue<string>();
void Log(string tag, string msg)
{
    string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {msg}";
    log.Enqueue(line);
    Console.WriteLine(line);
}

// Keep delegates alive in fields (static local functions capture into the
// closure, which is enough here since Main never returns until the process exits).
SdkA.KEY_CALLBACK cbA = (w, p, id) => Log("A", $"KEY wMatrix={w} pressed={p} id={id}");
SdkB.KEY_CALLBACK cbB = (w, p, id) => Log("B", $"KEY wMatrix={w} pressed={p} id={id}");
SdkC.KEY_CALLBACK cbC = (w, p, id) => Log("C", $"KEY wMatrix={w} pressed={p} id={id}");

bool OpenOne(string tag, Action<Delegate> setCb, Delegate cb, Func<bool> open, Func<bool> isPlug,
             Func<(bool ok, ushort vid, ushort pid, ushort fw)> devInfo)
{
    try
    {
        setCb(cb);
        bool ok = open();
        Log(tag, $"OpenUSBDriver -> {ok}");
        if (!ok) return false;
        Log(tag, $"IsDevicePlug -> {isPlug()}");
        var (dok, vid, pid, fw) = devInfo();
        Log(tag, $"GetDeviceInfo -> {dok} vid=0x{vid:X4} pid=0x{pid:X4} fw=0x{fw:X4}");
        return true;
    }
    catch (Exception ex)
    {
        Log(tag, $"EXCEPTION: {ex.Message}");
        return false;
    }
}

Console.WriteLine("Apro le 3 istanze (A=SDKDLL.dll, B=SDKDLL_2.dll, C=SDKDLL_3.dll)...");
bool okA = OpenOne("A", d => SdkA.SetKeyCallBack((SdkA.KEY_CALLBACK)d), cbA, SdkA.OpenUSBDriver, SdkA.IsDevicePlug, SdkA.DevInfo);
bool okB = OpenOne("B", d => SdkB.SetKeyCallBack((SdkB.KEY_CALLBACK)d), cbB, SdkB.OpenUSBDriver, SdkB.IsDevicePlug, SdkB.DevInfo);
bool okC = OpenOne("C", d => SdkC.SetKeyCallBack((SdkC.KEY_CALLBACK)d), cbC, SdkC.OpenUSBDriver, SdkC.IsDevicePlug, SdkC.DevInfo);

Console.WriteLine();
Console.WriteLine($"Riepilogo apertura: A={okA} B={okB} C={okC}");
Console.WriteLine();
Console.WriteLine("=== TEST DECISIVO ===");
Console.WriteLine("Premi UN tasto qualsiasi su UNA SOLA delle Everest Max fisiche alla volta");
Console.WriteLine("e osserva quale istanza (A/B/C) lo stampa sopra. Poi prova sulle altre.");
Console.WriteLine();
Console.WriteLine("- Se ogni tastiera fisica viene sempre riportata dalla STESSA istanza,");
Console.WriteLine("  e istanze diverse rispondono a tastiere diverse -> il copy-trick FUNZIONA.");
Console.WriteLine("- Se tutte le tastiere finiscono sulla stessa istanza (es. sempre solo A),");
Console.WriteLine("  o il comportamento e' incoerente/a caso -> il copy-trick NON funziona,");
Console.WriteLine("  si procede solo con l'HID nativo (Fase 3 del piano).");
Console.WriteLine();
Console.WriteLine("Premi INVIO per chiudere.");
Console.ReadLine();

try { SdkA.CloseUSBDriver(); } catch { }
try { SdkB.CloseUSBDriver(); } catch { }
try { SdkC.CloseUSBDriver(); } catch { }

Console.WriteLine();
Console.WriteLine($"Log completo ({log.Count} righe) sopra — copialo/incollalo per riportare il risultato.");

// ============================================================================

internal static class DllLocator
{
    /// <summary>Walks up from the tool's own build output looking for the repo's
    /// reference copy of SDKDLL.dll ("Mountain Base Camp\SDKDLL.dll", see
    /// _PROJECT_MAP.md — reference-only Base Camp install, do not modify it).</summary>
    public static string? FindSdkDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Mountain Base Camp", "SDKDLL.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>Raw HID enumeration of the Everest Max's MI_03 command interface —
/// same approach as K2.App/Services/EverestHidNative.cs's FindCommandInterfacePath,
/// but returns ALL matches instead of only the last one found.</summary>
internal static class HidEnum
{
    private const ushort VID = 0x3282;
    private const ushort PID = 0x0001;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0; // query-only open, no read/write needed here
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid gClass, string? enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid gClass,
        uint index, ref SP_DEVICE_INTERFACE_DATA ifData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData,
        IntPtr detail, int detailSize, out int required, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    public static System.Collections.Generic.List<string> FindAllCommandInterfacePaths()
    {
        var result = new System.Collections.Generic.List<string>();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero,
                                           DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == INVALID_HANDLE_VALUE) return result;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, IntPtr.Zero, 0, out int size, ref devInfo);
                if (size <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(size);
                string? path = null;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, detail, size, out _, ref devInfo))
                        path = Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }
                if (path is null) continue;

                using var h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;

                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != VID || attrs.ProductID != PID)
                    continue;

                if (path.ToLowerInvariant().Contains("mi_03"))
                    result.Add(path);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return result;
    }
}

// ---------------------------------------------------------------------------
// Three independent P/Invoke bindings, one per physical DLL FILE (a P/Invoke
// binding is resolved by file name — three different literal DLL names force
// the OS loader to treat them as three separate modules/instances, unlike
// calling LoadLibrary on the same path three times which just refcounts the
// same module). Minimal subset needed for this experiment, signatures copied
// from K2.App/Services/EverestSdkNative.cs.
// ---------------------------------------------------------------------------

internal static class SdkA
{
    private const string Dll = "SDKDLL.dll";
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool OpenUSBDriver();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseUSBDriver();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool IsDevicePlug();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK cb);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetDeviceInfo(ref DevInfoRaw info);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DevInfoRaw { public ushort vid; public ushort pid; public ushort fwVer; public ushort bootloadVer; }

    public static (bool ok, ushort vid, ushort pid, ushort fw) DevInfo()
    {
        var info = new DevInfoRaw();
        bool ok = GetDeviceInfo(ref info);
        return (ok, info.vid, info.pid, info.fwVer);
    }
}

internal static class SdkB
{
    private const string Dll = "SDKDLL_2.dll";
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool OpenUSBDriver();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseUSBDriver();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool IsDevicePlug();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK cb);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetDeviceInfo(ref DevInfoRaw info);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DevInfoRaw { public ushort vid; public ushort pid; public ushort fwVer; public ushort bootloadVer; }

    public static (bool ok, ushort vid, ushort pid, ushort fw) DevInfo()
    {
        var info = new DevInfoRaw();
        bool ok = GetDeviceInfo(ref info);
        return (ok, info.vid, info.pid, info.fwVer);
    }
}

internal static class SdkC
{
    private const string Dll = "SDKDLL_3.dll";
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool OpenUSBDriver();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseUSBDriver();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] public static extern bool IsDevicePlug();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK cb);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetDeviceInfo(ref DevInfoRaw info);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DevInfoRaw { public ushort vid; public ushort pid; public ushort fwVer; public ushort bootloadVer; }

    public static (bool ok, ushort vid, ushort pid, ushort fw) DevInfo()
    {
        var info = new DevInfoRaw();
        bool ok = GetDeviceInfo(ref info);
        return (ok, info.vid, info.pid, info.fwVer);
    }
}
