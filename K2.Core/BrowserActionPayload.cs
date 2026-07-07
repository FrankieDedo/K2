using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>
/// Payload of the "browser" button action when a specific browser was chosen (instead of
/// the system default). Serialized to JSON and stored in the button's <c>ActionValue</c>.
/// A plain legacy string (a raw URL, or empty) is NOT this format — <see cref="ButtonActionEngine"/>
/// falls back to the old "ShellExecute the URL with the OS default browser" behavior when
/// <see cref="Parse"/> returns null.
/// </summary>
public sealed class BrowserActionPayload
{
    /// <summary>"chrome" | "edge" | "firefox" | "opera" | "brave" | "other".</summary>
    public string Browser { get; set; } = "other";

    /// <summary>Executable path, only meaningful when <see cref="Browser"/> is "other".</summary>
    public string CustomPath { get; set; } = "";

    /// <summary>Optional URL to open; empty = just launch the browser.</summary>
    public string Url { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        Browser    = Browser,
        CustomPath = CustomPath,
        Url        = Url,
    });

    /// <summary>Decodes the payload; returns null if the JSON is invalid or not this shape.</summary>
    public static BrowserActionPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto?.Browser is null) return null;
            return new BrowserActionPayload
            {
                Browser    = dto.Browser,
                CustomPath = dto.CustomPath ?? "",
                Url        = dto.Url ?? "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("browser")]    public string? Browser    { get; set; }
        [JsonPropertyName("customPath")] public string? CustomPath { get; set; }
        [JsonPropertyName("url")]        public string? Url        { get; set; }
    }
}
