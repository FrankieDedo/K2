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

    public ButtonActionEngine(IActionHost host)
    {
        _host = host;
        _py = new PyBridge(host, this);
    }

    /// <summary>True if the Python runtime is installed and available.</summary>
    public bool PythonRuntimeAvailable => _py.RuntimeAvailable;

    /// <summary>Starts the Python bridge (RPC server). Call once at startup.</summary>
    public void Start() => _py.Start();

    public void Dispose() => _py.Dispose();

    /// <summary>
    /// Executes the action configured on a button. MUST BE CALLED ON THE UI THREAD.
    /// <paramref name="buttonIndex"/> is the context for Python scripts
    /// (-1 if unknown, e.g. action invoked via RPC).
    /// </summary>
    public void Execute(string? actionType, string? actionValue, int buttonIndex = -1)
    {
        if (string.IsNullOrEmpty(actionType)) return;
        var value = actionValue ?? "";
        try
        {
            Dispatch(actionType, value, buttonIndex);
        }
        catch (Exception ex)
        {
            _host.Log($"[ERR ] button #{buttonIndex} action execution: {ex.Message}");
        }
    }

    private void Dispatch(string type, string value, int buttonIndex)
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
                string seq = value.IndexOfAny(SendKeysMeta) >= 0
                    ? value : SendKeysTranslator.Translate(value);
                System.Windows.Forms.SendKeys.SendWait(seq);
                Log($"[EXEC] keys -> \"{value}\"  (sendkeys=\"{seq}\")");
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
                string seq = shortcut.IndexOfAny(SendKeysMeta) >= 0
                    ? shortcut : SendKeysTranslator.Translate(shortcut);
                System.Windows.Forms.SendKeys.SendWait(seq);
                Log($"[EXEC] hotkeyswitch -> \"{shortcut}\" ({(nextIsB ? "B" : "A")})");
                break;
            }

            case "adobe":
            case "davinci":
            case "zoom":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log($"[EXEC] {type} without payload"); break; }
                if (ActionExecutor.TryRunAppShortcutSpecial(value, Log)) break;
                string seq = value.IndexOfAny(SendKeysMeta) >= 0
                    ? value : SendKeysTranslator.Translate(value);
                System.Windows.Forms.SendKeys.SendWait(seq);
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
                    _ => LogUnhandledDiscordCommand(cmd, Log),
                };
                Log($"[EXEC] discord -> {cmd}{(arg.Length > 0 ? $" ({arg})" : "")} = {ok}");
                break;
            }

            case "spotify":
            {
                if (string.IsNullOrWhiteSpace(value)) { Log("[EXEC] spotify without payload"); break; }
                int tilde = value.IndexOf('~');
                string cmd = tilde < 0 ? value : value[..tilde];
                string arg = tilde < 0 ? "" : value[(tilde + 1)..];
                bool ok = cmd switch
                {
                    "play_pause"      => Services.SpotifyBridge.PlayPauseToggle(Log),
                    "next"            => Services.SpotifyBridge.Next(Log),
                    "previous"        => Services.SpotifyBridge.Previous(Log),
                    "like_toggle"     => Services.SpotifyBridge.LikeToggle(Log),
                    "shuffle_toggle"  => Services.SpotifyBridge.ShuffleToggle(Log),
                    "repeat_cycle"    => Services.SpotifyBridge.RepeatCycle(Log),
                    "mute_toggle"     => Services.SpotifyBridge.MuteToggle(Log),
                    "volume_up"       => Services.SpotifyBridge.VolumeUp(arg, Log),
                    "volume_down"     => Services.SpotifyBridge.VolumeDown(arg, Log),
                    "volume_set"      => Services.SpotifyBridge.VolumeSet(arg, Log),
                    "save_playlist"   => Services.SpotifyBridge.SaveToPlaylist(arg, Log),
                    "remove_playlist" => Services.SpotifyBridge.RemoveFromPlaylist(arg, Log),
                    _ => LogUnhandledSpotifyCommand(cmd, Log),
                };
                Log($"[EXEC] spotify -> {cmd}{(arg.Length > 0 ? $" ({arg})" : "")} = {ok}");
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
            {
                string seq = value.IndexOfAny(SendKeysMeta) >= 0
                    ? value : SendKeysTranslator.Translate(value);
                System.Windows.Forms.SendKeys.SendWait(seq);
                break;
            }
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
    /// Runs the "exec" action. .bat/.cmd scripts are launched hidden via cmd.exe
    /// (ShellExecute on a batch file always flashes a console window for an instant);
    /// anything else keeps using ShellExecute so file associations/UAC still work.
    /// </summary>
    private static void RunExecAction(string value)
    {
        var dir = Path.GetDirectoryName(value) ?? "";
        var ext = Path.GetExtension(value);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{value}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = dir
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true, WorkingDirectory = dir });
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
