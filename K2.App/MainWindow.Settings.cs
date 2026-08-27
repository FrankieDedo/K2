// MainWindow.Settings.cs — partial class: centralized General Settings tab.
//
// Debug UI/behavior on every device comes from the single AppSettings.DebugMode
// flag, read from the plain-text %LOCALAPPDATA%\K2\k2_debug.cfg (see
// K2.Core/DebugConfig.cs). It has no toggle in this tab (the old per-device
// checkboxes, then the app-wide one here, were both removed — 2026-08-27) and is
// applied once at startup by ApplyDebugModeToAllDevices.
// Logging is independent of the Debug flag — the Log
// level (Off/Normal/Verbose, default Normal) is always active/visible in this
// tab and controls logging verbosity across the app (see AppSettings.LogLevel —
// key-press logs and the LED-poll diagnostic log only fire at Verbose).

using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

public partial class MainWindow
{
    private UpdateCheckResult? _lastUpdateCheck;

    /// <summary>Shows the running version and kicks off a silent background check —
    /// called once from InitAppSettingsPanel. "Silent" means no status text/dialog on
    /// failure or "up to date", so a normal launch with no update available is a no-op
    /// the user never notices; only the manual button (BtnAppCheckUpdate_Click) and an
    /// actual update being found produce visible feedback.</summary>
    private void InitUpdatesPanel()
    {
        var v = UpdateChecker.CurrentVersion;
        TxtAppCurrentVersion.Text = Loc.Get("settings_update_current_version", $"{v.Major}.{v.Minor}.{v.Build}");
        _ = RunUpdateCheckAsync(silent: true);
    }

    private void BtnAppCheckUpdate_Click(object sender, RoutedEventArgs e) => _ = RunUpdateCheckAsync(silent: false);

    private async Task RunUpdateCheckAsync(bool silent)
    {
        if (!silent)
        {
            BtnAppCheckUpdate.IsEnabled = false;
            TxtAppUpdateStatus.Text = Loc.Get("settings_update_checking");
        }

        var result = await UpdateChecker.CheckAsync();
        _lastUpdateCheck = result;

        if (!result.Success)
        {
            if (!silent)
                TxtAppUpdateStatus.Text = Loc.Get("settings_update_check_failed", result.Error ?? "?");
            BtnAppCheckUpdate.IsEnabled = true;
            return;
        }

        if (result.UpdateAvailable)
        {
            TxtAppUpdateStatus.Text = Loc.Get("settings_update_available", result.LatestVersion ?? "?");
            TxtAppUpdateNotes.Text = result.ReleaseNotes ?? "";
            PnlAppUpdateAvailable.Visibility = Visibility.Visible;

            bool installed = InstallDetector.IsInstalled();
            BtnAppUpdateInstall.Visibility = installed && result.InstallerAsset is not null ? Visibility.Visible : Visibility.Collapsed;
            BtnAppUpdateZip.Visibility = !installed && result.PortableZipAsset is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            if (!silent) TxtAppUpdateStatus.Text = Loc.Get("settings_update_uptodate");
            PnlAppUpdateAvailable.Visibility = Visibility.Collapsed;
        }

        BtnAppCheckUpdate.IsEnabled = true;
    }

    /// <summary>Installed copies (Inno Setup): download the installer and launch it,
    /// then close K2 the same way the tray's "Exit" does (<c>_reallyClosing</c> lets
    /// MainWindow_Closing's close-to-tray redirect proceed to a real close instead of
    /// hiding the window) — the installer needs K2.App.exe unlocked to overwrite it.</summary>
    private async void BtnAppUpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateCheck?.InstallerAsset is not { } asset) return;

        var confirm = MessageBox.Show(this,
            Loc.Get("settings_update_install_confirm", _lastUpdateCheck.LatestVersion ?? "?"),
            Loc.Get("settings_update_group"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            BtnAppUpdateInstall.IsEnabled = false;
            PbAppUpdateDownload.Value = 0;
            PbAppUpdateDownload.Visibility = Visibility.Visible;
            var progress = new Progress<double>(p =>
            {
                PbAppUpdateDownload.Value = p * 100;
                TxtAppUpdateStatus.Text = Loc.Get("settings_update_downloading_pct", (int)Math.Round(p * 100));
            });
            TxtAppUpdateStatus.Text = Loc.Get("settings_update_downloading");
            string path = await UpdateInstaller.DownloadInstallerAsync(asset, progress);

            UpdateInstaller.LaunchInstaller(path);
            _reallyClosing = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Get("settings_update_group"), MessageBoxButton.OK, MessageBoxImage.Error);
            BtnAppUpdateInstall.IsEnabled = true;
            PbAppUpdateDownload.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Portable copies (no installer to hand off to): just download the ZIP
    /// wherever the user chooses to save it — K2 stays open, nothing is applied
    /// automatically (the user swaps the folder contents themselves, same as a fresh
    /// portable extract).</summary>
    private async void BtnAppUpdateZip_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateCheck?.PortableZipAsset is not { } asset) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = asset.Name,
            Filter = "ZIP archive|*.zip",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            BtnAppUpdateZip.IsEnabled = false;
            PbAppUpdateDownload.Value = 0;
            PbAppUpdateDownload.Visibility = Visibility.Visible;
            var progress = new Progress<double>(p =>
            {
                PbAppUpdateDownload.Value = p * 100;
                TxtAppUpdateStatus.Text = Loc.Get("settings_update_downloading_pct", (int)Math.Round(p * 100));
            });
            TxtAppUpdateStatus.Text = Loc.Get("settings_update_downloading");
            await UpdateInstaller.DownloadAsync(asset, dlg.FileName, progress);
            TxtAppUpdateStatus.Text = Loc.Get("settings_update_zip_done", dlg.FileName);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Get("settings_update_group"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnAppUpdateZip.IsEnabled = true;
            PbAppUpdateDownload.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnAppUpdateViewRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateCheck?.ReleaseUrl is not { } url) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Called once from the constructor (via Window.Loaded) — offers to import
    /// existing Base Camp profiles/settings the very first time K2 runs. Silently does
    /// nothing (no popup) if Base Camp isn't installed, so a user who never had it
    /// installed is never bothered. The flag is reset by "Restore all defaults" (see
    /// BtnAppRestoreDefaults_Click), so the prompt fires again after the following
    /// restart; it can also be forced again any time from the Settings tab.</summary>
    private void CheckFirstRunBcImport()
    {
        if (AppSettings.BcImportPromptShown) return;
        AppSettings.SetBcImportPromptShown(true);
        RunBaseCampImportPrompt(silentIfNotFound: true);
    }

    /// <summary>"Import from Base Camp" button in the Settings tab — forces the same
    /// prompt shown automatically on first run, regardless of whether it already ran.</summary>
    private void BtnAppImportFromBaseCamp_Click(object sender, RoutedEventArgs e) =>
        RunBaseCampImportPrompt(silentIfNotFound: false);

    /// <summary>Single entry-point gate: detects Base Camp's database and, if present,
    /// asks once whether to import existing profiles/settings — then hands off to each
    /// device's own (already-built) Base Camp import flow, which shows its own per-device
    /// summary/confirmation. Devices with no matching profiles in the DB, or not
    /// connected, simply no-op (see each BtnXxImportBc_Click).</summary>
    private void RunBaseCampImportPrompt(bool silentIfNotFound)
    {
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            if (!silentIfNotFound)
                MessageBox.Show(this, Loc.Get("dp_bc_db_not_found"), Loc.Get("bc_import_prompt_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var res = MessageBox.Show(this, Loc.Get("bc_import_prompt_text"), Loc.Get("bc_import_prompt_title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        // Macros FIRST, before any device's key bindings: a "Default" FunctionType binding
        // (BaseCampDbImporter.TranslateDefaultAction) is matched by name against the K2 macro
        // library at import time — if the library is still empty because this cascade never
        // imported macros, every named-macro reference lands unresolved (red "action not
        // found" triangle) even though the same-named macro exists right there in BaseCamp.db.
        // Reuses the same button handler (and its own confirm/count dialogs) as the standalone
        // "Import from BaseCamp" button on the Macro tab — same pattern every device below
        // already follows (each shows its own confirmation in this cascade).
        BtnMacroImportBC_Click(this, new RoutedEventArgs());

        BtnEvImportBc_Click(this, new RoutedEventArgs());
        BtnEv60ImportBc_Click(this, new RoutedEventArgs());
        BtnMkImportBc_Click(this, new RoutedEventArgs());
        BtnMpImportBc_Click(this, new RoutedEventArgs());
        // DisplayPad supports multiple simultaneous physical devices — repeat the
        // per-device import (incl. the Base Camp device picker, when needed) once per
        // connected pad instead of the single-tab BtnDpImportBc_Click.
        DpImportBcForAllDevices();
    }

    /// <summary>Loads the persisted "Base Camp DLL folder" (see
    /// <see cref="AppSettings.BaseCampDllFolder"/>) into the picker, mirrors the
    /// installer's "detected install vs. manual folder" radio choice (see
    /// K2Setup.iss's BcPage), and refreshes the found/missing status of the three
    /// non-redistributable Base Camp native DLLs (MacroPadSDK.dll, SDKDLL.dll,
    /// Everest360_USB.dll — see <see cref="NativeDependencyResolver"/>). Called once
    /// from InitAppSettingsPanel and again after the user changes the radio or
    /// browses the folder.</summary>
    private void InitBcDllFolderPanel()
    {
        if (_bcDllPanelUpdating) return; // re-entrancy guard: setting IsChecked below fires RbBcDllMode_Checked
        _bcDllPanelUpdating = true;
        try
        {
            string? detected = NativeDependencyResolver.BaseCampDirectories().FirstOrDefault();
            RbBcDllAuto.IsEnabled = detected is not null;
            TxtBcDetected.Text = detected ?? Loc.Get("settings_bc_dll_radio_auto_notfound");

            // Manual mode whenever the user has an explicit override saved, or nothing
            // was auto-detected to fall back on (same default as the installer's page).
            bool manual = !string.IsNullOrWhiteSpace(AppSettings.BaseCampDllFolder) || detected is null;
            RbBcDllManual.IsChecked = manual;
            RbBcDllAuto.IsChecked = !manual;
            TxtBcDllFolder.IsEnabled = manual;
            BtnBcDllFolderBrowse.IsEnabled = manual;

            TxtBcDllFolder.Text = AppSettings.BaseCampDllFolder ?? Loc.Get("settings_bc_dll_none");
            RefreshBcDllStatus();
        }
        finally
        {
            _bcDllPanelUpdating = false;
        }
    }

    private bool _bcDllPanelUpdating;

    private void RefreshBcDllStatus()
    {
        var parts = NativeDependencyResolver.BaseCampNativeDlls.Select(dll =>
            $"{dll}: {(NativeDependencyResolver.IsResolvable(dll) ? Loc.Get("settings_bc_dll_found") : Loc.Get("settings_bc_dll_missing"))}");
        TxtBcDllStatus.Text = string.Join("   ", parts);
    }

    /// <summary>Fires when either "Base Camp DLL folder" radio is checked. Switching to
    /// auto-detect clears any saved manual override so <see cref="NativeDependencyResolver"/>
    /// falls back to its own detection; switching to manual just unlocks the folder
    /// picker below (the folder itself is only saved once the user browses to one).</summary>
    private void RbBcDllMode_Checked(object sender, RoutedEventArgs e)
    {
        if (RbBcDllAuto.IsChecked == true)
            AppSettings.SetBaseCampDllFolder(null);
        InitBcDllFolderPanel();
    }

    /// <summary>"Browse…" in the "Base Camp DLL folder" group — lets the user point K2
    /// at a folder containing the Base Camp native DLLs (e.g. copied from another PC or
    /// extracted from the Base Camp installer) without installing Base Camp itself or
    /// setting the K2_BASECAMP_DIR environment variable by hand.</summary>
    private void BtnBcDllFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("settings_bc_dll_browse") };
        if (folder.ShowDialog(this) != true) return;

        AppSettings.SetBaseCampDllFolder(folder.FolderName);
        InitBcDllFolderPanel();
    }

    /// <summary>Loads persisted AppSettings into the Settings tab UI and applies
    /// the debug flag to every device module. Called once from the constructor,
    /// after all Init*Module() calls so their controls/fields already exist.</summary>
    private void InitAppSettingsPanel()
    {
        // Debug mode is no longer a checkbox here: it is read from the plain-text
        // %LOCALAPPDATA%\K2\k2_debug.cfg (K2.Core/DebugConfig.cs), default off, and
        // only applied at startup — see ApplyDebugModeToAllDevices at the end.
        bool debug = AppSettings.DebugMode;

        switch (AppSettings.LogLevel)
        {
            case K2LogLevel.Off:     RbLogOff.IsChecked     = true; break;
            case K2LogLevel.Verbose: RbLogVerbose.IsChecked = true; break;
            default:                 RbLogNormal.IsChecked  = true; break;
        }

        CkAutoStopBaseCamp.IsChecked = AppSettings.AutoStopBaseCamp;
        CkKillBcWorker.IsChecked = AppSettings.KillBaseCampWorker;
        CkRestartBcOnClose.IsChecked = AppSettings.RestartBaseCampOnClose;
        InitBcAutostartCheckbox();

        InitSignalRgbPanel();

        InitBcDllFolderPanel();

        CkCloseToTray.IsChecked = AppSettings.CloseToTray;
        CkStartMinToTray.IsChecked = AppSettings.StartMinimizedToTray;
        CkK2Autostart.IsChecked = Services.K2AutostartService.IsEnabled();

        InitAppFontCombo();
        InitAppAccentCombo();
        InitAppIconColorCombo();
        InitIconGalleryStyleRadios();
        InitUpdatesPanel();
        InitAcknowledgementsPanel();
        InitExtraLinksPanel();

        ApplyDebugModeToAllDevices(debug);
    }

    /// <summary>Reference links shown as cards in the Settings tab's "Extra" section
    /// (see THANKS.md), grouped into labeled sub-sections. Add more groups/links here
    /// as they come up — each card fetches its own preview title/image from the linked
    /// page (LinkPreviewService), so nothing else needs updating.</summary>
    private static readonly (string GroupTitleKey, (string Url, string FallbackTitle)[] Links)[] ExtraLinkGroups =
    {
        ("settings_extra_3dprinting", new[]
        {
            ("https://cults3d.com/en/3d-model/gadget/sidepad-everest-keyboard-support-for-displaypad-and-macropad", "SidePad Everest — DisplayPad/MacroPad support"),
            ("https://cults3d.com/en/3d-model/gadget/display-dial-riser-stand-for-everest-keyboard-minifig-stand", "Display Dial riser stand for Everest keyboard"),
        }),
        ("settings_extra_other_projects", new[]
        {
            ("https://github.com/ramisotti13-eng/BaseCamp-Linux", "BaseCamp-Linux (ramisotti13-eng)"),
            ("https://gitlab.com/FransM/Sherpa", "Sherpa (FransM)"),
        }),
    };

    /// <summary>Populates the "Extra" section with <see cref="ExtraLinkGroups"/> and
    /// kicks off an async Open Graph preview fetch (title + image) for every card —
    /// cards start showing the fallback title with no image and update in place as
    /// previews land (or stay on the fallback if the fetch fails, e.g. offline).</summary>
    private void InitExtraLinksPanel()
    {
        var groups = ExtraLinkGroups
            .Select(g => new ExtraLinkGroup(Loc.Get(g.GroupTitleKey),
                g.Links.Select(l => new ExtraLinkItem(l.Url, l.FallbackTitle)).ToList()))
            .ToList();

        IcExtraLinkGroups.ItemsSource = groups;
        foreach (var item in groups.SelectMany(g => g.Items))
            _ = LoadExtraLinkPreviewAsync(item);
    }

    private async Task LoadExtraLinkPreviewAsync(ExtraLinkItem item)
    {
        var preview = await LinkPreviewService.GetPreviewAsync(item.Url);
        if (!string.IsNullOrWhiteSpace(preview.Title))
            item.Title = preview.Title!;

        if (preview.ImagePath is null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(preview.ImagePath);
            bmp.EndInit();
            bmp.Freeze();
            item.Image = bmp;
        }
        catch { /* corrupt/unreadable cached image — card just stays without one */ }
    }

    /// <summary>Click handler for an "Extra" card — opens its link in the default
    /// browser. Bound directly in the DataTemplate (MainWindow.xaml), so the sender's
    /// DataContext is the clicked ExtraLinkItem.</summary>
    private void ExtraLinkCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ExtraLinkItem item) return;
        Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
    }

    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    /// <summary>Loads THANKS.md (shipped next to K2.App.exe, see the csproj's
    /// Content entry linking it from the repo's K2/THANKS.md) into the Settings
    /// tab's bottom box, turning any http(s) URL into a clickable Hyperlink that
    /// opens in the default browser. Missing/unreadable file just leaves the box
    /// empty — not worth bothering the user about.</summary>
    private void InitAcknowledgementsPanel()
    {
        TxtAppAcknowledgements.Inlines.Clear();
        try
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "THANKS.md");
            if (!System.IO.File.Exists(path)) return;
            string text = System.IO.File.ReadAllText(path);

            int last = 0;
            foreach (Match m in UrlRegex.Matches(text))
            {
                if (m.Index > last) TxtAppAcknowledgements.Inlines.Add(new Run(text[last..m.Index]));
                var link = new Hyperlink(new Run(m.Value)) { NavigateUri = new Uri(m.Value) };
                link.RequestNavigate += (s, e) =>
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    e.Handled = true;
                };
                TxtAppAcknowledgements.Inlines.Add(link);
                last = m.Index + m.Length;
            }
            if (last < text.Length) TxtAppAcknowledgements.Inlines.Add(new Run(text[last..]));
        }
        catch
        {
            TxtAppAcknowledgements.Inlines.Clear();
        }
    }

    /// <summary>Populates the Font combo with <see cref="FontCatalog.Options"/> and
    /// selects the persisted choice (default Roboto). The font itself is already
    /// applied at process startup (see App.OnStartup); this only drives the UI.</summary>
    private void InitAppFontCombo()
    {
        CmbAppFont.Items.Clear();
        foreach (var opt in FontCatalog.Options)
            CmbAppFont.Items.Add(new ComboBoxItem { Content = opt.DisplayName, Tag = opt.Key });

        string current = AppSettings.AppFontFamily;
        CmbAppFont.SelectedIndex = 0;
        for (int i = 0; i < CmbAppFont.Items.Count; i++)
        {
            if ((string)((ComboBoxItem)CmbAppFont.Items[i]).Tag == current)
            {
                CmbAppFont.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>Persists the chosen font and applies it live to every K2 window
    /// (see FontCatalog.Apply / K2Theme.xaml's K2AppFontFamily DynamicResource).</summary>
    private void CmbAppFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAppFont.SelectedItem is not ComboBoxItem item) return;
        string key = (string)item.Tag;
        AppSettings.SetAppFontFamily(key);
        FontCatalog.Apply(key);
    }

    /// <summary>Populates the Accent color combo with <see cref="AccentCatalog.Options"/>
    /// ("K2 Red" / "Mountain Blue") and selects the persisted choice (default K2 Red).
    /// The accent is already applied at process startup (see App.OnStartup); this only
    /// drives the UI.</summary>
    private void InitAppAccentCombo()
    {
        CmbAppAccentColor.Items.Clear();
        foreach (var opt in AccentCatalog.Options)
            CmbAppAccentColor.Items.Add(new ComboBoxItem { Content = Loc.Get(AccentDisplayNameKey(opt.Key)), Tag = opt.Key });

        string current = AppSettings.AccentTheme;
        CmbAppAccentColor.SelectedIndex = 0;
        for (int i = 0; i < CmbAppAccentColor.Items.Count; i++)
        {
            if ((string)((ComboBoxItem)CmbAppAccentColor.Items[i]).Tag == current)
            {
                CmbAppAccentColor.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>Persists the chosen accent color theme and applies it live to every K2
    /// window (see AccentCatalog.Apply / K2Theme.xaml's K2AccentBrush family of
    /// DynamicResources).</summary>
    private void CmbAppAccentColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAppAccentColor.SelectedItem is not ComboBoxItem item) return;
        string key = (string)item.Tag;
        AppSettings.SetAccentTheme(key);
        AccentCatalog.Apply(key);
    }

    /// <summary>Selects the persisted "Icon style" radio (default "color"). Deliberately set
    /// here in code rather than via XAML <c>IsChecked="True"</c> — a XAML-default-triggered
    /// <c>Checked</c> event firing synchronously during <c>InitializeComponent()</c>, before a
    /// later-declared element is wired up, is the exact WPF gotcha that caused a real crash in
    /// this same feature earlier the same day (see CHANGELOG.md 2026-07-29) — so every radio
    /// pair in this codebase now gets its initial selection from code-behind, after construction
    /// has fully finished, never from the XAML markup itself.</summary>
    private void InitIconGalleryStyleRadios()
    {
        bool black = AppSettings.IconGalleryStyle == "black";
        RbIconStyleBlack.IsChecked = black;
        RbIconStyleColor.IsChecked = !black;
    }

    /// <summary>Persists the chosen icon style — read by <c>K2.App.Services.IconGalleryDefaults</c>
    /// the next time a "Default icon" auto-generation runs. Not retroactive: icons already
    /// generated under the previous style stay as they are, same as every other auto-generated
    /// icon in this app (see <c>IconImageGenerator.AccentColor</c>'s equivalent note).</summary>
    private void IconGalleryStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (sender != RbIconStyleBlack && sender != RbIconStyleColor) return;
        AppSettings.SetIconGalleryStyle(RbIconStyleBlack.IsChecked == true ? "black" : "color");
    }

    /// <summary>Maps an <see cref="AccentCatalog.AccentOption.Key"/> to its loc string
    /// key ("K2 Red" / "Mountain Blue" are translatable labels, unlike FontCatalog's
    /// font family names, which are proper nouns and shown as-is).</summary>
    private static string AccentDisplayNameKey(string key) => key switch
    {
        "MountainBlue" => "settings_accent_mountainblue",
        _               => "settings_accent_k2red",
    };

    /// <summary>Populates the Icon color combo: the default "Same as accent color"
    /// (empty key) first, then every <see cref="AccentCatalog.Options"/> entry, then
    /// "White" — and selects the persisted choice (default: same as accent). See
    /// <see cref="AppSettings.IconColorTheme"/> / <c>IconImageGenerator.ResolveIconColor</c>.</summary>
    private void InitAppIconColorCombo()
    {
        CmbAppIconColor.Items.Clear();
        CmbAppIconColor.Items.Add(new ComboBoxItem { Content = Loc.Get("settings_icon_color_default"), Tag = "" });
        foreach (var opt in AccentCatalog.Options)
            CmbAppIconColor.Items.Add(new ComboBoxItem { Content = Loc.Get(AccentDisplayNameKey(opt.Key)), Tag = opt.Key });
        CmbAppIconColor.Items.Add(new ComboBoxItem { Content = Loc.Get("settings_icon_color_white"), Tag = "White" });

        string current = AppSettings.IconColorTheme;
        CmbAppIconColor.SelectedIndex = 0;
        for (int i = 0; i < CmbAppIconColor.Items.Count; i++)
        {
            if ((string)((ComboBoxItem)CmbAppIconColor.Items[i]).Tag == current)
            {
                CmbAppIconColor.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>Persists the chosen icon color theme. Not applied live like the accent
    /// color — icons are one-shot GDI+ renders (see IconImageGenerator), so this only
    /// affects icons generated from now on.</summary>
    private void CmbAppIconColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAppIconColor.SelectedItem is not ComboBoxItem item) return;
        AppSettings.SetIconColorTheme((string)item.Tag);
    }

    /// <summary>"DisplayPad device mapping" button — opens the popup that lets the user
    /// fix which stable logical id a pad's raw SDK id resolves to (see
    /// RemappingDisplayPadClient/DisplayPadDeviceMap), needed after a USB port change
    /// makes the SDK renumber a pad. Passes _dpDeviceLabels for the "currently shown as"
    /// hint and DpRefreshDevices as the post-save callback so tabs/profiles reload
    /// immediately under their corrected ids.</summary>
    private void BtnDpDeviceMap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DpDeviceMapWindow(_dpClient, _dpDeviceLabels, DpRefreshDevices) { Owner = this };
        dlg.ShowDialog();
    }

    private void CkCloseToTray_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetCloseToTray(CkCloseToTray.IsChecked == true);
    }

    /// <summary>Persists the "start minimized to tray" flag. Read once at process start
    /// by App.OnStartup, so it takes effect at the next app launch.</summary>
    private void CkStartMinToTray_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetStartMinimizedToTray(CkStartMinToTray.IsChecked == true);
    }

    private void CkK2Autostart_Click(object sender, RoutedEventArgs e)
    {
        Services.K2AutostartService.SetEnabled(CkK2Autostart.IsChecked == true);
    }

    /// <summary>Reflects the current Windows-autostart state of Base Camp entries
    /// (registry Run + StartupApproved). Disabled if no entry is found.</summary>
    private void InitBcAutostartCheckbox()
    {
        var entries = Services.BaseCampProcessGuard.FindAutostartEntries();
        if (entries.Count == 0)
        {
            CkBcAutostart.IsEnabled = false;
            CkBcAutostart.IsChecked = false;
            TxtBcAutostartHint.Text = Loc.Get("settings_bc_autostart_none");
            return;
        }
        CkBcAutostart.IsEnabled = true;
        CkBcAutostart.IsChecked = entries.Any(x => x.Enabled);
    }

    /// <summary>Persists the "auto-stop Base Camp on startup" flag. Takes effect at
    /// the next K2 launch (see App.OnStartup).</summary>
    private void CkAutoStopBaseCamp_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetAutoStopBaseCamp(CkAutoStopBaseCamp.IsChecked == true);
    }

    private void CkKillBcWorker_Click(object sender, RoutedEventArgs e)
    {
        bool on = CkKillBcWorker.IsChecked == true;
        AppSettings.SetKillBaseCampWorker(on);
        if (on) Services.BaseCampProcessGuard.KillDisplayPadWorkers(msg => DpLog(msg));
    }

    /// <summary>Persists the "restart Base Camp on close" flag. Read at the moment K2's
    /// window actually closes (see MainWindow.xaml.cs's OnWindowClosed).</summary>
    private void CkRestartBcOnClose_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetRestartBaseCampOnClose(CkRestartBcOnClose.IsChecked == true);
    }

    // ================================================================
    // SignalRGB coexistence (see Services/SignalRgbGuard.cs)
    // ================================================================

    /// <summary>True while InitSignalRgbPanel is setting the radio buttons, so their
    /// Checked handler doesn't re-arm the guard for a value it just read back.</summary>
    private bool _signalRgbInit;

    private void InitSignalRgbPanel()
    {
        _signalRgbInit = true;
        try
        {
            switch (AppSettings.SignalRgbMode)
            {
                case SignalRgbMode.Off:  RbSignalRgbOff.IsChecked  = true; break;
                case SignalRgbMode.Stop: RbSignalRgbStop.IsChecked = true; break;
                default:                 RbSignalRgbYield.IsChecked = true; break;
            }
        }
        finally { _signalRgbInit = false; }

        RefreshSignalRgbStatus();

        // Keep the status line honest while the user has the tab open: the guard already
        // polls, we just mirror its transitions onto the UI thread.
        Services.SignalRgbGuard.StateChanged -= OnSignalRgbStateChanged;
        Services.SignalRgbGuard.StateChanged += OnSignalRgbStateChanged;
        Services.SignalRgbGuard.LightingReclaimed -= OnSignalRgbLightingReclaimed;
        Services.SignalRgbGuard.LightingReclaimed += OnSignalRgbLightingReclaimed;
    }

    /// <summary>SignalRGB just closed (or the user turned coexistence off): push K2's own
    /// lighting back onto the devices, otherwise they keep whatever frame SignalRGB left
    /// behind until the user touches a slider. Everest 60 and Makalu have no single
    /// "reapply everything" entry point yet, so they come back on the next UI change.</summary>
    private void OnSignalRgbLightingReclaimed() => Dispatcher.BeginInvoke(new Action(() =>
    {
        try { ApplyCurrentEffect(); }      catch (Exception ex) { App.WriteLog("[SignalRGB] Everest reapply failed: " + ex.Message); }
        try { ApplyCurrentMacroEffect(); } catch (Exception ex) { App.WriteLog("[SignalRGB] MacroPad reapply failed: " + ex.Message); }
    }));

    private void OnSignalRgbStateChanged(bool running)
        => Dispatcher.BeginInvoke(new Action(RefreshSignalRgbStatus));

    private void RefreshSignalRgbStatus()
    {
        string? install = Services.SignalRgbGuard.InstallDirectory();
        string text = install is null
            ? Loc.Get("settings_signalrgb_status_missing")
            : string.Format(Loc.Get("settings_signalrgb_status_installed"), install);

        if (Services.SignalRgbGuard.LightingYielded)
            text += Environment.NewLine + Loc.Get("settings_signalrgb_status_running");

        TxtSignalRgbStatus.Text = text;

        bool installed = Services.SignalRgbGuard.K2PluginsInstalled();
        BtnSignalRgbRemovePlugins.IsEnabled = installed;
    }

    private void RbSignalRgbMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_signalRgbInit) return;

        SignalRgbMode mode = sender == RbSignalRgbOff  ? SignalRgbMode.Off
                           : sender == RbSignalRgbStop ? SignalRgbMode.Stop
                                                       : SignalRgbMode.Yield;
        AppSettings.SetSignalRgbMode(mode);

        // Stop mode only acts at startup (like AutoStopBaseCamp) — but if SignalRGB is up
        // right now, close it immediately so the user sees the setting do something.
        if (mode == SignalRgbMode.Stop)
            Services.SignalRgbGuard.KillSignalRgb(App.WriteLog);

        Services.SignalRgbGuard.Start(App.WriteLog);
        RefreshSignalRgbStatus();
    }

    private void BtnSignalRgbInstallPlugins_Click(object sender, RoutedEventArgs e)
    {
        int n = Services.SignalRgbGuard.InstallK2Plugins(App.WriteLog);
        MessageBox.Show(this,
            n > 0 ? string.Format(Loc.Get("settings_signalrgb_plugins_installed"), n)
                  : Loc.Get("settings_signalrgb_plugins_none"),
            Loc.Get("settings_signalrgb_group"), MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshSignalRgbStatus();
    }

    private void BtnSignalRgbRemovePlugins_Click(object sender, RoutedEventArgs e)
    {
        int n = Services.SignalRgbGuard.RemoveK2Plugins(App.WriteLog);
        MessageBox.Show(this,
            string.Format(Loc.Get("settings_signalrgb_plugins_removed"), n),
            Loc.Get("settings_signalrgb_group"), MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshSignalRgbStatus();
    }

    private void BtnSignalRgbOpenPluginFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = Services.SignalRgbGuard.UserPluginDirectory;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { App.WriteLog("[SignalRGB] cannot open plugin folder: " + ex.Message); }
    }

    /// <summary>Copies the current app log (and crash log, if any) to a user-chosen
    /// folder — "Export log" button in the General group.</summary>
    private void BtnAppExportLog_Click(object sender, RoutedEventArgs e)
    {
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("export_pick_folder") };
        if (folder.ShowDialog(this) != true) return;

        try
        {
            string dest = System.IO.Path.Combine(folder.FolderName, System.IO.Path.GetFileName(App.LogPath));
            System.IO.File.Copy(App.LogPath, dest, overwrite: true);

            if (System.IO.File.Exists(App.CrashLogPath))
            {
                string destCrash = System.IO.Path.Combine(folder.FolderName, System.IO.Path.GetFileName(App.CrashLogPath));
                System.IO.File.Copy(App.CrashLogPath, destCrash, overwrite: true);
            }

            LblStatus.Text = Loc.Get("settings_export_log_done", folder.FolderName);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Get("settings_export_log_btn"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CkBcAutostart_Click(object sender, RoutedEventArgs e)
    {
        bool enable = CkBcAutostart.IsChecked == true;
        int changed = Services.BaseCampProcessGuard.SetAutostartEnabled(enable, msg => DpLog(msg));
        LblStatus.Text = Loc.Get("settings_bc_autostart_done", changed);
        // Re-read the real state (HKLM entries may have failed without admin rights).
        InitBcAutostartCheckbox();
    }

    private void RbLogLevel_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == RbLogOff)          AppSettings.SetLogLevel(K2LogLevel.Off);
        else if (sender == RbLogVerbose) AppSettings.SetLogLevel(K2LogLevel.Verbose);
        else                              AppSettings.SetLogLevel(K2LogLevel.Normal);
    }

    /// <summary>Wipes every app preference AND every saved profile/key binding/lighting/
    /// macro for every device, then restarts K2 — the "Restore all defaults" button in
    /// the Settings tab's Danger Zone. Distinct from the per-device "Restore defaults"
    /// buttons (which only reset the currently selected profile of one device and don't
    /// restart). Restarting (rather than trying to refresh a dozen open panels in place)
    /// guarantees every tab comes back up reading the freshly-blank stores from scratch —
    /// and resetting <see cref="AppSettings.BcImportPromptShown"/> means the "Import from
    /// Base Camp?" prompt (see CheckFirstRunBcImport) fires again right after.</summary>
    private void BtnAppRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_app_confirm"),
            Loc.Get("restore_defaults_app"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        AppSettings.ResetToDefaults();
        _evStore.ResetAllData();
        _ev60Store.ResetAllData();
        _mkStore.ResetAllData();
        _store.ResetAllData();
        _macroStore?.ResetAllData();
        _dpStore.ResetAllData();

        RestartApp();
    }

    /// <summary>Applies the centralized debug flag to every device module at once.</summary>
    private void ApplyDebugModeToAllDevices(bool debug)
    {
        ApplyDebugMode(debug);     // Everest    — MainWindow.SectionNav.cs
        ApplyMpDebugMode(debug);   // MacroPad   — MainWindow.Keys.cs
        ApplyDpDebugMode(debug);   // DisplayPad — MainWindow.DisplayPad.cs
        ApplyEv60DebugMode(debug); // Everest 60 — MainWindow.Everest60.cs
        ApplyMkDebugMode(debug);   // Makalu     — MainWindow.Makalu.cs
    }
}
