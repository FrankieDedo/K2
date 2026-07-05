@echo off
REM ============================================================
REM  build-check.bat - compila le solution di K2 e raccoglie
REM  tutto l'output in build-check.log, cosi' da poter verificare
REM  rapidamente errori e warning di compilazione.
REM
REM  Uso: doppio click. Poi invia build-check.log (o incolla il
REM  riepilogo qui sotto) per la verifica.
REM ============================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "LOG=%~dp0build-check.log"

echo K2 - build di verifica - %DATE% %TIME%> "%LOG%"

echo.
echo [0] Stop processi K2 (libera lock file) ...
call :killproc "K2.App.exe"
call :killproc "K2.DisplayPad.exe"
call :killproc "K2.DisplayPad.Satellite.exe"

powershell -NoProfile -Command "Start-Sleep -Milliseconds 1500" >nul
echo   Processi terminati.

echo.
echo [1] Pulizia bin/obj ...
for %%D in (K2.App K2.Core K2.DisplayPad K2.DisplayPad.Satellite) do (
    if exist "%%D\bin" ( rd /s /q "%%D\bin" & echo   %%D\bin rimosso )
    if exist "%%D\obj" ( rd /s /q "%%D\obj" & echo   %%D\obj rimosso )
)
echo   Pulizia completata.

echo.
echo [2/3] dotnet build K2.DisplayPad.sln  (Debug, x64) ...
echo.>> "%LOG%"
echo === K2.DisplayPad.sln  Debug x64 ===>> "%LOG%"
dotnet build ".\K2.DisplayPad.sln" -c Debug -p:Platform=x64 >> "%LOG%" 2>&1

echo [3/3] dotnet build K2.sln           (Debug, x86) ...
echo.>> "%LOG%"
echo === K2.sln  Debug x86 ===>> "%LOG%"
dotnet build ".\K2.sln" -c Debug -p:Platform=x86 >> "%LOG%" 2>&1

echo.
echo ------------------------------------------------------------
echo Riepilogo (errori e warning):
echo ------------------------------------------------------------
findstr /I /R /C:": error" /C:": warning" "%LOG%"
echo ------------------------------------------------------------
echo Output completo salvato in:  %LOG%
echo.
pause
exit /b 0

REM ===========================================================
REM Subroutine: killa un processo se esiste, in silenzio.
REM Usa wmic per gestire nomi con punti (es. K2.DisplayPad.Satellite.exe)
REM ===========================================================
:killproc
set "PROC=%~1"
tasklist /FI "IMAGENAME eq %PROC%" 2>nul | find /I "%PROC%" >nul
if errorlevel 1 (
    echo       %PROC% : non in esecuzione.
    exit /b 0
)
REM Force kill diretto + retry con attesa (fino a 5 tentativi)
set "_KP_TRIES=0"
:kp_loop
taskkill /F /IM "%PROC%" /T >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1
tasklist /FI "IMAGENAME eq %PROC%" 2>nul | find /I "%PROC%" >nul
if errorlevel 1 (
    echo       %PROC% : killato.
    exit /b 0
)
set /a _KP_TRIES+=1
if %_KP_TRIES% LSS 5 goto :kp_loop
echo       %PROC% : ATTENZIONE - ancora in esecuzione dopo 5 tentativi!
exit /b 0
