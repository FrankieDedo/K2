using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>
/// Payload of the "audiodevice" button action: the Windows playback device to switch the
/// system default to. Both the device's current persistent id AND its friendly name are
/// kept — the id alone is not enough to survive a real-world "unplug the headset, plug it
/// back in" cycle, since Windows can hand the same physical device a different id if it
/// re-enumerates (e.g. a different USB port/hub). <see cref="Services.AudioDeviceService.
/// TryResolveDeviceId"/> matches by id first, falling back to a name match among the
/// devices actually present when the id no longer resolves.
/// </summary>
public sealed class AudioDevicePayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(new Dto { Id = Id, Name = Name });

    /// <summary>Decodes the payload; returns null if the JSON is invalid or not this shape.</summary>
    public static AudioDevicePayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null) return null;
            return new AudioDevicePayload { Id = dto.Id ?? "", Name = dto.Name ?? "" };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
