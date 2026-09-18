# ============================================================================
# Descargar-PilotX.ps1 - Baja el paquete de PilotX desde OrbitX a la pantalla,
#                        sin pasar 200 MB por RustDesk.
#
# Uso (PowerShell en la pantalla):
#     .\Descargar-PilotX.ps1 -Email leo@agroparallel.com -Listar
#     .\Descargar-PilotX.ps1 -Email leo@agroparallel.com
#     .\Descargar-PilotX.ps1 -Email leo@... -Version 1.0.71 -Salida C:\Paquetes
#
# La clave se pide por consola (no queda en el historial). Si preferis pasarla
# igual, esta -Clave.
#
# Que hace:
#   1. Login contra https://orbitx.agroparallel.com (POST /api/auth/login).
#   2. Lista lo publicado (GET /api/ota/firmwares?producto=PilotX) y elige la
#      ultima version, o la que le pidas.
#   3. Descarga (GET /api/ota/firmware/PilotX/<version>) por streaming a disco.
#   4. Verifica el SHA256 contra el que declara el catalogo. Si no coincide,
#      BORRA lo descargado: media descarga instalada es una pantalla que no abre.
#
# Despues de esto:
#     .\instalar-pilotx.ps1 -Paquete <el archivo que dejo este script>
#
# Nota: la pantalla NO necesita estar vinculada a OrbitX para esto. El endpoint
# de descarga acepta el token de un usuario logueado, no solo device-auth.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Email,

    [string]$Clave,
    [string]$Version,
    [string]$Producto = "PilotX",
    [string]$Salida = "C:\Paquetes-PilotX",
    [string]$Servidor = "https://orbitx.agroparallel.com",
    [switch]$Listar
)

$ErrorActionPreference = "Stop"

function Titulo($t) { Write-Host "`n$t" -ForegroundColor Cyan }
function Bien($t)   { Write-Host "    OK: $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "    OJO: $t" -ForegroundColor Yellow }
function Mal($t)    { Write-Host "    ERROR: $t" -ForegroundColor Red }

# Windows viejo negocia TLS 1.0 por defecto y el server lo rechaza: la descarga
# falla con "conexion cerrada de forma inesperada" y no queda claro por que.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Servidor = $Servidor.TrimEnd('/')

Write-Host "=========================================" -ForegroundColor White
Write-Host " Descargar paquete de PilotX desde OrbitX" -ForegroundColor White
Write-Host " Servidor: $Servidor" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor White

# --- 1. Login ---------------------------------------------------------------
Titulo "[1] Entrar a OrbitX"

if (-not $Clave) {
    $sec = Read-Host "    Clave de $Email" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { $Clave = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$token = ""
try {
    $r = Invoke-RestMethod -Method Post -Uri "$Servidor/api/auth/login" `
        -ContentType "application/json" `
        -Body (@{ email = $Email; password = $Clave } | ConvertTo-Json) `
        -UseBasicParsing -TimeoutSec 30
    foreach ($campo in @("token", "access_token", "jwt")) {
        if ($r.PSObject.Properties.Name -contains $campo -and $r.$campo) { $token = [string]$r.$campo; break }
    }
} catch {
    Mal "no pude entrar: $($_.Exception.Message)"
    Aviso "revisa mail/clave, o que la pantalla tenga internet (proba: ping orbitx.agroparallel.com)"
    exit 1
}

if (-not $token) { Mal "el servidor no devolvio token (respuesta inesperada)"; exit 1 }
Bien "sesion iniciada"
$headers = @{ Authorization = "Bearer $token" }

# --- 2. Que hay publicado ---------------------------------------------------
Titulo "[2] Versiones publicadas de $Producto"

try {
    $cat = @(Invoke-RestMethod -Method Get -Uri "$Servidor/api/ota/firmwares?producto=$([Uri]::EscapeDataString($Producto))" `
        -Headers $headers -UseBasicParsing -TimeoutSec 60)
} catch {
    Mal "no pude leer el catalogo: $($_.Exception.Message)"
    exit 1
}

if ($cat.Count -eq 0) {
    Mal "no hay ninguna version de '$Producto' publicada en OrbitX"
    Aviso "subila desde el panel (/firmwares, superadmin) o copia el ZIP a mano por RustDesk"
    exit 1
}

# CouchDB (o un upload viejo) puede devolver un campo como coleccion en vez de
# escalar. Sin esto, "$f.tamano_bytes / 1MB" explota con op_Division sobre
# System.Object[] y el script muere sin decir por que. Paso por aca TODO lo que
# despues se divide o se formatea como numero.
function Num($v) {
    if ($null -eq $v) { return 0 }
    if ($v -is [string] -and $v.Trim() -eq "") { return 0 }
    while ($v -is [System.Collections.IEnumerable] -and -not ($v -is [string])) {
        $primero = $null
        foreach ($x in $v) { $primero = $x; break }
        if ($null -eq $primero) { return 0 }
        $v = $primero
    }
    $n = 0.0
    if ([double]::TryParse([string]$v, [ref]$n)) { return $n }
    return 0
}

function Texto($v) {
    if ($null -eq $v) { return "" }
    while ($v -is [System.Collections.IEnumerable] -and -not ($v -is [string])) {
        $primero = $null
        foreach ($x in $v) { $primero = $x; break }
        if ($null -eq $primero) { return "" }
        $v = $primero
    }
    return [string]$v
}

# Orden por version semver, no alfabetico: 1.0.9 no es mayor que 1.0.71.
function ClaveVersion($v) {
    $p = ((Texto $v) -split '\.')
    $n = @(0, 0, 0)
    for ($i = 0; $i -lt 3 -and $i -lt $p.Count; $i++) {
        $x = 0
        if ([int]::TryParse($p[$i], [ref]$x)) { $n[$i] = $x }
    }
    return ($n[0] * 1000000 + $n[1] * 1000 + $n[2])
}

$cat = @($cat | Sort-Object { ClaveVersion $_.version } -Descending)

foreach ($f in $cat) {
    $bytes = Num $f.tamano_bytes
    $mb = ""
    if ($bytes -gt 0) { $mb = "  $([math]::Round($bytes / 1MB, 1)) MB" }
    $fecha = ""
    $ts = Num $f.ts
    if ($ts -gt 0) {
        try { $fecha = "  " + ([DateTimeOffset]::FromUnixTimeMilliseconds([int64]$ts)).LocalDateTime.ToString("yyyy-MM-dd") } catch { }
    }
    Write-Host "    $(Texto $f.version)$mb$fecha"
}

if ($Listar) {
    Write-Host "`n Para bajar una: -Version <la que quieras>" -ForegroundColor Yellow
    exit 0
}

if ($Version) {
    $elegida = $cat | Where-Object { (Texto $_.version) -eq $Version } | Select-Object -First 1
    if (-not $elegida) { Mal "la version $Version no esta publicada"; exit 1 }
} else {
    $elegida = $cat[0]
    Bien "se baja la ultima: $(Texto $elegida.version)"
}

# --- 3. Descargar -----------------------------------------------------------
$verElegida = Texto $elegida.version
Titulo "[3] Descargar $Producto $verElegida"

New-Item -ItemType Directory -Path $Salida -Force | Out-Null
$destino = Join-Path $Salida "$Producto-$verElegida.bin"
$parcial = "$destino.parcial"
foreach ($f in @($destino, $parcial)) { if (Test-Path $f) { Remove-Item $f -Force } }

$url = "$Servidor/api/ota/firmware/$([Uri]::EscapeDataString($Producto))/$([Uri]::EscapeDataString($verElegida))"

# HttpClient con streaming: Invoke-WebRequest arma el cuerpo entero en memoria
# y 200 MB en una pantalla de 4 GB es pedir problemas.
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$cliente = New-Object System.Net.Http.HttpClient($handler)
$cliente.Timeout = [TimeSpan]::FromMinutes(60)
$cliente.DefaultRequestHeaders.Authorization =
    New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $token)

try {
    $resp = $cliente.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $resp.IsSuccessStatusCode) {
        Mal "el servidor respondio $([int]$resp.StatusCode) $($resp.ReasonPhrase)"
        if ([int]$resp.StatusCode -eq 404) { Aviso "esa version no tiene binario subido en el servidor" }
        exit 1
    }

    $total = 0
    if ($resp.Content.Headers.ContentLength) { $total = [int64]$resp.Content.Headers.ContentLength }

    $entrada = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $archivo = [System.IO.File]::Create($parcial)
    try {
        $buffer = New-Object byte[] 1048576
        $leidos = 0L
        $ultimo = -1
        while (($n = $entrada.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $archivo.Write($buffer, 0, $n)
            $leidos += $n
            if ($total -gt 0) {
                $pct = [int](($leidos * 100) / $total)
                if ($pct -ne $ultimo) {
                    Write-Progress -Activity "Descargando $Producto $verElegida" `
                        -Status "$([math]::Round($leidos/1MB,1)) MB de $([math]::Round($total/1MB,1)) MB" `
                        -PercentComplete $pct
                    $ultimo = $pct
                }
            }
        }
    } finally {
        $archivo.Close()
        $entrada.Close()
    }
    Write-Progress -Activity "Descargando" -Completed
} finally {
    $cliente.Dispose()
}

$mb = [math]::Round((Get-Item $parcial).Length / 1MB, 1)
Bien "descargado: $mb MB"

# --- 4. Verificar -----------------------------------------------------------
Titulo "[4] Verificar"

$sha = (Get-FileHash $parcial -Algorithm SHA256).Hash.ToLower()

if ((Texto $elegida.hash_sha256) -ne "") {
    $esperado = (Texto $elegida.hash_sha256).ToLower()
    if ($sha -ne $esperado) {
        Mal "el SHA256 NO coincide - la descarga llego mal"
        Write-Host "      esperado: $esperado" -ForegroundColor DarkGray
        Write-Host "      obtenido: $sha" -ForegroundColor DarkGray
        Remove-Item $parcial -Force
        Aviso "se borro el archivo. Volve a correr el script."
        exit 1
    }
    Bien "SHA256 coincide con el del servidor"
} else {
    Aviso "el catalogo no publica hash para esta version - no se pudo verificar"
    Write-Host "      sha256 del archivo: $sha" -ForegroundColor DarkGray
}

Move-Item $parcial $destino -Force
Set-Content "$destino.sha256" $sha -Encoding ASCII

Write-Host "`n=========================================" -ForegroundColor Green
Write-Host " LISTO" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Paquete: $destino"
Write-Host " Version: $verElegida   $mb MB"
if ((Texto $elegida.changelog) -ne "") {
    Write-Host ""
    Write-Host " Cambios:" -ForegroundColor DarkGray
    Write-Host "   $(Texto $elegida.changelog)" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host " Siguiente paso:" -ForegroundColor Yellow
Write-Host "   .\instalar-pilotx.ps1 -Paquete `"$destino`"" -ForegroundColor Yellow
