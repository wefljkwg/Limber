@echo off
setlocal
rem One-time setup of NLF (WSL, Ubuntu) for Limber's VLC Live page. See README.md in this folder first.
where wsl >nul 2>nul || (echo WSL isn't installed. In an admin terminal run:  wsl --install -d Ubuntu  then restart and run this again. & pause & exit /b 1)

set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"

wsl.exe --exec bash -c "tr -d '\r' < \"$(wslpath -u \"$1\")/setup_nlf.sh\" > /tmp/limber_setup_nlf.sh && bash /tmp/limber_setup_nlf.sh \"$(wslpath -u \"$1\")\"" limber "%HERE%"
echo.
pause
