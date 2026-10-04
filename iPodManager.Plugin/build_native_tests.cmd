@echo off
setlocal
cd /d "%~dp0"
call "%~dp0setup_msvc.cmd"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /W4 /WX /DDIRECTINPUT_VERSION=0x0800 /Fo:tests\native_manifest_tests.obj tests\native_manifest_tests.cpp /Fe:tests\native_manifest_tests.exe
if errorlevel 1 exit /b %errorlevel%
tests\native_manifest_tests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /W4 /WX /DDIRECTINPUT_VERSION=0x0800 /Fo:tests\native_patch_tests.obj tests\native_patch_tests.cpp /Fe:tests\native_patch_tests.exe
if errorlevel 1 exit /b %errorlevel%
tests\native_patch_tests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /W4 /WX /Fo:tests\programmer_resume_tests.obj tests\programmer_resume_tests.cpp /Fe:tests\programmer_resume_tests.exe
if errorlevel 1 exit /b %errorlevel%
tests\programmer_resume_tests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /W4 /WX /Fo:tests\background_playback_tests.obj tests\background_playback_tests.cpp /Fe:tests\background_playback_tests.exe
if errorlevel 1 exit /b %errorlevel%
tests\background_playback_tests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /W4 /WX /Fo:tests\native_logging_tests.obj tests\native_logging_tests.cpp bcrypt.lib /Fe:tests\native_logging_tests.exe
if errorlevel 1 exit /b %errorlevel%
tests\native_logging_tests.exe
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /EHsc /W4 /WX /Fo:tests\native_activation_tests.obj tests\native_activation_tests.cpp bcrypt.lib /Fe:tests\native_activation_tests.exe
if errorlevel 1 exit /b %errorlevel%
tests\native_activation_tests.exe
exit /b %errorlevel%
