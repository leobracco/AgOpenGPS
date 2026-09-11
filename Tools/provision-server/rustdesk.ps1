# ============================================================================
# rustdesk.ps1 — deja RustDesk en la pantalla apuntando al servidor propio de
# Agro Parallel (asistx.agroparallel.com) con contraseña fija de acceso.
#
#   En la pantalla (PowerShell admin, misma LAN que la PC del taller):
#     irm __SERVIDOR__/rustdesk.ps1?p=__PEDIDO__ | iex
#
# Si RustDesk ya está instalado NO corre el instalador (con el servicio ya
# presente, --silent-install se queda colgado esperando: tablet de Clancy,
# 2026-09-11); solo reescribe la config y la contraseña. La config va en
# RustDesk2.toml del servicio (LocalService) y de cada usuario, con el
# formato real: rendezvous_server arriba y custom-rendezvous-server /
# relay-server / key en [options]. Lo llama también instalar.ps1.
# ============================================================================
$ErrorActionPreference = "Stop"
$Servidor = "__SERVIDOR__"
$Pedido   = "__PEDIDO__"
function Paso($t) { Write-Host ">> $t" -ForegroundColor Cyan }

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Paso "Pidiendo permisos de administrador..."
    Start-Process powershell -Verb RunAs -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", "irm $Servidor/rustdesk.ps1?p=$Pedido | iex"
    return
}

$info = Invoke-RestMethod -Uri "$Servidor/api/rustdesk?p=$Pedido" -TimeoutSec 15
$inst = "C:\Program Files\RustDesk\rustdesk.exe"

if (-not (Test-Path $inst)) {
    $kit = "C:\PilotX\kit"
    New-Item -ItemType Directory -Path $kit -Force | Out-Null
    $exe = Join-Path $kit $info.archivo
    Paso "Bajando RustDesk ($($info.servidor))..."
    Invoke-WebRequest -UseBasicParsing -Uri "$Servidor/kit/RustDesk.exe" -OutFile $exe -TimeoutSec 600
    Paso "Instalando como servicio (silencioso, hasta 3 min)..."
    $p = Start-Process $exe -ArgumentList "--silent-install" -PassThru
    if (-not $p.WaitForExit(180000)) { Write-Host "   el instalador no terminó solo; sigo igual" -ForegroundColor DarkGray }
    Start-Sleep -Seconds 8
    if (-not (Test-Path $inst)) { throw "No quedó instalado en $inst" }
} else { Paso "RustDesk ya estaba instalado: solo configuro servidor y contraseña" }

Stop-Service RustDesk -Force -ErrorAction SilentlyContinue
Get-Process rustdesk -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$srv = $info.servidor
$toml = "rendezvous_server = '${srv}:21116'`nnat_type = 1`nserial = 0`n`n[options]`ncustom-rendezvous-server = '$srv'`nrelay-server = '$srv'`nkey = '$($info.clave)'`n"
$dirs = @("C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config") + (Get-ChildItem C:\Users -Directory | ForEach-Object { $_.FullName + "\AppData\Roaming\RustDesk\config" })
foreach ($dir in $dirs) {
    try {
        if (-not (Test-Path (Split-Path $dir -Parent))) { continue }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Set-Content (Join-Path $dir "RustDesk2.toml") $toml -Encoding ascii
    } catch { Write-Host "   (config en $dir no escrita: $($_.Exception.Message))" -ForegroundColor DarkGray }
}
Start-Service RustDesk
Start-Sleep -Seconds 8
$pass = $info.password
& $inst --password $pass 2>$null | Out-Null
Start-Sleep -Seconds 2
$id = (& $inst --get-id 2>$null | Select-Object -Last 1).Trim()
Paso "RustDesk ID: $id  contraseña: $pass  servidor: $srv"

if ($Pedido) {
    try { Invoke-RestMethod -Method Post -Uri "$Servidor/api/pedidos/rustdesk" -ContentType "application/json" -Body (@{ codigo = $Pedido; id = $id; pass = $pass } | ConvertTo-Json) -TimeoutSec 10 | Out-Null } catch { }
}
Write-Host ""
Write-Host "=== LISTO: RustDesk contra $srv — ID $id ===" -ForegroundColor Green
