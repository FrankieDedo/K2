"""Registra/disinstalla la scheduled task Windows che esegue il fix al login.

Usa schtasks.exe per non dipendere da pywin32. La task gira nel contesto
dell'utente corrente con HighestAvailable, quindi serve elevation per
registrarla.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

TASK_NAME = "DisplayPadStabilizer_AutoFix"


def _schtasks(args):
    return subprocess.run(
        ["schtasks.exe", *args],
        capture_output=True,
        text=True,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )


def install_scheduled_task(exe_path, db_path, basecamp_exe, *, delay_seconds=15, watch_mode=False):
    """Registra la task. exe_path = stabilizer.exe (o python.exe + script)."""
    exe_path = Path(exe_path)
    if not exe_path.exists():
        raise FileNotFoundError("Eseguibile stabilizer non trovato: " + str(exe_path))

    action = "--watch" if watch_mode else "--fix"
    if exe_path.suffix.lower() == ".py":
        cmd = (
            '"' + sys.executable + '" "' + str(exe_path) + '" ' + action + ' '
            '--db "' + str(db_path) + '" --exe "' + str(basecamp_exe) + '"'
        )
    else:
        cmd = (
            '"' + str(exe_path) + '" ' + action + ' '
            '--db "' + str(db_path) + '" --exe "' + str(basecamp_exe) + '"'
        )

    xml = (
        '<?xml version="1.0" encoding="UTF-16"?>\n'
        '<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">\n'
        '  <RegistrationInfo>\n'
        '    <Description>Ripara mapping DeviceId Base Camp al login utente.</Description>\n'
        '  </RegistrationInfo>\n'
        '  <Triggers>\n'
        '    <LogonTrigger>\n'
        '      <Enabled>true</Enabled>\n'
        '      <Delay>PT' + str(delay_seconds) + 'S</Delay>\n'
        '    </LogonTrigger>\n'
        '  </Triggers>\n'
        '  <Principals>\n'
        '    <Principal id="Author">\n'
        '      <LogonType>InteractiveToken</LogonType>\n'
        '      <RunLevel>HighestAvailable</RunLevel>\n'
        '    </Principal>\n'
        '  </Principals>\n'
        '  <Settings>\n'
        '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n'
        '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n'
        '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n'
        '    <AllowHardTerminate>true</AllowHardTerminate>\n'
        '    <StartWhenAvailable>true</StartWhenAvailable>\n'
        '    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n'
        '    <AllowStartOnDemand>true</AllowStartOnDemand>\n'
        '    <Enabled>true</Enabled>\n'
        '    <Hidden>false</Hidden>\n'
        '    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n'
        '    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>\n'
        '    <WakeToRun>false</WakeToRun>\n'
        '    <ExecutionTimeLimit>' + ('PT0S' if watch_mode else 'PT5M') + '</ExecutionTimeLimit>\n'
        '    <Priority>5</Priority>\n'
        '  </Settings>\n'
        '  <Actions Context="Author">\n'
        '    <Exec>\n'
        '      <Command>cmd.exe</Command>\n'
        '      <Arguments>/c ' + cmd + '</Arguments>\n'
        '    </Exec>\n'
        '  </Actions>\n'
        '</Task>\n'
    )

    fd, tmp = tempfile.mkstemp(suffix=".xml")
    try:
        with os.fdopen(fd, "w", encoding="utf-16") as f:
            f.write(xml)
        r = _schtasks(["/Create", "/F", "/TN", TASK_NAME, "/XML", tmp])
        if r.returncode != 0:
            err = (r.stderr or r.stdout or "").strip()
            low = err.lower()
            if "ccesso negato" in err or "access is denied" in low:
                raise PermissionError(
                    "Registrazione della scheduled task negata da Windows. "
                    "Lo Stabilizer deve girare come amministratore: rilancia "
                    "via UAC oppure aprilo da un terminale avviato con "
                    "'Esegui come amministratore'."
                )
            raise RuntimeError("schtasks fallita (rc=" + str(r.returncode) + "): " + err)
        print("Scheduled task '" + TASK_NAME + "' registrata correttamente.")
        print("Verra' eseguita ad ogni login utente (delay " + str(delay_seconds) + "s).")
    finally:
        try:
            os.unlink(tmp)
        except OSError:
            pass


def uninstall_scheduled_task():
    r = _schtasks(["/Delete", "/F", "/TN", TASK_NAME])
    if r.returncode == 0:
        print("Scheduled task '" + TASK_NAME + "' rimossa.")
    else:
        out = (r.stderr or r.stdout or "").strip()
        print("Rimozione fallita o task assente: " + out)
