"""Provisioner deterministico del flash-fingerprint.

Obiettivo
---------
Garantire che OGNI DisplayPad collegato abbia, nella propria flash, un GUID
univoco, valido e *verificato*. E' il fix definitivo al bug per cui Base Camp
scambia i profili tra i DisplayPad quando ne sono collegati 3 o piu'.

Perche' funziona
----------------
Base Camp identifica i device per `DeviceGUID`, che legge dal settore 0 della
flash del device (vedi `DisplayPadOperations.SetDeviceId` nel worker). Quando
piu' device hanno lo stesso GUID in flash (di fabbrica, oppure flash vuota), la
logica nativa di risoluzione collisioni e' soggetta a race condition: due pad
possono prendersi lo stesso GUID e i profili si scambiano.

Questo provisioner gira UNA volta, con tutti i pad collegati e Base Camp chiuso,
e forza uno stato pulito e deterministico:

  1. Ferma Base Camp (libera SDK e DB) e fa il backup del DB.
  2. Deduplica il DB: se un GUID risulta su piu' DeviceId, lo separa.
  3. Legge i GUID DisplayPad presenti nel DB, in ordine di DeviceId.
  4. Apre l'SDK ed enumera la flash di tutti i device connessi.
  5. Costruisce un piano deterministico (`_build_assignment`):
       - un device TIENE il GUID in flash se valido e univoco tra i device;
       - agli altri assegna, in ordine, un GUID gia' presente nel DB (cosi'
         eredita i profili gia' configurati, senza perdere niente); se i GUID
         del DB finiscono, assegna un GUID nuovo.
  6. Scrive in flash i GUID dei device non-"keep", con verifica e retry.
  7. Rilegge TUTTA la flash e verifica che i GUID siano univoci e corretti.
  8. Riallinea la colonna DeviceId del DB ai device fisici.
  9. Chiude l'SDK e riavvia Base Camp.

Dopo il provisioning ogni pad ha un'identita' stabile: la stessa logica
`SetDeviceId` di Base Camp si limitera' a *rileggere* il GUID (ramo "keep")
invece di rigenerarlo, e i profili non si scambiano piu'.
"""
from __future__ import annotations

import logging
import sqlite3
import time
import uuid
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from .db_ops import (
    DISPLAYPAD_DEVICE_TYPE,
    backup_db,
    deduplicate_guids,
    reassign_device_ids,
)
from .flash import (
    close_sdk,
    enumerate_flash_state,
    get_dev_count,
    open_sdk,
    write_flash_guid,
)
from .service_ctrl import start_basecamp, stop_basecamp

logger = logging.getLogger(__name__)


@dataclass
class FlashFingerprintReport:
    flash_before: list = field(default_factory=list)   # (device_id, guid|None, prev_id)
    flash_after: list = field(default_factory=list)    # (device_id, guid|None)
    plan: list = field(default_factory=list)           # (device_id, guid, action)
    db_updates: list = field(default_factory=list)     # (old_guid, new_guid) da dedup
    db_id_changes: list = field(default_factory=list)  # (guid, old_device_id, new_device_id)
    errors: list = field(default_factory=list)         # (device_id, msg)
    dev_count: int | None = None
    dry_run: bool = False

    @property
    def ok(self) -> bool:
        return not self.errors


# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------
def _is_uuid(value) -> bool:
    if not value or value in ("0", "-1"):
        return False
    try:
        uuid.UUID(str(value))
        return True
    except (ValueError, AttributeError, TypeError):
        return False


def _fresh_guid(exclude: set) -> str:
    """Genera un GUID nuovo che non sia in `exclude`."""
    while True:
        g = str(uuid.uuid4())
        if g not in exclude:
            return g


def _ordered_db_guids(db_path) -> list[str]:
    """GUID DisplayPad nel DB, distinti, ordinati per DeviceId crescente.

    L'ordine per DeviceId fa si' che, quando il provisioner deve assegnare i
    GUID del DB ai device fisici, lo faccia seguendo l'ordinamento gia' inteso
    dall'utente il piu' possibile.
    """
    with sqlite3.connect(db_path) as con:
        cur = con.cursor()
        cur.execute(
            "SELECT DeviceGUID, MIN(DeviceId) AS d FROM Profiles "
            "WHERE DeviceType = ? AND DeviceGUID IS NOT NULL "
            "AND DeviceGUID <> '' AND DeviceGUID <> '0' "
            "GROUP BY DeviceGUID ORDER BY d, DeviceGUID",
            (DISPLAYPAD_DEVICE_TYPE,),
        )
        return [r[0] for r in cur.fetchall() if _is_uuid(r[0])]


def _build_assignment(flash_state, db_guids):
    """Costruisce il piano deterministico device -> GUID.

    flash_state : lista di (device_id, guid|None, prev_id)
    db_guids    : lista ordinata di GUID DisplayPad presenti nel DB

    Ritorna lista di (device_id, target_guid, action) ordinata per device_id,
    con action in {"keep", "assign_db", "new"}.

    Garantisce che i target_guid siano tutti distinti.
    """
    flash_counts = Counter(g for _did, g, _p in flash_state if _is_uuid(g))
    claimed: set[str] = set()
    decided: dict[int, tuple[str, str]] = {}
    deferred: list[int] = []

    # Pass A — un device tiene il suo GUID se valido e univoco tra i device.
    for did, g, _prev in sorted(flash_state, key=lambda t: t[0]):
        if _is_uuid(g) and flash_counts[g] == 1 and g not in claimed:
            decided[did] = (g, "keep")
            claimed.add(g)
        else:
            deferred.append(did)

    # Pass B — ai device rimanenti assegna prima i GUID liberi del DB,
    # poi (se finiscono) GUID nuovi.
    db_pool = [g for g in db_guids if _is_uuid(g) and g not in claimed]
    for did in sorted(deferred):
        if db_pool:
            g = db_pool.pop(0)
            decided[did] = (g, "assign_db")
        else:
            g = _fresh_guid(claimed | set(db_guids))
            decided[did] = (g, "new")
        claimed.add(g)

    return [(did, decided[did][0], decided[did][1]) for did in sorted(decided)]


# ---------------------------------------------------------------------------
# Sola lettura
# ---------------------------------------------------------------------------
def run_flash_status(sdk_path=None, max_device_id=10):
    """Enumera la flash di tutti i device connessi. Solo lettura, nessuna scrittura."""
    if not open_sdk(sdk_path):
        raise RuntimeError("OpenUSBDriver fallita")
    try:
        count = get_dev_count()
        state = enumerate_flash_state(max_device_id=max_device_id)
        return count, state
    finally:
        close_sdk()


# ---------------------------------------------------------------------------
# Provisioner
# ---------------------------------------------------------------------------
def run_flash_fingerprint(
    db_path,
    basecamp_exe=None,
    *,
    relaunch_basecamp=True,
    max_device_id=10,
    sdk_path=None,
    dry_run=False,
):
    """Esegue il provisioning completo. Vedi docstring del modulo."""
    db_path = Path(db_path)
    report = FlashFingerprintReport(dry_run=dry_run)

    logger.info("Fermo Base Camp prima di toccare SDK e DB...")
    stop_basecamp()
    time.sleep(2.0)

    # Backup DB (sempre, anche in dry-run: costa poco ed e' una rete di sicurezza).
    try:
        backup_db(db_path)
    except Exception as e:
        logger.warning("Backup DB fallito: %s", e)

    # 1) Deduplica i GUID gia' doppi nel DB (GUID condiviso da piu' DeviceId).
    try:
        dedup = deduplicate_guids(db_path, dry_run=dry_run)
        report.db_updates = [(old, new) for (old, _devid, new) in dedup]
        if dedup:
            logger.info("Deduplica DB: %d righe riassegnate", len(dedup))
    except Exception as e:
        logger.warning("Deduplica DB fallita: %s", e)

    # 2) GUID DisplayPad nel DB, ordinati per DeviceId.
    try:
        db_guids = _ordered_db_guids(db_path)
    except Exception as e:
        logger.warning("Lettura GUID dal DB fallita: %s", e)
        db_guids = []
    logger.info("GUID DisplayPad nel DB: %s", db_guids or "(nessuno)")

    # 3) Apertura SDK + enumerazione flash.
    if not open_sdk(sdk_path):
        raise RuntimeError("OpenUSBDriver fallita")
    try:
        time.sleep(2.0)
        report.dev_count = get_dev_count()
        report.flash_before = enumerate_flash_state(max_device_id=max_device_id)
        logger.info("Device enumerati: %d (GetDevCount=%s)",
                    len(report.flash_before), report.dev_count)
        for did, g, prev in report.flash_before:
            logger.info("  DeviceId=%d  flashGUID=%s  prevId=%s",
                        did, g if g else "(vuota)", prev)

        if not report.flash_before:
            report.errors.append((0, "Nessun DisplayPad rilevato dall'SDK"))
        else:
            # 4) Piano deterministico.
            report.plan = _build_assignment(report.flash_before, db_guids)
            logger.info("Piano di assegnazione:")
            for did, g, action in report.plan:
                logger.info("  DeviceId=%d  action=%-9s  guid=%s", did, action, g)

            # 5) Scrittura flash dei device non-"keep".
            if dry_run:
                report.flash_after = [(did, g) for did, g, _a in report.plan]
            else:
                for did, g, action in report.plan:
                    if action == "keep":
                        continue
                    if not write_flash_guid(did, g, attempts=3):
                        report.errors.append(
                            (did, "scrittura GUID " + g + " fallita"))

                # 6) Verifica globale: rileggi tutto e controlla.
                recheck = enumerate_flash_state(max_device_id=max_device_id)
                got = {d: gg for d, gg, _p in recheck}
                for did, target, _a in report.plan:
                    if got.get(did) != target:
                        logger.warning(
                            "DeviceId=%d non verificato (flash=%s, atteso=%s): retry",
                            did, got.get(did), target)
                        if write_flash_guid(did, target, attempts=2):
                            got[did] = target
                        else:
                            report.errors.append(
                                (did, "verifica flash fallita (flash=%s)"
                                 % got.get(did)))
                report.flash_after = [(did, got.get(did)) for did, _g, _a in report.plan]

                # univocita' finale
                finals = [got.get(did) for did, _g, _a in report.plan]
                dups = sorted({x for x in finals if x and finals.count(x) > 1})
                if dups:
                    report.errors.append(
                        (0, "GUID ancora duplicati in flash: " + ", ".join(dups)))
    finally:
        close_sdk()

    # 7) Riallinea la colonna DeviceId del DB ai device fisici.
    if report.plan:
        mapping = {g: did for did, g, _a in report.plan}
        try:
            changes = reassign_device_ids(db_path, mapping, dry_run=dry_run)
            # Mostra solo i GUID che esistono davvero nel DB (old_id != -1).
            report.db_id_changes = [
                (g, old, new) for g, (old, new) in changes.items() if old != -1
            ]
        except Exception as e:
            logger.warning("Riassegnazione DeviceId nel DB fallita: %s", e)

    # 8) Riavvio Base Camp.
    if relaunch_basecamp and basecamp_exe is not None:
        logger.info("Riavvio Base Camp...")
        start_basecamp(Path(basecamp_exe))

    return report
