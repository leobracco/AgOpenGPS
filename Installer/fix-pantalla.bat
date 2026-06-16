@echo off
REM fix-pantalla.bat
REM Aplica el fix de root-cause en una pantalla que tiene PilotX/AOG ya instalado
REM pero no levanta porque arrastra workingDirectory = G:\CentriX (que no existe).
REM
REM Que hace:
REM   1) Mata AgOpenGPS / AgIO si estan corriendo
REM   2) Crea D:\AgroParallel\PilotX\AgOpenGPS\{Fields,Vehicles,Logs,Tools}
REM   3) Reescribe aog_settings.json en el dir de instalacion (Piloto o PilotX)
REM   4) Limpia HKCU\SOFTWARE\AgOpenGPS y deja WorkingDirectory = D:\AgroParallel\PilotX
REM   5) Lanza AOG
REM
REM Correr como ADMINISTRADOR.

setlocal EnableDelayedExpansion

net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: hay que correr como Administrador.
    pause
    exit /b 1
)

set BASE=D:\AgroParallel\PilotX

echo ============================================================
echo   Fix-Pantalla PilotX - workingDirectory = %BASE%
echo ============================================================
echo.

REM ---- [1] Matar AOG y AgIO si estan corriendo
echo [1/5] Matando AOG/AgIO si estan abiertos...
taskkill /F /IM PilotX.exe /T 2>nul
taskkill /F /IM CoreX.exe /T 2>nul
echo.

REM ---- [2] Crear estructura de carpetas en D:
echo [2/5] Creando %BASE%\AgOpenGPS\{Fields,Vehicles,Logs,Tools}...
for %%S in (Fields Vehicles Logs Tools) do (
    if not exist "%BASE%\AgOpenGPS\%%S" (
        mkdir "%BASE%\AgOpenGPS\%%S" 2>nul && echo   creado %%S
    ) else (
        echo   ya existe %%S
    )
)
echo.

REM ---- [3] Detectar carpeta de instalacion (Piloto vs PilotX)
echo [3/5] Detectando carpeta de instalacion...
set INSTDIR=
if exist "C:\Program Files\AgroParallel\PilotX\PilotX.exe" (
    set "INSTDIR=C:\Program Files\AgroParallel\PilotX"
) else if exist "C:\Program Files\AgroParallel\Piloto\PilotX.exe" (
    set "INSTDIR=C:\Program Files\AgroParallel\Piloto"
) else if exist "C:\Program Files (x86)\AgroParallel\PilotX\PilotX.exe" (
    set "INSTDIR=C:\Program Files (x86)\AgroParallel\PilotX"
) else if exist "C:\Program Files (x86)\AgroParallel\Piloto\PilotX.exe" (
    set "INSTDIR=C:\Program Files (x86)\AgroParallel\Piloto"
)

if "%INSTDIR%"=="" (
    echo   ERROR: no encontre PilotX.exe instalado.
    echo   Buscado en C:\Program Files\AgroParallel\{Piloto,PilotX} y x86.
    pause
    exit /b 1
)
echo   encontrado: %INSTDIR%
echo.

REM ---- [4] Reescribir aog_settings.json
echo [4/5] Reescribiendo aog_settings.json...
> "%INSTDIR%\aog_settings.json" echo {
>> "%INSTDIR%\aog_settings.json" echo   "working_directory": "D:\\AgroParallel\\PilotX",
>> "%INSTDIR%\aog_settings.json" echo   "vehicle_file_name": "",
>> "%INSTDIR%\aog_settings.json" echo   "language": "es"
>> "%INSTDIR%\aog_settings.json" echo }
echo   OK
type "%INSTDIR%\aog_settings.json"
echo.

REM ---- [5] Limpiar HKCU stale y dejar WorkingDirectory correcto
echo [5/5] Limpiando HKCU\SOFTWARE\AgOpenGPS...
reg delete "HKCU\SOFTWARE\AgOpenGPS" /f >nul 2>&1
reg add "HKCU\SOFTWARE\AgOpenGPS" /v WorkingDirectory /t REG_SZ /d "%BASE%" /f >nul
reg add "HKCU\SOFTWARE\AgOpenGPS" /v Language /t REG_SZ /d "es" /f >nul
echo   OK - WorkingDirectory = %BASE%
echo.

REM ---- Verificacion
echo --- Verificacion ---
reg query "HKCU\SOFTWARE\AgOpenGPS" 2>nul
echo.

REM ---- Lanzar AOG
echo Lanzando AgOpenGPS...
echo (cerralo cuando termines de probar para volver a esta ventana)
echo.
pushd "%INSTDIR%"
start "" "%INSTDIR%\PilotX.exe"
popd

echo.
echo ============================================================
echo   FIN - si AOG abrio: el fix funciono, recompilamos installer
echo ============================================================
pause
