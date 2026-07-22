using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace K2.Core;

/// <summary>Verbosity level for K2's diagnostic logging.</summary>
public enum K2LogLevel
{
    Off = 0,
    Normal = 1,
    Verbose = 2,
}

/// <summary>
/// Centralized, app-wide settings shared by every K2 device module (Everest,
/// MacroPad, DisplayPad, ...). Replaces the old per-device "Debug" checkboxes:
/// a single <see cref="DebugMode"/> flag now drives debug UI/behavior everywhere,
/// and <see cref="LogLevel"/> controls how chatty the logging is (in particular,
/// per-key-press logs and LED-poll diagnostic logs only fire at <see cref="K2LogLevel.Verbose"/>).
///
/// Persisted as a small JSON file in %LOCALAPPDATA%\K2\app_settings.json so it is
/// shared across K2.App and any other K2 process, independent of the per-device
/// SQLite stores (which persist per-device profile settings, not app-wide ones).
/// </summary>
public static class AppSettings
{
    private sealed class Data
    {
        public bool DebugMode { get; set; }
        public K2LogLevel LogLevel { get; set; } = K2LogLevel.Off;
        public bool KillBaseCampWorker { get; set; } = true;
        public bool AutoStopBaseCamp { get; set; } = true;
        public bool CloseToTray { get; set; }
        public bool StartMinimizedToTray { get; set; }
        public bool RestartBaseCampOnClose { get; set; }
        public List<string> RecentExecPaths { get; set; } = new();
        public List<string> RecentFolderPaths { get; set; } = new();
        public string? MakaluDeviceName { get; set; }
        public string? Everest60DeviceName { get; set; }
        public string AppFontFamily { get; set; } = Services.FontCatalog.DefaultKey;
        public bool BcImportPromptShown { get; set; }
        public string? BaseCampDllFolder { get; set; }
        public List<string> SavedPickerColors { get; set; } = new();
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    /// <summary>Raised after DebugMode or LogLevel changes (and has been persisted).</summary>
    public static event Action? Changed;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "app_settings.json");

    /// <summary>Centralized debug flag: when true, debug UI/behavior is enabled on ALL devices.</summary>
    public static bool DebugMode
    {
        get { EnsureLoaded(); return _data.DebugMode; }
    }

    /// <summary>Log verbosity: Off, Normal, or Verbose.</summary>
    public static K2LogLevel LogLevel
    {
        get { EnsureLoaded(); return _data.LogLevel; }
    }

    public static void SetDebugMode(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.DebugMode == value) return;
            _data.DebugMode = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Drive the DisplayPad through the raw USB-HID engine (DisplayPadNativeClient)
    /// instead of DisplayPadSDK.dll + satellite process. Hardcoded ON since 2026-07-16:
    /// it went opt-in → default-ON → fixed as the native engines proved reliable, while
    /// the SDK backend accumulated machine-specific failures (DisplayPadResetPicture
    /// returning false on some installs, no raw-buffer blanking). The constant is kept
    /// (rather than deleting every call site) so the SDK/satellite code path stays
    /// compilable as reference and can be re-enabled here in one line if a machine ever
    /// needs ruling out. Old persisted values in app_settings.json are simply ignored.
    /// </summary>
    public static bool DisplayPadNativeEngine => true;

    /// <summary>
    /// Drive the Everest Max's connectivity (open/close/init) and RGB effects through the
    /// raw USB-HID engine (<c>EverestHidNative</c>) instead of SDKDLL.dll. The full
    /// 171-key remap event stream, numpad display icons, and Media Dock still go through
    /// SDKDLL.dll regardless of this flag — those are not natively implemented yet
    /// (unconfirmed wire protocol, see task/memory "Everest nativo — Fase 3/4").
    /// Hardcoded ON since 2026-07-16 — same rationale and re-enable path as
    /// <see cref="DisplayPadNativeEngine"/>.
    /// </summary>
    public static bool EverestNativeEngine => true;

    /// <summary>
    /// When the native DisplayPad engine is active, automatically terminate Base Camp's
    /// MountainDisplayPadWorker (a concurrent HID writer that corrupts native uploads).
    /// Checked at engine start and re-checked periodically. Default ON.
    /// </summary>
    public static bool KillBaseCampWorker
    {
        get { EnsureLoaded(); return _data.KillBaseCampWorker; }
    }

    public static void SetKillBaseCampWorker(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.KillBaseCampWorker == value) return;
            _data.KillBaseCampWorker = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// When true, K2 stops the Base Camp Windows service and kills every Base Camp
    /// executable still running (GUI, service, workers, Makalu monitor) every time
    /// K2 starts — including anything Windows autostart just relaunched — so K2 fully
    /// replaces Base Camp instead of the two fighting over the same USB devices.
    /// Checked once at K2.App startup (see App.OnStartup). Default ON.
    /// </summary>
    public static bool AutoStopBaseCamp
    {
        get { EnsureLoaded(); return _data.AutoStopBaseCamp; }
    }

    public static void SetAutoStopBaseCamp(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.AutoStopBaseCamp == value) return;
            _data.AutoStopBaseCamp = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>When true, closing the main window hides it to the system tray instead
    /// of exiting the app (the tray icon's "Exit" item performs the real close).</summary>
    public static bool CloseToTray
    {
        get { EnsureLoaded(); return _data.CloseToTray; }
    }

    public static void SetCloseToTray(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.CloseToTray == value) return;
            _data.CloseToTray = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>When true, K2 starts with its drivers active but the window hidden in
    /// the tray, instead of showing it. Read once at startup (see App.OnStartup).</summary>
    public static bool StartMinimizedToTray
    {
        get { EnsureLoaded(); return _data.StartMinimizedToTray; }
    }

    public static void SetStartMinimizedToTray(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.StartMinimizedToTray == value) return;
            _data.StartMinimizedToTray = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// When true, K2 puts Base Camp back the way it normally starts (see
    /// <see cref="Services.BaseCampProcessGuard.RestartKilledProcesses"/>: Windows
    /// service + autostart entries + MountainDisplayPadWorker) when the user closes K2
    /// (real exit, not close-to-tray). MountainDisplayPadWorker is always relaunched
    /// silently (no window); other Base Camp executables (e.g. the GUI) are relaunched
    /// the normal way. Default OFF — most users who enabled auto-stop want Base Camp to
    /// stay stopped after closing K2.
    /// </summary>
    public static bool RestartBaseCampOnClose
    {
        get { EnsureLoaded(); return _data.RestartBaseCampOnClose; }
    }

    public static void SetRestartBaseCampOnClose(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.RestartBaseCampOnClose == value) return;
            _data.RestartBaseCampOnClose = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>User-chosen nickname for the Makalu tab header. Makalu has no
    /// per-device SQLite store (no profile persistence — see MakaluService),
    /// so this lives here instead of a "device.name" store setting like
    /// MacroPad/Everest/DisplayPad use.</summary>
    public static string? MakaluDeviceName
    {
        get { EnsureLoaded(); return _data.MakaluDeviceName; }
    }

    public static void SetMakaluDeviceName(string? value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.MakaluDeviceName == value) return;
            _data.MakaluDeviceName = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>User-chosen nickname for the Everest 60 tab header. Same
    /// reasoning as <see cref="MakaluDeviceName"/> — no per-device store.</summary>
    public static string? Everest60DeviceName
    {
        get { EnsureLoaded(); return _data.Everest60DeviceName; }
    }

    public static void SetEverest60DeviceName(string? value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.Everest60DeviceName == value) return;
            _data.Everest60DeviceName = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Key into <see cref="Services.FontCatalog.Options"/> for the app-wide
    /// UI font (Settings &gt; Font). Applied live via <see cref="Services.FontCatalog.Apply"/>
    /// on every K2 process that reads this shared settings file.</summary>
    public static string AppFontFamily
    {
        get { EnsureLoaded(); return _data.AppFontFamily; }
    }

    public static void SetAppFontFamily(string key)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.AppFontFamily == key) return;
            _data.AppFontFamily = key;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Whether the one-time "Import from Base Camp?" prompt has already been
    /// shown (see MainWindow.Settings.cs's CheckFirstRunBcImport). Reset to false by
    /// <see cref="ResetToDefaults"/>, so the prompt fires again after the app-wide
    /// "Restore all defaults" and the following restart. Can also be forced again at
    /// any time from the Settings tab regardless of this flag.</summary>
    public static bool BcImportPromptShown
    {
        get { EnsureLoaded(); return _data.BcImportPromptShown; }
    }

    public static void SetBcImportPromptShown(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.BcImportPromptShown == value) return;
            _data.BcImportPromptShown = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>User-chosen folder to search for non-redistributable Base Camp native
    /// DLLs (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) when Base Camp isn't
    /// installed on this PC and the user doesn't want to copy them next to K2.App.exe
    /// or set the K2_BASECAMP_DIR environment variable by hand. Checked by
    /// K2.App's NativeDependencyResolver — see Settings tab's "Base Camp DLL folder"
    /// picker.</summary>
    public static string? BaseCampDllFolder
    {
        get { EnsureLoaded(); return _data.BaseCampDllFolder; }
    }

    public static void SetBaseCampDllFolder(string? value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.BaseCampDllFolder == value) return;
            _data.BaseCampDllFolder = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Global custom-color palette shared by every device's color picker
    /// (<see cref="ColorPickerDialog"/>), newest first. Mirrors Base Camp's own
    /// <c>Settings.SavedPickerColors</c> (one app-wide palette, not per-device — see
    /// <c>DATABASE_SCHEMA.sql</c>): a color saved while editing Everest Max lighting is
    /// immediately available while editing MacroPad/Everest 60/Makalu/DisplayPad too.
    /// Stored as "#RRGGBB" strings.</summary>
    public static IReadOnlyList<string> SavedPickerColors
    {
        get { EnsureLoaded(); return _data.SavedPickerColors; }
    }

    private const int MaxSavedPickerColors = 30;

    /// <summary>Adds (or moves to front, if already present) a "#RRGGBB" color in the
    /// shared palette. Oldest entries beyond <see cref="MaxSavedPickerColors"/> are
    /// dropped.</summary>
    public static void AddSavedPickerColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        EnsureLoaded();
        lock (_lock)
        {
            _data.SavedPickerColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
            _data.SavedPickerColors.Insert(0, hex);
            while (_data.SavedPickerColors.Count > MaxSavedPickerColors)
                _data.SavedPickerColors.RemoveAt(_data.SavedPickerColors.Count - 1);
            Save();
        }
        Changed?.Invoke();
    }

    public static void RemoveSavedPickerColor(string hex)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.SavedPickerColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase)) > 0)
                Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Resets every app-wide preference (tray/autostart/font/log level/native
    /// engine toggles/auto-stop/kill-worker/recent paths/device nicknames/saved color
    /// palette) back to its built-in default and persists it. Used by the app-wide
    /// "Restore all defaults" button in the Settings tab. Some flags (native engine
    /// toggles) are only read once at startup, so the caller should advise the user to
    /// restart K2.</summary>
    public static void ResetToDefaults()
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data = new Data();
            Save();
        }
        Changed?.Invoke();
    }

    public static void SetLogLevel(K2LogLevel value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.LogLevel == value) return;
            _data.LogLevel = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Most-recently-used executable/file paths chosen in the "Open program / file" action picker (newest first).</summary>
    public static IReadOnlyList<string> RecentExecPaths
    {
        get { EnsureLoaded(); return _data.RecentExecPaths; }
    }

    /// <summary>Most-recently-used folder paths chosen in the "Open folder" action picker (newest first).</summary>
    public static IReadOnlyList<string> RecentFolderPaths
    {
        get { EnsureLoaded(); return _data.RecentFolderPaths; }
    }

    public static void AddRecentExecPath(string path) => AddRecent(_data.RecentExecPaths, path);

    public static void AddRecentFolderPath(string path) => AddRecent(_data.RecentFolderPaths, path);

    public static void RemoveRecentExecPath(string path) => RemoveRecent(_data.RecentExecPaths, path);

    public static void RemoveRecentFolderPath(string path) => RemoveRecent(_data.RecentFolderPaths, path);

    private const int MaxRecentPaths = 10;

    private static void RemoveRecent(List<string> list, string path)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
                Save();
        }
    }

    private static void AddRecent(List<string> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        EnsureLoaded();
        lock (_lock)
        {
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            while (list.Count > MaxRecentPaths) list.RemoveAt(list.Count - 1);
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }
    }

    private static void Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Data>(json);
                if (data is not null) _data = data;
            }
        }
        catch
        {
            // Corrupt/missing settings file: fall back to defaults.
            _data = new Data();
        }

    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort persistence; a failed write just means the setting
            // won't survive a restart, which is not worth crashing over.
        }
    }
}
