using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using K2.Core;

namespace K2.App;

/// <summary>
/// Entry point for the unified K2 application.
///
/// K2.App is the multi-device shell that will eventually replace Base Camp
/// for all Mountain products. The first implemented module is the MacroPad
/// (see <see cref="Services.MacroPadService"/>); DisplayPad and Everest Max
/// will be integrated as subsequent modules.
/// </summary>
public partial class App : Application
{
    /// <summary>Log file next to the executable, shared by all modules.</summary>
    public static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "K2.App.log");

    /// <summary>Separate crash log file for native/fatal crashes.</summary>
    public static readonly string CrashLogPath = Path.Combine(
        AppContext.BaseDirectory, "K2.App.crash.log");

    // Windows MiniDump API — generates a .dmp of the process on native crash.
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, IntPtr hFile,
        uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // Stack page mapping — used to fix SDKDLL.dll timer thread stack-write faults.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, IntPtr dwSize,
        uint flAllocationType, uint flProtect);

    private const uint MEM_RESERVE    = 0x2000;
    private const uint MEM_COMMIT     = 0x1000;
    private const uint PAGE_READWRITE = 0x04;

    // VEH — intercepts native exceptions (access violations from SDKDLL.dll)
    // BEFORE Windows terminates the process. Logs address and module.
    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandlerDelegate handler);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, string moduleName, out IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

    [DllImport("kernel32.dll")]
    private static extern void RaiseFailFastException(IntPtr exceptionRecord, IntPtr contextRecord, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, uint size, uint type, uint protect);

    // ---- SDK stack watchdog: periodic, low-impact ESP snapshot of the DLL timer thread ----
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint THREAD_GET_CONTEXT    = 0x0008;
    private const uint THREAD_SUSPEND_RESUME = 0x0002;
    private const uint CONTEXT_CONTROL_X86   = 0x10001; // Ebp, Eip, SegCs, EFlags, Esp, SegSs

    private const int WATCHDOG_INTERVAL_MS = 20_000;

    // tid of the SDKDLL.dll timer thread, captured the first time the VEH sees it fault
    // (VehCore always runs on the faulting thread itself, so GetCurrentThreadId() there
    // IS the DLL thread's id). Guards against starting the watchdog more than once.
    private static uint _sdkWatchdogTid;
    private static int  _sdkWatchdogStartedFlag;

    // MEM_COMMIT|MEM_RESERVE=0x3000, PAGE_EXECUTE_READWRITE=0x40
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    // EXCEPTION_RECORD x86 layout (per leggere ExceptionCode e ExceptionAddress)
    private const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005;
    private const int  EXCEPTION_CONTINUE_SEARCH    = 0;
    private const int  EXCEPTION_CONTINUE_EXECUTION = -1;

    // x86 CONTEXT offsets
    private const int CTX_EAX = 0xB0;
    private const int CTX_EBP = 0xB4;
    private const int CTX_EIP = 0xB8;
    private const int CTX_ESP = 0xC4;

    private delegate int VectoredHandlerDelegate(IntPtr exceptionInfo);

    // Keep a static reference to prevent the GC from collecting the delegate.
    private static VectoredHandlerDelegate? _vehDelegate;

    // Re-entrancy guard: if the frame-walk causes another AV, don't recurse.
    private static volatile int _vehReentrancy;

    // Stack-write skip rate-limiter: allow at most 2 skips per 500 ms window.
    // After 2 skips, ExitThread kills the DLL timer thread before ESP drifts
    // far enough (≥ ~4 iterations) to itself hit the unmapped page and cause
    // a catastrophic (un-VEH-able) stack-overflow crash.
    private static volatile int _sdkStackWriteCount;
    private static long _sdkStackWriteWindowEndMs;

    // Throttle log noise for skip entries (log first + once per 5 s).
    private static long _sdkStackWriteLastLogMs = long.MinValue;
    private static volatile int _sdkStackWriteSinceLog;

    // Cumulative bytes committed by the PRIMARY (VirtualAlloc) stack-write fix, for the
    // life of the process. The DLL timer thread's stack usage appears to grow slowly and
    // permanently over time (not a one-off guard-page hiccup) — each successful map just
    // buys a few more iterations before the next fault, further out. Left unbounded, this
    // eventually either collides with another allocation or exhausts the address space in
    // that direction, producing a genuine STATUS_STACK_OVERFLOW with too little remaining
    // stack for the VEH itself to run (silent, un-loggable process death — confirmed
    // 2026-07-04: last VEH log was a successful map, then the process vanished ~3.5 min
    // later with no crash-log entry and no ProcessExit line).
    // Ceiling: once total committed extra stack exceeds this, stop extending and fall
    // through to the (already rate-limited) skip/ExitThread fallback, which sacrifices the
    // DLL thread cleanly instead of risking a catastrophic silent crash.
    private const long SDK_STACK_GROWTH_CEILING_BYTES = 0x40000; // 256 KB
    private static long _sdkStackGrownBytes;

    // Rate limit for minidumps taken on non-SDKDLL access violations (see VehCore,
    // !inSdkDll branch) — cap disk usage / dump-writing overhead if it ever repeats.
    private const int MAX_NON_SDK_AV_DUMPS = 3;
    private static int _nonSdkAvDumpCount;

    // ---- Crash survival: ExitThread stub to kill the DLL thread ----
    // When the crash is entirely inside SDKDLL.dll (DLL internal thread),
    // the frame-walk cannot find our return addresses. Instead of letting
    // the process die, we redirect EIP to an x86 stub that calls ExitThread(0) —
    // kills only that thread, the process survives.
    private static IntPtr _exitThreadStub;   // pointer to x86 executable code
    private static uint   _uiThreadId;       // to distinguish UI thread from DLL thread

    /// <summary>
    /// Volatile flag: the VEH sets it when it survives a SDKDLL.dll crash.
    /// The poller and other consumers check it to stop and perform recovery.
    /// </summary>
    public static volatile bool SdkCrashRecoveryNeeded;

    /// <summary>
    /// Set when the VEH rate-limiter triggers ExitThread after a crash loop.
    /// The DLL's heap/locks may be corrupted; recovery MUST NOT call Close/Open.
    /// Cleared at app startup; once set, LED preview is disabled for the session.
    /// </summary>
    public static volatile bool SdkRateLimitedExitThread;

    public App()
    {
        // Capture the UI thread ID — needed by the VEH to distinguish
        // our thread from SDKDLL.dll's internal thread.
        _uiThreadId = GetCurrentThreadId();

        // Prepare the x86 ExitThread(0) stub: if the crash is on the DLL thread,
        // the VEH redirects EIP here and the thread dies without taking the process.
        PrepareExitThreadStub();

        // Install the VEH as the first handler (first=1) — catches access violations
        // from SDKDLL.dll and logs before the process dies.
        _vehDelegate = VectoredExceptionHandler;
        AddVectoredExceptionHandler(1, _vehDelegate);

        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        // Initialize localization before any UI is created.
        Core.Loc.Init();

        WriteLog($"=== App start {DateTime.Now:O} pid={Environment.ProcessId} " +
                 $"arch={(Environment.Is64BitProcess ? "x64" : "x86")} lang={Core.Loc.CurrentLang} ===");

        // Register the resolver for non-redistributable native DLLs (e.g.
        // MacroPadSDK.dll) BEFORE any P/Invoke: so the loader finds them
        // even if they are not bundled with K2. See DISTRIBUTION.md.
        Services.NativeDependencyResolver.Install();
    }

    /// <summary>
    /// Allocates executable memory and writes an x86 stub that calls ExitThread(0).
    /// Layout (9 bytes):
    ///   push 0              ; 6A 00
    ///   mov eax, &lt;addr&gt;     ; B8 xx xx xx xx
    ///   call eax            ; FF D0
    /// </summary>
    private static void PrepareExitThreadStub()
    {
        IntPtr k32 = GetModuleHandleW("kernel32.dll");
        if (k32 == IntPtr.Zero) return;
        IntPtr exitThreadAddr = GetProcAddress(k32, "ExitThread");
        if (exitThreadAddr == IntPtr.Zero) return;

        _exitThreadStub = VirtualAlloc(IntPtr.Zero, 64,
            MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
        if (_exitThreadStub == IntPtr.Zero) return;

        int off = 0;
        Marshal.WriteByte(_exitThreadStub, off++, 0x6A); // push imm8
        Marshal.WriteByte(_exitThreadStub, off++, 0x00); // 0 (exit code)
        Marshal.WriteByte(_exitThreadStub, off++, 0xB8); // mov eax, imm32
        Marshal.WriteInt32(_exitThreadStub, off, exitThreadAddr.ToInt32());
        off += 4;
        Marshal.WriteByte(_exitThreadStub, off++, 0xFF); // call eax
        Marshal.WriteByte(_exitThreadStub, off++, 0xD0);
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("[DispatcherUnhandled] " + e.Exception);
        ShowError("Unhandled error (UI)", e.Exception.ToString());
        e.Handled = true; // keep the window alive
    }

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        var msg = $"[DomainUnhandled terminating={e.IsTerminating}] " + e.ExceptionObject;
        WriteLog(msg);
        if (e.IsTerminating) WriteCrashLog(msg);
        try
        {
            Dispatcher.Invoke(() =>
                ShowError("Unhandled error (domain)", e.ExceptionObject?.ToString() ?? "?"));
        }
        catch { /* dispatcher may already be dead */ }
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLog("[UnobservedTask] " + e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Vectored Exception Handler: intercepts native access violations from
    /// SDKDLL.dll. If the crash is inside SDKDLL.dll, attempts to survive by
    /// doing a frame-unwind to the first return address outside the DLL,
    /// simulating a "return 0" from the SDK function. Otherwise continues
    /// normally (EXCEPTION_CONTINUE_SEARCH → process dies).
    /// </summary>
    private static int VectoredExceptionHandler(IntPtr exceptionInfo)
    {
        // Re-entrancy guard: if the frame-walk causes another AV, let it die
        if (System.Threading.Interlocked.Increment(ref _vehReentrancy) > 1)
        {
            System.Threading.Interlocked.Decrement(ref _vehReentrancy);
            return EXCEPTION_CONTINUE_SEARCH;
        }

        try
        {
            return VehCore(exceptionInfo);
        }
        finally
        {
            System.Threading.Interlocked.Decrement(ref _vehReentrancy);
        }
    }

    private static int VehCore(IntPtr exceptionInfo)
    {
        // EXCEPTION_POINTERS { EXCEPTION_RECORD*, CONTEXT* }
        var pRecord = Marshal.ReadIntPtr(exceptionInfo, 0);
        if (pRecord == IntPtr.Zero) return EXCEPTION_CONTINUE_SEARCH;

        uint code = (uint)Marshal.ReadInt32(pRecord, 0);
        if (code != EXCEPTION_ACCESS_VIOLATION)
        {
            // Log fatal native exceptions so we can diagnose crashes that bypass the AV path.
            // Exclude CLR-internal exception wrappers (0xE0434352, 0xE0434F4D) and
            // informational codes (< 0x80000000) — those are internal CLR control-flow.
            if (code >= 0xC0000000u && code != 0xC000008Cu /* guard-page AV, benign */
                && code != 0xE0434352u /* COMPLUS_EXCEPTION: SEH used by every managed throw/catch, not fatal */
                && code != 0xE0434F4Du /* CLR notification exception (e.g. debugger), not fatal */)
            {
                string codeStr = code switch {
                    0xC00000FDu => "STATUS_STACK_OVERFLOW",
                    0xC0000374u => "STATUS_HEAP_CORRUPTION",
                    0xC0000008u => "STATUS_INVALID_HANDLE",
                    0xC000001Du => "STATUS_ILLEGAL_INSTRUCTION",
                    0xC0000096u => "STATUS_PRIVILEGED_INSTRUCTION",
                    _           => $"0x{code:X8}"
                };
                var addr2 = Marshal.ReadIntPtr(pRecord, 12);
                try { WriteLog($"[VEH] Fatal native exception {codeStr} at 0x{addr2:X8} — process will terminate"); }
                catch { }
                try { WriteCrashLog($"[VEH] Fatal native exception {codeStr} at 0x{addr2:X8}"); }
                catch { }
            }
            return EXCEPTION_CONTINUE_SEARCH;
        }

        var addr = Marshal.ReadIntPtr(pRecord, 12);

        // Identify the module and whether it is SDKDLL.dll
        bool inSdkDll = false;
        nint dllBase = 0, dllEnd = 0;
        string module = "???";
        try
        {
            foreach (System.Diagnostics.ProcessModule m in
                     System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                nint baseAddr = m.BaseAddress;
                if (addr >= baseAddr && addr < baseAddr + m.ModuleMemorySize)
                {
                    nint rvaInner = addr - baseAddr;
                    module = $"{m.ModuleName}+0x{rvaInner:X}";
                    if (m.ModuleName?.Equals("SDKDLL.dll", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        inSdkDll = true;
                        dllBase = baseAddr;
                        dllEnd = baseAddr + m.ModuleMemorySize;
                    }
                    break;
                }
            }
        }
        catch { }

        // Parse AV details: ExceptionInformation[0]=type (0=read,1=write), [1]=bad address
        nint avType = 0, avBadAddr = 0;
        try
        {
            int nParams = Marshal.ReadInt32(pRecord, 16);
            if (nParams >= 2)
            {
                avType    = Marshal.ReadIntPtr(pRecord, 20);
                avBadAddr = Marshal.ReadIntPtr(pRecord, 24);
            }
        }
        catch { }

        // Compute RVA early so we can throttle logs for the known stack-write loop.
        nint rva = inSdkDll && dllBase != 0 ? (addr - dllBase) : -1;
        bool isKnownLoopRva = inSdkDll && code == EXCEPTION_ACCESS_VIOLATION
                              && avType == (nint)1
                              && (rva == 0x512D || rva == 0x5133);

        // Throttle logging for known crash-loop RVAs: log first + once per 5 s.
        bool shouldLog = true;
        if (isKnownLoopRva)
        {
            long nowMs2 = Environment.TickCount64;
            int since2 = System.Threading.Interlocked.Increment(ref _sdkStackWriteSinceLog);
            shouldLog = (since2 == 1 || nowMs2 - System.Threading.Interlocked.Read(ref _sdkStackWriteLastLogMs) >= 5000);
            if (shouldLog)
                System.Threading.Interlocked.Exchange(ref _sdkStackWriteLastLogMs, nowMs2);
        }

        if (shouldLog)
        {
            var msg = $"[VEH] ACCESS VIOLATION a 0x{addr:X8} ({module}) code=0x{code:X8} " +
                      $"type={(avType == 0 ? "READ" : avType == 1 ? "WRITE" : $"{avType}")} " +
                      $"badAddr=0x{avBadAddr:X8}";
            try { WriteLog(msg); WriteCrashLog(msg); } catch { }
        }

        if (!inSdkDll)
        {
            // Unlike the SDKDLL.dll case (a known-flaky 3rd-party DLL whose heap might
            // already be corrupted — dumping it risks a second, nested AV inside
            // dbghelp.dll), an AV elsewhere (typically coreclr.dll doing a fault-based
            // null check, e.g. "callvirt on null" — badAddr is a small offset like
            // 0x00000008) is comparatively rare during healthy operation and the CLR
            // itself should be dumpable safely. Capture a minidump here (rate-limited)
            // so a real occurrence can be inspected in WinDbg for the exact managed
            // call stack, instead of just the single-line VEH log entry we had before.
            // If MiniDumpWriteDump itself faults, the reentrancy guard in
            // VectoredExceptionHandler prevents a recursive VEH loop — worst case we
            // crash a few microseconds later than we would have anyway.
            int dumpNo = System.Threading.Interlocked.Increment(ref _nonSdkAvDumpCount);
            if (dumpNo <= MAX_NON_SDK_AV_DUMPS)
                TryWriteMiniDump("nonsdk_av");
            return EXCEPTION_CONTINUE_SEARCH;
        }

        // ---- Crash in SDKDLL.dll: attempt frame-unwind to survive ----
        try
        {
            // x86: EXCEPTION_POINTERS.ContextRecord at offset IntPtr.Size (4)
            var pCtx = Marshal.ReadIntPtr(exceptionInfo, IntPtr.Size);

            // ---- Log instruction bytes at crash site (code section → always readable) ----
            if (shouldLog)
            try
            {
                var ib = new System.Text.StringBuilder(64);
                for (int i = 0; i < 16; i++)
                    ib.Append(Marshal.ReadByte(addr, i).ToString("X2")).Append(' ');
                WriteLog($"[VEH] istruzione a +0x{rva:X}: {ib}");
            }
            catch { }

            // ---- Stack-write skip for SDKDLL.dll internal thread (unlimited) ----
            //
            // The DLL's periodic timer thread crashes when its stack is near the committed
            // top: stores to [ESP+0x10] (RVA +0x512D) and [ESP+0x14] (RVA +0x5133) land
            // on the next uncommitted page above the stack.
            //
            // PRIMARY FIX — commit the missing stack page:
            //   The faulting address is a reserved-but-uncommitted page within the thread's
            //   stack reservation. VirtualAlloc(MEM_COMMIT) makes it writable; the failed
            //   instruction re-executes at the same EIP, succeeds, and the timer reschedules
            //   at the normal ~40 s interval. No skip, no ESP drift, no ExitThread, LED OK.
            //
            // FALLBACK — instruction skip + rate-limit (if VirtualAlloc fails, i.e. the
            //   address is beyond the thread's reservation):
            //   Skip +0x512D → +0x5148 (27 B) and +0x5133 → +0x5148 (21 B).
            //   Each skip shifts ESP +4; after 2 skips → ExitThread to prevent the
            //   process-killing stack-overflow that would otherwise occur after ~5 skips.
            if (code == EXCEPTION_ACCESS_VIOLATION && avType == (nint)1)
            {
                try
                {
                    uint esp = (uint)Marshal.ReadInt32(pCtx, CTX_ESP);
                    uint bad = (uint)avBadAddr;
                    uint delta = bad - esp;  // unsigned: wraps large if bad < esp
                    if (delta < 0x40)
                    {
                        // ---- Primary: map the missing page (bounded — see SDK_STACK_GROWTH_CEILING_BYTES) ----
                        // The faulting address is the page just past the thread's stack top.
                        // Try MEM_COMMIT (page within reservation) then MEM_RESERVE|MEM_COMMIT
                        // (page beyond reservation). No VirtualQuery needed — just try both.
                        // We also pre-map the next 3 pages (12 KB extra) so subsequent timer
                        // invocations don't need to map them one by one.
                        long grownSoFar = System.Threading.Interlocked.Read(ref _sdkStackGrownBytes);
                        IntPtr mapped = IntPtr.Zero;
                        if (grownSoFar < SDK_STACK_GROWTH_CEILING_BYTES)
                        {
                            IntPtr badPage = (IntPtr)(bad & ~0xFFFu);
                            mapped = VirtualAlloc(badPage, (IntPtr)0x4000, MEM_COMMIT, PAGE_READWRITE);
                            if (mapped == IntPtr.Zero)
                                mapped = VirtualAlloc(badPage, (IntPtr)0x4000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
                            // Fallback: try single-page in case only the first page is reservable
                            if (mapped == IntPtr.Zero)
                                mapped = VirtualAlloc(badPage, (IntPtr)0x1000, MEM_COMMIT, PAGE_READWRITE);
                            if (mapped == IntPtr.Zero)
                                mapped = VirtualAlloc(badPage, (IntPtr)0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
                            if (mapped != IntPtr.Zero)
                            {
                                long newTotal = System.Threading.Interlocked.Add(ref _sdkStackGrownBytes, 0x4000);
                                if (shouldLog)
                                    WriteLog($"[VEH] +0x{rva:X} stack-write: mapped 4 pages at 0x{bad & ~0xFFFu:X8} → retry at same EIP (thread survives) [cum={newTotal / 1024}KB/{SDK_STACK_GROWTH_CEILING_BYTES / 1024}KB]");
                                StartSdkStackWatchdogIfNeeded(GetCurrentThreadId());
                                // EIP unchanged — instruction re-executes and succeeds.
                                return EXCEPTION_CONTINUE_EXECUTION;
                            }
                            if (shouldLog)
                                WriteLog($"[VEH] +0x{rva:X} stack-write: VirtualAlloc failed err={Marshal.GetLastWin32Error()} → fallback skip");
                        }
                        else if (shouldLog)
                        {
                            WriteLog($"[VEH] +0x{rva:X} stack-write: growth ceiling reached ({grownSoFar / 1024}KB) — stopping extension, fallback skip/ExitThread to avoid a silent stack-overflow crash");
                        }

                        // ---- Fallback: skip + rate-limit ----
                        long nowMs = Environment.TickCount64;
                        if (nowMs > _sdkStackWriteWindowEndMs)
                        {
                            _sdkStackWriteCount = 0;
                            _sdkStackWriteWindowEndMs = nowMs + 500;
                        }
                        int count = System.Threading.Interlocked.Increment(ref _sdkStackWriteCount);

                        if (count <= 2)
                        {
                            int skip = rva switch
                            {
                                0x512D => 0x1B,
                                0x5133 => 0x15,
                                _      => X86InstrLen(addr),
                            };
                            if (skip > 0)
                            {
                                Marshal.WriteIntPtr(pCtx, CTX_EIP, addr + skip);
                                if (shouldLog)
                                {
                                    int totalSkips = _sdkStackWriteSinceLog;
                                    string extra = totalSkips > 1 ? $" #{count}/2 (total x{totalSkips})" : $" #{count}/2";
                                    WriteLog($"[VEH] +0x{rva:X} stack-write skip {skip}B [ESP+0x{delta:X}]{extra} → EIP=0x{(addr + skip):X8}");
                                }
                                StartSdkStackWatchdogIfNeeded(GetCurrentThreadId());
                                return EXCEPTION_CONTINUE_EXECUTION;
                            }
                            WriteLog($"[VEH] +0x{rva:X} stack-write decoder failed (skip=0), fallback ExitThread");
                        }
                        else
                        {
                            SdkRateLimitedExitThread = true;
                            WriteLog($"[VEH] +0x{rva:X} stack-write crash #{count} → ESP drift limit, ExitThread");
                        }
                    }
                }
                catch (Exception exSkip)
                {
                    WriteLog($"[VEH] stack-write handler threw: {exSkip.Message}");
                }
            }

            nint ebp = Marshal.ReadIntPtr(pCtx, CTX_EBP);

            // Walk the EBP chain until the return address leaves SDKDLL.dll
            for (int depth = 0; depth < 64; depth++)
            {
                if (ebp == IntPtr.Zero || ebp < 0x10000)
                    break; // chain ended or corrupted

                nint retAddr  = Marshal.ReadIntPtr(ebp, 4); // [EBP+4]
                nint prevEbp  = Marshal.ReadIntPtr(ebp, 0); // [EBP+0]

                if (retAddr < dllBase || retAddr >= dllEnd)
                {
                    // Found! This return address is outside SDKDLL.dll.
                    // Simulate return: EIP=retAddr, ESP=EBP+8, EBP=prevEbp, EAX=0
                    Marshal.WriteIntPtr(pCtx, CTX_EIP, retAddr);
                    Marshal.WriteIntPtr(pCtx, CTX_ESP, ebp + 8);
                    Marshal.WriteIntPtr(pCtx, CTX_EBP, prevEbp);
                    Marshal.WriteInt32(pCtx,  CTX_EAX, 0);

                    try
                    {
                        WriteLog($"[VEH] SDKDLL.dll crash survived — " +
                                 $"unwound {depth + 1} frame(s) → 0x{retAddr:X8}");
                    }
                    catch { }

                    return EXCEPTION_CONTINUE_EXECUTION;
                }

                ebp = prevEbp;
            }

            // Could not unwind out of SDKDLL.dll.
            // If on a secondary thread (DLL internal thread),
            // we can kill ONLY that thread and keep the process alive.
            uint tid = GetCurrentThreadId();
            bool isDllThread = tid != _uiThreadId;

            if (isDllThread && _exitThreadStub != IntPtr.Zero)
            {
                // Signal the rest of the app that the SDK has crashed
                SdkCrashRecoveryNeeded = true;

                // Redirect EIP to the ExitThread(0) stub: the DLL thread
                // will die, but the process survives.
                Marshal.WriteIntPtr(pCtx, CTX_EIP, _exitThreadStub);
                // ESP must point to a valid stack — use the current ESP
                // (still valid, only EIP was in corrupted code)

                try
                {
                    WriteLog($"[VEH] SDKDLL.dll crash on DLL thread (tid={tid}) — " +
                             "thread killed via ExitThread, process survived");
                    WriteCrashLog($"[VEH] DLL thread tid={tid} terminated (crash at 0x{addr:X8})");
                }
                catch { }

                // NOTE: do NOT call TryWriteMiniDump() here. MiniDumpWriteDump reads the
                // entire process including the corrupted DLL heap; if it triggers a native
                // AV it would re-enter the VEH with reentrancy=2 → CONTINUE_SEARCH → crash.
                return EXCEPTION_CONTINUE_EXECUTION;
            }

            try { WriteLog("[VEH] Frame-walk: no return address outside SDKDLL.dll found"); }
            catch { }
        }
        catch (Exception ex)
        {
            try { WriteLog($"[VEH] Frame-unwind failed: {ex.Message}"); } catch { }
        }

        // NOTE: do NOT call TryWriteMiniDump() — risk of re-entrant VEH crash.
        return EXCEPTION_CONTINUE_SEARCH;
    }

    /// <summary>
    /// Starts the SDK stack watchdog once, the first time the VEH observes the DLL
    /// timer thread survive a stack-write crash. Cheap diagnostic to confirm/refute
    /// whether that thread's stack usage keeps growing over time (see
    /// SDK_STACK_GROWTH_CEILING_BYTES): logs ESP drift every WATCHDOG_INTERVAL_MS.
    /// Never touches the thread's execution — only reads its register state.
    /// </summary>
    private static void StartSdkStackWatchdogIfNeeded(uint dllThreadId)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _sdkWatchdogStartedFlag, 1, 0) != 0)
            return; // already running

        _sdkWatchdogTid = dllThreadId;
        var t = new System.Threading.Thread(SdkStackWatchdogLoop)
        {
            IsBackground = true,
            Name = "K2-SdkStackWatchdog"
        };
        t.Start();
    }

    /// <summary>
    /// Background loop: every WATCHDOG_INTERVAL_MS, briefly suspends the monitored
    /// thread just long enough to snapshot its ESP via GetThreadContext, then resumes
    /// it immediately. Logs how much the stack has moved since the last reading and
    /// since the first ("baseline") reading. Stops on its own once the thread can no
    /// longer be opened (i.e. it has exited — normal shutdown or an ExitThread recovery).
    /// </summary>
    private static void SdkStackWatchdogLoop()
    {
        uint tid = _sdkWatchdogTid;
        WriteLog($"[Watchdog] avviato — monitoro ESP del thread timer SDKDLL.dll (tid={tid}) ogni {WATCHDOG_INTERVAL_MS / 1000}s");

        IntPtr ctxBuf = Marshal.AllocHGlobal(1024);
        nint baselineEsp = 0;
        nint lastEsp = 0;
        bool haveBaseline = false;

        try
        {
            while (true)
            {
                System.Threading.Thread.Sleep(WATCHDOG_INTERVAL_MS);

                IntPtr hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, false, tid);
                if (hThread == IntPtr.Zero)
                {
                    WriteLog($"[Watchdog] OpenThread fallito (tid={tid}, err={Marshal.GetLastWin32Error()}) — " +
                             "il thread è probabilmente terminato, fermo il watchdog");
                    return;
                }

                try
                {
                    uint suspendCount = SuspendThread(hThread);
                    if (suspendCount == 0xFFFFFFFF)
                    {
                        WriteLog("[Watchdog] SuspendThread fallito, salto questo giro");
                        continue;
                    }

                    try
                    {
                        // ContextFlags must be set before the call; CONTEXT_CONTROL covers Esp.
                        Marshal.WriteInt32(ctxBuf, 0, unchecked((int)CONTEXT_CONTROL_X86));
                        bool ok = GetThreadContext(hThread, ctxBuf);
                        if (!ok)
                        {
                            WriteLog($"[Watchdog] GetThreadContext fallito err={Marshal.GetLastWin32Error()}");
                            continue;
                        }

                        nint esp = Marshal.ReadIntPtr(ctxBuf, CTX_ESP);
                        if (!haveBaseline)
                        {
                            baselineEsp = esp;
                            lastEsp = esp;
                            haveBaseline = true;
                            WriteLog($"[Watchdog] baseline ESP=0x{esp:X8}");
                        }
                        else
                        {
                            // Stack grows toward lower addresses: a positive delta means
                            // the stack has grown (used more space) since that reading.
                            long fromLast = (long)lastEsp - (long)esp;
                            long fromBaseline = (long)baselineEsp - (long)esp;
                            string sLast = fromLast >= 0 ? $"+{fromLast}" : fromLast.ToString();
                            string sBase = fromBaseline >= 0 ? $"+{fromBaseline}" : fromBaseline.ToString();
                            WriteLog($"[Watchdog] ESP=0x{esp:X8}  Δultimo={sLast}B  Δbaseline={sBase}B  " +
                                     $"[cum VirtualAlloc={System.Threading.Interlocked.Read(ref _sdkStackGrownBytes)}B]");
                            lastEsp = esp;
                        }
                    }
                    finally
                    {
                        ResumeThread(hThread);
                    }
                }
                finally
                {
                    CloseHandle(hThread);
                }
            }
        }
        catch (Exception ex)
        {
            try { WriteLog($"[Watchdog] loop terminato per eccezione: {ex.Message}"); } catch { }
        }
        finally
        {
            Marshal.FreeHGlobal(ctxBuf);
        }
    }

    /// <summary>
    /// Minimal x86 (32-bit) instruction length decoder.
    /// Handles the most common memory-access opcodes that can cause access violations.
    /// Returns 0 if the instruction is not recognized (caller must fall back).
    /// </summary>
    private static int X86InstrLen(nint addr)
    {
        int pos = 0;

        // Consume legacy prefixes (operand/address size, segment, REP, LOCK)
        for (int p = 0; p < 4; p++)
        {
            byte b0 = Marshal.ReadByte(addr, pos);
            if (b0 is 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65
                    or 0x66 or 0x67 or 0xF0 or 0xF2 or 0xF3)
                pos++;
            else
                break;
        }

        byte op = Marshal.ReadByte(addr, pos++);

        // Two-byte escape (0F xx ...)
        if (op == 0x0F)
        {
            byte op2 = Marshal.ReadByte(addr, pos++);
            // 0F B6/B7 MOVZX, 0F BE/BF MOVSX, 0F 44/45/4F CMOV*, 0F 10-17 SSE MOV
            // 0F xx with ModRM (common memory-access 2-byte opcodes)
            bool has2 = op2 is 0xB6 or 0xB7 or 0xBE or 0xBF   // MOVZX/MOVSX
                           or 0x10 or 0x11 or 0x28 or 0x29 or 0x2B // SSE MOV
                           or (>= 0x40 and <= 0x4F);              // CMOVcc
            // 0F 80-8F are Jcc rel32 (no ModRM) — handled by "return 0" fallthrough
            if (has2) return pos + ModRmLen(addr, pos);
            return 0; // Unknown 2-byte opcode
        }

        // ---- Instructions with no ModRM ----
        if (op is >= 0x50 and <= 0x5F) return pos;          // PUSH/POP reg
        if (op is >= 0x90 and <= 0x97) return pos;          // XCHG/NOP
        if (op is 0xC3 or 0xCB or 0xC9 or 0x90) return pos; // RET/LEAVE/NOP
        if (op is >= 0xB8 and <= 0xBF) return pos + 4;     // MOV reg32, imm32
        if (op is 0x68) return pos + 4;                     // PUSH imm32
        if (op is 0x6A) return pos + 1;                     // PUSH imm8
        if (op is 0xE8 or 0xE9) return pos + 4;            // CALL/JMP rel32
        if (op is 0xEB) return pos + 1;                     // JMP rel8

        // ---- Instructions with ModRM ----
        int imm = 0;
        switch (op)
        {
            case 0x80: imm = 1; break;  // OP r/m8, imm8
            case 0x81: imm = 4; break;  // OP r/m32, imm32
            case 0x83: imm = 1; break;  // OP r/m32, imm8sign
            case 0x85: break;           // TEST r/m32, r32
            case 0x87: break;           // XCHG r/m32, r32
            case 0x88: break;           // MOV r/m8, r8
            case 0x89: break;           // MOV r/m32, r32
            case 0x8A: break;           // MOV r8, r/m8
            case 0x8B: break;           // MOV r32, r/m32
            case 0x8D: break;           // LEA r32, m
            case 0x8F: break;           // POP r/m32
            case 0xC6: imm = 1; break;  // MOV r/m8, imm8
            case 0xC7: imm = 4; break;  // MOV r/m32, imm32
            case 0xD1: break;           // SHL/SHR r/m32, 1
            case 0xD3: break;           // SHL/SHR r/m32, CL
            case 0xF6: imm = 1; break;  // TEST r/m8, imm8 (only for /0); others no imm
            case 0xF7: break;           // TEST/NOT/NEG/MUL/DIV r/m32
            case 0xFF: break;           // INC/DEC/CALL/PUSH r/m32
            case 0x01: break;           // ADD r/m32, r32
            case 0x03: break;           // ADD r32, r/m32
            case 0x09: break;           // OR r/m32, r32
            case 0x0B: break;           // OR r32, r/m32
            case 0x21: break;           // AND r/m32, r32
            case 0x23: break;           // AND r32, r/m32
            case 0x29: break;           // SUB r/m32, r32
            case 0x2B: break;           // SUB r32, r/m32
            case 0x31: break;           // XOR r/m32, r32
            case 0x33: break;           // XOR r32, r/m32
            case 0x39: break;           // CMP r/m32, r32
            case 0x3B: break;           // CMP r32, r/m32
            default: return 0;          // Unknown opcode
        }

        // For 0xF6 with /0 (TEST): has imm8; other /N (NOT,NEG,MUL,DIV): no imm
        if (op == 0xF6)
        {
            byte mrm = Marshal.ReadByte(addr, pos);
            imm = ((mrm >> 3) & 7) == 0 ? 1 : 0; // /0 = TEST, has imm8
        }

        return pos + ModRmLen(addr, pos) + imm;
    }

    /// <summary>
    /// Returns the byte count of a ModRM byte (+ optional SIB + displacement).
    /// Does NOT include the ModRM byte itself.
    /// </summary>
    private static int ModRmLen(nint addr, int modrm_pos)
    {
        byte modrm = Marshal.ReadByte(addr, modrm_pos);
        int mod = (modrm >> 6) & 3;
        int rm  = modrm & 7;
        int len = 1; // ModRM byte itself

        if (mod == 3) return len;       // register operand — no memory

        bool hasSib = (rm == 4);       // r/m=4 → SIB follows (except mod=3)
        if (hasSib) len++;             // SIB byte

        if (mod == 1) len += 1;        // disp8
        else if (mod == 2) len += 4;   // disp32
        else if (mod == 0)
        {
            if (rm == 5)
                len += 4;              // disp32 (EBP-relative or abs)
            else if (hasSib)
            {
                // SIB with mod=0: base=101 (EBP/none) → +disp32
                byte sib = Marshal.ReadByte(addr, modrm_pos + 1);
                if ((sib & 7) == 5) len += 4;
            }
        }

        return len;
    }

    private static void ShowError(string title, string message)
    {
        try
        {
            MessageBox.Show($"{message}\n\n(log: {LogPath})", title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* never throw inside an error handler */ }
    }

    /// <summary>Logs process exit — catches native crashes that bypass managed handlers.</summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        WriteLog($"=== ProcessExit {DateTime.Now:O} exitCode={Environment.ExitCode} ===");
        // ExitCode != 0 on native crash (access violation = -1073741819 / 0xC0000005)
        if (Environment.ExitCode != 0)
        {
            WriteCrashLog($"Process exit with code {Environment.ExitCode} (0x{Environment.ExitCode:X8})");
            TryWriteMiniDump();
        }
    }

    /// <summary>Writes a separate timestamped crash log, easier to find.</summary>
    public static void WriteCrashLog(string text)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:O}] pid={Environment.ProcessId} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Generates a .dmp minidump for post-mortem analysis.</summary>
    /// <param name="tag">Optional suffix identifying why the dump was taken (e.g. "nonsdk_av"), for easier triage when multiple dumps pile up.</param>
    private static void TryWriteMiniDump(string tag = "")
    {
        try
        {
            var suffix = string.IsNullOrEmpty(tag) ? "" : $"_{tag}";
            var dumpPath = Path.Combine(AppContext.BaseDirectory,
                $"K2.App_{DateTime.Now:yyyyMMdd_HHmmss}{suffix}.dmp");
            using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write);
            // MiniDumpWithDataSegs | MiniDumpWithHandleData = 0x01 | 0x04
            bool ok = MiniDumpWriteDump(GetCurrentProcess(),
                (uint)Environment.ProcessId, fs.SafeFileHandle.DangerousGetHandle(),
                0x05, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            WriteLog(ok ? $"[Crash] MiniDump written: {dumpPath}"
                        : $"[Crash] MiniDump failed, err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { WriteLog("[Crash] TryWriteMiniDump threw: " + ex.Message); }
    }

    /// <summary>Thread-safe append to the log file next to the executable.</summary>
    public static void WriteLog(string text)
    {
        try
        {
            lock (LogPath)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
            }
        }
        catch { /* never throw inside the logger */ }
    }
}
