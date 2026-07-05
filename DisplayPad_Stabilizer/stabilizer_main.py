"""Entry-point principale del DisplayPad Stabilizer.

Modi:
  --setup        Avvia la GUI di setup del fingerprint
  --fix          Esegue il fix all'avvio (modalita scheduled task)
  --dry-run      Come --fix ma senza scrivere niente n riavviare servizi
  --status       Stampa stato corrente (device USB + DB)
  --diagnose     Analizza il DB e segnala GUID duplicati
  --repair-guids Ripara i GUID duplicati nel DB (assegna nuovi GUID univoci)
  --install      Registra la scheduled task all'avvio utente
  --uninstall    Rimuove la scheduled task

Senza argomenti -> --setup (apre GUI).
"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from stabilizer.config import (
    DEFAULT_BASECAMP_DB,
    DEFAULT_BASECAMP_EXE,
    LOG_DIR,
    ensure_dirs,
)
from stabilizer.elevate import ensure_admin_or_relaunch, is_admin


def _setup_logging():
    ensure_dirs()
    from datetime import datetime
    log_file = LOG_DIR / ("stabilizer_" + datetime.now().strftime("%Y%m%d") + ".log")
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
        handlers=[
            logging.FileHandler(log_file, encoding="utf-8"),
            logging.StreamHandler(sys.stdout),
        ],
    )


def cmd_setup(args):
    from stabilizer.gui_setup import main as gui_main
    gui_main(args.db)
    return 0


def cmd_fix(args, *, dry_run=False):
    if not dry_run and not is_admin():
        ensure_admin_or_relaunch()
    from stabilizer.orchestrator import run_fix
    report = run_fix(
        db_path=args.db,
        basecamp_exe=None if args.no_launch else args.exe,
        relaunch=not args.no_launch,
        dry_run=dry_run,
    )
    log = logging.getLogger("fix")
    if report.skipped_reason:
        log.warning("Skipped: %s", report.skipped_reason)
        return 2
    log.info("Enumerati: %d", len(report.enumerated))
    log.info(
        "Riconosciuti: %d  Sconosciuti: %d",
        len(report.fingerprint_known), len(report.fingerprint_unknown),
    )
    for g, (old, new) in report.db_changes.items():
        log.info("  %s : DeviceId %s -> %s", g, old, new)
    return 0


def cmd_status(args):
    from stabilizer.orchestrator import preview_current_state
    from stabilizer.fingerprint import load_fingerprint
    devices, db = preview_current_state(args.db)
    print("Device USB enumerati: " + str(len(devices)))
    for d in devices:
        print("  [" + str(d.enumeration_index) + "] PID=0x"
              + ("%04X" % d.pid) + "  " + d.instance_id)
    print("DB DisplayPad entries: " + str(len(db)))
    for g, did, n in db:
        print("  GUID=" + g + "  DeviceId=" + str(did) + "  Profili=" + str(n))
    fp = load_fingerprint()
    if fp:
        print("Fingerprint: " + str(len(fp.entries)) + " entry, aggiornato " + fp.updated_at)
    else:
        print("Fingerprint: ASSENTE (esegui --setup)")
    return 0


def cmd_diagnose(args):
    from stabilizer.db_ops import find_duplicate_guids, list_displaypads
    db = args.db
    print("Path DB: " + str(db))
    if not Path(db).is_file():
        print("ERRORE: DB non trovato.")
        return 2
    rows = list_displaypads(db)
    print("Righe DisplayPad nel DB (raggruppate per GUID+DeviceId): " + str(len(rows)))
    for r in rows:
        print("  GUID=" + r.device_guid
              + "  DeviceId=" + str(r.current_device_id)
              + "  Profili=" + str(r.profile_count))
    dups = find_duplicate_guids(db)
    if not dups:
        print()
        print("OK: nessun GUID duplicato.")
        return 0
    print()
    print("ATTENZIONE: trovati " + str(len(dups)) + " GUID duplicati:")
    for d in dups:
        print("  GUID " + d.device_guid + " usato su DeviceId "
              + ",".join(str(x) for x in d.device_ids)
              + "  (profili: " + ",".join(str(x) for x in d.profile_counts) + ")")
    print()
    print("Per ripararli automaticamente: --repair-guids")
    return 1


def cmd_repair_guids(args, *, dry_run=False):
    if not dry_run and not is_admin():
        ensure_admin_or_relaunch()
    from stabilizer.db_ops import backup_db, deduplicate_guids, find_duplicate_guids
    db = args.db
    dups = find_duplicate_guids(db)
    if not dups:
        print("Nessun GUID duplicato. Niente da riparare.")
        return 0
    print("Trovati " + str(len(dups)) + " GUID duplicati.")
    if not dry_run:
        backup = backup_db(db)
        print("Backup salvato: " + str(backup))
    changes = deduplicate_guids(db, dry_run=dry_run)
    print()
    print("Riassegnazioni " + ("(dry-run)" if dry_run else "applicate") + ":")
    for old_guid, devid, new_guid in changes:
        print("  DeviceId " + str(devid) + ": " + old_guid + " -> " + new_guid)
    print()
    print("Totale: " + str(len(changes)) + " righe Profiles aggiornate.")
    return 0



def cmd_watch(args):
    if not is_admin():
        ensure_admin_or_relaunch()
    from stabilizer.watcher import run_watch_blocking
    print("Watcher attivo. Premi Ctrl+C per uscire.")
    run_watch_blocking(args.db, poll_interval=args.poll_interval)
    return 0




def cmd_flash_status(args):
    if not is_admin():
        ensure_admin_or_relaunch()
    from stabilizer.flash_fingerprint import run_flash_status
    print("Lettura stato flash di tutti i device (solo lettura, niente scritture)...")
    print("Si raccomanda di chiudere Base Camp prima di eseguire questa lettura.")
    print()
    try:
        count, state = run_flash_status(max_device_id=10)
    except Exception as e:
        print("ERRORE: " + str(e))
        return 1
    print(f"GetDevCount = {count}")
    print(f"Device enumerati: {len(state)}")
    for did, g, prev in state:
        print(f"  DeviceId={did}  flashGUID={g}  prevId={prev}")
    return 0


def cmd_flash_fingerprint(args, *, dry_run=False):
    dry_run = dry_run or getattr(args, 'flash_fingerprint_dry_run', False)
    if not is_admin():
        ensure_admin_or_relaunch()
    from stabilizer.flash_fingerprint import run_flash_fingerprint
    if dry_run:
        print("Flash fingerprint (DRY-RUN): nessuna scrittura su flash o DB.")
    else:
        print("Flash fingerprint in corso. Base Camp verra' fermato e riavviato.")
    print("NON SCOLLEGARE i DisplayPad durante l'operazione.")
    report = run_flash_fingerprint(
        db_path=args.db,
        basecamp_exe=None if args.no_launch else args.exe,
        relaunch_basecamp=not args.no_launch,
        dry_run=dry_run,
    )
    print()
    print(f"Device rilevati dall'SDK: {len(report.flash_before)}"
          f"  (GetDevCount={report.dev_count})")
    print()
    print("=== Stato flash PRIMA ===")
    for did, g, prev in report.flash_before:
        print(f"  DeviceId={did}  flashGUID={g or '(vuota)'}  prevId={prev}")
    print()
    print("=== Piano di assegnazione ===")
    _actions = {"keep": "tiene il GUID", "assign_db": "GUID dal DB",
                "new": "GUID nuovo"}
    for did, g, action in report.plan:
        print(f"  DeviceId={did}  [{_actions.get(action, action)}]  {g}")
    print()
    print("=== Stato flash DOPO ===")
    for did, g in report.flash_after:
        print(f"  DeviceId={did}  flashGUID={g or '(vuota)'}")
    if report.db_updates:
        print()
        print(f"DB - GUID duplicati separati: {len(report.db_updates)}")
        for old, new in report.db_updates:
            print(f"  {old} -> {new}")
    if report.db_id_changes:
        print()
        print(f"DB - DeviceId riallineati: {len(report.db_id_changes)}")
        for g, old, new in report.db_id_changes:
            print(f"  {g}: DeviceId {old} -> {new}")
    if report.errors:
        print()
        print("ERRORI:")
        for did, msg in report.errors:
            where = f"DeviceId={did}" if did else "generale"
            print(f"  {where}: {msg}")
        return 1
    print()
    if dry_run:
        print("Dry-run completato: rivedi il piano qui sopra, poi esegui "
              "--flash-fingerprint per applicarlo.")
    else:
        print("Provisioning completato. Ogni DisplayPad ha ora un GUID univoco "
              "e verificato in flash.")
        print("Verifica una volta in Base Camp che ogni pad mostri il profilo "
              "giusto: da ora la corrispondenza resta stabile.")
    return 0



def cmd_cycle_devices(args):
    if not is_admin():
        ensure_admin_or_relaunch()
    from stabilizer.device_cycle import cycle_all_displaypads
    print("Cycle (disable+enable) di tutti i DisplayPad Mountain...")
    ok, fail = cycle_all_displaypads()
    print(f"Completato: {ok} ok, {len(fail)} fail")
    for iid in fail:
        print(f"  FAIL: {iid}")
    return 0 if not fail else 1


def cmd_install(args):
    if not is_admin():
        ensure_admin_or_relaunch()
    from installer import install_scheduled_task
    install_scheduled_task(
        exe_path=Path(sys.argv[0]).resolve(),
        db_path=args.db,
        basecamp_exe=args.exe,
        watch_mode=getattr(args, 'watch_mode', False),
    )
    return 0


def cmd_uninstall(args):
    if not is_admin():
        ensure_admin_or_relaunch()
    from installer import uninstall_scheduled_task
    uninstall_scheduled_task()
    return 0


def build_parser():
    p = argparse.ArgumentParser(description="DisplayPad Stabilizer")
    p.add_argument("--db", type=Path, default=DEFAULT_BASECAMP_DB,
                   help="Path di BaseCamp.db")
    p.add_argument("--exe", type=Path, default=DEFAULT_BASECAMP_EXE,
                   help="Eseguibile Base Camp da rilanciare")
    p.add_argument("--no-launch", action="store_true",
                   help="Non riavviare Base Camp dopo il fix")
    p.add_argument("--poll-interval", type=float, default=2.0,
                   help="Intervallo polling del watcher (sec)")
    p.add_argument("--watch-mode", action="store_true",
                   help="Per --install: registra task per --watch invece di --fix")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--setup", action="store_true", help="Apri GUI setup")
    g.add_argument("--fix", action="store_true", help="Esegui fix immediato")
    g.add_argument("--dry-run", action="store_true", help="Simula il fix")
    g.add_argument("--status", action="store_true", help="Mostra stato")
    g.add_argument("--diagnose", action="store_true",
                   help="Analizza il DB e segnala GUID duplicati")
    g.add_argument("--repair-guids", action="store_true",
                   help="Ripara i GUID duplicati nel DB")
    g.add_argument("--repair-guids-dry-run", action="store_true",
                   help="Mostra cosa farebbe --repair-guids senza scrivere")
    g.add_argument("--watch", action="store_true",
                   help="Avvia watcher continuo del DB (fix in tempo reale)")
    g.add_argument("--flash-fingerprint", action="store_true",
                   help="Riscrive un GUID univoco nella flash di ogni DisplayPad (fix definitivo)")
    g.add_argument("--flash-fingerprint-dry-run", action="store_true",
                   help="Mostra il piano di flash-fingerprint senza scrivere niente")
    g.add_argument("--flash-status", action="store_true",
                   help="Legge la flash di tutti i device (solo lettura)")
    g.add_argument("--cycle-devices", action="store_true",
                   help="Disabilita e riabilita i DisplayPad via pnputil (replug software)")
    g.add_argument("--install", action="store_true", help="Registra scheduled task")
    g.add_argument("--uninstall", action="store_true", help="Rimuovi scheduled task")
    return p


def main(argv=None):
    _setup_logging()
    args = build_parser().parse_args(argv)

    if args.fix:
        return cmd_fix(args, dry_run=False)
    if args.dry_run:
        return cmd_fix(args, dry_run=True)
    if args.status:
        return cmd_status(args)
    if args.diagnose:
        return cmd_diagnose(args)
    if args.repair_guids:
        return cmd_repair_guids(args, dry_run=False)
    if args.repair_guids_dry_run:
        return cmd_repair_guids(args, dry_run=True)
    if args.watch:
        return cmd_watch(args)
    if args.flash_fingerprint:
        return cmd_flash_fingerprint(args)
    if args.flash_fingerprint_dry_run:
        return cmd_flash_fingerprint(args, dry_run=True)
    if args.flash_status:
        return cmd_flash_status(args)
    if args.cycle_devices:
        return cmd_cycle_devices(args)
    if args.install:
        return cmd_install(args)
    if args.uninstall:
        return cmd_uninstall(args)
    return cmd_setup(args)


if __name__ == "__main__":
    sys.exit(main())
