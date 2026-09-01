using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Button action execution engine, SHARED by all K2 device modules
/// (DisplayPad, MacroPad, Everest, ...).
///
/// Handles device-agnostic actions directly (url, exec, folder, browser,
/// command, keys, text, oscmd, media, mouse, multi, createfolder, back,
/// pyscript) and delegates device-specific ones (profile switching, macro
/// playback) to the <see cref="IActionHost"/>. Also encapsulates the Python
/// bridge (<see cref="PyBridge"/>).
/// </summary>
public sealed class ButtonActionEngine : IDisposable
{
    private static readonly char[] SendKeysMeta = { '^', '%', '{', '~' };

    private readonly IActionHost _host;
    private readonly PyBridge _py;

    /// <summary>Which half of a "hotkeyswitch" action fires next, per (button, ActionValue) —
    /// in-memory only (resets on restart), deliberately not persisted to any device's store; see
    /// the "hotkeyswitch" case in <see cref="Dispatch"/>.</summary>
    private readonly Dictionary<(int ButtonIndex, string Value), bool> _hotkeySwitchNextIsB = new();

    /// <summary>Shortcuts currently held down by a momentary ("press and hold") key, keyed by
    /// buttonIndex — populated only when a device passes <c>momentary: true</c> to
    /// <see cref="Execute"/> and cleared by the matching <see cref="Release"/> / by
    /// <see cref="ReleaseAllHeld"/>. Lets holding the physical key hold the mapped combination
    /// (e.g. Alt+Tab stays open) instead of firing a one-shot tap.</summary>
    private readonly Dictionary<int, (string Value, DateTime Since)> _heldShortcuts = new();

    /// <summary>Safety net for a physical up edge that never arrives (USB glitch, device drop):
    /// any hold older than this is force-released on the next engine call. Long enough to never
    /// clip a deliberate hold.</summary>
    private static readonly TimeSpan HeldShortcutMaxAge = TimeSpan.FromSeconds(20);

    public ButtonActionEngine(IActionHost host)
    {
        _host = host;
        _py = new PyBridge(host, this);
    }

    /// <summary>True if the Python runtime is installed and available.</summary>
    public bool PythonRuntimeAvailable => _py.RuntimeAvailable;

    /// <summary>Starts the Python bridge (RPC server). Call once at startup.</summary>
    public void Start() => _py.Start();

    public void Dispose()
    {
        ReleaseAllHeld();
        _py.Dispose();
    }

    /// <summary>
    /// Executes the action configured on a button. MUST BE CALLED ON THE UI THREAD.
    /// <paramref name="buttonIndex"/> is the context for Python scripts
    /// (-1 if unknown, e.g. action invoked via RPC).
    /// </summary>
    /// <param name="momentary">True when the caller is a physical key that will send a matching
    /// <see cref="Release"/> on its up edge (MacroPad / Everest Max / Everest 60). A "keys" action
    /// is then pressed-and-held instead of tapped; every other action type still fires once.</param>
    public void Execute(string? actionType, string? actionValue, int buttonIndex = -1, bool momentary = false)
    {
        if (string.IsNullOrEmpty(actionType)) return;
        SweepStaleHeld();
        var value = actionValue ?? "";
        try
        {
            Dispatch(actionType, value, buttonIndex, momentary);
        }
        catch (Exception ex)
        {
            _host.Log($"[ERR ] button #{buttonIndex} action execution: {ex.Message}");
        }
    }

    /// <summary>Up edge for a button that was executed with <c>momentary: true</c>. Lifts a held
    /// "keys" shortcut (key first, then modifiers in reverse); a no-op for anything else.</summary>
    public void Release(int buttonIndex)
    {
        if (buttonIndex < 0) return;
        if (!_heldShortcuts.TryGetValue(buttonIndex, out var held)) return;
        _heldShortcuts.Remove(buttonIndex);
        HotkeySender.TryHoldUp(held.Value, out _);
        _host.Log($"[EXEC] keys hold-up -> \"{held.Value}\"");
    }

    /// <summary>Lifts every still-held momentary shortcut — call on profile switch / device
    /// disconnect so a combination can't stay pressed system-wide.</summary>
    public void ReleaseAllHeld()
    {
        if (_heldShortcuts.Count == 0) return;
        foreach (var kv in _heldShortcuts) HotkeySender.TryHoldUp(kv.Value.Value, out _);
        _host.Log($"[EXEC] released {_heldShortcuts.Count} held shortcut(s)");
        _heldShortcuts.Clear();
    }

    /// <summary>Force-releases holds whose up edge never arrived (see <see cref="HeldShortcutMaxAge"/>).</summary>
    private void SweepStaleHeld()
    {
        if (_heldShortcuts.Count == 0) return;
        var now = DateTime.UtcNow;
        List<int>? stale = null;
        foreach (var kv in _heldShortcuts)
            if (now - kv.Value.Since > HeldShortcutMaxAge) (stale ??= new()).Add(kv.Key);
        if (stale is null) return;
        foreach (var idx in stale)
        {
            HotkeySender.TryHoldUp(_heldShortcuts[idx].Value, out _);
            _host.Log($"[EXEC] stale hold force-released -> \"{_heldShortcuts[idx].Value}\"");
            _heldShortcuts.Remove(idx);
        }
    }

    /// <summary>Runs a "Ctrl+Shift+A" / "Win+D" shortcut once. Prefers the SendInput path
    /// (<see cref="HotkeySender"/>) so the Windows key actually reaches the target and apps
    /// watching the input stream with a low-level hook see it; falls back to SendKeys for a raw
    /// sequence (already contains <c>^ % { ~</c>) or anything HotkeySender can't resolve.</summary>
    private void RunShortcut(string value, Action<string> log)
    {
        if (value.IndexOfAny(SendKeysMeta) < 0 && HotkeySender.TrySend(value, out _))
        {
            log($"[EXEC] keys -> \"{value}\"  (SendInput)");
            return;
        }
        string seq = value.IndexOfAny(SendKeysMeta) >= 0 ? value : SendKeysTranslator.Translate(value);
        System.Windows.Forms.SendKeys.SendWait(seq);
        log($"[EXEC] keys -> \"{value}\"  (sendkeys=\"{seq}\")");
    }

    private void Dispatch(string type, string value, int buttonIndex, bool momentary)
    {
        void Log(string m) => _host.Log(m);
        switch (type)
        {
            case "url":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] url without payload"); break; }
                Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true });
                Log($"[EXEC] url -> {value}");
                break;

            case "exec":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] exec without payload"); break; }
                RunExecAction(value);
                Log($"[EXEC] exec -> {value}");
                break;

            case "folder":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] folder without payload"); break; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{value}\"",
                    UseShellExecute = true
                });
                Log($"[EXEC] folder -> {value}");
                break;

            case "browser":
                RunBrowserAction(value, Log);
                break;

            case "profile":
                RunProfileSwitch(value, Log);
                break;

            case "command":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] command without payload"); break; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + value,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Log($"[EXEC] command -> {value}");
                break;

            case "keys":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] keys without payload"); break; }
                if (momentary && buttonIndex >= 0 && value.IndexOfAny(SendKeysMeta) < 0)
                {
                    // A stale hold on this same button (missed up edge) — lift it first.
                    if (_heldShortcuts.ContainsKey(buttonIndex)) Release(buttonIndex);
                    if (HotkeySender.TryHoldDown(value, out var err))
                    {
                        _heldShortcuts[buttonIndex] = (value, DateTime.UtcNow);
                        Log($"[EXEC] keys hold-down -> \"{value}\"");
                    }
                    else
                    {
                        Log($"[EXEC] keys hold-down failed ({err}) — one-shot");
                        RunShortcut(value, Log);
                    }
                    break;
                }
                RunShortcut(value, Log);
                break;
            }

            case "text":
            {
                if (string.IsNullOrEmpty(value)) { Log("[EXEC] text without payload"); break; }
                System.Windows.Forms.SendKeys.SendWait(EscapeSendKeysLiteral(value));
                Log($"[EXEC] text -> \"{value}\"");
                break;
            }

            case "emoji":
            {
                if (string.IsNullOrEmpty(value)) { Log("[EXEC] emoji without payload"); break; }
                // Unicode injection, not SendKeys — see ActionExecutor.SendUnicodeText.
                ActionExecutor.SendUnicodeText(value, Log);
                break;
            }

            case "hotkeyswitch":
            {
                var spec = HotkeySwitchPayload.Parse(value);
                if (spec is null) { Log("[EXEC] hotkeyswitch: invalid payload"); break; }

                var toggleKey = (buttonIndex, value);
                bool nextIsB = _hotkeySwitchNextIsB.TryGetValue(toggleKey, out var stored) && stored;
                _hotkeySwitchNextIsB[toggleKey] = !nextIsB;

                string shortcut = nextIsB ? spec.ShortcutB : spec.ShortcutA;
                if (string.IsNullOrWhiteSpace(shortcut)) { Log("[EXEC] hotkeyswitch: empty shortcut"); break; }
                RunShortcut(shortcut, Log);
                Log($"[EXEC] hotkeyswitch -> \"{shortcut}\" ({(nextIsB ? "B" : "A")})");
                break;
            }

            case "adobe":
            case "davinci":
            case "zoom":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log($"[EXEC] {type} without payload"); break; }
                if (ActionExecutor.TryRunAppShortcutSpecial(value, Log)) break;
                RunShortcut(value, Log);
                Log($"[EXEC] {type} -> \"{value}\"");
                break;
            }

            case "oscmd":
                ActionExecutor.RunOsCommand(value, Log);
                break;

            case "media":
                ActionExecutor.SendMediaKey(value, Log);
                break;

            case "mouse":
                ActionExecutor.DoMouse(value, Log);
                break;

            case "audiodevice":
                ActionExecutor.SetAudioDevice(value, Log);
                break;

            case "obs":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] obs without payload"); break; }
                int tilde = value.IndexOf('~');
                string cmd = tilde < 0 ? value : value[..tilde];
                string arg = tilde < 0 ? "" : value[(tilde + 1)..];
                if (!Services.ObsBridge.EnsureConnected(Log)) { Log("[EXEC] obs: not connected"); break; }
                object?[]? parameters = arg.Length == 0 ? null : new object?[] { Services.ObsBridge.ConvertArg(cmd, arg) };
                bool ok = Services.ObsBridge.ExecuteCommand(cmd, parameters);
                Log($"[EXEC] obs -> {cmd}{(arg.Length > 0 ? $" ({arg})" : "")} = {ok}");
                break;
            }

            case "twitch":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] twitch without payload"); break; }
                int tilde = value.IndexOf('~');
                string cmd = tilde < 0 ? value : value[..tilde];
                string arg = tilde < 0 ? "" : value[(tilde + 1)..];
                bool ok = cmd switch
                {
                    "chat_message"     => Services.TwitchBridge.SendChatMessage(arg, Log),
                    "clear_chat"       => Services.TwitchBridge.ClearChat(Log),
                    "emote_only"       => Services.TwitchBridge.ToggleEmoteOnly(Log),
                    "followers_only"   => Services.TwitchBridge.SetFollowersOnly(arg, Log),
                    "slow_mode"        => Services.TwitchBridge.SetSlowMode(arg, Log),
                    "subscribers_only" => Services.TwitchBridge.ToggleSubscribersOnly(Log),
                    "play_ad"          => Services.TwitchBridge.PlayAd(arg, Log),
                    "stream_title"     => Services.TwitchBridge.SetStreamTitle(arg, Log),
                    "stream_marker"    => Services.TwitchBridge.CreateStreamMarker(Log),
                    "create_clip"      => Services.TwitchBridge.CreateClip(Log),
                    "open_last_clip"   => Services.TwitchBridge.OpenLastClip(Log),
                    _ => LogUnhandledTwitchCommand(cmd, Log),
                };
                Log($"[EXEC] twitch -> {cmd}{(arg.Length > 0 ? $" ({arg})" : "")} = {ok}");
                break;
            }

            case "discord":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] discord without payload"); break; }
                int tilde = value.IndexOf('~');
                string cmd = tilde < 0 ? value : value[..tilde];
                string arg = tilde < 0 ? "" : value[(tilde + 1)..];
                bool ok = cmd switch
                {
                    "mute_toggle"       => Services.DiscordBridge.ToggleMute(Log),
                    "mute_on"           => Services.DiscordBridge.SetMute(true, Log),
                    "mute_off"          => Services.DiscordBridge.SetMute(false, Log),
                    "deafen_toggle"     => Services.DiscordBridge.ToggleDeaf(Log),
                    "deafen_on"         => Services.DiscordBridge.SetDeaf(true, Log),
                    "deafen_off"        => Services.DiscordBridge.SetDeaf(false, Log),
                    "input_mode_toggle" => Services.DiscordBridge.ToggleInputMode(Log),
                    "input_volume"      => Services.DiscordBridge.SetInputVolume(arg, Log),
                    "output_volume"     => Services.DiscordBridge.SetOutputVolume(arg, Log),
                    "join_voice"        => Services.DiscordBridge.JoinVoiceChannel(arg, Log),
                    "leave_voice"       => Services.DiscordBridge.LeaveVoiceChannel(Log),
                    "user_volume"       => Services.DiscordBridge.SetUserVolume(arg, Log),
                    "user_mute_toggle"  => Services.DiscordBridge.ToggleUserMute(arg, Log),
                    "send_message"      => Services.DiscordBridge.SendWebhookMessage(arg, Log),
                    // Handled by the DisplayPad key dispatch before it ever reaches the
                    // engine (MainWindow.DisplayPad.cs / DpHandleBackgroundKey); on any
                    // other device there is no panel to take over.
                    "voice_page"        => true,
                    _ => LogUnhandledDiscordCommand(cmd, Log),
                };
                Log($"[EXEC] discord -> {cmd}{(arg.Length > 0 ? $" ({arg})" : "")} = {ok}");
                break;
            }

            case "spotify":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] spotify without payload"); break; }
                // Wire format: command[~arg][~deviceId]  (arg = volume step / playlist id;
                // deviceId = per-key target Spotify Connect device, "" for the active one).
                var sp = value.Split('~');
                string cmd = sp.Length > 0 ? sp[0] : "";
                string arg = sp.Length > 1 ? sp[1] : "";
                string spDevice = sp.Length > 2 ? sp[2] : "";

                // Unless Web API playback is CONFIRMED to work for this account (tier read as
                // Premium), fall back to plain system media keys (and the SMTC Spotify session
                // for shuffle/repeat) so the key still does something — media keys are harmless
                // for Premium too. Like / playlist commands are NOT diverted: they use the Web
                // API but work on free accounts. Reconnecting Spotify is what flips an account
                // from "unknown" to confirmed.
                if (Services.SpotifyStore.IsConnected && !Services.SpotifyStore.WebApiPlaybackConfirmed
                    && SpotifyMediaFallback(cmd, Log))
                {
                    Log($"[EXEC] spotify -> {cmd}: no Web API (not Premium) — used media fallback");
                    break;
                }

                // SpotifyBridge is fire-and-forget: its outcome is logged from a thread-pool
                // thread, so wrap Log to hop back onto the UI thread (the host's Log touches
                // WPF controls). Dispatch() itself runs on the UI thread (see Execute).
                var sc = System.Threading.SynchronizationContext.Current;
                Action<string> slog = sc is null ? Log : m => sc.Post(_ => Log(m), null);

                bool ok = cmd switch
                {
                    "play_pause"      => Services.SpotifyBridge.PlayPauseToggle(spDevice, slog),
                    "next"            => Services.SpotifyBridge.Next(spDevice, slog),
                    "previous"        => Services.SpotifyBridge.Previous(spDevice, slog),
                    "like_toggle"     => Services.SpotifyBridge.LikeToggle(slog),
                    "shuffle_toggle"  => Services.SpotifyBridge.ShuffleToggle(spDevice, slog),
                    "repeat_cycle"    => Services.SpotifyBridge.RepeatCycle(spDevice, slog),
                    "mute_toggle"     => Services.SpotifyBridge.MuteToggle(spDevice, slog),
                    "volume_up"       => Services.SpotifyBridge.VolumeUp(arg, spDevice, slog),
                    "volume_down"     => Services.SpotifyBridge.VolumeDown(arg, spDevice, slog),
                    "volume_set"      => Services.SpotifyBridge.VolumeSet(arg, spDevice, slog),
                    "save_playlist"   => Services.SpotifyBridge.SaveToPlaylist(arg, slog),
                    "remove_playlist" => Services.SpotifyBridge.RemoveFromPlaylist(arg, slog),
                    _ => LogUnhandledSpotifyCommand(cmd, Log),
                };
                Log($"[EXEC] spotify -> {cmd}{(arg.Length > 0 ? $" ({arg})" : "")} = {(ok ? "dispatched" : "skipped")}");
                break;
            }

            case "multi":
                ActionExecutor.RunMultiAction(value, Log, ExecuteSub);
                break;

            case "createfolder":
                ActionExecutor.CreateFolderOnDesktop(value, Log);
                break;

            case "back":
                ActionExecutor.GoBackBrowser(Log);
                break;

            case "pyscript":
                RunPyScript(value, buttonIndex);
                break;

            case "youtube":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] youtube without payload"); break; }
                Services.YouTubeBridge.SendLiveChatMessage(value, Log);
                break;

            case "googlehome":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] googlehome without payload"); break; }
                GoogleHomeBridge.Instance.Trigger(value, Log);
                break;

            case "macro":
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] macro without payload"); break; }
                if (ActionTypeHelper.IsUnresolvedMacroValue(value))
                {
                    // Imported Base Camp reference that never got matched to a K2 macro —
                    // the value is just the preserved original name, not something playable.
                    Log($"[EXEC] macro unresolved (BC name \"{ActionTypeHelper.StripUnresolvedMacroPrefix(value)}\") — nothing to play");
                    break;
                }
                _host.PlayMacro(value);
                Log($"[EXEC] macro -> {value}");
                break;

            case "pcinfo":
            case "clock":
                Log($"[EXEC] {type}: dynamic rendering not yet implemented (payload \"{value}\")");
                break;

            case "none":
                // intentionally no action (placeholder for unresolved macros)
                break;

            case "disable":
                // "Key disabled": nothing to run by design. Whether the keystroke ITSELF
                // is suppressed is a per-device firmware matter settled when the binding
                // is pushed, not here — and today only the Everest 60's numpad accessory
                // manages it (any bound key there stops emitting, see
                // Everest60Protocol.NumpadKeyBinding). Everywhere else K2 only observes
                // presses through the vendor SDK callback and cannot swallow them, so the
                // key keeps typing; reaching this line at all means that's the case, hence
                // the explicit wording (user report 2026-07-27).
                Log("[EXEC] key disabled — no action (keystroke not suppressed on this device)");
                break;

            default:
                Log($"[EXEC] unknown action type: {type}");
                break;
        }
    }

    private static bool LogUnhandledDiscordCommand(string cmd, Action<string> log)
    {
        log($"[EXEC] discord: command \"{cmd}\" not handled");
        return false;
    }

    private static bool LogUnhandledTwitchCommand(string cmd, Action<string> log)
    {
        log($"[EXEC] twitch: command \"{cmd}\" not handled");
        return false;
    }

    private static bool LogUnhandledSpotifyCommand(string cmd, Action<string> log)
    {
        log($"[EXEC] spotify: command \"{cmd}\" not handled");
        return false;
    }

    /// <summary>Non-Premium fallback for the transport/volume "spotify" commands: system media
    /// keys, plus the SMTC Spotify session for shuffle/repeat (no media key exists for those).
    /// Returns false for commands with no media equivalent (volume_set) and for like/playlist,
    /// which keep going through the Web API — those work on free Spotify accounts.</summary>
    private static bool SpotifyMediaFallback(string cmd, Action<string> log)
    {
        switch (cmd)
        {
            case "play_pause":     ActionExecutor.SendMediaKey("playpause", log); return true;
            case "next":           ActionExecutor.SendMediaKey("next", log); return true;
            case "previous":       ActionExecutor.SendMediaKey("previous", log); return true;
            case "mute_toggle":    ActionExecutor.SendMediaKey("mute", log); return true;
            case "volume_up":      ActionExecutor.SendMediaKey("volup", log); return true;
            case "volume_down":    ActionExecutor.SendMediaKey("voldown", log); return true;
            case "shuffle_toggle": ActionExecutor.SendMediaKey("shuffle", log); return true;
            case "repeat_cycle":
                _ = Services.SpotifyMediaService.Instance.CycleRepeatAsync();
                log("[EXEC] media -> repeat (Spotify)");
                return true;
            default:               return false;
        }
    }

    /// <summary>Executes a single sub-action (called by Multi Action).</summary>
    internal void ExecuteSub(string type, string value)
    {
        void Log(string m) => _host.Log(m);
        switch (type)
        {
            case "url":
                Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true }); break;
            case "exec":
                if (!string.IsNullOrWhiteSpace(value)) RunExecAction(value);
                break;
            case "folder":
                if (!string.IsNullOrWhiteSpace(value))
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe",
                        Arguments = $"\"{value}\"", UseShellExecute = true });
                break;
            case "browser":
                RunBrowserAction(value, Log);
                break;
            case "profile":  RunProfileSwitch(value, Log); break;
            case "keys":
                RunShortcut(value, Log);
                break;
            case "text":
                System.Windows.Forms.SendKeys.SendWait(EscapeSendKeysLiteral(value)); break;
            case "emoji": ActionExecutor.SendUnicodeText(value, Log); break;
            case "oscmd": ActionExecutor.RunOsCommand(value, Log); break;
            case "media": ActionExecutor.SendMediaKey(value, Log); break;
            case "mouse": ActionExecutor.DoMouse(value, Log); break;
            default:
                Log($"[EXEC] sub-action type \"{type}\" not handled"); break;
        }
    }

    private void RunPyScript(string value, int buttonIndex)
    {
        var spec = PyScriptPayload.Parse(value);
        if (spec is null)
        {
            _host.Log($"[PY  ] button #{buttonIndex}: invalid pyscript payload");
            return;
        }
        var ctx = new PyScriptContext
        {
            Device  = _host.CurrentDevice,
            Profile = _host.CurrentProfile,
            Button  = buttonIndex,
        };
        _py.RunScript(spec, ctx);
    }

    /// <summary>
    /// Runs the "profile" action. <paramref name="value"/> is either a
    /// <see cref="ProfileTargetPayload"/> JSON (one or more device+target rows from the
    /// dialog's "switch profile" picker) or a legacy plain string ("Next"/"Previous"/"N")
    /// predating that payload — in which case we fall back to the original behavior:
    /// switch the profile of the device this button lives on.
    /// </summary>
    private void RunProfileSwitch(string value, Action<string> log)
    {
        var spec = ProfileTargetPayload.Parse(value);
        if (spec is null)
        {
            _host.SwitchProfile(null, value);
            return;
        }
        foreach (var t in spec.Targets)
        {
            try { _host.SwitchProfile(string.IsNullOrEmpty(t.Key) ? null : t.Key, t.Target); }
            catch (Exception ex) { log($"[EXEC] profile: target \"{t.Key}\" error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Runs the "exec" action. .bat/.cmd scripts are launched hidden via cmd.exe by default
    /// (ShellExecute on a batch file always flashes a console window for an instant); the
    /// dialog can mark a script as "run in a visible terminal" (see <see cref="ExecActionPayload"/>),
    /// in which case cmd.exe keeps its window. Anything else keeps using ShellExecute so
    /// file associations/UAC still work.
    /// </summary>
    private static void RunExecAction(string value)
    {
        var (path, showConsole) = ExecActionPayload.Split(value);
        if (string.IsNullOrWhiteSpace(path)) return;
        var dir = Path.GetDirectoryName(path) ?? "";
        if (ExecActionPayload.IsBatch(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{path}\"",
                UseShellExecute = showConsole,
                CreateNoWindow = !showConsole,
                WindowStyle = showConsole ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                WorkingDirectory = dir
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, WorkingDirectory = dir });
        }
    }

    /// <summary>
    /// Runs the "browser" action. <paramref name="value"/> is either a
    /// <see cref="BrowserActionPayload"/> JSON (specific browser chosen in the dialog) or
    /// a legacy plain string (a raw URL, or empty) predating that payload — in which case
    /// we fall back to the original behavior: open the URL with the OS default browser.
    /// </summary>
    private static void RunBrowserAction(string value, Action<string> log)
    {
        var spec = BrowserActionPayload.Parse(value);
        if (spec is null)
        {
            var url = string.IsNullOrWhiteSpace(value) ? "https://duckduckgo.com" : value;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            log($"[EXEC] browser -> {url}");
            return;
        }

        string? exe = spec.Browser == "other"
            ? spec.CustomPath
            : BrowserDetector.ResolveById(spec.Browser);

        if (string.IsNullOrWhiteSpace(exe))
        {
            // "Other" with no path (or a known browser that's no longer installed):
            // fall back to the OS default browser, same as the legacy behavior.
            var url = string.IsNullOrWhiteSpace(spec.Url) ? "https://duckduckgo.com" : spec.Url;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            log($"[EXEC] browser -> default -> {url}");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.IsNullOrWhiteSpace(spec.Url) ? "" : spec.Url,
            UseShellExecute = true
        });
        log($"[EXEC] browser -> {exe} {spec.Url}");
    }

    private static string EscapeSendKeysLiteral(string input)
    {
        var sb = new StringBuilder(input.Length + 8);
        foreach (var ch in input)
        {
            switch (ch)
            {
                case '{': case '}':
                case '(': case ')':
                case '+': case '^':
                case '%': case '~':
                case '[': case ']':
                    sb.Append('{').Append(ch).Append('}');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
