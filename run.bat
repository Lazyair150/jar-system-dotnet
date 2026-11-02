@echo off
cd /d "%~dp0"
rem ---------------------------------------------------------------------------
rem Launch the kernel host and open the WORLD VIEW (http://localhost:5289/world.html).
rem Port comes from src\JarSystem.Web\Properties\launchSettings.json (http profile).
rem ASCII only: a .bat with UTF-8 Chinese breaks under the GBK code page.
rem ---------------------------------------------------------------------------
echo Stopping old JarSystem.Web process...
rem Only kill this app's process. Do NOT use "taskkill /f /im dotnet.exe"
rem (that would kill every .NET process on the machine).
taskkill /f /im JarSystem.Web.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo.
echo Starting server:  http://localhost:5289
echo World view (new): http://localhost:5289/world.html
echo Legacy page:     http://localhost:5289/
echo Close this window to stop.
echo.
start "" cmd /c "timeout /t 6 /nobreak >nul & start "" http://localhost:5289/world.html"
dotnet run --project src\JarSystem.Web --launch-profile http
echo.
echo Server stopped.
pause
