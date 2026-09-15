# ============================================================================
# Rescatar-AOG.ps1 - Saca los lotes y los perfiles de vehiculo de una pantalla
#                    con AgOpenGPS original, ANTES de instalar PilotX.
#
# NO TOCA NADA. Solo lee y copia a un ZIP. Se puede correr las veces que haga
# falta.
#
# Uso (PowerShell en la pantalla vieja, por RustDesk):
#     .\Rescatar-AOG.ps1
#     .\Rescatar-AOG.ps1 -Destino E:\               (ademas copia el ZIP al USB)
#     .\Rescatar-AOG.ps1 -Buscar                    (rastrea discos si no aparece)
#     .\Rescatar-AOG.ps1 -Datos D:\AgOpenGPS        (carpeta de datos a mano)
#
# Por que existe:
#   PilotX usa EXACTAMENTE las mismas carpetas que AgOpenGPS original
#   (Documents\AgOpenGPS\Fields y \Vehicles) y el mismo formato de perfil
#   (<nombre>.XML, leido campo por campo). Asi que migrar es copiar. El
#   problema es OTRO: la carpeta de trabajo no siempre es Documents. AOG la
#   guarda en HKCU\SOFTWARE\AgOpenGPS\workingDirectory, y el instalador de
#   PilotX BORRA esa clave. Si no se lee antes de instalar, queda un disco con
#   lotes y nadie sabe donde. Este script la lee primero y deja constancia.
#
# Que deja:
#   C:\Rescate-PilotX\rescate-<equipo>-<fecha>.zip   Fields + Vehicles enteros
#   C:\Rescate-PilotX\informe-<equipo>-<fecha>.txt   que habia y donde estaba
# ============================================================================

param(
    [string]$Salida = "C:\Rescate-PilotX",
    [string]$Datos,
    [string]$Destino,
    [switch]$Buscar
)

$ErrorActionPreference = "Stop"

function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }
function Bien($t)   { Write-Host "    OK: $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "    OJO: $t" -ForegroundColor Yellow }
function Mal($t)    { Write-Host "    ERROR: $t" -ForegroundColor Red }

$informe = New-Object System.Collections.Generic.List[string]
function Anotar($t) { $informe.Add($t) | Out-Null }

$sello  = Get-Date -Format "yyyyMMdd-HHmmss"
$equipo = $env:COMPUTERNAME

Write-Host "=========================================" -ForegroundColor White
Write-Host " Rescate de lotes y vehiculos de AgOpenGPS" -ForegroundColor White
Write-Host " Equipo: $equipo   $((Get-Date).ToString('yyyy-MM-dd HH:mm'))" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor White

Anotar "Rescate AgOpenGPS -> PilotX"
Anotar "Equipo: $equipo"
Anotar "Fecha:  $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
Anotar "Usuario Windows: $env:USERNAME"
Anotar ""

# --- 1. Que version de AgOpenGPS hay ----------------------------------------
Titulo "[1] Version de AgOpenGPS instalada"

$exes = @()
# C:\AgroParallel va en la lista porque asi quedo instalado en la pantalla de
# Carrano: el instalador de AOG deja elegir carpeta y nadie respeta la default.
foreach ($raiz in @("C:\Program Files\AgOpenGPS", "C:\Program Files (x86)\AgOpenGPS",
                    "C:\AgOpenGPS", "C:\AgroParallel", "C:\PilotX",
                    "$env:LOCALAPPDATA\Programs\AgOpenGPS")) {
    if (Test-Path $raiz) {
        $exes += @(Get-ChildItem $raiz -Recurse -Filter "AgOpenGPS.exe" -ErrorAction SilentlyContinue)
    }
}
# Tambien por accesos directos del menu inicio (instalaciones en rutas raras).
foreach ($lnkDir in @("$env:ProgramData\Microsoft\Windows\Start Menu\Programs",
                      "$env:APPDATA\Microsoft\Windows\Start Menu\Programs")) {
    if (-not (Test-Path $lnkDir)) { continue }
    $sh = New-Object -ComObject WScript.Shell
    foreach ($lnk in @(Get-ChildItem $lnkDir -Recurse -Filter "*AgOpenGPS*.lnk" -ErrorAction SilentlyContinue)) {
        try {
            $t = $sh.CreateShortcut($lnk.FullName).TargetPath
            if ($t -and (Test-Path $t) -and ($t -like "*AgOpenGPS.exe")) {
                $exes += @(Get-Item $t)
            }
        } catch { }
    }
}

$exes = @($exes | Sort-Object FullName -Unique)
if ($exes.Count -eq 0) {
    Aviso "no encontre AgOpenGPS.exe (puede estar en otra ruta: no importa para el rescate)"
    Anotar "AgOpenGPS.exe: no encontrado"
} else {
    foreach ($e in $exes) {
        $v = $e.VersionInfo.FileVersion
        if (-not $v) { $v = "(sin version)" }
        Bien "$($e.FullName)  ->  v$v"
        Anotar "AgOpenGPS.exe: $($e.FullName)  v$v  (modificado $($e.LastWriteTime.ToString('yyyy-MM-dd')))"
    }
}

# --- 2. Donde trabaja de verdad (el dato que el instalador borra) ------------
Titulo "[2] Carpeta de trabajo segun el Registry"

$wd = ""; $vf = ""; $lang = ""
try {
    $k = Get-ItemProperty -Path "HKCU:\SOFTWARE\AgOpenGPS" -ErrorAction Stop
    $wd   = [string]$k.WorkingDirectory
    $vf   = [string]$k.VehicleFileName
    $lang = [string]$k.Language
} catch {
    Aviso "no hay HKCU\SOFTWARE\AgOpenGPS (AOG nunca corrio con este usuario?)"
}

Anotar ""
Anotar "HKCU\SOFTWARE\AgOpenGPS"
Anotar "  WorkingDirectory = '$wd'"
Anotar "  VehicleFileName  = '$vf'   <-- PERFIL ACTIVO"
Anotar "  Language         = '$lang'"

if ($wd) { Bien "WorkingDirectory = $wd" } else { Aviso "WorkingDirectory vacio -> se asume Documentos" }
if ($vf) { Bien "Perfil de vehiculo activo = $vf" } else { Aviso "sin perfil activo declarado" }

# Misma resolucion que hace AOG/PilotX: 'Default' o vacio -> MisDocumentos.
$docs = [Environment]::GetFolderPath("MyDocuments")
if ([string]::IsNullOrWhiteSpace($wd) -or $wd -eq "Default") {
    $base = Join-Path $docs "AgOpenGPS"
} else {
    $base = Join-Path $wd "AgOpenGPS"
}
Write-Host "    Carpeta de datos: $base"
Anotar "  -> carpeta de datos resuelta: $base"

# --- 3. Encontrar las carpetas de datos -------------------------------------
Titulo "[3] Carpetas de datos"

$candidatos = New-Object System.Collections.Generic.List[string]
if ($Datos) {
    $Datos = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Datos)
    if (-not (Test-Path $Datos)) { Mal "no existe la carpeta indicada en -Datos: $Datos"; exit 1 }
    $candidatos.Add($Datos) | Out-Null
    Anotar "  -> carpeta forzada por -Datos: $Datos"
}
# -Datos manda: si el tecnico dice de donde sacar los datos, no se agrega
# ninguna otra carpeta. Mezclar lotes de dos origenes sin pedirlo es peor que
# no encontrarlos.
if (-not $Datos) {
    if (Test-Path $base) { $candidatos.Add($base) | Out-Null }
    foreach ($c in @((Join-Path $docs "AgOpenGPS"), "C:\AgOpenGPS", "D:\AgOpenGPS")) {
        if ((Test-Path $c) -and (-not $candidatos.Contains($c))) { $candidatos.Add($c) | Out-Null }
    }
}

if ($Buscar -and (-not $Datos)) {
    Write-Host "    rastreando discos (puede tardar)..."
    foreach ($d in @(Get-PSDrive -PSProvider FileSystem)) {
        try {
            $hits = @(Get-ChildItem "$($d.Root)" -Directory -Filter "AgOpenGPS" -Recurse -Depth 4 -ErrorAction SilentlyContinue)
            foreach ($h in $hits) {
                if ((Test-Path (Join-Path $h.FullName "Fields")) -and (-not $candidatos.Contains($h.FullName))) {
                    $candidatos.Add($h.FullName) | Out-Null
                }
            }
        } catch { }
    }
}

$conDatos = @($candidatos | Where-Object {
    (Test-Path (Join-Path $_ "Fields")) -or (Test-Path (Join-Path $_ "Vehicles"))
})

if ($conDatos.Count -eq 0) {
    Mal "no encontre ninguna carpeta con Fields/Vehicles"
    Aviso "proba de nuevo con -Buscar, o decime en que disco trabaja el cliente"
    Anotar ""
    Anotar "RESULTADO: no se encontraron datos."
    New-Item -ItemType Directory -Path $Salida -Force | Out-Null
    $informe -join "`r`n" | Set-Content (Join-Path $Salida "informe-$equipo-$sello.txt") -Encoding UTF8
    exit 1
}

foreach ($c in $conDatos) { Bien $c }
Anotar ""
Anotar "Carpetas con datos:"
foreach ($c in $conDatos) { Anotar "  $c" }

# --- 4. Inventario de lotes -------------------------------------------------
Titulo "[4] Lotes"

$totalLotes = 0
Anotar ""
Anotar "LOTES"
Anotar "-----"
foreach ($c in $conDatos) {
    $fdir = Join-Path $c "Fields"
    if (-not (Test-Path $fdir)) { continue }
    $lotes = @(Get-ChildItem $fdir -Directory -ErrorAction SilentlyContinue)
    Write-Host "    $fdir : $($lotes.Count) lotes"
    Anotar "en $fdir  ($($lotes.Count))"
    foreach ($l in $lotes) {
        $archivos = @(Get-ChildItem $l.FullName -File -ErrorAction SilentlyContinue)
        $kb   = [math]::Round((($archivos | Measure-Object Length -Sum).Sum) / 1KB, 1)
        # Que el archivo EXISTA no quiere decir que tenga datos: AOG deja
        # Boundary.txt con solo el encabezado cuando nunca se dibujo un lindero.
        # Informar "lindero" ahi es mentirle al que decide si la migracion salio
        # bien, asi que se mira el tamano.
        function ConDatos($nombre, $minimo) {
            $a = $archivos | Where-Object { $_.Name -eq $nombre } | Select-Object -First 1
            return ($a -and $a.Length -gt $minimo)
        }
        $tiene = @()
        if (ConDatos "Field.txt" 20)     { $tiene += "origen" }
        if (ConDatos "Boundary.txt" 20)  { $tiene += "lindero" }
        if (ConDatos "Sections.txt" 20)  { $tiene += "pintado" }
        if (ConDatos "Headland.txt" 20)  { $tiene += "cabecera" }
        if ((ConDatos "TrackLines.txt" 20) -or (ConDatos "ABLines.txt" 20) -or (ConDatos "CurveLines.txt" 20)) { $tiene += "guias" }
        if ($tiene.Count -eq 0) { $tiene = @("VACIO?") }
        $detalle = "  - $($l.Name)  [$($tiene -join ', ')]  $kb KB  ultimo uso $($l.LastWriteTime.ToString('yyyy-MM-dd'))"
        Anotar $detalle
        $totalLotes++
    }
}
if ($totalLotes -eq 0) { Aviso "no hay ningun lote" } else { Bien "$totalLotes lotes en total" }

# --- 5. Inventario de perfiles de vehiculo ----------------------------------
Titulo "[5] Perfiles de vehiculo"

function LeerSetting($xml, $nombre) {
    $n = $xml.SelectSingleNode("//setting[@name='$nombre']/value")
    if ($n) { return $n.InnerText.Trim() }
    return ""
}
function TipoVehiculo($v) {
    if ($v -eq "0") { return "tractor" }
    if ($v -eq "1") { return "cosechadora" }
    if ($v -eq "2") { return "ARTICULADO (4WD)" }
    if ($v -eq "")  { return "(no declarado)" }
    return "tipo $v"
}

$totalPerfiles = 0
$articulados = @()
Anotar ""
Anotar "PERFILES DE VEHICULO"
Anotar "--------------------"
foreach ($c in $conDatos) {
    $vdir = Join-Path $c "Vehicles"
    if (-not (Test-Path $vdir)) { continue }
    $perfiles = @(Get-ChildItem $vdir -File -Filter "*.XML" -ErrorAction SilentlyContinue)
    Write-Host "    $vdir : $($perfiles.Count) perfiles"
    Anotar "en $vdir  ($($perfiles.Count))"
    foreach ($p in $perfiles) {
        $nombre = [IO.Path]::GetFileNameWithoutExtension($p.Name)
        $activo = ""
        if ($vf -and ($nombre -eq $vf)) { $activo = "  <== ACTIVO" }

        $tipo = "(ilegible)"
        $geo  = ""
        try {
            $xml = New-Object System.Xml.XmlDocument
            $xml.Load($p.FullName)
            $tv   = LeerSetting $xml "setVehicle_vehicleType"
            $tipo = TipoVehiculo $tv
            $wb   = LeerSetting $xml "setVehicle_wheelbase"
            $tw   = LeerSetting $xml "setVehicle_trackWidth"
            $ah   = LeerSetting $xml "setVehicle_antennaHeight"
            $ap   = LeerSetting $xml "setVehicle_antennaPivot"
            $ao   = LeerSetting $xml "setVehicle_antennaOffset"
            $ms   = LeerSetting $xml "setVehicle_maxSteerAngle"
            $tool = LeerSetting $xml "setVehicle_toolWidth"
            $secs = LeerSetting $xml "setVehicle_numSections"
            $geo  = "distancia entre ejes $wb m | trocha $tw m | antena alt $ah / pivote $ap / lateral $ao | giro max $ms deg | herramienta $tool m en $secs secciones"
            if ($tv -eq "2" -or $nombre -match "articul|4wd|zanello") { $articulados += $nombre }
        } catch {
            $tipo = "(no pude leer el XML: $($_.Exception.Message))"
        }

        Write-Host "      - $nombre : $tipo$activo"
        Anotar "  - $nombre$activo"
        Anotar "      tipo: $tipo"
        if ($geo) { Anotar "      $geo" }
        Anotar "      archivo: $($p.FullName)  ($([math]::Round($p.Length/1KB,1)) KB, $($p.LastWriteTime.ToString('yyyy-MM-dd')))"
        $totalPerfiles++
    }
}
if ($totalPerfiles -eq 0) { Aviso "no hay perfiles de vehiculo" } else { Bien "$totalPerfiles perfiles" }
if ($articulados.Count -gt 0) {
    Bien "articulado(s) detectado(s): $($articulados -join ', ')"
    Anotar ""
    Anotar "ARTICULADOS DETECTADOS: $($articulados -join ', ')"
} else {
    Aviso "ninguno declara tipo articulado - revisar el informe perfil por perfil"
    Anotar ""
    Anotar "ARTICULADOS: ninguno por setVehicle_vehicleType=2. Revisar a mano."
}

# --- 6. Empaquetar ----------------------------------------------------------
Titulo "[6] Empaquetar"

New-Item -ItemType Directory -Path $Salida -Force | Out-Null
$stage = Join-Path $Salida "stage-$sello"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$i = 0
foreach ($c in $conDatos) {
    $i++
    # Si hay mas de una carpeta con datos, cada una va a su propio subarbol
    # para no mezclar lotes de distinto origen.
    if ($conDatos.Count -eq 1) { $sub = $stage } else { $sub = Join-Path $stage "origen$i" }
    New-Item -ItemType Directory -Path $sub -Force | Out-Null
    Set-Content (Join-Path $sub "_origen.txt") $c -Encoding UTF8

    foreach ($carpeta in @("Fields", "Vehicles")) {
        $src = Join-Path $c $carpeta
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $sub $carpeta) -Recurse -Force
            $n = @(Get-ChildItem (Join-Path $sub $carpeta) -Recurse -File).Count
            Bien "$carpeta copiado ($n archivos)"
        }
    }
    # Los .json de configuracion de los productos X-* (si esta pantalla ya los
    # tuviera) y aog_settings.json viajan tambien: pesan nada y sirven de prueba.
    foreach ($j in @(Get-ChildItem $c -File -Filter "*.json" -ErrorAction SilentlyContinue)) {
        Copy-Item $j.FullName (Join-Path $sub $j.Name) -Force
    }
}

# El registry va adentro del ZIP: es el dato que despues nadie puede reconstruir.
$reg = @()
$reg += "WorkingDirectory=$wd"
$reg += "VehicleFileName=$vf"
$reg += "Language=$lang"
$reg += "CarpetaResuelta=$base"
Set-Content (Join-Path $stage "_registry-aog.txt") ($reg -join "`r`n") -Encoding UTF8

$rutaInforme = Join-Path $Salida "informe-$equipo-$sello.txt"
$informe -join "`r`n" | Set-Content $rutaInforme -Encoding UTF8
Copy-Item $rutaInforme (Join-Path $stage "_informe.txt") -Force

$zip = Join-Path $Salida "rescate-$equipo-$sello.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Bien "$zip  ($mb MB)"

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content "$zip.sha256" $sha -Encoding UTF8

if ($Destino) {
    try {
        New-Item -ItemType Directory -Path $Destino -Force | Out-Null
        Copy-Item $zip $Destino -Force
        Copy-Item "$zip.sha256" $Destino -Force
        Copy-Item $rutaInforme $Destino -Force
        Bien "copiado tambien a $Destino"
    } catch {
        Mal "no pude copiar a $Destino : $($_.Exception.Message)"
    }
}

Write-Host "`n=========================================" -ForegroundColor Green
Write-Host " LISTO - no se modifico nada de la pantalla" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Lotes rescatados:    $totalLotes"
Write-Host " Perfiles rescatados: $totalPerfiles"
Write-Host " Perfil activo:       $vf"
Write-Host ""
Write-Host " ZIP:     $zip"
Write-Host " Informe: $rutaInforme"
Write-Host ""
Write-Host " ANTES de instalar PilotX: bajate el ZIP por RustDesk (transferencia" -ForegroundColor Yellow
Write-Host " de archivos) o copialo a un USB. El instalador borra la clave del" -ForegroundColor Yellow
Write-Host " registry que dice donde estaban los lotes." -ForegroundColor Yellow
