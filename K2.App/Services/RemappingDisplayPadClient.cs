using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>
/// Decorator around a real <see cref="IDisplayPadClient"/> backend (SDK satellite or
/// native USB-HID) that translates between the SDK's raw, port-dependent device ids and
/// a persisted STABLE "logical" id (see <see cref="DisplayPadDeviceMap"/>). Everything
/// downstream of this class — MainWindow.DisplayPad.cs, DpGifAnimator,
/// DpFullscreenAnimator, SpotifyCoverService, DisplayPadStore keys, tab labels,
/// IActionHost lookups, ... — only ever sees the logical id; only this class (and the
/// "DisplayPad device mapping" popup, via <see cref="RawInner"/>) ever talks raw ids.
/// Unmapped raw ids pass through unchanged (logical == raw), so a fresh install/an
/// untouched mapping behaves exactly as if this class didn't exist.
/// </summary>
internal sealed class RemappingDisplayPadClient : IDisplayPadClient
{
    private readonly IDisplayPadClient _inner;

    public RemappingDisplayPadClient(IDisplayPadClient inner)
    {
        _inner = inner;
        _inner.PlugEvent += (s, e) => PlugEvent?.Invoke(this, e);
        // KeyEvent is the only event carrying a per-device id (ProgressEvent only ever
        // carries a firmware-update "percent", PlugEvent none at all — see OnDpProgress/
        // OnDpPlug in MainWindow.DisplayPad.cs) — so it's the only one that needs its
        // payload rewritten before reaching subscribers.
        _inner.KeyEvent += (s, e) => KeyEvent?.Invoke(this, TranslateIntField(e, "deviceId", ToLogical));
        _inner.ProgressEvent += (s, e) => ProgressEvent?.Invoke(this, e);
        _inner.SatelliteLog += (s, msg) => SatelliteLog?.Invoke(this, msg);
    }

    /// <summary>The wrapped, un-translated backend — used ONLY by the device-mapping
    /// popup (<see cref="K2.App.DpDeviceMapWindow"/>) to enumerate/identify the SDK's raw
    /// ids and let the user assign each one a logical id.</summary>
    public IDisplayPadClient RawInner => _inner;

    private static int ToLogical(int raw) =>
        DisplayPadDeviceMap.GetAll().TryGetValue(raw, out var logical) ? logical : raw;

    private static int ToRaw(int logical)
    {
        foreach (var kv in DisplayPadDeviceMap.GetAll())
            if (kv.Value == logical) return kv.Key;
        return logical;
    }

    public event EventHandler<JsonElement>? PlugEvent;
    public event EventHandler<JsonElement>? KeyEvent;
    public event EventHandler<JsonElement>? ProgressEvent;
    public event EventHandler<string>? SatelliteLog;

    public bool IsConnected => _inner.IsConnected;
    public bool Connect(int timeoutMs = 8000) => _inner.Connect(timeoutMs);
    public void Disconnect() => _inner.Disconnect();
    public JsonElement? Open() => _inner.Open();
    public JsonElement? Close() => _inner.Close();
    public int SdkVersion() => _inner.SdkVersion();

    public List<int> DeviceIds() => _inner.DeviceIds().Select(ToLogical).ToList();
    public bool IsPlugged(int id) => _inner.IsPlugged(ToRaw(id));
    public string FirmwareVersion(int id) => _inner.FirmwareVersion(ToRaw(id));
    public int GetBrightness(int id) => _inner.GetBrightness(ToRaw(id));
    public bool SetBrightness(int id, int level) => _inner.SetBrightness(ToRaw(id), level);
    public bool SwitchProfile(int id, int profile) => _inner.SwitchProfile(ToRaw(id), profile);
    public bool APEnable(int id, bool enable) => _inner.APEnable(ToRaw(id), enable);
    public bool ResetPictures(int id) => _inner.ResetPictures(ToRaw(id));

    public bool UploadImage(int id, string path, int btn, int rotation = 0, bool pressed = false) =>
        _inner.UploadImage(ToRaw(id), path, btn, rotation, pressed);
    public bool UploadImageToProfile(int id, string path, int btn, int profile, int rotation = 0) =>
        _inner.UploadImageToProfile(ToRaw(id), path, btn, profile, rotation);
    public bool Ping() => _inner.Ping();

    public bool TryUploadRawBgr(int id, byte[] bgr, int btn) => _inner.TryUploadRawBgr(ToRaw(id), bgr, btn);
    public bool SupportsRawPanel => _inner.SupportsRawPanel;
    public bool TryUploadRawPanel(int id, byte[] bgr) => _inner.TryUploadRawPanel(ToRaw(id), bgr);

    public void Dispose() => _inner.Dispose();

    /// <summary>Rebuilds a JsonElement with one integer property replaced — JsonElement is
    /// immutable, so translating KeyEvent's "deviceId" means re-serializing the whole
    /// object with the mapped value swapped in (every other property, e.g. keyMatrix/
    /// pressed, is copied through unchanged).</summary>
    private static JsonElement TranslateIntField(JsonElement e, string prop, Func<int, int> map)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            return e;

        int mapped = map(v.GetInt32());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals(prop)) writer.WriteNumber(prop, mapped);
                else p.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
