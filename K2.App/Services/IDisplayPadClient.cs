using System;
using System.Collections.Generic;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>
/// Common surface for the two DisplayPad backends:
/// <list type="bullet">
/// <item><see cref="DisplayPadSatelliteClient"/> — SDK path (DisplayPadSDK.dll via the satellite process).</item>
/// <item><see cref="DisplayPadNativeClient"/> — raw USB-HID path (no SDK, protocol from BaseCampLinux).</item>
/// </list>
/// Selected at startup via <c>AppSettings.DisplayPadNativeEngine</c>.
/// </summary>
public interface IDisplayPadClient : IDisposable
{
    event EventHandler<JsonElement>? PlugEvent;
    event EventHandler<JsonElement>? KeyEvent;
    event EventHandler<JsonElement>? ProgressEvent;
    event EventHandler<string>? SatelliteLog;

    bool IsConnected { get; }

    bool Connect(int timeoutMs = 8000);
    void Disconnect();

    JsonElement? Open();
    JsonElement? Close();
    int SdkVersion();
    List<int> DeviceIds();
    bool IsPlugged(int id);
    string FirmwareVersion(int id);
    int GetBrightness(int id);
    bool SetBrightness(int id, int level);
    bool SwitchProfile(int id, int profile);
    bool APEnable(int id, bool enable);
    bool ResetPictures(int id);

    /// <summary>
    /// Uploads <paramref name="path"/> to <paramref name="btn"/>. When <paramref name="pressed"/>
    /// is true, the icon is re-rendered shrunk (like Base Camp's hardware press-bounce:
    /// <c>DisplayPadOperations.UploadImage</c>'s <c>IsBtnPressed</c> branch in the decompiled
    /// worker shrinks the icon to 80×80 centered on a black 102×102 canvas) instead of the normal
    /// full-size icon — callers re-upload the same key with <c>pressed: true</c> on key-down and
    /// <c>pressed: false</c> on key-up to reproduce that feedback; there is no separate device
    /// animation, it's just a fast re-render + re-upload of the same icon at a different inner size.
    /// </summary>
    bool UploadImage(int id, string path, int btn, int rotation = 0, bool pressed = false);
    bool UploadImageToProfile(int id, string path, int btn, int profile, int rotation = 0);
    bool Ping();

    /// <summary>
    /// Fast path for animation (<see cref="DpGifAnimator"/>/<see cref="DpFullscreenAnimator"/>):
    /// uploads an ALREADY-decoded, already-sized (<c>DpHidNative.IconSize</c>²), already
    /// device-rotated 24bpp BGR buffer directly — no file I/O, no GDI+ decode/resize/rotate.
    /// Returns false if the backend doesn't support raw uploads (currently only
    /// <see cref="DisplayPadNativeClient"/> does); callers must fall back to the normal
    /// <see cref="UploadImage"/> path (with <c>rotation</c> passed through normally) in
    /// that case. <paramref name="bgr"/> must be exactly <c>IconSize*IconSize*3</c> bytes.
    /// </summary>
    bool TryUploadRawBgr(int id, byte[] bgr, int btn);

    /// <summary>
    /// True if this backend supports <see cref="TryUploadRawPanel"/> — currently only the
    /// native engine (a single 800×240 wire transfer instead of 12 sequential icon
    /// transfers). Callers (<see cref="DpFullscreenAnimator"/>) should check this once
    /// before committing to the full-panel code path and fall back to per-tile uploads
    /// otherwise (the satellite/SDK has no equivalent whole-panel command).
    /// </summary>
    bool SupportsRawPanel { get; }

    /// <summary>
    /// Uploads a full <c>DpHidNative.PanelW</c>×<c>PanelH</c> (800×240) BGR buffer in ONE
    /// wire transfer instead of 12 separate icon transfers — cuts the per-icon handshake
    /// overhead (START/READY/DONE ×12 → ×1). Note this does NOT shrink the raw byte count
    /// on the wire (576000 B either way, comparable to 12×31212 B), so the speed gain is
    /// from fewer handshakes/settle-delays, not from less data — expect a meaningful but
    /// not dramatic improvement (see DpFullscreenAnimator remarks). Buffer must already be
    /// exactly <c>PanelBytes</c> long, correctly composed AND rotated for the device mount
    /// (see DpFullscreenAnimator.BuildPanelBgr) — this call does no further processing.
    /// </summary>
    bool TryUploadRawPanel(int id, byte[] bgr);
}
