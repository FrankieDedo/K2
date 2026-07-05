using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>
/// Payload of the "pyscript" button action. Serialized to JSON and stored
/// in the button's <c>ActionValue</c> field (same as the "multi" action).
/// Encoding/decoding shared between the configuration dialog and the executor.
/// </summary>
public sealed class PyScriptPayload
{
    /// <summary>True = inline code; False = reference to a .py file.</summary>
    public bool Inline { get; set; }

    /// <summary>Path to the .py file (file mode).</summary>
    public string Path { get; set; } = "";

    /// <summary>Python code (inline mode).</summary>
    public string Code { get; set; } = "";

    /// <summary>Optional arguments passed to the script (sys.argv[1:]).</summary>
    public string Args { get; set; } = "";

    /// <summary>Timeout in seconds; 0 = no limit.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        Mode    = Inline ? "inline" : "file",
        Path    = Path,
        Code    = Code,
        Args    = Args,
        Timeout = TimeoutSeconds,
    });

    /// <summary>Decodes the payload; returns null if the JSON is invalid.</summary>
    public static PyScriptPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null) return null;
            return new PyScriptPayload
            {
                Inline         = string.Equals(dto.Mode, "inline", StringComparison.OrdinalIgnoreCase),
                Path           = dto.Path ?? "",
                Code           = dto.Code ?? "",
                Args           = dto.Args ?? "",
                TimeoutSeconds = dto.Timeout < 0 ? 0 : dto.Timeout,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("mode")]    public string? Mode    { get; set; }
        [JsonPropertyName("path")]    public string? Path    { get; set; }
        [JsonPropertyName("code")]    public string? Code    { get; set; }
        [JsonPropertyName("args")]    public string? Args    { get; set; }
        [JsonPropertyName("timeout")] public int     Timeout { get; set; } = 60;
    }
}
