using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace K2.DisplayPad.Services;

/// <summary>
/// Loads the library of named macros exported from Mountain BaseCamp's DB
/// (<c>Assets/BaseCampMacros.json</c>) and provides lookup by name.
/// Each entry has one of these shapes:
///   { "name": "À",                       "type": "text", "value": "À" }
///   { "name": "DISCORD_MUTE_MIC",        "type": "keys", "value": "^+%m" }
///   { "name": "TEST",                    "type": "raw",  "value": [ {...}, ... ] }
/// </summary>
public sealed class MacroLibrary
{
    private readonly Dictionary<string, MacroDefinition> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, MacroDefinition> ByName => _byName;
    public int Count => _byName.Count;

    /// <summary>Loads from the given path or, if null, from the default
    /// <c>BaseDirectory/Assets/BaseCampMacros.json</c>.</summary>
    public static MacroLibrary Load(string? path = null)
    {
        var lib = new MacroLibrary();
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "BaseCampMacros.json");
        if (!File.Exists(path))
        {
            App.WriteLog($"[MacroLibrary] file not found: {path}");
            return lib;
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                var type = item.GetProperty("type").GetString() ?? "";
                string? value = null;
                if (item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    value = v.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                lib._byName[name] = new MacroDefinition(name, type, value);
            }
            App.WriteLog($"[MacroLibrary] loaded {lib._byName.Count} macros");
        }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroLibrary] load error: {ex.Message}");
        }
        return lib;
    }

    public bool TryGet(string name, out MacroDefinition def) =>
        _byName.TryGetValue(name, out def!);
}

public sealed record MacroDefinition(string Name, string Type, string? Value);
