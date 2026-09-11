# PilotX-InstallAutostart.ps1
# Registra el launcher en HKCU\...\Run para que arranque al iniciar sesion.
# Correr una sola vez en la PC del tractor (PowerShell como el usuario que loguea).
#
# Para auto-arranque sin interaccion: configurar autologin del usuario via netplwiz
# (o el truco del registro PasswordLess\Device si la opcion esta oculta).
#
# Para desinstalar: borrar el valor "PilotX" de la misma key.

$ErrorActionPreference = "Stop"

$launcher = "C:\PilotX\PilotX-Launcher.bat"
$runKey   = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

if (-not (Test-Path $launcher)) {
    Write-Host "ERROR: $launcher no existe. Copia PilotX-Launcher.bat a C:\PilotX\ primero." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $runKey)) {
    New-Item -Path $runKey -Force | Out-Null
}

Set-ItemProperty -Path $runKey -Name "PilotX" -Value "`"$launcher`""
Write-Host "OK: PilotX registrado en HKCU\...\Run" -ForegroundColor Green
Write-Host "Valor: $((Get-ItemProperty -Path $runKey -Name PilotX).PilotX)"
Write-Host ""
Write-Host "Recordatorio: si la PC arranca a pantalla de login, configura autologin"
Write-Host "con netplwiz para que el launcher se dispare sin intervencion."
