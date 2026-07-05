@echo off
REM Installa Python embeddable in K2\lib\python-embed\ (vedi setup-python-embed.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-python-embed.ps1"
echo.
pause
