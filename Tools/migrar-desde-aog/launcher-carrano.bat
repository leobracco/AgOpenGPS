@echo off
:: ============================================================================
:: LAUNCHER.BAT - MODO EMBEBIDO - AGRO PARALLEL
::
:: Es el SHELL de Windows (HKLM\...\Winlogon\Shell). No hay explorer detras:
:: si este script termina, la pantalla queda en negro. Por eso NO lleva exit
:: y termina en un bucle que supervisa PilotX.
::
:: 2026-09-15 (Carrano e Hijos): se reemplazo AgOpenGPS por PilotX. El AOG
:: viejo sigue instalado en C:\AgroParallel pero YA NO SE INICIA. Para
:: levantarlo a mano en una emergencia: C:\AgroParallel\AgOpenGPS.exe
:: ============================================================================

title MODO EMBEBIDO - AGRO PARALLEL
color 0A
setlocal EnableDelayedExpansion

set PILOTX_DIR=C:\PilotX
set LOGDIR=%PILOTX_DIR%\logs
set LOGFILE=%LOGDIR%\launcher.log
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo [%date% %time%] === arranque modo embebido === >> "%LOGFILE%"

:: --- 1. AnyDesk PRIMERO -----------------------------------------------------
:: Va antes que todo a proposito: es el acceso remoto. Si PilotX falla, la
:: pantalla tiene que seguir siendo alcanzable para arreglarla sin ir al campo.
echo Iniciando AnyDesk...
start "" "C:\Program Files (x86)\AnyDesk\AnyDesk.exe"
ping -n 4 127.0.0.1 >nul 2>&1

:: --- 2. Widget de brillo ----------------------------------------------------
echo Iniciando AGP-BrilloWidget...
start "" "C:\AGP-BrilloWidget\AGP.WidgetLauncher.exe"
ping -n 4 127.0.0.1 >nul 2>&1

:: --- 3. Selector de WiFi ----------------------------------------------------
echo Abriendo selector WiFi...
start "WiFi Selector" cmd /k "call C:\AgroParallel\wifi_selector.bat"

:: --- 4. PilotX, supervisado -------------------------------------------------
:: El Engine va DENTRO del bucle: si el motor muere a mitad de jornada, el
:: proximo relanzamiento lo revive tambien. Sin motor la pantalla queda con
:: datos vencidos y nadie se da cuenta.
::
:: Anti-crashloop: si la pantalla vivio menos de 60 s, la espera sube a 30 s.
:: Un problema permanente queda en un ciclo lento que no quema CPU, pero
:: sigue intentando: en el tractor no hay nadie mirando.

set PILOTX_EXE=%PILOTX_DIR%\Desktop\PilotX.Desktop.exe
if not exist "%PILOTX_EXE%" set PILOTX_EXE=%PILOTX_DIR%\PilotX.Desktop.exe

if not exist "%PILOTX_EXE%" (
    echo [%date% %time%] ERROR: no existe %PILOTX_EXE% >> "%LOGFILE%"
    echo.
    echo ERROR: no se encontro PilotX en %PILOTX_DIR%.
    echo Para recuperar el escritorio: Ctrl+Shift+Esc, Archivo, Ejecutar, explorer.exe
    echo.
    pause
    goto :eof
)

:lanzar
if exist "%PILOTX_DIR%\Engine\PilotX.GuidanceEngine.exe" (
    tasklist /FI "IMAGENAME eq PilotX.GuidanceEngine.exe" 2>nul | find /I "PilotX.GuidanceEngine.exe" >nul
    if errorlevel 1 (
        echo [%date% %time%] Engine no corria: se levanta >> "%LOGFILE%"
        start "PilotX Engine" /min "%PILOTX_DIR%\Engine\PilotX.GuidanceEngine.exe" --webhost --corex
        ping -n 7 127.0.0.1 >nul 2>&1
    )
)

set T0=
for /f %%S in ('powershell -NoProfile -Command "[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()"') do set T0=%%S
if not defined T0 set T0=0

echo [%date% %time%] lanzando PilotX >> "%LOGFILE%"
cd /d "%PILOTX_DIR%"
start /wait "" "%PILOTX_EXE%"
set CODIGO=%ERRORLEVEL%

set T1=
for /f %%S in ('powershell -NoProfile -Command "[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()"') do set T1=%%S
if not defined T1 set T1=0
set /a VIVIO=T1-T0

echo [%date% %time%] PilotX termino con codigo %CODIGO% tras %VIVIO%s >> "%LOGFILE%"

:: Flag para actualizar sin pelear contra el supervisor: quien va a instalar
:: una version nueva lo crea, hace lo suyo, y lo borra.
if exist "%PILOTX_DIR%\actualizando.flag" (
    echo [%date% %time%] actualizando.flag presente: supervisor en pausa >> "%LOGFILE%"
    :esperarflag
    ping -n 11 127.0.0.1 >nul 2>&1
    if exist "%PILOTX_DIR%\actualizando.flag" goto esperarflag
)

:: Un VIVIO negativo o vacio (reloj, locale, variable sin setear) se trata como
:: "vivio poco": ante la duda, esperar. Lo contrario es el crashloop.
if not defined VIVIO set VIVIO=0
if %VIVIO% LSS 60 (
    call :esperar 30
) else (
    call :esperar 5
)

goto lanzar

:: --- espera de N segundos ---------------------------------------------------
:: ping en vez de timeout: timeout sale de inmediato cuando stdin no es una
:: consola (lanzado por Start-Process, como servicio, o con la entrada
:: redirigida), y ahi el anti-crashloop deja de existir.
:esperar
set /a _p=%~1+1
ping -n %_p% 127.0.0.1 >nul 2>&1
exit /b 0
