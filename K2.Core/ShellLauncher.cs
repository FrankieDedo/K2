using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace K2.Core;

/// <summary>
/// Launches programs for button actions. K2 runs elevated (<c>app.manifest</c>,
/// <c>requireAdministrator</c>), and anything it starts normally inherits that admin
/// token — which makes Windows (UIPI) block drag &amp; drop from the regular, non-elevated
/// Explorer/desktop into the launched app. When K2 is elevated we therefore start the
/// program with the desktop shell's own (medium-integrity) token, so it behaves exactly
/// as if the user had opened it from Explorer. Executables get the shell token directly
/// (arguments/working dir preserved); everything else (documents, URLs, folders, .lnk)
/// is handed to <c>explorer.exe</c>, which opens it in the shell's context. If anything
/// fails (no shell, target that itself requires admin, ...) we fall back to a plain
/// <see cref="Process.Start(ProcessStartInfo)"/>.
/// </summary>
internal static class ShellLauncher
{
    private static readonly bool _elevated = IsElevated();

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Starts <paramref name="file"/> (ShellExecute semantics) without inheriting K2's elevation.</summary>
    public static void Start(string file, string args = "", string? workingDir = null, bool hidden = false)
    {
        if (_elevated && TryStartDeElevated(file, args, workingDir, hidden)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = workingDir ?? "",
            UseShellExecute = true,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        });
    }

    private static bool TryStartDeElevated(string file, string args, string? workingDir, bool hidden)
    {
        try
        {
            string exe = file, cmdArgs = args;
            if (!IsExecutable(file))
            {
                // Document / URL / folder / shortcut: let the desktop shell open it.
                exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                cmdArgs = $"\"{file}\"";
            }
            else if (!Path.IsPathRooted(exe))
            {
                exe = Resolve(exe) ?? exe;
            }

            var shellWnd = GetShellWindow();
            if (shellWnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(shellWnd, out uint pid);
            if (pid == 0) return false;

            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return false;
            IntPtr hTok = IntPtr.Zero, hPrimary = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out hTok)) return false;
                if (!DuplicateTokenEx(hTok, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hPrimary))
                    return false;

                uint flags = CREATE_UNICODE_ENVIRONMENT;
                if (!CreateEnvironmentBlock(out env, hPrimary, false)) { env = IntPtr.Zero; flags = 0; }

                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                if (hidden) { si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_HIDE; }

                string? cwd = string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(exe) : workingDir;
                if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = null;

                bool ok = CreateProcessWithTokenW(hPrimary, 0, exe, $"\"{exe}\" {cmdArgs}".TrimEnd(),
                    flags, env, cwd, ref si, out var pi);
                if (!ok) return false;
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                return true;
            }
            finally
            {
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (hPrimary != IntPtr.Zero) CloseHandle(hPrimary);
                if (hTok != IntPtr.Zero) CloseHandle(hTok);
                CloseHandle(hProc);
            }
        }
        catch { return false; }
    }

    private static bool IsExecutable(string file) =>
        file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? Resolve(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        var sys = Path.Combine(Environment.SystemDirectory, name);
        return File.Exists(sys) ? sys : null;
    }

    // ── P/Invoke ─────────────────────────────────────
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_DUPLICATE = 0x2, TOKEN_QUERY = 0x8, TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const int STARTF_USESHOWWINDOW = 1;
    private const short SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int impLevel, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string app, string cmdLine,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
}
