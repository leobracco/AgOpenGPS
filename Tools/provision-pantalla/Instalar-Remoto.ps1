# ============================================================================
#  Instalar-Remoto.ps1 — deja una pantalla lista SIN estar en la misma red.
#
#  Para qué: el flujo normal (Tools\provision-server) sirve el paquete por LAN.
#  Una pantalla en el campo, con Starlink y a 300 km, no ve esa LAN. Esta la
#  baja DIRECTO de OrbitX, que es donde los paquetes ya están publicados, y
#  hace sola todo lo demás. El técnico solo mira por RustDesk/AnyDesk.
#
#  No hace falta túnel TCP ni transferir el ZIP por la sesión de escritorio:
#  193 MB por un canal de control remoto tardan una eternidad y se cortan con
#  la sesión. La pantalla tiene internet propio — que lo use.
#
#  Uso (en la pantalla, PowerShell como ADMINISTRADOR):
#      .\Instalar-Remoto.ps1 -Cliente "RODRIGUEZ, JORGE JUSTO JAVIER" `
#                            -Email leonardo@agroparallel.com
#
#  Si la pantalla venía con AgOpenGPS viejo, rescata los lotes ANTES de tocar
#  nada y los devuelve al final. Ver Tools\migrar-desde-aog\README.md.
#
#  El kiosko se activa SOLO si PilotX arrancó de verdad. Ver por qué en
#  Tools\PilotX-KioskSetup\Program.cs (post-mortem OTTAVIANO, 2026-09-24).
# ============================================================================
param(
    [Parameter(Mandatory = $true)][string]$Cliente,
    [string]$Email = "",
    [string]$Version = "",                 # vacío = la última publicada
    [string]$NombreEquipo = "",            # vacío = PILOTX-<CLIENTE>
    [string]$SoportePass = "Agro2026",
    [switch]$SinKiosko,
    [switch]$SinRescate,                   # saltear el backup de AgOpenGPS viejo
    [switch]$RescateListo                  # el ZIP ya esta a salvo fuera de la pantalla
)

$ErrorActionPreference = "Continue"
$Servidor = "https://orbitx.agroparallel.com"
$Aqui     = Split-Path -Parent $MyInvocation.MyCommand.Path
$Destino  = "C:\PilotX"

function Titulo($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Ok($t)     { Write-Host "  [OK] $t" -ForegroundColor Green }
function Aviso($t)  { Write-Host "  [--] $t" -ForegroundColor DarkGray }
function Mal($t)    { Write-Host "  [!!] $t" -ForegroundColor Red }

$problemas = @()

# ── 0. Admin ────────────────────────────────────────────────────────────────
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { Mal "Hay que correrlo como ADMINISTRADOR."; exit 2 }

New-Item -ItemType Directory -Path $Destino -Force | Out-Null
Start-Transcript -Path "$Destino\instalar-remoto.log" -Append | Out-Null
Write-Host ""
Write-Host "  Instalación remota de PilotX — $Cliente" -ForegroundColor White
Write-Host "  Equipo: $env:COMPUTERNAME   $(Get-Date)" -ForegroundColor DarkGray

# ── 1. Rescatar AgOpenGPS viejo ─────────────────────────────────────────────
# Primero de todo: si esta pantalla tenía AOG, sus lotes son lo único
# irreemplazable. El instalador de PilotX borra HKCU\SOFTWARE\AgOpenGPS\
# workingDirectory, así que después de instalar nadie sabe dónde estaban.
$zipRescate = $null
if (-not $SinRescate) {
    Titulo "1/8  Lotes y configuración de la instalación anterior"

    $rescatar = Join-Path (Split-Path -Parent $Aqui) "migrar-desde-aog\Rescatar-AOG.ps1"
    $hayAog = (Test-Path "HKCU:\SOFTWARE\AgOpenGPS") -or
              (Test-Path "$env:USERPROFILE\Documents\AgOpenGPS\Fields") -or
              (Test-Path "$env:USERPROFILE\Documents\AgOpenGPS\Vehicles")

    if (-not $hayAog) {
        Aviso "no hay instalación previa de AgOpenGPS: nada que rescatar"
    }
    else {
        if (Test-Path $rescatar) {
            # Rescatar-AOG.ps1 lee primero HKCU\SOFTWARE\AgOpenGPS\workingDirectory
            # (la carpeta de trabajo real, que no siempre es Documentos) y se lleva
            # Fields + Vehicles + los .json de config + esas claves del registro.
            & $rescatar
        }
        else {
            # Sin el kit al lado, copia cruda. Menos prolija, pero no se pierde nada.
            $origen = (Get-ItemProperty "HKCU:\SOFTWARE\AgOpenGPS" -EA 0).workingDirectory
            if (-not $origen) { $origen = "$env:USERPROFILE\Documents" }
            $src = Join-Path $origen "AgOpenGPS"
            if (Test-Path $src) {
                $crudo = "C:\Rescate-PilotX\rescate-$env:COMPUTERNAME-$(Get-Date -f yyyyMMdd-HHmm).zip"
                New-Item -ItemType Directory -Path (Split-Path $crudo) -Force | Out-Null
                Compress-Archive -Path $src -DestinationPath $crudo -Force
            }
        }

        $zipRescate = (Get-ChildItem "C:\Rescate-PilotX\*.zip" -EA 0 |
                       Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName

        # ACÁ SE FRENA SI ALGO NO CIERRA. Instalar PilotX borra la clave del
        # registro que dice dónde estaban los lotes: si seguimos sin un rescate
        # bueno, el cliente pierde su trabajo y nadie puede reconstruirlo.
        if (-not $zipRescate) {
            Mal "HAY una instalación de AgOpenGPS pero el rescate NO dejó ningún ZIP."
            Write-Host ""
            Write-Host "  NO se instala nada. Los lotes del cliente estan en juego." -ForegroundColor Red
            Write-Host "  Probá a mano y volvé a correr esto:" -ForegroundColor Yellow
            Write-Host "    ..\migrar-desde-aog\Rescatar-AOG.ps1 -Buscar" -ForegroundColor Yellow
            Write-Host "    ..\migrar-desde-aog\Rescatar-AOG.ps1 -Datos D:\AgOpenGPS" -ForegroundColor Yellow
            Stop-Transcript | Out-Null
            exit 10
        }

        # Que el ZIP tenga contenido de verdad, no que exista y esté vacío.
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zr = [System.IO.Compression.ZipFile]::OpenRead($zipRescate)
            try {
            # .Replace() y no -replace: el segundo es regex y una barra
            # invertida sola ahi es un escape incompleto.
            $ent = @($zr.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            $nFields = @($ent | Where-Object { $_ -match '(^|/)Fields/' }).Count
            $nVehicles = @($ent | Where-Object { $_ -match '(^|/)Vehicles/' }).Count
            } finally { $zr.Dispose() }
        } catch { $nFields = -1; $nVehicles = -1 }

        $mb = [math]::Round((Get-Item $zipRescate).Length / 1MB, 1)
        Ok "rescate: $([IO.Path]::GetFileName($zipRescate))  ($mb MB, $nFields archivos de lotes, $nVehicles de perfiles)"

        if ($nFields -le 0 -and $nVehicles -le 0) {
            Mal "el ZIP no trae ni lotes ni perfiles: el rescate no sirvió."
            Write-Host "  NO se instala nada. Revisá con -Buscar dónde están los datos." -ForegroundColor Red
            Stop-Transcript | Out-Null
            exit 11
        }

        # Segunda copia en otra unidad si hay alguna: el disco de la pantalla es
        # justamente lo que estamos por reescribir.
        $otra = Get-Volume -EA 0 | Where-Object {
            $_.DriveLetter -and $_.DriveLetter -ne 'C' -and $_.DriveType -in 'Fixed','Removable' -and $_.SizeRemaining -gt 200MB
        } | Select-Object -First 1
        if ($otra) {
            try {
                Copy-Item $zipRescate "$($otra.DriveLetter):\" -Force
                Ok "copia de seguridad en $($otra.DriveLetter):\"
            } catch { Aviso "no se pudo copiar a $($otra.DriveLetter): $($_.Exception.Message)" }
        }

        # Y la parada obligatoria: el seguro de verdad es que el ZIP salga de
        # esta máquina. Se pide confirmación escrita a propósito — un Enter
        # distraído no alcanza para arriesgar los lotes de un cliente.
        Write-Host ""
        Write-Host "  ANTES DE SEGUIR: bajate ese ZIP a tu PC" -ForegroundColor Yellow
        Write-Host "  (RustDesk/AnyDesk -> transferencia de archivos):" -ForegroundColor Yellow
        Write-Host "     $zipRescate" -ForegroundColor White
        Write-Host ""
        if (-not $RescateListo) {
            $r = Read-Host "  Escribi BAJADO cuando lo tengas en tu PC (cualquier otra cosa cancela)"
            if ($r.Trim().ToUpper() -ne "BAJADO") {
                Write-Host "  Cancelado. No se instalo nada; el rescate quedo en C:\Rescate-PilotX." -ForegroundColor Yellow
                Stop-Transcript | Out-Null
                exit 0
            }
        } else { Aviso "-RescateListo: se saltea la confirmación" }
    }
}

# ── 2. Runtimes ─────────────────────────────────────────────────────────────
# PilotX es nativo: sin el VC++ Redistributable no levanta, y el kiosko sobre
# un PilotX que no levanta deja la PC sin escritorio (Ottaviano, 2026-09-24).
Titulo "2/8  Runtimes"
$sys = Join-Path $env:SystemRoot "System32"
if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) {
    Ok "VC++ Redistributable presente"
} else {
    $vc = "$env:TEMP\vc_redist.x64.exe"
    try {
        Invoke-WebRequest "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vc -UseBasicParsing
        Start-Process $vc -ArgumentList "/install", "/quiet", "/norestart" -Wait
    } catch { Mal "no se pudo bajar el VC++: $($_.Exception.Message)" }
    if (Test-Path (Join-Path $sys "vcruntime140_1.dll")) { Ok "VC++ Redistributable instalado" }
    else { $problemas += "falta el VC++ Redistributable"; Mal "sigue faltando el VC++" }
}

$wv = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -EA 0).pv
if ($wv) { Ok "WebView2 $wv" }
else {
    try {
        $bs = "$env:TEMP\MicrosoftEdgeWebview2Setup.exe"
        Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bs -UseBasicParsing
        Start-Process $bs -ArgumentList "/silent", "/install" -Wait
        Ok "WebView2 instalado"
    } catch { Mal "WebView2: $($_.Exception.Message)" }
}

# ── 3. Bajar el paquete de OrbitX ───────────────────────────────────────────
# Reusa el descargador del kit: hace streaming a disco (no arma el ZIP en
# memoria) y verifica el SHA256 contra el catálogo antes de dar por buena la
# descarga. Por Starlink eso importa: un corte a mitad no puede pasar por sano.
Titulo "3/8  Paquete de PilotX"
$descargar = Join-Path (Split-Path -Parent $Aqui) "migrar-desde-aog\Descargar-PilotX.ps1"
if (-not (Test-Path $descargar)) {
    Mal "falta Descargar-PilotX.ps1 al lado de este script"
    Stop-Transcript | Out-Null; exit 3
}
if (-not $Email) { $Email = Read-Host "  Mail de OrbitX" }
$argsD = @("-Email", $Email)
if ($Version) { $argsD += @("-Version", $Version) }
& $descargar @argsD

$paq = Get-ChildItem "C:\Paquetes-PilotX\*.bin", "C:\Paquetes-PilotX\*.zip" -EA 0 |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $paq) { Mal "no quedó ningún paquete en C:\Paquetes-PilotX"; Stop-Transcript | Out-Null; exit 4 }
Ok "paquete: $($paq.Name)  ($([math]::Round($paq.Length/1MB)) MB)"

# ── 4. Instalar ─────────────────────────────────────────────────────────────
Titulo "4/8  Instalación"
Get-Process | Where-Object { $_.ProcessName -match "^PilotX|^AgroParallel|msedgewebview2" } |
    Stop-Process -Force -ErrorAction SilentlyContinue
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($paq.FullName)
    try {
        foreach ($e in $z.Entries) {
            $rel = $e.FullName -replace "/", "\"
            if (-not $rel -or $rel.EndsWith("\")) { continue }
            $dst = Join-Path $Destino $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        }
    } finally { $z.Dispose() }
    Ok "extraído en $Destino"
} catch {
    Mal "no se pudo extraer: $($_.Exception.Message)"
    Stop-Transcript | Out-Null; exit 5
}

# ── 5. Ajustes de cabina ────────────────────────────────────────────────────
# Todo lo que vino mal de fábrica en las pantallas de este año.
Titulo "5/8  Ajustes de cabina"

# Perfil Privado: con perfil Público el firewall tapa los módulos aunque las
# reglas existan.
try {
    Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq "Public" } | ForEach-Object {
        Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
        Ok "red '$($_.Name)': Pública -> Privada"
    }
} catch { }

# Puerto por puerto, no "corrí el .bat": sin 1883 ningún nodo se conecta al
# broker y sin 8088 toda OTA falla al bajar el .bin.
foreach ($x in @(@{p=1883;n="MQTT"}, @{p=5180;n="Hub"}, @{p=5181;n="CoreX"},
                 @{p=8088;n="Firmware OTA"}, @{p=9999;n="PGN UDP";udp=$true}, @{p=5985;n="WinRM"})) {
    $nombre = "PilotX $($x.n) $($x.p)"
    if (-not (Get-NetFirewallRule -DisplayName $nombre -EA 0)) {
        try {
            New-NetFirewallRule -DisplayName $nombre -Direction Inbound `
                -Protocol $(if ($x.udp) { "UDP" } else { "TCP" }) `
                -LocalPort $x.p -Action Allow -Profile Any -EA Stop | Out-Null
        } catch { Mal "no se pudo abrir $($x.p)" }
    }
}
Ok "firewall: 6 puertos de PilotX abiertos"

# La hora sella los lotes y el sync; una pantalla en otra zona descuadra todo.
try {
    Set-TimeZone -Id "Argentina Standard Time"
    Start-Service w32time -EA SilentlyContinue; w32tm /resync /force 2>&1 | Out-Null
    Ok "hora: $(Get-Date)"
} catch { }

# Una pantalla de tractor no se duerme: se apaga cuando se corta la llave.
foreach ($t in @("standby-timeout-ac","standby-timeout-dc","hibernate-timeout-ac",
                 "hibernate-timeout-dc","monitor-timeout-ac","monitor-timeout-dc","disk-timeout-ac")) {
    powercfg /change $t 0 2>&1 | Out-Null
}
Ok "no se suspende ni apaga la pantalla"

# Usuarios. OJO: -NoPassword y -PasswordNeverExpires son incompatibles en
# New-LocalUser; sin contraseña no hay caducidad que configurar.
function CrearUsuario($nombre, $pass, $grupos) {
    if (Get-LocalUser -Name $nombre -EA 0) { Aviso "usuario '$nombre' ya existía" ; return $true }
    try {
        if ($pass) {
            New-LocalUser -Name $nombre -Password (ConvertTo-SecureString $pass -AsPlainText -Force) `
                          -FullName $nombre -PasswordNeverExpires -AccountNeverExpires -EA Stop | Out-Null
        } else {
            New-LocalUser -Name $nombre -NoPassword -FullName $nombre -AccountNeverExpires -EA Stop | Out-Null
            try { Set-LocalUser -Name $nombre -PasswordNeverExpires $true } catch { }
        }
    } catch { Mal "usuario '$nombre': $($_.Exception.Message)"; return $false }
    foreach ($g in $grupos) { try { Add-LocalGroupMember -Group $g -Member $nombre -EA Stop | Out-Null; break } catch { } }
    Ok "usuario '$nombre' creado"
    return $true
}
CrearUsuario "soporte" $SoportePass @("Administradores", "Administrators") | Out-Null

# Nombre del equipo. NUNCA "PILOTX" a secas: Windows no permite una cuenta
# local homónima del equipo y el usuario del operario se llama 'pilotx'.
if (-not $NombreEquipo) {
    $slug = ($Cliente -replace '[^A-Za-z0-9]', '').ToUpper()
    $NombreEquipo = ("PILOTX-" + $slug)
    if ($NombreEquipo.Length -gt 15) { $NombreEquipo = $NombreEquipo.Substring(0, 15) }
}
if ($NombreEquipo -ieq "pilotx") { $NombreEquipo = "PILOTX-CAMPO" }
if ($env:COMPUTERNAME -ne $NombreEquipo) {
    Rename-Computer -NewName $NombreEquipo -Force -EA SilentlyContinue
    Ok "equipo renombrado a $NombreEquipo (aplica al reiniciar)"
}

# Soporte remoto por consola, para no depender de que alguien mire la pantalla.
try { Enable-PSRemoting -Force -SkipNetworkProfileCheck -EA Stop | Out-Null; Ok "WinRM habilitado" }
catch { Aviso "WinRM no se pudo habilitar" }

# Acceso directo en el escritorio PÚBLICO: lo ve también el operario.
try {
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut("$env:PUBLIC\Desktop\PilotX.lnk")
    $lnk.TargetPath = "$Destino\Lanzar-PilotX.bat"; $lnk.WorkingDirectory = $Destino
    $lnk.WindowStyle = 7; $lnk.Description = "PilotX - Agro Parallel"
    if (Test-Path "$Destino\Desktop\PilotX.Desktop.exe") { $lnk.IconLocation = "$Destino\Desktop\PilotX.Desktop.exe,0" }
    $lnk.Save(); Ok "acceso directo en el escritorio"
} catch { }

# ── 6. Devolver los lotes ───────────────────────────────────────────────────
# PilotX busca los lotes en el Documentos DEL USUARIO QUE LO CORRE. Con kiosko
# eso es 'pilotx', no el usuario con el que estamos instalando: restaurar solo
# "acá" deja al operario sin un lote a la vista. Por eso se copia a todos los
# destinos que importan — pesan poco y es preferible duplicar que perderlos.
#   · el usuario actual        (el técnico los ve al instante)
#   · C:\Users\pilotx          (el operario, si el perfil ya existe)
#   · C:\Users\Default         (si 'pilotx' todavía no inició sesión: Windows
#                               copia Default al crear el perfil nuevo)
Titulo "6/8  Lotes del cliente"
if (-not $zipRescate) { Aviso "no había lotes que devolver" }
else {
    $tmp = Join-Path $env:TEMP "restaurar-$(Get-Date -f yyyyMMddHHmmss)"
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipRescate, $tmp)
    } catch { Mal "no se pudo abrir el rescate: $($_.Exception.Message)" }

    # El ZIP trae Fields/Vehicles en la raíz, o bajo origen1/, origen2/… cuando
    # se encontraron datos en varios discos.
    $origenes = @()
    if ((Test-Path "$tmp\Fields") -or (Test-Path "$tmp\Vehicles")) { $origenes += $tmp }
    $origenes += @(Get-ChildItem $tmp -Directory -Filter "origen*" -EA 0 | ForEach-Object { $_.FullName })

    $destinos = @([Environment]::GetFolderPath("MyDocuments"))
    if (Test-Path "C:\Users\pilotx")  { $destinos += "C:\Users\pilotx\Documents" }
    else                              { $destinos += "C:\Users\Default\Documents" }
    $destinos = $destinos | Select-Object -Unique

    $totalLotes = 0
    foreach ($d in $destinos) {
        $base = Join-Path $d "AgOpenGPS"
        foreach ($carpeta in @("Fields", "Vehicles")) {
            $dst = Join-Path $base $carpeta
            New-Item -ItemType Directory -Path $dst -Force | Out-Null
            foreach ($o in $origenes) {
                $src = Join-Path $o $carpeta
                if (-not (Test-Path $src)) { continue }
                foreach ($item in Get-ChildItem $src -EA 0) {
                    $destino = Join-Path $dst $item.Name
                    # Un lote que ya exista NO se pisa: entra como "<nombre>~aog".
                    if (Test-Path $destino) {
                        $destino = Join-Path $dst ($item.BaseName + "~aog" + $item.Extension)
                    }
                    try { Copy-Item $item.FullName $destino -Recurse -Force -EA Stop } catch { }
                }
            }
        }
        $n = @(Get-ChildItem (Join-Path $base "Fields") -Directory -EA 0).Count
        if ($d -eq $destinos[0]) { $totalLotes = $n }
        Ok "$n lotes en $base"
    }
    Remove-Item $tmp -Recurse -Force -EA SilentlyContinue

    if ($totalLotes -eq 0) { $problemas += "no quedó ningún lote restaurado" }
    Write-Host "  Verificá la geometría del perfil contra la máquina real antes de trabajar." -ForegroundColor Yellow
}

# ── 7. Probar que PilotX arranca ────────────────────────────────────────────
# La prueba de fuego. Si el motor no responde, el kiosko no se toca.
Titulo "7/8  Prueba de arranque"
$arranco = $false
$eng = "$Destino\Engine\PilotX.GuidanceEngine.exe"
if (-not (Test-Path $eng)) { $problemas += "no está $eng" }
else {
    $pr = $null
    try {
        $pr = Start-Process $eng -ArgumentList "--webhost", "--corex" `
                            -WorkingDirectory "$Destino\Engine" -PassThru -WindowStyle Hidden
        foreach ($i in 1..20) {
            Start-Sleep -Seconds 2
            try { Invoke-WebRequest "http://127.0.0.1:5180/api/aog/state" -TimeoutSec 3 -UseBasicParsing | Out-Null
                  $arranco = $true; break } catch { }
        }
    } catch { $problemas += "PilotX no pudo ejecutarse: $($_.Exception.Message)" }
    finally { if ($pr -and -not $pr.HasExited) { Stop-Process -Id $pr.Id -Force -EA SilentlyContinue } }
    if ($arranco) { Ok "PilotX arranca y responde" } else { $problemas += "PilotX no respondió en 40 s" }
}

# ── 8. Kiosko ───────────────────────────────────────────────────────────────
Titulo "8/8  Modo kiosko"
if ($SinKiosko) { Aviso "pedido sin kiosko" }
elseif ($problemas.Count -gt 0) {
    Mal "NO se activa el kiosko. Motivos:"
    foreach ($p in $problemas) { Write-Host "     - $p" -ForegroundColor Yellow }
    Write-Host "  Windows arranca normal: la pantalla es usable y se arregla desde acá." -ForegroundColor Yellow
}
else {
    # El que viaja DENTRO del paquete puede ser más viejo que el del kit: el
    # paquete 1.0.83 lleva uno anterior a los chequeos del 2026-09-24. Se
    # prefiere el del kit, y si se usa se deja copiado para la próxima vez.
    $k = Join-Path $Aqui "PilotX-KioskSetup.exe"
    if (Test-Path $k) { Copy-Item $k "$Destino\PilotX-KioskSetup.exe" -Force -EA SilentlyContinue }
    else { $k = "$Destino\PilotX-KioskSetup.exe" }
    if (Test-Path $k) {
        # El propio KioskSetup vuelve a verificar nombre de equipo, VC++ y que
        # el usuario quede creado, y aborta si algo no da. Doble red a propósito.
        Start-Process cmd -ArgumentList "/c `"$k`" /yes < NUL" -Wait -NoNewWindow
        $shell = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon").Shell
        if ($shell -like "*PilotX*") { Ok "kiosko activado" } else { Mal "el kiosko no quedó activado: $shell" }
    } else { Mal "no está $k" }
}

# ── Cierre ──────────────────────────────────────────────────────────────────
Write-Host ""
if ($problemas.Count -eq 0) {
    Write-Host "=== LISTO — $Cliente ===" -ForegroundColor Green
} else {
    Write-Host "=== TERMINÓ CON PENDIENTES ===" -ForegroundColor Yellow
    foreach ($p in $problemas) { Write-Host "  - $p" -ForegroundColor Yellow }
}
Write-Host ""
Write-Host "  Falta un paso a mano: vincular el equipo a la organización." -ForegroundColor Cyan
Write-Host "  En PilotX -> Configuracion -> Hub -> Vincular por codigo." -ForegroundColor Cyan
Write-Host "  Los equipos no se dan de alta a mano (ver migrar-desde-aog\README.md)." -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Admin: soporte / $SoportePass     Equipo: $NombreEquipo" -ForegroundColor DarkGray
Write-Host "  Reiniciá para aplicar el nombre del equipo." -ForegroundColor Yellow
Stop-Transcript | Out-Null

exit $(if ($problemas.Count -gt 0) { 1 } else { 0 })
