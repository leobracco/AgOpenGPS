# ============================================================================
#  instalar-remoto.ps1 — instalación de UNA LÍNEA para una pantalla que VIENE
#  DE AgOpenGPS y cuyos lotes hay que conservar.
#
#  Se corre así (por túnel TCP de AnyDesk/RustDesk, o en la LAN):
#      irm __SERVIDOR__/instalar-remoto.ps1?p=__PEDIDO__ | iex
#
#  Diferencia con instalar.ps1: este RESCATA primero los lotes y la
#  configuración del AgOpenGPS viejo, no instala nada si ese rescate no salió
#  bien, y al final los devuelve donde el operario los va a ver.
#
#  El paquete lo baja de OrbitX con el token que entrega el registro, así los
#  194 MB no pasan por el túnel de la sesión remota.
# ============================================================================
$ErrorActionPreference = "Continue"
$Servidor = "__SERVIDOR__"
$Pedido   = "__PEDIDO__"
$Destino  = "C:\PilotX"
$Kit      = "C:\PilotX\kit"

function Titulo($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Ok($t)     { Write-Host "  [OK] $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "  [--] $t" -ForegroundColor DarkGray }
function Mal($t)    { Write-Host "  [!!] $t" -ForegroundColor Red }
function Avisar($msg, $estado) {
    try {
        Invoke-RestMethod -Method Post -Uri "$Servidor/api/progreso" -ContentType "application/json" `
            -Body (@{ pedido = $Pedido; msg = $msg; estado = $estado } | ConvertTo-Json) -TimeoutSec 10 | Out-Null
    } catch { }
}

if (-not $Pedido) { Mal "Falta el código de pedido."; return }

# ── Elevar ──────────────────────────────────────────────────────────────────
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Write-Host "Pidiendo permisos de administrador..." -ForegroundColor Cyan
    Start-Process powershell -Verb RunAs -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", `
        "-Command", "irm $Servidor/instalar-remoto.ps1?p=$Pedido | iex"
    return
}

New-Item -ItemType Directory -Path $Destino, $Kit -Force | Out-Null
Start-Transcript -Path "$Destino\instalar-log.txt" -Append | Out-Null
$problemas = @()

# ── 1. Registro ─────────────────────────────────────────────────────────────
Titulo "1/8  Registro del equipo"
Avisar "pantalla $env:COMPUTERNAME conectada (migración desde AgOpenGPS)" "instalando"
$deviceId = "OX-" + ((Get-CimInstance Win32_ComputerSystemProduct).UUID -replace '[^A-Fa-f0-9]', '').Substring(0, 12).ToUpper()
try {
    $reg = Invoke-RestMethod -Method Post -Uri "$Servidor/api/registrar" -ContentType "application/json" `
        -Body (@{ pedido = $Pedido; device_id = $deviceId; hostname = $env:COMPUTERNAME } | ConvertTo-Json) -TimeoutSec 60
} catch { Mal "no se pudo registrar: $($_.Exception.Message)"; Stop-Transcript | Out-Null; return }
Ok "$($reg.cliente) — device $($reg.device_id), org $($reg.estab_slug)"

# ── 2. Rescate de los lotes ─────────────────────────────────────────────────
# Lo primero de todo y lo único que no se puede rehacer. Si esto no sale bien,
# no se instala nada.
Titulo "2/8  Lotes y configuración de AgOpenGPS"
Avisar "rescatando lotes de AgOpenGPS" "instalando"
try {
    Invoke-WebRequest -UseBasicParsing -Uri "$Servidor/kit/Rescatar-AOG.ps1" -OutFile "$Kit\Rescatar-AOG.ps1" -TimeoutSec 120
} catch { Mal "no se pudo bajar Rescatar-AOG.ps1: $($_.Exception.Message)" }

$hayAog = (Test-Path "HKCU:\SOFTWARE\AgOpenGPS") -or
          (Test-Path "$env:USERPROFILE\Documents\AgOpenGPS\Fields") -or
          (Test-Path "$env:USERPROFILE\Documents\AgOpenGPS\Vehicles")
$zipRescate = $null

if (-not $hayAog) { Aviso "no hay AgOpenGPS previo: nada que rescatar" }
else {
    if (Test-Path "$Kit\Rescatar-AOG.ps1") { & "$Kit\Rescatar-AOG.ps1" }
    $zipRescate = (Get-ChildItem "C:\Rescate-PilotX\*.zip" -EA 0 |
                   Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName

    if (-not $zipRescate) {
        Mal "HAY AgOpenGPS pero el rescate NO dejó ningún ZIP."
        Write-Host "  NO se instala nada. Probá a mano y volvé a lanzar:" -ForegroundColor Red
        Write-Host "    $Kit\Rescatar-AOG.ps1 -Buscar" -ForegroundColor Yellow
        Write-Host "    $Kit\Rescatar-AOG.ps1 -Datos D:\AgOpenGPS" -ForegroundColor Yellow
        Avisar "ABORTADO: el rescate de lotes no dejó ZIP" "instalando"
        Stop-Transcript | Out-Null; return
    }

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zr = [System.IO.Compression.ZipFile]::OpenRead($zipRescate)
        try {
            $nF = @($zr.Entries | Where-Object { $_.FullName -match '(^|/)Fields/' }).Count
            $nV = @($zr.Entries | Where-Object { $_.FullName -match '(^|/)Vehicles/' }).Count
        } finally { $zr.Dispose() }
    } catch { $nF = -1; $nV = -1 }

    Ok "rescate: $([IO.Path]::GetFileName($zipRescate))  ($([math]::Round((Get-Item $zipRescate).Length/1MB,1)) MB, $nF archivos de lotes, $nV de perfiles)"
    if ($nF -le 0 -and $nV -le 0) {
        Mal "el ZIP no trae ni lotes ni perfiles: el rescate no sirvió. NO se instala nada."
        Avisar "ABORTADO: el rescate salió vacío" "instalando"
        Stop-Transcript | Out-Null; return
    }

    # Copia en otra unidad: el disco de la pantalla es lo que vamos a reescribir.
    $otra = Get-Volume -EA 0 | Where-Object {
        $_.DriveLetter -and $_.DriveLetter -ne 'C' -and $_.DriveType -in 'Fixed','Removable' -and $_.SizeRemaining -gt 200MB
    } | Select-Object -First 1
    if ($otra) { try { Copy-Item $zipRescate "$($otra.DriveLetter):\" -Force; Ok "copia en $($otra.DriveLetter):\" } catch { } }

    Write-Host ""
    Write-Host "  BAJATE ESTE ZIP A TU PC antes de seguir (transferencia de archivos):" -ForegroundColor Yellow
    Write-Host "     $zipRescate" -ForegroundColor White
    Write-Host ""
    $r = Read-Host "  Escribi BAJADO cuando lo tengas (cualquier otra cosa cancela)"
    if ($r.Trim().ToUpper() -ne "BAJADO") {
        Write-Host "  Cancelado. No se instaló nada; el rescate quedó en C:\Rescate-PilotX." -ForegroundColor Yellow
        Avisar "cancelado por el técnico antes de instalar" "esperando"
        Stop-Transcript | Out-Null; return
    }
}

# ── 3. Runtimes ─────────────────────────────────────────────────────────────
Titulo "3/8  Runtimes"
$sys = Join-Path $env:SystemRoot "System32"
if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) { Ok "VC++ Redistributable presente" }
else {
    Avisar "instalando VC++ Redistributable" "instalando"
    try {
        Invoke-WebRequest -UseBasicParsing "$Servidor/kit/vc_redist.x64.exe" -OutFile "$Kit\vc_redist.x64.exe" -TimeoutSec 900
    } catch {
        try { Invoke-WebRequest -UseBasicParsing "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile "$Kit\vc_redist.x64.exe" -TimeoutSec 900 } catch { }
    }
    if (Test-Path "$Kit\vc_redist.x64.exe") { Start-Process "$Kit\vc_redist.x64.exe" -ArgumentList "/install","/quiet","/norestart" -Wait }
    if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) { Ok "VC++ instalado" }
    else { $problemas += "falta el VC++ Redistributable"; Mal "sigue faltando el VC++" }
}
$wv = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -EA 0).pv
if ($wv) { Ok "WebView2 $wv" }
else {
    try {
        Invoke-WebRequest -UseBasicParsing "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile "$Kit\wv2.exe" -TimeoutSec 600
        Start-Process "$Kit\wv2.exe" -ArgumentList "/silent","/install" -Wait; Ok "WebView2 instalado"
    } catch { Aviso "WebView2 no se pudo instalar" }
}

# ── 4. Paquete ──────────────────────────────────────────────────────────────
# De OrbitX con el token del registro: por túnel, bajarlo del servidor de
# provisioning significaría pasar 194 MB por la sesión de escritorio remoto.
Titulo "4/8  Paquete de PilotX $($reg.version)"
Avisar "bajando PilotX $($reg.version)" "instalando"
$zip = "$Kit\PilotX_v$($reg.version).zip"
$bajado = $false
if ($reg.device_token -and $reg.version) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$($reg.server_url)/api/ota/firmware/PilotX/$($reg.version)" `
            -OutFile $zip -TimeoutSec 3600 `
            -Headers @{ "X-Device-ID" = $reg.device_id; "X-Auth-Token" = $reg.device_token }
        $bajado = (Test-Path $zip) -and ((Get-Item $zip).Length -gt 50MB)
        if ($bajado) { Ok "bajado de OrbitX ($([math]::Round((Get-Item $zip).Length/1MB)) MB)" }
    } catch { Aviso "OrbitX no sirvió el paquete: $($_.Exception.Message)" }
}
if (-not $bajado) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$Servidor$($reg.paquete)" -OutFile $zip -TimeoutSec 3600
        $bajado = (Test-Path $zip) -and ((Get-Item $zip).Length -gt 50MB)
        if ($bajado) { Ok "bajado del servidor ($([math]::Round((Get-Item $zip).Length/1MB)) MB)" }
    } catch { Mal "no se pudo bajar el paquete: $($_.Exception.Message)" }
}
if (-not $bajado) { Avisar "ABORTADO: no se pudo bajar el paquete" "instalando"; Stop-Transcript | Out-Null; return }

# ── 5. Instalación ──────────────────────────────────────────────────────────
Titulo "5/8  Instalación"
Get-Process | Where-Object { $_.ProcessName -match "^PilotX|^AgroParallel|msedgewebview2" } |
    Stop-Process -Force -ErrorAction SilentlyContinue
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $z.Entries) {
            $rel = $e.FullName -replace "/", "\"
            if (-not $rel -or $rel.EndsWith("\")) { continue }
            $dst = Join-Path $Destino $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        }
    } finally { $z.Dispose() }
    Ok "extraído en $Destino"
} catch { Mal "no se pudo extraer: $($_.Exception.Message)"; Stop-Transcript | Out-Null; return }

# Identidad propia + token de ESTE equipo: queda vinculado sin pasos a mano.
@{
    enabled = $true; server_url = $reg.server_url; device_token = $reg.device_token
    master_token = "vx-device-token"; device_id = $reg.device_id; estab_slug = $reg.estab_slug
} | ConvertTo-Json | Out-File "$Destino\Engine\orbitX.json" -Encoding utf8
Ok "vinculado a $($reg.estab_slug)"

# ── 6. Ajustes de cabina ────────────────────────────────────────────────────
Titulo "6/8  Ajustes de cabina"
try {
    Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq "Public" } | ForEach-Object {
        Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
    }
} catch { }
foreach ($x in @(@{p=1883;n="MQTT"}, @{p=5180;n="Hub"}, @{p=5181;n="CoreX"},
                 @{p=8088;n="Firmware OTA"}, @{p=9999;n="PGN UDP";udp=$true}, @{p=5985;n="WinRM"})) {
    $nombre = "PilotX $($x.n) $($x.p)"
    if (-not (Get-NetFirewallRule -DisplayName $nombre -EA 0)) {
        try { New-NetFirewallRule -DisplayName $nombre -Direction Inbound `
                -Protocol $(if ($x.udp) { "UDP" } else { "TCP" }) `
                -LocalPort $x.p -Action Allow -Profile Any -EA Stop | Out-Null } catch { }
    }
}
Ok "red privada y 6 puertos abiertos"
try { Set-TimeZone -Id "Argentina Standard Time"; Start-Service w32time -EA SilentlyContinue
      w32tm /resync /force 2>&1 | Out-Null; Ok "hora: $(Get-Date)" } catch { }
foreach ($t in @("standby-timeout-ac","standby-timeout-dc","hibernate-timeout-ac",
                 "hibernate-timeout-dc","monitor-timeout-ac","monitor-timeout-dc","disk-timeout-ac")) {
    powercfg /change $t 0 2>&1 | Out-Null
}
Ok "no se suspende"
if (-not (Get-LocalUser -Name "soporte" -EA 0)) {
    try {
        New-LocalUser -Name "soporte" -Password (ConvertTo-SecureString $reg.soporte_pass -AsPlainText -Force) `
                      -FullName "soporte" -PasswordNeverExpires -AccountNeverExpires -EA Stop | Out-Null
        foreach ($g in @("Administradores","Administrators")) { try { Add-LocalGroupMember -Group $g -Member "soporte" -EA Stop; break } catch { } }
        Ok "usuario soporte creado"
    } catch { Mal "usuario soporte: $($_.Exception.Message)" }
} else { Aviso "usuario soporte ya existía" }
$ne = $reg.nombre_equipo
if ($ne -and $ne -inotmatch '^pilotx$' -and $env:COMPUTERNAME -ne $ne) {
    Rename-Computer -NewName $ne -Force -EA SilentlyContinue; Ok "equipo -> $ne (al reiniciar)"
}
try { Enable-PSRemoting -Force -SkipNetworkProfileCheck -EA Stop | Out-Null; Ok "WinRM habilitado" } catch { }
try {
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut("$env:PUBLIC\Desktop\PilotX.lnk")
    $lnk.TargetPath = "$Destino\Lanzar-PilotX.bat"; $lnk.WorkingDirectory = $Destino
    $lnk.WindowStyle = 7; $lnk.Description = "PilotX - Agro Parallel"
    if (Test-Path "$Destino\Desktop\PilotX.Desktop.exe") { $lnk.IconLocation = "$Destino\Desktop\PilotX.Desktop.exe,0" }
    $lnk.Save(); Ok "acceso directo en el escritorio"
} catch { }

# ── 7. Devolver los lotes ───────────────────────────────────────────────────
# PilotX los busca en el Documentos DEL USUARIO QUE LO CORRE. Con kiosko eso es
# 'pilotx', no el usuario con el que instalamos. Se copian a los destinos que
# importan — pesan poco y es preferible duplicar que dejar al operario sin nada.
Titulo "7/8  Lotes del cliente"
if (-not $zipRescate) { Aviso "no había lotes que devolver" }
else {
    $tmp = Join-Path $env:TEMP "restaurar-$(Get-Date -f yyyyMMddHHmmss)"
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try { [System.IO.Compression.ZipFile]::ExtractToDirectory($zipRescate, $tmp) }
    catch { Mal "no se pudo abrir el rescate: $($_.Exception.Message)" }

    $origenes = @()
    if ((Test-Path "$tmp\Fields") -or (Test-Path "$tmp\Vehicles")) { $origenes += $tmp }
    $origenes += @(Get-ChildItem $tmp -Directory -Filter "origen*" -EA 0 | ForEach-Object { $_.FullName })

    $destinos = @([Environment]::GetFolderPath("MyDocuments"))
    if (Test-Path "C:\Users\pilotx") { $destinos += "C:\Users\pilotx\Documents" }
    else                             { $destinos += "C:\Users\Default\Documents" }
    $destinos = $destinos | Select-Object -Unique

    $primero = 0
    foreach ($d in $destinos) {
        $base = Join-Path $d "AgOpenGPS"
        foreach ($carpeta in @("Fields", "Vehicles")) {
            $dst = Join-Path $base $carpeta
            New-Item -ItemType Directory -Path $dst -Force | Out-Null
            foreach ($o in $origenes) {
                $src = Join-Path $o $carpeta
                if (-not (Test-Path $src)) { continue }
                foreach ($item in Get-ChildItem $src -EA 0) {
                    $dd = Join-Path $dst $item.Name
                    if (Test-Path $dd) { $dd = Join-Path $dst ($item.BaseName + "~aog" + $item.Extension) }
                    try { Copy-Item $item.FullName $dd -Recurse -Force -EA Stop } catch { }
                }
            }
        }
        $n = @(Get-ChildItem (Join-Path $base "Fields") -Directory -EA 0).Count
        if ($d -eq $destinos[0]) { $primero = $n }
        Ok "$n lotes en $base"
    }
    Remove-Item $tmp -Recurse -Force -EA SilentlyContinue
    if ($primero -eq 0) { $problemas += "no quedó ningún lote restaurado" }
    Write-Host "  Verificá la geometría del perfil contra la máquina real antes de trabajar." -ForegroundColor Yellow
}

# ── 8. Arranque y kiosko ────────────────────────────────────────────────────
Titulo "8/8  Prueba de arranque"
Avisar "probando que PilotX arranque" "instalando"
$eng = "$Destino\Engine\PilotX.GuidanceEngine.exe"
if (-not (Test-Path $eng)) { $problemas += "no está $eng" }
else {
    $pr = $null
    try {
        $pr = Start-Process $eng -ArgumentList "--webhost","--corex" -WorkingDirectory "$Destino\Engine" -PassThru -WindowStyle Hidden
        $arranco = $false
        foreach ($i in 1..20) {
            Start-Sleep -Seconds 2
            try { Invoke-WebRequest "http://127.0.0.1:5180/api/aog/state" -TimeoutSec 3 -UseBasicParsing | Out-Null
                  $arranco = $true; break } catch { }
        }
        if ($arranco) { Ok "PilotX arranca y responde" } else { $problemas += "PilotX no respondió en 40 s" }
    } catch { $problemas += "PilotX no pudo ejecutarse: $($_.Exception.Message)" }
    finally { if ($pr -and -not $pr.HasExited) { Stop-Process -Id $pr.Id -Force -EA SilentlyContinue } }
}

if ($reg.kiosko -eq $false) { Aviso "el pedido es sin kiosko" }
elseif ($problemas.Count -gt 0) {
    Mal "KIOSKO NO ACTIVADO. Motivos:"
    foreach ($p in $problemas) { Write-Host "     - $p" -ForegroundColor Yellow }
    Write-Host "  Windows arranca normal: la pantalla es usable." -ForegroundColor Yellow
    Avisar "kiosko NO activado: $($problemas -join '; ')" "instalando"
}
else {
    try { Invoke-WebRequest -UseBasicParsing "$Servidor/kit/PilotX-KioskSetup.exe" -OutFile "$Kit\PilotX-KioskSetup.exe" -TimeoutSec 300 } catch { }
    $k = if (Test-Path "$Kit\PilotX-KioskSetup.exe") { "$Kit\PilotX-KioskSetup.exe" } else { "$Destino\PilotX-KioskSetup.exe" }
    if (Test-Path $k) {
        Start-Process cmd -ArgumentList "/c `"$k`" /yes < NUL" -Wait -NoNewWindow
        $shell = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon").Shell
        if ($shell -like "*PilotX*") { Ok "kiosko activado" } else { Mal "el kiosko no quedó activado: $shell" }
    } else { Mal "no se encontró PilotX-KioskSetup.exe" }
}

# ── Cierre ──────────────────────────────────────────────────────────────────
Write-Host ""
if ($problemas.Count -eq 0) {
    Write-Host "=== LISTO — $($reg.cliente) ===" -ForegroundColor Green
    Avisar "instalación terminada; falta reiniciar" "instalado"
} else {
    Write-Host "=== TERMINÓ CON PENDIENTES ===" -ForegroundColor Yellow
    foreach ($p in $problemas) { Write-Host "  - $p" -ForegroundColor Yellow }
}
Write-Host ""
Write-Host "  Admin: soporte / $($reg.soporte_pass)    Equipo: $($reg.nombre_equipo)" -ForegroundColor DarkGray
if ($zipRescate) { Write-Host "  Rescate: $zipRescate" -ForegroundColor DarkGray }
Write-Host "  Reiniciá la pantalla." -ForegroundColor Yellow
Stop-Transcript | Out-Null
