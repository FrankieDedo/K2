using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// IPC client that talks to the DisplayPad satellite process (x64) via named pipe.
///
/// The satellite is started automatically on first connection and
/// terminated on close. The protocol is JSON line-delimited:
///   → { "id": N, "cmd": "...", ...params }
///   ← { "id": N, "ok": true/false, ...data }
///   ← { "id": 0, "evt": "...", ...data }   (async push)
/// </summary>
public sealed class DisplayPadSatelliteClient : IDisplayPadClient
{
    private const string SatelliteExeName = "K2.DisplayPad.Satellite.exe";

    private Process? _satellite;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private long _nextId;
    private readonly object _sendLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private Thread? _readThread;
    private volatile bool _disposed;

    // ---- push events from the satellite ----

    public event EventHandler<JsonElement>? PlugEvent;
    public event EventHandler<JsonElement>? KeyEvent;
    public event EventHandler<JsonElement>? ProgressEvent;
    public event EventHandler<string>? SatelliteLog;

    private static readonly JsonSerializerOptions JOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // ================================================================
    // Lifecycle
    // ================================================================

    /// <summary>Starts the satellite and connects the pipe.</summary>
    public bool Connect(int timeoutMs = 8000)
    {
        if (_pipe is { IsConnected: true }) return true;

        string pipeName = $"K2_DP_{Environment.ProcessId}_{Environment.TickCount}";

        // Look for the satellite next to K2.App.exe or in the x64 build
        string? satPath = FindSatellite();
        if (satPath is null)
        {
            RaiseLog("[SAT] K2.DisplayPad.Satellite.exe not found");
            return false;
        }

        RaiseLog($"[SAT] Starting {satPath} pipe={pipeName}");
        _satellite = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = satPath,
                // Arg 0 = pipe name, Arg 1 = parent PID.
                // The satellite monitors the parent and shuts itself down if it crashes.
                Arguments = $"{pipeName} {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            }
        };
        _satellite.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) RaiseLog("[SAT] " + e.Data);
        };
        _satellite.Start();
        _satellite.BeginErrorReadLine();

        _pipe = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try { _pipe.Connect(timeoutMs); }
        catch (Exception ex)
        {
            RaiseLog($"[SAT] Pipe connection failed: {ex.Message}");
            KillSatellite();
            return false;
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "DP-Pipe-Reader" };
        _readThread.Start();

        RaiseLog("[SAT] Connected.");
        return true;
    }

    public void Disconnect()
    {
        if (_disposed) return;
        try { SendCommandFireAndForget("exit"); } catch { }
        Thread.Sleep(200);
        KillSatellite();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Call shutdown logic directly — Disconnect() would short-circuit on _disposed
        try { SendCommandFireAndForget("exit"); } catch { }
        Thread.Sleep(200);
        KillSatellite();
    }

    // ================================================================
    // Public commands (synchronous, block until the response arrives)
    // ================================================================

    public JsonElement? Open() => Send("open");
    public JsonElement? Close() => Send("close");
    public int SdkVersion() => Send("sdkVersion")?.Get("version") ?? 0;
    public List<int> DeviceIds() => Send("deviceIds")?.GetIds("ids") ?? new();
    public bool IsPlugged(int id) => Send("isPlugged", ("deviceId", id))?.GetBool("plugged") ?? false;
    public string FirmwareVersion(int id) => Send("firmwareVersion", ("deviceId", id))?.GetStr("fw") ?? "";
    public int GetBrightness(int id) => Send("getBrightness", ("deviceId", id))?.Get("brightness") ?? -1;
    public bool SetBrightness(int id, int level) => Send("setBrightness", ("deviceId", id), ("level", level))?.GetBool("result") ?? false;
    public bool SwitchProfile(int id, int profile) => Send("switchProfile", ("deviceId", id), ("profile", profile))?.GetBool("result") ?? false;
    public bool APEnable(int id, bool enable) => Send("apEnable", ("deviceId", id), ("enable", enable))?.GetBool("result") ?? false;
    public bool ResetPictures(int id) => Send("resetPictures", ("deviceId", id))?.GetBool("result") ?? false;

    public bool UploadImage(int id, string path, int btn, int rotation = 0, bool pressed = false) =>
        Send("uploadImage", ("deviceId", id), ("imagePath", path), ("buttonIndex", btn), ("rotation", rotation), ("pressed", pressed))?.GetBool("result") ?? false;

    public bool UploadImageToProfile(int id, string path, int btn, int profile, int rotation = 0) =>
        Send("uploadImageToProfile", ("deviceId", id), ("imagePath", path), ("buttonIndex", btn), ("profile", profile), ("rotation", rotation))?.GetBool("result") ?? false;

    public bool Ping() => Send("ping")?.GetBool("pong") ?? false;

    /// <summary>Not supported over the IPC pipe (file-path protocol only) — callers fall
    /// back to <see cref="UploadImage"/>. See <see cref="IDisplayPadClient.TryUploadRawBgr"/>.</summary>
    public bool TryUploadRawBgr(int id, byte[] bgr, int btn) => false;

    /// <summary>No whole-panel command over the IPC pipe — DpFullscreenAnimator falls back
    /// to the 12-tile path when this is false.</summary>
    public bool SupportsRawPanel => false;

    public bool TryUploadRawPanel(int id, byte[] bgr) => false;

    public bool IsConnected => _pipe is { IsConnected: true } && _satellite is { HasExited: false };

    // ================================================================
    // Transport
    // ================================================================

    private JsonElement? Send(string cmd, params (string key, object value)[] args)
    {
        if (_pipe is not { IsConnected: true }) return null;
        long id = Interlocked.Increment(ref _nextId);
        var dict = new Dictionary<string, object> { ["id"] = id, ["cmd"] = cmd };
        foreach (var (k, v) in args) dict[k] = v;

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        string json = JsonSerializer.Serialize(dict, JOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (_sendLock) { _pipe.Write(bytes); _pipe.Flush(); }

        // Timeout 10s
        if (!tcs.Task.Wait(10_000))
        {
            lock (_pending) _pending.Remove(id);
            RaiseLog($"[SAT] Timeout cmd={cmd}");
            return null;
        }
        return tcs.Task.Result;
    }

    private void SendCommandFireAndForget(string cmd)
    {
        if (_pipe is not { IsConnected: true }) return;
        long id = Interlocked.Increment(ref _nextId);
        var dict = new Dictionary<string, object> { ["id"] = id, ["cmd"] = cmd };
        string json = JsonSerializer.Serialize(dict, JOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (_sendLock) { _pipe.Write(bytes); _pipe.Flush(); }
    }

    private void ReadLoop()
    {
        try
        {
            while (!_disposed && _reader is not null)
            {
                string? line = _reader.ReadLine();
                if (line is null) break;

                try
                {
                    var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    long id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;

                    if (id > 0)
                    {
                        // Response to a command
                        TaskCompletionSource<JsonElement>? tcs;
                        lock (_pending)
                        {
                            _pending.Remove(id, out tcs);
                        }
                        tcs?.SetResult(root.Clone());
                    }
                    else if (root.TryGetProperty("evt", out var evtProp))
                    {
                        // Push event
                        string? evt = evtProp.GetString();
                        var cloned = root.Clone();
                        switch (evt)
                        {
                            case "plug":     PlugEvent?.Invoke(this, cloned); break;
                            case "key":      KeyEvent?.Invoke(this, cloned); break;
                            case "progress": ProgressEvent?.Invoke(this, cloned); break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    RaiseLog($"[SAT] Parse error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) RaiseLog($"[SAT] ReadLoop: {ex.Message}");
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static string? FindSatellite()
    {
        // 1) Next to K2.App.exe
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir is not null)
        {
            string p = Path.Combine(dir, SatelliteExeName);
            if (File.Exists(p)) return p;
        }

        // 2) "Satellite\" subfolder next to K2.App.exe (installed self-contained
        //    layout): x86 and x64 self-contained publishes both ship a native
        //    host (hostfxr.dll/coreclr.dll/...) under the SAME filenames, so
        //    they cannot share a folder without one bitness clobbering the
        //    other. The installer keeps the x64 satellite isolated here.
        if (dir is not null)
        {
            string p = Path.Combine(dir, "Satellite", SatelliteExeName);
            if (File.Exists(p)) return p;
        }

        // 3) Relative x64 build (for debugging)
        //    K2.App/bin/x86/Debug/net8.0-windows → 5 levels up = K2/ (solution root)
        if (dir is not null)
        {
            string x64 = Path.Combine(dir, "..", "..", "..", "..", "..",
                "K2.DisplayPad.Satellite", "bin", "x64", "Debug", "net8.0-windows",
                SatelliteExeName);
            x64 = Path.GetFullPath(x64);
            if (File.Exists(x64)) return x64;
        }

        return null;
    }

    private void KillSatellite()
    {
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try
        {
            if (_satellite is { HasExited: false })
            {
                _satellite.Kill(entireProcessTree: true);
                _satellite.WaitForExit(2000);
            }
            _satellite?.Dispose();
        }
        catch { }
        _reader = null;
        _pipe = null;
        _satellite = null;
    }

    private void RaiseLog(string msg) => SatelliteLog?.Invoke(this, msg);
}

/// <summary>Extension methods for extracting values from JsonElement with fallbacks.</summary>
internal static class JsonElementExtensions
{
    public static int Get(this JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static bool GetBool(this JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Undefined &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0));

    public static string GetStr(this JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static List<int> GetIds(this JsonElement e, string prop)
    {
        var list = new List<int>();
        if (e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                list.Add(item.GetInt32());
        return list;
    }
}
