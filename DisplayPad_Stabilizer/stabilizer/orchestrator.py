"""Logica end-to-end: fingerprint, fix automatico, ripristino."""
from __future__ import annotations

import logging
import time
from dataclasses import dataclass
from pathlib import Path

from .config import (
    DEFAULT_BASECAMP_DB,
    DEFAULT_BASECAMP_EXE,
    USB_SETTLE_SECONDS,
    ensure_dirs,
)
from .device_cycle import cycle_all_displaypads
from .db_ops import (
    backup_db,
    db_is_locked,
    list_displaypads,
    reassign_device_ids,
)
from .fingerprint import (
    Fingerprint,
    FingerprintEntry,
    load_fingerprint,
    save_fingerprint,
)
from .hid_enum import DisplayPadDevice, enumerate_displaypads
from .service_ctrl import start_basecamp, stop_basecamp

logger = logging.getLogger(__name__)


# ---------- Risultati strutturati per UI/CLI -----------------------------------

@dataclass
class FixReport:
    enumerated: list[DisplayPadDevice]
    fingerprint_known: list[str]      # instance_id riconosciuti
    fingerprint_unknown: list[str]    # instance_id NON nel fingerprint
    db_before: list[tuple[str, int]]  # (guid, device_id) prima del fix
    db_changes: dict[str, tuple[int, int]]  # guid -> (old, new)
    skipped_reason: str | None = None


# ---------- FASE 1: fingerprint dallo stato attuale ----------------------------

def build_fingerprint_from_current_state(
    db_path: Path = DEFAULT_BASECAMP_DB,
    labels: dict[str, str] | None = None,
) -> Fingerprint:
    """Combina lo stato attuale del DB (DeviceId->GUID) con lo stato attuale
    dell'enumerazione USB (index->instance_id) per costruire il fingerprint.

    PRECONDIZIONE: nel momento in cui chiami questa funzione, l'utente DEVE
    avere Base Camp configurato correttamente — cio i profili devono essere
    visibili sul DisplayPad fisicamente giusto.
    """
    labels = labels or {}
    devices = enumerate_displaypads()
    db_entries = list_displaypads(db_path)

    if not devices:
        raise RuntimeError("Nessun DisplayPad Mountain rilevato via USB")
    if not db_entries:
        raise RuntimeError(
            "Nessun DisplayPad presente nel DB. Apri Base Camp almeno una "
            "volta con i device collegati prima di fingerprintare."
        )

    # Allineamento per indice: SDK_DeviceId atteso == enumeration_index + 1
    # MA il DB usa DeviceId che parte da 2 perch DeviceId=1 e' riservato (es. EverestKB).
    # Strategia robusta: ordiniamo le entry DB per DeviceId crescente e le
    # appaiamo per posizione con i device enumerati.
    db_sorted = sorted(db_entries, key=lambda e: e.current_device_id)

    if len(db_sorted) != len(devices):
        logger.warning(
            "Mismatch di conteggio: %d DisplayPad nel DB, %d enumerati via USB. "
            "Verra' fingerprintato solo il minimo comune.",
            len(db_sorted), len(devices),
        )

    n = min(len(db_sorted), len(devices))
    entries: list[FingerprintEntry] = []
    for i in range(n):
        dev = devices[i]
        db_e = db_sorted[i]
        entries.append(FingerprintEntry(
            instance_id=dev.instance_id,
            device_guid=db_e.device_guid,
            label=labels.get(dev.instance_id, ""),
            pid=dev.pid,
        ))

    fp = Fingerprint(entries=entries)
    return fp


# ---------- FASE 2: fix all'avvio ----------------------------------------------

def plan_device_id_mapping(
    devices: list[DisplayPadDevice],
    fingerprint: Fingerprint,
    db_entries_sorted_by_id: list[tuple[str, int]],
) -> tuple[dict[str, int], list[str], list[str]]:
    """Calcola: GUID -> nuovo DeviceId target.

    Strategia:
      - Si parte dall'insieme di DeviceId attualmente usati nel DB
        per i DisplayPad (es. {2,3,4}).
      - I device enumerati ricevono il DeviceId in ordine di
        enumeration_index, mappando l'i-esimo device fisico al
        i-esimo DeviceId disponibile.
      - Per ciascun device fisico si guarda nel fingerprint il GUID
        corretto e si assegna quel GUID al DeviceId i-esimo.

    Ritorna (mapping, known_instances, unknown_instances).
    """
    fp_by_inst = fingerprint.by_instance()
    known: list[str] = []
    unknown: list[str] = []

    target_guid_per_index: list[str | None] = []
    for dev in devices:
        e = fp_by_inst.get(dev.stable_key)
        if e is None:
            unknown.append(dev.instance_id)
            target_guid_per_index.append(None)
        else:
            known.append(dev.instance_id)
            target_guid_per_index.append(e.device_guid)

    # Lista dei DeviceId attualmente usati nel DB per DisplayPad, ordinati.
    available_ids = [did for _, did in db_entries_sorted_by_id]
    # Se ce ne sono meno del numero di device fisici, estendiamo con id liberi.
    next_free = (max(available_ids) + 1) if available_ids else 2
    while len(available_ids) < len(devices):
        available_ids.append(next_free)
        next_free += 1

    mapping: dict[str, int] = {}
    for i, guid in enumerate(target_guid_per_index):
        if guid is None:
            continue  # device sconosciuto: lo lasciamo gestire a Base Camp
        mapping[guid] = available_ids[i]

    return mapping, known, unknown



def _wait_for_devices(expected_count, timeout=40, poll=2.0):
    """Attende finche' SetupAPI enumera 'expected_count' DisplayPad o timeout.

    Risolve il problema per cui Base Camp al boot parte prima che Windows
    abbia finito di enumerare tutti i device USB.
    """
    deadline = time.time() + timeout
    last = 0
    while time.time() < deadline:
        last = len(enumerate_displaypads())
        if last >= expected_count:
            logger.info("Tutti i %d device pronti", last)
            return last
        logger.info("Device pronti: %d/%d, attendo...", last, expected_count)
        time.sleep(poll)
    logger.warning("Timeout: solo %d/%d device dopo %ds",
                   last, expected_count, timeout)
    return last


def run_fix(
    db_path: Path = DEFAULT_BASECAMP_DB,
    basecamp_exe: Path | None = DEFAULT_BASECAMP_EXE,
    *,
    relaunch: bool = True,
    settle_seconds: int = USB_SETTLE_SECONDS,
    dry_run: bool = False,
    cycle_devices: bool = True,
) -> FixReport:
    """Esegue la sequenza completa di riparazione."""
    ensure_dirs()
    fp = load_fingerprint()
    if fp is None or not fp.entries:
        return FixReport(
            enumerated=[], fingerprint_known=[], fingerprint_unknown=[],
            db_before=[], db_changes={},
            skipped_reason="Fingerprint assente. Esegui prima il setup.",
        )

    logger.info("Attendo %ds per settle USB...", settle_seconds)
    time.sleep(max(0, settle_seconds))

    devices = enumerate_displaypads()
    if not devices:
        return FixReport(
            enumerated=[], fingerprint_known=[], fingerprint_unknown=[],
            db_before=[], db_changes={},
            skipped_reason="Nessun DisplayPad rilevato via USB.",
        )

    # Ferma servizi prima di leggere/scrivere il DB
    if not dry_run:
        stop_basecamp()
        # extra wait sul file lock
        for _ in range(10):
            if not db_is_locked(db_path):
                break
            time.sleep(0.5)

    db_entries = list_displaypads(db_path)
    db_before = [(e.device_guid, e.current_device_id) for e in db_entries]
    db_sorted = sorted(db_entries, key=lambda e: e.current_device_id)
    db_sorted_pairs = [(e.device_guid, e.current_device_id) for e in db_sorted]

    mapping, known, unknown = plan_device_id_mapping(devices, fp, db_sorted_pairs)

    if not dry_run and mapping:
        backup_db(db_path)
    changes = reassign_device_ids(db_path, mapping, dry_run=dry_run)

    # Attendi che tutti i device attesi siano enumerati PRIMA di avviare BC.
    # Questo evita che Base Camp parta mentre Windows sta ancora montando
    # il 3 device (race condition al boot).
    if not dry_run:
        _wait_for_devices(len(fp.entries))

    if not dry_run and relaunch:
        start_basecamp(basecamp_exe)

    return FixReport(
        enumerated=devices,
        fingerprint_known=known,
        fingerprint_unknown=unknown,
        db_before=db_before,
        db_changes=changes,
        skipped_reason=None,
    )


# ---------- Utility per la GUI di setup ----------------------------------------

def preview_current_state(
    db_path: Path = DEFAULT_BASECAMP_DB,
) -> tuple[list[DisplayPadDevice], list[tuple[str, int, int]]]:
    """Ritorna stato live: device enumerati USB + entry DB.

    Pensato per la GUI: lo invoca a freddo, senza toccare niente."""
    devices = enumerate_displaypads()
    db_entries = list_displaypads(db_path)
    db_summary = [(e.device_guid, e.current_device_id, e.profile_count) for e in db_entries]
    return devices, db_summary
