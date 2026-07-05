"""Cycle dei DisplayPad Mountain via CM_Disable/Enable_DevNode (cfgmgr32).

Usa le Configuration Manager API invece di pnputil: funzionano su tutte
le versioni di Windows e operano sul device USB composito padre.
"""
from __future__ import annotations

import ctypes
import logging
import time
from ctypes import wintypes

from .hid_enum import enumerate_displaypads

logger = logging.getLogger(__name__)

DISPLAYPAD_PIDS = {0x0009}
MAX_TARGETS = 6
CR_SUCCESS = 0

try:
    _cfgmgr = ctypes.WinDLL("cfgmgr32", use_last_error=True)
except OSError:
    _cfgmgr = None


def _device_id_of(devinst):
    size = wintypes.ULONG(0)
    _cfgmgr.CM_Get_Device_ID_Size(ctypes.byref(size), devinst, 0)
    buf = ctypes.create_unicode_buffer(size.value + 1)
    if _cfgmgr.CM_Get_Device_IDW(devinst, buf, size.value + 1, 0) != 0:
        return None
    return buf.value


def _locate_node(instance_id):
    node = wintypes.DWORD(0)
    if _cfgmgr.CM_Locate_DevNodeW(
        ctypes.byref(node), ctypes.c_wchar_p(instance_id), 0
    ) != 0:
        return None
    return node


def _hid_interface_to_usb_composite(hid_instance_id):
    """Risale da HID al device USB COMPOSITO (USB\\... senza &MI_)."""
    if _cfgmgr is None:
        return None
    try:
        node = _locate_node(hid_instance_id)
        if node is None:
            return None
        for _ in range(6):
            parent = wintypes.DWORD(0)
            if _cfgmgr.CM_Get_Parent(ctypes.byref(parent), node, 0) != 0:
                return None
            pid_str = _device_id_of(parent)
            if pid_str is None:
                return None
            up = pid_str.upper()
            if up.startswith("USB"):
                if "&MI_" not in up:
                    return pid_str
                node = parent
                continue
            return None
        return None
    except Exception:
        logger.exception("Errore risalita composite per %s", hid_instance_id)
        return None


def _select_usb_targets(vid=0x3282):
    targets = []
    seen = set()
    for d in enumerate_displaypads(vid=vid):
        if d.pid not in DISPLAYPAD_PIDS:
            continue
        parent = _hid_interface_to_usb_composite(d.instance_id)
        if parent is None:
            logger.warning("No USB composite per %s", d.instance_id)
            continue
        if parent.upper() in seen:
            continue
        seen.add(parent.upper())
        targets.append(parent)
    return targets


def cycle_single_usb_device(usb_instance_id):
    """Disable + Enable di un device USB composito via CM API."""
    node = _locate_node(usb_instance_id)
    if node is None:
        logger.error("Device non trovato: %s", usb_instance_id)
        return False
    rc = _cfgmgr.CM_Disable_DevNode(node, 0)
    if rc != CR_SUCCESS:
        logger.error("CM_Disable_DevNode FAIL (CR=%d): %s", rc, usb_instance_id)
        return False
    time.sleep(1.0)
    # Ri-localizza il node (dopo disable l'handle puo' essere stale)
    node2 = _locate_node(usb_instance_id)
    if node2 is None:
        node2 = node
    rc = _cfgmgr.CM_Enable_DevNode(node2, 0)
    if rc != CR_SUCCESS:
        logger.error("CM_Enable_DevNode FAIL (CR=%d): %s - device LASCIATO "
                      "DISABILITATO, riabilitare da Gestione dispositivi!",
                      rc, usb_instance_id)
        return False
    logger.info("Cycle OK: %s", usb_instance_id)
    return True


def cycle_all_displaypads(vid=0x3282, *, settle_seconds=4.0):
    if _cfgmgr is None:
        logger.error("cfgmgr32 non disponibile")
        return 0, []
    targets = _select_usb_targets(vid=vid)
    if not targets:
        logger.warning("Nessun DisplayPad (PID %s) trovato", DISPLAYPAD_PIDS)
        return 0, []
    if len(targets) > MAX_TARGETS:
        logger.error("Trovati %d target (> %d): aborto", len(targets), MAX_TARGETS)
        return 0, list(targets)
    logger.info("Cycle di %d DisplayPad (CM API):", len(targets))
    for t in targets:
        logger.info("  - %s", t)
    success = 0
    failures = []
    for t in targets:
        if cycle_single_usb_device(t):
            success += 1
        else:
            failures.append(t)
        time.sleep(0.5)
    logger.info("Attendo %.1fs per re-enumerazione...", settle_seconds)
    time.sleep(settle_seconds)
    return success, failures
