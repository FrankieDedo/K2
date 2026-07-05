using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.App.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: "USB Recorder" panel.
/// Captures HID packets sent by Base Camp to the keyboard via
/// tshark (Wireshark CLI) + USBPcap, and displays them as a hex dump.
/// </summary>
public partial class MainWindow
{
    private readonly UsbRecorder _usbRec = new();
    private string? _lastCapturePath;

    // ================================================================
    // Initialization
    // ================================================================

    /// <summary>Called from the MainWindow constructor.</summary>
    private void InitUsbRecorderModule()
    {
        var tshark = _usbRec.FindTshark();
        if (tshark != null)
        {
            LblUsbStatus.Text = $"tshark found: {tshark}";
            // Load interfaces automatically
            RefreshUsbInterfaces();
        }
        else
        {
            LblUsbStatus.Text = "tshark not found. Install Wireshark to use the recorder.";
            BtnUsbRecord.IsEnabled = false;
        }
    }

    // ================================================================
    // USBPcap interfaces
    // ================================================================

    private void RefreshUsbInterfaces()
    {
        CbUsbInterface.Items.Clear();
        var ifaces = _usbRec.ListUsbInterfaces();
        if (ifaces.Count == 0)
        {
            CbUsbInterface.Items.Add("(no USBPcap interface)");
            CbUsbInterface.SelectedIndex = 0;
            BtnUsbRecord.IsEnabled = false;
            LblUsbStatus.Text = "No USBPcap interface found. Install USBPcap and restart.";
            return;
        }

        foreach (var (name, desc) in ifaces)
        {
            CbUsbInterface.Items.Add(new ComboBoxItem
            {
                Content = desc,
                Tag = name,
            });
        }
        CbUsbInterface.SelectedIndex = 0;
        BtnUsbRecord.IsEnabled = true;
        LblUsbStatus.Text = $"Ready. {ifaces.Count} USBPcap interfaces found.";
    }

    // ================================================================
    // Event handlers
    // ================================================================

    private void BtnUsbRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshUsbInterfaces();
    }

    private void BtnUsbRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_usbRec.IsRecording)
        {
            // --- STOP ---
            LblUsbStatus.Text = "Stopping capture...";
            _lastCapturePath = _usbRec.StopCapture();
            BtnUsbRecord.Content = "Start recording";
            BtnUsbRecord.Tag = ""; // play icon

            if (_lastCapturePath != null && File.Exists(_lastCapturePath))
            {
                var packets = UsbRecorder.ParseCapture(_lastCapturePath);
                var report = PcapParser.FormatAll(packets);
                TxtUsbResults.Text = report;

                var fileInfo = new FileInfo(_lastCapturePath);
                LblUsbStatus.Text = $"Capture complete: {packets.Count} OUT packets, "
                    + $"{fileInfo.Length / 1024.0:F1} KB -> {Path.GetFileName(_lastCapturePath)}";

                ExpUsbResults.Visibility = Visibility.Visible;
                ExpUsbResults.IsExpanded = true;
            }
            else
            {
                LblUsbStatus.Text = "Capture complete but the file was not found.";
            }
        }
        else
        {
            // --- START ---
            var selected = CbUsbInterface.SelectedItem as ComboBoxItem;
            if (selected == null) return;

            var ifaceName = selected.Tag as string;
            if (string.IsNullOrEmpty(ifaceName)) return;

            var label = TxtUsbLabel.Text.Trim();
            if (string.IsNullOrEmpty(label)) label = "capture";

            var path = _usbRec.StartCapture(ifaceName, label);
            if (path == null)
            {
                LblUsbStatus.Text = "Error starting capture.";
                return;
            }

            BtnUsbRecord.Content = "Stop recording";
            BtnUsbRecord.Tag = ""; // stop icon
            LblUsbStatus.Text = $"RECORDING -> {Path.GetFileName(path)}\n"
                + "Perform actions in Base Camp, then press 'Stop recording'.";
        }
    }

    private void BtnUsbSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtUsbResults.Text)) return;

        var dir = _lastCapturePath != null
            ? Path.GetDirectoryName(_lastCapturePath) ?? "."
            : ".";
        var name = _lastCapturePath != null
            ? Path.GetFileNameWithoutExtension(_lastCapturePath) + "_report.txt"
            : $"usb_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        var path = Path.Combine(dir, name);

        File.WriteAllText(path, TxtUsbResults.Text);
        LblUsbStatus.Text = $"Report saved: {path}";
    }

    private void BtnUsbOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _lastCapturePath != null
            ? Path.GetDirectoryName(_lastCapturePath)
            : null;
        if (dir != null && Directory.Exists(dir))
        {
            Process.Start("explorer.exe", dir);
        }
    }
}
