@echo off
setlocal
where cl >nul 2>nul
if errorlevel 1 (
  echo Run from an x64 Native Tools Command Prompt for Visual Studio with the Windows SDK installed. 1>&2
  exit /b 2
)
if not exist "%~dp0..\..\artifacts\taskbar-probe" mkdir "%~dp0..\..\artifacts\taskbar-probe"
pushd "%~dp0..\..\artifacts\taskbar-probe"
cl /nologo /std:c++20 /EHsc /W4 /WX /DUNICODE /D_UNICODE "%~dp0probe.cpp" /Fe:TaskbarProbe.exe
set "probe_result=%errorlevel%"
popd
exit /b %probe_result%
