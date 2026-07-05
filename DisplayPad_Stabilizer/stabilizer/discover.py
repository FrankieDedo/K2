"""Auto-discovery dei path di Base Camp (DB e exe)."""
from __future__ import annotations

import logging
import os
from pathlib import Path

logger = logging.getLogger(__name__)


def _roots() -> list[Path]:
    roots: list[Path] = []
    env = os.environ
    for var in ("ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
                "ProgramData", "LOCALAPPDATA", "APPDATA"):
        v = env.get(var)
        if v:
            roots.append(Path(v))
    # Fallback se le env mancano (es. lancio elevato)
    for hard in (r"C:\Program Files", r"C:\Program Files (x86)", r"C:\ProgramData"):
        p = Path(hard)
        if p not in roots:
            roots.append(p)
    return roots


# Nomi cartella conosciuti per Base Camp (con/senza spazio, vari case)
_FOLDER_VARIANTS: tuple[Path, ...] = (
    Path("Mountain Base Camp"),
    Path("Mountain BaseCamp"),
    Path("Mountain") / "Base Camp",
    Path("Mountain") / "BaseCamp",
    Path("Base Camp"),
    Path("BaseCamp"),
)

# Sub-path interno alla cartella di install dove sta il DB
_DB_SUBPATHS: tuple[Path, ...] = (
    Path("resources") / "bin" / "BaseCamp.db",
    Path("BaseCamp.db"),
)

_EXE_NAMES: tuple[str, ...] = ("Base Camp.exe", "BaseCamp.exe")


def _expand_db_candidates() -> list[Path]:
    out: list[Path] = []
    seen: set[str] = set()
    for r in _roots():
        for folder in _FOLDER_VARIANTS:
            for sub in _DB_SUBPATHS:
                p = r / folder / sub
                k = str(p).lower()
                if k not in seen:
                    seen.add(k)
                    out.append(p)
    return out


def _expand_exe_candidates() -> list[Path]:
    out: list[Path] = []
    seen: set[str] = set()
    for r in _roots():
        for folder in _FOLDER_VARIANTS:
            for name in _EXE_NAMES:
                p = r / folder / name
                k = str(p).lower()
                if k not in seen:
                    seen.add(k)
                    out.append(p)
    return out


def find_basecamp_db(explicit: Path | None = None) -> Path | None:
    """Restituisce il primo BaseCamp.db esistente, o None."""
    if explicit is not None:
        p = Path(explicit)
        if p.is_file():
            return p
    for p in _expand_db_candidates():
        if p.is_file():
            logger.info("BaseCamp.db trovato: %s", p)
            return p
    return None


def find_basecamp_exe(explicit: Path | None = None) -> Path | None:
    """Restituisce il primo Base Camp.exe esistente, o None."""
    if explicit is not None:
        p = Path(explicit)
        if p.is_file():
            return p
    for p in _expand_exe_candidates():
        if p.is_file():
            logger.info("Base Camp.exe trovato: %s", p)
            return p
    return None


def candidate_db_paths() -> list[Path]:
    return _expand_db_candidates()
