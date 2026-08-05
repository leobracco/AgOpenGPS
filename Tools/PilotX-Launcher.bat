@echo off
title PilotX - Agro Parallel (supervisor)
setlocal EnableDelayedExpansion

:: ============================================================================
:: Supervisor de PilotX: lanza la app y la RELANZA si el proceso muere.
::
:: Por que existe: un TDR del driver de video (Event ID 4101, igfx) puede
:: matar el proceso entero con un access violation NATIVO dentro del flush de
:: Skia (visto 2026-08-05 03:09: "Fatal error 0xC0000005 at sk_canvas_flush",
:: tres TDRs esa misma noche). Eso no se puede atrapar desde C#: el unico
:: remedio es que ALGUIEN relance. En cabina ese alguien es este script; sin
:: el, un crash de madrugada deja la pantalla muerta hasta que el operario
:: reinicia a mano -- y el operario no tiene por que saber hacerlo.
::
:: Anti-crashloop: si la app vivio menos de 60 s, la espera antes de relanzar
:: sube de 5 a 30 s. Un problema permanente (disco lleno, config rota) queda
:: en un ciclo lento que no quema CPU ni llena el log, pero sigue intentando:
:: en el tractor no hay nadie mirando y rendirse no es una opcion.
:: ============================================================================

set PILOTX_DIR=C:\PilotX
set LOGDIR=%PILOTX_DIR%\logs
set LOGFILE=%LOGDIR%\launcher.log

:: La pantalla real es el shell Avalonia (PilotX.Desktop.exe). Se prueban los
:: DOS layouts: el nuevo con subcarpetas (C:\PilotX\Desktop\ + Engine\, el del
:: taller y de deploy-taller.ps1) y el plano viejo. PilotX.exe (WinForms)
:: queda de ultimo fallback para instalaciones viejas.
set PILOTX_EXE=%PILOTX_DIR%\Desktop\PilotX.Desktop.exe
if not exist "%PILOTX_EXE%" set PILOTX_EXE=%PILOTX_DIR%\PilotX.Desktop.exe
if not exist "%PILOTX_EXE%" set PILOTX_EXE=%PILOTX_DIR%\PilotX.exe


if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo [%date% %time%] === supervisor start (exe: %PILOTX_EXE%) === >> "%LOGFILE%"

if not exist "%PILOTX_EXE%" (
    echo [%date% %time%] ERROR: %PILOTX_EXE% no existe >> "%LOGFILE%"
    echo ERROR: %PILOTX_EXE% no existe.
    timeout /t 10
    exit /b 1
)

:: Una sola instancia: si la app ya corre, otro supervisor ya la maneja.
for %%F in ("%PILOTX_EXE%") do set EXENAME=%%~nxF
tasklist /FI "IMAGENAME eq %EXENAME%" 2>nul | find /I "%EXENAME%" >nul
if not errorlevel 1 (
    echo [%date% %time%] %EXENAME% ya estaba corriendo, no se supervisa doble >> "%LOGFILE%"
    exit /b 0
)

cd /d "%PILOTX_DIR%"

:lanzar
:: El Engine primero, y DENTRO del bucle: si el motor muere a mitad de
:: jornada, el proximo relanzamiento lo revive tambien. Sin motor la pantalla
:: queda en negro con datos vencidos (medido 2026-08-05: edadFix creciendo,
:: vel=-1) — supervisar solo la pantalla era supervisar la mitad del sistema.
if exist "%PILOTX_DIR%\Engine\PilotX.GuidanceEngine.exe" (
    tasklist /FI "IMAGENAME eq PilotX.GuidanceEngine.exe" 2>nul | find /I "PilotX.GuidanceEngine.exe" >nul
    if errorlevel 1 (
        echo [%date% %time%] Engine no corria: se levanta >> "%LOGFILE%"
        start "PilotX Engine" /min "%PILOTX_DIR%\Engine\PilotX.GuidanceEngine.exe" --webhost --corex
        timeout /t 6 /nobreak >nul
    )
)

:: Epoch en segundos para medir cuanto vivio (parsear %TIME% en batch es
:: fragil con configuraciones regionales; PowerShell no).
for /f %%S in ('powershell -NoProfile -Command "[int][double]::Parse((Get-Date -UFormat %%s))"') do set T0=%%S

echo [%date% %time%] lanzando %EXENAME% >> "%LOGFILE%"
start /wait "" "%PILOTX_EXE%"
set CODIGO=%ERRORLEVEL%

for /f %%S in ('powershell -NoProfile -Command "[int][double]::Parse((Get-Date -UFormat %%s))"') do set T1=%%S
set /a VIVIO=T1-T0

echo [%date% %time%] %EXENAME% termino con codigo %CODIGO% tras %VIVIO%s >> "%LOGFILE%"

if %VIVIO% LSS 60 (
    echo [%date% %time%] vivio menos de 60s: espera larga anti-crashloop >> "%LOGFILE%"
    timeout /t 30 /nobreak >nul
) else (
    timeout /t 5 /nobreak >nul
)

goto lanzar
