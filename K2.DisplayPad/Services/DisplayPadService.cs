using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using DisplayPad.SDK;

namespace K2.DisplayPad.Services;

/// <summary>
/// Facade su <c>DisplayPad.SDK</c>.
///
/// Mappa reale dell'SDK (verificata leggendo i metadati ECMA-335 della DLL):
///   - <c>DisplayPad.SDK.DisplayPadSDK</c>: i metodi sono <em>internal static</em>,
///     quindi NON utilizzabili direttamente dall'esterno dell'assembly.
///     L'unica cosa raggiungibile dall'esterno via reflection e' il field statico
///     <c>lstDeviceID</c> (la collection degli ID dei device noti).
///   - <c>DisplayPad.SDK.DisplayPadHelper</c>: classe pubblica con metodi
///     pubblici di <em>istanza</em> (DisplayPadOpenUSBDriver, DisplayPadIsDevicePlug,
///     UploadImage, ...). Gli eventi <c>DisplayPadPlugCallBack</c>,
///     <c>DisplayPadKeyCallBack</c>, <c>DisplayPadProgressCallBack</c> sono invece
///     <em>statici</em> sulla classe.
///
/// Questa facade istanzia una sola <c>DisplayPadHelper</c>, aggancia i tre eventi
/// statici via reflection (firme target: <c>void(int,int,int)</c> come da
/// DisplayPad.SDK.xml) e ri-espone tutto come eventi .NET tipizzati.
/// </summary>
public sealed class DisplayPadService : IDisposable
{
    private readonly DisplayPadHelper _helper = new();
    private bool _opened;
    private Delegate? _plugHandler;
    private Delegate? _keyHandler;
    private Delegate? _progressHandler;

    /// <summary>Plug / Unplug / Suspend del device.</summary>
    public event EventHandler<DevicePlugEventArgs>? DevicePlug;

    /// <summary>Tasto del DisplayPad premuto/rilasciato.</summary>
    public event EventHandler<DisplayPadKeyEventArgs>? KeyEvent;

    /// <summary>Avanzamento update firmware (0..100, -1 = fail).</summary>
    public event EventHandler<FirmwareProgressEventArgs>? FirmwareProgress;

    /// <summary>Numero massimo di device gestiti dall'SDK (MAX_DEV_COUNT = 10).</summary>
    public const int MaxDeviceCount = 10;

    /// <summary>Apre il driver USB. <paramref name="hWnd"/> e' l'HWND della
    /// finestra principale: serve perche' l'SDK posta WM_DEVICE_PLUG /
    /// WM_FW_PROGRESS al suo message pump.
    /// L'API DisplayPadHelper accetta l'handle come <em>stringa</em> (decimale).</summary>
    public bool Open(IntPtr hWnd)
    {
        if (_opened) return true;

        AttachStaticEvent("DisplayPadPlugCallBack",     nameof(OnPlug),     out _plugHandler);
        AttachStaticEvent("DisplayPadKeyCallBack",      nameof(OnKey),      out _keyHandler);
        AttachStaticEvent("DisplayPadProgressCallBack", nameof(OnProgress), out _progressHandler);

        var hStr = hWnd.ToInt64().ToString();
        App.WriteLog($"[Open] DisplayPadOpenUSBDriver(\"{hStr}\")");
        bool ok = _helper.DisplayPadOpenUSBDriver(hStr);
        App.WriteLog($"[Open] -> {ok}");
        _opened = ok;
        return ok;
    }

    /// <summary>Chiude il driver e disiscrive gli eventi.</summary>
    public void Close()
    {
        if (!_opened) return;
        try { _helper.DisplayPadCloseUSBDriver(); } catch { /* swallow */ }
        _opened = false;
        DetachStaticEvent("DisplayPadPlugCallBack",     _plugHandler);
        DetachStaticEvent("DisplayPadKeyCallBack",      _keyHandler);
        DetachStaticEvent("DisplayPadProgressCallBack", _progressHandler);
        _plugHandler = _keyHandler = _progressHandler = null;
    }

    /// <summary>Versione della DLL SDK managed.</summary>
    public int SdkVersion() => _helper.DisplayPadDllVersion();

    // L'SDK consente solo i valori 0/25/50/75/100 per la luminosita'.
    // Usiamo questo come discriminante: gli slot phantom restituiscono
    // valori fuori range, mentre i device reali ritornano sempre uno
    // di questi cinque step.
    private static readonly HashSet<int> ValidBrightness = new() { 0, 25, 50, 75, 100 };

    /// <summary>ID dei device REALMENTE collegati.
    /// <c>DisplayPadIsDevicePlug</c> e' inaffidabile (puo' restituire true
    /// anche per slot phantom). Filtriamo verificando che la luminosita'
    /// letta sia uno dei cinque step ammessi dall'SDK.</summary>
    public IReadOnlyList<int> DeviceIds()
    {
        var found = new List<int>();
        for (int id = 1; id <= MaxDeviceCount; id++)
        {
            bool plugged;
            try { plugged = _helper.DisplayPadIsDevicePlug(id); }
            catch (Exception ex)
            {
                App.WriteLog($"[DeviceIds] IsDevicePlug({id}) threw: {ex.Message}");
                continue;
            }
            if (!plugged) continue;

            // Phantom slots return empty firmware version; real devices always have one.
            string fw;
            try { fw = _helper.DisplayPadGetDevAppVer(id) ?? ""; }
            catch { continue; }
            if (string.IsNullOrEmpty(fw))
            {
                App.WriteLog($"[DeviceIds] id={id} skipped (empty fw version -> phantom)");
                continue;
            }

            int brightness;
            try { brightness = _helper.DisplayPadGetMainBrightness(id); }
            catch { continue; }

            if (!ValidBrightness.Contains(brightness))
            {
                App.WriteLog($"[DeviceIds] id={id} skipped (brightness={brightness} non valida -> phantom)");
                continue;
            }
            found.Add(id);
        }
        App.WriteLog($"[DeviceIds] real devices -> [{string.Join(", ", found)}]");
        return found;
    }

    /// <summary>Snapshot del field <c>lstDeviceID</c> dell'SDK (puo' essere
    /// vuoto subito dopo l'Open finche' non si interroga il driver).
    /// Utile a scopo diagnostico.</summary>
    public IReadOnlyList<int> ListDeviceIdSnapshot()
    {
        var sdkType = typeof(DisplayPadHelper).Assembly
            .GetType("DisplayPad.SDK.DisplayPadSDK");
        if (sdkType is null) return Array.Empty<int>();
        var field = sdkType.GetField("lstDeviceID",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (field?.GetValue(null) is not System.Collections.IEnumerable raw)
            return Array.Empty<int>();
        var result = new List<int>();
        foreach (var item in raw)
        {
            if (item is null) continue;
            try { result.Add(Convert.ToInt32(item)); } catch { }
        }
        return result;
    }

    /// <summary>Numero di device collegati.</summary>
    public int DeviceCount() => DeviceIds().Count;

    /// <summary>True se il device e' fisicamente plugged.</summary>
    public bool IsPlugged(int id) => _helper.DisplayPadIsDevicePlug(id);

    /// <summary>Versione FW del device.
    /// L'API SDK ritorna la versione come <see cref="string"/> (es. "1.0.6"). </summary>
    public string FirmwareVersion(int id) => _helper.DisplayPadGetDevAppVer(id) ?? "";

    /// <summary>Luminosita' attuale del device (0/25/50/75/100).</summary>
    public int GetBrightness(int id) => _helper.DisplayPadGetMainBrightness(id);

    /// <summary>Imposta la luminosita' (0/25/50/75/100).</summary>
    public bool SetBrightness(int id, int level) =>
        _helper.DisplayPadSetMainBrightness(level, id);

    /// <summary>Cambia profilo attivo (1..5).</summary>
    public bool SwitchProfile(int id, int profile) =>
        _helper.DisplayPadSwitchProfile(profile.ToString(), id);

    /// <summary>Numero di tasti del DisplayPad (FW_NUM_KEY = 12).</summary>
    public const int ButtonCount = 12;

    /// <summary>Numero massimo di profili (FW_NUM_PROFILE = 5).</summary>
    public const int ProfileCount = 5;

    /// <summary>Abilita / disabilita il controllo SW del device.
    /// Quando true, l'host gestisce le immagini dei tasti in tempo reale
    /// (UploadImage usa SetIconPacket); quando false, le immagini stanno nel
    /// profilo memorizzato sul firmware.
    ///
    /// Il MountainDisplayPadWorker.exe originale fa retry fino a ~6 volte
    /// quando APEnable ritorna false: replichiamo lo stesso pattern.</summary>
    public bool APEnable(int id, bool enable, int retries = 10)
    {
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            bool ok;
            try { ok = _helper.DisplayPadAPEnable(enable ? "1" : "0", id); }
            catch (Exception ex)
            {
                App.WriteLog($"[APEnable] id={id} enable={enable} threw on attempt {attempt}: {ex.Message}");
                return false;
            }
            if (ok)
            {
                App.WriteLog($"[APEnable] id={id} enable={enable} OK (attempt {attempt})");
                return true;
            }
            if (attempt < retries) Thread.Sleep(150);
        }
        App.WriteLog($"[APEnable] id={id} enable={enable} FAIL after {retries} retries");
        return false;
    }

    /// <summary>Probe del device: chiama <c>DisplayPadGetFWInfo</c> e ritorna
    /// una stringa diagnostica con i campi della struct. Utile per distinguere
    /// device "veri" dagli slot phantom popolati da <c>IsDevicePlug</c>.</summary>
    public string ProbeFirmwareInfo(int id)
    {
        try
        {
            object? info = _helper.DisplayPadGetFWInfo(id);
            if (info is null) return "<null>";
            // Estraiamo i campi public via reflection: la struct FWInfo non e'
            // referenziabile in C# senza definirla esplicitamente.
            var t = info.GetType();
            var parts = new List<string>();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                parts.Add($"{f.Name}={f.GetValue(info)}");
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                try { parts.Add($"{p.Name}={p.GetValue(info)}"); } catch { }
            }
            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    /// <summary>Reset di TUTTE le immagini dei tasti del profilo corrente.</summary>
    public bool ResetAllPictures(int id) =>
        _helper.DisplayPadResetPicture(id);

    /// <summary>Carica un'immagine sul tasto in modalita' "live" via SetIconPacket
    /// (richiede AP enabled). E' veloce ma <b>non persistente</b>: il firmware
    /// ridisegna l'icona dal suo profilo memorizzato non appena riceve un
    /// evento di redraw. Utile solo per anteprima al volo.</summary>
    public bool UploadImage(int id, string imagePath, int buttonIndex) =>
        _helper.UploadImage(id, imagePath, buttonIndex);

    /// <summary>Carica un'immagine e la <b>salva</b> nello slot del profilo
    /// indicato (1..5) del firmware via SetIconPic. Da preferire all'<see cref="UploadImage"/>
    /// quando vogliamo che l'icona sopravviva a pressioni/redraw/cambi profilo.
    /// Il parametro <c>isAPEnable</c> passato all'SDK e' false: questo dice al
    /// firmware "scrivi nel mio storage, non solo nel display buffer".</summary>
    public bool UploadImageToProfile(int id, string imagePath, int buttonIndex, int profileIndex) =>
        _helper.UploadImageBySetIconPic(id, imagePath, buttonIndex, false, profileIndex);

    public void Dispose() => Close();

    // ---------- handler interni (reflection target) ----------
    //
    // Le firme dei delegate SDK (verificate dai metadati della DLL):
    //   DisplayPadStatus.Invoke         (int, int)            -> Plug
    //   DisplayPadKeyStatus.Invoke      (int, int, int)       -> Key
    //   DisplayPadProgressStatus.Invoke (int)                 -> Progress

    // Plug: i due int (a, b) non sono documentati esplicitamente nell'XML
    // doc dell'SDK. Empiricamente uno dovrebbe essere il device ID e
    // l'altro lo status (0=remove, 1=plug, 2=suspend). Riportiamo
    // entrambi e li chiamiamo "a" / "b" finche' non vediamo dal vivo
    // cosa sono; chi consuma l'evento puo' usarli direttamente.
    private void OnPlug(int a, int b)
    {
        DevicePlug?.Invoke(this, new DevicePlugEventArgs(
            arg0: a,
            arg1: b,
            status: b switch
            {
                0 => DevicePlugStatus.Removed,
                1 => DevicePlugStatus.Plugged,
                2 => DevicePlugStatus.Suspended,
                _ => DevicePlugStatus.Unknown
            }));
    }

    private void OnKey(int keyMatrix, int isPressed, int deviceId)
    {
        KeyEvent?.Invoke(this, new DisplayPadKeyEventArgs(
            deviceId: deviceId,
            keyMatrix: keyMatrix,
            pressed: isPressed == 1));
    }

    private void OnProgress(int percent)
    {
        FirmwareProgress?.Invoke(this, new FirmwareProgressEventArgs(
            percent: percent,
            failed: percent == -1));
    }

    // ---------- reflection helpers ----------

    // Gli eventi sono statici sulla classe DisplayPadHelper.
    private void AttachStaticEvent(string eventName, string handlerName, out Delegate? created)
    {
        created = null;
        var evt = typeof(DisplayPadHelper).GetEvent(
            eventName, BindingFlags.Static | BindingFlags.Public);
        if (evt is null)
        {
            App.WriteLog($"[AttachStaticEvent] event '{eventName}' not found");
            return;
        }
        var handlerType = evt.EventHandlerType
            ?? throw new InvalidOperationException($"{eventName}: missing EventHandlerType");
        var method = typeof(DisplayPadService).GetMethod(
            handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
        {
            App.WriteLog($"[AttachStaticEvent] handler '{handlerName}' not found");
            return;
        }
        try
        {
            created = Delegate.CreateDelegate(handlerType, this, method);
            evt.AddEventHandler(target: null, created);
            App.WriteLog($"[AttachStaticEvent] {eventName} <- {handlerName} ({handlerType.Name})");
        }
        catch (Exception ex)
        {
            App.WriteLog($"[AttachStaticEvent] FAIL {eventName} <- {handlerName}: {ex}");
            throw;
        }
    }

    private void DetachStaticEvent(string eventName, Delegate? handler)
    {
        if (handler is null) return;
        var evt = typeof(DisplayPadHelper).GetEvent(
            eventName, BindingFlags.Static | BindingFlags.Public);
        evt?.RemoveEventHandler(target: null, handler);
    }
}

public enum DevicePlugStatus { Unknown, Removed, Plugged, Suspended }

public sealed class DevicePlugEventArgs : EventArgs
{
    public DevicePlugEventArgs(int arg0, int arg1, DevicePlugStatus status)
    {
        Arg0 = arg0;
        Arg1 = arg1;
        Status = status;
    }
    /// <summary>Primo argomento del delegate SDK (deviceId o status, TBD).</summary>
    public int Arg0 { get; }
    /// <summary>Secondo argomento del delegate SDK.</summary>
    public int Arg1 { get; }
    /// <summary>Interpretazione di <see cref="Arg1"/> assumendo che sia lo status.</summary>
    public DevicePlugStatus Status { get; }
}

public sealed class DisplayPadKeyEventArgs : EventArgs
{
    public DisplayPadKeyEventArgs(int deviceId, int keyMatrix, bool pressed)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
    }
    public int DeviceId { get; }
    public int KeyMatrix { get; }
    public bool Pressed { get; }
}

public sealed class FirmwareProgressEventArgs : EventArgs
{
    public FirmwareProgressEventArgs(int percent, bool failed)
    {
        Percent = percent;
        Failed = failed;
    }
    public int Percent { get; }
    public bool Failed { get; }
}
