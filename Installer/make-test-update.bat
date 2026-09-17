@echo off
REM ============================================================
REM  make-test-update.bat - builds a fake "newer version" of K2
REM  and runs the in-place self-update against the copy already
REM  installed on this PC, WITHOUT a GitHub release, without the
REM  Inno installer and without zipping 250 MB.
REM
REM  It publishes only K2.App (win-x86, self-contained) stamped
REM  with the version you pass, into Installer\publish-test\K2.App,
REM  then starts the installed K2.App.exe with
REM      --test-update "<that folder>"
REM  which is exactly the code path a real update takes:
REM  stage -> launch the NEW exe with --apply-update -> wait for
REM  K2 to exit -> mirror only the changed files -> relaunch K2.
REM
REM  The Satellite\ and DisplayPad\ subtrees are NOT rebuilt here
REM  (the mirror never deletes anything, so the installed ones
REM  simply stay in place) - build-installer.bat is still the
REM  script that produces a complete package.
REM
REM  Usage:  make-test-update.bat 1.3.99  [ "C:\Path\To\Installed\K2" ]
REM
REM  Watch the result in:  %LOCALAPPDATA%\K2\update\update.log
REM ============================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0.."
set "ROOT=%CD%"
set "PUB=%ROOT%\Installer\publish-test\K2.App"
set "VER=%~1"
set "TARGET=%~2"

if "%VER%"=="" (
    echo Usage: make-test-update.bat ^<version^> [installed K2 folder]
    echo   e.g. make-test-update.bat 1.3.99
    pause
    exit /b 1
)

REM ---- locate the installed K2, unless it was passed explicitly ----
if "%TARGET%"=="" (
    for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{5C6A9E2A-6C6F-4C7B-9C64-1B7B6C7C7A21}_is1" /v InstallLocation 2^>nul ^| find "InstallLocation"') do set "TARGET=%%B"
)
if "%TARGET%"=="" if exist "%ProgramFiles(x86)%\K2\K2.App.exe" set "TARGET=%ProgramFiles(x86)%\K2"
if "%TARGET%"=="" if exist "%ProgramFiles%\K2\K2.App.exe" set "TARGET=%ProgramFiles%\K2"
if "%TARGET:~-1%"=="\" set "TARGET=%TARGET:~0,-1%"

if not exist "%TARGET%\K2.App.exe" (
    echo ERROR: no installed K2 found^^!  Pass the folder as the second argument:
    echo    make-test-update.bat %VER% "C:\Program Files ^(x86^)\K2"
    pause
    exit /b 1
)

echo.
echo Target install : %TARGET%
echo Test version   : %VER%
echo.
echo [1/2] Publishing K2.App (win-x86, self-contained, Version=%VER%) ...
if exist "%PUB%" rd /s /q "%PUB%"
dotnet publish "%ROOT%\K2.App\K2.App.csproj" -c Release -r win-x86 --self-contained true -p:Platform=x86 -p:Version=%VER% -o "%PUB%"
if errorlevel 1 goto :fail

echo.
echo [2/2] Launching the self-update (UAC prompt expected - K2.App.exe is elevated) ...
echo       "%TARGET%\K2.App.exe" --test-update "%PUB%"
start "" "%TARGET%\K2.App.exe" --test-update "%PUB%"

echo.
echo ------------------------------------------------------------
echo K2 should close (if it was running), get replaced and restart
echo on version %VER%. Trace:
echo    %LOCALAPPDATA%\K2\update\update.log
echo ------------------------------------------------------------
pause
exit /b 0

:fail
echo.
echo BUILD FAILED - see output above.
pause
exit /b 1
