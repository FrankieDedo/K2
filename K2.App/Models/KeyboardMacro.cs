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
    /// <summary>"keydown", "keyup", "mousedown", "mouseup", "mousemove", "text", "k2action"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "keydown";

    /// <summary>For <c>Type == "k2action"</c>: the K2 button-action type
    /// (<c>keys</c>/<c>exec</c>/<c>media</c>/…). The action's payload rides in
    /// <see cref="Text"/>. Only write-effect actions are offered — never a
    /// DisplayPad-visual-only type.</summary>
    [JsonPropertyName("k2type")]
    public string? K2Type { get; set; }

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

    /// <summary>Capture-time only, never persisted: true if this mouse button
    /// event landed on a window owned by K2 itself. <see cref="Services.MacroRecorder"/>
    /// uses it to drop the click that starts/stops the recording; it is decided
    /// at capture time with a pixel-accurate hit test, because by the time the
    /// recording ends the window under those coordinates may have changed.</summary>
    [JsonIgnore]
    public bool OnOwnWindow { get; set; }

    /// <summary>
    /// Converts a list from the ON-DISK convention (<c>DelayMs</c> = pause
    /// BEFORE this step, which is what both K2's own JSON and BaseCamp's
    /// recorder store) to the IN-MEMORY one (<c>DelayMs</c> = pause AFTER this
    /// step). In memory a keydown row's delay is therefore how long the key
    /// stays held and a keyup row's delay is the gap before the next step —
    /// which is what the INPUTS list shows and what the user edits there.
    /// The head delay (dead time between pressing Record and the first key) is
    /// dropped in the process; the last step gets 0. Mutates in place.
    /// </summary>
    public static void ShiftToDelayAfter(List<MacroInput> inputs)
    {
        for (int i = 0; i < inputs.Count - 1; i++)
            inputs[i].DelayMs = inputs[i + 1].DelayMs;
        if (inputs.Count > 0) inputs[^1].DelayMs = 0;
    }

    /// <summary>Deep copy of <paramref name="inputs"/> converted to the
    /// "delay after" convention — for the live INPUTS preview, which reads the
    /// recorder's own still-growing list (raw "delay before") and must not
    /// touch it.</summary>
    public static List<MacroInput> ToDelayAfterCopy(IReadOnlyList<MacroInput> inputs)
    {
        var list = new List<MacroInput>(inputs.Count);
        foreach (var i in inputs) list.Add(i.Clone());
        ShiftToDelayAfter(list);
        return list;
    }

    /// <summary>Inverse of <see cref="ShiftToDelayAfter"/> — returns a shallow
    /// copy of the list in the on-disk "delay before" convention, leaving the
    /// caller's own (in-memory) list untouched.</summary>
    public static List<MacroInput> ToDelayBefore(List<MacroInput> inputs)
    {
        var outList = new List<MacroInput>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            var copy = inputs[i].Clone();
            copy.DelayMs = i == 0 ? 0 : inputs[i - 1].DelayMs;
            outList.Add(copy);
        }
        return outList;
    }

    /// <summary>Independent copy — so editing a duplicated macro's inputs
    /// (reorder/delete) never touches the source macro's list.</summary>
    public MacroInput Clone() => new()
    {
        Type = Type,
        Key = Key,
        DelayMs = DelayMs,
        X = X,
        Y = Y,
        Text = Text,
        K2Type = K2Type
    };

    /// <summary>
    /// Parses BaseCamp's own macro-recording JSON (produced by its Electron/
    /// iohook-based recorder) — a different shape from <see cref="MacroDefinition.InputsToJson"/>'s
    /// output, extracted from BaseCamp.UI.exe's compiled Razor view (no
    /// source available otherwise). Key events carry the key identity in
    /// "rawcode" (the native Windows VK code, used by BC itself to look up
    /// display names and key bindings) — NOT "keycode", which is iohook's
    /// own cross-platform id and does not correspond to any Win32 VK value.
    /// Mouse events use "button" (1=left, 2=right, 3=middle — already lines
    /// up with K2's own numbering for 1/2). "mousewheel" events have no K2
    /// equivalent (no scroll support in <c>MacroPlayer</c>) and are dropped.
    /// </summary>
    public static List<MacroInput> ListFromBaseCampJson(string? json)
    {
        var result = new List<MacroInput>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("type", out var typeProp)) continue;
                string type = typeProp.GetString() ?? "";
                TryGetIntFlexible(el, "delay", out int delay);

                switch (type)
                {
                    case "keydown":
                    case "keyup":
                        if (!TryGetIntFlexible(el, "rawcode", out int vk)) continue;
                        result.Add(new MacroInput { Type = type, Key = vk, DelayMs = delay });
                        break;

                    case "mousedown":
                    case "mouseup":
                        TryGetIntFlexible(el, "button", out int button);
                        // BC's own click-repeat macros (e.g. rapid-fire click
                        // bindings) often record no x/y at all — meant to
                        // click wherever the cursor already is, not to warp
                        // it to a fixed spot. -1 signals "no position" to
                        // MacroPlayer, which then skips the move-cursor step.
                        int x = TryGetIntFlexible(el, "x", out int xv) ? xv : -1;
                        int y = TryGetIntFlexible(el, "y", out int yv) ? yv : -1;
                        result.Add(new MacroInput { Type = type, Key = button, X = x, Y = y, DelayMs = delay });
                        break;

                    // "mousewheel": no K2 equivalent — dropped rather than
                    // imported as a broken/zeroed action.
                }
            }
        }
        catch (JsonException)
        {
            return new List<MacroInput>();
        }
        ShiftToDelayAfter(result);   // BC stores "delay before"; K2 works in "delay after"
        return result;
    }

    /// <summary>
    /// Reads an int property that BC sometimes serializes as a JSON number
    /// and sometimes as a numeric string (e.g. "delay":"1" when it comes
    /// straight from a text input's .val()). Returns false (value 0) if the
    /// property is missing or unparseable.
    /// </summary>
    private static bool TryGetIntFlexible(JsonElement obj, string propertyName, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(propertyName, out var prop)) return false;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(prop.GetString(), out value),
            _ => false
        };
    }
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
    public bool RecordMouse { get; set; } = true;
    public bool RecordKeyboard { get; set; } = true;

    /// <summary>Only meaningful when <see cref="RecordMouse"/> is true: also
    /// captures mousemove events (off by default — a click-only macro is
    /// usually what's wanted, and movement recordings bloat playback).</summary>
    public bool RecordMouseMovement { get; set; }
    public MacroDelay DelayOption { get; set; } = MacroDelay.Recorded;
    public int CustomDelayMs { get; set; } = 50;
    public MacroPlayback PlaybackOption { get; set; } = MacroPlayback.Once;
    public int RepeatCount { get; set; } = 1;
    public List<MacroInput> Inputs { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    /// <summary>Serializes Inputs to JSON (for saving to the DB). Written in the
    /// on-disk "delay before" convention — see <see cref="MacroInput.ShiftToDelayAfter"/>.</summary>
    public string InputsToJson() =>
        JsonSerializer.Serialize(MacroInput.ToDelayBefore(Inputs), _jsonOpts);

    /// <summary>Independent copy under <paramref name="newName"/> — <c>Id</c> is
    /// left at 0 (the caller inserts it and gets a fresh one back), Inputs is a
    /// deep copy so reordering/deleting rows on the duplicate never touches
    /// the source macro.</summary>
    public MacroDefinition Clone(string newName) => new()
    {
        Name = newName,
        RecordMouse = RecordMouse,
        RecordKeyboard = RecordKeyboard,
        RecordMouseMovement = RecordMouseMovement,
        DelayOption = DelayOption,
        CustomDelayMs = CustomDelayMs,
        PlaybackOption = PlaybackOption,
        RepeatCount = RepeatCount,
        Inputs = Inputs.ConvertAll(i => i.Clone()),
        IsActive = IsActive,
        Order = Order,
        ModifiedAt = DateTime.Now
    };

    /// <summary>Deserializes Inputs from JSON (for loading from the DB) and
    /// converts them to the in-memory "delay after" convention.</summary>
    public static List<MacroInput> InputsFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var list = JsonSerializer.Deserialize<List<MacroInput>>(json, _jsonOpts) ?? new();
            MacroInput.ShiftToDelayAfter(list);
            return list;
        }
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

        // Map BC's option ids to our enums. These are literal ids from BC's
        // compiled Razor view (wwwroot has no .cshtml on disk — extracted by
        // scanning BaseCamp.UI.exe's embedded strings), NOT the semantic
        // keywords ("nodelay"/"custom"/...) previously assumed here — that
        // guess never matched, so every imported macro silently fell back to
        // Recorded/Once regardless of what was actually configured in BC.
        // Delay pills, in order: "delay-one"=Record delay, "delay-two"=Custom
        // (has the ms input), "delay-three"=No delay.
        def.DelayOption = delayOption?.ToLowerInvariant() switch
        {
            "delay-two"   => MacroDelay.Custom,
            "delay-three" => MacroDelay.NoDelay,
            _             => MacroDelay.Recorded   // "delay-one" and default
        };
        // Playback pills: BC's keyboard macros only have these three (no
        // "repeat N times" — that's a K2-only addition, unreachable via
        // import). "play-two"=Hold (repeats while the button is held down,
        // i.e. our WhileHeld); "play-three"=Repeat (BC's tooltip: "will
        // continue to execute your macro from the moment the assigned
        // button is pressed until it is pressed again" — a press-to-start/
        // press-to-stop toggle, matching our Toggle, not RepeatN).
        def.PlaybackOption = playbackOption?.ToLowerInvariant() switch
        {
            "play-two"   => MacroPlayback.WhileHeld,
            "play-three" => MacroPlayback.Toggle,
            _            => MacroPlayback.Once     // "play-one" and default
        };

        def.Inputs = MacroInput.ListFromBaseCampJson(inputsJson);
        return def;
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false
    };
}
