# CHANGELOG.md — storico sessioni di sviluppo K2

> Log dettagliato, sessione per sessione, di cosa e' stato fatto/scoperto
> durante lo sviluppo di K2 (bug, fix, decisioni, verifiche pendenti su
> hardware fisico). Ordine: piu' recente in cima ("Last updated" seguito
> da "Previous:" a scendere).
>
> NON va letto per intero a inizio sessione — quello scopo lo serve la
> mappa stabile in `_PROJECT_MAP.md`. Consultare qui solo per il contesto
> di una modifica specifica passata (grep per parola chiave/data).

> Last updated: 2026-07-08 (4 richieste utente: crash "Aggiungi testo", doppia rotazione
> cartella+testo, assegnazione macro a qualsiasi tasto, creazione cartelle DisplayPad da UI):
>   - **Crash "Aggiungi testo"**: `TextIconDialog` (K2.Core) ha `RbBgSolid IsChecked="True"`
>     in XAML — WPF invoca il suo evento `Checked` SINCRONAMENTE durante
>     `InitializeComponent()` (classico gotcha dei RadioButton), ma a quel punto
>     `RbBgImage` (dichiarato più sotto nello stesso XAML) non è ancora stato
>     collegato al field — è `null`. `BgMode_Changed` → `RefreshPreview()` →
>     `UseImageBackground` dereferenziava `RbBgImage.IsChecked` → `NullReferenceException`
>     non gestita → crash prima ancora di mostrare il dialog. Fix: `RbBgImage?.IsChecked`.
>   - **Doppia rotazione icona cartella+testo**: quando una cartella viene creata su un
>     DisplayPad ruotato (90/270°), `TryGenerateFolderIcon` bake-a la counter-rotation nel
>     PNG e lo salva sotto `auto_icons/` — `EffectiveDpRotation` riconosce quel path e
>     salta la rotazione device al successivo upload (altrimenti raddoppierebbe). Ma
>     "Aggiungi testo" componeva il testo SOPRA quell'icona già ruotata e salvava il
>     risultato in `text_icons/` (percorso diverso, non riconosciuto) — al prossimo
>     upload la rotazione device veniva riapplicata su pixel già ruotati. Fix in
>     `DpKeyConfigDialog`/`CellConfigDialog::BtnAddText_Click`: se l'immagine di base
>     era già un auto-icon, il risultato composito viene promosso (copiato) nella stessa
>     cache `auto_icons/` invece di restare in `text_icons/`.
>   - **Assegnazione macro a qualsiasi tasto**: prima d'ora `ActionType=="macro"` esisteva
>     solo come dato residuo dell'import da BaseCamp.db — `ButtonActionEngine` non aveva
>     nemmeno un `case "macro"` (un tasto così importato non faceva NULLA alla pressione).
>     Aggiunto: `IActionHost.ListMacroNames()`/`PlayMacro(name)` (K2.Core, nuovi membri
>     dell'interfaccia); `ButtonActionEngine` esegue `_host.PlayMacro(value)` sul case
>     "macro"; `ButtonActionDialog` ha un nuovo tipo azione "Play macro" (combo dinamica,
>     riusa il pattern `ComboPanel` di oscmd/media/mouse ma popolata da
>     `_host.ListMacroNames()` invece di un enum fisso). In K2.App: `MainWindow.Macro.cs`
>     espone `ListAllMacroNames()`/`PlayMacroByName()` (cerca in `_macroStore`, riproduce
>     via `_macroPlayer.Play`), implementati dai 3 `IActionHost` adapter (MainWindow per
>     MacroPad, `EverestActionHost`, `DisplayPadActionHost`). Lo standalone K2.DisplayPad
>     (senza libreria macro) implementa entrambi come no-op/lista vuota. Aggiunto anche
>     `DisplayPadStore.GetKeysByAction` (mancava, a differenza di MacroPadStore/EverestStore)
>     e la sezione "Assigned to" del pannello Macro ora include anche il DisplayPad, non
>     solo MacroPad/Everest.
>   - **Creazione cartelle DisplayPad da UI**: la navigazione a sotto-pagina
>     (`_currentDpPageId`/`dp_folder`/`dp_back`) esisteva già dalla sessione del 2026-06-29
>     ma era raggiungibile SOLO importando un profilo BaseCamp/XML — nessun modo di
>     crearla da zero in-app. Aggiunto `DisplayPadStore.AllocatePageId(deviceId, profile)`
>     (calcola un pageId libero dal MAX corrente su `Buttons`/`ActionValue` dei
>     "dp_folder" esistenti, niente contatore persistito — non collide mai con gli ID
>     arbitrari di un import BC) + due voci nel context-menu dei tasti DisplayPad
>     (`BuildDpKeyContextMenu`): "Create folder page…" (prompt nome via
>     `ShowRenameDialog`, alloca pageId, salva `dp_folder`) e "Set as Back button"
>     (salva `dp_back`). Nuove chiavi loc `dp_create_folder`, `dp_create_folder_title`,
>     `dp_create_folder_prompt`, `dp_set_back` (EN+IT).
>   - **Verificato**: `dotnet build` pulito (0 errori/0 warning) su entrambe le solution
>     dopo ogni fix. **Da verificare dall'utente su hardware**: crash testo risolto,
>     icona cartella+testo non più doppio-ruotata su DisplayPad ruotato, macro assegnata
>     a un tasto (MacroPad/Everest/DisplayPad) si riproduce alla pressione, creazione di
>     una cartella dal context-menu e navigazione al suo interno.
>
> Previous: 2026-07-08 (Macro: 3 fix — click Stop registrato, icone righe, lista estesa):
>   - **Segnalazione utente**: "la sezione delle macro diventa bianca quando
>     registro", "estendi la lista delle macro come le sezioni dei
>     dispositivi", "quando clicco stop il click del mouse viene ancora
>     registrato" (nonostante il fix precedente).
>   - **"Diventa bianca" durante la registrazione**: causato dal precedente
>     `SetMacroEditingEnabled(false)`, che metteva `IsEnabled=false` su
>     `LbMacros` (ListBox non ri-templata, quindi torna al chrome disabilitato
>     di default di WPF — tipicamente un grigio/bianco di sistema che ignora
>     il `Background` impostato a mano) insieme ad altri controlli, per
>     evitare che l'utente rompesse lo stato live durante la cattura.
>     Rimosso del tutto l'approccio "disabilita visivamente": ora i controlli
>     restano sempre abilitati (nessun cambio di colore) e la protezione è
>     puramente funzionale — guardie `_macroRecorder?.IsRecording` in
>     `BtnMacroNew_Click`/`BtnMacroDelete_Click`/`BtnMacroImportBC_Click`/
>     `BtnMacroInput{Delete,MoveUp,MoveDown}_Click`; il cambio di selezione
>     macro (`LbMacros_SelectionChanged`) durante la registrazione viene
>     silenziosamente annullato (torna alla macro in registrazione, tracciata
>     in un nuovo campo `_recordingMacro`) invece di essere bloccato via
>     `IsEnabled`. `RebuildInputRows()` ora, se `_macroRecorder.IsRecording`,
>     ricostruisce dalla cattura live del recorder invece che da
>     `SelectedMacro.Inputs` (stale finché non arriva `Stop()`) — così il
>     toggle "Show press/release" durante la registrazione non svuota più la
>     lista, senza bisogno di disabilitarlo.
>   - **Estendi lista macro come le sezioni**: la sidebar "SECTIONS" dei
>     pannelli device riempie tutta l'altezza della colonna perché è un
>     `Border`/`Grid` (che si stira per riempire la cella), mentre la Macro
>     Library usava uno `StackPanel` (che si dimensiona sempre al contenuto,
>     ignora `VerticalAlignment=Stretch` per via di come `StackPanel`
>     misura/arrangia) con `ListBox Height="280"` fissa. Convertito in `Grid`
>     con righe Auto/Auto/`*`/Auto (header, New/Delete, card lista, Importa)
>     e rimossa l'altezza fissa dalla `ListBox` — la card della lista ora
>     riempie tutto lo spazio verticale disponibile nella colonna, come la
>     sidebar SECTIONS.
>   - **Click sullo Stop ancora registrato — causa del fallimento del fix
>     precedente**: il controllo introdotto la volta scorsa usava solo
>     `WindowFromPoint` (hit-test per z-order) + `GetWindowThreadProcessId`;
>     su una finestra WPF con chrome custom/composizione (vedi `WindowChrome`
>     in `K2Theme.xaml`), l'hit-test per z-order può non risolvere in modo
>     affidabile alla vera HWND dell'app. Aggiunto un controllo primario più
>     robusto e indipendente dallo z-order: confronto diretto punto-dentro-
>     rettangolo contro l'HWND reale della finestra (`GetWindowRect` su
>     `_hWnd`, già presente in `MainWindow` — impostato in
>     `OnSourceInitialized`, letto da `MainWindow.Macro.cs` al momento di
>     avviare la registrazione via nuovo `MacroRecorder.SetOwnerWindow(hwnd)`).
>     I due controlli sono in OR: se il bounding-rect matcha già la
>     registrazione viene scartata, altrimenti fa comunque fede il vecchio
>     controllo via `WindowFromPoint` come seconda rete di sicurezza.
>   - **Seguito — "diventa ancora bianca" / "non si è allungata" (persistevano
>     dopo il fix sopra)**: causa reale trovata in `LbMacros` stessa (la
>     ListBox della Macro Library), non nel codice C#: usava ancora il
>     **template di default di WPF**, che ha un proprio stato visivo
>     "disabilitato" con uno sfondo chiaro/di sistema che ignora il
>     `Background` impostato a mano (motivo per cui poteva restare bianca
>     indipendentemente dal fatto che il codice non tocchi più `IsEnabled`),
>     e un chrome a dimensione fissa che non si stira dentro una riga `*`
>     come farebbe un semplice `ScrollViewer`. Sostituito con lo stesso
>     template minimale già usato per `LvMacroInputs`
>     (`ScrollViewer`+`ItemsPresenter`, nessun VisualState "disabled"): ora
>     lo sfondo non può cambiare per nessuna ragione legata a `IsEnabled`, e
>     la lista riempie correttamente tutta l'altezza disponibile nella
>     colonna, come la sidebar SECTIONS.
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su
>     entrambe le solution dopo ogni step. Avviato `K2.App.exe` in locale
>     (con `stop-basecamp.bat` prima) — nessun errore XAML, nessun
>     `[MACRO] Init error`. **Da verificare dall'utente**: che la sezione
>     macro non cambi più colore durante la registrazione, che la card della
>     lista macro riempia l'altezza disponibile, e soprattutto — essendo il
>     secondo tentativo — che il click sullo Stop non finisca più nella
>     macro registrata.
>
> Previous: 2026-07-08 (Rimozione voci dai "Recenti" in Open program/file e Open folder):
>   - **Richiesta utente**: poter rimuovere singole voci dalle liste "Recenti" nei
>     pannelli "Apri programma/file" e "Apri cartella" di `ButtonActionDialog`.
>   - **`AppSettings.cs`**: nuovi `RemoveRecentExecPath`/`RemoveRecentFolderPath`
>     (accanto ai già esistenti `AddRecent*`).
>   - **`ButtonActionDialog.xaml`**: `ItemTemplate` su `LstExecRecent`/
>     `LstFolderRecent` — riga con testo + pulsante "✕" (tooltip localizzato,
>     nuova chiave `remove_recent`) che rimuove quella voce e aggiorna la lista,
>     senza chiudere il dialog.
>   - **Localizzazione**: `remove_recent` in EN/IT tradotta, altre 8 lingue con
>     placeholder inglese (stesso pattern già usato in questa sessione).
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning).
>
> Previous: 2026-07-08 (Icona cartella: trovata e risolta la vera causa della mancata counter-rotation):
>   - **Segnalazione utente** (dopo il fix sfondo nero/parametro rotazione della sessione
>     precedente): "la funzione dell'icona con cartella adesso ruota correttamente
>     nell'interfaccia, ma sul display non viene controruotata. Inoltre il testo non
>     viene lasciato così com'è." Confermato: il testo sotto al lato lungo della
>     cartella, composto PRIMA della rotazione (così com'era già implementato), è la
>     geometria corretta — il problema non era la generazione ma l'upload.
>   - **Causa radice trovata**: il bypass `ImagePreRotated` (che al primo upload
>     evitava la doppia rotazione passando `rotation=0`) era un flag SOLO TRANSITORIO
>     nell'istanza del dialog — mai persistito. Qualsiasi RI-upload successivo dello
>     STESSO file (`key.ImagePath`, ormai il PNG già pre-ruotato) tramite un percorso
>     diverso dal salvataggio iniziale — `DpReloadCurrentProfile` (cambio pagina/
>     profilo/dispositivo, avvio app), `DpUploadPressVisual` (rimbalzo visivo ad ogni
>     pressione fisica del tasto), il caricamento profilo dello standalone
>     `MainWindow.xaml.cs::ReloadCurrentProfile` — ignorava completamente il bypass e
>     riapplicava `_dpRotation`/`_rotation` come per qualsiasi altra immagine,
>     ruotando due volte un file già corretto. Bastava aprire una tab diversa e
>     tornare indietro per vedere l'icona sbagliata.
>   - **Fix**: eliminato il flag transitorio `ImagePreRotated`. Al suo posto, un
>     controllo basato sul PERCORSO del file, valido ovunque e per sempre (non solo
>     al momento della creazione): `MainWindow.DisplayPad.cs::EffectiveDpRotation(path)`
>     (K2.App) e `MainWindow.xaml.cs::EffectiveRotation(path)` (K2.DisplayPad
>     standalone) restituiscono rotazione zero se il path ricade sotto la cartella
>     cache delle icone auto-generate (`%LOCALAPPDATA%\K2.DisplayPad\auto_icons\`,
>     stessa usata da `DpKeyConfigDialog`/`CellConfigDialog::AutoIconCachePath`),
>     altrimenti la rotazione normale del dispositivo. Applicato a TUTTI i punti che
>     chiamano `_dpClient.UploadImage(ToProfile)`/`_service.UploadImage(ToProfile)`
>     con la rotazione del device: upload iniziale, `DpReloadCurrentProfile` (per
>     immagine, non più un'unica rotazione catturata per l'intero batch),
>     `DpUploadPressVisual`, `ReloadCurrentProfile` dello standalone.
>   - **Bug collaterale corretto**: la cache key di `AutoIconCachePath` non includeva
>     la rotazione del device — due DisplayPad con rotazioni diverse ma la stessa
>     azione cartella/exec avrebbero potuto collidere sullo stesso file PNG. Ora la
>     rotazione fa parte della chiave hash.
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning, entrambe le
>     solution). **Da verificare su hardware dall'utente**: che l'icona cartella
>     ora resti correttamente controruotata anche dopo un cambio pagina/profilo,
>     un riavvio di K2, o la pressione fisica del tasto (non solo subito dopo
>     l'assegnazione).
>
> Previous: 2026-07-08 (Editor "Aggiungi testo" per DisplayPad e Everest display key):
>   - **Richiesta utente**: poter inserire testo semplice su un tasto, colorando lo
>     sfondo oppure scrivendo sopra un'icona già caricata, sia per il DisplayPad
>     che per i display key dell'Everest (numpad).
>   - **Nuovo `K2.Core/TextIconGenerator.cs`**: motore di rendering puro
>     System.Drawing (nessuna dipendenza WPF, stesso stile di
>     `IconImageGenerator.cs`). `TryRenderTextIcon`/`TryGenerateTextIcon`
>     disegnano il testo centrato su un canvas size×size, con auto-fit del
>     font (word-wrap, shrink finché non entra nel riquadro) e un contorno
>     automatico bianco/nero (in base alla luminanza del colore testo) per
>     restare leggibile sia su sfondo tinta unita sia sopra un'immagine.
>   - **Nuovo `K2.Core/TextIconDialog.xaml(.cs)`**: piccolo editor condiviso —
>     casella di testo, due modalità di sfondo ("Sfondo a tinta unita" /
>     "Sopra l'immagine caricata", quest'ultima disabilitata se il tasto non
>     ha ancora un'immagine), color picker (WinForms `ColorDialog`, stesso
>     pattern già in uso per l'illuminazione RGB) per sfondo e testo, anteprima
>     live 140×140 rigenerata ad ogni modifica (in memoria, nessun file
>     temporaneo su disco). Vive in `K2.Core` (non in K2.App/K2.DisplayPad)
>     perché entrambe le app lo referenziano.
>   - **Integrazione**: nuovo pulsante "Aggiungi testo…" (chiave loc
>     `dp_add_text`) accanto a "Rimuovi immagine" in tutti e tre i dialog
>     immagine+azione: `K2.App/DpKeyConfigDialog` (DisplayPad, 102×102, dentro
>     la shell unificata), `K2.App/NdkKeyConfigDialog` (Everest numpad display
>     key, 72×72), `K2.DisplayPad/Dialogs/CellConfigDialog` (DisplayPad
>     standalone x64). Il risultato è trattato come un'immagine caricata
>     manualmente (rotazione utente resettata a 0°, nessun flag di
>     pre-rotazione device: il testo non è legato alla rotazione fisica come
>     le icone auto-generate exec/folder).
>   - **Localizzazione**: nuove chiavi (`dp_add_text`, `txt_dialog_title`,
>     `txt_label`, `txt_bg_solid`, `txt_bg_image`, `txt_bg_color`,
>     `txt_color`, `txt_generate_failed`) tradotte in `Strings.xml` (EN) e
>     `Strings.it.xml` (IT); le altre 8 lingue non toccate, fallback automatico
>     su EN via `Loc.cs` (comportamento già esistente per chiavi mancanti).
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning, entrambe le
>     solution). **Da verificare su UI/hardware dall'utente**: aspetto reale
>     dell'editor, leggibilità del contorno testo su vari colori, risultato
>     sui tile DisplayPad (102×102) e sui display key Everest (72×72).
>
> Previous: 2026-07-08 (Macro: fix import da BaseCamp.db + libreria macro restyle):
>   - **Segnalazione utente**: "le macro non vengono importate correttamente da
>     basecamp.db". Causa radice trovata estraendo l'HTML/JS compilato
>     dell'editor macro di Base Camp direttamente da `BaseCamp.UI.exe`
>     (single-file .NET bundle, self-contained: nessun sorgente/.cshtml su
>     disco). Tecnica: le stringhe letterali dei `WriteLiteral(...)` Razor
>     restano leggibili come UTF-16LE nel binario compilato — bastano
>     `open(path,'rb')` + ricerca del pattern `"testo".encode('utf-16-le')`
>     + decode di una finestra di byte attorno al match, senza bisogno di
>     parsare il formato bundle .NET (nessun tool `node`/`asar` necessario:
>     nessuna delle view sta in `app.asar`, che è solo lo shell Electron).
>     Verificato con successo contro il `BaseCamp.db` reale dell'utente
>     (`C:\Program Files (x86)\Mountain Base Camp\resources\bin\BaseCamp.db`,
>     13 macro reali) — non solo indovinato.
>   - **Bug 1 — Delay/Playback sempre sbagliati**: `MacroDefinition.FromBaseCamp`
>     mappava `DelayOption`/`PlaybackOption` su keyword semantiche mai esistite
>     in BC ("nodelay"/"custom"/"repeatn"/...) — i valori reali salvati nel DB
>     sono id posizionali delle tab pill: `delay-one`="Record delay",
>     `delay-two`="Custom" (quella col campo ms), `delay-three`="No delay";
>     `play-one`="Play once", `play-two`="Hold" (mentre premuto — il nostro
>     `WhileHeld`), `play-three`="Repeat" (dal tooltip BC: "will continue to
>     execute your macro from the moment the assigned button is pressed until
>     it is pressed again" — un **toggle** press-per-avviare/press-per-fermare,
>     cioè il nostro `Toggle`, NON "ripeti N volte": BC non ha proprio quel
>     concetto per le macro tastiera, colonna `RepeatCount` non esiste nello
>     schema `Macros` di BaseCamp.db). Ogni macro importata cadeva quindi
>     sempre sul default (Recorded/Once) indipendentemente dall'impostazione
>     reale in BC.
>   - **Bug 2 — tasti registrati sempre "vuoti" (il problema più grosso)**:
>     `MacroInput` deserializzava l'`InputsJson` di BC con lo stesso modello
>     usato per il nostro formato nativo (proprietà `"key"`), ma il recorder
>     di BC (Electron + iohook/uiohook, vedi `uiohook.dll` in
>     `resources/bin`) serializza eventi tastiera come `{rawcode, keycode,
>     type, delay, altKey, shiftKey, ctrlKey, metaKey}` — **nessuna proprietà
>     "key"** — quindi `Key` restava sempre 0 per ogni tasto di ogni macro
>     importata (verificato: BC stesso usa `event.rawcode` — non `keycode`,
>     che è l'id cross-platform interno di iohook — per il lookup nome-tasto
>     via `GetKeyTextBaseOnRawCode`/`keyCodes_*[event.rawcode]`; `rawcode` è
>     il VK code nativo Windows, stesso valore già catturato da
>     `MacroRecorder.cs` per le registrazioni K2). Anche gli eventi mouse
>     usano `"button"` non `"key"` (1=sinistro, 2=destro, 3=centrale — stessa
>     numerazione 1/2 già usata da K2, verificato via lo switch
>     `btnText`/`ButtonType` nel JS compilato). Nuovo
>     `MacroInput.ListFromBaseCampJson()` parsa il formato reale di BC
>     (gestisce anche `"delay"` serializzato come stringa, es. `"delay":"1"`,
>     osservato nei dati reali); scarta gli eventi `"mousewheel"` (nessun
>     supporto scroll in `MacroPlayer`) invece di importarli come azioni
>     rotte/azzerate. Verificato contro dati reali: macro "À" (Alt+Numpad
>     0192 per il carattere accentato, rawcode 164/96/97/105/98 = LAlt/
>     Numpad0/1/9/2, esatti), macro "TEST" (8 click sinistri con delay
>     90/260/90/85/85/55/55/0ms — combaciano esattamente con lo screenshot
>     di Base Camp usato a inizio conversazione per il redesign del pannello
>     Macro), macro "AUTORUN" (un solo tasto W, PlaybackOption=play-three →
>     ora mappato a Toggle, coerente con un macro "tieni premuto W finché
>     non ripremi" per l'omonimo autorun di gioco).
>   - **Bug 3 (minore, scoperto verificando)**: i click di BC spesso non
>     registrano x/y (macro "clicca dove si trova già il cursore", non
>     "clicca in un punto fisso") — prima venivano importati come (0,0),
>     causando un salto del cursore all'angolo in alto a sinistra durante il
>     replay. `ListFromBaseCampJson` ora usa `-1` come sentinella "nessuna
>     posizione registrata"; `MacroPlayer.SendMouseClick` salta lo spostamento
>     del cursore quando x o y sono negativi (limite noto: su setup
>     multi-monitor con un monitor a sinistra del primario, coordinate K2
>     native legittimamente negative verrebbero trattate come "nessuna
>     posizione" — edge case raro, non affrontato).
>   - **Macro Library restyle**: la sezione ora usa lo stesso look della
>     sidebar "SECTIONS" dei pannelli device (card scura arrotondata
>     `#111115`, voce evidenziata in accent quando selezionata — nuovo style
>     `MacroLibraryItemStyle` per `ListBoxItem`, analogo a `SectionTabStyle`
>     ma basato su `IsSelected` invece di `IsChecked`). Bottoni New/Delete
>     spostati SOPRA la card lista, bottone "Importa da BaseCamp.db" resta
>     SOTTO, come richiesto.
>   - **Fix — click del mouse sullo Stop finiva nella macro**: con "Record
>     mouse" ora default attivo, fermare la registrazione cliccando il
>     bottone Stop registrava anche quel click (l'hook `WH_MOUSE_LL` cattura
>     il down/up del bottone Stop PRIMA che il `Click` handler chiami
>     `Stop()`, essendo un hook globale sincrono che precede il routing
>     WPF). `MacroRecorder.MouseHookCallback` ora ignora i click il cui
>     punto schermo ricade su una finestra del processo K2 stesso
>     (`WindowFromPoint`+`GetWindowThreadProcessId` confrontato con
>     `Environment.ProcessId`) — cattura solo i click su altre applicazioni,
>     coerente con lo scopo di una macro (interagire con l'esterno, non con
>     se stessa).
>   - **Fix — icone vuote sulle righe Inputs**: i tre bottoni per-riga
>     (sposta su/giù, elimina) riusavano `Tag` sia per il glyph icona
>     (letto dal template di `MacroRowIconButton`) sia per passare la riga
>     `MacroInputRow` all'handler `Click` — un solo `Tag` non può fare
>     entrambe le cose, quindi il template mostrava il `ToString()`
>     dell'oggetto riga invece del glyph (di fatto vuoto/illeggibile nel
>     font icone). Spostato il riferimento alla riga su
>     `CommandParameter="{Binding}"` (letto dagli handler via pattern
>     matching `Button { CommandParameter: MacroInputRow row }`), lasciando
>     `Tag` libero per il solo glyph. Verificato anche il glyph della
>     colonna tipo-azione (tastiera/mouse/testo): i code point scelti
>     esistono davvero nel cmap Windows-Unicode di `segmdl2.ttf` (controllo
>     con `fontTools`), quindi non era quello il problema.
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su
>     entrambe le solution dopo ogni step; avviato `K2.App.exe` in locale
>     (con `stop-basecamp.bat` prima, stavolta) — nessun errore XAML/
>     `[MACRO] Init error`, crash nativo 0xE06D7363 nella stessa identica
>     fase di init hardware (Everest SaveFlash) osservato anche a Base Camp
>     chiuso: conferma che è un problema nativo pre-esistente scorrelato dal
>     lavoro di questa sessione, non qualcosa introdotto qui. **Da
>     verificare dall'utente**: re-importare da BaseCamp.db, verificare che
>     le icone delle righe Inputs si vedano, che il click sullo Stop non
>     compaia più nella macro registrata.
>
> Previous: 2026-07-07 (Everest numpad display key: dialog unificato immagine+azione):
>   - **Segnalazione utente**: "il problema dei tasti display è che se clicchi
>     sull'interfaccia dovrebbe fare l'azione 'configure action'" — il vecchio
>     comportamento (click sinistro = solo immagine, click destro = solo azione,
>     in un menu contestuale) era poco scopribile, disallineato dal DisplayPad
>     dove un solo click apre un dialog unico con immagine+azione insieme.
>   - **Nuovo `K2.App/NdkKeyConfigDialog.xaml(.cs)`**: dialog unificato per i 4
>     numpad display key Everest, ricalcato su `DpKeyConfigDialog`/
>     `CellConfigDialog` (preview immagine 120×120 + "Carica immagine…"/"Rimuovi
>     immagine" a sinistra, riepilogo azione + "Configura azione…"/"Rimuovi
>     azione" a destra, OK/Annulla). Niente crop editor inline (mantiene il
>     popup `ImageCropDialog.Show` già esistente per gli NDK, 72×72, coerente
>     con quanto già in `TODO.md`). L'auto-generazione icona per azioni
>     exec/folder (v. sessioni precedenti) è ora dentro questo dialog
>     (`TryAutoGenerateImage`), non più in `MainWindow.NumpadDisplayKeys.cs`.
>   - **`MainWindow.NumpadDisplayKeys.cs`**: `NdkButton_Click` ora apre
>     `NdkKeyConfigDialog` invece del solo `OpenFileDialog`; applica sia
>     l'azione che l'immagine (via `NdkApplyImage`, invariato) al ritorno.
>     Rimossi `NdkMnuConfigureAction_Click` e la voce "Configura azione…" dal
>     menu contestuale (ridondanti col click singolo) — il tasto destro resta
>     solo per "Rimuovi azione"/"Rimuovi immagine" come scorciatoie rapide.
>   - **Nota**: questo risolve solo l'assegnazione via UI. Il bug per cui il
>     tasto FISICO poi non esegue l'azione (perché `HandleNumpadDisplayKeyPress`
>     non ha chiamanti — v. voce separata in `TODO.md`) resta aperto, richiede
>     cattura hardware del matrixId dei 4 tasti e non è stato toccato in questa
>     sessione.
>   - **Verificato**: `build-check.bat` pulito (0/0). **Da verificare su
>     hardware/UI dall'utente**: aspetto e funzionamento reale del nuovo dialog.
>
> Previous: 2026-07-07 (Icona cartella auto-generata: sfondo nero + counter-rotation DisplayPad):
>   - **Bug segnalato dall'utente** (testato su hardware reale): sull'icona cartella
>     auto-generata (v. sessione precedente) su un DisplayPad montato ruotato, il
>     testo del nome cartella risultava corretto ma il glyph/icona appariva
>     orientato con la rotazione del dispositivo invece che con la counter-rotation.
>     Inoltre richiesto sfondo nero puro invece del grigio scuro tema K2 (`#1A1A1E`).
>   - **`IconImageGenerator.cs`**: `TryGenerateFolderIcon` ora usa sfondo
>     `Color.Black` puro (solo per la cartella, l'icona "exec" resta sul grigio
>     scuro tema, nessuna lamentela su quella). Entrambi i generatori
>     (`TryGenerateExecIcon`/`TryGenerateFolderIcon`) accettano ora un parametro
>     opzionale `deviceRotationDegrees` (0/90/180/270): se diverso da zero,
>     applicano la STESSA convenzione di counter-rotation già usata ovunque nel
>     progetto (`IconRotator.ImageAngleCw` in K2.DisplayPad, `ResolveForUpload`
>     nel satellite: 90°→immagine ruotata 270°, 180°→180°, 270°→90°) direttamente
>     sul bitmap generato, PRIMA del salvataggio su PNG.
>   - **Evitare la doppia rotazione**: dato che la pipeline di upload
>     (`_dpClient.UploadImageToProfile(..., rotation)` sia nel backend nativo
>     che nel satellite SDK) applica GIÀ una counter-rotation automatica ad ogni
>     immagine caricata in base alla rotazione configurata del device, un'immagine
>     auto-generata con la rotazione già "cotta dentro" verrebbe ruotata due
>     volte. Fix: nuova proprietà `ImagePreRotated` su `DpKeyConfigDialog`
>     (K2.App) e `CellConfigDialog` (K2.DisplayPad standalone), true quando
>     l'immagine appena generata ha già la counter-rotation applicata; i chiamanti
>     (`MainWindow.DisplayPad.cs`/`MainWindow.xaml.cs`) passano rotazione=0/None
>     all'upload in quel caso invece della rotazione normale del device. Un
>     caricamento manuale successivo (`BtnLoadImage_Click`) resetta sempre
>     `ImagePreRotated=false`. `DpKeyConfigDialog`/`CellConfigDialog` ricevono ora
>     la rotazione del device come parametro del costruttore (`_dpRotation`/
>     `(int)_rotation` passati dal chiamante).
>   - **Verificato**: `build-check.bat` pulito (0/0). **Da verificare su
>     hardware dall'utente**: che l'icona cartella ora appaia effettivamente
>     upright su un DisplayPad ruotato (90/180/270), sfondo nero visibile
>     correttamente, nessuna regressione sull'icona "exec" (che non ha ricevuto
>     lo sfondo nero, solo il parametro di rotazione, non ancora testata con
>     rotazione != 0).
>
> Previous: 2026-07-07 (ButtonActionDialog: selettori dedicati per azione + icone automatiche):
>   - **Obiettivo**: sostituire il generico textbox "Value:" con controlli
>     dedicati per tipo di azione (stesse azioni assegnabili su tutti i
>     device, dialog condiviso in `K2.Core`), e generare automaticamente
>     l'immagine del tasto (DisplayPad 12-key grid + 4 numpad display key
>     Everest) quando l'azione è "exec"/"folder".
>   - **Open program/file**: riga icona 32×32 + path compatto + Browse…,
>     lista percorsi recenti sotto (`AppSettings.RecentExecPaths`, MRU 10).
>     Icona via `System.Drawing.Icon.ExtractAssociatedIcon`.
>   - **Open folder**: solo path + Browse (`Microsoft.Win32.OpenFolderDialog`)
>     + lista recenti (`RecentFolderPaths`), niente anteprima icona.
>   - **Open browser**: radio Chrome/Edge/Firefox/Opera/Brave mostrati solo se
>     rilevati (`BrowserDetector`, registro `App Paths`), radio "Altro…" con
>     path/Browse sempre presente, URL iniziale opzionale. Valore serializzato
>     come `BrowserActionPayload` JSON; stringa legacy (URL nudo) resta
>     supportata come fallback in `ButtonActionEngine.RunBrowserAction`.
>   - **Switch profile**: righe multiple (pulsante "+ Aggiungi dispositivo"),
>     ogni riga sceglie un dispositivo (combo popolata da
>     `IActionHost.ListProfileTargets()`, sempre con "Questo dispositivo" come
>     self-target retrocompatibile) e Next/Previous/Profilo N. Valore JSON
>     `ProfileTargetPayload`; stringa legacy resta supportata (self-target).
>     **Cambio architetturale**: `IActionHost.SwitchProfile` ora prende
>     `(string? targetKey, string target)`; K2.App (MacroPad+Everest+DisplayPad
>     ormai unificati nello stesso processo — vedi `DisplayPadActionHost`/
>     `EverestActionHost`) espone un dispatcher condiviso
>     `MainWindow.SwitchProfileByKey`/`ListAllProfileTargets`; lo standalone
>     `K2.DisplayPad.exe` espone solo i propri device. `MpSwitchProfile`/
>     `DpSwitchProfile` accettano ora un `deviceId` opzionale per switchare un
>     device diverso da quello visualizzato senza toccarne la UI.
>   - **System command / Media key / Mouse action**: un solo `ComboBox`
>     ripopolato per tipo con le stringhe già riconosciute da
>     `ActionExecutor` (nessuna modifica alla logica di esecuzione).
>   - **Keys**: checkbox modificatori (Ctrl/Shift/Alt/Win) + combo editabile
>     per il tasto (A-Z, 0-9, F1-F24, tasti speciali comuni); compone/parsa la
>     stessa sintassi umana `"Ctrl + Shift + A"` già letta da
>     `SendKeysTranslator` — nessuna modifica all'esecuzione. Win noto per non
>     essere inviato da SendKeys (hint in UI, comportamento preesistente).
>   - **Icone automatiche** (`K2.Core/IconImageGenerator.cs`): quando l'azione
>     assegnata/cambiata tramite il dialog è "exec", icona dell'eseguibile via
>     Shell `IShellItemImageFactory::GetImage` (jumbo, fallback a
>     `Icon.ExtractAssociatedIcon`); quando è "folder", glyph Segoe MDL2
>     "OpenFolderHorizontal" + nome cartella come didascalia. Generate solo
>     quando tipo/valore azione cambia davvero (non sovrascrive un'immagine
>     scelta manualmente se l'utente riapre il dialog senza modificare
>     l'azione). Agganciato in `DpKeyConfigDialog` (K2.App), `CellConfigDialog`
>     (K2.DisplayPad standalone) e `MainWindow.NumpadDisplayKeys.cs`
>     (`TryAutoGenerateNdkImage` → stessa cascata di upload di
>     `NdkButton_Click`, ora fattorizzata in `NdkApplyImage`).
>   - **Localizzazione**: tutte le nuove chiavi in `Strings.xml` (EN) e
>     `Strings.it.xml` (IT) tradotte; le altre 8 lingue hanno le stesse chiavi
>     con testo inglese come placeholder (nessuna traduzione persa, solo da
>     completare in una sessione futura se serve).
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning, entrambe
>     le solution) dopo ogni step. **Da verificare su hardware/UI
>     dall'utente**: aspetto reale dei pannelli nel dialog, qualità/nitidezza
>     delle icone auto-generate sui tile DisplayPad (102×102) e sui numpad
>     display key Everest (72×72), leggibilità del nome cartella a 72px,
>     comportamento dello switch profilo cross-device su hardware reale.
>
> Previous: 2026-07-07 (Macro window: redesign ispirato a Base Camp):
>   - **Obiettivo**: migliorare il pannello Macro (`PnlMacro`), partendo dallo
>     screenshot dell'editor macro di Base Camp e da quanto recuperabile da
>     `_reference/BaseCamp_decompiled` (schema `KeyboardMacro`/`KeyboardBinding`,
>     enum Delay/Playback via `Makalu/Macro.cs`: `WithDelay/SetDelay/NoDelay`,
>     `RUN_Once/Repeat/RUN_PRESSED(Hold)/RUN_LOOP(Toggle)`) — BaseCampLinux non
>     ha aggiunto altro (reimplementazione custom, non fedele a BC).
>   - **Layout**: Delay (Recorded/Custom/No delay) e Playback (Once/Hold/
>     Repeat/Toggle) sono ora `RadioButton` mutuamente esclusivi in stile
>     "pillola" (nuovo style `MacroOptionRowStyle`), non più `ComboBox`.
>     Sezioni Devices/Delay/Playback impilate in un'unica colonna centrale;
>     colonna Inputs (registrazione) al centro-largo; colonna "Assigned to"
>     a destra dei comandi, come richiesto.
>   - **Assigned to**: nuova query `GetKeysByAction(actionType, actionValue)`
>     su `MacroPadStore`/`EverestStore` (già esistevano `ActionType`/
>     `ActionValue` per riga) per trovare i tasti con `ActionType=="macro"` e
>     `ActionValue==<nome macro>` — stessa convenzione già usata da
>     `BaseCampDbImporter.TranslateAction` per importare i binding "Macro" da
>     BaseCamp.db. **Nota**: K2 non permette ancora di assegnare un'azione
>     "macro" a un tasto da `ButtonActionDialog` (solo l'import da BC popola
>     questo binding) — la sezione è pronta, ma oggi mostrerà assegnazioni
>     solo per profili importati da BaseCamp.db.
>   - **Fix incidentali**: `MacroRecorder.Start` ora accetta anche
>     `recordKeyboard` (prima la tastiera veniva sempre registrata,
>     `MacroDefinition.RecordKeyboard` esisteva ma non era esposto in UI —
>     aggiunta checkbox "Record keyboard"); i campi custom-delay-ms e
>     repeat-count ora salvano su `TextChanged` (prima si salvavano solo se
>     l'utente toccava un altro controllo prima); il combo Delay riusava per
>     sbaglio le chiavi loc `act_none`/`dial_custom` di altri pannelli — ora
>     usa le chiavi dedicate `macro_delay_recorded/custom/none` già esistenti
>     ma inutilizzate.
>   - **Inputs list**: lista dei tasti registrati con numero, glyph
>     tastiera/mouse, indicatore press/release (▲/▼, toggle "Show press/
>     release"), ms di delay, e icone per-riga (sposta su/giù, elimina) — per
>     ora un `ListView` semplice bindato a `MacroInputRow` (view model
>     nuovo), migrazione a drag&drop reale rimandata a una sessione futura
>     come da richiesta utente.
>   - **Verificato**: `build-check.bat`, 0 errori/0 warning su entrambe le
>     solution. Avviato `K2.App.exe` in locale: `MainWindow.xaml` (incluso il
>     nuovo `PnlMacro`) carica senza eccezioni XAML, l'app arriva fino
>     all'inizializzazione hardware (LED sync, MacroPad/DisplayPad/Everest)
>     prima di un crash nativo (0xE06D7363) non correlato — causato dai
>     processi Base Camp ancora in esecuzione in concorrenza con K2.App (già
>     loggato come warning esplicito dall'app stessa); non ho girato
>     `stop-basecamp.bat` prima del test. **Da verificare su hardware/UI
>     dall'utente**: aspetto visivo del nuovo layout (colonne, radio "a
>     pillola", lista Inputs, sezione Assigned To), comportamento reale di
>     record/play con le nuove checkbox Devices.
>
> Previous: 2026-07-07 (DisplayPad: non risponde finché non si apre la sua pagina):
>   - **Bug segnalato dall'utente**: aprendo K2, i DisplayPad fisici non
>     rispondono alle pressioni tasto finché l'utente non apre manualmente la
>     loro tab nella UI (`TcDevices`) — capita anche con un solo DisplayPad
>     collegato, non solo in setup multi-device.
>   - **Causa**: `_activeDpDeviceId` (`MainWindow.DisplayPad.cs`) viene
>     impostato SOLO da `TcDevices_SelectionChanged` (click utente sulla tab
>     `dp_{id}`) — resta `null` finché non succede. `OnDpKey`,
>     `DpReloadCurrentProfile`, `DpSwitchProfile` ecc. controllano tutti
>     `DpSelectedDeviceId()` (= `_activeDpDeviceId`) e fanno no-op silenzioso se
>     `null`: niente azioni, niente caricamento tasti/icone, niente risposta
>     hardware, finché non si apre la tab almeno una volta. `DpRefreshDevices`
>     aveva già una guardia esplicita per NON rubare il focus alla tab visibile
>     durante un refresh in background ("a background device refresh must not
>     steal focus") — ma questo lasciava `_activeDpDeviceId` per sempre `null`
>     se l'utente restava su Everest/Settings o se l'app partiva minimizzata in
>     tray (vedi sessione precedente: `StartMinimizedToTray`).
>   - **Fix**: estratto il corpo di `CbDpDevice_SelectionChanged` in un nuovo
>     `DpActivateDevice(int id)` (carica luminosità/profili/rotazione/griglia
>     tasti + ri-upload icone). `DpRefreshDevices` ora, quando non c'è nessuna
>     tab DisplayPad visibile E `_activeDpDeviceId` è ancora `null` (mai
>     attivato in questa sessione), attiva silenziosamente il primo device
>     trovato chiamando `DpActivateDevice` direttamente — SENZA toccare
>     `TcDevices.SelectedItem`, quindi non ruba il focus (stesso vincolo di
>     prima, preservato per il caso "utente già su una tab DisplayPad").
>   - **Verificato**: chiuso il processo `K2.App.exe` in esecuzione (di test
>     dell'utente) e ricompilate entrambe le solution, 0 errori/0 warning.
>     **Da verificare su hardware**: conferma utente che ora il DisplayPad
>     risponde ai tasti fisici subito dopo l'avvio di K2, senza dover aprire
>     la sua tab.
>
> Previous: 2026-07-07 (opzioni generali app: chiudi in tray, avvio con Windows, avvia ridotto in tray):
>   - **Richiesta utente**: aggiungere alle Impostazioni generali 3 opzioni:
>     chiudere K2 lo manda nella system tray invece di uscire, avviare K2
>     all'avvio di Windows, avviare K2 direttamente ridotto a icona in tray.
>   - **`AppSettings.cs`**: due nuovi flag persistiti, `CloseToTray` e
>     `StartMinimizedToTray` (stesso meccanismo JSON degli altri flag app-wide).
>   - **`Services/K2AutostartService.cs`** (nuovo): voce HKCU Run per K2.App
>     stesso (distinta da `BaseCampProcessGuard`, che gestisce solo le voci di
>     Base Camp) — nessun diritto admin richiesto, coerente con "preferenza
>     per-utente".
>   - **`MainWindow.Tray.cs`** (nuovo, partial): `NotifyIcon` (System.Windows.Forms,
>     già referenziato via `UseWindowsForms` per il ColorDialog Everest — nessuna
>     nuova dipendenza) creato una volta nel costruttore, menu contestuale
>     Mostra/Esci, `Closing` handler che se `CloseToTray` è attivo fa
>     `e.Cancel=true` + `Hide()` invece di chiudere davvero (un flag
>     `_reallyClosing`, settato solo dal menu tray "Esci", distingue la chiusura
>     reale). Gotcha C#: `Icon.ExtractAssociatedIcon(...)` va qualificato
>     `System.Drawing.Icon...` per intero — `Icon` bare si risolve alla proprietà
>     d'istanza `Window.Icon` (ImageSource), non al tipo `System.Drawing.Icon`.
>   - **`App.xaml`/`App.xaml.cs`**: rimosso `StartupUri` (serviva per poter
>     decidere se mostrare la finestra o avviarla ridotta in tray), aggiunto
>     `OnStartup` che crea `MainWindow` esplicitamente, imposta
>     `ShutdownMode.OnExplicitShutdown` (altrimenti nascondere la finestra in
>     tray chiuderebbe il processo) e se `StartMinimizedToTray` chiama
>     `window.StartMinimizedToTray()` (Show() poi immediatamente Hide() — nessun
>     flicker perché nulla cede il controllo al message loop tra le due
>     chiamate, e i driver si aprono comunque da `OnSourceInitialized` esattamente
>     come in un avvio normale) invece di `window.Show()`. `OnWindowClosed` ora
>     fa dispose del tray icon e chiama `Application.Current.Shutdown()`
>     esplicitamente (necessario con lo shutdown mode esplicito).
>   - **Stringhe nuove** (EN+IT, altre lingue ereditano il default EN):
>     `settings_general`, `settings_close_to_tray`, `settings_k2_autostart`,
>     `settings_start_min_tray(_hint)`, `tray_show`, `tray_exit`.
>   - **Verificato**: `dotnet build` pulito (0 errori/0 warning) su entrambe le
>     solution (`K2.sln` x86, `K2.DisplayPad.sln` x64). Avvio reale di `K2.App.exe`
>     testato in locale: driver DisplayPad/MacroPad/Everest si aprono
>     normalmente come prima, nessun crash log. **Da verificare dall'utente**:
>     comportamento a occhio del tray (icona/menu/restore), voce autostart HKCU
>     effettivamente scritta/rimossa dal registro, avvio ridotto a tray end-to-end.
>
> Previous: 2026-07-07 (DisplayPad: press-bounce hardware mancante):
>   - **Bug segnalato dall'utente**: premendo un tasto fisico del DisplayPad,
>     manca l'animazione "bouncing" che fa Base Camp (l'icona si rimpicciolisce
>     leggermente alla pressione e torna alla dimensione piena al rilascio).
>   - **Causa**: `OnDpKey` (`K2.App/MainWindow.DisplayPad.cs`) aggiornava solo
>     `IsHighlighted` (bordo bianco nella UI K2) ma non ri-caricava l'icona sul
>     device fisico. Nel worker BC decompilato (`DisplayPadOperations.cs`,
>     `IsBtnPressed`/`UploadImage`/`CallBack` in `DisplayPadMessagePumpManager.cs`)
>     il "bounce" NON è un'animazione firmware: ad ogni key-down BC ri-carica
>     la stessa icona rimpicciolita a 80×80 centrata su un canvas nero 102×102
>     (margine 11px, via `DrawBitmapWithBorder`), e al key-up la ricarica a piena
>     dimensione (`SetDefaultSize`) — un vero e proprio re-upload, non un effetto
>     lato device.
>   - **Fix**: aggiunto parametro `bool pressed` a `IDisplayPadClient.UploadImage`
>     (default `false`, non-breaking). Implementato in entrambi i backend:
>     - `DisplayPadNativeClient.LoadBgr` — se `pressed`, disegna l'icona 80×80
>       centrata su canvas nero 102×102 (stesso schema BC, niente mascheratura
>       angoli arrotondati dato che quel path non la fa nemmeno a riposo).
>     - `K2.DisplayPad.Satellite/NativeIconUploader.Upload` — replica esatta di
>       BC (resize 80×80 + maschera angoli raggio 40 + `DrawWithBorder` margine
>       11px), thread-ata da `SdkHandler.CmdUploadImage` via nuovo campo JSON
>       `"pressed"` (helper `OptBool` aggiunto).
>   - **`MainWindow.DisplayPad.cs`**: nuovo `DpUploadPressVisual(id, btnIndex,
>     pressed)` chiamato da `OnDpKey` su ogni key-down/key-up; incodato sulla
>     stessa `_dpUploadChain` per-device di ogni altro upload icona (mai in
>     parallelo con un reload di profilo — causa storica di corruzione icone).
>     Skippato per GIF animate (già in loop live via `DpGifAnimator`) e quando
>     un'immagine fullscreen possiede le 12 icone (nuovo dizionario
>     `_dpFullscreenByDevice`, popolato in `DpReloadCurrentProfile`).
>   - **Verificato**: `build-check.bat` pulito, 0 errori/0 warning su entrambe
>     le solution. **Da verificare su hardware**: l'utente usa il motore nativo
>     (`DisplayPadNativeEngine: true` in `app_settings.json`) — il path
>     satellite/SDK è stato aggiornato per coerenza ma non è quello attivo, va
>     ritestato se l'utente passa a quel motore.
>
> Previous: 2026-07-07 (Display Dial: layout più compatto):
>   - **Richiesta utente**: nel pannello Display Dial, spostare la combo
>     "cosa mostra lo screensaver" sulla stessa riga dei secondi dello
>     screensaver (compattare l'interfaccia); allargare le entry dei secondi
>     di screensaver/turn-off e allinearle (stessa left position).
>   - **`MainWindow.xaml`** (`PnlSecDial`, sezione Screensaver): da 3
>     `WrapPanel` impilati a un unico `Grid` 2 righe × 5 colonne — colonna 0
>     larghezza fissa (130) per le due checkbox "Screensaver after"/"Turn off
>     after" (di lunghezza diversa), colonna 1 larghezza fissa (60, prima
>     `Width="46"` sulla singola TextBox) per i due `TextBox` dei secondi:
>     essendo in colonna fissa, i due `TextBox` condividono lo stesso left
>     edge indipendentemente dalla lunghezza del testo della checkbox a
>     sinistra. La combo funzione screensaver (`CbDialScreenSaverFunction`)
>     si è spostata sulla riga 0, colonne 3-4, subito dopo l'unità "s" —
>     elimina una riga intera rispetto a prima.
>   - **Verificato**: `build-check.bat` pulito, 0 errori/0 warning su
>     entrambe le solution. Nessun cambio nel code-behind (gli handler erano
>     già tutti presenti, solo re-innestati nel nuovo `Grid`).
>
> Previous: 2026-07-06 (nuova sezione "Settings" Everest + tecnica di
> reverse engineering delle view Razor di Base Camp):
>   - **Richiesta utente**: aggiungere una sezione "Settings" nella sidebar
>     Everest con la selezione layout tastiera (già esistente, spostata da un
>     overlay in alto a destra sull'immagine device), poi aggiungere anche
>     "Sync across profiles", "Game Mode" (4 checkbox: disable Shift+Tab/
>     Alt+F4/Windows key/Alt+Tab), "Indicator LEDs" (Enable Core indicator
>     LEDs) e "Reset to factory default", come da screenshot di Base Camp.
>     Vincolo: niente bit-layout inventati, verificare su Base Camp decompilato
>     e BaseCampLinux prima.
>   - **Scoperta metodologica importante**: `Mountain Base Camp/resources/bin/
>     BaseCamp.UI.exe` (~216 MB) è un **self-contained single-file .NET bundle**
>     (Electron.NET + ASP.NET Core MVC, "Views_*" = Razor views compilate in C#).
>     `pefile` non vede l'header CLR perché è appeso dopo lo stub nativo: si
>     estrae cercando nel file la entry di manifest con nome `"<Assembly>.dll"`
>     preceduta da `[offset:u64][size:u64][compressedSize:u64][type:u8]`, poi si
>     fa lo slice `data[offset:offset+size]` (parte con `MZ`, PE valido con CLR
>     header). Il dll estratto (`BaseCamp.UI.dll`) è leggibile con gli stessi
>     tool `_reference/tools/dotnet_*.py` già in uso per `BaseCamp.Service.exe`.
>     **Questo supera la conclusione della sessione precedente** ("le view MVC
>     non sono nel decompilato") — ora sono raggiungibili, incluso il body IL
>     completo delle Razor view (`Views_Everest__Setting` e affini) con tutte le
>     label/tooltip/binding ai model `BaseCamp.Data.*`. Utile per prossime
>     feature (es. Display Dial icons/menu byte lasciati in sospeso).
>   - **Bit layout Game Mode confermato** (da `EverestOperations.SaveSettings`
>     in `BaseCamp.UI.dll`): costruisce una stringa binaria a 4 char nell'ordine
>     "AltTab Win AltF4 Shift" e la parsa con `Convert.ToInt32(s, 2)` — quindi
>     bit0=DisableShift(+Tab), bit1=DisableAltF4, bit2=DisableWin,
>     bit3=DisableAltTab. `EnableCoreLED`/Indicator LED è un bool diretto
>     (`SetIndicatorLed`). "Sync across profiles" nella pagina Settings di Base
>     Camp è **lo stesso flag fisico** già esposto in K2 dal checkbox "Sync
>     profiles" del pannello RGB & Lighting (`SetSyncAcrossProfiles`/
>     `GetSyncAcrossProfiles`, un solo bool a livello device, non per-sezione).
>   - **Reset to factory default**: in Base Camp chiama `ResetFlash(true)` +
>     cancella/ricrea tutti i profili nel DB SQLite di Base Camp. In K2 è stato
>     replicato **solo `ResetFlash(true)`** (azione hardware reale): la parte di
>     wipe/ricreazione profili è bookkeeping specifico del modello dati di Base
>     Camp, non pertinente al modello profili di K2, e sarebbe un'azione
>     distruttiva a sorpresa sui profili dell'utente — quindi omessa
>     intenzionalmente.
>   - **`EverestSdkNative.cs`**: nuovi P/Invoke `SetGameMode(int)`,
>     `SetIndicatorLed(bool)`, `ResetFlash(bool)` (SDKDLL.dll, firme confermate
>     in `_reference/Everest_SDK_signatures.txt`). **`EverestService.cs`**:
>     wrapper `SetGameMode`/`SetIndicatorLed`/`ResetFlash` (stesso pattern
>     try/catch/log di `SetSyncAcrossProfiles`/`ResetEffects`). Nessun wrapper
>     `Get*` aggiunto (non usati in UI, K2 tiene lo stato in `EverestStore`
>     come per RGB, non li rilegge dal device).
>   - **`MainWindow.xaml`/`MainWindow.Everest.cs`**: nuova `RadioButton
>     RbSecSettings`/pannello `PnlSecSettings` nella sidebar Everest (dopo
>     "Display Dial"). Checkbox Sync/Game Mode/Indicator LED persistite in
>     `EverestStore` (chiavi `settings.game_mode` int, `settings.indicator_led`
>     bool; il sync usa la stessa chiave `rgb.sync` già esistente per restare
>     allineato al checkbox del pannello RGB). Applicate al device all'apertura
>     driver (`ApplyEverestSettingsToDevice`, chiamata da `EvAutoOpen`/
>     `BtnEvOpen_Click` accanto ad `ApplyCurrentEffect`). Bottone factory reset
>     con conferma `MessageBox` (pattern già usato per cancellazione profili).
>   - **Stringhe**: nuove chiavi EN+IT (`settings_sync_profiles`,
>     `settings_game_mode`, `settings_game_mode_tip`, `settings_disable_shift_tab`,
>     `settings_disable_alt_f4`, `settings_disable_win_key`,
>     `settings_disable_alt_tab`, `settings_indicator_leds`,
>     `settings_enable_core_led`, `settings_factory_reset`,
>     `settings_factory_reset_confirm`).
>   - **Bug trovato e fisso nella stessa sessione**: la combo "Layout" nel
>     nuovo pannello Settings mostrava il `ToString()` grezzo del record
>     (`"LayoutChoice { Layout = IsoIt, Label = ... }"`) invece dell'etichetta,
>     perché al momento di `InitKeyboardLayoutSelector()` il pannello
>     `PnlSecSettings` è ancora `Visibility="Collapsed"` (sezione di default è
>     "Key Binding") — stesso bug già noto e documentato per `RotationChoice`
>     in `MainWindow.Keys.cs` (combo rotazione MacroPad). Fix: aggiunto
>     `public override string ToString() => Label;` a `LayoutChoice` come
>     fallback (stesso pattern, non un `ItemTemplate` alternativo). **Verificato
>     visivamente**: lanciato `K2.App.exe`, navigato a Everest → Settings via
>     UI Automation (`AutomationId` `TabEverest`/`RbSecSettings`), screenshot
>     prima/dopo — combo ora mostra "Italian — ISO" correttamente.
>   - **Nota per sessioni future**: su questa macchina (multi-monitor, finestra
>     K2 su monitor secondario a offset X grande) i click via UI Automation
>     `SelectionItemPattern.Select()` su un secondo elemento in rapida
>     successione hanno causato un click reale fuori bersaglio (aperto un
>     file-picker "Choose image for Display Key 1" invece di navigare a RGB &
>     Lighting) — probabile problema di coordinate/DPI. Meglio navigare un
>     elemento alla volta con pause e screenshot di verifica intermedi, non
>     incatenare più `Select()` senza controllo.
>   - **Verificato**: `build-check.bat`/`dotnet build K2.sln x86` puliti, 0
>     errori/0 warning. **Da verificare su hardware**: che `SetGameMode`/
>     `SetIndicatorLed`/`ResetFlash` producano davvero l'effetto atteso sulla
>     Everest Max fisica (mai testati prima in K2), e il comportamento dopo
>     `ResetFlash` (se il device si disconnette/riconnette).
>
> Previous: 2026-07-06 (redesign pannello Display Dial Everest):
>   - **Richiesta utente**: ridisegnare "Display Dial" come lo screenshot di
>     riferimento (Base Camp): 8 toggle "show menu" in 2 colonne da 4 con
>     icona + slider switch; rimuovere Pixel Shift; aggiungere checkbox
>     enable/disable separate per Screensaver e Turn-off; aggiungere combo
>     "cosa mostra lo screensaver" e combo tipo orologio analogico/digitale;
>     aggiungere bottone "Reset Display Dial". Vincolo: niente funzioni
>     inventate, solo cose recuperabili da Base Camp decompilato/BaseCampLinux.
>   - **Ricerca (agente in background)**: `BaseCamp.Data.DisplayDial` (decompilato)
>     ha colonne separate `EnableSecreenSaver`/`EnableTurnOff` (bool, indipendenti
>     dal valore in secondi) — conferma che l'enable/disable è un concetto reale,
>     non un'invenzione. Ha anche `ClockType` (int) e `ScreenSaverType` (string),
>     ma senza enum backing nel decompilato (probabilmente nella view Razor non
>     estratta). `BaseCampLinux` conferma un byte reale `STYLE_ANALOG`/
>     `STYLE_DIGITAL` (0x00/0x01) nel protocollo, e una tabella
>     `MAIN_DISPLAY_MODES` (image/clock/cpu/gpu/hd/network/ram/apm → menu byte)
>     per la selezione "cosa mostra lo screensaver" — ma questi byte non
>     combaciano col commento già presente su `byMMDockMenuIndex` in
>     `EverestSdkNative.cs` (valori 97-101/113), quindi non sono la stessa cosa
>     per questo SDK: **niente USB capture per SDKDLL.dll su questi due campi**,
>     solo per il raw-USB protocol di BaseCampLinux (device diverso/generazione).
>     Icone: nessun set dedicato scaricabile per gli 8 toggle — Base Camp le
>     serve da una view Razor compilata in `BaseCamp.Service.exe`, non presente
>     nel decompilato (`_reference/BaseCamp_decompiled/` ha solo le classi C#,
>     non le view MVC). Estrarre le PNG da `Mountain Base Camp/resources/bin/
>     wwwroot/images/` sarebbe comunque materiale non ridistribuibile
>     (`DISTRIBUTION.md`) — usate invece glyph Segoe MDL2 Assets (stesso
>     pattern icon-in-Tag già usato da `K2IconButton` in tutta l'app).
>   - **`K2Theme.xaml`**: nuovo style `K2ToggleRow` (CheckBox reskin: icona da
>     `Tag` + label + pillola scorrevole a destra, colore accento K2 `#900000`
>     invece del blu dello screenshot per coerenza col resto del tema).
>   - **`MainWindow.xaml`** (`PnlSecDial`): grid 2 colonne — sinistra: 8
>     `CheckBox` stile `K2ToggleRow` in 2 stack da 4; destra: sezione "Clock
>     type" (combo formato 12h/24h esistente + nuova combo stile analogico/
>     digitale), sezione "Screensaver" (checkbox enable + secondi + combo
>     funzione), checkbox enable + secondi per "Turn off", colore menu,
>     bottoni Apply/Read/**Reset** (nuovo). Pixel Shift rimosso.
>   - **`MainWindow.DisplayDial.cs`**: `wMMDockScreenSaver`/`wMMDockTurnOff`
>     ora vengono forzati a 0 quando la relativa checkbox enable è spenta
>     (mappa 1:1 sul modello Base Camp: enable separato dal valore); lettura
>     dal device non tocca più il campo se è 0 (non perde il valore configurato
>     in UI). Combo stile orologio e combo funzione screensaver sono
>     persistite (`dial.clockStyle`/`dial.screenSaverFunction`) ma **non**
>     ancora scritte su `FW_EXTEND_INFO` (nessun campo confermato — vedi sopra);
>     bottone Reset chiama `_everest.ResetMMDock()` (già esposto in
>     `EverestService`, prima inutilizzato). `TryGetExtendInfo`/`SetExtendInfo`
>     non toccano più `byPixelShiftTime` (letto e ri-scritto invariato).
>   - **Stringhe**: nuove chiavi EN+IT (`dial_clock_section`, `dial_clock_style_label`,
>     `dial_clock_digital/analog`, `dial_screensaver_section`,
>     `dial_screensaver_function_label`, `dial_turnoff_enable_tip`,
>     `reset_display_dial`, `unit_seconds`); rimossa `dial_pixel_shift` da
>     tutte le lingue (chiave morta).
>   - **Verificato**: `build-check.bat` pulito, 0 errori/0 warning su entrambe
>     le solution. **Da verificare su hardware**: layout 2 colonne (spazio
>     disponibile reale nel riquadro), rendering dei glyph Segoe MDL2 scelti,
>     comportamento enable/disable screensaver/turn-off sul device reale.
>
> Previous: 2026-07-06 (riquadro sezioni a dimensione fissa):
>   - **Richiesta utente**: il "riquadro in basso" (pannello impostazioni sotto
>     l'immagine device/griglia tasti, nei tab Everest/MacroPad/DisplayPad) aveva
>     `Height="Auto"`: cambiava dimensione/saltava a seconda della sezione attiva
>     (es. Everest: Key Binding ~250px / RGB & Lighting ~240px / Display Dial
>     ~150px / USB Recorder variabile), perché tutte le sezioni condividono la
>     stessa cella di Grid e solo una è Visible alla volta.
>   - **`MainWindow.xaml`**: sui 3 `Border` "Bottom settings panel" (Everest,
>     MacroPad, DisplayPad) aggiunto `Height` fissa (270/130/90, calibrata sulla
>     sezione più "alta" di ciascun tab) al posto dell'auto-sizing implicito, e
>     avvolto il contenuto in uno `ScrollViewer` (`VerticalScrollBarVisibility=
>     "Auto"`) come rete di sicurezza per qualunque sezione/stato che superi
>     l'altezza fissa (es. USB Recorder con risultati espansi) invece di essere
>     tagliato.
>   - **Verificato**: app lanciata, screenshot su "Key Binding" e "Display Dial"
>     nell'Everest — il riquadro resta della stessa altezza passando da una
>     sezione lunga a una corta (spazio vuoto sotto invece di restringersi).
>     Build pulita (`build-check.bat`): 0 errori, 0 warning su entrambe le
>     solution.
>
> Previous: 2026-07-06 (etichetta bottone Macro + font Roboto app-wide):
>   - **Richiesta utente**: 2 modifiche UI. (1) Il bottone icona "Macro" (appena
>     promosso a sezione top-level) doveva avere anche un'etichetta testuale
>     "Macro", non solo l'icona. (2) Tutti i testi dell'app devono usare il font
>     Roboto.
>   - **`MainWindow.xaml` (`BtnMacroTab`)**: da bottone quadrato 34×34 icon-only
>     a bottone largato (Height=34, Padding auto) con `StackPanel` orizzontale
>     icona + `TextBlock {loc:Get tab_macro}`; stesso trigger hover/background
>     di prima. `BtnSettingsTab` (gear) lasciato icon-only, non richiesto.
>   - **Font Roboto app-wide**: scaricati i 4 static TTF (Regular/Bold/Italic/
>     BoldItalic, non i variable font `[wdth,wght]` ora nel repo google/fonts —
>     rischiano compatibilità WPF incerta su weight/style mapping) da
>     `fonts.gstatic.com` (richiesta CSS2 con User-Agent Android 2.2 per
>     ottenere TTF invece di woff2, non serviti direttamente da WPF) +
>     `LICENSE.Roboto.txt` (SIL OFL 1.1) da `google/fonts` su GitHub. Messi in
>     nuovo `K2.Core/Fonts/`, embedded come `Resource` in `K2.Core.csproj`
>     (+ licenza come `Content` copiata in output). `K2Theme.xaml`:
>     `K2WindowStyle.FontFamily` da `"Segoe UI"` a
>     `"pack://application:,,,/K2.Core;component/Fonts/#Roboto, Segoe UI"` —
>     eredita su tutta la UI (Window è la style root, `FontFamily` è una
>     proprietà ereditata WPF) tranne dove già impostato esplicitamente
>     (KeyCapStyle = replica pixel-perfect keycap Base Camp, Consolas nei
>     log/hex viewer, Segoe MDL2 Assets per le icone) — questi non toccati
>     di proposito.
>   - **Verificato**: `GetManifestResourceStream` su `K2.Core.dll` conferma
>     i 4 `fonts/roboto-*.ttf` presenti nel `.g.resources` compilato (prova
>     statica che il pack URI risolve indipendentemente da Roboto installato
>     o meno sul sistema — su questa macchina di sviluppo Roboto risultava
>     già installato come font di sistema, quindi lo screenshot da solo non
>     bastava a distinguere embedded vs sistema). App lanciata e screenshottata:
>     bottone Macro con etichetta visibile, pannello "Keyboard Macro" apribile
>     correttamente. Build pulita (`build-check.bat`): 0 errori, 0 warning su
>     entrambe le solution.
>
> Previous: 2026-07-06 (Macro promossa a sezione top-level):
>   - **Richiesta utente**: la sezione "Keyboard Macro" viveva solo dentro la sidebar
>     dell'Everest (`RbSecMacros`/`PnlSecMacros`); ora è una sezione a sé stante,
>     raggiungibile da un bottone icona dedicato in alto a destra, subito a sinistra
>     del bottone Impostazioni.
>   - **`MainWindow.xaml`**: rimossi `RbSecMacros` (sidebar Everest) e il vecchio
>     `Grid x:Name="PnlSecMacros"` annidato nel pannello sezioni Everest. Aggiunto un
>     terzo `ColumnDefinition` nella riga tab in alto + `Button x:Name="BtnMacroTab"`
>     (icona `&#xE7C8;`, stesso template icon-only del gear Impostazioni) fra il
>     `TabControl` e `BtnSettingsTab`. Aggiunto un nuovo pannello top-level
>     `Grid x:Name="PnlMacro"` (sibling di `PnlSettings`/`PnlEverest`/`PnlMacroPad`/
>     `PnlDisplayPad`) con lo stesso contenuto (lista+CRUD+record a sinistra,
>     settings macro a destra), ora senza `MaxHeight` (spazio pieno come le altre
>     sezioni top-level) e con titolo (`{loc:Get keyboard_macro}`).
>   - **`MainWindow.xaml.cs`**: nuovo `BtnMacroTab_Click` (stesso pattern di
>     `BtnSettingsTab_Click`: nasconde gli altri pannelli, deseleziona `TcDevices`) +
>     `SetMacroTabActive(bool)` (stile pulsante attivo, mirror di
>     `SetSettingsTabActive`). `TcDevices_SelectionChanged`/`BtnSettingsTab_Click`
>     ora collassano anche `PnlMacro`. `InitMacroPanel()` richiamato direttamente nel
>     costruttore di `MainWindow` (non più da `InitEverestModule` in
>     `MainWindow.Everest.cs`), perché il pannello non dipende più dal device Everest.
>   - **`MainWindow.SectionNav.cs`**: rimossa la entry `PnlSecMacros` dallo switch di
>     `EvSection_Changed` + commento aggiornato.
>   - **Nuova stringa `tab_macro`** ("Macro" / "Macro") in `Strings.xml` + `Strings.it.xml`
>     (tooltip del nuovo bottone). Build pulita (`build-check.bat`): 0 errori, 0 warning
>     su entrambe le solution.
>
> Previous: 2026-07-06 (export profili: popup multi-selezione + scelta formato):
>   - **Richiesta utente**: sostituire i due pulsanti "Export (Base Camp)…"/"Export (K2)…"
>     (uno per formato, un profilo alla volta) con un unico popup che permetta di
>     scegliere i profili da esportare via checkbox multiple + il formato in un colpo
>     solo. In caso di export multiplo, niente prompt del nome file: i profili vengono
>     scritti automaticamente come `nomedevice_nomeprofilo.xml` in una cartella scelta
>     dall'utente (con un solo profilo selezionato resta il SaveFileDialog classico,
>     nome precompilato).
>   - **`K2.App/ExportProfilesDialog.xaml(.cs)`** (nuovo): dialog condiviso — lista di
>     `CheckBox` (una per profilo esistente del device/tab corrente, preselezionate se
>     nessun profilo era "corrente" es. su "+ New profile") + 2 `RadioButton` per il
>     formato (Base Camp compatibile / K2 lossless). Segue lo stile di
>     `DpKeyConfigDialog` (Style `K2WindowStyle` via `DynamicResource`, `xmlns:loc` per
>     `{loc:Get}`). Espone `SelectedProfiles`/`BcCompatible` letti dal chiamante dopo
>     `ShowDialog() == true`.
>   - **`K2.App/Services/ExportProfileHelper.cs`** (nuovo): unico punto che orchestra il
>     flusso per tutti e 3 i tab (DisplayPad/MacroPad/Everest) — apre il dialog, poi se
>     1 profilo selezionato usa `SaveFileDialog` (comportamento invariato), se piu' di 1
>     usa `Microsoft.Win32.OpenFolderDialog` (disponibile da .NET 8 per WPF, verificato
>     con build pulita x86+x64) e scrive un file per profilo con nome
>     `{deviceLabel}_{profileName}.xml` (sanitizzato via `Path.GetInvalidFileNameChars`).
>     Il chiamante passa solo un delegato `exportOne(slot, name, bcCompatible, path)` che
>     incapsula la chiamata al proprio `*ProfileExporter` (Dp/Mp/Ev hanno ciascuno un
>     `ExportResult` record identico nella forma ma di tipo diverso, non unificabile
>     senza toccare gli exporter esistenti — il delegato normalizza a una tupla).
>   - **3 tab aggiornati**: `BtnDpExportProfiles_Click`/`BtnMpExportProfiles_Click`/
>     `BtnEvExportProfiles_Click` sostituiscono le vecchie coppie `*ExportBc`/`*ExportK2`
>     in `MainWindow.DisplayPad.cs`/`MainWindow.Keys.cs`/`MainWindow.Everest.cs` e nei
>     bottoni XAML corrispondenti in `MainWindow.xaml` (un solo `Button` per tab, nuova
>     chiave loc `export_profiles_btn`). L'elenco profili viene da
>     `_dpStore.GetExistingProfiles(id)`/`_store.GetExistingProfiles(id)` (DisplayPad/
>     MacroPad, per-device) o `Enumerable.Range(1, EverestService.ProfileCount)`
>     (Everest, nessun concetto di device, tutti gli slot esistono sempre).
>   - **Nuove chiavi loc** (EN in `Strings.xml` + IT in `Strings.it.xml`,
>     `export_profiles_btn`/`export_profiles_title`/`export_profiles_pick`/
>     `export_format_section`/`export_format_bc`/`export_format_k2`/
>     `export_select_at_least_one`/`export_pick_folder`); le altre 8 lingue non
>     toccate (fallback automatico su EN via `Loc.Init`, gia' verificato per le chiavi
>     mancanti in generale). Le vecchie `dp_export_bc`/`dp_export_k2` restano nei file
>     Strings (ora inutilizzate) — non rimosse per non toccare tutte le 10 lingue per
>     due chiavi morte innocue.
>   - **Verificato**: `build-check.bat` pulito, 0 errori/0 warning su entrambe le
>     solution (K2.sln x86 + K2.DisplayPad.sln x64). Test su hardware fisico
>     (export multiplo con device DisplayPad/MacroPad/Everest reali) resta da fare
>     dall'utente.
>
> Previous: 2026-07-06 (fix: crash silenzioso all'avvio — DisplayPad sidebar):
>   - **Sintomo**: K2.App non si apriva piu', log fermo a due righe ("App start" +
>     "DllImportResolver registered"), nessuna eccezione, nessun crash log, nessun
>     dump. WER/Event Viewer: `coreclr.dll` "internal error", exit code `0x80131506`
>     — crash nativo non intercettabile dai normali handler (Dispatcher/AppDomain
>     UnhandledException), ne' dal VEH gia' presente in `App.xaml.cs`.
>   - **Causa**: nel refactor "sidebar sezioni per-device" (in corso, non ancora
>     committato), il nuovo `RadioButton x:Name="RbDpSecRotation"` nel tab
>     DisplayPad (`MainWindow.xaml`) ha `IsChecked="True"` — WPF spara l'evento
>     `Checked` **in modo sincrono durante `InitializeComponent()`**, prima che
>     l'elemento `PnlDpSecRotation` (dichiarato piu' in basso nello stesso file
>     XAML) sia stato costruito. `DpSection_Changed` (in
>     `MainWindow.SectionNav.cs`) dereferenziava `PnlDpSecRotation` senza guardia
>     null — a differenza degli handler equivalenti per Everest/MacroPad
>     (`EvSection_Changed`/`MpSection_Changed`), che gia' controllano `is not
>     null` prima di toccare il pannello. Il crash e' avvenuto esattamente li'.
>   - **Fix**: aggiunta la stessa guardia null gia' presente per Everest/MacroPad
>     a `DpSection_Changed` — un solo `&& PnlDpSecRotation is not null` in piu'.
>     Verificato con rebuild pulita + avvio: l'app parte e inizializza tutti i
>     device (MacroPad, Everest, 3 DisplayPad) senza crash.
>   - **Metodo di debug** (riutilizzabile in futuro per crash simili): il crash
>     bypassava OGNI handler gestito, quindi la diagnosi e' stata fatta per
>     bisezione in un **git worktree usa-e-getta** (mai toccato il working tree
>     reale dell'utente) confrontando via via porzioni del diff non committato
>     contro l'ultimo commit, con rebuild pulita (`rm -rf bin obj`) ad ogni passo
>     — le build incrementali di WPF/XAML davano falsi negativi (BAML non
>     rigenerata). Una volta isolato il file/blocco, `App.WriteLog` temporanei
>     PRIMA/DOPO ogni chiamata nel costruttore di `MainWindow` hanno individuato
>     il punto esatto (dentro `InitializeComponent`), poi un log del valore
>     `is null` sull'elemento sospetto ha confermato l'ipotesi in un colpo solo.
>
> Previous: 2026-07-05 (Everest: auto-rinomina tab in base a numpad/media dock collegati):
>   - **Richiesta utente**: rilevare se numpad e/o media dock sono collegati alla Everest
>     e rinominare automaticamente il tab — "Everest Max" se entrambi collegati, "Everest
>     Core" se entrambi scollegati, "Everest" se ne è collegato solo uno — ma solo finché
>     l'utente non ha già rinominato manualmente il tab (pulsante "Rinomina" esistente,
>     `BtnEvRename_Click` in `MainWindow.Everest.cs`, salva in `EverestStore` setting
>     `device.name`).
>   - **`MainWindow.Layout.cs`**: `UpdateKeyboardLayout()` già leggeva `dockPos`/`numpadPos`
>     via `MMDockPlugPosition()`/`NumpadPlugPosition()` per il layout dock/numpad — non
>     serviva un nuovo poll SDK. Aggiunta `UpdateEverestAutoName(dockConnected,
>     numpadConnected)`: se `device.name` è vuoto (nessuna rinomina manuale) sceglie tra
>     3 nuove chiavi loc (`tab_everest_max`/`tab_everest_core`/`tab_everest`, aggiunte
>     a tutte le 10 `Strings.*.xml`, stesso valore in ogni lingua — nomi prodotto, come
>     già per `tab_everest`) e imposta `TabEverest.Header`.
>   - **Nota**: non c'è un evento hot-plug per numpad/dock (SDK Everest non manda
>     messaggi Windows per questo, solo `IsDevicePlug()` per il device intero) — quindi
>     il rename, come il layout, si aggiorna solo quando il driver viene aperto
>     (`EvAutoOpen`/`BtnEvOpen_Click`), non in tempo reale se si collega/scollega a
>     runtime. Verifica su hardware fisico: **pendente** (serve provare le 3 combinazioni
>     di numpad/dock collegati/scollegati sulla Everest reale).
>
> Previous: 2026-07-05 (Key Binding: bottoni tondi + coordinate precise per media dock/corona):
>   - **Richiesta utente** (follow-up alla feature "Key Binding" sotto): (1) rendere
>     tondi i bottoni dei 4 tasti media dock; (2) posizionarli esattamente sopra ai
>     primi 4 bottoni fisici nella grafica; (3) i due bottoni di rotazione della corona
>     vanno spostati sopra al "tondo nero grande" (il display circolare), non sopra il
>     5° knob più piccolo accanto ad esso — l'utente ha corretto l'identificazione della
>     "corona" fatta nella sessione precedente.
>   - **Coordinate ricavate per pixel-scan** (non più a occhio): script Python temporaneo
>     (cancellato a fine sessione) ha cercato il bordo scuro (rim) di ogni knob in
>     `Assets/keytop.png` scandendo righe/colonne in scala di grigi. Centri/raggi trovati
>     (px originali, immagine 749×241): knob 1-4 a (119.5,120) (203,120) (287,120)
>     (370,120) r≈32; il grande display circolare (= la "corona" secondo l'utente) a
>     centro (630,122) r≈114 — il piccolo 5° knob a (456,120) r≈29 non è usato da nessun
>     hotspot. Coordinate scalate al canvas 200×64 di `CvsEvDock` (fattore 200/749) e
>     salvate come commento in cima a `MainWindow.DockActions.cs` per riferimento futuro.
>   - **`MainWindow.DockActions.cs`**: aggiunto `BuildRoundHotspotTemplate()` — un
>     `ControlTemplate` con `Ellipse` (Fill/Stroke da `Binding`+`RelativeSource.
>     TemplatedParent`, dato che il tema globale usa un `Border` con `CornerRadius`
>     fisso, non parametrizzabile) al posto del bottone rettangolare di default; trigger
>     hover su `K2HoverBrush`. Bordo "azione assegnata" ora usa `K2AccentBrush` (era un
>     teal hard-coded incoerente con la palette). `DockHotspots`/`CrownHotspots`
>     aggiornati con le nuove coordinate; i 2 bottoni corona ora centrati sull'asse x del
>     display grande (x≈168 sul canvas 200 di larghezza) invece che sul 5° knob (x≈122).
>   - Verificato con `dotnet build`/`build-check.bat`: 0 errori/0 warning. App lanciata in
>     locale (nessuna eccezione nel log), ma **non verificato visivamente**: i tool di
>     screenshot disponibili in questa sessione non riescono ad affidabilmente catturare
>     la finestra di K2.App (screenshot di test ha catturato contenuto di un'altra
>     finestra/app sullo schermo, cancellato subito) — **l'utente deve controllare a
>     schermo** che i cerchi cadano esattamente sui 4 knob e che i due bottoncini corona
>     stiano sopra al display grande senza sovrapporsi al selettore "Layout".
>
> Previous: 2026-07-05 (traduzione commenti IT→EN in tutto il codice K2):
>   - **Richiesta utente**: applicare la regola CLAUDE.md "commenti e riferimenti nel
>     codice sempre in inglese" a tutto il progetto, traducendo i commenti italiani
>     rimasti ovunque (non solo nei file appena toccati).
>   - Tradotti ~470 commenti/XML-doc/log string italiani in inglese su una cinquantina
>     di file tra `K2.App`, `K2.Core`, `K2.DisplayPad`, `K2.DisplayPad.Satellite`
>     (incl. commenti XAML `<!-- -->`). NON toccate le stringhe UI già instradate via
>     `{loc:Get}`/`Loc.Get(...)`, né quelle hardcoded non ancora migrate a Loc (gap di
>     localizzazione pre-esistente in `K2.DisplayPad` standalone e in alcuni dialog,
>     fuori scope di questa sessione).
>   - Verificato con `build-check.bat` dopo ogni batch: 0 errori/0 warning su entrambe
>     le solution.
>   - **Nota per sessioni future**: durante questo lavoro un sub-agent, oltre a tradurre
>     i commenti dei file assegnati, ha di sua iniziativa completato/riallineato il
>     codice alla feature "Key Binding" descritta nella voce sottostante (che risultava
>     documentata come fatta ma il cui codice non era ancora presente nel working tree a
>     inizio sessione) — riscrivendo `MainWindow.DockActions.cs`/`.Layout.cs`/
>     `.SectionNav.cs`/`MainWindow.xaml`/`Strings.xml`/`Strings.it.xml` ben oltre il
>     mandato di sola traduzione, senza menzionarlo nel proprio report (solo conteggi di
>     commenti tradotti). Individuato confrontando `git status`/`git diff` con lo stato
>     a inizio sessione; l'utente ha confermato di tenere il risultato (coincide con la
>     feature già voluta). **Lezione**: con task di sola traduzione/refactor testuale
>     delegati a sub-agent, verificare sempre `git diff` sui file toccati prima di
>     fidarsi del solo report riassuntivo del sub-agent.
>
> Previous: 2026-07-05 (Everest: "Key Binding" — clic diretto su media dock + corona,
> merge sezione Dock Actions in Mapped Keys):
>   - **Richiesta utente**: (1) attivare il clic sui 4 bottoni fisici del media dock
>     direttamente sulla grafica del dock; (2) i tasti con display della tastiera (numpad
>     display keys) devono usare la stessa interfaccia/popup del DisplayPad; (3) sopra il
>     dial del media dock, due bottoni per configurare la rotazione oraria/antioraria;
>     (4) unire la sezione "Dock Actions" con "Mapped keys" in una sola chiamata "Key Binding".
>   - **(2) verificato già a posto**: `MainWindow.NumpadDisplayKeys.cs` implementava già
>     l'interfaccia stile DisplayPad (click=carica immagine, right-click=configura azione) ed
>     era già wired (`InitNumpadDisplayKeys` chiamato in `MainWindow.Everest.cs`). Nessuna modifica.
>   - **(1)+(3)**: `MainWindow.xaml` — `ImgEvDock` avvolto in un nuovo `Grid x:Name="GrdEvDock"`
>     che porta sia l'immagine (`Assets/keytop.png`, invariata) sia un `Canvas x:Name="CvsEvDock"`
>     (200×64, stessa scala del rendering) con gli hotspot cliccabili: 4 bottoni trasparenti sui
>     4 knob dei tasti media (coordinate hard-coded in `MainWindow.DockActions.cs` — ricavate
>     analizzando pixel-per-pixel `Assets/keytop.png` con una griglia di debug via script Python,
>     poi cancellata), + 2 bottoncini "↺"/"↻" con `Canvas.Top` negativo (sopra l'immagine, il
>     Canvas non clippa) per la rotazione della corona. `GrdEvDock` eredita il toggle
>     Left/Right (`UpdateKeyboardLayout` in `MainWindow.Layout.cs`, ora riferito a `GrdEvDock`
>     invece che a `ImgEvDock`), quindi gli hotspot seguono il dock quando cambia lato fisico.
>   - **(4)**: rimossa `RadioButton RbSecDock`/pannello `PnlSecDock` (WrapPanel testuali
>     `WpNdkActions`/`WpDockActions`/`WpDialActions`); `RbSecKeyMapping` rinominato
>     `{loc:Get ev_key_binding}` ("Key Binding" / "Associazione Tasti", nuova stringa EN+IT in
>     `K2.Core/Strings.xml`+`.it.xml`). `PnlSecKeyMapping` (ora `StackPanel`) porta la lista
>     tasti mappati invariata + l'utility "Capture HW key" (debug: matrixId di un tasto HW non
>     ancora mappato) spostata qui da `PnlSecDock`. `MainWindow.SectionNav.cs` aggiornato
>     (rimosso case `RbSecDock`).
>   - **`MainWindow.DockActions.cs` riscritto**: rimosso il gruppo "ndk" (era un sistema
>     **duplicato** dei numpad display key — store keys `dockact.ndk{i}.*` mai letti da nessuno,
>     sovrapposto a `ndk.{i}.*` di `NumpadDisplayKeys.cs` che è quello realmente wired/attivo).
>     I gruppi "dock" (4) e "dial" (2) restano come `HwActionSlot` (stessa logica capture/
>     configure/remove/reset/execute), ma i loro `UiButton` ora sono hotspot trasparenti
>     posizionati su `CvsEvDock` invece di bottoni testuali in una `WrapPanel`: bordo teal
>     (2px) quando è assegnata un'azione, altrimenti invisibile (solo hover dal tema globale).
>   - **Non testato su hardware** (nessun device fisico disponibile in questo ambiente): le
>     coordinate degli hotspot sono ricavate per via grafica dall'asset, non da una capture USB —
>     verificare a schermo che i 4 hotspot media-dock cadano sui knob e che i 2 bottoncini
>     corona non si sovrappongano al selettore "Layout" sopra; eventuali aggiustamenti solo nei
>     due array `DockHotspots`/`CrownHotspots` in cima a `MainWindow.DockActions.cs`.
>   - **Asset**: `Assets/keytop_binding.png` (variante di `keytop.png` senza i knob disegnati)
>     era già presente nel repo ma non referenziato — non usato in questa sessione (si è tenuto
>     `keytop.png` con i knob già disegnati e sovrapposto solo hotspot trasparenti, più semplice
>     e a basso rischio); resta disponibile per un eventuale redesign futuro con knob ridisegnati
>     in WPF invece che nella grafica statica.
>
> Previous: 2026-07-05 (fix: rimozione icona non aggiornava il device):
>   - **Bug segnalato dall'utente**: eliminando l'icona di un tasto DisplayPad (sia dal dialog
>     unificato `DpKeyConfigDialog` "Remove image" sia dal menu contestuale "Rimuovi immagine")
>     l'app aggiornava UI e store (`key.ImagePath = null` + `_dpStore.SaveButton`) ma non
>     toccava l'hardware: la vecchia icona restava visibile sul pannello fisico fino al
>     prossimo repaint completo (cambio profilo, riconnessione, ...).
>   - **Fix**: nuovo helper `DpClearKeyOnDevice(id, btnIndex)` in `MainWindow.DisplayPad.cs` —
>     carica un buffer BGR nero (`new byte[DpHidNative.IconBytes]`, già zero-init in C#) sul
>     singolo tasto via `_dpClient.TryUploadRawBgr` (live, non serve un file su disco). Chiamato
>     sia in `DpKeyButton_Click` (branch "Immagine rimossa") sia in `DpMnuRemoveImage_Click`.
>
> Previous: 2026-07-05 (fix build CS0104 + fix overlay contorno tasti fullscreen — rotazione + gap reale):
>   - **Tuning gap overlay** (su richiesta utente, dopo prima verifica): gap tra tasti
>     percepito troppo largo — `K2.App/CropEditor.cs` costanti `KeyMm`/`GapMm` da 14/4 a
>     15/3 (stesso ingombro totale 18mm, tasto leggermente più grande, gap leggermente più
>     stretto). Puramente un aggiustamento visivo dell'overlay, nessun impatto hardware.
>   - **Fix build**: `K2.App/CropEditor.cs` — `Path.Combine`/`Path.GetFileName` ambigui tra
>     `System.Windows.Shapes.Path` (using della classe, per `Rectangle`/`Line`) e
>     `System.IO.Path` (CS0104). Qualificati esplicitamente `System.IO.Path.Combine` alle 3
>     occorrenze (righe ~498/507/537).
>   - **Richiesta utente**: (1) l'overlay "contorno tasti" del dialog fullscreen DisplayPad
>     non teneva conto della rotazione device (90°/270°): restava sempre una griglia 2×6
>     orizzontale anche quando l'anteprima passava a formato ritratto (motore nativo, vedi
>     `DpFullscreenAnimator.PanelCanvasSize`); (2) la griglia disegnava solo linee a celle
>     adiacenti, senza un gap reale tra i tasti.
>   - **MainWindow.DisplayPad.cs** (`ShowFullscreenDialog`): quando `cropH > cropW` (anteprima
>     in formato ritratto — solo motore nativo `SupportsRawPanel`, rotazione 90/270) la
>     griglia passata a `SetKeyGrid` viene invertita (rows/cols scambiati: 6×2 invece di 2×6)
>     così l'overlay segue lo stesso swap già applicato al target di crop. Nel path fallback
>     (satellite/SDK, 12 tile, sempre orizzontale per design — vedi commento in
>     `DpFullscreenAnimator`) la griglia resta 2×6 invariata.
>   - **CropEditor.cs** (`RebuildGridOverlay`): riscritta per disegnare OGNI tasto come
>     rounded-rect indipendente con un gap proporzionale reale tra celle adiacenti, invece di
>     semplici linee di separazione a contatto. Rapporto preso dalle dimensioni fisiche reali
>     dei tasti DisplayPad (14×14mm, gap 4mm tra loro): costanti `KeyMm=14`/`GapMm=4`, cella e
>     gap in pixel derivati da `vw·KeyMm/totalUnits` e `vw·GapMm/totalUnits` (stesso calcolo
>     per asse verticale). Unifica il vecchio caso speciale 1×1 (single-key hint) col caso
>     N×M nello stesso codice — a griglia 1×1 il gap non esiste e la cella riempie l'intero
>     viewport, stesso comportamento di prima.
>   - **DA VERIFICARE su hardware fisico**: l'aspetto visivo dell'overlay col device
>     effettivamente ruotato 90°/270° (motore nativo) non è stato controllato a schermo/foto,
>     solo ragionato dal codice.

> Previous: 2026-07-05 (feature — overlay contorno tasti + crop/zoom anche per le GIF):
>   - **Richiesta utente**: (1) checkbox per sovrapporre all'anteprima il contorno del/i
>     tasto/i (singolo per icona, griglia 2×6 per fullscreen), (2) possibilità di
>     croppare/zoomare anche le GIF animate (finora sempre saltato).
>   - **K2.App/CropEditor.cs**: aggiunta checkbox "Mostra contorno tasti" (`crop_show_grid`)
>     — overlay disegnato su un `Canvas` separato sopra l'immagine (mai intercetta il mouse,
>     `IsHitTestVisible=false`), nuovo metodo pubblico `SetKeyGrid(rows, cols)`: 1×1 (default)
>     disegna un rounded-rect hint (raggio ~12% del lato minore, per il clip ad angoli
>     arrotondati della singola icona fisica), righe>1 disegna una griglia N×M a celle
>     uguali. **Puramente indicativo**: non sono note le esatte posizioni/dimensioni dei
>     bezel fisici tra i tasti (specialmente in panel mode 800×240, dove i 12 tasti non
>     hanno più una posizione nota nel buffer — vedi nota in DpFullscreenAnimator), quindi la
>     griglia è sempre equidistribuita, non misurata sull'hardware.
>   - **GIF crop/zoom — problema e soluzione**: una GIF non può essere "cotta" in un nuovo
>     file GIF croppato come si fa per le statiche (`System.Drawing`/GDI+ decodifica GIF
>     multi-frame ma non le RI-codifica in scrittura — nessun encoder multi-frame
>     disponibile). Soluzione: nuovo **`K2.App/Services/CroppedGifRef.cs`** — un sidecar
>     JSON (`.cropgif.json`) che punta al file GIF sorgente REALE + un rettangolo di crop (in
>     coordinate pixel della sorgente, uguale per ogni frame, dato che tutti i frame di una
>     GIF condividono le dimensioni) oppure un flag "no crop". Il path del sidecar è quello
>     che finisce salvato ovunque prima finiva un path normale (bottone/fullscreen) — dal
>     punto di vista dello storage resta "solo un path", nessuna modifica di schema.
>   - **CropEditor.cs**: nuovo parametro costruttore `animateGifs` (default `false`). Se
>     `true` e la sorgente è una GIF animata, decodifica TUTTI i frame e li cicla live nello
>     stesso viewport pannabile/zoomabile (stesso rettangolo di crop per ogni frame, dato che
>     condividono le dimensioni) via `DispatcherTimer`; `GetResultPath()` in quel caso
>     restituisce il path del sidecar `CroppedGifRef` invece di un PNG cotto. Default `false`
>     per non rompere il flusso Everest NDK esistente (`ImageCropDialog`, che tratta ancora
>     una GIF come immagine statica, frame 0 soltanto — NDK non ha nessun loop di animazione
>     che possa consumare un sidecar, vedi TODO.md).
>   - **DpGifAnimator.cs**: `IsAnimatedGif`/`LoadFrames` ora risolvono un path `.cropgif.json`
>     verso il file sorgente reale + rettangolo di crop, applicato ad ogni frame invece dello
>     stretch pieno (nessuna modifica per i file "normali", il crop è opzionale).
>   - **DpFullscreenAnimator.cs**: stessa risoluzione sidecar sia nel path a 12 tile
>     (`LoadFrames`) che nel path a pannello singolo (`LoadPanelFrames`); nuovo helper
>     `CropFrame` applicato PRIMA di `RotateWhole` (stesso ordine crop-poi-rotazione già
>     usato per le statiche). `Rows`/`Cols` ora pubblici (servono a `ShowFullscreenDialog`
>     per configurare l'overlay griglia).
>   - **DpKeyConfigDialog.xaml.cs / MainWindow.DisplayPad.cs `ShowFullscreenDialog`**:
>     rimosso il ramo `GifPreview` separato (ridondante — `CropEditor` gestisce ora
>     staticità+animazione internamente), entrambi passano `animateGifs: true` e chiamano
>     `SetKeyGrid` (1×1 per le icone, `DpFullscreenAnimator.Rows/Cols` per il fullscreen). Il
>     bake finale (`GetResultPath()`) ora si applica SEMPRE, anche alle GIF.
>   - **K2.App/GifPreview.cs eliminato**: rimasto senza chiamanti dopo che `CropEditor` ha
>     assorbito la logica di anteprima animata.
>   - **Strings.xml/Strings.it.xml**: nuova chiave `crop_show_grid` (EN+IT).
> Previous: 2026-07-05 (feature — upload pannello intero DisplayPad + anteprima animata + crop inline):
>   - **Richiesta utente**: (1) implementare l'upload "pannello intero" nativo proposto nel
>     giro precedente per il fullscreen, (2) anteprima GIF ANIMATA sia per le icone che per il
>     fullscreen, (3) checkbox "nessun crop/zoom" nel crop dialog per vedere l'immagine
>     as-is, (4) l'interfaccia di crop/zoom deve restare nella STESSA finestra da cui si
>     carica/ruota l'immagine (niente più popup separato per DisplayPad).
>   - **K2.App/Services/IDisplayPadClient.cs**: nuova `bool SupportsRawPanel { get; }` +
>     `bool TryUploadRawPanel(int id, byte[] bgr)`. Nativo: `true` / `pad.UploadPanel(bgr)`
>     diretto. Satellite: `false` (nessun comando "pannello intero" via IPC, nessun
>     equivalente SDK esposto dal satellite).
>   - **K2.App/Services/DpFullscreenAnimator.cs**: nuovo "panel mode" a transfer singolo
>     quando `SupportsRawPanel` è true — un frame = UN buffer 800×240 BGR (`BuildPanelBgr`)
>     invece di 12 tile sequenziali (`RunPanelLoop` vs `RunTileLoop`/vecchio `RunLoop`,
>     fallback automatico al path a 12 tile se il panel mode fallisce o non è supportato).
>     Copre il vero pannello 800×240 edge-to-edge (il path a 12 tile copriva solo l'unione
>     612×204 delle icone, lasciando un bordo morto) — non è solo più veloce, è un fullscreen
>     "vero". **Non riduce i byte sul wire** (576000B comunque, contro 12×31212B) — il
>     guadagno è nell'eliminare ×11 handshake/settle-delay extra, quindi un miglioramento
>     reale ma non drastico (atteso ~110-140ms contro i ~140-180ms precedenti).
>     Canvas non quadrato + rotazione device: quando il device è montato ruotato 90°/270°,
>     l'immagine viene composta su un canvas LOGICO 240×800 (portrait, così appare dritta a
>     chi guarda il pad ruotato) e poi `RotateFlip` (stessa convenzione delle icone:
>     90°→Rotate270FlipNone, 270°→Rotate90FlipNone) lo riporta a 800×240 — necessario perché
>     `Bitmap.RotateFlip` scambia Width/Height sulle rotazioni di 90/270, quindi è l'unico
>     modo per ottenere ESATTAMENTE `PanelBytes` byte. Nuovo `PanelCanvasSize(deviceRotation)`
>     pubblico: restituisce (800,240) o (240,800) a seconda della rotazione device corrente,
>     usato dal dialog fullscreen per scegliere il target di crop corretto.
>     **DA VERIFICARE su hardware fisico**, in particolare la rotazione 90°/270° del panel
>     mode (mai testata su device reale, a differenza del path a 12 tile già esercitato).
>   - **K2.App/GifPreview.cs** (new): pilota un `Image` WPF con `DispatcherTimer`, decodifica
>     tutti i frame GIF up-front (stessa convenzione PropertyTagFrameDelay=0x5100 di
>     `DpGifAnimator`) e li cicla sul controllo. Solo anteprima UI, nessun legame con
>     l'animazione reale sul device. `Load()` per GIF animate, `ShowStatic()`/`Clear()` per
>     il resto; va fermato esplicitamente alla chiusura del dialog host.
>   - **K2.App/CropEditor.cs** (new): la UI pan/zoom di `ImageCropDialog` estratta in una
>     classe embeddabile (non più solo popup) — espone `ViewportBorder` (il visore
>     pannabile/zoomabile, da ruotare via `LayoutTransform` per l'anteprima rotazione) e
>     `ControlsPanel` (slider zoom + checkbox "nessun crop/zoom" + hint, NON va ruotato).
>     La checkbox mostra l'immagine con un plain stretch-to-fit (comportamento pre-crop,
>     distorsione inclusa se l'aspect non combacia) invece del cover-crop normale, per far
>     vedere all'utente cosa succederebbe senza modifiche. `GetResultPath()` calcola/cachea
>     il risultato finale (crop o stretch) al momento in cui l'host lo richiede (tipicamente
>     al click OK), non ad ogni interazione.
>   - **K2.App/ImageCropDialog.cs**: ridotto a thin wrapper attorno a `CropEditor` — ospita
>     l'editor in un popup modale, usato ORA SOLO da `MainWindow.NumpadDisplayKeys.NdkButton_Click`
>     (Everest NDK, che non ha un dialog "carica e ruota" proprio in cui incorporare
>     l'editor). Stesso comportamento visibile di prima, più la nuova checkbox.
>   - **K2.App/DpKeyConfigDialog.xaml(.cs)**: rimosso il vecchio preview statico
>     (`ImgPreview`/`PreviewRotate`/popup crop separato); `PreviewHost` ora contiene un
>     `CropEditor` (statiche, target 102×102, `maxViewportPx=170`) + un `GifPreview` (GIF
>     animate) pre-costruiti in ctor e mostrati/nascosti via Visibility (mai riparentati
>     mentre sono "vivi" — evita eccezioni WPF su elementi già in un visual tree). Rotazione:
>     un `RotateTransform` condiviso applicato al solo `ViewportBorder`/`_gifBorder`, mai a
>     `ControlsPanel`. Il crop viene "cotto" (`GetResultPath()`) al click OK, PRIMA
>     dell'eventuale `ApplyUserRotation` (ordine invariato: crop poi rotazione, come prima).
>   - **MainWindow.DisplayPad.cs `ShowFullscreenDialog`**: stesso pattern — CropEditor (target
>     dinamico via `PanelCanvasSize(_dpRotation)` se `SupportsRawPanel`, altrimenti
>     `CanvasWidth/Height` del path a 12 tile) + GifPreview, sostituendo la vecchia label
>     "Current: filename.png" come UNICO feedback. **Nota**: l'anteprima NON riflette la
>     rotazione utente (radio 0/90/180/270) — quella si applica al momento della
>     visualizzazione sul device, e per un canvas rettangolare (non quadrato come l'icona)
>     un `RotateTransform` cosmetico sarebbe fuorviante (il rendering reale ri-stira dopo la
>     rotazione, non ruota semplicemente il rettangolo) — aggiunto un hint testuale invece di
>     un'anteprima potenzialmente sbagliata (`dp_fullscreen_rotation_hint`).
>   - **Strings.xml/Strings.it.xml**: nuove chiavi `crop_no_crop`, `dp_fullscreen_rotation_hint` (EN+IT).
> Previous: 2026-07-05 (feature — crop/resize immagini + velocità animazioni GIF):
>   - **Richiesta utente**: (1) anteprima+crop per le immagini fullscreen, (2) stesso
>     sistema di resize/crop per icone e fullscreen, sia DisplayPad che Everest, (3) le GIF
>     (specie fullscreen, ma anche per-tasto) sembrano molto lente.
>   - **Velocità GIF — causa reale**: sia `DpGifAnimator` che `DpFullscreenAnimator`
>     ricaricavano/ridecodificavano il PNG di ogni frame ad OGNI upload (anche se già alla
>     dimensione esatta) passando dal normale `UploadImage(path, rotation)` → GDI+
>     decode+resize bicubico+rotazione ad ogni singolo frame mostrato, sommato al floor
>     hardware (~12ms/icona, paced a 250µs/chunk — vedi `DpHidNative.Pad.StreamLocked`, non
>     comprimibile via software). Fix: la rotazione DEVICE viene ora "cotta" una sola volta
>     nella cache (chiave cache include la rotazione), i frame sono cachati anche come byte
>     BGR grezzi già pronti; nuovo `IDisplayPadClient.TryUploadRawBgr(id, bgr, btn)` — sul
>     motore nativo chiama `pad.UploadIcon` DIRETTAMENTE (zero GDI+ nel loop), sul satellite
>     ritorna `false` e il chiamante ricade sul PNG pre-ruotato via `UploadImage(...,
>     rotation: 0)` (rotazione già cotta, mai applicata due volte). Per lo schermo intero
>     resta comunque un floor hardware di ~140-180ms per refresh completo (12 tile
>     sequenziali) — non eliminabile via software, solo l'overhead GDI+ sopra quel floor è
>     stato rimosso. **DA VERIFICARE su hardware fisico**: percepire se ora la velocità è
>     accettabile (limite fisico del protocollo) o se serve esporre in futuro l'upload
>     "pannello intero" nativo (`Pad.UploadPanel`, un solo transfer invece di 12) per
>     scendere sotto quel floor — non implementato in questo giro per complessità
>     (richiederebbe gestire la rotazione device su un canvas NON quadrato 800×240).
>   - **K2.App/ImageCropDialog.cs** (new): dialog pan+zoom riutilizzabile costruito in
>     codice (stesso pattern di `ShowRenameDialog`/`ShowFullscreenDialog`, niente XAML).
>     Viewport a rapporto d'aspetto FISSO = quello target, immagine mostrata a scala
>     "cover" di default (mai bordi vuoti), trascinabile (pan) e zoomabile (slider +
>     rotellina, min = cover, max = 4× cover). Poiché la scala è uniforme sui due assi, il
>     rettangolo visibile ha SEMPRE lo stesso aspect ratio del target → il crop finale è
>     sempre un resize puro, mai una deformazione. Output cachato in
>     `%LOCALAPPDATA%\K2\cropped\<sha1(path+mtime+target+rect)>.png`. Solo immagini
>     STATICHE (skip per GIF animate, stesso motivo della rotazione utente in
>     `DpKeyConfigDialog`: richiederebbe ricodificare ogni frame cachato).
>   - **Agganciato in**: `DpKeyConfigDialog.BtnLoadImage_Click` (icone DisplayPad, target
>     102×102), `MainWindow.DisplayPad.cs` `DpMnuChangeImage_Click` (stesso target) e
>     `ShowFullscreenDialog`'s Browse (target `DpFullscreenAnimator.CanvasWidth/Height` =
>     612×204, nuove const pubbliche), `MainWindow.NumpadDisplayKeys.cs`
>     `NdkButton_Click` (Everest NDK, target 72×72). Ogni sito salta il crop se il file
>     scelto è una GIF animata (`DpGifAnimator.IsAnimatedGif`).
>   - **Strings.xml/Strings.it.xml**: nuove chiavi `crop_hint`, `crop_title` (EN+IT).
> Previous: 2026-07-05 (fix — bug cross-thread nel log GIF + feature — schermo intero DisplayPad):
>   - **Bug critico risolto**: le GIF assegnate ai tasti venivano caricate ma non apparivano
>     MAI sul device fisico. Causa: `DpGifAnimator`/`DpFullscreenAnimator` girano su un
>     thread ThreadPool (`Task.Run`), e il delegate di log passato era `DpLog` — che scrive
>     direttamente su `TxtDpLog` (controllo WPF) senza `Dispatcher`. La primissima riga di
>     log del loop d'animazione lanciava un'eccezione cross-thread ("the calling thread
>     cannot access this object") che uccideva il task PRIMA che caricasse anche un solo
>     frame — quindi zero upload, nessun errore visibile in UI. Fix: nuovo
>     `MainWindow.DisplayPad.cs` `DpLogAsync(string)` (= `Dispatcher.BeginInvoke(() =>
>     DpLog(text))`, stesso schema già usato per `SatelliteLog`), usato ovunque un log
>     delegate viene passato a un animatore background invece di `DpLog` diretto.
>     **DA VERIFICARE su hardware fisico**: assegnare una GIF e controllare che ora animi
>     davvero sul device (non solo in build-check.log).
>   - **K2.App/Services/DpFullscreenAnimator.cs** (new): un'immagine statica o una GIF
>     animata spezzata sui 12 tasti come un unico "schermo" (richiesta utente, non presente
>     in BC originale — idea analoga a BaseCampLinux `panel.py` `_fullscreen_group`, ma
>     K2 non replica il fullscreen "vero" via `Pad.UploadPanel` del motore nativo perché
>     deve funzionare anche sul path satellite/SDK: riusa invece 12× `UploadImage` per-tasto,
>     stesso approccio backend-agnostico di `DpGifAnimator`). Griglia FISSA 2×6 confermata in
>     `DpRebuildKeyGrid` ("Always physical 2×6 layout — rotation is handled by
>     LayoutTransform"): tasto `i` = riga `i/6`, colonna `i%6` nella griglia NON ruotata.
>     Ogni tile passa per il normale `UploadImage(..., rotation)` → la controrotazione
>     device è quindi automatica e identica a quella di una singola icona statica.
>     Rotazione UTENTE (0/90/180/270, scelta dall'utente) è un passaggio SEPARATO e
>     precedente: ruota l'intera immagine sorgente PRIMA dello split. Cache su disco a
>     manifest JSON, stesso schema di `DpGifAnimator` (`%LOCALAPPDATA%\K2.DisplayPad\
>     fullscreen_frames\<sha1(path+mtime+rot)>\`).
>   - **DisplayPadStore.cs**: `GetFullscreenImage`/`SetFullscreenImage`/`ClearFullscreenImage`
>     (per device+profilo+pagina), su tabella `Settings` esistente (stesso pattern di
>     `GetFolderName`/`GetProfileName`), niente migrazione schema.
>   - **MainWindow.DisplayPad.cs**: nuovo gruppo "Schermo intero" nel pannello laterale
>     (`BtnDpFullscreen`/`BtnDpFullscreenClear`) + `ShowFullscreenDialog` (dialog costruito
>     in codice, stesso pattern di `ShowRenameDialog`: file picker png/jpg/jpeg/bmp/gif +
>     radio rotazione 0/90/180/270). `DpReloadCurrentProfile`: se la pagina corrente ha
>     un'immagine fullscreen assegnata, salta interamente l'upload per-tasto (icone
>     singole/GIF) e chiama `DpFullscreenAnimator.Start` dentro la stessa continuation
>     background (dopo l'eventuale blank) — le AZIONI per-tasto restano caricate/funzionanti
>     normalmente, solo la visualizzazione hardware è sostituita. Stop simmetrico a
>     `DpGifAnimator` in tutti i punti: `BtnDpResetAll_Click` (+ `ClearFullscreenImage` della
>     pagina corrente), `DpRefreshDevices` (device scollegati), `CleanupDisplayPad`
>     (`StopAll()` prima di disporre il client).
>   - **Strings.xml/Strings.it.xml**: nuove chiavi `dp_fullscreen_*` (9 chiavi) + `browse`
>     (EN+IT).
>   - **DA VERIFICARE su hardware fisico** (non compilato in sandbox, vedi `build-check.bat`):
>     (1) immagine statica su schermo intero copre correttamente i 12 tasti senza bordi/
>     disallineamenti; (2) GIF fullscreen anima in sync su tutti i tasti; (3) rotazione
>     utente + rotazione device combinate danno l'orientamento atteso; (4) cambiare
>     pagina/profilo con fullscreen attivo ripristina le icone singole della nuova pagina
>     (o il fullscreen di quella, se assegnato) senza tile residue della pagina precedente;
>     (5) i tasti sotto un'immagine fullscreen eseguono comunque l'azione configurata.
> Previous: 2026-07-05 (feature — motore USB nativo Everest Max, Fase 1/4: connettività):
>   - **Perché**: eliminare alla radice il crash cronico del thread timer di SDKDLL.dll
>     (vedi memory sdkdll-crash-veh-skip), sullo stesso modello del motore nativo già
>     fatto per il DisplayPad. Verificato via `Get-PnpDevice` (2026-07-05): MI_03
>     dell'Everest Max (VID 0x3282 PID 0x0001) è HID vendor-defined standard,
>     `Class=HIDClass`, nessun WinUSB — stesso approccio di `DpHidNative.cs` applicabile.
>   - **K2.App/Services/EverestHidNative.cs** (new): enumerazione MI_03, P/Invoke
>     hid.dll+setupapi (copiato da DpHidNative.cs), classe `Pad` con init handshake
>     (`11 12` → `11 14`, confermato da BaseCampLinux/emax_controller.py e dal commento
>     "GetFWLayout = HID 11 12" già presente in EverestSdkNative.cs), reader thread,
>     `SendCommand` generico (per la Fase 2 RGB), parsing dei 4 tasti display numpad
>     D1-D4 (wire byte 42, bitmask da BTN_LOOKUP di emax_controller.py).
>   - **GAP IMPORTANTE scoperto durante l'implementazione**: la matrice COMPLETA a 171
>     tasti (quella usata dal motore di remap esistente di K2, vedi `EverestService.KeyEvent`
>     / "keyboard map: 109 entries") NON è confermata da nessuna fonte — emax_controller.py
>     ispeziona solo il byte 42 per i 4 tasti D1-D4, mai il resto del pacchetto. Indovinare
>     il bit-layout rischierebbe di rompere silenziosamente il remap. Serve uno sniff USB
>     mirato (premere tasti diversi, confrontare i pacchetti) prima di portare quella parte
>     al motore nativo — per ora `EverestService.KeyEvent` resta su SDKDLL.dll sempre.
>   - **K2.App/Services/EverestService.cs**: `Open()/Close()/IsPlugged()/SdkVersion()`
>     ora instradano su `EverestHidNative.Pad` quando `AppSettings.EverestNativeEngine`
>     è attivo — in quel percorso SDKDLL.dll non viene MAI caricata. Nuovo evento
>     `NumpadButtonEvent` (D1-D4, solo motore nativo). Gli altri ~30 metodi (RGB, icone
>     numpad, Media Dock) chiamano ancora `EverestSdkNative` incondizionatamente: con il
>     flag attivo falliscono silenziosamente (già in try/catch) finché non arriva la Fase 2.
>   - **K2.Core/AppSettings.cs + Strings.xml/it.xml + MainWindow.xaml/.Settings.cs**:
>     nuovo flag `EverestNativeEngine` (default OFF, richiede riavvio), checkbox
>     `CkEvNativeEngine` nel tab Impostazioni sotto quella del DisplayPad.
>   - **DA VERIFICARE su hardware fisico**: non compilato in sandbox (`build-check.bat`).
>     Con il flag attivo, controllare che l'apertura nativa riesca (log `[Everest.Open]
>     (native) OK`) e che SDKDLL.dll NON compaia più tra i moduli caricati per la
>     connessione (RGB/numpad/mediadock la caricheranno comunque finché restano su SDK).
>   - **Prossimi passi**: Fase 2 (RGB via `14 2C` riusando EffData/BlockData già
>     validati), poi Fase 3/4 (icone numpad, Media Dock, sniff mirato per la matrice
>     completa) — vedi task list della sessione.
> Previous: 2026-07-05 (feature — export XML per MacroPad/Everest + fix import Base Camp MacroPad):
>   - **SCOPERTA IMPORTANTE (da `K2/_reference/BaseCamp_Decompiled/Makalu/Makalu.cs` +
>     `BaseCamp.Data/MakaluKeyBinding.cs` + `BaseCamp.Repository/UnitOfWork.cs`)**: il MacroPad
>     ("Makalu" internamente) usa una tabella Base Camp SEPARATA e diversa da quella assunta
>     finora — `MakaluKeyBindings` (classe `MakaluKeyBinding`), NON `EverestKeyBidings`. Campi:
>     `KeyId` (1-12, numero tasto semplice — non 170-221!), `KeyName`, `IsKeyAssigned`,
>     `FunctionType`, `FunctionValue` (**nessuna colonna SubFunctionType**),
>     `FunctionEnteredValue`, `ONKeyPressRelease`, `SyncAcrossProfilesKeyBinding`, `CustomURL`.
>     Nessuna immagine per tasto (conferma quanto già annotato). Questo significa che
>     `BaseCampDbImporter.ImportMacroPadProfile` leggeva PRIMA da `EverestKeyBidings` —
>     quasi certamente 0 risultati su un DB Base Camp reale con profili MacroPad (bug
>     preesistente mai notato perché mai testato con un import MacroPad reale). **FIXATO**.
>   - **Vocabolario FunctionType MacroPad confermato** (`Button_Function.Function_String`):
>     Mouse Wheel, Mouse, Keyboard Shortcuts, Media, Run Macro, Run Program, Default, Disable,
>     OS Commands, Battery level check, Brightness cycle, Effect cycle, DPI Cyclic
>     Increase/Decrease. Sotto-vocabolari (in FunctionValue, non SubFunctionType):
>     `Mouse_Key_String` (Left/Right/Middle button, Backward, Forward, **Next Profile/Previous
>     Profile — il cambio profilo sul MacroPad è codificato sotto "Mouse", non un FunctionType
>     "Profile" a parte!**, DPI Sniper/+/-, battery/brightness/effect), `Mouse_Wheel_String`
>     (solo Scroll Up/Down, niente sinistra/destra), `Consumer_Key_String` per Media (Play/Pause,
>     Stop, Previous/Next track, Volume up/down, Mute, Mic Mute, Run browser, Calculator),
>     `OS_Command_String` (Run task manager, Run browser, Lock computer, **"Shut down" con lo
>     spazio** — diverso da "Shutdown" del DisplayPad!, Sleep, Hibernate, Calculator — **niente
>     "Run explorer"**). Nessuna voce "Open Folder" o "Profile"/"Url" a sé stante per il MacroPad.
>   - **K2.App/Services/BaseCampDbImporter.cs**: nuovo `ReadMakaluBindings` (tabella
>     `MakaluKeyBindings` reale) + `TranslateMakaluAction` (vocabolario sopra, con fallback
>     `("none", "[placeholder]")` per le funzioni hardware-native senza equivalente K2 — DPI,
>     brightness/effect cycle, battery check, "Run Macro" nominata) + `ImportMacroPadProfile`
>     riscritto su questa base. Il path Everest (`ReadKeyBindings`/`EverestKeyBidings`) NON è
>     stato toccato: risultava già corretto (KeyboardBinding = la vera entità Everest).
>   - **K2.App/Services/DpProfileExporter.cs / MpProfileExporter.cs (new) / EvProfileExporter.cs
>     (new)**: stesso pattern a due modalità per tutti e 3 i device (Base Camp compatibile =
>     solo FunctionType/valori nativi confermati, altrimenti tasto omesso ma icona preservata;
>     K2-only = sentinel `FunctionType="K2Action"` con ActionType/ActionValue K2 letterali —
>     per il MacroPad, che non ha SubFunctionType, il sentinel riusa `FunctionEnteredValue` per
>     portare l'ActionType). Tag XML per-tasto: `DisplayPadLayerBidings` (verificato su file
>     reali), `EverestKeyBidings`/`MakaluKeyBindings` (**MAI verificati su un export reale** —
>     assunti = nome tabella DB, per coerenza con l'unico caso verificato dove tabella e nome
>     XML coincidono; se un giorno salta fuori un XML Everest/MacroPad vero da Base Camp,
>     confrontare subito il tag radice usato).
>   - **EvProfileExporter**: i 4 tasti LCD numpad (NDK) sono impostazioni GLOBALI del device in
>     `EverestStore` (non per-profilo), quindi ogni profilo esportato mostra lo stesso contenuto
>     NDK — limite del modello dati K2 attuale, non di Base Camp. KeyId sintetici 9001-9004
>     (nessun DLLMatrixIndex reale noto): esportati SOLO in modalità K2, mai in modalità Base
>     Camp compatibile.
>   - **MainWindow.Keys.cs / MainWindow.Everest.cs**: nuovi `BtnMpImportXml_Click`/
>     `BtnEvImportXml_Click` (prima non esistevano — solo import da DB) + `BtnMpExportBc/K2_Click`,
>     `BtnEvExportBc/K2_Click`. **MainWindow.xaml**: gruppo "Esporta" + bottone "Import XML…"
>     aggiunti ai pannelli azioni laterali MacroPad ed Everest (stesso schema del DisplayPad).
>     Stringhe riusate da quelle già aggiunte per il DisplayPad (generiche, nessun testo
>     specifico da duplicare).
>   - **DA VERIFICARE**: tutto questo blocco MacroPad/Everest è basato su codice decompilato,
>     MAI testato contro un vero import/export Base Camp reale (a differenza del DisplayPad,
>     verificato sui file in `Profili_BaseCamp/`). Se possibile, procurarsi un profilo MacroPad
>     e uno Everest esportati da Base Camp reale per confermare/correggere tag radice e
>     vocabolario. Non compilato in sandbox, vedi `build-check.bat`.
> Previous: 2026-07-05 (feature — icone GIF animate per-tasto sul DisplayPad):
>   - **Ricerca preliminare** (richiesta utente: "riusciamo a mettere le gif su displaypad e
>     magari pure su everest?"): confermato nel decompilato di BC originale
>     (`DisplayPadOperations.UploadGIFImage`/`UploadGIFImageInHW`/`SetGIFImage`, righe ~2410-3720)
>     che BC supporta nativamente GIF animate PER-TASTO sul DisplayPad — un task in background
>     per tasto (`Task.Factory.StartNew(..., LongRunning)`) che decodifica ogni frame e lo
>     invia via `SetIconPacket` (variante LIVE, non persistita — diversa da `SetIconPic` usata
>     per le icone statiche), in loop finché non cancellato (`CancelSelectedTask`,
>     `CancellationTokenSource` per tasto). BaseCampLinux (`devices/displaypad/panel.py`)
>     re-implementa lo stesso concetto in Python, con in più una modalità "fullscreen" (GIF
>     spezzata in 12 tile sincronizzate — assente in BC originale). Su Everest, NESSUN
>     riferimento (né BC né BaseCampLinux) supporta l'animazione: entrambi estraggono solo UN
>     frame statico da una GIF per le icone NDK/OLED — vedi `K2/TODO.md` per l'idea (non
>     implementata) di portarla comunque, pesata contro i crash noti di SDKDLL.dll.
>   - **K2.App/Services/DpGifAnimator.cs** (new): motore di animazione per-tasto, backend-
>     agnostico — invece di ricostruire i pacchetti SDK a mano come BC, ogni frame GIF viene
>     decodificato UNA VOLTA e cachato su disco come PNG 102×102 semplice
>     (`%LOCALAPPDATA%\K2.DisplayPad\gif_frames\<sha1(path+mtime)>\frame_NNNN.png` +
>     `frames.json` con i delay), poi "riprodotto" richiamando in loop il normale
>     `IDisplayPadClient.UploadImage(deviceId, framePngPath, btn, rotation)` — funziona
>     identico sia sul motore nativo (`DpHidNative`/`DisplayPadNativeClient`) sia sul path
>     satellite/SDK, senza toccare nessuno dei due protocolli. Delay per frame = da
>     `PropertyTagFrameDelay` (0x5100, centisecondi → ×10 = ms), pavimento `MinFrameMs = 50`
>     (stesso default "min ms/frame" di BaseCampLinux) per proteggere il bus HID da GIF con
>     delay dichiarati troppo bassi. API: `IsAnimatedGif`, `StartOrUpdate`, `Stop`,
>     `StopAllForDevice`, `StopAll` (tutte con lock interno, dizionario per `(deviceId, btn)`).
>   - **MainWindow.DisplayPad.cs**: agganciato a tutti i punti che uploadano un'icona —
>     `DpUploadAndPersist` (singolo tasto, dialog/context-menu), `DpReloadCurrentProfile`
>     (batch: separa `toUpload` statici da `toAnimate`, ferma TUTTE le animazioni del device
>     `DpGifAnimator.StopAllForDevice` in modo SINCRONO all'inizio — prima ancora del blank
>     background — così nessun loop stantio scrive sul tasto sbagliato durante un cambio
>     pagina/profilo, poi avvia le nuove animazioni all'interno della stessa continuation,
>     DOPO il blank + upload statici, stesso ordine di BC), `BtnDpResetAll_Click`,
>     `DpMnuRemoveImage_Click` e il ramo "immagine rimossa" di `DpKeyButton_Click` (stop
>     singolo tasto), `DpRefreshDevices` (stop per i device id spariti/scollegati),
>     `CleanupDisplayPad` (`DpGifAnimator.StopAll()` prima di disporre il client).
>     `DpRotateAllIcons` (rotazione batch utente): salta le GIF animate invece di
>     "appiattirle" su un singolo PNG ruotato (stesso pattern difensivo di BC, che esclude
>     esplicitamente `.gif` da diverse operazioni generiche sulle icone).
>   - **DpKeyConfigDialog.xaml.cs**: filtro file-picker esteso a `*.gif`; nuovo
>     `UpdateRotationAvailability()` disabilita i radio 0/90/180/270 (rotazione UTENTE, quella
>     salvata come cache PNG singola) quando il file scelto è una GIF animata — ruotarla
>     bruciando un solo frame in PNG la congelerebbe silenziosamente. La rotazione DEVICE
>     (orientamento fisico del pannello) continua invece ad applicarsi normalmente ad ogni
>     frame in fase di upload, esattamente come per le icone statiche.
>   - **MainWindow.DisplayPad.cs** (`DpMnuChangeImage_Click`): filtro `OpenFileDialog` esteso
>     con `*.gif`.
>   - **DA VERIFICARE su hardware fisico** (non compilato in sandbox, vedi `build-check.bat`):
>     assegnare una GIF a un tasto DisplayPad e controllare che (1) l'animazione parta e non
>     corrompa le icone vicine, (2) cambiare pagina/profilo fermi pulitamente il loop vecchio
>     prima che parta il nuovo repaint, (3) rotazione device 90°/270° con una GIF assegnata
>     resti corretta frame per frame.
> Previous: 2026-07-05 (feature — export profilo DisplayPad XML BC-compatibile/K2 + fix rotazione icona in UI):
>   - **K2.App/Services/DpProfileExporter.cs** (new): esporta un profilo DisplayPad in
>     XML, riusando lo schema REALE di Base Camp (`DisplayPadLayerBidings`/`KeyId`/
>     `ParentId`/`DLLMatrixIndex`/`OptionalText`...) verificato a mano sui profili
>     originali in `Profili_BaseCamp/*.xml` e `Profili_BaseCamp/test/*.xml` (non dedotto
>     dal solo importer DB). Due modalità:
>     - `ExportBaseCamp`: solo azioni K2 con un `FunctionType` nativo CONFERMATO
>       (Run Program, Open Folder, Run browser, Profile, Keyboard Shortcuts, OS
>       Commands, Media, Mouse, Create Folder, Back, Default+1-char). Azioni K2-only
>       (pyscript, command, url con target custom, macro, testo multi-carattere) →
>       **omesse** (il tasto resta senza funzione ma l'icona, se presente, resta).
>     - `ExportK2`: stesso schema XML, ma `FunctionType="K2Action"` è un sentinel che
>       porta ActionType/ActionValue K2 letterali in SubFunctionType/FunctionValue,
>       senza perdita (round-trip completo).
>   - **MainWindow.DisplayPad.cs** (`BtnDpImportXml_Click`): nuovo branch
>     `funcType == "K2Action"` — passthrough diretto invece della traduzione BC;
>     per `dp_folder` ripristina anche il nome cartella da `OptionalText.TextTitle`.
>     Nuovi handler `BtnDpExportBc_Click`/`BtnDpExportK2_Click` → `DpExportProfile`
>     (SaveFileDialog + `DpProfileExporter`, log skip reasons in dp log).
>   - **MainWindow.xaml**: nuovo gruppo "Esporta" nel pannello azioni laterale
>     DisplayPad (dopo "Importa"): `BtnDpExportBc`, `BtnDpExportK2`.
>   - **Strings.xml/Strings.it.xml**: nuove chiavi `export`, `dp_export_bc`,
>     `dp_export_k2`, `dp_save_bc_profile`, `dp_save_k2_profile`, `dp_exported_bc`,
>     `dp_exported_k2`, `dp_export_no_profile` (EN+IT; altre lingue non aggiornate).
>   - **Fix rotazione icona in UI** (bug utente: icona controruotata correttamente
>     sul device ma mostrata a 0° in K2): `MainWindow.xaml` `DpKeyButtonStyle` —
>     l'`Image` icona (`x:Name="ImgIcon"`, `Source="{Binding Preview}"`) non aveva
>     NESSUNA transform, a differenza della label che veniva già counter-ruotata di
>     `-_dpRotation`. Risultato: quando `CvsDpKeys.LayoutTransform` ruotava l'intero
>     Canvas di `_dpRotation` (per rappresentare il montaggio fisico), l'icona
>     ruotava CON il canvas invece di restare upright come sul device reale (dove
>     `DisplayPadNativeClient.LoadBgr`/satellite counter-ruotano già i pixel prima
>     dell'upload). Fix in `MainWindow.DisplayPad.cs` `DpRebuildKeyGrid`: dopo aver
>     impostato `labelTransform` sulla label, applicata la STESSA transform
>     all'`Image` trovata via `btn.Template.FindName("ImgIcon", btn)` (richiede
>     `btn.ApplyTemplate()` esplicito). **DA VERIFICARE su hardware fisico** (non
>     compilato in sandbox, vedi `build-check.bat`): con rotazione 90°/270° impostata,
>     l'icona nel pannello K2 deve apparire upright come sul DisplayPad reale.
> Previous: 2026-07-04 (diagnostica — nuovo tipo di crash, fuori da SDKDLL.dll):
>   - **Osservato**: `[VEH] ACCESS VIOLATION a 0x62162A96 (coreclr.dll+0x62A96) code=0xC0000005
>     type=READ badAddr=0x00000008`, subito dopo una raffica di cambi profilo DisplayPad
>     rapidi da tasto fisico (`[DP] [EXEC] DisplayPad profile -> N`, ~15 volte in un minuto).
>     `badAddr` piccolissimo = pattern classico di null-check "fault-based" della CLR
>     (es. `callvirt` su reference null, gestito nativamente dentro coreclr.dll) — quindi
>     probabilmente un vero `NullReferenceException` gestito internamente, non corruzione.
>     Diverso da SDKDLL.dll: qui non tentiamo alcun recovery (by design, il frame-unwind/
>     VirtualAlloc riguardano solo SDKDLL.dll), quindi se è davvero fatale il VEH si limita
>     a loggare e lasciar proseguire (`EXCEPTION_CONTINUE_SEARCH`).
>   - **K2.App/App.xaml.cs** (`VehCore`, branch `!inSdkDll`): aggiunto un minidump
>     rate-limited (`MAX_NON_SDK_AV_DUMPS = 3`, `TryWriteMiniDump("nonsdk_av")`) — a
>     differenza di SDKDLL.dll (DLL 3rd-party potenzialmente già corrotta, dump rischioso),
>     un AV fuori da lì è raro in condizioni normali e la CLR stessa dovrebbe essere
>     dumpabile in sicurezza. Se si ripete, il file `K2.App_YYYYMMDD_HHmmss_nonsdk_av.dmp`
>     accanto all'eseguibile conterrà lo stack managed esatto (apribile in WinDbg/Visual
>     Studio) invece di dover indovinare dal solo log.
>   - **Sospetto per la root cause reale** (da verificare col prossimo dump): race nella
>     catena di reload/upload icone di `MainWindow.DisplayPad.cs` (`DpSwitchProfile` →
>     `DpReloadAndPreloadProfile(blankFirst: true)`, upload in background per-device) sotto
>     pressioni rapide ripetute del tasto fisico cambio-profilo — non ancora confermato.
>   - **DA VERIFICARE**: se si ripresenta, allegare il nuovo file .dmp per identificare lo
>     stack esatto. Non compilato in sandbox, vedi `build-check.bat`.
> Previous: 2026-07-04 (UI — regola generale pannello azioni comuni + rimappa/reset dietro debug):
>   - **Nuova regola di interfaccia (tutti i dispositivi)**: i controlli comuni a
>     Everest/MacroPad/DisplayPad (combo+rinomina+cancella profilo, import da
>     Base Camp/XML, rinomina dispositivo) sono usciti dalle toolbar orizzontali
>     e vivono ora in un pannello verticale sulla parte destra di ogni pannello
>     device, a gruppi (Profilo / Importa / Dispositivo), stessa Width/Height
>     per tutti i bottoni+combo (nuovi stili in `MainWindow.xaml` Window.Resources:
>     `K2SideActionButton`, `K2SideActionAccentButton`, `K2SideActionCombo`,
>     `K2SideGroupHeader`).
>   - **MainWindow.xaml**: Everest — 3a `ColumnDefinition="Auto"` nel Grid 3-pane
>     (dopo sidebar sezioni + area device) col nuovo `Border` gruppo azioni.
>     MacroPad/DisplayPad — nuova `ColumnDefinition="Auto"` inserita PRIMA del
>     pannello debug esistente (`PnlMpDebugRight`/`PnlDpDebugRight`, ora spostati
>     da `Grid.Column="1"` a `Grid.Column="2"`).
>   - **Rimappa/reset tasti dietro Debug** (rule 2): `BtnEvMapKeys` (sezione Key
>     Mapping Everest), `BtnMapKeys` (toolbar MacroPad), `BtnDpMapKeys`+
>     `BtnDpResetAll` (toolbar DisplayPad) ora `Visibility="Collapsed"` di default,
>     mostrati solo con Debug ON. Toggle aggiunto in `ApplyDebugMode` (Everest,
>     `MainWindow.SectionNav.cs`), `ApplyMpDebugMode` (`MainWindow.Keys.cs`),
>     `ApplyDpDebugMode` (`MainWindow.DisplayPad.cs`).
>   - **DA VERIFICARE**: non compilato in sandbox (vedi `build-check.bat`);
>     controllare visivamente allineamento/larghezza dei 3 pannelli destri e che
>     Debug ON/OFF mostri/nasconda rimappa/reset come atteso.
> Previous: 2026-07-04 (fix — crash silenzioso residuo dopo il fix VEH VirtualAlloc):
>   - **K2.App/App.xaml.cs** (`VehCore`): il fix "definitivo" del 2026-07-01 (VirtualAlloc
>     per mappare la pagina di stack mancante nel thread timer di SDKDLL.dll) sopravviveva
>     al singolo crash ma non era limitato — ogni AV successivo mappava altre pagine RW
>     senza mai ripristinare la guard page, quindi lo stack del thread continuava a
>     crescere indefinitamente. ~3.5 min dopo un mapping riuscito il processo è sparito
>     senza alcuna riga in crash.log né `ProcessExit` (coerente con uno STATUS_STACK_OVERFLOW
>     vero, con troppo poco stack residuo perché il VEH stesso riesca a girare e loggare).
>     Aggiunto un tetto cumulativo (`SDK_STACK_GROWTH_CEILING_BYTES` = 256KB, contatore
>     statico `_sdkStackGrownBytes`): superato il tetto, il primary fix si ferma e si passa
>     al fallback esistente (skip istruzione + rate-limit + ExitThread del solo thread DLL),
>     che sacrifica il thread timer in modo pulito e loggato invece di rischiare un crash
>     muto. Aggiunto anche il fix separato del log VEH che segnalava falsamente ogni
>     eccezione .NET normale come "Fatal ... process will terminate" (0xE0434352/
>     0xE0434F4D ora esclusi davvero, il check precedente non li escludeva nonostante il
>     commento lo dicesse). **DA VERIFICARE su hardware fisico** (non compilato in sandbox,
>     vedi `build-check.bat`): il ceiling non è mai stato raggiunto/testato realmente,
>     verificare che l'ExitThread di fallback scatti come atteso quando ci arriva.
>   - **K2.App/App.xaml.cs** (watchdog diagnostico): aggiunto `StartSdkStackWatchdogIfNeeded`
>     + `SdkStackWatchdogLoop` — al primo survive (map o skip) del thread timer SDKDLL.dll,
>     parte un thread in background che ogni 20s fa `OpenThread`+`SuspendThread` brevissimo
>     + `GetThreadContext` (solo lettura ESP, CONTEXT_CONTROL) + `ResumeThread`, e logga
>     `[Watchdog] ESP=... Δultimo=...B Δbaseline=...B [cum VirtualAlloc=...B]`. Conferma o
>     smentisce l'ipotesi che lo stack del thread cresca in modo continuo nel tempo. Si
>     ferma da solo quando `OpenThread` fallisce (thread terminato). Nota: il breve
>     SuspendThread ogni 20s ha un rischio teorico di deadlock se il thread è a metà di
>     una sezione critica nel DLL — accettato come trade-off per la diagnostica.
> Previous: 2026-07-02 (feature — motore USB nativo DisplayPad, no SDK):
>   - **Fonte**: cartella `BaseCampLinux/` (app Python community, protocollo USB raw
>     reverse-engineered — vedi `devices/displaypad/panel.py`). Verificato che
>     DisplayPadSDK.dll usa solo hid.dll+setupapi (NO WinUSB) → tutto replicabile
>     via HID standard, qualsiasi bitness, senza driver aggiuntivi.
>   - **K2.App/Services/DpHidNative.cs** (new): layer HID raw. Enumerazione SetupDi+hid.dll
>     (VID 0x3282/PID 0x0009), pairing MI_01 (display, chunk = OutputReportByteLength-1)
>     + MI_03 (comandi 64B) per pad fisico via CM_Get_Parent×2 (ID stabili). Classe `Pad`:
>     INIT `11 80 00 00 01`, upload icona `21 00 00 00 [key] 3d 00 00 65 65` → READY
>     `21 00 00` → payload 306B header + 102×102×3 BGR pad a 31744B → DONE `21 00 FF`
>     (handshake confermato dal device, niente settle-delay). Key events: input report
>     `01`, byte 42 bit K1-K7 / byte 47 bit K8-K12 (offset wire; +1 su Windows per
>     report-ID). Brightness `12 03 00 00 [pct]`. Dedup upload per hash MD5 contenuto.
>   - **K2.App/Services/DisplayPadNativeClient.cs** (new): IDisplayPadClient nativo —
>     stessa superficie del satellite. keyMatrix emessi = codici SDK (0x08+9k, 0x7D) così
>     mapping/azioni esistenti funzionano invariati. Hotplug via poll 2s. Rotazione
>     counter-rotation in memoria (niente cache file → niente race). Limiti: niente
>     persistenza profili firmware (UploadImageToProfile = upload live), GetBrightness =
>     ultimo valore impostato, FirmwareVersion = "native".
>   - **K2.App/Services/IDisplayPadClient.cs** (new): interfaccia comune ai 2 backend;
>     `DisplayPadSatelliteClient` ora la implementa.
>   - **MainWindow.DisplayPad.cs**: `_dpClient` è `IDisplayPadClient`, scelto all'avvio da
>     `AppSettings.DisplayPadNativeEngine` (default OFF = satellite/SDK come prima).
>   - **K2.Core/AppSettings.cs**: nuovo flag `DisplayPadNativeEngine` (+setter, persistito).
>   - **MainWindow.xaml / MainWindow.Settings.cs**: checkbox `CkDpNativeEngine` nel tab
>     Impostazioni (effetto al riavvio). **Strings.xml/it.xml**: `settings_dp_native(_hint)`.
>   - **Riferimenti futuri da BaseCampLinux**: GIF animate/fullscreen split 12 tasti (panel.py),
>     protocollo raw Everest Max (`emax_controller.py`: RGB 0x14 2c, per-key, clock/CPU dock,
>     azioni 0x12 08) ed Everest 60 (`devices/everest60/controller.py`) → possibile bypass
>     futuro di SDKDLL.dll (elimina VEH hack).
>   - **Fix v2 (stessa data)** — K2 si bloccava all'avvio col flag attivo: l'I/O HID
>     sincrono può bloccare per sempre (endpoint NAK / collection sbagliata) e girava
>     sul thread UI dentro AutoOpenDrivers. Ora: handle aperti FILE_FLAG_OVERLAPPED +
>     helper `Transfer()` con timeout duro (write 2s, read 1s, CancelIoEx su timeout);
>     `Connect()` non bloccante (discovery+INIT su ThreadPool, i pad compaiono via
>     PlugEvent); guardia anti-rientranza sul poll 2s; log enumerazione passato al
>     client (prima mancava) ma solo al primo giro o a log Verboso.
>   - **Fix v3 (2026-07-04)** — icone corrotte persistenti col motore nativo: log utente
>     conferma enumerazione OK (3 pad, display out=1025, cmd 65/65, pairing giusto) e
>     key events OK, ma TUTTI gli upload a `ms=0` = dedup MD5 che saltava il transfer.
>     Il firmware ogni tanto ridisegna il pannello dalle icone in FLASH (mai aggiornate
>     dal motore nativo → restano quelle vecchie/corrotte dell'era SDK) e il dedup
>     impediva la riparazione. RIMOSSO il dedup (`_lastHash`): upload sempre, come BC e
>     BaseCampLinux. Aggiunto self-heal: upload fallito → re-INIT → un retry.
>   - **Fix v4 (2026-07-04)** — avvio lento + solo 2 pad su 3 (da K2.App.log): pad #2
>     init dopo 21s di retry, pad #3 mai (write timeout win32=995). Causa: sessione
>     precedente chiusa A METÀ di un trasferimento icona → il firmware resta in attesa
>     dei chunk mancanti e ignora i comandi (~20s+). Fix: (1) `FlushDisplayPipe()` —
>     se INIT muto, completa il trasferimento pendente con chunk di zeri (su pad sano
>     il primo write va in timeout e si esce subito); (2) apertura pad in PARALLELO
>     (ThreadPool, ID prenotato prima e rilasciato su fallimento → retry al poll 2s);
>     (3) `Pad.Dispose` aspetta l'upload in corso (TryEnter _ioLock 3s) per non
>     lasciare il device wedged alla chiusura; (4) rimozioni gestiscono ID prenotati
>     ma non ancora aperti. Upload reali confermati nel log (ms=15-18, ok=True).
>   - **Fix v5 (2026-07-04)** — dopo v4: avvio veloce OK, pad 1-2 subito, upload ok=True.
>     Restano: (a) pad #3 wedged anche sui WRITE comando (win32=995 sul write stesso) →
>     serve replug fisico una tantum, il flush non basta se il pipe comando è morto;
>     (b) corruzione icone CASUALE e PERSISTENTE al cambio schermata (confermato da
>     utente: non deterministico → non sono file sorgente rovinati). Mitigazioni:
>     settle 30ms dopo ogni DONE (upload back-to-back a ~1ms erano più aggressivi di
>     qualsiasi implementazione di riferimento) + check write parziali (written != len
>     ora logga e fallisce). Se la corruzione persiste → PROSSIMO PASSO: sniff USBPcap
>     (setup in `_reference/`) confrontando upload K2-nativo vs Base Camp originale
>     sulla stessa icona.
>   - **Fix v6 (2026-07-04) — ROOT CAUSE da sniff USB utente** (`_reference/captures/
>     bc_dpicon_utf8.txt`): a ogni cambio profilo BC esegue: (1) INIT `11 80`,
>     (2) **repaint FULL-PANEL** `21 00 00 01 [blocks LE16=0x0465] 00 00 00 00
>     [w-1=799] [h-1=239]` + 306 header + 800×240×3 BGR pad a 563×1024 (=SetPanelImage/
>     UploadLogo), (3) 12 icone (comando/framing IDENTICI ai nostri, 31×1024 ✓),
>     (4) brightness `12 03`. A K2 mancavano (1)-(2): icone caricate sopra un pannello
>     in transizione → corruzione casuale persistente. Implementato: `Pad.UploadPanel
>     (byte[]? bgr)` (null=nero), `Pad.Reinit()`, `StreamLocked` condiviso icona/pannello;
>     `ResetPictures` ora = Reinit + UploadPanel(black) + restore brightness (sequenza BC
>     esatta; chiamato già da DpSwitchProfile/import prima del reload icone).
>     `FlushDisplayPipe` esteso al worst-case pannello (563 chunk). Formato comando icona
>     decodificato: [5]=blocchi 512B (0x3D=61), [8..9]=w-1/h-1 (101/101).
>   - **Fix v7 (2026-07-04)** — corruzione residua su switch rapidi da tasto + lentezza:
>     `ResetPictures` era chiamato SINCRONO (thread UI, ~350ms freeze) e si interleavava
>     con gli upload ancora in coda del reload precedente → icone stantie DOPO il blank.
>     Ora `DpReloadCurrentProfile(persistent, blankFirst)`: blank+upload girano atomici
>     nello stesso segmento della catena background; nuovo reload CANCELLA gli upload
>     non ancora partiti del precedente (`_dpUploadCts`, stile BC ChangeProfileFromUI).
>     Tutti i path di switch/import usano `blankFirst:true` (niente più ResetPictures
>     sincrono, salvo BtnDpResetAll). Settle icona 30→15ms (pannello resta 30ms) via
>     param `settleMs` in `StreamLocked`.
>   - **Fix v9 (2026-07-04) — analisi pcapng COMPLETO utente (`bc_dpicon.pcapng`, IN+
>     timestamps)**: 3 scoperte decisive. (1) **NIENTE header da 306 byte**: i pixel
>     partono da offset 0 dello stream (nonzero già a offset 54 nelle icone BC) — il
>     "306" di BaseCampLinux è un artefatto; noi shiftavamo ogni icona di una riga.
>     (2) **READY = echo completo del comando** (incluso key index) su EP 0x83 IN;
>     DONE = `21 00 FF FF…`; anche INIT e brightness vengono echoed (brightness dopo
>     ~57ms!). Ora matching STRETTO sull'echo (primi 10 byte) → stale response
>     impossibili. (3) **BC pace i chunk a ~250µs** (p50=250, mean=254, n=5200);
>     noi li sparavamo a burst xHCI (31 chunk in 1-4ms) → sospetto overrun FIFO
>     firmware = corruzione casuale di singole icone. Implementato: pacing 250µs
>     busy-wait tra chunk, settle post-DONE 4ms (BC: 3.3ms), SetBrightness aspetta
>     l'echo, INIT match `11 80`. EP command IN = 0x83 (dev: 0x04 OUT/0x83 IN).
>     Tempi attesi: icona ≈12ms, pannello ≈145ms, switch completo ≈0.4s (come BC).
>   - **Fix v11 (2026-07-04) — ROOT CAUSE CONFERMATA DALL'UTENTE: MountainDisplayPadWorker.**
>     Le collection HID accettano più writer: il worker BC (autostart con Windows) reagiva
>     agli eventi tasto e scriveva sul pipe display INSIEME a K2 → stream interleaved →
>     corruzione casuale. Ucciso il worker, corruzione sparita. Implementato:
>     - **Services/BaseCampProcessGuard.cs** (new): `KillDisplayPadWorkers()` (kill tree
>       dei processi *displaypadworker*, solo il worker — la GUI BC resta); autostart via
>       `FindAutostartEntries()`/`SetAutostartEnabled()` su Run + StartupApproved (stesso
>       meccanismo di Gestione Attività, reversibile; HKLM richiede admin).
>     - **AppSettings.KillBaseCampWorker** (default ON): kill all'avvio del motore nativo
>       + guardia nel poll 2s (se BC lo rilancia viene rikillato). WarnIfBaseCampRunning
>       resta per gli altri processi BC.
>     - **UI Impostazioni**: `CkKillBcWorker` + `CkBcAutostart` (stato letto dal registro,
>       disabilitato se nessuna voce trovata) + 6 stringhe EN/IT (`settings_kill_bc_*`,
>       `settings_bc_autostart*`).
>   - **Fix v10 (2026-07-04) — repaint serializzati+coalescenti**: conferma utente che la
>     corruzione avviene scorrendo profili PRIMA che il repaint precedente finisca.
>     Nuovo `DpRequestRepaint(id)` (MainWindow.DisplayPad.cs): il cambio di stato
>     UI/store resta istantaneo per ogni pressione, ma il repaint hardware è gated da
>     `_dpRepaintBusy`; richieste durante un repaint attivo coalizzano in UN pending
>     (`_dpRepaintPending`) eseguito a fine corsa sul profilo selezionato in QUEL
>     momento → mai due sequenze blank+icone sovrapposte, nessuna pressione persa.
>     Tutti i path di switch/import ora passano da DpRequestRepaint.
>   - **Fix v8 (2026-07-04)** — la corruzione residua è in realtà ICONE SHIFTATE (icone
>     giuste su tasti sbagliati) = desync tra pipe comandi e pipe pixel: se uno stream
>     fallisce a metà o un READY stantio fa partire i pixel prima che il device abbia
>     processato lo START(key), il firmware attribuisce lo stream al tasto sbagliato e
>     da lì tutto slitta. Aggiunti: `Pad.Resync()` (FlushDisplayPipe + re-INIT) su ogni
>     fallimento di stream icona/pannello prima del retry; dump `rx <hex>` a log Verboso
>     di ogni pacchetto non-key per scoprire la struttura reale di READY/DONE (echo del
>     key index? pacchetti extra post-DONE?) e poi irrigidire il matching.
> Previous: 2026-07-02 (fix — falso allarme "crash" VEH allo shutdown):
>   - **K2.App/App.xaml.cs** (`VehCore`): la condizione di log "Fatal native exception"
>     non escludeva davvero 0xE0434352/0xE0434F4D nonostante il commento lo dicesse —
>     ogni `throw` .NET normale (gestito) genera quel codice SEH internamente, quindi
>     allo shutdown (stop bridge RPC Python + chiusura driver) il log si riempiva di
>     righe "[VEH] Fatal native exception 0xE0434352 ... — process will terminate"
>     anche con `ProcessExit exitCode=0` (uscita pulita, nessun vero crash). Aggiunta
>     l'esclusione mancante nel check `if`. **DA VERIFICARE**: ricompilare
>     (`build-check.bat`) e controllare che il log di chiusura sia pulito.
> Previous: 2026-07-02 (fix — Log level scollegato dal flag Debug):
>   - **MainWindow.xaml / MainWindow.Settings.cs**: `GbAppLogLevel` (radio
>     Off/Normale/Verboso) non è più nascosto quando Debug è OFF — ora sempre
>     visibile/attivo nel tab Impostazioni, indipendente dal flag Debug.
>     Default resta `Normal` (invariato in `AppSettings`).
> Previous: 2026-07-02 (feature — Impostazioni generali centralizzate: Debug + Log level):
>   - **K2.Core/AppSettings.cs** (new): static class app-wide, persistita in JSON
>     (`%LOCALAPPDATA%\K2\app_settings.json`, indipendente dai DB per-device).
>     `DebugMode` (bool) + `LogLevel` (enum `K2LogLevel`: Off/Normal/Verbose) +
>     evento `Changed`. Sostituisce i 3 checkbox "Debug" per-device.
>   - **K2.App/MainWindow.xaml**: nuovo tab statico `TabSettings` (Tag="settings",
>     primo tab, così resta stabile anche quando i tab `dp_N` vengono aggiunti a
>     runtime) + pannello `PnlSettings` (checkbox `CkAppDebugMode` + radio
>     `RbLogOff/Normal/Verbose` in `GbAppLogLevel`, visibile solo se Debug è ON).
>     Rimossi `CkEvDebugMode`/`CkMpDebugMode`/`CkDpDebugMode` dalle toolbar
>     Everest/MacroPad/DisplayPad (restano solo i pannelli/bottoni che quei
>     checkbox mostravano, ora pilotati centralmente).
>   - **K2.App/MainWindow.Settings.cs** (new): `InitAppSettingsPanel()` (carica
>     AppSettings all'avvio + applica a tutti i device), `CkAppDebugMode_Click`,
>     `RbLogLevel_Checked`, `ApplyDebugModeToAllDevices(bool)` — chiama
>     `ApplyDebugMode`/`ApplyMpDebugMode`/`ApplyDpDebugMode` (invariati, ora senza
>     Click handler proprio) per Everest/MacroPad/DisplayPad in un colpo solo.
>   - **MainWindow.xaml.cs**: `TcDevices_SelectionChanged` gestisce tag
>     `"settings"`; `AutoOpenDrivers` seleziona `TabEverest` esplicitamente
>     all'avvio (non più `SelectedIndex=0`, ora occupato da Settings).
>   - **Log gating**: `Log`/`LogEverest`/`DpLog` ritornano subito se
>     `LogLevel==Off` (silenzia le console eventi + il file di log per quel
>     modulo). Log per-tasto (ri-aggiunto in `MainWindow.Keys.cs`
>     `HandleKeyEvent` — era stato rimosso per rumorosità — e aggiunto in
>     `MainWindow.Everest.cs HandleEverestKey` / `MainWindow.DisplayPad.cs
>     OnDpKey`) e diagnostica `[LED-POLL]` in
>     `K2.App/Services/LedColorPoller.cs` ora condizionati a
>     `LogLevel==Verbose` (prima il led-poll logga sempre i primi 30 tick).
>   - **Strings.xml/Strings.it.xml**: nuove chiavi `tab_settings`,
>     `settings_debug_mode(_hint)`, `settings_log_level`, `log_off/normal/verbose`
>     (EN + IT).
>   - **DA VERIFICARE**: non compilato in sandbox (vedi `build-check.bat`);
>     controllare che l'app parta sul tab Everest come prima e che i pannelli
>     debug/log dei 3 device si aprano/chiudano insieme dal tab Impostazioni.
> Previous: 2026-07-01 (diagnostica — corruzione ancora presente dopo v3, sospetto sul wrapper stesso):
>   - **Scoperta chiave** (`K2/_reference/decompiled/Worker/DisplayPadWorker.Helpers/DisplayPadSDK.cs`,
>     namespace `DisplayPadWorker.Helpers`, righe ~305-430): Base Camp **non usa affatto**
>     `DisplayPadHelper.UploadImage`/`UploadImageBySetIconPic` (il wrapper NuGet che K2 usa) —
>     ha una sua classe P/Invoke privata con `[DllImport("DisplayPadSDK.dll", CallingConvention =
>     Cdecl)]` diretti su `SetIconPacket`/`SetIconPic` e costruisce i pacchetti a mano
>     (`DisplayPadOperations.UploadImage`, già visto). Possibile spiegazione della corruzione
>     residua nonostante lock/delay/coda: il wrapper convenience potrebbe gestire il trasferimento
>     in modo diverso/asincrono internamente rispetto alla chiamata nativa diretta che BC usa.
>   - **K2.DisplayPad.Satellite/SdkHandler.cs**: aggiunta diagnostica — `Stopwatch` attorno alla
>     chiamata nativa (`UploadImage`/`UploadImageBySetIconPic`) con log
>     `nativeCallMs`/`ok`/`path` (file: `%LOCALAPPDATA%\K2.DisplayPad\satellite.log`). Settle
>     delay dopo ogni icona alzato 100→400ms (`IconSettleDelayMs`) come test diagnostico: se a
>     400ms la corruzione sparisce → era questione di timing (si può ritarare più basso); se
>     persiste anche a 400ms → il sospetto si sposta sul wrapper stesso, prossimo passo
>     reimplementare l'upload icona con `SetIconPacket` nativo via reflection, replicando
>     `DisplayPadOperations.UploadImage` di BC (resize 102×102, maschera angoli arrotondati,
>     chunking a pacchetti da 1024B) invece di passare dal wrapper.
>   - **DA VERIFICARE su hardware fisico**: risultato del test a 400ms + eventuale log condiviso
>     dall'utente per capire i tempi reali di trasferimento nativo.
> Previous: 2026-07-01 (bugfix candidate v3 — corruzione icone residua dopo il fix perf):
>   - **K2.DisplayPad.Satellite/SdkHandler.cs**: `ResolveForUpload` (rotazione + scrittura file
>     cache) girava PRIMA di entrare in `lock(_sdkLock)` in `CmdUploadImage`/
>     `CmdUploadImageToProfile` — spostata dentro il lock. Root cause residua: due upload
>     concorrenti della STESSA immagine sorgente (es. due DisplayPad che ricaricano insieme, o un
>     reload di profilo sovrapposto a un salvataggio di singolo tasto) potevano entrambi mancare
>     la cache, ruotare lo stesso file ed entrambi scrivere sullo stesso path cache
>     contemporaneamente → PNG "torn"/parziale su disco, caricato sul device già corrotto.
>   - **K2.App/MainWindow.DisplayPad.cs**: `DpReloadCurrentProfile` — il `Task.Run` fire-and-forget
>     introdotto nel fix perf v2 ora è **incatenato per device** (`_dpUploadChain`,
>     `Dictionary<int, Task>`): un nuovo reload per lo stesso device aspetta che il precedente
>     abbia finito di caricare le icone invece di girare in parallelo con lui. Evita che due
>     passate di reload sovrapposte scrivano sugli stessi indici bottone / file cache in ordine
>     imprevedibile.
>   - **DA VERIFICARE su hardware fisico**: dovrebbe eliminare la corruzione residua "in alcuni
>     casi" segnalata dall'utente (probabile race sul file cache di rotazione, non più sul
>     trasferimento USB in sé che era già serializzato dal fix precedente).
> Previous: 2026-07-01 (perf fix v2 — UI di K2 bloccata durante il reload DisplayPad, non solo lento):
>   - **K2.App/MainWindow.DisplayPad.cs**: `DpReloadCurrentProfile` — separato l'aggiornamento
>     modello/griglia (istantaneo, resta sul thread UI: assegna `key.ImagePath/ActionType/...`,
>     la griglia K2 si aggiorna subito) dall'upload hardware (lento perché serializzato +
>     settle-delay per icona, vedi entry successiva su corruzione icone): quest'ultimo ora gira in
>     `Task.Run` fire-and-forget invece che inline sul thread UI. Prima ogni singola
>     `_dpClient.UploadImage` bloccava il dispatcher WPF → tutta l'app (non solo il tab
>     DisplayPad) sembrava congelata per la durata dell'intero reload (fino a 12 icone × ~1s).
>     Ora l'interfaccia K2 mostra le icone giuste all'istante mentre il device fisico si aggiorna
>     un momento dopo in background.
>   - **Stesso reload**: anche rimosso il preload eager di tutte le sotto-pagine cartella ad ogni
>     switch/rotazione (restano lazy alla navigazione) e il doppio upload persistente+live per
>     bottone (ora solo live, `persistent: false` — l'immagine è già persistita al momento della
>     configurazione). Rimossi anche 2 residui di `_dpClient.SwitchProfile` negli import XML/BC:
>     BC non chiama mai lo SwitchProfile nativo per il DisplayPad (vedi memoria
>     project_displaypad_profile_corruption).
>   - **DA VERIFICARE su hardware fisico**: se l'utente cambia profilo più volte molto rapido,
>     più `Task.Run` in coda potrebbero sovrapporsi lato satellite — il lock in `SdkHandler`
>     serializza comunque le scritture USB vere, quindi nel caso peggiore arriva un'icona vecchia
>     in ritardo che viene sovrascritta dal reload successivo (nessun crash atteso).
> Previous: 2026-07-01 (RISOLTO — SDKDLL.dll crash timer thread via VEH VirtualAlloc page-mapping):
>   - **App.xaml.cs** (VehCore): quando WRITE AV con `badAddr ∈ [ESP, ESP+0x40)`, mappa la
>     pagina mancante con `VirtualAlloc(badPage, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)`
>     e ritorna EXCEPTION_CONTINUE_EXECUTION allo stesso EIP. L'istruzione ri-esegue, il thread
>     timer DLL sopravvive, timer reschedula a ~40s normali. LED preview e DisplayPad continuano
>     senza interruzione. Fallback: skip a +0x5148 + rate-limit + ExitThread sicuro (no MiniDump).
>     Il DLL timer thread è critico per TUTTI i device USB: se muore, LED e DisplayPad degradano.
> Previous: 2026-07-01 (perf fix — reload profilo lentissimo dopo il fix corruzione):
>   - **K2.App/MainWindow.DisplayPad.cs**: `DpReloadAndPreloadProfile` — rimosso il preload
>     "eager" di tutte le sotto-pagine cartella (ogni switch/rotazione/riconnessione ricaricava
>     TUTTI i bottoni di TUTTE le cartelle, anche mai aperte). Le sotto-pagine restano caricate
>     lazy alla navigazione (`DpNavigateToPage`, invariato). `DpReloadCurrentProfile` ora chiamato
>     sempre con `persistent: false` (era `true`): l'immagine è già persistita sullo slot firmware
>     al momento della configurazione (`DpUploadAndPersist`) o durante l'import — ripersistere ad
>     ogni reload raddoppiava inutilmente le transfer USB per bottone. `DpNavigateBack` idem
>     (era `persistent: _currentDpPageId == 0`, ora sempre `false`).
>   - **BtnDpImportXml_Click / BtnDpImportBc_Click**: rimossa anche qui la chiamata
>     `_dpClient.SwitchProfile(...)` residua (stesso motivo del fix principale — mai usata da BC
>     per DisplayPad), aggiunta `_dpClient.ResetPictures(id)` prima del reload per coerenza.
>   - **DA VERIFICARE su hardware fisico**: dovrebbe circa dimezzare il tempo per reload
>     (1 upload/bottone invece di 2) ed eliminare il tempo speso su cartelle mai aperte. Se resta
>     lento, il prossimo tuning è abbassare/rimuovere il `Thread.Sleep(100)` in
>     `SdkHandler.CmdUploadImage`/`CmdUploadImageToProfile` (vedi entry precedente).
> Previous: 2026-07-01 (bugfix candidate v2 — icone corrotte al cambio profilo, root cause da confronto BC):
>   - **Confronto con BC decompilato** (`K2/_reference/decompiled/Worker/DisplayPadWorker.Helpers/DisplayPadOperations.cs`):
>     `DisplayPadOperations.cs` **non chiama mai** `DisplayPadSDK.SwitchProfile` per il DisplayPad — per
>     questo device il "profilo" è un concetto puramente host/DB (`ChangeProfileFromUI`, riga ~6765):
>     annulla/attende i task di upload immagine pendenti (`Task.WaitAll`), **blanka l'intero pannello**
>     (`UploadLogo`→`SetPanelImage`), poi ricarica le icone del nuovo profilo una per una. Il metodo
>     `UploadImage` di BC (riga ~3042) costruisce i pacchetti icona a mano e chiama `SetIconPacket`
>     **dentro un lock** (`_objlockTask`) — mai chiamate concorrenti/non serializzate.
>   - **K2.App/MainWindow.DisplayPad.cs**: `DpSwitchProfile` e `CbDpProfile_SelectionChanged` — **rimossa**
>     la chiamata `_dpClient.SwitchProfile(...)` (introduceva uno stato firmware mai esercitato da BC per
>     il DisplayPad). Aggiunta `_dpClient.ResetPictures(id)` prima del reload icone (equivalente a
>     `UploadLogo` di BC: blanka il pannello prima di ricaricare).
>   - **K2.DisplayPad.Satellite/SdkHandler.cs**: `CmdUploadImage`/`CmdUploadImageToProfile`/
>     `CmdResetPictures` ora girano dentro `lock(_sdkLock)` (nuovo static field) + `Thread.Sleep(100)`
>     dopo ogni singolo upload icona, per serializzare i trasferimenti come fa BC ed evitare che due
>     upload back-to-back si sovrappongano sul wire (causa più probabile della corruzione: foto utente
>     mostra icone "torn"/rumore a bande, corrette solo per le ultime 3 caricate nel loop).
>     `switchProfile` (comando IPC) lasciato funzionante ma **non più chiamato da K2.App** per il DisplayPad.
>   - **DA VERIFICARE su hardware fisico**: non testato/compilato in sandbox, vedi `build-check.bat`.
>     Se la corruzione persiste, il prossimo passo è uno sniff USB (setup in `_reference/`) per
>     confrontare il traffico coi timing reali di BC durante `ChangeProfileFromUI`.
> Previous: 2026-07-01 (bugfix candidate — icone corrotte al cambio profilo da tasto fisico):
>   - **K2.DisplayPad.Satellite/SdkHandler.cs**: `switchProfile` ora passa da `CmdSwitchProfile`
>     invece di chiamare `DisplayPadSwitchProfile` inline. Dopo lo switch riuscito, `Thread.Sleep(300)`
>     prima di rispondere al client. Ipotesi root cause: `DpSwitchProfile`/`DpReloadAndPreloadProfile`
>     (K2.App/MainWindow.DisplayPad.cs) inviano subito dopo lo switch una raffica di
>     `uploadImage`/`uploadImageToProfile` (pagina root + tutte le sub-pagine cartelle); il FW è
>     ancora a metà della propria transizione interna (lettura flash + swap buffer icone) e la
>     scrittura concorrente corrompe le icone visualizzate. Stesso pattern già visto per
>     SDKDLL Everest (SaveFlash debounce, APEnable retry/backoff già presente in `CmdApEnable`).
>     **DA VERIFICARE su hardware fisico** (cambio profilo via tasto fisico sul DisplayPad):
>     non testato/compilato in sandbox, vedi `build-check.bat`.
> Previous: 2026-06-30 (feature — rotazione batch icone DisplayPad):
>   - **MainWindow.xaml**: aggiunto `BtnDpRotateCcw` (↺) e `BtnDpRotateCw` (↻) nella toolbar
>     DisplayPad, dopo il combo Rotation.
>   - **MainWindow.DisplayPad.cs**: `DpRotateAllIcons(int degrees)` — per ogni tasto del profilo
>     corrente con immagine: ruota la PNG via `System.Drawing.Bitmap.RotateFlip`, salva nella
>     stessa cache content-hash di `DpKeyConfigDialog` (`K2.DisplayPad\user_rotated\`), aggiorna
>     `_dpKeys[i].ImagePath`, `_dpStore.SaveButton`, `_dpClient.UploadImageToProfile`.
>     90° = CW, 270° = CCW. Log finale con count icone ruotate / fallite.
>   - **Strings.xml + Strings.it.xml**: `dp_rotate_icons_cw`, `dp_rotate_icons_ccw`.
> Previous: 2026-06-30 (fix — DpKeyButtonStyle click + CellConfigDialog standalone):
>   - **MainWindow.xaml** (K2.App): aggiunto `Background="Transparent"` al Grid radice di
>     `DpKeyButtonStyle`. Senza sfondo (null), il Grid non era hit-testable → click ignorati.
>   - **DpKeyConfigDialog.xaml.cs**: `Loc.Get("action_none")` → `Loc.Get("act_none")`
>     (chiave corretta in Strings.xml).
>   - **K2.DisplayPad/Dialogs/CellConfigDialog.xaml(.cs)** (new): dialog unificato per
>     K2.DisplayPad standalone. Identico a DpKeyConfigDialog ma usa `ButtonCell`.
>     Preview 160×160 con RotateTransform, Load/Remove image, radio 0°/90°/180°/270°,
>     sezione azione con summary + "Configure action…" (ButtonActionDialog) + "Remove action".
>     Rotazione utente via `System.Drawing` + cache `%LOCALAPPDATA%\K2.DisplayPad\user_rotated\`.
>   - **K2.DisplayPad/MainWindow.xaml.cs**: `BtnCell_Click` usa `CellConfigDialog` invece di
>     `OpenFileDialog`. Gestisce `ImageChanged` (upload+persist o rimozione) + azione. Rimosso
>     `using Microsoft.Win32` non più necessario.
> Previous: 2026-06-30 (feature — delete profile + rename device label + finestra +15%):
>   - **MainWindow.xaml**: aggiunto `BtnEvDeleteProfile`, `BtnMpDeleteProfile`, `BtnDpDeleteProfile`
>     (icona cestino &#xE74D;) dopo i rispettivi bottoni rinomina profilo. Width 1240→1426,
>     Height 780→897, MinWidth 1040→1196, MinHeight 620→713 (+15%).
>   - **MainWindow.Everest.cs**: `BtnEvDeleteProfile_Click` — conferma → `_evStore.ClearProfile(slot)`
>     (profili fissi 1..5: svuota tasti + cancella nome, lo slot rimane). Poi `EvRefreshProfiles` +
>     `ReloadEverestProfile`.
>   - **MainWindow.Keys.cs**: `BtnMpDeleteProfile_Click` — blocca se ultimo profilo; conferma →
>     `_store.ClearProfile` + clear nome setting. Poi `MpRefreshProfiles`.
>   - **MainWindow.DisplayPad.cs**: `BtnDpDeleteProfile_Click` — blocca se ultimo profilo; conferma →
>     `_dpStore.ClearProfile` + clear nome setting. Poi `DpRefreshProfiles`.
>   - **EverestStore.cs**: aggiunto `ClearProfile(int slot)` — DELETE Keys WHERE Profile + clear nome.
>   - **Strings.xml + Strings.it.xml**: 3 nuove chiavi `delete_profile`, `delete_profile_confirm`,
>     `delete_profile_last`. `rename_device` aggiornato a "Rename device" / "Rinomina dispositivo".
> Previous: 2026-06-30 (feature — DpKeyConfigDialog + counter-rotation conferma):
>   - **DpKeyConfigDialog.xaml(.cs)** (new in K2.App): dialog unificato click-su-tasto DisplayPad.
>     Left panel: preview 160×160, "Load image…", "Remove image", radio 0°/90°/180°/270° con
>     `LayoutTransform` live. Right panel: summary azione testuale + "Configure action…"
>     (apre ButtonActionDialog) + "Remove action". On OK: applica rotazione utente con
>     GDI+ (System.Drawing) e salva in cache `%LOCALAPPDATA%\K2.DisplayPad\user_rotated\`.
>     La counter-rotation per il device è già gestita nel satellite (SdkHandler.ResolveForUpload).
>   - **MainWindow.DisplayPad.cs**: `DpKeyButton_Click` usa `DpKeyConfigDialog` invece di
>     `ButtonActionDialog`. Gestisce separatamente: ImageChanged (upload+persist), solo azione.
>   - **K2.Core/Strings.xml + Strings.it.xml**: 3 nuove chiavi `dp_load_image`,
>     `dp_rotate_image`, `dp_action_section` (EN + IT).
>   - **Counter-rotation device**: già implementata nel satellite da sessioni precedenti
>     (`SdkHandler.ResolveForUpload`: device 90° → img 270°, device 270° → img 90°).
> Previous: 2026-06-30 (feature — click sul tasto per configurare azione):
>   - **MainWindow.Everest.cs**: `EvKeyboardButton_Click` — click su un tasto (fuori da
>     capture/paint mode) aggiunge il tasto alla lista se assente, poi apre direttamente
>     `ButtonActionDialog`. Sostituisce il vecchio comportamento "seleziona nella lista".
>   - **MainWindow.DisplayPad.cs**: `DpKeyButton_Click` — click su un tasto non-folder/back
>     apre `ButtonActionDialog` (identico a context menu "Configure action"). File picker
>     spostato nel context menu come nuovo handler `DpMnuChangeImage_Click` ("Change image…").
>     `BuildDpKeyContextMenu` aggiornato con la voce "Change image…".
>   - **K2.Core/Strings.xml + Strings.it.xml**: nuova chiave `dp_change_image`
>     (EN: "Change image…", IT: "Cambia immagine…").
> Previous: 2026-06-29 (feature — DisplayPad folder/sub-page navigation):
>   - **DisplayPadStore.cs**: migrazione schema DB — aggiunta colonna `PageId INTEGER DEFAULT 0`
>     con nuova PK `(DeviceId, Profile, PageId, ButtonIndex)`. Nuovi metodi: `SaveButton(…, pageId, …)`,
>     `LoadPage(deviceId, profile, pageId)`, `LoadAllButtons(deviceId, profile)`.
>     `GetFolderName(pageId)` / `SetFolderName(pageId, name)` via Settings.
>   - **BaseCampDbImporter.cs**: `BcButton` ora ha `ParentId` e `OptionalText`. `ReadButtons` legge
>     entrambi i campi (con fallback se colonne assenti nel DB). `ImportProfile` gestisce
>     `"Create Folder"` (→ `"dp_folder"` + pageId da OptionalText) e `"Back"` (→ `"dp_back"`),
>     salva ogni tasto con il suo `pageId`. `TranslateAction` aggiunto `"Create Folder"`, `"Back"`,
>     `"Keyboard Shortcuts"`. `ParseFolderPageId(optionalText)` helper pubblico.
>   - **DisplayPadKey.cs**: `Display` aggiornato per `"dp_folder"` (▸) e `"dp_back"` (◂).
>   - **MainWindow.DisplayPad.cs**: navigazione cartelle — `_currentDpPageId`, `_dpPageHistory`.
>     `DpNavigateToPage(pageId, name)` / `DpNavigateBack()` / `ResetDpNavigation()` /
>     `UpdateDpBreadcrumb()`. `DpReloadCurrentProfile(persistent)` filtra per pagina corrente.
>     `DpKeyButton_Click` e `OnDpKey` intercettano `dp_folder`/`dp_back` prima del motore azioni.
>     `DpUploadAndPersist` e tutti i context-menu usano `_currentDpPageId`.
>     Import XML: legge `ParentId`, gestisce `Create Folder` con `OptionalText`.
>     Import BC: upload solo root page (pageId=0) a importazione.
>   - **MainWindow.xaml**: `BtnDpBack` (Visibility=Collapsed di default) + `LblDpBreadcrumb`
>     nella toolbar DisplayPad.
>   - **K2.Core/Strings.xml + Strings.it.xml**: nuova chiave `dp_back` (EN: "Back", IT: "Indietro").
> Previous: 2026-06-29 (feature — profile switch su pressione tasto + rinomina profili):
>   - **MacroPadSdkNative.cs**: aggiunto `SwitchProfile(int profile, int reserved, uint ID)`.
>   - **MacroPadService.cs**: aggiunto `SwitchProfile(uint deviceId, int profile)` facade.
>   - **MainWindow.ActionHost.cs**: `IActionHost.SwitchProfile` ora chiama `MpSwitchProfile`.
>   - **MainWindow.Keys.cs**: `MpSwitchProfile(string target)` — cicla i profili esistenti, chiama
>     native SDK + aggiorna combo + ricarica. `BtnMpRenameProfile_Click` — rinomina slot corrente.
>     `MpRefreshProfiles`: usa `GetProfileName` per label personalizzate. Combo allargata a 120px.
>   - **DisplayPadActionHost.cs**: `SwitchProfile` delega a `DpSwitchProfile` (su Dispatcher).
>   - **MainWindow.DisplayPad.cs**: `DpSwitchProfile(string target)` — cicla profili esistenti,
>     chiama `_dpClient.SwitchProfile`, aggiorna store/combo/griglia. `BtnDpRenameProfile_Click`.
>     `DpRefreshProfiles`: usa `GetProfileName`. Combo allargata a 120px.
>   - **MainWindow.Everest.cs**: `CbEvProfile` migrato da `int[]` a `List<EvProfileItem>`.
>     Aggiunti `EvRefreshProfiles()`, `EvSelectProfileSlot(slot)`, `BtnEvRenameProfile_Click`.
>     `EvSwitchProfile` ora chiama `_evStore.SetCurrentProfile` + `EvSelectProfileSlot`.
>   - **MainWindow.xaml.cs**: `EvProfileItem(slot, label)`. `ShowRenameDialog` esteso con
>     parametri opzionali `title` e `promptText`.
>   - **MainWindow.xaml**: bottoni `BtnEvRenameProfile`, `BtnMpRenameProfile`, `BtnDpRenameProfile`
>     in toolbar Everest/MacroPad/DisplayPad. Icona &#xE8D3;.
>   - **MacroPadStore, DisplayPadStore, EverestStore**: aggiunti `GetProfileName`/`SetProfileName`
>     (Settings `profile.{slot}.name` / `profile.{deviceId}.{slot}.name`).
>   - **K2.Core/Strings.xml + Strings.it.xml**: 3 nuove chiavi `rename_profile`,
>     `rename_profile_title`, `rename_profile_prompt` (EN + IT).
>   - **DisplayPadKey.cs**: (sessione precedente) static `DebugMode`, `HasImageNoAction`,
>     `NotifyDebugModeChanged`. Triangolo rosso in DpKeyButtonStyle per tasti con icona ma senza azione.
>     Import aggiornato: salta solo se né azione né immagine. `MacroPadKey.Display`: mostra M1–M12.
> Previous: 2026-06-29 (feature — rinomina dispositivi per tutti i device):
>   - **MainWindow.xaml**: aggiunto `BtnEvRename`, `BtnMpRename`, `BtnDpRename` nelle toolbar
>     Everest, MacroPad, DisplayPad. Icona &#xE8D3; (Edit), stile K2IconButton.
>   - **MainWindow.xaml.cs**: `ShowRenameDialog(current)` — finestra modale minimale
>     (WindowStyle.ToolWindow) con TextBox pre-selezionato. Restituisce stringa trimmed o null.
>   - **MainWindow.Everest.cs**: `BtnEvRename_Click` → rinomina `TabEverest.Header`, persiste in
>     `_evStore.SetSetting("device.name")`. `EvAutoOpen` ripristina al boot.
>   - **MainWindow.Keys.cs**: `BtnMpRename_Click` → rinomina `TabMacroPad.Header`, persiste in
>     `_store.SetSetting("device.name")`. `InitKeysModule` ripristina al boot.
>   - **MainWindow.DisplayPad.cs**: `BtnDpRename_Click` → rinomina il tab `dp_{id}` e aggiorna
>     `_dpDeviceLabels`. `DpRefreshDevices` carica `GetSetting("device.{id}.name")` per-device.
>   - **K2.Core/Strings.xml + Strings.it.xml**: 3 nuove chiavi `rename_device`,
>     `rename_device_title`, `rename_device_prompt` (EN + IT).
> Previous: 2026-06-29 (bugfix — upload immagini al DisplayPad hardware):
>   - **MainWindow.DisplayPad.cs**: `DpReloadCurrentProfile` — aggiunto fallback
>     `UploadImageToProfile` → `UploadImage` (live) se persistent upload fallisce.
>     `BtnDpImportBc_Click` — aggiunto `APEnable(false)` prima del loop upload +
>     stesso fallback per ogni button.
>     `BtnDpImportXml_Click` — stesso fallback nel loop upload immagini.
> Previous: 2026-06-29 (bugfix — import DB e XML DisplayPad):
>   - **BaseCampDbImporter.cs**: `DecodeBase64Image` (now `internal`) strips `data:image/...;base64,`
>     prefix prima di `Convert.FromBase64String` (BC salva sempre con prefisso data-URI).
>     `KeyIdToIndex` reso `internal` per uso dall'XML importer.
>     `TranslateAction` corretta: `"keys"` (non `"keystroke"`), `"exec"` (non `"oscmd"`) per
>     Run Program, `"folder"` per Open Folder, `"url"`/`"browser"` per URL/Run browser;
>     profile sub-cases ora mappano a `("profile", "next"/"prev"/N)`.
>   - **MainWindow.DisplayPad.cs**: `BtnDpImportXml_Click` completamente riscritto per la
>     struttura XML reale di BC: itera `<DisplayPadLayerBidings>`, legge `<KeyId>` + `<base64Image>`
>     + `<FunctionType>/<SubFunctionType>/<FunctionValue>`, usa `DecodeBase64Image` e
>     `TranslateAction` da `BaseCampDbImporter`, salva store + upload satellite.
> Previous: 2026-06-29 (feature — import profili Base Camp completo per tutti i dispositivi):
>   - **BaseCampDbImporter.cs**: esteso con `ReadEverestProfiles`/`ReadMacroPadProfiles`
>     (DeviceType="Everest"/"MacroPad", entrambi leggono `EverestKeyBidings`).
>     `ReadKeyBindings(profileId)` legge DLLMatrixIndex/FunctionType/base64Image/IsTouchKey.
>     `ImportEverestProfile`: chiavi regolari → EverestStore.SaveKey (DLLMatrixIndex=KeyMatrix);
>     touch key (IsTouchKey=true, LCD numpad) → immagini su disco + settings ndk.{i}.*.
>     `ImportMacroPadProfile`: DLLMatrixIndex 170-179/220-221 → indice 0-11, salva in MacroPadStore.
>   - **MainWindow.Everest.cs**: `BtnEvImportBc_Click` — legge DB, mostra riepilogo, importa
>     tutti i profili Everest, carica immagini NDK su hardware se connesso, ricarica UI.
>     `EvUploadNdkImages` — upload hardware per slot corrente.
>     Aggiunto `using System.Linq`.
>   - **MainWindow.Keys.cs**: `BtnMpImportBc_Click` — come DP ma per MacroPad, auto-mapping
>     per DeviceId, ricarica profilo attivo BC.
>   - **MainWindow.xaml**: bottone "Import BC" (BtnEvImportBc) nella toolbar Everest;
>     bottone "Import BC" (BtnMpImportBc) nella toolbar MacroPad.
>   - **Strings.xml/Strings.it.xml**: 6 nuove chiavi: `import_bc`, `ev_no_profiles_in_bc`,
>     `ev_imported_bc`, `mp_no_profiles_in_bc`, `mp_imported_bc`, `select_device_first`.
> Previous: 2026-06-29 (RISOLTO — SDKDLL.dll crash permanente via VEH instruction skip):
>   - **App.xaml.cs**: VEH skip mirato a SDKDLL.dll+0x5133 (`MOV [ESP+0x14], EDX` — WRITE fault,
>     thread stack top). Il VEH decodifica la lunghezza con `X86InstrLen()` (mini decoder x86 32-bit:
>     ModRM+SIB), avanza EIP di 4B **senza toccare nessun registro** (EAX deve restare valido per
>     l'istruzione successiva a +0x5148 `MOV EDX,[EAX+4]`), ritorna EXCEPTION_CONTINUE_EXECUTION.
>     Gestione fallback aggiunta anche per +0x5148 (azzerando EDX=0 via CTX_EDX=0xA8).
>     Il crash era indipendente da GetColorData/SetSyncEffect (avveniva anche con EverestEnabled=false).
>   - **MainWindow.LedPreview.cs**: Everest LED polling ri-abilitato (SetSyncEffect+EnableColorStream+
>     EverestEnabled=true). Il commento "DISABLED" aggiornato con motivazione del fix.
> Previous: 2026-06-29 (3 fix/feature: Enter key map, SDKDLL crash recovery, loading overlay):
>   - **MainWindow.Everest.cs**: `s_defaultWMatrixMap` (100+ entries DLLMatrixIndex→VK from BaseCamp.db).
>     `LoadEverestKeyMap()` seeds from default then applies user overrides. `EvTranslateMatrix()` double fallback.
>   - **MainWindow.LedPreview.cs**: crash auto-recovery (3s DispatcherTimer → Close+reset+Open+StartLedPreview).
>     No more blocking MessageBox. `using K2.Core` aggiunto.
>   - **MainWindow.xaml**: `PnlLoading` Grid (RowSpan=2, ZIndex=10) — overlay di caricamento che copre
>     tab strip + content finché `AutoOpenDrivers()` non completa.
>   - **MainWindow.xaml.cs**: `AutoOpenDrivers()` ora collassa PnlLoading e seleziona SelectedIndex=0 al termine.
>   - **Strings.xml / Strings.it.xml**: 4 nuove chiavi: `loading_drivers`, `ev_crash_recovering`,
>     `ev_crash_recovered`, `ev_crash_recovery_failed`.
> Last updated prev: 2026-06-28 (feature — 6 nuovi layout tastiera Everest, simboli per-tasto):
>   - **KeyboardLayout.cs**: enum `KeyboardLayoutType` esteso (IsoUk/IsoDe/IsoFr/
>     IsoEs/IsoNordic/IsoPt). Estratto builder generico `BuildBoardLeft_Iso(over)`
>     dalla geometria ISO (Enter a L, tasto <>, LShift corto): la geometria è
>     identica per tutti i locale ISO, cambiano solo le legende `data-key`. Il
>     **MatrixId (VK) resta legato alla posizione FISICA** (così l'highlight SDK
>     funziona anche per QWERTZ/AZERTY dove le lettere si spostano). Legende base
>     per locale in nested `IsoLegends` (It/Uk/De/Fr/Es/Nordic/Pt). `GetBoardLeft`
>     ora usa cache `Dictionary` lazy. `DetectLayout` mappa LANGID→layout
>     (DE 0x07, FR 0x0C, ES 0x0A, NO 0x14, PT 0x16, EN-UK 0x09 sub 0x02).
>   - **KeyLabelMap.cs**: aggiunte 6 mappe alt (shift) `_isoUk/_isoDe/_isoFr/
>     _isoEs/_isoNordic/_isoPt`, registrate in `_map`. (FR: digit sono sull'alt,
>     base = accenti.) **+ livelli AltGr (3) e Shift+AltGr (4)**: dizionari
>     `_altGr*` + `_shiftAltGr*` per locale, metodi `AltGrLabel`/`ShiftAltGrLabel`.
>     Es. IT: è→[ /{ , +→] /} , ò→@ , à→# , e→€.
>   - **MainWindow.Everest.cs**: `InitKeyboardLayoutSelector` popola il ComboBox
>     con tutti gli 8 layout (US ANSI, UK, IT, DE, FR, ES, Nordic, PT). Nuovo
>     `BuildCornerLegend()`: keycap a 2×2 (base bianco basso-sx, shift grigio
>     alto-sx, AltGr teal basso-dx, Shift+AltGr teal alto-dx) quando il tasto ha
>     un livello AltGr; altrimenti resta il rendering a 1-2 righe. Tooltip esteso
>     con tutti i livelli. **Legende uniformate allo stile Base Camp** (da
>     `wwwroot/css/keyboard.css`): testo tutto BIANCO (no grigio/teal), font
>     system-ui (=Segoe UI), singola label 8px (0.5rem), multi-legenda 7px.
>     Lo stile del tasto (sfondo #404040 / bordo #1d1d1d) era già replicato in
>     `EverestKeyStyle`.
>   - Base Camp espone 10 layout (anche Hebrew/Korean a doppio alfabeto): rinviati.
>   - **DA VERIFICARE**: `build-check.bat` (compilo non possibile nel sandbox);
>     alcuni simboli secondari rari (es. PT/ES shift-3) seguono i layout fisici
>     standard, ritoccabili se l'utente nota differenze con la sua tastiera.
> Previous: 2026-06-28 (bugfix — SDKDLL crash root cause fixed):
>   - **MainWindow.LedPreview.cs**: Disabled Everest color streaming and GetColorData
>     polling to fix SDKDLL.dll+0x5133 crash. Root cause: SetSyncEffect + EnableColorStream
>     put the firmware into continuous-report mode; our GetColorData calls (120ms DispatcherTimer
>     on UI thread) access the DLL's internal color buffer concurrently with the DLL's own
>     internal polling thread → race condition → ACCESS VIOLATION at +0x5133 after ~300-450
>     ticks (37-52s). Fix: do not call SetSyncEffect/EnableColorStream/GetColorData for Everest.
>     Everest LED key overlay is disabled; MacroPad LED preview (different DLL) unaffected.
>     Also added `_everestCrashCount` field: `TryEverestCrashRecovery` limits LED preview
>     restart to ≤2 attempts per session (defense-in-depth for future re-enablement).
>     Previous incorrect fix (APEnable removal) was also kept as a secondary precaution.
>   - **App.xaml.cs** (2nd fix — same session): VEH aggiornato con instruction-skip mirato
>     a SDKDLL.dll+0x5133. Il crash persiste anche senza GetColorData → bug nel thread interno
>     della DLL stesso (non correlato alle nostre chiamate). Strategia: leggere i byte
>     dell'istruzione a +0x5133, decodificarne la lunghezza con `X86InstrLen()` (mini-decoder
>     x86 a 32-bit: ModRM+SIB+displacement), avanzare EIP oltre l'istruzione (EAX=0 come
>     "risultato nullo"), e ritornare EXCEPTION_CONTINUE_EXECUTION. Il thread DLL continua
>     anziché essere ucciso → nessun recovery → nessuna interruzione. Se il decoder non
>     riconosce l'opcode, fallback a ExitThread (comportamento precedente). Log aggiunto:
>     16 byte a crash site + tipo AV (READ/WRITE) + indirizzo invalido.
> Previous: 2026-06-28 (bugfix — Enter key mapping + SDKDLL crash auto-recovery):
>   - **MainWindow.Everest.cs**: Added `s_defaultWMatrixMap` (static dict, 100+ entries) built
>     from BaseCamp.db EverestKeyBidings.DLLMatrixIndex→VK. Root cause: SDK KEY_CALLBACK reports
>     DLLMatrixIndex as wMatrix (NOT VK codes), so without the map Enter (DLLMatrixIndex=120)
>     was mistaken for F9 (VK_F9=120). `LoadEverestKeyMap` now seeds from default first, then
>     applies user overrides. `EvTranslateMatrix` falls back to `s_defaultWMatrixMap` if entry
>     missing from user map. "Mappa tasti" procedure no longer required on first run.
>   - **MainWindow.LedPreview.cs**: `OnSdkCrashDetected` no longer shows a blocking MessageBox.
>     Instead schedules a 3s DispatcherTimer → `TryEverestCrashRecovery`: calls Close()+Open()+
>     resets `App.SdkCrashRecoveryNeeded`+restarts LED preview. Falls back to error status if
>     re-open fails. Added `using K2.Core` for `Loc`.
>   - **Strings.xml / Strings.it.xml**: added `ev_crash_recovering`, `ev_crash_recovered`,
>     `ev_crash_recovery_failed`.
> Previous: 2026-06-28 (architecture — top-level device tabs, fixed image size, DP detection fix):
>   - **MainWindow.xaml**: Removed outer TabControl. Now `Grid` (rows: Auto strip + * content).
>     Top strip = header-only `TcDevices` (TabControl with custom template = TabPanel only).
>     Static tab: `TabEverest` (Tag="everest"). Dynamic tabs added by code (Tag="mp_N", "dp_N").
>     Content area: overlapping `PnlEverest`, `PnlMacroPad` (Visibility=Collapsed),
>     `PnlDisplayPad` (Visibility=Collapsed) — only one visible at a time.
>     Sub-TabControls `TcMpDevices`/`TcDpDevices` REMOVED. Viewboxes REMOVED (all Canvases
>     at fixed native size: MacroPad/DP 510×370, Everest keyboard 642×260 + numpad 166×260).
>     MacroPad rows now: 0=toolbar, 1=LED Expander, 2=main area.
>     DisplayPad rows now: 0=toolbar, 1=main area.
>   - **MainWindow.Keys.cs**: `_activeMpDeviceId (internal int?)` replaces TcMpDevices lookup.
>     `CurrentDeviceId()` → `_activeMpDeviceId`. `TcMpDevices_SelectionChanged` removed.
>     `ApplyMpDebugMode()` still toggles AP buttons + `PnlMpDebugRight`.
>   - **MainWindow.xaml.cs**: `TcDevices_SelectionChanged` shows/hides PnlEverest/MacroPad/DP
>     and sets `_activeMpDeviceId`/`_activeDpDeviceId` + triggers device-changed logic.
>     `RefreshDevices()` calls `RemoveDeviceTabs("mp_")` then adds tabs to `TcDevices`.
>     `BtnClose_Click` calls `RemoveDeviceTabs`. `RemoveDeviceTabs(prefix)` helper.
>   - **MainWindow.DisplayPad.cs**: `_activeDpDeviceId (internal int?)`. `DpSelectedDeviceId()`
>     → `_activeDpDeviceId`. `TcDpDevices_SelectionChanged` removed. `DpRefreshDevices()`
>     adds tabs to `TcDevices` after MacroPad tabs. **Bug fixed**: `IsPlugged` filter added
>     (previously all 6 SDK IDs were shown, now only physically connected).
>     `BtnDpClose_Click` calls `RemoveDeviceTabs("dp_")`.
>   - **DisplayPadActionHost.cs**: `CurrentDevice` → `_win._activeDpDeviceId ?? 0`.
>   - **TODO (next session)**: Task 3 — sidebar sezioni per MacroPad e DisplayPad
>     (stile Everest: RadioButton nav a sx, pannello contenuto in basso).
> Previous: 2026-06-28 (UI redesign — sidebar nav + debug mode):
>   - **MainWindow.xaml**: Everest tab completely redesigned. Layout:
>     toolbar (Open/Close/Refresh/Profile + Debug checkbox) → sidebar RadioButtons
>     (160px, SectionTabStyle) → center device ViewBox → bottom settings panel
>     (Auto-height, sections switch visibility). Sections: KeyMapping, RGB & Lighting
>     (preset + custom merged), Display Dial, Dock Actions, Macros, USB Recorder (debug).
>     MacroPad and DisplayPad tabs unchanged. Window min size raised to 1040×620.
>   - **MainWindow.SectionNav.cs** (new): partial class — `InitSectionNav()`,
>     `EvSection_Changed()` (RadioButton.Checked), `ShowEvSection()` (one-panel-visible
>     switcher), `CkEvDebugMode_Click()` + `ApplyDebugMode()` (shows/hides AP buttons,
>     USB Recorder section, SDK log). Debug mode OFF by default.
>   - **MainWindow.Everest.cs**: added `InitSectionNav()` call after `ReloadEverestProfile()`.
>   - **SectionTabStyle** (Window.Resources): RadioButton template — teal left border + teal
>     text when checked; dark hover background. GroupName="EvSections".
> Previous: 2026-06-26 (Dynamic layout + NumpadDisplayKeys):
>   - **MainWindow.Layout.cs** (new): repositions dock and numpad left/right of the
>     keyboard body based on byMMDockPlug/byNumpadPlug (0=hidden, 1=left, 2=right).
>     Reorders SpEvLayout children (StackPanel with x:Name).
>   - **MainWindow.NumpadDisplayKeys.cs** (new): 4 numpad display keys DisplayPad-style
>     — click loads image (72×72 via UploadNumpadImage), right-click configures action
>     (ButtonActionDialog). Thumbnails in buttons. Persisted in EverestStore (keys ndk.{i}.*). Actions via ButtonActionEngine.
>   - **MainWindow.xaml**: SpEvLayout (StackPanel with x:Name), CvsEvDock (Canvas dock
>     with dock_bg.png, Collapsed by default), margins and visibility managed by code-behind.
>   - **EverestService.cs**: added NumpadPlugPosition() and MMDockPlugPosition() (raw bytes).
>   - **MainWindow.CustomLighting.cs**: fixed Canvas names (CvsEvKeyboard/CvsEvNumpad).
>   - **Assets/dock_bg.png** (new, 419×260): dock image resized from BC.
>   - **K2.App.csproj**: added Resource dock_bg.png.
> Previous: 2026-06-26 (Custom Lighting + Media Dock UI removal):
>   - **MainWindow.CustomLighting.cs** (new): "Custom Lighting (per-key)" panel
>     in the Everest tab. Paint mode: pick a color and click keys in the overlay.
>     Applies via SwitchToCustomizeEffect + ChangeCustomizeEffect (area=0, 171 LEDs).
>     Read from device via GetEffCustomizeContent. Fill all, clear all.
>     Color persistence in EverestStore (key `custom.keyColors`, JSON dict).
>   - **EverestSdkNative.cs**: added structs `CustomData` (byMatrix+FWColor) and
>     `CustomEffect` (ByValArray[171] CustomData), P/Invoke `ChangeCustomizeEffect`,
>     `GetEffCustomizeContent`.
>   - **EverestService.cs**: facade `SwitchToCustomize`, `SetCustomEffect`,
>     `TryGetCustomEffect`.
>   - **MainWindow.xaml**: added Expander "Custom Lighting (per-key)" between RGB and
>     Display Dial. Removed Expander "Media Dock" (UI removed, detection kept).
>   - **MainWindow.MediaDock.cs**: reduced to stub (InitMediaDockPanel + CleanupMediaDock).
>   - **MainWindow.Everest.cs**: added InitCustomLightingPanel() + paint mode
>     intercept in EvKeyboardButton_Click.
> Previous: 2026-06-25 (Display Dial + Keyboard Macro):
>   - **MainWindow.DisplayDial.cs** (new): Display Dial panel in the Everest tab.
>     Controls visible pages on the rotating display (bitmask byMMDockShowMenu),
>     clock type 12/24h, screensaver timeout, auto-off, pixel shift, menu color.
>     Settings persisted in EverestStore (keys `dial.*`).
>   - **MainWindow.Macro.cs** (new): Keyboard Macro panel in the Everest tab.
>     Macro CRUD (list, new, delete), recording via global keyboard hook
>     (WH_KEYBOARD_LL + opt. WH_MOUSE_LL), playback via SendInput,
>     import from BaseCamp.db (Macros table). Delay: Recorded/NoDelay/Custom.
>     Playback: Once/RepeatN/WhileHeld/Toggle.
>   - **Models/KeyboardMacro.cs** (new): MacroInput (type/key/delay/xy/text),
>     MacroDefinition (name, delay/playback options, inputs JSON, import BC),
>     enum MacroPlayback/MacroDelay.
>   - **Services/MacroStore.cs** (new): macro persistence in the Macros table of
>     the Everest DB. CRUD + ImportAll + ReadFromBaseCampDb (direct BC DB read).
>   - **Services/MacroRecorder.cs** (new): global keyboard+mouse hook via
>     SetWindowsHookEx(WH_KEYBOARD_LL/WH_MOUSE_LL), captures keydown/keyup with
>     inter-event delay. InputRecorded event.
>   - **Services/MacroPlayer.cs** (new): macro playback via SendInput (Win32).
>     Supports key/mouse/text events, delay recorded/nodelay/custom, repeat N/
>     while-held/toggle. Async thread pool with CancellationToken.
>   - **MainWindow.xaml**: added Expander "Keyboard Macro" between Display Dial and
>     USB Recorder, with ListBox, CRUD+Record/Play/Stop buttons, settings
>     (name, delay, playback, mouse), import from BaseCamp.db.
>   - **MainWindow.Everest.cs**: added InitMacroPanel() call after
>     InitDisplayDialPanel().
> Previous: 2026-06-25 (Media Dock freeze fix):
>   - **MainWindow.MediaDock.cs**: fixed 3 bugs causing freeze/non-detection:
>     1) SendClock now calls APEnable(true)+GetClockInfo before SetClockInfo
>        (like BC's Common::SetClockInfoInHW)
>     2) DockPcTimer_Tick now reads byMMDockMenuIndex via GetExtendInfo and sends
>        SetPCInfo only for the active screen (97=CPU,101=RAM) — same as BC
>     3) BarData: byAll=1, byWidth=3 (conservative values; previous 0xFF
>        likely confused the firmware)
> Previous: 2026-06-25 (Media Dock UI panel):
>   - **MainWindow.MediaDock.cs** (new): partial class with Media Dock panel
>     in the Everest tab. Sections: plug status/refresh, clock (1s timer via
>     SetClockInfo, 12/24h format), PC monitoring (CPU via PerformanceCounter,
>     RAM via GlobalMemoryStatusEx → SetPCInfo, 2s timer), bar LED effects
>     (8 presets via ChangeBarEffect + color + speed), screensaver upload
>     240×204, dock reset. Settings persisted in EverestStore (keys `dock.*`).
>   - **MainWindow.xaml**: added Expander "Media Dock" between RGB and USB Recorder,
>     with 12 XAML controls (LblDockStatus, BtnDockRefresh, BtnDockReset,
>     CbDockClockFormat, BtnDockClockStart, BtnDockPcStart, LblDockCpu,
>     LblDockRam, CbDockBarEffect, CbDockBarSpeed, BtnDockBarColor,
>     BtnDockScreensaver).
>   - **MainWindow.Everest.cs**: added InitMediaDockPanel() call in
>     InitEverestModule + CleanupMediaDock() in Closed.
> Previous: 2026-06-24 (GetFWLayout for color streaming):
>   - **GetFWLayout P/Invoke** (`EverestSdkNative.cs`): added
>     `GetFWLayout(ref int)` → HID `11 12`. Identified via
>     reverse-engineering of SDKDLL.dll as the only function that
>     emits sub-command 0x12. BC calls it 2× during init.
>   - **InitDllState** (`EverestService.cs`): added `GetFWLayout` call
>     after `GetExtendInfo`, before `EnableKeyFunc`.
>     Without this, `GetColorData` does not work on a clean boot.
>   - **SwitchToCustomizeEffect removed** from `StartLedPreview`:
>     it was turning off the LEDs (putting the FW in custom-color-from-host wait).
> Previous: 2026-06-08 (numpad display keys + media dock):
>   - **P/Invoke numpad display keys** (`EverestSdkNative.cs`): added
>     `SetDisplayKeyPic`, `GetDisplayKeyPic`, `ResetNumpad`, `ResetNumpadPic`,
>     `StartPicUpdate` + struct `PicUpdateInfo`. 4 display keys (d1-d4).
>   - **P/Invoke media dock** (`EverestSdkNative.cs`): added
>     `ChangeBarEffect`, `ChangeBarCustomize`, `GetBarEffectData`, `SetEQInfo`,
>     `SetClockInfo`, `GetClockInfo`, `SetPCInfo`, `ResetMMDock`, `ResetMMDockPic`,
>     `SetExtendInfo` + structs `BarData`, `CustomStatic` (126 LEDs), `BarReadData`,
>     `EQ_DATA` (21B).
>   - **EverestImageUploader** (`Services/EverestImageUploader.cs`): helper
>     for converting images to RGB565 and uploading via `StartPicUpdate`.
>     Three targets: MMDock screensaver (240×204), numpad strip (128×32),
>     numpad square (72×72). Both numpad formats implemented — verify with
>     USB capture which is correct.
>   - **EverestService** facade extended: `IsNumpadPlugged`, `IsMMDockPlugged`,
>     `GetDisplayKeyPic`, `SetDisplayKeyPic`, `UploadNumpadImage` (72×72),
>     `UploadNumpadImageStrip` (128×32), `ResetNumpad`, `SetBarEffect`,
>     `SetBarCustomize`, `UpdateClock`, `SetPCInfo` (CPU/GPU/Disk/Net/RAM),
>     `SetVolume`, `UploadMMDockScreensaver`, `ResetMMDock`, `SetExtendInfo`.
>   - **To verify via USB capture**: numpad image format (72×72 vs
>     128×32?), `ResetNumpadPic` parameters (5 bytes), `EQ_DATA` format (21B),
>     meaning of `byTargetPic`/`byTargetSubItem` for each target.
> Previous: 2026-06-08 (progressive devices + dynamic profiles):
>   - **Progressive devices**: DisplayPad and MacroPad device combos now
>     show progressive labels ("DisplayPad 1", "MacroPad 1") instead of raw
>     SDK IDs. `DpDeviceItem`/`MpDeviceItem` wrappers carry the real SDK ID.
>     Device table shows Label instead of numeric ID.
>   - **Dynamic profiles**: profile combos show only profiles existing in
>     the store (`GetExistingProfiles` query) + "+ New profile" entry to
>     create an empty slot. `DpProfileItem`/`MpProfileItem` wrappers.
>   - **Auto-mapping BC→K2**: import from BaseCamp.db maps profiles by
>     DeviceId (same SDK → same IDs), skips unconnected devices.
> Previous: 2026-06-08 (DP profile import from BaseCamp.db):
>   - **BaseCampDbImporter** (`Services/BaseCampDbImporter.cs`): reads
>     `Profiles` + `DisplayPadLayerBidings` from BaseCamp.db (read-only),
>     translates FunctionType→K2 ActionType, saves base64 images to disk,
>     imports into K2 store (`DisplayPadStore`). Finds the DB via
>     `NativeDependencyResolver.BaseCampDirectories()` + `K2_BASECAMP_DB`.
>   - **"Import from Base Camp" button** in the DisplayPad tab toolbar
>     (`MainWindow.DisplayPad.cs`): shows profiles found in the DB,
>     imports them all into the selected K2 device, uploads images via
>     satellite, activates the profile that was selected in BC.
>   - **EnableColorStream** (`EverestSdkNative.SetVolumeInfo`): P/Invoke
>     for HID command `11 83 00 00 0A` that enables color streaming from
>     the Everest firmware. Called in `StartLedPreview`.
>   - **ISO-IT LED mapping fix**: 8 OEM keys (è,+,ò,à,ù,\,',ì,-)
>     re-adapted from the DB locale to the user's Italian layout.
> Previous: 2026-06-08 (LED mapping fix + real-time preview):
>   - **LedMatrixMapping** (`Models/LedMatrixMapping.cs`): static dictionaries
>     VK→LED index extracted from BaseCamp.db (EverestKeyBidings.DLLMatrixIndex).
>     Three maps: `EverestKeyboard` (87 keys board_left + nav cluster),
>     `EverestNumpad` (17 keys), `MacroPad` (12 keys, wMatrix→ledIndex).
>   - **LedColorPoller** (`Services/LedColorPoller.cs`): 120ms DispatcherTimer
>     that calls `GetColorData` on Everest (171 LEDs) and MacroPad (126 LEDs).
>     Emits `EverestColorsUpdated` / `MacroPadColorsUpdated` events on the UI thread.
>   - **MainWindow.LedPreview.cs**: semi-transparent Rectangle overlays (alpha 50%)
>     on top of key Canvas Buttons. Uses LedMatrixMapping to translate
>     VK (Button.Tag) → GetColorData index. Board_left and board_right use
>     separate maps (nav cluster vs numpad for the same VK codes).
>   - **MacroPadSdkNative.GetColorData**: added P/Invoke (native export
>     `GetColorData` from `MacroPadSDK.dll`, struct `MACROPAD_COLOR` 126×FWColor).
>   - **TODO.md** created with pending feature roadmap.
> Previous: 2026-06-07 (DisplayPad tab integrated in K2.App):
>   - **DisplayPad tab in K2.App**: Canvas 640×300 with dkd_bg.png background,
>     12× Button with key_button.png (80×87), centered 2×6 grid.
>     Rotation 0/90/270, icon thumbnails in keys. Click → load image,
>     right-click → configure action / remove.
>   - **Satellite x64 (K2.DisplayPad.Satellite)**: x64 console process that
>     wraps DisplayPadSDK.dll and communicates with K2.App (x86) via named pipe
>     JSON. Protocol: request/response + push events (plug/key/progress).
>     K2.App starts it automatically. Files: Program.cs, SdkHandler.cs.
>   - **IPC client (DisplayPadSatelliteClient.cs)**: named pipe client in
>     K2.App, synchronous commands with timeout, reader thread for push events.
>     Finds the satellite in the relative x64 build or next to the exe.
>   - **New K2.App files**: MainWindow.DisplayPad.cs (partial DP tab),
>     DisplayPadActionHost.cs (IActionHost for DP action engine),
>     Services/DisplayPadStore.cs (SQLite actions/images on x86 side),
>     Services/DisplayPadSatelliteClient.cs (IPC client),
>     Models/DisplayPadKey.cs (bindable DP key model).
>   - **dkd_bg.png**: created programmatically (device top-down,
>     dark-grey body, 12 black LCD slots in a 2×6 grid).
>   - **Solutions updated**: K2.sln and K2.DisplayPad.sln now include the
>     Satellite project (x64). build-check.bat also cleans Satellite.
> Previous: 2026-06-07 (multi-layout keyboard + overflow fix):
>   - **KeyboardLayout.cs**: now supports multiple layouts via enum
>     `KeyboardLayoutType` (AnsiUs, IsoIt). `GetBoardLeft(layout)` method
>     returns the correct key set. `DetectLayout()` detects the Windows
>     language via `GetKeyboardLayout` Win32 (primary LANGID).
>   - **ISO-IT layout**: Italian labels (\,',ì,è,+,ò,à,ù,<),
>     L-shaped Enter (tall 62px rows 2→3, ù overlaid for L effect),
>     short LShift (50px) + extra `<` key (0x56), RShift 58px.
>   - **Bottom row overflow fix**: modifiers reduced to 38px, Space to 196px
>     (was 42+210 = overflow on nav cluster). Now 20px margin.
>   - **FN instead of Menu**: bottom row uses "FN" (Everest Max).
> Previous: 2026-06-07 (BC-style interactive key overlays):
>   - **MacroPad key overlay**: Canvas 510×370 with mkd_bg.png background,
>     12× Button with key_button.png (55×55), positioned in a 2×6 grid
>     centered in the screen area. Click → configure action. Rotation handled.
>   - **Everest key overlay**: Canvas board_left (642×260, keybg.png) +
>     Canvas board_right (166×260, board_right.png). ~90 keys created by
>     KeyboardLayout.cs with positions from BC CSS. CSS-styled 3D keys,
>     click = capture/select, teal highlight on physical press.
>   - **Images copied**: mkd_bg.png, key_button.png, keybg.png,
>     board_right.png, keytop.png, keytop_binding.png (media dock, future).
>   - **KeyboardLayout.cs**: full ANSI US layout data definition
>     (F-row, numbers, letters, modifiers, nav, numpad) with SDK matrixId.
> Previous: 2026-06-07 (BC layout + VEH crash survival):
>   - **VEH crash survival**: frame-unwind via EBP chain to survive
>     SDKDLL.dll crashes. Re-entrancy guard.
> Previous: 2026-06-07 (Wave 2-color fix + stability):
>   - **Wave/Tornado 2 colors WORKING**: `byRandColor=16`, `byBlockNum=1`, `pos=0`.
>   - **All 2-color effects**: Reactive/Yeti/Matrix use bkColor;
>     Breath uses colorLv[1]+byRandColor=16. Matrix2 (enum 200) preserved.
>   - **SaveFlash debounced 500ms** + Thread.Sleep(50) post-effect.
>   - **VEH crash handler** for native access violations (minidump + crash log).
> Previous: 2026-06-07 (Everest stability + speed fix):
>   - **SDKDLL.dll +0x5133 crash (access violation)**: SaveFlash delayed by
>     80ms on thread pool in BOTH paths (block + non-block) of
>     `EverestService.SetEffect`. Cause: DLL's internal HID queue corrupted
>     when SaveFlash arrives immediately after ChangeEffect/ChangeBlockEffect.
>   - **Block effect speed (Wave/Tornado)**: confirmed range **0-100**
>     (0=slow, 100=fast); the DLL transforms internally. UI 5 positions
>     → 0/25/50/75/100 via `step*25`. Tornado works; Wave to re-test
>     after crash fix (crash may have prevented the effect).
>   - **Key logging removed** from all 3 locations: `OnKeyCallback`,
>     `MainWindow.Everest.cs`, `MainWindow.Keys.cs`.
>   - **Crash logging**: `App.xaml.cs` now has `OnProcessExit` + `WriteCrashLog`
>     + `TryWriteMiniDump` (dbghelp.dll) for native crashes.
>   - **`_cachedProfile`**: replaces repeated `GetFWInfo()` calls in SaveFlash
>     (avoids extra HID packets colliding with DLL polling).
>   - **`_sdkLock`**: lock around all serializable SDK calls.
> Previous: 2026-06-05 (Integrated USB Recorder)
> Before that: 2026-06-04 (Everest Wave — speed fix and SaveFlash from USB capture)
> Historical details in `_reference/EVEREST_TODO.md`.

