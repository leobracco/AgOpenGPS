# ============================================================================
# instalar-hook.ps1 - engancha sync-tabla.ps1 al post-commit de este clon.
#
# Se corre UNA VEZ por maquina. Los hooks de git no viajan en el repo (viven en
# .git/hooks, que no se versiona), asi que cada uno tiene que instalarlo en su
# clon. El script que hace el trabajo si esta versionado, aca al lado.
#
#   powershell -ExecutionPolicy Bypass -File Tools\cierre\instalar-hook.ps1
#
# Si el tablero no corre en esta maquina, apuntalo por variable de entorno:
#   setx CIERRE_URL http://192.168.1.12:6002
# ============================================================================

$ErrorActionPreference = 'Stop'

$repo = (git rev-parse --show-toplevel 2>$null)
if (-not $repo) { Write-Host 'No estoy dentro de un repo git.'; exit 1 }
$repo = $repo -replace '/', '\'

$hooks = Join-Path $repo '.git\hooks'
if (-not (Test-Path $hooks)) { New-Item -ItemType Directory -Path $hooks | Out-Null }

$destino = Join-Path $hooks 'post-commit'

# El hook es sh (lo que ejecuta git en Windows via Git Bash) y desde ahi se
# llama a PowerShell. -NoProfile para que no lo afecte el perfil del usuario y
# -ExecutionPolicy Bypass para que no dependa de la politica de la maquina.
$sh = @'
#!/bin/sh
# Actualiza el tablero de cierre del 15/8. Generado por Tools/cierre/instalar-hook.ps1
# Nunca frena el commit: el script sale siempre con 0.
powershell -NoProfile -ExecutionPolicy Bypass -File "$(git rev-parse --show-toplevel)/Tools/cierre/sync-tabla.ps1" || true
'@

[IO.File]::WriteAllText($destino, ($sh -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))

Write-Host "Hook instalado en $destino"
Write-Host ''
Write-Host 'Como se usa: en el mensaje del commit, una linea propia:'
Write-Host '  Cierra: ABDraw, Boundary, btnAutoSteer'
Write-Host '  Prueba: TramLines'
Write-Host '  Roto:   YouTurnU'
Write-Host '  Fuera:  bing1'
Write-Host ''
$url = $env:CIERRE_URL; if (-not $url) { $url = 'http://localhost:6002 (default)' }
Write-Host "Tablero: $url"
