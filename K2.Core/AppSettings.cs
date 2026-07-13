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
        public K2LogLevel LogLevel { get; set; } = K2LogLevel.Normal;
        public bool DisplayPadNativeEngine { get; set; }
        public bool EverestNativeEngine { get; set; }
        public bool KillBaseCampWorker { get; set; } = true;
        public bool AutoStopBaseCamp { get; set; } = true;
        public bool CloseToTray { get; set; }
        public bool StartMinimizedToTray { get; set; }
        public List<string> RecentExecPaths { get; set; } = new();
        public List<string> RecentFolderPaths { get; set; } = new();
        public string? MakaluDeviceName { get; set; }
        public string? Everest60DeviceName { get; set; }
        public string AppFontFamily { get; set; } = Services.FontCatalog.DefaultKey;
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
    /// Experimental: drive the DisplayPad through the raw USB-HID engine
    /// (DisplayPadNativeClient) instead of DisplayPadSDK.dll + satellite process.
    /// Read once at startup (backend is chosen when MainWindow is constructed),
    /// so changing it requires an app restart.
    /// </summary>
    public static bool DisplayPadNativeEngine
    {
        get { EnsureLoaded(); return _data.DisplayPadNativeEngine; }
    }

    public static void SetDisplayPadNativeEngine(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.DisplayPadNativeEngine == value) return;
            _data.DisplayPadNativeEngine = value;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Experimental: drive the Everest Max's connectivity (open/close/init) and RGB
    /// effects through the raw USB-HID engine (<c>EverestHidNative</c>) instead of
    /// SDKDLL.dll. The full 171-key remap event stream, numpad display icons, and
    /// Media Dock still go through SDKDLL.dll regardless of this flag — those are not
    /// natively implemented yet (unconfirmed wire protocol, see task/memory "Everest
    /// nativo — Fase 3/4"). Read once at startup, so changing it requires a restart.
    /// </summary>
    public static bool EverestNativeEngine
    {
        get { EnsureLoaded(); return _data.EverestNativeEngine; }
    }

    public static void SetEverestNativeEngine(bool value)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_data.EverestNativeEngine == value) return;
            _data.EverestNativeEngine = value;
            Save();
        }
        Changed?.Invoke();
    }

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
