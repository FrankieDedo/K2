using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace K2.App.Services;

/// <summary>
/// Registratore USB che orchestra tshark (Wireshark CLI) + USBPcap
/// per catturare i pacchetti HID inviati alla tastiera Everest Max.
///
/// Workflow:
///   1. <see cref="FindTshark"/> — localizza tshark.exe
///   2. <see cref="ListUsbInterfaces"/> — elenca le interfacce USBPcap
///   3. <see cref="StartCapture"/> — avvia tshark in background
///   4. (l'utente fa cose in Base Camp)
///   5. <see cref="StopCapture"/> — ferma tshark e restituisce il path pcapng
///   6. <see cref="ParseCapture"/> — analizza i pacchetti catturati
/// </summary>
internal sealed class UsbRecorder : IDisposable
{
    // VID/PID della tastiera Everest Max
    private const ushort EverestVid = 0x3282;
    private const ushort EverestPid = 0x0001;

    private Process? _tshark;
    private string?  _capturePath;
    private string?  _tsharkExe;

    public bool IsRecording => _tshark is { HasExited: false };

    // ================================================================
    // 1. Localizzazione tshark
    // ================================================================

    /// <summary>
    /// Cerca tshark.exe nei percorsi tipici di installazione di Wireshark.
    /// Restituisce il path o null se non trovato.
    /// </summary>
    public string? FindTshark()
    {
        if (_tsharkExe != null && File.Exists(_tsharkExe))
            return _tsharkExe;

        // Percorsi comuni
        var candidates = new[]
        {
            @"C:\Program Files\Wireshark\tshark.exe",
            @"C:\Program Files (x86)\Wireshark\tshark.exe",
        };

        // Aggiungi dal PATH di sistema
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), "tshark.exe");
            candidates = candidates.Append(p).ToArray();
        }

        _tsharkExe = candidates.FirstOrDefault(File.Exists);
        return _tsharkExe;
    }

    // ================================================================
    // 2. Lista interfacce USBPcap
    // ================================================================

    /// <summary>
    /// Elenca le interfacce di cattura USBPcap disponibili.
    /// Restituisce coppie (nome_interfaccia, descrizione).
    /// </summary>
    public List<(string Name, string Description)> ListUsbInterfaces()
    {
        var tshark = FindTshark();
        if (tshark == null) return new();

        var result = new List<(string, string)>();
        try
        {
            var psi = new ProcessStartInfo(tshark, "-D")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return result;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            // Output formato: "1. \Device\USBPcap1 (USBPcap1)"
            // oppure: "1. \\.\USBPcap1 (USB bus)"
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("USBPcap", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Extract interface name (the part between the number and the parenthesis)
                var m = Regex.Match(line.Trim(), @"^\d+\.\s+(.+?)(?:\s+\((.+)\))?$");
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    var desc = m.Groups[2].Success ? m.Groups[2].Value.Trim() : name;
                    result.Add((name, desc));
                }
            }
        }
        catch { /* tshark non disponibile */ }
        return result;
    }

    // ================================================================
    // 3. Avvio cattura
    // ================================================================

    /// <summary>
    /// Avvia la cattura USB su un'interfaccia USBPcap specificata.
    /// Il pcapng viene salvato nella cartella <c>K2/_reference/usb_dumps/</c>.
    /// </summary>
    /// <param name="interfaceName">
    /// Nome dell'interfaccia USBPcap (es. <c>\\.\USBPcap1</c>).
    /// </param>
    /// <param name="label">
    /// Etichetta per il file (es. "basecamp_wave"). Se null, usa un timestamp.
    /// </param>
    /// <returns>Path of the pcapng file that will be written, or null on error.</returns>
    public string? StartCapture(string interfaceName, string? label = null)
    {
        var tshark = FindTshark();
        if (tshark == null) return null;
        if (IsRecording) return null;

        // Crea cartella dump se non esiste
        var dumpDir = GetDumpDirectory();
        Directory.CreateDirectory(dumpDir);

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = string.IsNullOrEmpty(label)
            ? $"capture_{ts}.pcapng"
            : $"{label}_{ts}.pcapng";
        _capturePath = Path.Combine(dumpDir, fileName);

        // tshark -i <interface> -w <file>
        // Non filtriamo a livello di cattura: filtreremo dopo nel parser.
        // This gives us the full dump in case extra analysis is needed.
        var args = $"-i \"{interfaceName}\" -w \"{_capturePath}\"";

        try
        {
            var psi = new ProcessStartInfo(tshark, args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _tshark = Process.Start(psi);
            return _capturePath;
        }
        catch
        {
            _capturePath = null;
            return null;
        }
    }

    // ================================================================
    // 4. Stop cattura
    // ================================================================

    /// <summary>
    /// Ferma la cattura e restituisce il path del pcapng.
    /// </summary>
    public string? StopCapture()
    {
        if (_tshark == null) return null;

        try
        {
            if (!_tshark.HasExited)
            {
                // tshark si ferma con Ctrl+C; su Windows usiamo taskkill
                // because we cannot send SIGINT from .NET easily.
                var kill = new ProcessStartInfo("taskkill", $"/PID {_tshark.Id} /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(kill)?.WaitForExit(3000);
            }
            _tshark.WaitForExit(5000);
        }
        catch { /* already terminated */ }
        finally
        {
            _tshark.Dispose();
            _tshark = null;
        }

        var path = _capturePath;
        _capturePath = null;
        return path;
    }

    // ================================================================
    // 5. Analisi
    // ================================================================

    /// <summary>
    /// Parsa un file pcapng e restituisce i pacchetti OUT con hex dump.
    /// </summary>
    public static List<PcapParser.UsbPacket> ParseCapture(string pcapngPath)
    {
        if (!File.Exists(pcapngPath)) return new();
        return PcapParser.ParseOutPackets(pcapngPath);
    }

    /// <summary>
    /// Confronta due catture e restituisce un report delle differenze.
    /// </summary>
    public static string CompareCaptures(string pcapA, string pcapB,
        string labelA = "Base Camp", string labelB = "K2")
    {
        var a = ParseCapture(pcapA);
        var b = ParseCapture(pcapB);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"=== {labelA}: {a.Count} pacchetti OUT ===");
        sb.AppendLine(PcapParser.FormatAll(a));
        sb.AppendLine($"=== {labelB}: {b.Count} pacchetti OUT ===");
        sb.AppendLine(PcapParser.FormatAll(b));

        sb.AppendLine("=== Differenze ===");
        int maxCount = Math.Max(a.Count, b.Count);
        if (a.Count != b.Count)
            sb.AppendLine($"  Numero pacchetti diverso: {labelA}={a.Count}, {labelB}={b.Count}");

        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (!a[i].Payload.SequenceEqual(b[i].Payload))
            {
                sb.AppendLine($"  Pacchetto #{i + 1}: payload diverso");
                sb.AppendLine($"    {labelA}: {BitConverter.ToString(a[i].Payload).Replace("-", " ")}");
                sb.AppendLine($"    {labelB}: {BitConverter.ToString(b[i].Payload).Replace("-", " ")}");
                // Evidenzia byte diversi
                var diff = new System.Text.StringBuilder("    Diff:  ");
                for (int j = 0; j < Math.Max(a[i].Payload.Length, b[i].Payload.Length); j++)
                {
                    byte ba = j < a[i].Payload.Length ? a[i].Payload[j] : (byte)0;
                    byte bb = j < b[i].Payload.Length ? b[i].Payload[j] : (byte)0;
                    diff.Append(ba == bb ? ".. " : "^^ ");
                }
                sb.AppendLine(diff.ToString());
            }
        }
        return sb.ToString();
    }

    // ================================================================
    // Utility
    // ================================================================

    private static string GetDumpDirectory()
    {
        // Cerca _reference/usb_dumps/ salendo dalla directory dell'exe
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var refDir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "_reference", "usb_dumps"));
        if (Directory.Exists(Path.GetDirectoryName(refDir)))
            return refDir;
        // Fallback: accanto all'exe
        return Path.Combine(exeDir, "usb_dumps");
    }

    public void Dispose()
    {
        if (_tshark is { HasExited: false })
        {
            try { _tshark.Kill(); } catch { }
        }
        _tshark?.Dispose();
    }
}
