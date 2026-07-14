# THIRD_PARTY_LICENSES.md — cosa in K2 non è nostro, e sotto quali termini

K2 è per la maggior parte codice originale (vedi `_PROJECT_MAP.md` per la
struttura). Questo file elenca tutto ciò che non lo è: componenti di terzi
usati sotto la loro licenza, e materiale portato/estratto da altri progetti.
Non è consulenza legale — i termini delle licenze citate fanno fede.

## Portato da BaseCamp Linux — GPL v3 + Non-Commercial

**BaseCamp Linux** (<https://github.com/ramisotti13-eng/BaseCamp-Linux>),
copyright © 2026 Ramisotti, licenza GPLv3 + Non-Commercial Restriction —
è il progetto community che reimplementa Base Camp per Linux, e la fonte da
cui K2 ha portato i protocolli USB raw-HID per Everest 60 e Makalu (mai
documentati da Mountain, reverse-engineered dal team di BaseCamp Linux).

**Grazie al team di BaseCamp Linux per il lavoro di reverse engineering —
senza il loro codice come riferimento, i moduli Everest 60 e Makalu di K2
non esisterebbero nella forma attuale.**

File K2 derivati (porting 1:1, non solo ispirazione):

| File K2 | Portato da |
|---|---|
| `K2.App/Services/Everest60Protocol.cs` | `devices/everest60/controller.py` |
| `K2.App/Services/MakaluProtocol.cs` | `devices/makalu67/controller.py` |
| `K2.App/Models/Everest60KeyboardLayout.cs` (geometria `MainBoard`) | `shared/ui_helpers.py::_build_kb60_layout()` |

Perché la licenza dell'intero repo K2 è GPLv3 + Non-Commercial (vedi
`LICENSE`): questi file sono compilati nello stesso eseguibile del resto di
K2, quindi l'intera distribuzione va considerata opera derivata/combinata
sotto i termini copyleft di BaseCamp Linux, non solo i singoli file.

## Redistribuito sotto licenza permissiva

| Componente | Fonte | Licenza | Dove |
|---|---|---|---|
| `DisplayPad.SDK.dll`, `DisplayPadSDK.dll` | Mountain (SDK ufficiale) | MIT | `K2/lib/`, vedi `lib/LICENSE.DisplayPad.SDK.txt` |
| `Microsoft.Data.Sqlite` 8.0.10 | NuGet / Microsoft | MIT | dipendenza NuGet (`K2.App`, `K2.DisplayPad`) |
| Roboto, Inter, IBM Plex Sans, Public Sans, Work Sans, Source Sans 3 | Google Fonts | SIL OFL 1.1 | `K2.Core/Fonts/<Family>/`, licenza per famiglia in `LICENSE.<Family>.txt` |
| OpenDyslexic | [antijingoist/opendyslexic](https://github.com/antijingoist/opendyslexic) | SIL OFL 1.1 | `K2.Core/Fonts/OpenDyslexic/`, `LICENSE.OpenDyslexic.txt` |

## ⚠️ Materiale Mountain non licenziato, attualmente nel repo

A differenza delle voci sopra, questi asset **non hanno alcuna licenza di
redistribuzione** — sono estratti 1:1 dai binari/risorse dell'app Base Camp
di Mountain (foto prodotto, elementi grafici dell'interfaccia). Sono nel
repo per comodità di sviluppo; la loro presenza in un rilascio pubblico è
una decisione ancora aperta, non coperta da questo file — vedi
`DISTRIBUTION.md`.

- `K2.App/Assets/everest60_board.png`, `everest60_numpad.png`
- `K2.App/Assets/makalu_mouse.png`, `makalu_mouse_rainbow.png`
- `K2.App/Assets/mountain_logo.png`
- `K2.Core/Assets/dp_folder_template.png`
- `K2.App/Assets/{keybg,keytop,board_right,dock_bg,mkd_bg,dkd_bg,key_button,numpad_bg,setting_keyboard,MKD_setting,DKD_Setting}*.png`
- `K2.App/Assets/Home/*.png` (11 file)

## Non incluso nel repo — fornito dall'utente finale

`MacroPadSDK.dll`, `SDKDLL.dll`, `Everest360_USB.dll` sono componenti interni
di Base Camp, senza licenza di ridistribuzione. K2 non li include: li cerca a
runtime nell'installazione Base Camp dell'utente. Dettagli in
`DISTRIBUTION.md`.
