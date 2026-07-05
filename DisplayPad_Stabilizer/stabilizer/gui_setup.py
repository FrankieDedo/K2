"""GUI Tkinter per il setup iniziale del fingerprint.

Mostra i DisplayPad attualmente collegati (instance_id, PID), le voci del DB
(GUID, DeviceId, numero profili) e permette all'utente di:
  - selezionare il path di BaseCamp.db (auto-discovery + Sfoglia)
  - assegnare un'etichetta amichevole a ciascun device
  - confermare il fingerprint (salva il JSON)
  - testare un fix dry-run
  - diagnosticare e riparare GUID duplicati
"""
from __future__ import annotations

import logging
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from .config import DEFAULT_BASECAMP_DB, ensure_dirs
from .db_ops import backup_db, deduplicate_guids, find_duplicate_guids
from .discover import candidate_db_paths, find_basecamp_db
from .elevate import is_admin
from .fingerprint import Fingerprint, FingerprintEntry, load_fingerprint, save_fingerprint
from .orchestrator import preview_current_state, run_fix

logger = logging.getLogger(__name__)


class SetupApp(tk.Tk):
    def __init__(self, db_path=None):
        super().__init__()
        resolved = find_basecamp_db(db_path)
        if resolved is None and db_path is not None:
            resolved = Path(db_path)
        if resolved is None:
            resolved = DEFAULT_BASECAMP_DB

        self.db_var = tk.StringVar(value=str(resolved))
        self.title("DisplayPad Stabilizer - Setup")
        self.geometry("980x640")
        self.minsize(780, 500)

        self._devices = []
        self._db_summary = []

        self._build_ui()
        self.after(200, self._refresh)

    def _build_ui(self):
        ensure_dirs()

        header = ttk.Frame(self, padding=10)
        header.pack(fill="x")
        ttk.Label(header, text="Setup fingerprint DisplayPad",
                  font=("Segoe UI", 14, "bold")).pack(side="left")
        ttk.Button(header, text="Aggiorna", command=self._refresh).pack(side="right")

        db_row = ttk.Frame(self, padding=(10, 0))
        db_row.pack(fill="x")
        ttk.Label(db_row, text="BaseCamp.db:").pack(side="left")
        self.db_entry = ttk.Entry(db_row, textvariable=self.db_var)
        self.db_entry.pack(side="left", fill="x", expand=True, padx=6)
        ttk.Button(db_row, text="Sfoglia...", command=self._browse_db).pack(side="left")
        ttk.Button(db_row, text="Cerca auto", command=self._auto_find).pack(side="left", padx=(4, 0))

        # Riga azioni diagnosi/riparazione
        diag_row = ttk.Frame(self, padding=(10, 6))
        diag_row.pack(fill="x")
        ttk.Label(diag_row, text="Diagnosi DB:").pack(side="left")
        ttk.Button(diag_row, text="Controlla GUID duplicati",
                   command=self._diagnose).pack(side="left", padx=(6, 4))
        ttk.Button(diag_row, text="Ripara GUID duplicati...",
                   command=self._repair).pack(side="left")

        info = ttk.Frame(self, padding=(10, 6))
        info.pack(fill="x")
        ttk.Label(
            info,
            text=(
                "Procedura consigliata:\n"
                "1) Verifica che il path BaseCamp.db sia corretto.\n"
                "2) Premi 'Controlla GUID duplicati'. Se ce ne sono, premi "
                "'Ripara GUID duplicati...' (richiede admin, ferma Base Camp).\n"
                "3) Riapri Base Camp e configura un profilo su ogni DisplayPad.\n"
                "4) Torna qui, premi 'Aggiorna', assegna le etichette e poi "
                "'Salva fingerprint'.\n"
                "5) Da CLI: 'DisplayPadStabilizer.exe --install' per il fix "
                "automatico ad ogni boot."
            ),
            justify="left",
            wraplength=940,
        ).pack(anchor="w")

        table_frame = ttk.Frame(self, padding=10)
        table_frame.pack(fill="both", expand=True)

        columns = ("idx", "instance", "pid", "guid", "device_id", "profiles", "label")
        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings", height=10)
        for c, w, t in [
            ("idx", 40, "#"),
            ("instance", 340, "USB Instance ID"),
            ("pid", 70, "PID"),
            ("guid", 230, "DeviceGUID (DB)"),
            ("device_id", 60, "DevID"),
            ("profiles", 60, "#Prof."),
            ("label", 140, "Etichetta"),
        ]:
            self.tree.heading(c, text=t)
            self.tree.column(c, width=w, anchor="w")

        vsb = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        self.tree.bind("<Double-1>", self._on_edit_label)

        footer = ttk.Frame(self, padding=10)
        footer.pack(fill="x")
        self.status_var = tk.StringVar(value="Pronto.")
        ttk.Label(footer, textvariable=self.status_var).pack(side="left")
        ttk.Button(footer, text="Test fix (dry-run)",
                   command=self._dry_run_fix).pack(side="right", padx=4)
        ttk.Button(footer, text="Salva fingerprint",
                   command=self._save).pack(side="right", padx=4)

    # ----- DB path -----
    def _browse_db(self):
        current = Path(self.db_var.get())
        initial_dir = str(current.parent) if current.parent.exists() else str(Path.home())
        path = filedialog.askopenfilename(
            title="Seleziona BaseCamp.db",
            initialdir=initial_dir,
            filetypes=[("SQLite DB", "*.db"), ("Tutti i file", "*.*")],
        )
        if path:
            self.db_var.set(path)
            self._refresh()

    def _auto_find(self):
        found = find_basecamp_db()
        if found is not None:
            self.db_var.set(str(found))
            self._refresh()
        else:
            paths = "\n".join(str(p) for p in candidate_db_paths()[:10])
            messagebox.showwarning(
                "Auto-discovery fallita",
                "Nessun BaseCamp.db trovato.\n\nPath provati:\n" + paths
                + "\n\nUsa 'Sfoglia...' per indicarlo manualmente.",
            )

    @property
    def db_path(self):
        return Path(self.db_var.get())

    # ----- Diagnosi e riparazione GUID -----
    def _diagnose(self):
        db = self.db_path
        if not db.is_file():
            messagebox.showerror("Diagnosi", "BaseCamp.db non trovato: " + str(db))
            return
        try:
            dups = find_duplicate_guids(db)
        except Exception as e:
            logger.exception("Diagnosi fallita")
            messagebox.showerror("Diagnosi", str(e))
            return
        if not dups:
            messagebox.showinfo(
                "Diagnosi",
                "Nessun GUID duplicato. Il DB e' in stato pulito.",
            )
            self.status_var.set("Diagnosi: DB pulito.")
            return
        lines = ["Trovati " + str(len(dups)) + " GUID duplicati:", ""]
        for d in dups:
            lines.append(
                "  " + d.device_guid + "\n"
                "    DeviceId: " + ",".join(str(x) for x in d.device_ids)
                + "  (profili: " + ",".join(str(x) for x in d.profile_counts) + ")"
            )
        lines.append("")
        lines.append("Premi 'Ripara GUID duplicati...' per assegnare GUID nuovi.")
        messagebox.showwarning("Diagnosi", "\n".join(lines))
        self.status_var.set("Diagnosi: " + str(len(dups)) + " GUID duplicati.")

    def _repair(self):
        db = self.db_path
        if not db.is_file():
            messagebox.showerror("Riparazione", "BaseCamp.db non trovato: " + str(db))
            return
        try:
            dups = find_duplicate_guids(db)
        except Exception as e:
            messagebox.showerror("Riparazione", str(e))
            return
        if not dups:
            messagebox.showinfo("Riparazione", "Nessun GUID duplicato da riparare.")
            return

        if not is_admin():
            messagebox.showwarning(
                "Permessi insufficienti",
                "La riparazione modifica BaseCamp.db in Program Files: "
                "serve avviare lo Stabilizer come amministratore.\n\n"
                "Chiudi la GUI e rilancia DisplayPadStabilizer.exe via "
                "'Esegui come amministratore', poi torna su questa schermata.",
            )
            return

        # Mostra preview e chiedi conferma
        preview = deduplicate_guids(db, dry_run=True)
        text = ["Verranno riassegnati " + str(len(preview)) + " GUID:", ""]
        for old, devid, new in preview:
            text.append(
                "  DeviceId " + str(devid) + ": "
                + old[:8] + "... -> " + new[:8] + "..."
            )
        text.append("")
        text.append("Base Camp DEVE essere chiuso. Procedere?")
        confirm = messagebox.askyesno("Conferma riparazione", "\n".join(text))
        if not confirm:
            return

        try:
            backup = backup_db(db)
            changes = deduplicate_guids(db, dry_run=False)
        except Exception as e:
            logger.exception("Riparazione fallita")
            messagebox.showerror(
                "Errore riparazione",
                str(e) + "\n\nIl DB e' stato lasciato nello stato originale.",
            )
            return

        messagebox.showinfo(
            "Riparazione completata",
            str(len(changes)) + " righe Profiles aggiornate.\n"
            "Backup salvato in:\n" + str(backup) + "\n\n"
            "Riapri Base Camp per vedere ogni DisplayPad come device separato.",
        )
        self.status_var.set("GUID duplicati riparati: " + str(len(changes)))
        self._refresh()

    # ----- Refresh dati -----
    def _refresh(self):
        db = self.db_path
        if not db.is_file():
            self.status_var.set("BaseCamp.db non trovato: " + str(db))
            self._clear_tree()
            return
        self.status_var.set("Lettura USB e DB...")
        self.update_idletasks()
        try:
            self._devices, self._db_summary = preview_current_state(db)
        except Exception as e:
            logger.exception("Errore enumerazione")
            messagebox.showerror("Errore enumerazione",
                                 str(e) + "\n\nPath DB: " + str(db))
            self.status_var.set("Errore.")
            self._clear_tree()
            return

        existing = load_fingerprint()
        existing_labels = {}
        if existing:
            for e in existing.entries:
                existing_labels[e.key] = e.label

        db_sorted = sorted(self._db_summary, key=lambda r: r[1])
        self._clear_tree()

        n = max(len(self._devices), len(db_sorted))
        for i in range(n):
            dev = self._devices[i] if i < len(self._devices) else None
            db_row = db_sorted[i] if i < len(db_sorted) else None
            self.tree.insert("", "end", iid=str(i), values=(
                i + 1,
                dev.instance_id if dev else "",
                ("0x%04X" % dev.pid) if dev else "",
                db_row[0] if db_row else "",
                db_row[1] if db_row else "",
                db_row[2] if db_row else "",
                existing_labels.get(dev.stable_key, "") if dev else "",
            ))

        # Avvisa se ci sono GUID duplicati
        try:
            dups = find_duplicate_guids(db)
        except Exception:
            dups = []
        warn = (" | ATTENZIONE: " + str(len(dups)) + " GUID duplicati") if dups else ""
        self.status_var.set(
            str(len(self._devices)) + " device USB | "
            + str(len(self._db_summary)) + " entry DB"
            + warn + " | DB: " + str(db)
        )

    def _clear_tree(self):
        for iid in self.tree.get_children():
            self.tree.delete(iid)

    def _on_edit_label(self, event):
        row = self.tree.identify_row(event.y)
        if not row:
            return
        col = self.tree.identify_column(event.x)
        if col != "#7":
            return
        bbox = self.tree.bbox(row, col)
        if not bbox:
            return
        x, y, w, h = bbox
        current = self.tree.set(row, "label")
        entry = tk.Entry(self.tree)
        entry.insert(0, current)
        entry.select_range(0, "end")
        entry.focus()
        entry.place(x=x, y=y, width=w, height=h)

        def commit(_e=None):
            self.tree.set(row, "label", entry.get())
            entry.destroy()
        entry.bind("<Return>", commit)
        entry.bind("<FocusOut>", commit)
        entry.bind("<Escape>", lambda _e: entry.destroy())

    def _collect_fingerprint(self):
        entries = []
        db_sorted = sorted(self._db_summary, key=lambda r: r[1])
        n = min(len(self._devices), len(db_sorted))
        for i in range(n):
            dev = self._devices[i]
            db_row = db_sorted[i]
            label = self.tree.set(str(i), "label")
            entries.append(FingerprintEntry(
                instance_id=dev.instance_id,
                device_guid=db_row[0],
                label=label,
                pid=dev.pid,
            ))
        existing = load_fingerprint() or Fingerprint()
        existing.entries = entries
        return existing

    def _save(self):
        if not self._devices:
            messagebox.showwarning("Niente da salvare",
                                   "Nessun DisplayPad rilevato via USB.")
            return
        if not self._db_summary:
            messagebox.showwarning("Niente da salvare",
                                   "Nessun DisplayPad nel DB. Apri Base Camp prima.")
            return
        try:
            fp = self._collect_fingerprint()
            save_fingerprint(fp)
            messagebox.showinfo(
                "Fingerprint salvato",
                str(len(fp.entries)) + " DisplayPad mappati.",
            )
            self.status_var.set("Fingerprint salvato.")
        except Exception as e:
            logger.exception("Errore salvataggio")
            messagebox.showerror("Errore", str(e))

    def _dry_run_fix(self):
        db = self.db_path

        def _work():
            try:
                report = run_fix(db, basecamp_exe=None, relaunch=False, dry_run=True)
                self.after(0, lambda: self._show_report(report))
            except Exception as e:
                logger.exception("Errore dry-run")
                self.after(0, lambda: messagebox.showerror("Errore dry-run", str(e)))

        self.status_var.set("Dry-run in corso...")
        threading.Thread(target=_work, daemon=True).start()

    def _show_report(self, report):
        if report.skipped_reason:
            messagebox.showinfo("Dry-run", report.skipped_reason)
            self.status_var.set(report.skipped_reason)
            return
        lines = [
            "Device USB enumerati: " + str(len(report.enumerated)),
            "Riconosciuti dal fingerprint: " + str(len(report.fingerprint_known)),
            "Sconosciuti: " + str(len(report.fingerprint_unknown)),
            "",
            "Modifiche DB che verrebbero applicate:",
        ]
        if not report.db_changes:
            lines.append("  (nessuna, gia coerente)")
        else:
            for g, (old, new) in report.db_changes.items():
                lines.append("  " + g[:8] + "...  DeviceId " + str(old) + " -> " + str(new))
        messagebox.showinfo("Dry-run report", "\n".join(lines))
        self.status_var.set("Dry-run completato.")


def main(db_path=None):
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    app = SetupApp(db_path)
    app.mainloop()


if __name__ == "__main__":
    main()
