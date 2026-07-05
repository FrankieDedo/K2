"""Watcher continuo del DB BaseCamp.

Si avvia come daemon e ad ogni modifica del file BaseCamp.db (cambio mtime)
controlla se sono comparsi GUID duplicati. Se ne trova, esegue una
riparazione automatica senza fermare Base Camp.

Strategia per la riparazione "a caldo":
  - Base Camp tiene il DB in modalita journal (WAL o rollback). Una UPDATE
    e' atomica e visibile dopo il commit. Possiamo applicare la dedup
    senza fermare Base Camp; al peggio una scrittura concorrente fallisce
    con SQLITE_BUSY e riproviamo dopo un breve sleep.
  - Per evitare loop di scrittura, dopo aver deduplicato salviamo l'mtime
    finale e ignoriamo cambi che lo riportano. Riconsideriamo solo cambi
    successivi a tale mtime.
"""
from __future__ import annotations

import logging
import sqlite3
import threading
import time
from pathlib import Path

from .config import USB_SETTLE_SECONDS
from .db_ops import backup_db, deduplicate_guids, find_duplicate_guids

logger = logging.getLogger(__name__)


class DbWatcher:
    """Polling watcher: controlla mtime del DB e fa dedup se serve."""

    def __init__(
        self,
        db_path,
        *,
        poll_interval=2.0,
        max_retries=8,
        retry_sleep=0.5,
        backup_on_first_change=True,
    ):
        self.db_path = Path(db_path)
        self.poll_interval = poll_interval
        self.max_retries = max_retries
        self.retry_sleep = retry_sleep
        self.backup_on_first_change = backup_on_first_change
        self._stop = threading.Event()
        self._thread = None
        self._last_mtime = None
        self._backup_done = False
        self._total_fixes = 0

    def start(self):
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, daemon=True, name="DbWatcher")
        self._thread.start()
        logger.info("Watcher avviato su %s (poll %.1fs)", self.db_path, self.poll_interval)

    def stop(self):
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5.0)
        logger.info("Watcher fermato. Riparazioni totali: %d", self._total_fixes)

    @property
    def total_fixes(self):
        return self._total_fixes

    def _loop(self):
        # Carica mtime iniziale per non triggerare al primo poll
        try:
            self._last_mtime = self.db_path.stat().st_mtime
        except FileNotFoundError:
            self._last_mtime = 0.0

        while not self._stop.is_set():
            try:
                if not self.db_path.is_file():
                    time.sleep(self.poll_interval)
                    continue
                m = self.db_path.stat().st_mtime
                if self._last_mtime is not None and m > self._last_mtime + 0.01:
                    logger.info("DB modificato (mtime %.3f -> %.3f), controllo duplicati", self._last_mtime, m)
                    self._check_and_fix()
                    # Aggiorna mtime POST eventuale fix (cosi' non re-triggera)
                    try:
                        self._last_mtime = self.db_path.stat().st_mtime
                    except FileNotFoundError:
                        pass
                else:
                    self._last_mtime = m
            except Exception:
                logger.exception("Errore nel ciclo watcher")
            self._stop.wait(self.poll_interval)

    def _check_and_fix(self):
        # Retry su SQLITE_BUSY (BC potrebbe stare scrivendo)
        for attempt in range(self.max_retries):
            try:
                dups = find_duplicate_guids(self.db_path)
                if not dups:
                    return
                logger.warning("Rilevati %d GUID duplicati post-modifica BC", len(dups))
                # Backup la prima volta che troviamo problemi
                if self.backup_on_first_change and not self._backup_done:
                    try:
                        backup_db(self.db_path)
                        self._backup_done = True
                    except Exception as e:
                        logger.warning("Backup pre-fix fallito: %s", e)
                changes = deduplicate_guids(self.db_path)
                self._total_fixes += 1
                logger.info("Watcher: %d righe Profiles dedup-ate", len(changes))
                for old_g, devid, new_g in changes:
                    logger.info("  DeviceId %s: %s -> %s", devid, old_g[:8], new_g[:8])
                return
            except sqlite3.OperationalError as e:
                if "locked" in str(e).lower() or "busy" in str(e).lower():
                    logger.debug("DB locked, retry %d/%d", attempt + 1, self.max_retries)
                    time.sleep(self.retry_sleep)
                    continue
                logger.exception("Errore SQLite nel watcher")
                return
            except Exception:
                logger.exception("Errore inatteso nel watcher")
                return
        logger.error("DB rimasto locked dopo %d tentativi, salto questo ciclo", self.max_retries)


def run_watch_blocking(db_path, *, poll_interval=2.0, settle_seconds=USB_SETTLE_SECONDS):
    """Avvia il watcher in foreground e blocca finche' non si riceve KeyboardInterrupt."""
    logger.info("Settle %ds prima di iniziare il watcher...", settle_seconds)
    time.sleep(max(0, settle_seconds))
    w = DbWatcher(db_path, poll_interval=poll_interval)
    w.start()
    try:
        while True:
            time.sleep(60)
    except KeyboardInterrupt:
        logger.info("Watcher interrotto da utente")
    finally:
        w.stop()
