# build.ps1 - Compila AgIO + AgOpenGPS en Release y copia todo a /Build
param(
    [string]$Config = "Release",
    [string]$OutDir = "$PSScriptRoot\Build"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "`n=== Build AgIO ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\SourceCode\AgIO\Source\AgIO.csproj" -c $Config -v q
if ($LASTEXITCODE -ne 0) { Write-Host "AgIO FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== Build AgOpenGPS ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\SourceCode\GPS\AgOpenGPS.csproj" -c $Config -v q
if ($LASTEXITCODE -ne 0) { Write-Host "AgOpenGPS FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n=== Build PilotX-KioskSetup ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\Tools\PilotX-KioskSetup\PilotX-KioskSetup.csproj" -c $Config -v q
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
