@echo off
setlocal
rem One-time setup of GVHMR (WSL, Ubuntu) for Limber's Video tab. See README.md in this folder first.
where wsl >nul 2>nul || (echo WSL isn't installed. In an admin terminal run:  wsl --install -d Ubuntu  then restart and run this again. & pause & exit /b 1)

set "BODY=%~1"
if "%BODY%"=="" set /p BODY=Folder with your SMPL and SMPL-X downloads (zips are fine): 
if not defined BODY (echo No folder given. & pause & exit /b 1)
rem Drag-and-drop adds quotes; a trailing backslash would escape the closing quote passed to wsl.exe.
set "BODY=%BODY:"=%"
if "%BODY:~-1%"=="\" set "BODY=%BODY:~0,-1%"
if not exist "%BODY%" (echo That folder doesn't exist. & pause & exit /b 1)

set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"

wsl.exe --exec bash -c "tr -d '\r' < \"$(wslpath -u \"$1\")/setup_gvhmr.sh\" > /tmp/limber_setup_gvhmr.sh && bash /tmp/limber_setup_gvhmr.sh \"$(wslpath -u \"$1\")\" \"$(wslpath -u \"$2\")\"" limber "%HERE%" "%BODY%"
echo.
pause
