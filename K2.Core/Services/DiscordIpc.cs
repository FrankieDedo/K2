using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Minimal Discord RPC (IPC) client — the local transport the Discord DESKTOP app exposes on
/// the named pipe <c>discord-ipc-N</c> (N = 0..9, one per running client instance).
/// This is the only way to drive the user's own voice state (mute/deafen/volumes/current voice
/// channel): the public REST API is server-side and has no notion of "my local client".
///
/// Hand-rolled on purpose — the only mainstream NuGet for this (<c>DiscordRichPresence</c>)
/// covers activity/presence only, not the voice commands, and the wire format is trivial:
/// an 8-byte little-endian header (<c>int32 opcode</c>, <c>int32 payload length</c>) followed
/// by UTF-8 JSON. Opcodes: 0 HANDSHAKE, 1 FRAME, 2 CLOSE, 3 PING, 4 PONG.
///
/// The connection is long-lived (kept open by <see cref="DiscordBridge"/>) because K2 also
/// SUBSCRIBEs to voice events to drive the live mute/deafen key icons — a fire-and-forget
/// connect-per-command would never see them. A background read loop demultiplexes frames:
/// replies carry back the <c>nonce</c> of their request, everything else is an event dispatch.
/// </summary>
internal sealed class DiscordIpc : IDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    private NamedPipeClientStream? _pipe;
    private Thread? _reader;
    private volatile bool _running;
    private readonly object _writeLock = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Message from the CLOSE frame Discord sends instead of READY when it refuses
    /// the handshake (verified against a real client: an unknown client id comes back as
    /// opcode 2 with <c>{"code":4000,"message":"Invalid Client ID"}</c>). Reported by
    /// <see cref="Open"/> so the user sees the real reason, not a generic timeout.</summary>
    private volatile string? _closeMessage;

    /// <summary>Raised on the reader thread for every event dispatch (evt name, <c>data</c>
    /// object). Handlers must not block and must marshal to the UI thread themselves.</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised on the reader thread when the pipe drops (Discord closed/restarted).</summary>
    public event Action? Disconnected;

    public bool IsOpen => _running && _pipe is { IsConnected: true };

    /// <summary>Opens the first available <c>discord-ipc-N</c> pipe and performs the handshake.
    /// Returns an error message, or null once the client has answered with READY.</summary>
    public string? Open(string clientId, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return "Discord Client ID not set";

        for (int i = 0; i < 10; i++)
        {
            // NamedPipeClientStream.Connect POLLS until its timeout when the pipe doesn't
            // exist, so scanning 10 slots blind would freeze the caller for seconds with
            // Discord closed — and this runs on the UI thread from a keypress. The pipe
            // namespace is enumerable as a directory, so check first and only then connect.
            string pipeName = $"discord-ipc-{i}";
            if (!File.Exists($@"\\.\pipe\{pipeName}")) continue;

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(200); }
            catch { pipe.Dispose(); continue; }

            _pipe = pipe;
            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "K2-DiscordIpc" };
            _reader.Start();

            try { WriteFrame(OpHandshake, JsonSerializer.Serialize(new { v = 1, client_id = clientId })); }
            catch (Exception ex) { Close(); return $"Discord handshake failed: {ex.Message}"; }

            bool ready;
            try { ready = _ready.Task.Wait(timeout); }
            catch { ready = false; }   // CLOSE frame — the read loop cancels the handshake wait
            if (!ready)
            {
                string? closed = _closeMessage;
                Close();
                return closed ?? "Discord did not answer the handshake";
            }
            return null;
        }
        return "Discord desktop app not running (no discord-ipc pipe found)";
    }

    /// <summary>Sends a command and waits for its reply. Returns the reply's <c>data</c>
    /// element, or null on timeout/transport error; <paramref name="error"/> carries the
    /// Discord-reported message when the client answered with an ERROR frame.</summary>
    public JsonElement? Send(string cmd, object? args, TimeSpan timeout, out string? error)
    {
        error = null;
        if (!IsOpen) { error = "Discord RPC not connected"; return null; }

        string nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            WriteFrame(OpFrame, JsonSerializer.Serialize(new { cmd, args, nonce }));
            if (!tcs.Task.Wait(timeout)) { error = $"Discord {cmd} timed out"; return null; }
            var reply = tcs.Task.Result;

            if (reply.TryGetProperty("evt", out var evt) && evt.ValueKind == JsonValueKind.String && evt.GetString() == "ERROR")
            {
                error = reply.TryGetProperty("data", out var errData) && errData.TryGetProperty("message", out var msg)
                    ? msg.GetString() : $"Discord {cmd} failed";
                return null;
            }
            return reply.TryGetProperty("data", out var data) ? data : default(JsonElement);
        }
        catch (Exception ex) { error = ex.Message; return null; }
        finally { _pending.TryRemove(nonce, out _); }
    }

    /// <summary>Subscribes to an RPC event (<c>VOICE_SETTINGS_UPDATE</c>, <c>VOICE_CHANNEL_SELECT</c>, …).</summary>
    public bool Subscribe(string evt, object? args, TimeSpan timeout, out string? error)
    {
        error = null;
        if (!IsOpen) { error = "Discord RPC not connected"; return false; }

        string nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            WriteFrame(OpFrame, JsonSerializer.Serialize(new { cmd = "SUBSCRIBE", evt, args, nonce }));
            if (!tcs.Task.Wait(timeout)) { error = $"Discord SUBSCRIBE {evt} timed out"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { _pending.TryRemove(nonce, out _); }
    }

    /// <summary>Drops a subscription made with <see cref="Subscribe"/> — the per-channel voice
    /// events must be released when the user moves to another channel, or Discord keeps pushing
    /// events for the old one on top of the new ones (see <see cref="DiscordVoiceRoom"/>).</summary>
    public bool Unsubscribe(string evt, object? args, TimeSpan timeout, out string? error)
    {
        error = null;
        if (!IsOpen) { error = "Discord RPC not connected"; return false; }

        string nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            WriteFrame(OpFrame, JsonSerializer.Serialize(new { cmd = "UNSUBSCRIBE", evt, args, nonce }));
            if (!tcs.Task.Wait(timeout)) { error = $"Discord UNSUBSCRIBE {evt} timed out"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { _pending.TryRemove(nonce, out _); }
    }

    private void WriteFrame(int opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BitConverter.GetBytes(opcode).CopyTo(frame, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 4);
        payload.CopyTo(frame, 8);

        lock (_writeLock)
        {
            var pipe = _pipe ?? throw new IOException("Discord pipe closed");
            pipe.Write(frame, 0, frame.Length);
            pipe.Flush();
        }
    }

    private void ReadLoop()
    {
        var header = new byte[8];
        try
        {
            while (_running && _pipe is { IsConnected: true })
            {
                if (!ReadExactly(header, 8)) break;
                int opcode = BitConverter.ToInt32(header, 0);
                int length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 8 * 1024 * 1024) break;

                var payload = new byte[length];
                if (length > 0 && !ReadExactly(payload, length)) break;

                if (opcode == OpClose) { _closeMessage = ReadCloseMessage(payload); break; }
                if (opcode == OpPing) { try { WriteFrame(OpPong, Encoding.UTF8.GetString(payload)); } catch { break; } continue; }
                if (opcode != OpFrame && opcode != OpHandshake) continue;

                HandleFrame(Encoding.UTF8.GetString(payload));
            }
        }
        catch { /* pipe dropped — fall through to the disconnect notification */ }

        _running = false;
        _ready.TrySetCanceled();
        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        _pending.Clear();
        try { Disconnected?.Invoke(); } catch { }
    }

    /// <summary>The <c>message</c> field of a CLOSE frame, when it carries one.</summary>
    private static string? ReadCloseMessage(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch { return null; }
    }

    private void HandleFrame(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { return; }
        using (doc)
        {
            var root = doc.RootElement.Clone();

            if (root.TryGetProperty("nonce", out var nonceEl) && nonceEl.ValueKind == JsonValueKind.String
                && _pending.TryGetValue(nonceEl.GetString()!, out var tcs))
            {
                tcs.TrySetResult(root);
                return;
            }

            if (!root.TryGetProperty("evt", out var evtEl) || evtEl.ValueKind != JsonValueKind.String) return;
            string evt = evtEl.GetString()!;
            var data = root.TryGetProperty("data", out var d) ? d : default;

            if (evt == "READY") { _ready.TrySetResult(data); return; }
            try { EventReceived?.Invoke(evt, data); } catch { }
        }
    }

    private bool ReadExactly(byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = _pipe!.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    public void Close()
    {
        _running = false;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose() => Close();
}
