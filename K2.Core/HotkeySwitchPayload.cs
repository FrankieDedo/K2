using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>
/// Payload of the "hotkeyswitch" button action: two human-syntax shortcuts (same format
/// <see cref="SendKeysTranslator"/> already consumes for "keys") that alternate on
/// successive presses — mirrors real Base Camp's "Hotkey Switch" function type, which
/// stores a second shortcut (<c>FunctionEnteredValue</c>) and a persisted toggle bit
/// (<c>OnPressRelease</c>) per binding row. K2 keeps the two shortcuts here but tracks
/// which one is next in memory only (<see cref="ButtonActionEngine"/>'s toggle dictionary)
/// rather than writing the toggle bit back to storage on every press.
/// </summary>
public sealed class HotkeySwitchPayload
{
    public string ShortcutA { get; set; } = "";
    public string ShortcutB { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        ShortcutA = ShortcutA,
        ShortcutB = ShortcutB,
    });

    /// <summary>Decodes the payload; returns null if the JSON is invalid or not this shape.</summary>
    public static HotkeySwitchPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null) return null;
            return new HotkeySwitchPayload
            {
                ShortcutA = dto.ShortcutA ?? "",
                ShortcutB = dto.ShortcutB ?? "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("shortcutA")] public string? ShortcutA { get; set; }
        [JsonPropertyName("shortcutB")] public string? ShortcutB { get; set; }
    }
}
