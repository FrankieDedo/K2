# DisplayPad Stabilizer

Risolve in modo automatico il bug per cui Base Camp di Mountain confonde i profili tra DisplayPad quando ne sono collegati 3 o più.

## Il problema in due righe

L'SDK Mountain (`SDKDLL.dll`) non legge alcun serial number dai DisplayPad: identifica i device solo per l'ordine di enumerazione USB (`DeviceID = 1, 2, 3…`). Base Camp salva i profili nel proprio DB legandoli a quel `DeviceID` runtime. Con 3 device identici l'ordine di enumerazione cambia spesso (timing USB, hub, porta), e a ogni boot i profili "saltano" sul device sbagliato.

## La soluzione

`DisplayPadStabilizer` interpone uno strato che gira **prima di Base Camp** e:

1. enumera i DisplayPad Mountain via `SetupAPI` ottenendo il loro **USB Device Instance Path** (chiave stabile per porta/hub),
2. confronta l'ordine attuale con un fingerprint salvato (USB path → DeviceGUID),
3. riscrive la colonna `DeviceId` nella tabella `Profiles` di `BaseCamp.db` perché i profili di ciascun GUID arrivino al device fisico giusto,
4. riavvia Base Camp.

L'utente non deve fare nulla al boot.

## Prima esecuzione (una volta sola)

1. Apri Base Camp, configura i 3 DisplayPad come vuoi tu, verifica che ogni display abbia i profili corretti.
2. Lancia `DisplayPadStabilizer.exe` (senza argomenti apre la GUI di setup).
3. Nella tabella vedi i device USB rilevati appaiati alle voci del DB. Se vuoi, scrivi un'etichetta per ciascuno (es. `DisplayPad sinistro`).
4. **Test fix (dry-run)** simula il fix senza scrivere niente: utile per controllare che l'enumerazione SetupAPI corrisponda davvero all'ordine usato dall'SDK Mountain. Al primo run il dry-run deve mostrare *zero modifiche* (il DB è già coerente).
5. **Salva fingerprint** scrive `%APPDATA%\DisplayPadStabilizer\displaypad_fingerprint.json`.

## Auto-fix ad ogni boot

```cmd
DisplayPadStabilizer.exe --install
```

Registra una scheduled task chiamata `DisplayPadStabilizer_AutoFix` che parte 15 secondi dopo il login utente, ferma Base Camp, ripara il DB e lo riavvia.

Per rimuoverla:

```cmd
DisplayPadStabilizer.exe --uninstall
```

## Comandi CLI

| Comando | Cosa fa |
|---|---|
| (nessun arg) | Apre la GUI di setup |
| `--setup` | Idem |
| `--status` | Stampa stato USB + DB + fingerprint |
| `--dry-run` | Simula il fix senza scrivere niente |
| `--fix` | Esegue il fix (modalità scheduled task) |
| `--install` | Registra la scheduled task |
| `--uninstall` | Rimuove la scheduled task |
| `--no-launch` | Non riavvia Base Camp dopo il fix |
| `--db <path>` | Path custom di BaseCamp.db |
| `--exe <path>` | Path custom di Base Camp.exe |

Path di default: `C:\Program Files\Mountain\Base Camp\…`.

## Sicurezza

- Prima di ogni modifica al DB viene salvato un backup in `%APPDATA%\DisplayPadStabilizer\db_backups\BaseCamp_YYYYMMDD_HHMMSS.db` (massimo 20 backup).
- Le modifiche al DB sono atomiche (transazione SQLite con rollback) e fatte in due fasi per evitare collisioni di chiave.
- Lo Stabilizer **non** apre comunicazione col firmware dei DisplayPad: si limita a leggere SetupAPI e a sincronizzare il DB.

## Limiti noti

- L'ipotesi di fondo è che l'ordine di `SetupDiEnumDeviceInterfaces` coincida con l'ordine assegnato dall'SDK Mountain. Se per qualche motivo non fosse così sul tuo PC, il dry-run del primo setup lo segnala (modifiche già al primo dry-run prima di aver scollegato niente). In quel caso aprire una issue / contattarmi e raccogliamo i log da `%APPDATA%\DisplayPadStabilizer\logs`.
- Non risolve eventuali profili "duplicati" già presenti nel DB. Se ne vedi, conviene fare una pulizia manuale prima del setup; nel DB sono nella tabella `Profiles` con `DeviceType='DisplayPad'`.

## Build

Richiede Python 3.11+:

```cmd
build.bat
```

Produce `dist\DisplayPadStabilizer.exe` standalone.

## File prodotti

```
DisplayPad_Stabilizer/
├── stabilizer/
│   ├── config.py          # path e costanti
│   ├── hid_enum.py        # enumerazione SetupAPI / HID
│   ├── db_ops.py          # backup + riscrittura BaseCamp.db
│   ├── service_ctrl.py    # stop/start servizi e processi
│   ├── fingerprint.py     # I/O del JSON di fingerprint
│   ├── orchestrator.py    # logica end-to-end (setup + fix)
│   └── gui_setup.py       # GUI Tkinter
├── stabilizer_main.py     # entry-point CLI
├── installer.py           # scheduled task via schtasks.exe
├── build.bat              # build PyInstaller
├── requirements.txt
└── README.md
```
