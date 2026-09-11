param(
    [string]$OutDir = "$PSScriptRoot\..\SourceCode\AgroParallel\Web\AgroParallel.WebUI\wwwroot\img\icons"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}

$icons = [ordered]@{
    "agp-hub.svg" = @'
<rect x="3" y="3" width="7" height="8" rx="1.5"/>
<rect x="14" y="3" width="7" height="5" rx="1.5"/>
<rect x="14" y="12" width="7" height="9" rx="1.5"/>
<rect x="3" y="15" width="7" height="6" rx="1.5"/>
'@
    "agp-herramienta.svg" = @'
<path d="M14.7 6.3a4.2 4.2 0 0 0-5 5L4.2 16.8a2.1 2.1 0 0 0 3 3l5.5-5.5a4.2 4.2 0 0 0 5-5l-2.6 2.6-3-3 2.6-2.6Z"/>
<path class="accent" d="M6 18h.01"/>
'@
    "agp-implemento.svg" = @'
<path d="M4 17h16"/>
<path d="M6 17 8.5 8h7L18 17"/>
<path d="M9 8V5h6v3"/>
<path class="accent" d="M8.5 12h7"/>
<circle cx="7" cy="19" r="1.5"/>
<circle cx="17" cy="19" r="1.5"/>
'@
    "agp-quantix.svg" = @'
<path d="M4 6h16"/>
<path d="M7 6v12"/>
<path d="M12 6v12"/>
<path d="M17 6v12"/>
<path class="accent" d="M5 18h14"/>
<path class="accent" d="M9 10h6"/>
'@
    "agp-vistax.svg" = @'
<path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"/>
<circle class="accent" cx="12" cy="12" r="3"/>
'@
    "agp-vistax-live.svg" = @'
<path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"/>
<circle cx="12" cy="12" r="2.6"/>
<path class="accent" d="M18.8 5.2c1 .8 1.7 1.9 2 3.2"/>
<path class="accent" d="M5.2 5.2c-1 .8-1.7 1.9-2 3.2"/>
'@
    "agp-vistax-stats.svg" = @'
<path d="M4 19V5"/>
<path d="M4 19h16"/>
<path class="accent" d="M8 15v-4"/>
<path class="accent" d="M12 15V8"/>
<path class="accent" d="M16 15v-6"/>
<path d="m8 8 3-3 3 2 4-4"/>
'@
    "agp-flowx.svg" = @'
<path d="M4 6h16"/>
<path d="M4 12h16"/>
<path d="M4 18h16"/>
<path class="accent" d="m16 9 3 3-3 3"/>
<path class="accent" d="M5 12h14"/>
'@
    "agp-sectionx.svg" = @'
<rect x="3" y="7" width="18" height="10" rx="2"/>
<path d="M8 7v10"/>
<path d="M12 7v10"/>
<path d="M16 7v10"/>
<path class="accent" d="M8 17h8"/>
'@
    "agp-linex.svg" = @'
<path d="M5 19 19 5"/>
<path class="accent" d="M5 5h.01"/>
<path class="accent" d="M19 19h.01"/>
<path d="M9 19h10V9"/>
'@
    "agp-stormx.svg" = @'
<path d="M17.5 16.5A4.5 4.5 0 0 0 16 8a5.8 5.8 0 0 0-11 2.5A3.5 3.5 0 0 0 5.5 17H9"/>
<path class="accent" d="m13 11-3 6h4l-2 4"/>
'@
    "agp-corex-ecu.svg" = @'
<rect x="6" y="6" width="12" height="12" rx="2"/>
<path d="M9 9h6v6H9z"/>
<path class="accent" d="M9 2v3"/>
<path class="accent" d="M15 2v3"/>
<path class="accent" d="M9 19v3"/>
<path class="accent" d="M15 19v3"/>
<path d="M2 9h3"/>
<path d="M2 15h3"/>
<path d="M19 9h3"/>
<path d="M19 15h3"/>
'@
    "agp-insumos.svg" = @'
<path d="M12 21V10"/>
<path class="accent" d="M12 10c-3.5 0-6-2.5-6-6 3.5 0 6 2.5 6 6Z"/>
<path class="accent" d="M12 13c3.5 0 6-2.5 6-6-3.5 0-6 2.5-6 6Z"/>
<path d="M6 21h12"/>
'@
    "agp-mapas.svg" = @'
<path d="m3 6 6-3 6 3 6-3v15l-6 3-6-3-6 3V6Z"/>
<path d="M9 3v15"/>
<path d="M15 6v15"/>
<path class="accent" d="m6 13 3-2 3 2 3-2 3 2"/>
'@
    "agp-prescripciones.svg" = @'
<path d="M7 3h7l4 4v14H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z"/>
<path d="M14 3v5h5"/>
<path class="accent" d="M8 13h8"/>
<path class="accent" d="M8 17h5"/>
'@
    "agp-orbitx.svg" = @'
<circle cx="12" cy="12" r="3"/>
<path d="M3 12c2-5 5-7.5 9-7.5s7 2.5 9 7.5c-2 5-5 7.5-9 7.5S5 17 3 12Z"/>
<path class="accent" d="M12 2v2"/>
<path class="accent" d="M12 20v2"/>
'@
    "agp-firmwares.svg" = @'
<path d="M12 3v12"/>
<path class="accent" d="m7 10 5 5 5-5"/>
<path d="M5 19h14"/>
<path d="M8 21h8"/>
'@
    "agp-camaras.svg" = @'
<path d="M14.5 5 13 3H9L7.5 5H5a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4.5Z"/>
<circle class="accent" cx="12" cy="12" r="3"/>
'@
    "agp-camaras-widget.svg" = @'
<rect x="3" y="5" width="14" height="11" rx="2"/>
<path d="m17 9 4-2.5v9L17 13"/>
<path class="accent" d="M7 19h10"/>
<path class="accent" d="M9 16v3"/>
'@
    "agp-nodos.svg" = @'
<circle cx="12" cy="12" r="3"/>
<circle class="accent" cx="5" cy="5" r="2"/>
<circle class="accent" cx="19" cy="5" r="2"/>
<circle class="accent" cx="5" cy="19" r="2"/>
<circle class="accent" cx="19" cy="19" r="2"/>
<path d="m7 7 3 3"/>
<path d="m17 7-3 3"/>
<path d="m7 17 3-3"/>
<path d="m17 17-3-3"/>
'@
    "agp-nodo-detalle.svg" = @'
<circle class="accent" cx="12" cy="12" r="4"/>
<path d="M12 2v4"/>
<path d="M12 18v4"/>
<path d="M2 12h4"/>
<path d="M18 12h4"/>
<path d="m4.9 4.9 2.8 2.8"/>
<path d="m16.3 16.3 2.8 2.8"/>
<path d="m19.1 4.9-2.8 2.8"/>
<path d="m7.7 16.3-2.8 2.8"/>
'@
    "agp-setup.svg" = @'
<circle cx="12" cy="12" r="3"/>
<path d="M19.4 15a1.8 1.8 0 0 0 .4 2l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.8 1.8 0 0 0-2-.4 1.8 1.8 0 0 0-1 1.6V21a2 2 0 1 1-4 0v-.1a1.8 1.8 0 0 0-1-1.6 1.8 1.8 0 0 0-2 .4l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.8 1.8 0 0 0 .4-2 1.8 1.8 0 0 0-1.6-1H3a2 2 0 1 1 0-4h.1a1.8 1.8 0 0 0 1.6-1 1.8 1.8 0 0 0-.4-2l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.8 1.8 0 0 0 2 .4 1.8 1.8 0 0 0 1-1.6V3a2 2 0 1 1 4 0v.1a1.8 1.8 0 0 0 1 1.6 1.8 1.8 0 0 0 2-.4l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.8 1.8 0 0 0-.4 2 1.8 1.8 0 0 0 1.6 1H21a2 2 0 1 1 0 4h-.1a1.8 1.8 0 0 0-1.5 1Z"/>
'@
    "agp-sistema.svg" = @'
<rect x="3" y="4" width="18" height="12" rx="2"/>
<path d="M8 20h8"/>
<path d="M12 16v4"/>
<path class="accent" d="M7 8h4"/>
<path class="accent" d="M7 11h7"/>
'@
    "agp-debug.svg" = @'
<path d="M8 2h8"/>
<path d="M9 2v3"/>
<path d="M15 2v3"/>
<rect x="6" y="5" width="12" height="15" rx="5"/>
<path class="accent" d="M3 10h3"/>
<path class="accent" d="M18 10h3"/>
<path class="accent" d="M3 15h3"/>
<path class="accent" d="M18 15h3"/>
<path d="M10 9h.01"/>
<path d="M14 9h.01"/>
'@
    "agp-pwa-qr.svg" = @'
<rect x="7" y="2.5" width="10" height="19" rx="2"/>
<path d="M11 18h2"/>
<path class="accent" d="M9 6h2v2H9z"/>
<path class="accent" d="M13 6h2v2h-2z"/>
<path class="accent" d="M9 10h2v2H9z"/>
<path class="accent" d="M13 10h2"/>
<path class="accent" d="M13 12h2"/>
'@
    "agp-conectar-celular.svg" = @'
<rect x="7" y="2.5" width="10" height="19" rx="2"/>
<path d="M11 18h2"/>
<path class="accent" d="M4 8a8 8 0 0 1 16 0"/>
<path class="accent" d="M6.8 10a5.2 5.2 0 0 1 10.4 0"/>
'@
    "agp-actualizar.svg" = @'
<path d="M21 12a9 9 0 0 1-15.4 6.4L3 16"/>
<path d="M3 16h5v5"/>
<path class="accent" d="M3 12A9 9 0 0 1 18.4 5.6L21 8"/>
<path class="accent" d="M21 8h-5V3"/>
'@
    "agp-piloto.svg" = @'
<circle cx="12" cy="12" r="9"/>
<circle cx="12" cy="12" r="3"/>
<path class="accent" d="M12 3v6"/>
<path d="M6.5 17.5 9.9 14"/>
<path d="m17.5 17.5-3.4-3.4"/>
'@
    "agp-vehiculo.svg" = @'
<path d="M5 13 7 7h10l2 6"/>
<rect x="4" y="12" width="16" height="6" rx="2"/>
<path class="accent" d="M7 18v2"/>
<path class="accent" d="M17 18v2"/>
<circle cx="8" cy="15" r="1"/>
<circle cx="16" cy="15" r="1"/>
'@
    "agp-lote.svg" = @'
<path d="M4 19V5"/>
<path d="M4 5h14l-2 4 2 4H4"/>
<path class="accent" d="M8 17h12"/>
'@
    "agp-datos-gps.svg" = @'
<path d="M12 21s7-5.2 7-12A7 7 0 0 0 5 9c0 6.8 7 12 7 12Z"/>
<circle cx="12" cy="9" r="2.5"/>
<path class="accent" d="M8 3.7a10 10 0 0 1 8 0"/>
'@
    "agp-datos-lote.svg" = @'
<path d="M4 19V5"/>
<path d="M4 5h14l-2 4 2 4H4"/>
<path class="accent" d="M7 17h4"/>
<path class="accent" d="M13 17h4"/>
<path class="accent" d="M19 17h1"/>
'@
    "agp-cabina-alarmas.svg" = @'
<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9Z"/>
<path d="M10 21h4"/>
<path class="accent" d="M12 6v5"/>
<path class="accent" d="M12 14h.01"/>
'@
    "agp-widget-quantix.svg" = @'
<rect x="3" y="4" width="18" height="16" rx="2"/>
<path d="M7 8v8"/>
<path d="M12 8v8"/>
<path d="M17 8v8"/>
<path class="accent" d="M6 16h12"/>
'@
    "agp-cobertura.svg" = @'
<path d="M4 19V5"/>
<path d="M4 19h16"/>
<path class="accent" d="M7 16V9"/>
<path class="accent" d="M11 16V6"/>
<path class="accent" d="M15 16v-4"/>
<path class="accent" d="M19 16v-2"/>
'@
    "agp-gps.svg" = @'
<path d="M12 21s7-5.2 7-12A7 7 0 0 0 5 9c0 6.8 7 12 7 12Z"/>
<circle cx="12" cy="9" r="2.5"/>
<path class="accent" d="M3 4.5A13 13 0 0 1 21 4.5"/>
'@
    "agp-alarma.svg" = @'
<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9Z"/>
<path d="M10 21h4"/>
<path class="accent" d="M12 6v6"/>
<path class="accent" d="M12 15h.01"/>
'@
}

$template = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" aria-hidden="true">
  <style>
    path, rect, circle, line, polyline, polygon { stroke: #101612; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
    .accent { stroke: #4ABA3E; }
  </style>
__BODY__
</svg>
'@

foreach ($entry in $icons.GetEnumerator()) {
    $path = Join-Path $OutDir $entry.Key
    $svg = $template.Replace("__BODY__", $entry.Value.Trim())
    Set-Content -LiteralPath $path -Value $svg -Encoding UTF8
}

Write-Host "Generated $($icons.Count) WebUI icons in $OutDir"
