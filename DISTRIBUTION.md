# DISTRIBUTION.md — cosa è redistribuibile e cosa no

Obiettivo: K2 deve poter essere distribuito come **pacchetto autonomo**, senza
includere parti di Base Camp non redistribuibili. Dove una dipendenza di Base
Camp è tecnicamente necessaria, la regola è: **la fornisce l'utente finale**,
copiandola dalla propria installazione di Base Camp (scaricabile liberamente).

> Nota: questo documento è una ricognizione tecnica/organizzativa, non una
> consulenza legale. I termini delle licenze e la normativa applicabile fanno
> fede.

## ✅ Redistribuibile — può stare nel pacchetto K2

| Elemento | Note |
|---|---|
| `K2.App/` (sorgenti) | Codice originale scritto per il progetto K2. |
| `K2.DisplayPad/` (sorgenti) | Codice originale scritto per il progetto K2. |
| `DisplayPad_Stabilizer/` (script `.py`, `.bat`, `.spec`) | Codice Python originale. |
| `lib/DisplayPadSDK.dll`, `lib/DisplayPad.SDK.dll`, `lib/DisplayPad.SDK.xml` | SDK ufficiale Mountain, **licenza MIT** — vedi `lib/LICENSE.DisplayPad.SDK.txt`. La MIT consente la ridistribuzione a patto di includere l'avviso di licenza. |
| `lib/LICENSE.DisplayPad.SDK.txt` | Da tenere accanto alle DLL del DisplayPad SDK. |
| `*.sln`, `*.csproj`, `README.md`, `_PROJECT_MAP.md`, `DISTRIBUTION.md`, `.gitignore` | File di progetto e documentazione. |

## ⛔ NON redistribuibile — cartella `_reference/`

Tutto ciò che deriva dai binari di Base Camp. È raccolto in `K2/_reference/`,
che `.gitignore` esclude dal versionamento e che non va mai impacchettato.

| Elemento | Perché |
|---|---|
| `_reference/decompiled/` | Sorgente C# decompilato da `MountainDisplayPadWorker.exe` e `BaseCamp.Repository.dll`: opera derivata, copyright Mountain. |
| `_reference/BaseCamp_Patch/` | Patch di codice decompilato di Base Camp (fix 3° DisplayPad). |
| `_reference/native/MacroPadSDK.dll` | DLL interna dell'app Base Camp, senza licenza di ridistribuzione. |

## ⚠️ Zona grigia

| Elemento | Situazione |
|---|---|
| `K2.DisplayPad/Assets/BaseCampMacros.json` | Dati estratti da `BaseCamp.db`. È meglio trattarlo come dato fornito/ricreato dall'utente dalla propria installazione, non come asset distribuito. Da rivedere quando si lavora al modulo DisplayPad. |

## Dipendenza nativa: `MacroPadSDK.dll`

`MacroPadSDK.dll` serve a far funzionare il modulo MacroPad, ma non è
redistribuibile. K2 è progettato per **non** impacchettarla:

- `K2.App.csproj` la copia accanto all'eseguibile **solo** se esiste una copia
  di lavoro locale in `_reference/native/` (utile in sviluppo). In un pacchetto
  redistribuito `_reference/` non c'è, quindi non viene copiata.
- A runtime `Services/NativeDependencyResolver.cs` registra un
  `DllImportResolver` che cerca `MacroPadSDK.dll`, in ordine:
  1. accanto a `K2.App.exe`;
  2. nella cartella indicata dalla variabile d'ambiente `K2_BASECAMP_DIR`;
  3. in una installazione di Base Camp individuata da registro di sistema e
     percorsi d'installazione tipici.
- Se non la trova, K2 non va in crash: mostra nella console le istruzioni per
  fornirla.

### Cosa deve fare l'utente finale

Una delle tre, a scelta:

1. **Copiare** `MacroPadSDK.dll` dalla cartella di installazione di Base Camp
   (es. `...\Mountain\Base Camp\`) nella cartella di `K2.App.exe`.
2. **Tenere installato Base Camp** (scaricabile liberamente dal sito Mountain):
   K2 rileva l'installazione e si aggancia alla DLL.
3. Impostare la variabile d'ambiente `K2_BASECAMP_DIR` con il percorso della
   cartella di Base Camp.

## Produrre un pacchetto redistribuibile pulito

1. `dotnet publish .\K2.App\K2.App.csproj -c Release -p:Platform=x86`
2. Verificare che nell'output **non** ci sia `MacroPadSDK.dll` (se è finita lì
   perché esisteva `_reference/native/`, rimuoverla dal pacchetto).
3. Includere `lib/LICENSE.DisplayPad.SDK.txt` accanto alle DLL del DisplayPad SDK.
4. Non includere la cartella `_reference/`.
5. Allegare le istruzioni della sezione "Cosa deve fare l'utente finale".
