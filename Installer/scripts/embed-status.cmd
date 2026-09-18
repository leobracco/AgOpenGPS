@echo off
REM Wrapper para embed-status.ps1. No requiere admin estricto, pero algunas
REM keys del registry devuelven info parcial sin elevacion - asi que igual elevamos.
setlocal
set "PS=%~dp0embed-status.ps1"

net session >nul 2>&1
if %errorlevel% NEQ 0 (
    powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%PS%'"
    exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -NoExit -File "%PS%"
endlocal
