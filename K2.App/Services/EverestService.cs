using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// Facade applicativa sopra <see cref="EverestSdkNative"/>.
///
/// Espone l'SDK nativo della tastiera Everest Max come una API .NET pulita:
/// apertura/chiusura driver, info device/firmware, AP mode, cambio profilo ed
/// evento tipizzato per i tasti.
///
/// Mirrors the role of <c>MacroPadService</c>, but simpler: the Everest is
/// single-device (niente enumerazione di slot) e non usa messaggi Windows per
/// il plug — lo stato si interroga con <see cref="IsPlugged"/>.
///
/// <para>I tasti arrivano via callback su un thread interno dell'SDK: il
/// consumer of the <see cref="KeyEvent"/> event is responsible for marshalling
/// verso il thread UI.</para>
/// </summary>
public sealed class EverestService : IDisposable
{
    // Il delegate va tenuto vivo in un campo: se lo raccogliesse il GC, l'SDK
    // would call a dangling function pointer -> native crash.
    private EverestSdkNative.KEY_CALLBACK? _keyCallback;
    private bool _opened;

    // ---- Motore nativo (opt-in, AppSettings.EverestNativeEngine) ----------
    // Fase 1: bypassa SDKDLL.dll SOLO per apertura/chiusura driver + init +
    // i 4 tasti display del numpad (D1-D4). RGB/icone numpad/Media Dock e la
    // matrice completa a 171 tasti (usata dal motore di remap di K2) restano
    // su SDKDLL.dll finché non arrivano le fasi successive (layout wire non
    // ancora confermato per queste — vedi EverestHidNative.cs). Con il flag
    // attivo, quelle chiamate falliscono semplicemente (SDKDLL non è aperta)
    // invece di crashare: sono già tutte in try/catch con log.
    private EverestHidNative.Pad? _nativePad;
    private bool UseNativeEngine => K2.Core.AppSettings.EverestNativeEngine;

    /// <summary>Tasto display numpad (D1-D4) premuto/rilasciato — SOLO motore nativo.
    /// Popolato solo quando <see cref="UseNativeEngine"/> è true (vedi Open()).</summary>
    public event EventHandler<(int Button, bool Pressed)>? NumpadButtonEvent;

    // Profilo corrente cachato dall'init: evitiamo di chiamare GetFWInfo
    // repeatedly (each call is a HID packet that may collide with
    // polling interno della DLL → crash nativo 0xC0000005 a +0x5133).
    private int _cachedProfile = 1;

    // Lock globale per serializzare tutte le chiamate a SDKDLL.dll.
    // The DLL is not thread-safe: the key callback arrives on a thread
    // dell'SDK, le chiamate UI dal dispatcher WPF → accesso concorrente
    // → access violation (crash nativo 0xC0000005 a SDKDLL.dll+0x5133).
    private readonly object _sdkLock = new();

    // SaveFlash DEBOUNCED: if the user changes effect/speed rapidly,
    // annulla il SaveFlash precedente e ne programma uno nuovo. Evita di
    // inondare la coda HID della DLL con comandi ravvicinati → crash.
    private CancellationTokenSource? _saveFlashCts;

    /// <summary>Tasto della tastiera premuto o rilasciato.</summary>
    public event EventHandler<EverestKeyEventArgs>? KeyEvent;

    /// <summary>Profili memorizzati sulla tastiera.</summary>
    public const int ProfileCount = EverestSdkNative.FW_NUM_PROFILE;

    /// <summary>True if the USB driver was opened successfully and the DLL has not crashed.</summary>
    public bool IsOpen => _opened && !App.SdkCrashRecoveryNeeded;

    /// <summary>
    /// Apre il driver USB della tastiera e registra il callback dei tasti.
    /// </summary>
    public bool Open()
    {
        if (_opened) return true;

        if (UseNativeEngine)
            return OpenNative();

        _keyCallback = OnKeyCallback;
        try
        {
            EverestSdkNative.SetKeyCallBack(_keyCallback);
            App.WriteLog("[Everest.Open] SetKeyCallBack registrato");
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.Open] SetKeyCallBack ha lanciato: " + ex);
        }

        try
        {
            _opened = EverestSdkNative.OpenUSBDriver();
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.Open] OpenUSBDriver ha lanciato: " + ex);
            return false;
        }
        App.WriteLog($"[Everest.Open] OpenUSBDriver -> {_opened}");

        // Inizializzazione post-apertura: Base Camp chiama GetFWInfo,
        // GetProfileEffectTable, GetExtendInfo, EnableKeyFunc subito dopo
        // OpenUSBDriver. Queste letture hanno side-effect interni nella DLL
        // che mettono lo stato in "pronto per effetti". Senza di esse,
        // ChangeEffect/ChangeBlockEffect ritornano True ma NON emettono
        // pacchetti 14 2C sul bus USB (confermato via USB sniff 2026-06-05:
        // polling DLL mostra 0x1C senza init vs 0x2B con init di BC).
        if (_opened) InitDllState();

        return _opened;
    }

    /// <summary>
    /// Apertura via motore nativo (Fase 1, vedi commento sul campo <see cref="_nativePad"/>).
    /// SDKDLL.dll non viene MAI caricata in questo percorso: elimina alla radice il
    /// crash del suo thread timer per tutto ciò che il motore nativo copre.
    /// </summary>
    private bool OpenNative()
    {
        try
        {
            string? path = EverestHidNative.FindCommandInterfacePath(App.WriteLog);
            if (path is null)
            {
                App.WriteLog("[Everest.Open] (native) MI_03 non trovata — tastiera non collegata?");
                return false;
            }
            var pad = new EverestHidNative.Pad(path, App.WriteLog);
            pad.Open();
            pad.NumpadButtonChanged += (btn, pressed) =>
                NumpadButtonEvent?.Invoke(this, (btn, pressed));
            _nativePad = pad;
            _opened = true;
            App.WriteLog("[Everest.Open] (native) OK — SDKDLL.dll non caricata");
            return true;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.Open] (native) ha lanciato: " + ex);
            _nativePad?.Dispose();
            _nativePad = null;
            return false;
        }
    }

    /// <summary>
    /// Replica le chiamate di inizializzazione che Base Camp fa dopo
    /// OpenUSBDriver. Anche se non usiamo i dati restituiti, i side-effect
    /// interni della DLL preparano lo stato per ChangeEffect/ChangeBlockEffect.
    /// </summary>
    private void InitDllState()
    {
        try
        {
            var fwInfo = new EverestSdkNative.FWInfo();
            bool fi = EverestSdkNative.GetFWInfo(ref fwInfo);
            if (fi && fwInfo.currentlyProfileIndex >= 1)
                _cachedProfile = fwInfo.currentlyProfileIndex;
            App.WriteLog($"[Everest.Init] GetFWInfo -> {fi}  " +
                $"fwVer=0x{fwInfo.fwVer:X4} profile={fwInfo.currentlyProfileIndex} " +
                $"effectMode={fwInfo.byEffectModeIndex} effectMenu={fwInfo.byEffectMenuIndex}" +
                $" -> cachedProfile={_cachedProfile}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetFWInfo ha lanciato: " + ex); }

        try
        {
            var effectMenu = new EverestSdkNative.EffectMenu();
            bool em = EverestSdkNative.GetProfileEffectTable(ref effectMenu);
            App.WriteLog($"[Everest.Init] GetProfileEffectTable -> {em}  " +
                $"profileSize={effectMenu.byProfileSize} effectSize={effectMenu.byEffectSize}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetProfileEffectTable ha lanciato: " + ex); }

        try
        {
            var extInfo = new EverestSdkNative.FW_EXTEND_INFO();
            bool ei = EverestSdkNative.GetExtendInfo(ref extInfo);
            App.WriteLog($"[Everest.Init] GetExtendInfo -> {ei}  " +
                $"MMDockPlug={extInfo.byMMDockPlug} NumpadPlug={extInfo.byNumpadPlug}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetExtendInfo ha lanciato: " + ex); }

        // GetFWLayout (HID 11 12): BC la chiama 2 volte durante l'init.
        // From reverse engineering SDKDLL.dll, this is the only function that produces
        // il sub-command 0x12. Senza questa, GetColorData non funziona
        // on a clean boot (without BC having already called it).
        try
        {
            int layout = 0;
            bool fl = EverestSdkNative.GetFWLayout(ref layout);
            App.WriteLog($"[Everest.Init] GetFWLayout -> {fl}  layout={layout}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetFWLayout ha lanciato: " + ex); }

        try
        {
            bool ek = EverestSdkNative.EnableKeyFunc(true);
            App.WriteLog($"[Everest.Init] EnableKeyFunc(true) -> {ek}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] EnableKeyFunc ha lanciato: " + ex); }

        // Forza il firmware fuori da AP mode (potrebbe essere rimasto in AP
        // from a previous K2/BC session). Without this, ChangeEffect may
        // causare un flash arcobaleno transitorio prima dell'effetto.
        try
        {
            bool ap = EverestSdkNative.APEnable(false);
            _apEnabled = false;
            App.WriteLog($"[Everest.Init] APEnable(false) -> {ap}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] APEnable(false) ha lanciato: " + ex); }
    }

    /// <summary>Chiude il driver USB.</summary>
    public void Close()
    {
        if (!_opened) return;
        if (_nativePad is not null)
        {
            try { _nativePad.Dispose(); }
            catch (Exception ex) { App.WriteLog("[Everest.Close] (native) ha lanciato: " + ex); }
            _nativePad = null;
            _opened = false;
            App.WriteLog("[Everest.Close] (native) driver chiuso");
            return;
        }
        try { EverestSdkNative.CloseUSBDriver(); }
        catch (Exception ex) { App.WriteLog("[Everest.Close] ha lanciato: " + ex); }
        _opened = false;
        // AP mode si perde quando il driver si chiude: la prossima Open
        // will need to re-enable it.
        _apEnabled = false;
        App.WriteLog("[Everest.Close] driver chiuso");
    }

    /// <summary>Versione della DLL nativa dell'SDK.</summary>
    public int SdkVersion()
    {
        if (_nativePad is not null) return -1; // native engine: SDKDLL.dll not loaded
        lock (_sdkLock)
        try { return EverestSdkNative.GetDLLVersion(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SdkVersion] ha lanciato: " + ex);
            return 0;
        }
    }

    /// <summary>True if the keyboard is connected.</summary>
    public bool IsPlugged()
    {
        if (_nativePad is not null) return _opened;
        lock (_sdkLock)
        try { return EverestSdkNative.IsDevicePlug(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.IsPlugged] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Versione applicativa del firmware.</summary>
    public ushort FirmwareVersion()
    {
        lock (_sdkLock)
        try { return EverestSdkNative.GetDevAppVer(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.FirmwareVersion] ha lanciato: " + ex);
            return 0;
        }
    }

    /// <summary>Legge VID/PID e versioni del device.
    /// <c>internal</c>: espone un tipo del layer P/Invoke (anch'esso internal).</summary>
    internal bool TryGetDeviceInfo(out EverestSdkNative.DevInfo info)
    {
        info = default;
        lock (_sdkLock)
        try { return EverestSdkNative.GetDeviceInfo(ref info); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetDeviceInfo] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Legge lo stato firmware (profilo/effetto correnti).
    /// <c>internal</c>: espone un tipo del layer P/Invoke (anch'esso internal).</summary>
    internal bool TryGetFirmwareInfo(out EverestSdkNative.FWInfo info)
    {
        info = default;
        lock (_sdkLock)
        try { return EverestSdkNative.GetFWInfo(ref info); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetFirmwareInfo] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Profilo attualmente attivo sul firmware (1..ProfileCount), 0 se ignoto.</summary>
    public int CurrentProfile()
    {
        return TryGetFirmwareInfo(out var fw) ? fw.currentlyProfileIndex : 0;
    }

    /// <summary>
    /// Abilita/disabilita il controllo software (AP mode). Aggiorna il
    /// flag interno: una <see cref="EnsureApMode"/> successiva sa che deve
    /// riemettere il comando se l'utente ha disabilitato AP manualmente.
    /// </summary>
    public bool APEnable(bool enable)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.APEnable(enable);
            App.WriteLog($"[Everest.APEnable] enable={enable} -> {ok}");
            if (ok) _apEnabled = enable;
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.APEnable] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Reset del device.</summary>
    public bool ResetDevice()
    {
        lock (_sdkLock)
        try { return EverestSdkNative.ResetDevice(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetDevice] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Cambia il profilo attivo della tastiera. Il secondo parametro nativo di
    /// <c>SwitchProfile</c> is not confirmed by metadata: since the keyboard
    /// single-device si passa 0. Da verificare su hardware.
    /// </summary>
    public bool SwitchProfile(int profile)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SwitchProfile(profile, 0);
            App.WriteLog($"[Everest.SwitchProfile] profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SwitchProfile] ha lanciato: " + ex);
            return false;
        }
    }

    // ---- AP / SW mode ------------------------------------------------------

    /// <summary>
    /// True dopo la prima <see cref="EnsureApMode"/> riuscita: ricordiamo di
    /// non riemettere il comando ogni volta (sarebbe innocuo ma rumoroso nei log).
    /// </summary>
    private bool _apEnabled;

    /// <summary>
    /// Puts the keyboard in AP/SW mode (software control). Required
    /// because <c>ChangeEffect</c> and other lighting commands
    /// "soft" applicati dal PC siano accettati dal firmware. <c>EnableKeyFunc(true)</c>
    /// si chiama subito dopo per non perdere la funzione tasti durante AP.
    /// </summary>
    public bool EnsureApMode()
    {
        if (_apEnabled) return true;
        lock (_sdkLock)
        try
        {
            bool ap = EverestSdkNative.APEnable(true);
            // EnableKeyFunc(true) replica il comportamento di Base Camp: senza
            // Without this, in AP mode the keyboard may stop transmitting keys.
            bool keyFn = false;
            try { keyFn = EverestSdkNative.EnableKeyFunc(true); }
            catch (Exception ex2) { App.WriteLog("[Everest.EnsureApMode] EnableKeyFunc ha lanciato: " + ex2); }

            App.WriteLog($"[Everest.EnsureApMode] APEnable={ap}  EnableKeyFunc={keyFn}");
            _apEnabled = ap;
            return ap;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.EnsureApMode] ha lanciato: " + ex);
            return false;
        }
    }

    // ---- Illuminazione RGB (preset firmware) -------------------------------

    /// <summary>Preset di illuminazione: alias degli enum nativi.</summary>
    public enum Effect : byte
    {
        Static    = (byte)EverestSdkNative.EffectIndex.Static,
        Breath    = (byte)EverestSdkNative.EffectIndex.Breath,
        Wave      = (byte)EverestSdkNative.EffectIndex.Wave,
        ReactiveA = (byte)EverestSdkNative.EffectIndex.ReactiveA,
        ReactiveB = (byte)EverestSdkNative.EffectIndex.ReactiveB,
        ReactiveC = (byte)EverestSdkNative.EffectIndex.ReactiveC,
        Yeti      = (byte)EverestSdkNative.EffectIndex.Yeti,
        Tornado   = (byte)EverestSdkNative.EffectIndex.Tornado,
        Matrix    = (byte)EverestSdkNative.EffectIndex.Matrix,
        Off       = (byte)EverestSdkNative.EffectIndex.Off,
        /// <summary>Variante Matrix: stesso firmware index (9) ma con
        /// byRandColor=16 → linee verticali random del colore 2.</summary>
        Matrix2   = 200,
    }

    /// <summary>Effect speed.</summary>
    public enum Speed : byte { Slow = 0, Normal = 1, Fast = 2 }

    /// <summary>Senso di rotazione/scorrimento.</summary>
    public enum Direction : byte { ClockWise = 0, CounterClockWise = 1 }

    /// <summary>
    /// Applica un preset di illuminazione alla tastiera.
    /// <para>NOTA — i parametri <c>direction</c> e <c>width</c> sono stati
    /// rimossi: il dump CIL di <c>MacroPadSDK::getChangeEffect</c> di Base Camp
    /// mostra che <c>byDirection</c> e <c>byWidth</c> vengono sempre forzati a
    /// 255 and the CW/CCW direction is encoded in <c>EffMenuIndex</c> (see
    /// <see cref="EverestSdkNative.EffData.New"/>).</para>
    /// </summary>
    /// <param name="effect">Preset di firmware (Wave/Breath/Static/...).</param>
    /// <param name="primary">Colore principale (R,G,B).</param>
    /// <param name="secondary">Colore secondario (opzionale, usato dai preset multicolor).</param>
    /// <param name="tertiary">Terzo colore (opzionale).</param>
    /// <param name="background">Colore di background (opzionale, default nero).</param>
    /// <param name="speed">Animation speed.</param>
    /// <param name="brightness">Brightness 0..100 (mapped to firmware steps 0/25/50/75/100).</param>
    /// <param name="randomColor">true per ignorare i colori e usare colori casuali.</param>
    public bool SetEffect(Effect effect,
                          (byte r, byte g, byte b) primary,
                          (byte r, byte g, byte b)? secondary = null,
                          (byte r, byte g, byte b)? tertiary = null,
                          (byte r, byte g, byte b)? background = null,
                          Speed speed = Speed.Normal,
                          int brightness = 100,
                          bool randomColor = false,
                          int speedByte = -1,
                          int directionByte = -1,
                          int colorCountOverride = -1)
    {
      lock (_sdkLock)
      {
        // 2026-05-29 — TEST IPOTESI: AP mode era SBAGLIATO. AP mode (= Software
        // mode) e' solo per ChangeSWEffect / per-key streaming, dove l'host PC
        // spedisce ogni frame i 171 colori al firmware. Per i preset firmware
        // (ChangeEffect) il device DEVE essere in modalita' NORMALE: il
        // firmware riceve un EffData, lo memorizza nello slot corrente e lo
        // disegna lui dal suo runtime. Se entriamo in AP mode prima del
        // ChangeEffect, il firmware "ascolta" il comando ma non lo applica
        // perche' aspetta che noi pilotiamo i singoli LED.
        //
        // Quindi: NIENTE AP mode attorno a ChangeEffect. Se il device era
        // gia' in AP da una sessione precedente lo forziamo OFF prima.
        if (_apEnabled)
        {
            try
            {
                bool offOk = EverestSdkNative.APEnable(false);
                App.WriteLog($"[Everest.SetEffect] forzo APEnable(false) prima del ChangeEffect -> {offOk}");
                _apEnabled = false;
            }
            catch (Exception ex2) { App.WriteLog("[Everest.SetEffect] APEnable(false) prep ha lanciato: " + ex2); }
        }

        EverestSdkNative.FWColor C((byte, byte, byte) c) => new(c.Item1, c.Item2, c.Item3);
        var bright = QuantizeBrightness(brightness);

        // Parametri per-effetto dalla config esterna (everest_rgb.json), riletta
        // ad OGNI apply: si possono regolare byAll/bySpeed/byDirection/byWidth/
        // numero colori e ri-applicare l'effetto SENZA ricompilare.
        var def = EverestRgbConfig.Load().For(effect.ToString());
        App.WriteLog($"[Everest.SetEffect] cfg {effect}: byAll={def.ByAll} bySpeed={def.BySpeed} " +
                     $"byDir={def.ByDirection} byWidth={def.ByWidth} rand={def.ByRandColor} colors={def.ColorCount}");

        // La UI ha la precedenza (override >= 0); altrimenti si usa la config.
        int effSpeed = speedByte      >= 0 ? speedByte      : def.BySpeed;
        int effDir   = directionByte  >= 0 ? directionByte  : def.ByDirection;
        int effCount = colorCountOverride >= 0 ? colorCountOverride : def.ColorCount;

        // Wave(4) e Tornado(7) sono "block effect": ChangeEffect li RIFIUTA
        // (scoperto via USB sniff 2026-05-30). Vanno via ChangeBlockEffect,
        // con struct BlockData (byBlockNum + colori FWBColor pos+rgb).
        if (effect == Effect.Wave || effect == Effect.Tornado)
        {
            bool rainbowB = randomColor || def.ByRandColor != 0;
            // bySpeed: scala 0..100 (0=lento, 100=veloce) sia per block sia non-block.
            // La UI manda direttamente 0/25/50/75/100 (5 posizioni).
            // Se il JSON ha bySpeed >= 0 lo usa come override.
            byte spdB = (byte)(effSpeed >= 0 ? Math.Clamp(effSpeed, 0, 100) : 50);
            byte dirB     = (byte)(effDir >= 0 ? effDir : 0);
            EverestSdkNative.FWColor? c2b = null;
            if (secondary is { } s2) c2b = C(s2);

            var block = EverestSdkNative.BlockData.New(
                eff:       (EverestSdkNative.EffectIndex)effect,
                direction: dirB,
                speed:     spdB,
                lightness: (byte)bright,
                c1:        C(primary),
                c2:        c2b,
                rainbow:   rainbowB);
            try
            {
                // Dump hex diagnostico della struct PRIMA dell'invio
                App.WriteLog("[Everest.SetEffect] DUMP BlockData(62B): " + DumpBlockData(block));
                bool okB = EverestSdkNative.ChangeBlockEffect(block);
                App.WriteLog($"[Everest.SetEffect] BLOCK eff={effect} dir={dirB} speed={spdB} " +
                             $"rainbow={rainbowB} -> {okB}  (P/Invoke by-value)");
                if (!okB)
                {
                    App.WriteLog("[Everest.SetEffect] P/Invoke ha ritornato False, provo Raw...");
                    okB = EverestSdkNative.ChangeBlockEffectRaw(block);
                    App.WriteLog($"[Everest.SetEffect] ChangeBlockEffectRaw fallback -> {okB}");
                }
                // Piccolo delay per dare tempo alla coda HID interna della DLL
                // di processare il comando prima che arrivi SaveFlash.
                Thread.Sleep(50);
                DebouncedSaveFlash();
                return okB;
            }
            catch (Exception exB)
            {
                App.WriteLog("[Everest.SetEffect] ChangeBlockEffect ha lanciato: " + exB);
                return false;
            }
        }

        // Matrix2 (enum 200) → stesso firmware index di Matrix (9)
        // ma con forceRandColor16 per la variante visiva.
        bool isMatrix2 = effect == Effect.Matrix2;
        var fwIndex = isMatrix2
            ? EverestSdkNative.EffectIndex.Matrix
            : (EverestSdkNative.EffectIndex)effect;

        var data = EverestSdkNative.EffData.New(
            eff:              fwIndex,
            c1:               C(primary),
            c2:               secondary is { } s ? C(s) : null,
            c3:               tertiary  is { } t ? C(t) : null,
            background:       background is { } bg ? C(bg) : null,
            speed:            (EverestSdkNative.SpeedT)speed,
            bright:           bright,
            randomColor:      randomColor || def.ByRandColor != 0,
            byAll:            (byte)def.ByAll,
            byDirection:      (byte)effDir,
            byWidth:          (byte)def.ByWidth,
            colorCount:       effCount,
            speedOverride:    effSpeed,
            forceRandColor16: isMatrix2);
        try
        {
            bool ok = EverestSdkNative.ChangeEffect(data);
            App.WriteLog($"[Everest.SetEffect] eff={effect} speed={speed} bright={bright} -> {ok}");
            App.WriteLog("[Everest.SetEffect] DUMP EffData(62B): " + DumpEffData(data));

            Thread.Sleep(50);
            DebouncedSaveFlash();

            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetEffect] ha lanciato: " + ex);
            return false;
        }
      } // lock (_sdkLock)
    }

    /// <summary>
    /// Programma un SaveFlash con debounce: annulla l'eventuale timer
    /// precedente e ne crea uno nuovo a 300ms. Se l'utente cambia effetto
    /// or speed rapidly, only one SaveFlash is sent at the end
    /// della raffica — evita di sovraccaricare la coda HID della DLL.
    /// </summary>
    private void DebouncedSaveFlash()
    {
        _saveFlashCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveFlashCts = cts;
        var profile = _cachedProfile;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (TaskCanceledException) { return; }

            lock (_sdkLock)
            {
                try
                {
                    bool ok = EverestSdkNative.SaveFlash(profile);
                    App.WriteLog($"[Everest] SaveFlash({profile}) debounced -> {ok}");

                    // (2026-06-09: rimossa ri-attivazione color stream post-SaveFlash
                    //  because it caused flickering. To investigate whether SaveFlash
                    //  effettivamente interrompe il color stream.)
                }
                catch (Exception ex) { App.WriteLog("[Everest] SaveFlash ha lanciato: " + ex); }
            }
        });
    }


    /// <summary>Hex-dump dei 62 byte di BlockData (diagnostica).</summary>
    private static unsafe string DumpBlockData(EverestSdkNative.BlockData d)
    {
        int sz = sizeof(EverestSdkNative.BlockData);
        byte* src = (byte*)&d;
        var sb = new System.Text.StringBuilder(sz * 3 + 10);
        sb.Append($"{sz}B = ");
        for (int i = 0; i < sz; i++)
        {
            if (i > 0) sb.Append('-');
            sb.Append(src[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Hex-dump dei 62 byte della struct (diagnostica).</summary>
    private static string DumpEffData(EverestSdkNative.EffData d)
    {
        int sz = Marshal.SizeOf<EverestSdkNative.EffData>();
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

    /// <summary>Resetta gli effetti al default firmware.</summary>
    public bool ResetEffects()
    {
        try
        {
            bool ok = EverestSdkNative.ResetEffects();
            App.WriteLog($"[Everest.ResetEffects] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetEffects] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Attiva/disattiva la sincronizzazione dell'effetto su tutti i profili.
    /// When active, applying an effect to one profile replicates it
    /// sugli altri quattro.
    /// </summary>
    public bool SetSyncAcrossProfiles(bool enable)
    {
        try
        {
            bool ok = EverestSdkNative.SetSyncAcrossProfiles(enable);
            App.WriteLog($"[Everest.SetSyncAcrossProfiles] enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetSyncAcrossProfiles] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Legge lo stato corrente del sync cross-profilo.</summary>
    public bool GetSyncAcrossProfiles()
    {
        try
        {
            bool enabled = false;
            return EverestSdkNative.GetSyncAcrossProfiles(ref enabled) && enabled;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.GetSyncAcrossProfiles] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Imposta il sync effect (HID 12 [sync] 00 00 [brightness]).
    /// Necessario per abilitare il color stream su boot pulito.
    /// </summary>
    public bool SetSyncEffect(bool sync, int brightness)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetSyncEffect(sync, brightness);
            App.WriteLog($"[Everest.SetSyncEffect] sync={sync} bright={brightness} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetSyncEffect] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Salva sul flash della tastiera lo stato corrente (effetti/colori).
    /// Profilo 1..5 oppure 6 = ALL_PROFILE. Senza una SaveFlash gli effetti
    /// applicati via AP-mode si perdono al prossimo unplug.
    /// </summary>
    public bool SaveFlash(int profile = 6)
    {
        try
        {
            bool ok = EverestSdkNative.SaveFlash(profile);
            App.WriteLog($"[Everest.SaveFlash] profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SaveFlash] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Legge i colori LED correnti dalla tastiera, con lock non-bloccante.
    /// If the SDK lock is busy (another operation in progress), returns false
    /// without blocking — the poller can skip a tick with no visible impact.
    /// </summary>
    internal bool TryGetColorData(ref EverestSdkNative.KEYBOARD_COLOR buf)
    {
        if (!System.Threading.Monitor.TryEnter(_sdkLock))
            return false;
        try
        {
            return EverestSdkNative.GetColorData(ref buf);
        }
        catch { return false; }
        finally { System.Threading.Monitor.Exit(_sdkLock); }
    }

    /// <summary>
    /// Variante raw (IntPtr) di GetColorData, con lock non-bloccante.
    /// </summary>
    public bool TryGetColorDataRaw(IntPtr rawBuf)
    {
        if (!System.Threading.Monitor.TryEnter(_sdkLock))
            return false;
        try
        {
            return EverestSdkNative.GetColorDataRaw(rawBuf);
        }
        catch { return false; }
        finally { System.Threading.Monitor.Exit(_sdkLock); }
    }

    /// <summary>
    /// Abilita lo streaming dei report colore dal firmware (HID 0x11 0x83).
    /// Chiamare con value=10 prima di GetColorData, come fa Base Camp.
    /// </summary>
    public bool EnableColorStream(int value = 10)
    {
        try
        {
            bool ok = EverestSdkNative.SetVolumeInfo(value);
            App.WriteLog($"[Everest.EnableColorStream] value={value} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.EnableColorStream] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Turns the backlight on/off ("main" brightness).</summary>
    public bool SetBacklight(bool on)
    {
        try
        {
            bool ok = EverestSdkNative.SetMainBrightness(on);
            App.WriteLog($"[Everest.SetBacklight] on={on} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetBacklight] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Quantizes a percentage 0..100 to the 5 firmware brightness steps
    /// (0/25/50/75/100) — il firmware accetta solo questi valori.
    /// </summary>
    private static EverestSdkNative.BrightT QuantizeBrightness(int pct)
    {
        if (pct <= 12)  return EverestSdkNative.BrightT.B0;
        if (pct <= 37)  return EverestSdkNative.BrightT.B25;
        if (pct <= 62)  return EverestSdkNative.BrightT.B50;
        if (pct <= 87)  return EverestSdkNative.BrightT.B75;
        return EverestSdkNative.BrightT.B100;
    }

    // ==== Numpad Display Keys =================================================

    /// <summary>
    /// Legge le informazioni estese dal firmware: stato plug del Media Dock e
    /// of the Numpad, current menu, sub-device brightness, etc.
    /// </summary>
    internal bool TryGetExtendInfo(out EverestSdkNative.FW_EXTEND_INFO info)
    {
        info = default;
        lock (_sdkLock)
        try { return EverestSdkNative.GetExtendInfo(ref info); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetExtendInfo] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>True if the numpad (with display keys) is connected.</summary>
    public bool IsNumpadPlugged()
    {
        return TryGetExtendInfo(out var info) && info.byNumpadPlug != 0;
    }

    /// <summary>True if the Media Dock is connected.</summary>
    public bool IsMMDockPlugged()
    {
        return TryGetExtendInfo(out var info) && info.byMMDockPlug != 0;
    }

    /// <summary>
    /// Valore grezzo di byNumpadPlug (0=non collegato, 1=sinistra, 2=destra — ipotesi da verificare).
    /// </summary>
    public byte NumpadPlugPosition()
    {
        return TryGetExtendInfo(out var info) ? info.byNumpadPlug : (byte)0;
    }

    /// <summary>
    /// Valore grezzo di byMMDockPlug (0=non collegato, 1=sinistra, 2=destra — ipotesi da verificare).
    /// </summary>
    public byte MMDockPlugPosition()
    {
        return TryGetExtendInfo(out var info) ? info.byMMDockPlug : (byte)0;
    }

    /// <summary>
    /// Reads which image is assigned to each of the 4 numpad display keys.
    /// </summary>
    public bool GetDisplayKeyPic(out int d1, out int d2, out int d3, out int d4)
    {
        d1 = d2 = d3 = d4 = 0;
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.GetDisplayKeyPic(ref d1, ref d2, ref d3, ref d4);
            App.WriteLog($"[Everest.GetDisplayKeyPic] -> {ok}  d1={d1} d2={d2} d3={d3} d4={d4}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.GetDisplayKeyPic] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Imposta quale immagine mostrare su ciascuna delle 4 display key del numpad.
    /// </summary>
    public bool SetDisplayKeyPic(int d1, int d2, int d3, int d4)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetDisplayKeyPic(d1, d2, d3, d4);
            App.WriteLog($"[Everest.SetDisplayKeyPic] d1={d1} d2={d2} d3={d3} d4={d4} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetDisplayKeyPic] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Carica un'immagine su una display key del numpad (formato quadrato 72×72).
    /// </summary>
    /// <param name="imagePathOrBase64">Percorso o stringa base64.</param>
    /// <param name="keyIndex">Indice della display key (0-3).</param>
    /// <param name="picSlot">Slot immagine firmware (usato come byTargetPic).</param>
    public bool UploadNumpadImage(string imagePathOrBase64, int keyIndex, byte picSlot = 0)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestImageUploader.UploadImage(
                imagePathOrBase64,
                EverestImageUploader.PicTarget.NumpadSquare,
                picSlot,
                (byte)keyIndex);
            App.WriteLog($"[Everest.UploadNumpadImage] key={keyIndex} slot={picSlot} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UploadNumpadImage] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Carica un'immagine su una display key del numpad (formato strip 128×32).
    /// Tentativo alternativo — da verificare con USB capture quale formato
    /// is the right one for your hardware.
    /// </summary>
    public bool UploadNumpadImageStrip(string imagePathOrBase64, int keyIndex, byte picSlot = 0)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestImageUploader.UploadImage(
                imagePathOrBase64,
                EverestImageUploader.PicTarget.NumpadStrip,
                picSlot,
                (byte)keyIndex);
            App.WriteLog($"[Everest.UploadNumpadImageStrip] key={keyIndex} slot={picSlot} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UploadNumpadImageStrip] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Reset completo del numpad (display keys + stato).</summary>
    public bool ResetNumpad()
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ResetNumpad();
            App.WriteLog($"[Everest.ResetNumpad] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetNumpad] ha lanciato: " + ex);
            return false;
        }
    }

    // ==== Media Dock (MMDock) =================================================

    /// <summary>
    /// Applica un effetto LED sulla barra luminosa del Media Dock.
    /// </summary>
    internal bool SetBarEffect(EverestSdkNative.BarData data)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ChangeBarEffect(data);
            App.WriteLog($"[Everest.SetBarEffect] eff={data.byEffectIndex} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetBarEffect] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Imposta colori custom statici sulla barra del Media Dock (126 LED).
    /// </summary>
    internal bool SetBarCustomize(EverestSdkNative.CustomStatic data)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ChangeBarCustomize(data);
            App.WriteLog($"[Everest.SetBarCustomize] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetBarCustomize] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Aggiorna l'orologio sul display del Media Dock con l'ora corrente.
    /// Chiamare periodicamente (ogni secondo, come fa Base Camp).
    /// </summary>
    public bool UpdateClock()
    {
        lock (_sdkLock)
        try
        {
            // First read whether the clock is enabled and the format
            bool clockEnabled = false, format24h = false;
            bool gotClock = EverestSdkNative.GetClockInfo(ref clockEnabled, ref format24h);
            if (!gotClock || !clockEnabled) return false;

            var now = DateTime.Now;
            bool ok = EverestSdkNative.SetClockInfo(
                now.Month, now.Day, now.Hour, now.Minute, now.Second,
                clockEnabled, format24h);
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UpdateClock] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Invia un dato di monitoraggio PC al Media Dock.
    /// </summary>
    /// <param name="infoType">0=CPU, 1=GPU, 2=Disk, 3=Network, 4=RAM, 5=KeyPressCount.</param>
    /// <param name="value">Valore (percentuale o conteggio).</param>
    public bool SetPCInfo(int infoType, int value)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetPCInfo(infoType, value);
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[Everest.SetPCInfo] type={infoType} ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Invia il livello volume al Media Dock (0-100).
    /// NOTE: SetVolumeInfo is also used for EnableColorStream (value=10/0x0A
    /// attiva lo streaming colori). Per il volume reale del dock, chiamare
    /// quando <c>byMMDockMenuIndex == 65 ('A')</c>.
    /// </summary>
    public bool SetVolume(int volumePercent)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetVolumeInfo(volumePercent);
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetVolume] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Carica un'immagine screensaver sul display del Media Dock (240×204 px).
    /// </summary>
    public bool UploadMMDockScreensaver(string imagePathOrBase64)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestImageUploader.UploadImage(
                imagePathOrBase64,
                EverestImageUploader.PicTarget.MMDockScreensaver,
                picSlot: 1);
            App.WriteLog($"[Everest.UploadMMDockScreensaver] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UploadMMDockScreensaver] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>Reset completo del Media Dock.</summary>
    public bool ResetMMDock()
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ResetMMDock();
            App.WriteLog($"[Everest.ResetMMDock] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetMMDock] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Scrive la configurazione estesa nel firmware (MMDock settings, brightness, etc.).
    /// </summary>
    internal bool SetExtendInfo(EverestSdkNative.FW_EXTEND_INFO info)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetExtendInfo(info);
            App.WriteLog($"[Everest.SetExtendInfo] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetExtendInfo] ha lanciato: " + ex);
            return false;
        }
    }

    // ==== Custom per-key lighting =============================================

    /// <summary>
    /// Switches the firmware to "custom per-key" mode for the given profile.
    /// </summary>
    public bool SwitchToCustomize(int profile)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SwitchToCustomizeEffect(profile);
            App.WriteLog($"[Everest.SwitchToCustomize] profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SwitchToCustomize] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Invia un effetto custom per-key al device.
    /// </summary>
    internal bool SetCustomEffect(int profile, int area, EverestSdkNative.CustomEffect data, bool save = true)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ChangeCustomizeEffect(profile, area, data, save);
            App.WriteLog($"[Everest.SetCustomEffect] profile={profile} area={area} save={save} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetCustomEffect] ha lanciato: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Legge l'effetto custom corrente dal device.
    /// </summary>
    internal bool TryGetCustomEffect(int profile, int area, out EverestSdkNative.CustomEffect data)
    {
        data = new EverestSdkNative.CustomEffect
        {
            data = new EverestSdkNative.CustomData[171]
        };
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.GetEffCustomizeContent(profile, area, ref data);
            App.WriteLog($"[Everest.GetCustomEffect] profile={profile} area={area} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.GetCustomEffect] ha lanciato: " + ex);
            return false;
        }
    }

    public void Dispose() => Close();

    // ---- callback nativo (thread dell'SDK) ---------------------------------

    private void OnKeyCallback(ushort wMatrix, bool bPressed, uint id)
    {
        try
        {
            // L'evento lo emettiamo senza lock: i consumer potrebbero
            // richiamare altri metodi di EverestService (deadlock).
            // Log tasti rimosso — troppo rumoroso in uso normale.
            KeyEvent?.Invoke(this, new EverestKeyEventArgs(id, wMatrix, bPressed));
        }
        catch (Exception ex)
        {
            // Mai propagare un'eccezione gestita verso codice nativo.
            App.WriteLog("[Everest.OnKeyCallback] ha lanciato: " + ex);
        }
    }
}

/// <summary>Argomenti dell'evento <see cref="EverestService.KeyEvent"/>.</summary>
public sealed class EverestKeyEventArgs : EventArgs
{
    public EverestKeyEventArgs(uint deviceId, ushort keyMatrix, bool pressed)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
    }

    /// <summary>Id del device riportato dall'SDK.</summary>
    public uint DeviceId { get; }

    /// <summary>Indice di matrice del tasto (indice fisico del firmware).</summary>
    public ushort KeyMatrix { get; }

    /// <summary>True = premuto, false = rilasciato.</summary>
    public bool Pressed { get; }
}
