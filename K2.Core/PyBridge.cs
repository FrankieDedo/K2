using System;
using System.Collections.Generic;
using System.Text.Json;

namespace K2.Core;

/// <summary>
/// K2 &lt;-&gt; Python bridge. Encapsulates the RPC server (<see cref="K2RpcServer"/>)
/// and the script execution service (<see cref="PythonScriptService"/>), and
/// implements the RPC method dispatcher routing calls to <see cref="IActionHost"/>
/// and <see cref="ButtonActionEngine"/>.
/// </summary>
public sealed class PyBridge : IDisposable
{
    private readonly IActionHost _host;
    private readonly ButtonActionEngine _engine;
    private readonly PythonScriptService _pyService;
    private readonly K2RpcServer _rpcServer;

    public PyBridge(IActionHost host, ButtonActionEngine engine)
    {
        _host      = host;
        _engine    = engine;
        _pyService = new PythonScriptService(LogSafe);
        _rpcServer = new K2RpcServer(HandleRpc, LogSafe);
    }

    /// <summary>True if the Python runtime is installed and available.</summary>
    public bool RuntimeAvailable => _pyService.RuntimeAvailable;

    /// <summary>Starts the RPC server and connects the configured interpreter.</summary>
    public void Start()
    {
        _pyService.ConfiguredPythonPath = _host.ConfiguredPythonPath;

        if (_rpcServer.Start())
        {
            _pyService.RpcUrl   = _rpcServer.Url;
            _pyService.RpcToken = _rpcServer.Token;
        }
        else
        {
            _host.Log("[PY  ] RPC API not started: scripts can perform system actions "
                      + "but cannot call K2 functions.");
        }

        if (!_pyService.RuntimeAvailable)
            _host.Log("[PY  ] Python runtime not installed — run setup-python-embed.bat "
                      + "to enable Python scripts on buttons.");
    }

    public void Dispose()
    {
        try { _rpcServer.Dispose(); } catch { /* ignore */ }
        try { _pyService.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Launches the script described by the payload (file or inline).</summary>
    public void RunScript(PyScriptPayload spec, PyScriptContext ctx)
    {
        if (spec.Inline)
            _pyService.RunInline(spec.Code, ctx, spec.TimeoutSeconds, spec.Args);
        else
            _pyService.RunFile(spec.Path, ctx, spec.TimeoutSeconds, spec.Args);
    }

    // ---- thread-safe log --------------------------------------------

    private void LogSafe(string message)
    {
        var d = _host.Dispatcher;
        if (d.CheckAccess()) _host.Log(message);
        else d.BeginInvoke(new Action(() => _host.Log(message)));
    }

    // ---- RPC dispatcher ---------------------------------------------

    /// <summary>
    /// RPC server entry point (called from a background thread):
    /// marshals actual execution onto the UI thread.
    /// </summary>
    private RpcResult HandleRpc(string method, JsonElement parameters)
    {
        try
        {
            return _host.Dispatcher.Invoke(() => Dispatch(method, parameters));
        }
        catch (Exception ex)
        {
            return RpcResult.Fail(ex.Message);
        }
    }

    /// <summary>Executes the RPC method. ALWAYS runs on the UI thread.</summary>
    private RpcResult Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "log":
                _host.Log("[PY> ] " + RpcStr(p, "message"));
                return RpcResult.Success();

            case "get_state":
                return RpcResult.Success(new Dictionary<string, object?>
                {
                    ["device"]       = _host.CurrentDevice,
                    ["profile"]      = _host.CurrentProfile,
                    ["profileCount"] = _host.ProfileCount,
                    ["buttonCount"]  = _host.ButtonCount,
                    ["sdkVersion"]   = _host.SdkVersion,
                });

            case "get_buttons":
            {
                var list = new List<Dictionary<string, object?>>();
                foreach (var b in _host.GetButtons())
                    list.Add(new Dictionary<string, object?>
                    {
                        ["index"]       = b.Index,
                        ["keyMatrix"]   = b.KeyMatrix,
                        ["hasImage"]    = b.HasImage,
                        ["imagePath"]   = b.ImagePath,
                        ["actionType"]  = b.ActionType,
                        ["actionValue"] = b.ActionValue,
                    });
                return RpcResult.Success(list);
            }

            case "switch_profile":
            {
                var target = RpcStr(p, "target");
                if (string.IsNullOrWhiteSpace(target))
                    return RpcResult.Fail("missing 'target' parameter");
                _host.SwitchProfile(target);
                return RpcResult.Success(new Dictionary<string, object?>
                {
                    ["profile"] = _host.CurrentProfile,
                });
            }

            case "run_action":
            {
                var type = RpcStr(p, "type");
                if (string.IsNullOrWhiteSpace(type))
                    return RpcResult.Fail("missing 'type' parameter");
                if (string.Equals(type, "pyscript", StringComparison.OrdinalIgnoreCase))
                    return RpcResult.Fail("run_action cannot launch 'pyscript'");
                _engine.Execute(type, RpcStr(p, "value"), -1);
                return RpcResult.Success();
            }

            case "press_button":
            {
                if (!RpcInt(p, "index", out int idx) || idx < 0 || idx >= _host.ButtonCount)
                    return RpcResult.Fail("invalid 'index' parameter (expected 0.."
                                          + (_host.ButtonCount - 1) + ")");
                _host.PressButton(idx);
                return RpcResult.Success();
            }

            default:
                return RpcResult.Fail($"unknown RPC method: {method}");
        }
    }

    // ---- JSON parameter reading helpers ----------------------------

    private static string RpcStr(JsonElement p, string name)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                _                    => "",
            };
        }
        return "";
    }

    private static bool RpcInt(JsonElement p, string name, out int value)
    {
        value = 0;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value))
            return true;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out value))
            return true;
        return false;
    }
}
