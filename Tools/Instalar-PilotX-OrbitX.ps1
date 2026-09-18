# Instalar-PilotX-OrbitX.ps1
# ============================================================================
# Instala / actualiza PilotX en una pantalla de campo BAJANDO el ZIP desde
# OrbitX por internet. Pensado para pantallas que hoy tienen una version
# vieja (AOG legacy o PilotX viejo) y a las que solo llegamos por RustDesk:
# se les manda ESTE archivo solo (unos KB) y el resto lo baja el script.
#
# Se corre EN LA PANTALLA, como Administrador:
#
#   powershell -ExecutionPolicy Bypass -File .\Instalar-PilotX-OrbitX.ps1
#
# El equipo tiene que estar VINCULADO a OrbitX (device_id + token en el
# orbitX.json de la instalacion vieja). Si no lo esta, generar un token en
# el panel OrbitX -> Dispositivos y pasarlo por parametro:
#
#   ... -DeviceId "AGP-XXXX" -DeviceToken "abc123..."
#
# Parametros:
#   -Version "1.0.40"   instala esa version puntual (default: la ultima)
#   -SoloDescargar      baja y verifica el ZIP, no toca la instalacion
#   -SinLanzar          no abre PilotX al terminar
#
# Que hace (en orden):
#   1. Busca credenciales OrbitX en las instalaciones existentes
#   2. Pide el catalogo OTA (producto PilotX) y elige la version
#   3. Baja el ZIP con progreso y verifica SHA256 contra el catalogo
#   4. Frena el vigilante (actualizando.flag) y cierra los procesos viejos
#   5. Extrae el ZIP sobre C:\PilotX (los configs de runtime NO viajan en
#      el ZIP, la identidad del equipo no se pisa)
#   6. Migra el orbitX.json viejo a C:\PilotX\Engine\ si hace falta
#   7. Firewall + accesos directos (escritorio e inicio) al Lanzar-PilotX.bat
#   8. Deshabilita el arranque automatico del AOG viejo
#   9. Lanza PilotX SIN elevar (via explorer.exe; elevado rompe WebView2)
# ============================================================================
[CmdletBinding()]
param(
    [string]$DeviceId = "",
    [string]$DeviceToken = "",
    [string]$Version = "",
    [string]$Destino = "C:\PilotX",
    [string]$Server = "https://orbitx.agroparallel.com",
    [switch]$SoloDescargar,
    [switch]$SinLanzar
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Titulo($t) { Write-Host "`n=== $t ===" -ForegroundColor Green }
function Paso($t)   { Write-Host ">> $t" -ForegroundColor Cyan }
function Aviso($t)  { Write-Host "AVISO: $t" -ForegroundColor Yellow }
function Morir($t)  { Write-Host "ERROR: $t" -ForegroundColor Red; exit 1 }

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { Write-Host "Ejecutar como ADMINISTRADOR." -ForegroundColor Red; exit 2 }

# ============================================================================
# 1. CREDENCIALES — buscar orbitX.json en instalaciones existentes
# ============================================================================
Titulo "1/6 Credenciales OrbitX"

# Lee DeviceId/DeviceToken de un orbitX.json tolerando PascalCase (el que
# escribe PilotX) y snake_case (agentes viejos).
function LeerCreds($path) {
    try {
        $j = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $id  = $j.DeviceId;    if (-not $id)  { $id  = $j.device_id }
        $tok = $j.DeviceToken; if (-not $tok) { $tok = $j.device_token }
        if ($id -and $tok) { return @{ Id = "$id"; Token = "$tok"; Path = $path } }
    } catch { }
    return $null
}

$credsOrigen = $null
if ($DeviceId -and $DeviceToken) {
    $credsOrigen = @{ Id = $DeviceId; Token = $DeviceToken; Path = "(parametros)" }
} else {
    # Layout nuevo primero (Engine\), despues layouts viejos: plano en
    # C:\PilotX y el instalador Inno legacy bajo Program Files.
    $candidatos = @(
        (Join-Path $Destino "Engine\orbitX.json"),
        (Join-Path $Destino "orbitX.json"),
        "${env:ProgramFiles(x86)}\AgroParallel\PilotX\orbitX.json",
        "$env:ProgramFiles\AgroParallel\PilotX\orbitX.json"
    )
    foreach ($c in $candidatos) {
        if ($c -and (Test-Path $c)) {
            $r = LeerCreds $c
            if ($r) { $credsOrigen = $r; break }
        }
    }
    # Ultimo recurso: cualquier orbitX.json bajo Program Files\AgroParallel
    if (-not $credsOrigen) {
        foreach ($base in @("${env:ProgramFiles(x86)}\AgroParallel", "$env:ProgramFiles\AgroParallel")) {
            if ($base -and (Test-Path $base)) {
                Get-ChildItem $base -Recurse -Filter "orbitX.json" -ErrorAction SilentlyContinue |
                    ForEach-Object { if (-not $credsOrigen) { $r = LeerCreds $_.FullName; if ($r) { $credsOrigen = $r } } }
            }
        }
    }
}

if (-not $credsOrigen) {
    Morir ("No encontre credenciales OrbitX en el equipo y no vinieron por parametro.`n" +
           "  Opcion A: en el panel OrbitX -> Dispositivos genera/regenera el token de esta`n" +
           "            pantalla y corre de nuevo con -DeviceId y -DeviceToken.`n" +
           "  Opcion B: si la instalacion vieja esta en otra carpeta, pasa el token igual.")
}
Paso "Device: $($credsOrigen.Id)  (credenciales de $($credsOrigen.Path))"

$headers = @{ "X-Device-ID" = $credsOrigen.Id; "X-Auth-Token" = $credsOrigen.Token; "Cache-Control" = "no-cache" }

# ============================================================================
# 2. CATALOGO — que version hay para bajar
# ============================================================================
Titulo "2/6 Catalogo OTA de PilotX"
$urlCat = "$($Server.TrimEnd('/'))/api/ota/catalogo?producto=PilotX"
try {
    $cat = Invoke-RestMethod -Uri $urlCat -Headers $headers -TimeoutSec 30
} catch {
    $code = $null
    try { $code = [int]$_.Exception.Response.StatusCode } catch { }
    if ($code -eq 401) {
        Morir ("OrbitX rechazo las credenciales (401). El token de este equipo no es valido:`n" +
               "  regenera el token desde el panel OrbitX -> Dispositivos y corre de nuevo`n" +
               "  con -DeviceId `"$($credsOrigen.Id)`" -DeviceToken `"<token nuevo>`".")
    }
    Morir "No pude pedir el catalogo en $urlCat -> $($_.Exception.Message)"
}

# Mayor version semver (o la pedida por parametro)
function SemverPartes($v) {
    $v = "$v" -replace '[-+].*$', ''
    $p = $v.Split('.'); $r = @(0, 0, 0)
    for ($i = 0; $i -lt 3 -and $i -lt $p.Length; $i++) { $n = 0; [int]::TryParse($p[$i], [ref]$n) | Out-Null; $r[$i] = $n }
    return $r
}
function SemverMayor($a, $b) {
    $pa = SemverPartes $a; $pb = SemverPartes $b
    for ($i = 0; $i -lt 3; $i++) { if ($pa[$i] -ne $pb[$i]) { return ($pa[$i] -gt $pb[$i]) } }
    return $false
}

$item = $null
foreach ($it in @($cat)) {
    if (-not $it.version) { continue }
    if ($Version) { if ($it.version -eq $Version) { $item = $it; break } }
    elseif (-not $item -or (SemverMayor $it.version $item.version)) { $item = $it }
}
if (-not $item) {
    if ($Version) {
        $disponibles = (@($cat) | ForEach-Object { $_.version }) -join ", "
        Morir "La version $Version no esta en el catalogo. Disponibles: $disponibles"
    }
    Morir "El catalogo de PilotX vino vacio — subir el ZIP al panel OrbitX -> /firmwares primero."
}
$mb = [Math]::Round($item.tamano_bytes / 1MB, 1)
Paso "Version a instalar: $($item.version)  ($mb MB)"
if ($item.changelog) { Write-Host "   Cambios: $($item.changelog)" -ForegroundColor Gray }

$exeActual = Join-Path $Destino "Desktop\PilotX.Desktop.exe"
if (Test-Path $exeActual) {
    $vAct = (Get-Item $exeActual).VersionInfo.ProductVersion
    Paso "Version instalada hoy: $vAct"
}

# ============================================================================
# 3. DESCARGA — ZIP con progreso + SHA256
# ============================================================================
Titulo "3/6 Descarga del ZIP"
$stagingDir = Join-Path $Destino "AgroParallel\Updates\$($item.version)"
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
$zipPath = Join-Path $stagingDir "payload.zip"
$tmpPath = "$zipPath.part"

$yaBajado = $false
if (Test-Path $zipPath) {
    $hashLocal = (Get-FileHash -Algorithm SHA256 $zipPath).Hash
    if ($item.hash_sha256 -and ($hashLocal -ieq $item.hash_sha256)) {
        Paso "El ZIP ya estaba bajado y el SHA256 coincide — no se baja de nuevo"
        $yaBajado = $true
    }
}

if (-not $yaBajado) {
    $urlZip = "$($Server.TrimEnd('/'))/api/ota/firmware/PilotX/$($item.version)"
    if (Test-Path $tmpPath) { Remove-Item $tmpPath -Force }
    $req = [System.Net.HttpWebRequest]::Create($urlZip)
    $req.Headers.Add("X-Device-ID", $credsOrigen.Id)
    $req.Headers.Add("X-Auth-Token", $credsOrigen.Token)
    $req.Timeout = 60000
    $req.ReadWriteTimeout = 60000
    try {
        $resp = $req.GetResponse()
        $total = $resp.ContentLength
        $src = $resp.GetResponseStream()
        $dst = [System.IO.File]::Create($tmpPath)
        try {
            $buf = New-Object byte[] (256KB)
            $leido = [long]0; $ultimoPct = -1
            while (($n = $src.Read($buf, 0, $buf.Length)) -gt 0) {
                $dst.Write($buf, 0, $n); $leido += $n
                if ($total -gt 0) {
                    $pct = [int]($leido * 100 / $total)
                    if ($pct -ne $ultimoPct) {
                        Write-Progress -Activity "Bajando PilotX $($item.version)" -Status "$pct% de $mb MB" -PercentComplete $pct
                        $ultimoPct = $pct
                    }
                }
            }
        } finally { $dst.Dispose(); $src.Dispose(); $resp.Dispose() }
        Write-Progress -Activity "Bajando PilotX $($item.version)" -Completed
    } catch {
        if (Test-Path $tmpPath) { Remove-Item $tmpPath -Force -ErrorAction SilentlyContinue }
        Morir "Fallo la descarga de $urlZip -> $($_.Exception.Message)"
    }

    $hashLocal = (Get-FileHash -Algorithm SHA256 $tmpPath).Hash
    if ($item.hash_sha256 -and ($hashLocal -ine $item.hash_sha256)) {
        Remove-Item $tmpPath -Force
        Morir "SHA256 no coincide (esperado $($item.hash_sha256.Substring(0,8)).., recibido $($hashLocal.Substring(0,8))..) — descarga corrupta, corre de nuevo."
    }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Move-Item $tmpPath $zipPath
    Paso "ZIP bajado y verificado: $zipPath"
}

if ($SoloDescargar) {
    Aviso "Modo -SoloDescargar: el ZIP quedo verificado en $zipPath. No se instalo nada."
    exit 0
}

# ============================================================================
# 4. FRENAR LO VIEJO — vigilante + procesos
# ============================================================================
Titulo "4/6 Cierre de la version vieja"
# El flag frena a los vigilantes (Lanzar-PilotX.bat y PilotX-Launcher.bat)
# para que no relancen lo que estamos por matar.
$flag = Join-Path $Destino "actualizando.flag"
New-Item -ItemType File -Path $flag -Force | Out-Null

# Procesos de TODAS las generaciones: stack nuevo, WinForms legacy y AgIO.
$procesos = @("PilotX.Desktop", "PilotX.GuidanceEngine", "PilotX.Bars.Host",
              "PilotX", "AgOpenGPS", "AgIO", "CoreX", "AgroParallel.Updater")
foreach ($p in $procesos) {
    Get-Process -Name $p -ErrorAction SilentlyContinue | ForEach-Object {
        Paso "Cerrando $($_.ProcessName) (pid $($_.Id))"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 2

# ============================================================================
# 5. INSTALAR — extraer sobre el destino + migrar identidad
# ============================================================================
Titulo "5/6 Instalacion en $Destino"
New-Item -ItemType Directory -Path $Destino -Force | Out-Null
try {
    # Expand-Archive -Force pisa lo existente; los configs de runtime
    # (orbitX.json, aog_settings.json, etc.) NO viajan en el ZIP de release,
    # asi que la identidad y los ajustes del equipo quedan intactos.
    Expand-Archive -Path $zipPath -DestinationPath $Destino -Force
} catch {
    Remove-Item $flag -Force -ErrorAction SilentlyContinue
    Morir "No pude extraer el ZIP (archivo en uso?): $($_.Exception.Message)"
}
Paso "Archivos extraidos"

# Migrar orbitX.json de la instalacion vieja al layout nuevo (Engine\).
$orbitDestino = Join-Path $Destino "Engine\orbitX.json"
if (-not (Test-Path $orbitDestino) -and ($credsOrigen.Path -ne "(parametros)") `
    -and (Test-Path $credsOrigen.Path) -and ($credsOrigen.Path -ne $orbitDestino)) {
    New-Item -ItemType Directory -Path (Split-Path $orbitDestino) -Force | Out-Null
    Copy-Item $credsOrigen.Path $orbitDestino -Force
    Paso "orbitX.json migrado desde $($credsOrigen.Path)"
}
# Si vino por parametro y no hay orbitX.json, dejar la identidad escrita.
# OJO: snake_case obligatorio — PilotX NO lee "DeviceId" (el case-insensitive
# de .NET no cubre underscores).
if (-not (Test-Path $orbitDestino) -and $DeviceId -and $DeviceToken) {
    New-Item -ItemType Directory -Path (Split-Path $orbitDestino) -Force | Out-Null
    [ordered]@{ enabled = $true; server_url = $Server; device_id = $DeviceId; device_token = $DeviceToken } |
        ConvertTo-Json | Out-File $orbitDestino -Encoding utf8
    Paso "orbitX.json creado con las credenciales pasadas por parametro"
}

# ============================================================================
# 6. SISTEMA — firewall, accesos directos, arranque, lanzar
# ============================================================================
Titulo "6/6 Firewall, accesos directos y arranque"
foreach ($exe in @((Join-Path $Destino "Desktop\PilotX.Desktop.exe"),
                   (Join-Path $Destino "Engine\PilotX.GuidanceEngine.exe"))) {
    if (Test-Path $exe) {
        $nombre = "PilotX - $([IO.Path]::GetFileNameWithoutExtension($exe))"
        Remove-NetFirewallRule -DisplayName $nombre -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $nombre -Direction Inbound -Program $exe `
            -Action Allow -Profile Private,Domain | Out-Null
        Paso "Regla de firewall: $nombre"
    }
}

$lanzador = Join-Path $Destino "Lanzar-PilotX.bat"
if (-not (Test-Path $lanzador)) { Aviso "El ZIP no trajo Lanzar-PilotX.bat — revisar el build" }
else {
    $wsh = New-Object -ComObject WScript.Shell
    $escritorio = "$env:PUBLIC\Desktop\PilotX.lnk"
    $inicio = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\PilotX.lnk"
    foreach ($lnkPath in @($escritorio, $inicio)) {
        $lnk = $wsh.CreateShortcut($lnkPath)
        $lnk.TargetPath = $lanzador
        $lnk.WorkingDirectory = $Destino
        $ico = Join-Path $Destino "Desktop\PilotX.Desktop.exe"
        if (Test-Path $ico) { $lnk.IconLocation = $ico }
        $lnk.Save()
    }
    Paso "Accesos directos: escritorio + inicio automatico"

    # El AOG viejo no tiene que volver a arrancar solo: se deshabilita
    # (renombrado, no borrado) todo acceso de inicio que apunte a otro lado.
    $carpetasInicio = @("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp",
                        "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup")
    foreach ($dir in $carpetasInicio) {
        if (-not (Test-Path $dir)) { continue }
        Get-ChildItem $dir -Filter "*.lnk" -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -ieq "PilotX.lnk") { return }
            try { $destinoLnk = $wsh.CreateShortcut($_.FullName).TargetPath } catch { return }
            if ($destinoLnk -match 'AgOpenGPS|AgroParallel|AgIO|PilotX') {
                try {
                    Rename-Item $_.FullName "$($_.Name).deshabilitado" -Force -ErrorAction Stop
                    Paso "Arranque viejo deshabilitado: $($_.Name) -> $destinoLnk"
                } catch { Aviso "No pude deshabilitar $($_.Name): $($_.Exception.Message)" }
            }
        }
    }
}

Remove-Item $flag -Force -ErrorAction SilentlyContinue

if (-not $SinLanzar -and (Test-Path $lanzador)) {
    # explorer.exe lo lanza como el usuario de la sesion, SIN elevacion:
    # PilotX elevado rompe WebView2 (paginas en negro, trampa conocida).
    Start-Process explorer.exe -ArgumentList "`"$lanzador`""
    Paso "PilotX lanzado"
}

Write-Host ""
Write-Host "=== LISTO — PilotX $($item.version) instalado en $Destino ===" -ForegroundColor Green
Write-Host "Verificar: que abra la pantalla, que el mapa cargue y que OrbitX figure vinculado."
