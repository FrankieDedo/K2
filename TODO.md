# K2 — TODO

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
