using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Layer P/Invoke "raw" sopra <c>MacroPadSDK.dll</c> (la nativa C++ del MacroPad).
///
/// A differenza del DisplayPad — che dispone di un wrapper managed
/// (<c>DisplayPad.SDK.dll</c>) — il MacroPad espone solo la DLL nativa, quindi
/// qui dichiariamo direttamente le funzioni esportate.
///
/// <para>
/// Le firme NON sono state indovinate: sono state estratte dai metadati
/// ECMA-335 di <c>BaseCamp.Service.exe</c> (classe interna
/// <c>BaseCamp.Service.Helpers.MacroPadSDK</c>), che e' il binario originale
/// di Base Camp che pilota il MacroPad. Risultato della verifica:
/// </para>
/// <list type="bullet">
///   <item>tutte le funzioni esportate usano la convenzione <c>__cdecl</c>;</item>
///   <item>il callback dei tasti (<see cref="KEY_CALLBACK"/>) usa <c>__stdcall</c>
///         — l'attributo <c>UnmanagedFunctionPointer</c> nel binario originale
///         vale 3 = StdCall;</item>
///   <item><c>DevInfo</c> e <c>FWInfo</c> sono <c>Sequential</c> con <c>Pack=1</c>.</item>
/// </list>
///
/// <para>
/// <b>IMPORTANTE — bitness.</b> <c>MacroPadSDK.dll</c> e' un binario a 32 bit:
/// il processo che la carica DEVE essere x86 (<c>K2.App.csproj</c> imposta
/// <c>PlatformTarget=x86</c>). In un processo a 64 bit il primo P/Invoke
/// fallirebbe con <c>BadImageFormatException</c>.
/// </para>
/// </summary>
internal static class MacroPadSdkNative
{
    private const string Dll = "MacroPadSDK.dll";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    // ---- Costanti del firmware ---------------------------------------------
    // (valori const della classe MacroPadSDK in BaseCamp.Service.exe)

    /// <summary>Slot massimi che l'SDK puo' indirizzare.</summary>
    public const int MAX_DEV_COUNT = 10;

    /// <summary>Profili memorizzati su ciascun MacroPad.</summary>
    public const int FW_NUM_PROFILE = 5;

    /// <summary>Tasti fisici del MacroPad.</summary>
    public const int FW_NUM_KEY = 12;

    /// <summary>Messaggio Windows postato sull'HWND a ogni plug/unplug device.</summary>
    public const int WM_DEVICE_PLUG = 21505;

    /// <summary>Messaggio Windows di avanzamento update firmware.</summary>
    public const int WM_FW_PROGRESS = 21506;

    /// <summary>Messaggio Windows di stato tasto (i tasti arrivano anche via callback).</summary>
    public const int WM_KEY_STATUS = 25600;

    // ---- Struct hardware ----------------------------------------------------

    /// <summary>Identificativi USB e versioni del device.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DevInfo
    {
        public ushort vid;
        public ushort pid;
        public ushort fwVer;
        public ushort bootloadVer;
    }

    /// <summary>Stato firmware: versione, profili, indici effetto correnti.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FWInfo
    {
        public ushort fwVer;
        public ushort wUndef;
        public byte sizeProfile;
        public byte byEffectModeIndex;
        public byte currentlyProfileIndex;
        public byte byEffectMenuIndex;
    }

    // ---- Callback dei tasti -------------------------------------------------

    /// <summary>
    /// Delegate invocato dall'SDK a ogni pressione/rilascio di un tasto.
    /// Convenzione <c>__stdcall</c>. Parametri: matrice tasto, premuto/rilasciato,
    /// id del device. Viene chiamato su un thread interno dell'SDK: il
    /// consumer deve effettuare il marshalling verso il thread UI.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint ID);

    // ---- Funzioni esportate (__cdecl) --------------------------------------

    /// <summary>Versione della DLL nativa dell'SDK.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern int GetDLLVersion();

    /// <summary>
    /// Apre il driver USB. <paramref name="handle"/> e' l'HWND della finestra
    /// che ricevera' i messaggi <see cref="WM_DEVICE_PLUG"/> /
    /// <see cref="WM_FW_PROGRESS"/>.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool OpenUSBDriver(IntPtr handle);

    /// <summary>Chiude il driver USB.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void CloseUSBDriver();

    /// <summary>Numero di device attualmente noti all'SDK.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool GetDevCount(ref int iDevCount);

    /// <summary>
    /// True se sullo slot <paramref name="ID"/> c'e' un device collegato.
    /// Marshalling esplicito <c>I1</c>: questa funzione, nella famiglia di SDK
    /// Mountain, restituisce un <c>bool</c> C++ a 1 byte (verificato sul
    /// codice originale del worker DisplayPad).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsDevicePlug(uint ID);

    /// <summary>Versione applicativa del firmware del device.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern ushort GetDevAppVer(uint ID);

    /// <summary>Legge VID/PID e versioni del device nello slot indicato.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool GetDeviceInfo(ref DevInfo devInfo, uint ID);

    /// <summary>Legge lo stato firmware (profilo corrente, indici effetto).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool GetFWInfo(ref FWInfo fwInfo, uint ID);

    /// <summary>
    /// Legge il layout del firmware (HID <c>11 12</c>).
    /// Necessaria per abilitare il color streaming (GetColorData).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetFWLayout(ref int layout, uint ID);

    /// <summary>True se il device sta aggiornando il firmware.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool IsUpdating(uint ID);

    /// <summary>
    /// Abilita/disabilita il controllo software (AP mode) del device.
    /// Con AP abilitato l'host gestisce effetti e tasti in tempo reale.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool APEnable(bool bEnable, uint ID);

    /// <summary>Reset del device.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool ResetDevice(uint ID);

    /// <summary>
    /// Registra il callback globale per gli eventi dei tasti. Va passato un
    /// delegate mantenuto vivo dal chiamante (vedi <see cref="MacroPadService"/>),
    /// altrimenti il GC lo libera e l'SDK chiama un puntatore non valido.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK callback);

    // =======================================================================
    // Illuminazione LED (preset firmware)
    // =======================================================================
    //
    // Le firme NON sono indovinate: estratte dai metadati ECMA-335 di
    // BaseCamp.Service.exe (classe BaseCamp.Service.Helpers.MacroPadSDK), dump
    // P/Invoke via _reference/tools/dotnet_pinvoke_dump.py + signature decoder
    // (2026-05-29). Risultato chiave:
    //
    //   DIFFERENZA col wrapper Everest (SDKDLL.dll, single-device): ogni
    //   funzione del MacroPad prende un ULTIMO parametro `uint ID` = lo slot
    //   del device (1..MAX_DEV_COUNT), perche' il MacroPad e' multi-device.
    //   Es. SwitchProfile(int,int,uint) ha 3 parametri (non 2).
    //
    // La struct EffData e' la STESSA famiglia firmware Mountain dell'Everest:
    // verificata byte-per-byte identica (Pack=1, 62B, colorLv ByValArray[3],
    // byData ByValArray[43]) via dotnet_struct_dump.py + dotnet_marshalas.py.
    // Le costanti di EffData.New derivano dal dump CIL di
    // MacroPadSDK::getChangeEffect (byWidth=255, byDirection=255 sempre;
    // bySpeed=valore enum; byAll=1; byData=43 zeri).
    // -----------------------------------------------------------------------

    /// <summary>Indice numerico del preset di illuminazione (enum EFF_INDEX
    /// di Base Camp, condiviso tra tutti i device Mountain).</summary>
    public enum EffectIndex : byte
    {
        Static    = 0,
        Breath    = 1,
        ReactiveA = 3,
        Wave      = 4,
        ReactiveB = 5,
        Yeti      = 6,
        Tornado   = 7,
        Matrix    = 9,
        Custom    = 10,
        ReactiveC = 11,
        Off       = 12,
    }

    /// <summary>Speed (firmware SPEED_T enum).</summary>
    public enum SpeedT : byte { Slow = 0, Normal = 1, Fast = 2 }

    /// <summary>Senso di rotazione (enum DIRECTION_T del firmware).</summary>
    public enum DirectionT : byte { ClockWise = 0, CounterClockWise = 1 }

    /// <summary>Brightness (firmware BRIGHT_T enum: 0/25/50/75/100).</summary>
    public enum BrightT : byte { B0 = 0, B25 = 25, B50 = 50, B75 = 75, B100 = 100 }

    /// <summary>Terna RGB del firmware (3 byte, Pack=1).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FWColor
    {
        public byte r, g, b;

        public FWColor(byte r, byte g, byte b) { this.r = r; this.g = g; this.b = b; }

        /// <summary>Costruisce da intero 0xRRGGBB (es. <c>0x900000</c>).</summary>
        public static FWColor FromRgb(int rgb) =>
            new((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }

    /// <summary>
    /// Payload "ChangeEffect" per i preset principali. Pack=1, 62 byte totali.
    /// Identica alla struct EffData dell'Everest (stessa famiglia firmware).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EffData
    {
        public byte byEffectIndex;   // EffectIndex
        public byte byAll;            // 1 = applica a tutti i tasti
        public byte bySpeed;          // SpeedT
        public byte byLightness;      // BrightT
        public byte byRandColor;      // 1 = colori casuali
        public byte byDirection;      // DirectionT (forzato 255: vedi getChangeEffect)
        public byte byWidth;          // larghezza onda (forzato 255: vedi getChangeEffect)

        /// <summary>Colori principali dell'effetto (max 3).</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public FWColor[] colorLv;

        /// <summary>Colore di background dell'effetto.</summary>
        public FWColor bkColor;

        /// <summary>Coda parametri del comando firmware (43 byte, zeri per i preset).</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 43)]
        public byte[] byData;

        /// <summary>
        /// Crea un <see cref="EffData"/> replicando esattamente
        /// <c>MacroPadSDK::getChangeEffect</c> di Base Camp (dump CIL):
        /// <c>byWidth=255</c> e <c>byDirection=255</c> sempre, <c>bySpeed</c>
        /// = valore enum (0/1/2), <c>byAll=1</c>, <c>byData</c> = 43 zeri.
        /// Per <see cref="EffectIndex.Off"/>: colori e background a zero.
        /// </summary>
        public static EffData New(EffectIndex eff,
                                   FWColor c1, FWColor? c2 = null, FWColor? c3 = null,
                                   FWColor? background = null,
                                   SpeedT speed = SpeedT.Normal,
                                   BrightT bright = BrightT.B100,
                                   bool randomColor = false)
        {
            bool isOff  = eff == EffectIndex.Off;
            FWColor zero = default;
            var colors = isOff
                ? new[] { zero, zero, zero }
                : new[] { c1, c2 ?? zero, c3 ?? zero };

            return new EffData
            {
                byEffectIndex = (byte)eff,
                byAll         = 1,
                bySpeed       = (byte)speed,
                byLightness   = (byte)bright,
                byRandColor   = randomColor ? (byte)1 : (byte)0,
                byDirection   = 0xFF,
                byWidth       = 0xFF,
                colorLv       = colors,
                bkColor       = isOff ? zero : (background ?? zero),
                byData        = new byte[43],
            };
        }
    }

    /// <summary>
    /// Switches the active firmware profile (1..5). The second int is reserved (pass 0).
    /// <para>Note: SwitchProfile has 3 parameters on MacroPad (not 2 like Everest), because
    /// MacroPad supports multiple devices — the last uint ID identifies the device slot.</para>
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SwitchProfile(int profile, int reserved, uint ID);

    /// <summary>Applica un preset di illuminazione allo slot device indicato.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeEffect(EffData data, uint ID);

    /// <summary>Resetta gli effetti correnti al default firmware.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetEffects(uint ID);

    /// <summary>Sincronizza l'effetto del profilo indicato (firma <c>(bool,int,uint)</c>).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSyncEffect(
        [MarshalAs(UnmanagedType.I1)] bool enable, int profile, uint ID);

    /// <summary>Abilita la sincronizzazione dell'effetto su tutti i profili.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSyncAcrossProfiles(
        [MarshalAs(UnmanagedType.I1)] bool enable, uint ID);

    /// <summary>Reads whether cross-profile synchronization is enabled.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetSyncAcrossProfiles(
        [MarshalAs(UnmanagedType.I1)] ref bool enable, uint ID);

    /// <summary>Sets the global "main" brightness (backlight on/off).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetMainBrightness(
        [MarshalAs(UnmanagedType.I1)] bool enable, uint ID);

    /// <summary>Mantiene la funzione tasti durante AP mode (true = i tasti digitano).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EnableKeyFunc(
        [MarshalAs(UnmanagedType.I1)] bool enable, uint ID);

    /// <summary>Salva sul flash lo stato corrente. Profilo 1..5 o 6 = ALL_PROFILE.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SaveFlash(int profile, uint ID);

    // =====================================================================
    // Lettura colori LED live (per preview real-time)
    // =====================================================================

    /// <summary>Numero di LED nel buffer colori MacroPad (indici 0..125).</summary>
    public const int COLOR_LED_COUNT = 126;

    /// <summary>Buffer colori LED correnti del MacroPad (126 terne RGB).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MACROPAD_COLOR
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = COLOR_LED_COUNT)]
        public FWColor[] color;
    }

    /// <summary>Legge i colori LED correnti del MacroPad.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetColorData(ref MACROPAD_COLOR colorData, uint ID);
}
