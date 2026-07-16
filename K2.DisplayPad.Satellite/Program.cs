using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace K2.DisplayPad.Satellite;

/// <summary>
/// x64 satellite process for the DisplayPad.
///
/// K2.App (x86) cannot load DisplayPadSDK.dll (x64) in its own process.
/// This satellite wraps the SDK and communicates with K2.App via a JSON named pipe.
///
/// Protocol:
///   request  → { "id": N, "cmd": "...", ...params }
///   response ← { "id": N, "ok": true/false, ...data }
///   event    ← { "id": 0, "evt": "...", ...data }       (asynchronous push)
///
/// Startup: K2.App launches the satellite as a child process, passing the pipe
///          name as the first argument. The satellite exits when the pipe
///          closes or when it receives the "exit" command.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [STAThread]
    static int Main(string[] args)
    {
        string pipeName  = args.Length > 0 ? args[0] : "K2_DisplayPad_Pipe";
        int    parentPid = args.Length > 1 && int.TryParse(args[1], out int p) ? p : -1;
        Log($"Satellite started, pipe={pipeName}, PID={Environment.ProcessId}, parentPID={parentPid}");
        LogSdkDllInfo();

        // WPF Application needed for the message pump (the SDK posts WM_* to the
        // hidden window for plug/key/progress callbacks).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            Task.Run(() => RunPipeServer(pipeName, app));

            // Watchdog: if the parent dies (crash or kill), the satellite exits.
            // Covers the case where OnWindowClosed is never called.
            if (parentPid > 0)
                Task.Run(() => MonitorParent(parentPid));
        };
        app.Run();
        return 0;
    }

    /// <summary>
    /// Waits for the parent process to die; when it exits (or can't be found),
    /// terminates the satellite via Environment.Exit — guarantees exit even
    /// if the pipe thread is blocked in an SDK call.
    /// </summary>
    private static void MonitorParent(int parentPid)
    {
        try
        {
            var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit(); // blocks while the parent is alive
            Log($"Parent PID {parentPid} exited — satellite shutting down.");
        }
        catch
        {
            // The parent doesn't exist already (invalid PID or already dead)
            Log($"Parent PID {parentPid} not found — satellite shutting down.");
        }
        Environment.Exit(0);
    }

    private static void RunPipeServer(string pipeName, Application app)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(pipeName,
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Log("Waiting for connection...");
            pipe.WaitForConnection();
            Log("Client connected.");

            using var handler = new SdkHandler(app.Dispatcher);
            handler.EventRaised += (_, json) =>
            {
                try { WriteLine(pipe, json); }
                catch { /* pipe closed, ignore */ }
            };

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (pipe.IsConnected)
            {
                string? line = reader.ReadLine();
                if (line is null) break; // pipe closed

                try
                {
                    var doc = JsonDocument.Parse(line);
                    string? cmd = doc.RootElement.GetProperty("cmd").GetString();
                    long id = doc.RootElement.GetProperty("id").GetInt64();

                    var response = handler.Handle(cmd!, doc.RootElement);
                    // Inject the id into the response
                    var respObj = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        JsonSerializer.Serialize(response, JsonOpts), JsonOpts)
                        ?? new Dictionary<string, object?>();
                    respObj["id"] = id;
                    string respJson = JsonSerializer.Serialize(respObj, JsonOpts);
                    WriteLine(pipe, respJson);

                    if (cmd == "exit") break;
                }
                catch (Exception ex)
                {
                    Log($"[ERR] {ex.Message}");
                    try { WriteLine(pipe, JsonSerializer.Serialize(new { id = 0, ok = false, error = ex.Message }, JsonOpts)); }
                    catch { break; }
                }
            }

            Log("Pipe closed, exiting.");
        }
        catch (Exception ex)
        {
            Log($"[FATAL] {ex}");
        }
        finally
        {
            app.Dispatcher.InvokeShutdown();
        }
    }

    private static readonly object _writeLock = new();
    private static void WriteLine(Stream pipe, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (_writeLock) { pipe.Write(bytes); pipe.Flush(); }
    }

    /// <summary>
    /// Logs file version/size/timestamp of both the native DisplayPadSDK.dll (x64) and
    /// its managed wrapper DisplayPad.SDK.dll — rules out "wrong/corrupt/mismatched DLL
    /// on this particular install" before blaming the Open() call itself.
    /// </summary>
    private static void LogSdkDllInfo()
    {
        string dir = AppContext.BaseDirectory;
        foreach (string name in new[] { "DisplayPadSDK.dll", "DisplayPad.SDK.dll" })
        {
            string path = Path.Combine(dir, name);
            try
            {
                if (!File.Exists(path)) { Log($"[SdkDll] {name}: NOT FOUND at {path}"); continue; }
                var fi = new FileInfo(path);
                var ver = FileVersionInfo.GetVersionInfo(path);
                Log($"[SdkDll] {name}: size={fi.Length} modified={fi.LastWriteTime:yyyy-MM-dd HH:mm:ss} " +
                    $"fileVersion={ver.FileVersion} productVersion={ver.ProductVersion}");
            }
            catch (Exception ex)
            {
                Log($"[SdkDll] {name}: version read failed: {ex.Message}");
            }
        }
    }

    internal static void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [Satellite] {msg}";
        Console.Error.WriteLine(line);
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "K2.DisplayPad");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "satellite.log"), line + Environment.NewLine);
        }
        catch { /* best effort */ }
    }
}
