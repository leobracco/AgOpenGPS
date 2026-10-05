# ============================================================================
#  Actualizar-Pantalla.ps1 — actualiza una pantalla con el .bin ya copiado,
#  junta los lotes de TODOS los usuarios en un solo lugar y la deja arrancando
#  sola en PilotX.
#
#  Pensado para una pantalla que no tiene acceso a la LAN del taller: no baja
#  nada (salvo los runtimes de Microsoft si faltan). El paquete se copia antes
#  por RustDesk/AnyDesk.
#
#  Uso (PowerShell como ADMINISTRADOR, en la pantalla):
#      .\Actualizar-Pantalla.ps1 -Paquete C:\PilotX_v1.0.85.bin
#
#  Si el .bin quedó en el Escritorio o en Descargas lo encuentra solo:
#      .\Actualizar-Pantalla.ps1
#
#  Opciones:
#      -SinKiosko          deja Windows normal en vez de arrancar en PilotX
#      -DejarUsuarios      no deshabilita las cuentas sobrantes
#      -SoloVer            muestra qué haría y no toca nada
#
#  LO QUE RESUELVE, y es el motivo de este script: PilotX busca los lotes en el
#  "Documentos" DEL USUARIO QUE LO CORRE. En una pantalla con varias cuentas,
#  cada una ve un conjunto distinto y el operario jura que se perdieron. Acá
#  todos los lotes se juntan en C:\PilotX\Datos y se declara esa carpeta como
#  carpeta de trabajo, así PilotX muestra lo mismo entre por donde entre.
# ============================================================================
param(
    [string]$Paquete = "",
    [string]$SoportePass = "Agro2026",
    [switch]$SinKiosko,
    [switch]$DejarUsuarios,
    [switch]$SoloVer
)

$ErrorActionPreference = "Continue"
try { Set-ExecutionPolicy Bypass -Scope Process -Force -ErrorAction Stop } catch { }

$Destino = "C:\PilotX"
$Datos   = "C:\PilotX\Datos"          # carpeta de trabajo COMPARTIDA
$Base    = Join-Path $Datos "AgOpenGPS"

function Titulo($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Ok($t)     { Write-Host "  [OK] $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "  [--] $t" -ForegroundColor DarkGray }
function Mal($t)    { Write-Host "  [!!] $t" -ForegroundColor Red }
$problemas = @()

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { Mal "Hay que correrlo como ADMINISTRADOR."; exit 2 }

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $Destino -Force | Out-Null
Start-Transcript -Path "$Destino\actualizar-log.txt" -Append | Out-Null
Write-Host ""
Write-Host "  Actualizacion de pantalla — $env:COMPUTERNAME   $(Get-Date)" -ForegroundColor White
if ($SoloVer) { Write-Host "  MODO -SoloVer: no se modifica nada" -ForegroundColor Yellow }

# ── 1. Encontrar el paquete ─────────────────────────────────────────────────
Titulo "1/8  Paquete"
if (-not $Paquete) {
    $cand = @("$env:USERPROFILE\Desktop", "$env:USERPROFILE\Downloads", "C:\", "$env:PUBLIC\Desktop") |
            Where-Object { Test-Path $_ } |
            ForEach-Object { Get-ChildItem $_ -File -Include *.bin, PilotX_v*.zip -Depth 0 -EA 0 } |
            Sort-Object LastWriteTime -Descending
    if ($cand) { $Paquete = $cand[0].FullName }
}
if (-not $Paquete -or -not (Test-Path $Paquete)) {
    Mal "no encontre el paquete. Pasalo a mano:  -Paquete C:\ruta\PilotX_v1.0.85.bin"
    Stop-Transcript | Out-Null; exit 3
}
$mb = [math]::Round((Get-Item $Paquete).Length / 1MB)
Ok "$([IO.Path]::GetFileName($Paquete))  ($mb MB)"
if ($mb -lt 50) {
    Mal "el paquete pesa $mb MB: parece incompleto (el completo ronda los 190 MB)"
    Stop-Transcript | Out-Null; exit 4
}
# Que sea un ZIP de verdad y no una descarga cortada.
try { $z = [System.IO.Compression.ZipFile]::OpenRead($Paquete); $nEntradas = $z.Entries.Count; $z.Dispose() }
catch { Mal "el paquete no se puede abrir (descarga incompleta?): $($_.Exception.Message)"; Stop-Transcript | Out-Null; exit 5 }
Ok "$nEntradas archivos adentro"

# ── 2. Rescate de TODOS los perfiles ────────────────────────────────────────
# Antes que nada y sin excepciones. Acá está el trabajo del cliente.
Titulo "2/8  Rescate de lotes y configuracion (todos los usuarios)"
$origenes = @()
foreach ($p in (Get-ChildItem "C:\Users" -Directory -EA 0)) {
    $b = Join-Path $p.FullName "Documents\AgOpenGPS"
    if (-not (Test-Path $b)) { continue }
    $n = @(Get-ChildItem (Join-Path $b "Fields") -Directory -EA 0).Count
    $v = @(Get-ChildItem (Join-Path $b "Vehicles") -File -Filter *.XML -EA 0).Count
    if ($n -eq 0 -and $v -eq 0) { continue }
    $origenes += [pscustomobject]@{ Usuario = $p.Name; Base = $b; Lotes = $n; Perfiles = $v }
    Ok "$($p.Name): $n lotes, $v perfiles de vehiculo"
}
# La carpeta compartida puede existir de una corrida anterior.
if ((Test-Path $Base) -and -not ($origenes | Where-Object { $_.Base -eq $Base })) {
    $n = @(Get-ChildItem (Join-Path $Base "Fields") -Directory -EA 0).Count
    if ($n) { $origenes += [pscustomobject]@{ Usuario = "(compartida)"; Base = $Base; Lotes = $n; Perfiles = 0 }; Ok "carpeta compartida: $n lotes" }
}
# Y la carpeta de trabajo declarada en el registro, que puede estar fuera de Documentos.
$wd = (Get-ItemProperty "HKCU:\SOFTWARE\AgOpenGPS" -EA 0).workingDirectory
if ($wd -and $wd -ne "Default") {
    $b = Join-Path $wd "AgOpenGPS"
    if ((Test-Path $b) -and -not ($origenes | Where-Object { $_.Base -eq $b })) {
        $n = @(Get-ChildItem (Join-Path $b "Fields") -Directory -EA 0).Count
        $origenes += [pscustomobject]@{ Usuario = "(registro: $wd)"; Base = $b; Lotes = $n; Perfiles = 0 }
        Ok "carpeta declarada en el registro: $n lotes"
    }
}

$totalLotes = ($origenes | Measure-Object -Property Lotes -Sum).Sum
if (-not $origenes) { Aviso "no se encontraron lotes en ningun lado" }
else {
    Write-Host "  -> $totalLotes lotes en $($origenes.Count) ubicaciones" -ForegroundColor Cyan
    if (-not $SoloVer) {
        $zip = "C:\Rescate-PilotX\rescate-$env:COMPUTERNAME-$(Get-Date -f yyyyMMdd-HHmm).zip"
        New-Item -ItemType Directory -Path (Split-Path $zip) -Force | Out-Null
        $stage = Join-Path $env:TEMP "rescate-$(Get-Date -f HHmmss)"
        $i = 0
        foreach ($o in $origenes) {
            $i++
            $sub = Join-Path $stage ("origen{0}_{1}" -f $i, ($o.Usuario -replace '[^A-Za-z0-9]', '_'))
            New-Item -ItemType Directory -Path $sub -Force | Out-Null
            Set-Content (Join-Path $sub "_origen.txt") $o.Base -Encoding UTF8
            foreach ($c in @("Fields", "Vehicles")) {
                $src = Join-Path $o.Base $c
                if (Test-Path $src) { Copy-Item $src (Join-Path $sub $c) -Recurse -Force -EA SilentlyContinue }
            }
            foreach ($j in (Get-ChildItem $o.Base -File -Filter *.json -EA 0)) { Copy-Item $j.FullName $sub -Force -EA SilentlyContinue }
        }
        # El dato que nadie puede reconstruir despues.
        Set-Content (Join-Path $stage "_registry-aog.txt") @(
            "WorkingDirectory=$wd"
            "VehicleFileName=$((Get-ItemProperty 'HKCU:\SOFTWARE\AgOpenGPS' -EA 0).vehicleFileName)"
        ) -Encoding UTF8
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
        Remove-Item $stage -Recurse -Force -EA SilentlyContinue
        Ok "rescate: $zip  ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"
        Write-Host ""
        Write-Host "  BAJATE ESE ZIP por AnyDesk antes de seguir." -ForegroundColor Yellow
        $r = Read-Host "  Escribi BAJADO cuando lo tengas (cualquier otra cosa cancela)"
        if ($r.Trim().ToUpper() -ne "BAJADO") {
            Write-Host "  Cancelado. No se instalo nada." -ForegroundColor Yellow
            Stop-Transcript | Out-Null; exit 0
        }
    }
}
if ($SoloVer) { Write-Host ""; Write-Host "  (-SoloVer: hasta aca llega)" -ForegroundColor Yellow; Stop-Transcript | Out-Null; exit 0 }

# ── 3. Instalar ─────────────────────────────────────────────────────────────
Titulo "3/8  Instalacion de PilotX"
Get-Process | Where-Object { $_.ProcessName -match "^PilotX|^AgroParallel|^AgOpenGPS|^AgIO|msedgewebview2" } |
    Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 2
try {
    $z = [System.IO.Compression.ZipFile]::OpenRead($Paquete)
    try {
        foreach ($e in $z.Entries) {
            $rel = $e.FullName -replace "/", "\"
            if (-not $rel -or $rel.EndsWith("\")) { continue }
            $dst = Join-Path $Destino $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        }
    } finally { $z.Dispose() }
    $ver = (Get-Item "$Destino\Desktop\PilotX.Desktop.exe" -EA 0).VersionInfo.FileVersion
    Ok "instalado en $Destino  (version $ver)"
} catch { Mal "no se pudo extraer: $($_.Exception.Message)"; Stop-Transcript | Out-Null; exit 6 }

# ── 4. Runtimes ─────────────────────────────────────────────────────────────
Titulo "4/8  Runtimes"
$sys = Join-Path $env:SystemRoot "System32"
if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) { Ok "VC++ Redistributable presente" }
else {
    Aviso "falta el VC++: intentando bajarlo de Microsoft"
    try {
        $vc = "$env:TEMP\vc_redist.x64.exe"
        Invoke-WebRequest "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vc -UseBasicParsing -TimeoutSec 900
        Start-Process $vc -ArgumentList "/install", "/quiet", "/norestart" -Wait
    } catch { }
    if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) { Ok "VC++ instalado" }
    else { $problemas += "falta el VC++ Redistributable (PilotX no arranca sin el)"; Mal "sigue faltando" }
}

# ── 5. Juntar los lotes en la carpeta compartida ────────────────────────────
# El corazon del asunto. PilotX arma su carpeta de datos asi:
#   working_directory = "Default"  ->  <Documentos del usuario>\AgOpenGPS
#   working_directory = "C:\...\"  ->  <esa ruta>\AgOpenGPS
# Poniendo una ruta absoluta, todos los usuarios ven los MISMOS lotes.
Titulo "5/8  Carpeta de datos compartida"
New-Item -ItemType Directory -Path (Join-Path $Base "Fields"), (Join-Path $Base "Vehicles") -Force | Out-Null

$copiados = 0; $renombrados = 0
foreach ($o in $origenes) {
    if ($o.Base -eq $Base) { continue }
    foreach ($c in @("Fields", "Vehicles")) {
        $src = Join-Path $o.Base $c
        if (-not (Test-Path $src)) { continue }
        foreach ($item in (Get-ChildItem $src -EA 0)) {
            $dst = Join-Path (Join-Path $Base $c) $item.Name
            if (Test-Path $dst) {
                # Mismo nombre: si el contenido es igual no se duplica.
                $igual = $false
                if ($item.PSIsContainer) {
                    $hA = (Get-ChildItem $item.FullName -File -Recurse -EA 0 | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Length)" }) -join "|"
                    $hB = (Get-ChildItem $dst -File -Recurse -EA 0 | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Length)" }) -join "|"
                    $igual = ($hA -eq $hB)
                } else { $igual = ((Get-Item $dst).Length -eq $item.Length) }
                if ($igual) { continue }
                $dst = Join-Path (Join-Path $Base $c) ("{0} ({1}){2}" -f $item.BaseName, $o.Usuario, $item.Extension)
                $renombrados++
            }
            try { Copy-Item $item.FullName $dst -Recurse -Force -EA Stop; $copiados++ } catch { }
        }
    }
}
$nF = @(Get-ChildItem (Join-Path $Base "Fields") -Directory -EA 0).Count
$nV = @(Get-ChildItem (Join-Path $Base "Vehicles") -File -EA 0).Count
Ok "$nF lotes y $nV perfiles en $Base"
if ($renombrados) { Aviso "$renombrados entraron con el nombre del usuario entre parentesis (mismo nombre, distinto contenido)" }
if ($nF -eq 0 -and $totalLotes -gt 0) { $problemas += "no quedo ningun lote en la carpeta compartida" }

# El operario corre PilotX con una cuenta limitada: necesita escribir acá.
# S-1-5-32-545 es el grupo "Usuarios" en cualquier idioma de Windows.
try { icacls $Datos /grant "*S-1-5-32-545:(OI)(CI)M" /T /Q | Out-Null; Ok "permisos de escritura para todos los usuarios" }
catch { Mal "no se pudieron dar permisos sobre $Datos" }

# Declarar la carpeta de trabajo. Se preserva el resto del archivo.
$cfgPath = "$Destino\aog_settings.json"
$cfg = @{}
if (Test-Path $cfgPath) {
    try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $cfg[$_.Name] = $_.Value } } catch { }
    Copy-Item $cfgPath "$cfgPath.antes-migracion" -Force -EA SilentlyContinue
}
$cfg["working_directory"] = $Datos
if (-not $cfg["language"]) { $cfg["language"] = "es" }
# Vehículo activo: el que venía usando el cliente.
$vf = (Get-ItemProperty "HKCU:\SOFTWARE\AgOpenGPS" -EA 0).vehicleFileName
if ($vf -and -not $cfg["vehicle_file_name"]) { $cfg["vehicle_file_name"] = $vf }
$cfg | ConvertTo-Json -Depth 6 | Out-File $cfgPath -Encoding utf8
Ok "carpeta de trabajo declarada: $Datos"
if ($cfg["vehicle_file_name"]) { Ok "vehiculo activo: $($cfg['vehicle_file_name'])" }

# ── 6. Usuarios ─────────────────────────────────────────────────────────────
Titulo "6/8  Usuarios de Windows"
# Windows no permite una cuenta local con el nombre del equipo.
if ($env:COMPUTERNAME -ieq "pilotx") {
    $problemas += "el equipo se llama PILOTX, igual que el usuario del operario: renombralo"
    Mal "el equipo se llama PILOTX — hay que renombrarlo antes del kiosko"
}
function CrearUsuario($nombre, $pass, $grupos) {
    if (Get-LocalUser -Name $nombre -EA 0) { Aviso "'$nombre' ya existia"; return $true }
    try {
        if ($pass) {
            New-LocalUser -Name $nombre -Password (ConvertTo-SecureString $pass -AsPlainText -Force) `
                          -FullName $nombre -PasswordNeverExpires -AccountNeverExpires -EA Stop | Out-Null
        } else {
            # -NoPassword y -PasswordNeverExpires son INCOMPATIBLES.
            New-LocalUser -Name $nombre -NoPassword -FullName $nombre -AccountNeverExpires -EA Stop | Out-Null
            try { Set-LocalUser -Name $nombre -PasswordNeverExpires $true } catch { }
        }
    } catch { Mal "'$nombre': $($_.Exception.Message)"; return $false }
    foreach ($g in $grupos) { try { Add-LocalGroupMember -Group $g -Member $nombre -EA Stop | Out-Null; break } catch { } }
    Ok "'$nombre' creado"
    return $true
}
$okSoporte = CrearUsuario "soporte" $SoportePass @("Administradores", "Administrators")
$okPilotx  = $true
if (-not $SinKiosko) { $okPilotx = CrearUsuario "pilotx" $null @("Usuarios", "Users") }
if (-not $okPilotx) { $problemas += "no se pudo crear el usuario 'pilotx'" }

# Las cuentas sobrantes se DESHABILITAN, no se borran: borrar un perfil se
# lleva sus documentos, y los datos ya estan copiados pero no hay vuelta atras.
if (-not $DejarUsuarios) {
    $intocables = @("pilotx", "soporte", "Administrador", "Administrator", "DefaultAccount",
                    "WDAGUtilityAccount", "Invitado", "Guest", $env:USERNAME)
    foreach ($u in (Get-LocalUser -EA 0 | Where-Object Enabled)) {
        if ($intocables -contains $u.Name) { continue }
        try { Disable-LocalUser -Name $u.Name -EA Stop; Ok "'$($u.Name)' deshabilitado (no borrado: sus archivos quedan)" }
        catch { Mal "no se pudo deshabilitar '$($u.Name)'" }
    }
}

# ── 7. Ajustes de cabina y prueba de arranque ───────────────────────────────
Titulo "7/8  Ajustes y prueba"
try {
    Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq "Public" } |
        ForEach-Object { Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private }
} catch { }
foreach ($x in @(@{p=1883;n="MQTT"}, @{p=5180;n="Hub"}, @{p=5181;n="CoreX"},
                 @{p=8088;n="Firmware OTA"}, @{p=9999;n="PGN UDP";udp=$true}, @{p=5985;n="WinRM"})) {
    $nom = "PilotX $($x.n) $($x.p)"
    if (-not (Get-NetFirewallRule -DisplayName $nom -EA 0)) {
        try { New-NetFirewallRule -DisplayName $nom -Direction Inbound `
                -Protocol $(if ($x.udp) { "UDP" } else { "TCP" }) `
                -LocalPort $x.p -Action Allow -Profile Any -EA Stop | Out-Null } catch { }
    }
}
Ok "red privada y puertos de PilotX abiertos"
try { Set-TimeZone -Id "Argentina Standard Time"; Start-Service w32time -EA SilentlyContinue; w32tm /resync /force 2>&1 | Out-Null } catch { }
foreach ($t in @("standby-timeout-ac","standby-timeout-dc","hibernate-timeout-ac",
                 "hibernate-timeout-dc","monitor-timeout-ac","monitor-timeout-dc","disk-timeout-ac")) {
    powercfg /change $t 0 2>&1 | Out-Null
}
Ok "hora de Argentina y sin suspension"
try {
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut("$env:PUBLIC\Desktop\PilotX.lnk")
    $lnk.TargetPath = "$Destino\Lanzar-PilotX.bat"; $lnk.WorkingDirectory = $Destino
    $lnk.WindowStyle = 7; $lnk.Description = "PilotX - Agro Parallel"
    if (Test-Path "$Destino\Desktop\PilotX.Desktop.exe") { $lnk.IconLocation = "$Destino\Desktop\PilotX.Desktop.exe,0" }
    $lnk.Save(); Ok "acceso directo en el escritorio"
} catch { }

# La prueba de fuego: si el motor no responde, el kiosko no se toca.
$eng = "$Destino\Engine\PilotX.GuidanceEngine.exe"
if (-not (Test-Path $eng)) { $problemas += "no esta $eng" }
else {
    $pr = $null
    try {
        $pr = Start-Process $eng -ArgumentList "--webhost", "--corex" -WorkingDirectory "$Destino\Engine" -PassThru -WindowStyle Hidden
        $arranco = $false
        foreach ($i in 1..20) {
            Start-Sleep -Seconds 2
            try { Invoke-WebRequest "http://127.0.0.1:5180/api/aog/state" -TimeoutSec 3 -UseBasicParsing | Out-Null; $arranco = $true; break } catch { }
        }
        if ($arranco) { Ok "PilotX arranca y responde" } else { $problemas += "PilotX no respondio en 40 s" }
    } catch { $problemas += "PilotX no pudo ejecutarse: $($_.Exception.Message)" }
    finally { if ($pr -and -not $pr.HasExited) { Stop-Process -Id $pr.Id -Force -EA SilentlyContinue } }
}

# ── 8. Kiosko ───────────────────────────────────────────────────────────────
Titulo "8/8  Arranque automatico"
if ($SinKiosko) { Aviso "pedido sin kiosko: Windows arranca normal" }
elseif ($problemas.Count -gt 0) {
    Mal "NO se activa el kiosko. Motivos:"
    foreach ($p in $problemas) { Write-Host "     - $p" -ForegroundColor Yellow }
    Write-Host "  Windows arranca normal: la pantalla es usable y se arregla desde aca." -ForegroundColor Yellow
}
else {
    $k = "$Destino\PilotX-KioskSetup.exe"
    if (Test-Path $k) {
        Start-Process cmd -ArgumentList "/c `"$k`" /yes < NUL" -Wait -NoNewWindow
    } else {
        # Sin el instalador al lado, se escribe a mano lo mismo que hace él.
        $wl = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
        Set-ItemProperty $wl -Name "Shell" -Value "$Destino\Desktop\PilotX.Desktop.exe" -Type String -Force
        Set-ItemProperty $wl -Name "AutoAdminLogon" -Value "1" -Type String -Force
        Set-ItemProperty $wl -Name "DefaultUserName" -Value "pilotx" -Type String -Force
        Set-ItemProperty $wl -Name "DefaultPassword" -Value "" -Type String -Force
        Set-ItemProperty $wl -Name "DefaultDomainName" -Value $env:COMPUTERNAME -Type String -Force
    }
    $shell = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon").Shell
    if ($shell -like "*PilotX*") { Ok "la pantalla va a arrancar sola en PilotX" }
    else { Mal "el kiosko no quedo activado (Shell = $shell)" }
}

# ── Cierre ──────────────────────────────────────────────────────────────────
Write-Host ""
if ($problemas.Count -eq 0) { Write-Host "=== LISTO ===" -ForegroundColor Green }
else {
    Write-Host "=== TERMINO CON PENDIENTES ===" -ForegroundColor Yellow
    foreach ($p in $problemas) { Write-Host "  - $p" -ForegroundColor Yellow }
}
Write-Host ""
Write-Host "  Lotes:   $Base\Fields   ($nF lotes, los ve cualquier usuario)" -ForegroundColor DarkGray
Write-Host "  Admin:   soporte / $SoportePass" -ForegroundColor DarkGray
Write-Host "  Rescate: C:\Rescate-PilotX" -ForegroundColor DarkGray
Write-Host "  Reinicia la pantalla para comprobarlo." -ForegroundColor Yellow
Stop-Transcript | Out-Null
exit $(if ($problemas.Count -gt 0) { 1 } else { 0 })
