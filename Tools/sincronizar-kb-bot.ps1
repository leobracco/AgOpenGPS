# ============================================================================
#  sincronizar-kb-bot.ps1 — publica al cloud lo que usa el bot de soporte:
#  su base de conocimiento y su propio modulo.
#
#  Por que existe:
#    El bot (OrbitX services/soporte-bot.js) le contesta al operario con lo que
#    dice kb-pilotx.md. Hasta el 2026-09-19 los DOS archivos vivian SOLO en el
#    droplet: sin historia, sin respaldo, y sin forma de saber si estaban al
#    dia. Ahora la fuente de verdad es docs/bot/ en el repo y esto los publica.
#
#  La direccion importa: repo -> droplet. NUNCA al reves. Si alguien edita
#  directo en el servidor, ese cambio se pierde en el proximo sync (queda un
#  .previa), y esta bien que sea asi: lo que no esta en el repo no existe.
#
#  Que NO hace:
#    No reinicia OrbitX. El modulo y la KB se cargan al arrancar, asi que el
#    cambio recien se ve despues del reinicio. Reiniciar saca de linea el panel
#    y el sync de TODOS los tractores: eso lo decide una persona, no un script.
#
#  Uso:
#    .\Tools\sincronizar-kb-bot.ps1             publica lo que cambio
#    .\Tools\sincronizar-kb-bot.ps1 -Verificar  solo compara, no sube nada
# ============================================================================

param(
    # Compara local contra servidor e informa, sin publicar.
    [switch]$Verificar
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot

# Pares local -> remoto.
$archivos = @(
    @{ Local = "docs\bot\kb-pilotx.md";   Remoto = "/opt/AgroParallel/OrbitX/services/kb-pilotx.md";   Nombre = "KB " },
    @{ Local = "docs\bot\soporte-bot.js"; Remoto = "/opt/AgroParallel/OrbitX/services/soporte-bot.js"; Nombre = "bot" }
)

$huboCambios = $false

foreach ($a in $archivos) {
    $local = Join-Path $raiz $a.Local
    if (-not (Test-Path $local)) { throw "Falta $local" }

    $hashLocal = (Get-FileHash $local -Algorithm SHA256).Hash.ToLower()
    $hashRemoto = (ssh do "sha256sum $($a.Remoto) 2>/dev/null | cut -d' ' -f1").Trim()

    if ($hashLocal -eq $hashRemoto) {
        Write-Host "$($a.Nombre): al dia" -ForegroundColor DarkGray
        continue
    }

    $huboCambios = $true
    Write-Host "$($a.Nombre): DISTINTO (local $($hashLocal.Substring(0,12)))" -ForegroundColor Yellow
    if ($Verificar) { continue }

    # El bot es codigo: si sube roto, el chat de soporte deja de contestar.
    if ($a.Local -like "*.js") {
        node --check $local
        if ($LASTEXITCODE -ne 0) { throw "$($a.Local) no pasa node --check. No se sube nada." }
    }

    $tmp = "/tmp/" + (Split-Path $a.Remoto -Leaf)
    scp $local "do:$tmp"
    # Respaldo de lo que estaba: si alguien habia editado en el servidor, no se
    # pierde sin dejar rastro.
    ssh do "if [ -f $($a.Remoto) ]; then cp $($a.Remoto) $($a.Remoto).previa; fi; mv $tmp $($a.Remoto)"

    $verif = (ssh do "sha256sum $($a.Remoto) | cut -d' ' -f1").Trim()
    if ($verif -ne $hashLocal) { throw "El sha del servidor no coincide con el local. Reintentar." }
    Write-Host "$($a.Nombre): publicado" -ForegroundColor Green
}

if (-not $huboCambios) {
    Write-Host "`nTodo al dia: no hay nada que publicar." -ForegroundColor Green
    exit 0
}

if ($Verificar) {
    Write-Host "`nHay cambios sin publicar. Corre sin -Verificar." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nOJO: OrbitX carga el modulo y la KB al arrancar. Sigue" -ForegroundColor Yellow
Write-Host "     contestando con lo viejo hasta que se reinicie:" -ForegroundColor Yellow
Write-Host "       ssh do 'pm2 restart OrbitX'" -ForegroundColor Yellow
