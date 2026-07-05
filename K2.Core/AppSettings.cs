using System;
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
