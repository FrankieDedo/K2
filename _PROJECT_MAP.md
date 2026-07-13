# _PROJECT_MAP.md — struttura del progetto K2

> Leggi questo file a inizio sessione: è la mappa STABILE della struttura
> (cartelle, architettura, file index) — pensata per restare piccola.
>
> Lo storico dettagliato sessione-per-sessione (bug, fix, verifiche
> pendenti su hardware) vive in `CHANGELOG.md`, non qui — consultalo
> solo on-demand (grep per parola chiave/data), non ad ogni sessione.
>
> Aggiorna QUESTO file solo quando cambia la struttura (nuovi file/
> cartelle, decisioni architetturali durature). I dettagli di singola
> sessione vanno in `CHANGELOG.md` (prepend in cima, stesso formato
> "Last updated / Previous" di sempre).

## Project goal

Recreate the Mountain **Base Camp** application with a new app (**K2**).
Priority: **DisplayPad** first, then other devices starting from
**MacroPad** and the **Everest** keyboard. The `K2` folder contains all
produced files; dependencies from other folders are copied inside `K2`.

## Top-level folders (`K2 Tent - Base Camp Rework/`)

| Folder | Contents | Editable |
|---|---|---|
| `Mountain Base Camp/` | Original Base Camp program (.NET binaries, exe, logs). Reference only, **do not read raw binaries** | yes (patch via dnSpy) |
| `DisplayPad SDK/` | DisplayPad SDK NuGet package (wrapper DLL + native + XML doc). MIT license | no |
| `K2/` | **Active development area** — all produced code | yes |
| `Profili_BaseCamp/` | Original XML profiles exported from Base Camp | no (input) |
| `Profili_K2/` | Profiles that K2 will produce (currently empty) | yes (output) |
| `dnSpy-net-win32/`, `dnSpy-net-win64/` | Portable dnSpy tool for patching binaries. **Ignore in scans** | no |

## Key architectural facts (do not re-derive)

- **Platform constraint:** `K2.App` compiles **x86** (native `MacroPadSDK.dll`
  and `SDKDLL.dll` are 32-bit). `K2.DisplayPad` compiles **x64** (uses an x64
  build of `DisplayPadSDK.dll`). The two platforms cannot coexist in the same
  process: merging DisplayPad↔K2.App will require recompiling the DisplayPad
  module against the 32-bit native (future step).
- **Everest DLL (important):** Base Camp has two Everest wrappers —
  class `Everest` → `SDKDLL.dll` (**Everest Max** keyboard, ≈75 exports with
  bar/clock/MMDock/numpad), class `Everest60` → `Everest360_USB.dll`
  (**Everest 60** keyboard, 60%, Fn-heavy, ~60 exports). K2 targets **SDKDLL.dll**
  for the Max (the user has one). Verify bindings via `outputs/dotnet_pinvoke_dump.py`.
- **Everest 60 (hybrid: raw HID for lighting, vendor SDK for key remap):**
  unlike Everest Max/MacroPad, K2's **lighting** path does NOT P/Invoke
  `Everest360_USB.dll` — those exports (`ChangeEffect`, `ChangeCustomizeEffect`,
  ...) pass structs as opaque `IntPtr` (layout never reverse-engineered by
  anyone). Instead K2 talks HID **Feature Reports** directly (VID `0x3282`,
  PID `0x0005` ANSI/`0x0006` ISO, interface `mi_02`), porting the protocol
  already reverse-engineered by the community project `BaseCampLinux`
  (`devices/everest60/controller.py`). See `K2.App/Services/Everest60HidNative.cs`
  for the rationale in full. **Key remap is different**: decompiling
  `BaseCamp.UI.dll`/`BaseCamp.Service.exe` (2026-07-11, see CHANGELOG) found
  that `Everest60`'s remap exports (`ChangeKey`/`ChangeFnKey`/
  `ChangeShortcutKey`/`SetSingleMacroContent`/`SetKeyCallBack`) are plain
  `int`/`bool` parameters, not opaque structs — so K2 DOES P/Invoke
  `Everest360_USB.dll` for Key Binding only (`Everest60SdkNative.cs`/
  `Everest60SdkService.cs`), writing directly to firmware, same shape as
  Everest Max's SDK usage but without `IActionHost` involvement (Base Camp's
  own Everest 60 function taxonomy has no K2Action-style passthrough — see
  `Everest60KeyBindingPanel.xaml.cs`). **Not yet verified on real hardware.**
- **Makalu 67/Max (mouse, raw HID, no SDK at all):** no vendor SDK/DLL exists
  for this device (unlike MacroPad/Everest Max/DisplayPad). K2 talks HID
  **Feature Reports** directly (VID `0x3282`, PID `0x0003` Makalu 67/`0x0002`
  Makalu Max, interface `mi_01`), porting the protocol reverse-engineered by
  `BaseCampLinux` (`devices/makalu67/controller.py`). Buttons are remapped
  directly in firmware — no `IActionHost`/`ButtonActionEngine` involvement,
  no per-key action assignment, unlike every other device module. Panel
  state lives only in memory (same scope decision as Everest 60); persistence
  is a future step. **Verified on real hardware 2026-07-10** (Everest Max +
  MacroPad + DisplayPad also connected, K2 ran clean end-to-end).
  **Full 3-column layout (sidebar SECTIONS / device image with clickable
  remap hotspots / right-side Profile-Import-Export-Device column) shipped
  2026-07-10** — same pattern as MacroPad/Everest/DisplayPad. RGB lighting,
  DPI levels, and button remap (incl. sniper) are all wired and working;
  device settings (polling/debounce/lift-off/angle-snapping) too.
  **Sections reordered 2026-07-13** to match MacroPad/DisplayPad/Everest 60:
  Key Binding (default) → Lighting → Settings. The old standalone DPI
  section is gone — its levels list lives inside Settings now (right
  column), code moved from `MakaluDpiRemapPanel` to `MakaluRgbSettingsPanel`.
  **LED ring preview (2026-07-13, software-only by necessity):** the Makalu
  has no HID readback for lighting at all — confirmed twice, first via
  BaseCampLinux's source (lighting cmd `0x0C` is write-only, unlike DPI's
  `0x0B` which has a real GET sub-command), then definitively via a real
  Base Camp USB capture (`_reference/usb_dumps/makaluled.pcapng`, analyzed
  with tshark): every lighting SET_REPORT's paired GET_REPORT response is
  just `A0 01 00 00...` (bare ACK), never actual color/effect state — Base
  Camp's own official app doesn't read it back either. So the ring
  (`MainWindow.Makalu.cs`) mirrors `MakaluRgbSettingsPanel`'s own UI
  selection, not the physical device. It's drawn as two filled `Border`
  halves (`_mkLedRingLeft`/`_mkLedRingRight`, since the mouse has 8
  individually-addressable LEDs, 4 per side — see
  `MakaluProtocol.SetLightingCustom`'s LED0..7 doc) positioned BEHIND
  `ImgMkMouse` inside a dedicated `Canvas x:Name="CvsMkRingBack"`: the ring
  area in `Assets/makalu_mouse.png` is a genuine transparent cutout (Base
  Camp's own `makalu67_light.png`, the "off" variant with no rainbow baked
  into the pixels — swapped in from the previously-used `makalu67.png`,
  which is kept as `Assets/makalu_mouse_rainbow.png` and shown for every
  section except Lighting, see `MkUpdateMouseImage`). Geometry is
  native-pixel measurements the user gave directly from Photoshop, scaled
  into the 190×422 render space (`MkRingLeft`/`Top`/`Width`/`Height` consts).
  **Two real bugs found and fixed the same session** (pre-existing, not
  introduced this session): (1) `MakaluHidNative.FindDevice()` matched the
  first HID collection whose path merely contained `"mi_01"`, but that USB
  interface exposes several collections (`col01`..`col05`) and only one
  supports 64-byte Feature Reports — Windows' enumeration order isn't
  stable across reconnects, so this silently broke lighting-effect
  application (`HidD_SetFeature` failing) despite having worked on
  2026-07-10. Fixed by checking `FeatureReportByteLength` via `HidP_GetCaps`
  instead of trusting enumeration order. (2) `BitmapImage` built from a
  relative `Uri` constructed in code (not XAML) silently fails to resolve —
  fixed with an explicit `pack://application:,,,/` URI.

  This layout previously caused a fatal CLR crash on K2 startup ("Invalid
  Program: attempted to call a UnmanagedCallersOnly method from managed
  code") that a long build-and-launch bisection session couldn't explain.
  **Root-caused and fixed with WinDbg+SOS the same day**: it was never a
  JIT/CLR bug. WPF fires a control's XAML-wired event (`RadioButton.Checked`,
  `Slider.ValueChanged`, ...) **synchronously** the instant BAML sets the
  triggering property (`IsChecked="True"`, or `Minimum`/`Maximum` coercing
  `Value` away from its default) — and that happens mid-`InitializeComponent()`,
  before elements declared *later* in the same XAML file have been assigned.
  Three independent instances hit this so far: `RbMkSecRgb`'s `IsChecked="True"`
  null-refed into `MkRgbSettings` (declared later in `MainWindow.xaml`);
  `SldMkDpi`'s `Minimum="50"` null-refed into `TxtMkDpi` (declared right
  after it in `MakaluDpiRemapPanel.xaml`); and (2026-07-12) `SldEv60Brightness`/
  `SldMkBrightness`'s explicit `Value="100"` (≠ Slider's default `0`) firing
  `ValueChanged` mid-parse into `Ev60RgbPanel`/`MkRgbSettings` — same family
  of bug but via the control's own `Value=` attribute, not `Minimum`/`Maximum`
  coercion, the first time it was hit from a control living in the shared
  top-bar (declared early in `MainWindow.xaml`) reaching into a per-device
  `UserControl` (declared far later in the same file). .NET's crash reporter
  mislabels this class of AV with the generic "UnmanagedCallersOnly" message,
  which is what made it look exotic every time. Fixed by (1) setting
  `IsChecked` in code after `InitializeComponent()` finishes instead of in
  XAML, (2) defaulting `MakaluDpiRemapPanel`/`MakaluRgbSettingsPanel`'s
  `_mkSuppress` guard flag to `true` (cleared only at the end of `Init()`) so
  any XAML-load-time event is a no-op regardless of which property triggers
  it, and (3) null-conditional (`?.`) on any handler that reaches into a
  `UserControl` field declared later in the file (`Ev60RgbPanel?.SetBrightness(...)`/
  `MkRgbSettings?.SetBrightness(...)` in `MainWindow.Everest60.cs`/
  `MainWindow.Makalu.cs`). Verified stable across repeated clean-rebuild
  launches (both from the app itself and from a direct terminal run — see
  CHANGELOG 2026-07-12 for the bisection: stashing the day's changes and
  running HEAD alone confirmed the crash was 100% deterministic and
  hardware-independent, not related to any physical device being connected).
  Full sessions in CHANGELOG.md 2026-07-10 and 2026-07-12 — worth reading
  before writing any new XAML-wired event handler anywhere in this codebase;
  the same pattern can bite any tab, not just Makalu/Everest 60, and is NOT
  limited to `IsChecked`/`Minimum`/`Maximum` — any property with an explicit
  value differing from its type default can trigger it. **Not yet verified
  with a physical Makalu** (no unit available in the sessions so far) —
  DPI/remap/sniper over HID still need a hardware pass.
- **Shared library `K2.Core`:** the key action execution engine (incl. Python
  scripts and RPC bridge) lives in `K2.Core`, a WPF library shared by all device
  modules. Compiles both x86 and x64 (`<Platforms>x86;x64</Platforms>`): a .NET
  assembly marked x64 cannot load in an x86 process, so each app builds its own
  copy for its architecture. Device modules implement `K2.Core.IActionHost` to
  expose device-specific operations (profile switch, state, key press); device-
  agnostic actions are executed by `ButtonActionEngine`.
- **DisplayPad SDK API:** the real public class is `DisplayPad.SDK.DisplayPadHelper`
  (instance methods + static events). `DisplayPadSDK` has all methods
  `internal static`. Full mapping table in `K2/README.md`.
- **MacroPad SDK signatures:** extracted from `BaseCamp.Service.exe` metadata
  (not guessed). `SwitchProfile` for MacroPad has **3** parameters (not 2).
  Constants: `FW_NUM_KEY=12`, `FW_NUM_PROFILE=5`, `MAX_DEV_COUNT=10`,
  `WM_DEVICE_PLUG=21505`, `WM_FW_PROGRESS=21506`.
- **Distribution:** K2 aims to be a self-contained redistributable package.
  Material derived from Base Camp (decompiled, patched, `MacroPadSDK.dll`)
  lives in `K2/_reference/`, **non-distributable** and excluded from version
  control (`.gitignore`). `MacroPadSDK.dll` is provided by the end user; at
  runtime `NativeDependencyResolver` searches for it in a Base Camp installation.
  Full rules in `K2/DISTRIBUTION.md`.
- **3rd DisplayPad bug:** RESOLVED (confirmed 2026-05-21). Root cause in
  `DisplayPadOperations.SetDeviceId()` of `MountainDisplayPadWorker.exe`. Fix =
  C# patch via dnSpy, source in `K2/_reference/BaseCamp_Patch/`. External fixes
  (`DisplayPad_Stabilizer/`) alone do NOT hold: Base Camp overrides them at runtime.
- **DisplayPad/MacroPad rotation:** 90°/180°/270° all implemented ("Positioning"
  section in the UI, shows Horizontal/Vertical + degrees). Square 102×102 px
  icons → lossless rotation.
- **UI theme:** shared dark modern look in `K2.Core/Themes/K2Theme.xaml`
  (ResourceDictionary with `x:Class` + code-behind: the 3 window buttons).
  Custom title bar via `WindowChrome`. Palette: bg `#1A1A1E`, teal accent
  `#900000`. Merged in K2.DisplayPad **and** K2.App via pack URI.
  **WPF gotcha:** the `Window` style is **keyed** (`K2WindowStyle`), not
  implicit — WPF resolves implicit styles for the exact runtime type, and
  `MainWindow : Window` does not match `TargetType="Window"`. Every XAML
  Window must reference it: `Style="{StaticResource K2WindowStyle}"` (for
  `ButtonActionDialog` in K2.Core: use `DynamicResource`, because K2.Core
  does not merge the theme — the host app does).
  Icon-button styles (`K2IconButton`, `K2IconAccentButton`): the Segoe
  MDL2 Assets glyph comes from the button's `Tag`; `Content` stays a
  runtime-editable string without losing the icon.
- **Python key scripts:** key action `pyscript` (a .py file or inline code).
  Runtime = Python *embeddable* x64 in `lib/python-embed/` (NOT in the repo,
  created by `setup-python-embed`). Each script runs in its own process,
  launched via `k2_runner.py`. The bridge to K2 is an HTTP API on 127.0.0.1
  with a per-launch token: the script does `import k2` and calls log/get_state/
  switch_profile/run_action/press_button. Details in `K2/README.md`.

## Build & run (from `K2/`)

```
dotnet build .\K2.sln -c Debug -p:Platform=x86      # K2.App (MacroPad) + K2.Core
dotnet build .\K2.DisplayPad.sln -c Debug -p:Platform=x64   # K2.DisplayPad + K2.Core
```
Both solutions include `K2.Core`, which is compiled for the requested
platform (x86 or x64).
`stop-basecamp.bat` frees the driver by closing Base Camp processes before testing.

## File index — `K2/` (development area)

### Solution, build & distribution
- `K2.sln` — unified solution (K2.App + K2.DisplayPad + K2.Core + K2.DisplayPad.Satellite)
- `K2.DisplayPad.sln` — DisplayPad solution (K2.DisplayPad + K2.Core + K2.DisplayPad.Satellite)
- `build-and-run.bat` — build+run of K2.DisplayPad
- `build-check.bat` — compiles both solutions and writes errors/warnings
  to `build-check.log` (quick compilation check)
- `stop-basecamp.bat` — terminates Base Camp processes to free the drivers
- `stop-k2.bat` — terminates ghost instances of K2.App / K2.DisplayPad left
  after a crash (e.g. NullRef in InitializeComponent). Run when
  `build-check.bat` fails with MSB3027 "The file is locked by: K2.App (PID)"
- `setup-python-embed.bat`/`.ps1` — downloads/installs Python embeddable x64
  in `lib/python-embed/` (runtime for key Python scripts)
- `README.md` — K2.DisplayPad docs + API↔SDK mapping + Python scripts
- `DISTRIBUTION.md` — what is redistributable and what is not, end-user setup
- `.gitignore` — excludes `_reference/`, build output, logs
- `lib/` — **redistributable natives**: `DisplayPad.SDK.dll`+`.xml`,
  `DisplayPadSDK.dll` (x64) — all MIT, with `LICENSE.DisplayPad.SDK.txt`
- `lib/pybridge/` — Python bridge: `k2.py` (module imported by scripts),
  `k2_runner.py` (bootstrap launched by K2), `examples/`, `README.md`
- `lib/python-embed/` — Python embeddable x64 (created by `setup-python-embed`,
  excluded from version control)

### `K2.App/` — guscio unificato (WPF, x86) — moduli MacroPad + Everest — usa `K2.Core`
- `K2.App.csproj` — referenzia `K2.Core` + `Microsoft.Data.Sqlite`;
  `App.xaml(.cs)` entry point (`WriteLog`, `NativeDependencyResolver`)
- `MainWindow.xaml(.cs)` — tab MacroPad + tab Everest Max; verifica
  `MacroPadSDK.dll` all'avvio
- `MainWindow.Keys.cs` — partial MacroPad: griglia **2×6 ruotabile**
  (0/90/270 via `MacroPadLayout`; modello sempre in indici fisici), dialog
  azioni, rimappa matrice-tasti, persistenza, esecuzione. Combo orientamento
  salvata in Settings (`macropad.rotation`).
- `MainWindow.MacroLed.cs` — partial MacroPad: pannello **"Illuminazione LED"**
  (combo effetto/velocità/direzione, 3 color-picker, slider luminosità,
  sync cross-profilo, backlight ON/OFF, reset, salva su flash). Pilota lo
  slot del device selezionato; stato persistito in Settings (`macroled.*`).
- `Services/MacroPadLayout.cs` — geometria griglia 2×6 + rotazione
  (mirror di `DisplayPadLayout`, ma senza rotazione icone: il MacroPad non ha
  schermi per-tasto). Enum `MacroPadRotation`.
- `MainWindow.ActionHost.cs` — partial: MainWindow come `IActionHost` del MacroPad
- `MainWindow.MediaDock.cs` — partial Everest: pannello Media Dock (orologio,
  PC monitoring CPU/RAM, effetti LED barra, screensaver, reset). Timer
  DispatcherTimer 1s (clock) e 2s (PC info). Win32 GlobalMemoryStatusEx per RAM.
- `MainWindow.CustomLighting.cs` — partial Everest: pannello "Custom Lighting
  (per-key)". Paint mode + color picker + overlay sui tasti keyboard. SDK:
  SwitchToCustomizeEffect + ChangeCustomizeEffect (171 LED). Persistenza JSON.
- `MainWindow.DisplayDial.cs` — partial Everest: pannello Display Dial (8 pagine
  dial come toggle 2 colonne x 4, formato 12/24h + stile analog/digital, enable/
  disable separati per screensaver e auto-off, combo funzione screensaver, menu
  color, reset). Legge/
  scrive FW_EXTEND_INFO via EverestService. Persistenza in EverestStore `dial.*`.
- `MainWindow.Macro.cs` — partial: pannello **Macro**, sezione top-level a sé
  stante (non più dentro Everest), bottone dedicato nella barra in alto a
  sinistra delle Impostazioni (`BtnMacroTab`/`PnlMacro`). Layout in colonne
  stile Base Camp: libreria macro a sx; colonna Devices/Delay/Playback
  (RadioButton "a pillola", stile `MacroOptionRowStyle`) impilata; lista
  Inputs registrati (`MacroInputRow`) al centro; sezione **Assigned to**
  a destra dei comandi (query `GetKeysByAction` su MacroPadStore/EverestStore/
  DisplayPadStore per `ActionType=="macro"`). `ButtonActionDialog` espone
  "Play macro" come tipo di azione assegnabile a QUALSIASI tasto di
  QUALSIASI device (MacroPad/Everest/DisplayPad, dentro K2.App) — il picker
  legge `IActionHost.ListMacroNames()`, l'esecuzione passa da
  `ButtonActionEngine`'s case `"macro"` → `IActionHost.PlayMacro(name)` →
  `MainWindow.ListAllMacroNames()`/`PlayMacroByName()` in questo file (lo
  standalone K2.DisplayPad, senza libreria macro, implementa entrambi come
  no-op). CRUD macro, registrazione (MacroRecorder, con recordKeyboard/
  recordMouse separati), riproduzione (MacroPlayer), import da BaseCamp.db.
  Persistenza in MacroStore (tabella Macros in everest.db).
- `MainWindow.Layout.cs` — partial Everest: layout dinamico tastiera. Riposiziona
  dock (CvsEvDock) e numpad (CvsEvNumpad) a sx/dx del corpo tastiera in base a
  byMMDockPlug/byNumpadPlug (0=nascosto, 1=sx, 2=dx). **Check aggancio**
  (2026-07-12): oltre al check all'apertura di K2 (`EvAutoOpen`), `TcDevices_
  SelectionChanged` (`MainWindow.xaml.cs`) chiama `UpdateKeyboardLayout()` +
  `StartEvAccessoryPoll()` all'apertura della tab Everest Max, che avvia un
  `DispatcherTimer` 3s (`_evAccessoryPollTimer`) fermato da
  `StopEvAccessoryPoll()` non appena si lascia la tab — a differenza del
  poller di Everest 60 (`Ev60RefreshStatus`, sempre attivo), qui il polling
  gira SOLO mentre la tab è selezionata (richiesta esplicita utente).
- `MainWindow.NumpadDisplayKeys.cs` — partial Everest: 4 display key del numpad.
  Click → carica immagine 72×72 (UploadNumpadImage). Right-click → configura azione
  (ButtonActionDialog). Miniature inline. Persistenza EverestStore `ndk.{i}.*`.
- `MainWindow.Everest.cs` — partial Everest: cattura tasti on-demand, lista
  tasti, dialog azioni condiviso, esecuzione (motore K2.Core dedicato) +
  **pannello "Illuminazione RGB"** (Expander nel tab Everest): combo effetto
  (Static/Breath/Wave/ReactiveA-B-C/Yeti/Tornado/Matrix/Off), velocità
  (lenta/normale/veloce), direzione (CW/CCW), slider luminosità 0..100
  (quantizzato 0/25/50/75/100), 3 color-picker (WinForms `ColorDialog`),
  checkbox "sincronizza tra profili", pulsanti backlight ON/OFF e reset
  effetti. Stato persistito globalmente in `Settings` (chiavi `rgb.*`),
  riapplicato all'apertura del driver.
- `MainWindow.Everest60.cs` — partial: shell del tab **Everest 60**, stesso
  layout a 3 colonne di Makalu: sidebar SECTIONS ordinata **Key Binding**
  (`Everest60KeyBindingPanel`) / **Lighting** (RGB preset + Side Ring + Key
  Lighting per-tasto, fuse in `Everest60RgbPanel`) / **Settings**
  (`PnlEv60Settings` — Keycap Appearance **+ Keycap Style** funzionanti,
  importati da Everest Max e applicati anche al numpad decorativo; Layout
  disabilitato) — stesso ordine di Everest Max, 2026-07-11 su richiesta
  utente (sezione di default all'apertura resta "Lighting", non "Key
  Binding": evita di aprire eager la sessione SDK non ancora verificata su
  hardware). `Ev60Section_Changed`/`ShowEv60Section` (il click su
  `RbEv60SecKeyBinding` apre anche lazy la sessione SDK `_ev60Sdk.Open()` —
  non eager all'avvio, a differenza del path raw-HID di lighting).
  **Tastiera interattiva a 64 tasti** (`BuildEverest60KeyboardOverlay`,
  layout da `Everest60KeyboardLayout.MainBoard`, Tag = indice LED 0-63) su
  `Canvas CvsEv60Keyboard` con sfondo `Assets/everest60_board.png` (pannello
  piatto, non foto 3D — trovato identico in Base Camp come
  `Everest60/keyboardv2.png`), + **numpad accessorio** (`CvsEv60Numpad`,
  `Assets/everest60_numpad.png` = `Everest60/numpadv2.png`, layout
  `Everest60KeyboardLayout.Numpad` stimato a mano, `IsHitTestVisible=False`:
  ancora nessun protocollo di SCRITTURA/remap per i suoi 17 tasti — ma la
  LETTURA (LED preview live) sì: **2026-07-12, mappatura dei 17 indirizzi
  hardware trovata via cattura USBPcap reale di Base Camp (non indovinata,
  vedi CHANGELOG)** — `Everest60Protocol.NumpadLedIndex` (stesso ordine di
  `Everest60KeyboardLayout.Numpad`), applicato in
  `MainWindow.Everest60.cs::OnEv60ColorsUpdated` insieme ai 64 tasti della
  board. Il numpad condivide lo stesso schema di indirizzamento fisico
  della board principale (i suoi LED occupano gli slot liberi nelle stesse
  righe/colonne, es. indirizzi 38-41 subito dopo Backspace=34). L'anello
  perimetrale del numpad stesso (5 indirizzi 181-185 trovati, probabilmente
  parte di un anello più esteso, distinto da `SideLedIndex` 126-169 della
  board) resta non implementato — fuori scope, nessun overlay grafico per
  quei LED nell'UI attuale. **Presenza e lato (sinistra/destra) sono invece
  rilevati automaticamente** (2026-07-11,
  smentendo una conclusione precedente in questa stessa mappa): poll ogni
  3s in `Ev60RefreshStatus` → `Everest60SdkService.QueryNumpadPosition` →
  `Everest60SdkNative.GetSubDeviceInfo(1, ...)`, stesso meccanismo di Base
  Camp stesso (confermato via decompile). `ApplyEv60NumpadPosition` sposta
  `CvsEv60Numpad` nell'ordine dei figli di `SpEv60Layout` e specchia solo
  `BrushEv60NumpadBg` (non l'intero Canvas) per il lato destro — nessun
  asset "numpad destra" esiste in Base Camp, riusato lo stesso PNG
  specchiato. Il vecchio toggle manuale "Numpad attached" è stato rimosso
  (`AppSettings.Everest60NumpadAttached` eliminato, era dead code dopo
  l'auto-detect). Click su un tasto
  della tastiera principale (`Ev60KeyboardButton_Click`) instrada in base
  alla sezione attiva (`_activeEv60Section`): se è Key Binding, delega a
  `Ev60KeyBindingPanel.SelectKey`; altrimenti a `Ev60RgbPanel.TryPaintKey`
  (paint mode di Key Lighting). Colonna destra (Profilo/Import/Export
  disabilitati, Rinomina device via `AppSettings.Everest60DeviceName`).
  **Importante**: `RbEv60SecLighting.IsChecked` impostato in codice
  (`InitEv60SectionNav`), mai `IsChecked="True"` in XAML — stessa ragione di
  `RbMkSecRgb` in `MainWindow.Makalu.cs` (vedi lì e CHANGELOG 2026-07-10).
  **LED preview live** (2026-07-11): `_ev60LedPoller` (`Everest60LedColorPoller`)
  avviato/fermato da `UpdateEv60LedPreviewActive` quando la sezione Lighting
  diventa visibile/nascosta (stesso schema di `UpdateEverestLedPreviewActive`/
  `UpdateMpLedPreviewActive` in `MainWindow.LedPreview.cs`); `OnEv60ColorsUpdated`
  traduce indice logico tasto (0-63) → indirizzo hardware LED via
  `Everest60Protocol.LedIndex` prima di colorare l'overlay. Mentre il paint
  mode di Key Lighting è attivo, un tasto appena dipinto ma non ancora
  "Applicato" mantiene il colore dipinto invece del colore live (stesso
  schema del salto `IsHighlighted` del MacroPad). Vedi CHANGELOG per la
  scoperta completa (`GetColorData2`).
- `Everest60RgbPanel.xaml(.cs)` — `UserControl` figlio diretto di
  `MainWindow` (non annidato), sezione **Lighting**: preset effetti
  (Off/Static/Breathing/Wave/Tornado/Reactive/Yeti + velocità/luminosità/
  2 colori/rainbow/direzione, `CapsFor`/`UpdateEv60Capabilities` come
  l'Everest Max), Side Ring (44 LED perimetrali) e **Key Lighting** (editor
  illuminazione per-tasto sui 64 tasti principali: paint mode, brush color,
  Applica → `Everest60Service.SetCustomKeys` → `Everest60Protocol.SendCustom`;
  preview **live** via poll SDK (`Everest60LedColorPoller`/`GetColorData2`,
  vedi voce dedicata più sotto e CHANGELOG 2026-07-11 — smentisce la
  precedente nota "software-only, nessun readback": esiste, va solo per una
  strada diversa dalla scrittura) — Custom mode indirizza tasti+anello
  insieme, applicare l'uno spegne l'altro) — le tre, un tempo sezioni
  SECTIONS separate, ora fuse in un unico `Grid x:Name="SecLighting"`
  scrollabile (2026-07-11, su richiesta
  utente). Bridge verso `MainWindow.Everest60.cs` (che possiede i `Button`
  della `Canvas`, la UserControl no): `TryPaintKey(ledIndex, out color)`
  chiamato dal click handler, evento `CustomKeysCleared` per far ripulire a
  MainWindow i tasti quando si preme "Clear" — stesso schema di
  `MkDpiRemap.SelectRemapButton` per Makalu. Campo `_ev60Suppress` di default
  `true` (difesa in profondità, stesso pattern di `MakaluDpiRemapPanel`).
  **Persistenza cross-sessione FATTA (2026-07-13)**: stato (effetto/colori/
  side ring/colori per-tasto) salvato per profilo in `Everest60Store`,
  riapplicato allo switch profilo via `Ev60ReloadProfile` — vedi voce
  dedicata "Gestione profili" più sotto. **Non incluso**: layout multi-lingua
  (un solo layout ANSI-like per ora).
- `Everest60KeyBindingPanel.xaml(.cs)` — `UserControl` figlio diretto di
  `MainWindow`, sezione **Key Binding** (2026-07-11): riusa la stessa
  `Canvas CvsEv60Keyboard` già disegnata per il lighting come selettore
  tasto sorgente (`SelectKey(ledIndex, label)` chiamato da
  `MainWindow.Everest60.cs`), poi un `ComboBox` "Tipo" sceglie fra Remap
  Key/Fn Layer/Shortcut/Media e scrive **direttamente nel firmware** via
  `Everest60SdkService` (`ChangeKey`/`ChangeFnKey`/`ChangeShortcutKey`/
  `SetMediaKey`) — **nessun coinvolgimento di `IActionHost`/
  `ButtonActionEngine`**, stesso pattern del remap tasti Makalu (Base Camp
  stesso non ha un passthrough "K2Action" per l'Everest 60: solo le sue
  categorie built-in). Pulsanti Reset (ChangeKey→255) e Save (flash
  permanente). **Non verificato su hardware fisico** (nessun device
  disponibile nella sessione che l'ha scritto) — vedi CHANGELOG 2026-07-11
  per la traccia completa del reverse engineering.
- `Models/Everest60KeyboardLayout.cs` — `MainBoard` (64 `KeyDef`, geometria
  portata 1:1 da `BaseCampLinux/shared/ui_helpers.py` `_build_kb60_layout()`,
  riscalata da 0.82 a scala nativa K2 30px/2px; `MatrixId` riusato per
  l'indice LED 0-63, verificato contro `Everest60Protocol.LedIndex` — stesso
  ordine, due fonti indipendenti concordi) e `Numpad` (layout stimato a mano,
  solo decorativo, `MatrixId=-1`).
- `Services/Everest60SdkNative.cs` — P/Invoke `Everest360_USB.dll` (SDK
  vendor, non raw-HID), per **Key Binding**: `ChangeKey`/`ChangeFnKey`/
  `ChangeShortcutKey`/`SetSingleMacroContent`/`SetKeyCallBack`/`EnableKeyFunc`/
  `APEnable`/`ResetKeys`/`SaveFlash` — parametri primitivi (int/bool), NON
  struct opache come gli export di lighting della stessa classe (`ChangeEffect`
  ecc., mai reverse-engineered — la nota architetturale su `Everest60` resta
  valida solo per quella parte). Confermato via decompile 2026-07-11 di
  `BaseCamp.UI.dll`/`BaseCamp.Service.exe` (vedi CHANGELOG). Mirror di
  `EverestSdkNative.cs`. **Più `GetColorData2(IntPtr, ushort)`** (stessa
  sessione, seconda scoperta 2026-07-11): export di READBACK live dei colori
  LED — 576 byte = `FWColor[192]`, indicizzato per indirizzo hardware LED
  (stessa numerazione di `Everest60Protocol.LedIndex`/`SideLedIndex`), NON
  serve alcun priming (a differenza di Everest Max) — vedi CHANGELOG per la
  traccia di decompile completa (`BaseCamp.Service.exe` + `BaseCamp.UI.dll`
  `EverestMiniController.GetColorData`).
- `Services/Everest60SdkService.cs` — facade open-once (come `EverestService`,
  non per-call come `Everest60Service`): apre `Everest360_USB.dll` lazy alla
  prima visita della sezione Key Binding (o eager all'avvio per numpad
  detect — vedi sopra). `TryGetColorData` (2026-07-11) rispecchia 1:1 la
  sequenza alloc/call/copy/free decompilata di `Everest60.GetColorData`,
  riusa la stessa sessione SDK. **Coesistenza con il path raw-HID di
  lighting non verificata su hardware** — commentato esplicitamente nel
  codice, resta valida anche per il nuovo poll colori (stesso rischio, nuovo
  consumatore).
- `Services/Everest60LedColorPoller.cs` — poll LED live per l'Everest 60
  (2026-07-11), mirror dedicato di `LedColorPoller` (Everest Max/MacroPad):
  `DispatcherTimer` 300ms (cadenza reale di Base Camp per questo device,
  vedi CHANGELOG — più lenta dei 120ms altrove per prudenza, coesistenza
  SDK/raw-HID non verificata), avvia/ferma da `MainWindow.Everest60.cs` in
  base alla visibilità della sezione Lighting.
- `Services/Everest60RemapData.cs` — catalogo `KeyCatalog` (label → DLLKeyId,
  64 tasti fisici, estratto 2026-07-11 da
  `Everest60Operations.GetEverest60KeyBindings_English` decompilato, valori
  letti da IL letterale non inferiti) + `LedIndexToDllKeyIdArray` (ponte
  indice-LED→DLLKeyId, corrispondenza posizionale verificata riga per riga
  contro `Everest60KeyboardLayout.MainBoard`) + costanti bitmask modificatori
  (ctrl=1/shift=2/alt=4/win=8) + `MediaActions` (**codici non confermati**,
  ordinamento segnaposto 1-7).
- `Services/Everest60HidNative.cs` — P/Invoke raw HID (`hid.dll`+`setupapi.dll`,
  stesso schema di `EverestHidNative`/`DpHidNative` ma senza reader thread:
  le feature report sono transfer di controllo sincroni via
  `HidD_SetFeature`/`HidD_GetFeature`). Enumera l'interfaccia `mi_02`
  (VID `0x3282`, PID `0x0005` ANSI/`0x0006` ISO).
- `Services/Everest60Protocol.cs` — porting 1:1 di BaseCampLinux
  `devices/everest60/controller.py`: enum `Effect`/`ColorMode`, tabelle
  direzione Wave/Tornado (valori coincidenti con quelli già noti
  dell'Everest Max — riscontro incrociato tra due reverse-engineering
  indipendenti), `SendMode` (preset effects) e `SendCustom` (per-key +
  side ring, usato oggi solo per l'anello).
- `Services/Everest60Service.cs` — facade find-open-send-close per chiamata
  (feature report = transfer stateless, nessuna sessione persistente
  necessaria, come l'`open_device()`/`close()` per comando di BaseCampLinux).
- `Services/Everest60Store.cs` (2026-07-13) — persistenza profili Everest 60
  (`everest60.db`), stesso pattern `Settings` k/v + JSON blob di
  `EverestStore`: tabella `KeyBindings` (Profile,LedIndex,Mode,Value,
  ModifierMask) + blob `profile.{slot}.lighting`. Vedi "Gestione profili"
  nel file index principale.
- `Services/Ev60ProfileExporter.cs` (2026-07-13) — export XML profilo
  Everest 60 (K2/BC-compatibile), mirror di `EvProfileExporter.cs`.
- `MainWindow.Makalu.cs` — partial: tab **Makalu** (mouse, raw-HID, nessun
  SDK — vedi nota architetturale sopra). Shell del layout a 3 colonne:
  sidebar SECTIONS (RGB/DPI/Remap/Settings, `MkSection_Changed`/
  `ShowMkSection` toggolano le sezioni dentro `MkRgbSettings`/`MkDpiRemap`),
  immagine mouse con hotspot cliccabili (`BuildMkHotspots`; per il modello 67
  posizioni pixel-misurate su `Assets/makalu_mouse.png` allineate al diagramma
  numerato ufficiale Mountain, 2026-07-11 — vedi CHANGELOG; per Max ancora
  stimate a occhio, nessun diagramma di riferimento visto, click → seleziona
  il tasto nella sezione Remap), colonna destra (Profilo/Import/Export disabilitati —
  nessuna persistenza multi-profilo per questo device — Rinomina device
  funzionante via `AppSettings.MakaluDeviceName`). **Importante**:
  `RbMkSecRgb.IsChecked` è impostato in `InitMkSectionNav()` (codice), MAI
  con `IsChecked="True"` in XAML — quest'ultimo causava un crash fatale del
  CLR (falso allarme "bug JIT x86", root-causato 2026-07-10 con WinDbg+SOS:
  era solo `RadioButton.Checked` che scattava durante `InitializeComponent()`
  prima che `MkRgbSettings` fosse assegnato — vedi CHANGELOG). **Nessuna
  persistenza cross-sessione** (stessa scelta di scope di Everest 60).
- `MakaluDpiRemapPanel.xaml(.cs)` — `UserControl` figlio diretto di
  `MainWindow` (non annidato — vedi nota nel file) che ospita DPI+Remap+
  overlay di conferma. `Init`/`UpdateDeviceInfo`/`SelectRemapButton`
  `internal`, prende un `MakaluService` iniettato dal chiamante. Campo
  `_mkSuppress` di default `true` (non `false`), azzerato solo a fine
  `Init()`: qualunque handler XAML (`SldMkDpi_ValueChanged` ecc.) che
  scattasse durante il caricamento del BAML (es. `Minimum="50"` che forza
  la coercizione di `Value` da 0 a 50 su uno `Slider` appena costruito) è
  quindi un no-op invece di un null-ref — root-causato 2026-07-10, vedi
  CHANGELOG per i dettagli completi (stesso bug di `RbMkSecRgb` sopra, in un
  punto diverso).
- `MakaluRgbSettingsPanel.xaml(.cs)` — `UserControl` figlio diretto di
  `MainWindow`, ospita RGB+Settings. Stesso pattern `_mkSuppress = true` di
  default di `MakaluDpiRemapPanel` (difesa in profondità, non risulta
  necessario oggi ma costa nulla).
- `Services/MakaluRemapData.cs` — tabelle statiche condivise (nomi
  tasti/categorie/funzioni remap per modello 67 vs Max), usate sia da
  `MainWindow.Makalu.cs` (tooltip hotspot) sia da `MakaluDpiRemapPanel.xaml.cs`
  (lista bottoni remap).
- `MakaluCustomRgbWindow.xaml(.cs)` — finestra dedicata: editor 8 LED
  (4 sinistra + 4 destra, layout fisico del mouse) con color-picker per LED
  (WinForms `ColorDialog`, un click = un colore, non un canvas multi-select
  come il riferimento Python) + slider luminosità + Apply.
- `Assets/makalu_mouse.png` — foto top-down Makalu 67 (da Base Camp
  `wwwroot/images/makalu67.png`), usata come thumbnail 72×72 accanto allo
  stato di connessione nel tab Makalu.
- `Services/MakaluHidNative.cs` — P/Invoke raw HID (`hid.dll`+`setupapi.dll`,
  stesso schema di `Everest60HidNative`). Enumera l'interfaccia `mi_01`
  (VID `0x3282`, PID `0x0003` Makalu 67/`0x0002` Makalu Max). **Verificato
  su hardware reale 2026-07-10**: l'euristica di selezione interfaccia
  funziona (RGB applicato con successo sul device fisico).
- `Services/MakaluProtocol.cs` — porting 1:1 di BaseCampLinux
  `devices/makalu67/controller.py`: enum `Effect`, lighting (preset +
  custom 8-LED), polling rate/debounce/lift-off/angle-snapping, DPI
  get/set (5 livelli), button remap + sniper. Tutte le costanti (report id
  0xA1, mappe funzione remap, range DPI 50–19000 step 50, debounce
  2/4/6/8/10/12ms) copiate identiche dalla fonte. **DPI/remap non wired in
  UI** (vedi sopra) — codice pronto per quando si riprenderà l'indagine.
- `Services/MakaluService.cs` — facade find-open-send-close per chiamata,
  stesso pattern di `Everest60Service`. `DeviceInfo` incapsula
  modello/label/numero-tasti/DPI-minimo (67 vs Max differiscono). Metodi
  DPI/remap presenti ma non chiamati da nessuna UI (vedi sopra).
- `Services/MakaluStore.cs` (2026-07-13) — persistenza profili Makalu
  (`makalu.db`), stesso pattern `Settings` k/v + JSON blob di
  `EverestStore`: tabella `Remap` (Profile,ButtonIndex,FunctionName) + blob
  `profile.{slot}.lighting`/`.dpi`/`.settings`. Vedi "Gestione profili" nel
  file index principale.
- `Services/MkProfileExporter.cs` (2026-07-13) — export XML profilo Makalu
  (K2/BC-compatibile), mirror di `EvProfileExporter.cs`.
- `MainWindow.UsbRecorder.cs` — partial: pannello **"Registratore USB"**
  (Expander nel tab Everest). Orchestra `UsbRecorder` (tshark) per catturare
  i pacchetti HID inviati da Base Camp alla tastiera, mostra hex dump dei
  risultati, salva report `.txt`. UI: combo interfaccia USBPcap, etichetta
  file, Start/Stop, risultati con hex dump.
- `EverestActionHost.cs` — adattatore `IActionHost` per l'Everest (delegati)
- `Models/MacroPadKey.cs` — tasto della griglia MacroPad (bindabile)
- `Models/EverestKey.cs` — tasto Everest (identità = codice matrice)
- `Models/KeyLabelMap.cs` — VK → alt-label (shifted char) per layout AnsiUs/IsoIt;
  usato da `BuildEverestKeyboardOverlay` per la label a due righe sui tasti Everest
- `Services/MacroPadSdkNative.cs` — P/Invoke raw su `MacroPadSDK.dll` (apertura,
  device, callback tasti, AP, + **illuminazione LED**: enum `EffectIndex`/
  `SpeedT`/`DirectionT`/`BrightT`, struct `FWColor`/`EffData` (62B, identica
  all'Everest) + **`FWBColor`/`BlockData`/`ChangeBlockEffect`** (porting 2026-07-09
  da `EverestSdkNative.BlockData`, stessa famiglia firmware — Wave/Tornado NON
  passano da `ChangeEffect`, lo rifiuta: vanno su `ChangeBlockEffect`, scoperto
  via decompile di `BaseCamp.UI.dll`, vedi CHANGELOG 2026-07-09), `ChangeEffect`/
  `ChangeBlockEffect`/`ResetEffects`/`SetSyncEffect`/`SetSyncAcrossProfiles`/
  `GetSyncAcrossProfiles`/`SetMainBrightness`/`EnableKeyFunc`/`SaveFlash` —
  **tutte con `uint ID` finale** = slot device. **`EffData` è `unsafe struct`
  con campi inline** (`colorLv0/1/2` + `fixed byte byData[43]`), NON
  `[MarshalAs(ByValArray)] FWColor[]`/`byte[]` — quella versione era
  blittable-solo-in-apparenza: `ChangeEffect` tornava `true` ma il wire restava
  stale, nessun effetto (nemmeno "Off") funzionava finché non è stata allineata
  al layout della `EffData` dell'Everest (già corretto in una sessione
  precedente, mai riportato sul MacroPad). Vedi CHANGELOG 2026-07-09,
  seconda entry. **Qualsiasi nuova struct P/Invoke passata by-value a
  `MacroPadSDK.dll`/`SDKDLL.dll` deve usare questo schema** (campi inline +
  `fixed`), mai array marshalati.)
- `Services/MacroPadService.cs` — facade .NET con eventi tipizzati + facade
  effetti LED per-slot (`SetEffect`/`ResetEffects`/`SetSyncAcrossProfiles`/
  `SetBacklight`/`SaveFlash`); enum `Effect`, `speedByte`/`directionByte` int
  (allineato a `EverestService.SetEffect`, non più enum `Speed` a 3 valori).
  `SetEffect` instrada Wave/Tornado su `ChangeBlockEffect`, il resto su
  `ChangeEffect`; **nessun `APEnable` intorno** (verificato: Base Camp non lo
  chiama mai per il MacroPad in `BaseCamp.UI.dll`, a differenza dell'Everest).
- `Services/MacroPadStore.cs` — SQLite azioni tasti + mappa matrice (MacroPad)
- `Services/EverestSdkNative.cs` — P/Invoke raw su `SDKDLL.dll` (la nativa
  della tastiera Everest Max): apertura, info device, AP, profilo, callback,
  **illuminazione RGB** (EffData/BlockData/ChangeEffect/ChangeBlockEffect),
  **numpad display keys** (SetDisplayKeyPic/GetDisplayKeyPic/ResetNumpad/
  ResetNumpadPic/StartPicUpdate + struct PicUpdateInfo),
  **media dock** (ChangeBarEffect/ChangeBarCustomize/GetBarEffectData/SetEQInfo/
  SetClockInfo/GetClockInfo/SetPCInfo/ResetMMDock/ResetMMDockPic/SetExtendInfo +
  struct BarData/CustomStatic/BarReadData/EQ_DATA).
- `Services/EverestImageUploader.cs` — helper conversione immagini → RGB565 +
  upload via StartPicUpdate. Target: MMDock screensaver 240×204, numpad strip
  128×32, numpad square 72×72. Bitmap→LockBits→RGB565 little-endian.
- `Services/EverestService.cs` — facade .NET single-device (no HWND, no plug-msg)
- `Models/KeyboardMacro.cs` — modello macro: MacroInput (eventi key/mouse/text),
  MacroDefinition (macro completa con delay/playback options), enum MacroPlayback/
  MacroDelay. Import da formato BaseCamp via FromBaseCamp().
- `Models/MacroInputRow.cs` — view model di una riga della lista Inputs nel
  pannello Macro (glyph/label/ms/press-o-release/indice sorgente per riordino).
- `Models/MacroAssignment.cs` — view model per la sezione "Assigned to" del
  pannello Macro (key label + sottotitolo device/profilo).
- `Services/MacroStore.cs` — persistenza macro in tabella Macros (stesso DB
  di EverestStore). CRUD, ImportAll, ReadFromBaseCampDb per import da BC.
- `Services/MacroRecorder.cs` — hook globale keyboard+mouse (SetWindowsHookEx
  WH_KEYBOARD_LL/WH_MOUSE_LL). Cattura keydown/keyup/mousedown/mouseup/mousemove.
- `Services/MacroPlayer.cs` — riproduzione macro via SendInput Win32. Delay
  recorded/nodelay/custom, playback once/repeatN/whileHeld/toggle.
- `Services/EverestStore.cs` — SQLite azioni tasti Everest, identità = matrice
- `Services/UsbRecorder.cs` — registratore USB: localizza `tshark.exe`, elenca
  interfacce USBPcap, avvia/ferma cattura come processo figlio, parsa pcapng.
  Confronto due catture con `CompareCaptures()` per trovare i delta.
- `Services/PcapParser.cs` — parser pcap-ng in C# (port di
  `_reference/tools/parse_usb_pcap.py`). Filtra pacchetti OUT interrupt/bulk,
  hex dump. Nessuna dipendenza esterna.
- `Services/BaseCampDbImporter.cs` — import profili da BaseCamp.db per tutti i device:
  **DisplayPad**: legge `Profiles`+`DisplayPadLayerBidings`.
  **Everest**: legge `Profiles` (DeviceType="Everest") + `EverestKeyBidings`; chiavi regolari
  (IsTouchKey=false) → EverestStore; touch key (IsTouchKey=true, LCD numpad) → immagini su
  disco + EverestStore settings `ndk.{i}.*`.
  **MacroPad**: legge `Profiles` (DeviceType="MacroPad") + `EverestKeyBidings` (stesso table!);
  DLLMatrixIndex 170-179/220-221 → indice 0-11 → MacroPadStore.
  `TranslateAction` condiviso per tutti i device.
  **Makalu** (2026-07-13): `Profiles` (DeviceType="Makalu", **non verificato su dati
  reali** — nessun Makalu mai paired con questo BaseCamp.db) + `MakaluKeyBindings`/
  `MakaluLightings`/`MakaluSettings`/`DPILevels` → `MakaluStore`. Traduzione
  bottoni via `TranslateMakaluRemapFunction` (target = stringa funzione
  `MakaluRemapData`, NON un'azione K2.Core — Makalu non ha `IActionHost`).
  **Attenzione**: il blocco preesistente sopra ("MacroPad — MakaluKeyBindings
  table") in realtà legge una tabella VUOTA nel DB reale (0 righe) — i dati
  veri del MacroPad vivono in `EverestKeyBidings`, non qui; segnalato ma non
  corretto in questa sessione (fuori scope).
  **Everest 60** (2026-07-13): `Profiles` (DeviceType="EverestMini",
  **confermato su dati reali** — 232 righe key-binding + 9 lighting trovate)
  + `Everest60KeyBidings`/`Everest60Lightings` → `Everest60Store`. Lighting
  import ad alta confidenza; Key Binding import best-effort (solo match
  esatti contro `Everest60RemapData.KeyCatalog` — l'unico profilo reale
  visto ha solo legende factory Fn-layer, non veri remap utente).
  `ParseBcColor` (nuovo, condiviso) parsa sia `#RRGGBB` sia `rgb(r, g, b)`
  (entrambi i formati coesistono nella stessa tabella nei dati reali).
- `Services/NativeDependencyResolver.cs` — risolve a runtime le DLL native
  NON ridistribuibili: `MacroPadSDK.dll` (MacroPad), `SDKDLL.dll` (Everest Max)
  e **`Everest360_USB.dll`** (Everest 60, solo Key Binding + rilevamento
  numpad — aggiunta 2026-07-12: mancava dalla lista dal giorno in cui è
  stato introdotto `Everest60SdkNative`, causa reale di un bug "numpad non
  rilevato" che sembrava un problema di sequenza di init — vedi CHANGELOG).
  Cerca accanto all'exe, in `K2_BASECAMP_DIR`, o in un'installazione di
  Base Camp (registro + percorsi tipici). **Qualsiasi nuovo P/Invoke verso
  una DLL non ridistribuibile di Base Camp deve essere aggiunto a
  `BaseCampNativeDlls` in questo file**, altrimenti il CLR usa il default
  OS search path e la chiamata fallisce con `DllNotFoundException` silenziosa
  (nessun log "Paths tried" a differenza delle DLL già in lista).
- `README.md` — doc moduli MacroPad + Everest Max, azioni sui tasti, vincolo x86

### `K2.Core/` — libreria CONDIVISA: motore azioni + ponte Python (WPF, x86;x64)
- `K2.Core.csproj` — libreria WPF; `<Platforms>x86;x64</Platforms>`; porta con
  sé (Content transitivo) gli helper Python di `lib/pybridge/`
- `IActionHost.cs` — astrazione device-specific (log, profilo/device correnti,
  `SwitchProfile(targetKey?, target)` con targeting cross-device, `ListProfileTargets()`,
  GetButtons, PressButton) + record `HostButton`/`ProfileTargetOption`
- `ButtonActionEngine.cs` — motore di esecuzione delle azioni dei tasti
- `PyBridge.cs` — incapsula server RPC + servizio script + dispatcher RPC
- `ActionExecutor.cs` — esecuzione tipi di azione non triviali (oscmd/media/mouse/multi…)
- `SendKeysTranslator.cs` — traduce scorciatoie tipo "Ctrl+Shift+A"
- `K2RpcServer.cs` — API HTTP loopback (127.0.0.1) per gli script Python
- `PythonScriptService.cs` — lancio script in processo + streaming output
- `PythonRuntimeLocator.cs` — ricerca interprete embeddable + `k2_runner.py`
- `PyScriptPayload.cs` — codifica/decodifica azione "pyscript" (JSON)
- `ProfileTargetPayload.cs` — codifica/decodifica azione "profile" multi-device (JSON)
- `BrowserActionPayload.cs` — codifica/decodifica azione "browser" (browser scelto/path/URL, JSON)
- `BrowserDetector.cs` — rileva Chrome/Edge/Firefox/Opera/Brave installati (registro `App Paths`)
- `IconImageGenerator.cs` — genera l'immagine di un tasto da un'azione "exec" (icona
  eseguibile via Shell `IShellItemImageFactory`, fallback `Icon.ExtractAssociatedIcon`) o
  "folder" (glyph Segoe MDL2 + nome cartella)
- `ButtonActionDialog.xaml(.cs)` + partial `.Exec.cs`/`.Browser.cs`/`.Profile.cs`/
  `.Simple.cs`/`.Keys.cs` — dialog "Configura azione tasto": pannello dedicato per tipo
  (path+icona+recenti per exec, path+recenti per folder, radio browser rilevati per
  browser, righe multi-device per profile, combo per oscmd/media/mouse, modificatori+combo
  per keys, Script Python invariato)
- `Themes/K2Theme.xaml(.cs)` — **tema scuro condiviso**: palette, stili impliciti
  dei controlli (Button, ComboBox, Slider, CheckBox, ListView, GroupBox,
  StatusBar, ScrollBar, Menu, Tab…) + stile `Window` con barra del titolo
  custom. `.xaml.cs` = handler dei pulsanti minimizza/massimizza/chiudi.
  `.xaml` auto-incluso come `Page` (nessuna modifica al `.csproj`). Font
  app-wide selezionabile (Settings > Font, 2026-07-13): `K2WindowStyle`'s
  `FontFamily` è un `{DynamicResource K2AppFontFamily}` (non più valore
  statico) — il default resta Roboto, ma `K2.Core/Services/FontCatalog.cs`
  (`Options`/`Resolve`/`Apply`) elenca 8 scelte (Roboto, Segoe UI, Inter,
  IBM Plex Sans, Public Sans, Work Sans, Source Sans 3, OpenDyslexic) e
  `FontCatalog.Apply(key)` sovrascrive `Application.Current.Resources
  ["K2AppFontFamily"]` a runtime — ogni finestra con `K2WindowStyle` si
  aggiorna live, nessun riavvio.
  **Bug reale trovato e fisso 2026-07-13** (non un rischio teorico — il
  primo giro del selettore non cambiava nulla in nessuna scelta, Roboto
  incluso): costruire un `FontFamily` embedded da CODICE con
  `new FontFamily("pack://.../#Nome, Segoe UI")` (o via
  `FontFamilyConverter.ConvertFromString` chiamato senza un vero contesto
  di parsing XAML) **fallisce silenziosamente e ripiega sempre sul
  fallback di sistema** ("Segoe UI") — verificato empiricamente con un
  harness WPF a parte per Roboto/Inter/tutti gli altri, con tutte le
  varianti di costruttore provate (1 argomento, 2 argomenti con/senza
  baseUri, converter). Il motivo: il parsing XAML/BAML passa un
  `ITypeDescriptorContext` con la base-URI corretta del file XAML che sta
  venendo caricato — è per questo che il valore DICHIARATO in XAML
  (`K2AppFontFamily` sopra) risolve correttamente, ma la STESSA stringa
  passata a mano da C# no. L'unica API verificata affidabile per caricare
  un font embedded da codice è **`Fonts.GetFontFamilies(Uri folderUri)`**
  (restituisce famiglie reali già risolte dalla collection DirectWrite di
  quella cartella) — `FontCatalog.Apply` ora usa quella, non un
  `FontFamily(string)` costruito a mano. Qualsiasi futura API che debba
  impostare un font embedded a runtime (non in XAML) deve passare da lì,
  non riprovare la via "stringa con pack URI + frammento".
  Scelta persistita in
  `AppSettings.AppFontFamily` (file condiviso `app_settings.json`, quindi
  vale sia per K2.App che K2.DisplayPad — entrambi chiamano
  `FontCatalog.Apply` in `App.OnStartup` prima di creare la finestra). UI
  in `MainWindow.Settings.cs`/`.xaml` (`CmbAppFont`, gruppo "Font" sopra
  "General options").
  **Dimensione per-font** (2026-07-13, richiesta utente: OpenDyslexic
  "troppo grande"): stesso meccanismo di `K2AppFontFamily`, seconda
  risorsa `K2AppFontSize` (`sys:Double`, default 13, xmlns `sys` aggiunto
  all'inizio del file) — ma qui il problema non era solo sulla Window:
  quasi tutti gli stili impliciti dei controlli (Button/ComboBox/TextBox/
  CheckBox/RadioButton/MenuItem/TabItem/...) impostano `FontSize`
  ESPLICITAMENTE a 13 nel proprio Setter, quindi non ereditano affatto dal
  Window — ho sostituito tutte le 13 occorrenze di `FontSize" Value="13"`
  con `{DynamicResource K2AppFontSize}` (le dimensioni delle icone Segoe
  MDL2 Assets, 10/12/15, NON toccate: non c'entrano col font di testo
  scelto). `FontOption.SizeOverride` (nullable) applica un valore diverso
  da `FontCatalog.DefaultFontSize` (13) per famiglia — solo OpenDyslexic
  lo usa oggi (11), perché i suoi glifi rendono visibilmente più grandi
  delle altre famiglie alla stessa dimensione nominale. Verificato con lo
  stesso harness WPF a parte (Window + Button reali, non solo la entry nel
  ResourceDictionary) che sia `FontSize` che `FontFamily` si propagano
  correttamente dopo `FontCatalog.Apply`, non solo sulla Window ma anche
  sui controlli figli con Setter proprio.
  Tutti i font aggiuntivi sono SIL OFL 1.1, static
  Regular/Bold/Italic/BoldItalic scaricati da Google Fonts (gstatic),
  stesso schema di embedding di Roboto ma in sottocartelle
  `Fonts/<Family>/` (Roboto resta flat in `Fonts/` per compatibilità).
  **Inter**: il pacchetto static di Google Fonts ora esiste solo come
  varianti optical-size ("Inter 18pt/24pt/28pt"); usati i file 18pt
  (pensati per testo UI piccolo, coerenti con i 13px di default di K2) ma
  con il nome famiglia interno rinominato da "Inter 18pt" a "Inter" via
  `fontTools` prima dell'embedding, per coerenza col resto del catalogo.
  **OpenDyslexic** (font per dislessia, richiesto esplicitamente
  dall'utente): scaricato da `antijingoist/opendyslexic` su GitHub (SIL
  OFL 1.1), file `.otf` invece di `.ttf` — WPF li carica identicamente via
  pack URI, nessuna differenza di trattamento. Il face Italic upstream ha
  famiglia interna diversa ("OpenDyslexic Italic" invece di
  "OpenDyslexic" + stile Italic), che avrebbe rotto il lookup automatico
  per cartella di WPF: rinominato con `fontTools` per allinearlo a
  Regular/Bold/BoldItalic, stessa correzione già fatta per Inter.
  **`K2SegmentedGroupBorder`/`K2SegmentedButton`** (2026-07-13): stile
  condiviso "bottoni segmentati" (pill row orizzontale di `RadioButton` in
  un `UniformGrid Rows="1"` dentro un `Border` che dà l'arrotondamento via
  `CornerRadius`+`ClipToBounds`) — usato al posto di `ComboBox`/`CheckBox`
  in ogni sezione SECTIONS di ogni device (Makalu/Everest Max/Everest 60/
  MacroPad), ispirato allo screenshot originale di Base Camp. Due varianti:
  gruppi STATICI (≤3 voci fisse, es. Angle Snapping Off/On, Keyboard Color
  Silver/Black) dichiarati direttamente in XAML con `Width` fisso; gruppi
  DINAMICI (es. i picker direzione RGB: Wave = 4 voci, Tornado = 2, il
  conteggio cambia con l'effetto) ricostruiti a runtime via
  `K2.App/Services/SegmentedButtonGroup.Rebuild()` dentro un `UniformGrid`
  senza `Width` fisso (si auto-dimensiona al contenuto). Speed degli
  effetti RGB è invece uno `Slider` ovunque (0-100%, tacche 25% — eccetto
  Makalu, che usa 0-2 perché il byte firmware è letteralmente
  Slow/Medium/Fast, non una percentuale). Vedi CHANGELOG 2026-07-13
  (entrambe le entry) per l'elenco puntuale di cosa è stato convertito.
- `Fonts/Roboto-Regular.ttf`, `Roboto-Bold.ttf`, `Roboto-Italic.ttf`,
  `Roboto-BoldItalic.ttf` — font Roboto (licenza SIL OFL 1.1, vedi
  `Fonts/LICENSE.Roboto.txt`) embedded come `Resource` in `K2.Core.csproj`,
  referenziato da `K2Theme.xaml` via pack URI
  (`pack://application:,,,/K2.Core;component/Fonts/#Roboto`) — non richiede
  che Roboto sia installato sul PC dell'utente finale. Non tocca i font
  espliciti già presenti altrove (KeyCapStyle = replica pixel-perfect delle
  keycap CSS di Base Camp, log/hex viewer = Consolas monospace, icone =
  Segoe MDL2 Assets).
- `Fonts/Inter/`, `Fonts/IBMPlexSans/`, `Fonts/PublicSans/`, `Fonts/WorkSans/`,
  `Fonts/SourceSans3/`, `Fonts/OpenDyslexic/` — font selezionabili aggiuntivi
  (Settings > Font, 2026-07-13), stesso schema Regular/Bold/Italic/BoldItalic +
  `LICENSE.*.txt` (SIL OFL 1.1) di Roboto, ma in sottocartella propria (evita
  collisioni di nome). Vedi `FontCatalog.cs` e la nota su `Themes/K2Theme.xaml`
  sopra.

### `K2.DisplayPad/` — modulo DisplayPad (WPF, x64) — usa `K2.Core`
- `K2.DisplayPad.csproj` — referenzia `K2.Core`; `App.xaml(.cs)` entry point.
  `App.xaml` fonde il tema scuro `K2.Core/Themes/K2Theme.xaml` via pack URI
- `MainWindow.xaml(.cs)` — console DisplayPad: griglia tasti, device, eventi
- `MainWindow.Actions.cs` — partial: `TryExecuteAction` (delega al motore di
  `K2.Core`), cambio profilo, rimappa tasti, import profilo XML
- `MainWindow.ActionHost.cs` — partial: MainWindow come `IActionHost`, avvio del
  `ButtonActionEngine` condiviso
- `Models/ButtonCell.cs` — stato bindabile di una cella della griglia tasti
- `Dialogs/ErrorDialog` — dialog errore con path log
- `Dialogs/ImportProfileDialog` — dialog "Importa profilo BaseCamp"
- `Assets/BaseCampMacros.json` — libreria macro dal DB Base Camp (zona grigia,
  vedi `DISTRIBUTION.md`)
- `Services/DisplayPadService.cs` — facade su `DisplayPad.SDK` (apertura driver, eventi)
- `Services/BaseCampProfileImporter.cs` — importa profilo XML Base Camp in uno slot
- `Services/DisplayRotation.cs` — orientamento DisplayPad (90/270) + layout griglia
- `Services/MacroLibrary.cs` — carica la libreria di macro nominate dal DB
- `Services/StateStore.cs` — persistenza stato (deviceId, profile, buttonIndex)

### `K2/DisplayPad_Stabilizer/` — fix esterno (legacy, superato dalla patch)
Tool Python che riallinea i `DeviceId` nel DB di Base Camp. Da solo non basta
(la patch dnSpy è la soluzione vera). Moduli in `stabilizer/`:
- `config.py` path/costanti · `hid_enum.py` enum SetupAPI · `db_ops.py` riscrittura DB
- `device_cycle.py` cycle cfgmgr32 · `discover.py` autodiscovery path Base Camp
- `elevate.py` UAC · `fingerprint.py` I/O JSON fingerprint · `flash.py` GUID in flash
- `flash_fingerprint.py` provisioner deterministico · `gui_setup.py` GUI Tkinter
- `orchestrator.py` logica end-to-end · `service_ctrl.py` servizi/processi · `watcher.py` watcher DB
- `stabilizer_main.py` entry CLI · `installer.py` scheduled task · `build.bat`/`build_x86.bat`
- `build/`, `dist/` — artefatti PyInstaller (ignorati da `.gitignore`)

### `K2/_reference/` — ⛔ materiale NON distribuibile (escluso da `.gitignore`)
Tutto ciò che deriva dai binari di Base Camp. Solo per sviluppo locale.
- `decompiled/` — **sorgente decompilato parziale** (legacy): `Repo/` =
  `BaseCamp.Repository.dll`, `Worker/` = `MountainDisplayPadWorker.exe`
  (contiene `DisplayPadOperations.cs` — il file dove vive il bug SetDeviceId)
- `BaseCamp_decompiled/` — **sorgente decompilato COMPLETO** di tutte le DLL
  di Base Camp (export dnSpy, 2026-06-25). Sottocartelle rilevanti:
  - `BaseCamp.Data/` — modello dati (Entity → SQLite). **Classi principali:**
    `Profile` (tabella `Profiles`, hub con navigation a tutti i device),
    `Lighting` (tabella `KeyboardLightings`, enum `EffectIndex`/`MenuIndex`),
    `KeyboardBinding` (tabella `EverestKeyBidings`, con DLLKeyId/DLLMatrixIndex),
    `KeyboardSetting` (tabella `KeyboardSettings`, include Media Dock settings),
    `KeyboardMacro` (tabella `Macros`, InputsJson = JSON azioni registrate),
    `DisplayPadLayerBidings` (tabella omonima, dual image + IsHardWarePress),
    `DisplayDial` (tabella `DisplayDials`, config display Everest Max),
    `MakaluKeyBinding`/`MakaluLighting`/`MakaluSetting`/`DPILevel` (mouse Makalu),
    `Everest60KeyBinding`/`Everest60Lighting`/`Everest60Setting` (Everest 60),
    `Settings` (tabella singleton, config globale BC),
    `EverestCustomLighting` (per-key custom: EffectIndex + LedKeys[] + LedKeyColors[]),
    `CustomMakaluLighting` (KeyCode + ColorHex).
    Namespace `BusinessInsights`: telemetria (UserMaster, LogMaster, FeedbackMaster,
    UserKeyBinding, UserLighting, UserAccounts, VersionHistory) — NON rilevante per K2.
  - `BaseCamp.Repository/` — ORM SQLite custom (no EF). `GenericRepository<T>`:
    CRUD reflection-based (legge `[Table]`/`[Column]`/`[Key]` da Data Annotations),
    query via `MyQueryTranslator` (Expression → SQL WHERE). `UnitOfWork`: facade
    con un repo per tabella + migration helper (`CheckIfColumnExists`/`AddColumnsInTable`).
    DB path: `resources\bin\BaseCamp.db`, schema embedded come resource SQL.
  - `BaseCamp.Utility/` — `Logger` (file log giornaliero con pulizia 3gg),
    `UtilityController` (hack per SnippingTool), `TCPClient` (comunicazione
    socket asincrona porta 11000 tra UI BC e `BaseCamp.Service.exe` /
    `MountainDisplayPadWorker.exe` — rileva la porta via netstat+PID).
  - `Makalu/` — DLL del mouse Mountain Makalu (da analizzare in futuro).
  - Altre: `BaseCamp.Spotify/Twitch/YouTube` (integrazioni streaming),
    `BaseCamp.ResourceMonitorHelper/LibreHardwareMonitor` (monitoring HW),
    `GregsStack.InputSimulatorStandard` (simulazione input), `HidSharp` (HID),
    `obs-websocket-dotnet` (OBS), librerie .NET standard (ignorare).
- `BaseCamp_Patch/` — `SetDeviceId_patched.cs` (fix 3° DisplayPad) +
  `README_patch.md` (istruzioni per applicare la patch via dnSpy)
- `native/MacroPadSDK.dll` — copia di lavoro della nativa MacroPad (x86)
- `EVEREST_TODO.md` — checkpoint Fase 3 (modulo Everest): export SDK già
  estratti, piano di ripresa. **Leggere se si lavora sull'Everest.**
- `Everest_SDK_signatures.txt` — dump firme C# (classi Everest / Everest60 /
  SDKDLL_Helper / ...) e struct, estratto dai metadati ECMA-335
- `tools/dotnet_pinvoke_dump.py` — parser ImplMap (classe.metodo → DLL.entry)
  per BaseCamp.Service.exe. Lanciare con `python3 dotnet_pinvoke_dump.py
  <path-to-BaseCamp.Service.exe>` quando serve verificare a quale DLL nativa
  bind un wrapper C# di Base Camp.
- `tools/dotnet_enum_dump.py` — dumpa tutti gli enum della .NET assembly
  (ECMA-335). Usato per recuperare `EFF_INDEX`, `SPEED_T`, `DIRECTION_T`,
  `BRIGHT_T`, `DeviceType`, ...
- `tools/dotnet_struct_dump.py NAME ...` — dumpa i field delle struct
  selezionate (FWColor/EffData/BlockData/...) con `ClassLayout` (Pack/Size).
- `tools/dotnet_marshalas.py NAME ...` — legge `FieldMarshal` (es. `ByValArray
  SizeConst=N`) sui field — necessario per dichiarare correttamente le struct
  P/Invoke con array inline.
- `tools/dotnet_method_calls.py <exe> <Class.Method> [...]` — dump del body
  IL (ECMA-335) di uno o piu' metodi, con risoluzione dei token per
  `call`/`callvirt`/`newobj`/`ldfld`/`ldstr`. Modalita' `--callers <sym>`:
  scansiona tutti i metodi e elenca chi chiama un simbolo (es.
  `--callers ::ChangeEffect`). Usato per ricostruire la sequenza esatta
  di costruzione `EffData` in `MacroPadSDK::getChangeEffect` e trovare il
  wrapper end-to-end `Everest::EverestChangeEffect`.
- **Estrarre `BaseCamp.UI.dll` da `BaseCamp.UI.exe`** (per raggiungere le view
  Razor MVC dell'app Base Camp, es. `Views_Everest__Setting`, non presenti nei
  DLL di `BaseCamp_Decompiled/`): `Mountain Base Camp/resources/bin/
  BaseCamp.UI.exe` è un *self-contained single-file .NET bundle* (Electron.NET),
  quindi `pefile` non vede l'header CLR (appeso dopo lo stub nativo). Si cerca
  nel file la entry di manifest `"<Assembly>.dll"` (preceduta da
  `[offset:u64][size:u64][compressedSize:u64][type:u8]`), si fa lo slice
  `data[offset:offset+size]` (parte con `MZ`, PE valido con CLR header) e si
  passa il risultato ai `tools/dotnet_*.py` esistenti. Non serve `node`/`asar`
  per questo (serve solo per leggere `app.asar`, i file statici .js/.cshtml
  compilati non ci sono dentro).
  **Scorciatoia più veloce per singole stringhe/etichette** (non serve
  isolare l'assembly): i letterali `WriteLiteral("...")` generati dalla
  compilazione Razor restano leggibili come testo **UTF-16LE** dentro il
  binario così com'è. Basta `open(path,'rb').read()`, cercare
  `"testo noto".encode('utf-16-le')` col pattern che si sta cercando
  (un'etichetta UI, un tooltip, un id HTML) e decodificare come
  `utf-16-le` una finestra di ~1-3 KB di byte attorno al match
  (`errors='replace'` per i byte non testuali) — salta fuori l'HTML/JS
  circostante leggibile, comprese le classi/id/value dei controlli.
  Usato per trovare id/valori reali (`delay-one`/`delay-two`/`delay-three`,
  `play-one`/`play-two`/`play-three`) dell'editor macro, mai esposti nei
  DLL decompilati. **Attenzione ai falsi positivi**: id generici come
  `play-two`/`play-four` sono riusati altrove per feature diverse (es. un
  D-pad di navigazione immagini) — verificare il testo/HTML circostante,
  non solo l'id, prima di fidarsi del match.
- `tools/parse_usb_pcap.py <file.pcapng>` — parser pcap-ng minimo (link_type
  USBPcap 249), nessuna dipendenza. Filtra i pacchetti USB OUT non-vuoti
  e stampa hex dump. Usato per confrontare i comandi HID che Base Camp
  invia alla tastiera Everest Max (VID 0x3282 / PID 0x0001) vs quelli
  che invia K2.App. Vedi `_reference/USB_SNIFF_GUIDE.md`.
- `_reference/USB_SNIFF_GUIDE.md` — istruzioni passo-passo per catturare
  i pacchetti HID di Base Camp con USBPcap+Wireshark (necessario per
  capire perche' K2.App `ChangeEffect` non disegna sulla tastiera).
- `_reference/usb_dumps/` — destinazione dei `.pcapng` catturati.
- `_reference/BaseCamp_decompiled_UI/BaseCamp.UI.dll` — assembly estratto
  (2026-07-09) da `BaseCamp.UI.exe` con la tecnica sopra. A differenza di
  `BaseCamp_decompiled/` (solo librerie referenziate: Data/Repository/
  Utility/...), questo contiene i **Controllers ASP.NET Core** con la logica
  di business vera e propria (es. `MacroPadController`, `EverestController`,
  `MacroPadOperations`, `EverestOperations`) — il livello sopra ai wrapper
  P/Invoke `BaseCamp.Service.Helpers.*SDK` di `BaseCamp.Service.exe`. Usare
  gli stessi `tools/dotnet_method_calls.py --callers <needle>` /
  `<Class.Method>` per rintracciare sequenze UI-driven (es. cosa succede
  quando l'utente apre un pannello, non solo quale P/Invoke esiste).
  Nota tecnica: `find_method`/`--callers` fanno match diretto sul nome
  metodo — per i costruttori statici passare il nome esatto `.cctor` (con
  il punto) via script inline, non tramite `find_method` (che spezza su
  `.` e si rompe).
- `README.md` — marker "non distribuire"

## Profili
- `Profili_BaseCamp/` — 11 XML originali (`displaypad1..4`, `base`, `base2`,
  `cuffie`, `discord`, `discord2`, `lavoro`, `start`) + `test/test1..4.xml`
- `Profili_K2/` — output K2 (compatibili anche con Base Camp):
  - `displaypaddeadside.xml` — profilo gioco DeadSide, 12 azioni
    (Prev/Next profile, Tab/M/Y/F12, hotbar 1/2, R, 9, C, batch lancio gioco)
  - `displaypadorcaslicer.xml` — profilo OrcaSlicer, 12 azioni
    (Prev/Next profile, Ctrl+O/S/Z/Y/R, M/R/S/F, Run Program orca-slicer.exe)
  - `displaypaddeadside_v.xml`, `displaypadorcaslicer_v.xml` — stesse 12
    azioni ma con le **icone pre-ruotate 90° CCW** per il DisplayPad
    verticale (l'utente ne ha 1 in orientamento verticale, 2×6, oltre ai
    2 orizzontali 4×3).
  - `icons_deadside/`, `icons_orcaslicer/` — 12 PNG 102×102 ciascuno (flat
    minimal, palette K2: bg `#1A1A1E`, accent teal `#900000`). Già embeddati
    in base64 nel rispettivo XML; cartella "sciolta" per reimportarli.
  - `icons_deadside_v/`, `icons_orcaslicer_v/` — stesse icone ruotate 90° CCW
    (versione per DisplayPad verticale).
  - `gen_profiles.py` — script Python (Pillow) che rigenera icone + XML
    orizzontali. Modifica `DEADSIDE_ACTIONS`/`ORCASLICER_ACTIONS` o le
    funzioni `deadside_icon`/`orca_icon` e rilancia.
  - `rotate_v.py` — prende i due XML orizzontali e le cartelle icone
    standard, produce le varianti `_v` con icone ruotate 90° CCW. Da
    rilanciare ogni volta che cambia il set orizzontale.

## Riferimenti esterni
- **Memoria persistente** (cross-sessione): bug 3° DisplayPad, patch dirette OK,
  rotazione 90/270, K2.App unificata. Vedi i file in `memory/` (via `MEMORY.md`).
- **Log Base Camp:** `Mountain Base Camp/Logs/*_DPWorker.log` — diagnostica device.
- **DB Base Camp:** `BaseCamp.db` (sul PC, non nel repo); schema decompilato in
  `K2/_reference/decompiled/Repo/BaseCamp.Repository.DATABASE_SCHEMA.sql`.

## Prossimi step (dai README)
1. DisplayPad: editor macro, persistenza SQLite clonando le tabelle di `BaseCamp.db`.
2. MacroPad: griglia 2×6 ruotabile FATTA; invio effetti LED preset FATTO
   (da testare su device). **Da fare**: cambio profilo nativo
   (`SwitchProfile(int,int,uint)`, 3 parametri), editor effetti per-block
   (`ChangeBlockEffect`/`BlockData`), persistenza effetto per-device.
   (Fase 2 — griglia tasti + azioni + Script Python: FATTA.)
3. Everest Max (Fase 3): apertura driver + cattura tasti + azioni: FATTI.
   Effetti RGB preset + sync cross-profilo + persistenza: FATTI.
   **Numpad display keys**: P/Invoke + image uploader + facade: FATTI.
   **Media Dock**: P/Invoke + facade + **UI pannello**: FATTI. Pannello con
   orologio (timer 1s), PC monitoring (CPU+RAM, timer 2s), effetti LED barra
   (8 preset), screensaver 240×204, reset. **Da verificare con USB capture**:
   formato immagini numpad (72×72 vs 128×32?), parametri ResetNumpadPic,
   EQ_DATA, parametri BarData (byAll/byLightness/byWidth).
   **Da fare**: UI per numpad display keys nel tab Everest; rinomina tasti.
   **FATTO**: editor per-key custom lighting (pannello Custom Lighting,
   struct `CustomEffect`/`CustomData`, `ChangeCustomizeEffect`/
   `SwitchToCustomizeEffect`/`GetEffCustomizeContent`).
4. Fusione dei moduli dentro `K2.App` — FATTA: `K2.App` è ora il guscio
   unificato x86 con tab MacroPad + Everest + DisplayPad nello stesso
   processo (DisplayPad via `DisplayPadSatelliteClient`/`DisplayPadNativeClient`,
   niente più bisogno di `K2.DisplayPad.exe` separato per l'uso quotidiano).
   `K2.DisplayPad.sln`/`K2.DisplayPad.exe` restano come prodotto standalone
   x64 a parte (stessa base `K2.Core`, tre `IActionHost` distinti nello stesso
   processo di K2.App: `MainWindow` per MacroPad, `EverestActionHost`,
   `DisplayPadActionHost` — vedi `K2.Core/IActionHost.cs`).
5. Everest 60 (nuovo tab, raw HID — non SDK, vedi nota architetturale sopra):
   **FATTO** (MVP 2026-07-10, **verificato su hardware reale 2026-07-10**):
   connessione (poll), RGB preset (Off/Static/Breathing/Wave/Tornado/Reactive/
   Yeti + speed/brightness/2 colori/rainbow/direzione), anello laterale 44 LED.
   Layout a 3 colonne (sidebar/immagine/colonna destra) completato 2026-07-10.
   **Tastiera interattiva a 64 tasti + numpad decorativo + editor "Key
   Lighting" per-tasto completati 2026-07-11** (vedi CHANGELOG per la sessione
   completa): layout portato da BaseCampLinux, asset piatti board/numpad
   trovati identici in Base Camp e riusati (non ricreati), paint mode
   per-tasto via `SendCustom` (già esistente, prima solo per l'anello),
   toggle "Numpad attached" cosmetico. Build pulita, **non ancora verificato
   su hardware fisico** (nessun device disponibile in questa sessione) — da
   testare: geometria dei 64 tasti sovrapposta all'immagine, paint mode →
   applicazione reale. **Da fare**: verifica hardware del nuovo overlay;
   persistenza cross-sessione (RGB preset e colori per-tasto); layout
   multi-lingua (ISO IT/UK/DE/...).
   **Key Binding reale + riorganizzazione sezioni completati 2026-07-11**
   (vedi CHANGELOG per la sessione completa e la traccia di decompile): al
   contrario di quanto scritto sopra, il remap tasti/Fn-layer/scorciatoie/
   media **è** disponibile — non via HID raw ma via l'SDK vendor
   `Everest360_USB.dll` (`ChangeKey`/`ChangeFnKey`/`ChangeShortcutKey`/
   `SetSingleMacroContent`), i cui export di remap si sono rivelati
   parametri primitivi (non struct opache come quelli di lighting).
   Implementato: nuova sezione "Key Binding" (scrittura diretta in firmware,
   niente `IActionHost`), sezioni SECTIONS riorganizzate — poi riordinate
   ancora 2026-07-11 in Key Binding/Lighting/Settings (stesso ordine
   Everest Max) — con Settings che ora include anche **Keycap Style**
   (importato da Everest Max, il segnale "live" con cui si mescola è il
   colore dipinto in Key Lighting anziché un readback LED che non esiste per
   questo device; applicato anche al numpad decorativo). Layout resta
   disabilitato, nessuna fonte di legende multi-lingua verificata per
   questo layout. Build pulita. **Da fare**: verifica su hardware fisico
   (nessun device disponibile finora) di tutto il path Key Binding — inclusa
   la possibile contesa fra la sessione SDK persistente e le burst raw-HID
   di lighting; conferma dei codici media/OS (attualmente segnaposto).
   **Persistenza cross-sessione dei binding FATTA (2026-07-13)**, vedi
   voce "Gestione profili Everest 60 e Makalu" più sotto.
   **Rilevamento numpad completato 2026-07-11**: al contrario di quanto
   scritto sopra e in una risposta precedente in questa stessa sessione,
   presenza E lato del numpad **sono** rilevabili — `GetSubDeviceInfo(1,...)`
   (mai indagato a fondo prima), stesso meccanismo di Base Camp. Toggle
   manuale rimosso, sostituito da poll automatico ogni 3s. **Da fare**:
   verifica su hardware fisico (nessun Everest 60 con numpad disponibile
   finora) dei valori reali `position` e del posizionamento/mirroring lato
   destro (mai visto in nessun asset Base Camp).
6. Makalu 67/Max (mouse, nuovo tab, raw HID — non SDK, vedi nota
   architetturale sopra): RGB preset (Off/Static/Breathing/RGB Breathing/
   Rainbow/Responsive/Yeti + speed/brightness/2 colori/direzione Rainbow),
   editor custom per-LED (finestra dedicata, 8 LED) e impostazioni (polling
   rate/debounce/angle snapping/lift-off) **verificati su hardware reale
   2026-07-10**. DPI e remap tasti (incl. sniper), e il layout a 3 colonne
   (sidebar SECTIONS/immagine con hotspot/colonna destra) **completati e
   funzionanti 2026-07-10** — il crash fatale del CLR che li aveva fatti
   disabilitare due volte è stato root-causato con WinDbg+SOS: era un
   null-ref banale (evento XAML che scatta durante `InitializeComponent()`
   prima che un elemento dichiarato più avanti nel file fosse assegnato),
   non un bug del JIT/CLR — v. nota architetturale sopra e CHANGELOG
   2026-07-10 per la sessione di debug completa. **Da fare**: verifica su
   hardware Makalu fisico (DPI/remap/sniper mai testati end-to-end su
   device reale — nessuna unità disponibile finora); dare lo stesso layout
   a 3 colonne a Everest 60 (non tentato in questo giro, era rimasto
   indietro per prudenza mentre la causa del crash era ignota — ora che si
   sa cos'è, si può procedere sapendo cosa evitare: niente
   `IsChecked`/`Minimum`/`Maximum` letterali in XAML abbinati a un handler
   che tocca elementi dichiarati più avanti nel file).
   **Persistenza cross-sessione + import da `BaseCamp.db` FATTI (2026-07-13)**
   — vedi voce dedicata "Gestione profili Everest 60 e Makalu" subito sotto.
7. **Gestione profili Everest 60 e Makalu (2026-07-13)**: entrambi i device
   erano gli unici due rimasti senza profili (combo/rename/delete/import/
   export disabilitati in `MainWindow.xaml`, nessuno store SQLite, stato
   solo in-memory). Nessuno dei due ha un vero profilo firmware — stesso
   caso già risolto per il DisplayPad (`DpSwitchProfile`): profilo = 5 slot
   fissi puramente lato-K2, "cambiare profilo" = ricaricare lo stato
   salvato e ri-mandarlo al device con le stesse chiamate HID/SDK
   esistenti. Nuovi store `Services/MakaluStore.cs` (`makalu.db`) e
   `Services/Everest60Store.cs` (`everest60.db`), stesso pattern k/v +
   JSON blob di `EverestStore`. Ogni "Apply" esistente ora persiste lo
   stato per lo slot corrente (incondizionatamente, anche a device
   scollegato); `MkReloadProfile`/`Ev60ReloadProfile`/
   `Ev60ReloadKeyBindings` (nuovi) ricaricano+riapplicano all'hardware,
   chiamati su switch combo/init modulo/riconnessione. Switch profilo su
   Everest 60 riscrive dal vivo la mappa 64-tasti ma **non chiama mai
   `SaveFlash` automaticamente** (resta dietro il pulsante Save manuale).
   Import da `BaseCamp.db` (`BaseCampDbImporter.cs`): `DeviceType`
   verificato contro il vero DB di riferimento — Everest 60 =
   `"EverestMini"` (confermato su dati reali), Makalu = `"Makalu"`
   (nessun profilo Makalu mai esistito in questo DB, dedotto dalle
   proprietà di navigazione `Profile.MakaluLightings/...` e dal bridge RPC
   `{'Class':'Makalu',...}` in `BaseCamp.UI.dll`). Lighting import ad alta
   confidenza (verificato su dati reali, incl. parser colore che gestisce
   sia `#RRGGBB` sia `rgb(r, g, b)` nella stessa tabella); Key Binding
   import Everest 60 è best-effort (l'unico profilo reale visto ha solo
   legende factory Fn-layer, non veri remap utente — si importano solo i
   match esatti contro `Everest60RemapData.KeyCatalog`). Export XML nuovi
   `MkProfileExporter.cs`/`Ev60ProfileExporter.cs` (mirror di
   `EvProfileExporter.cs`). **Scoperta collaterale fuori scope**: il
   MacroPad legge da tempo `MakaluKeyBindings` (VUOTA nel DB reale) invece
   che `EverestKeyBidings` (dove vivono i suoi dati veri, con
   DLLMatrixIndex che non combaciano con `KeyIdToIndex` 170-179/220-221) —
   segnalato, non corretto in questa sessione. Build pulita (dotnet build
   pulito, entrambe le solution, 0 errori/0 warning). **Non verificato su
   hardware fisico** (nessun Everest 60/Makalu disponibile) — da provare:
   riapplica reale RGB/DPI/remap su switch profilo; import di un vero
   profilo Makalu/Everest 60 con remap `LayerType=1` reali (mai visti finora).
