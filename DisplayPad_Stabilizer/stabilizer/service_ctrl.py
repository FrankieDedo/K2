"""Gestione servizi Windows e processi Base Camp."""
from __future__ import annotations

import logging
import os
import subprocess
import time
from pathlib import Path

from .config import (
    BASECAMP_PROCESSES,
    BASECAMP_SERVICES,
    DB_LOCK_SETTLE_SECONDS,
)

logger = logging.getLogger(__name__)


def _run(cmd: list[str], *, check: bool = False, capture: bool = True) -> subprocess.CompletedProcess:
    logger.debug("Run: %s", " ".join(cmd))
    return subprocess.run(
        cmd,
        check=check,
        capture_output=capture,
        text=True,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )


def stop_basecamp() -> None:
    """Ferma servizi Windows e processi Base Camp prima di toccare il DB."""
    for svc in BASECAMP_SERVICES:
        result = _run(["sc.exe", "stop", svc])
        if result.returncode == 0:
            logger.info("Servizio %s fermato", svc)
        else:
            logger.debug("sc stop %s: rc=%d %s", svc, result.returncode,
                          (result.stderr or "").strip())

    for proc in BASECAMP_PROCESSES:
        result = _run(["taskkill.exe", "/F", "/IM", proc, "/T"])
        if result.returncode == 0:
            logger.info("Processo %s terminato", proc)
        else:
            # 128 = "not found" — OK
            logger.debug("taskkill %s: rc=%d", proc, result.returncode)

    time.sleep(DB_LOCK_SETTLE_SECONDS)


def start_basecamp(exe_path: Path | None) -> None:
    """Riavvia i servizi e (se passato) il GUI."""
    for svc in BASECAMP_SERVICES:
        result = _run(["sc.exe", "start", svc])
        if result.returncode == 0:
            logger.info("Servizio %s avviato", svc)
        else:
            logger.debug("sc start %s: rc=%d %s", svc, result.returncode,
                          (result.stderr or "").strip())

    if exe_path is not None and exe_path.is_file():
        try:
            # Avviato detached cos sopravvive al tool.
            subprocess.Popen(
                [str(exe_path)],
                cwd=str(exe_path.parent),
                creationflags=getattr(subprocess, "DETACHED_PROCESS", 0)
                              | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
                close_fds=True,
            )
            logger.info("Base Camp avviato: %s", exe_path)
        except OSError as e:
            logger.error("Impossibile avviare Base Camp: %s", e)
    else:
        if exe_path is not None:
            logger.warning("Eseguibile Base Camp non trovato: %s", exe_path)


def is_basecamp_running() -> bool:
    """Heuristic: c' almeno uno dei processi Base Camp attivi?"""
    result = _run(["tasklist.exe", "/FO", "CSV", "/NH"])
    if result.returncode != 0:
        return False
    out = (result.stdout or "").lower()
    return any(proc.lower() in out for proc in BASECAMP_PROCESSES)
