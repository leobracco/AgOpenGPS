# deploy-taller.ps1 — publica y despliega PilotX en la pantalla del taller
# (192.168.1.78) por WinRM. Flujo local->taller: aca se desarrolla, el taller
# prueba (2026-08-05).
#
#   Tools\deploy-taller.ps1                  -> Desktop + wwwroot (ciclo visual)
#   Tools\deploy-taller.ps1 -ConEngine       -> + binarios del Engine (DTOs, API)
#   Tools\deploy-taller.ps1 -SinPublish      -> re-desplegar lo ya publicado
#   Tools\deploy-taller.ps1 -Forzar          -> saltear el guard de prueba en curso
#
# El default NO toca el Engine a proposito: el ciclo de cambios visuales es
# wwwroot + Desktop, y el Engine solo hace falta cuando cambian DTOs o
# controllers. Menos piezas movidas = menos que puede salir mal a distancia.
#
# Que hay que saber del taller:
#   · WinRM :5985, usuario Admin sin password (red del taller, no exponer).
#   · Layout: C:\PilotX\{Desktop,Engine,AgroParallel\wwwroot,logs}.
#   · La GUI nace en la SESION FISICA via tarea programada /it (lanzar-diag.bat,
#     stderr en C:\PilotX\logs\stderr-diag.log: latido, espia, caja negra).
#   · El Engine se actualiza POR ENCIMA (overlay), nunca rename-replace:
#     adentro viven Fields/Vehicles (GuidanceEngineData), data/prescripciones,
#     y TODOS los .json de config/estado — incluido orbitX.json con la
#     IDENTIDAD del taller (OX-E15F6994C295). Pisarla hace que dos maquinas se
#     reporten al cloud como el mismo equipo. Por eso: se copian solo
#     binarios; de los .json viajan UNICAMENTE *.deps.json y
#     *.runtimeconfig.json (lista blanca, no negra: un config nuevo de un
#     producto futuro queda excluido por defecto, no clobbereado por olvido).
#   · Lote de prueba: "Taller 100ha" (3 franjas DOSIS 4/5/6 sem/m).

param(
    [switch]$SinPublish,
    [switch]$ConEngine,
    # BenchX (simulador de banco) para demo/Expo: se publica AUTOCONTENIDO
    # (net9 self-contained, ~100 MB) porque el taller solo tiene el runtime
    # .NET 8; va a C:\PilotX\BenchX con acceso directo "BenchX" en el
    # escritorio. Manda NMEA + modulos por UDP broadcast :9999 a la misma PC.
    [switch]$ConBenchX,
    # Solo BenchX: no toca Desktop, wwwroot ni Engine ni reinicia PilotX.
    [switch]$SoloBenchX,
    [switch]$Forzar,
    [string]$Taller = "192.168.1.78"
)

$ErrorActionPreference = "Stop"
if ($SoloBenchX) { $SinPublish = $true; $ConBenchX = $true; $ConEngine = $false; $Forzar = $true }
$root = Split-Path $PSScriptRoot -Parent
$tmp = Join-Path $env:TEMP "pilotx-deploy-taller"

# Guard: lote abierto en el taller = probable prueba en curso; desplegar ahora
# la mataria a mitad de camino. Se salta con -Forzar. API caida NO bloquea
# (puede ser justamente lo que el deploy arregla).
if (-not $Forzar) {
    try {
        $cur = (Invoke-WebRequest "http://${Taller}:5180/api/lotes/current" -UseBasicParsing -TimeoutSec 5).Content
        if ($cur -match '"name":"(?!null)[^"]+"') {
            Write-Host "El taller tiene un lote abierto ($cur) — probable prueba en curso." -ForegroundColor Yellow
            Write-Host "No se despliega. Reintentar cuando termine, o -Forzar si es a proposito." -ForegroundColor Yellow
            exit 2
        }
    } catch { }
}

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force $tmp | Out-Null

if (-not $SinPublish) {
    # Publish a STAGING propio, nunca sobre Build\: el PilotX local corre
    # desde Build\Desktop y publicar ahi con la app abierta se traba en locks
    # de DLL (MSB3026 en bucle de reintentos — pasó 2026-08-05). El deploy no
    # tiene por qué molestar a la instancia de desarrollo.
    Write-Host "== publish PilotX.Desktop (staging) ==" -ForegroundColor Cyan
    dotnet publish "$root\SourceCode\PilotX.Desktop\PilotX.Desktop.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=false -p:PublishReadyToRunComposite=false -o "$tmp\Desktop" -v q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "publish Desktop FALLO" -ForegroundColor Red; exit 1 }

    # PRUEBA DE ARRANQUE antes de mandar nada a 200 km de distancia.
    # La 1.0.48 se publico, se subio y se instalo en un tractor sin que nadie
    # la hubiera ejecutado nunca: moria al arrancar con FailFast y sin ningun
    # mensaje, por una mezcla de flags de compilacion. Compilar no prueba nada.
    Write-Host "== prueba de arranque (staging) ==" -ForegroundColor Cyan
    $smErr = Join-Path $env:TEMP "taller_smoke.err.txt"
    $smp = Start-Process "$tmp\Desktop\PilotX.Desktop.exe" -PassThru -WorkingDirectory "$tmp" -RedirectStandardError $smErr
    if ($smp.WaitForExit(25000)) {
        Write-Host "La app murio al arrancar. NO se despliega al taller." -ForegroundColor Red
        $txt = ""
        try { $txt = (Get-Content $smErr -Raw -ErrorAction SilentlyContinue) } catch { }
        if ($txt -and $txt.Trim()) { Write-Host $txt.Trim() -ForegroundColor Red }
        else { Write-Host "Sin mensaje: suele ser FailFast por ReadyToRun/Composite." -ForegroundColor Red }
        if (-not $Forzar) { exit 1 }
        Write-Host "-Forzar activo: se despliega igual, bajo tu responsabilidad." -ForegroundColor Yellow
    } else {
        try { $smp.Kill() } catch { }
        Get-Process WerFault, WerFaultSecure -ErrorAction SilentlyContinue | Stop-Process -Force
        Write-Host "OK: abrio." -ForegroundColor Green
    }
    if ($ConEngine) {
        Write-Host "== publish PilotX.GuidanceEngine (staging) ==" -ForegroundColor Cyan
        dotnet publish "$root\SourceCode\PilotX.GuidanceEngine\PilotX.GuidanceEngine.csproj" `
            -c Release -r win-x64 --self-contained true `
            -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -o "$tmp\Engine" -v q --nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "publish Engine FALLO" -ForegroundColor Red; exit 1 }
    }
} else {
    # -SinPublish: copiar lo ya publicado en Build\ (excluyendo el cache de
    # WebView2, lockeado si la app corre, y los logs).
    if (-not $SoloBenchX) {
        robocopy "$root\Build\Desktop" "$tmp\Desktop" /E /XD "PilotX.Desktop.exe.WebView2" "Logs" /XF "*.log" /NFL /NDL /NJH /NJS | Out-Null
    }
    if ($ConEngine) {
        robocopy "$root\Build\Engine" "$tmp\Engine-src" /E /NFL /NDL /NJH /NJS | Out-Null
    }
}

Write-Host "== staging ==" -ForegroundColor Cyan
if (-not $SoloBenchX) {
    Compress-Archive -Path "$tmp\Desktop\*" -DestinationPath "$tmp\desktop.zip" -CompressionLevel Optimal

    # wwwroot del Hub: EL DEL SOURCE (fuente de verdad de la UI), completo.
    Compress-Archive -Path "$root\SourceCode\AgroParallel\Web\AgroParallel.WebUI\wwwroot\*" -DestinationPath "$tmp\wwwroot.zip" -CompressionLevel Optimal
}

if ($ConEngine) {
    # Engine: SOLO binarios. Dos pasadas: (1) todo menos json/log/datos,
    # (2) lista blanca de los json de runtime que si tienen que viajar.
    # El origen depende del camino: publish fresco ($tmp\Engine, que ya nace
    # sin configs de usuario) o la copia de Build ($tmp\Engine-src, que los
    # arrastra porque el runtime los escribe ahi). El filtro corre igual en
    # ambos: mas vale filtrar de mas que clobberear la identidad del taller.
    $engOrigen = if (Test-Path "$tmp\Engine-src") { "$tmp\Engine-src" } else { "$tmp\Engine" }
    robocopy $engOrigen "$tmp\Engine-filtrado" /E `
        /XD "data" "GuidanceEngineData" "implementos" "firmware-cache" "AgroParallel" "logs" "Logs" `
        /XF "*.json" "*.log" /NFL /NDL /NJH /NJS | Out-Null
    Copy-Item "$engOrigen\*.deps.json" "$tmp\Engine-filtrado\" -Force
    Copy-Item "$engOrigen\*.runtimeconfig.json" "$tmp\Engine-filtrado\" -Force
    Compress-Archive -Path "$tmp\Engine-filtrado\*" -DestinationPath "$tmp\engine.zip" -CompressionLevel Optimal
}

if ($ConBenchX) {
    # Siempre publish fresco y autocontenido: Build\BenchX es framework-dependent
    # (net9) y en el taller no hay runtime 9. ~100 MB, se acepta por ser demo.
    Write-Host "== publish BenchX autocontenido (win-x64) ==" -ForegroundColor Cyan
    dotnet publish "$root\SourceCode\BenchX\BenchX.csproj" -c Release -r win-x64 --self-contained true `
        -o "$tmp\BenchX" -v q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "publish BenchX FALLO" -ForegroundColor Red; exit 1 }
    if (-not (Test-Path "$tmp\BenchX\BenchX.exe")) { Write-Host "publish BenchX sin exe" -ForegroundColor Red; exit 1 }
    Compress-Archive -Path "$tmp\BenchX\*" -DestinationPath "$tmp\benchx.zip" -CompressionLevel Optimal
}

Get-ChildItem "$tmp\*.zip" | ForEach-Object { Write-Host ("  {0}: {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

Write-Host "== deploy a $Taller ==" -ForegroundColor Cyan
$cred = New-Object System.Management.Automation.PSCredential(
    "Admin", (New-Object System.Security.SecureString))
$s = New-PSSession -ComputerName $Taller -Credential $cred

if (-not $SoloBenchX) {
    Copy-Item "$tmp\desktop.zip" -Destination "C:\PilotX\deploy-desktop.zip" -ToSession $s
    Copy-Item "$tmp\wwwroot.zip" -Destination "C:\PilotX\deploy-wwwroot.zip" -ToSession $s
}
if ($ConEngine) { Copy-Item "$tmp\engine.zip" -Destination "C:\PilotX\deploy-engine.zip" -ToSession $s }
if ($ConBenchX) { Copy-Item "$tmp\benchx.zip" -Destination "C:\PilotX\deploy-benchx.zip" -ToSession $s }

Invoke-Command -Session $s -ScriptBlock {
    param($conEngine, $conBenchX, $soloBenchX)

    if (-not $soloBenchX) { Get-Process PilotX.Desktop -EA SilentlyContinue | Stop-Process -Force }
    if ($conEngine) { Get-Process PilotX.GuidanceEngine -EA SilentlyContinue | Stop-Process -Force }
    if ($conBenchX) { Get-Process BenchX -EA SilentlyContinue | Stop-Process -Force }
    Start-Sleep -Seconds 2

    # BenchX: rename-replace (no tiene datos de usuario que importen; su
    # benchx.json de config se conserva si existia). Lanzador + acceso directo
    # en el escritorio publico para la demo.
    if ($conBenchX) {
        $bxCfg = $null
        if (Test-Path "C:\PilotX\BenchX\benchx.json") { $bxCfg = Get-Content "C:\PilotX\BenchX\benchx.json" -Raw }
        $bxBak = "C:\PilotX\BenchX-anterior"
        if (Test-Path $bxBak) { Remove-Item $bxBak -Recurse -Force }
        if (Test-Path "C:\PilotX\BenchX") { Rename-Item "C:\PilotX\BenchX" $bxBak }
        Expand-Archive "C:\PilotX\deploy-benchx.zip" -DestinationPath "C:\PilotX\BenchX"
        Remove-Item "C:\PilotX\deploy-benchx.zip" -Force
        if ($bxCfg) { Set-Content "C:\PilotX\BenchX\benchx.json" $bxCfg -Encoding UTF8 }

        Set-Content "C:\PilotX\Lanzar-BenchX.bat" "@echo off`r`ncd /d C:\PilotX\BenchX`r`nstart `"BenchX`" C:\PilotX\BenchX\BenchX.exe" -Encoding ascii
        $ws = New-Object -ComObject WScript.Shell
        $lnk = $ws.CreateShortcut("C:\Users\Public\Desktop\BenchX.lnk")
        $lnk.TargetPath = "C:\PilotX\BenchX\BenchX.exe"
        $lnk.WorkingDirectory = "C:\PilotX\BenchX"
        $lnk.Description = "BenchX - simulador de GPS y modulos para PilotX (demo)"
        $lnk.Save()
        "BenchX instalado: " + (Test-Path "C:\PilotX\BenchX\BenchX.exe") + " (acceso directo en el escritorio)"
    }

    if ($soloBenchX) { return }

    # Desktop: rename-replace (no tiene datos de usuario adentro). Un solo
    # backup; el taller no es archivo historico.
    $bak = "C:\PilotX\Desktop-anterior"
    if (Test-Path $bak) { Remove-Item $bak -Recurse -Force }
    if (Test-Path "C:\PilotX\Desktop") { Rename-Item "C:\PilotX\Desktop" $bak }
    Expand-Archive "C:\PilotX\deploy-desktop.zip" -DestinationPath "C:\PilotX\Desktop"
    Remove-Item "C:\PilotX\deploy-desktop.zip" -Force

    # wwwroot: rename-replace (estaticos, la fuente de verdad es el source).
    $wbak = "C:\PilotX\AgroParallel\wwwroot-anterior"
    if (Test-Path $wbak) { Remove-Item $wbak -Recurse -Force }
    if (Test-Path "C:\PilotX\AgroParallel\wwwroot") { Rename-Item "C:\PilotX\AgroParallel\wwwroot" $wbak }
    Expand-Archive "C:\PilotX\deploy-wwwroot.zip" -DestinationPath "C:\PilotX\AgroParallel\wwwroot"
    Remove-Item "C:\PilotX\deploy-wwwroot.zip" -Force

    # Engine: OVERLAY sobre el existente — los datos y configs del taller
    # (Fields, prescripciones, orbitX.json con su identidad) quedan intactos.
    if ($conEngine) {
        Expand-Archive "C:\PilotX\deploy-engine.zip" -DestinationPath "C:\PilotX\Engine" -Force
        Remove-Item "C:\PilotX\deploy-engine.zip" -Force
    }

    # Relanzar en la sesion fisica. lanzar-diag.bat levanta el Engine si no
    # esta corriendo y la pantalla con stderr redirigido.
    if (-not (Test-Path "C:\PilotX\lanzar-diag.bat")) {
        Set-Content "C:\PilotX\lanzar-diag.bat" "@echo off`r`ncd /d C:\PilotX`r`ntasklist | find /i `"PilotX.GuidanceEngine.exe`" >nul || start `"PilotX Engine`" /min C:\PilotX\Engine\PilotX.GuidanceEngine.exe --webhost --corex`r`ntimeout /t 6 /nobreak >nul`r`nC:\PilotX\Desktop\PilotX.Desktop.exe 2> C:\PilotX\logs\stderr-diag.log" -Encoding ascii
    }
    # /rl LIMITED, nunca highest: WebView2 no lanza su proceso hijo bajo un
    # proceso elevado — con la tarea elevada TODA pagina HTML (lote, cabecera,
    # overlays) queda en negro aunque el server :5180 responda (2026-08-05).
    schtasks /query /tn "PilotX-Diag" >$null 2>&1
    if ($LASTEXITCODE -ne 0) {
        schtasks /create /tn "PilotX-Diag" /tr "C:\PilotX\lanzar-diag.bat" /sc once /st 23:59 /it /f /rl limited | Out-Null
    }
    schtasks /run /tn "PilotX-Diag" | Out-Null
    Start-Sleep -Seconds 15
    $d = Get-Process PilotX.Desktop -EA SilentlyContinue
    $e = Get-Process PilotX.GuidanceEngine -EA SilentlyContinue
    if ($d -and $e) { "OK: Desktop (PID $($d.Id)) + Engine (PID $($e.Id)) corriendo" }
    else { "ERROR: falta " + $(if (-not $d) { "Desktop " }) + $(if (-not $e) { "Engine" }) + " -- mirar C:\PilotX\logs\stderr-diag.log" }
} -ArgumentList ([bool]$ConEngine), ([bool]$ConBenchX), ([bool]$SoloBenchX)

Remove-PSSession $s
Write-Host "== listo ==" -ForegroundColor Green
