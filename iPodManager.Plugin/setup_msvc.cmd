@echo off
rem Use the caller's selected x64 toolchain, or locate an installed C++ toolchain.
if /i "%VSCMD_ARG_TGT_ARCH%"=="x64" exit /b 0
set "IPOD_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%IPOD_VSWHERE%" (
  echo Run from an x64 Native Tools Command Prompt, or install Visual Studio C++ Build Tools.
  exit /b 1
)
set "IPOD_VSROOT="
for /f "usebackq delims=" %%I in (`"%IPOD_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "IPOD_VSROOT=%%I"
if not defined IPOD_VSROOT exit /b 1
call "%IPOD_VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
exit /b %errorlevel%
