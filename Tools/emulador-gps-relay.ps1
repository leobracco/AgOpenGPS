# =============================================================================
# emulador-gps-relay.ps1 — mete el GPS de la LAN adentro del emulador Android.
#
# POR QUÉ HACE FALTA
# El emulador corre detrás del NAT de QEMU: NO ve el broadcast UDP de la LAN,
# así que el bridge de PilotX.Android (que escucha en 0.0.0.0:9999 adentro)
# nunca recibe nada y la app queda sin velocidad, sin posición y sin fix.
# En una tablet real esto no pasa: está en la misma WiFi y recibe directo.
#
# CÓMO ENTRA ALGO AL EMULADOR
# Solo por un redir del propio emulador: un puerto del host (por defecto 9998)
# que QEMU reenvía al puerto del invitado (9999). Este script escucha el mismo
# stream que CoreX y copia cada datagrama a ese puerto.
#
# USO
#   .\tools\emulador-gps-relay.ps1                 # 9999 -> 127.0.0.1:9998
#   .\tools\emulador-gps-relay.ps1 -HostPort 9998  # si usaste otro redir
#   Ctrl+C para cortar.
#
# ANTES: dejá puesto el redir en la consola del emulador (una vez por arranque):
#   telnet localhost 5554
#   auth <token de %USERPROFILE%\.emulator_console_auth_token>
#   redir add udp:9998:9999
# Verificá que quedó: netstat -ano -p UDP | findstr 9998   (lo tiene qemu)
#
# NOTA: se bindea con ReuseAddress para convivir con CoreX, que ya tiene el
# 9999 tomado. Con tráfico broadcast (el que manda ModSim) los dos sockets
# reciben copia; si la fuente mandara unicast, uno solo de los dos la vería.
# =============================================================================
[CmdletBinding()]
param(
    [int]$ListenPort = 9999,
    [string]$TargetHost = "127.0.0.1",
    [int]$HostPort = 9998
)

$ErrorActionPreference = "Stop"

Write-Host "Relay GPS -> emulador" -ForegroundColor Green
Write-Host "  escucha : 0.0.0.0:$ListenPort (compartido con CoreX)"
Write-Host "  reenvia : ${TargetHost}:$HostPort (redir del emulador -> :9999 adentro)"
Write-Host ""

# Aviso temprano: sin redir, esto manda a un puerto que no escucha nadie.
$redir = netstat -ano -p UDP | Select-String ":$HostPort\s"
if (-not $redir) {
    Write-Warning "Nadie escucha en ${TargetHost}:$HostPort — falta el 'redir add udp:${HostPort}:9999' en la consola del emulador. Los datagramas se van a perder."
}

$sock = New-Object System.Net.Sockets.Socket(
    [System.Net.Sockets.AddressFamily]::InterNetwork,
    [System.Net.Sockets.SocketType]::Dgram,
    [System.Net.Sockets.ProtocolType]::Udp)
$sock.ExclusiveAddressUse = $false
$sock.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket,
                      [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
$sock.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, $ListenPort)))

$out = New-Object System.Net.Sockets.UdpClient
$target = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse($TargetHost), $HostPort)

$buf = New-Object byte[] 4096
$remote = [System.Net.EndPoint](New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0))
$n = 0
$nmea = 0
$pgn = 0

Write-Host "Andando. Ctrl+C para cortar." -ForegroundColor Green
try {
    while ($true) {
        $len = $sock.ReceiveFrom($buf, [ref]$remote)
        if ($len -le 0) { continue }

        $null = $out.Send($buf, $len, $target)
        $n++

        # Contadores por tipo: NMEA crudo ($) vs PGN ya envuelto (0x80 0x81).
        if ($buf[0] -eq 0x24) { $nmea++ }
        elseif ($buf[0] -eq 0x80 -and $buf[1] -eq 0x81) { $pgn++ }

        if ($n % 50 -eq 0) {
            Write-Host ("  {0} datagramas reenviados (NMEA {1} / PGN {2})" -f $n, $nmea, $pgn)
        }
    }
}
finally {
    $sock.Close()
    $out.Close()
    Write-Host "`nCortado. Total reenviado: $n" -ForegroundColor Yellow
}
