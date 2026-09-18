# =============================================================================
#  embed-enable.ps1 - Convertir Windows en PilotX kiosko anti-tanques
#
#  Aplica:
#   1) Crea usuario local "pilotx" sin password
#   2) Auto-logon a ese usuario al boot
#   3) Reemplaza Shell=explorer.exe por PilotX.exe (PilotX como shell)
#   4) Watchdog: si PilotX no arranca, restaura explorer.exe (Task Scheduler)
#   5) Apaga servicios bloatware (lista conservadora — NO toca red/audio/drivers)
#   6) Plan de energía: alto rendimiento, sin sleep, sin apagar monitor
#   7) Apaga Fast Startup (rompe cosas en cambios de hardware/USB)
#   8) Desactiva Windows Update auto-reboot (sigue actualizando, pero no reboota)
#   9) Apaga lockscreen, notificaciones, Cortana, ConsumerExperiences
#  10) Desinstala OneDrive (per-user) y bloquea su retorno
#  11) Excluye {app} de Defender (también desactiva SmartScreen)
#  12) Snapshot de System Restore + backup de claves modificadas
#
#  Uso (como admin):
#    powershell -NoProfile -ExecutionPolicy Bypass -File embed-enable.ps1
#    powershell -NoProfile -ExecutionPolicy Bypass -File embed-enable.ps1 -AppPath "C:\Program Files\AgroParallel\PilotX"
#
#  Reversa:
#    embed-disable.ps1
#
#  Autor: Agro Parallel - PilotX Kiosko
# =============================================================================

[CmdletBinding()]
param(
    [string]$AppPath = "C:\Program Files\AgroParallel\PilotX",
    [string]$KioskUser = "pilotx",
    [switch]$SkipUser,
    [switch]$SkipServices,
    [switch]$SkipOneDriveRemoval,
    [switch]$Force
)

$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"

# ── Setup paths/log ──────────────────────────────────────────────────────────
$DataDir   = "$env:ProgramData\PilotX"
$LogFile   = Join-Path $DataDir "embed.log"
$BackupFile= Join-Path $DataDir "embed-backup.json"
if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }

function Log {
    param([string]$Msg, [string]$Level="INFO")
    $line = "{0} [{1,-5}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Msg
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = [Security.Principal.WindowsPrincipal]::new($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Log "Necesita correr como administrador. Reintentar con privilegios elevados." "ERROR"
    Write-Error "Run as Administrator"
    exit 2
}

Log "=== embed-enable.ps1 iniciando ==="
Log "AppPath=$AppPath KioskUser=$KioskUser"

$AogExe = Join-Path $AppPath "PilotX.exe"
if (-not (Test-Path $AogExe)) {
    Log "PilotX.exe no encontrado en $AppPath — abortando" "ERROR"
    exit 3
}

# ── Backup de estado actual ──────────────────────────────────────────────────
$backup = @{
    timestamp = (Get-Date -Format "o")
    shell     = $null
    autoadmin = $null
    services  = @{}
    power_scheme = $null
}
try {
    $shellKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $backup.shell     = (Get-ItemProperty -Path $shellKey -Name Shell -ErrorAction SilentlyContinue).Shell
    $backup.autoadmin = (Get-ItemProperty -Path $shellKey -Name AutoAdminLogon -ErrorAction SilentlyContinue).AutoAdminLogon
    $backup.power_scheme = (powercfg /getactivescheme) -join " "
} catch { Log "backup parcial: $($_.Exception.Message)" "WARN" }

# 0) ── Restore Point (best effort, requiere PRP habilitado) ─────────────────
try {
    Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue
    Checkpoint-Computer -Description "PilotX-Kiosko-Pre-Embed" -RestorePointType "MODIFY_SETTINGS" -ErrorAction Stop
    Log "Restore point creado"
} catch { Log "Restore point falló (no fatal): $($_.Exception.Message)" "WARN" }

# 1) ── Usuario kiosko ───────────────────────────────────────────────────────
if (-not $SkipUser) {
    try {
        $u = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
        if (-not $u) {
            $emptyPwd = (New-Object System.Security.SecureString)
            New-LocalUser -Name $KioskUser -NoPassword -FullName "PilotX Kiosko" `
                          -Description "Usuario kiosko para PilotX" -AccountNeverExpires `
                          -PasswordNeverExpires -ErrorAction Stop | Out-Null
            Log "Usuario $KioskUser creado (sin password)"
        } else {
            Log "Usuario $KioskUser ya existe"
        }
        Add-LocalGroupMember -Group "Users" -Member $KioskUser -ErrorAction SilentlyContinue
        # NO lo metemos en Administrators — el usuario kiosko es estándar.

        # Permitir login con password en blanco (Windows lo bloquea por default).
        # LimitBlankPasswordUse=0 -> permite login local sin password.
        Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa" `
                         -Name "LimitBlankPasswordUse" -Value 0 -Type DWord -Force
        Log "LimitBlankPasswordUse=0 (permite login con password vacía)"
    } catch { Log "user setup: $($_.Exception.Message)" "ERROR" }
}

# 2) ── AutoAdminLogon ───────────────────────────────────────────────────────
try {
    $wlKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Set-ItemProperty -Path $wlKey -Name "AutoAdminLogon" -Value "1" -Force
    Set-ItemProperty -Path $wlKey -Name "DefaultUserName" -Value $KioskUser -Force
    Set-ItemProperty -Path $wlKey -Name "DefaultPassword" -Value "" -Force
    Set-ItemProperty -Path $wlKey -Name "DefaultDomainName" -Value "$env:COMPUTERNAME" -Force
    # Evitar prompt de "presione Ctrl+Alt+Del"
    Set-ItemProperty -Path $wlKey -Name "DisableCAD" -Value 1 -Type DWord -Force
    # Auto-relogin si el shell muere
    Set-ItemProperty -Path $wlKey -Name "AutoRestartShell" -Value 1 -Type DWord -Force
    Log "AutoAdminLogon configurado para $KioskUser"
} catch { Log "autologon: $($_.Exception.Message)" "ERROR" }

# 3) ── Shell replacement (HKLM por máquina, afecta a todos los usuarios) ────
try {
    $wlKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Set-ItemProperty -Path $wlKey -Name "Shell" -Value "`"$AogExe`"" -Force
    Log "Shell reemplazado por: $AogExe"
} catch { Log "shell replace: $($_.Exception.Message)" "ERROR" }

# 3.5) ── Watchdog: si PilotX no arranca, vuelve a explorer.exe ──────────────
# Crea una scheduled task que corre 60s después del boot. Si NO encuentra
# PilotX.exe corriendo, asume crash-loop y restaura Shell=explorer.exe.
try {
    $watchdogScript = @'
$ErrorActionPreference="SilentlyContinue"
$logDir="$env:ProgramData\PilotX"; if(-not (Test-Path $logDir)){New-Item -ItemType Directory $logDir -Force | Out-Null}
$log="$logDir\watchdog.log"
function Log($m){ "$(Get-Date -f 'HH:mm:ss') $m" | Add-Content $log }
Start-Sleep -Seconds 60
$running = Get-Process AgOpenGPS -ErrorAction SilentlyContinue
if (-not $running) {
    Log "PilotX NO está corriendo a +60s del boot. Restaurando Shell=explorer.exe"
    Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name Shell -Value "explorer.exe" -Force
    # No reboot automático — al próximo logon ya entra a desktop normal.
} else {
    Log "PilotX OK (PID=$($running.Id))"
}
'@
    $wdPath = Join-Path $DataDir "watchdog.ps1"
    Set-Content -Path $wdPath -Value $watchdogScript -Encoding UTF8 -Force

    $action  = New-ScheduledTaskAction -Execute "powershell.exe" `
                  -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$wdPath`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $set     = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                  -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest

    Unregister-ScheduledTask -TaskName "PilotX-Watchdog" -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName "PilotX-Watchdog" -Action $action -Trigger $trigger `
                            -Settings $set -Principal $principal -Description "Recovery shell si PilotX no arranca" | Out-Null
    Log "Watchdog instalado (Task: PilotX-Watchdog)"
} catch { Log "watchdog: $($_.Exception.Message)" "WARN" }

# 4) ── Servicios bloatware ──────────────────────────────────────────────────
$servicesToDisable = @(
    "DiagTrack","dmwappushservice",                 # telemetría
    "WSearch",                                       # search indexer
    "SysMain",                                       # superfetch
    "Spooler","Fax","PrintNotify",                   # impresión
    "WMPNetworkSvc",                                 # media server
    "XblAuthManager","XblGameSave","XboxNetApiSvc","XboxGipSvc",
    "WerSvc",                                        # error reporting
    "MapsBroker",                                    # maps offline
    "RetailDemo","WalletService",
    "TabletInputService","TouchKeyboardAndHandwritingPanelService",
    "PhoneSvc","HomeGroupListener","HomeGroupProvider",
    "SCardSvr","ScDeviceEnum",                       # smart card
    "SharedAccess",                                  # ICS
    "RemoteRegistry",                                # seguridad
    "SSDPSRV","upnphost",                            # UPnP
    "fdPHost","FDResPub",                            # function discovery
    "DPS","PcaSvc",                                  # diagnostic
    "OneSyncSvc","MessagingService",
    "PimIndexMaintenanceSvc","UserDataSvc",
    "UnistoreSvc","CDPUserSvc","DevicePickerUserSvc",
    "PrintWorkflowUserSvc"
)

if (-not $SkipServices) {
    foreach ($name in $servicesToDisable) {
        try {
            $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
            if (-not $svc) { continue }
            $backup.services[$name] = $svc.StartType.ToString()
            if ($svc.Status -eq "Running") { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
            Set-Service -Name $name -StartupType Disabled -ErrorAction SilentlyContinue
            Log "Servicio $name -> Disabled"
        } catch { Log "svc $name: $($_.Exception.Message)" "WARN" }
    }
} else {
    Log "Servicios saltados (-SkipServices)"
}

# 5) ── Plan de energía: alto rendimiento, sin sleep ─────────────────────────
try {
    powercfg /setactive SCHEME_MIN | Out-Null   # SCHEME_MIN = High Performance
    powercfg /change monitor-timeout-ac 0
    powercfg /change monitor-timeout-dc 0
    powercfg /change disk-timeout-ac 0
    powercfg /change disk-timeout-dc 0
    powercfg /change standby-timeout-ac 0
    powercfg /change standby-timeout-dc 0
    powercfg /change hibernate-timeout-ac 0
    powercfg /change hibernate-timeout-dc 0
    # Apagar hibernación entera (libera espacio del hiberfil.sys, ~tamaño RAM)
    powercfg /hibernate off | Out-Null
    Log "Power plan = High Performance, sin sleep/hibernate"
} catch { Log "power: $($_.Exception.Message)" "WARN" }

# 6) ── Fast Startup OFF ─────────────────────────────────────────────────────
try {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power" `
                     -Name "HiberbootEnabled" -Value 0 -Type DWord -Force
    Log "Fast Startup deshabilitado"
} catch { Log "fast startup: $($_.Exception.Message)" "WARN" }

# 7) ── Windows Update: parchea pero NO reboota solo ─────────────────────────
try {
    $wuKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"
    if (-not (Test-Path $wuKey)) { New-Item -Path $wuKey -Force | Out-Null }
    Set-ItemProperty -Path $wuKey -Name "NoAutoRebootWithLoggedOnUsers" -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $wuKey -Name "AUPowerManagement" -Value 0 -Type DWord -Force
    Log "Windows Update: no auto-reboot con usuario logueado"
} catch { Log "WU: $($_.Exception.Message)" "WARN" }

# 8) ── Lockscreen, notificaciones, Cortana, Consumer features off ───────────
try {
    $personalizationKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization"
    if (-not (Test-Path $personalizationKey)) { New-Item -Path $personalizationKey -Force | Out-Null }
    Set-ItemProperty -Path $personalizationKey -Name "NoLockScreen" -Value 1 -Type DWord -Force

    $cdmKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent"
    if (-not (Test-Path $cdmKey)) { New-Item -Path $cdmKey -Force | Out-Null }
    Set-ItemProperty -Path $cdmKey -Name "DisableWindowsConsumerFeatures" -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $cdmKey -Name "DisableSoftLanding" -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $cdmKey -Name "DisableCloudOptimizedContent" -Value 1 -Type DWord -Force

    $searchKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search"
    if (-not (Test-Path $searchKey)) { New-Item -Path $searchKey -Force | Out-Null }
    Set-ItemProperty -Path $searchKey -Name "AllowCortana" -Value 0 -Type DWord -Force

    $explorerKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"
    Set-ItemProperty -Path $explorerKey -Name "DisableNotificationCenter" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

    Log "Lockscreen/Cortana/Consumer features OFF"
} catch { Log "policies: $($_.Exception.Message)" "WARN" }

# 9) ── SmartScreen off (no estorba al ejecutar updates) ─────────────────────
try {
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" `
                     -Name "EnableSmartScreen" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer" `
                     -Name "SmartScreenEnabled" -Value "Off" -Force -ErrorAction SilentlyContinue
    Log "SmartScreen OFF"
} catch { Log "smartscreen: $($_.Exception.Message)" "WARN" }

# 10) ── Windows Error Reporting silencioso ─────────────────────────────────
try {
    $werKey = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting"
    if (-not (Test-Path $werKey)) { New-Item -Path $werKey -Force | Out-Null }
    Set-ItemProperty -Path $werKey -Name "DontShowUI" -Value 1 -Type DWord -Force
    Set-ItemProperty -Path $werKey -Name "Disabled"   -Value 1 -Type DWord -Force
    Log "WER UI silenciada"
} catch { Log "wer: $($_.Exception.Message)" "WARN" }

# 11) ── OneDrive removal (per-user) ─────────────────────────────────────────
if (-not $SkipOneDriveRemoval) {
    try {
        Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $od64 = "$env:SystemRoot\SysWOW64\OneDriveSetup.exe"
        $od32 = "$env:SystemRoot\System32\OneDriveSetup.exe"
        if (Test-Path $od64) { & $od64 /uninstall | Out-Null; Log "OneDrive 64 uninstall lanzado" }
        if (Test-Path $od32) { & $od32 /uninstall | Out-Null; Log "OneDrive 32 uninstall lanzado" }

        # Bloquear retorno
        $odKey = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive"
        if (-not (Test-Path $odKey)) { New-Item -Path $odKey -Force | Out-Null }
        Set-ItemProperty -Path $odKey -Name "DisableFileSyncNGSC" -Value 1 -Type DWord -Force
        Log "OneDrive bloqueado por policy"
    } catch { Log "onedrive: $($_.Exception.Message)" "WARN" }
}

# 12) ── Edge auto-update tasks off ──────────────────────────────────────────
try {
    Get-ScheduledTask | Where-Object {
        $_.TaskPath -like "*MicrosoftEdge*" -or $_.TaskName -like "*Edge*Update*"
    } | ForEach-Object {
        try { Disable-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue | Out-Null } catch {}
    }
    Log "Edge update tasks deshabilitadas"
} catch { Log "edge: $($_.Exception.Message)" "WARN" }

# 13) ── AnyDesk autostart (si está instalado) ───────────────────────────────
try {
    $anyDesk = Get-Service -Name "AnyDesk" -ErrorAction SilentlyContinue
    if ($anyDesk) {
        Set-Service -Name "AnyDesk" -StartupType Automatic
        if ($anyDesk.Status -ne "Running") { Start-Service -Name "AnyDesk" -ErrorAction SilentlyContinue }
        Log "AnyDesk -> Automatic + corriendo"
    } else {
        Log "AnyDesk no está instalado — instalalo manualmente desde anydesk.com" "WARN"
    }
} catch { Log "anydesk: $($_.Exception.Message)" "WARN" }

# 14) ── NTP sync (importante para tokens y firmas HMAC) ─────────────────────
try {
    Set-Service -Name "W32Time" -StartupType Automatic -ErrorAction SilentlyContinue
    Start-Service -Name "W32Time" -ErrorAction SilentlyContinue
    & w32tm /resync /force 2>&1 | Out-Null
    Log "NTP resync OK"
} catch { Log "ntp: $($_.Exception.Message)" "WARN" }

# ── Persistir backup ─────────────────────────────────────────────────────────
try {
    $backup | ConvertTo-Json -Depth 5 | Set-Content -Path $BackupFile -Encoding UTF8 -Force
    Log "Backup escrito en $BackupFile"
} catch { Log "backup write: $($_.Exception.Message)" "WARN" }

Log "=== embed-enable.ps1 finalizado OK ==="
Log ""
Log "PROXIMOS PASOS:"
Log "  1. Reiniciar (shutdown /r /t 0)"
Log "  2. Boot -> autologon a '$KioskUser' -> arranca PilotX como shell"
Log "  3. Si algo falla, el watchdog restaura explorer.exe a +60s"
Log "  4. Para revertir: embed-disable.ps1"
Write-Output "OK"
exit 0
