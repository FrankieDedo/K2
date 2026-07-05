using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Facade applicativa sopra <see cref="MacroPadSdkNative"/>.
///
/// Espone l'SDK nativo del MacroPad come una API .NET pulita: apertura/chiusura
/// driver, enumerazione device, lettura info firmware ed eventi tipizzati
/// (.NET <see cref="EventHandler{TEventArgs}"/>) per tasti, plug/unplug e
/// avanzamento update firmware.
///
/// Replica il ruolo che <c>DisplayPadService</c> ha nel modulo DisplayPad,
/// cosi' che il guscio unificato K2.App tratti ogni device allo stesso modo.
///
/// <para>
/// <b>Eventi.</b> I tasti arrivano via callback su un thread interno
/// dell'SDK; plug e progress arrivano come messaggi Windows sull'HWND passato
/// a <see cref="Open"/>. Il consumer deve inoltrare i messaggi della finestra
/// a <see cref="HandleWindowMessage"/> (tipicamente da un hook WndProc).
/// Tutti gli eventi possono quindi essere sollevati su un thread diverso da
/// quello UI: il gestore e' responsabile del marshalling.
/// </para>
/// </summary>
public sealed class MacroPadService : IDisposable
{
    // Il delegate va tenuto in un campo: se lo raccogliesse il GC, l'SDK
    // chiamerebbe un puntatore a funzione non piu' valido -> crash nativo.
    private MacroPadSdkNative.KEY_CALLBACK? _keyCallback;
    private bool _opened;

    /// <summary>Tasto del MacroPad premuto o rilasciato.</summary>
    public event EventHandler<MacroPadKeyEventArgs>? KeyEvent;

    /// <summary>Device collegato / scollegato (messaggio <c>WM_DEVICE_PLUG</c>).</summary>
    public event EventHandler<MacroPadPlugEventArgs>? DevicePlug;

    /// <summary>Avanzamento update firmware (messaggio <c>WM_FW_PROGRESS</c>).</summary>
    public event EventHandler<MacroPadProgressEventArgs>? FirmwareProgress;

    /// <summary>Slot massimi indirizzabili dall'SDK.</summary>
    public const int MaxDeviceCount = MacroPadSdkNative.MAX_DEV_COUNT;

    /// <summary>Tasti fisici del MacroPad.</summary>
    public const int ButtonCount = MacroPadSdkNative.FW_NUM_KEY;

    /// <summary>Profili memorizzati su ciascun device.</summary>
    public const int ProfileCount = MacroPadSdkNative.FW_NUM_PROFILE;

    /// <summary>True se il driver USB e' stato aperto con successo.</summary>
    public bool IsOpen => _opened;

    /// <summary>
    /// Apre il driver USB del MacroPad. <paramref name="hWnd"/> e' l'HWND della
    /// finestra che ricevera' i messaggi di plug/progress: deve essere lo
    /// stesso HWND il cui WndProc inoltra a <see cref="HandleWindowMessage"/>.
    /// Registra inoltre il callback dei tasti.
    /// </summary>
    public bool Open(IntPtr hWnd)
    {
        if (_opened) return true;

        // Il callback dei tasti e' globale (una sola registrazione per processo):
        // lo agganciamo prima di aprire il driver, come fa il worker originale.
        _keyCallback = OnKeyCallback;
        try
        {
            MacroPadSdkNative.SetKeyCallBack(_keyCallback);
            App.WriteLog("[MacroPad.Open] SetKeyCallBack registrato");
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.Open] SetKeyCallBack ha lanciato: " + ex);
        }

        App.WriteLog($"[MacroPad.Open] OpenUSBDriver(0x{hWnd.ToInt64():X})");
        bool ok;
        try
        {
            ok = MacroPadSdkNative.OpenUSBDriver(hWnd);
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.Open] OpenUSBDriver ha lanciato: " + ex);
            return false;
        }
        App.WriteLog($"[MacroPad.Open] -> {ok}");
        _opened = ok;
        return ok;
    }

    /// <summary>Chiude il driver USB.</summary>
    public void Close()
    {
        if (!_opened) return;
        try { MacroPadSdkNative.CloseUSBDriver(); }
        catch (Exception ex) { App.WriteLog("[MacroPad.Close] ha lanciato: " + ex); }
        _opened = false;
        // _keyCallback resta referenziato finche' il service e' vivo: l'SDK
        // potrebbe ancora avere il puntatore registrato.
        App.WriteLog("[MacroPad.Close] driver chiuso");
    }

    /// <summary>Cambia il profilo attivo del MacroPad. Chiama il native SwitchProfile(profile, 0, id).</summary>
    public bool SwitchProfile(uint deviceId, int profile)
    {
        try
        {
            bool ok = MacroPadSdkNative.SwitchProfile(profile, 0, deviceId);
            App.WriteLog($"[MacroPad.SwitchProfile] device={deviceId} profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SwitchProfile] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Versione della DLL nativa dell'SDK.</summary>
    public int SdkVersion()
    {
        try { return MacroPadSdkNative.GetDLLVersion(); }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SdkVersion] ha lanciato: " + ex);
            return 0;
        }
    }

    /// <summary>Numero di device riportato da <c>GetDevCount</c> (-1 se errore).</summary>
    public int DeviceCount()
    {
        int n = 0;
        try
        {
            bool ok = MacroPadSdkNative.GetDevCount(ref n);
            if (!ok) App.WriteLog("[MacroPad.DeviceCount] GetDevCount -> false");
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.DeviceCount] ha lanciato: " + ex);
            return -1;
        }
        return n;
    }

    /// <summary>
    /// Slot dei device realmente collegati. Sonda gli slot 1..<see cref="MaxDeviceCount"/>
    /// con <c>IsDevicePlug</c>. In questo primo step nessun filtro "phantom":
    /// il log riporta tutto cosi' da osservare il comportamento reale.
    /// </summary>
    public IReadOnlyList<uint> DeviceIds()
    {
        var found = new List<uint>();
        for (uint id = 1; id <= MaxDeviceCount; id++)
        {
            try
            {
                if (MacroPadSdkNative.IsDevicePlug(id))
                    found.Add(id);
            }
            catch (Exception ex)
            {
                App.WriteLog($"[MacroPad.DeviceIds] IsDevicePlug({id}) ha lanciato: {ex.Message}");
            }
        }
        App.WriteLog($"[MacroPad.DeviceIds] device collegati -> [{string.Join(", ", found)}]");
        return found;
    }

    /// <summary>True se sullo slot indicato c'e' un device.</summary>
    public bool IsPlugged(uint id)
    {
        try { return MacroPadSdkNative.IsDevicePlug(id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.IsPlugged] id={id} ha lanciato: {ex.Message}");
            return false;
        }
    }

    /// <summary>Versione applicativa del firmware del device.</summary>
    public ushort FirmwareVersion(uint id)
    {
        try { return MacroPadSdkNative.GetDevAppVer(id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.FirmwareVersion] id={id} ha lanciato: {ex.Message}");
            return 0;
        }
    }

    /// <summary>True se l'aggiornamento firmware e' in corso sul device.</summary>
    public bool IsUpdating(uint id)
    {
        try { return MacroPadSdkNative.IsUpdating(id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.IsUpdating] id={id} ha lanciato: {ex.Message}");
            return false;
        }
    }

    /// <summary>Legge <see cref="MacroPadSdkNative.DevInfo"/> (VID/PID/versioni).
    /// <c>internal</c>: espone un tipo del layer P/Invoke (anch'esso internal).</summary>
    internal bool TryGetDeviceInfo(uint id, out MacroPadSdkNative.DevInfo info)
    {
        info = default;
        try { return MacroPadSdkNative.GetDeviceInfo(ref info, id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.TryGetDeviceInfo] id={id} ha lanciato: {ex.Message}");
            return false;
        }
    }

    /// <summary>Legge <see cref="MacroPadSdkNative.FWInfo"/> (profilo/effetto correnti).
    /// <c>internal</c>: espone un tipo del layer P/Invoke (anch'esso internal).</summary>
    internal bool TryGetFirmwareInfo(uint id, out MacroPadSdkNative.FWInfo info)
    {
        info = default;
        try { return MacroPadSdkNative.GetFWInfo(ref info, id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.TryGetFirmwareInfo] id={id} ha lanciato: {ex.Message}");
            return false;
        }
    }

    /// <summary>Abilita/disabilita il controllo software (AP mode) del device.</summary>
    public bool APEnable(uint id, bool enable)
    {
        try
        {
            bool ok = MacroPadSdkNative.APEnable(enable, id);
            App.WriteLog($"[MacroPad.APEnable] id={id} enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.APEnable] id={id} ha lanciato: {ex.Message}");
            return false;
        }
    }

    // =======================================================================
    // Illuminazione LED (preset firmware)
    //
    // Replica la logica provata del modulo Everest, ma ogni chiamata nativa del
    // MacroPad prende un ultimo parametro `uint ID` = lo slot del device.
    // =======================================================================

    /// <summary>Preset di illuminazione: alias degli indici nativi firmware.</summary>
    public enum Effect : byte
    {
        Static    = (byte)MacroPadSdkNative.EffectIndex.Static,
        Breath    = (byte)MacroPadSdkNative.EffectIndex.Breath,
        Wave      = (byte)MacroPadSdkNative.EffectIndex.Wave,
        ReactiveA = (byte)MacroPadSdkNative.EffectIndex.ReactiveA,
        ReactiveB = (byte)MacroPadSdkNative.EffectIndex.ReactiveB,
        ReactiveC = (byte)MacroPadSdkNative.EffectIndex.ReactiveC,
        Yeti      = (byte)MacroPadSdkNative.EffectIndex.Yeti,
        Tornado   = (byte)MacroPadSdkNative.EffectIndex.Tornado,
        Matrix    = (byte)MacroPadSdkNative.EffectIndex.Matrix,
        Off       = (byte)MacroPadSdkNative.EffectIndex.Off,
    }

    /// <summary>Effect speed.</summary>
    public enum Speed : byte { Slow = 0, Normal = 1, Fast = 2 }

    /// <summary>Senso di rotazione/scorrimento.</summary>
    public enum Direction : byte { ClockWise = 0, CounterClockWise = 1 }

    /// <summary>
    /// Applica un preset di illuminazione allo slot device indicato.
    /// <para>As with the Everest: <c>ChangeEffect</c> requires the device to be in
    /// NORMALE (non AP), quindi si forza <c>APEnable(false)</c> prima; dopo si
    /// fa <c>SaveFlash</c> per rendere il preset persistente sullo slot.</para>
    /// </summary>
    public bool SetEffect(uint id, Effect effect,
                          (byte r, byte g, byte b) primary,
                          (byte r, byte g, byte b)? secondary = null,
                          (byte r, byte g, byte b)? tertiary = null,
                          (byte r, byte g, byte b)? background = null,
                          Speed speed = Speed.Normal,
                          int brightness = 100,
                          bool randomColor = false)
    {
        // ChangeEffect e' un preset firmware: il device lo memorizza e lo
        // disegna dal proprio runtime. AP mode (SW mode) e' solo per lo
        // streaming per-key, quindi va spento prima del comando.
        try
        {
            bool offOk = MacroPadSdkNative.APEnable(false, id);
            App.WriteLog($"[MacroPad.SetEffect] APEnable(false,id={id}) prep -> {offOk}");
        }
        catch (Exception ex2) { App.WriteLog("[MacroPad.SetEffect] APEnable(false) prep ha lanciato: " + ex2); }

        MacroPadSdkNative.FWColor C((byte, byte, byte) c) => new(c.Item1, c.Item2, c.Item3);
        var bright = QuantizeBrightness(brightness);
        var data = MacroPadSdkNative.EffData.New(
            eff:        (MacroPadSdkNative.EffectIndex)effect,
            c1:         C(primary),
            c2:         secondary is { } s ? C(s) : null,
            c3:         tertiary  is { } t ? C(t) : null,
            background: background is { } bg ? C(bg) : null,
            speed:      (MacroPadSdkNative.SpeedT)speed,
            bright:     bright,
            randomColor: randomColor);
        try
        {
            bool ok = MacroPadSdkNative.ChangeEffect(data, id);
            App.WriteLog($"[MacroPad.SetEffect] id={id} eff={effect} speed={speed} bright={bright} -> {ok}");
            App.WriteLog("[MacroPad.SetEffect] DUMP EffData(62B): " + DumpEffData(data));

            try
            {
                bool flashOk = MacroPadSdkNative.SaveFlash(6, id); // 6 = ALL_PROFILE
                App.WriteLog($"[MacroPad.SetEffect] SaveFlash(ALL,id={id}) (commit) -> {flashOk}");
            }
            catch (Exception ex2) { App.WriteLog("[MacroPad.SetEffect] SaveFlash ha lanciato: " + ex2); }

            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SetEffect] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Hex-dump dei 62 byte della struct (diagnostica).</summary>
    private static string DumpEffData(MacroPadSdkNative.EffData d)
    {
        int sz = Marshal.SizeOf<MacroPadSdkNative.EffData>();
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(d, p, fDeleteOld: false);
            byte[] buf = new byte[sz];
            Marshal.Copy(p, buf, 0, sz);
            return $"{sz}B = " + BitConverter.ToString(buf);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Resetta gli effetti dello slot al default firmware.</summary>
    public bool ResetEffects(uint id)
    {
        try
        {
            bool ok = MacroPadSdkNative.ResetEffects(id);
            App.WriteLog($"[MacroPad.ResetEffects] id={id} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.ResetEffects] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Attiva/disattiva la sincronizzazione dell'effetto su tutti i profili.</summary>
    public bool SetSyncAcrossProfiles(uint id, bool enable)
    {
        try
        {
            bool ok = MacroPadSdkNative.SetSyncAcrossProfiles(enable, id);
            App.WriteLog($"[MacroPad.SetSyncAcrossProfiles] id={id} enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SetSyncAcrossProfiles] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Legge lo stato corrente del sync cross-profilo dello slot.</summary>
    public bool GetSyncAcrossProfiles(uint id)
    {
        try
        {
            bool enabled = false;
            return MacroPadSdkNative.GetSyncAcrossProfiles(ref enabled, id) && enabled;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.GetSyncAcrossProfiles] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Salva sul flash lo stato corrente. Profilo 1..5 o 6 = ALL_PROFILE.</summary>
    public bool SaveFlash(uint id, int profile = 6)
    {
        try
        {
            bool ok = MacroPadSdkNative.SaveFlash(profile, id);
            App.WriteLog($"[MacroPad.SaveFlash] id={id} profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SaveFlash] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Turns the slot backlight on/off ("main" brightness).</summary>
    public bool SetBacklight(uint id, bool on)
    {
        try
        {
            bool ok = MacroPadSdkNative.SetMainBrightness(on, id);
            App.WriteLog($"[MacroPad.SetBacklight] id={id} on={on} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SetBacklight] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Quantizes 0..100 to the 5 firmware brightness steps (0/25/50/75/100):
    /// il firmware accetta solo questi valori.
    /// </summary>
    private static MacroPadSdkNative.BrightT QuantizeBrightness(int pct)
    {
        if (pct <= 12) return MacroPadSdkNative.BrightT.B0;
        if (pct <= 37) return MacroPadSdkNative.BrightT.B25;
        if (pct <= 62) return MacroPadSdkNative.BrightT.B50;
        if (pct <= 87) return MacroPadSdkNative.BrightT.B75;
        return MacroPadSdkNative.BrightT.B100;
    }

    /// <summary>
    /// Va invocata dal WndProc della finestra che ha passato il proprio HWND a
    /// <see cref="Open"/>. Traduce i messaggi <c>WM_DEVICE_PLUG</c> e
    /// <c>WM_FW_PROGRESS</c> negli eventi .NET corrispondenti.
    /// </summary>
    public void HandleWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case MacroPadSdkNative.WM_DEVICE_PLUG:
            {
                int w = wParam.ToInt32();
                int l = lParam.ToInt32();
                App.WriteLog($"[MacroPad.WM_DEVICE_PLUG] wParam={w} lParam={l}");
                DevicePlug?.Invoke(this, new MacroPadPlugEventArgs(w, l));
                break;
            }
            case MacroPadSdkNative.WM_FW_PROGRESS:
            {
                int percent = wParam.ToInt32();
                App.WriteLog($"[MacroPad.WM_FW_PROGRESS] percent={percent}");
                FirmwareProgress?.Invoke(this, new MacroPadProgressEventArgs(percent));
                break;
            }
        }
    }

    public void Dispose() => Close();

    // ---- callback nativo (thread dell'SDK) ----------------------------------

    private void OnKeyCallback(ushort wMatrix, bool bPressed, uint id)
    {
        try
        {
            App.WriteLog($"[MacroPad.Key] id={id} matrix={wMatrix} pressed={bPressed}");
            KeyEvent?.Invoke(this, new MacroPadKeyEventArgs(id, wMatrix, bPressed));
        }
        catch (Exception ex)
        {
            // Mai propagare un'eccezione gestita verso codice nativo.
            App.WriteLog("[MacroPad.OnKeyCallback] ha lanciato: " + ex);
        }
    }
}

/// <summary>Argomenti dell'evento <see cref="MacroPadService.KeyEvent"/>.</summary>
public sealed class MacroPadKeyEventArgs : EventArgs
{
    public MacroPadKeyEventArgs(uint deviceId, ushort keyMatrix, bool pressed)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
    }

    /// <summary>Slot del device che ha generato l'evento.</summary>
    public uint DeviceId { get; }

    /// <summary>Indice di matrice del tasto (indice fisico del firmware).</summary>
    public ushort KeyMatrix { get; }

    /// <summary>True = premuto, false = rilasciato.</summary>
    public bool Pressed { get; }
}

/// <summary>Argomenti dell'evento <see cref="MacroPadService.DevicePlug"/>.</summary>
public sealed class MacroPadPlugEventArgs : EventArgs
{
    public MacroPadPlugEventArgs(int wParam, int lParam)
    {
        WParam = wParam;
        LParam = lParam;
    }

    /// <summary>wParam grezzo del messaggio <c>WM_DEVICE_PLUG</c>.</summary>
    public int WParam { get; }

    /// <summary>lParam grezzo del messaggio <c>WM_DEVICE_PLUG</c>.</summary>
    public int LParam { get; }
}

/// <summary>Argomenti dell'evento <see cref="MacroPadService.FirmwareProgress"/>.</summary>
public sealed class MacroPadProgressEventArgs : EventArgs
{
    public MacroPadProgressEventArgs(int percent) => Percent = percent;

    /// <summary>Percentuale di avanzamento update firmware (-1 = fallito).</summary>
    public int Percent { get; }

    /// <summary>True se l'update e' fallito.</summary>
    public bool Failed => Percent == -1;
}
