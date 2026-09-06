# build.ps1 - Compila el stack PilotX (Desktop + Engine + BarsHost + tools)
# y copia todo a /Build. El WinForms legacy (PilotX.exe) y AgIO (CoreX.exe)
# se eliminaron del repo el 2026-08-14: el Engine trae el CoreX embebido
# (broker MQTT, bridge UDP, NTRIP, seriales y panel :5181).
param(
    [string]$Config = "Release",
    [string]$OutDir = "$PSScriptRoot\Build",
    [string]$Version,
    [switch]$SinLinux,   # saltear el paquete linux-x64 (ciclo rapido)
    [switch]$SkipSmoke   # saltear la prueba de arranque (maquina sin sesion grafica)
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
# ----------------------------------------------------------------------------
# LIMPIAR las carpetas de publicacion antes de generarlas.
#
# `dotnet publish -o` NO borra lo que ya habia: escribe encima y deja
# conviviendo los archivos de builds anteriores. Build\Desktop llego a tener
# 172 archivos de un build y 53 de otro al mismo tiempo.
#
# Eso fue lo que rompio la 1.0.48: se publico con ReadyToRun apagado, pero
# quedaron las DLL CON ReadyToRun del build anterior. La mezcla compila y
# empaqueta sin quejarse, y muere al arrancar con FailFast y sin mensaje.
# Termino instalada en el tractor de un cliente.
#
# Solo se borran las tres carpetas que `dotnet publish` regenera enteras. No
# se toca AgroParallel\wwwroot (lo espeja robocopy /MIR), ni Branding, ni
# Fonts, ni config-captures, que vienen de otro lado.
# ----------------------------------------------------------------------------
foreach ($sub in @("Desktop", "Engine", "BarsHost")) {
    $dir = Join-Path $OutDir $sub
    if (Test-Path $dir) {
        Write-Host "Limpiando $sub\ (evita mezclar builds)" -ForegroundColor DarkGray
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

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

# Herramientas del flasheo por USB: esptool + drivers USB-serial.
# Viajan en el build para que la pantalla flashee sin internet.
$toolsSrc = Join-Path $root "Tools"
$toolsDst = Join-Path $OutDir "Engine\tools"
foreach ($t in @("esptool", "usb-drivers")) {
    $src = Join-Path $toolsSrc $t
    if (Test-Path $src) {
        $dst = Join-Path $toolsDst $t
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item "$src\*" $dst -Recurse -Force
        Write-Host "Copiado tools/$t -> $dst" -ForegroundColor DarkGray
    }
}

# PilotX.Desktop: la UI Avalonia nativa (mapa GL + barras + pantallas). Igual que
# BarsHost, self-contained: la pantalla de la cabina no tiene runtime .NET 9 y no
# queremos que el arranque dependa de instalarlo.
Write-Host "`n=== Publish PilotX.Desktop ($Config) ===" -ForegroundColor Cyan
dotnet publish "$root\SourceCode\PilotX.Desktop\PilotX.Desktop.csproj" `
    -c $Config -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:PublishReadyToRunComposite=false -o "$OutDir\Desktop" $verArg
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

# Copiar setup_pilotx_lan.bat (abre los puertos del firewall en el tractor).
# NO viajaba en el ZIP hasta 1.0.49: vivia solo en el repo, asi que la regla
# de UDP 9999 que necesita el ToolX nunca llegaba a una maquina de cliente.
# El sintoma era enganoso: el nodo aparecia CONECTADO por MQTT (TCP 1883 si
# tenia regla) mientras su PGN se descartaba en silencio y la herramienta no
# pintaba.
$lanBat = "$root\setup_pilotx_lan.bat"
if (Test-Path $lanBat) {
    Write-Host "Copiando setup_pilotx_lan.bat..." -ForegroundColor Yellow
    Copy-Item $lanBat -Destination $OutDir -Force
} else {
    Write-Host "AVISO: falta setup_pilotx_lan.bat, el paquete va sin el." -ForegroundColor Red
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
    # Single-file: un binario en la raiz del paquete (PilotXSelfUpdate lo busca
    # como <install>/AgroParallel.Updater), sin desparramar el runtime.
    dotnet publish "$root\SourceCode\AgroParallel\Tools\AgroParallel.Updater\AgroParallel.Updater.csproj" -f net9.0 `
        -c $Config -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$linuxDir\Updater-tmp" -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "Updater linux FAILED" -ForegroundColor Red; exit 1 }
    Copy-Item "$linuxDir\Updater-tmp\AgroParallel.Updater" -Destination $linuxDir -Force
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

# ----------------------------------------------------------------------------
# PRUEBA DE ARRANQUE — no empaquetar algo que no abre.
#
# Por que existe: la 1.0.48 se publico, se subio a OrbitX y se instalo en el
# tractor de un cliente ANTES de que nadie la ejecutara una sola vez. Moria al
# arrancar con FailFast (0xC0000602) y sin ningun mensaje, por una combinacion
# de flags de compilacion. Dejo una pantalla sin poder trabajar.
#
# El build compila sin errores igual, asi que compilar no prueba nada: hay que
# ABRIR la aplicacion. Si no levanta y sigue viva unos segundos, el build falla
# aca y no se genera el ZIP.
#
# Con -SkipSmoke se saltea (maquina sin GPU/sesion grafica).
# ----------------------------------------------------------------------------
if (-not $SkipSmoke) {
    Write-Host "`n=== Prueba de arranque de PilotX.Desktop ===" -ForegroundColor Cyan
    $smokeExe = Join-Path $OutDir "Desktop\PilotX.Desktop.exe"
    if (-not (Test-Path $smokeExe)) {
        Write-Host "No existe $smokeExe" -ForegroundColor Red; exit 1
    }
    $smokeErr = Join-Path $env:TEMP "pilotx_smoke.err.txt"
    $sp = Start-Process $smokeExe -PassThru -WorkingDirectory $OutDir -RedirectStandardError $smokeErr
    $murio = $sp.WaitForExit(25000)
    $salida = ""
    try { $salida = (Get-Content $smokeErr -Raw -ErrorAction SilentlyContinue) } catch { }
    if ($murio) {
        Write-Host "FALLO: PilotX.Desktop murio al arrancar (0x$("{0:X8}" -f $sp.ExitCode))." -ForegroundColor Red
        if ($salida -and $salida.Trim()) { Write-Host $salida.Trim() -ForegroundColor Red }
        else { Write-Host "Sin mensaje. Suele ser FailFast por flags de compilacion (ReadyToRun/Composite)." -ForegroundColor Red }
        Write-Host "NO se empaqueta. Arreglar antes de publicar." -ForegroundColor Red
        exit 1
    }
    try { $sp.Kill() } catch { }
    Get-Process WerFault, WerFaultSecure -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "OK: abrio y se mantuvo viva." -ForegroundColor Green
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
# ----------------------------------------------------------------------------
# Exclusion de configs de runtime — EN CUALQUIER NIVEL del ZIP, no solo la
# raiz. El filtro viejo (solo raiz) dejo pasar Engine/orbitX.json con
# device_id + device_token REALES en PilotX_v1.0.25.zip: todo tractor que lo
# aplicara quedaba con el pairing y el perfil de OTRA maquina. Criterio doble:
#  (1) LISTA DE NOMBRES CONOCIDOS ($configJson): los .json que Engine/Desktop
#      escriben en su BaseDirectory (relevados por grep de los Save()/
#      File.WriteAllText de configs + lo que aparecio en el ZIP v1.0.25).
#      Se excluyen a CUALQUIER nivel, salvo bajo AgroParallel\wwwroot\
#      (los estaticos del Hub estan versionados en git, no son config).
#  (2) PATRON DEFENSIVO: cualquier .json que NO termine en .deps.json /
#      .runtimeconfig.json y viva en la raiz del ZIP o en el PRIMER nivel de
#      Engine\ / Desktop\ / BarsHost\ tambien se excluye — ahi .NET solo
#      necesita esos dos tipos; todo otro .json es config acumulada por
#      haber corrido PilotX desde el Build local.
# OJO: NUNCA excluir '.json' a secas — los *.deps.json y *.runtimeconfig.json
# son obligatorios para que las apps .NET arranquen.
# Ademas: el cache de WebView2 se llama "PilotX.Desktop.exe.WebView2" (el
# skipDir viejo 'WebView2Data' no lo matcheaba) -> se filtra todo dir
# '*.WebView2'.
# ----------------------------------------------------------------------------
$configJson = @('orbitX.json','aog_settings.json','corex_settings.json',
    'corex-integrado.json','corexEcu.json','gps_status.json','nodos.json',
    'vistaX.json','sectionX.json','flowX.json','stormX.json','lineX.json',
    'quantiX.json','quantiX_motores.json','setup.json','sonidos.json',
    'insumos.json','idioma.json','camaras.json','debug.json',
    'overlayPrefs.json','prescripciones-state.json','steer-config.json',
    'tool.json','benchx.json','implemento.json','shapefile.json',
    'pilotx_ui_prefs.json','default.json')
$files = Get-ChildItem $OutDir -Recurse -File -Force | Where-Object {
    $rel   = $_.FullName.Substring($OutDir.Length + 1)
    $parts = $rel.Split([IO.Path]::DirectorySeparatorChar)
    $esJson       = $_.Extension.ToLower() -eq '.json'
    $esJsonDotnet = ($_.Name -like '*.deps.json') -or ($_.Name -like '*.runtimeconfig.json')
    $enWwwroot    = ($parts.Length -ge 3) -and ($parts[0] -eq 'AgroParallel') -and ($parts[1] -eq 'wwwroot')
    # (1) nombre de config conocido, a cualquier nivel (menos wwwroot)
    $esConfigConocida = $esJson -and (-not $enWwwroot) -and ($configJson -contains $_.Name)
    # (2) patron defensivo: .json no-.NET en la raiz o en el primer nivel de las apps
    $esConfigDefensiva = $esJson -and (-not $esJsonDotnet) -and (
        ($parts.Length -eq 1) -or
        (($parts.Length -eq 2) -and (@('Engine','Desktop','BarsHost') -contains $parts[0]))
    )
    (-not ($parts | Where-Object { ($skipDirs -contains $_) -or ($_ -like '*.WebView2') })) -and
    ($skipExt -notcontains $_.Extension.ToLower()) -and
    (-not $esConfigConocida) -and
    (-not $esConfigDefensiva) -and
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
