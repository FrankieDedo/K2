// MainWindow.MediaDock.cs — partial: Media Dock stub (UI panel removed).
// Keeps only init/cleanup to avoid breaking references in MainWindow.Everest.cs.
// Dock detection remains available via _everest.IsMMDockPlugged().

using System.Runtime.InteropServices;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Stub — UI panel removed. No initialization needed.</summary>
    private void InitMediaDockPanel() { }

    private void CleanupMediaDock() { }

    // Win32 for RAM monitoring (kept for future use)
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint   dwLength;
        public uint   dwMemoryLoad;
        public ulong  ullTotalPhys;
        public ulong  ullAvailPhys;
        public ulong  ullTotalPageFile;
        public ulong  ullAvailPageFile;
        public ulong  ullTotalVirtual;
        public ulong  ullAvailVirtual;
        public ulong  ullAvailExtendedVirtual;
    }
}
