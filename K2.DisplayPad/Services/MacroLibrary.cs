using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace K2.DisplayPad.Services;

/// <summary>
/// Carica la libreria di macro nominate esportate dal DB di Mountain
/// BaseCamp (<c>Assets/BaseCampMacros.json</c>) e fornisce lookup per nome.
/// Ogni entry e' una delle forme:
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

    /// <summary>Carica dal path indicato o, se null, dal default
    /// <c>BaseDirectory/Assets/BaseCampMacros.json</c>.</summary>
    public static MacroLibrary Load(string? path = null)
    {
        var lib = new MacroLibrary();
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "BaseCampMacros.json");
        if (!File.Exists(path))
        {
            App.WriteLog($"[MacroLibrary] file non trovato: {path}");
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
            App.WriteLog($"[MacroLibrary] caricate {lib._byName.Count} macro");
        }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroLibrary] errore caricamento: {ex.Message}");
        }
        return lib;
    }

    public bool TryGet(string name, out MacroDefinition def) =>
        _byName.TryGetValue(name, out def!);
}

public sealed record MacroDefinition(string Name, string Type, string? Value);
