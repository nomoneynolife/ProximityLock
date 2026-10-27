@echo off
REM Remove ProximityLock Credential Provider (auto-elevate + bypass execution policy)
REM Works in Safe Mode too.
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process -FilePath powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0uninstall-cp.ps1')"

if errorlevel 1 (
  echo.
  echo [ERROR] Failed to start elevated PowerShell. Right-click this .cmd and choose "Run as administrator".
  pause
)
