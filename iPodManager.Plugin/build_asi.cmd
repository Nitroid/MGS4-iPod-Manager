@echo off
setlocal
cd /d "%~dp0"
call "%~dp0setup_msvc.cmd"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /LD /W4 /WX /Fo:mgs4_ipod_asi.obj mgs4_ipod_asi.cpp bcrypt.lib /link /Brepro /OUT:iPodManager.asi
exit /b %errorlevel%
