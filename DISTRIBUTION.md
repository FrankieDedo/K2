# DISTRIBUTION.md — cosa è redistribuibile e cosa no

Obiettivo: K2 deve poter essere distribuito come **pacchetto autonomo**, senza
includere parti di Base Camp non redistribuibili. Dove una dipendenza di Base
Camp è tecnicamente necessaria, la regola è: **la fornisce l'utente finale**,
copiandola dalla propria installazione di Base Camp (scaricabile liberamente).

Licenza dell'intero progetto: **GPLv3 + Non-Commercial** (vedi `LICENSE`),
ereditata da BaseCamp Linux per via del codice portato (protocolli Everest 60/
Makalu) — dettagli e crediti completi in `THIRD_PARTY_LICENSES.md`.

> Nota: questo documento è una ricognizione tecnica/organizzativa, non una
> consulenza legale. I termini delle licenze e la normativa applicabile fanno
> fede.

## ✅ Redistribuibile — può stare nel pacchetto K2

| Elemento | Note |
|---|---|
| `K2.App/`, `K2.Core/`, `K2.DisplayPad/`, `K2.DisplayPad.Satellite/` (sorgenti) | Codice originale scritto per il progetto K2, salvo i file esplicitamente elencati in `THIRD_PARTY_LICENSES.md` come portati da BaseCamp Linux (Everest60Protocol.cs, MakaluProtocol.cs, geometria Everest60KeyboardLayout). |
| `DisplayPad_Stabilizer/` (script `.py`, `.bat`, `.spec`) | Codice Python originale. |
| `lib/DisplayPadSDK.dll`, `lib/DisplayPad.SDK.dll`, `lib/DisplayPad.SDK.xml` | SDK ufficiale Mountain, **licenza MIT** — vedi `lib/LICENSE.DisplayPad.SDK.txt`. |
| `lib/LICENSE.DisplayPad.SDK.txt` | Da tenere accanto alle DLL del DisplayPad SDK. |
| `K2.Core/Fonts/**/*.ttf`, `*.otf` | Roboto + font selezionabili aggiuntivi (Inter, IBM Plex Sans, Public Sans, Work Sans, Source Sans 3, OpenDyslexic), tutti **SIL OFL 1.1** — licenza per famiglia in `K2.Core/Fonts/<Family>/LICENSE.<Family>.txt`. |
| `*.sln`, `*.csproj`, `README.md`, `LICENSE`, `THIRD_PARTY_LICENSES.md`, `_PROJECT_MAP.md`, `DISTRIBUTION.md`, `.gitignore` | File di progetto e documentazione. |

## ⛔ NON redistribuibile — cartella `_reference/`

Tutto ciò che deriva dai binari di Base Camp. È raccolto in `K2/_reference/`,
che `.gitignore` esclude dal versionamento e che non va mai impacchettato.

| Elemento | Perché |
|---|---|
| `_reference/decompiled/`, `_reference/BaseCamp_decompiled*/` | Sorgente C# decompilato dai binari Base Camp: opera derivata, copyright Mountain. |
| `_reference/BaseCamp_Patch/` | Patch di codice decompilato di Base Camp (fix 3° DisplayPad). |
| `_reference/native/MacroPadSDK.dll` | DLL interna dell'app Base Camp, senza licenza di ridistribuzione. |

## ⚠️ Presente nel repo ma non regolarizzato — decisione aperta

| Elemento | Situazione |
|---|---|
| `K2.App/Assets/everest60_board.png`, `everest60_numpad.png`, `makalu_mouse*.png`, `mountain_logo.png`, `K2.Core/Assets/dp_folder_template.png`, il set `keybg*/keytop*/board_right*/dock_bg/mkd_bg/dkd_bg/key_button/numpad_bg/setting_keyboard/MKD_setting/DKD_Setting`, e le 11 immagini in `Assets/Home/` | Estratti 1:1 dalle risorse di Base Camp (foto prodotto, chrome grafico pannelli), **nessuna licenza di redistribuzione**, oggi compilati come `Resource` dentro l'eseguibile. Vedi `THIRD_PARTY_LICENSES.md`. Deciso (2026-07-13): restano nel repo per ora, "sistemare dopo" — non è una scelta definitiva, solo il punto in cui siamo. Opzioni valutate: risolverli a runtime dalla cartella Base Camp dell'utente (stesso pattern delle DLL sotto), oppure ridisegnarli come artwork originale K2. |
| `K2.DisplayPad/Assets/BaseCampMacros.json` | Dati estratti da `BaseCamp.db`. Trattarlo come dato fornito/ricreato dall'utente dalla propria installazione, non come asset distribuito. |

## Dipendenze native: `MacroPadSDK.dll`, `SDKDLL.dll`, `Everest360_USB.dll`

Servono rispettivamente a MacroPad, Everest Max, e al key binding di Everest 60
(il lighting di Everest 60 e tutto Makalu parlano HID raw, nessuna DLL vendor
coinvolta). Nessuna delle tre è redistribuibile. K2 è progettato per **non**
impacchettarle:

- I `.csproj` dei moduli le copiano accanto all'eseguibile **solo** se esiste
  una copia di lavoro locale in `_reference/native/` (comodità di sviluppo).
  In un pacchetto redistribuito `_reference/` non c'è, quindi non vengono
  copiate.
- A runtime `Services/NativeDependencyResolver.cs` registra un
  `DllImportResolver` che cerca ciascuna DLL, in ordine:
  1. accanto all'eseguibile;
  2. nella cartella indicata dalla variabile d'ambiente `K2_BASECAMP_DIR`;
  3. in una installazione di Base Camp individuata da registro di sistema e
     percorsi d'installazione tipici.
- Se non le trova, K2 non va in crash: mostra nella console le istruzioni per
  fornirle.

### Cosa deve fare l'utente finale

Una delle tre, a scelta:

1. **Copiare** le DLL necessarie dalla cartella di installazione di Base Camp
   (es. `...\Mountain\Base Camp\`) nella cartella di `K2.App.exe`.
2. **Tenere installato Base Camp** (scaricabile liberamente dal sito Mountain):
   K2 rileva l'installazione e si aggancia alle DLL.
3. Impostare la variabile d'ambiente `K2_BASECAMP_DIR` con il percorso della
   cartella di Base Camp.

## Produrre un pacchetto redistribuibile pulito

1. `dotnet publish .\K2.App\K2.App.csproj -c Release -p:Platform=x86`
2. Verificare che nell'output **non** ci siano `MacroPadSDK.dll`/`SDKDLL.dll`/
   `Everest360_USB.dll` (se sono finite lì perché esisteva `_reference/native/`,
   rimuoverle dal pacchetto).
3. Includere `lib/LICENSE.DisplayPad.SDK.txt` e i `LICENSE.<Family>.txt` dei
   font accanto ai rispettivi file, più `LICENSE`/`THIRD_PARTY_LICENSES.md` in
   root del pacchetto.
4. Non includere la cartella `_reference/`.
5. Allegare le istruzioni della sezione "Cosa deve fare l'utente finale".
