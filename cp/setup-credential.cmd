@echo off
REM Record Windows credentials for auto-unlock (auto-elevate + bypass execution policy)
REM Default target account: current user, local domain "."
REM Usage: setup-credential.cmd [user] [domain]
setlocal
cd /d "%~dp0"

set "TARGET_USER=%~1"
if "%TARGET_USER%"=="" set "TARGET_USER=%USERNAME%"
set "TARGET_DOMAIN=%~2"
if "%TARGET_DOMAIN%"=="" set "TARGET_DOMAIN=."

echo Target account: %TARGET_DOMAIN%\%TARGET_USER%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process -FilePath powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0setup-credential.ps1','-User','%TARGET_USER%','-Domain','%TARGET_DOMAIN%')"

if errorlevel 1 (
  echo.
  echo [ERROR] Failed to start elevated PowerShell. Right-click this .cmd and choose "Run as administrator".
  pause
)
