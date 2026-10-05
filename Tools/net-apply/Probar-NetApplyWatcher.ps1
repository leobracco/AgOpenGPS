# ============================================================
# Probar-NetApplyWatcher.ps1 — autoprueba de NetApplyWatcher.ps1 (sin Pester).
#
# Carga las funciones del watcher con -SoloFunciones (NO arranca el ciclo, no
# toca la red ni C:\PilotX) y verifica:
#  - la validación de IPs, prefijos y pedidos con una tabla de casos buenos y malos;
#  - la detección de enlaces (junction) y el acceso seguro a archivos
#    (lectura+borrado por handle, hardlinks rechazados, escritura atómica)
#    en una carpeta temporal.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File Probar-NetApplyWatcher.ps1
# Sale con código 0 si todo pasa, 1 si algo falla.
# ============================================================
param([string]$Carpeta = (Join-Path ([System.IO.Path]::GetTempPath()) ("netapply-prueba-" + [guid]::NewGuid().ToString('N'))))

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NetApplyWatcher.ps1') -SoloFunciones

$script:fallas = 0
$script:total = 0
function Esperar($nombre, $obtenido, $esperado) {
    $script:total++
    if ($obtenido -eq $esperado) { Write-Host ("  ok    " + $nombre) }
    else { $script:fallas++; Write-Host ("  FALLA " + $nombre + " -> obtenido=" + $obtenido + " esperado=" + $esperado) -ForegroundColor Red }
}
function Pedido($json) { $json | ConvertFrom-Json }

Write-Host "== IPv4 exacta =="
$buenas = @('192.168.5.10', '8.8.8.8', '0.0.0.0', '255.255.255.255', '10.0.0.1')
$malas = @('192.168.5', '010.0.0.1', '192.168.5.10 ', ' 192.168.5.10', '192.168.5.256', '1.2.3.4"', '1.2.3.4 name=x',
           '::1', 'fe80::1', '1.2.3.4/24', '', $null, 12345, '0x7f.0.0.1', '1.2.3.4.5', '127.1')
foreach ($b in $buenas) { Esperar ("válida '" + $b + "'") (Test-IPv4Exacta $b) $true }
foreach ($m in $malas) { Esperar ("inválida '" + $m + "'") (Test-IPv4Exacta $m) $false }

Write-Host "== Máscara =="
Esperar 'prefijo 24' (MascaraDe 24) '255.255.255.0'
Esperar 'prefijo 32' (MascaraDe 32) '255.255.255.255'
Esperar 'prefijo 1' (MascaraDe 1) '128.0.0.0'
$tiro = $false; try { MascaraDe 0 | Out-Null } catch { $tiro = $true }
Esperar 'prefijo 0 tira' $tiro $true
$tiro = $false; try { MascaraDe 33 | Out-Null } catch { $tiro = $true }
Esperar 'prefijo 33 tira' $tiro $true

Write-Host "== id del pedido =="
Esperar 'id normal' (Test-IdPedido 'r638945123456789') $true
Esperar 'id con comillas' (Test-IdPedido 'r1"; x') $false
Esperar 'id numérico' (Test-IdPedido 123) $false
Esperar 'id largo' (Test-IdPedido ('r' * 65)) $false

Write-Host "== Pedidos =="
$idx = @(10, 12)
$casos = @(
    @{ n = 'static completo'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"gateway":"192.168.5.1","dns":["8.8.8.8","1.1.1.1"]}'; ok = $true },
    @{ n = 'static sin gateway (vacío) ni dns'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"gateway":"","dns":[]}'; ok = $true },
    @{ n = 'static un solo dns'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"gateway":null,"dns":["8.8.8.8"]}'; ok = $true },
    @{ n = 'static dns null'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24}'; ok = $true },
    @{ n = 'dhcp como lo manda PilotX'; j = '{"id":"r1","ifIndex":12,"mode":"dhcp","ip":null,"prefix":0,"gateway":null,"dns":[]}'; ok = $true },
    @{ n = 'primary como lo manda PilotX'; j = '{"id":"r1","ifIndex":12,"mode":"primary","ip":null,"prefix":0,"gateway":null,"dns":[]}'; ok = $true },
    @{ n = 'ifIndex inexistente'; j = '{"id":"r1","ifIndex":11,"mode":"dhcp"}'; ok = $false },
    @{ n = 'ifIndex texto'; j = '{"id":"r1","ifIndex":"10","mode":"dhcp"}'; ok = $false },
    @{ n = 'ifIndex decimal'; j = '{"id":"r1","ifIndex":10.5,"mode":"dhcp"}'; ok = $false },
    @{ n = 'ifIndex negativo'; j = '{"id":"r1","ifIndex":-10,"mode":"dhcp"}'; ok = $false },
    @{ n = 'sin ifIndex'; j = '{"id":"r1","mode":"dhcp"}'; ok = $false },
    @{ n = 'modo desconocido'; j = '{"id":"r1","ifIndex":10,"mode":"borrar"}'; ok = $false },
    @{ n = 'modo en mayúsculas'; j = '{"id":"r1","ifIndex":10,"mode":"DHCP"}'; ok = $false },
    @{ n = 'modo número'; j = '{"id":"r1","ifIndex":10,"mode":1}'; ok = $false },
    @{ n = 'static IP con comillas (inyección netsh)'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"1.2.3.4\" name=\"Wi-Fi","prefix":24}'; ok = $false },
    @{ n = 'static IP corta'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5","prefix":24}'; ok = $false },
    @{ n = 'static IPv6'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"fe80::1","prefix":24}'; ok = $false },
    @{ n = 'static sin IP'; j = '{"id":"r1","ifIndex":10,"mode":"static","prefix":24}'; ok = $false },
    @{ n = 'static prefijo 0'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":0}'; ok = $false },
    @{ n = 'static prefijo 33'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":33}'; ok = $false },
    @{ n = 'static prefijo texto'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":"24"}'; ok = $false },
    @{ n = 'static gateway malo'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"gateway":"192.168.5.1 x"}'; ok = $false },
    @{ n = 'static dns malo'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"dns":["8.8.8.8","index=1"]}'; ok = $false },
    @{ n = 'static dns no lista'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"dns":"8.8.8.8"}'; ok = $false },
    @{ n = 'static 9 dns'; j = '{"id":"r1","ifIndex":10,"mode":"static","ip":"192.168.5.10","prefix":24,"dns":["1.1.1.1","1.1.1.2","1.1.1.3","1.1.1.4","1.1.1.5","1.1.1.6","1.1.1.7","1.1.1.8","1.1.1.9"]}'; ok = $false },
    @{ n = 'no es objeto'; j = '[1,2]'; ok = $false }
)
foreach ($c in $casos) {
    $v = Test-Pedido (Pedido $c.j) $idx
    Esperar ($c.n + $(if (-not $v.ok) { ' [' + $v.error + ']' } else { '' })) $v.ok $c.ok
}
$v = Test-Pedido (Pedido '{"id":"r1","ifIndex":10,"mode":"static","ip":" 192.168.5.10 ","prefix":24,"gateway":"192.168.5.1","dns":["8.8.8.8"]}') $idx
Esperar 'normaliza: ip recortada' $v.pedido.ip '192.168.5.10'
Esperar 'normaliza: dns como lista' ($v.pedido.dns -join ',') '8.8.8.8'
Esperar 'normaliza: prefijo int' ($v.pedido.prefix -is [int]) $true

Write-Host "== Enlaces y acceso seguro a archivos (en $Carpeta) =="
Initialize-Seguro
$real = Join-Path $Carpeta 'real'
$otra = Join-Path $Carpeta 'otra'
$junction = Join-Path $Carpeta 'junction'
New-Item -ItemType Directory -Path $real, $otra -Force | Out-Null
try {
    cmd /c mklink /J "$junction" "$otra" | Out-Null
    Esperar 'carpeta real no es enlace' (Get-ProblemaEnlace @($Carpeta, $real)) $null
    Esperar 'junction detectado' ([bool](Get-ProblemaEnlace @($Carpeta, $junction))) $true
    Esperar 'ruta inexistente no es enlace' (Get-ProblemaEnlace @((Join-Path $real 'no-existe.json'))) $null

    $h = [PilotXNetApply.Seguro]::FijarCarpeta($real)
    Esperar 'fijar carpeta real' ($null -ne $h) $true
    # Fijada, no se puede renombrar.
    $renombro = $true
    try { Rename-Item -LiteralPath $real -NewName 'real2' -EA Stop } catch { $renombro = $false }
    Esperar 'carpeta fijada no se puede renombrar' $renombro $false
    $h.Dispose()
    $tiro = $false; try { [PilotXNetApply.Seguro]::FijarCarpeta($junction).Dispose() } catch { $tiro = $true }
    Esperar 'fijar junction tira' $tiro $true

    $req = Join-Path $real 'request.json'
    [System.IO.File]::WriteAllText($req, '{"id":"r1"}')
    $b = [PilotXNetApply.Seguro]::LeerYBorrar($req, 65536)
    Esperar 'lee el pedido' ([System.Text.Encoding]::UTF8.GetString($b)) '{"id":"r1"}'
    Esperar 'y lo borra' (Test-Path -LiteralPath $req) $false
    Esperar 'sin pedido devuelve null' ($null -eq [PilotXNetApply.Seguro]::LeerYBorrar($req, 65536)) $true

    # Mientras otro lo escribe (como PilotX con File.WriteAllText): null, no se borra.
    $fs = [System.IO.File]::Open($req, 'Create', 'Write', 'Read')
    Esperar 'pedido a medio escribir: null' ($null -eq [PilotXNetApply.Seguro]::LeerYBorrar($req, 65536)) $true
    $fs.Dispose()
    Esperar 'pedido a medio escribir: sigue ahí' (Test-Path -LiteralPath $req) $true
    Remove-Item -LiteralPath $req -Force

    # Hardlink: se rechaza y el destino no se toca.
    $victima = Join-Path $otra 'victima.txt'
    [System.IO.File]::WriteAllText($victima, 'no tocar')
    New-Item -ItemType HardLink -Path $req -Target $victima | Out-Null
    $tiro = $false; try { [PilotXNetApply.Seguro]::LeerYBorrar($req, 65536) | Out-Null } catch { $tiro = $true }
    Esperar 'pedido hardlink: tira' $tiro $true
    Esperar 'pedido hardlink: víctima intacta' ([System.IO.File]::ReadAllText($victima)) 'no tocar'
    Remove-Item -LiteralPath $req -Force

    # Pedido demasiado grande: tira (y se consume).
    [System.IO.File]::WriteAllText($req, ('x' * 70000))
    $tiro = $false; try { [PilotXNetApply.Seguro]::LeerYBorrar($req, 65536) | Out-Null } catch { $tiro = $true }
    Esperar 'pedido gigante: tira' $tiro $true

    # Escritura atómica.
    $res = Join-Path $real 'result.json'
    [PilotXNetApply.Seguro]::EscribirAtomico($res, [System.Text.Encoding]::UTF8.GetBytes('{"ok":true}'))
    Esperar 'escribe result.json' ([System.IO.File]::ReadAllText($res)) '{"ok":true}'
    [PilotXNetApply.Seguro]::EscribirAtomico($res, [System.Text.Encoding]::UTF8.GetBytes('{"ok":false}'))
    Esperar 'reemplaza result.json' ([System.IO.File]::ReadAllText($res)) '{"ok":false}'
    Esperar 'no quedan temporales' (@(Get-ChildItem -LiteralPath $real -Filter '*.tmp').Count) 0
    Remove-Item -LiteralPath $res -Force
    New-Item -ItemType HardLink -Path $res -Target $victima | Out-Null
    $tiro = $false; try { [PilotXNetApply.Seguro]::EscribirAtomico($res, [System.Text.Encoding]::UTF8.GetBytes('pisado')) } catch { $tiro = $true }
    Esperar 'result.json hardlink: tira' $tiro $true
    Esperar 'result.json hardlink: víctima intacta' ([System.IO.File]::ReadAllText($victima)) 'no tocar'
    Esperar 'result.json hardlink: no quedan temporales' (@(Get-ChildItem -LiteralPath $real -Filter '*.tmp').Count) 0

    # Por un junction: la ruta final no coincide y no se lee nada.
    [System.IO.File]::WriteAllText((Join-Path $otra 'request.json'), '{"id":"r2"}')
    $tiro = $false; try { [PilotXNetApply.Seguro]::LeerYBorrar((Join-Path $junction 'request.json'), 65536) | Out-Null } catch { $tiro = $true }
    Esperar 'pedido a través de junction: tira' $tiro $true
    Esperar 'pedido a través de junction: no se borra el de destino' (Test-Path -LiteralPath (Join-Path $otra 'request.json')) $true
} finally {
    if (Test-Path -LiteralPath $junction) { cmd /c rmdir "$junction" | Out-Null }
    Remove-Item -LiteralPath $Carpeta -Recurse -Force -EA SilentlyContinue
}

Write-Host ""
Write-Host ("{0} casos, {1} fallas" -f $script:total, $script:fallas)
exit $(if ($script:fallas -eq 0) { 0 } else { 1 })
