using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.Core;

/// <summary>
/// The picker's "Lighting" category: actions that drive a device's backlight from a key.
/// Unlike the Makalu's own <see cref="MakaluActionTypes.Lighting"/> (a function stored in the
/// mouse), these are executed by K2 and can target any device the host can reach — see
/// <see cref="ILightingController"/>.
/// </summary>
public static class LightingActionTypes
{
    /// <summary>Raise / lower / cycle the brightness of one or more devices, one level per press.</summary>
    public const string Brightness = "light_brightness";

    /// <summary>Pick one of a device's lighting effects, or step through its list.</summary>
    public const string Effect = "light_effect";

    public static readonly string[] All = { Brightness, Effect };
}

/// <summary>One lighting effect of a device: <see cref="Id"/> is the stable token persisted in
/// the action payload, <see cref="Name"/> the label the device's own Lighting section shows.</summary>
public sealed record LightingEffectChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One device a lighting action can target. <see cref="Key"/> uses the same
/// <c>"{kind}:{id}"</c> format as <see cref="ProfileTargetOption.Key"/>, so
/// <see cref="IActionHost.SelfTargetKey"/> identifies the host's own device here too.
/// <see cref="Effects"/> is empty for a device with brightness but no effects (DisplayPad).
/// </summary>
public sealed record LightingTargetOption(
    string Key, string Label, bool Connected, IReadOnlyList<LightingEffectChoice> Effects)
{
    public override string ToString() => Label;
}

/// <summary>
/// What a host exposes for the lighting actions (see <see cref="IActionHost.Lighting"/>).
/// Target keys are always real ones — the engine replaces an empty key with
/// <see cref="IActionHost.SelfTargetKey"/> before calling in.
/// </summary>
public interface ILightingController
{
    /// <summary>Every device this host knows, connected or not, in the order of the device
    /// tabs (the dialog offers the connected ones, plus whatever a saved action already
    /// points at).</summary>
    IReadOnlyList<LightingTargetOption> ListLightingTargets();

    /// <summary>Moves the brightness of <paramref name="targetKey"/> one level —
    /// <see cref="BrightnessActionPayload.Next"/> says where to.</summary>
    void StepBrightness(string targetKey, string mode);

    /// <summary>Changes the lighting effect of <paramref name="targetKey"/>.
    /// <paramref name="target"/> = "Next" | "Previous" | a <see cref="LightingEffectChoice.Id"/>.</summary>
    void SwitchEffect(string targetKey, string target);
}

/// <summary>Payload of the <see cref="LightingActionTypes.Brightness"/> action, stored as JSON
/// in the button's <c>ActionValue</c>.</summary>
public sealed class BrightnessActionPayload
{
    public const string Cycle = "cycle";
    public const string Up = "up";
    public const string Down = "down";

    /// <summary>One press = one level, on every device. 25% because that is all the
    /// keyboards' and the MacroPad's firmware can show (0/25/50/75/100): a finer step made
    /// some presses do nothing visible there, so it is the same grid everywhere.</summary>
    public const int Step = 25;

    [JsonPropertyName("mode")] public string Mode { get; set; } = Cycle;

    /// <summary>Target keys. The dialog always saves real keys; "" (the default of a
    /// never-configured action) = the device this button lives on.</summary>
    [JsonPropertyName("targets")] public List<string> Targets { get; set; } = new() { "" };

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Decodes the payload; an empty/invalid value yields the defaults (cycle, this
    /// device) so a never-configured key still does something sensible.</summary>
    public static BrightnessActionPayload Parse(string? json)
    {
        BrightnessActionPayload? p = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { p = JsonSerializer.Deserialize<BrightnessActionPayload>(json); }
            catch (JsonException) { /* fall through to defaults */ }
        }
        p ??= new BrightnessActionPayload();
        p.Mode = p.Mode is Up or Down ? p.Mode : Cycle;
        p.Targets = (p.Targets ?? new List<string>()).Select(t => t ?? "").Distinct().ToList();
        if (p.Targets.Count == 0) p.Targets.Add("");
        return p;
    }

    /// <summary>The brightness one press leads to: the next level of the <see cref="Step"/>
    /// grid (a value off the grid, e.g. set by hand on a DisplayPad, lands on it). Up/Down stop
    /// at the ends; Cycle climbs to 100 and the press after that restarts from 0.</summary>
    public static int Next(int current, string mode)
    {
        current = Math.Clamp(current, 0, 100);
        int up = Math.Min(100, (current / Step + 1) * Step);
        int down = Math.Max(0, (current + Step - 1) / Step * Step - Step);
        return mode switch
        {
            Up   => up,
            Down => down,
            _    => current >= 100 ? 0 : up,
        };
    }
}

/// <summary>Payload of the <see cref="LightingActionTypes.Effect"/> action, stored as JSON in
/// the button's <c>ActionValue</c>.</summary>
public sealed class LightEffectPayload
{
    public const string NextEffect = "Next";
    public const string PreviousEffect = "Previous";

    /// <summary>Target key. The dialog always saves a real key; "" = the device this button
    /// lives on.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>"Next" | "Previous" | a <see cref="LightingEffectChoice.Id"/>.</summary>
    [JsonPropertyName("target")] public string Target { get; set; } = NextEffect;

    /// <summary>Effect and device labels captured at save time, for the key list.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(this);

    public static LightEffectPayload Parse(string? json)
    {
        LightEffectPayload? p = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { p = JsonSerializer.Deserialize<LightEffectPayload>(json); }
            catch (JsonException) { /* fall through to defaults */ }
        }
        p ??= new LightEffectPayload();
        p.Key ??= "";
        p.Name ??= "";
        p.Device ??= "";
        if (string.IsNullOrWhiteSpace(p.Target)) p.Target = NextEffect;
        return p;
    }

    /// <summary>Index in <paramref name="effectIds"/> that <paramref name="target"/> leads to
    /// from <paramref name="current"/>: Next/Previous wrap around, an id selects itself.
    /// -1 when the id is not one of this device's effects.</summary>
    public static int ResolveIndex(IReadOnlyList<string> effectIds, int current, string target)
    {
        int n = effectIds.Count;
        if (n == 0) return -1;
        if (target == NextEffect) return current < 0 ? 0 : (current + 1) % n;
        if (target == PreviousEffect) return current <= 0 ? n - 1 : current - 1;
        for (int i = 0; i < n; i++)
            if (string.Equals(effectIds[i], target, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
