# ============================================================================
# instalar.ps1 — instalación de PilotX en una pantalla/tablet nueva, con UN
# comando, desde el servidor interno del taller (Tools/provision-server).
#
#   En la tablet (PowerShell, la misma LAN que la PC del taller):
#     irm __SERVIDOR__/instalar.ps1?p=__PEDIDO__ | iex
#
# Qué hace:
#   1. Se eleva a Administrador si hace falta.
#   2. Baja el kit (Provision-Pantalla.ps1, KioskSetup, branding, runtimes)
#      y el paquete PilotX del pedido a C:\PilotX\kit.
#   3. Calcula el device_id igual que PilotX (MD5 del MAC) y se registra en el
#      servidor, que lo da de alta en OrbitX y lo ASIGNA al cliente del pedido.
#   4. Corre Provision-Pantalla.ps1 (usuarios, limpieza, energía, firewall,
#      WinRM, runtimes, cliente.json, nombre de equipo).
#   5. Extrae PilotX en C:\PilotX y escribe C:\PilotX\Engine\orbitX.json con
#      el token del equipo y la org del cliente.
#   6. Activa el kiosko (PilotX-KioskSetup /yes) si el pedido lo pide.
# El servidor recibe el progreso y lo muestra en la web.
# ============================================================================
$ErrorActionPreference = "Stop"
$Servidor = "__SERVIDOR__"
$Pedido   = "__PEDIDO__"

function Paso($t) { Write-Host ">> $t" -ForegroundColor Cyan }
function Avisar($msg, $estado) {
    # Bytes UTF-8 explícitos: PowerShell 5.1 manda un -Body string como
    # ISO-8859-1 y los acentos llegaban rotos al panel ("instalaci?n").
    try {
        $json = @{ pedido = $Pedido; msg = $msg; estado = $estado } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$Servidor/api/progreso" -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 10 | Out-Null
    } catch { }
}

if (-not $Pedido) { Write-Host "Falta el código de pedido: abrí $Servidor y creá uno." -ForegroundColor Red; return }

# ── 1. Elevar ────────────────────────────────────────────────────────────────
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Paso "Pidiendo permisos de administrador..."
    $cmd = "irm $Servidor/instalar.ps1?p=$Pedido | iex"
    Start-Process powershell -Verb RunAs -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $cmd
    return
}

$kit = "C:\PilotX\kit"
New-Item -ItemType Directory -Path $kit, "$kit\Branding" -Force | Out-Null
Start-Transcript -Path "C:\PilotX\instalar-log.txt" -Append | Out-Null
Avisar "tablet $env:COMPUTERNAME conectada, empezando" "instalando"

# ── 2. Identidad: mismo device_id que calcula PilotX (MD5 del primer MAC activo) ──
function DeviceId {
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -eq "Up" -and $nic.NetworkInterfaceType -ne "Loopback") {
            $mac = $nic.GetPhysicalAddress().ToString()
            if ($mac -and $mac.Length -ge 12) {
                $md5 = [System.Security.Cryptography.MD5]::Create()
                $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($mac))
                return "OX-" + (([System.BitConverter]::ToString($hash)) -replace "-", "").Substring(0, 12).ToUpper()
            }
        }
    }
    return "OX-" + ([guid]::NewGuid().ToString("N").Substring(0, 12).ToUpper())
}
$deviceId = DeviceId
Paso "device_id de esta pantalla: $deviceId"

# ── 3. Registro en el servidor (alta + asignación en OrbitX) ────────────────
Paso "Registrando en OrbitX a través del servidor del taller..."
$reg = Invoke-RestMethod -Method Post -Uri "$Servidor/api/registrar" -ContentType "application/json" -Body (@{ pedido = $Pedido; device_id = $deviceId; hostname = $env:COMPUTERNAME } | ConvertTo-Json) -TimeoutSec 60
if (-not $reg.ok) { throw "El servidor rechazó el registro: $($reg.error)" }
Paso "Equipo dado de alta y asignado a '$($reg.estab_slug)' (cliente $($reg.cliente))"

# ── 4. Bajar kit + paquete ───────────────────────────────────────────────────
$bajar = @(
    @{ n = "Provision-Pantalla.ps1"; d = "$kit\Provision-Pantalla.ps1" },
    @{ n = "PilotX-KioskSetup.exe";  d = "$kit\PilotX-KioskSetup.exe" },
    @{ n = "Branding/logo.png";      d = "$kit\Branding\logo.png"; opc = $true },
    @{ n = "Branding/logo-fondo-blanco.png"; d = "$kit\Branding\logo-fondo-blanco.png"; opc = $true },
    @{ n = "Branding/fondo.png";     d = "$kit\Branding\fondo.png"; opc = $true },
    @{ n = "vc_redist.x64.exe";      d = "$kit\vc_redist.x64.exe"; opc = $true },
    @{ n = "MicrosoftEdgeWebview2Setup.exe"; d = "$kit\MicrosoftEdgeWebview2Setup.exe"; opc = $true }
)
foreach ($b in $bajar) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$Servidor/kit/$($b.n)" -OutFile $b.d -TimeoutSec 600
        Paso "kit: $($b.n)"
    } catch {
        if ($b.opc) { Write-Host "   (opcional, no está en el servidor: $($b.n))" -ForegroundColor DarkGray }
        else { throw "No pude bajar $($b.n): $($_.Exception.Message)" }
    }
}
$zipNombre = Split-Path -Leaf $reg.paquete
$zip = "$kit\$zipNombre"
Paso "Bajando $zipNombre (unos 200 MB, paciencia)..."
Avisar "bajando $zipNombre"

# De donde bajar los 200 MB. Por LAN da igual, pero cuando este servidor se
# alcanza por un TUNEL TCP (AnyDesk/RustDesk, pantalla en el campo con
# Starlink) todo el trafico pasa por la sesion de escritorio remoto: lento, y
# se corta con la sesion. La pantalla tiene internet propio y el registro ya le
# dio su token de OrbitX, asi que puede bajarlo de la nube ella misma.
# Se intenta el cloud primero cuando el control vino por loopback; si falla, se
# cae al servidor de provisioning, que siempre funciona.
$porTunel = ($Servidor -match "127\.0\.0\.1|localhost")
$bajado = $false

if ($porTunel -and $reg.device_token -and $reg.version) {
    $urlCloud = "$($reg.server_url)/api/ota/firmware/PilotX/$($reg.version)"
    Paso "Servidor alcanzado por tunel: bajando de OrbitX en vez de por la sesion remota"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $urlCloud -OutFile $zip -TimeoutSec 3600 `
            -Headers @{ "X-Device-ID" = $reg.device_id; "X-Auth-Token" = $reg.device_token }
        $bajado = (Test-Path $zip) -and ((Get-Item $zip).Length -gt 50MB)
        if ($bajado) { Paso "Bajado de OrbitX (no paso por el tunel)" }
        else { Write-Host "   la descarga del cloud quedo corta, se reintenta por el servidor" -ForegroundColor Yellow }
    } catch {
        Write-Host "   OrbitX no sirvio el paquete ($($_.Exception.Message)); se usa el servidor" -ForegroundColor Yellow
    }
}

if (-not $bajado) {
    Invoke-WebRequest -UseBasicParsing -Uri "$Servidor$($reg.paquete)" -OutFile $zip -TimeoutSec 1800
}
Paso "Paquete bajado: $([math]::Round((Get-Item $zip).Length / 1MB)) MB"

# ── 5. Aprovisionamiento base (usuarios, limpieza, energía, red, runtimes…) ──
Avisar "corriendo Provision-Pantalla.ps1" "instalando"
# OJO: Start-Process -ArgumentList con un array concatena con espacios y NO
# comilla nada. Con un cliente de varias palabras ("OTTAVIANO, MARIO HORACIO")
# el script recibia -Cliente con la primera palabra sola y el resto como
# argumentos sueltos: error de enlace de parametros, exit 1, y ni una linea
# ejecutada. Clientes de una sola palabra andaban, por eso tardo en aparecer.
# Cada valor va comillado a mano.
function Cita($v) { '"' + (([string]$v) -replace '"', '\"') + '"' }
$psArgs = @("-ExecutionPolicy", "Bypass", "-File", (Cita "$kit\Provision-Pantalla.ps1"),
            "-Cliente", (Cita $reg.cliente), "-SinKiosko")
if ($reg.cuit) { $psArgs += @("-Cuit", (Cita $reg.cuit)) }
if ($reg.nombre_equipo) { $psArgs += @("-NombreEquipo", (Cita $reg.nombre_equipo)) }
if ($reg.soporte_pass) { $psArgs += @("-SoportePass", (Cita $reg.soporte_pass)) }   # la genera y guarda el instalador
$p = Start-Process powershell -ArgumentList $psArgs -Wait -PassThru -NoNewWindow
Paso "Provision-Pantalla terminó con código $($p.ExitCode)"

# El 2026-09-24 este paso devolvió 1 (no instaló el VC++) y la instalación siguió
# igual hasta activar el kiosko: la pantalla quedó sin escritorio y en bucle de
# reinicios, y hubo que rescatarla a mano con un teclado. El código de salida de
# un paso no se ignora nunca más.
$provisionFallo = ($p.ExitCode -ne 0)
if ($provisionFallo) {
    Write-Host ""
    Write-Host "!! Provision-Pantalla falló (código $($p.ExitCode))." -ForegroundColor Red
    Write-Host "   PilotX se instala igual, pero el KIOSKO NO se va a activar." -ForegroundColor Yellow
    Write-Host "   Revisá C:\PilotX\instalar-log.txt antes de entregar la pantalla." -ForegroundColor Yellow
    Avisar "Provision-Pantalla falló (código $($p.ExitCode)): no se activa el kiosko" "instalando"
}

# ── 6. PilotX ────────────────────────────────────────────────────────────────
Avisar "extrayendo PilotX $($reg.version) en C:\PilotX" "instalando"
Get-Process | Where-Object { $_.ProcessName -match "^PilotX|^AgroParallel|msedgewebview2" } | Stop-Process -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($e in $z.Entries) {
        $rel = $e.FullName -replace "/", "\"
        if (-not $rel -or $rel.EndsWith("\")) { continue }
        $dst = Join-Path "C:\PilotX" $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
    }
} finally { $z.Dispose() }
Paso "PilotX extraído"

# orbitX.json: identidad propia + token de ESTE equipo + org del cliente.
$engine = "C:\PilotX\Engine"
New-Item -ItemType Directory -Path $engine -Force | Out-Null
$orbit = @{
    enabled      = $true
    server_url   = $reg.server_url
    device_token = $reg.device_token
    master_token = "vx-device-token"
    device_id    = $reg.device_id
    estab_slug   = $reg.estab_slug
}
$orbit | ConvertTo-Json | Out-File "$engine\orbitX.json" -Encoding utf8
Paso "orbitX.json escrito (device $($reg.device_id), org $($reg.estab_slug))"

# ── 6b. Firewall de PilotX (UDP 9999 módulos/ECU, MQTT 1883, 5180/5181, 8888) ──
# El kit crea reglas por programa, pero en la tablet de Clancy no quedó ninguna:
# setup_pilotx_lan.bat (viene en el paquete) las crea por puerto. Se corre sin
# interacción (< nul salta el "Presione una tecla").
if (Test-Path "C:\PilotX\setup_pilotx_lan.bat") {
    Avisar "creando reglas de firewall de PilotX" "instalando"
    cmd /c "C:\PilotX\setup_pilotx_lan.bat < nul" 2>&1 | Out-Null
    $reglas = (Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match "PilotX" } | Measure-Object).Count
    Paso "Firewall: $reglas reglas PilotX"
}

# La red tiene que quedar en perfil Privado: con perfil Público el firewall tapa
# los módulos aunque las reglas existan.
try {
    Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq "Public" } | ForEach-Object {
        Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
        Paso "Red '$($_.Name)': Pública -> Privada"
    }
} catch { }

# Y después se verifica PUERTO POR PUERTO, no "se corrió el .bat". En Ottaviano
# el .bat dejó 5 reglas y faltaban justo las dos que usan los nodos: sin 1883
# ningún QuantiX se conecta al broker, y sin 8088 toda OTA falla al bajar el .bin.
$puertosPilotX = @(
    @{ p = 1883; n = "MQTT" },
    @{ p = 5180; n = "Hub" },
    @{ p = 5181; n = "CoreX" },
    @{ p = 8088; n = "Firmware OTA" },
    @{ p = 9999; n = "PGN UDP"; udp = $true },
    @{ p = 5985; n = "WinRM" }
)
foreach ($x in $puertosPilotX) {
    $nombre = "PilotX $($x.n) $($x.p)"
    if (-not (Get-NetFirewallRule -DisplayName $nombre -ErrorAction SilentlyContinue)) {
        try {
            New-NetFirewallRule -DisplayName $nombre -Direction Inbound `
                -Protocol $(if ($x.udp) { "UDP" } else { "TCP" }) `
                -LocalPort $x.p -Action Allow -Profile Any -ErrorAction Stop | Out-Null
            Paso "Firewall: faltaba y se agregó -> $nombre"
        } catch { Write-Host "   no se pudo abrir $($x.p): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

# ── 6c. Ajustes de cabina ────────────────────────────────────────────────────
# Tres cosas que vinieron mal de fábrica en la pantalla de Ottaviano.

# Zona horaria: venía en Pacific (4 horas menos). Los lotes y el sync a OrbitX se
# sellan con la hora local; con ese corrimiento nada cuadra después.
try {
    Set-TimeZone -Id "Argentina Standard Time"
    Start-Service w32time -ErrorAction SilentlyContinue
    w32tm /resync /force 2>&1 | Out-Null
    Paso "Hora: $(Get-Date) (Argentina)"
} catch { }

# Suspensión: venía a 15 minutos. Una pantalla de tractor no se duerme nunca; se
# apaga cuando se corta la llave.
try {
    foreach ($t in @("standby-timeout-ac", "standby-timeout-dc", "hibernate-timeout-ac",
                     "hibernate-timeout-dc", "monitor-timeout-ac", "monitor-timeout-dc",
                     "disk-timeout-ac")) {
        powercfg /change $t 0 2>&1 | Out-Null
    }
    Paso "Energía: no se suspende ni se apaga la pantalla"
} catch { }

# WinRM: sin esto, diagnosticar una pantalla exige ir físicamente hasta ella.
try {
    Enable-PSRemoting -Force -SkipNetworkProfileCheck -ErrorAction Stop | Out-Null
    Paso "Soporte remoto por consola habilitado (WinRM)"
} catch { Write-Host "   WinRM no se pudo habilitar: $($_.Exception.Message)" -ForegroundColor Yellow }

# Acceso directo: en Ottaviano no quedó ninguno, y con el kiosko caído no había
# forma de abrir PilotX. Va al escritorio PÚBLICO para que lo vea el operario.
try {
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut("$env:PUBLIC\Desktop\PilotX.lnk")
    $lnk.TargetPath       = "C:\PilotX\Lanzar-PilotX.bat"
    $lnk.WorkingDirectory = "C:\PilotX"
    $lnk.WindowStyle      = 7
    $lnk.Description      = "PilotX - Agro Parallel"
    if (Test-Path "C:\PilotX\Desktop\PilotX.Desktop.exe") {
        $lnk.IconLocation = "C:\PilotX\Desktop\PilotX.Desktop.exe,0"
    }
    $lnk.Save()
    Paso "Acceso directo de PilotX en el escritorio"
} catch { }

# ── 7. Helper de red (tarea SYSTEM PilotXNetApply, de ViewX TabletTools) ────
# Sin esto PilotX (usuario limitado) no puede aplicar la IP fija del Ethernet.
Avisar "instalando helper de red (PilotXNetApply)" "instalando"
try { Invoke-Expression (Invoke-RestMethod -Uri "$Servidor/red.ps1" -TimeoutSec 30) }
catch { Write-Host "Helper de red: $($_.Exception.Message) — correr después: irm $Servidor/red.ps1 | iex" -ForegroundColor Yellow }

# Y se VERIFICA que la tarea haya quedado. Correr el script y no mirar el
# resultado es como no correrlo: en OTTAVIANO el paso figuraba en el log y la
# tarea no existia, asi que la pantalla no podia aplicar su IP fija — PilotX
# corre como usuario limitado y la aplica a traves de este helper. El sintoma
# aparece semanas despues, en el campo, cuando alguien quiere fijar la IP de
# los modulos y "no la toma".
if (Get-ScheduledTask -TaskName "PilotXNetApply" -ErrorAction SilentlyContinue) {
    Paso "Helper de red OK (tarea PilotXNetApply creada)"
} else {
    Write-Host "!! La tarea PilotXNetApply NO quedo creada." -ForegroundColor Red
    Write-Host "   Sin ella PilotX no puede aplicar la IP fija del Ethernet." -ForegroundColor Yellow
    Write-Host "   Correr a mano:  irm $Servidor/red.ps1 | iex" -ForegroundColor Yellow
    Avisar "ATENCION: PilotXNetApply no se creo — la pantalla no va a poder fijar su IP" "instalando"
}

# ── 8. RustDesk contra el servidor propio, con contraseña fija (queda en el pedido) ──
Avisar "instalando RustDesk" "instalando"
try { Invoke-Expression (Invoke-RestMethod -Uri "$Servidor/rustdesk.ps1?p=$Pedido" -TimeoutSec 30) }
catch { Write-Host "RustDesk: $($_.Exception.Message) — correr después: irm $Servidor/rustdesk.ps1?p=$Pedido | iex" -ForegroundColor Yellow }

# ULTIMO A PROPOSITO: el kiosko cambia el Shell de Windows y el arranque, y
# es el paso que puede llevarse el flujo puesto. En OTTAVIANO (2026-09-24) el
# instalador murio justo despues de activarlo y nunca llegaron ni el helper de
# red ni RustDesk: la pantalla quedo sin poder aplicar su IP fija (PilotX corre
# como usuario limitado y la aplica a traves de PilotXNetApply) y sin acceso
# remoto para arreglarlo. Todo lo que la pantalla necesita para trabajar va
# ANTES; el kiosko, al final.
# ── 9. Kiosko ────────────────────────────────────────────────────────────────
# El kiosko cambia el Shell de Windows por PilotX: si después PilotX no puede
# arrancar, la PC queda SIN ESCRITORIO al que volver y entra en bucle de
# reinicios. Por eso acá no se activa nada "por las dudas": primero hay que
# PROBAR que el motor levanta. Una pantalla sin kiosko se arregla en dos
# minutos; una en bucle hay que ir a buscarla al campo.
if ($reg.kiosko -and (Test-Path "$kit\PilotX-KioskSetup.exe")) {

    $frenos = @()
    if ($provisionFallo) { $frenos += "el aprovisionamiento base falló" }

    # PilotX es nativo: sin VC++ Redistributable no levanta. Se comprueba el DLL,
    # que es la prueba real; la clave de registro sobrevive a desinstalaciones.
    $sys = Join-Path $env:SystemRoot "System32"
    if (-not (Test-Path (Join-Path $sys "vcruntime140_1.dll"))) {
        if (Test-Path "$kit\vc_redist.x64.exe") {
            Paso "Falta el VC++ Redistributable: instalándolo"
            Start-Process "$kit\vc_redist.x64.exe" -ArgumentList "/install", "/quiet", "/norestart" -Wait
        }
        if (-not (Test-Path (Join-Path $sys "vcruntime140_1.dll"))) {
            $frenos += "falta el VC++ Redistributable"
        }
    }

    # Windows no permite una cuenta local homónima del equipo: con la PC llamada
    # "PILOTX" el usuario del operario no se puede crear y el autologon apunta a
    # la nada. Es exactamente lo que pasó en Ottaviano.
    if ($env:COMPUTERNAME -ieq "pilotx") {
        $frenos += "el equipo se llama 'PILOTX', igual que el usuario del operario"
    }

    # Prueba real: se levanta el Engine headless y se le pregunta por HTTP.
    if ($frenos.Count -eq 0) {
        Avisar "probando que PilotX arranque" "instalando"
        $eng = "C:\PilotX\Engine\PilotX.GuidanceEngine.exe"
        $pr = $null
        try {
            $pr = Start-Process $eng -ArgumentList "--webhost", "--corex" `
                                -WorkingDirectory "C:\PilotX\Engine" -PassThru -WindowStyle Hidden
            $arranco = $false
            foreach ($i in 1..20) {
                Start-Sleep -Seconds 2
                try {
                    Invoke-WebRequest "http://127.0.0.1:5180/api/aog/state" -TimeoutSec 3 -UseBasicParsing | Out-Null
                    $arranco = $true; break
                } catch { }
            }
            if ($arranco) { Paso "PilotX arranca correctamente" }
            else { $frenos += "PilotX no llegó a responder en 40 s" }
        } catch {
            $frenos += "PilotX no pudo ejecutarse: $($_.Exception.Message)"
        } finally {
            if ($pr -and -not $pr.HasExited) { Stop-Process -Id $pr.Id -Force -ErrorAction SilentlyContinue }
        }
    }

    if ($frenos.Count -gt 0) {
        Write-Host ""
        Write-Host "!! KIOSKO NO ACTIVADO. Motivos:" -ForegroundColor Red
        foreach ($f in $frenos) { Write-Host "   - $f" -ForegroundColor Yellow }
        Write-Host "   Windows arranca normal: la pantalla es usable y se arregla acá mismo." -ForegroundColor Yellow
        Write-Host "   Cuando esté resuelto:  C:\PilotX\PilotX-KioskSetup.exe /yes" -ForegroundColor Yellow
        Avisar "kiosko NO activado: $($frenos -join '; ')" "instalando"
    } else {
        Avisar "activando modo kiosko" "instalando"
        $k = Start-Process "$kit\PilotX-KioskSetup.exe" -ArgumentList "/yes" -Wait -PassThru -NoNewWindow
        Paso "Kiosko: código $($k.ExitCode)"
    }
}

Avisar "instalación terminada; falta reiniciar" "instalado"
Stop-Transcript | Out-Null
Write-Host ""
Write-Host "=== LISTO: PilotX $($reg.version) instalado y asignado a $($reg.cliente) ===" -ForegroundColor Green
Write-Host "Reiniciá la pantalla para aplicar nombre de equipo, fondo y kiosko." -ForegroundColor Yellow
$r = Read-Host "¿Reiniciar ahora? (s/N)"
if ($r -match "^[sS]") { Restart-Computer -Force }
