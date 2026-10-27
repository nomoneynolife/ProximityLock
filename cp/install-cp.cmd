@echo off
REM Install ProximityLock Credential Provider (auto-elevate + bypass execution policy)
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process -FilePath powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0install-cp.ps1')"

if errorlevel 1 (
  echo.
  echo [ERROR] Failed to start elevated PowerShell. Right-click this .cmd and choose "Run as administrator".
  pause
)
