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
rem       + JS syntax gate         (node --check on wwwroot/js/*.js; node is NOT
rem                                 required -- absent means JS-SKIPPED and stays
rem                                 green, a real syntax error prints JS-SYNTAX-ERROR
rem                                 and fails the gate)
rem    8) solver differential       (--check-solver: production solver vs an
rem                                 independent reference solver; must be SOLVER-OK)
rem    9) session replay           (--check-replay: command log -> replay into a
rem                                 fresh system; GetState() + log must match)
rem       purpose: command & message names ARE the contract but live as string literals --
rem       a single typo passes build, tests and snapshot, yet silently breaks the client
rem  OUTPUT
rem    full log written to : _verify_output.txt   (this folder)
rem    contract snapshot   : _contract\state_keys.txt
rem    config snapshot     : _contract\state_keys_config.txt
rem    config vs builtin   : _contract\diff_config.txt  (CONFIG-WORLD-MATCH / MISMATCH)
rem    snapshot vs baseline: _contract\diff.txt         (SNAPSHOT-MISMATCH / NO-BASELINE)
rem    nonzero step exit   : "STEP-FAILED stepN" in the log  (a real gate: every one
rem                           of the nine steps is now checked, not just its text)
rem    js syntax gate      : JS-SKIPPED / JS-OK / JS-SYNTAX-ERROR  (same log)
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

echo [1/9] dotnet build JarSystem.slnx  (full rebuild: --no-incremental)
echo [1/9] dotnet build JarSystem.slnx >> "%LOG%"
dotnet build JarSystem.slnx --no-incremental > "%REP%_build_output.txt" 2>&1
if errorlevel 1 echo STEP-FAILED step1 >> "%LOG%"
echo     exit=%ERRORLEVEL%
type "%REP%_build_output.txt" >> "%LOG%"
type "%REP%_build_output.txt"

echo [2/9] JarSystem.Test   (expect: 0 bugs / 2000 + 0 bugs / 3000 + 32 deterministic)
echo [2/9] JarSystem.Test >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step2 >> "%LOG%"
echo     exit=%ERRORLEVEL%

echo [3/9] StrategyExperiment
echo [3/9] StrategyExperiment >> "%LOG%"
dotnet run --project test\StrategyExperiment -c Release >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step3 >> "%LOG%"
echo     exit=%ERRORLEVEL%

echo [4/9] contract snapshot
echo [4/9] contract snapshot >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step4 >> "%LOG%"
echo     exit=%ERRORLEVEL%

echo [5/9] contract schema   (expect: 11 types, 0 FAILED, total loose=1, types dts: OK)
echo [5/9] contract schema >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-schema >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step5 >> "%LOG%"
echo     exit=%ERRORLEVEL%

rem  NOTE: do NOT write "->" inside an echo line.  cmd parses the ">" of "->"
rem  as a redirection, so "echo a -> contract" silently creates a file named
rem  "contract" in the repo root on EVERY run (real accident, fixed 2026-09).
echo [6/9] world config to contract   (expect: CONFIG-WORLD-MATCH)
echo [6/9] world config to contract >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --dump-contract --config config\world.json >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step6 >> "%LOG%"
echo     exit=%ERRORLEVEL%
fc /n "%CONTRACT%\state_keys.txt" "%CONTRACT%\state_keys_config.txt" > "%CONTRACT%\diff_config.txt" 2>&1
rem  HARD RULE: these two echoes MUST redirect to the log.  Without ">> %LOG%"
rem  the marker never reaches the log, so the VERDICT findstr below can never
rem  fire -- the gate stayed green even when the assembled world differed from
rem  the built-in world.  (Real defect found 2026-09-16 by the 15-expert review.)
if errorlevel 1 echo CONFIG-WORLD-MISMATCH >> "%LOG%"
if not errorlevel 1 echo CONFIG-WORLD-MATCH >> "%LOG%"

echo.
echo [7/9] protocol guard   (expect: PROTOCOL-OK)
echo [7/9] protocol guard >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --check-protocol >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step7 >> "%LOG%"
echo     exit=%ERRORLEVEL%

rem  JS syntax gate (7b).  The nine steps otherwise never look at JavaScript at all
rem  (found by the 2026-09-16 review: the whole front-end had zero gates).  node is
rem  NOT required -- this repo must verify offline -- so a missing node prints
rem  JS-SKIPPED and stays green; a real syntax error prints JS-SYNTAX-ERROR and the
rem  VERDICT below turns red.  This is a *syntax* gate only: it cannot see a wrong
rem  message name (that is what the type products + a real tsc run would catch).
rem  The two files are named explicitly on purpose (keeps this file LF-safe, no
rem  for-block); add any new front-end .js file here when one is created.
set "JSOK=1"
set "JSERR=0"
where node >nul 2>&1
if errorlevel 1 set "JSOK=0"
if "%JSOK%"=="0" echo JS-SKIPPED no-node >> "%LOG%"
if "%JSOK%"=="1" node --check "src\JarSystem.Web\wwwroot\js\app.js" >> "%LOG%" 2>&1
if "%JSOK%"=="1" if errorlevel 1 echo JS-SYNTAX-ERROR app.js >> "%LOG%"
if "%JSOK%"=="1" if errorlevel 1 set "JSERR=1"
if "%JSOK%"=="1" node --check "src\JarSystem.Web\wwwroot\js\canvas.js" >> "%LOG%" 2>&1
if "%JSOK%"=="1" if errorlevel 1 echo JS-SYNTAX-ERROR canvas.js >> "%LOG%"
if "%JSOK%"=="1" if errorlevel 1 set "JSERR=1"
if "%JSOK%"=="1" if "%JSERR%"=="0" echo JS-OK >> "%LOG%"

rem  P5 sub-check: scenario data gate (kept INSIDE step 7, so the nine-step shape is unchanged).
rem  It validates the shipped sample with the SAME validator the editor backend uses.
dotnet run --project test\JarSystem.Test -c Release -- --validate-scenario config\scenario.sample.json >> "%LOG%" 2>&1
if errorlevel 1 echo SCENARIO-GATE-FAILED >> "%LOG%"

echo [8/9] solver differential   (expect: SOLVER-OK)
echo [8/9] solver differential >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --check-solver >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step8 >> "%LOG%"
echo     exit=%ERRORLEVEL%

echo.
echo [9/9] session replay   (expect: REPLAY-OK)
echo [9/9] session replay >> "%LOG%"
dotnet run --project test\JarSystem.Test -c Release -- --check-replay >> "%LOG%" 2>&1
if errorlevel 1 echo STEP-FAILED step9 >> "%LOG%"
echo     exit=%ERRORLEVEL%
rem  Contract snapshot drift is now a REAL gate (2026-09-16 fix).
rem  Two defects were fixed here: (a) a missing baseline was silently created
rem  from the current output, so whatever drifted "healed" itself into a new
rem  green baseline; (b) the fc result only went to diff.txt and no VERDICT
rem  check read it, so drift never turned the gate red.
if not exist "%CONTRACT%\state_keys.baseline.txt" echo SNAPSHOT-NO-BASELINE >> "%LOG%"
fc /n "%CONTRACT%\state_keys.baseline.txt" "%CONTRACT%\state_keys.txt" > "%CONTRACT%\diff.txt" 2>&1
if errorlevel 1 echo SNAPSHOT-MISMATCH >> "%LOG%"

echo.
echo ---------------- KEY LINES ----------------
findstr /i /c:"error CS" /c:"warning CS" /c:" error " /c:"FAIL " /c:"bugs /" /c:"exit=" /c:"schema " /c:"loose=" /c:"CONFIG-WORLD" /c:"world " /c:"contract dumped" /c:"PROTOCOL-" /c:"protocol " /c:"script ascii" /c:"types dumped" /c:"types dts" /c:"SOLVER-" /c:"MODDIFF-" /c:"REPLAY-" /c:"SNAPSHOT-" /c:"STEP-FAILED" /c:"JS-" /c:"DOC-" /c:"SCENARIO-" "%LOG%"
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
findstr /c:"SOLVER-MISMATCH" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"MODDIFF-MISMATCH" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"REPLAY-MISMATCH" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"SNAPSHOT-MISMATCH" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"SNAPSHOT-NO-BASELINE" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"STEP-FAILED" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"JS-SYNTAX-ERROR" "%LOG%" >nul && set "VERDICT=FAIL"
findstr /c:"SCENARIO-GATE-FAILED" "%LOG%" >nul && set "VERDICT=FAIL"
echo VERDICT=%VERDICT%
echo VERDICT=%VERDICT% >> "%LOG%"
echo.
echo Paste the KEY LINES above back to the assistant.
if not defined CI pause
if "%VERDICT%"=="FAIL" exit /b 1
exit /b 0
