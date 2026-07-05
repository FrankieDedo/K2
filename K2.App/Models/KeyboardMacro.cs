// Models/KeyboardMacro.cs — model for recorded macros
// Compatible with the BaseCamp InputsJson format (for import)

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.App.Models;

/// <summary>Single recorded action within a macro.</summary>
public sealed class MacroInput
{
    /// <summary>"keydown", "keyup", "mousedown", "mouseup", "mousemove", "text"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "keydown";

    /// <summary>Virtual key code (for key events) or mouse button name.</summary>
    [JsonPropertyName("key")]
    public int Key { get; set; }

    /// <summary>Delay in ms before this action (0 = immediate).</summary>
    [JsonPropertyName("delay")]
    public int DelayMs { get; set; }

    /// <summary>Mouse coordinates (for mousemove).</summary>
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    /// <summary>Text (for "text" type).</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>Macro playback options.</summary>
public enum MacroPlayback
{
    Once,           // Execute once
    RepeatN,        // Repeat N times
    WhileHeld,      // Repeat while the key is held
    Toggle          // Start/stop with the same key
}

/// <summary>Delay options between actions.</summary>
public enum MacroDelay
{
    Recorded,       // Use recorded delays
    NoDelay,        // No delay
    Custom          // Use a fixed custom delay
}

/// <summary>Complete macro definition.</summary>
public sealed class MacroDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool RecordMouse { get; set; }
    public bool RecordKeyboard { get; set; } = true;
    public MacroDelay DelayOption { get; set; } = MacroDelay.Recorded;
    public int CustomDelayMs { get; set; } = 50;
    public MacroPlayback PlaybackOption { get; set; } = MacroPlayback.Once;
    public int RepeatCount { get; set; } = 1;
    public List<MacroInput> Inputs { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    /// <summary>Serializes Inputs to JSON (for saving to the DB).</summary>
    public string InputsToJson() =>
        JsonSerializer.Serialize(Inputs, _jsonOpts);

    /// <summary>Deserializes Inputs from JSON (for loading from the DB).</summary>
    public static List<MacroInput> InputsFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<MacroInput>>(json, _jsonOpts) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Creates from BaseCamp format (Macros table).</summary>
    public static MacroDefinition FromBaseCamp(
        int id, string name, bool recordMouse, bool recordKeyboard,
        string? delayOption, int customDelay, string? playbackOption,
        string? inputsJson, bool isActive, int order)
    {
        var def = new MacroDefinition
        {
            Id = id,
            Name = name ?? "",
            RecordMouse = recordMouse,
            RecordKeyboard = recordKeyboard,
            CustomDelayMs = customDelay,
            IsActive = isActive,
            Order = order,
            ModifiedAt = DateTime.Now
        };

        // Map BC strings to our enums
        def.DelayOption = delayOption?.ToLowerInvariant() switch
        {
            "nodelay" => MacroDelay.NoDelay,
            "custom"  => MacroDelay.Custom,
            _         => MacroDelay.Recorded
        };
        def.PlaybackOption = playbackOption?.ToLowerInvariant() switch
        {
            "repeatn"   => MacroPlayback.RepeatN,
            "whileheld" => MacroPlayback.WhileHeld,
            "toggle"    => MacroPlayback.Toggle,
            _           => MacroPlayback.Once
        };

        // BC InputsJson: may be an array of objects with different keys.
        // Try parsing directly — our MacroInput is a superset.
        def.Inputs = InputsFromJson(inputsJson);
        return def;
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false
    };
}
