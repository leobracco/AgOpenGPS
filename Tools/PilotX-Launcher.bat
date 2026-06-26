@echo off
title PilotX - Agro Parallel
setlocal

set PILOTX_DIR=C:\PilotX
set PILOTX_EXE=%PILOTX_DIR%\PilotX.exe
set LOGDIR=%PILOTX_DIR%\logs
set LOGFILE=%LOGDIR%\launcher.log

if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo [%date% %time%] === PilotX launcher start === >> "%LOGFILE%"

if not exist "%PILOTX_EXE%" (
    echo [%date% %time%] ERROR: %PILOTX_EXE% no existe >> "%LOGFILE%"
    echo ERROR: %PILOTX_EXE% no existe.
    timeout /t 10
    exit /b 1
)

:: Si ya hay una instancia corriendo, no abrir otra.
tasklist /FI "IMAGENAME eq PilotX.exe" 2>nul | find /I "PilotX.exe" >nul
if not errorlevel 1 (
    echo [%date% %time%] AOG ya estaba corriendo, no se relanza >> "%LOGFILE%"
    exit /b 0
)

cd /d "%PILOTX_DIR%"
start "" "%PILOTX_EXE%"
echo [%date% %time%] PilotX.exe lanzado >> "%LOGFILE%"

exit /b 0
