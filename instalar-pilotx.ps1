# ============================================================================
# instalar-pilotx.ps1 - Instala, actualiza o parchea PilotX en una pantalla,
#                       verificando cada paso y con vuelta atras si algo falla.
#
# Uso (PowerShell COMO ADMINISTRADOR, en la pantalla):
#     .\instalar-pilotx.ps1 -Paquete C:\Users\...\Downloads\1.0.51.bin
#
# Opcionales:
#     -Instalacion C:\PilotX     donde esta instalado (default C:\PilotX)
#     -Sha <hash>                verifica el paquete antes de tocar nada
#     -SinFirewall               no correr setup_pilotx_lan.bat
#
# Acepta las dos cosas y se da cuenta solo de cual es:
#
#   PAQUETE COMPLETO (~190 MB): reemplaza Desktop, Engine y BarsHost enteras.
#   Es lo que hay que usar para instalar de cero o para venir de una version
#   vieja. El respaldo se hace con un rename, que es instantaneo.
#
#   PARCHE (~5 MB): trae solo lo que cambio. Se aplica ENCIMA, sin borrar nada,
#   y exige que la instalacion sea exactamente la version base para la que se
#   armo. Si no coincide se rechaza sin tocar nada: los ensamblados se
#   referencian por version exacta y mezclarlos deja la pantalla sin arrancar.
#   El respaldo copia solo los archivos que el parche va a pisar.
#
# Pase lo que pase, el ultimo paso es ABRIR LA PANTALLA para comprobar que
# funciona. Si no abre, se deshace y queda la version anterior andando.
#
# Por que existe: la 1.0.48 se publico sin que nadie la hubiera ejecutado una
# sola vez, no arrancaba, y dejo una pantalla sin poder trabajar en plena
# campana. Recuperarla fueron horas de comandos a ciegas.
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
$barra = [char]47

# Rutas ABSOLUTAS antes de cualquier otra cosa.
# PowerShell y .NET no comparten el directorio actual: los cmdlets (Test-Path,
# Get-FileHash) resuelven contra la carpeta donde estas parado, pero cualquier
# llamada .NET -como ZipFile::OpenRead- resuelve contra el directorio del
# proceso, que suele ser C:\Windows\System32. Pasar ".\paquete.bin" hacia que
# el hash verificara bien y dos lineas despues fallara diciendo que el archivo
# no existe en System32. Confuso y facil de repetir, asi que se normaliza aca
# una sola vez.
if (Test-Path -LiteralPath $Paquete) {
    $Paquete = (Resolve-Path -LiteralPath $Paquete).Path
}
$Instalacion = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Instalacion)

function Paso($n, $txt) { Write-Host "`n[$n] $txt" -ForegroundColor Cyan }
function Bien($txt) { Write-Host "    OK: $txt" -ForegroundColor Green }
function Mal($txt) { Write-Host "    ERROR: $txt" -ForegroundColor Red }

function VersionCorta($v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return "" }
    $p = $v.Trim().Split(".")
    if ($p.Length -lt 3) { return $v.Trim() }
    return "$($p[0]).$($p[1]).$($p[2])"
}

# Carpetas de programa. Todo lo que NO este aca sobrevive siempre: la
# configuracion, los lotes, los logs y los respaldos.
$carpetasPrograma = @("Desktop", "Engine", "BarsHost")
$exe = Join-Path $Instalacion "Desktop\PilotX.Desktop.exe"

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

$entradas = @($z.Entries | Where-Object { -not $_.FullName.EndsWith($barra) })

# Instalada actualmente (puede no haber nada: instalacion nueva)
$instalada = $null
if (Test-Path $exe) { $instalada = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion }

# Es parche? -> parche.json en la raiz
$esParche = $false
$manif = $z.GetEntry("parche.json")
if ($manif) {
    $esParche = $true
    $sr = New-Object System.IO.StreamReader($manif.Open())
    $json = $sr.ReadToEnd(); $sr.Dispose()
    $vBase = ([regex]'"version_base"\s*:\s*"([^"]*)"').Match($json).Groups[1].Value
    $vNueva = ([regex]'"version_nueva"\s*:\s*"([^"]*)"').Match($json).Groups[1].Value

    Write-Host "    tipo: PARCHE  ($vBase -> $vNueva, $($entradas.Count) archivos)"

    if (-not $instalada) {
        $z.Dispose()
        Mal "esto es un parche para la version $vBase, pero aca no hay ninguna instalacion."
        Write-Host "    Instala primero el paquete completo." -ForegroundColor Yellow
        exit 1
    }
    if ((VersionCorta $instalada) -ne (VersionCorta $vBase)) {
        $z.Dispose()
        Mal "este parche es para la version $vBase y este equipo tiene la $(VersionCorta $instalada)."
        Write-Host "    Aplicarlo dejaria la pantalla sin arrancar. Hace falta el paquete" -ForegroundColor Yellow
        Write-Host "    completo de la $vNueva. No se toco nada." -ForegroundColor Yellow
        exit 1
    }
    Bien "parche valido para esta instalacion"
} else {
    if ($entradas.Count -lt 100) { $z.Dispose(); Mal "el paquete tiene solo $($entradas.Count) archivos. Descarga incompleta."; exit 1 }
    Write-Host "    tipo: PAQUETE COMPLETO  ($($entradas.Count) archivos)"
    if ($instalada) { Write-Host "    version actual en el equipo: $instalada" }
}

# ---------------------------------------------------------------------------
Paso 2 "Cerrando PilotX"
# ---------------------------------------------------------------------------
function CerrarTodo {
    Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Lanzar-PilotX*' } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch { } }
    foreach ($n in @("PilotX.Desktop", "PilotX.GuidanceEngine", "PilotX.Bars.Host", "WerFault", "WerFaultSecure")) {
        Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    }
    Start-Sleep -Seconds 4
}
CerrarTodo
Bien "procesos cerrados"

# ---------------------------------------------------------------------------
Paso 3 "Respaldando"
# ---------------------------------------------------------------------------
$marca = (Get-Date).ToString("yyyyMMdd-HHmmss")
$respaldos = @{}                 # paquete completo: carpeta -> carpeta renombrada
$respPatch = Join-Path $Instalacion "AgroParallel\Respaldos\parche-$marca"
$nuevosDelParche = @()           # archivos que el parche crea y antes no existian

if ($esParche) {
    # Copiar solo lo que el parche va a pisar. Son pocos archivos y unos MB.
    New-Item -ItemType Directory -Path $respPatch -Force | Out-Null
    $copiados = 0
    foreach ($e in $entradas) {
        if ($e.FullName -eq "parche.json") { continue }
        $dst = Join-Path $Instalacion ($e.FullName.Replace($barra, $sep))
        if (Test-Path $dst) {
            $bak = Join-Path $respPatch ($e.FullName.Replace($barra, $sep))
            New-Item -ItemType Directory -Path (Split-Path $bak) -Force | Out-Null
            Copy-Item $dst $bak -Force
            $copiados++
        } else {
            $nuevosDelParche += $dst
        }
    }
    Bien "$copiados archivos respaldados en $respPatch"
} else {
    # Rename: instantaneo y sin gastar disco.
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
}

function Deshacer {
    Write-Host "`n    Deshaciendo: volviendo a la version anterior..." -ForegroundColor Yellow
    if ($esParche) {
        foreach ($f in Get-ChildItem $respPatch -Recurse -File -ErrorAction SilentlyContinue) {
            $rel = $f.FullName.Substring($respPatch.Length).TrimStart($sep)
            Copy-Item $f.FullName (Join-Path $Instalacion $rel) -Force
        }
        foreach ($n in $nuevosDelParche) { if (Test-Path $n) { Remove-Item $n -Force -ErrorAction SilentlyContinue } }
    } else {
        foreach ($c in $carpetasPrograma) {
            $dst = Join-Path $Instalacion $c
            if ($respaldos.ContainsKey($c)) {
                if (Test-Path $dst) { Remove-Item $dst -Recurse -Force -ErrorAction SilentlyContinue }
                Move-Item $respaldos[$c] $dst -Force
            }
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
        if ($e.FullName -eq "parche.json") { continue }
        $dst = Join-Path $Instalacion ($e.FullName.Replace($barra, $sep))
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
if (-not (Test-Path $exe)) { Mal "no aparecio $exe"; Deshacer; exit 1 }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
Write-Host "    version instalada: $version"

# Todos los ensamblados propios tienen que tener la MISMA version. Mezclar
# versiones es lo que dejo una pantalla sin arrancar: se referencian por
# version exacta y la que quede vieja apunta a algo que ya no existe. En un
# parche esta es LA comprobacion que importa.
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
# Este es el paso por el que existe todo este script.
$errFile = Join-Path $env:TEMP "pilotx_instalar.err.txt"
$p = Start-Process $exe -PassThru -WorkingDirectory $Instalacion -RedirectStandardError $errFile
$murio = $p.WaitForExit(30000)
$txt = ""
try { $txt = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) } catch { }

# Cerrar TODO antes de decidir: la pantalla levanta su propio Engine, que se
# queda con los puertos y los archivos abiertos. Con el Engine vivo, deshacer
# falla por DLL bloqueadas justo cuando mas hace falta.
if ($murio) {
    Mal "la pantalla NO abrio (salio con 0x$("{0:X8}" -f $p.ExitCode))"
    if ($txt -and $txt.Trim()) { Write-Host $txt.Trim() -ForegroundColor Red }
    else { Write-Host "    Sin mensaje. Suele ser un problema del paquete, no de esta pantalla." -ForegroundColor Red }
    CerrarTodo
    Deshacer
    Write-Host "`n    Avisale a Leonardo: el paquete $version no arranca." -ForegroundColor Yellow
    exit 1
}
try { $p.Kill() } catch { }
CerrarTodo
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
if ($esParche) {
    Write-Host ""
    Write-Host " Lo que se piso quedo en $respPatch."
} elseif ($respaldos.Count -gt 0) {
    Write-Host ""
    Write-Host " La version anterior quedo en las carpetas *.anterior-$marca."
    Write-Host " Si todo anda bien manana, se pueden borrar para recuperar disco."
}
