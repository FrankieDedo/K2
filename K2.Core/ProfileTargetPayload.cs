using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>One row of a "switch profile" action: which device and which profile/direction.</summary>
public sealed class ProfileTarget
{
    /// <summary>"" = the device this button lives on; otherwise "{kind}:{id}" (see <see cref="IActionHost.ListProfileTargets"/>).</summary>
    public string Key { get; set; } = "";

    /// <summary>"Next" | "Previous" | "1".."N".</summary>
    public string Target { get; set; } = "Next";
}

/// <summary>
/// Payload of the "profile" button action when it targets one or more devices.
/// Serialized to JSON and stored in the button's <c>ActionValue</c>. A plain legacy
/// string (e.g. "Next", "1") is NOT this format — <see cref="ButtonActionEngine"/>
/// falls back to the old single-target/self-device behavior when <see cref="Parse"/>
/// returns null.
/// </summary>
public sealed class ProfileTargetPayload
{
    public List<ProfileTarget> Targets { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        Targets = Targets.ConvertAll(t => new TargetDto { Key = t.Key, Target = t.Target }),
    });

    /// <summary>Decodes the payload; returns null if the JSON is invalid or not this shape.</summary>
    public static ProfileTargetPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto?.Targets is null) return null;
            var result = new ProfileTargetPayload();
            foreach (var t in dto.Targets)
                result.Targets.Add(new ProfileTarget { Key = t.Key ?? "", Target = t.Target ?? "Next" });
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("targets")] public List<TargetDto>? Targets { get; set; }
    }

    private sealed class TargetDto
    {
        [JsonPropertyName("key")]    public string? Key    { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
    }
}
