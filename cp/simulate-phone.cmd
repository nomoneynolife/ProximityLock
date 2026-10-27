@echo off
REM Simulate phone near/away for ProximityLock Credential Provider
REM No admin needed IF install-cp.cmd already granted Users write on the data dir.
REM Usage: simulate-phone.cmd -KeepNear   /   -Near   /   -Away   /   -Status
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0simulate-phone.ps1" %*

if "%~1"=="" (
  echo.
  pause
)
