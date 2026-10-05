# ============================================================================
# Limpiar-Lotes.ps1 - Borra los lotes viejos de la pantalla, conservando los
#                     que se indiquen.
#
# POR DEFECTO NO BORRA NADA: muestra que haria. Para borrar de verdad hay que
# pasar -Ejecutar y escribir BORRAR cuando lo pida.
#
# Uso:
#   .\Limpiar-Lotes.ps1                                  ver que se borraria
#   .\Limpiar-Lotes.ps1 -Conservar "urea 15-44 2026-09-14"
#   .\Limpiar-Lotes.ps1 -Conservar "urea 15-44 2026-09-14" -Ejecutar
#   .\Limpiar-Lotes.ps1 -Conservar "urea*","matiiii*" -Ejecutar
#
# Antes de borrar arma un ZIP con TODO lo que va a eliminar, en
# %ProgramData%\AgroParallel\Backups\LotesBorrados\. Un lote borrado por error
# es una campana de datos perdida; el ZIP pesa poco al lado de eso.
#
# Los nombres aceptan comodines (*), y la comparacion NO distingue mayusculas.
# ============================================================================

param(
    [string[]]$Conservar = @("urea 15-44 2026-09-14"),
    [string]$Fields,
    [switch]$Ejecutar,
    [switch]$Si
)

$ErrorActionPreference = "Stop"

function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }
function Bien($t)   { Write-Host "    OK: $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "    OJO: $t" -ForegroundColor Yellow }
function Mal($t)    { Write-Host "    ERROR: $t" -ForegroundColor Red }

# --- Donde estan los lotes ---------------------------------------------------
if (-not $Fields) {
    $cfg = "C:\PilotX\aog_settings.json"
    $wd = ""
    if (Test-Path $cfg) {
        try { $wd = [string](Get-Content $cfg -Raw | ConvertFrom-Json).working_directory } catch { }
    }
    $docs = [Environment]::GetFolderPath("MyDocuments")
    if ([string]::IsNullOrWhiteSpace($wd) -or $wd -eq "Default") {
        $Fields = Join-Path (Join-Path $docs "AgOpenGPS") "Fields"
    } else {
        $Fields = Join-Path (Join-Path $wd "AgOpenGPS") "Fields"
    }
}

Write-Host "=========================================" -ForegroundColor White
Write-Host " Limpieza de lotes" -ForegroundColor White
Write-Host " Carpeta: $Fields" -ForegroundColor White
if (-not $Ejecutar) { Write-Host " MODO VER: no se borra nada" -ForegroundColor Yellow }
Write-Host "=========================================" -ForegroundColor White

if (-not (Test-Path $Fields)) { Mal "no existe $Fields"; exit 1 }

# --- Que se conserva y que se borra ------------------------------------------
Titulo "[1] Repaso"

$todos = @(Get-ChildItem $Fields -Directory -ErrorAction SilentlyContinue)
if ($todos.Count -eq 0) { Aviso "no hay lotes"; exit 0 }

function SeConserva($nombre) {
    foreach ($patron in $Conservar) {
        if ($nombre -like $patron) { return $true }
    }
    return $false
}

$quedan = @($todos | Where-Object { SeConserva $_.Name })
$borrar = @($todos | Where-Object { -not (SeConserva $_.Name) })

Write-Host "`n  SE CONSERVAN ($($quedan.Count)):" -ForegroundColor Green
if ($quedan.Count -eq 0) {
    Write-Host "      (ninguno)" -ForegroundColor Red
} else {
    foreach ($l in $quedan) { Write-Host "      $($l.Name)" -ForegroundColor Green }
}

$bytes = 0
Write-Host "`n  SE BORRAN ($($borrar.Count)):" -ForegroundColor Yellow
foreach ($l in $borrar) {
    $t = (Get-ChildItem $l.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if (-not $t) { $t = 0 }
    $bytes += $t
    Write-Host ("      {0,-45} {1,8:N1} KB   ultimo uso {2}" -f $l.Name, ($t/1KB), $l.LastWriteTime.ToString("yyyy-MM-dd"))
}

# Ningun patron matcheo: casi seguro el nombre esta mal escrito. Borrar TODO
# por un patron equivocado es el peor final posible de este script.
if ($quedan.Count -eq 0) {
    Write-Host ""
    Mal "los patrones de -Conservar no coinciden con NINGUN lote"
    Aviso "revisa el nombre (se aceptan comodines: -Conservar 'urea*')"
    exit 1
}

if (-not $Ejecutar) {
    Write-Host "`n-----------------------------------------"
    Write-Host " Se borrarian $($borrar.Count) lotes ($([math]::Round($bytes/1MB,1)) MB)." -ForegroundColor Yellow
    Write-Host " Para hacerlo de verdad: volve a correrlo con -Ejecutar" -ForegroundColor Yellow
    exit 0
}

if ($borrar.Count -eq 0) { Bien "no hay nada para borrar"; exit 0 }

# --- Confirmacion ------------------------------------------------------------
Titulo "[2] Confirmacion"

if (-not $Si) {
    Write-Host "    Se van a BORRAR $($borrar.Count) lotes de $Fields" -ForegroundColor Red
    $r = Read-Host "    Escribi BORRAR para continuar"
    if ($r -ne "BORRAR") { Aviso "cancelado, no se toco nada"; exit 0 }
}

# --- Respaldo ----------------------------------------------------------------
Titulo "[3] Respaldo antes de borrar"

$dirBk = Join-Path $env:ProgramData "AgroParallel\Backups\LotesBorrados"
New-Item -ItemType Directory -Path $dirBk -Force | Out-Null
$sello = Get-Date -Format "yyyyMMdd-HHmmss"
$stage = Join-Path $dirBk "stage-$sello"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

foreach ($l in $borrar) { Copy-Item $l.FullName (Join-Path $stage $l.Name) -Recurse -Force }

$zip = Join-Path $dirBk "lotes-borrados-$sello.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force
Bien "$zip ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"

# --- Borrar ------------------------------------------------------------------
Titulo "[4] Borrando"

$ok = 0; $fallaron = @()
foreach ($l in $borrar) {
    try {
        Remove-Item $l.FullName -Recurse -Force
        $ok++
    } catch {
        $fallaron += "$($l.Name): $($_.Exception.Message)"
    }
}
Bien "$ok lotes borrados"
foreach ($f in $fallaron) { Mal $f }

Write-Host "`n=========================================" -ForegroundColor Green
Write-Host " LISTO" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Quedaron $((Get-ChildItem $Fields -Directory).Count) lotes:"
foreach ($l in (Get-ChildItem $Fields -Directory)) { Write-Host "    $($l.Name)" }
Write-Host ""
Write-Host " Respaldo de lo borrado: $zip" -ForegroundColor DarkGray
Write-Host " Si borraste de mas, esta todo ahi: descomprimilo dentro de $Fields" -ForegroundColor DarkGray
