@echo off
REM stop-legacy-services.bat
REM Baja y desactiva TODOS los servicios/procesos legacy AgroParallel:
REM   - Procesos node.exe (vistax-server, OrbitX-Sync standalone, etc.)
REM   - Procesos AgOpenGPS / AgIO
REM   - Servicios Windows registrados (pm2, node-windows, vistax, orbitx)
REM   - Entradas de autostart en HKCU\Run, HKLM\Run
REM   - Shortcuts en carpetas Startup (user y common)
REM   - Tareas programadas (schtasks)
REM
REM Correr como ADMINISTRADOR (click derecho -> Ejecutar como administrador).

setlocal EnableDelayedExpansion

REM Verificar admin
net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: hay que correr como Administrador.
    echo Click derecho en el .bat -^> Ejecutar como administrador.
    pause
    exit /b 1
)

echo ============================================================
echo   Bajando legacy AgroParallel ^(Node, servicios, autostart^)
echo ============================================================
echo.

REM ----------------------------------------------------------------------
REM 1) Matar procesos
REM ----------------------------------------------------------------------
echo [1/5] Matando procesos...
REM AgOpenGPS.exe/AgIO.exe son los nombres viejos (ahora PilotX.exe/CoreX.exe);
REM se matan como legacy. NO incluir CoreX.exe: es el nombre NUEVO de AgIO.
for %%P in (AgOpenGPS.exe AgIO.exe GPS_Out.exe AgDiag.exe ModSim.exe) do (
    taskkill /F /IM %%P /T 2>nul && echo   killed %%P || echo   %%P no estaba corriendo
)

REM Node.exe filtrado por commandline AgroParallel/vistax/orbitx
for /f "tokens=*" %%I in ('wmic process where "name='node.exe' and (CommandLine like '%%%%AgroParallel%%%%' or CommandLine like '%%%%vistax%%%%' or CommandLine like '%%%%orbitx%%%%')" get ProcessId /value 2^>nul ^| find "ProcessId="') do (
    set %%I
    taskkill /F /PID !ProcessId! /T 2>nul && echo   killed node.exe PID !ProcessId!
)
echo.

REM ----------------------------------------------------------------------
REM 2) Detener y eliminar servicios Windows
REM ----------------------------------------------------------------------
echo [2/5] Servicios Windows registrados...
for /f "tokens=2" %%S in ('sc query state^= all ^| findstr /B "SERVICE_NAME"') do (
    echo %%S | findstr /I "vistax orbitx agroparallel pilotx pm2.*vistax pm2.*orbitx node.*vistax node.*orbitx" >nul
    if not errorlevel 1 (
        echo   Encontrado: %%S
        sc stop %%S >nul 2>&1
        sc delete %%S >nul 2>&1 && echo     -^> detenido y eliminado || echo     -^> fallo al eliminar
    )
)
echo.

REM ----------------------------------------------------------------------
REM 3) Limpiar entradas de autostart en registry
REM ----------------------------------------------------------------------
echo [3/5] Autostart en registry ^(HKCU\Run, HKLM\Run^)...
for %%H in (HKCU HKLM) do (
    for /f "tokens=1,2*" %%A in ('reg query "%%H\Software\Microsoft\Windows\CurrentVersion\Run" 2^>nul ^| findstr /I "vistax orbitx node agroparallel pilotx aog"') do (
        echo   %%H\...\Run\%%A
        reg delete "%%H\Software\Microsoft\Windows\CurrentVersion\Run" /v "%%A" /f >nul 2>&1 && echo     -^> borrado
    )
)
echo.

REM ----------------------------------------------------------------------
REM 4) Limpiar shortcuts en carpetas Startup
REM ----------------------------------------------------------------------
echo [4/5] Shortcuts en carpetas Startup...
for %%D in (
    "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
    "%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp"
) do (
    if exist %%D (
        for %%F in ("%%~D\*vistax*.lnk" "%%~D\*orbitx*.lnk" "%%~D\*node*.lnk" "%%~D\*agro*.lnk") do (
            if exist "%%F" (
                echo   borrando %%F
                del /F /Q "%%F" 2>nul
            )
        )
    )
)
echo.

REM ----------------------------------------------------------------------
REM 5) Tareas programadas
REM ----------------------------------------------------------------------
echo [5/5] Tareas programadas ^(schtasks^)...
for /f "tokens=*" %%T in ('schtasks /query /fo LIST 2^>nul ^| findstr /I /B "TaskName" ^| findstr /I "vistax orbitx node agroparallel pilotx aog"') do (
    set "TASK=%%T"
    set "TASK=!TASK:TaskName:=!"
    set "TASK=!TASK: =!"
    if defined TASK (
        echo   borrando tarea: !TASK!
        schtasks /delete /tn "!TASK!" /f >nul 2>&1
    )
)
echo.

REM ----------------------------------------------------------------------
REM Verificar puerto 1883 libre
REM ----------------------------------------------------------------------
echo --- Verificacion final ---
netstat -ano ^| findstr :1883 >nul
if errorlevel 1 (
    echo   Puerto 1883 ^(MQTT broker^): LIBRE
) else (
    echo   ATENCION: puerto 1883 sigue ocupado:
    netstat -ano | findstr :1883
)

echo.
echo ============================================================
echo   FIN
echo ============================================================
pause
