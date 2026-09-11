# ============================================================================
# instalar.ps1 — instalación de PilotX en una pantalla/tablet nueva, con UN
# comando, desde el servidor interno del taller (Tools/provision-server).
#
#   En la tablet (PowerShell, la misma LAN que la PC del taller):
#     irm __SERVIDOR__/instalar.ps1?p=__PEDIDO__ | iex
#
# Qué hace:
#   1. Se eleva a Administrador si hace falta.
#   2. Baja el kit (Provision-Pantalla.ps1, KioskSetup, branding, runtimes)
#      y el paquete PilotX del pedido a C:\PilotX\kit.
#   3. Calcula el device_id igual que PilotX (MD5 del MAC) y se registra en el
#      servidor, que lo da de alta en OrbitX y lo ASIGNA al cliente del pedido.
#   4. Corre Provision-Pantalla.ps1 (usuarios, limpieza, energía, firewall,
#      WinRM, runtimes, cliente.json, nombre de equipo).
#   5. Extrae PilotX en C:\PilotX y escribe C:\PilotX\Engine\orbitX.json con
#      el token del equipo y la org del cliente.
#   6. Activa el kiosko (PilotX-KioskSetup /yes) si el pedido lo pide.
# El servidor recibe el progreso y lo muestra en la web.
# ============================================================================
$ErrorActionPreference = "Stop"
$Servidor = "__SERVIDOR__"
$Pedido   = "__PEDIDO__"

function Paso($t) { Write-Host ">> $t" -ForegroundColor Cyan }
function Avisar($msg, $estado) {
    try { Invoke-RestMethod -Method Post -Uri "$Servidor/api/progreso" -ContentType "application/json" -Body (@{ pedido = $Pedido; msg = $msg; estado = $estado } | ConvertTo-Json) -TimeoutSec 10 | Out-Null } catch { }
}

if (-not $Pedido) { Write-Host "Falta el código de pedido: abrí $Servidor y creá uno." -ForegroundColor Red; return }

# ── 1. Elevar ────────────────────────────────────────────────────────────────
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Paso "Pidiendo permisos de administrador..."
    $cmd = "irm $Servidor/instalar.ps1?p=$Pedido | iex"
    Start-Process powershell -Verb RunAs -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $cmd
    return
}

$kit = "C:\PilotX\kit"
New-Item -ItemType Directory -Path $kit, "$kit\Branding" -Force | Out-Null
Start-Transcript -Path "C:\PilotX\instalar-log.txt" -Append | Out-Null
Avisar "tablet $env:COMPUTERNAME conectada, empezando" "instalando"

# ── 2. Identidad: mismo device_id que calcula PilotX (MD5 del primer MAC activo) ──
function DeviceId {
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -eq "Up" -and $nic.NetworkInterfaceType -ne "Loopback") {
            $mac = $nic.GetPhysicalAddress().ToString()
            if ($mac -and $mac.Length -ge 12) {
                $md5 = [System.Security.Cryptography.MD5]::Create()
                $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($mac))
                return "OX-" + (([System.BitConverter]::ToString($hash)) -replace "-", "").Substring(0, 12).ToUpper()
            }
        }
    }
    return "OX-" + ([guid]::NewGuid().ToString("N").Substring(0, 12).ToUpper())
}
$deviceId = DeviceId
Paso "device_id de esta pantalla: $deviceId"

# ── 3. Registro en el servidor (alta + asignación en OrbitX) ────────────────
Paso "Registrando en OrbitX a través del servidor del taller..."
$reg = Invoke-RestMethod -Method Post -Uri "$Servidor/api/registrar" -ContentType "application/json" -Body (@{ pedido = $Pedido; device_id = $deviceId; hostname = $env:COMPUTERNAME } | ConvertTo-Json) -TimeoutSec 60
if (-not $reg.ok) { throw "El servidor rechazó el registro: $($reg.error)" }
Paso "Equipo dado de alta y asignado a '$($reg.estab_slug)' (cliente $($reg.cliente))"

# ── 4. Bajar kit + paquete ───────────────────────────────────────────────────
$bajar = @(
    @{ n = "Provision-Pantalla.ps1"; d = "$kit\Provision-Pantalla.ps1" },
    @{ n = "PilotX-KioskSetup.exe";  d = "$kit\PilotX-KioskSetup.exe" },
    @{ n = "Branding/logo.png";      d = "$kit\Branding\logo.png"; opc = $true },
    @{ n = "Branding/logo-fondo-blanco.png"; d = "$kit\Branding\logo-fondo-blanco.png"; opc = $true },
    @{ n = "Branding/fondo.png";     d = "$kit\Branding\fondo.png"; opc = $true },
    @{ n = "vc_redist.x64.exe";      d = "$kit\vc_redist.x64.exe"; opc = $true },
    @{ n = "MicrosoftEdgeWebview2Setup.exe"; d = "$kit\MicrosoftEdgeWebview2Setup.exe"; opc = $true }
)
foreach ($b in $bajar) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$Servidor/kit/$($b.n)" -OutFile $b.d -TimeoutSec 600
        Paso "kit: $($b.n)"
    } catch {
        if ($b.opc) { Write-Host "   (opcional, no está en el servidor: $($b.n))" -ForegroundColor DarkGray }
        else { throw "No pude bajar $($b.n): $($_.Exception.Message)" }
    }
}
$zipNombre = Split-Path -Leaf $reg.paquete
$zip = "$kit\$zipNombre"
Paso "Bajando $zipNombre (unos 200 MB, paciencia)..."
Avisar "bajando $zipNombre"
Invoke-WebRequest -UseBasicParsing -Uri "$Servidor$($reg.paquete)" -OutFile $zip -TimeoutSec 1800
Paso "Paquete bajado: $([math]::Round((Get-Item $zip).Length / 1MB)) MB"

# ── 5. Aprovisionamiento base (usuarios, limpieza, energía, red, runtimes…) ──
Avisar "corriendo Provision-Pantalla.ps1" "instalando"
$psArgs = @("-ExecutionPolicy", "Bypass", "-File", "$kit\Provision-Pantalla.ps1", "-Cliente", $reg.cliente, "-SinKiosko")
if ($reg.cuit) { $psArgs += @("-Cuit", $reg.cuit) }
if ($reg.nombre_equipo) { $psArgs += @("-NombreEquipo", $reg.nombre_equipo) }
$p = Start-Process powershell -ArgumentList $psArgs -Wait -PassThru -NoNewWindow
Paso "Provision-Pantalla terminó con código $($p.ExitCode)"

# ── 6. PilotX ────────────────────────────────────────────────────────────────
Avisar "extrayendo PilotX $($reg.version) en C:\PilotX" "instalando"
Get-Process | Where-Object { $_.ProcessName -match "^PilotX|^AgroParallel|msedgewebview2" } | Stop-Process -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($e in $z.Entries) {
        $rel = $e.FullName -replace "/", "\"
        if (-not $rel -or $rel.EndsWith("\")) { continue }
        $dst = Join-Path "C:\PilotX" $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
    }
} finally { $z.Dispose() }
Paso "PilotX extraído"

# orbitX.json: identidad propia + token de ESTE equipo + org del cliente.
$engine = "C:\PilotX\Engine"
New-Item -ItemType Directory -Path $engine -Force | Out-Null
$orbit = @{
    enabled      = $true
    server_url   = $reg.server_url
    device_token = $reg.device_token
    master_token = "vx-device-token"
    device_id    = $reg.device_id
    estab_slug   = $reg.estab_slug
}
$orbit | ConvertTo-Json | Out-File "$engine\orbitX.json" -Encoding utf8
Paso "orbitX.json escrito (device $($reg.device_id), org $($reg.estab_slug))"

# ── 7. Kiosko ────────────────────────────────────────────────────────────────
if ($reg.kiosko -and (Test-Path "$kit\PilotX-KioskSetup.exe")) {
    Avisar "activando modo kiosko" "instalando"
    $k = Start-Process "$kit\PilotX-KioskSetup.exe" -ArgumentList "/yes" -Wait -PassThru -NoNewWindow
    Paso "Kiosko: código $($k.ExitCode)"
}

# ── 8. Helper de red (tarea SYSTEM PilotXNetApply, de ViewX TabletTools) ────
# Sin esto PilotX (usuario limitado) no puede aplicar la IP fija del Ethernet.
Avisar "instalando helper de red (PilotXNetApply)" "instalando"
try { Invoke-Expression (Invoke-RestMethod -Uri "$Servidor/red.ps1" -TimeoutSec 30) }
catch { Write-Host "Helper de red: $($_.Exception.Message) — correr después: irm $Servidor/red.ps1 | iex" -ForegroundColor Yellow }

Avisar "instalación terminada; falta reiniciar" "instalado"
Stop-Transcript | Out-Null
Write-Host ""
Write-Host "=== LISTO: PilotX $($reg.version) instalado y asignado a $($reg.cliente) ===" -ForegroundColor Green
Write-Host "Reiniciá la pantalla para aplicar nombre de equipo, fondo y kiosko." -ForegroundColor Yellow
$r = Read-Host "¿Reiniciar ahora? (s/N)"
if ($r -match "^[sS]") { Restart-Computer -Force }
