# ============================================================================
# build-parche.ps1 - Genera un paquete de actualizacion LIVIANO para PilotX.
#
# Por que existe:
#   El ZIP completo pesa ~190 MB (377 MB descomprimido). De eso, codigo
#   nuestro son 16,5 MB: el resto es runtime .NET, Avalonia, SkiaSharp,
#   esptool, RustDesk e imagenes de wwwroot, que NO cambian entre nuestras
#   versiones. Mandar 190 MB para arreglar una linea es absurdo, y en el
#   campo con datos moviles directamente no se puede.
#
#   Este script compara el paquete de la version nueva contra el de una
#   version anterior y arma un ZIP solo con lo que cambio.
#
# COMPARA PAQUETE CONTRA PAQUETE, no carpetas:
#   Build/ tiene 3500+ archivos y muchos NO van al producto: el build de
#   Linux, BenchX, la cache de WebView2, logs de corridas de prueba. Si se
#   comparan carpetas el parche se llena de basura; la primera version de
#   este script daba 875 MB por ese motivo, con un log de 94 MB adentro.
#   Tomando como fuente el ZIP ya empaquetado heredamos gratis las reglas de
#   build.ps1 y es imposible que se cuele un archivo que el producto no lleva.
#
# REGLA DE ORO (aprendida rompiendo una pantalla en produccion):
#   Los ensamblados se referencian por version EXACTA. Si mandamos solo los
#   que cambiaron, los que quedan viejos apuntan a una version que ya no
#   existe y la app NO ARRANCA. Por eso TODOS nuestros ensamblados van
#   siempre en el parche, hayan cambiado o no. Son 16,5 MB: barato al lado
#   de dejar a alguien sin pantalla en plena labor.
#
# REQUISITO DE BASE:
#   Solo se puede parchear una instalacion 1.0.48 o superior. Hasta la 1.0.47
#   el build usaba ReadyToRunComposite: todo el codigo nativo vivia en un
#   unico PilotX.Desktop.r2r.dll y una DLL suelta no tenia ningun efecto.
#
# Uso:
#   .\build-parche.ps1 -Base 1.0.48            genera el parche
#   .\build-parche.ps1 -Base 1.0.48 -Medir     solo informa, no genera nada
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Base,

    # Modo medicion: calcula que entraria y cuanto pesaria, sin generar el
    # ZIP. Sirve para ver el costo de una release antes de sacarla, y para
    # probar el script sin dejar dando vueltas un paquete que alguien pueda
    # instalar por error.
    [switch]$Medir
)

$ErrorActionPreference = "Stop"
$raiz = $PSScriptRoot
$nueva = (Get-Content (Join-Path $raiz "Installer\VERSION") -Raw).Trim()

Write-Host "=== Parche PilotX: $Base -> $nueva ===" -ForegroundColor Cyan

if ($Base -eq $nueva -and -not $Medir) {
    throw "La version base y la nueva son la misma ($nueva). Subi Installer/VERSION antes, o usa -Medir para solo estimar."
}

# La 1.0.48 es la primera sin composite. Antes de eso, parchear no sirve.
if ([Version]$Base -lt [Version]"1.0.48") {
    throw "No se puede parchear una $Base. Hasta la 1.0.47 el build era composite y una DLL suelta no tiene efecto: esa instalacion necesita el ZIP completo."
}

$zipBase = Join-Path $raiz "PilotX_v$Base.zip"
$zipNueva = Join-Path $raiz "PilotX_v$nueva.zip"
if (-not (Test-Path $zipBase)) { throw "Falta $zipBase. Hace falta el paquete de la version base para saber que cambio." }
if (-not (Test-Path $zipNueva)) { throw "Falta $zipNueva. Compila primero con .\build.ps1" }

# --- Nuestros ensamblados: van SIEMPRE, cambien o no (ver regla de oro) -----
$patronesSiempre = @(
    "AgroParallel*.dll", "AgOpenGPS*.dll", "AgLibrary.dll",
    "PilotX*.dll", "PilotX*.exe",
    "*.deps.json", "*.runtimeconfig.json"
)
$carpetasApp = @("Desktop", "Engine", "BarsHost")

Add-Type -AssemblyName System.IO.Compression.FileSystem
$sha = [System.Security.Cryptography.SHA256]::Create()

function Get-HashDeEntrada($entrada) {
    $s = $entrada.Open()
    try { return [BitConverter]::ToString($sha.ComputeHash($s)).Replace("-", "") }
    finally { $s.Dispose() }
}

# --- Indice de hashes de la version base -----------------------------------
Write-Host "Leyendo $Base para comparar..."
$hashBase = @{}
$zb = [System.IO.Compression.ZipFile]::OpenRead($zipBase)
try {
    foreach ($e in $zb.Entries) {
        if ($e.FullName.EndsWith("/")) { continue }
        $hashBase[$e.FullName] = Get-HashDeEntrada $e
    }
}
finally { $zb.Dispose() }
Write-Host "  $($hashBase.Count) archivos en la base."

# --- Elegir que entra, leyendo el paquete nuevo ----------------------------
$staging = Join-Path $env:TEMP "pilotx_parche_$nueva"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

$archivos = @()
$tam = @{}
$nCambio = 0; $nConsistencia = 0; $nNuevos = 0; $bytes = 0

$zn = [System.IO.Compression.ZipFile]::OpenRead($zipNueva)
try {
    foreach ($e in $zn.Entries) {
        if ($e.FullName.EndsWith("/")) { continue }
        $rel = $e.FullName
        $nombre = Split-Path $rel -Leaf
        $primerSeg = $rel.Split("/")[0]

        $esNuestro = $false
        if ($carpetasApp -contains $primerSeg) {
            foreach ($p in $patronesSiempre) {
                if ($nombre -like $p) { $esNuestro = $true; break }
            }
        }

        $h = Get-HashDeEntrada $e
        $existia = $hashBase.ContainsKey($rel)
        $cambio = (-not $existia) -or ($hashBase[$rel] -ne $h)

        if (-not ($cambio -or $esNuestro)) { continue }

        if (-not $existia) { $motivo = "nuevo"; $nNuevos++ }
        elseif ($cambio) { $motivo = "cambio"; $nCambio++ }
        else { $motivo = "consistencia"; $nConsistencia++ }

        $bytes += $e.Length
        $tam[$rel] = $e.Length
        $archivos += [pscustomobject]@{ ruta = $rel; sha256 = $h.ToLower(); motivo = $motivo }

        if (-not $Medir) {
            $dst = Join-Path $staging ($rel -replace "/", "\")
            New-Item -ItemType Directory -Path (Split-Path $dst) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        }
    }
}
finally { $zn.Dispose() }

Write-Host "  $nCambio cambiados, $nNuevos nuevos, $nConsistencia incluidos por consistencia de version."

$mbFull = [math]::Round((Get-Item $zipNueva).Length / 1MB, 1)

# --- Modo medicion: informar y salir ---------------------------------------
if ($Medir) {
    Remove-Item $staging -Recurse -Force
    Write-Host ""
    Write-Host "=== MEDICION (no se genero ningun paquete) ===" -ForegroundColor Yellow
    Write-Host "Archivos que entrarian: $($archivos.Count)"
    Write-Host "Peso sin comprimir    : $([math]::Round($bytes / 1MB, 1)) MB"
    Write-Host "ZIP completo          : $mbFull MB"
    Write-Host ""
    Write-Host "Top 10 por peso:"
    $archivos | Sort-Object { - $tam[$_.ruta] } | Select-Object -First 10 | ForEach-Object {
        "{0,8:N1} KB  {1,-13} {2}" -f ($tam[$_.ruta] / 1KB), $_.motivo, $_.ruta
    }
    return
}

# --- Manifiesto ------------------------------------------------------------
# Lo lee quien aplique el parche: sirve para rechazarlo si la instalacion no
# es la version base esperada. Ese chequeo es lo que evita repetir el
# incidente de las DLL con la version equivocada.
$manifiesto = [pscustomobject]@{
    tipo          = "parche"
    version_base  = $Base
    version_nueva = $nueva
    base_minima   = "1.0.48"
    archivos      = $archivos
}
$manifiesto | ConvertTo-Json -Depth 5 | Out-File (Join-Path $staging "parche.json") -Encoding utf8

# --- Empaquetar ------------------------------------------------------------
$salida = Join-Path $raiz "PilotX_parche_${Base}_a_${nueva}.zip"
if (Test-Path $salida) { Remove-Item $salida -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $salida, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $staging -Recurse -Force

$mb = [math]::Round((Get-Item $salida).Length / 1MB, 1)
$shaZip = (Get-FileHash $salida -Algorithm SHA256).Hash
if ($mb -gt 0) { $veces = [math]::Round($mbFull / $mb, 1) } else { $veces = 0 }

Write-Host ""
Write-Host "Completo: $mbFull MB   Parche: $mb MB   ($veces veces mas chico)" -ForegroundColor Green
Write-Host "PARCHE: $salida" -ForegroundColor Green
Write-Host "SHA   : $shaZip"
Write-Host "Aplica sobre una instalacion $Base y la deja en $nueva."
