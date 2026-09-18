# ============================================================================
# red.ps1 — instala en la pantalla el helper de red de ViewX TabletTools
# (NetApplyWatcher, tarea PilotXNetApply como SYSTEM). Sin él, PilotX corre
# como usuario limitado y NO puede aplicar la IP fija del Ethernet
# (Configuración › Red): el pedido queda en C:\PilotX\netconfig\request.json
# y nadie lo atiende ("El helper de red no respondió").
#
#   En la pantalla (PowerShell, misma LAN que la PC del taller):
#     irm __SERVIDOR__/red.ps1 | iex
#
# Es el mismo paso 7 de ViewX\Software\TabletTools\provision-tablet.ps1.
# Lo llama también instalar.ps1 al final de una instalación nueva.
# ============================================================================
$ErrorActionPreference = "Stop"
$Servidor = "__SERVIDOR__"
function Paso($t) { Write-Host ">> $t" -ForegroundColor Cyan }

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Paso "Pidiendo permisos de administrador..."
    Start-Process powershell -Verb RunAs -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", "irm $Servidor/red.ps1 | iex"
    return
}

$tt = "C:\PilotX\TabletTools"
New-Item -ItemType Directory -Path $tt, "C:\PilotX\netconfig" -Force | Out-Null
$netScript = "$tt\NetApplyWatcher.ps1"
Invoke-WebRequest -UseBasicParsing -Uri "$Servidor/kit/TabletTools/NetApplyWatcher.ps1" -OutFile $netScript -TimeoutSec 60
Paso "NetApplyWatcher.ps1 copiado a $tt"

# Tarea SYSTEM al arranque, sin límite de tiempo, se relanza sola (igual que provision-tablet.ps1).
$arg = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $netScript + '"'
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arg
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval ([TimeSpan]::FromMinutes(1))
Register-ScheduledTask -TaskName "PilotXNetApply" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName "PilotXNetApply"
Start-Sleep -Seconds 3
$estado = (Get-ScheduledTask -TaskName "PilotXNetApply").State
Paso "Tarea PilotXNetApply: $estado"

Write-Host ""
if ($estado -eq "Running") {
    Write-Host "=== LISTO: el helper de red está corriendo ===" -ForegroundColor Green
    Write-Host "Ahora en PilotX › Configuración › Red volvé a aplicar la IP fija del Ethernet (por ejemplo 192.168.5.10 / 24)." -ForegroundColor Yellow
} else {
    Write-Host "La tarea no quedó corriendo. Ver C:\PilotX\netconfig\netapply.log" -ForegroundColor Red
}
