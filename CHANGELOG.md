# CHANGELOG.md — storico sessioni di sviluppo K2

> Log dettagliato, sessione per sessione, di cosa e' stato fatto/scoperto
> durante lo sviluppo di K2 (bug, fix, decisioni, verifiche pendenti su
> hardware fisico). Ordine: piu' recente in cima ("Last updated" seguito
> da "Previous:" a scendere).
>
> NON va letto per intero a inizio sessione — quello scopo lo serve la
> mappa stabile in `_PROJECT_MAP.md`. Consultare qui solo per il contesto
> di una modifica specifica passata (grep per parola chiave/data).

> Last updated: 2026-07-13 (Tab Home: card ingrandite del 30%):
>   - **Richiesta utente**: "ingrandisci le schede di un 30%". Ridimensionate
>     tutte le misure della card in `MainWindow.xaml` (`Card` Width 270→351,
>     Height 190→247, `CornerRadius` 10→13; barra accento 4→5; immagine
>     220×150→286×195, margine negativo di sbordo -10→-13; nome device
>     FontSize 22→29, margini testo 18/18/10/10→23/23/13/13; spaziatura tra
>     card 16→21) — stesso fattore ×1.3 ovunque per mantenere le proporzioni.
>   - Il build ha richiesto `Stop-Process K2.App` prima: un'istanza Debug
>     era rimasta aperta e teneva bloccato l'exe (MSB3027, stesso scenario
>     documentato per `stop-k2.bat` in `_PROJECT_MAP.md`/CLAUDE.md).
>   - **Verificato**: `dotnet build K2.sln -p:Platform=x86` pulito (0
>     errori/warning).
>
> Previous: 2026-07-13 (Tab Home: rimossa la dicitura categoria
> KEYBOARD/MOUSE/KEYPAD/DISPLAY dalle card):
>   - **Richiesta utente**: via l'etichetta piccola sopra il nome device
>     nelle card della Home. `HomeDeviceTile.Category` rimosso dal modello
>     (`Models/HomeDeviceTile.cs`) e da tutte le chiamate in `RefreshHomeTiles`
>     (`MainWindow.Home.cs`); XAML della card semplificata a un solo
>     `TextBlock` (il nome) al posto dello `StackPanel` con due righe.
>     Rimosse anche le 4 chiavi loc ora inutilizzate (`home_cat_keyboard`/
>     `_mouse`/`_keypad`/`_display`, EN+IT) — `home_heading` resta (titolo
>     sopra la griglia, non sulla singola card).
>   - **Verificato**: `dotnet build K2.sln -p:Platform=x86` pulito (0
>     errori/warning).
>
> Previous: 2026-07-13 (Tab Home: stato "nessun device" con logo K2 +
> testo, invece di icona casa + scritta "K2"):
>   - **Richiesta utente**: quando `_homeTiles` è vuota (nessun dispositivo
>     connesso), `PnlHomeEmpty` mostrava l'icona Segoe MDL2 casa + "K2" in
>     grande — sostituito con il logo K2 (`Assets/K2_logo.png`, 96×96,
>     stesso stile/dimensione già usato in `PnlLoading`) + testo "No device
>     connected" sotto. Chiave loc `home_subtitle` rinominata
>     `home_no_device` (era usata solo qui) con nuovo valore, EN+IT.
>   - **Verificato**: `dotnet build K2.sln -p:Platform=x86` pulito (0
>     errori/warning). Non ritestato su hardware fisico (modifica solo
>     visuale/testuale).
>
> Previous: 2026-07-13 (Keycap Appearance: legende traslucide come
> checkbox indipendente + personalizzazione per-tasto colore/immagine +
> logo Mountain fisso su Esc):
>   - **Richiesta utente**: (1) le legende traslucide non sono più un 4°
>     valore della combo "Keycap type" ma un checkbox indipendente
>     applicabile a qualunque stile (Normal/Pudding/Reverse Pudding); (2)
>     possibilità di cambiare il colore del singolo keycap; (3) possibilità
>     di mettere un'immagine personalizzata al posto della legenda di un
>     singolo tasto (simula un keycap diverso); (4) per il tasto Esc,
>     opzione fissa "usa il logo Mountain" senza dover scegliere un file.
>     Tutto puramente cosmetico (anteprima a schermo), stessa scoping
>     device-wide/non-per-profilo della feature preesistente. Applicato a
>     tutti e 3 i device che hanno Keycap Appearance: Everest Max, Everest
>     60, MacroPad.
>   - **`KeycapStyle` enum ridotto a 3 valori** (Normal=0/Pudding=1/
>     ReversePudding=2, era Normal/Translucent/Pudding/ReversePudding=0-3).
>     Nuovo bool indipendente "Translucent legends"/"Legende traslucide"
>     (`settings.keycap_translucent_legend`, stessa chiave k/v in
>     EverestStore/MacroPadStore/Everest60Store — vedi voce successiva sul
>     riallineamento della persistenza di Everest 60).
>     **Migrazione automatica al primo caricamento**: l'assenza della
>     chiave `translucent_legend` è il marker "schema vecchio" — il
>     vecchio valore 1 (Translucent) diventa Normal+checkbox ON, i vecchi
>     2/3 (Pudding/ReversePudding) scendono a 1/2; scritto subito nel nuovo
>     schema così la migrazione scatta una volta sola. La logica si è
>     semplificata pulita: il vecchio "Translucent" era già esattamente
>     "Normal + tint della legenda", quindi l'alone (halo) resta legato solo
>     a Normal (Pudding/ReversePudding mostrano già il LED via bordo/centro)
>     e il tint della legenda ora è ortogonale allo stile.
>   - **Override per-tasto colore/immagine** (2026-07-13): nuova tabella
>     `KeycapOverrides(KeyId INTEGER PRIMARY KEY, ColorHex TEXT, ImagePath
>     TEXT)` in `EverestStore`/`MacroPadStore`/`Everest60Store` (stesso
>     pattern `CREATE TABLE IF NOT EXISTS` delle altre tabelle), global/
>     device-wide come il resto di Keycap Appearance (non per-profilo).
>     `KeyId` riusa l'identità già esistente delle dictionary `KeyVisual`
>     (LED index per Everest Max/60, indice fisico 0-11 per MacroPad) —
>     nessuna mappatura aggiuntiva necessaria. `KeyVisual` stesso non è
>     stato esteso: le funzioni `ApplyKeycapAppearanceToAllKeys` (e
>     equivalenti MacroPad/Everest 60) ora iterano le dictionary con le
>     chiavi invece che solo `.Values`, per avere il KeyId a disposizione.
>   - **Nuovo `KeycapCustomizeDialog.xaml(.cs)`**: checkbox "Override
>     color" + swatch (stesso `ColorDialog` WinForms usato ovunque), bottone
>     "Custom image..." che riusa la pipeline già esistente per la LCD del
>     numpad Everest (`OpenFileDialog` → `ImageCropDialog`/`CropEditor`,
>     NON `EverestImageUploader` — quello è specifico per l'upload RGB565
>     verso l'LCD fisico del numpad, qui l'immagine è solo cosmetica),
>     "Clear image", e — solo per il tasto Esc (`KeyId==0`, identico su
>     Everest Max via `LedMatrixMapping` e su Everest 60 essendo il primo
>     tasto del layout) — checkbox "Use Mountain logo" che punta a un
>     sentinel (`MainWindow.MountainLogoImagePath`) invece di un file reale.
>     Il dialog applica ogni modifica **live** via evento `Changed` (niente
>     OK/Cancel: stesso stile "apply immediato" già usato dai color-picker
>     esistenti in tutta l'app) — il chiamante persiste sullo store e
>     rilancia `ApplyKeycap*AppearanceToAllKeys` ad ogni evento.
>   - **Asset Mountain logo**: nessun logo Mountain era già presente in K2;
>     trovato in `Mountain Base Camp/resources/bin/wwwroot/images/
>     logo-square.png` (= `favicon-mountain.png`, 53×53, il simbolo "ala"
>     del brand Mountain su sfondo blu) e copiato in
>     `K2.App/Assets/mountain_logo.png` (stesso precedente di
>     `makalu_mouse.png`/`everest60_board.png`: asset PNG di Base Camp
>     copiati 1:1, non codice decompilato).
>   - **Entry point modifica per-tasto**: nuovo checkbox "Edit individual
>     keycaps"/"Personalizza singoli keycap" dentro la sezione Settings di
>     ciascun device (`_evKeycapEditMode`/`_mpKeycapEditMode`/
>     `_ev60KeycapEditMode`, transiente, non persistito). Il click sui
>     tasti della tastiera/griglia (già sempre visibile in ogni sezione,
>     solo il pannello laterale cambia) instrada al dialog quando la
>     sezione Settings è attiva E il checkbox è spuntato — nuovo branch
>     aggiunto in cima a `EvKeyboardButton_Click`/`Ev60KeyboardButton_Click`/
>     `KeyButton_Click`, prima della paint-mode/action-dialog esistente.
>   - **Swap Content legenda↔immagine sicuro**: `SetLegendForeground` fa già
>     pattern-match su `Button.Content` (TextBlock/Panel) e non fa nulla per
>     altri tipi — quindi impostare `Content` a un'`Image` non rompe il
>     codice esistente di tint della legenda. Il Content originale (legenda
>     costruita a build-time: TextBlock semplice, StackPanel 2 righe, o Grid
>     4 angoli per Everest Max) viene cachato una volta in un nuovo
>     dictionary (`_evOriginalKeyContent`/`_mpOriginalKeyContent`/
>     `_ev60OriginalKeyContent`, popolato in `BuildEverestKeyVisuals`/
>     `BuildMacroPadKeyVisuals`/`BuildEverest60KeyboardOverlay`) per poter
>     ripristinare la legenda esatta quando l'override immagine viene
>     rimosso, invece di provare a ricostruirla da zero.
>   - **Icone Win colorate dal LED preview** (follow-up richiesta utente):
>     `BuildWinIcon` (Everest Max) disegna il tasto Win come 4 `Rectangle`
>     bianche fisse dentro un `Grid`, mai toccate da `SetLegendForeground`
>     (che gestiva solo `TextBlock.Foreground`). Estesa `SetLegendForeground`
>     per settare anche `.Fill` su ogni `System.Windows.Shapes.Shape` dentro
>     un `Panel` — ora le icone Win seguono lo stesso colore statico/tint
>     LED live del resto delle legende (nessun impatto su MacroPad/
>     Everest 60, che non hanno icone non-testuali).
>   - **Persistenza Keycap Appearance di Everest 60 riallineata a Everest
>     Max/MacroPad** (follow-up richiesta utente "allinea le impostazioni
>     keycap"): color mode/custom hex/text color/text custom hex/style/
>     translucent-legend erano le UNICHE impostazioni Keycap rimaste in
>     `AppSettings` (JSON condiviso da tutta l'app) invece che nel proprio
>     store per-device, eredità di quando Everest60Store non esisteva
>     ancora — asimmetria trovata confrontando le 3 sezioni Settings a
>     comando dell'utente, nessun'altra differenza reale trovata (le uniche
>     2 legittime: "Keyboard color" Silver/Black esiste solo su Everest Max
>     perché è l'unico con asset per entrambi gli chassis; le icone Win
>     sopra, idem, solo Everest Max ha tasti Windows). Spostate in
>     `Everest60Store`'s tabella `Settings` k/v, stesse chiavi
>     `settings.keycap_*` di EverestStore/MacroPadStore (prima erano le
>     uniche impostazioni Keycap Appearance NON nel proprio store — la
>     tabella `KeycapOverrides` per-tasto, introdotta nella stessa sessione,
>     usava già Everest60Store, quindi risultava incoerente con le altre).
>     **Nessuna migrazione dati necessaria**: i campi `AppSettings.
>     Everest60Keycap*` non sono mai stati scritti da nessuna sessione reale
>     (il tab Everest 60 richiede hardware rilevato per essere visibile, e
>     nessun Everest 60 è mai stato collegato finora) — rimossi
>     completamente da `K2.Core/AppSettings.cs` invece di lasciarli come
>     fallback morto.
>   - **Build pulita** (dotnet build --rebuild, entrambe le solution, 0
>     errori/0 warning). **Da fare**: verifica su hardware fisico/UI reale
>     di tutto il flusso (nessun device disponibile in questa sessione per
>     testare click→dialog→persistenza→re-render dal vivo); nessun asset
>     "Mountain logo" alternativo è stato mostrato all'utente per scelta,
>     preso quello che sembrava più adatto a un keycap quadrato.
>
> Previous: 2026-07-13 (Tab Home: griglia di riquadri per dispositivo
> connesso, ispirata al device-picker di Base Camp):
>   - **Richiesta utente**: riquadri nella tab Home in base ai dispositivi
>     connessi, nome grande, ispirati a uno screenshot di Base Camp
>     (categoria piccola in alto + nome grande + immagine dispositivo +
>     barra accento laterale), click → va al tab del dispositivo. Immagini
>     fornite dall'utente in `Grafiche/png_home/` con regole di scelta
>     specifiche per Everest Max (dock/numpad) ed Everest 60 (lato numpad).
>   - **Asset**: copiate in `K2.App/Assets/Home/` (11 PNG) + `<Resource
>     Include>` in `K2.App.csproj` (nessun glob nel csproj esistente, ogni
>     file elencato a mano, stesso pattern degli altri Assets). Caricate a
>     runtime via `pack://application:,,,/Assets/Home/{file}` (helper
>     `HomeImage` in `MainWindow.Home.cs`).
>   - **Nuovo `MainWindow.Home.cs`** (partial) + `Models/HomeDeviceTile.cs`
>     (Category/Name/ImagePath/Target). `RefreshHomeTiles()` ricostruisce
>     `_homeTiles` (ObservableCollection, bindata a `IcHomeTiles` in XAML)
>     da zero ad ogni chiamata — costo trascurabile (manciata di item) —
>     nello stesso ordine fisso dei tab (Everest Max → Everest 60 → Makalu
>     → DisplayPad → MacroPad), un tile per ogni `TabItem` con
>     `Visibility=Visible`. Chiamata da **4 punti**: `SetDeviceTabVisible`
>     (MainWindow.xaml.cs, copre Everest Max/60/Makalu/MacroPad),
>     `DpRefreshDevices` (DisplayPad, tab aggiunti/rimossi per davvero, non
>     via `SetDeviceTabVisible`), `UpdateKeyboardLayout` (MainWindow.Layout.cs,
>     quando cambia dock/numpad Everest Max), `ApplyEv60NumpadPosition`
>     (quando cambia lato/presenza numpad Everest 60).
>   - **Regole immagine Everest Max** (utente): entrambi dock+numpad →
>     `everest_max.png`; solo dock → `everest_mediadock.png`; solo numpad →
>     `everest_numpad.png`; nessuno dei due → `everest.png`. Lette da due
>     nuovi campi cache `_evDockConnected`/`_evNumpadConnected`
>     (`MainWindow.Layout.cs`) invece di ri-interrogare l'SDK ad ogni tile
>     refresh — **limite noto**: questi si aggiornano solo quando
>     `UpdateKeyboardLayout` gira (apertura driver + tab Everest Max aperto,
>     poll ogni 3s SOLO mentre quel tab è selezionato, scelta di scope
>     esistente e deliberata — vedi voce Layout.cs più sotto), quindi il
>     tile può mostrare uno stato dock/numpad non aggiornato finché l'utente
>     non apre almeno una volta il tab Everest Max nella sessione. Non
>     esteso il poll per non riaprire quella decisione di scope.
>   - **Regole immagine Everest 60** (utente): numpad assente →
>     `everest60.png`; presente → `everest60_left.png`/`_right.png` in base
>     al lato. Letto da `_ev60NumpadPosition`, sempre fresco (il poll
>     Everest 60 gira incondizionatamente ogni 3s, non solo a tab aperta) —
>     nessuna limitazione qui.
>   - **Bug di ordinamento trovato e fissato in `MkRefreshStatus`**
>     (Makalu): `SetDeviceTabVisible(TabMakalu, connected)` — che dentro
>     chiama `RefreshHomeTiles()` — girava PRIMA che `_mkInfo` (il modello
>     rilevato, letto da `MkHomeImageFile()`) venisse aggiornato: al primo
>     collegamento della sessione il tile veniva costruito con il modello
>     di default (Makalu 67) anche se il device reale era il Max, e non si
>     autocorreggeva più finché non scattava un altro cambio di visibilità.
>     Fix: spostato il blocco che aggiorna `_mkInfo`/header PRIMA della
>     chiamata a `SetDeviceTabVisible`. Verificato che Everest 60 non ha lo
>     stesso problema: lì `ApplyEv60NumpadPosition` chiama
>     `RefreshHomeTiles()` una seconda volta subito dopo (stesso tick
>     sincrono), quindi si autocorregge comunque prima che l'utente veda
>     nulla.
>   - **Verificato**: `dotnet build` di entrambe le solution puliti (0
>     errori/warning) DOPO clean di bin/obj — un primo tentativo senza clean
>     ha dato un falso errore CS0246 (`KeycapOverrideRecord` non trovato in
>     `EverestStore.cs`, tipo definito nello stesso file, causa quasi
>     certamente cache di build incrementale/csproj temporaneo WPF stale,
>     non un vero errore — sparito dopo `rm -rf */bin */obj`). **Non
>     verificato su hardware fisico** (nessun test di collegamento/
>     scollegamento reale in questa sessione) — da confermare: comparsa/
>     sparizione dei riquadri, scelta immagine corretta per ogni
>     combinazione dock/numpad Everest Max e lato Everest 60, click →
>     navigazione al tab giusto.
>
> Previous: 2026-07-13 (Tab Home sempre presente; fix rilevamento
> disconnessione Everest Max; Everest 60 default su Key Binding):
>   - **Richiesta utente** (follow-up al giro tab dispositivi sotto, dopo
>     conferma "ok funziona" su hardware reale): 3 richieste — (1) tab
>     "Home" iniziale con icona casa, sempre presente; (2) il tab Everest
>     Max ("Everest Core" — nome che il tab assume via `UpdateEverestAutoName`
>     quando nessun accessorio dock/numpad è agganciato, vedi
>     `MainWindow.Layout.cs`) non spariva quando la tastiera veniva
>     scollegata del tutto; (3) Everest 60 deve aprirsi sulla sezione Key
>     Binding invece di Lighting.
>   - **Bug reale trovato (2)**: `EvRefreshConnectionStatus`/`EvRefresh`
>     usavano `EverestService.IsPlugged()` → `SDKDLL.dll`'s `IsDevicePlug()`
>     per pilotare la visibilità del tab — confermato su hardware che
>     questa chiamata continua a riportare "plugged" anche dopo uno
>     scollegamento fisico completo (il suo stato interno sembra
>     aggiornarsi solo alla prossima `OpenUSBDriver()`, non ad ogni query).
>     Fix: **`EvIsPhysicallyConnected()`** (`MainWindow.Everest.cs`) usa
>     invece `EverestHidNative.FindCommandInterfacePath()` — enumerazione
>     HID raw dal vivo della command interface MI_03, stesso approccio già
>     usato da `Everest60Service`/`MakaluService` per il loro poll di
>     connessione (mai affidarsi allo stato cache dell'SDK vendor — lezione
>     già imparata più volte in questo progetto, vedi bug MacroPad/Makalu
>     analoghi in `_PROJECT_MAP.md`). L'apertura è a 0 permessi di accesso
>     (query-only), quindi non confligge mai con l'handle che `SDKDLL.dll`
>     tiene aperto sulla stessa interfaccia. Sia il poll (`EvRefreshConnectionStatus`,
>     ogni 3s) sia il refresh manuale (`EvRefresh`, bottone toolbar) ora
>     usano questo check per la visibilità del tab.
>   - **Tab Home (1)**: `TabHome` (Tag="home") aggiunto come primo elemento
>     di `TcDevices`, **mai** `Visibility="Collapsed"` (a differenza degli
>     altri 4 tab statici) — header icona-only (`&#xE80F;`, Segoe MDL2
>     Assets "Home"), tooltip da `Loc.Get("tab_home")`. Contenuto
>     (`PnlHome`): titolo "K2" + sottotitolo (`home_subtitle`) centrati,
>     nessuna logica oltre al semplice show/hide in `TcDevices_SelectionChanged`
>     (aggiunto anche a `BtnSettingsTab_Click`/`BtnMacroTab_Click`, che
>     gestiscono i pannelli a mano). Essendo sempre visibile e sempre primo,
>     diventa automaticamente il tab selezionato di default sia
>     all'avvio (`AutoOpenDrivers`'s `FirstOrDefault(Visible)`) sia come
>     fallback quando l'ultimo device connesso si scollega (il ramo
>     "nessun tab visibile" di `SetDeviceTabVisible`, che prima poteva
>     deselezionare tutto, ora di fatto non scatta mai più — tenuto come
>     fallback difensivo). **Nessun rischio del bug "evento XAML durante
>     InitializeComponent" già documentato in questo progetto** (RbMkSecRgb/
>     SldMkDpi/SldEv60Brightness): quel bug scatta quando un attributo XAML
>     esplicito (`IsChecked="True"`, `Value="100"` ≠ default) forza un
>     evento *CLR* sincrono verso un campo non ancora assegnato; la
>     selezione di default del *primo TabItem* di un `TabControl` non è
>     governata da un attributo del genere (nessun `IsSelected="True"`/
>     `SelectedIndex` espliciti in XAML) — prova empirica: `TabEverest` era
>     già il primo tab prima di questa sessione con `PnlEverest` dichiarato
>     molto più avanti nello stesso file, e non ha mai causato questo
>     crash.
>   - **Everest 60 default section (3)**: `InitEv60SectionNav` ora imposta
>     `RbEv60SecKeyBinding.IsChecked = true` invece di `RbEv60SecLighting`
>     (era stata una scelta deliberata 2026-07-11 per non aprire eager la
>     sessione SDK non ancora verificata — vedi `_PROJECT_MAP.md` — ora
>     superata dal test hardware reale di questa sessione).
>   - **Verificato**: `dotnet build K2.sln -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -p:Platform=x64` entrambi puliti (0 errori, 0
>     warning). **Non riverificato su hardware fisico in questa sessione**
>     (l'utente aveva testato la versione precedente, non queste 3 modifiche)
>     — da confermare: che il tab Everest Max sparisca davvero entro 3s da
>     uno scollegamento fisico reale con il nuovo check raw-HID, che Home
>     compaia/si comporti come atteso, e che Everest 60 apra su Key Binding.
>
> Previous: 2026-07-13 (Tab dispositivi: nascosti se scollegati,
> ordine fisso Everest Max > Everest 60 > Makalu > DisplayPad > MacroPad):
>   - **Richiesta utente**: "Assicurati che ogni dispositivo non abbia la
>     scheda visibile se è scollegato e che quando viene collegato, compaia
>     la scheda" + ordine esplicito dei tab. Prima di questa sessione solo
>     i tab DisplayPad (multi-istanza, aggiunti/rimossi da `DpRefreshDevices`)
>     rispettavano già questa regola; Everest Max/Everest 60/Makalu/MacroPad
>     erano tab statici sempre presenti in `MainWindow.xaml`, MacroPad e
>     Everest Max senza nemmeno un check di connessione periodico.
>   - **Everest 60 e Makalu avevano già un poll ogni 3s** (`Ev60RefreshStatus`/
>     `MkRefreshStatus`, timer avviati incondizionatamente in `InitEverest60Module`/
>     `InitMakaluModule`) — bastava agganciarci `SetDeviceTabVisible` (nuovo
>     helper in `MainWindow.xaml.cs`). **Everest Max non aveva alcun poll**:
>     aggiunto `_evPollTimer`/`EvRefreshConnectionStatus()` in
>     `MainWindow.Everest.cs`, deliberatamente silenzioso (nessun `Log()` per
>     tick, a differenza del verboso `EvRefresh()` usato dai bottoni toolbar)
>     per non floodare la console ogni 3s — chiama comunque `_everest.
>     IsPlugged()` prima che il driver sia mai stato aperto, pattern già
>     tollerato dal codice esistente (`BtnEvOpen_Click` chiama `EvRefresh()`
>     anche quando `Open()` fallisce). **MacroPad**: nessun poll nuovo, il
>     plug/unplug arriva già via `WM_DEVICE_PLUG` → `RefreshDevices()`;
>     bastava chiamare `SetDeviceTabVisible(TabMacroPad, items.Count > 0)`
>     lì e in `BtnClose_Click`.
>   - **`SetDeviceTabVisible(TabItem, bool)`** (`MainWindow.xaml.cs`, vicino
>     a `RemoveDeviceTabs`): helper condiviso per i 4 tab statici (Everest
>     Max/60, Makalu, MacroPad — i tab DisplayPad restano gestiti con add/
>     remove vero, non Visibility, essendo multi-istanza). Se il tab
>     nascosto è quello attualmente selezionato, sposta la selezione sul
>     prossimo tab visibile, o — se non ne resta nessuno — pulisce
>     esplicitamente tutti i `Pnl*`/`Br*` e `TcDevices.SelectedIndex = -1`
>     (stesso stato di `BtnSettingsTab_Click`): necessario perché
>     `TcDevices_SelectionChanged` fa un early-return silenzioso quando
>     `SelectedItem` diventa `null`, altrimenti il pannello dell'ultimo
>     device disconnesso resterebbe visibile senza un tab selezionato.
>   - **Ordine tab fisso in XAML**: `TabEverest` → `TabEverest60` →
>     `TabMakalu` → (tab DisplayPad inseriti qui da `DpRefreshDevices`,
>     `insertIdx` spostato da `TabMacroPad` a `TabMakalu`) → `TabMacroPad`
>     (era `Everest, MacroPad, Everest60, Makalu` prima). Tutti e 4 i tab
>     statici partono `Visibility="Collapsed"` in XAML (prima solo 3 su 4 —
>     `PnlEverest`/`BrEverest` erano Visible di default, essendo il primo
>     tab storico): innocuo perché `PnlLoading` (ZIndex 10, copre tab strip
>     + contenuto) resta visibile per tutta `AutoOpenDrivers()`, quindi
>     nessun flash all'avvio. Selezione finale in `AutoOpenDrivers`
>     (`TcDevices.SelectedItem = ... FirstOrDefault(t => t.Visibility ==
>     Visible)`, prima sceglieva il primo tab a prescindere dallo stato di
>     connessione).
>   - **Makalu Max vs Makalu 67**: restano UN SOLO tab (`TabMakalu`), non
>     due — l'hardware attuale (`MakaluHidNative.FindDevice()`) trova UN
>     mouse alla volta (PID 0x0002 Max / 0x0003 67), quindi i due modelli
>     occupano lo stesso slot nell'ordine richiesto invece di due slot
>     indipendenti; supportare Max+67 collegati simultaneamente sarebbe un
>     redesign più ampio (oggi `MakaluService` non traccia un path HID per
>     device, riapre find-first ad ogni chiamata) e non richiesto qui.
>     `MkRefreshStatus` ora aggiorna anche `TabMakalu.Header` al modello
>     rilevato (`info.Label`, "Makalu Max"/"Makalu 67") **solo se**
>     `AppSettings.MakaluDeviceName` è null, per non calpestare un rename
>     utente esistente (che prima non veniva comunque mai riapplicato
>     all'avvio — bug preesistente, non toccato in questa sessione, fuori
>     scope).
>   - **Verificato**: `dotnet build K2.sln -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -p:Platform=x64` entrambi puliti (0 errori, 0
>     warning). **Non verificato su hardware fisico** (nessun test di
>     plug/unplug reale in questa sessione) — da confermare: che i tab
>     compaiano/spariscano davvero entro 3s dal collegamento/scollegamento
>     fisico per Everest Max/60/Makalu, e che MacroPad reagisca al vero
>     evento `WM_DEVICE_PLUG` (non solo al refresh manuale).
>
> Previous: 2026-07-13 (Drag & drop: scambio azione+icona tra due
> bottoni, per tutti i device con vere azioni K2 — MacroPad, Everest Max
> tastiera + numpad display keys, DisplayPad):
>   - **Richiesta utente**: "imposta, per tutti i dispositivi, lo
>     spostamento drag & drop delle azioni ed eventuali icone da un
>     bottone a un altro". Chiarito con l'utente (AskUserQuestion) lo
>     scope prima di implementare: **solo** i device con un vero oggetto
>     "azione" `IActionHost`/`ButtonActionEngine` (MacroPad, Everest Max,
>     DisplayPad) — Everest 60 (Key Binding via `Everest360_USB.dll`) e
>     Makalu (remap funzione firmware) non hanno azioni K2 assegnabili,
>     restano fuori da questo giro. Comportamento: **scambio (swap)**,
>     mai sovrascrittura silenziosa — trascinare A su B scambia
>     ActionType/ActionValue (e ImagePath dove esiste) tra i due, nessuna
>     azione va persa.
>   - **Nessun pattern drag & drop preesistente in tutto il repo**
>     (verificato: zero `DoDragDrop`/`AllowDrop`/`QueryContinueDrag`
>     ovunque) — introdotto da zero. Pattern condiviso: `PreviewMouseLeft
>     ButtonDown` registra punto iniziale + candidato; `PreviewMouseMove`
>     controlla soglia (`SystemParameters.MinimumHorizontal/VerticalDrag
>     Distance`) e solo se superata chiama `DragDrop.DoDragDrop`, che
>     assorbe la cattura del mouse — il `Button.Click` normale (click
>     senza trascinamento) continua a funzionare invariato, perché parte
>     solo su un vero rilascio senza passare da `DoDragDrop`. Helper
>     condiviso nuovo **`K2.Core/DragDropHelper.cs`** (soglia +
>     evidenziazione drop-target via `Button.Opacity`), riusato dai
>     quattro punti di innesto per non duplicare la stessa logica in
>     K2.App/K2.DisplayPad (progetti/piattaforme diverse, x86 vs x64,
>     entrambi referenziano K2.Core).
>   - **MacroPad** (`MainWindow.Keys.cs`): tasti indicizzati per indice
>     fisico stabile (`_keyButtons[i].Tag = MacroPadKey`, la rotazione è
>     un `LayoutTransform` sul Canvas — non richiede alcuna traduzione
>     visuale→fisica per il drag). Scambia `ActionType`/`ActionValue`,
>     due `_store.SaveKey(...)`.
>   - **Everest Max tastiera** (`MainWindow.Everest.cs`): `Tag` è
>     `int matrixId`, non l'oggetto modello (la tastiera ha 100+ tasti,
>     nessuna griglia fissa pre-allocata — un tasto esiste in `_evByMatrix`
>     solo se già configurato). Drop su un tasto non ancora configurato fa
>     get-or-create (stesso pattern già usato da `EvKeyboardButton_Click`),
>     poi `EvPersistOrDiscardKey` su entrambi i lati (gestisce già la
>     cancellazione quando un lato risulta vuoto dopo lo scambio). Tasto
>     FN (matrixId 261, riservato al layer switching) escluso sia come
>     sorgente che come target.
>   - **Everest Max numpad display keys** (`MainWindow.NumpadDisplayKeys.cs`,
>     4 tasti con icona 72×72 — l'unico dei tre moduli K2.App con
>     immagine per-tasto): stato in array paralleli (`_ndkActions[]`/
>     `_ndkImagePaths[]`, nessuna classe modello). Scambio locale degli
>     array, poi per lato con immagine non vuota: se il device è connesso
>     ri-carica l'immagine in firmware via `NdkApplyImage` (l'immagine
>     vive fisicamente sulla tastiera, indicizzata per tasto — uno scambio
>     solo-locale lascerebbe il device fisico a mostrare le figure
>     pre-scambio); se non connesso, solo thumbnail + persistenza locale.
>   - **DisplayPad** (`K2.DisplayPad/MainWindow.xaml.cs`): stesso pattern
>     "Tag = oggetto modello direttamente" di MacroPad (`ButtonCell`), la
>     rotazione riordina i `Children` del `UniformGrid` ma sono sempre le
>     stesse istanze `Button` per indice fisico — nessuna traduzione
>     richiesta al momento del drag. Scambio: `ActionType`/`ActionValue`
>     diretti sui due `ButtonCell`, `ImagePath` invece passa da
>     `UploadAndPersist` per ciascun lato (re-invia l'icona al device
>     nella nuova posizione, rispettando la rotazione corrente via
>     `IconRotator.ResolveForUpload` già usata dal resto del file — anche
>     qui l'icona vive in firmware per indice bottone, non solo nel DB).
>   - **Verificato**: `build-check.bat` pulito su entrambe le solution
>     (0 errori/0 warning). **Da verificare dall'utente**: il gesto stesso
>     (trascinare un tasto/bottone configurato su un altro) sul MacroPad/
>     Everest Max/DisplayPad reali o anche solo in UI — questo ambiente
>     non ha un tool di automazione GUI per simulare un vero drag WPF, la
>     sola build pulita non prova che `DoDragDrop` si comporti come
>     previsto a runtime (in particolare: che il click semplice, senza
>     trascinamento, resti intatto su tutti e quattro i punti di innesto).
>
> Previous: 2026-07-13 (DisplayPad: import profilo Base Camp — un
> bottone "Back" senza icona propria nei dati BC ora riceve l'icona
> auto-generata invece di restare senza immagine):
>   - **Richiesta**: "quando viene importato un profilo di base camp, se è
>     presente un bottone back senza icone modificate, aggiungi di default
>     quella auto generata" — completamento della sessione precedente
>     (icona freccia+"Back" auto-generata per `dp_back`, vedi entry sotto):
>     lì la generazione avveniva solo per "Set as Back button" (menu tasto
>     destro, in-app) e per la Key #0 di default di una pagina senza alcuna
>     riga — un bottone "Back" che ARRIVA già così dai dati Base Camp
>     (XML o BaseCamp.db) restava senza icona, perché BC quasi mai porta un
>     `<base64Image>`/`Base64Image` reale per il suo bottone Back (spesso
>     solo chrome interno BC, non un'immagine decodificabile — vedi il
>     ramo "else: BC internal path" già esistente).
>   - **Fix — entrambi i percorsi di import**:
>     `MainWindow.DisplayPad.cs` (import XML, blocco `funcType == "Back"`)
>     e `BaseCampDbImporter.cs::ImportProfile` (import BaseCamp.db, blocco
>     `btn.FunctionType == "Back"`): se `imagePath` è ancora `null` dopo il
>     tentativo di decodifica dell'immagine BC, generano l'icona con
>     `IconImageGenerator.TryGenerateBackIcon(Loc.Get("dp_back"), ...)` —
>     stessa funzione introdotta per `DpMnuSetBack_Click` — e la assegnano
>     a `imagePath` prima del `SaveButton`/salvataggio su disco. Se BC
>     aveva invece fornito un'icona reale e decodificabile, `imagePath` è
>     già valorizzato a quel punto e il ramo non scatta: un'icona
>     effettivamente personalizzata nei dati importati non viene mai
>     sovrascritta.
>   - **Percorso file icona**: XML import riusa la stessa cache condivisa
>     hash-based di `DpMnuSetBack_Click`/`DpEnsureDefaultBackButton`
>     (`DpAutoIconCachePath("dpback", caption)`, in
>     `%LocalAppData%\K2.DisplayPad\auto_icons\`); l'import BaseCamp.db
>     (classe statica separata, senza accesso a quell'helper privato di
>     `MainWindow`) scrive invece nella stessa cartella `iconsDir` per-
>     profilo già usata per le altre icone importate
>     (`key_{btn}_back.png`/`key_p{page}_{btn}_back.png`), aggiunta
>     `using K2.Core;` per `IconImageGenerator`/`Loc`.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e
>     `dotnet build K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti
>     (0/0). **Da verificare su hardware/dati reali dall'utente**:
>     importare un profilo XML o BaseCamp.db con un bottone Back senza
>     immagine propria e confermare che compaia l'icona freccia+"Back"
>     invece del solo glifo "◂" di fallback.
>
> Previous: 2026-07-13 (DisplayPad: cambio pagina lasciava l'icona
> precedente sui tasti vuoti della pagina nuova — non venivano mai
> "sbiancati" sul pannello fisico):
>   - **Bug segnalato dall'utente**: "quando si cambia pagina, i pulsanti
>     vuoti non si refreshano e resta l'eventuale icona precedente".
>   - **Causa**: `DpReloadCurrentProfile`/`DpUploadPageForDevice`
>     (`MainWindow.DisplayPad.cs`) caricano le righe della pagina e
>     accodano un upload SOLO per i tasti che hanno un'immagine — un tasto
>     senza riga/immagine sulla pagina di destinazione non riceveva MAI un
>     comando di blank esplicito, a meno di `blankFirst: true` (un
>     `ResetPictures` a pannello intero, riservato agli switch di profilo
>     via `DpRequestRepaint`). La navigazione tra cartelle
>     (`DpNavigateToPage`/`DpNavigateBack`/`DpBgNavigateToPage`/
>     `DpBgNavigateBack`) chiama invece sempre `blankFirst: false` (un
>     `ResetPictures` ad ogni click sarebbe uno sfarfallio inutile per
>     quello che di solito è 1-2 tasti stantii) — quindi l'icona della
>     pagina precedente restava fisicamente sul tasto finché qualcosa non
>     lo sovrascriveva.
>   - **Fix**: entrambe le funzioni ora calcolano `toBlank` = l'insieme dei
>     12 indici tasto SENZA immagine sulla pagina appena caricata (a meno
>     che `blankFirst` o un'immagine fullscreen non coprano già tutto il
>     pannello) e mandano un blank per-tasto mirato (`DpClearKeyOnDevice`,
>     lo stesso già usato da "Remove action") per ciascuno, dentro la stessa
>     catena di upload in background — nessun full-panel `ResetPictures`
>     aggiuntivo, solo i tasti realmente vuoti vengono toccati. La griglia
>     UI (`_dpKeys`) era già corretta prima di questo fix (resettata a inizio
>     `DpReloadCurrentProfile`) — il bug riguardava solo il pannello fisico.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e
>     `dotnet build K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti
>     (0/0). **Da verificare su hardware dall'utente**: pagina A con
>     un'icona sul tasto #3, navigare su pagina B senza icona sul tasto #3,
>     confermare che il tasto #3 diventi nero sul pannello fisico invece di
>     mostrare ancora l'icona di A.
>
> Previous: 2026-07-13 (DisplayPad: Key #0 di ogni pagina/cartella
> torna "Back" di default, con icona freccia+didascalia auto-generata,
> personalizzabile via tasto destro — richiesta esplicita utente):
>   - **Richiesta**: il tasto in alto a sinistra (Key #0) di una pagina
>     DisplayPad deve di default essere un pulsante "Back" (freccia +
>     etichetta localizzata), anche quando la pagina arriva da un profilo
>     XML di Base Camp importato — con un modo per personalizzarne comunque
>     l'icona (tasto destro).
>   - **Stato pre-esistente**: l'azione `dp_back` esisteva già (menu tasto
>     destro "Set as Back button"/`DpMnuSetBack_Click`, gestione navigazione
>     in `OnDpKey`/`DpHandleBackgroundKey`), ma (1) non generava mai
>     un'icona — a differenza di `dp_folder` (vedi
>     `DpMnuCreateFolder_Click`/`IconImageGenerator.TryGenerateFolderIcon`),
>     lasciava il tasto con la sola label di fallback "◂"
>     (`DisplayPadKey.Display`); (2) nessuna pagina (creata in-app o
>     importata) riceveva mai un back-button di default sulla Key #0 — andava
>     impostato a mano ogni volta.
>   - **Fix — icona**: nuovo `IconImageGenerator.TryGenerateBackIcon(caption,
>     size, path)` (`K2.Core/IconImageGenerator.cs`), stesso schema di
>     `TryGenerateFolderIcon` (canvas nero, `DrawCaption` condiviso) ma con
>     un glifo "Back" di Segoe MDL2 Assets (U+E72B) tinto del colore accent
>     al posto del template cartella — riusa lo stesso `IconBox` per
>     allineare le due varianti di tile. `DpMnuSetBack_Click` ora lo chiama
>     (via `DpUploadAndPersist`, upload+persist live) quando il tasto non ha
>     già un'immagine propria — se ce l'ha, la lascia intatta (l'utente può
>     comunque sostituirla in qualsiasi momento con "Change image" dal menu
>     tasto destro, invariato).
>   - **Fix — default automatico**: nuovo
>     `DpEnsureDefaultBackButton(id, profile, pageId)`: no-op sulla pagina
>     radice (pageId 0, nessun "indietro" possibile) e se la Key #0 ha già
>     una riga in `Buttons` per quella pagina (azione impostata dall'utente O
>     dati Base Camp importati, incl. una riga esplicitamente "nessuna
>     azione" dopo un "Remove action" manuale — rispettata, non
>     ri-forzata); altrimenti genera l'icona e salva `dp_back` sulla Key #0.
>     Chiamato in testa a `DpReloadCurrentProfile` (tab foreground) e
>     `DpUploadPageForDevice` (pad in background) — **unici due punti** da
>     cui qualunque pagina viene letta/caricata (navigazione cartella,
>     switch profilo, pad non-foreground, E dopo un import XML/BaseCamp.db,
>     dato che una pagina importata passa comunque da uno di questi due
>     loader alla prima apertura) — quindi nessuna necessità di toccare
>     `BaseCampDbImporter`/l'importer XML separatamente: il default scatta
>     lazy alla prima volta che la pagina viene davvero mostrata,
>     indipendentemente da come è stata creata.
>   - **Stringhe**: nessuna nuova stringa necessaria — `dp_back`="Back"/
>     "Indietro" esisteva già in `Strings.xml`/`Strings.it.xml` (era definita
>     ma mai usata come didascalia fino ad ora).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e
>     `dotnet build K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti (0/0)
>     — `TryGenerateBackIcon` vive in `K2.Core`, compilato per entrambe le
>     piattaforme. **Da verificare su hardware dall'utente**: creare una
>     cartella, navigarci dentro, controllare che Key #0 mostri
>     l'icona freccia+"Back" sul pannello fisico; importare un profilo XML
>     con una sotto-pagina che non definisce la Key #0 e verificare lo
>     stesso; personalizzare l'icona via tasto destro > "Change image" e
>     confermare che resti quella scelta al prossimo caricamento pagina.
>
> Previous: 2026-07-13 (DisplayPad: il "bounce" di pressione tasto non
> partiva sui pad non-foreground con più DisplayPad collegati):
>   - **Bug segnalato dall'utente**: con 3 DisplayPad collegati, "l'effetto
>     animazione si attiva solo se è già stata selezionata la pagina del pad
>     corrispondente" — chiarito che "animazione" = l'effetto zoom/bounce che
>     rimpicciolisce l'icona a key-down e la riporta a piena dimensione a
>     key-up (vedi entry 2026-07-07 sotto per la sua introduzione).
>   - **Causa**: `DpHandleBackgroundKey` (`MainWindow.DisplayPad.cs`, percorso
>     per i DisplayPad NON foreground introdotto il 2026-07-09) aveva un
>     `if (!pressed) return;` esplicito con commento "background pads skip the
>     UI press-bounce visual" — il bounce era stato deliberatamente scartato
>     come fuori scope in quella sessione, perché `DpUploadPressVisual`
>     leggeva solo lo stato UI-bound del tab foreground (`_dpKeys`,
>     `_dpRotation`). Risultato: il bounce funzionava SOLO sul pad con la tab
>     aperta in K2 — esattamente il sintomo riportato.
>   - **Fix**: `DpUploadPressVisual` scomposto in un core device-agnostic
>     `DpUploadPressVisualForDevice(id, btnIndex, imgPath, rotation, pressed)`
>     che prende image path/rotazione come parametri invece di leggerli da
>     `_dpKeys`/`_dpRotation`; il vecchio `DpUploadPressVisual` ora è un thin
>     wrapper che passa lo stato foreground. `DpHandleBackgroundKey` non
>     ritorna più subito su key-up: per ogni matrice risolta cerca la riga
>     corrente (`_dpStore.LoadPage`) e chiama sempre il bounce device-agnostic
>     con `_dpStore.GetRotation(devId)` (sia su press che su release);
>     l'esecuzione dell'azione resta condizionata a `pressed && row is not
>     null`, invariata. Stessi guard di skip del path foreground (GIF animata,
>     fullscreen attivo) riusati via la funzione condivisa.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito
>     (0/0). **Da verificare su hardware dall'utente**: con 2+ DisplayPad
>     collegati, che il bounce compaia anche sui pad non ancora aperti come
>     tab in K2.
>
> Previous: 2026-07-13 (Gestione profili per Everest 60 e Makalu:
> persistenza SQLite nuova, riapplica-su-switch, import da BaseCamp.db,
> export XML — colma il gap "profilo non disponibile" rimasto per questi
> due device):
>   - **Design**: nessuno dei due device ha un vero profilo firmware (a
>     differenza di Everest Max/MacroPad che chiamano una `SwitchProfile`
>     nativa) — stesso identico caso già risolto per il DisplayPad
>     (`MainWindow.DisplayPad.cs::DpSwitchProfile`, commento esplicito sul
>     perché BC stesso non chiama mai una SwitchProfile nativa lì). Quindi:
>     profilo = concetto puramente lato-K2 (5 slot fissi, come tutti gli
>     altri device), "cambiare profilo" = leggere lo stato salvato per quello
>     slot e ri-mandarlo al device con le stesse chiamate HID/SDK già
>     esistenti.
>   - **Nuovi store** (stesso pattern `Settings` k/v + JSON blob di
>     `EverestStore`): `K2.App/Services/MakaluStore.cs` (`makalu.db`) —
>     lighting/DPI/settings come blob JSON per slot + tabella `Remap`
>     (Profile,ButtonIndex,FunctionName); `Everest60Store.cs`
>     (`everest60.db`) — lighting come blob JSON per slot + tabella
>     `KeyBindings` (Profile,LedIndex,Mode,Value,ModifierMask).
>   - **Wiring cattura+riapplica**: ogni handler "Apply" esistente
>     (`ApplyCurrentMkEffect`, `BtnMkDpiApply_Click`, `BtnMkRemapApply_Click`,
>     `ApplyCurrentEv60Effect`/`BtnEv60SideApply_Click`/
>     `BtnEv60CustomApply_Click`, `BtnEv60RemapApply_Click`) ora persiste lo
>     stato appena applicato nello store per lo slot corrente — persistenza
>     **incondizionata** (anche a device scollegato), così un profilo
>     modificato senza device collegato resta salvato. `Everest60KeyBindingPanel`
>     non aveva PRIMA nessuno stato locale (ogni Apply era una scrittura SDK
>     one-shot): aggiunto `Dictionary<int,(Mode,Value,Mask)> _bindings` come
>     unica fonte sia per la persistenza sia per il replay firmware.
>     `MkReloadProfile(slot)`/`Ev60ReloadProfile(slot)`/`Ev60ReloadKeyBindings(slot)`
>     (nuovi) leggono lo store e ri-applicano tutto all'hardware (se
>     connesso/sessione SDK aperta) — chiamati allo switch combo, all'init
>     modulo, e alla transizione disconnesso→connesso dei rispettivi poll
>     (`MkRefreshStatus`/`Ev60RefreshStatus`, più i 2 punti di apertura lazy
>     della sessione SDK Ev60). **Decisione esplicita**: lo switch profilo su
>     Everest 60 riscrive dal vivo l'intera mappa 64-tasti in firmware ma
>     **non chiama mai `SaveFlash` automaticamente** (resta dietro il
>     pulsante "Save" manuale, per non consumare cicli di scrittura flash ad
>     ogni switch).
>   - **UI**: sostituiti i due blocchi placeholder disabilitati
>     (`IsEnabled="False"`, tooltip `profile_not_available_tip`) in
>     `MainWindow.xaml` con controlli reali (`CbMkProfile`/`CbEv60Profile` +
>     rename/delete/import/export), copiati 1:1 dal blocco Everest Max e
>     ricollegati (`CbMkProfile_SelectionChanged`,
>     `BtnMkRenameProfile_Click`, `BtnMkDeleteProfile_Click`, e omologhi
>     Ev60 in `MainWindow.Everest60.cs`).
>   - **Import da BaseCamp.db**: `DeviceType` reale verificato (non
>     indovinato) contro il vero `BaseCamp.db` di riferimento del progetto
>     (`Mountain Base Camp/resources/bin/BaseCamp.db`, via `sqlite3`) —
>     Everest 60 = **`"EverestMini"`** (confermato: 1 profilo reale, 232
>     righe key-binding, 9 righe lighting — una per effetto — trovate);
>     Makalu = **`"Makalu"`** (nessun profilo Makalu mai esistito in questo
>     DB — inferito dalle proprietà di navigazione `Profile.MakaluLightings/
>     MakaluKeyBindings/MakaluSettings` e dal bridge RPC JS
>     `{'Class':'Makalu',...}` embedded in `BaseCamp.UI.dll`, non da un
>     campione reale). **Scoperta collaterale, fuori scope, segnalata non
>     corretta**: il MacroPad legge da tempo (codice pre-esistente,
>     `ReadMakaluBindings`/`ImportMacroPadProfile`) la tabella
>     `MakaluKeyBindings` — che nel DB reale è VUOTA (0 righe) — mentre i
>     dati reali del MacroPad vivono in `EverestKeyBidings` (12 righe
>     trovate, DLLMatrixIndex reali 8/17/26.../125, che NON combaciano con
>     `KeyIdToIndex` 170-179/220-221 già in uso) — il MacroPad ha già un
>     secondo path di lettura corretto su `EverestKeyBidings`
>     (`ReadMacroPadProfiles`), ma quello su `MakaluKeyBindings` sembra
>     dati/mai-eseguito. Non toccato in questa sessione (fuori scope), solo
>     verificato e segnalato.
>     `BaseCampDbImporter.cs`: nuove `ReadMakaluProfiles`/
>     `ReadMakaluMouseKeyBindings`/`ReadMakaluMouseLighting`/
>     `ReadMakaluMouseSettings`/`ImportMakaluProfile`,
>     `ReadEverest60Profiles`/`ReadEverest60KeyBindingsRaw`/
>     `ReadEverest60LightingRaw`/`ImportEverest60Profile` + `ParseBcColor`
>     (parser condiviso, i dati reali usano SIA `#RRGGBB` SIA `rgb(r, g, b)`
>     nella stessa tabella). **Lighting import ad alta confidenza**
>     (verificato contro dati reali); **Key Binding import best-effort**: le
>     uniche righe `IsKeyAssigned=1` viste nel profilo reale sono legende
>     factory `LayerType=3` ("FN + 10", non un vero remap utente) — nessun
>     campione reale di remap `LayerType=1` disponibile, quindi si importano
>     solo le righe il cui `FunctionValue` combacia esattamente con
>     `Everest60RemapData.KeyCatalog`, tutto il resto viene scartato (mai
>     indovinato).
>   - **Export XML**: nuovi `MkProfileExporter.cs`/`Ev60ProfileExporter.cs`
>     (mirror di `EvProfileExporter.cs`, stesso `ExportProfileHelper.Run`
>     condiviso) — modalità K2 (round-trip lossless, `FunctionType="K2Remap"`)
>     e Base Camp compatibile (vocabolario BC reale/dedotto, con skip+motivo
>     per ciò che non ha equivalente).
>   - Nuove stringhe EN+IT: `mk_no_profiles_in_bc`/`mk_imported_bc`/
>     `ev60_no_profiles_in_bc`/`ev60_imported_bc` (`Strings.xml`/`.it.xml`).
>   - Verificato con `dotnet build` pulito (rimossi bin/obj) su **entrambe**
>     le solution: **0 errori, 0 warning**. **Non verificato su hardware
>     fisico** (nessun Everest 60/Makalu disponibile in questa sessione) —
>     da provare: che lo switch profilo ridisegni davvero RGB/DPI/remap sul
>     Makalu e riscriva davvero i 64 tasti su Everest 60; l'import reale da
>     BaseCamp.db per un profilo Makalu vero (mai esistito finora) e per un
>     profilo Everest 60 con veri remap `LayerType=1` (mai visto finora).
>
> Previous: 2026-07-13 (Makalu: log box nella colonna destra spostato
> dietro Debug Mode, stesso gating già usato da Everest):
>   - `TxtMkLog` (box log Makalu, `MainWindow.xaml`) era sempre visibile,
>     unico device rimasto scoperto dal pattern "log solo con Debug ON" già
>     applicato a Everest (`GbEvLog`, `MainWindow.SectionNav.cs::ApplyDebugMode`).
>     Avvolto Separator+header+Border/TextBox in un nuovo `StackPanel
>     x:Name="PnlMkLog" Visibility="Collapsed"`, toggolato in
>     `ApplyMkDebugMode` (`MainWindow.Makalu.cs`) insieme a `PnlMkDebugGroup`
>     — nessun nuovo flag: riusa `AppSettings.DebugMode` già centralizzato
>     (checkbox "Debug Mode" nel tab General Settings →
>     `ApplyDebugModeToAllDevices` → `ApplyMkDebugMode`/`ApplyDebugMode`/
>     `ApplyMpDebugMode`/`ApplyDpDebugMode`/`ApplyEv60DebugMode`).
>     `LogMakalu()` non necessitava modifiche: scrive su `TxtMkLog` a
>     prescindere dalla sua Visibility (un controllo Collapsed esiste ancora,
>     semplicemente non è disegnato), quindi il log continua a popolarsi in
>     background e appare non appena si attiva Debug Mode.
>   - Verificato con `build-check.bat`: **0 errori, 0 warning** su entrambe
>     le solution. **Non verificato a schermo** in questa sessione.
>
> Previous: 2026-07-13 (Makalu: colonna DPI (Settings) allargata a metà
> pannello, livelli DPI in un'unica riga estendibile con entry "Level N /
> NNNNN DPI"):
>   - **Colonna DPI = metà pannello**: `SecSettings`'s `Grid.ColumnDefinitions`
>     in `MakaluRgbSettingsPanel.xaml` — la colonna destra (DPI) era
>     `Width="230"` fisso contro `Width="*"` a sinistra (Device settings),
>     quindi su finestre larghe la colonna sinistra si allargava molto più
>     della destra. Ora entrambe sono `Width="*"` → 50/50 sempre, a qualunque
>     dimensione finestra (richiesta esplicita utente).
>   - **DPI levels in una riga sola, estendibile**: `PnlMkDpiLevels` da
>     `WrapPanel` (andava a capo se non c'entravano tutti e 5) a
>     `UniformGrid Rows="1"` — con `Rows="1"` esplicito, `UniformGrid` calcola
>     da sé `Columns` = numero di figli, quindi la riga resta sempre unica e
>     ogni entry si allarga/restringe per occupare una quota uguale della
>     larghezza disponibile (non serve larghezza fissa sui pulsanti). Resta a
>     **5 livelli fissi** (il firmware Makalu non supporta un conteggio
>     variabile — `MakaluProtocol.SetAllDpi`/`GetDpi` sono cablati a 5 slot,
>     vedi `_PROJECT_MAP.md`): "estendibile" qui è la RIGA (si adatta alla
>     larghezza), non il numero di livelli — valori di default (400/800/1600/
>     3200/6400) lasciati invariati, da rivedere in una sessione futura
>     (richiesta esplicita dell'utente di rimandare quella scelta).
>   - **Entry allargate col contenuto giusto**: prima ogni bottone era
>     `Width=54 Height=40` con `Content="L1\n800"` (abbreviato, a stento
>     leggibile). Ora `BuildMkDpiButtonContent()` (nuovo helper in
>     `MakaluRgbSettingsPanel.xaml.cs`) costruisce un `StackPanel` con
>     "Level N" (riga muted, sopra) e "NNNNN" + "DPI" (numero in grassetto +
>     etichetta fissa a fianco, sotto) — stessa struttura a due righe dello
>     screenshot Base Camp fornito dall'utente in una sessione precedente.
>     Bottoni senza `Width` fisso (`Height=52`, si allargano da soli dentro
>     lo `UniformGrid`), `HorizontalContentAlignment="Left"` (testo allineato
>     a sinistra come nello screenshot, non centrato). `Foreground` ora
>     impostato esplicitamente per contrasto (`K2AccentTextBrush` sul livello
>     attivo, `K2TextBrush` sugli altri) — prima veniva lasciato al default
>     del tema, mai un problema quando il contenuto era testo semplice, ma
>     ora che è un albero di `TextBlock` con `Opacity` (non `Foreground`)
>     serve un `Foreground` esplicito sul `Button` perché i figli lo
>     ereditino.
>   - **Slider DPI ora a larghezza piena**: la riga slider+textbox era una
>     `WrapPanel` con `Slider Width="140"` fisso — sproporzionatamente corto
>     ora che la colonna è più larga. Cambiata in `DockPanel` (TextBox
>     `Dock="Right"` a larghezza fissa, Slider senza `Width` riempie il resto)
>     così segue la larghezza della colonna come "la barra" della richiesta
>     dell'utente.
>   - Rebuild bloccata la prima volta da MSB3027 (K2.App.exe di una sessione
>     precedente ancora in esecuzione, lock su `K2.Core.dll`) — killato il
>     processo (PID trovato via `Get-Process`, `stop-k2.bat` non l'ha
>     rilevato per qualche motivo) e rilanciato: **0 errori, 0 warning** su
>     entrambe le solution. **Non verificato a schermo/hardware** in questa
>     sessione.
>
> Previous: 2026-07-13 (seguito della sessione slider/bottoni segmentati:
> Speed degli effetti RGB → slider per TUTTI i device, Direction → bottoni
> segmentati per TUTTI i device anche quando le opzioni sono 4, non solo ≤3):
>   - **Nuovo `K2.App/Services/SegmentedButtonGroup.cs`**: helper condiviso
>     `Rebuild(UniformGrid, groupName, labels[], checkedHandler, selectedIndex)`
>     che ricostruisce un `UniformGrid` come riga di `RadioButton`
>     `K2SegmentedButton` — usato per i picker Direction, il cui numero di
>     opzioni cambia con l'effetto (Wave = 4 direzioni Right/Down/Left/Up,
>     Tornado = 2 CW/CCW): a differenza dei gruppi statici a 2-3 voci fatti
>     nella sessione precedente (dichiarati fissi in XAML), qui non si può
>     sapere a design-time quanti bottoni servono. Ogni bottone porta il suo
>     indice 0-based in `Tag`, letto dall'handler `Checked` del chiamante.
>     Richiede `GroupName` univoco per grid (il grouping di `RadioButton` in
>     WPF non è scoped al container).
>   - **`K2SegmentedButton` style**: aggiunto `Padding="10,0"` +
>     `MinWidth="40"` (mancavano nella sessione precedente — bottoni con testo
>     lungo tipo "Clockwise"/"Counter-CW" o "Silver"/"Digital" risultavano
>     troppo compressi). I `Border` dei gruppi DINAMICI (Direction) non hanno
>     più `Width` fisso: si auto-dimensionano al contenuto (il `UniformGrid`
>     senza vincoli di larghezza usa la `DesiredSize` del figlio più grande),
>     visto che il numero di segmenti varia. I gruppi STATICI della sessione
>     precedente con `Width` stretto (Keyboard Color, Clock Style) sono stati
>     allargati a 140px per lo stesso motivo.
>   - **Speed → Slider per tutti i device** (0-100%, tacche 25%, stesso
>     schema già in uso per Everest 60 — che quindi non è cambiato):
>     - **Everest Max** (`MainWindow.Everest.cs`): `CbEvSpeed` (5 voci "1 —
>       slow"…"5 — fast") → `SldEvSpeed`. Il valore letto da
>       `ApplyCurrentEffect` era già concettualmente 0-100 a 5 posizioni
>       (`SelectedIndex*25`, vedi commento preesistente "Speed: 5 UI
>       positions -> scale 0..100") — lo slider lo rende esplicito, nessun
>       cambio di significato per il firmware.
>     - **MacroPad** (`MainWindow.MacroLed.cs`): `CbMacroSpeed` → `SldMacroSpeed`,
>       stesso schema (mirror dichiarato di Everest, vedi commento di file).
>     - **Makalu** (`MakaluRgbSettingsPanel.xaml(.cs)`): **eccezione** — il
>       byte `param2` di `MakaluProtocol.SetLighting` è letteralmente
>       0/1/2 (Slow/Medium/Fast), NON una percentuale 0-100 come gli altri
>       device — quindi qui lo slider è a 3 tacche (0-2), non 0-100.
>       Sostituisce i bottoni segmentati Slow/Medium/Fast introdotti nella
>       sessione precedente (richiesta esplicita dell'utente in questa
>       sessione: "rendiamo anche la speed... uno slider per tutti i
>       dispositivi", Makalu incluso).
>   - **Direction → bottoni segmentati per tutti i device, incluse le combo
>     a 4 voci** (richiesta esplicita: "Fallo anche se la direzione ha 4
>     voci" — la sessione precedente le aveva escluse proprio perché il
>     conteggio varia oltre 3):
>     - **Everest Max**: `CbEvDirection` → `GridEvDirection` (dentro nuovo
>       `PnlEvDirection`, che ora si Collapsa quando l'effetto non supporta
>       la direzione — prima restava visibile solo disabilitato, la stessa
>       incoerenza già segnalata dall'utente per il MacroPad e corretta lì
>       in una sessione precedente). Campo `_evDirIndex` sostituisce
>       `CbEvDirection.SelectedIndex`.
>     - **MacroPad**: `CbMacroDirection` → `GridMacroDirection` (riusa
>       `PnlMpDirection`, già Collapsibile). Campo `_macroDirIndex`.
>     - **Everest 60**: `CbEv60Direction` → `GridEv60Direction` (nuovo
>       `PnlEv60Direction`). Campo `_ev60DirIndex`. Stessa sessione,
>       aggiunto anche `PnlEv60Speed` Collapsibile per coerenza (lo slider
>       Speed di Everest 60 prima restava visibile solo disabilitato).
>     - **Makalu**: `CbMkDirection` (←/→, solo 2 voci, sempre le stesse per
>       ogni effetto) era già bottoni segmentati dalla sessione precedente —
>       nessun cambiamento, non è un caso "4 voci dinamiche".
>   - Persistenza (`EverestStore`/Settings): `rgb.speed`/`macroled.speed`
>     cambiano semantica da indice 0-4 a percentuale 0-100 diretta (stesso
>     numero di stati, solo rinominato — un valore salvato da PRIMA di
>     questa sessione verrebbe reinterpretato come percentuale bassa invece
>     che come indice; effetto collaterale minore accettato, nessuna
>     migrazione scritta, per un tool locale single-utente non versionato
>     pubblicamente).
>   - Verificato con `build-check.bat`: **0 errori** su entrambe le
>     solution dopo ogni passaggio. **Non verificato a schermo/hardware**
>     in questa sessione (nessun ambiente grafico disponibile).
>
> Previous: 2026-07-13 (UI: Makalu Polling Rate → slider a 4 tacche,
> DPI slider a step non uniformi, e "bottoni segmentati" al posto di
> ComboBox/CheckBox con ≤3 voci nelle sezioni SECTIONS di tutti i device):
>   - **Nuovo stile condiviso `K2SegmentedGroupBorder`/`K2SegmentedButton`**
>     (`K2.Core/Themes/K2Theme.xaml`): riga orizzontale di `RadioButton`
>     dentro un `UniformGrid Rows="1"` avvolto in un `Border` con
>     `CornerRadius`+`ClipToBounds` (l'arrotondamento pill viene dal clip
>     del contenitore, non da un `ControlTemplate` per-segmento — più
>     semplice e già gestisce automaticamente 2, 3 o N segmenti). Ispirato
>     allo screenshot di Base Camp originale (Angle Snapping Off/On,
>     Lift-off Low/High) fornito dall'utente.
>   - **Ambito scelto dall'utente**: solo le ComboBox con **esattamente
>     ≤3 voci** dentro le sezioni SECTIONS (Lighting/DPI/Key Binding/
>     Settings) di ogni device — non le combo con selezione dinamica che
>     può superare 3 voci (es. `CbEvDirection`/`CbEv60Direction`/
>     `CbMacroDirection`: Wave usa 4 direzioni, Tornado 2 — stessa combo,
>     conteggio variabile, lasciata invariata), né le combo >3 voci fisse
>     (keycap style, layout tastiera, effetti RGB, categorie/funzioni
>     remap, rotazione MacroPad/DisplayPad).
>   - **Makalu** (`MakaluRgbSettingsPanel.xaml(.cs)`): `CbMkPolling` (4
>     valori fissi) → `SldMkPolling` slider a tacche, stesso pattern indice
>     0-3 → array già usato da `SldMkDebounce`. `CbMkSpeed`
>     (Slow/Medium/Fast) e `CbMkDirection` (←/→) → gruppi segmentati
>     `RbMkSpeed*`/`RbMkDir*`, backed da campi `_mkSpeedIndex`/`_mkDirIndex`
>     (prima erano `ComboBox.SelectedIndex`). `CkMkAngleSnap`/`CkMkLiftHigh`
>     (CheckBox singola) → `RbMkAngle*`/`RbMkLift*` a 2 voci (Off/On,
>     Low/High — **non** aggiunto "Custom" per il lift-off, il protocollo
>     reverse-engineered in `MakaluProtocol.cs` non lo supporta, richiesta
>     esplicita dell'utente di ignorare le feature non supportate invece di
>     fingerle). Nuove chiavi loc `makalu_setting_off`/
>     `makalu_setting_liftoff_low` (EN+IT, stessa sessione).
>   - **DPI slider a step non uniformi** (richiesta esplicita utente,
>     diversa dagli altri slider "a tacche fisse"): nuovo
>     `MakaluProtocol.QuantizeDpiTiered(int)` — step 50 sotto 4000, 100 fra
>     4000-10000, 500 sopra 10000 (ogni risultato resta comunque multiplo di
>     50, compatibile col firmware che accetta solo step-50 reali — è solo
>     la UI che arrotonda più "grosso" man mano che il DPI sale, come lo
>     slider di riferimento). Sostituisce `QuantizeDpi` (uniforme, ancora
>     usato per il clamp wire-level in `SetAllDpi`) sia nello slider DPI
>     principale (`SldMkDpi`) sia nello slider sniper
>     (`SldMkSniperDpi` in `MakaluDpiRemapPanel.xaml.cs`). **Limite noto**:
>     lo `Slider` WPF resta lineare in pixel (`Minimum`/`Maximum` fissi) —
>     solo lo *snapping* del valore è a tacche variabili, la spaziatura
>     visiva delle tacche non replica esattamente quella dello screenshot
>     Base Camp (che le disegna più dense in basso). Implementare tacche
>     visive non uniformi richiederebbe un `TickBar` custom, non fatto
>     (nessun `TickBar` esiste già nel `ControlTemplate` Slider condiviso —
>     nessuno slider K2 mostra tacche visive oggi, solo snapping invisibile).
>   - **Everest Max**: `CbSettingsKeyboardColor` (Silver/Black,
>     `MainWindow.Everest.cs`) → `RbEvKbColor*` (rimossi
>     `KeyboardColorChoice`/`_keyboardColorChoices`/
>     `InitKeyboardColorSelector`, ora ridondanti). `CbDialClockType`
>     (24h/12h) e `CbDialClockStyle` (Digital/Analog, entrambi in
>     `MainWindow.DisplayDial.cs`) → `RbDialClock12h/24h`/
>     `RbDialClockDigital/Analog`, backed da property calcolate
>     `DialClockTypeIndex`/`DialClockStyleIndex` (stesso ruolo di
>     `SelectedIndex` prima).
>   - Verificato con `build-check.bat`: **0 errori** su entrambe le
>     solution. **Non ancora verificato a schermo/hardware** in questa
>     sessione (nessun ambiente grafico disponibile) — da controllare al
>     prossimo avvio che i gruppi segmentati mostrino lo stato iniziale
>     corretto (specialmente Makalu, dove `_mkSuppress`/l'ordine
>     dichiarazione XAML è storicamente delicato, vedi note in
>     `_PROJECT_MAP.md` sul crash CLR dell'`IsChecked="True"` in XAML — qui
>     evitato impostando tutti gli `IsChecked` in `Init()`/code-behind, mai
>     in XAML, stesso pattern già in uso).
>
> Previous: 2026-07-13 (Makalu: sections riordinate/rinominate (Key
> Binding/Lighting/Settings, DPI assorbito in Settings), LED ring preview
> software-only implementato dietro l'immagine, e bug fix HID reale su
> hardware):
>   - **Sections**: ordine sidebar allineato a MacroPad/DisplayPad/Everest 60
>     — **Key Binding** primo (ora anche default all'avvio, vedi
>     `InitMkSectionNav` in `MainWindow.Makalu.cs`), **Lighting** secondo,
>     **Settings** ultimo. La vecchia sezione **DPI** a sé stante è sparita:
>     la sua lista livelli è migrata dentro Settings (colonna destra, layout
>     a 2 colonne come lo screenshot reale di Base Camp) — tutto il codice
>     DPI (campi + metodi) si è spostato da `MakaluDpiRemapPanel.xaml(.cs)`
>     (ora solo Key Binding/Remap, nome classe invariato per limitare
>     churn) a `MakaluRgbSettingsPanel.xaml(.cs)`. **Bug reale trovato e
>     risolto nello stesso passaggio**: la sovrapposizione "impostazioni
>     sovrapposte" lamentata dall'utente era perché `MakaluDpiRemapPanel.SecDpi`
>     partiva `Visibility` di default (Visible, nessun `Collapsed` esplicito
>     in XAML) e non veniva MAI nascosta finché l'utente non cliccava
>     esplicitamente DPI/Remap la prima volta — dato che RGB (sezione
>     default all'epoca) viveva in un `UserControl` DIVERSO
>     (`MakaluRgbSettingsPanel`), il meccanismo `_activeMkSection` condiviso
>     non la toccava mai. Fix strutturale: ogni sezione non-default ora ha
>     `Visibility="Collapsed"` esplicito in XAML, zero stati impliciti.
>   - **LED ring preview** (richiesta: "implementiamo il led preview
>     sull'anello intorno alla rotella/tasto dpi"): il Makalu non ha NESSUNA
>     capacità di readback HID per l'illuminazione (confermato due volte:
>     prima analizzando `devices/makalu67/controller.py` di BaseCampLinux —
>     comando lighting `0x0C` è write-only, a differenza di DPI `0x0B` che
>     ha un vero sub-comando GET `0x07` — poi in modo DEFINITIVO analizzando
>     con tshark una cattura USBPcap reale fornita dall'utente
>     (`_reference/usb_dumps/makaluled.pcapng`, Base Camp originale con vari
>     effetti applicati dall'utente stesso): ogni SET_REPORT lighting (0x0C)
>     è seguito da un GET_REPORT la cui risposta è SEMPRE `A0 01 00 00...`
>     (solo ACK, zero altrove) — nessun colore/effetto/luminosità viene mai
>     letto indietro, nemmeno da Base Camp stesso). Quindi il preview è
>     necessariamente **software-only**: rispecchia lo stato scelto nella UI
>     (`MakaluRgbSettingsPanel.PreviewChanged`/`GetPreviewState()`), non il
>     device reale — stesso limite di Base Camp, non una lacuna di K2.
>     Implementazione (`MainWindow.Makalu.cs`): l'area dell'anello nel PNG
>     è risultata un vero **cutout trasparente** (non pixel grigi/bianchi
>     dipinti, come una prima ispezione via soglia-di-luminosità aveva fatto
>     credere) — l'utente ha fornito `makalu67_light.png` di Base Camp
>     stesso (variante "off", senza rainbow pre-renderizzato) al posto del
>     vecchio `makalu67.png` (rainbow bakato nei pixel, ora tenuto come
>     `Assets/makalu_mouse_rainbow.png`, mostrato per Key Binding/Settings —
>     l'immagine si scambia in base alla sezione attiva via
>     `MkUpdateMouseImage`, chiamata da `MkSection_Changed`). L'anello vero e
>     proprio è quindi un `Border`/coppia di `Border` disegnati DIETRO
>     l'`Image` (nuovo `Canvas x:Name="CvsMkRingBack"` in `MainWindow.xaml`,
>     aggiunto PRIMA di `<Image>` nello stesso `Grid`), sagomato/posizionato
>     su misure native-pixel date a voce dall'utente via Photoshop
>     (`left=152 top=252 width=83 height=273 lineWidth=13 radius=38` su
>     364×809, poi corrette due volte: top -14px, height -8px). L'anello è
>     diviso in **due metà** (sinistra/destra, `_mkLedRingLeft`/
>     `_mkLedRingRight` dentro `_mkLedRingHost`) perché il Makalu fisico ha
>     8 LED indirizzabili singolarmente, 4 per lato (`MakaluProtocol.
>     SetLightingCustom`: LED0=top-left…LED3=bottom-left,
>     LED4=bottom-right…LED7=top-right) — richiesto esplicitamente
>     dall'utente dopo che la prima versione (un solo gradiente verticale
>     su tutto l'anello) ignorava su quale lato fosse fisicamente ogni LED. Un
>     `BlurEffect` sull'host ammorbidisce la giunzione centrale (specie ai
>     capi arrotondati). Per Custom (finestra `MakaluCustomRgbWindow`, ora
>     con evento `ColorsChanged` per il preview live) le due metà mostrano
>     davvero gli 8 colori indipendenti; per gli altri effetti condividono
>     lo stesso brush/animazione (simmetrici per natura, es. Rainbow).
>   - **Bug HID reale trovato e risolto** (non introdotto in questa
>     sessione, pre-esistente): l'applicazione effetti falliva su hardware
>     reale con log `[RGB] apply ... -> False` pur con device "found" e
>     "open" riusciti. Causa: `MakaluHidNative.FindDevice()` sceglieva la
>     prima interfaccia HID il cui path contenesse `"mi_01"`, ma
>     quell'interfaccia USB espone PIÙ collection HID separate
>     (`col01`...`col05`, ognuna un path/handle Windows distinto) e solo
>     una supporta davvero Feature Report da 64 byte — l'ordine di
>     enumerazione di Windows non è garantito stabile tra riavvii/riconnessioni
>     (funzionava il 2026-07-10, non più il 2026-07-13 senza alcuna modifica
>     di codice nel frattempo). Fix: `FindDevice` ora chiama `HidP_GetCaps`
>     su ogni candidato e accetta solo quello con `FeatureReportByteLength
>     >= 64`, deterministico invece che affidato all'ordine di enumerazione.
>   - Bug secondario trovato durante la stessa sessione: `MkUpdateMouseImage`
>     costruiva un `BitmapImage` da un `Uri` RELATIVO creato in codice
>     (`new Uri("Assets/foo.png", UriKind.Relative)`) — funziona in XAML
>     (dove la markup extension risolve la base automaticamente) ma fallisce
>     silenziosamente in code-behind. Fix: pack URI esplicito
>     (`pack://application:,,,/Assets/...`).
>   - **Non ancora verificato su hardware**: DPI/remap via HID erano già
>     `Verificato su hardware reale 2026-07-10` prima di questa sessione;
>     l'unica cosa confermata OGGI su hardware reale è l'applicazione
>     effetti RGB (fix sopra). Il ring preview è stato verificato solo
>     visivamente in K2 (nessun readback possibile per natura, vedi sopra).
>
> Previous: 2026-07-12 (Everest 60: numpad LED preview live ATTIVATO —
> mappatura dei 17 tasti trovata via cattura USBPcap reale, non indovinata;
> poll velocizzato a 120ms; rimossa l'etichetta testuale "Numpad: sinistra/
> destra" a favore del solo posizionamento grafico già esistente):
>   - **Richiesta utente**: dopo la conferma "adesso il numpad funziona,
>     proviamo a far funzionare il led preview" (vedi entry precedenti per
>     numpad-detect e LED-preview main board), il preview del readback
>     mostrava dati validi (`TryGetColorData=True` sempre) ma su indirizzi
>     "sconosciuti" — 84 slot fuori da `LedIndex`/`SideLedIndex` che si
>     accendevano seguendo lo stesso gradiente Wave della board, troppi per
>     essere solo il numpad (17 tasti) e quindi ambigui: rischio concreto di
>     cablare l'overlay su indirizzi sbagliati, cosa che le regole del
>     progetto vietano esplicitamente ("non indovinare il bit-layout").
>   - **Reverse engineering pulito**: l'utente ha catturato con USBPcap+
>     Wireshark una sessione di Base Camp originale, dipingendo i 17 tasti
>     del numpad UNO ALLA VOLTA (ordine noto, confermato dall'utente:
>     Num,7,4,1,0,/,8,5,2,*,9,6,3,.,-,+,Enter) più 5 LED dell'anello del
>     numpad stesso. Scritti 4 tool Python usa-e-getta in
>     `_reference/tools/` (`find_magic.py`, `find_ev60_reports.py`,
>     `dump_dev.py`, `track_changes.py` — quest'ultimo il decisivo: traccia
>     ogni CAMBIO di colore in ordine cronologico tra scritture successive
>     di Custom Map, isolando esattamente quale indirizzo si accende ad ogni
>     tasto dipinto, senza bisogno di indovinare offset). Confermato: Base
>     Camp usa lo STESSO comando `Begin(0x34)/Map(0x35)/End(0x36)` già noto
>     da `Everest60Protocol.SendCustom` — nessuna sorpresa sul formato,
>     solo sugli indirizzi. I 17 indirizzi del numpad cadono nei "buchi"
>     della stessa riga/colonna della board principale (es. 38-41 = i 4
>     slot liberi subito dopo Backspace=34 nella Riga 0 di `LedIndex`) —
>     il numpad condivide letteralmente lo schema di indirizzamento fisico
>     della board, solo più a destra. Progressione aritmetica pulita
>     (passo +21 per riga, stesso passo già visto nella board) per 14 dei
>     17 tasti; i due tasti "alti" (+, Enter, che occupano 2 righe) rompono
>     lievemente il pattern lineare (comportamento plausibile per tasti
>     che coprono più slot fisici).
>   - **Trovato anche**: l'anello dell'ECCESSORIO numpad (5 LED testati sul
>     bordo inferiore, indirizzi 181-185) è una zona DIVERSA dall'anello
>     della board principale (`SideLedIndex`, 126-169) — probabilmente un
>     anello perimetrale separato sul modulo numpad stesso. Non ancora
>     implementato (fuori scope della richiesta originale, solo 5 indirizzi
>     su un totale sconosciuto) — annotato per una sessione futura se
>     interessa.
>   - **Implementato**: `Everest60Protocol.NumpadLedIndex` (17 byte, stesso
>     ordine di `Everest60KeyboardLayout.Numpad`), aggiunto a
>     `KnownLedAddresses`; `MainWindow.Everest60.cs::OnEv60ColorsUpdated`
>     ora applica il colore live anche a `_ev60NumpadVisuals` (solo
>     readback — nessun path di scrittura/paint per il numpad, resta fuori
>     scope). **Verificato dall'utente su hardware reale**: "adesso funziona
>     il led preview su tutta la tastiera".
>   - **Poll velocizzato**: `Everest60LedColorPoller` da 300ms a 120ms
>     (stessa cadenza di Everest Max/MacroPad) — la cautela iniziale (SDK+
>     raw-HID insieme, mai verificato) è stata ampiamente confermata sicura
>     da una sessione di debug reale su hardware fisico senza contese
>     osservate. Rimosso anche il logging diagnostico per-tick (aveva già
>     esaurito il suo scopo, avrebbe solo intasato il log a 120ms).
>   - **UI cleanup su richiesta utente**: rimossa l'etichetta testuale
>     "Numpad: Sinistra/Destra/Non rilevato" (`LblEv60NumpadStatus` +
>     stringhe `ev60_numpad_attached*`/`ev60_numpad_left/right/none`,
>     eliminate da `Strings.xml`/`Strings.it.xml`) dalla colonna destra —
>     il posizionamento grafico del numpad (`ApplyEv60NumpadPosition` sposta
>     e specchia `CvsEv60Numpad`) resta invariato e continua a funzionare,
>     era solo la ridondante indicazione testuale ad essere rimossa.
>
> Previous: 2026-07-12 (Everest Max: aggiunto il check periodico
> aggancio numpad/Media Dock, allineandolo a Everest 60):
>   - **Richiesta utente**: "metti il listener del aggancio di numpad o
>     media dock anche su everest max. Mettilo un check all'apertura di k2,
>     uno all'apertura della tab del dispositivo e un check periodico se il
>     tab everest è selezionato".
>   - Il rilevamento esisteva già (`UpdateKeyboardLayout` in
>     `MainWindow.Layout.cs`, via `EverestService.MMDockPlugPosition`/
>     `NumpadPlugPosition`), ma veniva invocato solo all'apertura del driver
>     (`EvAutoOpen`/`BtnEvOpen_Click`) e dal refresh manuale
>     (`BtnEvRefresh_Click`) — nessun check all'apertura della tab né polling
>     periodico, a differenza di Everest 60 (`Ev60RefreshStatus`, poll 3s).
>   - Aggiunti in `MainWindow.Layout.cs`: `_evAccessoryPollTimer`
>     (`DispatcherTimer` 3s, stessa cadenza di `Ev60RefreshStatus`) +
>     `StartEvAccessoryPoll`/`StopEvAccessoryPoll`. **A differenza** del
>     poller di Everest 60 (che gira sempre, indipendentemente dalla tab
>     attiva), questo è gated alla tab Everest Max selezionata — richiesta
>     esplicita dell'utente ("se il tab everest è selezionato").
>   - `TcDevices_SelectionChanged` (`MainWindow.xaml.cs`) ora chiama
>     `UpdateKeyboardLayout()` + `StartEvAccessoryPoll()` quando si apre la
>     tab `"everest"`, `StopEvAccessoryPoll()` altrimenti. Siccome
>     `BtnSettingsTab_Click`/`BtnMacroTab_Click` impostano
>     `TcDevices.SelectedIndex = -1` (che fa uscire subito
>     `TcDevices_SelectionChanged` per `SelectedItem == null`, senza
>     raggiungere il branch che ferma il poller), `StopEvAccessoryPoll()` è
>     stato aggiunto anche lì esplicitamente. Timer fermato anche alla
>     chiusura finestra (`Closed` in `MainWindow.Everest.cs`).
>   - Check all'apertura di K2: già coperto, nessuna modifica necessaria —
>     `EvAutoOpen()` (chiamato da `AutoOpenDrivers` all'avvio) chiama già
>     `UpdateKeyboardLayout()`.
>   - Build pulita (`dotnet build K2.sln -c Debug -p:Platform=x86`, 0
>     errori/warning). **Non verificato su hardware fisico** (nessun device
>     Everest Max disponibile in questa sessione) — verificare che
>     collegare/scollegare numpad o Media Dock a caldo mentre la tab Everest
>     Max è aperta aggiorni il layout entro 3s.
>
> Previous: 2026-07-12 (Everest 60/Makalu: colonna destra continuava ad
> allargarsi — il fix "Auto" della sessione precedente era la causa, non la
> cura; rimossa anche la sezione Log di Everest 60):
>   - **Richiesta utente**: "su everest 60 la parte a destra continua ad
>     allargarsi. Lasciala a larghezza fissa (come gli altri dispositivi) e
>     togli la sezione del log" — dopo che la sessione precedente aveva
>     cambiato quella `ColumnDefinition` da `Width="210"` fisso a
>     `Width="Auto"` per risolvere lo stesso problema (colonna 8px più
>     stretta delle altre 3, per via del margin sottratto in una colonna
>     fissa).
>   - **Causa reale del "continua ad allargarsi"**: `Width="Auto"` fa sì che
>     WPF dimensioni la colonna sul contenuto più largo — e dentro quella
>     stessa colonna vive `TxtEv60Log`, una `TextBox` con
>     `TextWrapping="NoWrap"`, che vuole essere larga quanto la riga di log
>     più lunga mai scritta. Ogni nuova riga di log più lunga della
>     precedente allargava quindi l'intera colonna, da cui l'impressione di
>     "continua ad allargarsi" mentre l'app gira. Su Everest Max/MacroPad/
>     DisplayPad questo non succede perché il loro log di debug vive in una
>     colonna SEPARATA a larghezza fissa (`PnlMpDebugRight`/equivalente,
>     380px), non nella colonna "impostazioni generiche" comune a tutti.
>   - **Fix**: `ColumnDefinition Width="Auto"` → `Width="218"` fisso su
>     ENTRAMBI Everest 60 e Makalu (218 = 8px margin sinistro del Border +
>     210px larghezza desiderata del contenuto — lo stesso numero a cui si
>     risolveva "Auto" quando funzionava, ma senza il rischio di crescita
>     illimitata). Rimossa la sezione "Log" di Everest 60 su richiesta
>     esplicita: `TextBlock`/`Border`/`TextBox x:Name="TxtEv60Log"` tolti da
>     `MainWindow.xaml`, `LogEverest60()` in `MainWindow.Everest60.cs`
>     semplificato (restava comunque `App.WriteLog(...)` verso il file di
>     log applicativo — quello non è stato toccato, solo lo specchio a
>     schermo). Rimossa anche la stringa loc `ev60_log` (EN+IT, non più
>     referenziata). **Makalu ha lo stesso `TxtMkLog` con `NoWrap` nella
>     stessa colonna** — non ancora segnalato come problema dall'utente, ma
>     stesso identico rischio: il fix di larghezza fissa lo previene già,
>     la sezione Log di Makalu però NON è stata rimossa (non richiesto).
>   - **Lezione**: quando una colonna `Auto` include una `TextBox` con
>     `TextWrapping="NoWrap"` (o qualunque controllo a larghezza intrinseca
>     non limitata), la colonna non è mai davvero "sicura" a lungo termine —
>     va isolata in una colonna propria a larghezza fissa (come fanno le
>     altre 3 board) oppure il contenitore va vincolato con un `MaxWidth`.
>   - Build verificata pulita (0 errori/0 warning). **Verifica visiva su
>     hardware fisico non ancora fatta.**
>
> Previous: 2026-07-12 (Everest 60: LED preview CONFERMATO funzionante
> su hardware reale — fix HWND + retry di resilienza verificati end-to-end
> da log):
>   - Log reale mandato dall'utente dopo il fix di resilienza (vedi entry
>     sotto): `Ev60AutoOpen()` riesce al primo colpo con l'HWND reale
>     (`OpenUSBDriver(0x871274) -> True`, `APEnable=True EnableKeyFunc=True`),
>     poller partito subito (`[Ev60-POLL] started (sdk.IsOpen=True)`) e
>     **37/37 tick consecutivi** (~19s) con `TryGetColorData=True` — nessun
>     fallimento. `GetSubDeviceInfo(1) -> ok=True fwVer=259 position=2`
>     confermato ripetutamente ogni 3s (numpad a destra, coerente col
>     posizionamento fisico riferito dall'utente).
>   - **Nota minore, non bloccante**: nel log compaiono 2× `OpenUSBDriver(0x0)
>     -> False` a inizio avvio (da `Ev60RefreshStatus()`'s retry-open +
>     `QueryNumpadPosition`'s fallback, entrambi chiamati sincronamente dentro
>     `InitEverest60Module()` nel costruttore, quindi ancora prima che
>     `OnSourceInitialized` assegni l'HWND reale a `MainWindow._hWnd`) — due
>     tentativi sprecati ma innocui, il vero `Ev60AutoOpen()` da
>     `AutoOpenDrivers()` (con HWND reale) arriva poco dopo e funziona. Non
>     corretto in questa sessione (cosmetico, nessun impatto funzionale).
>   - **Entrambi i bug hardware originali di oggi (numpad non rilevato, LED
>     preview assente) sono quindi risolti e verificati su hardware fisico
>     reale**, stessa causa radice (HWND mancante) per entrambi.
>
> Previous: 2026-07-12 (Everest 60: numpad CONFERMATO funzionante su
> hardware reale dopo il fix HWND — estesa la stessa resilienza al LED
> preview + aggiunto logging diagnostico per-tick, ancora da confermare):
>   - **Richiesta utente**: "ok ora il numpad funziona, proviamo a far
>     funzionare il led preview" — primo fix di oggi (passare l'HWND reale a
>     `OpenUSBDriver`, vedi entry sotto) confermato su hardware fisico.
>   - **Rischio individuato senza nuovo log**: `Ev60AutoOpen()` prova
>     `_ev60Sdk.Open(_hWnd, ...)` una sola volta durante `AutoOpenDrivers()`.
>     Dal log reale della sessione precedente, `OpenUSBDriver` falliva le
>     prime 1-2 volte prima di iniziare a riuscire regolarmente — se il
>     singolo tentativo eager fallisce, `_ev60Sdk.IsOpen` resta `false` per
>     sempre: `QueryNumpadPosition` ha un fallback che apre/richiude una
>     sessione breve ogni 3s (per questo il numpad ora funziona anche così),
>     ma quel fallback NON lascia la sessione aperta — quindi
>     `Everest60LedColorPoller`, che richiede `_ev60Sdk.IsOpen` persistente
>     per partire, non veniva mai (ri)avviato anche dopo che il device
>     iniziava a rispondere.
>   - **Fix**: `Ev60RefreshStatus()` (già in poll ogni 3s per lo status di
>     connessione) ora ritenta anche l'apertura persistente
>     (`_ev60Sdk.Open(_hWnd, ...)`) finché non riesce, e alla prima riuscita
>     chiama `UpdateEv60LedPreviewActive` per far partire il poller se la
>     sezione Lighting è quella attiva — stessa logica di retry già
>     dimostrata funzionante per il numpad, applicata anche alla sessione
>     persistente invece che solo al fallback breve.
>   - **Aggiunto logging diagnostico** in `Everest60LedColorPoller` (tag
>     `[Ev60-POLL]`, start/stop + risultato `TryGetColorData` ad ogni tick),
>     prima assente — mirror del logging già esistente in `LedColorPoller`
>     per Everest Max/MacroPad (tag `[LED-POLL]`), che si è già dimostrato
>     decisivo per diagnosticare rapidamente lo stesso genere di problema.
>   - **Non ancora verificato**: build pulita locale, ma serve un nuovo test
>     dell'utente con l'Everest 60 fisico sulla sezione Lighting per
>     confermare che `[Ev60-POLL] tick#N TryGetColorData=True` compaia nel
>     log e che i tasti mostrino davvero il colore live.
>
> Previous: 2026-07-12 (Everest 60: trovata la causa reale per cui
> `GetSubDeviceInfo`/`APEnable`/`EnableKeyFunc` fallivano SEMPRE anche con
> `OpenUSBDriver -> True` e un Everest 60 fisico collegato — `OpenUSBDriver`
> veniva chiamato con `IntPtr.Zero` invece dell'HWND reale della finestra):
>   - **Richiesta utente**: dopo il crash fix (vedi entry sotto), l'utente ha
>     confermato "Il numpad ancora non compare" con l'Everest 60 fisicamente
>     sempre collegato, e ha mandato il log completo di un avvio reale.
>   - **Lettura del log reale** (prima volta con hardware fisico davvero
>     collegato in questa serie di sessioni): `Everest360_USB.dll` si carica,
>     `OpenUSBDriver` fallisce le prime 2 volte poi INIZIA A RIUSCIRE
>     regolarmente ogni poll da 3s (`OpenUSBDriver -> True`) — quindi il
>     device risponde davvero. Ma **`APEnable`/`EnableKeyFunc` tornano
>     SEMPRE `False`**, e di conseguenza `GetSubDeviceInfo(1)` torna sempre
>     `ok=False`, ad ogni singolo tentativo, anche subito dopo un Open
>     riuscito — a differenza di MacroPad/Everest Max nello stesso log, dove
>     le chiamate equivalenti (`EnableKeyFunc(true) -> True`,
>     `APEnable(false) -> True`, `SetSyncEffect`, ecc.) riescono sempre.
>   - **Causa reale**: `Everest60SdkNative.OpenUSBDriver(IntPtr hWnd)` (a
>     differenza di `EverestSdkNative.OpenUSBDriver()` per l'Everest Max, che
>     via metadata ECMA-335 non prende proprio l'HWND) **prende un HWND reale**
>     — confermato anche questo via lo stesso dump di metadata
>     (`_reference/Everest_SDK_signatures.txt`, non un'ipotesi). K2 lo
>     chiamava però sempre con `IntPtr.Zero`, e per di più da
>     `InitEverest60Module()`, eseguito nel COSTRUTTORE di `MainWindow` — cioè
>     PRIMA che `OnSourceInitialized` assegni l'HWND vero (`_hWnd`) alla
>     finestra. MacroPad/DisplayPad invece chiamano il proprio
>     `OpenUSBDriver(_hWnd)` da `AutoOpenDrivers()`, eseguito DOPO
>     `OnSourceInitialized` — Everest 60 era l'unico modulo a non seguire
>     questo ordine. Ipotesi (coerente con la scoperta CHANGELOG 2026-07-11
>     sull'`EV60MessageHandler`, una `NativeWindow` nascosta usata da Base
>     Camp per i messaggi di plug/unplug, messaggio `0x5401`): l'SDK ha
>     bisogno di un HWND vero per completare l'inizializzazione del proprio
>     message pump interno, anche se K2 non consuma quei messaggi (la
>     connettività resta pollata) — senza un HWND vero `OpenUSBDriver` torna
>     comunque `true` (nessuna validazione dell'argomento), ma lo stato
>     interno resta incompleto e ogni chiamata successiva silenziosamente
>     fallisce.
>   - **Fix**: `Everest60SdkService.Open` ora richiede un `IntPtr hWnd` reale
>     (salvato in un campo, riusato anche dal fallback open/close di
>     `QueryNumpadPosition`); `DoOpenAndInit` non è più `static` per poter
>     leggere quel campo. `MainWindow.Everest60.cs`: l'apertura eager è stata
>     spostata da `InitEverest60Module()` (costruttore) a un nuovo
>     `Ev60AutoOpen()`, chiamato da `AutoOpenDrivers()` subito dopo
>     `EvAutoOpen()` — stesso punto in cui MacroPad/DisplayPad aprono i propri
>     driver con l'HWND vero. Anche la Open lazy di `Ev60Section_Changed`
>     (visita della sezione Key Binding) ora passa `_hWnd`.
>   - **Non ancora verificato**: serve un nuovo giro di test dell'utente con
>     l'Everest 60 fisico per confermare che `GetSubDeviceInfo`/`APEnable`
>     tornino `True` ora che l'HWND è reale — build pulita locale (0 errori),
>     nessun crash, ma senza un HWND-dipendente confermato funzionante da
>     log reale ancora mancante quando scritta questa entry.
>
> Previous: 2026-07-12 (Everest 60/Makalu: colonna destra "impostazioni
> generiche" 8px più stretta delle altre 3 — fix ColumnDefinition):
>   - **Richiesta utente**: "la sezione delle impostazioni generiche sulla
>     destra è ancora troppo stretta rispetto a quello degli altri
>     dispositivi. Allinea" — dopo la sessione di unificazione layout qui
>     sotto, che aveva già portato quella colonna a `Width="210"` fisso
>     (matematicamente identico ai 190px bottone + 20px padding degli altri
>     device) ma il problema persisteva visivamente.
>   - **Causa reale**: il `Border` di quella colonna ha sempre avuto
>     `Margin="8,0,0,0"` (stesso valore su tutti e 5 i tab). Su Everest Max/
>     MacroPad/DisplayPad quella colonna è `Width="Auto"`, quindi WPF la
>     dimensiona includendo il margin del child (8 + 210 = 218px di colonna,
>     Border reso alla sua piena larghezza desiderata di 210px). Su Everest 60/
>     Makalu la colonna era invece fissa a `Width="210"`: il margin sinistro
>     dell'8px viene SOTTRATTO dallo spazio disponibile per il child
>     (`HorizontalAlignment` default = Stretch), quindi il Border veniva
>     renderizzato a soli 202px — 8px più stretto delle altre 3, non
>     rilevabile confrontando solo i numeri scritti in XAML (210 contro 210)
>     ma reale a runtime per via di come Grid tratta Auto vs colonne fisse
>     rispetto al margin del contenuto.
>   - **Fix**: cambiato `ColumnDefinition Width="210"` → `Width="Auto"` su
>     entrambi i tab (Everest 60 e Makalu), stessa identica dichiarazione
>     ora usata da tutti e 5 i device — nessun'altra modifica necessaria
>     (Border/Padding/Style dei bottoni già identici). Build verificata
>     pulita (0 errori/0 warning). **Verifica visiva su hardware fisico non
>     ancora fatta.**
>
> Previous: 2026-07-12 (CRASH FIX: K2 non partiva più — terza occorrenza
> del bug "evento XAML sparato durante InitializeComponent()", questa volta
> via Slider.Value invece di RadioButton.IsChecked):
>   - **Richiesta utente**: "Non mi parte più k2" — subito dopo la sessione
>     di layout qui sotto (vedi entry successiva "Layout dei 5 tab device
>     unificato").
>   - **Diagnosi**: Windows Event Log mostrava lo stesso identico crash già
>     documentato per Makalu il 2026-07-10 ("Fatal error. Invalid Program:
>     attempted to call a UnmanagedCallersOnly method from managed code",
>     stesso indirizzo IP in coreclr.dll ad ogni lancio — .NET fail-fast
>     generico, NON un vero `UnmanagedCallersOnly`, root-causato allora via
>     WinDbg+SOS come AV durante il parse BAML). Confermato che la causa era
>     nel lavoro non committato di oggi eseguendo `K2.App.exe` direttamente
>     da terminale (ambiente locale, non serve l'utente per riprodurre):
>     `git stash` di tutte le modifiche → l'HEAD committato partiva pulito
>     (Everest Max/MacroPad/DisplayPad aperti correttamente, nessun crash) →
>     `git stash pop` → crash riprodotto in modo deterministico, indipendente
>     dall'hardware fisico collegato (si verificava anche senza un Everest 60
>     reale connesso).
>   - **Causa reale**: spostando lo slider di luminosità di Everest 60/Makalu
>     dal pannello per-device alla barra condivisa in alto a destra
>     (`BrEverest60`/`BrMakalu` in `MainWindow.xaml`, vedi entry sotto), i
>     nuovi `SldEv60Brightness`/`SldMkBrightness` hanno `Value="100"`
>     esplicito — diverso dal default `0` di `Slider` — quindi WPF applica il
>     valore durante `InitializeComponent()` e spara `ValueChanged`
>     SINCRONAMENTE in quel momento (stesso meccanismo già noto per
>     `Minimum`/`Maximum` che coercono `Value`, qui innescato da `Value`
>     stesso). `SldEv60Brightness_ValueChanged`/`SldMkBrightness_ValueChanged`
>     (in `MainWindow.Everest60.cs`/`MainWindow.Makalu.cs`) chiamavano
>     incondizionatamente `Ev60RgbPanel.SetBrightness(...)`/
>     `MkRgbSettings.SetBrightness(...)` — ma questi due `UserControl` sono
>     dichiarati MOLTO più avanti nello stesso `MainWindow.xaml` (contenuto
>     dei tab Everest 60/Makalu, righe 1200+/1500+, contro la barra in alto a
>     righe 619+) e quindi ancora `null` in quel punto del parse →
>     `NullReferenceException`/AV non gestita durante il caricamento XAML →
>     crash immediato ad ogni avvio, prima ancora che il costruttore di
>     `MainWindow` eseguisse una sola riga del proprio corpo (bisezionato con
>     log temporanei che confermavano il crash PRIMA del primo `Init*Module()`
>     chiamato dal costruttore).
>   - **Fix**: `Ev60RgbPanel?.SetBrightness(...)`/`MkRgbSettings?.SetBrightness(...)`
>     (null-conditional), stesso principio difensivo già in uso altrove in
>     questo file (`if (LblEv60Brightness != null) ...` nella riga subito
>     sopra, che infatti non crashava). Verificato con lanci diretti ripetuti
>     dal terminale locale (ambiente non-sandbox): 0 crash, avvio completo,
>     Everest Max/MacroPad/DisplayPad operativi.
>   - **Lezione per il futuro**: il pattern "evento XAML-wired che spara
>     sincrono durante `InitializeComponent()` prima che un campo dichiarato
>     più avanti nello stesso file sia assegnato" NON è limitato a
>     `RadioButton.IsChecked="True"` o a coercizione `Minimum`/`Maximum` —
>     vale per QUALSIASI proprietà con un valore esplicito diverso dal
>     default del tipo, su qualunque controllo con un handler XAML-wired
>     (qui: `Slider.Value="100"` vs default `0`). Ogni volta che si sposta un
>     controllo con un handler che referenzia un altro elemento del file, va
>     verificato l'ordine di dichiarazione E se l'handler ha già un guard
>     null — non solo se il controllo spostato stesso ha `IsChecked="True"`.
>
> Previous: 2026-07-12 (Layout dei 5 tab device unificato: riquadro
> immagine ad altezza fissa, pannello opzioni elastico, Everest 60/Makalu
> allineati al pattern toolbar/brightness degli altri 3 device):
>   - **Richiesta utente**: il riquadro centrale (immagine device) doveva
>     avere altezza FISSA su tutti i device — non stiracchiarsi in verticale
>     come prima (`RowDefinition Height="*"`) — mentre il pannello opzioni
>     SOTTO l'immagine doveva invece crescere quando la finestra si allarga
>     in verticale. Altezza scelta su richiesta esplicita = quella che il
>     MacroPad raggiunge quando ruotato in verticale (Positioning 90°/270°):
>     canvas 510x370, ma `RotateTransform` scambia l'ingombro renderizzato in
>     370x510, quindi 510 + 8px padding per lato = **526px**, il valore ora
>     fisso su `RowDefinition` Row 0 di tutti e 5 i tab (Everest Max,
>     Everest 60, Makalu, MacroPad, DisplayPad). Row 1 (pannello sotto) è
>     passata da `Height="Auto"` + `Height` fisso in pixel sul `Border` a
>     `Height="*"` + `MinHeight` (stesso valore di prima) come pavimento, così
>     non collassa sotto la sezione più alta ma cresce col resto. Per Makalu
>     (mouse 190x422, più alto delle altre board 510x370) inizialmente era
>     stato aggiunto un `Viewbox StretchDirection="DownOnly"` per non
>     tagliarlo dentro gli originali 386px — poi rimosso perché con 526px
>     l'immagine ci sta comoda senza scalare (Grid semplice, come gli altri).
>   - **Seconda richiesta utente**: allineare Everest 60 e Makalu al layout
>     degli altri 3 device su due punti rimasti diversi:
>     1. La striscia "Connected"/pulsante Refresh in cima (era una
>        `RowDefinition Height="Auto"` sempre visibile) è stata spostata nel
>        gruppo **Debug** della colonna destra comune (`PnlEv60DebugGroup`/
>        `PnlMkDebugGroup`, `Visibility="Collapsed"` di default, stessa
>        posizione/pattern di `PnlEvDebugGroup`/`PnlMpDebugGroup`/
>        `PnlDpDebugGroup` — subito dopo il gruppo "Device"). La Row 0 in
>        cima è ora `Height="32"` fisso (come gli altri) con un `WrapPanel`
>        vuoto. Aggiunte `ApplyEv60DebugMode`/`ApplyMkDebugMode` (stesso
>        pattern di `ApplyDebugMode`/`ApplyMpDebugMode`/`ApplyDpDebugMode`),
>        chiamate da `ApplyDebugModeToAllDevices` in `MainWindow.Settings.cs`.
>        Il poll ogni 3s (`Ev60RefreshStatus`/`MkRefreshStatus`) continua ad
>        aggiornare label/colore anche a gruppo nascosto (Collapsed non ferma
>        il codice, solo il rendering).
>     2. Lo slider luminosità (dentro `Everest60RgbPanel`/
>        `MakaluRgbSettingsPanel`, unico per entrambi questi device — a
>        differenza di Everest Max che ne ha solo uno "globale" già in
>        cima) è stato spostato nella barra condivisa in alto a destra
>        (`BrEverest60`/`BrMakalu` in `MainWindow.xaml`, stesso meccanismo di
>        `BrEverest`/`BrMacroPad`/`BrDisplayPad`, switch di visibilità in
>        `TcDevices_SelectionChanged`/`BtnSettingsTab_Click`/
>        `BtnMacroTab_Click`). Differenza architetturale da tenere a mente:
>        Everest Max/MacroPad/DisplayPad non hanno una UserControl dedicata
>        (tutto lo stato RGB vive dentro `MainWindow`), mentre Ev60/Makalu sì
>        — quindi lo Slider è stato spostato in XAML ma la logica di apply
>        resta dentro le UserControl via una proprietà interna
>        `Brightness`/metodo `SetBrightness(value)` che MainWindow chiama
>        dal proprio `ValueChanged` handler (`SldEv60Brightness_ValueChanged`/
>        `SldMkBrightness_ValueChanged` in `MainWindow.Everest60.cs`/
>        `MainWindow.Makalu.cs`). Per Everest 60 il vecchio handler condiviso
>        `SldEv60Param_ValueChanged` (Speed+Brightness insieme) è stato diviso:
>        `SldEv60Speed_ValueChanged` resta nel panel (governa solo Speed), la
>        Brightness rimossa è quella che ora vive in MainWindow. La
>        Brightness "Custom" per il Key Lighting per-tasto
>        (`SldEv60CustomBrightness`) NON è stata toccata: resta dentro il
>        panel, è specifica della sezione Key Lighting, non un valore
>        "sempre applicato" come l'altra.
>   - Build verificata pulita (`K2.sln` x86, 0 errori/0 warning) dopo ogni
>     passo. **Verifica visiva su hardware fisico non ancora fatta** in
>     questa sessione — da controllare al prossimo avvio con i device
>     collegati (in particolare Makalu con mouse fisico più alto delle altre
>     board, e che il flusso Debug mode centrale mostri/nasconda
>     correttamente i due nuovi gruppi).

> Previous: 2026-07-12 (Everest 60: trovata e risolta la vera causa del
> "numpad non rilevato" — Everest360_USB.dll non era nemmeno cercata):
>   - **Richiesta utente**: "il numpad non viene rilevato" (dopo
>     l'implementazione dell'auto-detect via `GetSubDeviceInfo`).
>   - **Falsa pista iniziale**: prima ipotesi (mancava la sequenza di
>     warm-up `APEnable`/`EnableKeyFunc` prima di `GetSubDeviceInfo`,
>     rifattorizzata in `Everest60SdkService.DoOpenAndInit`, riusata sia da
>     `Open()` sia da `QueryNumpadPosition()`) — cambiamento ragionevole e
>     tenuto, ma NON la causa reale.
>   - **Causa reale, trovata leggendo il log runtime**: `Everest60SdkNative`
>     dichiara `[DllImport("Everest360_USB.dll")]` ma
>     `NativeDependencyResolver.BaseCampNativeDlls` (l'elenco delle DLL non
>     ridistribuibili che K2 sa cercare in un'installazione Base Camp)
>     conteneva SOLO `MacroPadSDK.dll`/`SDKDLL.dll` — un commento nel codice
>     diceva esplicitamente "Everest360_USB.dll non è nella lista: il modulo
>     Everest di K2 non lo usa", vero quando scritto (prima della sessione
>     Key Binding) ma mai aggiornato dopo. Il CLR quindi non chiamava mai il
>     resolver custom per questa DLL e finiva sul default OS search path
>     (mai next to l'exe, mai dentro un'installazione Base Camp) →
>     `DllNotFoundException` silenziosa, loggata solo come eccezione generica
>     senza alcun elenco di percorsi tentati (a differenza di SDKDLL.dll/
>     MacroPadSDK.dll, che invece già passavano dal resolver e mostravano un
>     log chiaro "NOT found. Paths tried: ...").
>   - **Fix**: aggiunta `"Everest360_USB.dll"` a `BaseCampNativeDlls` in
>     `NativeDependencyResolver.cs` (una riga) + commento aggiornato.
>     Verificato via smoke-test con log fresco: `[NativeResolver]
>     'Everest360_USB.dll' loaded from: C:\Program Files (x86)\Mountain Base
>     Camp\Everest360_USB.dll` — la DLL ora si carica correttamente.
>     `OpenUSBDriver -> False` nello stesso log non è un bug residuo: sulla
>     macchina di test non risultava collegato un Everest 60 fisico in quel
>     momento (i log mostravano invece un Everest Max attivo, con numpad
>     rilevato dal SUO meccanismo separato — `FW_EXTEND_INFO.byNumpadPlug`,
>     non `GetSubDeviceInfo`). **Da verificare**: test end-to-end con un
>     Everest 60 fisico collegato, per confermare che `GetSubDeviceInfo`
>     restituisca `position` 1/2 corretti una volta che `OpenUSBDriver`
>     riesce.
>   - **Nota di sessione**: durante questo lavoro si sono verificate diverse
>     collisioni di build (`InitializeComponent` mancante su file non
>     toccati in questa sessione, es. `ExportProfilesDialog.xaml.cs`) per
>     via di un'altra sessione Claude Code concorrente sullo stesso
>     repository che stava lavorando in parallelo (aggiunta del readback LED
>     live via `GetColorData2`/`Everest60LedColorPoller` — vedi entry
>     successiva). Bastava ripetere `build-check.bat`: non erano errori di
>     codice, solo `bin`/`obj` cancellati/ricreati da due build simultanee.
>
> Previous: 2026-07-11 (Everest 60: LED preview live attivato via
> `GetColorData2` — smentisce la precedente conclusione "nessun readback,
> solo software"):
>   - **Richiesta utente**: "Riusciamo ad attivare il led preview su
>     Everest 60?" — la mappa di progetto affermava fino a questa sessione
>     che il Key Lighting fosse "software-only, nessun readback live —
>     stesso limite noto della side ring", perché la lighting di questo
>     device passa da HID raw (Everest60Protocol/Everest60HidNative), non
>     dall'SDK vendor (i cui export di lighting sono struct opache mai
>     decodificate).
>   - **Scoperta**: quella conclusione riguardava solo la SCRITTURA. La
>     LETTURA è un export diverso e innocuo: `Everest60::GetColorData2(IntPtr,
>     ushort)` in `Everest360_USB.dll`, mai investigato prima. Confermato via
>     decompile di `BaseCamp.Service.exe`: il wrapper managed
>     `Everest60::GetColorData` alloca 576 byte non gestiti, chiama
>     `GetColorData2(ptr, 576)`, copia il buffer in un `FWColor[192]` se
>     l'esito è vero, libera la memoria — nessuno struct opaco coinvolto.
>     Confermato anche lato UI: `BaseCamp.UI.dll` ha
>     `EverestMiniController.GetColorData` ("EverestMini" = nome interno di
>     Base Camp per l'Everest 60, viste `Views/EverestMini/Mini60*.cshtml`),
>     un handler websocket che alloca lo stesso `FWColor[192]` e lo pubblica
>     in un loop `Thread.Sleep(300)` semplice, gated da un flag
>     `StartSyncColorForEv60` (settato da `SyncColorFlagChanged`) e da un
>     controllo "app non minimizzata" — **senza alcuna chiamata di
>     priming/warm-up** (a differenza dell'Everest Max, che richiede
>     `SetSyncEffect`+`EnableColorStream` prima che `GetColorData` funzioni;
>     coerente col fatto che `SetSyncEffect`/`EnableColorStream` non
>     compaiono nell'export list della classe `Everest60`). Il buffer è
>     indicizzato per INDIRIZZO HARDWARE LED firmware — la stessa
>     numerazione già portata in `Everest60Protocol.LedIndex`/`SideLedIndex`
>     per la scrittura (`SendCustom`), verificata da due fonti indipendenti
>     concordi in una sessione precedente.
>   - **Implementato**: `Everest60SdkNative.GetColorData2` P/Invoke +
>     costante `ColorBufferSize=576`; `Everest60SdkService.TryGetColorData`
>     (rispecchia 1:1 la sequenza alloc/call/copy/free decompilata, riusa
>     `EverestSdkNative.FWColor` già esistente); nuovo
>     `Everest60LedColorPoller` (mirror di `LedColorPoller` ma dedicato,
>     300ms — la cadenza reale di Base Camp, più prudente dei 120ms di
>     Everest Max/MacroPad viste le incertezze di convivenza sotto);
>     `MainWindow.Everest60.cs`: poller avviato/fermato in base alla
>     sezione Lighting visibile (`UpdateEv60LedPreviewActive`, stesso
>     schema di `UpdateEverestLedPreviewActive`/`UpdateMpLedPreviewActive`),
>     riusa la sessione SDK già aperta eager per Key Binding/numpad;
>     `OnEv60ColorsUpdated` traduce indice logico (0-63) → indirizzo
>     hardware via `Everest60Protocol.LedIndex` prima di leggere il colore.
>     **Dettaglio UX**: mentre il paint-mode di Key Lighting è attivo, un
>     tasto appena dipinto ma non ancora "Applicato" mantiene il colore
>     dipinto invece di essere sovrascritto dal (ancora vecchio) colore
>     hardware al prossimo tick — stesso schema del salto `IsHighlighted`
>     del MacroPad in `MainWindow.LedPreview.cs`, qui per un motivo diverso
>     (anteprima di paint invece di flash da pressione fisica).
>   - **Non verificato su hardware fisico** (nessun Everest 60 disponibile
>     nella sessione che ha scritto questo): in particolare la convivenza
>     tra la sessione SDK persistente (ora anche col poll colori ogni 300ms)
>     e le scritture HID raw della lighting (`Everest60Service`/
>     `Everest60HidNative`) resta il rischio noto già segnalato in
>     `Everest60SdkService`'s doc comment — nessuna nuova evidenza a favore
>     o contro, solo un nuovo consumatore dello stesso rischio esistente.
>     Solo build pulita verificata (`build-check.bat`, 0 errori).

> Previous: 2026-07-11 (Everest 60: rilevamento automatico numpad
> sinistra/destra via SDK — smentisce la precedente conclusione "nessun
> protocollo"):
>   - **Richiesta utente**: "riusciamo a rilevare dove si trova il numpad e
>     se è attivo?" — dopo che una risposta precedente in questa stessa
>     sessione aveva concluso (erroneamente, per ricerca incompleta) che
>     nessun protocollo esistesse.
>   - **Scoperta**: `Everest60::GetSubDeviceInfo(int, ref int, ref int)`
>     (mai investigato a fondo prima) è esattamente il meccanismo. Confermato
>     via decompile: `Everest60::Everest60GetSubDeviceInfo` chiama sempre
>     `GetSubDeviceInfo(1, ...)` (1 = sottodispositivo numpad, unico indice
>     mai usato per questa classe); i due output sono `fwVer` e `position`.
>     `Everest60Operations::GetEverest60NumPadStatus` (BaseCamp.UI.dll)
>     conferma la mappatura: `position==1`→sinistra, `position==2`→destra,
>     altro→assente. **Bonus**: esiste anche un meccanismo push (messaggio
>     Windows 0x5401 gestito da `EV60MessageHandler`, una `NativeWindow`
>     nascosta) — ridondante con il poll, non implementato in K2 (il poll
>     ogni 3s già esistente è sufficiente e più semplice). Nessun
>     checkbox manuale in Base Camp: è sempre stato automatico.
>   - **Implementato**: nuovo `Everest60SdkNative.GetSubDeviceInfo` P/Invoke,
>     `Everest60SdkService.QueryNumpadPosition()` (riusa la sessione SDK
>     persistente se già aperta per Key Binding, altrimenti apre/chiude una
>     sessione breve — mai tenuta aperta solo per il poll di stato), enum
>     `Ev60NumpadPosition` (None/Left/Right). `MainWindow.Everest60.cs`:
>     `ApplyEv60NumpadPosition` sposta `CvsEv60Numpad` a sinistra o a destra
>     di `CvsEv60Keyboard` dentro `SpEv60Layout` e specchia solo l'immagine
>     di sfondo (`BrushEv60NumpadBg.RelativeTransform`, non l'intero Canvas —
>     altrimenti il testo dei tasti risulterebbe speculare) — nessun asset
>     "numpad destra" esiste in Base Camp, riusato lo stesso
>     `everest60_numpad.png` specchiato.
>   - **Rimosso** il toggle manuale "Numpad attached" (checkbox +
>     `AppSettings.Everest60NumpadAttached`, ora dead code eliminato):
>     sostituito da uno stato di sola lettura auto-aggiornato ogni 3s
>     insieme al poll di connessione esistente (`Ev60RefreshStatus`).
>   - Build pulita, smoke-test avvio OK. **Non verificato su hardware
>     fisico** (nessun Everest 60 con numpad disponibile in questa sessione)
>     — da testare: valore reale di `position` per sinistra/destra/assente,
>     e se il posizionamento a destra (mai visto in nessun asset Base Camp)
>     è visivamente corretto.
>
> Previous: 2026-07-11 (Makalu 67: hotspot del mouse ripiazzati con
> coordinate pixel-misurate, allineate al diagramma numerato ufficiale):
>   - **Richiesta utente**: allineare gli hotspot cliccabili sull'immagine
>     Makalu ("Imposta i bottoni del mouse... esattamente nella stessa
>     posizione di questo screenshot e con gli stessi numeri") — screenshot
>     fornito è il diagramma di riferimento Mountain (stessa foto di
>     `Assets/makalu_mouse.png`, con badge numerati 1/2 top, 3 wheel, 5 sopra
>     4 sul lato, 6 sotto la wheel).
>   - **Root cause**: `MkHotspotPos67` in `MainWindow.Makalu.cs` era
>     "hand-estimated" (commento originale), mai misurato sui pixel reali —
>     in particolare 4 (back) e 5 (forward) erano invertiti rispetto al
>     diagramma (4 sopra/5 sotto invece di 5 sopra/4 sotto), e 3/6 erano
>     troppo in alto (sulla curva superiore del case, non sulla wheel/DPI).
>   - **Fix**: misurate le coordinate pixel esatte su `makalu_mouse.png`
>     (364×809) con una griglia overlay + crop zoomati (PowerShell +
>     System.Drawing), poi convertite nello spazio canvas dell'app (190×422,
>     scala 190/364≈0.522). Nuovi valori in `MkHotspotPos67`: `[1]=(62,100)`,
>     `[2]=(128,100)`, `[3]=(95,133)`, `[4]=(15,218)`, `[5]=(15,195)`,
>     `[6]=(95,238)`. Verificato visivamente renderizzando i cerchi hotspot
>     sull'immagine alla stessa scala usata dall'app: allineamento coerente
>     col diagramma di riferimento. **`MkHotspotPosMax`** (layout a 8 bottoni)
>     **non toccato** — nessun diagramma di riferimento equivalente visto per
>     quel modello, resta hand-estimated.
>
> Previous: 2026-07-11 (Everest 60: ordine sezioni come Everest Max +
> Keycap Style importato da Everest Max, applicato anche al numpad):
>   - **Richiesta utente**: "ordina le sezioni come in everest max (key
>     binding, lighting, settings)" e "importa da everest anche il tipo di
>     keycap e applicalo anche al numpad".
>   - **Ordine sezioni**: sidebar SECTIONS riordinata Key Binding → Lighting
>     → Settings (prima era Lighting → Key Binding → Settings), stesso ordine
>     di Everest Max. La sezione di default all'apertura del tab resta
>     "Lighting" (`RbEv60SecLighting.IsChecked` in `InitEv60SectionNav`), non
>     cambiata: aprire "Key Binding" per default avrebbe reso eager il
>     caricamento dell'SDK `Everest360_USB.dll` (mai verificato su hardware),
>     scelta deliberatamente conservativa non richiesta esplicitamente.
>   - **Keycap Style importato**: aggiunto `CbEv60KeycapStyle` alla sezione
>     Settings, stessi 4 valori di Everest Max (Normal/Translucent/Pudding/
>     ReversePudding, `KeycapStyleChoices`/`KeycapStyle` riusati as-is da
>     `MainWindow.KeycapAppearance.cs`). Adattamento necessario: l'Everest 60
>     non ha un readback LED live (nessun `GetColorData` equivalente) con cui
>     gli stili di Everest Max normalmente si "mescolano" — il segnale usato
>     al suo posto è il colore dipinto dall'utente nella sezione Key Lighting
>     (`Ev60RgbPanel.TryGetPaintedColor`, nuovo metodo). Riscritta
>     `ApplyEv60KeycapAppearanceToAllKeys` sul modello a due fasi di Everest
>     Max (baseline statica + overlay "live") invece dell'assegnazione diretta
>     precedente; nuovi `_ev60KeyVisuals`/`_ev60NumpadVisuals`
>     (`Dictionary<int, KeyVisual>`/`List<KeyVisual>`, riuso del record
>     `KeyVisual` di `MainWindow.KeycapAppearance.cs`) catturano il layer
>     `LedHalo` di ogni tasto via `FindName` sul template condiviso
>     `EverestKeyStyle`.
>   - **Applicato anche al numpad**: il numpad decorativo ora riceve la
>     stessa baseline di Keycap Appearance (colore base/testo + stile) dei
>     64 tasti principali — sempre nello stato "off" dello stile scelto,
>     dato che non è mai dipingibile (nessun protocollo per questo
>     accessorio, invariato).
>   - Build pulita (`build-check.bat`, 0 errori/0 warning), smoke-test avvio
>     K2.App locale OK. Rimosso `_ev60KeyboardButtons` (dizionario diventato
>     dead code dopo il refactor, sostituito da `_ev60KeyVisuals`).
>
> Previous: 2026-07-11 (Everest 60: Key Binding reale via Everest360_USB.dll
> + riorganizzazione sezioni Lighting/Settings):
>   - **Richiesta utente**: fondere Key Lighting/Side Ring dentro "Lighting",
>     creare le sezioni Key Binding e Settings (Game Mode/Keycap Appearance/
>     Layout/Sync across profiles), verificare se si può riconoscere il lato
>     di attacco del numpad.
>   - **Numpad sinistra/destra**: nessuna fonte (Base Camp, BaseCampLinux) ha
>     mai un comando di rilevamento — unico asset esistente `EV60_NumpadLeft.png`,
>     nessun `NumpadRight`. Il numpad si attacca solo a sinistra: nessun
>     selettore lato necessario, il toggle sì/no esistente è già corretto.
>   - **Key Binding — scoperta chiave**: `BaseCamp.Service.Helpers.Everest60`
>     (wrapper di `Everest360_USB.dll`) espone `ChangeKey(int,int)`/
>     `ChangeFnKey(int,int)`/`ChangeShortcutKey(int,int,int)`/
>     `SetSingleMacroContent`/`SetKeyCallBack` con parametri primitivi, NON
>     struct opache come gli export di lighting (`ChangeEffect` ecc. — quella
>     parte della nota architetturale originale resta valida, era solo
>     incompleta). Due sessioni di decompile (`BaseCamp.UI.dll`
>     `Everest60Operations.SetEV60KeyBingingInHW` + `GetEverest60KeyBindings_English`)
>     hanno confermato via IL letterale: entrambi i parametri di `ChangeKey`
>     sono `DLLKeyId` (255=reset/disabilita); `ChangeShortcutKey` usa bitmask
>     modificatori ctrl=1/shift=2/alt=4/win=8; `ChangeFnKey` riusa la STESSA
>     numerazione DLLKeyId del layer principale (distinto solo da `LayerType`);
>     `SetKeyCallBack` esiste identico a Everest Max/MacroPad. Estratta la
>     tabella completa DLLKeyId per i 64 tasti fisici (catalogo English/US,
>     `Everest60Operations.GetEverest60KeyBindings_English`), verificata per
>     corrispondenza posizionale 1:1 con l'ordine righe di
>     `Everest60KeyboardLayout.MainBoard` (già usato per l'illuminazione).
>     **Non confermati**: i codici numerici delle azioni media/OS per
>     `SetSingleMacroContent` (nessun catalogo enumerabile trovato nel tempo
>     a disposizione) — implementati con ordinamento segnaposto 1-7, chiaramente
>     marcati "unconfirmed" in UI e codice, pronti per la correzione quando si
>     troverà la fonte reale o si potrà fare una USB capture su hardware.
>   - **Nuovi file**: `Services/Everest60SdkNative.cs` (P/Invoke
>     `Everest360_USB.dll`, mirror di `EverestSdkNative.cs`),
>     `Services/Everest60SdkService.cs` (facade open-once, come `EverestService`
>     — non per-call come il path raw-HID esistente), `Services/Everest60RemapData.cs`
>     (catalogo DLLKeyId + tabella LED-index→DLLKeyId + costanti modificatori),
>     `Everest60KeyBindingPanel.xaml(.cs)` (UserControl, terza sezione SECTIONS
>     "Key Binding": clic su un tasto della tastiera già disegnata seleziona
>     la sorgente, poi si sceglie Remap Key/Fn Layer/Shortcut/Media e si
>     applica — scrittura diretta in firmware, NESSUN coinvolgimento di
>     `IActionHost`/`ButtonActionEngine`, stesso pattern del remap tasti
>     Makalu). SDK aperto lazy solo alla prima visita della sezione (non
>     eager all'avvio come il path raw-HID di lighting).
>   - **Limite noto non verificabile in questa sessione** (nessun device
>     collegato): se tenere aperta la sessione SDK (`Everest360_USB.dll`)
>     contemporaneamente alle brevi burst raw-HID per l'illuminazione causi
>     contesa sulla stessa interfaccia USB — commentato esplicitamente nel
>     codice, da verificare su hardware reale.
>   - **Riorganizzazione sezioni**: SECTIONS ora `Lighting` (RGB preset +
>     Side Ring + Key Lighting per-tasto, fuse in un solo pannello scrollabile
>     dentro `Everest60RgbPanel`, invece di 3 voci separate) → `Key Binding`
>     (nuovo) → `Settings` (nuovo: Keycap Appearance funzionante — colore
>     base/testo solo cosmetico sull'overlay on-screen, nessun blend con LED
>     live perché questo device non ha un equivalente di `GetColorData`;
>     Layout = ComboBox disabilitata con tooltip, **non implementata**:
>     nessuna fonte ha ancora un catalogo legende multi-lingua verificato per
>     questo layout a 64 tasti). **Deciso esplicitamente fuori scope** (con
>     conferma utente): Game Mode (nessun comando firmware per-device,
>     l'unica via sarebbe un hook globale di sistema — semantica diversa dal
>     vero Game Mode via firmware) e "Sync across profiles" (l'Everest 60 non
>     ha alcun cambio-profilo in K2 al momento, checkbox senza effetto).
>   - Build pulita (`build-check.bat`, 0 errori/0 warning) dopo un fix
>     (using mancante per `GetValueOrDefault` su `IReadOnlyDictionary` in
>     `Everest60KeyBindingPanel.xaml.cs`). Smoke-test avvio K2.App locale OK
>     (nessun crash XAML). **Key Binding non verificato su hardware fisico**
>     in questa sessione — da testare: apertura SDK in parallelo al path
>     raw-HID, `ChangeKey`/`ChangeFnKey`/`ChangeShortcutKey` reali, e i codici
>     media placeholder.
>
> Previous: 2026-07-11 (MacroPad: LED preview live — root-caused e risolto
> il bug "solo M1 si accende", VERIFICATO su hardware reale):
>   - **Richiesta utente**: "il led preview su macropad funziona, ma solo per
>     il tasto M1, riusciamo a estenderlo per tutti e 12 i tasti?"
>   - **Diagnosi**: il log runtime (`matrixToIndex=12 visuals=12 nonZero=12`)
>     mostrava che la mappatura wMatrix→tasto e i 12 overlay erano già a
>     posto — il `nonZero` era calcolato su tutto il buffer 126 slot di
>     `GetColorData`, non sui 12 indici reali, quindi non provava che i colori
>     arrivassero su tutti e 12 i tasti. Aggiunto un log per-tasto
>     (`M{n}(led=..)=RRGGBB`): confermava colore reale e cangiante solo su M1
>     (`led=8`), sempre `000000` su M2-M12 (`led=17,26,35,...,125`).
>   - **Root cause**: la nota del 2026-07-10 (vedi entry sotto) assumeva che il
>     codice `wMatrix` del `KEY_CALLBACK` (usato per riconoscere quale tasto è
>     stato premuto in fase di remap) fosse DIRETTAMENTE l'indice dell'array
>     colori di `GetColorData` — mai verificato indice-per-indice. Dump
>     completo dei 126 slot non-zero durante un effetto rainbow: i dati reali
>     stavano contigui agli indici **0-11** (due gruppi di colore, 0-5 e
>     6-11), non ai valori wMatrix. M1 "funzionava" per puro caso: wMatrix=8
>     cade comunque dentro il range 0-11 valido; gli altri wMatrix (17,26,...)
>     puntano a slot del buffer sempre a zero. Stesso tranello già visto per
>     Everest (VK code vs DLLMatrixIndex, due domini diversi), qui si era
>     ripetuto per analogia senza verifica reale.
>   - **Fix**: `MainWindow.LedPreview.cs::OnMacroPadColorsUpdated` non usa più
>     `_matrixToIndex` per leggere i colori — legge `colors[btnIndex]`
>     direttamente (indice = posizione fisica del tasto, M1=0..M12=11).
>     `_matrixToIndex` resta invariato per il suo scopo originale (identità
>     tasto premuto/remap), dominio indipendente e non toccato. Aggiornato il
>     commento in `Models/LedMatrixMapping.cs` (sezione MACROPAD) con la
>     spiegazione corretta.
>   - **Verificato su hardware reale 2026-07-11**: "funziona, perfetto" —
>     tutti e 12 i tasti mostrano ora il colore live nella preview.
>
> Previous: 2026-07-11 (Everest 60: tastiera interattiva a 64 tasti +
> numpad decorativo + editor LED per-tasto):
>   - **Richiesta utente**: portare l'Everest 60 allo stesso livello di
>     gestione "tastiera" dell'Everest Max — tasti generati da layout reale,
>     supporto numpad accessorio, "LED preview". L'utente ha allegato in
>     chat le due immagini piatte board/numpad; sono state trovate identiche
>     in Base Camp (`wwwroot/images/Everest60/keyboardv2.png` e
>     `numpadv2.png`) invece di provare a estrarle dalla chat.
>   - **Layout 64 tasti**: portato 1:1 da `BaseCampLinux/shared/ui_helpers.py`
>     `_build_kb60_layout()` (label + indice LED 0-63 + geometria), riscalato
>     da 0.82 (Tk) a scala nativa K2 (30px/2px). Indici LED verificati contro
>     `Everest60Protocol.LedIndex` (già in K2, stesso ordine) — cross-check
>     tra due fonti indipendenti. Nuovo `Models/Everest60KeyboardLayout.cs`
>     (`MainBoard` 64 voci paintable + `Numpad` stimato a mano, decorativo).
>   - **Numpad**: nessuna fonte (Base Camp, BaseCampLinux `has_numpad=False`,
>     schema DB `Everest60Settings`/`Everest60KeyBidings` senza campo plug)
>     ha mai avuto un protocollo LED/remap per l'accessorio — trattato come
>     puramente decorativo (`MatrixId=-1`, `IsHitTestVisible=False`), toggle
>     "Numpad attached" in `AppSettings.Everest60NumpadAttached`.
>   - **"Key Lighting"**: nuova terza sezione (`RbEv60SecKeys`/`SecKeys` in
>     `Everest60RgbPanel`) — paint mode per-tasto sui 64 tasti principali,
>     Applica invia via nuovo `Everest60Service.SetCustomKeys()` →
>     `Everest60Protocol.SendCustom` (già esistente, prima usato solo per
>     l'anello). Preview è software-only: nessun `GetColorData` equivalente
>     per questo device raw-HID. Stesso limite noto della side ring: Custom
>     mode indirizza tasti+anello insieme, applicare l'uno spegne l'altro.
>   - **XAML**: sostituita l'immagine 3D decorativa del tab con due
>     `Canvas`+`ImageBrush` (board 504×186 su `everest60_board.png`, numpad
>     154×186 su `everest60_numpad.png`, entrambi copiati 1:1 da Base Camp),
>     stesso pattern di `CvsEvKeyboard`/`keybg.png` dell'Everest Max. Riusa
>     `EverestKeyStyle` esistente (nessuno stile nuovo necessario).
>   - Build pulita (`build-check.bat`, 0 errori/0 warning, entrambe le
>     solution). **Non verificato su hardware fisico** in questa sessione
>     (nessun Everest 60 disponibile) — da testare: geometria dei 64 tasti
>     sovrapposta a `everest60_board.png`, paint mode → `SetCustomKeys`.
>   - Deciso esplicitamente FUORI scope (con conferma utente via
>     AskUserQuestion): niente multi-lingua ISO per il layout (un solo
>     layout ANSI-like, come BaseCampLinux), niente anteprima animata dei
>     preset RGB sulla tastiera on-screen, niente remap/azioni per-tasto
>     (protocollo firmware ancora ignoto — invariato).
>
> Previous: 2026-07-10 (Everest 60: stesso layout a 3 colonne di Makalu,
> applicate le stesse precauzioni fin da subito — nessun crash, 5/5 lanci OK):
>   - **Richiesta utente**: "continua con Everest 60" dopo commit del fix
>     Makalu.
>   - **Fatto**: stesso pattern esatto di Makalu. Nuovo `Everest60RgbPanel.xaml(.cs)`
>     (`UserControl`, figlio diretto di `MainWindow`, non annidato) che ospita
>     `SecRgb`/`SecSideRing` (contenuto identico ai due pannelli che prima
>     stavano inline in `MainWindow.xaml`, solo spostati). `MainWindow.xaml`:
>     `PnlEverest60` ristrutturato a 3 colonne — sidebar SECTIONS (RGB
>     Lighting/Side Ring), immagine `Assets/everest60_keyboard.png` al centro
>     (**solo decorativa, nessun hotspot cliccabile**: a differenza di Makalu,
>     per Everest 60 non esiste alcun protocollo di remap per-tasto — vedi nota
>     architetturale, firmware mai reverse-engineered), colonna destra
>     Profilo/Import/Export (disabilitati, stesso tooltip di Makalu)/Device
>     (Rinomina, funzionante via nuovo `AppSettings.Everest60DeviceName`, già
>     aggiunto in sessione precedente ma mai usato finora). `MainWindow.Everest60.cs`
>     riscritto come shell (sidebar/status/rename/log), stesso schema di
>     `MainWindow.Makalu.cs`.
>   - **Precauzioni applicate PRIMA di testare** (lezione della sessione Makalu,
>     non riscoperte da zero): `RbEv60SecRgb.IsChecked` impostato in codice
>     (`InitEv60SectionNav`, chiamato da `InitEverest60Module` dopo che
>     `InitializeComponent()` è già completo) — MAI `IsChecked="True"` in XAML.
>     Campo `_ev60Suppress` in `Everest60RgbPanel.xaml.cs` inizializzato a
>     `true` di default (non `false`), azzerato solo a fine `Init()` — anche se
>     i due `Slider` di questo pannello (`SldEv60Speed`/`SldEv60Brightness`,
>     entrambi `Minimum="0"` che combacia col `Value` di default 0, quindi
>     nessuna coercizione) non risultano a rischio dell'esatto bug trovato in
>     Makalu, costa nulla applicare la stessa difesa.
>   - **Verificato**: rebuild pulita (`rm -rf obj bin`) + **5 lanci consecutivi,
>     0 crash**, log applicativo regolare (~828 righe/run, tutti e tre i driver
>     MacroPad/Everest/DisplayPad aperti). `K2.DisplayPad.sln` (x64) verificato
>     pulito separatamente (non toccato da questa modifica, solo controllo di
>     non-regressione).
>   - **Da fare**: verifica su hardware Everest 60 fisico del nuovo layout
>     (RGB/Side Ring funzionavano già prima, solo la disposizione UI è
>     cambiata — rischio basso ma non testato con device reale in questa
>     sessione); persistenza cross-sessione; Import/Export/Profilo restano
>     disabilitati come per Makalu (nessuna persistenza multi-profilo per
>     questo device).
>
> Previous: 2026-07-10 (Makalu: ROOT CAUSE TROVATA E RISOLTA con WinDbg+SOS —
> NON era un bug del JIT/CLR x86, era un null-ref banale in due punti diversi.
> Layout a 3 colonne per Makalu ORA FUNZIONANTE, DPI/Remap riattivati, sidebar
> interattiva con hotspot sull'immagine del mouse):
>   - **Setup debug**: generato un dump completo (`DOTNET_DbgEnableMiniDump=1`,
>     `DOTNET_DbgMiniDumpType=4`, `DOTNET_DbgMiniDumpName=...` — attenzione:
>     vanno impostate con `$env:NOME = "valore"` in PowerShell, `set` è sintassi
>     cmd.exe e non propaga la variabile al processo figlio). Aperto in WinDbg
>     con `.symfix`/`.reload`/`.loadby sos coreclr`/`!analyze -v`.
>   - **Prima sorpresa**: `!analyze -v` decodifica l'exception record e mostra
>     un `ExceptionCode: c0000005 (Access violation)` — **non** un vero errore
>     interno del CLR — con indirizzo di lettura `0x00000100` dentro
>     `K2.App.MainWindow.MkSection_Changed+0xdc`, riga sorgente esatta:
>     `nameof(RbMkSecRgb) => MkRgbSettings.SecRgb`. Il messaggio generico
>     `Invalid Program: attempted to call a UnmanagedCallersOnly method from
>     managed code` che si vedeva su console era quindi **fuorviante** — solo
>     l'etichetta che .NET assegna in modo generico ad alcuni AV non gestiti
>     che avvengono in profondità nello stack nativo, non una descrizione
>     reale del fault.
>   - **Causa reale**: `RbMkSecRgb` ha `IsChecked="True"` nello XAML. WPF
>     imposta quella proprietà durante il parsing del BAML dentro
>     `MainWindow.InitializeComponent()`, e questo fa scattare l'evento
>     `Checked` **sincronamente, immediatamente** — ma `MkRgbSettings`
>     (l'elemento `<local:MakaluRgbSettingsPanel x:Name="MkRgbSettings"/>`,
>     dichiarato più avanti nello stesso file XAML, nella colonna centrale)
>     non è ancora stato assegnato da `Connect()` a quel punto (gli elementi
>     vengono cablati in ordine di apparizione nel documento). `MkSection_Changed`
>     legge quindi `MkRgbSettings.SecRgb` con `MkRgbSettings == null` → crash.
>     Deterministico al 100%, non un fenomeno raro o dipendente da timing/ASLR
>     (coerente con l'aver visto lo stesso crash "sempre" in ogni sessione di
>     test precedente che includeva questo pattern).
>   - **Fix #1**: tolto `IsChecked="True"` da `RbMkSecRgb` in `MainWindow.xaml`;
>     impostato invece `RbMkSecRgb.IsChecked = true;` dentro `InitMkSectionNav()`
>     in `MainWindow.Makalu.cs`, chiamata da `InitMakaluModule()` **dopo** che
>     il costruttore ha già eseguito `InitializeComponent()` per intero — a
>     quel punto tutti i campi `x:Name` sono garantiti assegnati.
>   - **Il crash persisteva ancora dopo il fix #1** (stesso messaggio generico,
>     ma stavolta rebuild pulita + nuovo dump). Rianalizzato con lo stesso
>     procedimento (`!analyze -v` su `k2crash2.dmp`): **stesso identico pattern**,
>     stavolta in `K2.App.MakaluDpiRemapPanel.SldMkDpi_ValueChanged`, riga
>     `TxtMkDpi.Text = dpi.ToString();`. Causa: `SldMkDpi` ha `Minimum="50"`
>     nello XAML; uno `Slider` appena costruito ha `Value=0` di default, quindi
>     impostare `Minimum="50"` forza la coercizione di `Value` da 0 a 50, il
>     che fa scattare `ValueChanged` — di nuovo, sincronamente, durante il
>     parsing del BAML di `MakaluDpiRemapPanel`, PRIMA che `TxtMkDpi`
>     (dichiarato dopo `SldMkDpi` nello stesso `WrapPanel`) sia assegnato.
>     **Stessa classe di bug, secondo punto indipendente.**
>   - **Fix #2** (più generale, non solo per questo caso specifico): cambiato
>     il default del campo `_mkSuppress` da `false` a **`true`** sia in
>     `MakaluDpiRemapPanel.xaml.cs` sia in `MakaluRgbSettingsPanel.xaml.cs`
>     (azzerato a `false` solo alla fine di `Init()`, che viene chiamato dopo
>     che `InitializeComponent()` di quel controllo è già completo) — così
>     QUALSIASI handler che controlla `_mkSuppress` all'inizio (pattern già
>     usato ovunque in questi due file) è automaticamente no-op durante tutto
>     il caricamento del BAML, non solo per i due casi trovati. Aggiunta anche
>     la guardia `if (_mkSuppress) return;` mancante in
>     `SldMkSniperDpi_ValueChanged` (stesso `Minimum="50"`, stesso rischio,
>     mai arrivato a crashare solo perché il flusso di esecuzione non ci era
>     ancora arrivato).
>   - **Verificato**: rebuild pulita (`rm -rf obj bin` su entrambi i progetti)
>     + **6 lanci consecutivi, 0 crash**, log applicativo che arriva
>     regolarmente all'apertura di tutti e tre i driver (MacroPad/Everest/
>     DisplayPad, ~820 righe di log per run). Il layout a 3 colonne per Makalu
>     (sidebar SECTIONS, immagine mouse con hotspot cliccabili per il remap,
>     colonna destra Profilo/Import/Export/Device) richiesto dall'utente
>     all'inizio di questa giornata **ora funziona correttamente**.
>   - **Rivalutazione della "teoria del nesting"**: la sessione di bisezione
>     precedente (stessa giornata) aveva concluso che annidare
>     `MakaluDpiRemapPanel` dentro un secondo `UserControl` "shell" fosse
>     intrinsecamente pericoloso (2 livelli di nesting di controlli custom).
>     Con la root cause reale ora nota, è plausibile che ANCHE quel crash
>     fosse lo stesso identico bug (la shell aveva la stessa combinazione
>     `IsChecked="True"` + `Checked="MkSection_Changed"` che referenziava
>     `MkDpiRemap.SecDpi` non ancora assegnato) — **non riverificato
>     esplicitamente**, quindi la nota nel codice tratta il nesting come
>     "rischio non confermato", non "sicuro". Se in futuro serve un'architettura
>     annidata, vale la pena ritestarla ora che si sa cosa cercare.
>   - **Lezione generale per il futuro**: qualunque `RadioButton`/`CheckBox`
>     con `IsChecked` impostato a un letterale in XAML, o qualunque `Slider`
>     con `Minimum`/`Maximum` che differisce dal `Value` di default, che ha
>     anche un handler (`Checked`/`ValueChanged`/...) collegato via XAML,
>     rischia di far scattare quell'handler **sincronamente durante
>     `InitializeComponent()`**, prima che elementi dichiarati più avanti nello
>     stesso file siano assegnati. Il fix generale è: non impostare quei valori
>     "di attivazione" in XAML se l'handler tocca altri elementi nominati —
>     impostarli in codice dopo che l'intera `InitializeComponent()` è
>     terminata, oppure guardare l'handler con un flag tipo `_mkSuppress`
>     inizializzato a `true` di default.
>
> Previous: 2026-07-10 (Makalu: crash-repro RIATTIVATO di proposito su
> richiesta utente, per debug con WinDbg+SOS — vedi entry sotto per lo stato
> attuale della build, NON è più lo stato "sicuro" descritto più sotto):
>   - **Richiesta utente**: dopo il resoconto della sessione precedente
>     (crash ricomparso, root cause non trovata, revert a stato sicuro), ha
>     chiesto esplicitamente di rimettere la configurazione che fa crashare
>     l'app, per indagarla lui stesso con WinDbg + estensione SOS.
>   - **Fatto**: ricreati `MakaluRgbSettingsPanel.xaml(.cs)` (identici alla
>     versione cancellata nella sessione precedente) e ripristinato in
>     `MainWindow.xaml`/`MainWindow.Makalu.cs` il layout completo a 3 colonne
>     (sidebar SECTIONS + immagine mouse con hotspot + colonna destra) con
>     `MkRgbSettings`/`MkDpiRemap` entrambi wired come figli diretti — la
>     stessa identica configurazione già confermata come reproducer nella
>     bisezione precedente. **Verificato che il crash si riproduce ancora**
>     (`Fatal error. Invalid Program: attempted to call a UnmanagedCallersOnly
>     method from managed code` dentro `Dispatcher.Run`, exit code 6) subito
>     dopo la build. Questo è quindi lo stato ATTUALE della working tree:
>     **NON è sicuro da lanciare per uso normale**, è lasciato così apposta
>     per il debug. Istruzioni WinDbg+SOS fornite in chat (non salvate qui,
>     sono indipendenti dal codice — vedi cronologia sessione se servono di
>     nuovo).
>   - **Per tornare allo stato sicuro** (RGB+Settings inline, DPI/Remap
>     disabilitati): vedi l'entry precedente per la procedura già fatta una
>     volta in questa stessa giornata — in breve, rimuovere
>     `MakaluRgbSettingsPanel.xaml(.cs)`, ripristinare RGB+Settings come XAML
>     inline in `MainWindow.xaml`/`MainWindow.Makalu.cs`, non referenziare più
>     `MkDpiRemap`.
>
> Previous: 2026-07-10 (Makalu: il layout "sezioni/immagine/impostazioni" fa
> ricomparire il crash fatale — DPI/Remap DISABILITATI di nuovo, root cause non
> trovata, Everest 60 non toccato per lo stesso motivo):
>   - **Richiesta utente**: dare a Everest 60 e Makalu lo stesso layout delle
>     altre tab (MacroPad/Everest/DisplayPad) — sezioni specifiche a sinistra
>     (RadioButton), immagine interattiva del device al centro, impostazioni
>     generiche (Profilo/Import/Export/Device) a destra. Per Makalu: sezioni
>     RGB/DPI/Remap/Settings separate (oggi DPI+Remap sono un unico blocco),
>     colonna destra con gli stessi gruppi del template (Profilo/Import/Export
>     disabilitati per ora — nessuna persistenza multi-profilo per questo
>     device — solo "Rinomina device" funzionante via nuovo
>     `AppSettings.MakaluDeviceName`/`Everest60DeviceName`).
>   - **Refactor preparatorio**: spostati `SectionTabStyle`/`K2SideActionButton`/
>     `K2SideActionAccentButton`/`K2SideActionCombo`/`K2SideGroupHeader` da
>     `MainWindow.xaml` (locali) a `K2.Core/Themes/K2Theme.xaml` (merged a
>     livello Application) — **poi ripristinati** (vedi sotto, esclusi come causa
>     ma non serviva più tenerli spostati una volta abbandonato il refactor).
>   - **Ricostruito il layout Makalu a 3 colonne** (sidebar SECTIONS + immagine
>     mouse con hotspot cliccabili per selezionare il tasto da rimappare,
>     posizioni stimate a occhio su `Assets/makalu_mouse.png` + colonna destra),
>     inizialmente come UserControl "shell" (`MakaluTabPanel`) che incapsulava
>     tutto (sidebar/immagine/RGB/DPI/Remap/Settings/colonna destra).
>   - **Il crash fatale del CLR già visto l'8 luglio è ricomparso**
>     (`Fatal error. Invalid Program: attempted to call a UnmanagedCallersOnly
>     method from managed code` dentro `Dispatcher.Run`) — stessa firma esatta.
>     Segue la sequenza di bisezione fatta in questa sessione (per intero, è
>     stato un pomeriggio di tentativi, riportata perché la prossima persona
>     che riprende questo lavoro NON deve rifare gli stessi test):
>     1. **Un solo `MakaluTabPanel` gigante** (sidebar+immagine+4 sezioni+colonna
>        destra, ~55 elementi nominati + ~28 handler) → crash.
>     2. **Split in 3 UserControl annidati** (`MakaluTabPanel` shell che
>        incapsula `MakaluRgbSettingsPanel` + `MakaluDpiRemapPanel`, 2 livelli
>        di nesting) → crash.
>     3. **Bisezione dentro la shell**: shell vuota (solo un TextBlock) →
>        stabile. Shell con sidebar+colonna destra (senza immagine né
>        contenuto sezioni) → stabile. + immagine/hotspot → ancora stabile.
>        + **`MakaluDpiRemapPanel` annidato dentro la shell** → **crash** (con
>        contenuto byte-identico a quello stabile l'8 luglio, unica differenza:
>        nesting a 2 livelli invece di 1).
>     4. **Eliminata la shell**: `MakaluRgbSettingsPanel` e `MakaluDpiRemapPanel`
>        come figli DIRETTI di `MainWindow` (niente più nesting a 2 livelli,
>        stesso pattern di `MakaluDpiRemapPanel` da solo l'8 luglio) → **crash
>        comunque**, sia col layout pieno (sidebar+immagine+colonna destra) sia
>        col solo layout minimale a singola colonna.
>     5. **Isolato l'ultimo fattore**: `MakaluDpiRemapPanel` DA SOLO (senza
>        `MakaluRgbSettingsPanel`) come figlio diretto → **stabile**.
>        `MakaluRgbSettingsPanel` DA SOLO (senza `MakaluDpiRemapPanel`) →
>        **stabile**. **ENTRAMBI insieme, come semplici fratelli diretti di
>        MainWindow, layout minimale identico a quello dell'8 luglio tranne
>        per il fatto che RGB+Settings sono ORA un UserControl invece di XAML
>        inline** → **crash**, riproducibile 4/4 volte, anche dopo
>        `rm -rf obj bin` e rebuild pulita completa (quindi non cache/staleness).
>     6. **Ripristinato l'ESATTO layout dell'8 luglio** (RGB+Settings come XAML
>        inline in `MainWindow.xaml`/`MainWindow.Makalu.cs`, SOLO
>        `MakaluDpiRemapPanel` estratto) → **crash comunque**, 3/3 volte, anche
>        con rebuild pulita — la stessa identica configurazione che l'8 luglio
>        era stata verificata stabile più volte, inclusa una sessione con
>        hardware reale collegato (MacroPad+Everest+DisplayPad), ora non lo è
>        più in questa sessione.
>     7. **Sospettato lo spostamento degli stili** (punto precedente,
>        `SectionTabStyle` ecc. in `K2Theme.xaml` invece che locali in
>        `MainWindow.xaml`) come causa residua — **ripristinati locali,
>        rebuild pulita, testato 3 volte → crash identico**. Escluso anche
>        questo.
>   - **Root cause NON trovata**. Il fatto che la configurazione del punto 6
>     (identica, byte per byte nella parte Makalu, a quella verificata stabile
>     l'8 luglio) ora crashi in modo riproducibile è la scoperta più
>     preoccupante della sessione: significa che la causa non è (solo) nel
>     contenuto/struttura del tab Makalu, ma in qualcosa che è cambiato
>     nell'ambiente/toolchain/stato del repository tra l'8 luglio e ora, mai
>     isolato. Ipotesi non testate per mancanza di strumenti adeguati in questo
>     ambiente: bug di cache dello stub table dei reverse P/Invoke a livello di
>     processo host .NET (non per-progetto, quindi non pulibile con `rm -rf
>     obj/bin`), comportamento diverso di un aggiornamento SDK/runtime
>     installato nel frattempo (`dotnet --list-sdks` andrebbe ricontrollato),
>     o un fattore ambientale del tutto estraneo al codice K2. **Serve un
>     debugger nativo (WinDbg + estensione SOS) attaccato al processo che
>     crasha per vedere l'istruzione/stub corrotto**: la bisezione a colpi di
>     build+lancio ha ormai raggiunto il suo limite di utilità su questo bug.
>   - **Deciso**: tornare allo stato sicuro noto — Makalu con RGB+Settings
>     inline (invariati) e **DPI/Remap disabilitati di nuovo** (stesso stato di
>     prima dell'8 luglio: `MakaluDpiRemapPanel.xaml(.cs)` resta nel repo,
>     protocollo/servizio pronti, ma non referenziato da `MainWindow.xaml`).
>     Verificato stabile 4/4 lanci consecutivi con rebuild pulita, log
>     applicativo che arriva regolarmente all'apertura driver MacroPad/Everest.
>   - **Everest 60 NON toccato** (il secondo device del task originale): dato
>     che il bug si è dimostrato meno prevedibile del previsto (rompe anche
>     configurazioni "innocue" già provate sicure), non ha più senso assumere
>     che l'assenza di codice DPI/Remap-like in Everest 60 lo renda
>     automaticamente sicuro senza testarlo — rimandato a quando si potrà usare
>     un debugger nativo o si sarà isolata la causa reale.
>   - **Materiale preparato ma non wired**: `K2.App/Assets/everest60_keyboard.png`
>     (foto Everest 60, da `Mountain Base Camp/.../Everest60/everest_60_home.png`,
>     stessa fonte usata per `makalu_mouse.png`) copiato ma non ancora inserito
>     in nessun tab. `K2.App/Services/MakaluRemapData.cs` (tabelle remap
>     condivise, estratte durante il refactor) resta in uso da
>     `MakaluDpiRemapPanel.xaml.cs`. `K2.Core.AppSettings.MakaluDeviceName`/
>     `Everest60DeviceName` (nuove proprietà per un futuro "rinomina device"
>     senza store SQLite dedicato) aggiunte ma non ancora usate da nessuna UI.
>   - **Da fare**: (1) capire la vera causa del crash con strumenti adeguati
>     prima di ritentare QUALSIASI modifica strutturale al tab Makalu; (2)
>     valutare se procedere con Everest 60 in una sessione separata, con test
>     più incrementali (un elemento alla volta, rebuild pulita ad ogni step) e
>     accettando che anche lì possa ripresentarsi lo stesso problema.
>
> Previous: 2026-07-10 (Makalu: aggiunta immagine device nel tab):
>   - **Richiesta utente**: "aggiungi la possibilità di vedere i dispositivi nella UI", con 3 PNG
>     allegati (mouse Makalu top-down + 2 pannelli device generici) e nota che sono reperibili anche
>     nel Base Camp decompilato.
>   - **Sorgente scelta**: invece di provare a salvare i PNG incollati in chat (nessun tool disponibile
>     per estrarre i byte grezzi di un'immagine incollata), usato l'equivalente già presente in
>     `Mountain Base Camp/resources/bin/wwwroot/images/makalu67.png` (foto top-down identica per
>     inquadratura al PNG incollato) — confermato dallo stesso utente come fonte valida alternativa.
>   - **Controllato prima di copiare alla cieca**: le due immagini "pannello generico" allegate
>     corrispondono quasi certamente a `mkd_bg.png` (già usato dal tab MacroPad) e a un `dkd_bg.png`
>     trovato in `K2.App/Assets/` ma MAI referenziato in nessun `.xaml`/`.cs` — sembrava un bug
>     ("DisplayPad usa lo sfondo del MacroPad invece del proprio"). Verificato `MainWindow.DisplayPad.cs`
>     riga 28-30: è una scelta di design ESPLICITA e documentata ("Canvas with device background
>     (mkd_bg.png, same graphic as MacroPad)"), non un bug — lasciato invariato. `dkd_bg.png` resta
>     un asset orfano (non toccato, nessuna richiesta di usarlo).
>   - **Fatto**: copiato `makalu67.png` → `K2.App/Assets/makalu_mouse.png`, aggiunto a
>     `K2.App.csproj` come `<Resource>` (stesso schema di `mkd_bg.png`/`keybg.png`/etc.). Tab Makalu
>     (`MainWindow.xaml`, riga status): la `WrapPanel` di stato è ora dentro un `DockPanel` con
>     l'immagine del mouse (72×72) a sinistra. Nessuna modifica a MacroPad/DisplayPad/Everest60 (non
>     evidenziato alcun gap concreto lì, evitato lavoro non richiesto).
>   - **Verificato**: `dotnet build` pulito su entrambe le solution (0 errori/0 warning); app lanciata
>     in locale senza crash/eccezioni nuove (stesso VEH pre-esistente e non correlato di SDKDLL.dll
>     osservato quando il processo viene terminato a forza da un timeout esterno, non un problema
>     introdotto qui).
>
> Previous: 2026-07-10 (Makalu: trovata root cause del crash fatale DPI/Remap, riattivati come MakaluDpiRemapPanel UserControl):
>   - **Contesto**: ripresa dell'indagine sospesa nella entry precedente (stesso giorno). L'utente ha
>     chiesto di capire come riattivare il pannello DPI/Remap del Makalu 67/Max e di indagare la causa
>     del crash fatale del CLR che lo aveva fatto disabilitare (v. entry sotto per sintomo/bisezione
>     originale: `Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code`
>     dentro `Dispatcher.Run`, isolato al tab Makalu ma con causa radice non trovata).
>   - **Codice pre-fix recuperato**: il commit che ha introdotto Makalu (`444b62b`) contiene già la
>     versione RGB+Settings-only; il codice DPI/Remap COMPLETO (739 righe) era però ancora recuperabile
>     da uno stash lasciato dalla sessione di bisezione precedente (`git stash list` → `bisect-full-session`,
>     `git show stash@{0}^3:K2.App/MainWindow.Makalu.cs`, i file nuovi di quella sessione erano stati
>     stashati come "untracked files"). Riportato in un ambiente locale (Windows, non sandbox — build e
>     lancio diretti via `dotnet build`/`./K2.App.exe`), il crash si riproduce ISTANTANEAMENTE anche
>     SENZA hardware collegato: il log si ferma dopo 5 righe (init pre-driver), coerente con quanto
>     già osservato nella bisezione originale.
>   - **Due ipotesi escluse con test diretti** (mai provati nella bisezione originale):
>     - `DOTNET_JITMinOpts=1` (forza il tier JIT più semplice, nessuna ottimizzazione) → crash identico.
>       Esclude un bug di miscompilazione del JIT ottimizzante sul codice IL del tipo.
>     - `DOTNET_ReadyToRun=0` (forza il re-JIT delle DLL WPF invece di usare le immagini R2R
>       precompilate) → crash identico. Esclude un'immagine R2R di WPF corrotta/stale come causa.
>     - (Provato anche il roll-forward a .NET 10.0.8 via `DOTNET_ROLL_FORWARD=LatestMajor`: non
>       verificabile su questa macchina, l'host **x86** a `C:\Program Files (x86)\dotnet` ha solo
>       runtime 8.0.x — .NET 10 è installato solo per x64. Rimane un test aperto se in futuro si
>       installa un runtime desktop x86 più recente.)
>   - **Root cause isolata**: `MainWindow.xaml` è un unico `Window` monolitico che ospita TUTTI i tab
>     device (MacroPad/Everest/Everest60/Makalu/Macro/Settings). WPF compila l'intero file in UN SOLO
>     metodo generato `IComponentConnector.Connect(int connectionId, object target)` — lo switch che
>     assegna ogni `x:Name` e aggancia ogni event handler. Nella build corrente (DPI/Remap ancora
>     disabilitati) quello switch ha già **328 case** (`K2.App/obj/x86/Debug/net8.0-windows/MainWindow.g.cs`).
>     Riaggiungere le ~18 elementi nominati + ~12 event handler di DPI/Remap direttamente nello stesso
>     `MainWindow.xaml` porta lo switch a **346 case** — e riproduce il crash. Spostando lo STESSO
>     markup in un secondo file compilato separatamente (un `UserControl` con il proprio `Connect()`,
>     stesso contenuto totale, nessuna riga di logica cambiata) il crash **sparisce**: l'app supera il
>     punto in cui moriva prima e apre regolarmente driver MacroPad/Everest/DisplayPad (verificato via
>     `K2.App.log`, centinaia di righe post-init contro le 5 di prima). Root cause quindi: un bug/soglia
>     del JIT x86 legato alle dimensioni/complessità del metodo `Connect()` generato da WPF per markup
>     molto grandi (non del codice C# scritto a mano, già escluso nella bisezione originale) — non
>     confermato su un caso minimale isolato, ma il fix strutturale (spezzare il `Connect()`) è
>     verificato empiricamente e riproducibile.
>   - **Fix applicato**: nuovo `K2.App/MakaluDpiRemapPanel.xaml(.cs)` — `UserControl` che ospita SOLO
>     il markup DPI+Remap+overlay di conferma (stesso contenuto di prima, spostato 1:1). Gli event
>     handler XAML (`Click`/`ValueChanged`/`SelectionChanged`/`KeyDown`/`LostFocus`) vivono nel
>     code-behind del nuovo file come thin forwarder verso `MainWindow` (proprietà `Host`, impostata da
>     `InitMakaluModule`), tutta la logica reale resta invariata in `MainWindow.Makalu.cs` (i 12 metodi
>     handler sono ora `internal` invece di `private` per essere richiamabili dal forwarder). `MainWindow.xaml`
>     referenzia il pannello con `<local:MakaluDpiRemapPanel x:Name="MkDpiRemap" Grid.Row="1"/>`
>     (nuovo `xmlns:local="clr-namespace:K2.App"` sul `Window`); i riferimenti agli elementi nominati in
>     `MainWindow.Makalu.cs` sono ora qualificati `MkDpiRemap.NomeElemento`. Grid righe del tab Makalu
>     tornate a 4 (RGB/DPI+Remap/Settings/Log) invece delle 5 con DPI e Remap separati.
>   - **Verificato in locale**: `build-check.bat` pulito (0 errori/0 warning su entrambe le solution).
>     App lanciata più volte senza hardware Makalu collegato (crash istantaneo prima, ora nessun crash);
>     lanciata anche CON MacroPad+Everest+DisplayPad reali collegati (stessa macchina di sviluppo): tutti
>     e tre i driver si aprono regolarmente, nessuna regressione visibile nei log.
>   - **NON verificato**: nessun Makalu fisico disponibile in questo giro — DPI/remap/sniper via HID
>     restano da testare su hardware reale (stesso stato "mai provato" della prima implementazione,
>     v. entry sotto). Le stringhe loc `makalu_dpi_*`/`makalu_remap_*` erano già presenti in
>     `Strings.xml`/`Strings.it.xml` da quando la feature era stata scritta la prima volta (mai rimosse
>     insieme al codice), quindi non serviva aggiungerne di nuove in questa sessione.
>   - **Da fare**: test hardware completo del pannello DPI/Remap Makalu; se confermato stabile, applicare
>     lo stesso pattern "UserControl separato" ad altre sezioni pesanti di `MainWindow.xaml` se in futuro
>     si osservano crash simili aggiungendo altre feature (es. l'editor RGB per-tasto pianificato per
>     Everest 60, v. `_PROJECT_MAP.md` step 5); capire se esiste un limite noto/documentato lato .NET
>     runtime per la dimensione del metodo `Connect()` generato da WPF (non trovato un riferimento
>     esterno in questa sessione, solo il comportamento riprodotto empiricamente).
>
> Previous: 2026-07-10 (Makalu: crash fatale isolato a DPI/remap, rilasciato RGB+Settings, DPI/remap disabilitati):
>   - **Contesto**: dopo l'implementazione MVP di Makalu (vedi entry sotto, stessa giornata),
>     l'utente ha segnalato "continua a non avviarsi, sembra crashare all'avvio" — K2.App
>     crashava SEMPRE all'avvio con Makalu abilitato, con o senza mouse collegato.
>   - **Sintomo**: crash fatale del CLR, non un'eccezione .NET normale — `Fatal error. Invalid
>     Program: attempted to call a UnmanagedCallersOnly method from managed code`, stack trace
>     dentro `MS.Win32.UnsafeNativeMethods.DispatchMessage` → `Dispatcher.PushFrame` →
>     `Dispatcher.Run` → `Application.Run` → `Main()`. Nessuna riga di log oltre l'apertura driver
>     MacroPad — il crash avveniva nel primo giro del message pump di WPF, dopo la costruzione
>     di `MainWindow` ma primissimo evento processato.
>   - **Bisezione** (>30 cicli build+lancio, `build-check.bat` + `Start-Process` con redirect
>     stderr, controllo `HasExited`): isolato con certezza al tab Makalu (con Everest 60 — aggiunto
>     nella stessa giornata — confermato pulito da solo). Escluso via `git stash`/ricostruzione
>     chirurgica con `sed`/awk uno-alla-volta:
>     - Rimuovere la sola `InitMakaluModule()` (codice C# non chiamato) → crash persisteva.
>     - `DOTNET_EnableWriteXorExecute=0` (workaround noto per bug JIT/W^X simili) → non risolve.
>     - Sostituire l'intera sezione XAML DPI+Remap+overlay con ~280 righe di codice INERTE della
>       stessa dimensione (40 metodi triviali, nessuna API WPF/P-Invoke) → **stabile**, quindi
>       NON è una questione di volume di codice/soglia dimensionale del tipo.
>     - Bypassare le chiamate P/Invoke reali (`_makalu.SetButtonRemap`/`SetButtonSniper`,
>       sostituite con `ok = false` letterale) → crash persisteva: non è l'HID P/Invoke.
>     - Sostituire i lookup `(Brush)FindResource(...)` nei metodi di evidenziazione pulsanti
>       dinamici con brush letterali → crash persisteva: non è FindResource.
>     - Disabilitare SOLO il gruppo `DispatcherTimer`+lambda del dialog di conferma remap
>       (`MkShowRemapConfirm`/`BtnMkRemapKeep_Click`/`BtnMkRemapRevert_Click`/`MkRemapRevert`)
>       → crash persisteva.
>     - Disabilitare SOLO `BuildMkRemapButtons` (loop di creazione dinamica pulsanti) → crash
>       persisteva.
>     - **Nessun singolo metodo, preso isolatamente, riproduce il crash da solo** — eppure
>       qualsiasi combinazione che include codice reale di DPI O Remap (anche mai eseguito,
>       solo presente/compilato nel tipo) lo causa, mentre RGB+Settings (sezioni comunque
>       corpose, con lo stesso pattern di creazione dinamica pulsanti in `BuildMkPresets`)
>       restano stabili da sole e insieme. Effetto combinatorio/di soglia mai pienamente isolato.
>     - Ipotesi principale non confermata: bug del JIT specifico per il processo x86 (vincolo
>       di piattaforma imposto da `MacroPadSDK.dll`/`SDKDLL.dll`), non un difetto di logica nel
>       codice C# scritto. **Causa radice non trovata.**
>   - **Decisione utente** (`AskUserQuestion`): rilasciare subito RGB+Settings (entrambe provate
>     stabili), disabilitare DPI e remap tasti invece di continuare l'indagine.
>   - **Fatto**: `MainWindow.Makalu.cs` riscritto da zero senza alcun riferimento a DPI/Remap
>     (nessuno stub, codice rimosso pulito). `MainWindow.xaml`: card DPI, card Remap e l'overlay
>     di conferma tolti da `PnlMakalu` (righe/`Grid.RowDefinitions` rinumerate, restano solo RGB/
>     Settings/Log). `Services/MakaluProtocol.cs`/`MakaluService.cs`/`MakaluHidNative.cs`
>     **NON toccati** — il livello protocollo/servizio non è mai stato implicato dalla bisezione,
>     resta lì pronto (metodi `GetDpi`/`SetAllDpi`/`SetButtonRemap`/`SetButtonSniper` inerti,
>     non referenziati da nessuna UI) per quando si riprenderà l'indagine. Riabilitata
>     `AppSettings.AutoStopBaseCamp` in `App.xaml.cs` (disattivata temporaneamente durante la
>     diagnosi, confermata NON essere la causa).
>   - **Verificato su hardware reale 2026-07-10**: K2.App avviato con successo 2 volte di fila
>     con Everest Max + MacroPad + DisplayPad + Makalu (RGB) tutti collegati — nessun crash,
>     driver aperti correttamente, effetti LED applicati. Prima verifica hardware reale sia per
>     Makalu (RGB) sia per Everest 60 (aggiunto nella stessa giornata, mai testato prima d'ora).
>   - **Da fare in una sessione futura**: capire la causa radice del crash DPI/Remap (magari
>     bisecando ulteriormente K2.App.csproj/impostazioni compilatore, o isolando su una minimal
>     repro app separata prima di reintrodurre il codice in K2); poi reintrodurre DPI e remap
>     tasti nella UI.
>
> Previous: 2026-07-10 (Nuovo modulo Makalu 67/Max — MVP tab RGB/DPI/remap/settings via raw HID, niente SDK):
>   - **Richiesta utente**: "Aggiungiamo il supporto a Makalu 67". Stessa strategia già validata
>     per l'Everest 60 in questa stessa giornata: nessun SDK/DLL vendor esiste affatto per questo
>     mouse (a differenza di MacroPad/Everest Max/DisplayPad), quindi niente da decompilare — solo
>     da portare un protocollo già reverse-engineered.
>   - **Trovato**: `BaseCampLinux/devices/makalu67/controller.py` ha già il protocollo COMPLETO
>     (non solo RGB come Everest 60) reverse-engineered da USB capture: illuminazione (preset +
>     custom 8-LED), DPI (5 livelli, get/set), polling rate/debounce/lift-off/angle-snapping,
>     remap tasti + DPI sniper. HID Feature Report puri, report id `0xA1`, 64 byte, interfaccia 1,
>     VID `0x3282` PID `0x0003` (Makalu 67) / `0x0002` (Makalu Max).
>   - **Decisione di architettura presa DURANTE l'implementazione** (piano iniziale in plan mode
>     proponeva HidSharp, un nuovo pacchetto NuGet): scoperto che il modulo Everest 60, aggiunto
>     poche ore prima nella stessa sessione, usa già un pattern HID raw via P/Invoke diretto
>     (`hid.dll`+`setupapi.dll`, niente reader thread, feature report sincrone) — riusato
>     IDENTICO per Makalu invece di introdurre una dipendenza NuGet nuova. Nessun impatto su
>     `DISTRIBUTION.md` (stesso motivo per cui Everest 60 non ce l'ha: `hid.dll`/`setupapi.dll`
>     sono di sistema, non ridistribuiti).
>   - **Nuovi file** (`K2.App/Services/`): `MakaluHidNative.cs` (P/Invoke, enumera interfaccia
>     `mi_01`), `MakaluProtocol.cs` (porting 1:1 di `controller.py`: enum `Effect`, lighting
>     preset+custom, polling/debounce/liftoff/angle, DPI get/set, remap+sniper), `MakaluService.cs`
>     (facade find-open-send-close per chiamata, stesso pattern di `Everest60Service`).
>   - **Nuovo tab "Makalu"** (`MainWindow.Makalu.cs` + sezione XAML in `MainWindow.xaml`, tag
>     `makalu`): stato connessione con rilevamento modello 67/Max (poll 3s), pannello RGB (Off/
>     Static/Breathing/RGB Breathing/Rainbow/Responsive/Yeti + speed/brightness/2 colori/direzione
>     solo Rainbow, pattern `CapsFor` condiviso con Everest/Everest60), finestra dedicata
>     `MakaluCustomRgbWindow` per l'editor 8-LED custom (click-per-LED, non canvas multi-select
>     come il riferimento Python — semplificazione deliberata), pannello DPI (5 livelli, slider+
>     entry, refresh dal device), pannello remap tasti (griglia dinamica 6 tasti su 67 / 8 su Max,
>     categorie Mouse/DPI/Scroll/Sniper — **dialog di conferma con countdown 10s e auto-revert
>     quando si rimappa il tasto sinistro**, per non bloccare l'utente fuori dal click, stessa
>     logica di sicurezza del riferimento Python), pannello impostazioni (polling rate/debounce/
>     angle snapping/lift-off), log dedicato.
>   - **NON incluso in questo MVP** (stessa scelta di scope di Everest 60): persistenza
>     cross-sessione dei parametri (stato solo in memoria, rimandata a dopo il primo test su
>     hardware reale), import profili Makalu da `BaseCamp.db` (`MakaluKeyBinding`/`MakaluLighting`/
>     `MakaluSetting`/`DPILevel`, già presenti nel decompilato ma non collegati).
>   - **Nuove stringhe** `tab_makalu`, `makalu_*` in `Strings.xml`/`Strings.it.xml` (EN+IT nella
>     stessa sessione, come da regola CLAUDE.md).
>   - **Verificato**: build pulite su `K2.sln` (x86) e `K2.DisplayPad.sln` (x64) dopo un fix di
>     accessibilità (costruttore di `MakaluCustomRgbWindow` doveva essere `internal`, non
>     `public`, perché prende un parametro di tipo `MakaluService` interno — CS0051, stessa
>     regola già in CLAUDE.md per i metodi facade). **NON verificato su hardware reale** (nessun
>     Makalu disponibile in questo ambiente) — da testare: enumerazione device (VID/PID/
>     interfaccia `mi_01` — se l'euristica è sbagliata NESSUN comando funzionerà), RGB (preset +
>     custom), DPI get/set, remap+sniper, polling/debounce/lift-off/angle-snapping.
>
> Previous: 2026-07-10 (Nuovo modulo Everest 60 — MVP tab + RGB via raw HID, niente SDK):
>   - **Richiesta utente**: "Aggiungiamo il supporto a everest 60". Decisione di scope discussa
>     PRIMA di scrivere codice (vedi `AskUserQuestion` in sessione): l'SDK ufficiale
>     (`Everest360_USB.dll`, classe `Everest60`, ~60 export) passa quasi tutte le struct come
>     `IntPtr` opachi — nessun layout noto, richiederebbe lo stesso lavoro di reverse-engineering
>     multi-sessione già fatto per `EffData`/`BlockData` dell'Everest Max. Il remap tasti/macro via
>     firmware non è MAI stato decodificato da nessuna fonte nota (nemmeno BaseCampLinux).
>   - **Trovato**: il progetto community `BaseCampLinux` (`devices/everest60/controller.py`) ha
>     già un protocollo di illuminazione RGB reverse-engineered e FUNZIONANTE via **HID Feature
>     Report** puri (interfaccia 2, VID 0x3282 PID 0x0005 ANSI/0x0006 ISO, magic bytes
>     `46 23 EA`), cross-validato con OpenRGB's MountainKeyboard60Controller. Scelto di portare
>     QUESTO protocollo invece dell'SDK: niente DLL non ridistribuibile, niente guessing di
>     bit-layout, coerente con la regola CLAUDE.md "sniff prima di decompilare — non indovinare
>     il bit-layout" (qui lo sniff l'ha già fatto la community).
>   - **Nuovi file** (`K2.App/Services/`): `Everest60HidNative.cs` (P/Invoke hid.dll+setupapi.dll,
>     stesso schema di `EverestHidNative`/`DpHidNative` già in uso per l'Everest Max nativo, ma
>     SENZA il reader thread overlapped — le feature report sono transfer di controllo sincroni:
>     `HidD_SetFeature`/`HidD_GetFeature`), `Everest60Protocol.cs` (porting 1:1 di
>     `controller.py`: enum Effect/ColorMode, tabelle direzione Wave/Tornado — i valori
>     combaciano con quelli già noti dell'Everest Max, riscontro incrociato tra le due
>     reverse-engineering indipendenti —, `SendMode`/`SendCustom`), `Everest60Service.cs`
>     (facade find-open-send-close per chiamata, nessuna sessione persistente necessaria).
>   - **Nuovo tab "Everest 60"** (`MainWindow.Everest60.cs` + sezione XAML dedicata in
>     `MainWindow.xaml`, tag `everest60`): stato connessione (poll ogni 3s), pannello RGB
>     (Off/Static/Breathing/Wave/Tornado/Reactive/Yeti, velocità, luminosità, 2 colori, rainbow,
>     direzione per-effetto Wave/Tornado — stesso pattern `CapsFor`/`UpdateCapabilities` già
>     usato per l'Everest Max), pannello Anello laterale (44 LED perimetrali, colore statico —
>     nota: attivarlo forza i tasti principali in modalità Custom/spenti, il device indirizza
>     l'anello solo così), log dedicato.
>   - **NON incluso in questo MVP** (deliberatamente, vedi scelta di scope sopra): remap
>     tasti/Fn-layer/macro (protocollo firmware ignoto), editor per-key RGB con overlay tastiera
>     (il metodo `Everest60Protocol.SendCustom` esiste già e supporta il caso, manca solo la UI di
>     paint), persistenza cross-sessione dei parametri RGB (Everest Max l'ha aggiunta in una
>     sessione successiva al primo cut — stesso percorso previsto qui).
>   - **Nuove stringhe** `tab_everest60`, `ev60_*` in `Strings.xml`/`Strings.it.xml` (EN+IT nella
>     stessa sessione, come da regola CLAUDE.md).
>   - **Verificato**: build pulite (0 errori/0 warning) su `K2.sln` (x86) e `K2.DisplayPad.sln`
>     (x64). **NON verificato su hardware reale** (nessun Everest 60 disponibile in questo
>     ambiente) — da testare: enumerazione device (VID/PID/interfaccia mi_02), invio feature
>     report, effetti che animano davvero, anello laterale.
>
> Previous: 2026-07-09 (MacroPad: CONFERMATO funzionante su hardware dopo il fix byAll/SwitchProfile/SaveFlash; rifinitura elenco effetti Reactive):
>   - **Conferma utente**: "Ottimo, funziona" — i 3 fix della entry precedente (byAll=0, SwitchProfile
>     prima dell'apply, SaveFlash(EffMenuIndex)) risolvono definitivamente gli effetti MacroPad su
>     hardware reale. Chiude l'indagine iniziata a inizio sessione.
>   - **Richiesta utente**: disattivare "Reactive B" dal combo effetti e rinominare "Reactive A" →
>     "Reactive", "Reactive C" → "Reactive Wave".
>   - **`MainWindow.MacroLed.cs`**: `MacroEffectList` — rimossa la voce `ReactiveB` (il firmware la
>     supporta ancora, `MacroPadService.Effect.ReactiveB` e il suo `MenuIndexFor`/`CapsFor` non sono
>     stati toccati: semplicemente non più selezionabile dal combo), label `ReactiveA`→"Reactive",
>     `ReactiveC`→"Reactive Wave".
>   - **Verificato**: build pulite (0/0) su entrambe le solution.
>
> Previous: 2026-07-09 (MacroPad: 3 bug CONFERMATI su cattura USB reale — byAll, SwitchProfile mancante, SaveFlash(profilo) — non più ipotesi):
>   - **L'utente ha fatto la cattura USB** (`_reference/usb_dumps/macropad.pcapng`): sequenza Static →
>     Breathing → Reactive → Matrix → Custom → Yeti → Off applicata da Base Camp reale. Parsato con
>     `tools/parse_usb_pcap.py`. Traffico su **bus=2 dev=7 ep=0x03** (dev=6 ep=0x05 è rumore di fondo,
>     pacchetti periodici "11 14"/"11 83 00 00 28" indipendenti dai click — quasi certamente heartbeat
>     dell'Everest connesso in parallelo, ignorato).
>   - **Struttura confermata byte-per-byte**: ogni cambio effetto è ESATTAMENTE 3 pacchetti da 64B:
>     1. `14 00 00 00 <profilo> <EffMenuIndex> 00...` — **SwitchProfile(profilo, EffMenuIndex, id)**.
>        Il 6° byte incrementa **0,3,4,5,6,7,8 nello stesso ordine dei click dell'utente**
>        (Static/Breathing/Reactive/Matrix/Custom/Yeti/Off) — coincide esattamente con l'enum
>        `Lighting.MenuIndex` decompilato la sessione scorsa. **Non è un artefatto del decompile: viene
>        davvero mandato, per OGNI effetto**, non solo quando l'utente cambia profilo esplicitamente.
>     2. `14 2C <EffData 62B>` — ChangeEffect. Offset [2..] del pacchetto = byte 0..61 di `EffData`:
>        confermano l'ordine campi (byEffectIndex, byAll, bySpeed, byLightness, byRandColor,
>        byDirection, byWidth, colorLv0/1/2, bkColor, byData) e **byDirection=0xFF/byWidth=0xFF sempre**
>        (già noto). **`byAll` è SEMPRE `0x00`** in tutti i 6 pacchetti non-Custom — il codice K2 lo
>        mandava **`1`** da sempre, mai verificato: bug concreto, primo sospettato per "ChangeEffect
>        torna true ma non succede nulla" (i pacchetti BlockData di Wave/Tornado, che GIA' funzionavano,
>        avevano per coincidenza `byAll=0` corretto fin dall'inizio — combacia perfettamente col fatto
>        che solo quelli funzionassero).
>     3. `13 55 00 00 <EffMenuIndex> 00...` — **SaveFlash(EffMenuIndex, id)**. Il 5° byte è lo stesso
>        MenuIndex del punto 1, NON la costante `6=ALL_PROFILE` che K2 mandava.
>   - **"Custom" (MenuIndex=6)** usa una sequenza diversa/più lunga (`14 2C 0A...FF...` +
>     `11 01 00 03 01 02...` + un secondo giro con più pacchetti) — conferma il ramo decompilato
>     `SetCustomLighting`, MAI implementato per il MacroPad in K2 (a differenza dell'Everest, che ha
>     "Custom Lighting per-key"): fuori scope per questo fix, resta un gap noto.
>   - **Fix `MacroPadSdkNative.cs::EffData.New`**: `byAll` 1 → **0**.
>   - **Fix `MacroPadService.cs::SetEffect`**: nuovo `MenuIndexFor(Effect)` (mappa Effect→EffMenuIndex,
>     stessi valori confermati sul wire; ReactiveA/B/C → 4, l'unica voce "Reactive" del menu BC).
>     Aggiunta chiamata **`SwitchProfile(profile, menuIndex, id)`** prima di ChangeEffect/
>     ChangeBlockEffect (mai chiamata da qui prima d'ora — decisione esplicita della sessione
>     precedente di NON aggiungerla, per mancanza di conferma; ora confermata). `SaveFlash` in
>     entrambi i rami passa `menuIndex` invece della costante `6`. Nuovo parametro `int profile = 1` su
>     `SetEffect`, valorizzato da `MainWindow.MacroLed.cs` con `CurrentProfile()` (combo profilo già
>     esistente).
>   - **Verificato**: build pulite (0/0) su entrambe le solution. **Da riverificare sull'hardware**:
>     Static/Breathing/Reactive/Matrix/Yeti/Off ora dovrebbero applicarsi davvero (questi 3 bug, in
>     particolare `byAll`, sono confermati sul wire — non più un'ipotesi). Nota: se `BaseCampService`
>     (vedi entry precedente) era ancora attivo durante QUESTA cattura, il confronto resta comunque
>     valido perché il capture legge il traffico REALE di Base Camp verso il device, indipendentemente
>     da cosa facesse K2 nel frattempo — ma **ripetere il test di K2.App con `BaseCampService`
>     davvero fermo** resta comunque necessario per isolare eventuali interferenze residue.
>
> Previous: 2026-07-09 (Nuova funzione "ferma automaticamente Base Camp all'avvio", attiva di default):
>   - **Richiesta utente**: K2 deve poter spegnere tutti i servizi/eseguibili che Base Camp apre
>     automaticamente, di default attiva ma disattivabile nei Settings.
>   - **`Services/BaseCampProcessGuard.cs`** (K2.App): nuovo metodo `KillAllBaseCampProcesses()` —
>     equivalente in-process di `stop-basecamp.bat`: ferma il servizio Windows `BaseCampService`
>     via `sc.exe stop` (best-effort, serve admin, fallisce in silenzio senza) poi killa ogni
>     processo che matcha i needles condivisi (`displaypadworker`/`basecamp`/`base camp`/`mountain`/
>     **`makalu`** — aggiunto ora, mancava e "Makalu Monitor.exe" non veniva mai preso), esclude
>     sempre processi con "k2" nel nome. Distinto da `KillDisplayPadWorkers()` (esistente, mirato
>     solo al worker DisplayPad per il conflitto col motore nativo).
>   - **`AppSettings.AutoStopBaseCamp`** (default ON, persistito in `app_settings.json`): nuovo flag
>     accanto a `KillBaseCampWorker`/`CloseToTray`/etc.
>   - **`App.xaml.cs` → `OnStartup`**: se `AutoStopBaseCamp` è ON, chiama `KillAllBaseCampProcesses()`
>     PRIMA di creare `MainWindow` (quindi prima che i moduli device aprano i driver) — solo in
>     K2.App (il guscio unificato usato quotidianamente), non in K2.DisplayPad standalone.
>   - **UI**: nuovo checkbox "Ferma automaticamente Base Camp all'avvio" nel tab Impostazioni
>     (`MainWindow.xaml`/`MainWindow.Settings.cs`), sopra il checkbox esistente "Termina worker
>     DisplayPad" — stringhe loc `settings_auto_stop_bc(_hint)` in `Strings.xml` (EN) e
>     `Strings.it.xml` (IT).
>   - **Verificato**: `build-check.bat` → entrambe le solution 0 errori/0 warning. Test su
>     hardware/comportamento reale (Base Camp che si riavvia da solo, servizio che resiste senza
>     admin) resta da fare dal vivo dall'utente.

> Previous: 2026-07-09 (Aggiunto controllo istanza singola per K2.App e K2.DisplayPad — bug trovato e fixato durante il test dal vivo: il MessageBox "già in esecuzione" non era visibile):
>   - **Richiesta utente**: impedire l'apertura di più istanze contemporanee di K2.
>   - **Implementazione**: `Mutex` con nome fisso (`K2App_SingleInstance_Mutex` / `K2DisplayPad_SingleInstance_Mutex`,
>     nessun prefisso `Global\`, quindi valido per sessione utente) creato all'avvio in entrambi `K2.App/App.xaml.cs`
>     e `K2.DisplayPad/App.xaml.cs`; nuova stringa loc `app_already_running` (EN in `Strings.xml`, IT in
>     `Strings.it.xml`).
>   - **Bug trovato testando dal vivo** (build in locale + `Start-Process` doppio + enumerazione finestre via
>     P/Invoke `EnumWindows`, dato che qui non c'è modo di vedere una GUI a schermo): la prima versione
>     chiamava `MessageBox.Show(...)` **dentro il costruttore di `App`**, prima che `Main()` chiamasse
>     `InitializeComponent()`/`Run()`. `MessageBox.Show` di WPF pompa il Dispatcher per il proprio loop
>     modale — e questo faceva scattare `OnStartup()` (quindi la creazione di `MainWindow`) mentre i
>     resource dictionary del tema (`K2Theme.xaml`, merged in `App.xaml`) non erano ancora caricati:
>     `MainWindow..cctor()` → `BuildRoundHotspotTemplate()` → `FindResource("K2HoverBrush")` falliva con
>     `ResourceReferenceKeyNotFoundException` (wrappata in `XamlParseException`), **non gestita** perché
>     anche `DispatcherUnhandledException` non era ancora agganciato a quel punto → crash silenzioso
>     (nessuna finestra, nessun log, processo sparito in ~1-3s). Confermato leggendo l'Application event
>     log di Windows (`Get-WinEvent -LogName Application`, provider ".NET Runtime", exception info completa).
>   - **Fix**: il costruttore ora si limita ad acquisire il mutex e salvare il risultato in un campo statico
>     (`_singleInstanceGranted`); il `MessageBox.Show` + `Shutdown()` sono spostati dentro `OnStartup()`
>     (per `K2.App`, dopo `base.OnStartup(e)`; per `K2.DisplayPad`, che usa `StartupUri` invece di creare
>     `MainWindow` a mano, l'`OnStartup` override salta del tutto `base.OnStartup(e)` se il lock non è
>     stato acquisito, evitando che WPF crei la finestra da `StartupUri`). A quel punto i resource
>     dictionary sono già mergiati e gli handler di eccezione già agganciati, quindi il dialog è sicuro.
>   - **Verificato dal vivo**: doppio `Start-Process` + `EnumWindows` → seconda istanza resta viva con una
>     finestra `visible=True` titolo "K2" (il MessageBox), invece di sparire senza traccia.
>   - **Secondo bug trovato dall'utente**: dopo il fix sopra, il **restart per cambio lingua**
>     (`MainWindow.Language.cs` → `RestartApp()`, agganciato a `Loc.RestartRequested`) smetteva di
>     funzionare — l'utente riceveva il dialog "già in esecuzione" e doveva riaprire K2 a mano.
>     **Causa**: `RestartApp()` lanciava il nuovo processo (`Process.Start`) e SOLO DOPO chiamava
>     `Application.Current.Shutdown()` sul vecchio — ma il vecchio processo tiene il mutex finché non
>     ha finito di spegnersi (non istantaneo), quindi il nuovo processo nasceva mentre il mutex era
>     ancora posseduto dal vecchio, si vedeva come "seconda istanza" e usciva subito, lasciando
>     l'utente senza nessuna istanza aperta.
>   - **Fix**: aggiunto `App.ReleaseSingleInstanceLockForRestart()` (rilascia+`Dispose()` il mutex)
>     chiamato in `RestartApp()` **prima** di `Process.Start`, cosi' il nuovo processo trova il mutex
>     libero fin da subito. **Verificato dal vivo** con UI Automation (`System.Windows.Automation`,
>     `InvokePattern` su `BtnLang` + MenuItem "Italiano"): il vecchio processo (pid X, log `lang=en`)
>     sparisce e un nuovo processo (pid Y, log `lang=it`, `K2.lang`="it") parte regolarmente con la
>     `MainWindow` reale (verificato cercando `BtnLang` nell'albero UIA della nuova finestra), non con
>     il dialog bloccato.
>
> Previous: 2026-07-09 (SCOPERTA IMPORTANTE — BaseCampService gira come servizio Windows di background e interferisce con TUTTI i test K2, `stop-basecamp.bat` non lo fermava mai per un bug nel nome):
>   - **Contesto**: dopo 3 round di fix "alla cieca" sul MacroPad (ChangeBlockEffect, EffData blittable,
>     EnsureSlotInitialized) senza alcun miglioramento osservato, l'utente ha chiesto se potessi gestire
>     io stesso Base Camp + cattura USB. Prima di rispondere ho controllato `K2.App.log` (esiste, da un
>     run recente dell'utente) per capire lo stato reale.
>   - **Trovato nel log** (riga 89): `[DP] [DpNative] *** WARNING: Base Camp processes are running and
>     WILL corrupt native uploads (concurrent HID writers): BaseCamp.Service (pid 6712) — close Base
>     Camp completely (including the tray icon / worker) ***` — **BaseCamp.Service.exe era ANCORA IN
>     ESECUZIONE mentre l'utente testava K2.App per il MacroPad**, non solo per il DisplayPad (dove il
>     warning esiste già). Verificato dal vivo con `tasklist`: PID 6712 attivo, sessione **"Services"**
>     (non "Console") — è un vero **servizio Windows**, non un processo tray closable dalla UI di Base
>     Camp. `Get-CimInstance Win32_Service` conferma: nome reale **`BaseCampService`** (nessuno spazio),
>     `StartMode=Auto`, `State=Running`.
>   - **BUG trovato in `stop-basecamp.bat`**: usava `sc query "BaseCamp Service"` / `sc stop "BaseCamp
>     Service"` — **con lo spazio**, nome SBAGLIATO. Il nome reale del servizio è `BaseCampService`
>     (senza spazio). Questo significa che lo script ha **sempre fallito silenziosamente** nel fermare
>     il servizio (anche se lanciato come amministratore) — probabilmente da quando è stato scritto (fase
>     DisplayPad). Se il servizio non viene fermato correttamente (kill del solo processo, senza `sc
>     stop`), Windows SCM lo puo' far ripartire, quindi **BaseCampService è verosimilmente rimasto
>     attivo durante gran parte, se non tutti, i test K2 fatti finora** (MacroPad E DisplayPad), a
>     prescindere da "aver chiuso Base Camp" (chiudere la finestra/tray non ferma il servizio).
>   - **Perché è plausibile la causa reale del MacroPad**: con BaseCamp.Service.exe ancora connesso allo
>     stesso `MacroPadSDK.dll`/HID device, due host stanno scrivendo comandi concorrenti al firmware —
>     esattamente lo scenario "concurrent HID writers" già diagnosticato (e loggato) per il DisplayPad.
>     Spiegherebbe perfettamente "ChangeEffect torna true ma non succede nulla" (il servizio BC potrebbe
>     ri-scrivere/sovrascrivere periodicamente il suo stato, o il device si confonde con due master) —
>     un fattore MAI controllato nei 3 round di fix precedenti, che quindi potrebbero non essere mai
>     stati testati in un ambiente pulito.
>   - **Fix**: `stop-basecamp.bat` corretto (`BaseCampService` senza spazio) per gli step `sc query`/
>     `sc stop`. **Non sono riuscito a fermare il servizio da qui**: la sessione Claude Code non ha
>     privilegi amministratore (`sc stop`/`taskkill /PID 6712` → "Accesso negato" su entrambi) — un
>     servizio Windows in sessione 0 richiede elevazione, che non ho. Risposta alla domanda dell'utente
>     "non riesci a gestire tu Base Camp": posso lanciare/killare processi utente e tool a riga di
>     comando (tshark, build, ecc.), ma NON posso interagire con GUI desktop arbitrarie (niente mouse/
>     schermo su app Windows) né elevare privilegi — serve l'utente per: (1) fermare il servizio (ora
>     che il bat è corretto, `stop-basecamp.bat` "Esegui come amministratore"), (2) cliccare dentro
>     Base Camp per applicare un effetto durante una cattura USB.
>   - **Prossimo passo raccomandato**: eseguire `stop-basecamp.bat` **come amministratore**, verificare
>     con `tasklist` che `BaseCamp.Service.exe` sia sparito per davvero (non solo la finestra), POI
>     riprovare gli effetti MacroPad in K2.App in un ambiente pulito, prima di investire tempo in una
>     cattura USB — potrebbe risolversi da solo senza bisogno di ulteriori fix di codice.
>
> Previous: 2026-07-09 (Everest/MacroPad: hover meno "ingrigito" + fix pressione fisica tasto Everest non illuminava):
>   - **Richiesta utente**: "puoi rendere meno ingrigito l'effetto hover sopra i keycap? Inoltre,
>     quando premo un tasto sulla tastiera non si 'illumina' nell'interfaccia".
>   - **Causa hover**: `KeyCapStyle`/`EverestKeyStyle` (`MainWindow.xaml`) sostituivano Face/Mount/
>     SideGrad con un grigio piatto `#656565` su `IsMouseOver`, cancellando qualsiasi colore keycap
>     configurato (nero/bianco/custom, stili Pudding/Reverse Pudding) sotto un grigio uniforme.
>   - **Fix hover**: entrambi gli `IsMouseOver` Trigger ora impostano solo `Tint.Background` (il
>     layer overlay già usato per il flash scuro di `IsPressed`, `#33000000`) a un bianco translucido
>     `#26FFFFFF`, invece di sostituire Face/Mount/SideGrad — l'hover schiarisce il colore ATTUALE del
>     tasto invece di sostituirlo con un grigio slegato dalla configurazione.
>   - **Causa mancata illuminazione fisica (solo Everest)**: `EvHighlightKeyboardButton` (chiamata da
>     `HandleEverestKey` ad ogni pressione fisica) faceva `btn.Background = teal` (assegnazione diretta
>     → WPF "local value", precedenza massima) e poi `btn.ClearValue(Background)` al rilascio — che
>     però ripristina il default dello Style (`#404040`), NON il colore keycap configurato
>     dall'utente, "silenziando" per sempre l'aspetto keycap su quel tasto dopo la prima pressione
>     (stesso bug di classe già risolto per `Mount`/hover in una sessione precedente, mai applicato
>     qui). Stesso pattern in `EvHighlightMapTarget`/`EvEndMapping` (evidenziazione oro durante la
>     mappatura guidata).
>   - **Fix**: nuovo helper `SetKeyTint` (`MainWindow.KeycapAppearance.cs`) che pesca l'elemento
>     `Tint` via `FindName` e ne imposta `Background` — un layer MAI toccato dal sistema di aspetto
>     keycap, quindi flash/clear non entrano mai in conflitto con Background/BorderBrush (colore
>     keycap/LED) e tornare a `Transparent` ripristina sempre esattamente il baseline giusto.
>     `EvHighlightKeyboardButton`/`EvHighlightMapTarget`/`EvEndMapping` migrati da
>     `Background=.../ClearValue`/`if (Content is TextBlock)` a `SetKeyTint`/`SetLegendForeground`
>     (quest'ultimo già gestiva anche i tasti multi-legenda Panel/Grid, non solo TextBlock).
>     MacroPad non toccato in questa entry: l'evidenziazione a pressione fisica lì usa un
>     `DataTrigger` a livello di Style su `IsHighlighted` (bindato al ViewModel) — vedi però l'entry
>     sotto per una regressione correlata (LED poll che sporca lo stesso `Background`) trovata da
>     una sessione in parallelo sullo stesso repo.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti (0/0 ciascuno). **Da verificare
>     dall'utente**: hover più leggero su tutti gli stili keycap, tasto Everest che si illumina
>     (teal) alla pressione fisica e riprende il colore keycap corretto al rilascio.
>
> Previous: 2026-07-09 (MacroPad: fix regressione "tasto rimane grigio" dopo pressione fisica + tentativo EnsureSlotInitialized per ChangeEffect ancora rotto):
>   - **Feedback utente**: "Niente, non funzionano ancora esattamente come prima. Nemmeno il led
>     preview. Inoltre, quando premo un tasto sul device fisico poi a video rimane grigio." — il fix
>     EffData (entry precedente) non ha cambiato nulla, e introduce una regressione nuova.
>   - **Riconsiderazione onesta del fix EffData precedente**: confrontando byte-per-byte il vecchio
>     layout (`[MarshalAs(ByValArray)] FWColor[3]` + `byte[43]`) col nuovo (campi inline + `fixed
>     byte[43]`), occupano ESATTAMENTE gli stessi 62 byte nello stesso ordine — un array Pack=1 di
>     struct blittable marshalato ByValArray produce lo stesso layout sul wire di campi inline
>     equivalenti. Il fix della sessione precedente quasi certamente **non cambiava alcun byte
>     inviato al firmware** — da qui il "non funzionano ancora esattamente come prima" (letterale: il
>     comportamento non è cambiato perché il payload non è cambiato). Struct comunque tenuta (più
>     coerente con `BlockData`/Everest, nessun downside), ma non è la causa.
>   - **Causa regressione "tasto grigio dopo pressione fisica"**: il MacroPad LED preview poll (120ms,
>     riattivato/mai disattivato da sezione — a differenza dell'Everest, che lo fa solo mentre la
>     sezione "RGB & Lighting" è visibile) scriveva il colore "LED off" (`LedOffColor`, `#D0D0D0`
>     grigio chiaro) su Background/BorderBrush del tasto via `SetCurrentValue` ad ogni tick, per gli
>     stili keycap Pudding/Reverse Pudding. Se un tick del poll cade MENTRE il tasto è fisicamente
>     premuto (`IsHighlighted=True`, style DataTrigger attivo su `MacroKeyStyle` che porta
>     `Background` a `#900000` rosso), `SetCurrentValue` scrive comunque il grigio "sotto" al trigger
>     attivo (senza essere visibile in quel momento) — al rilascio, quando il trigger si disattiva,
>     WPF torna al valore corrente della proprietà, che ora è il grigio scritto dal poll invece del
>     colore keycap giusto. Risultato: il tasto resta grigio dopo ogni pressione fisica, coerente con
>     quanto riportato.
>   - **Fix regressione**:
>     1. **`MainWindow.LedPreview.cs`**: nuovo `UpdateMpLedPreviewActive(bool active)`, mirror di
>        `UpdateEverestLedPreviewActive` — spegne/pulisce il poll MacroPad quando non si guarda la
>        sezione "LED Lighting". `StartLedPreview()`: `_ledPoller.MacroPadEnabled` ora parte da
>        `_activeMpSection == PnlMpSecLed` invece che da `_macroPad.IsOpen` incondizionato.
>     2. **`MainWindow.SectionNav.cs::ShowMpSection`**: chiama `UpdateMpLedPreviewActive(panel ==
>        PnlMpSecLed)`, esattamente come `ShowEvSection` fa per l'Everest.
>     3. **`OnMacroPadColorsUpdated`**: seconda linea di difesa — salta l'update di un tasto se
>        `_keys[btnIndex].IsHighlighted` è vero in quel momento (pressione fisica in corso), a
>        prescindere dalla sezione attiva.
>   - **Nuovo tentativo per ChangeEffect (Static/Breath/Reactive/Yeti/Matrix/Off ancora rotti)**:
>     rianalizzando la storia dell'Everest (`EverestService.InitDllState`, commento: "Even though we
>     don't use the returned data, the DLL's internal side effects prepare the state for
>     ChangeEffect/ChangeBlockEffect") — l'Everest chiama UNA VOLTA, dopo `OpenUSBDriver`: `GetFWInfo`,
>     `GetProfileEffectTable`, `GetExtendInfo`, `GetFWLayout`, `EnableKeyFunc(true)`, `APEnable(false)`.
>     **Il MacroPad non ha MAI avuto un equivalente** — `MacroPadService.Open()` chiamava solo
>     `SetKeyCallBack`+`OpenUSBDriver`. Dato che Wave/Tornado (via `ChangeBlockEffect`) funzionano SENZA
>     alcun init dedicato, non è chiaro se questo sia davvero il fattore mancante per `ChangeEffect` —
>     è un'ipotesi basata su analogia strutturale con l'Everest, non su una nuova evidenza dal
>     decompilato UI (che per il MacroPad non mostra questa sequenza, essendo lato `BaseCamp.Service.exe`
>     — il servizio background, mai estratto/decompilato in questa sessione — non lato UI).
>   - **Fix tentativo**: nuovo `MacroPadService.EnsureSlotInitialized(uint id)` (idempotente per slot,
>     `HashSet<uint>` cache, reset in `Close()`): `GetFWInfo`/`GetFWLayout`/`EnableKeyFunc(true)`/
>     `APEnable(false)`, stessa sequenza dell'Everest ma per singolo slot MacroPad. Chiamato all'inizio
>     di `SetEffect` e in `StartLedPreview` (prima di `SetSyncEffect`), così gira comunque indipendentemente
>     da quale flusso parte per primo.
>   - **Verificato**: build pulite (0/0) su entrambe le solution — un primo tentativo era fallito per
>     `SetKeyTint` "non esiste nel contesto corrente" in `MainWindow.Everest.cs`, causato da uno stato
>     transitorio di un'altra sessione in parallelo sullo stesso repo (non miei file, risolto da solo
>     al retry, `SetKeyTint` è correttamente definito in `MainWindow.KeycapAppearance.cs`).
>   - **Nota onesta per il prossimo giro**: due tentativi di fix mirati al decompilato UI-layer
>     (rimozione APEnable per-apply, struct blittable) non hanno cambiato il comportamento osservato.
>     Se anche `EnsureSlotInitialized` non risolve, la prossima mossa corretta per la regola di
>     progetto ("prima sniff con USBPcap+Wireshark, poi eventuale decompile — non indovinare il
>     bit-layout") è una **cattura USB reale** di Base Camp che applica un preset semplice (es. Static)
>     al MacroPad, da confrontare byte-per-byte con l'hex-dump che K2 già logga (`DumpEffData` in
>     `K2.App.log`, cerca `[MacroPad.SetEffect]`) — non ancora fatta, `_reference/usb_dumps/` ha solo
>     catture Everest/DisplayPad. Vedi `_reference/USB_SNIFF_GUIDE.md`.
>
> Previous: 2026-07-09 (MacroPad: EffData non era blittable — ChangeEffect tornava true ma il wire restava stale; UI nasconde velocità/direzione se non supportate):
>   - **Feedback utente dopo test su hardware**: "wave e tornado funzionano. Gli altri effetti no,
>     nemmeno 'Off'." — conferma che il fix precedente (ChangeBlockEffect per Wave/Tornado, entry sotto)
>     ha funzionato, ma isola il problema: TUTTO ciò che passa da `ChangeEffect` (il ramo non-block) è
>     rotto, "Off" incluso — il caso più semplice possibile (nessun colore, nessuna velocità).
>   - **Causa root**: `MacroPadSdkNative.EffData` era ancora dichiarata con
>     `[MarshalAs(UnmanagedType.ByValArray)] FWColor[] colorLv` / `byte[] byData` — una struct NON
>     blittable, che il marshaler P/Invoke di .NET converte copiandola in un buffer nativo temporaneo
>     ad ogni chiamata. `BlockData` (funzionante) invece usa campi inline (`colorLv0`/`colorLv1`/
>     `undefA0..4` come `FWBColor` dirette, non un array) + `fixed byte tail[23]` — una struct
>     VERAMENTE blittable, passata by-value byte-per-byte. Root cause identica a un problema già
>     documentato per l'Everest (`EverestSdkNative.cs`, storico "P/Invoke returns True, wire stale") —
>     ma per l'Everest **solo `BlockData` aveva avuto questo trattamento**: la sua `EffData` era GIÀ
>     stata silenziosamente riscritta con campi inline (`colorLv0/1/2` + `fixed byte byData[43]`) in una
>     sessione precedente (visibile confrontando `EverestSdkNative.cs` con la vecchia versione di
>     `MacroPadSdkNative.cs`, mai allineata). La versione MacroPad non era mai stata portata a questo
>     schema: `ChangeEffect` tornava `true` (nessun errore) ma i byte che arrivavano al firmware erano
>     quelli residui del comando precedente ("wire stale") — spiega perché letteralmente NULLA cambiava,
>     "Off" compreso.
>   - **Fix `MacroPadSdkNative.cs`**: `EffData` riscritta come `unsafe struct` con campi inline
>     `colorLv0`/`colorLv1`/`colorLv2` (FWColor dirette) + `fixed byte byData[43]`, IDENTICA nel layout
>     alla `EffData` ora-funzionante dell'Everest. `EffData.New` riscritta di conseguenza, portando
>     anche la logica `usesBkColor` (Reactive/Yeti/Matrix mettono il 2° colore in `bkColor`, Breath in
>     `colorLv1` con `byRandColor=16`) 1:1 da `EverestSdkNative.EffData.New` — stessa famiglia firmware,
>     stesso comportamento decompilato.
>   - **UI — richiesta utente**: "andrebbe tolta la combo di velocità e direzione per gli effetti che
>     non ce l'hanno" (prima erano solo disabilitate/`IsEnabled=false`, restavano visibili grigie).
>     `MainWindow.xaml`: le due `StackPanel` che contengono label+combo di Velocità (`PnlMpSpeed`) e
>     Direzione (`PnlMpDirection`) hanno ora `x:Name`. `MainWindow.MacroLed.cs::UpdateMpCapabilities`:
>     oltre a `IsEnabled`, imposta `Visibility=Collapsed` quando l'effetto selezionato non supporta il
>     parametro (stessa logica estesa anche al checkbox Rainbow, stesso principio anche se non
>     esplicitamente richiesto). Il layout del pannello si restringe/allarga a seconda dell'effetto
>     scelto invece di mostrare controlli grigi inutilizzabili.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti (0/0 ciascuno) — richiesto `taskkill /F /PID
>     <pid> /T` diretto (K2.App aperto per il test sul MacroPad, MSB3027). **Da verificare dall'utente
>     su hardware fisico**: che Static/Breath/Reactive A-B-C/Yeti/Matrix/Off ora funzionino davvero (non
>     solo che il P/Invoke torni true), che i colori vadano nel campo giusto (`bkColor` vs `colorLv1` a
>     seconda dell'effetto) e che il pannello nasconda/mostri correttamente velocità/direzione/rainbow
>     cambiando effetto. Nessun ambiente qui ha accesso al device USB.
>
> Previous: 2026-07-09 (MacroPad: effetti LED — Wave/Tornado erano rotti, ChangeBlockEffect mancante; pannello allineato a Everest):
>   - **Richiesta utente**: dopo il fix della preview (entry sotto), l'utente ha riportato "non sembra
>     succedere niente" — nessun cambiamento visibile sul MacroPad. Richiesta: allineare il pannello
>     "LED Lighting" del MacroPad a quello RGB dell'Everest e far funzionare gli effetti basandosi sul
>     decompilato di Base Camp.
>   - **Causa root trovata**: dump IL di `MacroPadOperations.SetMacroPadLighting` /
>     `MacroPadDLLHelper.getChangeEffect` / `getChangeBlockEffect` (stesso `BaseCamp.UI.dll` estratto
>     nella sessione precedente). **Wave e Tornado NON sono preset applicati via `ChangeEffect`** — Base
>     Camp li instrada su **`ChangeBlockEffect`** (struct `BlockData`, non `EffData`), esattamente come
>     già scoperto per l'Everest il 2026-05-30 (`EverestSdkNative.BlockData`, mai riusato per il
>     MacroPad). **Wave è l'effetto selezionato di default nel combo** (`CbMacroEffect.SelectedIndex=2`)
>     — quindi la primissima cosa che l'utente vede aprendo il pannello è esattamente il caso rotto:
>     `ChangeEffect` veniva chiamato comunque, il firmware la rifiuta/ignora (come per l'Everest,
>     "ChangeEffect REJECTS indices 4/5/7"), niente cambia sui LED.
>   - **Bug secondario, stesso file**: `bySpeed` veniva inviato come **enum 0/1/2** (`MacroPadService.
>     Speed`), non come il vero byte firmware 0..100 che Base Camp manda (`Lighting.Speed`, default DB
>     60; forzato a 255 solo per Static/Off — visto nel dump IL di `getChangeEffect`). Un valore 0/1/2
>     su una scala 0-100 è ai limiti/fuori dal range utile atteso dal firmware — ulteriore causa
>     probabile di animazioni percepite come "non funzionanti" anche per gli effetti che passano da
>     `ChangeEffect`. **Terzo bug**: `byRandColor` per il rainbow veniva mandato come `1`, ma il dump IL
>     mostra che BC usa **`2`** (`1` non corrisponde a nessun valore osservato: 0/2/16).
>   - **Fix `MacroPadSdkNative.cs`**: `EffData.New` ora accetta `byte speed` (non più `SpeedT`), forza
>     `bySpeed=255` per Static/Off (replica esatta della `getChangeEffect`), `byRandColor` rainbow=2
>     (non 1). Aggiunti `FWBColor`/`BlockData`/`ChangeBlockEffect(BlockData,uint ID)` — porting diretto
>     da `EverestSdkNative.BlockData` (stessa famiglia firmware, stesso layout 62B Pack=1), con
>     `direction`/`speed`/`byRandColor`/`byBlockNum` verificati byte-per-byte identici via
>     `getChangeBlockEffect`: direzione Wave 4-way `{0,2,4,6}`, Tornado CW/CCW `{9,10}` — stessi codici
>     dell'Everest.
>   - **Fix `MacroPadService.cs::SetEffect`**: firma allineata a `EverestService.SetEffect`
>     (`speedByte`/`directionByte` invece dell'enum `Speed`/`Direction`). Ramo Wave/Tornado →
>     `BlockData`+`ChangeBlockEffect`; tutti gli altri → `EffData`+`ChangeEffect` (invariato).
>     **Rimossa la chiamata incondizionata `APEnable(false, id)`** prima di ogni apply: verificato con
>     `--callers APEnable` su tutto `BaseCamp.UI.dll` che Base Camp non tocca mai l'AP mode per il
>     MacroPad in questo percorso (zero risultati) — era un'altra assunzione copiata dall'Everest (che
>     invece lo fa, ma solo se già in AP) mai confermata per il MacroPad, e potenzialmente un'altra
>     causa del "nulla succede" se mette il device in una modalità diversa dal normale rendering preset.
>   - **`MainWindow.MacroLed.cs` riscritto** per rispecchiare la struttura di `MainWindow.Everest.cs`
>     (regione "RGB lighting panel"): nuovo `MacroCaps`/`CapsFor(Effect)` (tabella capacità per-effetto:
>     Rainbow/Speed/direzione — stessi valori della tabella `EvCaps` dell'Everest, stessi effetti),
>     combo velocità **5 posizioni** ("1 — slow".."5 — fast", scala 0/25/50/75/100) al posto delle 3
>     vecchie (Slow/Normal/Fast → 0/1/2), gating dinamico di velocità/direzione/rainbow in base
>     all'effetto selezionato (`UpdateMpCapabilities`, mirror di `UpdateEvCapabilities`),
>     `DropDownOpened`/`DropDownClosed` per re-inviare l'effetto anche ri-selezionando la stessa voce
>     (comportamento Everest). Rimosso l'enum `MacroPadService.Speed` (sostituito da `speedByte` int);
>     `Direction` lasciato come alias non più usato internamente (commento aggiornato).
>   - **`MainWindow.xaml`**: aggiunta `CkMacroRainbow` (riusa le chiavi di traduzione già esistenti
>     `rgb_rainbow`/`rgb_rainbow_tip`, già usate dall'Everest — nessuna nuova stringa da tradurre),
>     `DropDownOpened`/`DropDownClosed` sul combo effetto MacroPad.
>   - **Persistenza**: nuova chiave Settings `macroled.rainbow` (stesso schema chiave-valore delle
>     altre `macroled.*`, nessuna migrazione DB necessaria).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti (0/0 ciascuno). **Da verificare dall'utente su
>     hardware fisico**: che Wave/Tornado ora animino davvero i LED del MacroPad (il caso che prima non
>     faceva NULLA), che gli altri effetti (Static/Breath/Reactive/Yeti/Matrix/Off) continuino a
>     funzionare con la nuova scala di velocità, e che il rainbow funzioni dove abilitato (Breath/Wave/
>     Tornado). Nessun ambiente qui ha accesso al device USB.
>
> Previous: 2026-07-09 (DisplayPad: fix multi-device — solo il primo pad si attivava all'avvio di K2):
>   - **Bug segnalato dall'utente** (con più DisplayPad collegati insieme, es. 2-4): "i displaypad non
>     funzionano subito ma devo ancora entrare nelle loro pagine per attivarli" — profili/azioni devono
>     rispondere sempre appena si apre K2, per tutti i dispositivi. Confermato dall'utente: già ricompilato
>     con il fix del 2026-07-07 (che attiva solo il PRIMO device trovato, vedi entry sotto) e il problema
>     persiste per i pad successivi.
>   - **Causa**: tutto lo stato del modulo DisplayPad (`_dpKeys`, `_dpMatrixToIndex`, `_currentDpPageId`)
>     rappresenta SOLO il device "foreground" (`_activeDpDeviceId`); `OnDpKey` scartava esplicitamente
>     ogni evento tasto da un device diverso da quello attivo ("Only handle events from the currently
>     selected device"). Il fix precedente attivava solo `items[0]` all'avvio — con 2+ pad collegati, il
>     secondo/terzo restavano muti finché l'utente non apriva la loro tab (che chiama `DpActivateDevice`).
>   - **Fix**: nuovo percorso "background" per i device NON foreground, che legge/esegue direttamente da
>     `DisplayPadStore` invece che dai campi UI-bound. `MainWindow.DisplayPad.cs`: `DpActivateBackgroundDevice`/
>     `DpUploadPageForDevice` (upload icone pagina corrente senza toccare `_dpKeys`/`CbDpProfile`),
>     `DpHandleBackgroundKey` (matrix→index statico `DpDefaultMatrixToIndex`, azione letta da
>     `_dpStore.LoadPage` per profilo/pagina correnti del device, esegue via nuovo `DpEngineFor(id)` —
>     un `ButtonActionEngine` dedicato per device, creato pigramente al primo utilizzo), `DpBgNavigateToPage`/
>     `DpBgNavigateBack` (navigazione cartelle per-device, `_dpBgPageId`/`_dpBgPageHistory`). `DpRefreshDevices`
>     ora attiva in background OGNI device connesso diverso da `_activeDpDeviceId` (non solo il primo),
>     saltando quelli già tracciati per evitare re-upload inutili ad ogni refresh; alla disconnessione
>     dimentica lo stato background del device (se si ricollega va ri-attivato, la memoria immagini
>     on-board non sopravvive al replug). Nuova classe `DisplayPadBackgroundActionHost` (`DisplayPadActionHost.cs`,
>     `IActionHost` fissato su un device id esplicito, mai su `_activeDpDeviceId`): il self-target di
>     "switch profile" passa `deviceId` esplicito a `DpSwitchProfile` invece di `null` (che altrimenti
>     avrebbe risolto al device foreground, sbagliato).
>   - **Bug correlato scoperto e risolto nello stesso punto**: `DpRequestRepaint(int id)` ignorava il
>     parametro `id` e richiamava sempre `DpReloadAndPreloadProfile()` (che opera implicitamente su
>     `DpSelectedDeviceId()`) — quindi qualsiasi switch-profilo cross-device (incluso quello già esistente
>     via `SwitchProfileByKey`/azione "profile" con target esplicito da un altro device, es. MacroPad→
>     DisplayPad) aggiornava il DB correttamente ma ridisegnava lo schermo del device SBAGLIATO (quello
>     foreground). Ora `DpRequestRepaint` instrada a `DpReloadAndPreloadProfile` solo se `id` è davvero il
>     tab visibile, altrimenti a `DpUploadPageForDevice`. Aggiunto anche il reset del cursore pagina
>     per-device (`_dpBgPageId[id]=0`) nel branch non-attivo di `DpSwitchProfile`, mirror di
>     `ResetDpNavigation()` per il foreground.
>   - **Verificato**: `stop-k2.bat` + `dotnet build K2.sln -c Debug -p:Platform=x86` e
>     `K2.DisplayPad.sln -c Debug -p:Platform=x64`, entrambi puliti (0 errori/0 warning). **Da verificare
>     su hardware dall'utente**: con 2+ DisplayPad collegati, che TUTTI rispondano ai tasti fisici subito
>     dopo l'avvio di K2 (non solo il primo), incluso cambio profilo/navigazione cartelle sul device non
>     ancora aperto nella UI.
>   - **Fix richiesto dall'utente lo stesso giorno, dopo il primo test**: "quando cambio profilo, se
>     nel profilo ci sono icone vuote, non vengono ripulite sul dispositivo ma restano le icone del
>     vecchio profilo" — riproducibile SOLO sul percorso background appena introdotto sopra:
>     `DpUploadPageForDevice` caricava/uploadava solo i tasti CON immagine nel nuovo profilo, senza
>     mai un `ResetPictures` preventivo, quindi un tasto vuoto nel profilo nuovo restava con l'icona
>     del profilo precedente ancora disegnata sul pannello (il percorso foreground, invece, già lo
>     faceva correttamente via `DpReloadCurrentProfile`'s `blankFirst`, per questo il bug non si vedeva
>     lì). Aggiunto parametro `blankFirst` a `DpUploadPageForDevice` (stesso pattern del foreground:
>     `_dpClient.ResetPictures(id)` dentro la continuation, prima degli upload), passato `true` solo
>     dal branch background di `DpRequestRepaint` (switch-profilo vero e proprio) — non dalla
>     navigazione cartelle (`DpBgNavigateToPage`/`DpBgNavigateBack`) né dall'attivazione iniziale
>     (`DpActivateBackgroundDevice`), esattamente come il foreground non blanka in quei due casi.
>     **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0). **Da verificare su
>     hardware dall'utente**: cambiando profilo su un DisplayPad NON foreground, i tasti vuoti nel
>     nuovo profilo devono apparire vuoti (non più l'icona del profilo precedente).
>
> Previous: 2026-07-09 (MacroPad: fix sequenza LED preview live, riportata da Base Camp via decompile UI):
>   - **Richiesta utente**: "dobbiamo trovare il modo di attivare la led preview su macropad. Analizza
>     decompiled + linux e cerchiamo di riportare la funzione di base camp". La macchina/infrastruttura
>     per la preview live (overlay colore reale letto dal device, non solo l'anteprima software statica)
>     esisteva già dal 2026-06-08 (`LedColorPoller`, `MainWindow.LedPreview.cs`, `GetColorData` P/Invoke)
>     ma per il MacroPad non era mai stata verificata su hardware — il codice in `StartLedPreview()`
>     era "la stessa sequenza di Everest" (GetFWLayout + APEnable(true) + SetSyncEffect off→on +
>     SetBacklight(true)), MAI confermata contro il comportamento reale di Base Camp.
>   - **BaseCampLinux: vicolo cieco confermato**. `grep -ri macropad` sull'intero repo BaseCampLinux
>     non trova NULLA — il progetto supporta solo Everest Max/60, Makalu, DisplayPad (confermato anche
>     dal README). Non è quindi una fonte utilizzabile per il MacroPad, a differenza di quanto sperato.
>   - **Nuova estrazione**: `BaseCamp.Service.exe` (già disponibile) espone solo il livello P/Invoke
>     grezzo (`BaseCamp.Service.Helpers.MacroPadSDK.*`, via `dotnet_pinvoke_dump.py`) — la vera logica
>     applicativa (quando/come viene chiamato `GetColorData`) vive in `BaseCamp.UI.exe`, MAI estratto
>     prima d'ora. Estratto per la prima volta **`BaseCamp.UI.dll`** dal bundle self-contained
>     (.NET single-file, tecnica già documentata in `_PROJECT_MAP.md`: cerca `b"BaseCamp.UI.dll"`
>     ASCII nel manifest — non UTF-16LE, quello matcha solo le stringhe di version-resource — decodifica
>     l'header `[offset:i64][size:i64][compressedSize:i64][type:u8][pathlen:u8]` che lo precede,
>     slice `data[offset:offset+size]`), salvato in nuovo
>     `K2/_reference/BaseCamp_decompiled_UI/BaseCamp.UI.dll` (11MB, escluso da git). Contiene i
>     Controllers ASP.NET Core (`MacroPadController`, `MacroPadOperations`, ecc.) — riusabili con gli
>     stessi `tools/dotnet_method_calls.py` già esistenti.
>   - **Sequenza reale trovata** (`MacroPadController.<GetAnimationFromHW>d__22::MoveNext` +
>     `<GetColorData>d__23::MoveNext`, entrambi async state machine dumpati via IL): Base Camp espone
>     un endpoint WebSocket (`GetAnimationFromHW`) che il JS della UI apre quando il pannello lighting
>     è visibile; il loop server-side è banale — **nessun priming**, solo
>     `while (StartSyncColorForMacroPad && !IsAppMinimized) { GetColorData(client, ref color); se ok
>     manda JSON dei colori sul socket; Thread.Sleep(300); }`. Il flag `StartSyncColorForMacroPad`
>     (default `false`, verificato in `GlobalVariables::.cctor`) viene settato da un secondo endpoint
>     HTTP, `MacroPadController.SyncColorFlagChanged(bool)`, chiamato dal JS quando il pannello si apre/
>     chiude. **`SetSyncEffect(id, true, 50)` viene chiamato UNA volta sola**, non nel loop di
>     streaming ma in `MacroPadOperations.getDefaultLighting` (al caricamento della pagina lighting,
>     per applicare l'effetto salvato) — **senza** il toggle off→on che K2 faceva. Verificato con
>     `--callers` su tutto `BaseCamp.UI.dll`: **zero chiamate** a `GetFWLayout`, `APEnable`,
>     `EnableKeyFunc`/`SetBacklight` per il MacroPad in tutta la UI — a differenza di Everest, che le
>     usa davvero (`EnableColorStream`/`GetFWLayout` sono specifiche Everest, copiate qui per analogia
>     ma mai verificate).
>   - **Fix in `MainWindow.LedPreview.cs::StartLedPreview()`**: rimossi `GetFWLayout`,
>     `_macroPad.APEnable(uid, true)`, il toggle `SetSyncEffect(false,50)`+`(true,50)` e
>     `_macroPad.SetBacklight(uid, true)`. Sostituiti con la singola chiamata
>     `MacroPadSdkNative.SetSyncEffect(true, 50, uid)` che Base Camp effettivamente usa. Sospetto
>     principale sul mancato funzionamento: `APEnable(true)` — stesso nome/famiglia di
>     `StartFWUpdate`/`ResetDevice` nell'elenco P/Invoke, verosimilmente mette il device in una
>     modalità (AP = firmware update?) diversa da quella di reporting HID normale, quindi
>     `GetColorData` da lì in poi non riceve più nulla di significativo. Rimosso anche
>     `SetBacklight(true)` perché sovrascriveva ad ogni avvio della preview l'impostazione backlight
>     ON/OFF persistita dall'utente (`BtnMacroLightOn`/`BtnMacroLightOff` in `MainWindow.MacroLed.cs`,
>     path separato e non toccato da questo fix) — anche Base Camp non lo fa in questo punto.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti (0/0 ciascuno) — richiesto `stop-k2.bat`
>     (due istanze K2.App zombie, PID diversi da bat/taskkill diretto: il primo tentativo via
>     `stop-k2.bat` non ha killato i processi per motivi non chiari, risolto con `taskkill /F /PID
>     <pid> /T` diretto sui due PID riportati dall'errore MSB3027). **Da verificare dall'utente su
>     hardware fisico**: che l'overlay LED del MacroPad ora si accenda/segua i colori reali quando la
>     sezione "LED Lighting" del tab MacroPad è aperta — nessun ambiente qui ha accesso al device USB.
>     Se ancora non funziona, prossimo passo è una cattura USB reale (`USBPcap+Wireshark`, vedi
>     `_reference/USB_SNIFF_GUIDE.md`) di Base Camp che pilota il MacroPad — non ancora fatta,
>     `_reference/usb_dumps/` ha solo catture Everest/DisplayPad.
>
> Previous: 2026-07-09 (MacroPad: portata la feature "aspetto keycap" di Everest, identica UX):
>   - **Richiesta utente**: "aggiungiamo la gestione dei keycap esattamente come è per Everest anche
>     per Macropad" — stessa impostazione colore keycap/testo (nero/bianco/personalizzato) e tipo
>     keycap (Normal/Translucent/Pudding/Reverse Pudding) vista nelle ultime sessioni per Everest.
>   - **Rifattorizzati come tipi condivisi** (in `MainWindow.KeycapAppearance.cs`, usati da entrambi
>     i device): `EverestKeycapColorMode`→`KeycapColorMode`, `EverestKeycapStyle`→`KeycapStyle`,
>     `EvKeyVisual`→`KeyVisual` (record struct Button+Halo), `EvDefaultBorderColor`→`LedOffColor`,
>     `KeycapStyleChoice(s)` (già senza prefisso device). Chiavi di traduzione rinominate da
>     `settings_ev_keycap_*` a `settings_keycap_*` (testo già generico, riusabile identico per
>     entrambi i tab — nessuna nuova stringa introdotta).
>   - **`MainWindow.xaml`**: `KeyCapStyle` (il ControlTemplate base usato SOLO da `MacroKeyStyle` —
>     `EverestKeyStyle` lo eredita ma sovrascrive interamente `Template`, quindi non ne condivide il
>     markup a runtime) ora ha lo stesso trattamento di `EverestKeyStyle`: aggiunto layer `LedHalo`,
>     `Mount.Background` passato da letterale `#090909` a `{TemplateBinding BorderBrush}`, rimosso
>     `LedTint` (il vecchio "wash" traslucido, ora sostituito dal meccanismo halo/pudding). Nuova
>     sezione sidebar **Settings** nel tab MacroPad (`RbMpSecSettings`/`PnlMpSecSettings`, 4° sezione
>     dopo Key Binding/Orientation/LED Lighting — prima non esisteva una sezione Impostazioni per
>     MacroPad), stessi controlli della sezione Everest (radio colore/testo + swatch, combo stile).
>   - **`MainWindow.MacroKeycapAppearance.cs`** (nuovo file, mirror di `MainWindow.KeycapAppearance.cs`):
>     cache `_mpKeycap*`, `InitMpSettingsPanel`/`LoadMpKeycapAppearanceFromStore` (nuovo flag dedicato
>     `_mpSettingsSuppress` — il MacroPad non aveva ancora una sezione Impostazioni unificata: Rotation
>     e LED lighting hanno ciascuno il proprio flag/metodo separato), persistenza in `MacroPadStore`
>     (stesse chiavi `settings.keycap_*` di Everest ma store diverso, nessuna collisione),
>     `ApplyMacroKeycapAppearanceToAllKeys`/`ApplyMacroPadLedColor`/`ResetMacroPadKeyToOff` identici
>     nella logica alla controparte Everest.
>   - **`MainWindow.LedPreview.cs`**: `_mpKeyTints` (Dictionary&lt;int,Border&gt;) → `_mpKeyVisuals`
>     (Dictionary&lt;int,KeyVisual&gt;, come Everest ma keyed per indice-tasto 0-11 invece che per
>     ledIndex); `BuildMacroPadLedTints`→`BuildMacroPadKeyVisuals`; `OnMacroPadColorsUpdated` delega
>     ora a `ApplyMacroPadLedColor` invece di scrivere `Background` direttamente.
>   - **Bug potenziale individuato e corretto PRIMA che arrivasse a schermo**: `MacroKeyStyle` ha due
>     `DataTrigger` (`HasAction`→bordo verde, `IsHighlighted`→sfondo rosso flash sul press fisico,
>     entrambi già esistenti, non toccati da questa feature) che impostano `Background`/`BorderBrush`
>     a livello di Style Trigger. Un'assegnazione diretta `button.Background = ...` da codice crea un
>     WPF "local value", che ha precedenza SEMPRE maggiore di un qualunque Style Trigger — avrebbe
>     quindi zittito per sempre sia l'indicatore "has action" sia il flash di pressione al primo
>     cambio di impostazione keycap. Fix: nuovi helper `SetKeyBackground`/`SetKeyBorderBrush` in
>     `MainWindow.KeycapAppearance.cs` che usano `Button.SetCurrentValue(...)` invece
>     dell'assegnazione diretta — aggiorna il valore senza acquisire quella precedenza, quindi i due
>     DataTrigger di `MacroKeyStyle` continuano a funzionare/sovrascrivere normalmente. Applicato
>     anche lato Everest (che oggi non ha DataTrigger equivalenti) per coerenza tra le due
>     implementazioni gemelle.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` e `dotnet build
>     K2.DisplayPad.sln -c Debug -p:Platform=x64` puliti (0 errori/0 warning ciascuno) — build dirette
>     senza `build-check.bat`/pulizia bin-obj (nessun processo K2 residuo trovato questa volta).
>     **Da verificare dall'utente**: aspetto a schermo dei 4 stili sul MacroPad con LED accesi/spenti,
>     hover sui tasti, E soprattutto che l'indicatore verde "has action"/il flash rosso di pressione
>     sui tasti MacroPad funzionino ancora dopo aver toccato le impostazioni keycap (il fix
>     `SetCurrentValue` sopra non è stato ancora testato su hardware reale).
>
> Previous: 2026-07-09 (Everest: riga superiore angoli spostata su per non sovrapporsi a quella sotto; ENTER maiuscolo):
>   - **Richiesta utente**: sui tasti con 3+ glifi, la riga superiore (shift/Shift+AltGr) si sovrappone
>     a quella inferiore (base/AltGr) — alcune lettere scompaiono dietro quelle sotto. Inoltre "Enter"
>     va messo tutto maiuscolo come gli altri tasti (SHIFT/CTRL/ALT).
>   - **`BuildCornerLegend`**: la riga superiore (row 0, shift/Shift+AltGr) ora ha `Margin(0,-2,0,0)` —
>     spostata su di 2px verso il bordo del tasto. Solo la riga superiore si muove: quella inferiore
>     (base/AltGr) resta ancorata al fondo com'era, dato che è già a ridosso del bordo inferiore del
>     tasto e non ha margine di manovra.
>   - **`KeyboardLayout.cs`**: `"Enter"` → `"ENTER"` (sia il tasto Enter della tastiera principale
>     che quello del numpad, quest'ultimo già cambiato in una sessione precedente).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) al primo tentativo
>     (nessuna istanza K2.App aperta questa volta). **Da verificare dall'utente**: se lo spostamento
>     di 2px basta a separare le due righe sui tasti con più glifi.
>
> Previous: 2026-07-09 (Everest: CTRL alla stessa dimensione di ALT, più respiro orizzontale tra gli angoli accenti):
>   - **Richiesta utente** (con 2 screenshot: K2 vs Base Camp reale): CTRL appariva molto più piccolo
>     di ALT nella stessa riga; i glifi sui tasti accentati hanno ormai la dimensione giusta ma sono
>     ancora troppo vicini tra loro, soprattutto in larghezza.
>   - **Causa CTRL/ALT**: la soglia `longWord` (introdotta per rimpicciolire "HOME"/"ENTER"/"PAUSE")
>     usava `kd.W <= 40`, che includeva anche la riga modificatori (`ModW`=38px, CTRL/ALT/FN) oltre ai
>     tasti nav-cluster da 30px per cui era pensata. "CTRL" (4 caratteri) veniva quindi rimpicciolito
>     a 5px mentre "ALT" (3 caratteri, sotto soglia) restava a 8px — da qui il disallineamento visivo.
>     Soglia corretta a `kd.W <= 32` (esclude la riga da 38px, che ha già spazio a sufficienza).
>   - **`BuildCornerLegend`**: spaziatore centrale aumentato (colonna 1→4px, riga 1→2px) — più
>     orizzontale che verticale su richiesta esplicita, dato che il tasto è più largo che alto
>     rispetto ai glifi. Dimensione font invariata (già confermata giusta dall'utente).
>   - **Nota di sessione**: la solution è condivisa con un'altra sessione in corso in parallelo sulla
>     feature "aspetto keycap" (`MainWindow.KeycapAppearance.cs`/`MainWindow.LedPreview.cs`,
>     `EvKeyVisual`) — un primo tentativo di build era fallito per uno stato transitorio incoerente
>     di quei file (non miei), risolto da solo al retry successivo una volta che l'altra sessione ha
>     salvato in modo coerente.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0). **Da verificare
>     dall'utente**: CTRL/ALT ora alla pari, spaziatura accenti sufficiente — nessun modo di fare
>     screenshot WPF da qui.
>
> Previous: 2026-07-09 (Everest: aspetto keycap — ripristinato l'hover sul bordo inferiore dei tasti):
>   - **Richiesta utente**: passando sopra i tasti con il mouse, il bordo inferiore ("Mount", la
>     striscia scura visibile nel gap sotto la Face) non cambiava più colore — l'hover funzionava
>     solo sul resto del tasto.
>   - **Causa**: dalla sessione precedente, `Mount.Background` veniva impostato DIRETTAMENTE da
>     codice (`v.Mount.Background = ...`) per ogni stile, per farlo seguire bordo/LED/colore keycap.
>     Ma un valore assegnato da codice su un elemento del ControlTemplate è un "local value" — ha
>     precedenza MAGGIORE del Trigger di hover dello stesso template (`TemplatedParent template
>     trigger`), quindi lo zittiva permanentemente, per qualunque stile (non solo Pudding/Reverse
>     Pudding come prima di introdurre bordi colorati anche su Normal/Translucent).
>   - **Fix** (invece di gestire manualmente MouseEnter/Leave): notato che in OGNI ramo del codice
>     `Mount.Background` veniva sempre impostato allo STESSO valore di `Button.BorderBrush` nello
>     stesso istante — quindi `Mount` è ridondante come valore tracciato a parte. In
>     `MainWindow.xaml`, `EverestKeyStyle`: `Mount.Background` ora è `{TemplateBinding BorderBrush}`
>     invece del letterale `"#090909"` — segue automaticamente `Button.BorderBrush` (già impostato
>     da codice esattamente come serve) SENZA che il codice tocchi `Mount` direttamente, quindi il
>     Trigger di hover esistente (`Setter TargetName="Mount" Property="Background" Value="#656565"`,
>     già presente, mai attivo per Mount da quando si è iniziato a colorarlo da codice) torna a
>     funzionare, esattamente come già faceva per `Face`.
>   - **`MainWindow.KeycapAppearance.cs`**: rimosso `Mount` da `EvKeyVisual` (ora solo
>     `Button`+`Halo`) e tutte le assegnazioni `v.Mount.Background = ...` in
>     `ApplyKeycapAppearanceToAllKeys`/`ApplyEverestLedColor` (ridondanti, non serve più settarlo
>     esplicitamente). **`MainWindow.LedPreview.cs`**: `BuildEverestKeyVisuals` non cerca più
>     l'elemento `Mount` via `FindName`.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0 errori/0 warning) —
>     build diretta senza `build-check.bat` (un'altra sessione in parallelo aveva processi
>     `dotnet.exe` attivi sullo stesso repo). **Da verificare dall'utente**: hover visibile su
>     tutto il tasto, incluso il bordo inferiore, in tutti e 4 gli stili.
>
> Previous: 2026-07-09 (Everest: glifi multi-legenda ingranditi/spinti ai bordi, stack verticale per AltGr-only tipo E/€):
>   - **Richiesta utente**: ingrandire ancora i caratteri dei tasti con più simboli, spaziarli
>     spingendoli verso gli angoli, e per i tasti con SOLO AltGr (es. E/€) usare uno stack verticale
>     (lettera base sopra, simbolo AltGr sotto) invece della griglia ad angoli.
>   - **`MainWindow.xaml`** (`EverestKeyStyle` template): `ContentPresenter.Margin` 2px→**1px** — libera
>     ~2px di area utile per TUTTI i tasti Everest (non solo quelli multi-legenda), aiuta anche le
>     label lunghe come INS/DEL/HOME.
>   - **`BuildCornerLegend`**: margine esterno e spaziatore centrale portati a **zero/1px** (prima
>     1px/2px) — i glifi sono ora spinti fino ai veri angoli del tasto, usando tutto lo spazio fisico
>     rimasto per la dimensione del font piuttosto che per il respiro tra loro.
>   - **`BuildEverestKeyboardOverlay`**: nuova variabile `fsBig = fs + 1` usata per i casi multi-
>     legenda (era `fs`, ora un filo più grande delle lettere normali, per "ingrandisci i glifi").
>     Split del ramo AltGr in due:
>     - **AltGr + shift (e/o Shift+AltGr)** → resta la griglia `BuildCornerLegend` (ora a `fsBig`).
>     - **Solo AltGr, nessuno shift** (es. E→€, o le tante lettere DE con solo AltGr) → NUOVO: stack
>       verticale a 2 righe, lettera base sopra (bianco, `fsBig`) e simbolo AltGr sotto (teal,
>       `fsMulti+1`) — la griglia a 4 angoli per questo caso lasciava tutta la riga superiore vuota e
>       schiacciava le due legende nei soli angoli inferiori, illeggibile. L'ordine è INVERTITO
>       rispetto allo stack shift-only esistente (shift sopra/base sotto) perché sulle keycap reali
>       il simbolo AltGr va tipicamente sotto la lettera base, non sopra.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) — richiesto kill di
>     un'istanza K2.App riaperta durante la sessione. **Nota per il prossimo giro**: lo spazio fisico
>     dentro un tasto Everest da 30px è quasi del tutto esaurito con queste dimensioni (vedi nota
>     nella entry precedente) — un ulteriore ingrandimento dei 4 angoli richiederebbe allargare
>     fisicamente quei tasti specifici nella geometria (`KeyboardLayout.cs`), il che sposta le X dei
>     tasti successivi nella stessa riga: più invasivo, da valutare solo se davvero necessario.
>
> Previous: 2026-07-09 (Everest: aspetto keycap — bordo/centro grigio chiaro quando i LED sono spenti):
>   - **Richiesta utente**: nei tasti Pudding, il bordo quando i LED non sono attivi deve essere
>     bianco leggermente grigio invece dello scuro usato finora; nei Reverse Pudding, lo stesso
>     trattamento va al centro (Background) invece che al bordo.
>   - **`MainWindow.KeycapAppearance.cs`**: rinominata la costante `EvDefaultBorderColor` (`#1D1D1D`,
>     scura) in `EvLedOffColor` = `#D0D0D0` (bianco leggermente grigio) — stesso colore usato sia
>     per il fallback "LED spento" del bordo/Mount di **Pudding** sia per quello del centro/Background
>     di **Reverse Pudding** (prima, Reverse Pudding spento ricadeva sul colore keycap scelto anziché
>     su un accento neutro dedicato). Aggiornati sia `ApplyKeycapAppearanceToAllKeys` (baseline
>     statica, applicata subito dopo un cambio impostazione o un rebuild della tastiera) sia
>     `ApplyEverestLedColor` (fallback per-tick quando il colore ricevuto dal poller è nero/spento),
>     così restano sempre coerenti tra loro.
>   - **Verificato**: build pulita (0 errori/0 warning) — questa volta senza rilanciare
>     `build-check.bat` (che pulisce bin/obj di tutto il repo): un'altra sessione in parallelo aveva
>     un processo `dotnet.exe` con lock su `K2.Core.dll`, quindi ho usato `dotnet build` diretto sui
>     due file `.sln` senza toccare bin/obj, per non disturbare il suo build in corso.
>     **Da verificare dall'utente**: aspetto a schermo di Pudding/Reverse Pudding con LED spenti.
>
> Previous: 2026-07-09 (Everest: fix regressione — spaziatore troppo largo tagliava le legende accenti; HOME rimpicciolito):
>   - **Richiesta utente** (con screenshot): i tasti con 4 simboli non si leggono quasi più dopo
>     l'ultima modifica, font minuscolo; "Home" resta l'unica parola singola che ancora non entra.
>   - **Causa (mia regressione)**: nel giro precedente ho aumentato SIA la dimensione del font dei 4
>     angoli (a `fs`, come le lettere normali) SIA lo spazio riservato a margine/spaziatore
>     (Margin 3,2,3,2 + spaziatore colonna 7px/riga 6px) — ma l'area utile dentro un tasto da 30px è
>     solo ~20px dopo bordi/margini del template. Aumentare contemporaneamente font E spaziatore ha
>     lasciato pochissimo spazio reale per i glifi, che sono stati tagliati quasi a zero: esattamente
>     il "font minuscolo/illeggibile" segnalato.
>   - **`BuildCornerLegend`**: margine e spaziatore riportati a valori minimi (Margin 1px uniforme,
>     spaziatore colonna/riga 2px) — bastano per leggere "non attaccati", non uno spazio generoso:
>     con solo ~20px totali a disposizione per 2 colonne × 2 righe di testo a dimensione normale, non
>     c'è margine per uno spaziatore ampio. La dimensione dei 4 angoli resta `fs` (invariata da
>     questa sessione, come richiesto: uguale alle lettere normali).
>   - **HOME**: soglia `longWord` abbassata da `Length >= 5` a `>= 4` — "HOME" ora rientra nel ramo a
>     font extra-piccolo (stesso trattamento già applicato a "ENTER"/"PAUSE"); "END" (3 caratteri)
>     resta escluso, non era mai stato segnalato come problema.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0), nessuna istanza
>     K2.App aperta questa volta a bloccare la copia. **Da verificare dall'utente**: se ora i 4 angoli
>     sono leggibili E abbastanza separati — attenzione: lo spazio fisico a disposizione è molto
>     risicato (~20px), quindi il margine di manovra per ANCORA più spaziatura mantenendo la stessa
>     dimensione del font è quasi esaurito; se serve ancora più aria l'unica strada resterebbe
>     rimpicciolire un po' i 3 angoli modificatori (shift/AltGr/Shift+AltGr) rispetto al carattere
>     base, come nel design originale di questa feature.
>
> Previous: 2026-07-09 (Everest: legende accenti a dimensione normale, INSERT/DELETE abbreviate invece di rimpicciolite ancora):
>   - **Richiesta utente** (2° follow-up): i glifi degli accenti sono ora TROPPO piccoli — devono
>     essere della stessa dimensione delle altre lettere, ma più distanziati; "Insert" e le altre
>     parole lunghe continuano ad andare in overflow nonostante il rimpicciolimento precedente.
>   - **Indagine overflow persistente**: ho ri-estratto dal binario di Base Camp (stessa tecnica
>     UTF-16LE) il markup per Insert/Home/PgUp/Del/End/PgDn/PrtSc/ScrollLk/NumLock — ma tutte le
>     occorrenze trovate sono dentro `<div class="keylighting">`/`"keylight-1"`, cioè la tastiera di
>     anteprima del pannello **Lighting** (effetti RGB), non quella interattiva **Key Binding** che
>     l'utente ha screenshottato. Lì le label sono infatti abbreviate a due righe (cifra sopra +
>     parola corta sotto: "0"/"Ins", "7"/"Home", "9"/"PgUp", "1"/"End", "3"/"PgDn", "."/"Del") — utile
>     comunque come conferma indiretta che pure Base Camp non riesce a stare largo su un tasto 30px:
>     anche loro abbreviano, non usano mai la parola intera lì. Non ho invece trovato il markup della
>     tastiera Key Binding vera e propria (quella con le label estese viste nello screenshot).
>   - **Decisione**: invece di rimpicciolire ANCORA il font (già a 5-6px, il prossimo passo sarebbe
>     stato illeggibile), ho accorciato le due parole peggiori — `INSERT`→**`INS`**, `DELETE`→**`DEL`**
>     (3 caratteri, stessa convenzione delle tastiere fisiche reali che hanno lo stesso identico
>     vincolo di spazio) — così il testo entra comodamente anche a font normale, invece di dover
>     scegliere fra "leggibile" e "dentro dal tasto".
>   - **`KeyboardLayout.cs`**: `INSERT`→`INS`, `DELETE`→`DEL` (in entrambi `BuildBoardLeft_AnsiUs` e
>     `BuildBoardLeft_Iso`). `HOME`/`END`/`PAUSE`/`PG UP`/`PG DN`/`PRT SCN`/`SCR LK`/`NUM LOCK`/
>     `CAPS LOCK` invariati (già entravano, o vanno a capo sullo spazio).
>   - **`MainWindow.Everest.cs`**:
>     - `BuildCornerLegend`: i 4 angoli (shift/AltGr/Shift+AltGr/base) ora usano tutti **`fs`** (la
>       stessa dimensione delle lettere normali), non più `fsMulti` più piccolo per i 3 angoli
>       modificatore — la leggibilità viene dalla spaziatura, non da un font ridotto. Spaziatore
>       centrale aumentato ulteriormente (colonna 5→7px, riga 4→6px).
>     - Ramo "parola lunga senza spazio" (`longWord`): rimasto solo per `PAUSE`/`ENTER` (numpad) dato
>       che `INS`/`DEL` non lo attivano più (length<5) — dimensione extra-small ridotta un altro
>       gradino (5/6px → 4/5px) per i pochi casi residui.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) — richiesto kill di
>     DUE istanze K2.App aperte contemporaneamente durante questa sessione. **Da verificare
>     dall'utente**: se la dimensione uniforme + spaziatura extra sui tasti accentati è sufficiente
>     ora, e se INS/DEL si leggono bene — nessun modo di fare screenshot WPF da qui.
>
> Previous: 2026-07-09 (Everest: zoom +20%, font gruppo nav più piccolo, spaziatura accenti aumentata):
>   - **Richiesta utente** (follow-up alla sessione precedente): il gruppo Insert/Home/PgUp/Delete/
>     End/PgDn deve stare più piccolo per entrare nei tasti, i glifi sui tasti accentati sono ancora
>     un po' stretti, e la tastiera nel complesso va ingrandita del 20%.
>   - **Causa gruppo nav ancora clippato**: il fix precedente abilitava `Wrap` solo per le label CON
>     uno spazio (`"PG UP"`, `"SCR LK"`...) — ma `INSERT`/`DELETE`/`PAUSE`/`ENTER` sono una SINGOLA
>     parola senza spazio, quindi non vanno mai a capo e restavano piene a font 8px in un tasto 30px.
>   - **`MainWindow.Everest.cs`** (`BuildEverestKeyboardOverlay`, ramo etichetta singola): nuova
>     regola `longWord` — parola singola (niente spazio) di **5+ caratteri su un tasto ≤40px** usa
>     un font ancora più piccolo (5px/6px a seconda di `kd.W`) invece del font normale; le label con
>     spazio continuano a fare Wrap come prima, quelle corte (`HOME`, `END`, cifre, `F1`...) restano
>     invariate.
>   - **`BuildCornerLegend`**: spaziatore centrale della griglia 3×3 aumentato (colonna 3→5px, riga
>     2→4px) e margine esterno aumentato (2,1,2,1 → 3,2,3,2) per dare più respiro tra i 4 angoli
>     (shift/AltGr/Shift+AltGr/base) sui tasti con accenti.
>   - **`MainWindow.xaml`**: `SpEvLayout` (lo `StackPanel` che contiene sia `CvsEvKeyboard` che
>     `CvsEvNumpad`) ora ha `LayoutTransform` con `ScaleTransform 1.2×1.2` — **LayoutTransform** e non
>     RenderTransform: la differenza è che il primo fa ri-misurare il layout a monte (il `Grid`/
>     `Border` attorno si adatta alla dimensione scalata) E l'hit-testing di WPF segue automaticamente
>     la trasformazione visiva, quindi i click sui tasti continuano a mappare al `Button` giusto senza
>     toccare la matematica `KeyDef.X/Y/W/H` (che resta nello spazio di coordinate nativo, non scalato).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) — richiesto kill di
>     un'istanza K2.App aperta due volte di fila durante questa sessione (l'utente la sta tenendo
>     aperta per confrontare visivamente ad ogni giro). **Da verificare dall'utente**: aspetto reale
>     (zoom, spaziatura, font gruppo nav) e soprattutto che i click sui tasti restino precisi dopo lo
>     scale — nessun modo di fare screenshot WPF da qui.
>
> Previous: 2026-07-09 (Everest: markup Razor reale estratto da BaseCamp.UI.exe — Tab/Win/FN/spaziatura accenti):
>   - **Richiesta utente**: le legende dei tasti con accenti (è/à/ù/+ ecc.) restano troppo vicine tra
>     loro, il tasto Tab è molto diverso da Base Camp, chiede di verificare anche decompilato/Linux
>     per copiare meglio la struttura originale, usare l'icona vera sul tasto Windows, e disattivare
>     FN dalla configurazione (riservato, ma illuminabile).
>   - **Metodo**: `BaseCamp.UI.exe` è un self-contained single-file .NET+Electron bundle — non c'è
>     modo diretto di isolare l'assembly .NET per estrarre le view Razor, ma i letterali HTML
>     `WriteLiteral("...")` compilati restano leggibili come testo UTF-16LE grezzo nel file (stessa
>     tecnica già in uso per altre stringhe UI, vedi `_reference/BaseCamp_decompiled/README.md`).
>     Script Python throwaway: cerca l'encoding UTF-16LE di needle noti (`data-alt="TAB"`,
>     `data-key="lwin"`, `data-key="FN"`...) nel binario e stampa ±250 byte di contesto decodificati.
>     Trovato il markup Razor REALE della tastiera Everest (non supposizione):
>     - **Tab**: `<span data-alt="TAB" data-key="⇆" .../>` → mostra **"TAB" + glifo "⇆"** (icona
>       frecce), non solo testo "Tab".
>     - **Windows**: `<span data-key="lwin"/"rwin" .../>` — il valore letterale non è mai mostrato,
>       il CSS lo sovrascrive con `content:'\f17a'; font-family:"Font Awesome 5 Brands"` = icona
>       bandiera Windows vera (K2 mostrava "⊞" Unicode come sostituto testuale).
>     - **FN**: `<div class="keylighting" style="pointer-events:none;"><span data-key="FN" .../></div>`
>       — Base Camp stessa disabilita il click sul tasto FN (pointer-events:none) ma lo lascia dentro
>       il div "keylighting", quindi resta illuminabile/preview RGB ma non cliccabile/configurabile.
>     - BaseCampLinux (community, Python/USB raw) verificato ma non contiene asset di layout tastiera
>       (solo protocollo USB) — nessuna informazione aggiuntiva utile qui.
>   - **`MainWindow.Everest.cs`**:
>     - `BuildCornerLegend`: grid da 2×2 a **3×3 con riga/colonna spaziatrice** (3px/2px) tra i 4
>       angoli — a 30px i testi corner erano quasi a contatto; la spaziatrice crea un gap visibile
>       senza spostare gli angoli stessi.
>     - Nuovo `BuildWinIcon()`: piccola bandiera Windows disegnata a mano (4 `Rectangle` 2×2 con gap)
>       — Segoe MDL2 Assets non ha un glifo "logo Windows" (Microsoft lo esclude deliberatamente) e
>       K2 non porta Font Awesome, quindi si disegna la forma invece di usare un font.
>     - `BuildEverestKeyboardOverlay`: nuovi case per `MatrixId` 91/92 (Win → icona) e 9 (Tab →
>       "TAB" + "⇆" due righe), prima del ramo generico corner/due-righe/singolo.
>     - `EvKeyboardButton_Click`: guard `if (matrixId == 261) return;` DOPO `TryCustomPaint` (la
>       paint mode per l'illuminazione custom resta attiva) ma PRIMA di capture/apertura dialog
>       azione — FN non entra più in `_evKeys`/non apre `ButtonActionDialog` da overlay.
>     - `BtnEvConfig_Click`: stesso guard se FN è già in lista (es. da un vecchio import DB) —
>       "Configura" si rifiuta con un log invece di aprire il dialog.
>     - Tooltip overlay: aggiunge "— riservato, non configurabile" per FN (0x{...}).
>   - **`KeyboardLayout.cs`**: `(91, "⊞", ModW)`/`(92, "⊞", ModW)` → `(91, "Win", ModW)`/
>     `(92, "Win", ModW)` (solo per il tooltip, il rendering visivo ora usa sempre l'icona).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) — richiesto kill
>     di un'istanza K2.App aperta (bloccava `K2.App.exe`, MSB3027). **Da verificare dall'utente**:
>     aspetto reale in app (spaziatura corner, icona Tab/Win, comportamento click su FN) — nessun
>     modo di fare screenshot WPF da qui.
>
> Previous: 2026-07-09 (Everest: aspetto keycap — testo nero/bianco/personalizzato, bordi Normal/Translucent col colore keycap):
>   - **Richiesta utente** (correzioni alla feature "aspetto keycap" di poco fa):
>     1. anche il colore del testo va offerto come nero/bianco/personalizzato (non solo uno swatch
>        diretto), default bianco; il colore keycap resta di default nero (già così, confermato);
>     2. i bordi di Normal e Translucent devono seguire il colore keycap selezionato, non restare
>        neri fissi come deciso nella richiesta precedente — correzione esplicita, ribalta quel punto.
>   - **`MainWindow.KeycapAppearance.cs`**: `RbEvKeycapTextBlack`/`White`/`Custom` (radio, stesso
>     pattern di `RbEvKeycapBlack`/`White`/`Custom`) al posto dello swatch diretto; nuovo campo
>     `_evKeycapTextColorMode` + `_evKeycapTextCustomHex`, chiavi store `settings.keycap_text_color_mode`/
>     `settings.keycap_text_custom_hex` (rinominata da `settings.keycap_text_hex`, nessuna necessità di
>     migrazione: feature introdotta in questa stessa sessione, mai rilasciata). Estratti helper
>     `ParseColorMode`/`ColorModeToString` condivisi tra keycap-color e text-color per evitare
>     duplicazione dello switch black/white/custom.
>     `ApplyKeycapAppearanceToAllKeys`: semplificato — ora SOLO lo stile **Pudding** ha un trattamento
>     speciale per `BorderBrush`/`Mount` (baseline dark, poi live LED per-tick); Normal, Translucent
>     e Reverse Pudding condividono lo stesso ramo "bordo + Mount = colore keycap statico" (identico
>     per Normal/Translucent che lo tengono fisso, e per Reverse Pudding che ci parte come baseline
>     prima che il tick LED sovrascriva `Background`). Rimosso `Mount.ClearValue(...)`: non serve più,
>     ora ogni stile imposta sempre esplicitamente `Mount.Background` (prima serviva solo per
>     ripristinare il default nero del ControlTemplate quando si tornava a Normal/Translucent).
>   - **Localizzazione**: nessuna nuova chiave necessaria (le label degli stili non menzionavano già
>     "bordi neri fissi", quindi la correzione non ne rende nessuna stale).
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su entrambe le solution — killata
>     un'altra istanza K2.App.exe rimasta aperta. **Da verificare dall'utente**: che Normal/Translucent
>     mostrino ora il bordo (e il Mount) del colore keycap scelto, che il color-picker del testo
>     nero/bianco/personalizzato funzioni e persista, e il comportamento di Pudding/Reverse Pudding
>     (invariato in questa sessione).
>
> Previous: 2026-07-09 (Everest: bug reale trovato — testo dei tasti CLIPPATO, non un problema di font):
>   - **Richiesta utente**: screenshot affiancato K2 (sopra) vs Base Camp reale (sotto) sullo stesso
>     layout IT, chiedendo di individuare le differenze di font e riprodurle.
>   - **Root cause reale (non il font)**: sui tasti stretti (30px) con etichette lunghe ("Prt Sc",
>     "Scroll Lk", "Pause", "Insert", "Home", "PgUp", "Del", "End", "PgDn") il `TextBlock` aveva
>     `TextWrapping.NoWrap` a font-size 8px: il testo trabocca oltre il bottone e viene TAGLIATO dal
>     clip automatico che WPF applica quando un `Border` ha `CornerRadius>0` (il `Face` di
>     `EverestKeyStyle` ha `CornerRadius="3"`) — risultato: "nser" invece di "Insert", "'ause" invece
>     di "Pause", ecc. Base Camp (CSS `.keyboard span` = `white-space: normal` di default, i.e. va a
>     capo) non ha questo problema perché il browser wrappa il testo su più righe nello stesso box.
>   - **Verificato in `keyboard.css`** (`Mountain Base Camp/resources/bin/wwwroot/css/keyboard.css`):
>     nessun `@font-face` custom per le legende — `font-family: system-ui, sans-serif` (= Segoe UI su
>     Windows), stesso valore già usato da K2 (`_evKeyFont`): **il font non era il problema**.
>     Trovate anche regole `content:` esplicite CSS-confermate (non supposizione): lshift/rshift →
>     `'SHIFT'`, lctrl/rctrl → `'CTRL'`, lalt/ralt → `'ALT'`, space → **nessuna label** (`content:
>     none`) — K2 mostrava invece "LShift"/"RShift"/"LCtrl"/"RCtrl"/"LAlt"/"RAlt"/"Space" (nomi
>     interni, mai pensati per essere il testo visibile sul tasto).
>   - **`MainWindow.Everest.cs`** (`BuildEverestKeyboardOverlay`, branch senza alt/AltGr): ora abilita
>     `TextWrapping.Wrap` (invece di `NoWrap`) + font più piccolo (`fsMulti` invece di `fs`) quando
>     `kd.Label` contiene uno spazio — le etichette corte a una parola (Esc, F1, Tab...) non vanno mai
>     a capo quindi il comportamento per loro è invariato. Tooltip: se `Label` è vuota (barra spazio)
>     mostra "Space" invece di stringa vuota.
>   - **`KeyboardLayout.cs`**: rinominate le label per matchare esattamente Base Camp — `LCtrl`/`RCtrl`
>     → `CTRL`, `LAlt`/`RAlt` → `ALT`, `LShift`/`RShift` → `SHIFT` (CSS-confermato, in entrambi
>     `BuildBoardLeft_AnsiUs` e `BuildBoardLeft_Iso`), `Space` → stringa vuota (CSS-confermato).
>     `Prt Sc`→`PRT SCN`, `Scroll Lk`→`SCR LK`, `Pause`→`PAUSE`, `Insert`→`INSERT`, `Home`→`HOME`,
>     `PgUp`→`PG UP`, `Del`→`DELETE`, `End`→`END`, `PgDn`→`PG DN`, `Caps Lk`→`CAPS LOCK` (questi da
>     lettura diretta dello screenshot Base Camp allegato dall'utente, non da CSS — tutto maiuscolo,
>     abbreviazioni a 2 parole così vanno a capo nel box 30px invece di uscire clippate). Numpad:
>     `Num Lk`→`NUM LOCK`, `Enter`→`ENTER`.
>   - **Non toccato/rinviato**: il numpad reale di Base Camp sembra mostrare doppie etichette
>     cifra+funzione-nav sui tasti 7/9/1/3/0/. (Home/PgUp/End/PgDn/Ins/Del quando NumLock è off) —
>     `BuildBoardRight()` oggi mostra solo la cifra. Non implementato: incerto sul formato esatto
>     senza uno screenshot ravvicinato del numpad, e comunque fuori dallo scope del bug segnalato
>     (etichette clippate). Da riprendere se l'utente lo nota/richiede.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) — richiesto
>     kill di un'istanza K2.App aperta che bloccava `K2.Core.dll` (MSB3027). **Da verificare
>     dall'utente**: confronto visivo diretto in app (nessun modo di fare screenshot WPF da qui).
>     Nota: `K2.sln`/`K2.Core` condivisi con un'altra sessione in corso in parallelo (feature
>     "aspetto keycap") — vedi voce successiva.
>
> Previous: 2026-07-09 (Everest: aspetto keycap spostato nelle impostazioni del device, colore testo, bordo Mount):
>   - **Richiesta utente** (correzioni alla feature della sessione precedente):
>     1. la sezione va nelle impostazioni del **device Everest**, non in quelle generali dell'app;
>     2. aggiungere un colore del testo, per i keycap normal/pudding/reverse pudding;
>     3. per pudding e reverse pudding, il bordo inferiore (il "Mount" scuro visibile nel gap
>        sotto la Face) restava nero fisso — deve colorarsi come gli altri 3 lati (LED per
>        pudding, colore keycap statico per reverse pudding);
>     4. conferma che per normal/translucent tutti e 4 i bordi (compreso il Mount) restano neri
>        fissi, mai colorati dall'impostazione.
>   - **Persistenza spostata da `AppSettings` (JSON app-wide) a `EverestStore`** (SQLite
>     per-device, tabella `Settings`, stesso store di `settings.game_mode`/`settings.keyboard_color`):
>     nuove chiavi `settings.keycap_color_mode` (black/white/custom), `settings.keycap_custom_hex`,
>     `settings.keycap_text_hex` (nuovo, colore testo), `settings.keycap_style` (0-3). Gli enum
>     `EverestKeycapColorMode`/`EverestKeycapStyle` sono ora `internal` in `K2.App` (rimossi da
>     `K2.Core/AppSettings.cs`, non più app-wide).
>   - **`MainWindow.xaml`**: rimosso il GroupBox dal tab Impostazioni generale (`PnlSettings`);
>     nuovi controlli aggiunti dentro `PnlSecSettings` (sezione "Settings" del tab Everest,
>     insieme a Game Mode/Indicator LED/Keyboard color/Layout): 3 RadioButton colore keycap +
>     swatch personalizzato, nuovo swatch "Colore testo" (singolo, no varianti nero/bianco/
>     personalizzato — è già un color picker diretto), ComboBox tipo keycap.
>   - **`MainWindow.KeycapAppearance.cs`**: `EvKeyVisual` esteso con `Mount` (oltre a `Button`/
>     `Halo`), trovato via `FindName("Mount", btn)` come già per `LedHalo`. `ApplyKeycapAppearanceToAllKeys`/
>     `ApplyEverestLedColor`: per **Pudding**, `Mount.Background` segue lo stesso brush di
>     `BorderBrush` ad ogni tick (colore LED se acceso, altrimenti il dark default `#1D1D1D`); per
>     **Reverse Pudding**, `Mount.Background` = stesso brush statico del colore keycap scelto
>     (baseline, mai per-tick, coerente col fatto che in questo stile è `Background`/centro a
>     seguire il LED, non il bordo); per **Normal/Translucent**, `Mount.ClearValue(Border.
>     BackgroundProperty)` ripristina il default del ControlTemplate (nero fisso) — necessario
>     perché un valore locale via codice ha precedenza MAGGIORE del TemplatedParent template
>     trigger dell'hover, quindi un vecchio local value lasciato da Pudding/ReversePudding
>     "silenzierebbe" per sempre l'hover su Mount se non veniva ripulito passando a un altro stile.
>     **Nota**: colorare `Mount` da codice (a differenza di `Face`, mai toccato direttamente,
>     solo via `Button.Background`/`BorderBrush` + TemplateBinding) fa perdere l'hover-brightening
>     su quella striscia di 6px in stile Pudding/ReversePudding — richiesto esplicitamente
>     dall'utente (bordo sempre colorato), quindi accettato come effetto collaterale minore.
>     Nuovo `ResolveEverestKeycapTextColor`/`BtnEvKeycapTextColor_Click` (stesso pattern
>     ColorDialog del colore keycap); applicato al `Foreground` della legenda per Normal/Pudding/
>     ReversePudding, MAI per Translucent (che continua a colorare la legenda dinamicamente col
>     LED live, invariato — l'impostazione "colore testo" la esclude di proposito).
>   - **`MainWindow.Everest.cs`**: `InitEverestSettingsPanel` chiama il nuovo
>     `InitKeycapAppearanceControls` (setup ItemsSource combo); `LoadEverestSettingsFromStore`
>     chiama il nuovo `LoadKeycapAppearanceFromStore` — stesso flag `_evSettingsSuppress`
>     condiviso con Game Mode/Keyboard color (niente flag dedicato separato come nella prima
>     versione della feature).
>   - **Localizzazione**: chiavi `settings_ev_keycap_*` spostate vicino a `settings_keyboard_color`
>     (sezione Everest, non più sezione Settings generale); nuova chiave `settings_ev_keycap_text_color`
>     (EN + IT).
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su entrambe le solution
>     (killato un altro K2.App.exe rimasto aperto, PID diverso dalla sessione precedente).
>     **Da verificare dall'utente**: aspetto a schermo con LED accesi/spenti nei 4 stili, hover
>     sui tasti in ciascuno stile (in particolare la perdita di hover-brightening sul Mount in
>     Pudding/ReversePudding, minore e attesa), e che i valori persistano correttamente riavviando K2.
>
> Previous: 2026-07-08 (Everest: nuova impostazione "Aspetto tastiera Everest" — colore/tipo keycap):
>   - **Richiesta utente**: aggiungere un'impostazione per il colore dei keycap (nero/bianco/
>     personalizzato) e il tipo di keycap (normale/traslucido/pudding/reverse pudding), dove il
>     tipo determina come il colore LED live si combina col colore keycap nell'anteprima a
>     schermo. **Solo cosmetico**: non tocca i keycap fisici (fissi dalla produzione) né manda
>     nulla al device — l'effetto RGB reale resta il pannello "Illuminazione RGB" esistente.
>   - **`K2.Core/AppSettings.cs`**: nuovi enum `EverestKeycapColorMode` (Black/White/Custom) e
>     `EverestKeycapStyle` (Normal/Translucent/Pudding/ReversePudding), persistiti in
>     `app_settings.json` (stesso file JSON app-wide di CloseToTray/DebugMode/..., non per-profilo).
>   - **`MainWindow.xaml`**: `EverestKeyStyle` — aggiunto layer `LedHalo` (Border con Margin
>     negativo, dietro Mount, per l'alone che sborda nel gap tra tasti) e rimosso `LedTint`
>     (il vecchio "wash" traslucido sull'intera faccia, non più usato da nessuno dei 4 stili).
>     `Face.Background`/`BorderBrush` restano `TemplateBinding` sul `Button.Background`/
>     `BorderBrush`: il codice imposta questi ultimi (non `Face` direttamente), così il trigger
>     hover — che punta a `Face` per nome — mantiene precedenza su mouse-over (local value su
>     `Face` vincerebbe sempre sul trigger, rompendo l'hover). Nuovo GroupBox "Aspetto tastiera
>     Everest" nel tab Impostazioni: 3 RadioButton colore + swatch personalizzato (ColorDialog,
>     stesso pattern del pannello RGB) + ComboBox tipo (4 scelte, pattern `EvEffectChoice`-style).
>   - **`MainWindow.KeycapAppearance.cs`** (nuovo file): `EvKeyVisual` (record struct Button+Halo),
>     `ApplyKeycapAppearanceToAllKeys` (baseline statica, richiamata dopo ogni rebuild della
>     tastiera — nuovi Button ripartono dai default di stile), `ApplyEverestLedColor`/
>     `ResetEverestKeyToOff` (applicano il colore LED live al layer giusto per lo stile corrente:
>     Halo per Normal/Translucent, + foreground legenda per Translucent, `BorderBrush` per
>     Pudding, `Background` per Reverse Pudding), `SetLegendForeground` (gestisce i 3 shape di
>     `Button.Content` — singolo `TextBlock`, `StackPanel` a due righe, `Grid` 4 angoli da
>     `BuildCornerLegend` — tutti già bianchi fissi in `BuildEverestKeyboardOverlay`).
>   - **`MainWindow.LedPreview.cs`**: `_evKeyTints` (Dictionary&lt;int,Border&gt;) → `_evKeyVisuals`
>     (Dictionary&lt;int,EvKeyVisual&gt;); `BuildEverestLedTints` → `BuildEverestKeyVisuals` (cattura
>     anche il Button, non solo l'ex-LedTint); `OnEverestColorsUpdated`/`OnSdkCrashDetected`/
>     `UpdateEverestLedPreviewActive` delegano ora a `ApplyEverestLedColor`/`ResetEverestKeyToOff`
>     invece di scrivere `Background` direttamente. MacroPad (`_mpKeyTints`, `KeyCapStyle`) non
>     toccato — la feature è solo Everest, come richiesto.
>   - **Nota nascosta**: in stile Translucent, un eventuale "flash" bianco/nero della legenda al
>     press (logica esistente altrove in MainWindow.Everest.cs) può essere sovrascritto dal
>     prossimo tick del poller LED (~ogni 100ms) se quel tasto è acceso — edge case cosmetico
>     minore, non bloccante.
>   - **Localizzazione**: nuove chiavi `settings_ev_keycap_*` (EN + IT).
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su entrambe le solution, dopo
>     aver killato un K2.App.exe (PID 66036) rimasto aperto e bloccava `K2.Core.dll`.
>     **Da verificare dall'utente**: aspetto a schermo dei 4 stili con LED accesi/spenti, e che
>     l'hover sui tasti resti visibile in ciascuno stile.
>
> Previous: 2026-07-08 (Everest: colore/dimensione legende tasti multi-simbolo, come da riferimento fisico):
>   - **Richiesta utente**: allegata foto di una tastiera IT reale con più simboli per tasto,
>     chiedendo di far combaciare il rendering K2 (font/layout dei tasti con più legende).
>   - **Contesto**: in una sessione precedente (2026-06-28) `BuildCornerLegend()` era stata
>     "appiattita" a tutto bianco per uniformarsi alla CSS del sito Base Camp (`keyboard.css`),
>     perdendo la distinzione colore grigio/teal che una versione di lavoro ancora precedente
>     nella stessa sessione aveva. Il commento in `KeyLabelMap.cs` (mai aggiornato) descriveva
>     comunque l'intento originale: "engraved... often in a different colour".
>   - **`MainWindow.Everest.cs`**: reintrodotta la distinzione, stavolta esplicita — `_evBaseBrush`
>     (bianco), `_evShiftBrush` (grigio `#9A9AA2`, stesso muted-text del tema K2),
>     `_evAltGrBrush`/Shift+AltGr (teal `#5BBEC3`, lo stesso già usato in questo file per l'accento
>     del selettore layout). `BuildCornerLegend` ora accetta due font-size separate (`fsCorner` per
>     shift/AltGr/Shift+AltGr, `fsBase` per il carattere base) invece di una sola uguale per tutti i
>     4 angoli: il base resta nello stesso angolo basso-sx ma più grande e bianco, dominante come su
>     una keycap reale, mentre gli angoli modificatori restano piccoli e colorati. Stessa logica
>     nel path a due righe (tasto con solo shift, niente AltGr): riga alt (shift) grigia più piccola,
>     riga base bianca più grande (prima erano identiche, entrambe bianche 7px).
>   - **Font**: verificato che resta `_evKeyFont` = "Segoe UI,system-ui,Arial,sans-serif" (nessun
>     cambio necessario, il problema segnalato era colore/gerarchia dimensionale, non il font stesso).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0/0) dopo aver killato
>     un'istanza K2.App rimasta aperta (bloccava `K2.Core.dll`, MSB3027 — vedi `stop-k2.bat`).
>     **Da verificare dall'utente**: confronto visivo diretto con la foto allegata (l'ambiente di
>     sviluppo non ha un modo di fare screenshot della UI WPF).
>
> Previous: 2026-07-08 (DisplayPad: click su un tasto fuori da Key Binding porta lì):
>   - **Richiesta utente**: "Su displaypad, se clicco su un tasto e non sono in Key binding,
>     portamici".
>   - **`MainWindow.DisplayPad.cs`**: `DpKeyButton_Click` — il ramo che prima faceva
>     `if (!IsDpKeyBindingSectionActive) return;` (no-op silenzioso cliccando un tasto mentre si
>     è su Positioning/Pages) ora imposta `RbDpSecKeyBinding.IsChecked = true` prima di uscire,
>     spostando l'utente sulla sezione Key Binding. Non apre subito il dialog azione sullo stesso
>     click (il click era ambiguo tra "portami in Key Binding" e "configura questo tasto"):
>     serve un secondo click, ora che si è nella sezione giusta. Navigazione cartella/back
>     (`dp_folder`/`dp_back`) invariata: continua a funzionare da qualunque sezione.
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0 errori/0 warning).
>     **Da verificare dall'utente**: comportamento su hardware reale.
>
> Previous: 2026-07-08 (DisplayPad: sezione "Pages" per eliminare le pagine cartella create):
>   - **Richiesta utente**: "ho bisogno di una funzione che mi permetta di eliminare le vecchie
>     pagine del displaypad già create. Avevo detto di fare una seconda sezione sotto le sezioni
>     del displaypad chiamata 'pages'. Metti un bottone 'elimina' in hover con prompt di conferma".
>   - **`MainWindow.xaml`**: nuovo `RadioButton RbDpSecPages` ("Pages") nella sidebar SECTIONS del
>     DisplayPad, sotto "Positioning". Nuovo pannello `PnlDpSecPages`: `ItemsControl LstDpPages`
>     con `DataTemplate` per riga (nome pagina + bottone "Elimina" con glifo cestino, `Visibility`
>     Collapsed di default, resa Visible via `DataTemplate.Triggers`/`IsMouseOver` sulla `Border`
>     della riga — hover-reveal). `TextBlock LblDpNoPages` per lo stato vuoto.
>   - **`MainWindow.SectionNav.cs`**: `DpSection_Changed` aggiunto case `RbDpSecPages` →
>     `PnlDpSecPages`; `ShowDpSection` richiama `RefreshDpPagesList()` quando quel pannello
>     diventa attivo (la lista può risultare stale se una pagina è stata creata/rinominata altrove,
>     es. dal nuovo tipo azione "Pagina" in `ButtonActionDialog` — più semplice ricalcolarla ogni
>     volta che la sezione torna visibile che tracciare ogni punto di modifica).
>   - **`MainWindow.DisplayPad.cs`**: `_dpPages` (`ObservableCollection<DpPageRow>`, nuovo record)
>     bindato a `LstDpPages`. `RefreshDpPagesList()` — rilegge `DisplayPadStore.ListPages` per il
>     device+profilo correnti. `BtnDpDeletePage_Click` — prompt di conferma (`MessageBox.Show`,
>     stesso pattern di `BtnDpDeleteProfile_Click`), poi `DpDeletePage`. `DpDeletePage(deviceId,
>     profile, pageId)` — cancella lo store; se la pagina eliminata è quella attualmente mostrata
>     sulla griglia, naviga fuori (`DpNavigateBack`/`ResetDpNavigation`) prima di ricaricare, per
>     non lasciare la UI a mostrare tasti appena cancellati dal DB.
>   - **`DisplayPadStore.cs`**: nuovo `DeletePage(deviceId, profile, pageId)` — cancella le righe
>     `Buttons` della pagina, il nome salvato, e (via nuovo helper privato
>     `ClearActionEverywhere`) azzera `ActionType`/`ActionValue` (mantenendo l'immagine) di
>     qualunque tasto altrove nello stesso device+profilo che puntava a quella pagina con
>     `dp_folder`, così non resta un link morto. **Non ricorsivo**: eventuali sotto-pagine
>     annidate DENTRO la pagina eliminata non vengono cancellate a cascata — restano nel DB ma
>     diventano irraggiungibili; fuori scope per questa prima versione.
>   - **Localizzazione**: nuove chiavi `dp_pages`, `dp_no_pages`, `dp_delete_page`,
>     `dp_delete_page_title`, `dp_delete_page_confirm` (EN + IT).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0 errori/0 warning) —
>     richiesto `stop-k2.bat`/`Stop-Process` prima perché l'utente aveva K2.App aperto (PID
>     bloccava `K2.Core.dll`). **Da verificare dall'utente**: comportamento reale della lista/hover
>     e dell'eliminazione su hardware.
>
> Previous: 2026-07-08 (DisplayPad: icone cartella/pagina — rotazione doppia, icone
> reali di Windows, template Base Camp, azione "Pagina" nel dialog azioni):
>   - **Richiesta utente**: partita da "l'icona della cartella arriva ruotata e col testo
>     verticale su un DisplayPad ruotato di 90°", poi evoluta via via in: usare l'icona
>     vera di Windows per le cartelle su disco, usare il PNG originale di Base Camp per le
>     icone-pagina invece di un glifo/disegno a codice, poter assegnare/rinominare una
>     pagina anche dal dialog "Configura azione" (non solo da "Crea cartella"), ripulire
>     l'icona quando si rimuove un'azione, angoli arrotondati nell'anteprima, e la lista
>     "recenti" delle cartelle che non funzionava.
>   - **Bug 1 — doppia rotazione cartella+testo**: `IconImageGenerator.TryGenerateFolderIcon`
>     "cuoceva" la counter-rotation del device dentro il PNG (convenzione usata altrove per
>     evitare di ruotare due volte all'upload), ma questo rompeva l'anteprima nel dialog
>     (che mostra il file grezzo) e rendeva l'icona illeggibile per qualunque rotazione
>     ≠0°. Rimossa la rotazione baked-in ovunque (`TryGenerateExecIcon`/`TryGenerateFolderIcon`
>     ora generano sempre upright): `DpKeyConfigDialog`/`CellConfigDialog`/`MainWindow.DisplayPad.cs`
>     non passano più `deviceRotation` al generatore, e la rotazione del device si applica
>     SOLO all'upload (`_dpRotation`/`EffectiveDpRotation`), come per qualsiasi altra immagine.
>   - **Bug 2 — glifo Segoe MDL2 sbagliato**: il commento diceva "OpenFolderHorizontal" ma
>     quel glifo non esiste in Segoe MDL2 Assets (solo "Folder"/E8B7 e "FolderOpen"/E838,
>     nessuno dei due è la sagoma larga attesa). Rimosso l'uso di font glyph.
>   - **Bug 3 — GUID COM sbagliato**: `IShellItemImageFactory` in `GetBestIcon` aveva il
>     GUID mistyped (`...8a20b1a396a3` invece di `...8a59c30c463b`), quindi l'estrazione
>     dell'icona "jumbo" di Windows falliva SEMPRE silenziosamente — mascherato per i file
>     dal fallback `Icon.ExtractAssociatedIcon` (che funziona su file ma non su cartelle),
>     quindi le cartelle non avevano mai avuto un'icona reale. Corretto il GUID: ora
>     `TryGenerateDiskFolderIcon` (nuovo metodo, per "folder" = cartella reale su disco)
>     estrae davvero l'icona gialla di Explorer.
>   - **Nuovo: template PNG reale per le pagine**: su richiesta esplicita, l'icona delle
>     pagine DisplayPad (`TryGenerateFolderIcon`, azione "dp_folder", nessun path reale)
>     ora usa l'asset originale `BaseCampLinux/resources/DPFolder.png` (copiato in
>     `K2.Core/Assets/dp_folder_template.png`, embedded resource), ritagliato sulla sagoma
>     e ricolorato dal blu originale all'accento K2 via remap sul canale blu (l'asset non
>     ha alpha, sfondo nero pieno). **Bug GDI+ trovato nel farlo**: `Bitmap(Stream)` può
>     restare legato pigramente allo stream sottostante — chiudere lo `stream` subito dopo
>     la costruzione (con `using`) causava un "Out of memory" fuorviante al primo uso reale
>     del bitmap; risolto clonando (`new Bitmap(lazy)`) per staccarlo dallo stream prima
>     che venga chiuso. Icona-pagina e icona-cartella-reale condividono ora lo stesso
>     riquadro (`IconBox`: quadrato 56% del tile, offset 8% dall'alto) così le due
>     compaiono alla stessa dimensione/allineamento sulla griglia.
>   - **Nuovo: azione "Pagina" nel dialog "Configura azione"**: prima l'unico modo per
>     creare un tasto "dp_folder" era il menu tasto destro "Crea cartella" — il dialog
>     standard non sapeva nulla di "dp_folder" (rischio concreto: aprendolo su un tasto
>     già assegnato a una pagina, il combo tipo-azione ripiombava su "Nessuna" e salvare
>     avrebbe cancellato l'azione). Aggiunta voce "Pagina" (`ButtonActionDialog.Page.cs`,
>     nuovo file) con combo pagine esistenti + "Nuova pagina" + campo nome editabile
>     (rinomina la pagina esistente se cambiato, anche a parità di ID pagina — segnalato
>     al chiamante via `PageIconNeedsRefresh` perché rigeneri l'icona con la didascalia
>     aggiornata). Aggiunto `IActionHost.ListPages/CreatePage/RenamePage/SupportsPages`
>     (K2.Core) con implementazione reale solo in `DisplayPadActionHost`; no-op/vuoto per
>     MacroPad/Everest/K2.DisplayPad standalone (nessun concetto di "pagina" lì — la voce
>     non compare proprio nel combo, non solo vuota, a differenza di "macro"). Aggiunto
>     `DisplayPadStore.ListPages`/`RenamePage` (query `DISTINCT ActionValue` su
>     `ActionType='dp_folder'` scoped per device+profilo).
>   - **Rimozione azione pulisce anche l'icona**: `BtnRemoveAction_Click` in
>     `DpKeyConfigDialog`/`NdkKeyConfigDialog`, e i menu tasto destro
>     `DpMnuRemoveAction_Click`/`NdkMnuRemoveAction_Click`, ora azzerano anche l'immagine
>     (prima restava l'icona vecchia orfana). Rimossa la voce ridondante "Rimuovi icona"
>     dal menu tasto destro (DisplayPad ed Everest) — resta solo aggiungi/modifica.
>   - **Angoli arrotondati anteprima**: `CropEditor` (usato da `DpKeyConfigDialog`,
>     `CellConfigDialog`, e via `ImageCropDialog` da Everest) ora clippa il viewport con
>     angoli arrotondati (stesso raggio della guida "mostra contorno tasto" già presente)
>     solo per il caso 1×1 (tasto singolo) — resta rettangolare per la vista fullscreen
>     multi-tasto, dove un unico raggio su più tasti non avrebbe senso.
>   - **Bug lista "recenti" cartelle/eseguibili**: `AppSettings.RecentFolderPaths`/
>     `RecentExecPaths` restituiscono sempre la STESSA istanza `List<string>` (mutata
>     in-place) — riassegnare `ListBox.ItemsSource` a quel riferimento identico è un
>     no-op per WPF (niente `INotifyCollectionChanged`), quindi il tasto rimuoveva
>     davvero il percorso dalle impostazioni ma la lista visibile non si aggiornava mai.
>     Fix: `.ToList()` ad ogni refresh per forzare un riferimento nuovo. Ristilizzata la
>     lista in dark (stesso schema del tema, prima bianca di default WPF) e il tasto
>     rimozione ora è un'icona cestino (stesso glifo Segoe MDL2 già usato altrove)
>     visibile solo in hover sulla riga.
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su entrambe le
>     solution, ripetuto ad ogni step. **Da verificare dall'utente su hardware**: aspetto
>     reale delle icone-pagina e icone-cartella su un DisplayPad fisico a rotazione ≠0°
>     (verificato solo via rendering diretto lato PC in questa sessione).
>
> Previous: 2026-07-08 (DisplayPad: fix icone perse alla riconnessione USB):
>   - **Richiesta utente**: "serve che quando si ricollega un dispositivo vengano
>     ricaricati i profili... se ad esempio ricollego un DisplayPad, resta senza icone".
>   - **Root cause** in `MainWindow.DisplayPad.cs` → `DpRefreshDevices()`: il flag
>     `currentlyOnDp` (ero già sulla tab del DisplayPad?) veniva calcolato leggendo
>     `TcDevices.SelectedItem` **dopo** `RemoveDeviceTabs("dp_")` — che però rimuove e
>     ricrea le tab `dp_*`, quindi WPF sposta automaticamente la selezione altrove PRIMA
>     di quel controllo. Risultato: al replug il branch che ri-seleziona la tab e triggera
>     `DpActivateDevice`/`DpReloadAndPreloadProfile` (re-upload icone) non scattava mai —
>     la memoria icone on-board del device non sopravvive a uno scollegamento USB, quindi
>     senza quel re-upload il pannello fisico resta vuoto.
>   - **Fix**: il flag (rinominato `wasOnDpTab`) è ora catturato in cima al metodo, PRIMA
>     di `RemoveDeviceTabs`. Nessun'altra logica toccata: `DpSwitchProfile`/`DpActivateDevice`
>     restano invariati, si accendono di nuovo correttamente sul reconnect.
>   - **Verificato solo per il MacroPad** (per esclusione, non serviva fix): `RefreshDevices()`
>     in `MainWindow.xaml.cs` non fa gestione dinamica di tab per-device (unica tab statica)
>     e richiama sempre `CbDevice_SelectionChanged` se il pannello è visibile — nessun bug
>     analogo lì.
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning, dopo aver killato
>     un'istanza fantasma di K2.App che teneva bloccata `K2.Core.dll`). **Da verificare
>     dall'utente su hardware**: scollega/ricollega un DisplayPad mentre la sua tab è
>     attiva e conferma che le icone tornano.
>
> Previous: 2026-07-08 (DisplayPad: icona auto-generata per le cartelle create da UI):
>   - **Richiesta utente**: "quando aggiungi una pagina, aggiungi un'icona con una freccia
>     o qualcosa del genere e il nome della pagina. Segui le regole delle icone generate
>     da cartelle" — `DpMnuCreateFolder_Click` (feature "Create folder" appena introdotta
>     dalla sessione precedente) creava la sotto-pagina e assegnava l'azione `dp_folder`
>     ma lasciava il tasto senza immagine (tile vuoto).
>   - **`MainWindow.DisplayPad.cs`**: nuovo helper `DpAutoIconCachePath(kind, sourceValue,
>     deviceRotation)` — stesso schema hash/cache-root di `DpKeyConfigDialog.AutoIconCachePath`
>     (root `DpAutoIconDir`, così `EffectiveDpRotation` riconosce il risultato come già
>     pre-ruotato). `DpMnuCreateFolder_Click` ora genera l'icona via
>     `IconImageGenerator.TryGenerateFolderIcon(name, ...)` — stesso glifo+didascalia già
>     usato per l'azione "folder" (Open Folder) — e la carica con `DpUploadAndPersist`;
>     fallback a `SaveButton` diretto se la generazione fallisse (azione comunque persistita).
>   - **Verificato**: `dotnet build K2.sln -c Debug -p:Platform=x86` pulito (0 errori/0
>     warning). **Da verificare dall'utente**: aspetto reale del tile su hardware dopo la
>     creazione di una cartella.
>   - **Non toccato in questa sessione**: `DpMnuSetBack_Click` (tasto "Imposta Back") resta
>     senza icona auto-generata — l'utente ha chiesto esplicitamente solo il caso "aggiungi
>     una pagina"; possibile follow-up simmetrico se richiesto.
>
> Previous: 2026-07-08 (Macro: aggiunta duplicazione macro):
>   - **Richiesta utente**: "aggiungi una funzione di duplicazione delle macro".
>   - **`Models/KeyboardMacro.cs`**: `MacroInput.Clone()` (copia indipendente
>     di un singolo evento registrato) e `MacroDefinition.Clone(newName)`
>     (copia completa — stesse impostazioni Devices/Delay/Playback, `Inputs`
>     copiato in profondità via `List.ConvertAll(i => i.Clone())` così
>     riordinare/eliminare righe sulla copia non tocca mai la macro
>     sorgente; `Id`=0, lasciato assegnare dall'insert).
>   - **UI**: nuovo bottone "Duplica" nella Macro Library, tra New e Delete
>     (`BtnMacroDuplicate_Click` in `MainWindow.Macro.cs`) — clona la macro
>     selezionata come "{Nome} (copia)", la inserisce nello store e la
>     seleziona subito. Bloccato durante la registrazione, stesso pattern
>     delle altre guardie introdotte nella sessione precedente (no `IsEnabled`,
>     solo controllo funzionale).
>   - **Localizzazione**: nuove chiavi `macro_duplicate`/`macro_duplicate_name`
>     tradotte in EN+IT.
>   - **Verificato**: `build-check.bat` pulito (0 errori/0 warning) su
>     entrambe le solution. **Da verificare dall'utente**: comportamento su
>     hardware/UI reale (duplicazione con Inputs registrati, assegnazioni
>     non copiate — corretto, dato che "Assigned to" è per-nome-macro e la
>     copia ha un nome diverso).
>
> Previous: 2026-07-08 (2 richieste utente: font/dimensione nell'editor testo-su-icona,
> pulsante "Indietro" per le sottopagine DisplayPad):
>   - **Font e dimensione nel testo generato su icona**: `TextIconGenerator.DrawFittedText`
>     (K2.Core) aveva "Segoe UI" e la formula di partenza `size*0.42f` cablati. Aggiunti
>     parametri opzionali `fontFamily`/`fontSize` a `TryRenderTextIcon`/`TryGenerateTextIcon`
>     (default `null` = comportamento di prima, invariato per compatibilità coi call site
>     esistenti). `fontSize` è trattato come dimensione di PARTENZA per il loop di shrink
>     esistente (mai ingrandito, solo rimpicciolito se il testo non ci sta) — così una
>     scelta manuale non può mai causare overflow/clipping. Gestita anche la (rara) eccezione
>     GDI+ quando un font non ha una variante Bold (`Font` la lancia solo per lo stile, non
>     per un nome di famiglia sconosciuto, che invece sostituisce silenziosamente) — fallback
>     a `FontStyle.Regular` invece di far fallire l'intera generazione. UI in
>     `TextIconDialog.xaml(.cs)`: nuova `ComboBox` famiglia (popolata da
>     `Fonts.SystemFontFamilies`, default "Segoe UI" se presente) + `CheckBox` "Dimensione
>     automatica" (default, comportamento di prima) + `Slider` (8..size*0.9) abilitato solo
>     quando l'auto-fit è disattivato. **Stesso gotcha RadioButton/ToggleButton già visto
>     per `RbBgSolid`**: `ChkAutoSize IsChecked="True"` e la coercizione di `Slider.Value`
>     al nuovo `Minimum` durante `InitializeComponent()` fanno scattare `Checked`/
>     `ValueChanged` PRIMA che i controlli fratelli dichiarati più sotto nello stesso XAML
>     (`SldFontSize`, `LblFontSizeValue`) siano collegati — entrambi gli handler hanno una
>     guardia `if (... is null) return;` per tollerarlo, stesso pattern del fix precedente.
>     Nuove chiavi loc `txt_font_label`, `txt_font_size_auto` (EN+IT).
>   - **Pulsante "Indietro" per le sottopagine DisplayPad**: l'utente segnalava "se elimino
>     il tasto Back dentro una cartella, come torno indietro?". Verificato: esiste già dalla
>     sessione del 2026-06-29 un pulsante `BtnDpBack` + breadcrumb (`LblDpBreadcrumb`) nella
>     toolbar del tab DisplayPad (K2.App), del tutto INDIPENDENTE da qualsiasi tasto fisico/
>     software configurato con l'azione "dp_back" — mostrato/nascosto automaticamente da
>     `UpdateDpBreadcrumb()` in base a `_currentDpPageId` (0 = pagina radice → nascosto,
>     altrimenti visibile), quindi resta sempre disponibile per uscire da una sottopagina
>     anche senza alcun tasto Back configurato al suo interno. Nessun bug trovato nella
>     logica. Per eliminare ogni dubbio di scarsa visibilità, ristilizzato da
>     `K2IconButton` (grigio, si confondeva) a `K2IconAccentButton` (colore accento) —
>     unico pulsante non-debug visibile nella toolbar quando si è dentro una cartella.
>   - **Verificato**: `dotnet build` pulito (0 errori/0 warning) su entrambe le solution.
>     **Nota sessione**: durante la rebuild, `K2.App.exe` risultava già in esecuzione
>     (l'utente lo stava testando) e bloccava la copia di `K2.Core.dll` (MSB3027) — killato
>     con lo stesso approccio di `stop-k2.bat` prima di ricompilare.
>
> Previous: 2026-07-08 (4 richieste utente: crash "Aggiungi testo", doppia rotazione
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

