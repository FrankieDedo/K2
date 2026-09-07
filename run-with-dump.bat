@echo off
REM ============================================================
REM  run-with-dump.bat - launches the locally built K2.App with
REM  the .NET runtime's own crash-dump writer enabled.
REM
REM  Why: K2 dies at startup, intermittently, with exit code
REM  0x80131506 (COR_E_EXECUTIONENGINE) inside coreclr.dll - the
REM  signature of a corrupted GC heap. That kind of failure kills
REM  the process before any managed handler runs, so K2.App.log
REM  just stops mid-sentence and Windows Error Reporting keeps
REM  only a Report.wer with no stack in it.
REM
REM  These four variables make the runtime write a FULL dump
REM  itself, into this folder, at the moment it gives up - the
REM  only artefact that carries the faulting thread's managed
REM  stack. Read it afterwards with:
REM
REM     dotnet tool install -g dotnet-dump
REM     dotnet-dump analyze <the .dmp>
REM     > clrstack -all
REM     > verifyheap
REM
REM  Run K2 from here until it crashes once; a normal run leaves
REM  no dump behind, so it costs nothing to keep using it.
REM ============================================================
setlocal
cd /d "%~dp0"

set "DUMPDIR=%~dp0crashdumps"
if not exist "%DUMPDIR%" mkdir "%DUMPDIR%"

REM 1 = write a dump when the runtime fails fatally.
set DOTNET_DbgEnableMiniDump=1
REM 4 = MiniDumpWithFullMemory: needed for managed stacks and heap verification.
set DOTNET_DbgMiniDumpType=4
set DOTNET_DbgMiniDumpName=%DUMPDIR%\k2_crash_%%p.dmp
REM Keeps the dump readable by dotnet-dump rather than only by a native debugger.
set DOTNET_CreateDumpDiagnostics=1

set "EXE=%~dp0K2.App\bin\x86\Debug\net8.0-windows10.0.19041.0\K2.App.exe"
if not exist "%EXE%" (
    echo.
    echo   Build not found:
    echo     %EXE%
    echo   Run build-check.bat first ^(with K2 closed^).
    echo.
    pause
    exit /b 1
)

echo.
echo   Dumps will be written to: %DUMPDIR%
echo   Launching K2 ...
echo.
"%EXE%"

echo.
echo   K2 exited with code %ERRORLEVEL%.
dir /b "%DUMPDIR%" 2>nul
echo.
pause
