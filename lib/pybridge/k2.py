"""
k2.py - ponte Python <-> K2 DisplayPad.

Modulo helper che gli script lanciati dalla pressione di un tasto del
DisplayPad possono importare per richiamare le funzioni di K2:
cambiare profilo, eseguire azioni, leggere lo stato dei tasti, scrivere
nel log dell'applicazione, premere un tasto via software, ...

Lo script gira in un PROCESSO Python separato; la comunicazione con K2
avviene tramite una piccola API HTTP che K2 espone su 127.0.0.1.
URL e token vengono passati dallo stesso K2 tramite variabili d'ambiente,
quindi di norma non serve configurare nulla: basta `import k2`.

Usa SOLO la libreria standard (urllib, json, os): funziona anche con la
distribuzione Python "embeddable" che non include pip / pacchetti esterni.

Esempio minimo (file legato a un tasto):

    import k2
    k2.log("Ciao dal mio script!")
    stato = k2.get_state()
    print("profilo corrente:", stato["profile"])
    k2.switch_profile("Next")           # passa al profilo successivo
    k2.run_action("keys", "Ctrl + C")   # esegue una qualsiasi azione K2
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request

__all__ = [
    "K2Error",
    "available",
    "log",
    "get_state",
    "get_buttons",
    "switch_profile",
    "run_action",
    "press_button",
    "call",
    "device",
    "profile",
    "button",
    "script_args",
]


class K2Error(RuntimeError):
    """Errore di comunicazione con K2 o errore restituito da K2."""


# ----------------------------------------------------------------------
# Comunicazione di basso livello
# ----------------------------------------------------------------------

def _rpc_url() -> str | None:
    return os.environ.get("K2_RPC_URL")


def available() -> bool:
    """True se lo script e' stato avviato da K2 e l'API e' raggiungibile."""
    return bool(_rpc_url())


def call(method: str, params: dict | None = None, timeout: float = 15.0):
    """Invoca un metodo RPC di K2 e restituisce il campo 'result'.

    Solleva K2Error se K2 non e' raggiungibile o restituisce un errore.
    Normalmente non serve usare questa funzione direttamente: esistono i
    wrapper log()/get_state()/switch_profile()/... qui sotto.
    """
    url = _rpc_url()
    if not url:
        raise K2Error(
            "API K2 non disponibile (variabile K2_RPC_URL assente). "
            "Lo script e' stato avviato fuori da K2?"
        )
    token = os.environ.get("K2_RPC_TOKEN", "")
    payload = json.dumps({"method": method, "params": params or {}}).encode("utf-8")

    req = urllib.request.Request(url, data=payload, method="POST")
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("X-K2-Token", token)

    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:           # 4xx/5xx: il corpo c'e' lo stesso
        raw = exc.read().decode("utf-8", "replace")
    except urllib.error.URLError as exc:
        raise K2Error(f"K2 non raggiungibile: {exc}") from exc

    try:
        data = json.loads(raw)
    except ValueError as exc:
        raise K2Error(f"risposta K2 non valida: {raw!r}") from exc

    if not data.get("ok", False):
        raise K2Error(str(data.get("error", "errore sconosciuto da K2")))
    return data.get("result")


# ----------------------------------------------------------------------
# API ad alto livello
# ----------------------------------------------------------------------

def log(message: str) -> None:
    """Scrive una riga nel log di K2 (utile per il debug degli script)."""
    call("log", {"message": str(message)})


def get_state() -> dict:
    """Restituisce lo stato corrente di K2.

    Dizionario con: device, profile, profileCount, buttonCount, sdkVersion.
    """
    return dict(call("get_state") or {})


def get_buttons() -> list:
    """Elenco dei tasti del profilo corrente.

    Ogni elemento e' un dict: index, keyMatrix, hasImage, imagePath,
    actionType, actionValue.
    """
    return list(call("get_buttons") or [])


def switch_profile(target) -> int:
    """Cambia il profilo del DisplayPad.

    target puo' essere un numero (1..5) oppure "Next" / "Previous".
    Restituisce il numero del profilo selezionato.
    """
    res = call("switch_profile", {"target": str(target)}) or {}
    return int(res.get("profile", 0))


def run_action(action_type: str, value: str = "") -> None:
    """Esegue una qualsiasi azione di K2 (le stesse dei tasti).

    Esempi di action_type: url, exec, folder, browser, profile, oscmd,
    media, mouse, keys, command, text. Vedi la documentazione delle azioni.
    """
    call("run_action", {"type": str(action_type), "value": str(value)})


def press_button(index: int) -> None:
    """Esegue via software l'azione configurata sul tasto `index` (0..11)."""
    call("press_button", {"index": int(index)})


# ----------------------------------------------------------------------
# Contesto della pressione (passato da K2 via variabili d'ambiente)
# ----------------------------------------------------------------------

def device() -> int:
    """Id del device DisplayPad attivo quando lo script e' stato avviato."""
    try:
        return int(os.environ.get("K2_DEVICE", "0"))
    except ValueError:
        return 0


def profile() -> int:
    """Profilo attivo quando lo script e' stato avviato (1..5)."""
    try:
        return int(os.environ.get("K2_PROFILE", "0"))
    except ValueError:
        return 0


def button() -> int:
    """Indice del tasto che ha avviato lo script (0..11), -1 se non noto."""
    try:
        return int(os.environ.get("K2_BUTTON", "-1"))
    except ValueError:
        return -1


def script_args() -> list:
    """Argomenti opzionali configurati per lo script (sys.argv[1:])."""
    import sys
    return list(sys.argv[1:])
