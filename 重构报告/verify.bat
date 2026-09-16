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
rem       + front-end types         (contract.d.ts) with a machine-checkable
rem                                 quality metric: DTS-DEGRADED must not appear
rem                                 (it means a describable node collapsed to
rem                                 unknown/Record, e.g. an unresolvable $ref)
rem    6) world config as contract  (--dump-contract --config config\world.json)
rem       purpose: prove "assembled from config world" == "built-in world" byte-for-byte
rem    7) protocol guard           (--check-protocol: command/message name inventory)
rem       purpose: command & message names ARE the contract but live as string literals --
rem       a single typo passes build, tests and snapshot, yet silently breaks the client
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
rem
rem  HARD RULE (do not break this again)
rem    this script MUST stay pure ASCII. cmd parses .bat under the GBK code page,
rem    so non-ASCII bytes here make it fail to run at all (double-click: window
rem    flashes and closes = "cannot open"). Chinese text belongs in the reports,
rem    never in this file. Step 7 enforces it: it prints SCRIPT-NON-ASCII if violated.
rem ============================================================

set "REP=%~dp0"
set "LOG=%REP%_verify_output.txt"
set "CONTRACT=%REP%_contract"
cd /d "%REP%.."

echo ==== %DATE% %TIME% ==== > "%LOG%"
echo repo root: %CD% >> "%LOG%"
echo. >> "%LOG%"

echo [1/7] dotnet build JarSystem.slnx
echo [1/7] dotnet build JarSystem.slnx >> "%LOG%"
dotnet build JarSystem.slnx > "%REP%_build_output.txt" 2>&1
echo     exit=%ERRORLEVEL%
type "%REP%_build_output.txt" >> "%LOG%"
type "%REP%_build_output.txt"

echo [2/7] JarSystem.Test   (expect: 0 bugs / 2000 + 0 bugs / 3000 + 21 deterministic)
echo [2/7] JarSystem.Test >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [3/7] StrategyExperiment
echo [3/7] StrategyExperiment >> "%LOG%"
dotnet run --project test\StrategyExperiment -c Release >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [4/7] contract snapshot
echo [4/7] contract snapshot >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

echo [5/7] contract schema   (expect: StateDto + 9 part types, 0 FAILED, total loose=3, types dts: OK)
echo [5/7] contract schema >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-schema >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

rem  NOTE: do NOT write "->" inside an echo line.  cmd parses the ">" of "->"
rem  as a redirection, so "echo a -> contract" silently creates a file named
rem  "contract" in the repo root on EVERY run (real accident, fixed 2026-09).
echo [6/7] world config to contract   (expect: CONFIG-WORLD-MATCH)
echo [6/7] world config to contract >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract --config config\world.json >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%
fc /n "%CONTRACT%\state_keys.txt" "%CONTRACT%\state_keys_config.txt" > "%CONTRACT%\diff_config.txt" 2>&1
if errorlevel 1 echo CONFIG-WORLD-MISMATCH
if not errorlevel 1 echo CONFIG-WORLD-MATCH

echo [7/7] protocol guard   (expect: PROTOCOL-OK)
echo [7/7] protocol guard >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --check-protocol >> "%LOG%" 2>&1
echo     exit=%ERRORLEVEL%

if not exist "%CONTRACT%\state_keys.baseline.txt" copy /Y "%CONTRACT%\state_keys.txt" "%CONTRACT%\state_keys.baseline.txt" >nul 2>&1
if exist "%CONTRACT%\state_keys.txt" fc /n "%CONTRACT%\state_keys.baseline.txt" "%CONTRACT%\state_keys.txt" > "%CONTRACT%\diff.txt" 2>&1

echo.
echo ---------------- KEY LINES ----------------
findstr /i /c:"error CS" /c:"warning CS" /c:" error " /c:"FAIL " /c:"bugs /" /c:"exit=" /c:"schema " /c:"loose=" /c:"CONFIG-WORLD" /c:"world " /c:"contract dumped" /c:"PROTOCOL-" /c:"protocol " /c:"script ascii" /c:"types dumped" /c:"types dts" "%LOG%"
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
findstr /c:"PROTOCOL-MISMATCH" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"SOURCE-NOT-FOUND" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"SCRIPT-NON-ASCII" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"DTS-DEGRADED" "%LOG%" >nul && set "VERDICT=FAIL"
echo VERDICT=%VERDICT%
echo VERDICT=%VERDICT% >> "%LOG%"
echo.
echo Paste the KEY LINES above back to the assistant.
if not defined CI pause
if "%VERDICT%"=="FAIL" exit /b 1
exit /b 0
