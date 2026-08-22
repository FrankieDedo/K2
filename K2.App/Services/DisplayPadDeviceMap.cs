using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Persisted "raw SDK device id" -&gt; "logical id" redirect table for DisplayPad, edited
/// by the user via the "DisplayPad device mapping" popup in General Settings
/// (<see cref="K2.App.DpDeviceMapWindow"/>). Exists because the SDK re-numbers a pad's
/// device id when its USB port changes (a real user report: swapping two pads' ports made
/// the SDK swap which one reports id 2 vs 3, scrambling which stored profiles/settings
/// showed up on which physical panel) — everything else in K2.App (DisplayPadStore keys,
/// tab labels, IActionHost lookups, ...) is keyed by a STABLE logical id, and only
/// <see cref="RemappingDisplayPadClient"/> (which consults this table) and the mapping
/// popup itself ever see the SDK's raw, port-dependent id. Ids never remapped default to
/// identity (logical == raw), so a fresh install/an untouched mapping behaves exactly as
/// before this feature existed.
/// </summary>
public static class DisplayPadDeviceMap
{
    private static string FilePath => Path.Combine(K2Paths.For("K2.DisplayPad"), "device_map.json");

    private static Dictionary<int, int>? _cache;
    private static readonly object _lock = new();

    /// <summary>Raw SDK id -&gt; logical id. Ids not present here map to themselves.</summary>
    public static IReadOnlyDictionary<int, int> GetAll()
    {
        EnsureLoaded();
        return _cache!;
    }

    /// <summary>Replaces the whole table and persists it — called once by
    /// DpDeviceMapWindow's Save button.</summary>
    public static void SetAll(IReadOnlyDictionary<int, int> map)
    {
        lock (_lock)
        {
            _cache = new Dictionary<int, int>(map);
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;
        lock (_lock)
        {
            if (_cache is not null) return;
            Load();
        }
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<Dictionary<int, int>>(json);
                if (data is not null) { _cache = data; return; }
            }
        }
        catch
        {
            // Corrupt/missing file: fall back to an empty (identity) mapping.
        }
        _cache = new Dictionary<int, int>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence; a failed write just means the mapping
            // won't survive a restart, which is not worth crashing over.
        }
    }
}
