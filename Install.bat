@echo off
setlocal
cd /d "%~dp0"

echo ========================================================
echo               Bearly - Quick Setup
echo ========================================================
echo.

:: Automatically unblock all downloaded files (clears Windows Mark-of-the-Web 0x80131515)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path '%~dp0' -Recurse | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1

:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo [*] Installing Bearly & creating Desktop shortcuts...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"

echo.
echo [*] Launching Bearly...
start "" wscript.exe "%~dp0Bearly.vbs"

echo.
echo [DONE] Setup complete! You can now launch Bearly anytime from your Desktop.
timeout /t 3 >nul
exit /b
