"""Operazioni sul DB BaseCamp.db (sola tabella Profiles, lato DisplayPad)."""
from __future__ import annotations

import logging
import shutil
import sqlite3
import uuid
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from .config import BACKUP_DIR, DISPLAYPAD_DEVICE_TYPE, MAX_DB_BACKUPS, ensure_dirs

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class DbDisplayPad:
    """Riassunto della presenza di un DisplayPad nel DB."""
    device_guid: str
    current_device_id: int
    profile_count: int
    last_modified: str


@dataclass(frozen=True)
class DuplicateGuid:
    """Un DeviceGUID condiviso da pi DeviceId distinti."""
    device_guid: str
    device_ids: tuple[int, ...]
    profile_counts: tuple[int, ...]  # parallelo a device_ids


def backup_db(db_path):
    """Copia BaseCamp.db in BACKUP_DIR con timestamp. Mantiene MAX_DB_BACKUPS."""
    ensure_dirs()
    db_path = Path(db_path)
    if not db_path.is_file():
        raise FileNotFoundError(db_path)
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    dst = BACKUP_DIR / ("BaseCamp_" + ts + ".db")
    shutil.copy2(db_path, dst)
    logger.info("Backup DB salvato: %s", dst)
    backups = sorted(BACKUP_DIR.glob("BaseCamp_*.db"), key=lambda p: p.stat().st_mtime)
    for old in backups[:-MAX_DB_BACKUPS]:
        try:
            old.unlink()
        except OSError:
            pass
    return dst


def list_displaypads(db_path):
    """Tutti i DisplayPad presenti nel DB raggruppati per (DeviceGUID, DeviceId)."""
    with sqlite3.connect(db_path) as con:
        cur = con.cursor()
        cur.execute(
            """
            SELECT DeviceGUID, DeviceId, COUNT(*) AS n, MAX(modified_at)
              FROM Profiles
             WHERE DeviceType = ?
               AND DeviceGUID IS NOT NULL
               AND DeviceGUID <> '0'
             GROUP BY DeviceGUID, DeviceId
             ORDER BY DeviceId
            """,
            (DISPLAYPAD_DEVICE_TYPE,),
        )
        return [DbDisplayPad(g, did, n, m) for g, did, n, m in cur.fetchall()]


def find_duplicate_guids(db_path):
    """Ritorna i DeviceGUID condivisi da pi DeviceId distinti."""
    with sqlite3.connect(db_path) as con:
        cur = con.cursor()
        cur.execute(
            """
            SELECT DeviceGUID
              FROM Profiles
             WHERE DeviceType = ?
             GROUP BY DeviceGUID
            HAVING COUNT(DISTINCT DeviceId) > 1
            """,
            (DISPLAYPAD_DEVICE_TYPE,),
        )
        dup_guids = [r[0] for r in cur.fetchall()]

        out = []
        for g in dup_guids:
            cur.execute(
                """
                SELECT DeviceId, COUNT(*) FROM Profiles
                 WHERE DeviceType = ? AND DeviceGUID = ?
                 GROUP BY DeviceId
                 ORDER BY DeviceId
                """,
                (DISPLAYPAD_DEVICE_TYPE, g),
            )
            pairs = cur.fetchall()
            out.append(DuplicateGuid(
                device_guid=g,
                device_ids=tuple(p[0] for p in pairs),
                profile_counts=tuple(p[1] for p in pairs),
            ))
    return out


def deduplicate_guids(db_path, *, dry_run=False):
    """Per ogni GUID duplicato (stesso GUID, DeviceId diversi):
    tiene il DeviceId con pi profili sul GUID originale, riassegna a tutti
    gli altri DeviceId un nuovo GUID univoco.

    Ritorna lista di tuple (old_guid, device_id, new_guid).
    """
    duplicates = find_duplicate_guids(db_path)
    if not duplicates:
        return []

    changes = []
    with sqlite3.connect(db_path) as con:
        cur = con.cursor()
        cur.execute("BEGIN IMMEDIATE")
        try:
            for dup in duplicates:
                # Scegli il "keeper": DeviceId con pi profili (a parit, il pi piccolo)
                pairs = list(zip(dup.device_ids, dup.profile_counts))
                pairs.sort(key=lambda p: (-p[1], p[0]))
                keeper_devid = pairs[0][0]
                for devid in dup.device_ids:
                    if devid == keeper_devid:
                        continue
                    new_guid = str(uuid.uuid4())
                    if not dry_run:
                        cur.execute(
                            "UPDATE Profiles SET DeviceGUID = ? "
                            "WHERE DeviceType = ? AND DeviceGUID = ? AND DeviceId = ?",
                            (new_guid, DISPLAYPAD_DEVICE_TYPE, dup.device_guid, devid),
                        )
                    changes.append((dup.device_guid, devid, new_guid))
                    logger.info(
                        "Dedup: GUID %s su DeviceId %s -> nuovo GUID %s",
                        dup.device_guid, devid, new_guid,
                    )
            if dry_run:
                con.rollback()
            else:
                con.commit()
        except Exception:
            con.rollback()
            raise
    return changes


def reassign_device_ids(db_path, guid_to_new_device_id, *, dry_run=False):
    """Riscrive la colonna DeviceId per ogni DeviceGUID in input."""
    if not guid_to_new_device_id:
        return {}

    changes = {}
    with sqlite3.connect(db_path) as con:
        cur = con.cursor()
        cur.execute("BEGIN IMMEDIATE")
        try:
            placeholders = ",".join("?" for _ in guid_to_new_device_id)
            cur.execute(
                "SELECT DeviceGUID, DeviceId FROM Profiles "
                "WHERE DeviceType = ? AND DeviceGUID IN (" + placeholders + ") "
                "GROUP BY DeviceGUID",
                (DISPLAYPAD_DEVICE_TYPE, *guid_to_new_device_id.keys()),
            )
            current = {g: did for g, did in cur.fetchall()}

            actual_updates = {
                g: nid for g, nid in guid_to_new_device_id.items()
                if current.get(g) != nid
            }
            if not actual_updates:
                con.rollback()
                return {}
            if dry_run:
                for g, nid in actual_updates.items():
                    changes[g] = (current.get(g, -1), nid)
                con.rollback()
                return changes

            OFFSET = 9000
            for guid in actual_updates:
                cur.execute(
                    "UPDATE Profiles SET DeviceId = DeviceId + ? "
                    "WHERE DeviceType = ? AND DeviceGUID = ?",
                    (OFFSET, DISPLAYPAD_DEVICE_TYPE, guid),
                )
            for guid, new_id in actual_updates.items():
                cur.execute(
                    "UPDATE Profiles SET DeviceId = ? "
                    "WHERE DeviceType = ? AND DeviceGUID = ?",
                    (new_id, DISPLAYPAD_DEVICE_TYPE, guid),
                )
                changes[guid] = (current.get(guid, -1), new_id)
            con.commit()
        except Exception:
            con.rollback()
            raise
    return changes


def db_is_locked(db_path):
    try:
        with sqlite3.connect("file:" + str(db_path) + "?mode=rw", uri=True, timeout=0.5) as con:
            con.execute("BEGIN IMMEDIATE").fetchone()
            con.rollback()
        return False
    except sqlite3.OperationalError:
        return True
