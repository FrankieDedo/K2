using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Grabs the pixels of ONE window's client area, so a screen probe can read a value a program
/// only shows on screen (see <see cref="ScreenProbe"/>).
///
/// <para>
/// <b>Why the screen and not PrintWindow.</b> <c>PrintWindow</c> asks a window to redraw itself
/// into a bitmap, which works for ordinary GDI/WPF windows and returns BLACK for a Direct3D game:
/// the frame the player sees was never drawn through GDI, it came out of a swap chain. Since the
/// whole point here is reading game HUDs, the capture is a copy of the SCREEN — but cropped, at
/// the copy itself, to the target window's client rectangle: K2 never holds a picture of the
/// whole desktop, and never of a window it wasn't pointed at.
/// </para>
///
/// <para>
/// The consequence to be honest about: this reads what is actually on screen at that position, so
/// it needs the window visible. A minimised window, one on another virtual desktop, or one buried
/// under another window reads as "no frame" (or, if buried, would read the wrong pixels — which is
/// why <see cref="TryCaptureClient"/> refuses a window that isn't the foreground one). While you
/// are playing the game, that condition holds by construction.
/// </para>
///
/// <para><b>Exclusive fullscreen is not supported</b> and cannot be: the desktop compositor has no
/// pixels for it. Borderless/windowed fullscreen — what most games default to, and what Deadside's
/// <c>FullscreenMode=1</c> means — works.</para>
/// </summary>
internal static class WindowCapture
{
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>Main window of the first running process with this name (no extension, the same
    /// spelling <c>GameProfileCatalog.Definition.ExeName</c> uses), or zero when the program isn't
    /// running or has no window yet.</summary>
    public static IntPtr FindWindow(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return IntPtr.Zero;
        try
        {
            foreach (var p in Process.GetProcessesByName(processName.Trim()))
                using (p)
                {
                    IntPtr h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h)) return h;
                }
        }
        catch { /* the process can exit between the enumeration and the read */ }
        return IntPtr.Zero;
    }

    /// <summary>Programs the probe dialog offers as a capture target: everything with a visible
    /// main window and a title, K2 itself excluded (a probe reading K2's own window would be a
    /// mirror, and the dialog would be reading itself while the user drags the rectangle).</summary>
    public static IReadOnlyList<(string Process, string Title)> ListCapturable()
    {
        var seen = new List<(string, string)>();
        try
        {
            string self = Process.GetCurrentProcess().ProcessName;
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    try
                    {
                        if (string.Equals(p.ProcessName, self, StringComparison.OrdinalIgnoreCase)) continue;
                        IntPtr h = p.MainWindowHandle;
                        if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) continue;
                        string title = p.MainWindowTitle;
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        if (seen.Any(x => string.Equals(x.Item1, p.ProcessName, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        seen.Add((p.ProcessName, title));
                    }
                    catch { /* access denied on a protected process: skip it */ }
                }
        }
        catch { }
        return seen.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Size of a window's client area in pixels, or null when the handle is gone.</summary>
    public static Size? ClientSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        return w > 0 && h > 0 ? new Size(w, h) : null;
    }

    /// <summary>A bitmap of the window's client area, or null when it can't be read right now.
    /// The caller owns the bitmap.
    ///
    /// <para><paramref name="requireForeground"/> is the safety rule for LIVE reading: another
    /// window on top of the game would otherwise be measured as if it were the game, and a probe
    /// silently reporting the pixels of a browser window is worse than one reporting nothing. The
    /// calibration dialog passes false, because there the user is deliberately looking at K2 while
    /// the game sits behind it.</para></summary>
    public static Bitmap? TryCaptureClient(IntPtr hwnd, bool requireForeground)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        if (requireForeground && GetForegroundWindow() != hwnd) return null;
        if (ClientSize(hwnd) is not { } size) return null;

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return null;

        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(origin.X, origin.Y, 0, 0, size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            // A capture can fail on a screen lock, a resolution change mid-copy, or a window that
            // moved off-screen between the two calls. None of those deserve a log line every tick.
            bmp?.Dispose();
            return null;
        }
    }
}
