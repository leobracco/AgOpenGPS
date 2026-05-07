# build-installer.ps1
# Compila los binarios (build.ps1) y luego empaqueta el instalador con Inno Setup.
#
# Requisitos:
#   - .NET 4.8 SDK (dotnet build)
#   - Inno Setup 6 instalado: https://jrsoftware.org/isdl.php
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 1.2.3
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -SkipBuild

param(
    [string]$Version,
    [switch]$Bump,        # auto-incrementa el patch (1.0.5 -> 1.0.6)
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root        = Split-Path -Parent $PSScriptRoot
$iss         = Join-Path $PSScriptRoot "AgroParallelPilot.iss"
$versionFile = Join-Path $PSScriptRoot "VERSION"

# ---------------------------------------------------------------------------
# Resolucion de version:
#   1) Si -Version fue pasado, ese gana y se persiste a VERSION.
#   2) Si -Bump, leer VERSION y +1 al patch (X.Y.Z -> X.Y.(Z+1)).
#   3) Caso contrario, leer VERSION (o usar 1.0.0 si no existe).
# ---------------------------------------------------------------------------
if ($Version) {
    # version explicita - ok
} elseif (Test-Path $versionFile) {
    $Version = (Get-Content $versionFile -Raw).Trim()
    if ($Bump) {
        if ($Version -match '^(\d+)\.(\d+)\.(\d+)$') {
            $Version = "{0}.{1}.{2}" -f $Matches[1], $Matches[2], ([int]$Matches[3] + 1)
        } else {
            Write-Host "VERSION '$Version' invalida (no semver X.Y.Z), reseteando a 1.0.0" -ForegroundColor Yellow
            $Version = "1.0.0"
        }
    }
} else {
    $Version = "1.0.0"
}

# Persistir version usada para la proxima corrida
Set-Content -Path $versionFile -Value $Version -NoNewline
Write-Host "Version: $Version" -ForegroundColor Cyan

# 1) Localizar ISCC.exe (compilador de Inno Setup)
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",  # winget user-scope
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

# Fallback: buscar en PATH
if (-not $iscc) {
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { $iscc = $found.Source }
}

if (-not $iscc) {
    Write-Host "ERROR: Inno Setup no encontrado." -ForegroundColor Red
    Write-Host "Descargalo desde https://jrsoftware.org/isdl.php (recomendado: Inno Setup 6)"
    exit 1
}
Write-Host "ISCC: $iscc" -ForegroundColor DarkGray

# 2) Compilar binarios (a menos que se pida saltearlos)
if (-not $SkipBuild) {
    Write-Host "`n=== Compilando AOG + AgIO ===" -ForegroundColor Cyan
    & (Join-Path $root "build.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "build.ps1 fallo" -ForegroundColor Red
        exit 1
    }
}

# 3) Verificar que la carpeta Build tenga los exes
$buildDir = Join-Path $root "Build"
foreach ($exe in @("AgOpenGPS.exe", "AgIO.exe")) {
    if (-not (Test-Path (Join-Path $buildDir $exe))) {
        Write-Host "ERROR: falta $exe en $buildDir. Corre build.ps1 primero." -ForegroundColor Red
        exit 1
    }
}

# 4) Compilar el instalador
Write-Host "`n=== Empaquetando instalador (v$Version) ===" -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    Write-Host "ISCC fallo" -ForegroundColor Red
    exit 1
}

# 5) Listar salida
$outDir = Join-Path $PSScriptRoot "Output"
Write-Host "`n=== OK ===" -ForegroundColor Green
Get-ChildItem $outDir -Filter "*.exe" | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 1)
    Write-Host ("  {0}  ({1} MB)" -f $_.FullName, $sizeMB) -ForegroundColor White
}
