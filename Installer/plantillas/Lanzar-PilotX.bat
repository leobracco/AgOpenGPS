@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem PilotX (stack nuevo): Engine headless + pantalla Avalonia.
rem Doble clic y listo. El Engine queda minimizado (broker MQTT
rem :1883, API :5180, panel :5181); la pantalla se abre sola.
rem
rem VIGILANTE (2026-08-18): si la pantalla muere con error (p. ej.
rem el driver de video se cuelga y la voltea, TDR igfx), se
rem relanza sola a los 3 segundos y queda registro en
rem vigilante.log. Cerrarla con el boton de siempre NO la
rem relanza (sale con codigo 0).
rem
rem BACKOFF (2026-09-19): el 05/09 el vigilante relanzo CUATRO
rem veces en 43 segundos. Eso es un bucle de crash: la pantalla
rem se rompia al arrancar y el vigilante la levantaba para que
rem se volviera a romper, gastando CPU y llenando el log sin
rem que el operario viera nunca nada.
rem Ahora la espera crece cuando las caidas son seguidas:
rem   3 s -> 10 s -> 30 s -> 60 s (tope).
rem NUNCA deja de relanzar: no hay tope de reintentos. Que el
rem vigilante decida por su cuenta dejar al operario sin
rem pantalla en medio del lote es peor que cualquier bucle.
rem Si la corrida duro mas de un minuto no fue un bucle, asi que
rem el contador se resetea y la proxima caida vuelve a esperar
rem solo 3 segundos (el caso TDR, que es el normal aca).
rem ============================================================
cd /d "%~dp0"
tasklist | find /i "PilotX.GuidanceEngine.exe" >nul || start "PilotX Engine" /min "%~dp0Engine\PilotX.GuidanceEngine.exe" --webhost --corex
timeout /t 6 /nobreak >nul

rem ============================================================
rem POST-MORTEM (2026-09-19). Sin esto, las caidas duras no dejan
rem rastro NINGUNO: un ACCESS_VIOLATION (0xC0000005) no le llega
rem al codigo, asi que el CrashHandler de la pantalla no lo ve y
rem errores.log queda vacio. Comprobado: 15 caidas en el log de
rem este vigilante y cero lineas en Build\Desktop\Logs.
rem
rem Dos cosas, y las dos se verificaron en banco con un
rem ACCESS_VIOLATION de verdad:
rem
rem 1) La SALIDA de cada corrida va a Logs\salida.log, y la de la
rem    corrida anterior se rota a Logs\salida-anterior.log. Antes
rem    de morir el runtime escribe ahi el cartel "Fatal error."
rem    con el tipo de excepcion y el stack completo: eso es lo
rem    que dice QUE se rompio. La pantalla es una app de ventana,
rem    asi que sin esta redireccion ese texto no va a ningun lado.
rem    PilotX.Desktop levanta la rotada al arrancar y la vuelca a
rem    errores.log (SourceCode\PilotX.Desktop\PostMortem.cs).
rem    Por eso se invoca el .exe directo en vez de "start /wait":
rem    start no deja redirigir, y cmd espera igual al programa.
rem
rem 2) DbgEnableMiniDump le pide al runtime que al morir escriba
rem    un minidump con createdump.exe (que ya viaja al lado del
rem    ejecutable). Es para el analisis profundo con WinDbg o
rem    dotnet-dump cuando el stack no alcanza. PostMortem.cs deja
rem    solo los 5 ultimos para no llenar el disco.
rem    (El crash report en JSON del runtime NO se usa: se probo
rem    DOTNET_EnableCrashReport en .NET 9.0.14 y no genera nada.)
rem ============================================================
if not exist "%~dp0Desktop\Logs\crash" mkdir "%~dp0Desktop\Logs\crash" >nul 2>&1
set "DOTNET_DbgEnableMiniDump=1"
set "DOTNET_DbgMiniDumpType=2"
set "DOTNET_DbgMiniDumpName=%~dp0Desktop\Logs\crash\crash_%%p.dmp"

set /a REINTENTOS=0

:pantalla
call :marca INICIO
if exist "%~dp0Desktop\Logs\salida.log" move /y "%~dp0Desktop\Logs\salida.log" "%~dp0Desktop\Logs\salida-anterior.log" >nul 2>&1
"%~dp0Desktop\PilotX.Desktop.exe" > "%~dp0Desktop\Logs\salida.log" 2>&1
set ERR=%errorlevel%

rem Con el flag "actualizando.flag" presente el vigilante NO relanza: lo crea
rem quien va a recompilar/actualizar (si no, el vigilante pelea contra el
rem publish relanzando lo que se acaba de matar - paso el 2026-08-18).
rem
rem PERO EL FLAG VENCE. Sin vencimiento, si el Updater se muere, lo matan o se
rem corta la luz entre que lo crea y que lo borra, el vigilante NO RELANZA
rem NUNCA MAS y la cabina queda muerta hasta que alguien borre un archivo a
rem mano por consola remota. Un update no tarda 10 minutos: pasado ese rato el
rem flag es basura de un intento que fracaso, y relanzar es siempre mejor que
rem dejar al operario sin pantalla.
if exist "%~dp0actualizando.flag" (
    powershell -NoProfile -Command "exit ([int](((Get-Date) - (Get-Item '%~dp0actualizando.flag').LastWriteTime).TotalMinutes -gt 10))" >nul 2>&1
    if errorlevel 1 (
        echo [%date% %time%] actualizando.flag con mas de 10 min - update abortado, se borra y se relanza >> "%~dp0vigilante.log"
        del /q "%~dp0actualizando.flag" >nul 2>&1
    ) else (
        exit /b 0
    )
)
if %ERR% equ 0 exit /b 0

call :marca FIN
set /a DUR=FIN-INICIO
if %DUR% lss 0 set /a DUR+=8640000

rem DUR esta en centesimas de segundo. 6000 = 60 s.
if %DUR% geq 6000 (set /a REINTENTOS=0) else (set /a REINTENTOS+=1)

set ESPERA=3
if !REINTENTOS! geq 3 set ESPERA=10
if !REINTENTOS! geq 5 set ESPERA=30
if !REINTENTOS! geq 8 set ESPERA=60

set /a SEG=DUR/100
if !REINTENTOS! leq 1 (
    echo [%date% %time%] PilotX.Desktop salio con error %ERR% tras !SEG! s - relanzando en !ESPERA! s >> "%~dp0vigilante.log"
) else (
    echo [%date% %time%] PilotX.Desktop salio con error %ERR% tras !SEG! s - caida seguida n!REINTENTOS!, relanzando en !ESPERA! s >> "%~dp0vigilante.log"
)
timeout /t !ESPERA! /nobreak >nul
goto pantalla

rem ---- reloj en centesimas de segundo desde medianoche --------
rem El "%TIME: =0%" es para la hora de un digito, que cmd escribe
rem con espacio adelante (" 9:31:57.22") y romperia la cuenta.
:marca
set "t=%TIME: =0%"
set /a %1=(1%t:~0,2%-100)*360000+(1%t:~3,2%-100)*6000+(1%t:~6,2%-100)*100+(1%t:~9,2%-100)
exit /b
