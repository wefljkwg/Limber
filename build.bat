@echo off
setlocal
rem Builds both Limber plugins.
rem  - FFXIV:    Limber\bin\Release\Limber.dll  (your Dev Plugin Location points here)
rem  - Koikatsu: built and copied into <Koikatsu>\BepInEx\plugins\Limber
rem Close FFXIV and Koikatsu first.
rem Your Koikatsu folder is asked once and remembered in build.local.txt (kept out of git).

cd /d "%~dp0"

set "KKDIR="
if exist build.local.txt set /p KKDIR=<build.local.txt
if defined KKDIR goto havedir

echo.
echo Where is Koikatsu? Paste the folder that has Koikatu.exe / CharaStudio.exe in it.
echo (Leave empty to build the Koikatsu plugin without installing it.)
set /p "KKDIR=Koikatsu folder: "
if not defined KKDIR goto build
set KKDIR=%KKDIR:"=%
if "%KKDIR:~-1%"=="\" set "KKDIR=%KKDIR:~0,-1%"
>build.local.txt echo %KKDIR%

:havedir
if not exist "%KKDIR%\BepInEx" (
    echo.
    echo [!] No BepInEx folder found in the Koikatsu folder from build.local.txt.
    echo     Delete build.local.txt and run build.bat again to pick the folder again.
    goto fail
)

:build
echo.
echo ===== Building the FFXIV plugin =====
dotnet build Limber\Limber.csproj -c Release
if errorlevel 1 goto fail

echo.
echo ===== Building the Koikatsu plugin =====
if defined KKDIR (
    dotnet build Limber.Koikatsu\Limber.Koikatsu.csproj -c Release "-p:KoikatsuDir=%KKDIR%" -p:InstallToGame=true
) else (
    dotnet build Limber.Koikatsu\Limber.Koikatsu.csproj -c Release
)
if errorlevel 1 goto fail

echo.
echo ===== Done =====
echo FFXIV:    %~dp0Limber\bin\Release\Limber.dll
if defined KKDIR (echo Koikatsu: installed to BepInEx\plugins\Limber) else (echo Koikatsu: %~dp0Limber.Koikatsu\bin\KK\Limber.Koikatsu.dll)
echo.
pause
exit /b 0

:fail
echo.
echo ===== Build failed - see the messages above =====
echo.
pause
exit /b 1
