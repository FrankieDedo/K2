"""
k2_runner.py - bootstrap di esecuzione per gli script Python di K2.

K2 NON lancia direttamente lo script dell'utente: lancia QUESTO runner, che
prepara l'ambiente e poi esegue lo script vero. Serve perche':

  * la distribuzione Python "embeddable" parte in modalita' isolata (il file
    pythonXY._pth ridefinisce sys.path) e NON aggiunge automaticamente la
    cartella dello script: senza questo runner `import k2` fallirebbe;
  * va impostato sys.argv, la working directory e il nome __main__ in modo
    coerente, sia per gli script da file che per il codice "inline".

Lo script da eseguire e gli argomenti arrivano da K2 via variabili
d'ambiente:
    K2_USER_SCRIPT  percorso del file .py da eseguire
    K2_SCRIPT_ARGS  argomenti, come array JSON (opzionale)
"""

import json
import os
import runpy
import sys
import traceback


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    if here not in sys.path:
        sys.path.insert(0, here)                  # rende importabile k2.py

    script = os.environ.get("K2_USER_SCRIPT")
    if not script or not os.path.isfile(script):
        print(f"[k2_runner] script non trovato: {script!r}", file=sys.stderr)
        return 2
    script = os.path.abspath(script)

    script_dir = os.path.dirname(script)
    if script_dir and script_dir not in sys.path:
        sys.path.insert(0, script_dir)            # import "vicini" allo script

    raw_args = os.environ.get("K2_SCRIPT_ARGS", "")
    try:
        args = json.loads(raw_args) if raw_args else []
        if not isinstance(args, list):
            args = []
    except ValueError:
        args = []
    sys.argv = [script] + [str(a) for a in args]

    try:
        os.chdir(script_dir or here)
    except OSError:
        pass

    try:
        runpy.run_path(script, run_name="__main__")
        return 0
    except SystemExit as exc:
        if exc.code is None:
            return 0
        return exc.code if isinstance(exc.code, int) else 1
    except BaseException:                         # pylint: disable=broad-except
        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
