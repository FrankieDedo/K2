"""
esempio_tasto.py - script di esempio per un tasto del DisplayPad.

Mostra i due usi previsti:
  1. azioni "arbitrarie" sul sistema operativo (qui: si scrive un file);
  2. manipolazione delle funzioni di K2 tramite il modulo `k2`.

Per provarlo: configura un tasto -> azione "Script Python" -> modalita'
"File .py" -> seleziona questo file.
"""

import datetime
import os

import k2   # ponte verso K2 (vedi lib/pybridge/k2.py)


def main():
    # --- 1. azione arbitraria sul sistema operativo --------------------
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    nota = os.path.join(desktop, "k2_premuto.txt")
    with open(nota, "a", encoding="utf-8") as fh:
        fh.write(f"Tasto premuto il {datetime.datetime.now():%Y-%m-%d %H:%M:%S}\n")
    print(f"Scritto: {nota}")          # stdout finisce nel log di K2

    # --- 2. interazione con K2 ----------------------------------------
    if not k2.available():
        print("API K2 non disponibile: script avviato fuori da K2.")
        return

    k2.log("esempio_tasto.py in esecuzione")

    stato = k2.get_state()
    print("Stato K2:", stato)
    print(f"Device {k2.device()}, profilo {k2.profile()}, tasto {k2.button()}")

    # Esegue una qualsiasi azione di K2, come se fosse su un tasto.
    k2.run_action("oscmd", "Calculator")

    # Esempio di logica: se siamo sul profilo 1, passa al successivo.
    if stato.get("profile") == 1:
        nuovo = k2.switch_profile("Next")
        k2.log(f"passato al profilo {nuovo}")


if __name__ == "__main__":
    main()
