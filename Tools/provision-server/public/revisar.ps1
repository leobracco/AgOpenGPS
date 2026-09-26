# ============================================================================
#  revisar.ps1 — radiografía de una pantalla antes de tocarla. NO MODIFICA NADA.
#
#  Para qué: antes de migrar hay que saber qué hay. En particular cuando la
#  pantalla tiene VARIOS usuarios de Windows: PilotX y AgOpenGPS buscan los
#  lotes en el "Documentos" DEL USUARIO QUE LOS CORRE, asi que cada uno ve un
#  conjunto distinto y el operario jura que "se perdieron los lotes" cuando en
#  realidad entro con otra cuenta.
#
#  Se corre EN LA PANTALLA:
#      irm <IP>:8090/revisar.ps1|iex
#
#  Solo lee. Se puede correr con la máquina trabajando.
# ============================================================================
$ErrorActionPreference = "Continue"
try { Set-ExecutionPolicy Bypass -Scope Process -Force -ErrorAction Stop } catch { }

function Titulo($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Dato($k, $v) { Write-Host ("  {0,-26} {1}" -f $k, $v) }
function Aviso($t) { Write-Host "  $t" -ForegroundColor Yellow }

Write-Host ""
Write-Host "  RADIOGRAFIA DE LA PANTALLA — no se modifica nada" -ForegroundColor White
Write-Host "  $env:COMPUTERNAME   $(Get-Date)" -ForegroundColor DarkGray

# ── Equipo ──────────────────────────────────────────────────────────────────
Titulo "Equipo"
$os = Get-CimInstance Win32_OperatingSystem
Dato "Nombre" $env:COMPUTERNAME
Dato "Windows" "$($os.Caption) ($($os.OSArchitecture))"
Dato "RAM" ("{0:N1} GB" -f ($os.TotalVisibleMemorySize / 1MB))
Dato "Disco C: libre" ("{0:N1} GB de {1:N1} GB" -f ((Get-PSDrive C).Free / 1GB), (((Get-PSDrive C).Free + (Get-PSDrive C).Used) / 1GB))
Dato "Zona horaria" (Get-TimeZone).Id
$scr = Get-CimInstance Win32_VideoController | Select-Object -First 1
if ($scr) { Dato "Resolucion" "$($scr.CurrentHorizontalResolution)x$($scr.CurrentVerticalResolution)" }

# ── Software de guiado instalado ────────────────────────────────────────────
Titulo "Software de guiado"
foreach ($ruta in @("C:\PilotX\Desktop\PilotX.Desktop.exe", "C:\PilotX\Engine\PilotX.GuidanceEngine.exe",
                    "C:\AgroParallel\AgOpenGPS.exe", "C:\AgroParallel\PilotX.exe", "C:\AgroParallel\AgIO.exe")) {
    if (Test-Path $ruta) {
        $v = (Get-Item $ruta).VersionInfo.FileVersion
        Dato ([IO.Path]::GetFileName($ruta)) "$v   ($ruta)"
    }
}
$sys = Join-Path $env:SystemRoot "System32"
Dato "VC++ Redistributable" $(if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) { "instalado" } else { "FALTA (PilotX no arranca sin esto)" })
$wv = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -EA 0).pv
Dato "WebView2" $(if ($wv) { $wv } else { "FALTA" })

# ── EL PUNTO: los datos de cada usuario ─────────────────────────────────────
# Acá se ve por qué el operario "pierde" los lotes: están repartidos por perfil.
Titulo "Lotes y perfiles, USUARIO POR USUARIO"
$hayEnVarios = 0
foreach ($perfil in Get-ChildItem "C:\Users" -Directory -EA 0) {
    $base = Join-Path $perfil.FullName "Documents\AgOpenGPS"
    if (-not (Test-Path $base)) { continue }
    $fields   = Join-Path $base "Fields"
    $vehicles = Join-Path $base "Vehicles"
    $nL = @(Get-ChildItem $fields -Directory -EA 0).Count
    $nV = @(Get-ChildItem $vehicles -File -Filter *.XML -EA 0).Count
    if ($nL -eq 0 -and $nV -eq 0) { continue }
    $hayEnVarios++
    $ult = (Get-ChildItem $fields -Recurse -File -EA 0 | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    Write-Host ""
    Write-Host "  >> $($perfil.Name)" -ForegroundColor Green
    Dato "   lotes" $nL
    Dato "   perfiles de vehiculo" $nV
    Dato "   ultimo trabajo" $(if ($ult) { "{0}  ({1})" -f $ult.LastWriteTime, $ult.Directory.Name } else { "-" })
    Dato "   carpeta" $base
    # Duplicados de una restauracion previa.
    $dup = @(Get-ChildItem $fields -Directory -EA 0 | Where-Object { $_.Name -match '~aog' }).Count
    if ($dup) { Aviso "   $dup lotes con sufijo ~aog (restauracion corrida mas de una vez)" }
}
if ($hayEnVarios -eq 0) { Aviso "  no se encontraron lotes en ningun perfil de C:\Users" }
elseif ($hayEnVarios -gt 1) {
    Write-Host ""
    Aviso "  OJO: hay datos en $hayEnVarios usuarios distintos."
    Aviso "  PilotX muestra SOLO los del usuario con el que se abre."
}

# Carpeta de trabajo declarada, que puede apuntar afuera de Documentos.
Titulo "Carpeta de trabajo declarada"
$wdAog = (Get-ItemProperty "HKCU:\SOFTWARE\AgOpenGPS" -EA 0).workingDirectory
Dato "AgOpenGPS (registro, este usuario)" $(if ($wdAog) { $wdAog } else { "(no declarada)" })
if (Test-Path "C:\PilotX\aog_settings.json") {
    try {
        $s = Get-Content "C:\PilotX\aog_settings.json" -Raw | ConvertFrom-Json
        Dato "PilotX (aog_settings.json)" $(if ($s.working_directory) { $s.working_directory } else { "(vacio)" })
        Dato "PilotX vehiculo activo" $(if ($s.vehicle_file_name) { $s.vehicle_file_name } else { "-" })
    } catch { Aviso "aog_settings.json ilegible" }
}
# Lotes fuera de los perfiles (discos externos, carpetas propias).
$otros = @()
foreach ($d in (Get-PSDrive -PSProvider FileSystem -EA 0 | Where-Object { $_.Used -gt 0 })) {
    foreach ($c in @("AgOpenGPS\Fields", "Documents\AgOpenGPS\Fields")) {
        $p = Join-Path $d.Root $c
        if ((Test-Path $p) -and ($p -notlike "C:\Users\*")) { $otros += $p }
    }
}
if ($otros) { Write-Host ""; Aviso "  tambien hay lotes fuera de los perfiles:"; $otros | ForEach-Object { Write-Host "     $_" -ForegroundColor Yellow } }

# ── Usuarios de Windows ─────────────────────────────────────────────────────
Titulo "Usuarios de Windows"
foreach ($u in (Get-LocalUser -EA 0 | Where-Object Enabled)) {
    $perfil = "C:\Users\$($u.Name)"
    $admin = $false
    foreach ($g in @("Administradores", "Administrators")) {
        try { if (Get-LocalGroupMember -Group $g -Member $u.Name -EA Stop) { $admin = $true } } catch { }
    }
    Dato "  $($u.Name)" ("{0}{1}{2}" -f $(if ($admin) { "administrador" } else { "limitado" }),
                                        $(if (Test-Path $perfil) { ", con perfil" } else { ", sin perfil" }),
                                        $(if ($u.LastLogon) { ", ultimo ingreso $($u.LastLogon)" } else { "" }))
}

# ── Arranque ────────────────────────────────────────────────────────────────
Titulo "Que arranca solo"
$wl = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -EA 0
Dato "Shell de Windows" $(if ($wl.Shell) { $wl.Shell } else { "explorer.exe (normal)" })
Dato "Inicio automatico" $(if ($wl.AutoAdminLogon -eq "1") { "SI, como '$($wl.DefaultUserName)'" } else { "no" })
foreach ($k in @("HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")) {
    if (-not (Test-Path $k)) { continue }
    $p = Get-ItemProperty $k
    foreach ($n in $p.PSObject.Properties.Name) {
        if ($n -like "PS*") { continue }
        if ("$($p.$n)" -match 'agopengps|agroparallel|pilotx|agio') { Dato "  Run: $n" $p.$n }
    }
}
foreach ($dir in @([Environment]::GetFolderPath("Startup"), [Environment]::GetFolderPath("CommonStartup"))) {
    foreach ($f in (Get-ChildItem $dir -File -EA 0)) {
        if ($f.Name -match 'agopengps|agro|pilotx|agio') { Dato "  Inicio: $($f.Name)" $dir }
    }
}

# ── Red y conectividad ──────────────────────────────────────────────────────
Titulo "Red"
foreach ($n in (Get-NetConnectionProfile -EA 0)) { Dato "  $($n.Name)" "perfil $($n.NetworkCategory)" }
Get-NetIPAddress -AddressFamily IPv4 -EA 0 |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
    ForEach-Object { Dato "  IP" "$($_.IPAddress)  ($($_.InterfaceAlias))" }
Dato "Internet" $(if (Test-NetConnection orbitx.agroparallel.com -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue) { "llega a OrbitX" } else { "NO llega a OrbitX" })
Dato "RustDesk" $(if (Get-Service RustDesk -EA 0) { (Get-Service RustDesk).Status } else { "no instalado" })

Write-Host ""
Write-Host "=== FIN — no se modifico nada ===" -ForegroundColor Green
Write-Host "  Pasale esta salida al soporte para decidir como migrar." -ForegroundColor DarkGray
Write-Host ""
