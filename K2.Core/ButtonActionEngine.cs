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
                RunBrowserAction(value, Log);
                break;

            case "profile":
                RunProfileSwitch(value, Log);
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
                RunBrowserAction(value, Log);
                break;
            case "profile":  RunProfileSwitch(value, Log); break;
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

    /// <summary>
    /// Runs the "profile" action. <paramref name="value"/> is either a
    /// <see cref="ProfileTargetPayload"/> JSON (one or more device+target rows from the
    /// dialog's "switch profile" picker) or a legacy plain string ("Next"/"Previous"/"N")
    /// predating that payload — in which case we fall back to the original behavior:
    /// switch the profile of the device this button lives on.
    /// </summary>
    private void RunProfileSwitch(string value, Action<string> log)
    {
        var spec = ProfileTargetPayload.Parse(value);
        if (spec is null)
        {
            _host.SwitchProfile(null, value);
            return;
        }
        foreach (var t in spec.Targets)
        {
            try { _host.SwitchProfile(string.IsNullOrEmpty(t.Key) ? null : t.Key, t.Target); }
            catch (Exception ex) { log($"[EXEC] profile: target \"{t.Key}\" error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Runs the "browser" action. <paramref name="value"/> is either a
    /// <see cref="BrowserActionPayload"/> JSON (specific browser chosen in the dialog) or
    /// a legacy plain string (a raw URL, or empty) predating that payload — in which case
    /// we fall back to the original behavior: open the URL with the OS default browser.
    /// </summary>
    private static void RunBrowserAction(string value, Action<string> log)
    {
        var spec = BrowserActionPayload.Parse(value);
        if (spec is null)
        {
            var url = string.IsNullOrWhiteSpace(value) ? "https://duckduckgo.com" : value;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            log($"[EXEC] browser -> {url}");
            return;
        }

        string? exe = spec.Browser == "other"
            ? spec.CustomPath
            : BrowserDetector.ResolveById(spec.Browser);

        if (string.IsNullOrWhiteSpace(exe))
        {
            // "Other" with no path (or a known browser that's no longer installed):
            // fall back to the OS default browser, same as the legacy behavior.
            var url = string.IsNullOrWhiteSpace(spec.Url) ? "https://duckduckgo.com" : spec.Url;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            log($"[EXEC] browser -> default -> {url}");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.IsNullOrWhiteSpace(spec.Url) ? "" : spec.Url,
            UseShellExecute = true
        });
        log($"[EXEC] browser -> {exe} {spec.Url}");
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
