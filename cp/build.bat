@echo off
REM Build ProximityLock Credential Provider (x64)
REM Produces: ProximityCredentialProvider.dll  +  cptest.exe (self-test tool)
setlocal
set "VCVARS=C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
  echo [ERROR] vcvars64.bat not found. Edit VCVARS path in this script.
  exit /b 1
)
call "%VCVARS%" >nul
cd /d "%~dp0"

echo [1/2] building ProximityCredentialProvider.dll ...
cl /nologo /EHsc /std:c++17 /W3 /O2 /MT /DUNICODE /D_UNICODE /utf-8 /LD ProximityCredentialProvider.cpp -link -DEF:ProximityCredentialProvider.def -OUT:ProximityCredentialProvider.dll
if errorlevel 1 goto fail

echo.
echo [2/2] building cptest.exe ...
cl /nologo /EHsc /std:c++17 /W3 /O2 /MT /DUNICODE /D_UNICODE /utf-8 cptest.cpp -link -OUT:cptest.exe ole32.lib advapi32.lib user32.lib
if errorlevel 1 goto fail

del /q *.obj *.exp *.lib 2>nul
echo.
echo [OK] ProximityCredentialProvider.dll + cptest.exe
echo.
echo Next: run cptest.cmd to verify the CP logic BEFORE installing it.
exit /b 0

:fail
echo.
echo [FAILED]
exit /b 1
