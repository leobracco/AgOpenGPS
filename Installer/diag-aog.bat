@echo off
REM diag-aog.bat - diagnostica por que AgOpenGPS no levanta. Solo CMD, sin PowerShell.
REM Uso: doble click, o   diag-aog.bat   en CMD.

setlocal EnableDelayedExpansion

set AOG=C:\Program Files\AgroParallel\Piloto\AgOpenGPS.exe
set DIR=C:\Program Files\AgroParallel\Piloto

echo === Diagnostico AgOpenGPS ===
echo.

REM ---- [1] Existe el .exe?
echo [1] Existe el .exe?
if not exist "%AOG%" (
    echo   NO - %AOG% no existe
    goto :fin
)
echo   OK - %AOG%
echo.

REM ---- [2] SmartScreen / Zone.Identifier
echo [2] Bloqueo SmartScreen / Zone.Identifier?
dir /R "%AOG%" 2>nul | find /I "Zone.Identifier" >nul
if not errorlevel 1 (
    echo   SI - bloqueado por SmartScreen
    echo   Solucion: en la pestania "Propiedades" del .exe, tildar "Desbloquear"
) else (
    echo   No bloqueado
)
echo.

REM ---- [3] Lanzar AOG y capturar ExitCode
echo [3] Ejecutando AgOpenGPS... (cuando lo cierres o crashee, vemos el ExitCode)
pushd "%DIR%"
"%AOG%"
set EC=%ERRORLEVEL%
popd
if "%EC%"=="0" (
    echo   ExitCode: 0  ^(cierre normal^)
) else (
    echo   ExitCode: %EC%  ^(CRASHEO^)
)
echo.

REM ---- [4] Ultimos errores en EventLog (Application)
echo [4] Ultimos errores .NET Runtime / Application Error en EventLog...
echo --- .NET Runtime ---
wevtutil qe Application /q:"*[System[Provider[@Name='.NET Runtime'] and (Level=2 or Level=3)]]" /c:2 /rd:true /f:text 2^>nul
echo --- Application Error ---
wevtutil qe Application /q:"*[System[Provider[@Name='Application Error']]]" /c:2 /rd:true /f:text 2^>nul
echo.

REM ---- [5] Registry y carpetas
echo [5] Estado del registry HKCU\SOFTWARE\AgOpenGPS...
reg query "HKCU\SOFTWARE\AgOpenGPS" /v workingDirectory 2^>nul
if errorlevel 1 (
    echo   Sin registry de AgOpenGPS ^(instalacion limpia^)
) else (
    for /f "tokens=2,*" %%a in ('reg query "HKCU\SOFTWARE\AgOpenGPS" /v workingDirectory 2^>nul ^| find "workingDirectory"') do (
        set WD=%%b
    )
    echo   workingDirectory: !WD!
    if exist "!WD!" (
        echo     -^> existe? SI
    ) else (
        echo     -^> existe? NO   ^(REGISTRY STALE - causa habitual de crash^)
        echo     Solucion: reg delete "HKCU\SOFTWARE\AgOpenGPS" /v workingDirectory /f
    )
)
echo.

echo [5b] Carpetas de datos en %%USERPROFILE%%\Documents\AgOpenGPS
for %%S in (Fields Vehicles Logs Tools) do (
    if exist "%USERPROFILE%\Documents\AgOpenGPS\%%S" (
        echo   %%S        : SI
    ) else (
        echo   %%S        : NO  ^(falta - crash garantizado^)
    )
)
echo.

:fin
echo === Fin ===
pause
