# ============================================================================
#  sincronizar-kb-bot.ps1 — sube la base de conocimiento del bot de soporte.
#
#  Por que existe:
#    El bot del cloud (OrbitX services/soporte-bot.js) le contesta al operario
#    con lo que dice kb-pilotx.md. Hasta el 2026-09-19 ese archivo vivia SOLO en
#    el droplet: sin historia, sin respaldo, y sin forma de saber si estaba al
#    dia. Ahora la fuente de verdad es docs/bot/kb-pilotx.md en el repo y esto
#    la publica.
#
#  La direccion importa: repo -> droplet. NUNCA al reves. Si alguien edita el
#  archivo directo en el servidor, ese cambio se pierde en el proximo sync, y
#  esta bien que sea asi: lo que no esta en el repo no existe.
#
#  Que NO hace:
#    No reinicia el servicio. soporte-bot.js lee la KB una sola vez al cargar el
#    modulo, asi que el cambio recien se ve cuando OrbitX se reinicia. El script
#    lo avisa al final en vez de reiniciar por su cuenta: reiniciar OrbitX saca
#    de linea el panel y el sync de todos los tractores, y eso lo decide una
#    persona.
#
#  Uso:
#    .\Tools\sincronizar-kb-bot.ps1            sube si cambio
#    .\Tools\sincronizar-kb-bot.ps1 -Verificar solo compara, no sube
# ============================================================================

param(
    # Compara la KB local contra la del servidor y informa, sin subir nada.
    [switch]$Verificar
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
$kbLocal = Join-Path $raiz "docs\bot\kb-pilotx.md"
$kbRemota = "/opt/AgroParallel/OrbitX/services/kb-pilotx.md"

if (-not (Test-Path $kbLocal)) {
    throw "Falta $kbLocal. Es la fuente de verdad de lo que el bot le contesta al operario."
}

$hashLocal = (Get-FileHash $kbLocal -Algorithm SHA256).Hash.ToLower()
$lineas = (Get-Content $kbLocal | Measure-Object -Line).Lines
Write-Host "KB local : $lineas lineas, sha $($hashLocal.Substring(0,12))" -ForegroundColor Cyan

$hashRemoto = (ssh do "sha256sum $kbRemota 2>/dev/null | cut -d' ' -f1").Trim()
if ([string]::IsNullOrWhiteSpace($hashRemoto)) {
    Write-Host "KB remota: NO EXISTE todavia" -ForegroundColor Yellow
} else {
    Write-Host "KB remota: sha $($hashRemoto.Substring(0,12))" -ForegroundColor Cyan
}

if ($hashLocal -eq $hashRemoto) {
    Write-Host "`nIguales: no hay nada que subir." -ForegroundColor Green
    exit 0
}

if ($Verificar) {
    Write-Host "`nDISTINTAS. Corre sin -Verificar para publicar la del repo." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nSubiendo la KB del repo al bot..." -ForegroundColor Cyan
scp $kbLocal "do:/tmp/kb-pilotx.md"
# Respaldo de la que estaba: si alguien habia editado en el servidor, no se
# pierde sin dejar rastro.
ssh do "if [ -f $kbRemota ]; then cp $kbRemota ${kbRemota}.previa; fi; mv /tmp/kb-pilotx.md $kbRemota"

$verif = (ssh do "sha256sum $kbRemota | cut -d' ' -f1").Trim()
if ($verif -ne $hashLocal) {
    throw "El sha del servidor no coincide con el local. Reintentar."
}

Write-Host "`nOK: la KB del bot quedo igual a la del repo." -ForegroundColor Green
Write-Host "OJO: soporte-bot.js la lee al cargar el modulo. El bot sigue" -ForegroundColor Yellow
Write-Host "     contestando con la vieja hasta que se reinicie OrbitX:" -ForegroundColor Yellow
Write-Host "       ssh do 'pm2 restart OrbitX'" -ForegroundColor Yellow
