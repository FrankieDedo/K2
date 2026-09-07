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
/// "LibreSpeed" adds a picker of the bundled public server list
/// (<see cref="LibreSpeedServerList"/>) that fills the two URL boxes; the other rows just
/// prefill those boxes with an editable example for that kind of endpoint (a plain download
/// file, or nothing for fully custom). Anything actually entered is stored as a plain custom
/// endpoint; on reopen the row is re-derived from the saved URLs
/// (<see cref="GuessPreset"/>), so the stored index is only a fallback.</para>
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

    // Dropdown row indices (must match the ComboBoxItem order in the XAML).
    private const int PresetCloudflare = 0;
    private const int PresetLibre = 1;
    private const int PresetTestFile = 2;
    private const int PresetCustom = 3;
    private const int PresetMax = PresetCustom;

    // Editable examples the dropdown drops into the URL boxes. Kept here so switching between
    // presets swaps one example for the next, while a value the user actually typed is left alone.
    // LibreSpeed's boxes are filled from the server picker instead (CbLibreServer), not from here.
    private static readonly (string Down, string Up)[] Examples =
    {
        ("", ""),                                                                     // 0 Cloudflare (unused)
        ("https://librespeed.example.net/backend/garbage.php?ckSize={mb}",
         "https://librespeed.example.net/backend/empty.php"),                          // 1 LibreSpeed
        ("https://speed.hetzner.de/100MB.bin", ""),                                    // 2 test file
        ("", ""),                                                                      // 3 fully custom
    };

    // Bundled public LibreSpeed servers, index-aligned with CbLibreServer's rows; [0] is null
    // for the "custom / manual" row that leaves the URL boxes untouched.
    private readonly System.Collections.Generic.List<LibreSpeedServer?> _libreServers = new() { null };

    public SpeedTestConfigWindow(SpeedTestConfig current)
    {
        InitializeComponent();
        _original = current;

        // Populate the LibreSpeed server picker from the bundled list before anything can
        // select a row.
        var libreRows = new System.Collections.Generic.List<string> { Loc.Get("speedtest_libre_server_custom") };
        foreach (var s in LibreSpeedServerList.Load())
        {
            _libreServers.Add(s);
            libreRows.Add(s.Display);
        }
        CbLibreServer.ItemsSource = libreRows;
        CbLibreServer.SelectedIndex = 0;

        // The saved Preset is only a hint; derive the row from the URLs so a config saved
        // under an older layout (when Ookla was row 2) still opens on a sensible row.
        CbProvider.SelectedIndex = GuessPreset(current);

        TxtDownUrl.Text = current.DownUrl;
        TxtUpUrl.Text = current.UpUrl;
        if (CbProvider.SelectedIndex == PresetLibre) SelectLibreServerFor(current.DownUrl);
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

    private int SelectedPreset => Math.Clamp(CbProvider.SelectedIndex, 0, PresetMax);
    private bool IsCloudflare => SelectedPreset == PresetCloudflare;

    private void CbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;

        int p = SelectedPreset;
        if (p is PresetLibre or PresetTestFile)
        {
            // Prefill only when the box is empty or still holds another preset's example.
            // For LibreSpeed this is just a placeholder until a server is picked below.
            if (IsExampleOrEmpty(TxtDownUrl.Text)) TxtDownUrl.Text = Examples[p].Down;
            if (IsExampleOrEmpty(TxtUpUrl.Text)) TxtUpUrl.Text = Examples[p].Up;
        }
        ApplyProviderVisibility();
    }

    /// <summary>Row to open on for a saved config — derived from the URLs, not the stored
    /// <see cref="SpeedTestConfig.Preset"/>, so a config written under an older layout (Ookla
    /// was a row once) still lands somewhere sensible.</summary>
    private int GuessPreset(SpeedTestConfig c)
    {
        if (c.Mode == SpeedTestMode.Cloudflare) return PresetCloudflare;
        string down = (c.DownUrl ?? "").Trim();
        foreach (var s in _libreServers)
            if (s is not null && string.Equals(s.DownUrl, down, StringComparison.OrdinalIgnoreCase))
                return PresetLibre;
        if (down.Contains("ckSize=", StringComparison.OrdinalIgnoreCase) ||
            down.Contains("garbage.php", StringComparison.OrdinalIgnoreCase))
            return PresetLibre;
        if (string.IsNullOrWhiteSpace(c.UpUrl)) return PresetTestFile;
        return PresetCustom;
    }

    /// <summary>Point <see cref="CbLibreServer"/> at the bundled server whose download URL
    /// matches <paramref name="downUrl"/>, or the "custom / manual" row if none does.</summary>
    private void SelectLibreServerFor(string? downUrl)
    {
        string d = (downUrl ?? "").Trim();
        for (int i = 1; i < _libreServers.Count; i++)
            if (string.Equals(_libreServers[i]!.DownUrl, d, StringComparison.OrdinalIgnoreCase))
            {
                CbLibreServer.SelectedIndex = i;
                return;
            }
        CbLibreServer.SelectedIndex = 0;
    }

    private void CbLibreServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        int i = CbLibreServer.SelectedIndex;
        if (i <= 0 || i >= _libreServers.Count) return;   // "custom / manual" row: leave the boxes
        var s = _libreServers[i]!;
        TxtDownUrl.Text = s.DownUrl;
        TxtUpUrl.Text = s.UpUrl;
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
        PnlLibre.Visibility = SelectedPreset == PresetLibre ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Opens the shared <see cref="GuideWindow"/> on the <c>speedtest</c> guide block
    /// (<c>Guides/guide.&lt;lang&gt;.md</c>) — what each Server option measures against and when to
    /// pick it. Same button and window as the other config popups.</summary>
    private void BtnGuide_Click(object sender, RoutedEventArgs e) =>
        new GuideWindow("speedtest", Loc.Get("speedtest_config_title")) { Owner = this }.ShowDialog();

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
