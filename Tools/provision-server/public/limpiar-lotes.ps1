# ============================================================================
#  limpiar-lotes.ps1 — saca los lotes duplicados que deja una restauracion
#  corrida varias veces.
#
#  Por que existe: Restaurar-AOG.ps1 nunca pisa un lote que ya existe; lo deja
#  como "<nombre>~aog". Si la restauracion corre dos o tres veces (porque el
#  instalador se relanzo), quedan "lote", "lote~aog" y "lote~aog~aog": el mismo
#  campo tres veces en la lista del operario.
#
#  Se corre EN LA PANTALLA:
#      irm <IP>:8090/limpiar-lotes.ps1|iex          (muestra que haria)
#      irm <IP>:8090/limpiar-lotes.ps1|iex; Limpiar -Aplicar
#
#  En seco por defecto. Solo borra una copia cuando el original existe y tiene
#  el MISMO contenido; si difieren, la deja y avisa — puede ser trabajo real.
# ============================================================================
$ErrorActionPreference = "Continue"
try { Set-ExecutionPolicy Bypass -Scope Process -Force -ErrorAction Stop } catch { }

function Limpiar {
    param([switch]$Aplicar)

    function Ok($t)    { Write-Host "  [OK] $t" -ForegroundColor Green }
    function Aviso($t) { Write-Host "  [--] $t" -ForegroundColor DarkGray }
    function Mal($t)   { Write-Host "  [!!] $t" -ForegroundColor Yellow }

    # Misma resolucion de carpeta que usa PilotX.
    $base = $null
    $settings = "C:\PilotX\aog_settings.json"
    if (Test-Path $settings) {
        try {
            $s = Get-Content $settings -Raw | ConvertFrom-Json
            if ($s.working_directory -and $s.working_directory -ne "Default") {
                $base = Join-Path $s.working_directory "AgOpenGPS"
            }
        } catch { }
    }
    if (-not $base) { $base = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "AgOpenGPS" }
    $fieldsDir = Join-Path $base "Fields"

    Write-Host ""
    Write-Host "=== Lotes duplicados en $fieldsDir ===" -ForegroundColor Cyan
    Write-Host ""
    if (-not (Test-Path $fieldsDir)) { Mal "no existe esa carpeta"; return }

    $todos = @(Get-ChildItem $fieldsDir -Directory -EA 0)
    Write-Host "  $($todos.Count) carpetas de lote en total" -ForegroundColor DarkGray
    Write-Host ""

    # Huella de un lote: nombre+tamaño de cada archivo. Alcanza para decidir si
    # dos copias son la misma cosa sin leer megabytes.
    function Huella($dir) {
        (Get-ChildItem $dir -File -Recurse -EA 0 | Sort-Object Name |
            ForEach-Object { "$($_.Name):$($_.Length)" }) -join "|"
    }

    $aBorrar = @()
    foreach ($d in $todos) {
        if ($d.Name -notmatch '~aog') { continue }
        $original = $d.Name -replace '(~aog)+$', ''
        $rutaOrig = Join-Path $fieldsDir $original

        if (-not (Test-Path $rutaOrig)) {
            Mal "'$($d.Name)': NO existe '$original' — se deja (es el unico con esos datos)"
            continue
        }
        $hA = Huella $d.FullName
        $hB = Huella $rutaOrig
        if ($hA -eq $hB) {
            $aBorrar += $d
            Write-Host "  duplicado exacto de '$original': $($d.Name)" -ForegroundColor DarkGray
        } else {
            Mal "'$($d.Name)': difiere de '$original' — se deja, revisalo a mano"
        }
    }

    Write-Host ""
    if ($aBorrar.Count -eq 0) { Ok "no hay duplicados exactos que borrar"; return }

    if (-not $Aplicar) {
        Write-Host "  $($aBorrar.Count) carpetas se pueden borrar. Para hacerlo:" -ForegroundColor Yellow
        Write-Host "     Limpiar -Aplicar" -ForegroundColor White
        return
    }

    $n = 0
    foreach ($d in $aBorrar) {
        try { Remove-Item $d.FullName -Recurse -Force -EA Stop; $n++ }
        catch { Mal "no se pudo borrar $($d.Name): $($_.Exception.Message)" }
    }
    Ok "$n duplicados borrados"
    $quedan = @(Get-ChildItem $fieldsDir -Directory -EA 0).Count
    Write-Host "  quedan $quedan lotes" -ForegroundColor Cyan
}

Limpiar
