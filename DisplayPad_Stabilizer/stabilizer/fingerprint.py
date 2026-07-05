"""Lettura/scrittura del file fingerprint che mappa instance_id USB -> GUID."""
from __future__ import annotations

import json
import logging
from dataclasses import dataclass, asdict, field
from datetime import datetime
from pathlib import Path

from .config import FINGERPRINT_FILE, ensure_dirs

logger = logging.getLogger(__name__)


@dataclass
class FingerprintEntry:
    instance_id: str        # path USB stabile (chiave)
    device_guid: str        # GUID Base Camp da preservare
    label: str = ""         # etichetta amichevole (es. "DisplayPad sinistro")
    pid: int | None = None  # PID osservato, solo informativo

    @property
    def key(self) -> str:
        return self.instance_id.upper()


@dataclass
class Fingerprint:
    version: int = 1
    created_at: str = ""
    updated_at: str = ""
    entries: list[FingerprintEntry] = field(default_factory=list)

    def by_instance(self) -> dict[str, FingerprintEntry]:
        return {e.key: e for e in self.entries}

    def by_guid(self) -> dict[str, FingerprintEntry]:
        return {e.device_guid: e for e in self.entries}


def load_fingerprint(path: Path = FINGERPRINT_FILE) -> Fingerprint | None:
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        entries = [FingerprintEntry(**e) for e in data.get("entries", [])]
        return Fingerprint(
            version=data.get("version", 1),
            created_at=data.get("created_at", ""),
            updated_at=data.get("updated_at", ""),
            entries=entries,
        )
    except (OSError, ValueError, TypeError) as e:
        logger.error("Fingerprint corrotto in %s: %s", path, e)
        return None


def save_fingerprint(fp: Fingerprint, path: Path = FINGERPRINT_FILE) -> None:
    ensure_dirs()
    now = datetime.now().isoformat(timespec="seconds")
    if not fp.created_at:
        fp.created_at = now
    fp.updated_at = now
    payload = {
        "version": fp.version,
        "created_at": fp.created_at,
        "updated_at": fp.updated_at,
        "entries": [asdict(e) for e in fp.entries],
    }
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    tmp.replace(path)
    logger.info("Fingerprint salvato (%d entry) in %s", len(fp.entries), path)
