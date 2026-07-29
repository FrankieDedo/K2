using System;
using System.IO;
using System.Text.Json;

namespace K2.Core.Services;

/// <summary>
/// Global OBS Studio connection settings (host/port/password) — one connection for the whole
/// app, matching real Base Camp's single global <c>Settings.OBSPort</c>/<c>OBSPassword</c>
/// (confirmed via the decompiled DB schema). Persisted as a small JSON file, same
/// Load/Save/lock skeleton as <see cref="GoogleHomeStore"/>/<see cref="AppSettings"/>.
/// </summary>
public static class ObsStore
{
    private sealed class Data
    {
        public string Host { get; set; } = "127.0.0.1";
        public string Port { get; set; } = "4455";
        public string Password { get; set; } = "";
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    private static string StorePath => Path.Combine(K2Paths.Root, "obs_settings.json");

    public static string Host { get { EnsureLoaded(); return _data.Host; } }
    public static string Port { get { EnsureLoaded(); return _data.Port; } }
    public static string Password { get { EnsureLoaded(); return _data.Password; } }

    public static void SetConnection(string host, string port, string password)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data.Host = host;
            _data.Port = port;
            _data.Password = password;
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
            string path = StorePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Data>(json);
                if (data is not null) _data = data;
            }
        }
        catch
        {
            _data = new Data();
        }
    }

    private static void Save()
    {
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
