# ============================================================================
#  subir-lotes.ps1 — manda TODOS los lotes al cloud, de una sola vez.
#
#  Por que existe: PilotX sincroniza SOLO el lote que esta abierto
#  (OrbitXSync.cs → CurrentFieldDirectory). Es lo correcto en el dia a dia — no
#  tiene sentido resubir 19 lotes cada cinco minutos — pero despues de migrar
#  una pantalla el cloud queda en cero hasta que el operario los va abriendo
#  uno por uno. Esto los sube todos juntos, una vez, y despues el motor sigue
#  como siempre.
#
#  Se corre EN LA PANTALLA con una linea:
#      irm <IP>:8090/subir-lotes.ps1|iex
#
#  No modifica nada local: lee los .txt de cada lote y los manda. Usa la
#  identidad del propio equipo (C:\PilotX\Engine\orbitX.json), asi que los
#  lotes entran en la organizacion que corresponde y con el mismo formato que
#  usa el motor. Se puede correr las veces que haga falta.
# ============================================================================
$ErrorActionPreference = "Continue"
try { Set-ExecutionPolicy Bypass -Scope Process -Force -ErrorAction Stop } catch { }

function Ok($t)    { Write-Host "  [OK] $t" -ForegroundColor Green }
function Aviso($t) { Write-Host "  [--] $t" -ForegroundColor DarkGray }
function Mal($t)   { Write-Host "  [!!] $t" -ForegroundColor Red }

Write-Host ""
Write-Host "=== Subir todos los lotes a OrbitX ===" -ForegroundColor Cyan
Write-Host ""

# --- 1. Identidad del equipo ------------------------------------------------
$cfgPath = "C:\PilotX\Engine\orbitX.json"
if (-not (Test-Path $cfgPath)) { Mal "no esta $cfgPath — PilotX no esta instalado o no esta vinculado"; return }
try { $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json } catch { Mal "orbitX.json ilegible"; return }
if (-not $cfg.device_token) { Mal "el equipo no tiene token: vinculalo primero"; return }
$servidor = ($cfg.server_url).TrimEnd('/')
Ok "equipo $($cfg.device_id) -> $($cfg.estab_slug) en $servidor"

# --- 2. Donde estan los lotes -----------------------------------------------
# Misma resolucion que usa PilotX: working_directory de aog_settings.json, y si
# dice Default (o no esta), el Documentos del usuario que corre esto.
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

if (-not (Test-Path $fieldsDir)) {
    Mal "no existe $fieldsDir"
    Write-Host "  Buscando lotes en otros perfiles..." -ForegroundColor Yellow
    $otros = @(Get-ChildItem "C:\Users" -Directory -EA 0 |
               ForEach-Object { Join-Path $_.FullName "Documents\AgOpenGPS\Fields" } |
               Where-Object { Test-Path $_ })
    if ($otros) { $otros | ForEach-Object { Write-Host "     hay lotes en: $_" -ForegroundColor Yellow } }
    return
}
$lotes = @(Get-ChildItem $fieldsDir -Directory -EA 0)
Ok "$($lotes.Count) lotes en $fieldsDir"
if ($lotes.Count -eq 0) { return }

# --- 3. Subir ---------------------------------------------------------------
# Los mismos archivos y los mismos subtipos que manda el motor: el cloud los
# interpreta por el subtipo, no por el nombre, y si no coinciden el lote entra
# pero sin pasadas (ver OrbitXSync.SubtipoCloud).
$subtipos = @{
    "field.txt"      = "field_origin"
    "sections.txt"   = "sections_coverage"
    "tracklines.txt" = "track_lines"
    "ablines.txt"    = "ab_line"
    "boundary.txt"   = "boundary"
    "headland.txt"   = "headland"
    "contour.txt"    = "contour"
    "flags.txt"      = "flags"
    "recpath.txt"    = "recpath"
}
$headers = @{ "X-Device-ID" = $cfg.device_id; "X-Auth-Token" = $cfg.device_token }
$md5 = [System.Security.Cryptography.MD5]::Create()
$subidos = 0; $fallados = 0; $saltados = 0

foreach ($lote in $lotes) {
    $n = 0
    foreach ($archivo in Get-ChildItem $lote.FullName -File -Filter *.txt -EA 0) {
        $clave = $archivo.Name.ToLowerInvariant()
        if (-not $subtipos.ContainsKey($clave)) { $saltados++; continue }

        try {
            # ReadAllText y NO Get-Content -Raw: el segundo devuelve un String
            # con las propiedades de PowerShell pegadas (PSPath, PSProvider...),
            # y ConvertTo-Json las serializa TODAS, mandando
            # {"value":"...","PSPath":"..."} en vez del texto. El cloud guarda
            # ese objeto y despues el lote no se puede dibujar.
            $texto = [System.IO.File]::ReadAllText($archivo.FullName)
            if ($null -eq $texto) { $texto = "" }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto)
            $hash  = ([System.BitConverter]::ToString($md5.ComputeHash($bytes)) -replace '-', '').ToLower()

            $cuerpo = @{
                ruta_rel    = "aog/fields/$($lote.Name)/$($archivo.Name)"
                nombre      = $archivo.Name
                subtipo     = $subtipos[$clave]
                producto    = "aog"
                hash_md5    = $hash
                tamano      = $bytes.Length
                ts          = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                device_id   = $cfg.device_id
                es_lote     = $true
                lote_nombre = $lote.Name
                contenido   = $texto
            } | ConvertTo-Json -Depth 5 -Compress

            Invoke-RestMethod -Method Post -Uri "$servidor/api/aog/sync" -Headers $headers `
                -ContentType "application/json; charset=utf-8" `
                -Body ([System.Text.Encoding]::UTF8.GetBytes($cuerpo)) -TimeoutSec 60 | Out-Null
            $subidos++; $n++
        } catch {
            $fallados++
            Mal "$($lote.Name)/$($archivo.Name): $($_.Exception.Message)"
        }
    }
    if ($n -gt 0) { Ok "$($lote.Name): $n archivos" } else { Aviso "$($lote.Name): sin archivos que subir" }
}

Write-Host ""
Write-Host "=== $subidos archivos subidos, $fallados con error ===" -ForegroundColor $(if ($fallados) { "Yellow" } else { "Green" })
Write-Host "  Revisalo en el panel de OrbitX. El motor sigue sincronizando como siempre." -ForegroundColor DarkGray
Write-Host ""
