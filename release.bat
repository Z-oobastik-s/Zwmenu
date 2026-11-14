@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1"
set "ERR=%ERRORLEVEL%"
if /I not "%~1"=="nopause" pause
exit /b %ERR%
