using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.Core;

/// <summary>
/// Result of an RPC call. <see cref="Result"/> is serialized to JSON
/// and returned to the Python script; <see cref="Error"/> becomes a K2Error
/// on the Python side.
/// </summary>
public sealed record RpcResult(bool Ok, object? Result, string? Error)
{
    public static RpcResult Success(object? result = null) => new(true, result, null);
    public static RpcResult Fail(string error)             => new(false, null, error);
}

/// <summary>
/// Mini HTTP server that exposes K2 functions to Python scripts launched
/// from device buttons ("bridge" K2 &lt;-&gt; Python).
///
/// Security features:
///  - listens ONLY on 127.0.0.1 (loopback), never on external interfaces;
///  - free port chosen dynamically at startup;
///  - every request must present the <c>X-K2-Token</c> header with the random
///    token generated at each startup: a random web page cannot drive K2
///    even if it guessed the port.
///
/// The actual dispatch is delegated to a callback provided by the server's
/// creator (see <see cref="PyBridge"/>), which marshals execution onto the UI thread.
/// </summary>
public sealed class K2RpcServer : IDisposable
{
    private readonly Func<string, JsonElement, RpcResult> _dispatch;
    private readonly Action<string> _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    /// <summary>Full URL of the RPC endpoint (passed to the script in K2_RPC_URL).</summary>
    public string Url { get; private set; } = "";

    /// <summary>Shared token required on every call (passed in K2_RPC_TOKEN).</summary>
    public string Token { get; } = Guid.NewGuid().ToString("N");

    public bool IsRunning => _running;

    /// <param name="dispatch">
    /// Callback invoked for each RPC method: receives the method name and the
    /// "params" object as <see cref="JsonElement"/>. Called from a background
    /// thread: the implementation is responsible for marshalling to the UI thread.
    /// </param>
    /// <param name="log">Log callback (thread-safe).</param>
    public K2RpcServer(Func<string, JsonElement, RpcResult> dispatch, Action<string> log)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _log      = log      ?? (_ => { });
    }

    /// <summary>Starts the server. Returns true if listening started.</summary>
    public bool Start()
    {
        if (_running) return true;

        // Try a few free ports: there is a small race window between port
        // selection and Start(), so we retry.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int port = FreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/k2/");
            try
            {
                listener.Start();
                _listener = listener;
                Url = $"http://127.0.0.1:{port}/k2/rpc";
                break;
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (attempt == 5)
                {
                    _log($"[RPC ] server start failed: {ex.Message}");
                    return false;
                }
            }
        }
        if (_listener is null) return false;

        _running = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log($"[RPC ] Python bridge active on {Url}");
        return true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _cts?.Cancel(); }            catch { /* ignore */ }
        try { _listener?.Stop();  }        catch { /* ignore */ }
        try { _listener?.Close(); }        catch { /* ignore */ }
        _listener = null;
        _log("[RPC ] Python bridge stopped");
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        while (_running && listener is not null && !token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!_running)
            {
                break;                              // normal stop
            }
            catch (Exception ex)
            {
                if (_running) _log($"[RPC ] accept error: {ex.Message}");
                break;
            }
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;

            if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, 405, RpcResult.Fail("use POST"));
                return;
            }
            if (!string.Equals(req.Headers["X-K2-Token"], Token, StringComparison.Ordinal))
            {
                WriteJson(ctx, 403, RpcResult.Fail("invalid token"));
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            string method;
            JsonElement parameters;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                method = root.TryGetProperty("method", out var m) ? (m.GetString() ?? "") : "";
                parameters = root.TryGetProperty("params", out var p)
                    ? p.Clone()
                    : EmptyObject();
            }
            catch (JsonException ex)
            {
                WriteJson(ctx, 200, RpcResult.Fail($"invalid JSON: {ex.Message}"));
                return;
            }

            if (string.IsNullOrEmpty(method))
            {
                WriteJson(ctx, 200, RpcResult.Fail("missing 'method' field"));
                return;
            }

            RpcResult result;
            try
            {
                result = _dispatch(method, parameters);
            }
            catch (Exception ex)
            {
                result = RpcResult.Fail(ex.Message);
            }
            WriteJson(ctx, 200, result);
        }
        catch (Exception ex)
        {
            try { WriteJson(ctx, 200, RpcResult.Fail(ex.Message)); } catch { /* ignore */ }
        }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static void WriteJson(HttpListenerContext ctx, int status, RpcResult result)
    {
        object payload = result.Ok
            ? new { ok = true,  result = result.Result }
            : new { ok = false, error  = result.Error ?? "error" };

        byte[] buffer = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            ctx.Response.StatusCode  = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        finally
        {
            ctx.Response.OutputStream.Close();
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
