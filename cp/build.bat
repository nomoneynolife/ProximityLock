@echo off
REM Build ProximityLock Credential Provider (x64)
setlocal
set "VCVARS=C://Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
  echo [ERROR] vcvars64.bat not found, edit VCVARS path in this script
  exit /b 1
)
call "%VCVARS%" >nul
cd /d "%~dp0"
cl /nologo /EHsc /std:c++17 /W3 /O2 /MT /DUNICODE /D_UNICODE /utf-8 /LD ProximityCredentialProvider.cpp -link -DEF:ProximityCredentialProvider.def -OUT:ProximityCredentialProvider.dll
if errorlevel 1 (
  echo [FAILED]
  exit /b 1
)
echo.
echo [OK] ProximityCredentialProvider.dll
