@echo off
echo =======================================================
echo Deploying InternalChat to Public (chat.benda.io.vn)
echo =======================================================

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File deploy\scripts\Start-Public.ps1

echo.
echo Deployment script finished.
pause
