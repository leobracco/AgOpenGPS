@echo off
REM Wrapper que auto-eleva via UAC y corre embed-disable.ps1
setlocal
set "PS=%~dp0embed-disable.ps1"

net session >nul 2>&1
if %errorlevel% NEQ 0 (
    powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%PS%'"
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -NoExit -File "%PS%"
endlocal
