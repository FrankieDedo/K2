# K2 — TODO

## Roadmap 2026-07-11 (richiesta utente, non ancora iniziata)

### Everest Max
- [ ] Custom Lighting: interfaccia che permetta di dipingere i colori
  direttamente sulla tastiera (paint mode già esiste in
  `MainWindow.CustomLighting.cs`/`TryCustomPaint` — verificare cosa manca
  rispetto a "dipingere direttamente" o se è già questo e serve solo rifinire)
- [ ] Togliere i tasti con display (i 4 NDK del numpad) dalla modalità paint —
  oggi `ClearAllOverlays` include anche `CvsEvNumpad`, e non risulta un filtro
  che escluda gli NDK dal click di paint
- [ ] La paint mode deve colorare solo il "LED preview", non il tasto
  effettivo — oggi `ApplyColorOverlay` scrive direttamente su
  `keyButton.Background`, sovrascrivendo lo stile reale del tasto invece di
  un layer di anteprima separato
- [ ] Gestire il **LED ring** (side ring) sia in paint mode che nel LED
  preview — al momento il ring non risulta gestito nel modulo Everest Max
  (a differenza di Everest 60, che ha side ring 44 LED via `Everest60RgbPanel`)
- [ ] Hot-swap di numpad e media dock (collega/scollega a caldo, aggiorna la
  UI senza riavvio) — per Everest 60 esiste già l'auto-detect posizione
  numpad via poll (`Ev60RefreshStatus`/`QueryNumpadPosition`); Everest Max
  usa `byMMDockPlug`/`byNumpadPlug` in `MainWindow.Layout.cs` ma andrebbe
  verificato se il refresh è a caldo o solo all'avvio

### Everest 60
- [ ] Gestire il LED ring con paint mode e LED preview — il ring (44 LED,
  `Everest60RgbPanel`) oggi ha solo i preset RGB, non risulta integrato nel
  Key Lighting paint mode né in un "preview" separato
- [ ] Aggiungere i layout da Base Camp (Key Binding: layout tastiera
  multi-lingua/ISO — oggi `Everest60KeyboardLayout.MainBoard` è "un solo
  layout ANSI-like", e la sezione "Layout" in Settings risulta disabilitata,
  vedi `MainWindow.Everest60.cs`)

### Makalu 67
- [ ] Gestire il LED preview (mostrare lo stato luci corrente letto dal
  device, non solo impostarlo — pattern già usato per DisplayPad/MacroPad)
- [ ] Migliorare l'interfaccia delle azioni utilizzabili, renderla più simile
  a Base Camp — oggi il Makalu non ha `IActionHost`/assegnazione azioni
  per-tasto (solo remap firmware diretto, vedi nota architetturale in
  `_PROJECT_MAP.md`); valutare cosa "più simile a Base Camp" implica qui
  (UI del remap, o vera azione assegnabile)

### Nice to have
- [ ] Interfaccia che mostri l'intero setup Mountain (tutti i device
  collegati insieme, dashboard unificata)
- [ ] Sincronizzazione effetti luminosi coesa fra tutti i dispositivi (**già
  presente più sopra** come voce "Da aggiungere ex novo" — stesso item,
  duplicato qui dalla richiesta utente)
- [ ] Mini interfaccia da icona tray (system tray)
- [ ] Opzioni di accessibilità (richiesta utente 2026-07-13): modalità
  daltonismo (palette alternative per gli indicatori a colori nell'UI, es.
  stati/preview LED) + altre opzioni tipiche (dimensione testo/UI scalabile,
  alto contrasto, riduzione animazioni). Da valutare in dettaglio quando si
  inizia: quali elementi UI dipendono da colore per trasmettere informazione
  (non solo LED preview, anche eventuali badge di stato) e se serve un
  meccanismo di tema centralizzato oltre a `K2.Core/Themes/K2Theme.xaml`.

## Da portare da Base Camp

- [x] Visualizzare effetti luminosi attivi sul dispositivo in tempo reale nell'app
- [ ] Connettere il database di Base Camp per porting dati
- [x] DisplayPad: ricaricare da DB i profili attualmente caricati sui dispositivi
- [ ] Everest: supporto tasti e rotella media dock + 4 tasti con display del tastierino numerico
- [ ] Everest: supporto modalità numpad montato a sinistra + media dock a destra e a sinistra
- [ ] Funzioni accessorie: programmazione macro, display dial, sezione impostazioni, layout keyboard

- [x] DisplayPad: icone GIF animate per-tasto — implementato in `K2.App/Services/DpGifAnimator.cs` (2026-07-05), bug cross-thread nel log risolto lo stesso giorno (impediva l'animazione di partire). Vedi `_PROJECT_MAP.md` per dettagli. **DA VERIFICARE su hardware fisico** (non compilato in sandbox).

## Da aggiungere ex novo

- [ ] Sincronizzazione effetti luminosi coesa fra tutti i dispositivi
- [ ] Rotazione automatica immagini quando assegnate a layout ruotati su DisplayPad
- [ ] (idea, non presente in BC né in BaseCampLinux) Everest: animazione GIF su NDK/OLED — oggi sia BC che BaseCampLinux estraggono solo 1 frame statico. Tecnicamente fattibile riusando `EverestSdkNative.StartPicUpdate` (live, non persistito) in loop, ma mai validato da nessun riferimento e da valutare contro i crash noti di SDKDLL.dll sotto stress (vedi memoria project_sdkdll_crash_fix).
- [x] DisplayPad: immagine/GIF a schermo intero sui 12 tasti, con rotazione utente + controrotazione device — implementato in `K2.App/Services/DpFullscreenAnimator.cs` (2026-07-05). Vedi `_PROJECT_MAP.md` per dettagli. **DA VERIFICARE su hardware fisico** (non compilato in sandbox).
- [x] Dialog di crop/resize riutilizzabile per icone (DisplayPad + Everest NDK) e fullscreen (DisplayPad) — `K2.App/ImageCropDialog.cs` (2026-07-05). Skip per GIF animate.
- [x] Velocità GIF (per-tasto e fullscreen) — rimosso l'overhead GDI+ per-frame cotto una volta a inizio animazione, upload raw via `IDisplayPadClient.TryUploadRawBgr` sul motore nativo (2026-07-05). Resta un floor hardware ~140-180ms per refresh fullscreen completo (12 tile sequenziali) — vedi `_PROJECT_MAP.md`.
- [x] DisplayPad: upload "pannello intero" nativo per il fullscreen (un solo transfer via `Pad.UploadPanel` invece di 12 tile sequenziali) — implementato in `DpFullscreenAnimator` (2026-07-05, `BuildPanelBgr`/`RunPanelLoop`, fallback automatico ai 12 tile se non supportato/fallisce). Copre anche il vero 800×240 edge-to-edge, non solo l'unione 612×204 delle icone. **DA VERIFICARE su hardware fisico**, in particolare la rotazione 90°/270° (mai testata).
- [x] Anteprima GIF animata (icone DisplayPad + fullscreen DisplayPad) — implementata inizialmente in `K2.App/GifPreview.cs` (2026-07-05), poi assorbita direttamente in `CropEditor` (stesso giorno, vedi sotto) e il file rimosso perché rimasto senza chiamanti.
- [x] Checkbox "nessun crop/zoom" nel crop dialog (mostra l'immagine as-is) — `CropEditor` (2026-07-05).
- [x] Crop/zoom incorporato nella stessa finestra di caricamento/rotazione (niente più popup separato) per DisplayPad icone + fullscreen — `CropEditor` embedded in `DpKeyConfigDialog`/`ShowFullscreenDialog` (2026-07-05). Everest NDK resta sul popup (`ImageCropDialog`, ora thin wrapper attorno a `CropEditor`) perché non ha un dialog "carica e ruota" proprio.
- [x] Checkbox overlay "contorno tasti" sull'anteprima (singolo per icona, griglia 2×6 per fullscreen) — `CropEditor.SetKeyGrid` (2026-07-05). Puramente indicativo, non misurato sull'hardware (vedi `_PROJECT_MAP.md`).
- [x] Crop/resize per GIF animate — `K2.App/Services/CroppedGifRef.cs` (2026-07-05, sidecar JSON che punta al sorgente reale + rettangolo di crop, dato che GDI+ non sa ri-codificare una GIF multi-frame). Risolto in `DpGifAnimator`/`DpFullscreenAnimator`. **NON** abilitato per Everest NDK (nessun loop di animazione lì). **DA VERIFICARE su hardware fisico**.
- [ ] (idea, follow-up) Anteprima rotazione utente nel dialog fullscreen — al momento solo un hint testuale ("non riflette la scelta"), dato che un `RotateTransform` cosmetico su un canvas rettangolare sarebbe fuorviante rispetto al reale rotate+restretch applicato a runtime.
- [x] UI numpad display key Everest unificata (2026-07-07): un solo click apre `NdkKeyConfigDialog` (nuovo, `K2.App/NdkKeyConfigDialog.xaml(.cs)`) con immagine + azione insieme, sullo stesso modello di `DpKeyConfigDialog` per il DisplayPad — prima era click=immagine/click destro=azione, poco scopribile (segnalato dall'utente: "se clicchi sull'interfaccia dovrebbe fare l'azione configure action"). Il tasto destro resta solo per le scorciatoie rapide "Rimuovi azione"/"Rimuovi immagine". L'auto-generazione icona (exec/folder, v. sessione precedente) è ora dentro il dialog stesso.
- [ ] **BUG ancora aperto, confermato dall'utente (2026-07-07)**: a parte l'assegnazione via UI (sopra, risolta), **premendo il tasto fisico l'azione configurata sui 4 numpad display key non parte** — `MainWindow.NumpadDisplayKeys.cs::HandleNumpadDisplayKeyPress(keyIndex)` esiste ma non ha NESSUN chiamante nel codebase (verificato via grep), quindi è dead code. Il dispatcher `HandleEverestKey` (`MainWindow.Everest.cs`) chiama solo `TryHwCapture`/`TryExecuteHwAction` (sistema separato per dock/crown, vedi `MainWindow.DockActions.cs` — che esplicitamente NON copre gli NDK) prima di procedere alla logica tasti normali. Serve capire il/i matrixId reali dei 4 display key (il commento in `HandleNumpadDisplayKeyPress` dice "typically 0xF0-0xF3, to be verified" — MAI verificato, non va indovinato per regola di progetto). Fix proposto (non ancora implementato): aggiungere un meccanismo di cattura matrixId per gli NDK analogo a quello già esistente per gli `HwActionSlot` (`TryHwCapture`/"Capture matrixId…"), poi un `TryExecuteNdkAction(rawMatrix)` chiamato da `HandleEverestKey` accanto a `TryExecuteHwAction`. Richiede che l'utente prema fisicamente i 4 tasti display per catturarne il matrixId reale — non fattibile senza hardware.
