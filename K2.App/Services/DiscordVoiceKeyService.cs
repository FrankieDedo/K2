using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using K2.Core;
using K2.Core.Services;

namespace K2.App.Services;

/// <summary>
/// Live mute/deafen icons for DisplayPad keys bound to Discord's two STATE-dependent voice
/// commands ("Toggle Mute" / "Toggle Deafen"): the key shows a microphone or a crossed-out
/// microphone (speaker / crossed-out speaker for deafen) according to what the Discord client
/// actually reports, so it stays right even when the state is changed from Discord itself.
///
/// Same shape as <see cref="SpotifyCoverService"/> — a transient overlay pushed straight to
/// the hardware, never persisted in <c>DisplayPadStore</c>. Two rules decide which keys it may
/// paint on, both there to never overwrite a deliberate choice of the user's:
/// <list type="number">
/// <item>only the two commands whose MEANING depends on the state ("Toggle Mute"/"Toggle
/// Deafen") — for the fixed ones ("Mute", "Unmute", …) the icon says what the key DOES, and
/// that never changes;</item>
/// <item>only keys whose picture is auto-generated (under <c>auto_icons/</c>) or missing. A
/// picture the user picked themselves ALWAYS wins (user report 2026-08-23: their own icon was
/// replaced by the live tile at every startup). Wanting the live state back is then a matter of
/// pressing "Default icon" on that key, or leaving it without a picture.</item>
/// </list>
///
/// Both tiles are rendered by <see cref="IconImageGenerator.TryGenerateGlyphIcon"/>, i.e. the
/// same generator (and accent color) as every other auto-icon, so they sit next to the
/// profile's own tiles without looking foreign.
/// </summary>
internal static class DiscordVoiceKeyService
{
    /// <summary>One key this service owns: its button index, which state it mirrors, and the
    /// key's own icon style (so the live tile is drawn with the colors/font/"with text" choice
    /// the user made for that key — see <see cref="KeyIconSpec"/>/<see cref="IconStyleScope"/>).</summary>
    private readonly record struct VoiceKey(int Button, bool IsDeafen, KeyIconSpec? Spec);

    private readonly record struct DeviceCtx(IDisplayPadClient Client, Action<string> Log, int Rotation, VoiceKey[] Keys);

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "K2.DiscordVoice");

    private static readonly object _gate = new();
    private static readonly Dictionary<int, DeviceCtx> _devices = new();
    private static bool _subscribed;

    /// <summary>Starts (or refreshes, or stops) the overlay for one device from the page rows
    /// that are being painted — called from every repaint path, so the set of live keys always
    /// matches the page actually on the panel. Registration only: the tiles are painted by
    /// <see cref="Repaint"/> at the END of that repaint's upload batch, otherwise the profile's
    /// own icon for the same key would land on top of them.</summary>
    public static void Sync(IDisplayPadClient client, Action<string> log, int deviceId, int rotation,
                            IEnumerable<DpButtonRecord> rows)
    {
        var keys = rows
            .Where(r => string.Equals(r.ActionType, "discord", StringComparison.OrdinalIgnoreCase))
            .Where(IsDefaultIcon)
            .Select(r => (r.ButtonIndex, Command: CommandOf(r.ActionValue), Spec: KeyIconSpec.FromJson(r.IconSpec)))
            .Where(t => t.Command is "mute_toggle" or "deafen_toggle")
            .Select(t => new VoiceKey(t.ButtonIndex, t.Command == "deafen_toggle", t.Spec))
            .ToArray();

        if (keys.Length == 0 || !DiscordStore.IsConnected) { Stop(deviceId); return; }

        // Goes through DiscordBridge.Log (the app log) rather than the DisplayPad log action:
        // the latter is silenced when the log level is Off, and this is the line that tells
        // whether the overlay took a key over at all.
        DiscordBridge.Log?.Invoke($"[Discord] live keys on device {deviceId}: "
            + string.Join(", ", keys.Select(k => $"btn{k.Button}={(k.IsDeafen ? "deafen" : "mute")}")));

        DeviceCtx ctx;
        bool firstSubscriber;
        lock (_gate)
        {
            ctx = new DeviceCtx(client, log, rotation, keys);
            _devices[deviceId] = ctx;
            firstSubscriber = !_subscribed;
            _subscribed = true;
        }
        if (firstSubscriber) DiscordBridge.VoiceStateChanged += OnVoiceStateChanged;

        // Opens the RPC connection in the background when it isn't up yet; the resulting
        // state change comes back through VoiceStateChanged and repaints these keys.
        DiscordBridge.StartLiveVoiceState();
    }

    /// <summary>Paints the live tiles for a device that <see cref="Sync"/> has registered —
    /// called at the tail of the repaint batch that page belongs to. No-op for devices with no
    /// live keys.</summary>
    public static void Repaint(int deviceId)
    {
        DeviceCtx ctx;
        lock (_gate) { if (!_devices.TryGetValue(deviceId, out ctx)) return; }
        Push(deviceId, ctx);
    }

    /// <summary>True when the overlay currently owns that key — the repaint paths use it to
    /// SKIP uploading the key's persisted picture, which would otherwise land on top of (or
    /// alternate with) the live tile on every repaint. User report 2026-08-23: the caption kept
    /// flipping back to the stored action tile.</summary>
    public static bool Owns(int deviceId, int buttonIndex)
    {
        lock (_gate)
            return _devices.TryGetValue(deviceId, out var ctx)
                && Array.Exists(ctx.Keys, k => k.Button == buttonIndex);
    }

    /// <summary>The live tile the overlay would paint on that key RIGHT NOW (state included),
    /// or null when it doesn't own the key. Used by the press-bounce, which otherwise re-uploads
    /// the key's stored picture on every press/release and wipes the live tile (user report
    /// 2026-08-24: "the glyph doesn't change when I click it").</summary>
    public static string? CurrentIconPath(int deviceId, int buttonIndex)
    {
        VoiceKey key;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var ctx)) return null;
            int i = Array.FindIndex(ctx.Keys, k => k.Button == buttonIndex);
            if (i < 0) return null;
            key = ctx.Keys[i];
        }
        return EnsureIcon(key, StateOf(key));
    }

    public static void Stop(int deviceId)
    {
        lock (_gate) _devices.Remove(deviceId);
    }

    private static void OnVoiceStateChanged()
    {
        List<(int Id, DeviceCtx Ctx)> targets;
        lock (_gate) targets = _devices.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (id, ctx) in targets) Push(id, ctx);
    }

    private static void Push(int deviceId, DeviceCtx ctx)
    {
        foreach (var key in ctx.Keys)
        {
            bool state = StateOf(key);
            string? path = EnsureIcon(key, state);
            if (path is null) continue;
            bool ok = ctx.Client.UploadImage(deviceId, path, key.Button, ctx.Rotation);
            DiscordBridge.Log?.Invoke($"[Discord] tile dev={deviceId} btn={key.Button} "
                + $"{(key.IsDeafen ? "deafen" : "mute")}={state} uploaded={ok}");
        }
    }

    /// <summary>Whether the state this key mirrors is currently ON (muted / deafened).
    /// Unknown (not connected yet) reads as off.
    ///
    /// Deafening implies muting: while deafened you are not transmitting either, so the mic key
    /// shows "Mic off" too (user request 2026-08-24 — "sono collegati di fatto"). This is a
    /// DISPLAY rule only: nothing is sent to Discord, and undeafening brings the mic tile back
    /// to whatever the real mute flag says.</summary>
    private static bool StateOf(VoiceKey key)
    {
        bool deaf = DiscordBridge.Deaf ?? false;
        return key.IsDeafen ? deaf : deaf || (DiscordBridge.Mute ?? false);
    }

    /// <summary>Renders (and caches) the tile for one key + state. The caption states the mode
    /// in words ("Mic on"/"Mic off", "Audio on"/"Audio off" — the negative states, i.e. muted
    /// and deafened, are the OFF ones, so both keys read the same way round
    /// one) and the glyph repeats it visually; all four measure 73-81 px against
    /// <c>DrawCaption</c>'s ~90 px box, so none of them ellipsizes (the stock action caption,
    /// "Toggle Mute", measures 108 px — that's where the "Toggle..." the user kept seeing came
    /// from). Drawn inside the key's own <see cref="IconStyleScope"/>, so the colors/font it was
    /// configured with carry over; the style fingerprint is part of the cache file name so two
    /// keys styled differently can't collide on the same PNG.</summary>
    private static string? EnsureIcon(VoiceKey key, bool active)
    {
        string name = (key.IsDeafen, active) switch
        {
            (true, true) => "audio_off",
            (true, false) => "audio_on",
            (false, true) => "mic_off",    // muted = microphone off
            (false, false) => "mic_on",
        };
        string glyph = name switch
        {
            "audio_off" => "", // speaker, crossed out
            "audio_on" => "",  // speaker
            "mic_off" => "",   // microphone, crossed out
            _ => "",           // microphone
        };
        // "Without text" is a per-key choice like any other — honour it here too.
        string caption = key.Spec is { ShowText: false } ? "" : Loc.Get("discord_key_" + name);

        string style = key.Spec?.StyleFingerprint ?? "";
        string stamp = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(style))).ToLowerInvariant()[..8];
        string path = Path.Combine(CacheDir, $"{name}_{stamp}.png");

        using (IconStyleScope.Push(key.Spec))
            return IconImageGenerator.TryGenerateGlyphIcon(glyph, caption, DpHidNative.IconSize, path)
                ? path : null;
    }

    /// <summary>Whether the key's picture is one K2 generates from the action ("Default icon")
    /// rather than a picture the user chose — only the former may be painted over.
    ///
    /// The authoritative answer is the key's <see cref="KeyIconSpec"/> (<c>DefaultIcon</c>),
    /// stored next to the action since 2026-08-24. Testing the image PATH instead was wrong the
    /// moment that pipeline started writing generated icons under <c>cropped\</c> rather than
    /// <c>auto_icons\</c>: every Discord key then read as "user icon" and the overlay skipped
    /// them all (user report: caption stuck on "Toggle Mute", glyph never changing). The path
    /// test survives only as the fallback for rows written before the spec column existed.</summary>
    private static bool IsDefaultIcon(DpButtonRecord row)
    {
        if (KeyIconSpec.FromJson(row.IconSpec) is { } spec) return spec.DefaultIcon;

        if (string.IsNullOrEmpty(row.ImagePath) || !File.Exists(row.ImagePath)) return true;
        return Path.GetFullPath(row.ImagePath).StartsWith(
            Path.GetFullPath(MainWindow.DpAutoIconDir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The command half of a "command~arg" Discord action value.</summary>
    private static string CommandOf(string? actionValue)
    {
        string value = actionValue ?? "";
        int tilde = value.IndexOf('~');
        return tilde < 0 ? value : value[..tilde];
    }
}
