@echo off
REM ============================================================
REM  build-check.bat - builds K2's projects directly (does not
REM  depend on the .sln files, which live outside this folder)
REM  and collects all output in build-check.log for a quick
REM  pass/fail check.
REM
REM  Usage: double-click. Then send build-check.log (or paste the
REM  summary below) for review.
REM ============================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "LOG=%~dp0build-check.log"

echo K2 - build check - %DATE% %TIME%> "%LOG%"

echo.
echo [0] Stopping K2 processes (release file locks) ...
call :killproc "K2.App.exe"
call :killproc "K2.DisplayPad.exe"
call :killproc "K2.DisplayPad.Satellite.exe"

powershell -NoProfile -Command "Start-Sleep -Milliseconds 1500" >nul
echo   Processes stopped.

echo.
echo [1] Cleaning bin/obj ...
for %%D in (K2.App K2.Core K2.DisplayPad K2.DisplayPad.Satellite) do (
    if exist "%%D\bin" ( rd /s /q "%%D\bin" & echo   %%D\bin removed )
    if exist "%%D\obj" ( rd /s /q "%%D\obj" & echo   %%D\obj removed )
)
echo   Cleanup done.

echo.
echo [2/3] dotnet build K2.DisplayPad + K2.DisplayPad.Satellite (Debug, x64) ...
echo.>> "%LOG%"
echo === K2.DisplayPad.csproj  Debug x64 ===>> "%LOG%"
dotnet build ".\K2.DisplayPad\K2.DisplayPad.csproj" -c Debug -p:Platform=x64 >> "%LOG%" 2>&1

echo.>> "%LOG%"
echo === K2.DisplayPad.Satellite.csproj  Debug x64 ===>> "%LOG%"
dotnet build ".\K2.DisplayPad.Satellite\K2.DisplayPad.Satellite.csproj" -c Debug -p:Platform=x64 >> "%LOG%" 2>&1

echo [3/3] dotnet build K2.App (Debug, x86) ...
echo.>> "%LOG%"
echo === K2.App.csproj  Debug x86 ===>> "%LOG%"
dotnet build ".\K2.App\K2.App.csproj" -c Debug -p:Platform=x86 >> "%LOG%" 2>&1

echo.
echo ------------------------------------------------------------
echo Summary (errors and warnings):
echo ------------------------------------------------------------
findstr /I /R /C:": error" /C:": warning" "%LOG%"
echo ------------------------------------------------------------
echo Full output saved to:  %LOG%
echo.
pause
exit /b 0

REM ===========================================================
REM Subroutine: kill a process if it exists, silently.
REM Uses tasklist/taskkill to handle dotted names
REM (e.g. K2.DisplayPad.Satellite.exe)
REM ===========================================================
:killproc
set "PROC=%~1"
tasklist /FI "IMAGENAME eq %PROC%" 2>nul | find /I "%PROC%" >nul
if errorlevel 1 (
    echo       %PROC% : not running.
    exit /b 0
)
REM Direct force kill + retry with wait (up to 5 attempts)
set "_KP_TRIES=0"
:kp_loop
taskkill /F /IM "%PROC%" /T >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1
tasklist /FI "IMAGENAME eq %PROC%" 2>nul | find /I "%PROC%" >nul
if errorlevel 1 (
    echo       %PROC% : killed.
    exit /b 0
)
set /a _KP_TRIES+=1
if %_KP_TRIES% LSS 5 goto :kp_loop
echo       %PROC% : WARNING - still running after 5 attempts!
exit /b 0
