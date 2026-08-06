# modsim-a-loopback.ps1 — deja el ModSim de ESTA máquina en subred 127.255.255
# (modo loopback: su tráfico no sale de la PC; cada banco queda aislado).
#
# Uso: abrir Build\ModSim.exe y correr este script UNA vez. ModSim muestra
# "IP Set / New Values Changed", pide reiniciar y queda persistido. Listo:
# un ModSim por PC sin pisarse entre pantallas.
#
# Cómo funciona: manda el PGN 201 (set subnet, el mismo que usa AgIO nativo)
# con 127.255.255 a los caminos posibles del :8888 local. ModSim adopta la
# subred, la guarda en su config y se relanza solo. El engine de PilotX no
# necesita nada: aprende solo que el sim vive en loopback (2026-08-06).

$pgn = [byte[]](0x80, 0x81, 0x7F, 201, 5, 201, 201, 127, 255, 255, 0)
$ck = 0
for ($i = 2; $i -lt $pgn.Length - 1; $i++) { $ck += $pgn[$i] }
$pgn[$pgn.Length - 1] = [byte]($ck -band 0xFF)

$u = New-Object System.Net.Sockets.UdpClient
$u.EnableBroadcast = $true
foreach ($dest in @("127.255.255.255", "127.0.0.1")) {
    $u.Send($pgn, $pgn.Length, $dest, 8888) | Out-Null
}
$u.Close()

Write-Host "PGN 201 mandado: mirá ModSim — acepta el cartel y se reinicia con subred 127.255.255." -ForegroundColor Green
