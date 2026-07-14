@echo off
REM ============================================================
REM  build-installer.bat - publishes K2.App (win-x86), the
REM  DisplayPad satellite helper and the standalone K2.DisplayPad
REM  app (both win-x64) as self-contained builds, stages them
REM  into the layout K2Setup.iss expects, compiles the Inno Setup
REM  installer, and zips the same tree as a portable package.
REM
REM  Everything referenced lives inside K2\ except the top-level
REM  LICENSE (one folder up - see _PROJECT_MAP.md layout).
REM
REM  Requires: dotnet SDK, Inno Setup 6 (ISCC.exe).
REM  Usage: double-click, or run from anywhere.
REM ============================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0.."
set "ROOT=%CD%"
set "PUB=%ROOT%\Installer\publish"
set "OUT=%ROOT%\Installer\output"
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" (
    echo ERROR: Inno Setup compiler not found at "%ISCC%"
    echo Install it from https://jrsoftware.org/isinfo.php, or edit the ISCC
    echo path at the top of this script if you installed it elsewhere.
    pause
    exit /b 1
)

echo.
echo [1] Cleaning previous publish/output ...
if exist "%PUB%" rd /s /q "%PUB%"
if exist "%OUT%" rd /s /q "%OUT%"

echo.
echo [2/4] Publishing K2.App (win-x86, self-contained) ...
dotnet publish "%ROOT%\K2.App\K2.App.csproj" -c Release -r win-x86 --self-contained true -p:Platform=x86 -o "%PUB%\K2.App"
if errorlevel 1 goto :fail

echo.
echo [3/4] Publishing K2.DisplayPad.Satellite (win-x64, self-contained) into Satellite\ ...
dotnet publish "%ROOT%\K2.DisplayPad.Satellite\K2.DisplayPad.Satellite.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "%PUB%\K2.App\Satellite"
if errorlevel 1 goto :fail

echo.
echo [4/4] Publishing K2.DisplayPad standalone (win-x64, self-contained) into DisplayPad\ ...
dotnet publish "%ROOT%\K2.DisplayPad\K2.DisplayPad.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "%PUB%\K2.App\DisplayPad"
if errorlevel 1 goto :fail

echo.
echo [check] Verifying no non-redistributable vendor DLLs leaked into the publish output ...
set "LEAK=0"
for %%F in (MacroPadSDK.dll SDKDLL.dll Everest360_USB.dll) do (
    for /r "%PUB%" %%G in (%%F) do (
        if exist "%%G" (
            echo   WARNING: found non-redistributable %%F at %%G
            set "LEAK=1"
        )
    )
)
if "!LEAK!"=="1" (
    echo   Remove the file^(s^) above ^(or the local _reference\native\ copy that
    echo   produced them^) before shipping this package - see DISTRIBUTION.md.
    pause
)

echo.
echo [5] Compiling Inno Setup installer ...
if not exist "%OUT%" mkdir "%OUT%"
"%ISCC%" "%ROOT%\Installer\K2Setup.iss"
if errorlevel 1 goto :fail

echo.
echo [6] Building portable ZIP (same tree as the installer) ...
powershell -NoProfile -Command "Compress-Archive -Path '%PUB%\K2.App\*' -DestinationPath '%OUT%\K2-portable.zip' -Force"

echo.
echo ------------------------------------------------------------
echo Done. Installer + portable zip are in:  %OUT%
echo ------------------------------------------------------------
pause
exit /b 0

:fail
echo.
echo BUILD FAILED - see output above.
pause
exit /b 1
