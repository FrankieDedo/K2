@echo off
setlocal EnableDelayedExpansion
title K2 - Stop Base Camp (libera DisplayPad)

REM ============================================================
REM   Ferma tutti i processi/servizi di Mountain Base Camp che
REM   tengono aperto l'handle HID del DisplayPad, cosi' K2 puo'
REM   connettersi al dispositivo senza conflitti.
REM
REM   NB: richiede privilegi di amministratore per fermare il
REM   servizio Windows. Se non sei admin, salta lo stop del
REM   servizio e killa solo i processi utente.
REM ============================================================

echo.
echo ============================================================
echo   K2 - Stop Base Camp
echo ============================================================
echo.

REM --- Controllo privilegi admin (serve per "sc stop") ---------
net session >nul 2>&1
if errorlevel 1 (
    echo [!] Non sei amministratore: posso killare solo i processi
    echo     utente, NON fermare il servizio "BaseCamp Service".
    echo     Se K2 non riesce ancora a collegarsi, rilancia questo
    echo     .bat come amministratore.
    echo.
    set "IS_ADMIN=0"
) else (
    set "IS_ADMIN=1"
)

REM --- 1) Ferma il servizio Windows di Base Camp ---------------
if "%IS_ADMIN%"=="1" (
    echo [1/3] Stop servizio "BaseCamp Service"...
    sc query "BaseCamp Service" >nul 2>&1
    if not errorlevel 1 (
        sc stop "BaseCamp Service" >nul 2>&1
        if errorlevel 1 (
            echo       servizio gia' fermo o non fermabile.
        ) else (
            echo       servizio fermato.
        )
    ) else (
        echo       servizio non installato, skip.
    )
) else (
    echo [1/3] Skip stop servizio (non admin).
)

REM --- 2) Killa i processi utente di Base Camp -----------------
echo [2/3] Killing processi Base Camp...
call :killproc "Base Camp.exe"
call :killproc "BaseCamp.Service.exe"
call :killproc "Basecamp.Worker.exe"
call :killproc "MountainDisplayPadWorker.exe"
call :killproc "Makalu Monitor.exe"

REM --- 3) Piccola pausa per rilascio handle HID ----------------
echo [3/3] Attendo rilascio handle HID...
powershell -NoProfile -Command "Start-Sleep -Milliseconds 800" >nul

echo.
echo ============================================================
echo   Fatto. Ora puoi lanciare K2.DisplayPad.
echo ============================================================
echo.
pause
endlocal
exit /b 0


REM ===========================================================
REM Subroutine: killa un processo se esiste, in silenzio.
REM Uso:  call :killproc "nome.exe"
REM ===========================================================
:killproc
set "PROC=%~1"
tasklist /FI "IMAGENAME eq %PROC%" 2>nul | find /I "%PROC%" >nul
if not errorlevel 1 (
    taskkill /F /IM "%PROC%" /T >nul 2>&1
    if errorlevel 1 (
        echo       %PROC% : kill fallito.
    ) else (
        echo       %PROC% : killato.
    )
) else (
    echo       %PROC% : non in esecuzione.
)
exit /b 0
