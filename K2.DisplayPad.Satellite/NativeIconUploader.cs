using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace K2.DisplayPad.Satellite;

/// <summary>
/// Uploads a single DisplayPad button icon by talking to <c>DisplayPadSDK.dll</c> directly via
/// <c>SetIconPacket</c>, instead of going through the <c>DisplayPad.SDK</c> NuGet convenience
/// wrapper (<see cref="DisplayPad.SDK.DisplayPadHelper.UploadImage"/> /
/// <see cref="DisplayPad.SDK.DisplayPadHelper.UploadImageBySetIconPic"/>).
///
/// Background (2026-07-01): icons kept corrupting on profile switch even after serializing every
/// call through a lock, moving cache-file writes inside that lock, and raising the post-upload
/// settle delay to 400ms — none of that fixed it. Comparing with the decompiled original Base
/// Camp worker (<c>K2/_reference/decompiled/Worker/DisplayPadWorker.Helpers/DisplayPadSDK.cs</c>
/// + <c>DisplayPadOperations.cs</c>) showed BC never calls the convenience wrapper at all: it has
/// its own private P/Invoke declarations straight onto the native DLL and manually builds the 31
/// USB packets itself, calling <c>SetIconPacket</c> directly. This class replicates that exact
/// path (resize to 102×102, rounded-corner mask, BGR packet layout) instead of trusting the
/// wrapper to do the same thing internally — the wrapper is the one piece BC's own code avoids.
/// </summary>
internal static class NativeIconUploader
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PicPacket
    {
        public byte byReportID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024, ArraySubType = UnmanagedType.U1)]
        public byte[] byData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PicPacketInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 31, ArraySubType = UnmanagedType.Struct)]
        public PicPacket[] picPacket;
    }

    [DllImport("DisplayPadSDK.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool SetIconPacket(byte byIconNumber, ref PicPacketInfo pData, uint ID);

    /// <summary>
    /// Uploads <paramref name="imagePath"/> to button <paramref name="buttonIndex"/> of device
    /// <paramref name="deviceId"/>. Caller must already hold the same lock used for every other
    /// SDK call (see <c>SdkHandler._sdkLock</c>) — this method does not lock internally.
    /// <paramref name="pressed"/> reproduces BC's hardware press-bounce
    /// (<c>DisplayPadOperations.UploadImage</c>'s <c>IsBtnPressed</c> branch in the decompiled
    /// worker): the icon is rendered at 80×80 instead of 102×102, then centered on a black
    /// 102×102 canvas (11px margin all around, via <see cref="DrawWithBorder"/>) instead of
    /// filling the whole tile.
    /// </summary>
    public static bool Upload(string imagePath, int buttonIndex, uint deviceId, bool pressed = false)
    {
        try
        {
            byte[] fileBytes = File.ReadAllBytes(imagePath);
            using var ms = new MemoryStream(fileBytes);
            using var source = (Bitmap)Image.FromStream(ms);

            const int fullSize = 102;
            int innerSize = pressed ? 80 : fullSize;
            using var resized = ResizeImage(source, innerSize, innerSize);
            var inner = new Bitmap(innerSize, innerSize, PixelFormat.Format32bppRgb);
            const int cornerRadius = 40;
            using (Graphics g = Graphics.FromImage(inner))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new TextureBrush(resized);
                using var path = new GraphicsPath();
                path.AddArc(0, 0, cornerRadius, cornerRadius, 180f, 90f);
                path.AddArc(inner.Width - cornerRadius, 0, cornerRadius, cornerRadius, 270f, 90f);
                path.AddArc(inner.Width - cornerRadius, inner.Height - cornerRadius, cornerRadius, cornerRadius, 0f, 90f);
                path.AddArc(0, inner.Height - cornerRadius, cornerRadius, cornerRadius, 90f, 90f);
                g.FillPath(brush, path);
            }

            Bitmap canvas;
            if (pressed)
            {
                canvas = DrawWithBorder(inner, (fullSize - innerSize) / 2);
                inner.Dispose();
            }
            else
            {
                canvas = inner;
            }
            using (canvas)
            {
                byte[] pixelBytes = GetBitmapBytes(canvas);
                var pData = new PicPacketInfo { picPacket = new PicPacket[31] };
                int outIndex = 0;
                // canvas is exactly fullSize×fullSize (32bpp, no row padding since fullSize*4 is a
                // multiple of 4), so every pixel is in range — no need for BC's rectangle-
                // containment check here.
                for (int i = 0; i + 3 < pixelBytes.Length; i += 4)
                {
                    byte b = pixelBytes[i];
                    byte g8 = pixelBytes[i + 1];
                    byte r = pixelBytes[i + 2];
                    WriteByte(ref pData, outIndex * 3, b);
                    WriteByte(ref pData, outIndex * 3 + 1, g8);
                    WriteByte(ref pData, outIndex * 3 + 2, r);
                    outIndex++;
                }

                byte iconNumber = Convert.ToByte(buttonIndex);
                return SetIconPacket(iconNumber, ref pData, deviceId);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"[NativeIconUploader] Upload failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Centers <paramref name="bmp"/> on a black square canvas <paramref name="margin"/>
    /// pixels larger on every side (mirrors BC's <c>DrawBitmapWithBorder</c>).</summary>
    private static Bitmap DrawWithBorder(Bitmap bmp, int margin)
    {
        int size = bmp.Width + margin * 2;
        var result = new Bitmap(size, size, PixelFormat.Format32bppRgb);
        using Graphics g = Graphics.FromImage(result);
        g.Clear(Color.Black);
        g.DrawImage(bmp, margin, margin, bmp.Width, bmp.Height);
        return result;
    }

    private static void WriteByte(ref PicPacketInfo pData, int byteOffset, byte value)
    {
        int packetIndex = byteOffset / 1024;
        int inPacketOffset = byteOffset % 1024;
        if (packetIndex >= pData.picPacket.Length) return; // safety net, should never trigger for 102x102
        if (pData.picPacket[packetIndex].byData is null)
            pData.picPacket[packetIndex].byData = new byte[1024];
        pData.picPacket[packetIndex].byData[inPacketOffset] = value;
        pData.picPacket[packetIndex].byReportID = 0;
    }

    private static Bitmap ResizeImage(Image image, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        bitmap.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
        }
        return bitmap;
    }

    private static byte[] GetBitmapBytes(Bitmap image)
    {
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
            ImageLockMode.ReadOnly, image.PixelFormat);
        try
        {
            int length = data.Stride * image.Height;
            byte[] result = new byte[length];
            Marshal.Copy(data.Scan0, result, 0, length);
            return result;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }
}
