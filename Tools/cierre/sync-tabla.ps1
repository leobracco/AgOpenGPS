# ============================================================================
# sync-tabla.ps1 - el commit cierra iconos en el tablero del 15/8.
#
# Se llama desde el hook post-commit. Lee el mensaje del commit, saca que
# iconos declara y los marca en LA base (una sola, la del server en :6002).
#
# Sintaxis en el mensaje del commit, una linea propia:
#
#   Cierra: ABDraw, Boundary, btnAutoSteer     -> quedan "anda"
#   Prueba: TramLines                          -> quedan "en prueba"
#   Roto:   YouTurnU                           -> quedan "roto"
#   Fuera:  bing1                              -> "fuera de alcance"
#
# Se puede escribir el nombre del icono (ABDraw) o el del control (btnABDraw):
# el server resuelve los dos. Varios por linea, separados por coma.
#
# Va por HTTP y no escribiendo el archivo para que ande igual desde la maquina
# de Santi: la base vive en una sola parte y todos escriben ahi.
#   CIERRE_URL   url del tablero (default http://localhost:6002)
#
# ASCII PURO a proposito: PowerShell 5.1 lee un .ps1 sin BOM con el codepage
# ANSI y corrompe los acentos, y esto corre dentro de un hook donde nadie ve el
# error.
#
# NUNCA falla el commit: si el server no esta, avisa y sale con 0. El commit ya
# esta hecho; romper aca solo confundiria.
# ============================================================================

$ErrorActionPreference = 'Stop'

function Salir($msg) {
  if ($msg) { Write-Host "[cierre] $msg" }
  exit 0
}

try {
  $url = $env:CIERRE_URL
  if (-not $url) { $url = 'http://localhost:6002' }
  $url = $url.TrimEnd('/')

  $mensaje = (git log -1 --format=%B)
  if (-not $mensaje) { Salir 'sin mensaje de commit' }

  # Mapa palabra clave -> estado del tablero
  $mapa = @{
    'cierra' = 'anda'; 'cierro' = 'anda'; 'closes' = 'anda'
    'prueba' = 'en_prueba'; 'probando' = 'en_prueba'
    'roto'   = 'roto'
    'fuera'  = 'fuera'
  }

  # Se junta por estado: un commit puede cerrar unos y marcar otros en prueba.
  $porEstado = @{}
  foreach ($linea in ($mensaje -split "`r?`n")) {
    # Las lineas con "->" son documentacion, no declaraciones. Sin esto, el
    # commit que documenta la sintaxis se marca a si mismo: paso en el primer
    # commit del hook, que cerro ABDraw y Boundary porque estaban en los
    # ejemplos del propio mensaje.
    if ($linea -match '->') { continue }

    $m = [regex]::Match($linea, '^\s*(cierra|cierro|closes|prueba|probando|roto|fuera)\s*:\s*(.+)$',
                        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) { continue }
    $estado = $mapa[$m.Groups[1].Value.ToLowerInvariant()]
    $nombres = @($m.Groups[2].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (-not $nombres) { continue }
    if (-not $porEstado.ContainsKey($estado)) { $porEstado[$estado] = @() }
    $porEstado[$estado] += $nombres
  }

  if ($porEstado.Count -eq 0) { exit 0 }   # commit normal, sin declaraciones

  $hash   = (git log -1 --format=%h).Trim()
  $fecha  = (git log -1 --format=%ad --date=short).Trim()
  $autor  = (git log -1 --format=%an).Trim()
  $asunto = (git log -1 --format=%s).Trim()

  foreach ($estado in $porEstado.Keys) {
    $cuerpo = @{
      nombres = @($porEstado[$estado] | Select-Object -Unique)
      estado  = $estado
      commit  = $hash; fecha = $fecha; autor = $autor; asunto = $asunto
    } | ConvertTo-Json -Compress

    try {
      $bytes = [Text.Encoding]::UTF8.GetBytes($cuerpo)
      $req = [Net.WebRequest]::Create("$url/api/marcar")
      $req.Method = 'POST'; $req.ContentType = 'application/json; charset=utf-8'
      $req.Timeout = 4000; $req.ContentLength = $bytes.Length
      $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
      $rta = (New-Object IO.StreamReader($req.GetResponse().GetResponseStream())).ReadToEnd()
      $r = $rta | ConvertFrom-Json

      $ap = @($r.aplicados).Count
      $msg = "[cierre] $hash -> $estado : $ap item(s)"
      if ($r.no_encontrados -and @($r.no_encontrados).Count) {
        $msg += "  SIN ENCONTRAR: " + (@($r.no_encontrados) -join ', ')
      }
      Write-Host $msg
    }
    catch {
      Write-Host "[cierre] tablero no disponible en $url ($($_.Exception.Message)); nada se perdio, marcalo a mano"
    }
  }
  exit 0
}
catch {
  Write-Host "[cierre] error: $($_.Exception.Message)"
  exit 0
}
