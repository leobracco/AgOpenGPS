# =============================================================================
#  download-ffmpeg.ps1 - Descarga ffmpeg.exe para bundling con el installer
#
#  Bajamos el "essentials" build de gyan.dev (Windows static, single .exe).
#  Verificamos SHA256 contra el archivo .sha256 que publican junto al ZIP.
#  Solo extraemos ffmpeg.exe (no necesitamos ffplay/ffprobe en field).
#
#  Salida: Installer\assets\ffmpeg.exe (~85 MB, no se commitea — ver .gitignore)
#
#  Uso:
#     powershell -NoProfile -ExecutionPolicy Bypass -File download-ffmpeg.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File download-ffmpeg.ps1 -Force   # re-descarga
# =============================================================================

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # acelera Invoke-WebRequest x10

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$AssetsDir = Resolve-Path (Join-Path $ScriptDir "..\assets")
$OutFile   = Join-Path $AssetsDir "ffmpeg.exe"
$TmpDir    = Join-Path $env:TEMP "pilotx-ffmpeg-dl"

# Build "essentials" estatico — el archivo "release" siempre apunta al ultimo
$ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$ShaUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256"

if (-not (Test-Path $AssetsDir)) {
    New-Item -ItemType Directory -Path $AssetsDir | Out-Null
}

if ((Test-Path $OutFile) -and -not $Force) {
    $size = (Get-Item $OutFile).Length / 1MB
    Write-Host ("ffmpeg.exe ya existe ({0:N1} MB). Usa -Force para re-descargar." -f $size) -ForegroundColor Green
    & $OutFile -version 2>&1 | Select-Object -First 1
    exit 0
}

if (Test-Path $TmpDir) { Remove-Item $TmpDir -Recurse -Force }
New-Item -ItemType Directory -Path $TmpDir | Out-Null

try {
    $ZipFile = Join-Path $TmpDir "ffmpeg.zip"
    $ShaFile = Join-Path $TmpDir "ffmpeg.zip.sha256"

    Write-Host "[*] Bajando $ZipUrl ..."
    Invoke-WebRequest -Uri $ZipUrl -OutFile $ZipFile -UseBasicParsing

    Write-Host "[*] Bajando SHA256 sidecar ..."
    Invoke-WebRequest -Uri $ShaUrl -OutFile $ShaFile -UseBasicParsing

    $expected = (Get-Content $ShaFile -Raw).Trim().Split()[0].ToLower()
    $actual   = (Get-FileHash -Path $ZipFile -Algorithm SHA256).Hash.ToLower()

    if ($expected -ne $actual) {
        throw "SHA256 mismatch! expected=$expected actual=$actual"
    }
    Write-Host "[OK] SHA256 verificado ($expected)" -ForegroundColor Green

    Write-Host "[*] Extrayendo ffmpeg.exe ..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipFile)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like "*/bin/ffmpeg.exe" } | Select-Object -First 1
        if (-not $entry) { throw "ffmpeg.exe no encontrado en el ZIP" }
        if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $OutFile, $true)
    } finally {
        $zip.Dispose()
    }

    $finalSize = (Get-Item $OutFile).Length / 1MB
    Write-Host ("[OK] ffmpeg.exe extraido en $OutFile ({0:N1} MB)" -f $finalSize) -ForegroundColor Green
    & $OutFile -version 2>&1 | Select-Object -First 1
}
finally {
    if (Test-Path $TmpDir) { Remove-Item $TmpDir -Recurse -Force -ErrorAction SilentlyContinue }
}
