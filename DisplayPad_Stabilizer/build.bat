@echo off
REM Build dello stabilizer in singolo .exe con PyInstaller.
REM Richiede Python 3.11+ e PyInstaller installato.

setlocal
cd /d "%~dp0"

where python >nul 2>&1 || (
    echo Python non trovato nel PATH.
    exit /b 1
)

python -m pip install --upgrade pyinstaller || exit /b 1

REM Costruiamo in modalita console (NO --windowed):
REM serve per vedere output di --install/--status/--fix, e per ricevere
REM messaggi stderr quando viene rilanciato elevato.
python -m PyInstaller ^
    --noconfirm ^
    --clean ^
    --onefile ^
    --name DisplayPadStabilizer ^
    --add-data "stabilizer;stabilizer" ^
    --hidden-import sqlite3 ^
    --hidden-import tkinter ^
    --hidden-import tkinter.ttk ^
    --hidden-import tkinter.messagebox ^
    stabilizer_main.py

if errorlevel 1 (
    echo Build fallita.
    exit /b 1
)

echo.
echo Build OK. Eseguibile in dist\DisplayPadStabilizer.exe
endlocal
