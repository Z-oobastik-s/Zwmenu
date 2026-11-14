@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
set "GIT_TERMINAL_PROMPT=0"

set "GIT=%~dp0.tools\mingit\cmd\git.exe"
if exist "%GIT%" goto gotgit

where git >nul 2>&1
if not errorlevel 1 (
  set "GIT=git"
  goto gotgit
)

echo Git ne nayden. Stavlju lokalnuyu kopiyu, odin raz.
powershell -NoProfile -ExecutionPolicy Bypass -Command "New-Item -ItemType Directory -Force -Path '%~dp0.tools\mingit' | Out-Null; Invoke-WebRequest -Uri 'https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.2/MinGit-2.47.1.2-64-bit.zip' -OutFile $env:TEMP\mingit-zwmenu.zip; Expand-Archive -Path $env:TEMP\mingit-zwmenu.zip -DestinationPath '%~dp0.tools\mingit' -Force; Remove-Item $env:TEMP\mingit-zwmenu.zip -Force"
set "GIT=%~dp0.tools\mingit\cmd\git.exe"
if not exist "%GIT%" (
  echo Git ne ustanovilsya.
  exit /b 1
)

:gotgit
if not exist "%~dp0.github-token" (
  echo Ryadom so skriptom net fayla .github-token
  exit /b 1
)

set /p GHTOKEN=<"%~dp0.github-token"
if "%GHTOKEN%"=="" (
  echo Fajl .github-token pustoy.
  exit /b 1
)

if not exist "%~dp0.git\" "%GIT%" init
"%GIT%" checkout -B main
"%GIT%" remote get-url origin >nul 2>&1
if errorlevel 1 (
  "%GIT%" remote add origin https://github.com/Z-oobastik-s/Zwmenu.git
) else (
  "%GIT%" remote set-url origin https://github.com/Z-oobastik-s/Zwmenu.git
)

"%GIT%" add -A
"%GIT%" diff --cached --name-only | findstr /i /c:".github-token" /c:".tools/" /c:"publish/" /c:"dist/" /c:"Action-Wheel" /c:"tools/"
if not errorlevel 1 (
  echo V indeks popalo lishnee. Push ostanovlen.
  "%GIT%" reset -q
  set "GHTOKEN="
  exit /b 1
)
"%GIT%" grep -I -n "ghp_" --cached >nul 2>&1
if not errorlevel 1 (
  echo V fajlah nashelsya token. Push ostanovlen.
  "%GIT%" reset -q
  set "GHTOKEN="
  exit /b 1
)

"%GIT%" diff --cached --quiet
if errorlevel 1 (
  "%GIT%" -c user.name=Z-oobastik-s -c user.email=Z-oobastik-s@users.noreply.github.com commit -m "Update Zwmenu"
  if errorlevel 1 (
    set "GHTOKEN="
    exit /b 1
  )
)

"%GIT%" -c credential.helper= -c http.extraheader="AUTHORIZATION: bearer %GHTOKEN%" push -u origin main
set "ERR=%ERRORLEVEL%"
set "GHTOKEN="
exit /b %ERR%