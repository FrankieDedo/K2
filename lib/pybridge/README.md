# PyBridge — script Python sui tasti di K2

Questa cartella contiene il "ponte" che permette a K2.DisplayPad di eseguire
script Python alla pressione di un tasto, sia per fare azioni arbitrarie sul
sistema operativo sia per manipolare le funzioni di K2.

## File

| File | Ruolo |
|---|---|
| `k2.py` | Modulo helper che lo script utente importa (`import k2`) per richiamare le funzioni di K2 via l'API HTTP locale. Solo libreria standard. |
| `k2_runner.py` | Bootstrap lanciato da K2: sistema `sys.path`/`argv`/cwd ed esegue lo script utente. Necessario perché il Python *embeddable* parte isolato. |
| `examples/esempio_tasto.py` | Script di esempio: azione sul SO + uso del modulo `k2`. |

## Runtime Python

K2 esegue gli script con una distribuzione **Python embeddable x64** che vive
in `K2/lib/python-embed/`. Non è inclusa nel repo: installala una volta sola
lanciando `K2/setup-python-embed.bat` (oppure `setup-python-embed.ps1`).

K2 cerca l'interprete in quest'ordine: percorso salvato nelle impostazioni →
variabile d'ambiente `K2_PYTHON_DIR` → `python-embed/` accanto all'eseguibile →
`lib/python-embed/` nel repo.

## Come scrivere uno script per un tasto

```python
import k2

k2.log("Ciao dal mio script")          # scrive nel log di K2
stato = k2.get_state()                  # device / profile / buttonCount ...
k2.switch_profile("Next")               # cambia profilo (1..5 | Next | Previous)
k2.run_action("keys", "Ctrl + C")       # esegue una qualsiasi azione di K2
k2.press_button(3)                      # "preme" via software il tasto #3
righe = k2.get_buttons()                # stato dei 12 tasti del profilo
```

Contesto della pressione (variabili d'ambiente, esposte anche come funzioni):
`k2.device()`, `k2.profile()`, `k2.button()`, `k2.script_args()`.

Lo `stdout`/`stderr` dello script viene riversato nel log di K2.
La comunicazione con K2 avviene su `127.0.0.1` ed è protetta da un token
generato a ogni avvio (variabili `K2_RPC_URL` / `K2_RPC_TOKEN`).
