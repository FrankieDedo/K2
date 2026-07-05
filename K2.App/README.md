# K2.App — guscio unificato + moduli MacroPad ed Everest Max

`K2.App` e' l'applicazione **unificata** K2 che, a regime, sostituira' Base
Camp per tutti i dispositivi Mountain. Il **modulo MacroPad** copre:

- apertura / chiusura del driver USB del MacroPad ed enumerazione dei device;
- ricezione degli eventi (tasti via callback, plug/unplug e progress firmware
  via messaggi Windows);
- **griglia dei 12 tasti**: a ogni tasto si assegna un'azione (URL, programma,
  scorciatoie, comandi di sistema, **Script Python**, ...) eseguita alla
  pressione tramite il motore condiviso `K2.Core`;
- console di log degli eventi e dell'esecuzione.

## Layout

```
K2/
├── K2.sln                      ← solution unificata (K2.App + K2.DisplayPad + K2.Core)
├── DISTRIBUTION.md             ← cosa è redistribuibile e cosa no
├── K2.Core/                    ← libreria CONDIVISA: motore azioni + ponte Python
├── K2.App/                     ← progetto WPF, x86 — referenzia K2.Core
│   ├── K2.App.csproj
│   ├── App.xaml(.cs)
│   ├── MainWindow.xaml(.cs)        ← tab MacroPad: toolbar, griglia tasti, device, log
│   ├── MainWindow.Keys.cs          ← griglia 12 tasti, azioni, rimappa, persistenza
│   ├── MainWindow.ActionHost.cs    ← MainWindow come K2.Core.IActionHost
│   ├── Models/
│   │   └── MacroPadKey.cs          ← cella bindabile di un tasto
│   └── Services/
│       ├── MacroPadSdkNative.cs        ← layer P/Invoke "raw" su MacroPadSDK.dll
│       ├── MacroPadService.cs          ← facade .NET con eventi tipizzati
│       ├── MacroPadStore.cs            ← persistenza SQLite delle azioni dei tasti
│       └── NativeDependencyResolver.cs ← trova MacroPadSDK.dll a runtime
└── _reference/native/          ← NON distribuibile, escluso da .gitignore
    └── MacroPadSDK.dll         ← nativa 32 bit, copia di lavoro per lo sviluppo
```

## Perche' x86 (vincolo importante)

`MacroPadSDK.dll` e `SDKDLL.dll` di Base Camp sono
**binari nativi a 32 bit**. (`Everest360_USB.dll` esiste anch'essa nei
binari di Base Camp ma è la SDK dell'Everest **60**, non quella della Max:
non viene usata da K2 — vedi `EverestSdkNative.cs`.) Un processo a 64 bit non puo' caricarli: il primo
P/Invoke fallirebbe con `BadImageFormatException`. Per questo `K2.App` e'
compilato **x86** (`PlatformTarget=x86`).

`K2.DisplayPad` resta invece **x64** perche' usa una build a 64 bit dedicata
di `DisplayPadSDK.dll`. Le due piattaforme non possono convivere nello stesso
processo: nella `K2.sln` ogni progetto compila solo sotto la propria
piattaforma (scegliere `x86` per K2.App, `x64` per K2.DisplayPad). L'effettiva
fusione del DisplayPad dentro `K2.App` richiedera' di ricompilare il modulo
DisplayPad sulla nativa a 32 bit: e' un passo successivo.

## Come sono state ricavate le firme dell'SDK

`MacroPadSDK.dll` e' nativa e non ha documentazione. Le firme P/Invoke in
`MacroPadSdkNative.cs` **non sono indovinate**: sono estratte dai metadati
ECMA-335 di `BaseCamp.Service.exe` (classe interna
`BaseCamp.Service.Helpers.MacroPadSDK`), il binario originale di Base Camp che
pilota il MacroPad. Risultato della verifica:

- tutte le funzioni esportate usano la convenzione `__cdecl`;
- il callback dei tasti (`KEY_CALLBACK`) usa `__stdcall`, firma
  `void(ushort wMatrix, bool bPressed, uint ID)`;
- `DevInfo` e `FWInfo` sono `LayoutKind.Sequential, Pack=1`;
- costanti firmware: `FW_NUM_KEY=12`, `FW_NUM_PROFILE=5`, `MAX_DEV_COUNT=10`,
  `WM_DEVICE_PLUG=21505`, `WM_FW_PROGRESS=21506`.

## Dipendenza nativa `MacroPadSDK.dll` (NON distribuibile)

`MacroPadSDK.dll` è un componente interno di Base Camp, senza licenza di
ridistribuzione: **K2 non la impacchetta**. La gestione è automatica:

- in **sviluppo**, se esiste `K2/_reference/native/MacroPadSDK.dll`, il
  `.csproj` la copia accanto all'eseguibile;
- a **runtime**, `NativeDependencyResolver` la cerca — accanto all'exe, nella
  cartella `K2_BASECAMP_DIR`, o in una installazione di Base Camp individuata
  via registro e percorsi tipici;
- se non la trova, l'app non va in crash: la console mostra le istruzioni.

L'utente finale deve copiare la DLL accanto a `K2.App.exe` **oppure** tenere
installato Base Camp. Dettagli completi in `K2/DISTRIBUTION.md`.

## Azioni sui tasti

Ogni tasto del MacroPad puo' avere un'azione, configurata con un clic sul
tasto nella griglia (o clic destro → *Configura azione*). I tipi disponibili
— URL, programma, cartella, browser, comando di sistema, tasto multimediale,
mouse, scorciatoie, comando shell, testo, **Script Python** — sono gestiti dal
motore condiviso in `K2.Core`, lo stesso del DisplayPad. Le azioni sono
salvate per (device, profilo, tasto) in un DB SQLite
(`%LocalAppData%\K2.App\macropad.db`).

I tasti del MacroPad inviano un codice di **matrice hardware**: alla prima
configurazione di un device va eseguita la **rimappatura** (pulsante *Rimappa
tasti*: premere in sequenza i tasti fisici per le celle #0..#11). La mappa
matrice→tasto viene salvata e riusata.

Lo *Script Python* funziona come nel DisplayPad (vedi `K2/README.md` e
`K2/lib/pybridge/`); richiede il runtime installato con `setup-python-embed.bat`.

**Limite attuale:** il cambio profilo del MacroPad non e' ancora cablato (manca
l'export nativo nel layer P/Invoke), quindi l'azione "Cambia profilo" sul
MacroPad per ora viene solo loggata. Il selettore *Profilo* serve comunque a
scegliere quale profilo si sta configurando. Tutte le altre azioni funzionano.

## Modulo Everest Max

Tab **Everest Max** nella stessa MainWindow. La tastiera Everest Max è
single-device (non slot-based come il MacroPad) e ha 100+ tasti: niente
griglia fissa, i tasti si **catturano on-demand**.

Flusso d'uso: *Apri driver* → premi **Cattura tasto** → premi il tasto fisico
desiderato → si aggiunge alla lista del profilo corrente → *Configura azione*
per assegnare qualsiasi tipo di azione (incluso Script Python). I tasti sono
identificati dal loro **codice di matrice** (l'identità hardware); la lista
si popola incrementalmente man mano che mappi i tasti che ti interessano.
Vale per tastiera ISO, numpad, 4 tasti programmabili dedicati, e i 5 tasti +
encoder (CW/CCW = 2 codici) del media dock. Lo schermo del dock è gestito
dal firmware.

Persistenza SQLite in `%LocalAppData%\K2.App\everest.db`, chiave
(profilo, codice matrice). A differenza del MacroPad, il **cambio profilo**
è cablato sul nativo (l'export `SwitchProfile` esiste): l'azione *Cambia
profilo* e `k2.switch_profile()` funzionano davvero sull'Everest.

Le firme dell'SDK sono state estratte dai metadati di `BaseCamp.Service.exe`
(parser ECMA-335) e salvate in `K2/_reference/Everest_SDK_signatures.txt`.
Il P/Invoke layer (`EverestSdkNative`) dichiara per ora solo il sottoinsieme
"scheletro" — apertura, info, AP mode, profilo, callback dei tasti. Le ~60
funzioni per effetti RGB / macro complete / immagini restano da aggiungere.

## Build

```powershell
dotnet build .\K2.sln -c Debug -p:Platform=x86
```

Output in `K2.App\bin\x86\Debug\net8.0-windows\`: include `K2.App.exe`.
`MacroPadSDK.dll` vi compare solo se è presente la copia di lavoro in
`_reference/native/` — vedi sopra.

## Run

```powershell
dotnet run --project .\K2.App\K2.App.csproj -c Debug -p:Platform=x86
```

A finestra aperta, nel tab **MacroPad**:

1. premere **Apri driver** — chiama `OpenUSBDriver(hwnd)` passando l'HWND
   della finestra (serve a ricevere `WM_DEVICE_PLUG` / `WM_FW_PROGRESS`);
2. la tabella device si popola con gli slot riconosciuti dall'SDK;
3. ogni evento (tasto, plug, progress) compare nella console **Eventi** e nel
   file `K2.App.log` accanto all'eseguibile.

> ⚠️ Con Base Camp originale in esecuzione, il driver puo' essere "preso" da
> `BaseCamp.Service.exe` e `OpenUSBDriver` puo' tornare `false`. Chiudere Base
> Camp prima di testare K2.

## Prossimi step

1. **Cambio profilo nativo**: aggiungere a `MacroPadSdkNative` l'export
   `SwitchProfile(int, int, uint)` — per il MacroPad ha **tre** parametri, non
   due come il DisplayPad — e cablare `IActionHost.SwitchProfile`.
2. **Luminosita' / effetti RGB**: `SetMainBrightness`, `SetFullMacroData`,
   `ChangeEffect`, `ChangeBlockEffect` (struct `KEYMAP_EVENT`, `EffData`,
   `BlockData` gia' mappate nei metadati di Base Camp).
3. **Tastiera Everest Max**: stessa architettura (modulo + `IActionHost`),
   SDK **`SDKDLL.dll`** (≈75 export, HID + media bar + clock/numpad/MMDock).
   _Nota:_ `Everest360_USB.dll` (110 export) è invece la SDK dell'Everest 60.
4. **Fusione DisplayPad in K2.App**: richiede di ricompilare il modulo
   DisplayPad sulla nativa a 32 bit.
