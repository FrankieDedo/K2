using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>
/// Value of a <see cref="Tag"/> action: one key that does different things depending on HOW it
/// is pressed (issue #31 + the Discord "Ctrl/Shift+Numpad3" request). It wraps the key's ordinary
/// action (<see cref="Type"/>/<see cref="Value"/>) and adds, on top of it:
/// <list type="bullet">
/// <item>a hold behaviour — <see cref="HoldMode.Long"/> (a second action after
/// <see cref="HoldMs"/>) or <see cref="HoldMode.Repeat"/> (the base action again every
/// <see cref="RepeatMs"/> while held);</item>
/// <item>per-modifier actions: holding Ctrl / Shift / Alt / Win on the keyboard while pressing the
/// key runs that action instead of the base one.</item>
/// </list>
/// The wrapper is how this reaches every device without a schema change: the stores keep a plain
/// (ActionType, ActionValue) pair, and anything that has no reason to know about triggers (labels,
/// icons, Base Camp export) reads the wrapped action through <see cref="Unwrap"/>. Because the
/// stored type is not one a firmware understands, a wrapped key is always run by K2 itself —
/// which is the point: the firmware can't time a long press.
/// Only "executable" base types can be wrapped (<see cref="CanWrap"/>): page navigation and live
/// tiles keep their own dedicated handling on the DisplayPad.
/// </summary>
public sealed class TriggerPayload
{
    public const string Tag = "trigger";

    public enum HoldMode { None, Long, Repeat }

    /// <summary>Modifier names in the order they are tested — the first one held wins.</summary>
    public static readonly string[] Modifiers = { "ctrl", "shift", "alt", "win" };

    [JsonPropertyName("t")] public string Type { get; set; } = "none";
    [JsonPropertyName("v")] public string Value { get; set; } = "";

    [JsonPropertyName("hold")] public HoldMode Hold { get; set; }
    /// <summary>Long press threshold (per key), ms.</summary>
    [JsonPropertyName("holdMs")] public int HoldMs { get; set; } = 500;
    [JsonPropertyName("lt")] public string? LongType { get; set; }
    [JsonPropertyName("lv")] public string? LongValue { get; set; }
    /// <summary>Repeat interval while held, ms (the first repeat waits <see cref="HoldMs"/>).</summary>
    [JsonPropertyName("repeatMs")] public int RepeatMs { get; set; } = 100;

    [JsonPropertyName("mods")] public List<TriggerModAction> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TriggerPayload? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<TriggerPayload>(value, JsonOpts); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>True when there is anything beyond the base action — otherwise the key should be
    /// stored as its plain action (see <see cref="Wrap"/>).</summary>
    [JsonIgnore]
    public bool HasExtras =>
        (Hold == HoldMode.Long && !string.IsNullOrEmpty(LongType) && LongType != "none")
        || Hold == HoldMode.Repeat
        || Mods.Exists(m => !string.IsNullOrEmpty(m.Type) && m.Type != "none");

    public TriggerModAction? ModAction(string modifier) =>
        Mods.Find(m => string.Equals(m.Modifier, modifier, StringComparison.OrdinalIgnoreCase)
                       && !string.IsNullOrEmpty(m.Type) && m.Type != "none");

    /// <summary>The base action of a stored pair: the wrapped one for a trigger, the pair itself
    /// otherwise. Use it wherever only "what does this key do" matters (labels, icons, export).</summary>
    public static (string? Type, string? Value) Unwrap(string? type, string? value)
    {
        if (type != Tag) return (type, value);
        var p = Parse(value);
        return p is null ? ("none", "") : (p.Type, p.Value);
    }

    /// <summary>The pair to store: the bare base action when <paramref name="extras"/> adds
    /// nothing (or can't wrap it), the trigger otherwise.</summary>
    public static (string Type, string Value) Wrap(string baseType, string baseValue, TriggerPayload? extras)
    {
        if (extras is null || !CanWrap(baseType)) return (baseType, baseValue);
        extras.Type = baseType;
        extras.Value = baseValue;
        return extras.HasExtras ? (Tag, extras.Serialize()) : (baseType, baseValue);
    }

    /// <summary>Base types that may carry trigger extras: anything the engine runs by itself.
    /// Excluded: no action, the DisplayPad's navigation/visual types (they are dispatched by the
    /// DisplayPad before the engine ever sees them) and live tiles.</summary>
    public static bool CanWrap(string? baseType) =>
        !string.IsNullOrEmpty(baseType) && baseType != Tag
        && baseType is not ("none" or "disable" or "dp_wheel" or "dp_custom")
        && Array.IndexOf(ActionTypeHelper.PageOnlyActionTypes, baseType) < 0;
}

public sealed class TriggerModAction
{
    [JsonPropertyName("m")] public string Modifier { get; set; } = "ctrl";
    [JsonPropertyName("t")] public string Type { get; set; } = "none";
    [JsonPropertyName("v")] public string Value { get; set; } = "";
}
