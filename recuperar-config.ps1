# ============================================================================
# recuperar-config.ps1 - Devuelve la configuracion y los datos que quedaron en
#                        las carpetas *.anterior-* despues de una instalacion.
#
# Uso (PowerShell COMO ADMINISTRADOR):
#     .\recuperar-config.ps1
#     .\recuperar-config.ps1 -Instalacion C:\PilotX
#     .\recuperar-config.ps1 -Marca 20260906-093425    (si hay varios respaldos)
#     .\recuperar-config.ps1 -SoloVer                  (no copia, solo informa)
#
# Que problema resuelve:
#   Las versiones del instalador anteriores al 2026-09-06 reemplazaban las
#   carpetas Desktop, Engine y BarsHost enteras. Pero adentro de Engine\ no
#   vive solo el programa: tambien GuidanceEngineData (perfil del vehiculo,
#   geometria del implemento), data\prescripciones y TODOS los .json de
#   configuracion y estado, incluido orbitX.json con la IDENTIDAD del equipo.
#   Todo eso quedo en Engine.anterior-<fecha>, intacto pero fuera de juego.
#
# Regla: el CODIGO se queda como esta (es el nuevo, y funciona), los DATOS
#   vuelven. Se copia del respaldo todo lo que NO sea binario, salvo los .json
#   que pertenecen al paquete (*.deps.json y *.runtimeconfig.json). Es lista
#   blanca y no negra a proposito: la configuracion de un producto que todavia
#   no existe queda protegida por defecto.
#
# Es seguro correrlo dos veces: copia encima, no borra nada, y nunca toca una
# DLL ni un EXE.
# ============================================================================

param(
    [string]$Instalacion = "C:\PilotX",
    [string]$Marca,
    [switch]$SoloVer
)

$ErrorActionPreference = "Stop"
$sep = [char]92
$Instalacion = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Instalacion)

function Bien($t) { Write-Host "    OK: $t" -ForegroundColor Green }
function Mal($t) { Write-Host "    ERROR: $t" -ForegroundColor Red }

Write-Host "=========================================" -ForegroundColor White
Write-Host " Recuperar configuracion de PilotX" -ForegroundColor White
Write-Host " Instalacion: $Instalacion" -ForegroundColor White
if ($SoloVer) { Write-Host " MODO SOLO VER: no se copia nada" -ForegroundColor Yellow }
Write-Host "=========================================" -ForegroundColor White

if (-not (Test-Path $Instalacion)) { Mal "no existe $Instalacion"; exit 1 }

# --- Encontrar los respaldos ------------------------------------------------
$patron = if ($Marca) { "*.anterior-$Marca" } else { "*.anterior-*" }
$backups = @(Get-ChildItem $Instalacion -Directory -Filter $patron -ErrorAction SilentlyContinue)

if ($backups.Count -eq 0) {
    Write-Host "`nNo hay carpetas *.anterior-* en $Instalacion." -ForegroundColor Yellow
    Write-Host "O ya se borraron, o esta instalacion no las genero." -ForegroundColor Yellow
    exit 1
}

# Si hay varias tandas, usar la mas reciente salvo que se pida una.
if (-not $Marca) {
    $marcas = $backups | ForEach-Object { $_.Name.Substring($_.Name.IndexOf(".anterior-") + 10) } | Sort-Object -Unique
    if ($marcas.Count -gt 1) {
        Write-Host "`nHay respaldos de varias instalaciones:" -ForegroundColor Yellow
        $marcas | ForEach-Object { Write-Host "    $_" }
        $elegida = $marcas[-1]
        Write-Host "Se usa la mas reciente: $elegida" -ForegroundColor Yellow
        Write-Host "(para otra: -Marca <la que quieras>)" -ForegroundColor DarkGray
        $backups = @($backups | Where-Object { $_.Name.EndsWith(".anterior-$elegida") })
    }
}

Write-Host "`nRespaldos a revisar:"
$backups | ForEach-Object { Write-Host "    $($_.Name)" }

# --- Cerrar PilotX ----------------------------------------------------------
if (-not $SoloVer) {
    Write-Host "`nCerrando PilotX..."
    Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Lanzar-PilotX*' } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
    foreach ($n in @("PilotX.Desktop", "PilotX.GuidanceEngine", "PilotX.Bars.Host", "WerFault", "WerFaultSecure")) {
        Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    }
    Start-Sleep -Seconds 4
    Bien "procesos cerrados"
}

# --- Restaurar --------------------------------------------------------------
$total = 0
$destacados = @()

foreach ($b in $backups) {
    # "Engine.anterior-20260906-093425" -> "Engine"
    $carpeta = $b.Name.Substring(0, $b.Name.IndexOf(".anterior-"))
    $destinoRaiz = Join-Path $Instalacion $carpeta

    if (-not (Test-Path $destinoRaiz)) {
        Write-Host "`n  $carpeta" -ForegroundColor Cyan
        Write-Host "    no existe la carpeta nueva: se saltea" -ForegroundColor Yellow
        continue
    }

    $n = 0
    foreach ($f in Get-ChildItem $b.FullName -Recurse -File -ErrorAction SilentlyContinue) {
        if ($f.Extension -in ".dll", ".exe", ".pdb") { continue }
        if ($f.Name -like "*.deps.json" -or $f.Name -like "*.runtimeconfig.json") { continue }

        $rel = $f.FullName.Substring($b.FullName.Length).TrimStart($sep)
        $dst = Join-Path $destinoRaiz $rel

        if ($rel -like "*GuidanceEngineData*" -or $rel -like "orbitX.json" -or
            $rel -like "*prescripciones*" -or $rel -like "*.json") {
            if ($destacados.Count -lt 12 -and $rel -notlike "*wwwroot*") { $destacados += "$carpeta$sep$rel" }
        }

        if ($SoloVer) { $n++; continue }
        try {
            New-Item -ItemType Directory -Path (Split-Path $dst) -Force | Out-Null
            Copy-Item $f.FullName $dst -Force
            $n++
        } catch {
            Write-Host "    no pude copiar $rel : $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    Write-Host "`n  $carpeta" -ForegroundColor Cyan
    if ($SoloVer) { Write-Host "    $n archivos se recuperarian" }
    else { Bien "$n archivos de configuracion y datos devueltos" }
    $total += $n
}

# --- Que quedo --------------------------------------------------------------
Write-Host "`n-----------------------------------------"
if ($destacados.Count -gt 0) {
    Write-Host "Algunos de los archivos afectados:"
    $destacados | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
}

if ($SoloVer) {
    Write-Host "`nSOLO VER: no se copio nada. Corre sin -SoloVer para hacerlo." -ForegroundColor Yellow
    exit 0
}

Write-Host ""
$idJson = Join-Path $Instalacion "Engine\orbitX.json"
if (Test-Path $idJson) {
    $txt = Get-Content $idJson -Raw
    $devId = ([regex]'"device_id"\s*:\s*"([^"]*)"').Match($txt).Groups[1].Value
    if ($devId) { Write-Host " Identidad del equipo: $devId" -ForegroundColor Green }
}
$ged = Join-Path $Instalacion "Engine\GuidanceEngineData"
if (Test-Path $ged) {
    $c = @(Get-ChildItem $ged -Recurse -File -ErrorAction SilentlyContinue).Count
    Write-Host " GuidanceEngineData: $c archivos (vehiculo, implemento, estado)" -ForegroundColor Green
}

Write-Host "`n=========================================" -ForegroundColor Green
Write-Host " LISTO - $total archivos devueltos" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Abri la pantalla con Lanzar-PilotX.bat y revisa que el vehiculo,"
Write-Host " el implemento y los nodos esten como los tenias."
Write-Host ""
Write-Host " Los respaldos NO se borraron. Cuando confirmes que esta todo bien:"
$backups | ForEach-Object { Write-Host "    Remove-Item '$($_.FullName)' -Recurse -Force" -ForegroundColor DarkGray }
