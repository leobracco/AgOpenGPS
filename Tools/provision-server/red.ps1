# ============================================================================
# red.ps1 — instala en la pantalla el helper de red (NetApplyWatcher, tarea
# PilotXNetApply como SYSTEM). Sin él, PilotX corre como usuario limitado y
# NO puede aplicar la IP fija del Ethernet (Configuración › Red): el pedido
# queda en C:\PilotX\netconfig\request.json y nadie lo atiende ("El helper de
# red no respondió").
#
#   En la pantalla (PowerShell, misma LAN que la PC del taller o por túnel):
#     irm __SERVIDOR__/red.ps1 | iex
#
# Instala la copia ENDURECIDA de este repo (Tools/net-apply/NetApplyWatcher.ps1,
# la misma que embebe PilotX-Instalador.exe) en una carpeta solo-admin:
#   %ProgramFiles%\PilotX Instalador\red\NetApplyWatcher.ps1  (+ netapply.log)
# Corre como SYSTEM: nunca desde C:\PilotX, que puede modificar el operario.
# Si había una instalación anterior en C:\PilotX\TabletTools, la tarea se
# re-apunta y el script viejo se borra.
# Lo llama también instalar.ps1 al final de una instalación nueva.
# ============================================================================
$ErrorActionPreference = "Stop"
$Servidor = "__SERVIDOR__"
function Paso($t) { Write-Host ">> $t" -ForegroundColor Cyan }

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Paso "Pidiendo permisos de administrador..."
    Start-Process powershell -Verb RunAs -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", "irm $Servidor/red.ps1 | iex"
    return
}

$Raiz = Join-Path $env:ProgramFiles "PilotX Instalador"
$DirRed = Join-Path $Raiz "red"
$netScript = Join-Path $DirRed "NetApplyWatcher.ps1"
$Pedidos = "C:\PilotX\netconfig"
$Viejo = "C:\PilotX\TabletTools"
$Tarea = "PilotXNetApply"

function EsEnlace([string]$ruta) {
    $it = Get-Item -LiteralPath $ruta -Force -EA SilentlyContinue
    return ($it -and (($it.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0))
}

# Permisos explícitos (sin herencia de arriba): Administradores y SYSTEM
# control total; los Usuarios solo leen si $usuariosLeen.
function PermisosSoloAdmin([string]$ruta, [bool]$usuariosLeen) {
    $hereda = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    $ds = New-Object Security.AccessControl.DirectorySecurity
    $ds.SetOwner((New-Object Security.Principal.SecurityIdentifier("S-1-5-32-544")))
    $ds.SetAccessRuleProtection($true, $false)
    $sids = @("S-1-5-32-544", "S-1-5-18")
    if ($usuariosLeen) { $sids += "S-1-5-32-545" }
    foreach ($s in $sids) {
        $derechos = if ($s -eq "S-1-5-32-545") { "ReadAndExecute" } else { "FullControl" }
        $ds.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($s)), $derechos, $hereda, "None", "Allow")))
    }
    return $ds
}

# 1) Carpeta solo-admin. La raíz, si ya existe, la maneja el instalador; la
#    de red siempre queda solo para Administradores y SYSTEM.
if (-not (Test-Path -LiteralPath $Raiz)) { [void][IO.Directory]::CreateDirectory($Raiz, (PermisosSoloAdmin $Raiz $true)) }
if (EsEnlace $Raiz) { throw "$Raiz es un enlace: no se usa" }
if (-not (Test-Path -LiteralPath $DirRed)) { [void][IO.Directory]::CreateDirectory($DirRed, (PermisosSoloAdmin $DirRed $false)) }
if (EsEnlace $DirRed) { throw "$DirRed es un enlace: no se usa" }
[IO.Directory]::SetAccessControl($DirRed, (PermisosSoloAdmin $DirRed $false))
Paso "Carpeta $DirRed (solo Administradores y SYSTEM)"

# 2) Bajar el watcher endurecido. Se baja a un temporal de la misma carpeta y
#    se controla que sea la copia endurecida y que sea PowerShell válido.
$tmp = "$netScript.descarga"
Invoke-WebRequest -UseBasicParsing -Uri "$Servidor/kit/NetApplyWatcher.ps1" -OutFile $tmp -TimeoutSec 60
$texto = [IO.File]::ReadAllText($tmp)
$errores = $null
[void][Management.Automation.Language.Parser]::ParseInput($texto, [ref]$null, [ref]$errores)
if ($errores.Count -gt 0 -or $texto -notmatch "Copia endurecida") {
    Remove-Item -LiteralPath $tmp -Force -EA SilentlyContinue
    throw "lo que bajó de $Servidor/kit/NetApplyWatcher.ps1 no es el helper endurecido"
}
Move-Item -LiteralPath $tmp -Destination $netScript -Force
Paso "NetApplyWatcher.ps1 copiado a $DirRed"

# 3) Carpeta de pedidos (la escribe PilotX como pilotx; el watcher ya no la crea).
if (EsEnlace "C:\PilotX") { throw "C:\PilotX es un enlace: no se usa" }
if (-not (Test-Path -LiteralPath $Pedidos)) { New-Item -ItemType Directory -Path $Pedidos -Force | Out-Null }
if (EsEnlace $Pedidos) { throw "$Pedidos es un enlace: no se usa" }
$op = Get-LocalUser -Name "pilotx" -EA SilentlyContinue
if ($op) {
    $acl = Get-Acl -LiteralPath $Pedidos
    $hereda = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($op.SID, "Modify", $hereda, "None", "Allow")))
    Set-Acl -LiteralPath $Pedidos -AclObject $acl
    Paso "$Pedidos : pilotx puede escribir"
}

# 4) Tarea SYSTEM al arranque, sin límite de tiempo, se relanza sola. Si ya
#    existía (p.ej. apuntando a C:\PilotX\TabletTools) se frena la instancia
#    que corre (el watcher es de instancia única) y se re-apunta.
if (Get-ScheduledTask -TaskName $Tarea -EA SilentlyContinue) {
    Stop-ScheduledTask -TaskName $Tarea -EA SilentlyContinue
    Paso "Tarea $Tarea existente: se re-apunta a $netScript"
}
$ps = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$arg = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $netScript + '"'
$action = New-ScheduledTaskAction -Execute $ps -Argument $arg
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval ([TimeSpan]::FromMinutes(1))
Register-ScheduledTask -TaskName $Tarea -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# 5) Script viejo (en una carpeta que el operario puede modificar): se borra.
if ((Test-Path -LiteralPath $Viejo) -and -not (EsEnlace $Viejo)) {
    $v = Join-Path $Viejo "NetApplyWatcher.ps1"
    if ((Test-Path -LiteralPath $v) -and -not (EsEnlace $v)) {
        try { Remove-Item -LiteralPath $v -Force; Paso "Borrado el script viejo $v" }
        catch { Write-Host "No se pudo borrar $v : $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if (-not (Get-ChildItem -LiteralPath $Viejo -Force -EA SilentlyContinue)) { Remove-Item -LiteralPath $Viejo -Force -EA SilentlyContinue }
}

Start-ScheduledTask -TaskName $Tarea
Start-Sleep -Seconds 3
$t = Get-ScheduledTask -TaskName $Tarea
$estado = $t.State
$apunta = ($t.Actions | Select-Object -First 1).Arguments
Paso "Tarea ${Tarea}: $estado ($apunta)"

Write-Host ""
if ($estado -eq "Running" -and $apunta -like "*$netScript*") {
    Write-Host "=== LISTO: el helper de red está corriendo ===" -ForegroundColor Green
    Write-Host "Ahora en PilotX › Configuración › Red volvé a aplicar la IP fija del Ethernet (por ejemplo 192.168.5.10 / 24)." -ForegroundColor Yellow
} else {
    Write-Host "La tarea no quedó corriendo. Ver $DirRed\netapply.log" -ForegroundColor Red
}
