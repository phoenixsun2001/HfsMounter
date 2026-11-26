@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-service.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" (echo Failed with exit code %EXITCODE%.) else (echo Done.)
pause
exit /b %EXITCODE%