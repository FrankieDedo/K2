using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Configuration popup for the DisplayPad's <c>dp_speedtest</c> keys, opened from
/// <see cref="DpKeyConfigDialog"/> while a speed-test key is being configured. One
/// <see cref="SpeedTestConfig"/>, held globally (there is a single <see cref="SpeedTestService"/>
/// for the whole app), not per pad.
///
/// <para>The <b>Server</b> dropdown is a convenience: "Cloudflare" is the zero-config default;
/// the other rows just prefill the two URL boxes with an editable example for that kind of
/// endpoint (LibreSpeed / an Ookla test server / a plain download file). Anything actually
/// entered is stored as a plain custom endpoint — the specific preset isn't re-derived on
/// reopen, only remembered for which row to show.</para>
///
/// <para><b>Test now</b> applies the form's settings to the live service immediately and runs a
/// measurement, so a user chasing "nothing happens" sees the real error here. Closing with
/// Cancel puts the previous configuration back; Save hands <see cref="Result"/> to the caller,
/// which persists it.</para>
/// </summary>
public partial class SpeedTestConfigWindow : Window
{
    /// <summary>True only when the user pressed Save.</summary>
    public bool Saved { get; private set; }

    /// <summary>The chosen config — meaningful only when <see cref="Saved"/>.</summary>
    public SpeedTestConfig Result { get; private set; }

    private readonly SpeedTestConfig _original;
    private bool _loadingUi = true;

    // Editable examples the dropdown drops into the URL boxes. Kept here so switching between
    // presets swaps one example for the next, while a value the user actually typed is left alone.
    private static readonly (string Down, string Up)[] Examples =
    {
        ("", ""),                                                                     // 0 Cloudflare (unused)
        ("https://librespeed.example.net/backend/garbage.php?ckSize={mb}",
         "https://librespeed.example.net/backend/empty.php"),                          // 1 LibreSpeed
        ("http://your-ookla-server:8080/download?size={bytes}",
         "http://your-ookla-server:8080/upload"),                                      // 2 Ookla
        ("https://speed.hetzner.de/100MB.bin", ""),                                    // 3 test file
        ("", ""),                                                                      // 4 fully custom
    };

    public SpeedTestConfigWindow(SpeedTestConfig current)
    {
        InitializeComponent();
        _original = current;

        int preset = Math.Clamp(current.Preset, 0, 4);
        if (current.Mode == SpeedTestMode.Cloudflare) preset = 0;
        else if (preset == 0) preset = 4;
        CbProvider.SelectedIndex = preset;

        TxtDownUrl.Text = current.DownUrl;
        TxtUpUrl.Text = current.UpUrl;
        TxtDownMb.Text = BytesToMb(current.Mode == SpeedTestMode.Cloudflare
            ? SpeedTestConfig.DefaultDownBytes : current.DownBytes);
        TxtUpMb.Text = BytesToMb(current.Mode == SpeedTestMode.Cloudflare
            ? SpeedTestConfig.DefaultUpBytes : current.UpBytes);
        TxtTimeout.Text = (current.Mode == SpeedTestMode.Cloudflare
            ? SpeedTestConfig.DefaultTimeoutSeconds
            : SpeedTestConfig.ClampTimeout(current.TimeoutSeconds)).ToString(CultureInfo.InvariantCulture);

        _loadingUi = false;
        ApplyProviderVisibility();

        SpeedTestService.Changed += OnServiceChanged;
        Closed += (_, _) =>
        {
            SpeedTestService.Changed -= OnServiceChanged;
            if (!Saved) SpeedTestService.ApplyConfig(_original);   // undo any "Test now"
        };
    }

    private static string BytesToMb(int bytes) =>
        Math.Max(1, (int)Math.Round(bytes / 1_000_000d)).ToString(CultureInfo.InvariantCulture);

    private int SelectedPreset => Math.Clamp(CbProvider.SelectedIndex, 0, 4);
    private bool IsCloudflare => SelectedPreset == 0;

    private void CbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;

        int p = SelectedPreset;
        if (p is >= 1 and <= 3)
        {
            // Prefill only when the box is empty or still holds another preset's example.
            if (IsExampleOrEmpty(TxtDownUrl.Text)) TxtDownUrl.Text = Examples[p].Down;
            if (IsExampleOrEmpty(TxtUpUrl.Text)) TxtUpUrl.Text = Examples[p].Up;
        }
        ApplyProviderVisibility();
    }

    private static bool IsExampleOrEmpty(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return true;
        foreach (var (down, up) in Examples)
            if (s == down || s == up) return true;
        return false;
    }

    private void ApplyProviderVisibility()
    {
        var custom = IsCloudflare ? Visibility.Collapsed : Visibility.Visible;
        PnlCustom.Visibility = custom;
        PnlNumbers.Visibility = custom;
        LblSources.Visibility = custom;
    }

    private static readonly Regex NonDigit = new(@"[^0-9]", RegexOptions.Compiled);
    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = NonDigit.IsMatch(e.Text);

    /// <summary>Builds a config from the current form state (no validation).</summary>
    private SpeedTestConfig FromForm()
    {
        if (IsCloudflare)
            return SpeedTestConfig.Default with { Preset = 0 };

        int Mb(TextBox t, int fallbackBytes) =>
            int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb) && mb > 0
                ? SpeedTestConfig.ClampBytes(mb * 1_000_000) : fallbackBytes;
        int sec = int.TryParse(TxtTimeout.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
            ? SpeedTestConfig.ClampTimeout(s) : SpeedTestConfig.DefaultTimeoutSeconds;

        return new SpeedTestConfig(
            Mode: SpeedTestMode.Custom,
            DownUrl: TxtDownUrl.Text.Trim(),
            UpUrl: TxtUpUrl.Text.Trim(),
            DownBytes: Mb(TxtDownMb, SpeedTestConfig.DefaultDownBytes),
            UpBytes: Mb(TxtUpMb, SpeedTestConfig.DefaultUpBytes),
            TimeoutSeconds: sec,
            Preset: SelectedPreset);
    }

    private void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        var cfg = FromForm();
        if (!cfg.IsUsable)
        {
            ShowProbe(Loc.Get("speedtest_test_failed", Loc.Get("speedtest_bad_url")));
            return;
        }
        SpeedTestService.ApplyConfig(cfg);
        SpeedTestService.Start();
        ShowProbe(Loc.Get("speedtest_test_running"));
    }

    private void OnServiceChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (SpeedTestService.IsRunning)
        {
            ShowProbe($"{Loc.Get("speedtest_test_running")}  ({SpeedTestService.CurrentPhase})");
            return;
        }
        if (SpeedTestService.LastRunFailed)
        {
            ShowProbe(Loc.Get("speedtest_test_failed", SpeedTestService.LastError ?? "?"));
            return;
        }
        if (SpeedTestService.LastRunAt is not null)
        {
            string up = SpeedTestService.UploadDisabled
                ? Loc.Get("speedtest_upload_off")
                : $"{SpeedTestService.LastUpMbps:F1} Mbps";
            ShowProbe(Loc.Get("speedtest_test_ok",
                SpeedTestService.LastPingMs?.ToString("F0") ?? "?",
                SpeedTestService.LastDownMbps?.ToString("F1") ?? "?",
                up));
        }
    });

    private void ShowProbe(string text)
    {
        TxtProbe.Text = text;
        TxtProbe.Visibility = Visibility.Visible;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var cfg = FromForm();
        if (!cfg.IsUsable)
        {
            MessageBox.Show(this, Loc.Get("speedtest_bad_url"), Loc.Get("speedtest_config_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = cfg;
        Saved = true;
        DialogResult = true;
    }
}
