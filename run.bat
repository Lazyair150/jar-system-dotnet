@echo off
cd /d "%~dp0"
echo Stopping old JarSystem.Web process...
rem Only kill this app's process. Do NOT use "taskkill /f /im dotnet.exe"
rem (that would kill every .NET process on the machine).
taskkill /f /im JarSystem.Web.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo.
echo Starting server on http://localhost:3457
echo Close this window to stop.
echo.
dotnet run --project src\JarSystem.Web --urls http://localhost:3457
echo.
echo Server stopped.
pause
