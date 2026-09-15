@echo off
setlocal
rem ============================================================
rem  JarSystem verify  (ASCII-only script: no code-page switch,
rem  no parenthesized blocks -> safe with LF line endings)
rem
rem  HOW TO RUN
rem    double-click this file, OR drag it into an open cmd window and press Enter
rem    (running from an open cmd window shows any error text)
rem
rem  WHAT IT DOES
rem    1) dotnet build JarSystem.slnx
rem    2) test/JarSystem.Test       -c Release
rem    3) test/StrategyExperiment   -c Release
rem    4) contract snapshot         (--dump-contract)
rem    5) contract schema           (--dump-schema, JsonSchemaExporter)
rem  OUTPUT
rem    full log written to : _verify_output.txt   (this folder)
rem    contract snapshot   : _contract\state_keys.txt
rem    contract schema     : _contract\state.schema.json
rem    key lines are printed on screen at the end
rem ============================================================

set "REP=%~dp0"
set "LOG=%REP%_verify_output.txt"
set "CONTRACT=%REP%_contract"
cd /d "%REP%.."

echo ==== %DATE% %TIME% ==== > "%LOG%"
echo repo root: %CD% >> "%LOG%"
echo. >> "%LOG%"

echo [1/5] dotnet build JarSystem.slnx
echo [1/5] dotnet build JarSystem.slnx >> "%LOG%"
dotnet build JarSystem.slnx > "%REP%_build_output.txt" 2>&1
echo     exit=%ERRORLEVEL%
type "%REP%_build_output.txt" >> "%LOG%"
type "%REP%_build_output.txt"

echo [2/5] JarSystem.Test   (expect: 0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic)
echo [2/5] JarSystem.Test >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [3/5] StrategyExperiment
echo [3/5] StrategyExperiment >> "%LOG%"
dotnet run --project test\StrategyExperiment -c Release >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [4/5] contract snapshot
echo [4/5] contract snapshot >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [5/5] contract schema   (expect: StateDto + 7 part types, 0 FAILED)
echo [5/5] contract schema >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-schema >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

if not exist "%CONTRACT%\state_keys.baseline.txt" copy /Y "%CONTRACT%\state_keys.txt" "%CONTRACT%\state_keys.baseline.txt" >nul 2>&1
if exist "%CONTRACT%\state_keys.txt" fc /n "%CONTRACT%\state_keys.baseline.txt" "%CONTRACT%\state_keys.txt" > "%CONTRACT%\diff.txt" 2>&1

echo.
echo ---------------- KEY LINES ----------------
findstr /i /c:"error CS" /c:"warning CS" /c:" error " /c:"FAIL " /c:"bugs /" /c:"exit=" /c:"schema " /c:"loose=" "%LOG%"
echo -------------------------------------------
echo full log : %LOG%
echo contract : %CONTRACT%\state_keys.txt
echo schema   : %CONTRACT%\state.schema.json
echo.
echo Paste the KEY LINES above back to the assistant.
pause
