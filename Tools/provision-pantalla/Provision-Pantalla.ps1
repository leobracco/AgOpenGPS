# Provision-Pantalla.ps1
# ============================================================================
# Aprovisionamiento de una pantalla nueva de tractor para PilotX.
# Se corre EN LA PANTALLA, como Administrador, desde el USB del kit.
#
#   powershell -ExecutionPolicy Bypass -File .\Provision-Pantalla.ps1 `
#       -Cliente "LAS GRINGAS" -Cuit "33716946539"
#
# Modos:
#   -SoloRevisar   solo genera el inventario (no toca nada)
#   -SinKiosko     no ofrece activar el modo kiosko al final
#
# Qué hace (en orden):
#   1. Inventario completo del equipo -> C:\PilotX\provision-report.txt
#   2. Limpieza: apps preinstaladas, OneDrive, publicidad, widgets, Copilot
#   3. Usuarios locales: 'pilotx' (operario, sin pass) + 'soporte' (admin)
#      -> NUNCA hace falta cuenta Microsoft
#   4. Energía: alto rendimiento, nunca suspender, sin hibernación
#   5. Imagen de marca: fondo de pantalla Agro Parallel (política, todos los
#      usuarios) + datos de soporte en Configuración
#   6. Red: perfil Privado + reglas de firewall para PilotX
#   7. Runtimes: VC++ 2015-2022 x64 y WebView2 (si vienen en el kit)
#   8. WinRM habilitado para soporte remoto en LAN
#   9. cliente.json con los datos del cliente
#  10. Renombra el equipo y (opcional) activa modo kiosko
# ============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Cliente,
    [string]$Cuit = "",
    [string]$NombreEquipo = "",
    [string]$SoportePass = "",     # clave del admin 'soporte'; vacio = se pide por consola
    [switch]$SoloRevisar,
    [switch]$SinKiosko
)

$ErrorActionPreference = "Continue"
$kit = Split-Path -Parent $MyInvocation.MyCommand.Path

function Titulo($t) { Write-Host "`n=== $t ===" -ForegroundColor Green }
function Paso($t)   { Write-Host ">> $t" -ForegroundColor Cyan }
function Aviso($t)  { Write-Host "AVISO: $t" -ForegroundColor Yellow }
# OJO: NUNCA New-Item -Force sobre una clave de registro existente — la RECREA
# y BORRA todos sus valores (nos comió LocalAccountTokenFilterPolicy en la
# primera pantalla y perdimos el acceso remoto a mitad del provisioning).
function AsegurarClave($path) { if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null } }

# ── Chequeo admin ───────────────────────────────────────────────────────────
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { Write-Host "Ejecutar como ADMINISTRADOR." -ForegroundColor Red; exit 2 }

if (-not (Test-Path "C:\PilotX")) { New-Item -ItemType Directory -Path "C:\PilotX" -Force | Out-Null }
$reporte = "C:\PilotX\provision-report.txt"
Start-Transcript -Path "C:\PilotX\provision-log.txt" -Append | Out-Null

# ============================================================================
# 1. INVENTARIO — "revisar todo lo que tiene"
# ============================================================================
Titulo "1/10 Inventario del equipo"
$os  = Get-CimInstance Win32_OperatingSystem
$cs  = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor
$dsk = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3"
$res = Get-CimInstance Win32_VideoController | Select-Object -First 1

$inv = @()
$inv += "REPORTE DE APROVISIONAMIENTO — $Cliente $(if($Cuit){"(CUIT $Cuit)"})"
$inv += "Fecha: $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
$inv += "-" * 70
$inv += "Equipo:     $($cs.Name)  ($($cs.Manufacturer) $($cs.Model))"
$inv += "Windows:    $($os.Caption) build $($os.BuildNumber)"
$inv += "CPU:        $($cpu.Name)"
$inv += "RAM:        {0:N1} GB" -f ($cs.TotalPhysicalMemory / 1GB)
foreach ($d in $dsk) { $inv += "Disco $($d.DeviceID)   {0:N0} GB libres de {1:N0} GB" -f ($d.FreeSpace/1GB), ($d.Size/1GB) }
$inv += "Pantalla:   $($res.CurrentHorizontalResolution)x$($res.CurrentVerticalResolution)"
$inv += ""
$inv += "── Programas instalados (Win32) ──"
$paths = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
         "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
$progs = Get-ItemProperty $paths -ErrorAction SilentlyContinue |
         Where-Object { $_.DisplayName } |
         Sort-Object DisplayName | Select-Object -Unique DisplayName, DisplayVersion
foreach ($p in $progs) { $inv += "  $($p.DisplayName)  $($p.DisplayVersion)" }
$inv += ""
$inv += "── Apps de Store (Appx) ──"
Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Sort-Object Name |
    ForEach-Object { $inv += "  $($_.Name)" }
$inv += ""
$inv += "── Arranque automático ──"
Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue |
    ForEach-Object { $inv += "  [$($_.Location)] $($_.Name) = $($_.Command)" }
$inv += ""
$inv += "── Usuarios locales ──"
Get-LocalUser | ForEach-Object { $inv += "  $($_.Name)  habilitado=$($_.Enabled)" }
$inv | Out-File $reporte -Encoding utf8
Paso "Inventario guardado en $reporte"

if ($SoloRevisar) {
    Aviso "Modo -SoloRevisar: no se modificó nada. Revisá el reporte y volvé a correr sin el flag."
    Stop-Transcript | Out-Null
    exit 0
}

# ============================================================================
# 2. LIMPIEZA — sacar todo lo que no sirve en un tractor
# ============================================================================
Titulo "2/10 Limpieza de software preinstalado"
# Bloat típico de Windows 10/11. Nada de esto sirve en la cabina.
$bloat = @(
    "Microsoft.3DBuilder", "Microsoft.Microsoft3DViewer", "Microsoft.MSPaint",
    "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance", "Microsoft.BingSports",
    "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.WindowsFeedbackHub",
    "Microsoft.MicrosoftOfficeHub", "Microsoft.Office.OneNote", "Microsoft.MicrosoftSolitaireCollection",
    "Microsoft.MixedReality.Portal", "Microsoft.People", "Microsoft.SkypeApp",
    "Microsoft.WindowsMaps", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo",
    "Microsoft.Xbox.TCUI", "Microsoft.XboxApp", "Microsoft.XboxGameOverlay",
    "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay",
    "Microsoft.GamingApp", "Microsoft.YourPhone", "Microsoft.WindowsCommunicationsApps",
    "Microsoft.Todos", "Microsoft.PowerAutomateDesktop", "MicrosoftTeams", "MSTeams",
    "Microsoft.549981C3F5F10", "Microsoft.Windows.DevHome", "Microsoft.OutlookForWindows",
    "Clipchamp.Clipchamp", "Microsoft.WindowsAlarms", "Microsoft.windowscommunicationsapps",
    "Microsoft.QuickAssist", "Microsoft.Copilot", "Microsoft.Windows.Copilot",
    "7EE7776C.LinkedInforWindows", "SpotifyAB.SpotifyMusic", "Disney.37853FC22B2CE",
    "Netflix", "king.com.CandyCrushSaga", "king.com.CandyCrushSodaSaga",
    "Facebook.Facebook", "BytedancePte.Ltd.TikTok", "AmazonVideo.PrimeVideo"
)
foreach ($app in $bloat) {
    # Instaladas para usuarios actuales
    Get-AppxPackage -AllUsers -Name $app -ErrorAction SilentlyContinue |
        Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
    # Aprovisionadas (así el usuario 'pilotx' nuevo no las hereda)
    Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq $app } |
        Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null
}
Paso "Apps de Store innecesarias eliminadas"

# OneDrive fuera
$od = "$env:SystemRoot\SysWOW64\OneDriveSetup.exe"
if (-not (Test-Path $od)) { $od = "$env:SystemRoot\System32\OneDriveSetup.exe" }
if (Test-Path $od) {
    Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue
    Start-Process $od "/uninstall" -Wait -ErrorAction SilentlyContinue
    Paso "OneDrive desinstalado"
}

# Publicidad / sugerencias / contenido de consumo
$cdm = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent"
AsegurarClave $cdm
Set-ItemProperty $cdm "DisableWindowsConsumerFeatures" 1 -Type DWord
Set-ItemProperty $cdm "DisableSoftLanding" 1 -Type DWord
Set-ItemProperty $cdm "DisableCloudOptimizedContent" 1 -Type DWord

# Copilot y Widgets fuera (políticas)
$cop = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot"
AsegurarClave $cop
Set-ItemProperty $cop "TurnOffWindowsCopilot" 1 -Type DWord
$dsh = "HKLM:\SOFTWARE\Policies\Microsoft\Dsh"
AsegurarClave $dsh
Set-ItemProperty $dsh "AllowNewsAndInterests" 0 -Type DWord

# Telemetría al mínimo que permite la edición
$dc = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection"
AsegurarClave $dc
Set-ItemProperty $dc "AllowTelemetry" 0 -Type DWord

# Sin login con cuenta Microsoft para usuarios nuevos (bloquea el nag)
$sys = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
Set-ItemProperty $sys "NoConnectedUser" 3 -Type DWord -ErrorAction SilentlyContinue
Paso "Publicidad, Copilot, widgets, telemetría y nag de cuenta Microsoft desactivados"

# ============================================================================
# 3. USUARIOS LOCALES — sin cuenta Microsoft
# ============================================================================
Titulo "3/10 Usuarios locales"
# 'pilotx': el operario. Sin contraseña, entra solo (autologon lo pone el kiosko).
if (-not (Get-LocalUser -Name "pilotx" -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name "pilotx" -NoPassword -FullName "Operario PilotX" `
        -Description "Usuario del operario — $Cliente" | Out-Null
    Set-LocalUser -Name "pilotx" -PasswordNeverExpires $true -UserMayChangePassword $false
    Add-LocalGroupMember -Group "Users" -Member "pilotx" -ErrorAction SilentlyContinue
    Paso "Usuario 'pilotx' (operario, sin contraseña) creado"
} else { Paso "Usuario 'pilotx' ya existía" }

# 'soporte': admin local para nosotros. Contraseña se pide en el momento
# (no viaja hardcodeada en el script, ver commit 7e42e851).
# Si viene -SoportePass (instalador LAN: la genera y la guarda por equipo en
# la web del taller) no se pregunta nada; si el usuario ya existia, se le
# pone esa clave para que la registrada sea la que vale.
if ($SoportePass) { $pass = ConvertTo-SecureString $SoportePass -AsPlainText -Force }
if (-not (Get-LocalUser -Name "soporte" -ErrorAction SilentlyContinue)) {
    if (-not $SoportePass) { $pass = Read-Host "Contraseña para el usuario admin 'soporte'" -AsSecureString }
    New-LocalUser -Name "soporte" -Password $pass -FullName "Soporte Agro Parallel" `
        -PasswordNeverExpires -Description "Admin de mantenimiento Agro Parallel" | Out-Null
    Add-LocalGroupMember -Group "Administrators" -Member "soporte" -ErrorAction SilentlyContinue
    Add-LocalGroupMember -Group "Administradores" -Member "soporte" -ErrorAction SilentlyContinue
    Paso "Usuario 'soporte' (admin) creado"
} elseif ($SoportePass) {
    Set-LocalUser -Name "soporte" -Password $pass
    Paso "Usuario 'soporte' ya existía: clave actualizada a la registrada en el instalador"
} else { Paso "Usuario 'soporte' ya existía" }

# ============================================================================
# 4. ENERGÍA — la pantalla del tractor no se duerme
# ============================================================================
Titulo "4/10 Energía"
powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null  # Alto rendimiento
powercfg /change standby-timeout-ac 0
powercfg /change standby-timeout-dc 0
powercfg /change monitor-timeout-ac 0
powercfg /change monitor-timeout-dc 0
powercfg /change hibernate-timeout-ac 0
powercfg /change hibernate-timeout-dc 0
powercfg /hibernate off
Paso "Nunca suspende, monitor siempre encendido, hibernación deshabilitada"

# ============================================================================
# 5. IMAGEN DE MARCA
# ============================================================================
Titulo "5/10 Imagen de marca"
$brandSrc = Join-Path $kit "Branding"
$brandDst = "C:\PilotX\Branding"
if (Test-Path $brandSrc) {
    Copy-Item $brandSrc $brandDst -Recurse -Force
    $fondo = @("$brandDst\fondo.png", "$brandDst\logo.png") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($fondo) {
        # Política: fondo forzado para TODOS los usuarios (incluido pilotx)
        AsegurarClave $sys
        Set-ItemProperty $sys "Wallpaper" $fondo -Type String
        Set-ItemProperty $sys "WallpaperStyle" "10" -Type String   # 10 = rellenar
        Paso "Fondo de pantalla: $fondo"
    }
} else {
    Aviso "No hay carpeta Branding\ en el kit — copiá Build\Branding\ del repo al USB"
}
# Datos de soporte visibles en Configuración > Sistema
$oem = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation"
AsegurarClave $oem
Set-ItemProperty $oem "Manufacturer" "Agro Parallel"
Set-ItemProperty $oem "Model" "Pantalla PilotX — $Cliente"
Set-ItemProperty $oem "SupportURL" "https://orbitx.agroparallel.com"
if (Test-Path "$brandDst\logo-fondo-blanco.png") {
    Set-ItemProperty $oem "Logo" "$brandDst\logo-fondo-blanco.png"
}
Paso "Datos de Agro Parallel en Configuración del sistema"

# ============================================================================
# 6. RED — perfil privado + firewall para PilotX
# ============================================================================
Titulo "6/10 Red y firewall"
# Red Pública mata los módulos UDP (regla de la casa). Todo a Privado.
Get-NetConnectionProfile | ForEach-Object {
    Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
}
Paso "Perfiles de red en Privado"
# Reglas por programa (cubren UDP módulos, MQTT 1883, HTTP 5180)
$exes = @("C:\PilotX\Desktop\PilotX.Desktop.exe", "C:\PilotX\Engine\PilotX.GuidanceEngine.exe")
foreach ($exe in $exes) {
    if (Test-Path $exe) {
        $nombre = "PilotX - $([IO.Path]::GetFileNameWithoutExtension($exe))"
        Remove-NetFirewallRule -DisplayName $nombre -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $nombre -Direction Inbound -Program $exe `
            -Action Allow -Profile Private,Domain | Out-Null
        Paso "Regla de firewall: $nombre"
    } else {
        Aviso "$exe no existe todavía — instalá PilotX y volvé a correr esta sección"
    }
}

# ============================================================================
# 7. RUNTIMES — VC++ y WebView2 (trampa conocida del 0x80070490)
# ============================================================================
Titulo "7/10 Runtimes"
if (-not (Test-Path "C:\Windows\System32\vcruntime140.dll")) {
    $vc = Join-Path $kit "vc_redist.x64.exe"
    if (Test-Path $vc) {
        Start-Process $vc "/install /quiet /norestart" -Wait
        Paso "VC++ Redistributable instalado"
    } else {
        Aviso "FALTA vcruntime140.dll y el kit no trae vc_redist.x64.exe (bajarlo de aka.ms/vs/17/release/vc_redist.x64.exe). Sin esto WebView2 falla con 0x80070490."
    }
} else { Paso "VC++ Redistributable: OK" }

$wv2 = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue
if (-not $wv2) {
    $boot = Join-Path $kit "MicrosoftEdgeWebview2Setup.exe"
    if (Test-Path $boot) {
        Start-Process $boot "/silent /install" -Wait
        Paso "WebView2 Runtime instalado"
    } else {
        Aviso "WebView2 Runtime no detectado y el kit no trae el bootstrapper"
    }
} else { Paso "WebView2 Runtime: OK ($($wv2.pv))" }

# ============================================================================
# 8. SOPORTE REMOTO — WinRM en LAN
# ============================================================================
Titulo "8/10 Soporte remoto"
# Si ya estamos entrando POR WinRM (provisioning remoto), no tocarlo:
# Enable-PSRemoting reinicia el servicio y nos corta la sesión a mitad de script.
$yaRemoto = ($env:PSSenderInfo -ne $null) -or ((Get-Service WinRM -ErrorAction SilentlyContinue).Status -eq 'Running')
if ($yaRemoto) {
    Paso "WinRM ya estaba activo — no se toca"
} else {
    try {
        Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
        Paso "WinRM habilitado (puerto 5985, solo LAN)"
    } catch { Aviso "No pude habilitar WinRM: $($_.Exception.Message)" }
}
# Sin esto, el UAC remoto filtra el token de los admins locales y WinRM da
# "Acceso denegado" aunque la contraseña esté bien.
Set-ItemProperty $sys "LocalAccountTokenFilterPolicy" 1 -Type DWord
Paso "LocalAccountTokenFilterPolicy=1 (acceso admin remoto para cuentas locales)"
Aviso "RustDesk: instalar el cliente apuntando al relay 134.122.2.215 (ver memoria del proyecto; el auto-install del paquete sigue pendiente de pipeline)"

# ============================================================================
# 9. FICHA DEL CLIENTE
# ============================================================================
Titulo "9/10 Ficha del cliente"
$ficha = [ordered]@{
    cliente     = $Cliente
    cuit        = $Cuit
    equipo      = $env:COMPUTERNAME
    provisionado = (Get-Date -Format "yyyy-MM-dd")
    kit_version = "1.0"
}
$ficha | ConvertTo-Json | Out-File "C:\PilotX\cliente.json" -Encoding utf8
Paso "C:\PilotX\cliente.json escrito"

# ============================================================================
# 10. NOMBRE DE EQUIPO + KIOSKO
# ============================================================================
Titulo "10/10 Cierre"
if (-not $NombreEquipo) {
    # PILOTX- + primeras letras del cliente, máx 15 chars total (límite NetBIOS)
    $slug = ($Cliente -replace '[^A-Za-z0-9]', '').ToUpper()
    $NombreEquipo = ("PILOTX-" + $slug).Substring(0, [Math]::Min(15, 7 + $slug.Length))
}
if ($env:COMPUTERNAME -ne $NombreEquipo) {
    Rename-Computer -NewName $NombreEquipo -Force -ErrorAction SilentlyContinue
    Paso "Equipo renombrado a $NombreEquipo (aplica al reiniciar)"
}

$kiosko = Join-Path $kit "PilotX-KioskSetup.exe"
if ((-not $SinKiosko) -and (Test-Path $kiosko)) {
    Write-Host ""
    Write-Host "Para dejar la pantalla en modo KIOSKO (arranca directo en PilotX):" -ForegroundColor Yellow
    Write-Host "  $kiosko /yes" -ForegroundColor Yellow
    Write-Host "(correrlo DESPUÉS de instalar PilotX en C:\PilotX)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== LISTO ===" -ForegroundColor Green
Write-Host "Reporte de lo que tenía el equipo: $reporte"
Write-Host "Pendientes manuales:"
Write-Host "  1. Instalar PilotX (ZIP del build) en C:\PilotX si no está"
Write-Host "  2. Verificar orbitX.json SIN device_id de otra máquina (identidad propia)"
Write-Host "  3. Vincular el equipo a la org del cliente desde el panel OrbitX"
Write-Host "  4. Reiniciar para aplicar nombre de equipo y fondo"
Stop-Transcript | Out-Null
