param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)

$ErrorActionPreference = "Stop"

$webIconDir = Join-Path $RepoRoot "SourceCode\AgroParallel\Web\AgroParallel.WebUI\wwwroot\img\icons"
$outDir = Join-Path $webIconDir "existing"
$gpsIcons = Join-Path $RepoRoot "SourceCode\GPS\btnImages"
$pilotVariants = Join-Path $gpsIcons "PilotXVariants"
$pilotX = Join-Path $RepoRoot "SourceCode\GPS\btnImages_pilotx"
$agioIcons = Join-Path $RepoRoot "SourceCode\AgIO\Source\btnImages"

if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

$items = @(
    @{ id = "hub"; label = "Hub"; src = "$pilotVariants\fileMenu_day.png" },
    @{ id = "herramienta"; label = "Herramienta"; src = "$pilotVariants\ToolAcceptChange_day.png" },
    @{ id = "implemento"; label = "Implemento"; src = "$pilotVariants\SectionMapping_day.png" },
    @{ id = "quantix"; label = "QuantiX"; src = "$pilotVariants\FieldStats_day.png" },
    @{ id = "vistax"; label = "VistaX"; src = "$pilotX\Camera2D64.png" },
    @{ id = "vistax-live"; label = "VistaX live"; src = "$pilotX\CameraNorth2D.png" },
    @{ id = "vistax-stats"; label = "VistaX stats"; src = "$pilotVariants\FieldStats_day.png" },
    @{ id = "flowx"; label = "FlowX"; src = "$pilotVariants\TrackOn_day.png" },
    @{ id = "sectionx"; label = "SectionX"; src = "$pilotVariants\SectionMasterOn_day.png" },
    @{ id = "linex"; label = "LineX"; src = "$pilotVariants\TrackLine_day.png" },
    @{ id = "stormx"; label = "StormX"; src = "$pilotVariants\Warning_day.png" },
    @{ id = "corex-ecu"; label = "CoreX ECU"; src = "$pilotVariants\AgIO_day.png" },
    @{ id = "insumos"; label = "Insumos"; src = "$pilotVariants\FlagGrn_day.png" },
    @{ id = "mapas"; label = "Mapas"; src = "$pilotVariants\FieldTools_day.png" },
    @{ id = "prescripciones"; label = "Prescripciones"; src = "$pilotVariants\FileOpen_day.png" },
    @{ id = "orbitx"; label = "OrbitX"; src = "$pilotVariants\SaveToCloud_day.png" },
    @{ id = "firmwares"; label = "Firmwares"; src = "$gpsIcons\DownloadAndUse.png" },
    @{ id = "camaras"; label = "Camaras"; src = "$pilotX\Camera2D64.png" },
    @{ id = "camaras-widget"; label = "Widget camaras"; src = "$pilotX\Camera3D64.png" },
    @{ id = "nodos"; label = "Nodos"; src = "$pilotVariants\AgIO_day.png" },
    @{ id = "nodo-detalle"; label = "Detalle nodo"; src = "$pilotVariants\Info_day.png" },
    @{ id = "setup"; label = "Asistente"; src = "$pilotVariants\Settings48_day.png" },
    @{ id = "sistema"; label = "Sistema"; src = "$pilotVariants\NavigationSettings_day.png" },
    @{ id = "debug"; label = "Debug"; src = "$pilotVariants\Warning_day.png" },
    @{ id = "pwa-qr"; label = "QR celular"; src = "$agioIcons\www.png" },
    @{ id = "conectar-celular"; label = "Conectar celular"; src = "$agioIcons\RadioSettings.png" },
    @{ id = "actualizar"; label = "Actualizar"; src = "$gpsIcons\DownloadAll.png" },
    @{ id = "piloto"; label = "Piloto"; src = "$pilotVariants\AutoSteerOn_day.png" },
    @{ id = "vehiculo"; label = "Vehiculo"; src = "$gpsIcons\vehiclePageTractor.png" },
    @{ id = "lote"; label = "Lote"; src = "$pilotVariants\Boundary_day.png" },
    @{ id = "datos-gps"; label = "Datos GPS"; src = "$pilotVariants\GPSQuality_day.png" },
    @{ id = "datos-lote"; label = "Datos lote"; src = "$pilotVariants\BoundaryOuter_day.png" },
    @{ id = "cabina-alarmas"; label = "Cabina alarmas"; src = "$pilotVariants\Con_SourcesRTKAlarm_day.png" },
    @{ id = "widget-quantix"; label = "Widget QuantiX"; src = "$pilotVariants\FieldStats_active.png" },
    @{ id = "cobertura"; label = "Cobertura"; src = "$pilotVariants\SectionMapping_day.png" },
    @{ id = "gps"; label = "GPS"; src = "$pilotVariants\GPSQuality_day.png" },
    @{ id = "alarma"; label = "Alarma"; src = "$pilotVariants\Con_SourcesRTKAlarm_day.png" }
)

$manifest = @()
foreach ($item in $items) {
    if (-not (Test-Path -LiteralPath $item.src)) {
        throw "Missing source icon for $($item.id): $($item.src)"
    }

    $destName = "agp-$($item.id).png"
    $destPath = Join-Path $outDir $destName
    Copy-Item -LiteralPath $item.src -Destination $destPath -Force

    $manifest += [ordered]@{
        id = $item.id
        label = $item.label
        file = "existing/$destName"
        source = ($item.src.Substring($RepoRoot.Length + 1)).Replace("\", "/")
    }
}

$json = $manifest | ConvertTo-Json -Depth 3
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $webIconDir "agp-icons.json"), $json, $utf8NoBom)

$js = "window.AGP_ICONS = " + $json + ";"
[System.IO.File]::WriteAllText((Join-Path $webIconDir "agp-icons.js"), $js, $utf8NoBom)

Write-Host "Applied $($manifest.Count) existing platform icons to $outDir"
