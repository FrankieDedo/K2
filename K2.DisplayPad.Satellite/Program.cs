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
/// Processo satellite x64 per il DisplayPad.
///
/// K2.App (x86) cannot load DisplayPadSDK.dll (x64) in its own process.
/// Questo satellite wrappa l'SDK e comunica con K2.App via named pipe JSON.
///
/// Protocollo:
///   request  → { "id": N, "cmd": "...", ...params }
///   response ← { "id": N, "ok": true/false, ...data }
///   event    ← { "id": 0, "evt": "...", ...data }       (push asincrono)
///
/// Avvio: K2.App lancia il satellite come processo figlio passando il nome
///        della pipe come primo argomento. Il satellite esce quando la pipe
///        si chiude o quando riceve il comando "exit".
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
        Log($"Satellite avviato, pipe={pipeName}, PID={Environment.ProcessId}, parentPID={parentPid}");

        // WPF Application necessaria per il message pump (l'SDK posta WM_* alla
        // finestra nascosta per i callback plug/key/progress).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            Task.Run(() => RunPipeServer(pipeName, app));

            // Watchdog: se il parent muore (crash o kill), il satellite esce.
            // Copre il caso in cui OnWindowClosed non viene mai chiamato.
            if (parentPid > 0)
                Task.Run(() => MonitorParent(parentPid));
        };
        app.Run();
        return 0;
    }

    /// <summary>
    /// Attende la morte del processo parent; quando esce (o non è trovabile),
    /// termina il satellite con Environment.Exit — garantisce l'uscita anche
    /// se il thread della pipe è bloccato in una chiamata SDK.
    /// </summary>
    private static void MonitorParent(int parentPid)
    {
        try
        {
            var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit(); // blocca finché il parent è vivo
            Log($"Parent PID {parentPid} uscito — satellite in chiusura.");
        }
        catch
        {
            // Il parent non esiste già (PID invalido o già morto)
            Log($"Parent PID {parentPid} non trovato — satellite in chiusura.");
        }
        Environment.Exit(0);
    }

    private static void RunPipeServer(string pipeName, Application app)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(pipeName,
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Log("In attesa di connessione...");
            pipe.WaitForConnection();
            Log("Client connesso.");

            using var handler = new SdkHandler(app.Dispatcher);
            handler.EventRaised += (_, json) =>
            {
                try { WriteLine(pipe, json); }
                catch { /* pipe chiusa, ignora */ }
            };

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (pipe.IsConnected)
            {
                string? line = reader.ReadLine();
                if (line is null) break; // pipe chiusa

                try
                {
                    var doc = JsonDocument.Parse(line);
                    string? cmd = doc.RootElement.GetProperty("cmd").GetString();
                    long id = doc.RootElement.GetProperty("id").GetInt64();

                    var response = handler.Handle(cmd!, doc.RootElement);
                    // Inietta l'id nella response
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

            Log("Pipe chiusa, uscita.");
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
