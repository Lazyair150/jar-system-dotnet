@echo off
setlocal
rem ============================================================
rem  Self test: confirms that .bat files can run on this machine.
rem  ASCII only, no blocks, no code-page switching.
rem  If you can read the text below, batch execution works.
rem ============================================================

echo.
echo [OK] batch files DO run on this machine.
echo.
echo this folder : %~dp0
echo current dir : %CD%
echo.
echo contents of this folder:
dir /b "%~dp0"
echo.
echo next step: run  verify.bat  (in this folder)
pause
