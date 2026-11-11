@echo off
rem Drag a folder of screenshots (taken with Limber's Backdrop, Green screen) onto this file.
rem The keyed pictures go into a "keyed" folder inside it: pure white background, cropped square, 1024 px.
rem Needs Python 3 with numpy and Pillow (pip install numpy pillow).
if "%~1"=="" (
    echo Drag a folder of screenshots onto this file.
    pause
    exit /b 1
)
python "%~dp0backdrop_key.py" "%~1" %2 %3 %4 %5 %6 %7 %8
pause
