@echo off
setlocal
rem Builds Proppy (its own plugin, separate from Limber).
rem  - Proppy\bin\Release\Proppy.dll  (add this folder to Dalamud's Dev Plugin Locations)
rem Close FFXIV first (or unload Proppy in /xlplugins).

cd /d "%~dp0"

echo.
echo ===== Building Proppy =====
dotnet build Proppy\Proppy.csproj -c Release
if errorlevel 1 goto fail

echo.
echo ===== Done =====
echo Proppy: %~dp0Proppy\bin\Release\Proppy.dll
echo (Add %~dp0Proppy\bin\Release to /xlsettings ^> Experimental ^> Dev Plugin Locations, once.)
echo.
pause
exit /b 0

:fail
echo.
echo ===== Build failed - see the messages above =====
echo.
pause
exit /b 1
