# build.ps1 - Compila el stack PilotX (Desktop + Engine + BarsHost + tools)
# y copia todo a /Build. El WinForms legacy (PilotX.exe) y AgIO (CoreX.exe)
# se eliminaron del repo el 2026-08-14: el Engine trae el CoreX embebido
# (broker MQTT, bridge UDP, NTRIP, seriales y panel :5181).
param(
    [string]$Config = "Release",
    [string]$OutDir = "$PSScriptRoot\Build",
    [string]$Version,
    [switch]$SinLinux    # saltear el paquete linux-x64 (ciclo rapido)
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

Write-Host "`n=== Build PilotX-KioskSetup ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\Tools\PilotX-KioskSetup\PilotX-KioskSetup.csproj" -c $Config -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "PilotX-KioskSetup FAILED" -ForegroundColor Red; exit 1 }

# El Updater es el helper externo que aplica el ZIP de self-update en sitio.
# Debe viajar en el paquete (PilotXSelfUpdate.ApplyAsync lo lanza desde el
# install dir); sin ÃƒÂ©l, la actualizaciÃƒÂ³n OTA aborta con FileNotFoundException.
Write-Host "`n=== Build AgroParallel.Updater ($Config) ===" -ForegroundColor Cyan
dotnet build "$root\SourceCode\AgroParallel\Tools\AgroParallel.Updater\AgroParallel.Updater.csproj" -c $Config -v q
if ($LASTEXITCODE -ne 0) { Write-Host "AgroParallel.Updater FAILED" -ForegroundColor Red; exit 1 }

# BenchX: simulador de banco (ex ModSim) Ã¢â‚¬â€ GPS/NMEA + mÃƒÂ³dulos por UDP :8888.
# Se publica a Build\BenchX\ (framework-dependent net9); NO viaja en el ZIP
# de release (ver $skipDirs): con el CoreX embebido arma un lazo de eco UDP.
Write-Host "`n=== Publish BenchX ($Config) ===" -ForegroundColor Cyan
dotnet publish "$root\SourceCode\BenchX\BenchX.csproj" -c $Config -o "$OutDir\BenchX" -v q $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "BenchX FAILED" -ForegroundColor Red; exit 1 }

# PilotX.Bars.Host: proceso Avalonia standalone que dibuja las barras nativas
# (reemplazo liviano de las barras WebView2). Se publica self-contained porque
# corre como proceso hijo separado de PilotX y FormGPS.ResolveBarsHostExe lo
# busca en <baseDir>/BarsHost/PilotX.Bars.Host.exe en produccion.
Write-Host "`n=== Publish PilotX.Bars.Host ($Config) ===" -ForegroundColor Cyan
dotnet publish "$root\SourceCode\PilotX.Bars.Host\PilotX.Bars.Host.csproj" `
    -c $Config -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o "$OutDir\BarsHost" $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "PilotX.Bars.Host FAILED" -ForegroundColor Red; exit 1 }

# --- Stack Avalonia (el que reemplaza al WinForms en la cabina) --------------
# PilotX.GuidanceEngine: motor de guiado headless + API :5180. Es el backend de
# PilotX.Desktop. El Desktop lo lanza con --webhost --corex (CoreX embebido:
# broker MQTT :1883, bridge UDP, NTRIP y panel :5181). Lee su perfil de
# vehiculo del aog_settings.json de la instalacion, un nivel arriba.
Write-Host "`n=== Publish PilotX.GuidanceEngine ($Config) ===" -ForegroundColor Cyan
dotnet publish "$root\SourceCode\PilotX.GuidanceEngine\PilotX.GuidanceEngine.csproj" `
    -c $Config -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o "$OutDir\Engine" $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "PilotX.GuidanceEngine FAILED" -ForegroundColor Red; exit 1 }

# PilotX.Desktop: la UI Avalonia nativa (mapa GL + barras + pantallas). Igual que
# BarsHost, self-contained: la pantalla de la cabina no tiene runtime .NET 9 y no
# queremos que el arranque dependa de instalarlo.
Write-Host "`n=== Publish PilotX.Desktop ($Config) ===" -ForegroundColor Cyan
dotnet publish "$root\SourceCode\PilotX.Desktop\PilotX.Desktop.csproj" `
    -c $Config -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -o "$OutDir\Desktop" $verArg
if ($LASTEXITCODE -ne 0) { Write-Host "PilotX.Desktop FAILED" -ForegroundColor Red; exit 1 }

# Crear directorio de salida
if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# Hub wwwroot: la fuente de verdad es el source. Se ESPEJA a
# Build\AgroParallel\wwwroot (el Engine lo sirve desde <install>\AgroParallel\
# wwwroot). Antes lo arrastraba la copia del bin WinForms Ã¢â‚¬â€ eliminada
# 2026-08-14 Ã¢â‚¬â€ y sin esta linea el ZIP viajaba con un Hub viejo.
Write-Host "Copiando wwwroot del Hub..." -ForegroundColor Yellow
robocopy "$root\SourceCode\AgroParallel\Web\AgroParallel.WebUI\wwwroot" `
    "$OutDir\AgroParallel\wwwroot" /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host "wwwroot copy FAILED" -ForegroundColor Red; exit 1 }
$global:LASTEXITCODE = 0

# Copiar PilotX-KioskSetup.exe (utility para configurar kiosko en tractor)
$kioskBin = "$root\Tools\PilotX-KioskSetup\bin\$Config\net48"
if (Test-Path $kioskBin) {
    Write-Host "Copiando PilotX-KioskSetup..." -ForegroundColor Yellow
    Get-ChildItem $kioskBin -File -Filter "PilotX-KioskSetup.*" | ForEach-Object {
        Copy-Item $_.FullName -Destination $OutDir -Force
    }
}

# Copiar AgroParallel.Updater (helper de self-update Ã¢â‚¬â€ lo lanza PilotXSelfUpdate)
$updBin = "$root\SourceCode\AgroParallel\Tools\AgroParallel.Updater\bin\$Config\win-x64"
if (Test-Path $updBin) {
    Write-Host "Copiando AgroParallel.Updater..." -ForegroundColor Yellow
    Get-ChildItem $updBin -File -Filter "AgroParallel.Updater.*" | ForEach-Object {
        Copy-Item $_.FullName -Destination $OutDir -Force
    }
}

# ----------------------------------------------------------------------------
# Paquete LINUX (linux-x64, self-contained). Mismo layout que el de Windows:
# Desktop/ Engine/ BarsHost/ AgroParallel/wwwroot + pilotx.sh. Sin ReadyToRun:
# el crossgen cruzado WindowsÃ¢â€ â€™Linux alarga el build y no se validÃƒÂ³ en cabina.
# Se saltea con -SinLinux (p. ej. para el ciclo rapido de taller).
# ----------------------------------------------------------------------------
if (-not $SinLinux) {
    $linuxDir = "$OutDir\Linux"
    Write-Host "`n=== Publish linux-x64 (Engine + BarsHost + Desktop + Updater) ===" -ForegroundColor Cyan
    dotnet publish "$root\SourceCode\PilotX.GuidanceEngine\PilotX.GuidanceEngine.csproj" `
        -c $Config -r linux-x64 --self-contained true -o "$linuxDir\Engine" -v q $verArg -p:PublishReadyToRun=false -p:PublishReadyToRunComposite=false
    if ($LASTEXITCODE -ne 0) { Write-Host "Engine linux FAILED" -ForegroundColor Red; exit 1 }
    dotnet publish "$root\SourceCode\PilotX.Bars.Host\PilotX.Bars.Host.csproj" `
        -c $Config -r linux-x64 --self-contained true -o "$linuxDir\BarsHost" -v q $verArg -p:PublishReadyToRun=false -p:PublishReadyToRunComposite=false
    if ($LASTEXITCODE -ne 0) { Write-Host "BarsHost linux FAILED" -ForegroundColor Red; exit 1 }
    dotnet publish "$root\SourceCode\PilotX.Desktop\PilotX.Desktop.csproj" `
        -c $Config -r linux-x64 --self-contained true -o "$linuxDir\Desktop" -v q $verArg -p:PublishReadyToRun=false -p:PublishReadyToRunComposite=false
    if ($LASTEXITCODE -ne 0) { Write-Host "Desktop linux FAILED" -ForegroundColor Red; exit 1 }
    dotnet publish "$root\SourceCode\AgroParallel\Tools\AgroParallel.Updater\AgroParallel.Updater.csproj" -f net9.0 `
        -c $Config -r linux-x64 --self-contained true -o "$linuxDir\Updater-tmp" -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "Updater linux FAILED" -ForegroundColor Red; exit 1 }
    Get-ChildItem "$linuxDir\Updater-tmp" -File | Copy-Item -Destination $linuxDir -Force
    Remove-Item "$linuxDir\Updater-tmp" -Recurse -Force

    robocopy "$root\SourceCode\AgroParallel\Web\AgroParallel.WebUI\wwwroot" `
        "$linuxDir\AgroParallel\wwwroot" /MIR /NFL /NDL /NJH /NJS | Out-Null
    $global:LASTEXITCODE = 0
    Copy-Item "$root\Tools\linux\pilotx.sh" -Destination $linuxDir -Force
    Copy-Item "$root\Tools\linux\README-linux.md" -Destination $linuxDir -Force

    # tar.gz (bsdtar de Windows 10+): conserva el formato que Linux espera.
    # Los permisos +x no viajan desde NTFS Ã¢â‚¬â€ pilotx.sh se los da al arrancar.
    $tarPath = Join-Path $root ("PilotX_linux_v" + $Version + ".tar.gz")
    if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
    tar -czf $tarPath -C $linuxDir .
    if ($LASTEXITCODE -ne 0) { Write-Host "tar linux FAILED" -ForegroundColor Red; exit 1 }
    Write-Host ("Linux: " + $tarPath) -ForegroundColor Green
}

Write-Host "`n=== Build OK === Output: $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Filter "*.exe" | ForEach-Object { Write-Host "  $_" -ForegroundColor White }

# ----------------------------------------------------------------------------
# Empaquetado para deploy: produce PilotX_v<version>.zip listo para
#   (a) subir al panel OrbitX Ã¢â€ â€™ /firmwares (producto = PilotX)  -> OTA cloud
#   (b) copiar a USB y dejar que el Updater lo aplique en sitio
# La raÃƒÂ­z del ZIP corresponde al install dir del tractor (sin wrapper).
# Excluimos basura de build (.pdb, vshost) y subdirs de runtime (Updates/,
# Backups/, WebView2Data/).
#
# NO usamos Compress-Archive: abre cada origen con FileShare.Read y revienta con
# "seccion asignada a usuario abierta" si algun archivo del Build tiene una
# seccion memory-mapped (DLLs retenidas por build-servers de dotnet, leveldb de
# WebView2, o el AV escaneando). Armamos el ZIP a mano leyendo con
# FileShare.ReadWrite|Delete, que tolera esos locks.
# ----------------------------------------------------------------------------
$zipPath = Join-Path $root ("PilotX_v" + $Version + ".zip")
Write-Host "`n=== Empaquetando $zipPath ===" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

# El paquete de release lleva SOLO binarios + estÃƒÂ¡ticos (wwwroot). NUNCA
# configuraciones de runtime: si el Build local acumulÃƒÂ³ configs por haber
# corrido PilotX acÃƒÂ¡, extraerlos sobre una pantalla en uso le PISA la config
# del cliente (vistaX.json, perfil, overlays, etc.). Se excluyen:
#  - dirs de datos/cache de runtime (firmware-cache, data, Fields, Logs...)
#  - backups y logs (.bak, .log) y flags de runtime (.on, ej barras-html.on)
#  - todos los .json de config que viven en la RAÃƒÂZ del install dir
#    (los .json legÃƒÂ­timos del release estÃƒÂ¡n en subdirs: wwwroot, runtimes...)
$skipDirs = @('Updates','Backups','WebView2Data','firmware-cache',
              'data','implementos','Fields','Vehicles','Logs','Profiles',
              'PilotXDesktop','BenchX','Linux')
$skipExt  = @('.pdb','.bak','.log','.on')
# Exes que NO viajan a una pantalla: BenchX vÃƒÂ­a $skipDirs (simulador de banco;
# con el CoreX embebido del engine arma un lazo de eco UDP que infla el proceso
# a GBs) y createdump (herramienta de debug de .NET, puro peso).
$skipFiles = @('createdump.exe')
$files = Get-ChildItem $OutDir -Recurse -File -Force | Where-Object {
    $rel   = $_.FullName.Substring($OutDir.Length + 1)
    $parts = $rel.Split([IO.Path]::DirectorySeparatorChar)
    $esConfigRaiz = ($parts.Length -eq 1) -and ($_.Extension.ToLower() -eq '.json')
    (-not ($parts | Where-Object { $skipDirs -contains $_ })) -and
    ($skipExt -notcontains $_.Extension.ToLower()) -and
    (-not $esConfigRaiz) -and
    ($skipFiles -notcontains $_.Name) -and
    ($_.Name -notlike '*.vshost.*') -and ($_.Name -ne 'updater.log')
}

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $rel   = $f.FullName.Substring($OutDir.Length + 1).Replace('\','/')
        $fs    = [System.IO.File]::Open($f.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
                                        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        try {
            $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
            $es = $entry.Open()
            try { $fs.CopyTo($es) } finally { $es.Dispose() }
        } finally { $fs.Dispose() }
    }
} finally { $zip.Dispose() }

# SHA256 para subir al cloud junto al ZIP (verifica integridad post-OTA).
$sha = (Get-FileHash -Algorithm SHA256 $zipPath).Hash
$shaPath = "$zipPath.sha256"
"$sha  $(Split-Path -Leaf $zipPath)" | Out-File -FilePath $shaPath -Encoding ascii
Write-Host ("ZIP : " + $zipPath) -ForegroundColor Green
Write-Host ("SHA : " + $sha)    -ForegroundColor Green
Write-Host "`nSubilo a OrbitX /firmwares (producto=PilotX, version=$Version) o copialo al tractor por USB." -ForegroundColor Yellow
