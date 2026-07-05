using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Helper per convertire e caricare immagini sui sub-device della tastiera
/// Everest Max (numpad display keys, Media Dock screensaver).
///
/// <para>
/// Le immagini vengono ridimensionate alla risoluzione del target e convertite
/// in formato RGB565 (R5 G6 B5, little-endian) come fa <c>UploadImageInHw</c>
/// di Base Camp. La conversione replica esattamente il codice IL decompilato:
/// <c>R >> 3 &lt;&lt; 11 | G >> 2 &lt;&lt; 5 | B >> 3</c>, serializzato come
/// LoByte/HiByte (little-endian).
/// </para>
/// </summary>
internal static class EverestImageUploader
{
    /// <summary>Target per l'upload di un'immagine.</summary>
    public enum PicTarget
    {
        /// <summary>Screensaver del Media Dock: 240×204 px, targetDev=0 targetPic=1.</summary>
        MMDockScreensaver,
        /// <summary>Numpad display key (strip): 128×32 px, targetDev=0 targetPic=2+.</summary>
        NumpadStrip,
        /// <summary>Numpad display key (quadrato): 72×72 px, targetDev=1.</summary>
        NumpadSquare,
    }

    /// <summary>Dimensioni per ciascun target.</summary>
    public static (int w, int h) GetTargetSize(PicTarget target) => target switch
    {
        PicTarget.MMDockScreensaver => (240, 204),
        PicTarget.NumpadStrip       => (128, 32),
        PicTarget.NumpadSquare      => (72,  72),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>
    /// Carica un'immagine da file o base64 su un sub-device.
    /// </summary>
    /// <param name="imagePathOrBase64">Percorso file PNG/JPG o stringa base64 (con o senza prefisso data:image).</param>
    /// <param name="target">Tipo di target (determina risoluzione).</param>
    /// <param name="picSlot">Slot immagine sul target (es. 0-3 per le 4 display key).</param>
    /// <param name="subItem">Sub-item (usato per numpad display key: indice 0-3).</param>
    /// <returns>true se l'SDK ha accettato l'upload.</returns>
    public static bool UploadImage(string imagePathOrBase64, PicTarget target, byte picSlot, byte subItem = 0)
    {
        // --- 1. Decodifica immagine sorgente ---
        byte[] rawBytes;
        if (imagePathOrBase64.Contains("data:image"))
        {
            var b64 = imagePathOrBase64.Replace("data:image/png;base64,", "")
                                       .Replace("data:image/jpeg;base64,", "");
            rawBytes = Convert.FromBase64String(b64);
        }
        else
        {
            rawBytes = File.ReadAllBytes(imagePathOrBase64);
        }

        using var ms = new MemoryStream(rawBytes);
        using var srcBitmap = new Bitmap(ms);

        // --- 2. Ridimensiona alla risoluzione target ---
        var (w, h) = GetTargetSize(target);
        using var resized = ResizeImage(srcBitmap, w, h);

        // --- 3. Converti in RGB565 ---
        byte[] rgb565 = BitmapToRgb565(resized);

        // --- 4. Prepara PicUpdateInfo ---
        byte targetDev, targetPic;
        switch (target)
        {
            case PicTarget.MMDockScreensaver:
                targetDev = 0; targetPic = 1;
                break;
            case PicTarget.NumpadStrip:
                targetDev = 0; targetPic = (byte)(2 + picSlot);
                break;
            case PicTarget.NumpadSquare:
                targetDev = 1; targetPic = picSlot;
                break;
            default:
                return false;
        }

        // --- 5. Upload via SDK ---
        return UploadRgb565(rgb565, targetDev, targetPic, subItem);
    }

    /// <summary>
    /// Loads an image from an already in-memory Bitmap.
    /// </summary>
    public static bool UploadBitmap(Bitmap bitmap, PicTarget target, byte picSlot, byte subItem = 0)
    {
        var (w, h) = GetTargetSize(target);
        using var resized = ResizeImage(bitmap, w, h);
        byte[] rgb565 = BitmapToRgb565(resized);

        byte targetDev, targetPic;
        switch (target)
        {
            case PicTarget.MMDockScreensaver:
                targetDev = 0; targetPic = 1;
                break;
            case PicTarget.NumpadStrip:
                targetDev = 0; targetPic = (byte)(2 + picSlot);
                break;
            case PicTarget.NumpadSquare:
                targetDev = 1; targetPic = picSlot;
                break;
            default:
                return false;
        }

        return UploadRgb565(rgb565, targetDev, targetPic, subItem);
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Converte un Bitmap in un array di byte RGB565 (little-endian).
    /// Replica esattamente il loop di <c>Everest.UploadImageInHw</c>:
    /// per ogni pixel BGR24, calcola <c>B>>3 | (G>>2 &lt;&lt; 5) | (R>>3 &lt;&lt; 11)</c>
    /// e scrive 2 byte little-endian.
    /// </summary>
    internal static byte[] BitmapToRgb565(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int pixelCount = w * h;
        byte[] result = new byte[pixelCount * 2];

        // Leggiamo i pixel come BGR24 (Format24bppRgb)
        // Per efficienza usiamo LockBits
        var rect = new Rectangle(0, 0, w, h);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = bmpData.Stride;
            int srcBytes = stride * h;
            byte[] raw = new byte[srcBytes];
            Marshal.Copy(bmpData.Scan0, raw, 0, srcBytes);

            int dst = 0;
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int srcIdx = rowBase + x * 3;
                    byte b = raw[srcIdx];       // Format24bppRgb = BGR
                    byte g = raw[srcIdx + 1];
                    byte r = raw[srcIdx + 2];

                    // RGB565: R(5 bit) << 11 | G(6 bit) << 5 | B(5 bit)
                    int r5 = r >> 3;
                    int g6 = g >> 2;
                    int b5 = b >> 3;
                    int rgb565 = (r5 << 11) | (g6 << 5) | b5;

                    // Little-endian (LoByte, HiByte)
                    result[dst++] = (byte)(rgb565 & 0xFF);
                    result[dst++] = (byte)((rgb565 >> 8) & 0xFF);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        return result;
    }

    /// <summary>Ridimensiona un'immagine alla dimensione target.</summary>
    internal static Bitmap ResizeImage(Bitmap source, int targetW, int targetH)
    {
        // Usa Format32bppPArgb (0x00E09B) come BC usa PixelFormat 137224 = 0x00021808
        // which is Format16bppRgb565 for the intermediate bitmap. We use 32bpp for
        // resize quality, then convert to RGB565 in the next step.
        var result = new Bitmap(targetW, targetH, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, targetW, targetH);
        }
        return result;
    }

    /// <summary>
    /// Sends already-prepared RGB565 data to the SDK via <see cref="EverestSdkNative.StartPicUpdate"/>.
    /// Alloca e libera la memoria nativa automaticamente.
    /// </summary>
    private static bool UploadRgb565(byte[] rgb565, byte targetDev, byte targetPic, byte subItem)
    {
        IntPtr nativeBuf = Marshal.AllocHGlobal(rgb565.Length);
        try
        {
            Marshal.Copy(rgb565, 0, nativeBuf, rgb565.Length);

            var info = new EverestSdkNative.PicUpdateInfo
            {
                pImageData      = nativeBuf,
                dwDataLength    = (uint)rgb565.Length,
                byTargetDev     = targetDev,
                byTargetPic     = targetPic,
                byTargetSubItem = subItem,
            };

            return EverestSdkNative.StartPicUpdate(info);
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuf);
        }
    }
}
