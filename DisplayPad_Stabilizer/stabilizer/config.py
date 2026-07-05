"""Costanti globali e path di default del modulo."""
from __future__ import annotations

import os
from pathlib import Path

# Mountain USB Vendor ID
MOUNTAIN_VID = 0x3282

# Path del DB Base Camp (default install Mountain Base Camp 1.9.x x86).
# Sovrascrivibile via env var BASECAMP_DB o CLI --db.
DEFAULT_BASECAMP_DB = Path(
    os.environ.get(
        "BASECAMP_DB",
        r"C:\Program Files (x86)\Mountain Base Camp\resources\bin\BaseCamp.db",
    )
)

# Eseguibile Base Camp da rilanciare al termine del fix.
DEFAULT_BASECAMP_EXE = Path(
    os.environ.get(
        "BASECAMP_EXE",
        r"C:\Program Files (x86)\Mountain Base Camp\Base Camp.exe",
    )
)

# Servizi e processi Mountain da fermare prima di toccare il DB.
BASECAMP_SERVICES = (
    "BaseCampService",
    "Mountain.BaseCamp",
)
BASECAMP_PROCESSES = (
    "Base Camp.exe",
    "BaseCamp.Service.exe",
    "MountainDisplayPadWorker.exe",
    "Basecamp.Worker.exe",
    "Makalu Monitor.exe",
)

# Cartella dati persistenti dello Stabilizer.
APPDATA = Path(os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming")))
STABILIZER_DATA = APPDATA / "DisplayPadStabilizer"
FINGERPRINT_FILE = STABILIZER_DATA / "displaypad_fingerprint.json"
LOG_DIR = STABILIZER_DATA / "logs"
BACKUP_DIR = STABILIZER_DATA / "db_backups"

# Solo Profiles con questo DeviceType vengono toccati.
DISPLAYPAD_DEVICE_TYPE = "DisplayPad"

# Timing.
USB_SETTLE_SECONDS = 8
DB_LOCK_SETTLE_SECONDS = 2

# Numero massimo di backup DB tenuti.
MAX_DB_BACKUPS = 20


def ensure_dirs():
    """Crea le directory di lavoro se non esistono."""
    for d in (STABILIZER_DATA, LOG_DIR, BACKUP_DIR):
        d.mkdir(parents=True, exist_ok=True)
