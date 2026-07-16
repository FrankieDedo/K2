@echo off
REM ============================================================
REM  build-installer-ask.bat - double-click entry point: asks for
REM  the version number, then calls build-installer.bat with it.
REM  Leave blank and press Enter to use K2Setup.iss's default version.
REM ============================================================
cd /d "%~dp0"
set /p VER="Numero di versione (es. 1.2.3, vuoto = default): "
call build-installer.bat %VER%
