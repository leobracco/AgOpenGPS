# build-android.ps1 - Compila el APK de PilotX.Android (Release) y lo deja en
# la raiz del repo como PilotX_android_v<version>.apk + .sha256, gemelo de lo
# que build.ps1 hace con el ZIP de Windows.
#
# Por que existe: el proyecto Android comparte el 90 % del codigo con el
# Desktop pero NO esta en AgOpenGPS.sln ni en build.ps1, asi que nadie lo
# compilaba entre releases y las interfaces se le adelantaban (2026-09-03:
# ICoverageService sumo GetSnapshot(string) y el APK dejo de compilar). Correr
# esto en cada release mantiene el APK vivo.
#
# Requisitos (una sola vez): workload android del SDK 9 + Android SDK (API 35)
# + JDK 17. Ver SourceCode/PilotX.Android/README.md.
param(
    [string]$Config = "Release",
    [string]$Version,
    [string]$AndroidSdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$JavaSdk    = "$env:LOCALAPPDATA\Android\Jdk",
    [switch]$Install     # ademas instalar en la tablet conectada por USB (adb)
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Version: la misma fuente de verdad que build.ps1 (Installer/VERSION).
if (-not $Version) {
    $versionFile = Join-Path $root "Installer\VERSION"
    $Version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "1.0.0" }
}
# versionCode de Android: entero creciente (Play/PackageInstaller rechazan
# un APK con versionCode menor al instalado). MAJOR*10000 + MINOR*100 + PATCH.
$parts = $Version.Split('.') | ForEach-Object { [int]($_ -replace '[^0-9].*$','') }
while ($parts.Count -lt 3) { $parts += 0 }
$versionCode = $parts[0] * 10000 + $parts[1] * 100 + $parts[2]

if ($env:ANDROID_HOME -and -not (Test-Path $AndroidSdk)) { $AndroidSdk = $env:ANDROID_HOME }
if (-not (Test-Path $AndroidSdk)) { Write-Host "No se encontro el Android SDK en $AndroidSdk (pasar -AndroidSdk o setear ANDROID_HOME)" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $JavaSdk))    { Write-Host "No se encontro el JDK en $JavaSdk (pasar -JavaSdk)" -ForegroundColor Red; exit 1 }

Write-Host "Stamping APK con version: $Version (versionCode $versionCode)" -ForegroundColor Cyan
Write-Host "SDK: $AndroidSdk  JDK: $JavaSdk" -ForegroundColor DarkGray

$proj = Join-Path $root "SourceCode\PilotX.Android\PilotX.Android.csproj"
$targets = @("Build")
if ($Install) { $targets += "Install" }

Write-Host "`n=== Build PilotX.Android ($Config, apk) ===" -ForegroundColor Cyan
dotnet build $proj -c $Config -v q `
    -t:($targets -join ';') `
    -p:AndroidPackageFormat=apk `
    -p:AndroidSdkDirectory="$AndroidSdk" `
    -p:JavaSdkDirectory="$JavaSdk" `
    -p:ApplicationDisplayVersion=$Version `
    -p:ApplicationVersion=$versionCode
if ($LASTEXITCODE -ne 0) { Write-Host "PilotX.Android FAILED" -ForegroundColor Red; exit 1 }

# El APK firmado (debug keystore en Release tambien, como hasta ahora).
$binDir = Join-Path $root "SourceCode\PilotX.Android\bin\$Config\net9.0-android"
$apk = Get-ChildItem $binDir -Filter "*-Signed.apk" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { Write-Host "No se encontro *-Signed.apk en $binDir" -ForegroundColor Red; exit 1 }

$dst = Join-Path $root ("PilotX_android_v" + $Version + ".apk")
Copy-Item $apk.FullName $dst -Force
$sha = (Get-FileHash -Algorithm SHA256 $dst).Hash
"$sha  $(Split-Path -Leaf $dst)" | Out-File -FilePath "$dst.sha256" -Encoding ascii

Write-Host "`n=== Build OK ===" -ForegroundColor Green
Write-Host ("APK : " + $dst) -ForegroundColor Green
Write-Host ("SHA : " + $sha) -ForegroundColor Green
Write-Host "`nSubilo a OrbitX /firmwares (producto=PilotXAndroid, version=$Version) o instalalo con adb install -r." -ForegroundColor Yellow
