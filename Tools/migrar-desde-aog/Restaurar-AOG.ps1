# ============================================================================
# Restaurar-AOG.ps1 - Devuelve a PilotX los lotes y perfiles rescatados de una
#                     pantalla con AgOpenGPS original.
#
# Se corre DESPUES de instalar PilotX, en la misma pantalla.
#
# Uso (PowerShell COMO ADMINISTRADOR):
#     .\Restaurar-AOG.ps1 -Zip C:\Rescate-PilotX\rescate-PANTALLA-2026....zip
#     .\Restaurar-AOG.ps1 -Zip ... -Perfil "Articulado"     (lo deja activo)
#     .\Restaurar-AOG.ps1 -Zip ... -SoloVer                 (no copia nada)
#     .\Restaurar-AOG.ps1 -Zip ... -Pisar                   (pisa lotes homonimos)
#
# Que hace:
#   Copia Fields\ y Vehicles\ del ZIP a la carpeta de datos que PilotX este
#   usando de verdad (la que declara <instalacion>\aog_settings.json), y deja
#   activo el perfil de vehiculo que corresponda.
#
#   Un lote que ya exista en el destino NO se pisa: entra como "<nombre>~aog".
#   Asi nunca se pierde trabajo, ni el viejo ni el nuevo. Con -Pisar se invierte.
#
# Lo que este script NO puede garantizar:
#   El perfil se lee campo por campo y lo que no coincide con esta version
#   queda en el valor por defecto. Un perfil de AOG 6.x entra casi entero; uno
#   de AOG 5.x entra a medias. SIEMPRE hay que abrir Configuracion y verificar
#   geometria (distancia entre ejes, antena, ancho, secciones) antes de salir
#   a trabajar. El script te lista al final que valores trajo el XML.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Zip,

    [string]$Instalacion = "C:\PilotX",
    [string]$Perfil,
    [switch]$Pisar,
    [switch]$SoloVer
)

$ErrorActionPreference = "Stop"

function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }
function Bien($t)   { Write-Host "    OK: $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "    OJO: $t" -ForegroundColor Yellow }
function Mal($t)    { Write-Host "    ERROR: $t" -ForegroundColor Red }

# Rutas absolutas antes de cualquier llamada .NET (ZipFile resuelve contra
# System32, no contra la carpeta donde estas parado).
if (Test-Path -LiteralPath $Zip) { $Zip = (Resolve-Path -LiteralPath $Zip).Path }
$Instalacion = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Instalacion)

Write-Host "=========================================" -ForegroundColor White
Write-Host " Restaurar lotes y vehiculos en PilotX" -ForegroundColor White
if ($SoloVer) { Write-Host " MODO SOLO VER: no se copia nada" -ForegroundColor Yellow }
Write-Host "=========================================" -ForegroundColor White

if (-not (Test-Path $Zip)) { Mal "no existe el ZIP: $Zip"; exit 1 }

# --- 1. A donde van los datos -----------------------------------------------
Titulo "[1] Carpeta de datos de PilotX"

$cfgPath = Join-Path $Instalacion "aog_settings.json"
$cfg = $null
if (Test-Path $cfgPath) {
    try { $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json } catch { Aviso "aog_settings.json ilegible, se usa Documentos" }
    Bien "config de arranque: $cfgPath"
} else {
    Aviso "no existe $cfgPath (PilotX no instalado ahi?) - se asume Documentos"
}

$wd = ""
if ($cfg -and $cfg.working_directory) { $wd = [string]$cfg.working_directory }

$docs = [Environment]::GetFolderPath("MyDocuments")
if ([string]::IsNullOrWhiteSpace($wd) -or $wd -eq "Default") {
    $base = Join-Path $docs "AgOpenGPS"
} else {
    $base = Join-Path $wd "AgOpenGPS"
}
Write-Host "    Destino: $base"

$destFields   = Join-Path $base "Fields"
$destVehicles = Join-Path $base "Vehicles"

# --- 2. Abrir el rescate ----------------------------------------------------
Titulo "[2] Abrir el rescate"

$tmp = Join-Path $env:TEMP "restaurar-aog-$(Get-Date -Format yyyyMMdd-HHmmss)"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $tmp)
Bien "extraido"

$regTxt = Join-Path $tmp "_registry-aog.txt"
$perfilOriginal = ""
if (Test-Path $regTxt) {
    foreach ($linea in (Get-Content $regTxt)) {
        if ($linea -like "VehicleFileName=*") { $perfilOriginal = $linea.Substring(16).Trim() }
    }
    if ($perfilOriginal) { Bien "perfil que estaba activo en AgOpenGPS: $perfilOriginal" }
}

# Un solo origen -> Fields/Vehicles en la raiz. Varios -> origen1, origen2...
$origenes = @()
if ((Test-Path (Join-Path $tmp "Fields")) -or (Test-Path (Join-Path $tmp "Vehicles"))) {
    $origenes += $tmp
}
foreach ($d in @(Get-ChildItem $tmp -Directory -Filter "origen*" -ErrorAction SilentlyContinue)) {
    $origenes += $d.FullName
}
if ($origenes.Count -eq 0) { Mal "el ZIP no tiene Fields ni Vehicles adentro"; exit 1 }

# --- 3. Cerrar PilotX -------------------------------------------------------
if (-not $SoloVer) {
    Titulo "[3] Cerrar PilotX"
    Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Lanzar-PilotX*' } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
    foreach ($n in @("PilotX.Desktop", "PilotX.GuidanceEngine", "PilotX.Bars.Host", "AgOpenGPS", "AgIO")) {
        Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    }
    Start-Sleep -Seconds 3
    Bien "procesos cerrados"
}

# --- 4. Lotes ---------------------------------------------------------------
Titulo "[4] Lotes"

$lotesOk = 0; $lotesRenombrados = 0
foreach ($o in $origenes) {
    $src = Join-Path $o "Fields"
    if (-not (Test-Path $src)) { continue }
    foreach ($l in @(Get-ChildItem $src -Directory)) {
        $dst = Join-Path $destFields $l.Name
        $nombreFinal = $l.Name

        if ((Test-Path $dst) -and (-not $Pisar)) {
            $nombreFinal = "$($l.Name)~aog"
            $dst = Join-Path $destFields $nombreFinal
            $n = 2
            while (Test-Path $dst) {
                $nombreFinal = "$($l.Name)~aog$n"
                $dst = Join-Path $destFields $nombreFinal
                $n++
            }
            $lotesRenombrados++
        }

        if ($SoloVer) {
            Write-Host "    $($l.Name)  ->  $nombreFinal"
        } else {
            New-Item -ItemType Directory -Path $destFields -Force | Out-Null
            Copy-Item $l.FullName $dst -Recurse -Force
            if ($nombreFinal -eq $l.Name) { Bien $nombreFinal } else { Aviso "$($l.Name) ya existia -> entra como $nombreFinal" }
        }
        $lotesOk++
    }
}
if ($lotesOk -eq 0) { Aviso "no habia lotes en el rescate" }

# --- 5. Perfiles ------------------------------------------------------------
Titulo "[5] Perfiles de vehiculo"

function LeerSetting($xml, $nombre) {
    $n = $xml.SelectSingleNode("//setting[@name='$nombre']/value")
    if ($n) { return $n.InnerText.Trim() }
    return ""
}

$perfilesOk = 0
$resumenPerfiles = New-Object System.Collections.Generic.List[string]
foreach ($o in $origenes) {
    $src = Join-Path $o "Vehicles"
    if (-not (Test-Path $src)) { continue }
    foreach ($p in @(Get-ChildItem $src -File -Filter "*.XML")) {
        $nombre = [IO.Path]::GetFileNameWithoutExtension($p.Name)
        $dst = Join-Path $destVehicles $p.Name
        $nombreFinal = $nombre

        if ((Test-Path $dst) -and (-not $Pisar)) {
            $nombreFinal = "$nombre~aog"
            $dst = Join-Path $destVehicles "$nombreFinal.XML"
        }

        if (-not $SoloVer) {
            New-Item -ItemType Directory -Path $destVehicles -Force | Out-Null
            Copy-Item $p.FullName $dst -Force
        }

        # Mostrar la geometria que trae: es lo que hay que verificar en cabina.
        try {
            $xml = New-Object System.Xml.XmlDocument
            $xml.Load($p.FullName)
            $tv = LeerSetting $xml "setVehicle_vehicleType"
            $tipo = "tractor"
            if ($tv -eq "1") { $tipo = "cosechadora" }
            if ($tv -eq "2") { $tipo = "ARTICULADO (4WD)" }
            $resumenPerfiles.Add("  $nombreFinal  [$tipo]") | Out-Null
            $resumenPerfiles.Add("      entre ejes $(LeerSetting $xml 'setVehicle_wheelbase') m | trocha $(LeerSetting $xml 'setVehicle_trackWidth') m | giro max $(LeerSetting $xml 'setVehicle_maxSteerAngle') deg") | Out-Null
            $resumenPerfiles.Add("      antena: alt $(LeerSetting $xml 'setVehicle_antennaHeight') / pivote $(LeerSetting $xml 'setVehicle_antennaPivot') / lateral $(LeerSetting $xml 'setVehicle_antennaOffset')") | Out-Null
            $resumenPerfiles.Add("      herramienta $(LeerSetting $xml 'setVehicle_toolWidth') m en $(LeerSetting $xml 'setVehicle_numSections') secciones | enganche $(LeerSetting $xml 'setVehicle_hitchLength')") | Out-Null
            Write-Host "    $nombreFinal : $tipo"
        } catch {
            Aviso "$nombreFinal : no pude leer el XML para mostrarlo (se copio igual)"
        }
        $perfilesOk++
    }
}
if ($perfilesOk -eq 0) { Aviso "no habia perfiles en el rescate" }

# --- 6. Dejar activo el perfil ----------------------------------------------
Titulo "[6] Perfil activo"

$perfilElegido = $Perfil
if (-not $perfilElegido) { $perfilElegido = $perfilOriginal }

if (-not $perfilElegido) {
    Aviso "no se indico perfil y el rescate no dice cual estaba activo"
    Aviso "elegilo a mano en PilotX (Configuracion > Perfiles) o corre con -Perfil <nombre>"
} elseif (-not (Test-Path (Join-Path $destVehicles "$perfilElegido.XML"))) {
    Aviso "el perfil '$perfilElegido' no quedo en $destVehicles (se renombro?) - elegilo a mano en PilotX"
} elseif ($SoloVer) {
    Write-Host "    quedaria activo: $perfilElegido"
} else {
    if (-not $cfg) {
        $cfg = [pscustomobject]@{ working_directory = ""; vehicle_file_name = ""; language = "es" }
    }
    # Preservar las claves tal cual las escribe PilotX (snake_case).
    $nuevo = [ordered]@{
        working_directory = [string]$cfg.working_directory
        vehicle_file_name = $perfilElegido
        language          = [string]$cfg.language
    }
    if (-not $nuevo.language) { $nuevo.language = "es" }
    if (Test-Path $cfgPath) { Copy-Item $cfgPath "$cfgPath.antes-migracion" -Force }
    New-Item -ItemType Directory -Path $Instalacion -Force | Out-Null
    # Sin BOM: PilotX lo lee con File.ReadAllText (que lo toleraria), pero el
    # resto de las herramientas que tocan este archivo no necesariamente.
    $utf8SinBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($cfgPath, ($nuevo | ConvertTo-Json), $utf8SinBom)
    Bien "perfil activo = $perfilElegido  (en $cfgPath)"
}

if (-not $SoloVer) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# --- Cierre -----------------------------------------------------------------
Write-Host "`n=========================================" -ForegroundColor Green
if ($SoloVer) { Write-Host " SOLO VER - no se copio nada" -ForegroundColor Yellow }
else { Write-Host " LISTO" -ForegroundColor Green }
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Lotes:    $lotesOk  (renombrados por coincidir: $lotesRenombrados)"
Write-Host " Perfiles: $perfilesOk"
Write-Host ""
if ($resumenPerfiles.Count -gt 0) {
    Write-Host " Geometria que trajo cada perfil - VERIFICALA EN PANTALLA:" -ForegroundColor Yellow
    foreach ($r in $resumenPerfiles) { Write-Host $r -ForegroundColor DarkGray }
    Write-Host ""
}
Write-Host " Antes de salir a trabajar:" -ForegroundColor Yellow
Write-Host "   1. Abri PilotX y entra a Configuracion: distancia entre ejes, antena," -ForegroundColor Yellow
Write-Host "      ancho de herramienta y secciones. Lo que el XML viejo no tenia" -ForegroundColor Yellow
Write-Host "      quedo en el valor por defecto, no en el del cliente." -ForegroundColor Yellow
Write-Host "   2. Abri un lote conocido y mira que el lindero y el pintado esten." -ForegroundColor Yellow
