using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using K2.Core;

namespace K2.App.Services;

// Mod-link tiles (dp_modlink) — Minecraft and Space Engineers, read through the mods K2 ships
// (ModLinkClient) and pressed through the player's own key bindings (ModLinkBinds).
internal static partial class DpLiveTileService
{
    private readonly record struct ModReading(string Text, string Unit, double? Fraction,
                                              LiveTileRenderer.EdState Lamp, string? IconFile = null,
                                              string? Badge = null, double? Wear = null);

    private static readonly ModReading ModUnknown = new("—", "", null, LiveTileRenderer.EdState.Unknown);

    internal static bool RenderModLinkTile(string? value, string caption, int size, string outputPngPath)
    {
        var hit = ModLinkGames.Find(value);
        if (hit is { Game.Midi: true } studio) return RenderStudioTile(studio.Game, studio.Item, size, outputPngPath);
        string profileId = hit?.Game.ProfileId ?? "";
        var accent = GameProfileTheme.AccentFor(profileId);
        LiveTileRenderer.EdAccentOverride = accent;

        var r = hit is { } h ? ModRead(h.Game, h.Item) : ModUnknown;
        ApplyModStyle(profileId, r.Lamp);

        var item = hit?.Item;
        if (item?.Format == ModLinkFormat.Bars && hit is { } bh)
        {
            var (big, rows) = ModBars(bh.Game, item);
            ApplyModStyle(profileId, rows.Count > 0 ? LiveTileRenderer.EdState.On : LiveTileRenderer.EdState.Unknown);
            return LiveTileRenderer.TryRenderBarsTile(big, Color.FromArgb(unchecked((int)0xFF80FF20)), rows,
                                                      size, outputPngPath, KspEdgeInset,
                                                      item.SizeRef ?? "-88888");
        }

        if (item?.Format == ModLinkFormat.Icon)
        {
            // The picture IS the key; the caption only stands in for a slot with nothing in it.
            bool lit = r.Lamp is LiveTileRenderer.EdState.On or LiveTileRenderer.EdState.Alarm;
            return LiveTileRenderer.TryRenderAbilityTile(r.IconFile, r.IconFile is null ? caption : "", lit,
                                                         size, outputPngPath, accent, pixelArt: true,
                                                         badge: r.IconFile is null ? null : r.Badge,
                                                         wear: r.IconFile is null ? null : r.Wear);
        }

        if (item is null || item.Format == ModLinkFormat.Lamp)
            return LiveTileRenderer.TryRenderEdStatus(r.Lamp, caption, size, outputPngPath);

        if (item.Format is ModLinkFormat.Gauge or ModLinkFormat.Percent)
            return LiveTileRenderer.TryRenderSegmentedGauge(r.Text, r.Fraction, caption, size, outputPngPath,
                                                            accent, KspDialSegments,
                                                            edgeInset: KspEdgeInset, dialScale: KspDialScale);

        return LiveTileRenderer.TryRenderSpeedTile(r.Text, null, caption, r.Unit, size, outputPngPath,
                                                   ownValueSize: item.Format == ModLinkFormat.Text,
                                                   edgeInset: KspEdgeInset,
                                                   valueSizeReference: item.SizeRef ?? DefaultSizeRef(item.Format));
    }

    /// <summary>The big number and bars of a <see cref="ModLinkFormat.Bars"/> key; no rows while the
    /// mod is not answering, which the painter draws as an unlit, empty key.</summary>
    private static (string? Big, System.Collections.Generic.List<LiveTileRenderer.BarRow> Rows) ModBars(
        ModLinkGame game, ModLinkItem item)
    {
        var rows = new System.Collections.Generic.List<LiveTileRenderer.BarRow>();
        var s = ModLinkClient.Want(game);
        if (!s.Alive || !s.InGame) return (null, rows);

        string? big = item.BigRead is { } br && s.Get(br) is { } bv && ModNumber(bv) is { } bn
            ? Math.Round(bn).ToString("0", CultureInfo.CurrentCulture) : null;
        foreach (var bar in item.Bars ?? Array.Empty<ModLinkBar>())
        {
            // An empty label key is a bare bar (no text): the renderer draws it as a thin strip.
            bool bare = bar.LabelLocKey.Length == 0;
            string label = bare ? "" : Loc.Get(bar.LabelLocKey);
            double? value = s.Get(bar.Read) is { } v ? ModNumber(v) : null;
            double? max = bar.Max;
            if (bar.ReadMax is { } rm && s.Get(rm) is { } mv && ModNumber(mv) is { } m) max = m;

            double? fraction = value is null ? null
                             : max is > 0 ? Math.Clamp(value.Value / max.Value, 0, 1)
                             : Math.Clamp(value.Value, 0, 1);
            string text = value is null ? "—"
                        : !bar.ShowBar ? Math.Round(value.Value).ToString("0", CultureInfo.CurrentCulture)
                        : max is > 0 ? Num(value.Value, value.Value % 1 == 0 || value.Value >= 10 ? 0 : 1)
                        : Pct(value.Value);
            rows.Add(new LiveTileRenderer.BarRow(label, bare ? "" : text, fraction, Color.FromArgb(bar.Argb), bar.ShowBar));
        }
        return (big, rows);
    }

    /// <summary>The widest value a key of this format normally shows — what its font is sized
    /// from, so the number keeps one size as it changes (a clock must not grow at 11:11).</summary>
    private static string? DefaultSizeRef(ModLinkFormat format) => format switch
    {
        ModLinkFormat.Integer => "-8888",
        ModLinkFormat.Number => "888.8",
        ModLinkFormat.Speed => "88.8",
        ModLinkFormat.Distance => "8888",
        ModLinkFormat.Text => "Overworld",
        _ => null,
    };

    private static string ModLinkStamp(string? value)
    {
        var hit = ModLinkGames.Find(value);
        if (hit is { Game.Midi: true } studio)
        {
            var st = ModLinkClient.Want(studio.Game);
            return $"{value}:{st.Alive}:{st.InGame}:{ModRead(studio.Game, studio.Item).Lamp}";
        }
        if (hit is { Item.Format: ModLinkFormat.Bars } bars)
        {
            var (big, rows) = ModBars(bars.Game, bars.Item);
            return value + ":" + big + ":" + string.Join(";", rows.Select(r => r.Label + "=" + r.Value));
        }
        var r = hit is { } h ? ModRead(h.Game, h.Item) : ModUnknown;
        return $"{value}:{r.Text}:{r.Unit}:{r.Fraction?.ToString("0.00", CultureInfo.InvariantCulture)}:{r.Lamp}:{r.IconFile}:{r.Badge}:{r.Wear?.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    // Discord's own red and green (DiscordTileRenderer's MutedRed / SpeakingGreen): the same
    // "live / hot" colours the pad already uses for a call, so Record and Play read the same way.
    private static readonly Color StudioRed    = Color.FromArgb(237, 66, 69);
    private static readonly Color StudioGreen  = Color.FromArgb(59, 165, 93);
    private static readonly Color StudioYellow = Color.FromArgb(250, 200, 40);
    private static readonly Color StudioBlue   = Color.FromArgb(88, 101, 242);
    private static readonly Color StudioWhite  = Color.FromArgb(235, 235, 240);

    /// <summary>Raised with the pad's id when its back key is pressed. Set by MainWindow, which
    /// owns profile switching; called on the key thread.</summary>
    internal static Action<int>? StudioBackRequested;

    /// <summary>A Fender Studio Pro key: the function's pictogram in its colour on black, and the
    /// two swapped while the program's LED for it is on (see
    /// <see cref="LiveTileRenderer.TryRenderTransportTile"/>). No caption and none of the icon
    /// style: the picture is the label.</summary>
    private static bool RenderStudioTile(ModLinkGame game, ModLinkItem item, int size, string outputPngPath)
    {
        var s = ModLinkClient.Want(game);
        // The back key is K2's own: it works with no program behind it, so it never dims.
        bool known = (s.Alive && s.InGame) || item.Mcu == ModLinkGames.McuBack;
        // A command with no LED behind it (Save, Undo) never lights: it has no state to show.
        bool lit = item.Read is not null &&
                   ModRead(game, item).Lamp is LiveTileRenderer.EdState.On or LiveTileRenderer.EdState.Alarm;
        var (icon, label, color) = StudioLook(item.Value);
        if (IconStyleScope.Current is { } spec) spec.BgImagePath = null;
        return LiveTileRenderer.TryRenderTransportTile(icon, label, color, lit, known, size, outputPngPath);
    }

    private static (string Icon, string Label, Color Color) StudioLook(string value)
    {
        string id = value.StartsWith("sp.", StringComparison.Ordinal) ? value[3..] : value;
        string n = id.Length > 0 && char.IsDigit(id[^1]) ? id[^1..] : "";
        return id switch
        {
            "back"   => ("back", "", StudioWhite),
            "record" => ("record", "", StudioRed),
            "play"   => ("play", "", StudioGreen),
            "loop"   => ("loop", "", StudioBlue),
            "click"  => ("click", "", StudioBlue),
            "saveas" => ("", "SAVE AS", StudioWhite),
            "timemode"   => ("", "TIME", StudioWhite),
            "anysolo"    => ("", "SOLO", StudioYellow),
            "bankprev"   => ("", "BANK ◀", StudioWhite),
            "banknext"   => ("", "BANK ▶", StudioWhite),
            "trackprev"  => ("", "TRK ◀", StudioWhite),
            "tracknext"  => ("", "TRK ▶", StudioWhite),
            "flip"       => ("", "FLIP", StudioBlue),
            "globalview" => ("", "ALL", StudioBlue),
            _ when id.StartsWith("arm", StringComparison.Ordinal)    => ("", "R" + n, StudioRed),
            _ when id.StartsWith("solo", StringComparison.Ordinal)   => ("", "S" + n, StudioYellow),
            _ when id.StartsWith("mute", StringComparison.Ordinal)   => ("", "M" + n, StudioBlue),
            _ when id.StartsWith("select", StringComparison.Ordinal) => ("", n, StudioWhite),
            // stop, rewind, forward, marker, save, undo, redo, zoom, up/down/left/right: the id IS
            // the pictogram's name.
            _ => (id, id.ToUpperInvariant(), StudioWhite),
        };
    }

    private static string ModLinkCaption(string? value) =>
        ModLinkGames.Find(value) is { } hit
            ? Loc.Get(hit.Item.LocKey).ToUpper(CultureInfo.CurrentCulture)
            : "";

    /// <summary>A key press: the shortcut pinned on the key, else the player's real bind, else the
    /// game's shipped default. A reading with no control behind it presses nothing.</summary>
    private static void ModLinkPress(string value, Action<string> log, int deviceId)
    {
        if (ModLinkGames.Find(value) is not { } hit) return;

        // A MIDI game (Fender Studio Pro) presses a Mackie Control button, not a keystroke.
        if (hit.Game.Midi)
        {
            if (hit.Item.Mcu == ModLinkGames.McuBack)
            {
                log($"[MODLINK] btn press -> {hit.Item.Value}: back to the previous profile");
                StudioBackRequested?.Invoke(deviceId);
                return;
            }
            if (hit.Item.Mcu is { } note)
                log(McuClient.Press(note)
                    ? $"[MODLINK] btn press -> {hit.Item.Value} sends MCU note 0x{note & 0xFF:X2}{((note & ModLinkGames.McuShift) != 0 ? " +Shift" : "")}"
                    : $"[MODLINK] btn press -> {hit.Item.Value} not sent: {McuClient.LastError}");
            ModLinkClient.PollSoon(hit.Game);
            return;
        }

        var (_, pinned) = ModLinkGames.Split(value);
        string? keys = pinned ?? ModLinkBinds.For(hit.Game, hit.Item.Bind) ?? hit.Item.Keys;
        if (string.IsNullOrEmpty(keys)) return;

        if (HotkeySender.TrySend(keys, out _))
            log($"[MODLINK] btn press -> {hit.Item.Value} sends \"{keys}\" (SendInput)");
        else
        {
            System.Windows.Forms.SendKeys.SendWait(SendKeysTranslator.Translate(keys));
            log($"[MODLINK] btn press -> {hit.Item.Value} sends \"{keys}\" (sendkeys)");
        }
        ModLinkClient.PollSoon(hit.Game);
    }

    private static void ApplyModStyle(string profileId, LiveTileRenderer.EdState state)
    {
        if (IconStyleScope.Current is not { } spec) return;
        bool lit = state is LiveTileRenderer.EdState.On or LiveTileRenderer.EdState.Alarm;
        if (GameProfileTheme.BgImagePathFor(profileId, lit) is { } bg)
            spec.BgImagePath = bg;
        spec.TextColor = GameProfileTheme.TextHexFor(profileId, lit);
    }

    private static ModReading ModRead(ModLinkGame game, ModLinkItem item)
    {
        var on = LiveTileRenderer.EdState.On;

        // A plain command is a keystroke: always "ready", mod or no mod.
        if (item.Read is null) return new("", "", null, on);

        var s = ModLinkClient.Want(game);
        if (s.Get(item.Read) is not { } v) return ModUnknown;

        switch (item.Format)
        {
            case ModLinkFormat.Icon:
            {
                bool lit = item.LitWhen is { } slot && string.Equals(ModText(v), slot, StringComparison.OrdinalIgnoreCase);
                int rev = item.IconRev is { } rf && s.Get(rf) is { } rv && ModNumber(rv) is { } revNum ? (int)revNum : 0;
                string? file = item.Icon is { } path ? ModLinkClient.IconFile(game, path, rev) : null;

                // Newer mods send the item alone (iconsPlain) and leave count and wear to us; the
                // 1.21 mod baked them into the picture, where drawing them again would double them.
                string? badge = null;
                double? wear = null;
                if (s.Get("iconsPlain") is { ValueKind: JsonValueKind.True } && item.LitWhen is { } slotNo)
                {
                    if (s.Get("slotCount" + slotNo) is { } cv && ModNumber(cv) is > 1 and var count)
                        badge = ((int)count).ToString(CultureInfo.InvariantCulture);
                    if (s.Get("slotWear" + slotNo) is { } wv && ModNumber(wv) is < 1 and var worn)
                        wear = worn;
                }
                return new("", "", null, lit ? on : LiveTileRenderer.EdState.Off, file, badge, wear);
            }
            case ModLinkFormat.Lamp:
            {
                bool lit = item.LitWhen is { } word
                    ? string.Equals(ModText(v), word, StringComparison.OrdinalIgnoreCase)
                    : ModBool(v);
                return new("", "", null,
                           !lit ? LiveTileRenderer.EdState.Off
                           : item.AlarmWhenLit ? LiveTileRenderer.EdState.Alarm : on);
            }
            case ModLinkFormat.Text:
            {
                string t = ModText(v);
                return t.Length == 0 ? ModUnknown with { Lamp = LiveTileRenderer.EdState.Off } : new(t, "", null, on);
            }
        }

        if (ModNumber(v) is not { } n) return ModUnknown;

        switch (item.Format)
        {
            case ModLinkFormat.Gauge:
            {
                double? max = item.Max;
                if (item.ReadMax is { } rm && s.Get(rm) is { } mv && ModNumber(mv) is { } m) max = m;
                if (max is not > 0) return ModUnknown;
                double f = Math.Clamp(n / max.Value, 0, 1);
                return new(Num(n, n % 1 == 0 || n >= 10 ? 0 : 1), "", f, AlarmOr(item, f));
            }
            case ModLinkFormat.Percent:
            {
                double f = Math.Clamp(n, 0, 1);
                return new(Pct(f), "", f, AlarmOr(item, f));
            }
            case ModLinkFormat.Integer:
                return new(Math.Round(n).ToString("0", CultureInfo.CurrentCulture), item.Unit, null, on);
            case ModLinkFormat.Speed:
                return new(Num(n, Math.Abs(n) >= 100 ? 0 : 1), "m/s", null, on);
            case ModLinkFormat.Distance:
            {
                if (n < 0) return ModUnknown with { Lamp = LiveTileRenderer.EdState.Off };
                var (t, u) = KspDistance(n);
                return new(t, u, null, on);
            }
            default:
                return new(Num(n, Math.Abs(n) >= 100 ? 0 : Math.Abs(n) >= 10 ? 1 : 2), item.Unit, null, on);
        }
    }

    private static LiveTileRenderer.EdState AlarmOr(ModLinkItem item, double fraction) =>
        item.AlarmBelow is { } limit && fraction < limit
            ? LiveTileRenderer.EdState.Alarm
            : LiveTileRenderer.EdState.On;

    private static double? ModNumber(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetDouble(out double d) && double.IsFinite(d) => d,
        JsonValueKind.True  => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    private static bool ModBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0,
        JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) && b,
        _ => false,
    };

    private static string ModText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        _ => "",
    };
}
