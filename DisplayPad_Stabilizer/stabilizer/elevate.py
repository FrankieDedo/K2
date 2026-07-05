"""Helper per rilevare privilegi admin e auto-elevare via UAC."""
from __future__ import annotations

import ctypes
import logging
import os
import sys

logger = logging.getLogger(__name__)


def is_admin() -> bool:
    """True se il processo corrente gira con privilegi di amministratore."""
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except (AttributeError, OSError):
        return False


def relaunch_as_admin(extra_args: list[str] | None = None) -> int:
    """Rilancia lo stesso programma elevato via UAC.

    Ritorna il return code di ShellExecuteW (>32 = OK, <=32 = errore).
    Dopo il rilancio il processo corrente deve terminare; la nuova istanza
    gira in una sessione separata.
    """
    if sys.platform != "win32":
        raise RuntimeError("UAC disponibile solo su Windows")

    # Costruiamo argv per il rilancio
    exe = sys.executable
    if getattr(sys, "frozen", False):
        # PyInstaller: sys.argv[0] e' il .exe stesso
        params = " ".join(_quote(a) for a in sys.argv[1:])
        target = sys.argv[0]
    else:
        # script .py: ri-lanciamo Python con lo script
        target = exe
        script = os.path.abspath(sys.argv[0])
        params = " ".join(_quote(a) for a in [script, *sys.argv[1:]])

    if extra_args:
        params = (params + " " + " ".join(_quote(a) for a in extra_args)).strip()

    logger.info("Rilancio elevato: %s %s", target, params)

    SW_NORMAL = 1
    rc = ctypes.windll.shell32.ShellExecuteW(
        None, "runas", target, params, None, SW_NORMAL
    )
    if rc <= 32:
        # 5 = ACCESS_DENIED (UAC negato), 1223 = ERROR_CANCELLED
        raise PermissionError(
            f"Elevazione UAC negata o fallita (ShellExecute rc={rc}). "
            f"Riprova accettando il prompt UAC, oppure lancia il programma "
            f"come amministratore."
        )
    return rc


def ensure_admin_or_relaunch() -> None:
    """Se non siamo admin, rilancia elevato e termina. Se siamo gia admin, no-op."""
    if is_admin():
        return
    try:
        relaunch_as_admin()
    except PermissionError as e:
        print(str(e), file=sys.stderr)
        sys.exit(1)
    # Il processo elevato e' un altro processo, il corrente termina pulito
    sys.exit(0)


def _quote(s: str) -> str:
    """Quota un argomento per la command line di Windows."""
    if not s:
        return '""'
    if any(c in s for c in (' ', '\t', '"')):
        return '"' + s.replace('"', r'\"') + '"'
    return s
