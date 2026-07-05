"""Lettura/scrittura del GUID nella flash dei DisplayPad via SDK nativo Mountain.

Layer basso del flash-fingerprint. Espone le primitive con cui il provisioner
(`flash_fingerprint.py`) assegna a ogni DisplayPad un GUID univoco persistente.

Note tecniche importanti
------------------------
* L'SDK nativo Mountain (`SDKDLL.dll` / `DisplayPadSDK.dll`) e' una DLL 32-bit
  che esporta funzioni con convenzione **cdecl** (cosi' le dichiara il worker
  ufficiale: ``[DllImport(..., CallingConvention = CallingConvention.Cdecl)]``).
  Va quindi caricata con ``ctypes.CDLL``, NON con ``WinDLL`` (stdcall): caricarla
  come stdcall sbilancia lo stack a ogni chiamata e su 32-bit porta a crash o
  comportamenti erratici dopo molte chiamate (tipico sul 3o device).
* Il GUID del device vive nel **settore 0** della flash. Base Camp, prima di
  riscriverlo, cancella i settori 0 **e** 1 (``EraseSectorMem(0, 1)``): qui ci
  allineiamo a quel comportamento.
"""
from __future__ import annotations

import ctypes
import logging
import time
import uuid
from ctypes import c_bool, c_int, c_uint, c_void_p, byref
from pathlib import Path

logger = logging.getLogger(__name__)

# --- Costanti flash ---------------------------------------------------------
GUID_SECTOR = 0          # settore in cui vive il GUID del device
GUID_ADDRESS = 0         # offset nel settore
GUID_LEN = 36            # lunghezza ASCII di un GUID canonico
SECTOR_SIZE = 4096       # FW_SECTOR_MAX_LENGTH
ERASE_START = 0          # Base Camp cancella i settori 0..1 prima di scrivere
ERASE_END = 1
READ_INTERVAL = 2        # iInterval: velocita' pacchetti FW

# --- Timing (secondi) -------------------------------------------------------
SDK_ENUM_SETTLE = 3.0    # attesa dopo OpenUSBDriver perche' l'SDK enumeri
OP_RETRY_PAUSE = 1.0     # pausa tra retry di erase/write
VERIFY_PAUSE = 0.6       # pausa prima della rilettura di verifica

# Possibili nomi/percorsi della DLL nativa.
_SDK_DLL_NAMES = ("SDKDLL.dll", "DisplayPadSDK.dll")
_DEFAULT_SDK_DIRS = [
    r"C:\Program Files (x86)\Mountain Base Camp",
    r"C:\Program Files\Mountain Base Camp",
    r"C:\Program Files (x86)\Mountain\Base Camp",
    r"C:\Program Files\Mountain\Base Camp",
]

_sdk = None
_sdk_opened = False
_hidden_hwnd = None


# ---------------------------------------------------------------------------
# Caricamento DLL
# ---------------------------------------------------------------------------
def _find_sdk_dll(explicit=None):
    """Cerca la DLL nativa dell'SDK. `explicit` puo' essere un file o una cartella."""
    candidates: list[Path] = []
    if explicit:
        p = Path(explicit)
        if p.is_file():
            candidates.append(p)
        elif p.is_dir():
            candidates.extend(p / n for n in _SDK_DLL_NAMES)
    # accanto all'eseguibile dello stabilizer
    try:
        import sys
        here = Path(sys.argv[0]).resolve().parent
        candidates.extend(here / n for n in _SDK_DLL_NAMES)
    except Exception:
        pass
    # cartelle di install note
    for d in _DEFAULT_SDK_DIRS:
        candidates.extend(Path(d) / n for n in _SDK_DLL_NAMES)
    for c in candidates:
        if c.is_file():
            return c
    return None


def _load_sdk(sdk_path=None):
    global _sdk
    if _sdk is not None:
        return _sdk
    if ctypes.sizeof(ctypes.c_void_p) != 4:
        raise RuntimeError(
            "L'SDK nativo Mountain e' una DLL 32-bit (PE32 i386), ma il processo "
            "corrente e' 64-bit. Usa DisplayPadStabilizer_x86.exe."
        )
    dll = _find_sdk_dll(sdk_path)
    if dll is None:
        raise FileNotFoundError(
            "DLL SDK Mountain non trovata (SDKDLL.dll / DisplayPadSDK.dll). "
            "Passa il path con --sdk o installa Base Camp."
        )
    try:
        # CDLL = convenzione cdecl, coerente con il worker ufficiale.
        _sdk = ctypes.CDLL(str(dll))
    except OSError as e:
        raise RuntimeError("Impossibile caricare la DLL SDK (" + str(dll) + "): " + str(e))
    logger.info("SDK nativo caricato (cdecl): %s", dll)

    _sdk.OpenUSBDriver.argtypes = [c_void_p]
    _sdk.OpenUSBDriver.restype = c_bool
    _sdk.CloseUSBDriver.argtypes = []
    _sdk.CloseUSBDriver.restype = c_bool
    _sdk.IsDevicePlug.argtypes = [c_uint]
    _sdk.IsDevicePlug.restype = c_bool
    _sdk.APEnable.argtypes = [c_bool, c_uint]
    _sdk.APEnable.restype = c_bool
    _sdk.EraseSectorMem.argtypes = [c_int, c_int, c_uint]
    _sdk.EraseSectorMem.restype = c_bool
    _sdk.WriteSectorMem.argtypes = [c_int, c_int, c_int, c_void_p, c_uint]
    _sdk.WriteSectorMem.restype = c_bool
    _sdk.ReadSectorMemory.argtypes = [c_int, c_int, c_int, c_void_p, c_int, c_uint]
    _sdk.ReadSectorMemory.restype = c_bool
    try:
        _sdk.GetDevCount.argtypes = [ctypes.POINTER(c_int)]
        _sdk.GetDevCount.restype = c_bool
    except AttributeError:
        pass
    try:
        _sdk.GetDLLVersion.argtypes = []
        _sdk.GetDLLVersion.restype = c_int
    except AttributeError:
        pass
    return _sdk


def _create_hidden_window():
    """Crea una finestra Win32 message-only per ricevere WM_DEVICECHANGE.

    L'SDK Mountain si appoggia all'HWND per inizializzare la sua macchina
    interna; passare NULL puo' dare bug come 'IsDevicePlug True per default'.
    """
    global _hidden_hwnd
    if _hidden_hwnd is not None:
        return _hidden_hwnd
    try:
        user32 = ctypes.WinDLL("user32", use_last_error=True)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        HWND_MESSAGE = ctypes.c_void_p(-3)
        user32.CreateWindowExW.argtypes = [
            ctypes.c_ulong, ctypes.c_wchar_p, ctypes.c_wchar_p,
            ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
            ctypes.c_int, ctypes.c_int, ctypes.c_void_p,
            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
        ]
        user32.CreateWindowExW.restype = ctypes.c_void_p
        h_instance = kernel32.GetModuleHandleW(None)
        hwnd = user32.CreateWindowExW(
            0, "STATIC", "DisplayPadStabilizerHidden", 0,
            0, 0, 0, 0, HWND_MESSAGE, None, h_instance, None,
        )
        if not hwnd:
            logger.warning("CreateWindowExW fallita, passo NULL all'SDK")
            return None
        _hidden_hwnd = hwnd
        logger.info("Hidden HWND creata: 0x%x", hwnd)
        return hwnd
    except Exception as e:
        logger.warning("Impossibile creare hidden HWND: %s", e)
        return None


# ---------------------------------------------------------------------------
# Apertura / chiusura
# ---------------------------------------------------------------------------
def open_sdk(sdk_path=None):
    """Inizializza l'SDK Mountain con una hidden HWND e attende l'enumerazione."""
    global _sdk_opened
    sdk = _load_sdk(sdk_path)
    hwnd = _create_hidden_window()
    handle = ctypes.c_void_p(hwnd) if hwnd else None
    ok = sdk.OpenUSBDriver(handle)
    _sdk_opened = bool(ok)
    logger.info("OpenUSBDriver(hwnd=%s) -> %s", hwnd, ok)
    time.sleep(SDK_ENUM_SETTLE)
    return _sdk_opened


def close_sdk():
    global _sdk_opened
    if _sdk is None:
        return
    try:
        _sdk.CloseUSBDriver()
    except Exception:
        pass
    _sdk_opened = False


def get_dll_version():
    if _sdk is None or not hasattr(_sdk, "GetDLLVersion"):
        return None
    try:
        return int(_sdk.GetDLLVersion())
    except Exception:
        return None


def get_dev_count():
    """Numero di device Mountain connessi secondo l'SDK, o None se non disponibile."""
    if _sdk is None or not hasattr(_sdk, "GetDevCount"):
        return None
    n = c_int(0)
    try:
        ok = _sdk.GetDevCount(byref(n))
    except Exception:
        return None
    return n.value if ok else None


def is_device_plug(device_id):
    return bool(_sdk.IsDevicePlug(c_uint(device_id)))


def ap_enable(device_id, enable=True):
    return bool(_sdk.APEnable(c_bool(enable), c_uint(device_id)))


# ---------------------------------------------------------------------------
# Lettura / scrittura GUID
# ---------------------------------------------------------------------------
def _parse_guid(raw: bytes):
    """Estrae un GUID canonico dai primi GUID_LEN byte. Ritorna str|None."""
    head = raw[:GUID_LEN]
    if len(head) < GUID_LEN:
        return None
    # flash vuota / non inizializzata
    if all(b == 0x00 for b in head[:4]) or all(b == 0xFF for b in head[:4]):
        return None
    try:
        text = head.decode("ascii")
    except UnicodeDecodeError:
        return None
    try:
        return str(uuid.UUID(text))   # normalizzato lowercase
    except (ValueError, AttributeError):
        return None


def read_flash_guid(device_id):
    """Legge il GUID dal settore 0. Ritorna (guid|None, prev_id|-1).

    `prev_id` e' un byte legacy informativo (non e' necessario al provisioner).
    """
    buf = (ctypes.c_ubyte * SECTOR_SIZE)()
    ok = _sdk.ReadSectorMemory(
        GUID_SECTOR, GUID_ADDRESS, SECTOR_SIZE,
        ctypes.cast(buf, c_void_p), READ_INTERVAL, c_uint(device_id),
    )
    if not ok:
        return None, -1
    raw = bytes(buf)
    guid = _parse_guid(raw)
    prev = raw[GUID_LEN] if len(raw) > GUID_LEN else -1
    return guid, prev


def erase_flash(device_id, start=ERASE_START, end=ERASE_END):
    """Cancella i settori [start, end] (clear a 0xFF). Default 0..1 come Base Camp."""
    return bool(_sdk.EraseSectorMem(c_int(start), c_int(end), c_uint(device_id)))


def _write_once(device_id, guid_str):
    """Un singolo ciclo erase+write (senza verifica). Ritorna bool."""
    if not ap_enable(device_id, True):
        logger.warning("APEnable fallita su device %d", device_id)
        return False

    erased = False
    for _ in range(5):
        if erase_flash(device_id):
            erased = True
            break
        if not is_device_plug(device_id):
            return False
        time.sleep(OP_RETRY_PAUSE)
    if not erased:
        logger.error("EraseSectorMem fallita su device %d", device_id)
        return False

    buf = (ctypes.c_ubyte * SECTOR_SIZE)()
    for i, b in enumerate(guid_str.encode("ascii")):
        buf[i] = b
    for _ in range(5):
        if _sdk.WriteSectorMem(GUID_SECTOR, GUID_ADDRESS, SECTOR_SIZE,
                               ctypes.cast(buf, c_void_p), c_uint(device_id)):
            return True
        if not is_device_plug(device_id):
            return False
        time.sleep(OP_RETRY_PAUSE)
    logger.error("WriteSectorMem fallita su device %d", device_id)
    return False


def write_flash_guid(device_id, guid_str, *, verify=True, attempts=3):
    """Scrive il GUID nel settore 0 e lo verifica rileggendo.

    Ripete l'intero ciclo erase+write+verify fino a `attempts` volte.
    Ritorna True solo se la rilettura conferma il GUID atteso.
    """
    try:
        guid_str = str(uuid.UUID(guid_str))
    except (ValueError, AttributeError):
        raise ValueError("GUID non valido: " + str(guid_str))

    for attempt in range(1, attempts + 1):
        if not is_device_plug(device_id):
            logger.error("Device %d non connesso, write annullata", device_id)
            return False
        if not _write_once(device_id, guid_str):
            logger.warning("Write tentativo %d/%d fallito su device %d",
                            attempt, attempts, device_id)
            time.sleep(OP_RETRY_PAUSE)
            continue
        if not verify:
            return True
        time.sleep(VERIFY_PAUSE)
        got, _ = read_flash_guid(device_id)
        if got and got.lower() == guid_str.lower():
            logger.info("Device %d: GUID %s scritto e verificato", device_id, guid_str)
            return True
        logger.warning("Verifica fallita su device %d (tentativo %d/%d): "
                        "scritto=%s, riletto=%s",
                        device_id, attempt, attempts, guid_str, got)
        time.sleep(OP_RETRY_PAUSE)
    return False


def enumerate_flash_state(max_device_id=10):
    """Enumera i device connessi e lo stato GUID della loro flash.

    Ritorna lista di (device_id, guid|None, prev_id) per i soli device che
    rispondono in modo coerente all'SDK.
    """
    out = []
    count = get_dev_count()
    if count is None:
        logger.warning("GetDevCount non disponibile, scansione 1..%d", max_device_id)
    else:
        logger.info("GetDevCount = %d device", count)

    found = 0
    for did in range(1, max_device_id + 1):
        try:
            if not is_device_plug(did):
                continue
            ap_enable(did, True)
            time.sleep(0.15)
            guid, prev = read_flash_guid(did)
            out.append((did, guid, prev))
            found += 1
            if count is not None and found >= count:
                break
        except Exception:
            logger.exception("Errore enumerazione device %d", did)
            continue
    return out
