"""Enumerazione HID dei DisplayPad Mountain via Windows SetupAPI.

Restituisce per ogni dispositivo il suo Device Instance Path (stabile per
porta USB) nell'ordine di enumerazione di Windows — lo stesso ordine in cui
l'SDK Mountain (SDKDLL.dll) assegna i DeviceId interni.
"""
from __future__ import annotations

import ctypes
import logging
import re
from ctypes import wintypes
from dataclasses import dataclass

from .config import MOUNTAIN_VID

logger = logging.getLogger(__name__)


# ---------- Windows API bindings -----------------------------------------------

# DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
DIGCF_PRESENT = 0x00000002
DIGCF_DEVICEINTERFACE = 0x00000010
INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value
ERROR_INSUFFICIENT_BUFFER = 122
ERROR_NO_MORE_ITEMS = 259


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", wintypes.DWORD),
        ("Data2", wintypes.WORD),
        ("Data3", wintypes.WORD),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("InterfaceClassGuid", GUID),
        ("Flags", wintypes.DWORD),
        ("Reserved", ctypes.c_void_p),
    ]


class SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("ClassGuid", GUID),
        ("DevInst", wintypes.DWORD),
        ("Reserved", ctypes.c_void_p),
    ]


# Loading lazy: il modulo deve essere importabile anche su Linux (test/dev).
try:
    _setupapi = ctypes.WinDLL("setupapi", use_last_error=True)
    _hid = ctypes.WinDLL("hid", use_last_error=True)
    _HAS_WIN_API = True
except (OSError, AttributeError):
    _setupapi = None
    _hid = None
    _HAS_WIN_API = False


if _HAS_WIN_API:
    _setupapi.SetupDiGetClassDevsW.restype = wintypes.HANDLE
    _setupapi.SetupDiGetClassDevsW.argtypes = [
        ctypes.POINTER(GUID), wintypes.LPCWSTR, wintypes.HWND, wintypes.DWORD,
    ]

    _setupapi.SetupDiDestroyDeviceInfoList.restype = wintypes.BOOL
    _setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wintypes.HANDLE]

    _setupapi.SetupDiEnumDeviceInterfaces.restype = wintypes.BOOL
    _setupapi.SetupDiEnumDeviceInterfaces.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(SP_DEVINFO_DATA),
        ctypes.POINTER(GUID),
        wintypes.DWORD,
        ctypes.POINTER(SP_DEVICE_INTERFACE_DATA),
    ]

    _setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wintypes.BOOL
    _setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(SP_DEVICE_INTERFACE_DATA),
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.POINTER(SP_DEVINFO_DATA),
    ]

    _setupapi.SetupDiGetDeviceInstanceIdW.restype = wintypes.BOOL
    _setupapi.SetupDiGetDeviceInstanceIdW.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(SP_DEVINFO_DATA),
        wintypes.LPWSTR,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    ]

    _hid.HidD_GetHidGuid.restype = None
    _hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]


# ---------- Tipi di ritorno ----------------------------------------------------

@dataclass(frozen=True)
class DisplayPadDevice:
    """Rappresenta un singolo DisplayPad fisicamente collegato."""
    enumeration_index: int   # 0-based, ordine di SetupDiEnumDeviceInterfaces
    instance_id: str         # es. HID\\VID_3282&PID_xxxx\\6&abc&0&0001
    interface_path: str      # path completo aperto-bile con CreateFile
    vid: int
    pid: int

    @property
    def stable_key(self) -> str:
        """Chiave persistente: instance_id case-insensitive."""
        return self.instance_id.upper()

    @property
    def short_label(self) -> str:
        """Label leggibile per UI (port suffix)."""
        m = re.search(r"\\([^\\]+)$", self.instance_id)
        return m.group(1) if m else self.instance_id


# ---------- Enumerazione -------------------------------------------------------

_INSTANCE_RE = re.compile(r"VID_([0-9A-Fa-f]{4}).+PID_([0-9A-Fa-f]{4})", re.IGNORECASE)


def _hid_class_guid() -> GUID:
    g = GUID()
    _hid.HidD_GetHidGuid(ctypes.byref(g))
    return g


def enumerate_displaypads(vid: int = MOUNTAIN_VID) -> list[DisplayPadDevice]:
    """Restituisce tutti i DisplayPad Mountain attualmente collegati,
    nell'ordine in cui Windows li enumera (= ordine SDK)."""
    if not _HAS_WIN_API:
        raise RuntimeError("Enumerazione HID disponibile solo su Windows")

    hid_guid = _hid_class_guid()
    h_dev_info = _setupapi.SetupDiGetClassDevsW(
        ctypes.byref(hid_guid),
        None,
        None,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
    )
    if h_dev_info == INVALID_HANDLE_VALUE or h_dev_info is None:
        raise ctypes.WinError(ctypes.get_last_error())

    devices: list[DisplayPadDevice] = []
    seen_instances: set[str] = set()
    try:
        idx = 0
        while True:
            iface = SP_DEVICE_INTERFACE_DATA()
            iface.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
            ok = _setupapi.SetupDiEnumDeviceInterfaces(
                h_dev_info, None, ctypes.byref(hid_guid), idx, ctypes.byref(iface)
            )
            if not ok:
                err = ctypes.get_last_error()
                if err == ERROR_NO_MORE_ITEMS:
                    break
                raise ctypes.WinError(err)

            # Prima chiamata per ottenere la dimensione richiesta
            required = wintypes.DWORD(0)
            dev_info = SP_DEVINFO_DATA()
            dev_info.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
            _setupapi.SetupDiGetDeviceInterfaceDetailW(
                h_dev_info, ctypes.byref(iface), None, 0,
                ctypes.byref(required), None,
            )

            # Buffer dinamico per SP_DEVICE_INTERFACE_DETAIL_DATA_W:
            #   DWORD cbSize; WCHAR DevicePath[ANYSIZE_ARRAY];
            buf = ctypes.create_string_buffer(required.value)
            # cbSize del header: su x64 = 8 (DWORD + 4 byte pad? in realta
            # SP_DEVICE_INTERFACE_DETAIL_DATA_W ha cbSize=6 su x86 e 8 su x64).
            # Soluzione robusta: scrivi entrambe le possibilita; Windows
            # accetta cbSize == offsetof(DevicePath).
            cb_header = 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6
            ctypes.memmove(buf, ctypes.byref(wintypes.DWORD(cb_header)), 4)

            ok = _setupapi.SetupDiGetDeviceInterfaceDetailW(
                h_dev_info, ctypes.byref(iface),
                ctypes.cast(buf, ctypes.c_void_p),
                required.value, None, ctypes.byref(dev_info),
            )
            if not ok:
                raise ctypes.WinError(ctypes.get_last_error())

            device_path = ctypes.wstring_at(
                ctypes.addressof(buf) + 4, (required.value - 4) // 2
            ).rstrip("\x00")

            # Filtra subito per VID Mountain (path contiene VID_3282).
            m = _INSTANCE_RE.search(device_path)
            if not m or int(m.group(1), 16) != vid:
                idx += 1
                continue
            this_pid = int(m.group(2), 16)

            # Instance ID stabile (raggruppa interfaces della stessa USB device).
            id_buf = ctypes.create_unicode_buffer(512)
            req2 = wintypes.DWORD(0)
            ok = _setupapi.SetupDiGetDeviceInstanceIdW(
                h_dev_info, ctypes.byref(dev_info), id_buf, 512, ctypes.byref(req2)
            )
            if not ok:
                raise ctypes.WinError(ctypes.get_last_error())
            instance_id = id_buf.value

            # Un DisplayPad pu esporre piu interfaces HID (es. due "collection").
            # Usiamo la prima vista per ogni instance_id.
            key = instance_id.upper()
            if key not in seen_instances:
                seen_instances.add(key)
                devices.append(DisplayPadDevice(
                    enumeration_index=len(devices),
                    instance_id=instance_id,
                    interface_path=device_path,
                    vid=vid,
                    pid=this_pid,
                ))
                logger.debug(
                    "Enum #%d: %s (PID=0x%04X)",
                    len(devices) - 1, instance_id, this_pid,
                )

            idx += 1
    finally:
        _setupapi.SetupDiDestroyDeviceInfoList(h_dev_info)

    return devices
