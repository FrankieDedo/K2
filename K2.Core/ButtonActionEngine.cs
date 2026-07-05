using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace K2.Core;

/// <summary>
/// Button action execution engine, SHARED by all K2 device modules
/// (DisplayPad, MacroPad, Everest, ...).
///
/// Handles device-agnostic actions directly (url, exec, folder, browser,
/// command, keys, text, oscmd, media, mouse, multi, createfolder, back,
/// pyscript) and delegates device-specific ones (profile switching) to
/// the <see cref="IActionHost"/>. Also encapsulates the Python bridge
/// (<see cref="PyBridge"/>).
/// </summary>
public sealed class ButtonActionEngine : IDisposable
{
    private static readonly char[] SendKeysMeta = { '^', '%', '{', '~' };

    private readonly IActionHost _host;
    private readonly PyBridge _py;

    public ButtonActionEngine(IActionHost host)
    {
        _host = host;
        _py = new PyBridge(host, this);
    }

    /// <summary>True if the Python runtime is installed and available.</summary>
    public bool PythonRuntimeAvailable => _py.RuntimeAvailable;

    /// <summary>Starts the Python bridge (RPC server). Call once at startup.</summary>
    public void Start() => _py.Start();

    public void Dispose() => _py.Dispose();

    /// <summary>
    /// Executes the action configured on a button. MUST BE CALLED ON THE UI THREAD.
    /// <paramref name="buttonIndex"/> is the context for Python scripts
    /// (-1 if unknown, e.g. action invoked via RPC).
    /// </summary>
    public void Execute(string? actionType, string? actionValue, int buttonIndex = -1)
    {
        if (string.IsNullOrEmpty(actionType)) return;
        var value = actionValue ?? "";
        try
        {
            Dispatch(actionType, value, buttonIndex);
        }
        catch (Exception ex)
        {
            _host.Log($"[ERR ] button #{buttonIndex} action execution: {ex.Message}");
        }
    }

    private void Dispatch(string type, string value, int buttonIndex)
    {
        void Log(string m) => _host.Log(m);
        switch (type)
        {
            case "url":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] url without payload"); break; }
                Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true });
                Log($"[EXEC] url -> {value}");
                break;

            case "exec":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] exec without payload"); break; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = value,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(value) ?? ""
                });
                Log($"[EXEC] exec -> {value}");
                break;

            case "folder":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] folder without payload"); break; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{value}\"",
                    UseShellExecute = true
                });
                Log($"[EXEC] folder -> {value}");
                break;

            case "browser":
            {
                var url = string.IsNullOrWhiteSpace(value) ? "https://duckduckgo.com" : value;
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                Log($"[EXEC] browser -> {url}");
                break;
            }

            case "profile":
                _host.SwitchProfile(value);
                break;

            case "command":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] command without payload"); break; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + value,
                    UseShellExecute = true,
                    CreateNoWindow = false
                });
                Log($"[EXEC] command -> {value}");
                break;

            case "keys":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] keys without payload"); break; }
                string seq = value.IndexOfAny(SendKeysMeta) >= 0
                    ? value : SendKeysTranslator.Translate(value);
                System.Windows.Forms.SendKeys.SendWait(seq);
                Log($"[EXEC] keys -> \"{value}\"  (sendkeys=\"{seq}\")");
                break;
            }

            case "text":
            {
                if (string.IsNullOrEmpty(value)) { Log("[EXEC] text without payload"); break; }
                System.Windows.Forms.SendKeys.SendWait(EscapeSendKeysLiteral(value));
                Log($"[EXEC] text -> \"{value}\"");
                break;
            }

            case "oscmd":
                ActionExecutor.RunOsCommand(value, Log);
                break;

            case "media":
                ActionExecutor.SendMediaKey(value, Log);
                break;

            case "mouse":
                ActionExecutor.DoMouse(value, Log);
                break;

            case "multi":
                ActionExecutor.RunMultiAction(value, Log, ExecuteSub);
                break;

            case "createfolder":
                ActionExecutor.CreateFolderOnDesktop(value, Log);
                break;

            case "back":
                ActionExecutor.GoBackBrowser(Log);
                break;

            case "pyscript":
                RunPyScript(value, buttonIndex);
                break;

            case "pcinfo":
            case "clock":
                Log($"[EXEC] {type}: dynamic rendering not yet implemented (payload \"{value}\")");
                break;

            case "none":
                // intentionally no action (placeholder for unresolved macros)
                break;

            default:
                Log($"[EXEC] unknown action type: {type}");
                break;
        }
    }

    /// <summary>Executes a single sub-action (called by Multi Action).</summary>
    internal void ExecuteSub(string type, string value)
    {
        void Log(string m) => _host.Log(m);
        switch (type)
        {
            case "url":
                Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true }); break;
            case "exec":
                if (!string.IsNullOrWhiteSpace(value))
                    Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(value) ?? "" });
                break;
            case "folder":
                if (!string.IsNullOrWhiteSpace(value))
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe",
                        Arguments = $"\"{value}\"", UseShellExecute = true });
                break;
            case "browser":
                Process.Start(new ProcessStartInfo {
                    FileName = string.IsNullOrWhiteSpace(value) ? "https://duckduckgo.com" : value,
                    UseShellExecute = true });
                break;
            case "profile":  _host.SwitchProfile(value); break;
            case "keys":
            {
                string seq = value.IndexOfAny(SendKeysMeta) >= 0
                    ? value : SendKeysTranslator.Translate(value);
                System.Windows.Forms.SendKeys.SendWait(seq);
                break;
            }
            case "text":
                System.Windows.Forms.SendKeys.SendWait(EscapeSendKeysLiteral(value)); break;
            case "oscmd": ActionExecutor.RunOsCommand(value, Log); break;
            case "media": ActionExecutor.SendMediaKey(value, Log); break;
            case "mouse": ActionExecutor.DoMouse(value, Log); break;
            default:
                Log($"[EXEC] sub-action type \"{type}\" not handled"); break;
        }
    }

    private void RunPyScript(string value, int buttonIndex)
    {
        var spec = PyScriptPayload.Parse(value);
        if (spec is null)
        {
            _host.Log($"[PY  ] button #{buttonIndex}: invalid pyscript payload");
            return;
        }
        var ctx = new PyScriptContext
        {
            Device  = _host.CurrentDevice,
            Profile = _host.CurrentProfile,
            Button  = buttonIndex,
        };
        _py.RunScript(spec, ctx);
    }

    private static string EscapeSendKeysLiteral(string input)
    {
        var sb = new StringBuilder(input.Length + 8);
        foreach (var ch in input)
        {
            switch (ch)
            {
                case '{': case '}':
                case '(': case ')':
                case '+': case '^':
                case '%': case '~':
                case '[': case ']':
                    sb.Append('{').Append(ch).Append('}');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
