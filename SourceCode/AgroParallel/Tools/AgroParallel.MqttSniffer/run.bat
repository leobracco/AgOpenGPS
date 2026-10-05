@echo off
REM Launcher de AgroParallel.MqttSniffer
REM Hace build si no existe el .exe y arranca con el broker local (127.0.0.1:1883).
REM Pasale args: run.bat -h 192.168.5.10 -t agp/vistax/#

setlocal
set ROOT=%~dp0
set EXE=%ROOT%bin\Release\win-x64\AgroParallel.MqttSniffer.exe
if not exist "%EXE%" (
  echo [build] no encuentro %EXE%, compilando...
  pushd "%ROOT%"
  dotnet build -c Release || ( echo Build fallo. & exit /b 1 )
  popd
)
"%EXE%" %*
endlocal
