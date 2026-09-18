@echo off
REM Wrapper que auto-eleva via UAC y corre embed-enable.ps1
setlocal
set "PS=%~dp0embed-enable.ps1"
set "APP=%~dp0.."

REM Si ya somos admin, corremos directo. Si no, relanzamos elevado.
net session >nul 2>&1
if %errorlevel% NEQ 0 (
    powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%PS%','-InstallDir','%APP%'"
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -NoExit -File "%PS%" -InstallDir "%APP%"
endlocal
