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
rem    6) world config -> contract  (--dump-contract --config config\world.json)
rem       purpose: prove "assembled from config world" == "built-in world" byte-for-byte
rem  OUTPUT
rem    full log written to : _verify_output.txt   (this folder)
rem    contract snapshot   : _contract\state_keys.txt
rem    config snapshot     : _contract\state_keys_config.txt
rem    config vs builtin   : _contract\diff_config.txt  (CONFIG-WORLD-MATCH / MISMATCH)
rem    contract schema     : _contract\state.schema.json
rem
rem  EXIT CODE
rem    0 = all checks passed, 1 = something failed (VERDICT=FAIL)
rem    set CI=1 to skip the final "press any key" (so it can run as a CI gate)
rem    key lines are printed on screen at the end
rem ============================================================

set "REP=%~dp0"
set "LOG=%REP%_verify_output.txt"
set "CONTRACT=%REP%_contract"
cd /d "%REP%.."

echo ==== %DATE% %TIME% ==== > "%LOG%"
echo repo root: %CD% >> "%LOG%"
echo. >> "%LOG%"

echo [1/6] dotnet build JarSystem.slnx
echo [1/6] dotnet build JarSystem.slnx >> "%LOG%"
dotnet build JarSystem.slnx > "%REP%_build_output.txt" 2>&1
echo     exit=%ERRORLEVEL%
type "%REP%_build_output.txt" >> "%LOG%"
type "%REP%_build_output.txt"

echo [2/6] JarSystem.Test   (expect: 0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic)
echo [2/6] JarSystem.Test >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [3/6] StrategyExperiment
echo [3/6] StrategyExperiment >> "%LOG%"
dotnet run --project test\StrategyExperiment -c Release >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [4/6] contract snapshot
echo [4/6] contract snapshot >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [5/6] contract schema   (expect: StateDto + 9 part types, 0 FAILED, total loose=3)
echo [5/6] contract schema >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-schema >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [6/6] world config -> contract   (expect: CONFIG-WORLD-MATCH)
echo [6/6] world config -> contract >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract --config config\world.json >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%
fc /n "%CONTRACT%\state_keys.txt" "%CONTRACT%\state_keys_config.txt" > "%CONTRACT%\diff_config.txt" 2>&1
if errorlevel 1 echo CONFIG-WORLD-MISMATCH
if not errorlevel 1 echo CONFIG-WORLD-MATCH

if not exist "%CONTRACT%\state_keys.baseline.txt" copy /Y "%CONTRACT%\state_keys.txt" "%CONTRACT%\state_keys.baseline.txt" >nul 2>&1
if exist "%CONTRACT%\state_keys.txt" fc /n "%CONTRACT%\state_keys.baseline.txt" "%CONTRACT%\state_keys.txt" > "%CONTRACT%\diff.txt" 2>&1

echo.
echo ---------------- KEY LINES ----------------
findstr /i /c:"error CS" /c:"warning CS" /c:" error " /c:"FAIL " /c:"bugs /" /c:"exit=" /c:"schema " /c:"loose=" /c:"CONFIG-WORLD" /c:"world " /c:"contract dumped" "%LOG%"
echo -------------------------------------------
echo full log : %LOG%
echo contract : %CONTRACT%\state_keys.txt
echo schema   : %CONTRACT%\state.schema.json
echo.

rem ---- machine-checkable verdict (so this script can be used as a CI gate) ----
set "VERDICT=OK"
findstr /c:"error CS" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"FAIL " "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"CONFIG-WORLD-MISMATCH" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"schema dump FAILED" "%LOG%" >nul && set "VERDICT=FAIL"
echo VERDICT=%VERDICT%
echo VERDICT=%VERDICT% >> "%LOG%"
echo.
echo Paste the KEY LINES above back to the assistant.
if not defined CI pause
if "%VERDICT%"=="FAIL" exit /b 1
exit /b 0
