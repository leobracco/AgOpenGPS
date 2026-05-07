# =============================================================================
#  embed-status.ps1 - Diagnostico del modo kiosko PilotX
#
#  Imprime un reporte de estado actual de la PC: detecta si esta en modo embed
#  (kiosko) o en modo desktop normal. Util para debug en campo.
#
#  Reporta:
#    - Shell actual (explorer.exe vs AgOpenGPS.exe)
#    - AutoAdminLogon ON/OFF y usuario default
#    - Watchdog Task presente?
#    - Servicios desactivados / corriendo (los que toca embed-enable)
#    - Plan de energia activo
#    - Fast Startup ON/OFF
#    - Backup file presente (con timestamp)
#    - Usuario kiosko existe?
#    - Policies activas
#
#  Uso:
#     powershell -NoProfile -ExecutionPolicy Bypass -File embed-status.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File embed-status.ps1 -Json   # output JSON parseable
# =============================================================================

[CmdletBinding()]
param(
    [switch]$Json,
    [string]$KioskUser = "pilotx"
)

$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"

$DataDir    = "$env:ProgramData\PilotX"
$BackupFile = Join-Path $DataDir "embed-backup.json"

# ─── Recolector ─────────────────────────────────────────────────────────────
$state = [ordered]@{
    timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    host      = $env:COMPUTERNAME
    user      = $env:USERNAME
}

# Shell
try {
    $wlKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $shell = (Get-ItemProperty -Path $wlKey -Name Shell -ErrorAction SilentlyContinue).Shell
    $state.shell = if ($shell) { $shell } else { "(default explorer.exe)" }
} catch { $state.shell = "ERROR" }

# AutoAdminLogon
try {
    $wlKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $aal   = (Get-ItemProperty -Path $wlKey -Name AutoAdminLogon -ErrorAction SilentlyContinue).AutoAdminLogon
    $du    = (Get-ItemProperty -Path $wlKey -Name DefaultUserName -ErrorAction SilentlyContinue).DefaultUserName
    $state.autoLogon       = ($aal -eq "1" -or $aal -eq 1)
    $state.autoLogonUser   = if ($du) { $du } else { "(none)" }
} catch { $state.autoLogon = $null }

# Watchdog Task
try {
    $task = Get-ScheduledTask -TaskName "PilotX-Watchdog" -ErrorAction SilentlyContinue
    $state.watchdogTask = [bool]$task
    if ($task) { $state.watchdogState = "$($task.State)" }
} catch { $state.watchdogTask = $false }

# Servicios que toca embed-enable
$watchedSvcs = @(
    "DiagTrack","WSearch","SysMain","Spooler","WMPNetworkSvc",
    "MapsBroker","SCardSvr","SSDPSRV","upnphost","fdPHost","FDResPub",
    "DPS","PcaSvc","TabletInputService","XblAuthManager","XblGameSave","XboxGipSvc","XboxNetApiSvc"
)
$svcReport = @{}
foreach ($n in $watchedSvcs) {
    $s = Get-Service -Name $n -ErrorAction SilentlyContinue
    if ($s) {
        $svcReport[$n] = "$($s.StartType)/$($s.Status)"
    }
}
$state.services = $svcReport

# Plan de energia
try {
    $pwr = (powercfg /getactivescheme) -join " "
    $state.powerPlan = $pwr
} catch { $state.powerPlan = "ERROR" }

# Fast Startup
try {
    $fs = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power" `
           -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
    $state.fastStartup = ($fs -eq 1)
} catch { $state.fastStartup = $null }

# Backup file
if (Test-Path $BackupFile) {
    $state.backupExists = $true
    try {
        $b = Get-Content $BackupFile -Raw | ConvertFrom-Json
        $state.backupTimestamp = $b.timestamp
    } catch {
        $state.backupTimestamp = "(corrupt)"
    }
} else {
    $state.backupExists = $false
}

# Usuario kiosko
try {
    $u = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    $state.kioskUserExists = [bool]$u
    if ($u) {
        $state.kioskUserEnabled  = $u.Enabled
        $state.kioskUserPwdLast  = $u.PasswordLastSet
    }
} catch { $state.kioskUserExists = $false }

# Policies clave
$policies = @(
    @{ Name="NoLockScreen";                Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization" },
    @{ Name="DisableWindowsConsumerFeatures"; Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent" },
    @{ Name="AllowCortana";                Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search" },
    @{ Name="NoAutoRebootWithLoggedOnUsers"; Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" },
    @{ Name="DisableFileSyncNGSC";         Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive" }
)
$polReport = @{}
foreach ($p in $policies) {
    $v = (Get-ItemProperty -Path $p.Path -Name $p.Name -ErrorAction SilentlyContinue).$($p.Name)
    if ($null -ne $v) { $polReport[$p.Name] = $v }
}
$state.policies = $polReport

# AnyDesk
$ad = Get-Service -Name "AnyDesk" -ErrorAction SilentlyContinue
if ($ad) { $state.anydesk = "$($ad.StartType)/$($ad.Status)" } else { $state.anydesk = "(not installed)" }

# Veredicto: estamos en modo embed?
$inEmbed = (
    ($state.shell -like "*AgOpenGPS*") -or
    $state.autoLogon -or
    $state.watchdogTask -or
    $state.backupExists
)
$state.embedMode = $inEmbed

# ─── Output ─────────────────────────────────────────────────────────────────
if ($Json) {
    $state | ConvertTo-Json -Depth 5
    return
}

Write-Host ""
Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host " PilotX Embed Mode Status" -ForegroundColor Cyan
Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host (" Host             : {0}" -f $state.host)
Write-Host (" Usuario actual   : {0}" -f $state.user)
Write-Host (" Timestamp        : {0}" -f $state.timestamp)
Write-Host ""
if ($inEmbed) {
    Write-Host " >>> MODO EMBED (KIOSKO) ACTIVO <<<" -ForegroundColor Yellow
} else {
    Write-Host " >>> Modo desktop normal" -ForegroundColor Green
}
Write-Host ""
Write-Host " Shell            : $($state.shell)"
Write-Host " AutoLogon        : $($state.autoLogon)  [$($state.autoLogonUser)]"
Write-Host " Watchdog Task    : $($state.watchdogTask)"
if ($state.watchdogState) { Write-Host "   estado          : $($state.watchdogState)" }
Write-Host " Plan de energia  : $($state.powerPlan)"
Write-Host " Fast Startup     : $($state.fastStartup)"
Write-Host " Backup file      : $($state.backupExists)  [$($state.backupTimestamp)]"
Write-Host " Usuario kiosko   : $KioskUser exists=$($state.kioskUserExists) enabled=$($state.kioskUserEnabled)"
Write-Host " AnyDesk svc      : $($state.anydesk)"
Write-Host ""
Write-Host " Servicios (StartType/Status):" -ForegroundColor Gray
foreach ($k in $svcReport.Keys | Sort-Object) {
    Write-Host ("   {0,-22} {1}" -f $k, $svcReport[$k])
}
Write-Host ""
if ($polReport.Count -gt 0) {
    Write-Host " Policies activas:" -ForegroundColor Gray
    foreach ($k in $polReport.Keys | Sort-Object) {
        Write-Host ("   {0,-32} {1}" -f $k, $polReport[$k])
    }
} else {
    Write-Host " Policies activas: (ninguna de las nuestras)" -ForegroundColor Gray
}
Write-Host ""
Write-Host " Comandos utiles:" -ForegroundColor Gray
Write-Host "   embed-enable.ps1   - activar modo kiosko"
Write-Host "   embed-disable.ps1  - revertir a desktop normal"
Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host ""
