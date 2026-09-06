# ============================================================================
# instalar-pilotx.ps1 - Instala o actualiza PilotX en una pantalla, verificando
#                       cada paso y con vuelta atras si algo sale mal.
#
# Uso (PowerShell COMO ADMINISTRADOR, en la pantalla):
#     .\instalar-pilotx.ps1 -Paquete C:\Users\...\Downloads\1.0.49.bin
#
# Opcionales:
#     -Instalacion C:\PilotX     donde esta instalado (default C:\PilotX)
#     -Sha <hash>                verifica el paquete antes de tocar nada
#     -SinFirewall               no correr setup_pilotx_lan.bat
#
# Que hace, en orden, y frenando ante el primer problema:
#   1. Verifica que el paquete exista, sea un ZIP valido y (si se dio -Sha)
#      que el hash coincida. Nada se toca hasta que esto pasa.
#   2. Cierra PilotX, el Engine, la barra y el lanzador.
#   3. Respalda las carpetas de programa con un rename (instantaneo, sin copiar
#      gigas). La configuracion y los lotes NO se tocan en ningun momento.
#   4. Extrae el paquete completo.
#   5. Verifica: cantidad de archivos, que todos los ensamblados propios tengan
#      la version esperada, y que no haya quedado ningun .r2r.dll huerfano.
#   6. PRUEBA QUE LA PANTALLA ABRA. Si no abre, deshace todo y deja la version
#      anterior funcionando.
#   7. Abre los puertos del firewall.
#
# Por que existe: la 1.0.48 se publico sin que nadie la hubiera ejecutado una
# sola vez, no arrancaba, y dejo una pantalla sin poder trabajar en plena
# campana. Recuperarla fueron horas de comandos a ciegas. Este script existe
# para que eso no dependa de que alguien se acuerde de mirar.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$Paquete,

    [string]$Instalacion = "C:\PilotX",
    [string]$Sha,
    [switch]$SinFirewall
)

$ErrorActionPreference = "Stop"
$sep = [char]92

function Paso($n, $txt) { Write-Host "`n[$n] $txt" -ForegroundColor Cyan }
function Bien($txt) { Write-Host "    OK: $txt" -ForegroundColor Green }
function Mal($txt) { Write-Host "    ERROR: $txt" -ForegroundColor Red }

# Carpetas que se reemplazan enteras. Todo lo que NO este aca sobrevive: la
# configuracion, los lotes, los logs y los respaldos siguen donde estaban.
$carpetasPrograma = @("Desktop", "Engine", "BarsHost")

Write-Host "=========================================" -ForegroundColor White
Write-Host " Instalador PilotX" -ForegroundColor White
Write-Host " Destino: $Instalacion" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor White

# ---------------------------------------------------------------------------
Paso 1 "Verificando el paquete"
# ---------------------------------------------------------------------------
if (-not (Test-Path $Paquete)) { Mal "no existe: $Paquete"; exit 1 }
$mb = [math]::Round((Get-Item $Paquete).Length / 1MB, 1)
Write-Host "    archivo: $Paquete ($mb MB)"

if ($Sha) {
    $real = (Get-FileHash $Paquete -Algorithm SHA256).Hash
    if ($real -ne $Sha.ToUpper().Trim()) {
        Mal "el hash no coincide. La descarga esta incompleta o corrupta."
        Write-Host "    esperado: $($Sha.ToUpper())"
        Write-Host "    obtenido: $real"
        exit 1
    }
    Bien "hash verificado"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
try { $z = [System.IO.Compression.ZipFile]::OpenRead($Paquete) }
catch { Mal "no es un ZIP valido (aunque se llame .bin, adentro es un ZIP): $($_.Exception.Message)"; exit 1 }

$entradas = @($z.Entries | Where-Object { -not $_.FullName.EndsWith([char]47) })
$totalEsperado = $entradas.Count
if ($totalEsperado -lt 100) { $z.Dispose(); Mal "el paquete tiene solo $totalEsperado archivos. Descarga incompleta."; exit 1 }

# Version que trae el paquete: la leemos del propio ejecutable mas adelante.
Bien "$totalEsperado archivos en el paquete"

# ---------------------------------------------------------------------------
Paso 2 "Cerrando PilotX"
# ---------------------------------------------------------------------------
Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*Lanzar-PilotX*' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }

foreach ($n in @("PilotX.Desktop", "PilotX.GuidanceEngine", "PilotX.Bars.Host", "WerFault", "WerFaultSecure")) {
    Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
}
Start-Sleep -Seconds 3
Bien "procesos cerrados"

# ---------------------------------------------------------------------------
Paso 3 "Respaldando la version actual"
# ---------------------------------------------------------------------------
# Rename en vez de copia: es instantaneo y no consume disco extra. Si algo
# falla mas adelante, se renombra de vuelta y la pantalla queda como estaba.
$marca = (Get-Date).ToString("yyyyMMdd-HHmmss")
$respaldos = @{}
foreach ($c in $carpetasPrograma) {
    $orig = Join-Path $Instalacion $c
    if (Test-Path $orig) {
        $bak = Join-Path $Instalacion "$c.anterior-$marca"
        try {
            Move-Item $orig $bak -Force
            $respaldos[$c] = $bak
        } catch {
            Mal "no se pudo respaldar $c ($($_.Exception.Message)). Suele ser un proceso abierto."
            foreach ($k in $respaldos.Keys) { Move-Item $respaldos[$k] (Join-Path $Instalacion $k) -Force }
            $z.Dispose(); exit 1
        }
    }
}
if ($respaldos.Count -gt 0) { Bien "respaldadas: $($respaldos.Keys -join ', ') (sufijo .anterior-$marca)" }
else { Write-Host "    no habia instalacion previa: es una instalacion nueva" }

function Deshacer {
    Write-Host "`n    Deshaciendo: volviendo a la version anterior..." -ForegroundColor Yellow
    foreach ($c in $carpetasPrograma) {
        $dst = Join-Path $Instalacion $c
        if ($respaldos.ContainsKey($c)) {
            if (Test-Path $dst) { Remove-Item $dst -Recurse -Force -ErrorAction SilentlyContinue }
            Move-Item $respaldos[$c] $dst -Force
        }
    }
    Write-Host "    La pantalla quedo como estaba antes. Podes abrirla con Lanzar-PilotX.bat" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
Paso 4 "Instalando"
# ---------------------------------------------------------------------------
$n = 0
try {
    foreach ($e in $entradas) {
        $dst = Join-Path $Instalacion ($e.FullName.Replace([char]47, $sep))
        $dir = Split-Path $dst
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        $n++
    }
} catch {
    Mal "fallo al extraer en el archivo ${n}: $($_.Exception.Message)"
    $z.Dispose(); Deshacer; exit 1
}
$z.Dispose()
Bien "$n archivos instalados"

# ---------------------------------------------------------------------------
Paso 5 "Verificando la instalacion"
# ---------------------------------------------------------------------------
$exe = Join-Path $Instalacion "Desktop\PilotX.Desktop.exe"
if (-not (Test-Path $exe)) { Mal "no aparecio $exe"; Deshacer; exit 1 }

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
Write-Host "    version instalada: $version"

# Todos los ensamblados propios tienen que tener la MISMA version. Mezclar
# versiones es lo que dejo una pantalla sin arrancar: se referencian por
# version exacta y la que quede vieja apunta a algo que ya no existe.
$patrones = @("AgroParallel*.dll", "AgOpenGPS*.dll", "AgLibrary.dll", "PilotX*.dll", "PilotX*.exe")
$distintos = @()
foreach ($c in $carpetasPrograma) {
    $d = Join-Path $Instalacion $c
    if (-not (Test-Path $d)) { continue }
    foreach ($f in Get-ChildItem $d -File) {
        $coincide = $false
        foreach ($p in $patrones) { if ($f.Name -like $p) { $coincide = $true; break } }
        if (-not $coincide) { continue }
        $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($f.FullName).FileVersion
        if ($v -and $v -ne $version) { $distintos += "$($f.Name) = $v" }
    }
}
if ($distintos.Count -gt 0) {
    Mal "hay ensamblados con otra version (la app no va a arrancar):"
    $distintos | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    Deshacer; exit 1
}
Bien "todos los ensamblados en $version"

$huerfanos = @(Get-ChildItem $Instalacion -Recurse -Filter *.r2r.dll -ErrorAction SilentlyContinue)
if ($huerfanos.Count -gt 0) {
    Write-Host "    quitando $($huerfanos.Count) archivo(s) .r2r.dll de versiones viejas (peso muerto)" -ForegroundColor DarkGray
    $huerfanos | ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }
}

# ---------------------------------------------------------------------------
Paso 6 "Probando que la pantalla abra"
# ---------------------------------------------------------------------------
# Este es el paso que faltaba y por el que existe todo este script.
$errFile = Join-Path $env:TEMP "pilotx_instalar.err.txt"
$p = Start-Process $exe -PassThru -WorkingDirectory $Instalacion -RedirectStandardError $errFile
$murio = $p.WaitForExit(30000)
$txt = ""
try { $txt = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) } catch { }

if ($murio) {
    Mal "la pantalla NO abrio (salio con 0x$("{0:X8}" -f $p.ExitCode))"
    if ($txt -and $txt.Trim()) { Write-Host $txt.Trim() -ForegroundColor Red }
    else { Write-Host "    Sin mensaje. Suele ser un problema del paquete, no de esta pantalla." -ForegroundColor Red }
    Deshacer
    Write-Host "`n    Avisale a Leonardo: el paquete $version no arranca." -ForegroundColor Yellow
    exit 1
}
try { $p.Kill() } catch { }
Get-Process WerFault, WerFaultSecure -ErrorAction SilentlyContinue | Stop-Process -Force
if ($txt -match "Cold-start[^\r\n]*") { Write-Host "    $($matches[0])" }
Bien "abrio y se mantuvo abierta"

# ---------------------------------------------------------------------------
Paso 7 "Abriendo los puertos del firewall"
# ---------------------------------------------------------------------------
if ($SinFirewall) {
    Write-Host "    salteado por -SinFirewall"
} else {
    $bat = Join-Path $Instalacion "setup_pilotx_lan.bat"
    if (Test-Path $bat) {
        & cmd /c "`"$bat`"" | Out-Null
        Bien "puertos abiertos (UDP 9999 del ToolX, TCP 5180 y 5181)"
    } else {
        Write-Host "    AVISO: no vino setup_pilotx_lan.bat en el paquete." -ForegroundColor Yellow
        Write-Host "    Sin la regla de UDP 9999, un nodo ToolX aparece conectado pero no pinta." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
Write-Host "`n=========================================" -ForegroundColor Green
Write-Host " LISTO - PilotX $version instalado y probado" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Abri la pantalla con Lanzar-PilotX.bat"
if ($respaldos.Count -gt 0) {
    Write-Host ""
    Write-Host " La version anterior quedo en las carpetas *.anterior-$marca."
    Write-Host " Si todo anda bien manana, se pueden borrar para recuperar disco."
}
