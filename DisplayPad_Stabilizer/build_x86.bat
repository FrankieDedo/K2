@echo off
REM Build x86 dello Stabilizer per usare SDKDLL.dll Mountain (PE32 i386).
REM Richiede Python 3.10..3.13 a 32-bit (3.15 e' troppo nuovo per molti pacchetti).

setlocal
cd /d "%~dp0"

REM Cerca Python x86 in ordine: py launcher con flag specifici, poi path tipici.
set "PY="

REM 1) py launcher con preferenze precise
where py >nul 2>&1
if %ERRORLEVEL%==0 (
    for %%V in (3.12-32 3.11-32 3.10-32 3.13-32 3-32) do (
        if not defined PY (
            py -%%V -c "import struct,sys;sys.exit(0 if struct.calcsize('P')==4 else 1)" >nul 2>&1
            if not errorlevel 1 set "PY=py -%%V"
        )
    )
)

REM 2) Path standard di installazione (preferisci 3.12/3.11 a 3.15)
if not defined PY (
    for %%P in (
        "%LOCALAPPDATA%\Programs\Python\Python312-32\python.exe"
        "%LOCALAPPDATA%\Programs\Python\Python311-32\python.exe"
        "%LOCALAPPDATA%\Programs\Python\Python310-32\python.exe"
        "%LOCALAPPDATA%\Programs\Python\Python313-32\python.exe"
        "%LOCALAPPDATA%\Programs\Python\Python315-32\python.exe"
        "C:\Python312-32\python.exe"
        "C:\Python311-32\python.exe"
    ) do (
        if not defined PY (
            if exist %%P (
                %%P -c "import struct,sys;sys.exit(0 if struct.calcsize('P')==4 else 1)" >nul 2>&1
                if not errorlevel 1 set "PY=%%~P"
            )
        )
    )
)

if not defined PY (
    echo Non ho trovato un Python 32-bit utilizzabile.
    echo Installa Python 3.12 32-bit da:
    echo   https://www.python.org/downloads/windows/
    echo   ^(seleziona "Windows installer (32-bit)" - 3.12 o 3.13^)
    echo.
    echo NOTA: Python 3.15 e' troppo recente per molti pacchetti.
    exit /b 1
)

echo Uso Python: %PY%
%PY% -c "import sys,struct; print('  Versione:', sys.version); print('  Arch:', struct.calcsize('P')*8, 'bit')"
if errorlevel 1 exit /b 1

echo.
echo Pulizia PyInstaller vecchio e installazione versione recente...
%PY% -m pip uninstall -y pyinstaller pyinstaller-hooks-contrib 2>nul
%PY% -m pip install --upgrade pip setuptools wheel || exit /b 1
%PY% -m pip install --upgrade "pyinstaller>=6.10" || exit /b 1

REM Verifica versione installata
%PY% -m PyInstaller --version
if errorlevel 1 exit /b 1

echo.
echo Avvio build...
%PY% -m PyInstaller ^
    --noconfirm ^
    --clean ^
    --onefile ^
    --name DisplayPadStabilizer_x86 ^
    --add-data "stabilizer;stabilizer" ^
    --hidden-import sqlite3 ^
    --hidden-import tkinter ^
    --hidden-import tkinter.ttk ^
    --hidden-import tkinter.messagebox ^
    stabilizer_main.py

if errorlevel 1 (
    echo Build x86 fallita.
    exit /b 1
)

echo.
echo Build x86 OK. Eseguibile: dist\DisplayPadStabilizer_x86.exe
echo.
echo Per il flash fingerprint usa:
echo   dist\DisplayPadStabilizer_x86.exe --flash-fingerprint
echo.
endlocal
