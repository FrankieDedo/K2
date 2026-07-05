using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace K2.App.Services;

/// <summary>
/// USB recorder that orchestrates tshark (Wireshark CLI) + USBPcap
/// to capture the HID packets sent to the Everest Max keyboard.
///
/// Workflow:
///   1. <see cref="FindTshark"/> — locates tshark.exe
///   2. <see cref="ListUsbInterfaces"/> — lists the USBPcap interfaces
///   3. <see cref="StartCapture"/> — starts tshark in the background
///   4. (the user does things in Base Camp)
///   5. <see cref="StopCapture"/> — stops tshark and returns the pcapng path
///   6. <see cref="ParseCapture"/> — analyzes the captured packets
/// </summary>
internal sealed class UsbRecorder : IDisposable
{
    // VID/PID of the Everest Max keyboard
    private const ushort EverestVid = 0x3282;
    private const ushort EverestPid = 0x0001;

    private Process? _tshark;
    private string?  _capturePath;
    private string?  _tsharkExe;

    public bool IsRecording => _tshark is { HasExited: false };

    // ================================================================
    // 1. Locating tshark
    // ================================================================

    /// <summary>
    /// Searches for tshark.exe in the typical Wireshark install paths.
    /// Returns the path or null if not found.
    /// </summary>
    public string? FindTshark()
    {
        if (_tsharkExe != null && File.Exists(_tsharkExe))
            return _tsharkExe;

        // Common paths
        var candidates = new[]
        {
            @"C:\Program Files\Wireshark\tshark.exe",
            @"C:\Program Files (x86)\Wireshark\tshark.exe",
        };

        // Also check the system PATH
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
    // 2. List USBPcap interfaces
    // ================================================================

    /// <summary>
    /// Lists the available USBPcap capture interfaces.
    /// Returns (interface_name, description) pairs.
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

            // Output format: "1. \Device\USBPcap1 (USBPcap1)"
            // or: "1. \\.\USBPcap1 (USB bus)"
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
        catch { /* tshark not available */ }
        return result;
    }

    // ================================================================
    // 3. Starting the capture
    // ================================================================

    /// <summary>
    /// Starts a USB capture on the specified USBPcap interface.
    /// The pcapng is saved in the <c>K2/_reference/usb_dumps/</c> folder.
    /// </summary>
    /// <param name="interfaceName">
    /// USBPcap interface name (e.g. <c>\\.\USBPcap1</c>).
    /// </param>
    /// <param name="label">
    /// Label for the file (e.g. "basecamp_wave"). If null, uses a timestamp.
    /// </param>
    /// <returns>Path of the pcapng file that will be written, or null on error.</returns>
    public string? StartCapture(string interfaceName, string? label = null)
    {
        var tshark = FindTshark();
        if (tshark == null) return null;
        if (IsRecording) return null;

        // Create the dump folder if it doesn't exist
        var dumpDir = GetDumpDirectory();
        Directory.CreateDirectory(dumpDir);

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = string.IsNullOrEmpty(label)
            ? $"capture_{ts}.pcapng"
            : $"{label}_{ts}.pcapng";
        _capturePath = Path.Combine(dumpDir, fileName);

        // tshark -i <interface> -w <file>
        // We don't filter at capture time: filtering happens later in the parser.
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
    // 4. Stopping the capture
    // ================================================================

    /// <summary>
    /// Stops the capture and returns the pcapng path.
    /// </summary>
    public string? StopCapture()
    {
        if (_tshark == null) return null;

        try
        {
            if (!_tshark.HasExited)
            {
                // tshark stops via Ctrl+C; on Windows we use taskkill
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
    // 5. Analysis
    // ================================================================

    /// <summary>
    /// Parses a pcapng file and returns the OUT packets with a hex dump.
    /// </summary>
    public static List<PcapParser.UsbPacket> ParseCapture(string pcapngPath)
    {
        if (!File.Exists(pcapngPath)) return new();
        return PcapParser.ParseOutPackets(pcapngPath);
    }

    /// <summary>
    /// Compares two captures and returns a diff report.
    /// </summary>
    public static string CompareCaptures(string pcapA, string pcapB,
        string labelA = "Base Camp", string labelB = "K2")
    {
        var a = ParseCapture(pcapA);
        var b = ParseCapture(pcapB);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"=== {labelA}: {a.Count} OUT packets ===");
        sb.AppendLine(PcapParser.FormatAll(a));
        sb.AppendLine($"=== {labelB}: {b.Count} OUT packets ===");
        sb.AppendLine(PcapParser.FormatAll(b));

        sb.AppendLine("=== Differences ===");
        int maxCount = Math.Max(a.Count, b.Count);
        if (a.Count != b.Count)
            sb.AppendLine($"  Different packet count: {labelA}={a.Count}, {labelB}={b.Count}");

        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (!a[i].Payload.SequenceEqual(b[i].Payload))
            {
                sb.AppendLine($"  Packet #{i + 1}: different payload");
                sb.AppendLine($"    {labelA}: {BitConverter.ToString(a[i].Payload).Replace("-", " ")}");
                sb.AppendLine($"    {labelB}: {BitConverter.ToString(b[i].Payload).Replace("-", " ")}");
                // Highlight differing bytes
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
        // Look for _reference/usb_dumps/ by walking up from the exe directory
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var refDir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "_reference", "usb_dumps"));
        if (Directory.Exists(Path.GetDirectoryName(refDir)))
            return refDir;
        // Fallback: next to the exe
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
