using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace K2.Core;

/// <summary>Press context, passed to the script via environment variables.</summary>
public sealed class PyScriptContext
{
    public int Device  { get; init; }
    public int Profile { get; init; }
    public int Button  { get; init; } = -1;
}

/// <summary>
/// Executes Python scripts bound to device buttons.
///
/// Each script runs in a SEPARATE PROCESS launched via the embeddable
/// interpreter and the <c>k2_runner.py</c> bootstrap (see <see cref="PythonRuntimeLocator"/>).
/// Execution happens on a background thread: the UI thread / button callback
/// is never blocked. Script stdout/stderr are streamed to the log via the
/// provided callback.
/// </summary>
public sealed class PythonScriptService : IDisposable
{
    private readonly Action<string> _log;          // must be thread-safe
    private readonly object _gate = new();
    private readonly List<Process> _running = new();
    private bool _disposed;

    /// <summary>Manually configured interpreter path (overrides the locator).</summary>
    public string? ConfiguredPythonPath { get; set; }

    /// <summary>URL of the K2 RPC API (passed to the script in K2_RPC_URL).</summary>
    public string? RpcUrl { get; set; }

    /// <summary>RPC API token (passed to the script in K2_RPC_TOKEN).</summary>
    public string? RpcToken { get; set; }

    public PythonScriptService(Action<string> log) => _log = log ?? (_ => { });

    public bool RuntimeAvailable => PythonRuntimeLocator.IsReady(ConfiguredPythonPath);

    /// <summary>Executes a script from a .py file.</summary>
    public void RunFile(string filePath, PyScriptContext ctx, int timeoutSeconds, string? args)
        => Launch(inline: false, payload: filePath, ctx, timeoutSeconds, args);

    /// <summary>Executes "inline" Python code.</summary>
    public void RunInline(string code, PyScriptContext ctx, int timeoutSeconds, string? args)
        => Launch(inline: true, payload: code, ctx, timeoutSeconds, args);

    // ------------------------------------------------------------------

    private void Launch(bool inline, string payload, PyScriptContext ctx,
                        int timeoutSeconds, string? args)
    {
        if (_disposed) return;

        var python = PythonRuntimeLocator.FindPythonExe(ConfiguredPythonPath);
        var runner = PythonRuntimeLocator.FindRunnerScript();
        if (python is null)
        {
            _log("[PY  ] Python interpreter not found. Run K2/setup-python-embed.bat "
                 + "to install Python embeddable, or set the K2_PYTHON_DIR variable.");
            return;
        }
        if (runner is null)
        {
            _log("[PY  ] k2_runner.py not found (expected in pybridge/ next to the exe "
                 + "or in lib/pybridge/ in the repo).");
            return;
        }

        string scriptPath;
        bool tempScript = false;
        if (inline)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "K2", "pyscript");
                Directory.CreateDirectory(dir);
                scriptPath = Path.Combine(dir, $"inline_{Guid.NewGuid():N}.py");
                File.WriteAllText(scriptPath, payload ?? "", new UTF8Encoding(false));
                tempScript = true;
            }
            catch (Exception ex)
            {
                _log($"[PY  ] unable to prepare inline script: {ex.Message}");
                return;
            }
        }
        else
        {
            scriptPath = (payload ?? "").Trim().Trim('"');
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                _log($"[PY  ] script file not found: \"{scriptPath}\"");
                return;
            }
        }

        var thread = new Thread(() =>
            Execute(python, runner, scriptPath, tempScript, ctx, timeoutSeconds, args))
        {
            IsBackground = true,
            Name = "K2-PyScript",
        };
        thread.Start();
    }

    private void Execute(string python, string runner, string scriptPath, bool tempScript,
                         PyScriptContext ctx, int timeoutSeconds, string? args)
    {
        Process? proc = null;
        string label = tempScript ? "inline" : Path.GetFileName(scriptPath);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = python,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                WorkingDirectory       = Path.GetDirectoryName(scriptPath)
                                         ?? Environment.CurrentDirectory,
            };
            psi.ArgumentList.Add(runner);

            psi.Environment["K2_USER_SCRIPT"]   = scriptPath;
            psi.Environment["K2_SCRIPT_ARGS"]   = ArgsToJson(args);
            psi.Environment["K2_DEVICE"]        = ctx.Device.ToString();
            psi.Environment["K2_PROFILE"]       = ctx.Profile.ToString();
            psi.Environment["K2_BUTTON"]        = ctx.Button.ToString();
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            if (!string.IsNullOrEmpty(RpcUrl))   psi.Environment["K2_RPC_URL"]   = RpcUrl;
            if (!string.IsNullOrEmpty(RpcToken)) psi.Environment["K2_RPC_TOKEN"] = RpcToken;

            proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) _log("[PY  ] " + e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) _log("[PY! ] " + e.Data); };

            string timeoutLabel = timeoutSeconds > 0 ? $"{timeoutSeconds}s" : "none";
            _log($"[PY  ] starting script \"{label}\" (timeout {timeoutLabel})");
            proc.Start();
            lock (_gate) _running.Add(proc);

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (timeoutSeconds > 0 && !proc.WaitForExit(timeoutSeconds * 1000))
            {
                _log($"[PY  ] timeout: force-killing \"{label}\"");
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            proc.WaitForExit();                       // drain async output
            _log($"[PY  ] script \"{label}\" finished (exit={SafeExitCode(proc)})");
        }
        catch (Exception ex)
        {
            _log($"[PY  ] execution error \"{label}\": {ex.Message}");
        }
        finally
        {
            if (proc != null)
            {
                lock (_gate) _running.Remove(proc);
                try { proc.Dispose(); } catch { /* ignore */ }
            }
            if (tempScript)
            {
                try { File.Delete(scriptPath); } catch { /* ignore */ }
            }
        }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>Serializes the arguments string as a JSON array for K2_SCRIPT_ARGS.</summary>
    private static string ArgsToJson(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "";
        return JsonSerializer.Serialize(SplitArgs(args));
    }

    /// <summary>Splits a line into tokens, respecting double quotes.</summary>
    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var sb   = new StringBuilder();
        bool inQuote = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    public void Dispose()
    {
        _disposed = true;
        Process[] snapshot;
        lock (_gate) { snapshot = _running.ToArray(); _running.Clear(); }
        foreach (var p in snapshot)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }
}
