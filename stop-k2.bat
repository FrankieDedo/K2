@echo off
setlocal EnableDelayedExpansion
title K2 - Stop K2.App / K2.DisplayPad (libera i lock di build)

REM ============================================================
REM   Termina TUTTE le istanze di K2.App.exe e K2.DisplayPad.exe.
REM   Utile quando una build fallisce con
REM       MSB3027 "Il file e' bloccato da: K2.App (PID...)"
REM   perche' un'istanza precedente e' rimasta come zombie (es.
REM   crash dentro InitializeComponent prima che il loop di
REM   messaggi parta -> il processo non si chiude da solo).
REM ============================================================

echo.
echo ============================================================
echo   K2 - Stop processi K2
echo ============================================================
echo.

call :killproc "K2.App.exe"
call :killproc "K2.DisplayPad.exe"

REM --- Pausa breve perche' Windows rilasci i file ---------------
powershell -NoProfile -Command "Start-Sleep -Milliseconds 500" >nul

echo.
echo ============================================================
echo   Fatto. Ora puoi rilanciare build-check.bat o la build.
echo ============================================================
echo.
endlocal
exit /b 0


REM ===========================================================
REM Subroutine: killa un processo se esiste, in silenzio.
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
