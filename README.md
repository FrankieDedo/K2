# K2 — DisplayPad

Scheletro di applicazione per controllare il DisplayPad di Mountain
sostituendo Base Camp. Questo primo step copre:

- apertura del driver USB,
- enumerazione dei device collegati,
- ricezione degli eventi (plug/unplug, key, progress FW),
- console di log per validare end-to-end la pipeline SDK ⇄ applicazione.

## Layout

```
K2/
├── K2.DisplayPad.sln                  ← solution
├── K2.DisplayPad/                     ← progetto WPF
│   ├── K2.DisplayPad.csproj
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)
│   └── Services/
│       └── DisplayPadService.cs       ← facade sopra DisplayPad.SDK
├── lib/
│   ├── DisplayPad.SDK.dll             ← wrapper managed (net6)
│   ├── DisplayPad.SDK.xml             ← doc per IntelliSense
│   └── DisplayPadSDK.dll              ← nativa x64 (P/Invoke target)
└── README.md
```

Il `.csproj` referenzia `lib/DisplayPad.SDK.dll` come `Reference` e copia
`lib/DisplayPadSDK.dll` nella cartella di output: la nativa deve trovarsi
accanto all'eseguibile altrimenti il wrapper fallisce al primo P/Invoke.

## Prerequisiti

- Windows 10/11 a **64 bit**.
- **.NET 8 SDK** (o successivo che mantenga il workload `windowsdesktop`).
- Visual Studio 2022 17.4+ *oppure* CLI `dotnet`.
- Driver Mountain DisplayPad installato (per essere certi che `setupapi`
  veda il device HID).

## Build

Da terminale, dalla cartella `K2/`:

```powershell
dotnet build .\K2.DisplayPad.sln -c Debug -p:Platform=x64
```

L'output finisce in `K2.DisplayPad\bin\x64\Debug\net8.0-windows\` e
include `K2.DisplayPad.exe`, il wrapper `DisplayPad.SDK.dll` e la
nativa `DisplayPadSDK.dll`.

## Run

```powershell
dotnet run --project .\K2.DisplayPad\K2.DisplayPad.csproj -c Debug -p:Platform=x64
```

A finestra aperta:

1. premere **Apri driver** — il servizio chiama `OpenUSBDriver(hwnd)`
   passando l'HWND della finestra principale (serve per ricevere
   `WM_DEVICE_PLUG` / `WM_FW_PROGRESS` dall'SDK);
2. la lista device si popola con gli ID che l'SDK ha riconosciuto;
3. ogni evento (plug, key, progress) compare nel pannello **Eventi**.

> ⚠️ Mentre Base Camp originale e' in esecuzione, il driver puo' essere
> "preso" dal worker di Mountain (`MountainDisplayPadWorker.exe`) e la
> nostra `OpenUSBDriver` puo' tornare `false`. Chiudere Base Camp prima
> di testare K2.

## Mapping API ↔ SDK

L'ispezione dei metadati della DLL ha rivelato che **`DisplayPad.SDK.DisplayPadSDK`
ha tutti i metodi `internal static`** (assembly-private), quindi non
referenziabili da fuori l'assembly. L'API pubblica effettiva e' la
classe **`DisplayPad.SDK.DisplayPadHelper`** (metodi di istanza, eventi
statici).

| Funzione K2                  | Chiamata SDK reale                                            |
|------------------------------|---------------------------------------------------------------|
| `Open(hWnd)`                 | `helper.DisplayPadOpenUSBDriver(hWnd.ToString())`             |
| `Close()`                    | `helper.DisplayPadCloseUSBDriver()`                           |
| `SdkVersion()`               | `helper.DisplayPadDllVersion()`                               |
| `DeviceIds()`                | reflection su `DisplayPadSDK.lstDeviceID` (internal static)   |
| `IsPlugged(id)`              | `helper.DisplayPadIsDevicePlug(id)`                           |
| `FirmwareVersion(id)`        | `helper.DisplayPadGetDevAppVer(id)`                           |
| `SetBrightness(id, level)`   | `helper.DisplayPadSetMainBrightness(level, id)`               |
| `SwitchProfile(id, n)`       | `helper.DisplayPadSwitchProfile(n.ToString(), id)`            |
| `UploadImage(id, path, btn)` | `helper.UploadImage(id, path, btn)`                           |
| evento `DevicePlug`          | `DisplayPadHelper.DisplayPadPlugCallBack`     (static)        |
| evento `KeyEvent`            | `DisplayPadHelper.DisplayPadKeyCallBack`      (static)        |
| evento `FirmwareProgress`    | `DisplayPadHelper.DisplayPadProgressCallBack` (static)        |

Gli handler dei tre eventi statici sono allacciati via reflection
(`EventInfo.AddEventHandler` con target null) per non dipendere dal
nome esatto del delegate tipizzato dichiarato dall'SDK; le firme dei
target restano `void (int, int, int)` come da XML doc.

## Script Python sui tasti

Un tasto puo' essere configurato con l'azione **"Script Python"**: alla
pressione K2 esegue uno script Python che puo' fare azioni arbitrarie sul
sistema operativo *e* richiamare le funzioni di K2.

**Runtime** — gli script girano con una distribuzione Python *embeddable*
x64 in `K2/lib/python-embed/`. Non e' nel repo: installala una volta con
`setup-python-embed.bat` (scarica ~10 MB da python.org). K2 cerca
l'interprete anche tramite la variabile `K2_PYTHON_DIR` o un percorso
salvato nelle impostazioni (`python.exePath`).

**Configurazione** — menu contestuale del tasto → *Configura azione* →
tipo *Script Python*. Si puo' puntare a un **file .py** oppure scrivere
**codice inline**; si impostano argomenti opzionali e un timeout
(0 = nessun limite). Il payload e' salvato come JSON nel campo
`ActionValue` del tasto (vedi `K2.Core/PyScriptPayload.cs`).

**Ponte K2 ⇄ Python** — K2 espone una piccola API HTTP su `127.0.0.1`
(`K2.Core/K2RpcServer.cs`), protetta da un token casuale generato a ogni
avvio. Lo script importa il modulo `k2` (vedi `lib/pybridge/`) e chiama:

| Funzione Python        | Effetto in K2                                  |
|------------------------|------------------------------------------------|
| `k2.log(msg)`          | scrive nel log dell'app                        |
| `k2.get_state()`       | device / profilo / conteggi / versione SDK     |
| `k2.get_buttons()`     | stato dei 12 tasti del profilo corrente        |
| `k2.switch_profile(t)` | cambia profilo (`1..5` / `Next` / `Previous`)  |
| `k2.run_action(t, v)`  | esegue una qualsiasi azione K2                 |
| `k2.press_button(i)`   | "preme" via software il tasto `i`              |

Contesto della pressione: `k2.device()`, `k2.profile()`, `k2.button()`,
`k2.script_args()`. `stdout`/`stderr` dello script finiscono nel log.

Il motore azioni e il ponte Python vivono nella libreria **condivisa**
`K2.Core` (`ButtonActionEngine`, `PyBridge`, `K2RpcServer`,
`PythonScriptService`, `PythonRuntimeLocator`, `PyScriptPayload`,
`ButtonActionDialog`), riusabile da tutti i moduli dispositivo. Il DisplayPad
vi si aggancia con `MainWindow.ActionHost.cs`, che implementa
`K2.Core.IActionHost` esponendo le operazioni device-specific (cambio profilo,
stato, pressione tasto).

## Prossimi step

1. **Anteprima tasti**: griglia 4×3 in MainWindow con miniatura del PNG
   caricato; drag&drop di un'immagine → `UploadImage(id, path, btn)`.
2. **Slider luminosita'** e selettore profilo (1..5) live.
3. **Editor macro** usando `DisplayPadSetFullMacroData` (struct
   `MacroInputContent_DisplayPad`).
4. **Persistenza**: schema SQLite (clonando le tabelle di
   `BaseCamp.db`) per profili/immagini/macro.
5. Replicare la stessa architettura per **MacroPad**
   (`MacroPadSDK.dll`) e **Everest Max** (`SDKDLL.dll`; l'Everest 60
   userebbe invece `Everest360_USB.dll`).
