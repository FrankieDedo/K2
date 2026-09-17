@echo off
REM ============================================================
REM  build-check.bat - builds K2.UnityLink (the generic BepInEx
REM  mod) on its own, Release config. Separate from K2's own
REM  build-check.bat: this project is not in K2.sln/K2.DisplayPad
REM  .sln and ships INTO other games, not into K2 itself, so it
REM  has nothing to kill/clean beyond its own bin/obj.
REM
REM  Usage: double-click. Then send build-check.log (or paste the
REM  summary below) for review. Output DLL on success:
REM  bin\Release\K2.UnityLink.dll
REM ============================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "LOG=%~dp0build-check.log"

echo K2.UnityLink - build check - %DATE% %TIME%> "%LOG%"

echo.
echo [1] Cleaning bin/obj ...
if exist "bin" ( rd /s /q "bin" & echo   bin removed )
if exist "obj" ( rd /s /q "obj" & echo   obj removed )
echo   Cleanup done.

echo.
echo   Restarting MSBuild/VBCSCompiler build server (stale node-reuse cache) ...
dotnet build-server shutdown >nul 2>&1
set MSBUILDDISABLENODEREUSE=1

echo.
echo [2/2] dotnet build K2.UnityLink (Release) ...
echo.>> "%LOG%"
echo === K2.UnityLink.csproj  Release ===>> "%LOG%"
dotnet build ".\K2.UnityLink.csproj" -c Release -nodeReuse:false >> "%LOG%" 2>&1

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
