# ============================================================================
#  remoto.ps1 - abre la pantalla para que la manejemos por consola desde la LAN.
#
#  Para que existe: RustDesk sirve para ver la pantalla, pero el trabajo de
#  provisioning es todo consola (instalar runtimes, revisar servicios, probar
#  que PilotX abra). WinRM es el camino corto y no depende de que haya alguien
#  mirando la pantalla.
#
#  Se corre en la pantalla con UNA linea:
#      irm <IP_SERVIDOR>:8090/remoto.ps1|iex
#
#  Es idempotente.
# ============================================================================
$ErrorActionPreference = "Continue"

Write-Host ""
Write-Host "=== Habilitando acceso remoto por consola ===" -ForegroundColor Cyan
Write-Host ""

# La NIC tiene que estar en perfil Privado: con perfil Publico el firewall de
# Windows bloquea WinRM aunque la regla exista. Es la misma trampa que nos
# comio en los modulos X-*.
try {
    Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq "Public" } |
        ForEach-Object {
            Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
            Write-Host "  [OK] Red '$($_.Name)' pasada de Publica a Privada" -ForegroundColor Green
        }
} catch { Write-Host "  [!!] No se pudo cambiar el perfil de red: $($_.Exception.Message)" -ForegroundColor Red }

try {
    Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
    Write-Host "  [OK] WinRM encendido" -ForegroundColor Green
} catch { Write-Host "  [!!] WinRM: $($_.Exception.Message)" -ForegroundColor Red }

try {
    Set-Item WSMan:\localhost\Client\TrustedHosts -Value "*" -Force
    Set-Item WSMan:\localhost\Service\Auth\Basic -Value $false -Force -ErrorAction SilentlyContinue
    Write-Host "  [OK] Confianza de clientes configurada" -ForegroundColor Green
} catch { Write-Host "  [!!] TrustedHosts: $($_.Exception.Message)" -ForegroundColor Red }

# La regla que trae Windows a veces queda atada solo al perfil de Dominio.
try {
    Enable-NetFirewallRule -Name "WINRM-HTTP-In-TCP*" -ErrorAction SilentlyContinue
    if (-not (Get-NetFirewallRule -DisplayName "PilotX WinRM 5985" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "PilotX WinRM 5985" -Direction Inbound -Protocol TCP `
                            -LocalPort 5985 -Action Allow -Profile Any | Out-Null
    }
    Write-Host "  [OK] Puerto 5985 abierto en el firewall" -ForegroundColor Green
} catch { Write-Host "  [!!] Firewall: $($_.Exception.Message)" -ForegroundColor Red }

Write-Host ""
Write-Host "=== DECIME ESTOS DATOS ===" -ForegroundColor Green
Write-Host "  Equipo: $env:COMPUTERNAME" -ForegroundColor Yellow
Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
    ForEach-Object { Write-Host "  IP:     $($_.IPAddress)   ($($_.InterfaceAlias))" -ForegroundColor Yellow }
Write-Host ""
