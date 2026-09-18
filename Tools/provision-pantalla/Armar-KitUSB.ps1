# Armar-KitUSB.ps1 — junta todo el kit de aprovisionamiento en una carpeta/USB.
# Correr en la PC de desarrollo:
#   powershell -ExecutionPolicy Bypass -File .\Armar-KitUSB.ps1 -Destino E:\KitPilotX
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Destino)

$ErrorActionPreference = "Stop"
$aqui = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $aqui "..\..")).Path   # raíz AgOpenGPS

New-Item -ItemType Directory -Path $Destino -Force | Out-Null
function Paso($t) { Write-Host ">> $t" -ForegroundColor Cyan }

# 1. Script de aprovisionamiento + README
Copy-Item (Join-Path $aqui "Provision-Pantalla.ps1") $Destino -Force
Copy-Item (Join-Path $aqui "README.md") $Destino -Force
Paso "Script y README copiados"

# 2. Branding (logo + fondo)
$brandDst = Join-Path $Destino "Branding"
New-Item -ItemType Directory -Path $brandDst -Force | Out-Null
$fuentes = @(
    (Join-Path $repo "Build\Branding\logo.png"),
    (Join-Path $repo "Build\Branding\logo-fondo-blanco.png"),
    (Join-Path $repo "Build\AgroParallel\wwwroot\img\fondo.png")
)
foreach ($f in $fuentes) {
    if (Test-Path $f) { Copy-Item $f $brandDst -Force; Paso "Branding: $(Split-Path -Leaf $f)" }
    else { Write-Warning "No encontrado: $f" }
}

# 3. KioskSetup
$kiosk = Join-Path $repo "Tools\PilotX-KioskSetup\bin\Release\net48\PilotX-KioskSetup.exe"
if (Test-Path $kiosk) { Copy-Item $kiosk $Destino -Force; Paso "PilotX-KioskSetup.exe copiado" }
else { Write-Warning "Falta PilotX-KioskSetup.exe — compilarlo: dotnet build Tools\PilotX-KioskSetup -c Release" }

# 4. Último build de PilotX (ZIP)
$zip = Get-ChildItem (Join-Path $repo "Build") -Filter "PilotX_v*.zip" -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($zip) { Copy-Item $zip.FullName $Destino -Force; Paso "Build: $($zip.Name)" }
else { Write-Warning "No hay PilotX_v*.zip en Build\ — correr build.ps1 primero" }

# 5. Runtimes (descarga si hay internet y no están ya en el destino)
$descargas = @{
    "vc_redist.x64.exe"               = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
    "MicrosoftEdgeWebview2Setup.exe"  = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
}
foreach ($nombre in $descargas.Keys) {
    $dst = Join-Path $Destino $nombre
    if (Test-Path $dst) { Paso "$nombre ya estaba"; continue }
    try {
        Invoke-WebRequest $descargas[$nombre] -OutFile $dst -UseBasicParsing
        Paso "$nombre descargado"
    } catch {
        Write-Warning "No pude bajar $nombre — bajarlo a mano: $($descargas[$nombre])"
    }
}

Write-Host "`nKit listo en $Destino" -ForegroundColor Green
