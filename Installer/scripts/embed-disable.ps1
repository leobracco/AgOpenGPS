# =============================================================================
#  embed-disable.ps1 - Revertir Windows del modo kiosko PilotX a desktop normal
#
#  Lee el backup que dejó embed-enable.ps1 y restaura:
#   1) Shell=explorer.exe
#   2) AutoAdminLogon OFF (limpia DefaultUserName/Password)
#   3) Servicios: vuelven al StartType original
#   4) Plan de energía: vuelve al original (best effort)
#   5) Fast Startup, Lockscreen, Cortana, Consumer features → defaults
#   6) Watchdog Task eliminada
#   7) Hibernación on (si la querés)
#   8) Policies WindowsUpdate / OneDrive borradas
#   9) Permite blank password vuelve al default (LimitBlankPasswordUse=1)
#
#  El usuario "pilotx" NO se borra (puede haber datos en su perfil).
#  Si querés borrarlo:
#     Remove-LocalUser -Name pilotx
#     Remove-Item "C:\Users\pilotx" -Recurse -Force
#
#  Uso (admin):
#     powershell -NoProfile -ExecutionPolicy Bypass -File embed-disable.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File embed-disable.ps1 -Reboot
# =============================================================================

[CmdletBinding()]
param(
    [switch]$Reboot,
    [switch]$RemoveKioskUser,
    [string]$KioskUser = "pilotx"
)

$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"

$DataDir    = "$env:ProgramData\PilotX"
$LogFile    = Join-Path $DataDir "embed.log"
$BackupFile = Join-Path $DataDir "embed-backup.json"

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
    Write-Error "Run as Administrator"
    exit 2
}

Log "=== embed-disable.ps1 iniciando ==="

$backup = $null
if (Test-Path $BackupFile) {
    try {
        $backup = Get-Content $BackupFile -Raw | ConvertFrom-Json
        Log "Backup cargado de $BackupFile (timestamp $($backup.timestamp))"
    } catch {
        Log "No pude parsear backup: $($_.Exception.Message). Sigo con defaults" "WARN"
    }
} else {
    Log "No hay backup en $BackupFile — uso defaults" "WARN"
}

# 1) ── Shell ────────────────────────────────────────────────────────────────
try {
    $wlKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $orig  = if ($backup -and $backup.shell) { $backup.shell } else { "explorer.exe" }
    Set-ItemProperty -Path $wlKey -Name "Shell" -Value $orig -Force
    Log "Shell restaurado a: $orig"
} catch { Log "shell restore: $($_.Exception.Message)" "ERROR" }

# 2) ── AutoAdminLogon OFF ───────────────────────────────────────────────────
try {
    $wlKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Set-ItemProperty -Path $wlKey -Name "AutoAdminLogon" -Value "0" -Force
    Remove-ItemProperty -Path $wlKey -Name "DefaultUserName"   -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $wlKey -Name "DefaultPassword"   -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $wlKey -Name "DefaultDomainName" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $wlKey -Name "DisableCAD"        -ErrorAction SilentlyContinue
    Log "AutoAdminLogon OFF, credenciales por defecto borradas"
} catch { Log "autologon revert: $($_.Exception.Message)" "ERROR" }

# Volver el LimitBlankPasswordUse al default (=1, bloquea login con pwd vacía remoto)
try {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Lsa" `
                     -Name "LimitBlankPasswordUse" -Value 1 -Type DWord -Force
    Log "LimitBlankPasswordUse=1 (default)"
} catch { Log "lsa: $($_.Exception.Message)" "WARN" }

# 3) ── Watchdog Task ────────────────────────────────────────────────────────
try {
    Unregister-ScheduledTask -TaskName "PilotX-Watchdog" -Confirm:$false -ErrorAction SilentlyContinue
    $wdPath = Join-Path $DataDir "watchdog.ps1"
    if (Test-Path $wdPath) { Remove-Item $wdPath -Force -ErrorAction SilentlyContinue }
    Log "Watchdog task eliminada"
} catch { Log "watchdog rm: $($_.Exception.Message)" "WARN" }

# 4) ── Servicios: restaurar StartType ───────────────────────────────────────
if ($backup -and $backup.services) {
    foreach ($name in $backup.services.PSObject.Properties.Name) {
        $orig = $backup.services.$name
        try {
            $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
            if (-not $svc) { continue }
            # PowerShell StartType strings: Automatic, Manual, Disabled, Boot, System
            $startType = switch ($orig) {
                "Automatic" { "Automatic" }
                "Manual"    { "Manual" }
                "Disabled"  { "Disabled" }
                default     { "Manual" }
            }
            Set-Service -Name $name -StartupType $startType -ErrorAction SilentlyContinue
            Log "Servicio $name → $startType (original)"
        } catch { Log "svc revert $name : $($_.Exception.Message)" "WARN" }
    }
} else {
    # Sin backup: poner en Manual los que sabemos que tocó embed-enable
    $knownServices = @(
        "DiagTrack","WSearch","SysMain","Spooler","WMPNetworkSvc",
        "MapsBroker","SCardSvr","SSDPSRV","upnphost","fdPHost","FDResPub",
        "DPS","PcaSvc","TabletInputService"
    )
    foreach ($name in $knownServices) {
        try {
            $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
            if ($svc) {
                Set-Service -Name $name -StartupType Manual -ErrorAction SilentlyContinue
                Log "Servicio $name → Manual (sin backup, default conservador)"
            }
        } catch {}
    }
}

# 5) ── Plan de energía ──────────────────────────────────────────────────────
try {
    # Vuelve a Balanced (default Windows) si no hay backup específico
    $balancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e"
    powercfg /setactive $balancedGuid | Out-Null
    Log "Plan de energía → Balanced"

    # Restaurar timeouts default razonables (30 min monitor, 60 min disk, sleep)
    powercfg /change monitor-timeout-ac 15
    powercfg /change monitor-timeout-dc 5
    powercfg /change disk-timeout-ac    20
    powercfg /change disk-timeout-dc    10
    powercfg /change standby-timeout-ac 30
    powercfg /change standby-timeout-dc 15
    Log "Timeouts de energía restaurados a defaults"
} catch { Log "power revert: $($_.Exception.Message)" "WARN" }

# 6) ── Fast Startup ON (default Windows) ────────────────────────────────────
try {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power" `
                     -Name "HiberbootEnabled" -Value 1 -Type DWord -Force
    powercfg /hibernate on | Out-Null
    Log "Fast Startup + hibernación ON (default)"
} catch { Log "fast startup revert: $($_.Exception.Message)" "WARN" }

# 7) ── Borrar policies que pusimos ──────────────────────────────────────────
$policiesToClear = @(
    @{ Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"; Names=@("NoAutoRebootWithLoggedOnUsers","AUPowerManagement") },
    @{ Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization"; Names=@("NoLockScreen") },
    @{ Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent";    Names=@("DisableWindowsConsumerFeatures","DisableSoftLanding","DisableCloudOptimizedContent") },
    @{ Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search";  Names=@("AllowCortana") },
    @{ Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\System";          Names=@("EnableSmartScreen") },
    @{ Path="HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive";        Names=@("DisableFileSyncNGSC") },
    @{ Path="HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting";  Names=@("DontShowUI","Disabled") },
    @{ Path="HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";  Names=@("DisableNotificationCenter","SmartScreenEnabled") }
)
foreach ($pol in $policiesToClear) {
    if (Test-Path $pol.Path) {
        foreach ($n in $pol.Names) {
            Remove-ItemProperty -Path $pol.Path -Name $n -ErrorAction SilentlyContinue
        }
        Log "Policy limpiada: $($pol.Path) → $($pol.Names -join ',')"
    }
}

# 8) ── Edge update tasks: re-habilitar ──────────────────────────────────────
try {
    Get-ScheduledTask | Where-Object {
        $_.TaskPath -like "*MicrosoftEdge*" -or $_.TaskName -like "*Edge*Update*"
    } | ForEach-Object {
        try { Enable-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue | Out-Null } catch {}
    }
    Log "Edge update tasks re-habilitadas"
} catch { Log "edge re-enable: $($_.Exception.Message)" "WARN" }

# 9) ── Borrar usuario kiosko (opcional) ─────────────────────────────────────
if ($RemoveKioskUser) {
    try {
        $u = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
        if ($u) {
            Remove-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
            $profile = "$env:SystemDrive\Users\$KioskUser"
            if (Test-Path $profile) {
                # No borrar perfil si está logueado — Windows lo bloquea igual
                Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
            }
            Log "Usuario $KioskUser eliminado (con perfil)"
        }
    } catch { Log "rm user: $($_.Exception.Message)" "WARN" }
}

Log "=== embed-disable.ps1 finalizado OK ==="

if ($Reboot) {
    Log "Reboot en 5s..."
    Start-Sleep -Seconds 5
    Restart-Computer -Force
}

Write-Output "OK"
exit 0
