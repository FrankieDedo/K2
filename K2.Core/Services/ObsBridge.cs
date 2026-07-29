using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using OBSWebsocketDotNet.Types;

namespace K2.Core.Services;

/// <summary>
/// OBS Studio integration via obs-websocket v5 (<c>OBSWebsocketDotNet</c> NuGet package,
/// confirmed to match the exact API real Base Camp itself used —
/// <c>_reference/decompiled/Worker/DisplayPadWorker.Helpers/OBS.cs</c> — every method name
/// checked against the installed 5.0.1 assembly before porting). One shared connection for
/// the whole app, configured via <see cref="ObsStore"/> (host/port/password).
///
/// Each actionable command is tagged <see cref="DescriptionAttribute"/> the same way the
/// decompiled reference is; <see cref="ExecuteCommand"/> dispatches by that name via
/// reflection, exactly like Base Camp's own <c>OBS.ExecuteCommand</c> — this is what lets
/// <c>ButtonActionDialog.Simple.cs</c>'s OBS combo just store the command's display name as
/// the button's ActionValue (optionally with a <c>~</c>-separated argument, same wire format
/// Base Camp itself already used) instead of a bespoke enum.
/// </summary>
public static class ObsBridge
{
    private enum SourceTypeId
    {
        wasapi_input_capture,
        wasapi_output_capture,
    }

    private static OBSWebsocket? _obs;
    private static int _connectStatus = -1;

    private static OBSWebsocket Obs => _obs ??= CreateClient();

    private static OBSWebsocket CreateClient()
    {
        var obs = new OBSWebsocket();
        obs.Connected += (_, _) => _connectStatus = 1;
        obs.Disconnected += (_, _) => _connectStatus = 0;
        return obs;
    }

    public static bool IsConnected => _obs?.IsConnected == true;

    /// <summary>Connects using <see cref="ObsStore"/>'s saved host/port/password if not already
    /// connected. Synchronous (mirrors the decompiled reference's busy-wait), so callers on the
    /// UI thread should only invoke this from a background action execution, not directly from a
    /// dialog's UI event — <see cref="ButtonActionEngine"/>'s "obs" case already runs off the
    /// button-press path, not a UI callback.</summary>
    public static bool EnsureConnected(Action<string>? log = null)
    {
        if (IsConnected) return true;
        try
        {
            _connectStatus = -1;
            string url = $"ws://{ObsStore.Host}:{ObsStore.Port}";
            Obs.ConnectAsync(url, ObsStore.Password);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_connectStatus == -1 && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(20);
            if (!IsConnected) log?.Invoke("[EXEC] obs: connect timed out or failed");
            return IsConnected;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[EXEC] obs: connect error: {ex.Message}");
            return false;
        }
    }

    public static void Disconnect()
    {
        try { if (IsConnected) Obs.Disconnect(); }
        catch { /* best-effort */ }
    }

    public static string GetVersionString()
    {
        try { return Obs.GetVersion().OBSStudioVersion; }
        catch { return ""; }
    }

    /// <summary>Converts a picker/imported arg string to whatever type the named command's
    /// method actually expects — <see cref="ExecuteCommand"/>'s reflection <c>Invoke</c> throws
    /// if a boxed string is passed where the method wants an <c>int</c>/<c>double</c>.</summary>
    public static object ConvertArg(string commandName, string arg) => commandName switch
    {
        "Set Transition Duration" => int.TryParse(arg, out var i) ? i : 0,
        "Set Mic Volume" or "Set Desktop Volume" => double.TryParse(arg, out var d) ? d : 0.0,
        _ => arg,
    };

    /// <summary>Dispatches by the <see cref="DescriptionAttribute"/> name (e.g. "Next Scene"),
    /// same reflection approach as the decompiled reference's <c>OBS.ExecuteCommand</c>.</summary>
    public static bool ExecuteCommand(string commandName, object?[]? parameters = null)
    {
        foreach (var method in typeof(ObsBridge).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = method.GetCustomAttribute<DescriptionAttribute>();
            if (attr is null || attr.Description != commandName) continue;
            return method.Invoke(null, parameters) is true;
        }
        return false;
    }

    [Description("Start Streaming")]
    public static bool StartStreaming()
    {
        try
        {
            if (!IsConnected) return false;
            if (!Obs.GetStreamStatus().IsActive) Obs.StartStream();
            return true;
        }
        catch (Exception ex) { return ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase); }
    }

    [Description("Stop Streaming")]
    public static bool StopStreaming()
    {
        try
        {
            if (!IsConnected) return false;
            if (Obs.GetStreamStatus().IsActive) Obs.StopStream();
            return true;
        }
        catch { return false; }
    }

    [Description("Start Recording")]
    public static bool StartRecording()
    {
        try
        {
            if (!IsConnected) return false;
            var status = Obs.GetRecordStatus();
            if (!status.IsRecording || status.IsRecordingPaused) Obs.StartRecord();
            return true;
        }
        catch { return false; }
    }

    [Description("Stop Recording")]
    public static bool StopRecording()
    {
        try
        {
            if (!IsConnected) return false;
            if (Obs.GetRecordStatus().IsRecording) Obs.StopRecord();
            return true;
        }
        catch (Exception ex) { return ex.Message.Contains("not active", StringComparison.OrdinalIgnoreCase); }
    }

    [Description("Pause Recording")]
    public static bool PauseRecording()
    {
        try
        {
            if (!IsConnected) return false;
            if (Obs.GetRecordStatus().IsRecording) Obs.PauseRecord();
            return true;
        }
        catch (Exception ex) { return ex.Message.Contains("not active", StringComparison.OrdinalIgnoreCase); }
    }

    [Description("Resume Recording")]
    public static bool ResumeRecording()
    {
        try
        {
            if (!IsConnected) return false;
            if (Obs.GetRecordStatus().IsRecordingPaused) Obs.ResumeRecord();
            return true;
        }
        catch (Exception ex) { return ex.Message.Contains("not active", StringComparison.OrdinalIgnoreCase); }
    }

    [Description("Next Profile")]
    public static bool NextProfile()
    {
        try
        {
            if (!IsConnected) return false;
            var list = Obs.GetProfileList();
            if (list is null || list.Profiles.Count == 0) return false;
            if (list.Profiles.Count == 1) return true;
            int i = list.Profiles.IndexOf(list.CurrentProfileName);
            Obs.SetCurrentProfile(list.Profiles[i != list.Profiles.Count - 1 ? i + 1 : 0]);
            return true;
        }
        catch { return false; }
    }

    [Description("Previous Profile")]
    public static bool PreviousProfile()
    {
        try
        {
            if (!IsConnected) return false;
            var list = Obs.GetProfileList();
            if (list is null || list.Profiles.Count == 0) return false;
            if (list.Profiles.Count == 1) return true;
            int i = list.Profiles.IndexOf(list.CurrentProfileName);
            Obs.SetCurrentProfile(list.Profiles[i == 0 ? list.Profiles.Count - 1 : i - 1]);
            return true;
        }
        catch { return false; }
    }

    [Description("Get Profiles")]
    public static string? GetProfiles()
    {
        try { return IsConnected ? JsonSerializer.Serialize(Obs.GetProfileList().Profiles) : null; }
        catch { return null; }
    }

    [Description("Set Current Profile")]
    public static bool SetCurrentProfile(string profileName)
    {
        try { if (!IsConnected) return false; Obs.SetCurrentProfile(profileName); return true; }
        catch { return false; }
    }

    /// <summary>Live profile names — see <see cref="ListSceneNames"/> remarks.</summary>
    public static string[] ListProfileNames()
    {
        try { return EnsureConnected() ? Obs.GetProfileList().Profiles.ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    [Description("Next Scene")]
    public static bool NextScene()
    {
        try
        {
            if (!IsConnected) return false;
            var scenes = Obs.ListScenes();
            if (scenes is null || scenes.Count == 0) return false;
            if (scenes.Count == 1) return true;
            string current = Obs.GetCurrentProgramScene();
            int i = scenes.FindIndex(s => s.Name == current);
            Obs.SetCurrentProgramScene(scenes[i != scenes.Count - 1 ? i + 1 : 0].Name);
            return true;
        }
        catch { return false; }
    }

    [Description("Previous Scene")]
    public static bool PreviousScene()
    {
        try
        {
            if (!IsConnected) return false;
            var scenes = Obs.ListScenes();
            if (scenes is null || scenes.Count == 0) return false;
            if (scenes.Count == 1) return true;
            string current = Obs.GetCurrentProgramScene();
            int i = scenes.FindIndex(s => s.Name == current);
            Obs.SetCurrentProgramScene(scenes[i == 0 ? scenes.Count - 1 : i - 1].Name);
            return true;
        }
        catch { return false; }
    }

    [Description("Get Scenes")]
    public static string? GetScenes()
    {
        try { return IsConnected ? JsonSerializer.Serialize(Obs.ListScenes()) : null; }
        catch { return null; }
    }

    /// <summary>Live scene names for the "Set Current Scene" picker's secondary dropdown
    /// (<c>ButtonActionDialog.Simple.cs</c>) — connects on demand (like <see cref="ExecuteCommand"/>'s
    /// own commands do), so only call this off the UI thread. Empty when OBS isn't reachable.</summary>
    public static string[] ListSceneNames()
    {
        try { return EnsureConnected() ? Obs.ListScenes().Select(s => s.Name).ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    [Description("Set Current Scene")]
    public static bool SetCurrentScene(string sceneName)
    {
        try { if (!IsConnected) return false; Obs.SetCurrentProgramScene(sceneName); return true; }
        catch { return false; }
    }

    [Description("Get Sources")]
    public static string? GetSources()
    {
        try { return IsConnected ? JsonSerializer.Serialize(Obs.GetSceneItemList(Obs.GetCurrentProgramScene())) : null; }
        catch { return null; }
    }

    /// <summary>Live source names (current program scene) — see <see cref="ListSceneNames"/> remarks.</summary>
    public static string[] ListSourceNames()
    {
        try
        {
            if (!EnsureConnected()) return Array.Empty<string>();
            return Obs.GetSceneItemList(Obs.GetCurrentProgramScene()).Select(i => i.SourceName).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Toggles a source's visibility in the current scene by name. The decompiled
    /// reference took a comma-joined "sceneName,sourceName" string but only ever used the
    /// source-name half (the scene half was dead — always the current program scene regardless),
    /// so this takes just the source name directly.</summary>
    [Description("Set Current Source")]
    public static bool SetRenderSource(string sourceName)
    {
        try
        {
            if (!IsConnected) return false;
            string currentScene = Obs.GetCurrentProgramScene();
            foreach (var item in Obs.GetSceneItemList(currentScene))
            {
                if (item.SourceName != sourceName) continue;
                bool enabled = Obs.GetSceneItemEnabled(currentScene, item.ItemId);
                Obs.SetSceneItemEnabled(currentScene, item.ItemId, !enabled);
                break;
            }
            return true;
        }
        catch { return false; }
    }

    [Description("Next Transition")]
    public static bool NextTransition()
    {
        try
        {
            if (!IsConnected) return false;
            var list = Obs.GetSceneTransitionList();
            if (list is null || list.Transitions.Count == 0) return false;
            if (list.Transitions.Count == 1) return true;
            int i = list.Transitions.FindIndex(t => t.Name == list.CurrentTransition);
            Obs.SetCurrentSceneTransition(list.Transitions[i != list.Transitions.Count - 1 ? i + 1 : 0].Name);
            return true;
        }
        catch { return false; }
    }

    [Description("Previous Transition")]
    public static bool PreviousTransition()
    {
        try
        {
            if (!IsConnected) return false;
            var list = Obs.GetSceneTransitionList();
            if (list is null || list.Transitions.Count == 0) return false;
            if (list.Transitions.Count == 1) return true;
            int i = list.Transitions.FindIndex(t => t.Name == list.CurrentTransition);
            Obs.SetCurrentSceneTransition(list.Transitions[i == 0 ? list.Transitions.Count - 1 : i - 1].Name);
            return true;
        }
        catch { return false; }
    }

    [Description("Set Transition Duration")]
    public static bool SetTransitionDuration(int duration)
    {
        try { if (!IsConnected) return false; Obs.SetCurrentSceneTransitionDuration(duration); return true; }
        catch { return false; }
    }

    [Description("Get Transitions")]
    public static string? GetTransitions()
    {
        try
        {
            if (!IsConnected) return null;
            return JsonSerializer.Serialize(Obs.GetSceneTransitionList().Transitions.Select(t => t.Name));
        }
        catch { return null; }
    }

    [Description("Set Current TransitionName")]
    public static bool SetCurrentTransition(string transitionName)
    {
        try { if (!IsConnected) return false; Obs.SetCurrentSceneTransition(transitionName); return true; }
        catch { return false; }
    }

    /// <summary>Live transition names — see <see cref="ListSceneNames"/> remarks.</summary>
    public static string[] ListTransitionNames()
    {
        try { return EnsureConnected() ? Obs.GetSceneTransitionList().Transitions.Select(t => t.Name).ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static List<InputBasicInfo> GetSourcesList(SourceTypeId kind)
        => Obs.GetInputList()?.Where(i => i.InputKind == kind.ToString()).ToList() ?? new();

    [Description("Mic Volume +")]
    public static bool IncreaseMicVolume() => AdjustVolume(SourceTypeId.wasapi_input_capture, +10, -96);

    [Description("Mic Volume -")]
    public static bool DecreaseMicVolume() => AdjustVolume(SourceTypeId.wasapi_input_capture, -10, -100);

    [Description("Mute Mic")]
    public static bool MuteMic() => SetMute(SourceTypeId.wasapi_input_capture, true);

    [Description("Unmute Mic")]
    public static bool UnmuteMic() => SetMute(SourceTypeId.wasapi_input_capture, false);

    [Description("Set Mic Volume")]
    public static bool SetMicVolume(double volumeDb) => SetVolume(SourceTypeId.wasapi_input_capture, volumeDb, -96);

    [Description("Desktop Volume +")]
    public static bool IncreaseDesktopVolume() => AdjustVolume(SourceTypeId.wasapi_output_capture, +10, -96);

    [Description("Desktop Volume -")]
    public static bool DecreaseDesktopVolume() => AdjustVolume(SourceTypeId.wasapi_output_capture, -10, -100);

    [Description("Mute Desktop Volume")]
    public static bool MuteDesktop() => SetMute(SourceTypeId.wasapi_output_capture, true);

    [Description("Unmute Desktop Volume")]
    public static bool UnmuteDesktop() => SetMute(SourceTypeId.wasapi_output_capture, false);

    [Description("Set Desktop Volume")]
    public static bool SetDesktopVolume(double volumeDb) => SetVolume(SourceTypeId.wasapi_output_capture, volumeDb, -100);

    private static bool AdjustVolume(SourceTypeId kind, double stepDb, double floorDb)
    {
        try
        {
            if (!IsConnected) return false;
            foreach (var item in GetSourcesList(kind))
            {
                double current = Obs.GetInputVolume(item.InputName).VolumeDb;
                double next = current < 0 ? Math.Clamp(current + stepDb, floorDb, 0) : 0;
                Obs.SetInputVolume(item.InputName, (float)next, true);
            }
            return true;
        }
        catch { return false; }
    }

    private static bool SetVolume(SourceTypeId kind, double volumeDb, double floorDb)
    {
        try
        {
            if (!IsConnected) return false;
            double clamped = Math.Clamp(volumeDb, floorDb, 0);
            foreach (var item in GetSourcesList(kind))
                Obs.SetInputVolume(item.InputName, (float)clamped, true);
            return true;
        }
        catch { return false; }
    }

    private static bool SetMute(SourceTypeId kind, bool muted)
    {
        try
        {
            if (!IsConnected) return false;
            foreach (var item in GetSourcesList(kind)) Obs.SetInputMute(item.InputName, muted);
            return true;
        }
        catch { return false; }
    }

    [Description("Enable Studio Mode")]
    public static bool EnableStudioMode()
    {
        try { if (!IsConnected) return false; Obs.SetStudioModeEnabled(true); return true; }
        catch { return false; }
    }

    [Description("Disable Studio Mode")]
    public static bool DisableStudioMode()
    {
        try { if (!IsConnected) return false; Obs.SetStudioModeEnabled(false); return true; }
        catch { return false; }
    }

    [Description("Start Replay Buffer")]
    public static bool StartReplyBuffer()
    {
        try { if (!IsConnected) return false; Obs.StartReplayBuffer(); return true; }
        catch { return false; }
    }

    [Description("Stop Replay Buffer")]
    public static bool StopReplyBuffer()
    {
        try { if (!IsConnected) return false; Obs.StopReplayBuffer(); return true; }
        catch { return false; }
    }

    [Description("Save Replay Buffer")]
    public static bool SaveReplyBuffer()
    {
        try { if (!IsConnected) return false; Obs.SaveReplayBuffer(); return true; }
        catch { return false; }
    }

    private static bool TriggerMediaAction(string action)
    {
        try
        {
            if (!IsConnected) return false;
            string scene = Obs.GetCurrentProgramScene();
            var mediaItems = Obs.GetSceneItemList(scene)
                .Where(i => i.SourceKind is "ffmpeg_source" or "vlc_source");
            foreach (var item in mediaItems) Obs.TriggerMediaInputAction(item.SourceName, action);
            return true;
        }
        catch { return false; }
    }

    [Description("Next Media")]
    public static bool NextMedia() => TriggerMediaAction("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_NEXT");

    [Description("Previous Media")]
    public static bool PreviousMedia() => TriggerMediaAction("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PREVIOUS");

    [Description("Play Media")]
    public static bool PlayMedia() => TriggerMediaAction("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY");

    [Description("Pause Media")]
    public static bool PauseMedia() => TriggerMediaAction("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE");

    [Description("Stop Media")]
    public static bool StopMedia() => TriggerMediaAction("OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP");

    [Description("Open Projector")]
    public static bool OpenProjector()
    {
        try
        {
            if (!IsConnected) return false;
            Obs.OpenSourceProjector(Obs.GetCurrentProgramScene(), null);
            return true;
        }
        catch { return false; }
    }
}
