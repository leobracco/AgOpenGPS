# build.ps1 - Compila AgIO + AgOpenGPS en Release y copia todo a /Build
param(
    [string]$Config = "Release",
    [string]$OutDir = "$PSScriptRoot\Build",
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Resolver version: si no la pasaron, leer Installer/VERSION (fuente de verdad).
# Se la pasamos a dotnet build via -p:Version=, asi queda estampada en el
# AssemblyInformationalVersionAttribute del .exe. PilotXSelfUpdate la lee en
# runtime con Assembly.GetEntryAssembly() para comparar contra el catalogo OTA.
if (-not $Version) {
    $versionFile = Join-Path $root "Installer\VERSION"
    if (Test-Path $versionFile) {
        $Version = (Get-Content $versionFile -Raw).Trim()
    } else {
        $Version = "1.0.0"
    }
}
$verArg = "-p:Version=$Version"
Write-Host "Stamping build con version: $Version" -ForegroundColor Cyan

Write-Host "`n=== Build AgIO ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\SourceCode\AgIO\Source\AgIO.csproj" -c $Config -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "AgIO FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== Build AgOpenGPS ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\SourceCode\GPS\AgOpenGPS.csproj" -c $Config -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "AgOpenGPS FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== Build PilotX-KioskSetup ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\Tools\PilotX-KioskSetup\PilotX-KioskSetup.csproj" -c $Config -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "PilotX-KioskSetup FAILED" -ForegroundColor Red; exit 1 }

# Crear directorio de salida
if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# Copiar AgOpenGPS (tiene mas archivos, va primero)
$aogBin = "$root\SourceCode\GPS\bin\$Config\win-x64"
if (Test-Path $aogBin) {
    Write-Host "`nCopiando AgOpenGPS..." -ForegroundColor Yellow
    Copy-Item "$aogBin\*" -Destination $OutDir -Recurse -Force
}

# Copiar PilotX-KioskSetup.exe (utility para configurar kiosko en tractor)
$kioskBin = "$root\Tools\PilotX-KioskSetup\bin\$Config\net48"
if (Test-Path $kioskBin) {
    Write-Host "Copiando PilotX-KioskSetup..." -ForegroundColor Yellow
    Get-ChildItem $kioskBin -File -Filter "PilotX-KioskSetup.*" | ForEach-Object {
        Copy-Item $_.FullName -Destination $OutDir -Force
    }
}

# Copiar AgIO encima (no sobreescribe DLLs comunes mas nuevas)
$aioBin = "$root\SourceCode\AgIO\Source\bin\$Config"
if (Test-Path $aioBin) {
    Write-Host "Copiando AgIO..." -ForegroundColor Yellow
    Get-ChildItem $aioBin -File | ForEach-Object {
        $dest = Join-Path $OutDir $_.Name
        # Solo copiar si no existe o es mas nuevo
        if (!(Test-Path $dest) -or ($_.LastWriteTime -gt (Get-Item $dest).LastWriteTime)) {
            Copy-Item $_.FullName -Destination $dest -Force
        }
    }
}

Write-Host "`n=== Build OK === Output: $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Filter "*.exe" | ForEach-Object { Write-Host "  $_" -ForegroundColor White }

# ----------------------------------------------------------------------------
# Empaquetado para deploy: produce PilotX-<version>.zip listo para
#   (a) subir al panel OrbitX → /firmwares (producto = PilotX)  -> OTA cloud
#   (b) copiar a USB y dejar que el Updater lo aplique en sitio
# La raíz del ZIP corresponde al install dir del tractor (sin wrapper).
# Excluimos basura de build (.pdb, vshost, Updates/, Backups/).
# ----------------------------------------------------------------------------
$zipPath = Join-Path $root ("PilotX-" + $Version + ".zip")
Write-Host "`n=== Empaquetando $zipPath ===" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Staging temporal con exclusiones (Compress-Archive no soporta -Exclude recursivo)
$staging = Join-Path $env:TEMP ("pilotx-pack-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $exclude = @('*.pdb','*.vshost.*','updater.log')
    Copy-Item "$OutDir\*" -Destination $staging -Recurse -Force -Exclude $exclude
    # Limpieza de subdirs runtime que no van al ZIP
    foreach ($d in 'AgroParallel\Updates','AgroParallel\Backups','AgroParallel\WebView2Data') {
        $p = Join-Path $staging $d
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
    Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -CompressionLevel Optimal
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

# SHA256 para subir al cloud junto al ZIP (verifica integridad post-OTA).
$sha = (Get-FileHash -Algorithm SHA256 $zipPath).Hash
$shaPath = "$zipPath.sha256"
"$sha  $(Split-Path -Leaf $zipPath)" | Out-File -FilePath $shaPath -Encoding ascii
Write-Host ("ZIP : " + $zipPath) -ForegroundColor Green
Write-Host ("SHA : " + $sha)    -ForegroundColor Green
Write-Host "`nSubilo a OrbitX /firmwares (producto=PilotX, version=$Version) o copialo al tractor por USB." -ForegroundColor Yellow
