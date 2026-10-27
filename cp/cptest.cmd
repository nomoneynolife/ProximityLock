@echo off
REM Run the CP self-test: loads the DLL directly, no COM registration, no LogonUI involved.
REM This is the safe way to verify the CP logic before installing it into the logon screen.
setlocal
cd /d "%~dp0"
chcp 65001 >nul

if not exist "%~dp0ProximityCredentialProvider.dll" (
  echo [ERROR] DLL not found. Run build.bat first.
  pause
  exit /b 1
)

cptest.exe "%~dp0ProximityCredentialProvider.dll" %1
echo.
pause
