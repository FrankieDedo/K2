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
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>PrintWindow flag that renders the whole window, composited layers included. The
    /// documented flag (PW_CLIENTONLY = 1) predates DWM and comes back blank for most modern
    /// windows; 2 is what actually works.</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>Main window of the first running process with this name (no extension, the same
    /// spelling <c>GameProfileDefinition.ExeName</c> uses), or zero when the program isn't
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

        // A window that is NOT required to be in front is very likely behind K2 itself — that is
        // the studio's and the calibration dialog's whole situation. Copying from the screen there
        // would return K2's own pixels, so the window is asked to draw itself instead. Falls back
        // to the screen copy when it comes back blank, which is what a game rendering through
        // DirectX usually does.
        if (!requireForeground && TryPrintWindow(hwnd, size) is { } printed) return printed;

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

    /// <summary>Asks the window to render itself into a bitmap — works while it is covered, which
    /// <see cref="Graphics.CopyFromScreen"/> cannot do. Returns null when the window refuses (the
    /// call fails) or draws nothing at all, so the caller can fall back.
    ///
    /// <para>The result is the WINDOW, frame and all, so it is cropped down to the client area to
    /// match what the screen copy returns — a probe's rectangle is relative to the client area and
    /// the two paths must agree about what it is relative to.</para></summary>
    private static Bitmap? TryPrintWindow(IntPtr hwnd, Size clientSize)
    {
        Bitmap? full = null;
        try
        {
            if (!GetWindowRect(hwnd, out var wr)) return null;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0) return null;

            full = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                try { if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)) return null; }
                finally { g.ReleaseHdc(hdc); }
            }

            if (IsBlank(full)) return null;

            // Client area inside the window rectangle.
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) return null;
            var crop = new Rectangle(origin.X - wr.Left, origin.Y - wr.Top,
                                     clientSize.Width, clientSize.Height);
            crop.Intersect(new Rectangle(0, 0, w, h));
            if (crop.Width <= 0 || crop.Height <= 0) return null;

            var client = new Bitmap(clientSize.Width, clientSize.Height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(client))
                g.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
            return client;
        }
        catch { return null; }
        finally { full?.Dispose(); }
    }

    /// <summary>True when the bitmap is entirely one colour — how a window that ignored
    /// PrintWindow comes back. Sampled on a coarse grid: proving it exactly would cost more than
    /// the capture it is guarding.</summary>
    private static bool IsBlank(Bitmap bmp)
    {
        int stepX = Math.Max(1, bmp.Width / 32), stepY = Math.Max(1, bmp.Height / 32);
        int first = bmp.GetPixel(0, 0).ToArgb();
        for (int y = 0; y < bmp.Height; y += stepY)
            for (int x = 0; x < bmp.Width; x += stepX)
                if (bmp.GetPixel(x, y).ToArgb() != first) return false;
        return true;
    }
}
