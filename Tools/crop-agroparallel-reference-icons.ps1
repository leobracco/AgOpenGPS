param(
    [string]$Root = (Resolve-Path "$PSScriptRoot\..").Path,
    [string]$Source = "G:\AgroParallel\Marketing\d62f4b17-66ff-45b8-985f-d0ca012651ff.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) {
    throw "No existe la imagen fuente: $Source"
}

$src = [System.Drawing.Bitmap]::new($Source)

function Convert-PixelForMode([System.Drawing.Color]$p, [string]$Mode) {
    if ($p.A -eq 0) { return [System.Drawing.Color]::Transparent }

    $isGreen = ($p.G -gt $p.R + 25) -and ($p.G -gt $p.B + 25)
    if ($Mode -eq "night") {
        if ($isGreen) {
            return [System.Drawing.Color]::FromArgb($p.A, 0x7C, 0xFF, 0x5B)
        }

        $max = [Math]::Max($p.R, [Math]::Max($p.G, $p.B))
        if ($max -lt 150) {
            return [System.Drawing.Color]::FromArgb($p.A, 0xEA, 0xF2, 0xE8)
        }
    }

    return $p
}

function Save-IconVariant([System.Drawing.Bitmap]$Symbol, [string]$OriginalPath, [string]$Name, [System.Drawing.Color]$Bg, [System.Drawing.Color]$Border, [string]$Mode) {
    $variantDir = Join-Path (Split-Path -Parent $OriginalPath) "PilotXVariants"
    if (-not (Test-Path $variantDir)) {
        New-Item -ItemType Directory -Path $variantDir | Out-Null
    }

    $base = [System.IO.Path]::GetFileNameWithoutExtension($OriginalPath)
    $path = Join-Path $variantDir ("{0}_{1}.png" -f $base, $Name)

    $out = [System.Drawing.Bitmap]::new($Symbol.Width, $Symbol.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear($Bg)
    if ($Symbol.Width -gt 28 -and $Symbol.Height -gt 28) {
        $pen = [System.Drawing.Pen]::new($Border, 1)
        $g.DrawRectangle($pen, 0, 0, $Symbol.Width - 1, $Symbol.Height - 1)
        $pen.Dispose()
    }
    $g.Dispose()

    for ($x = 0; $x -lt $Symbol.Width; $x++) {
        for ($y = 0; $y -lt $Symbol.Height; $y++) {
            $p = $Symbol.GetPixel($x, $y)
            if ($p.A -gt 0) {
                $out.SetPixel($x, $y, (Convert-PixelForMode $p $Mode))
            }
        }
    }

    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
}

function Copy-ReferenceIcon([int]$Col, [int]$Row, [string[]]$Targets) {
    # Coordenadas calibradas contra la lamina de iconografia enviada.
    # Recortan solo el icono, sin textos ni tarjeta.
    $cardX = @(75, 292, 520, 747, 969, 1194)
    $cardY = @(186, 414, 631, 839)

    $cropY = $cardY[$Row] + 18
    $cropH = if ($Row -eq 0) { 112 } else { 94 }
    $crop = [System.Drawing.Rectangle]::new($cardX[$Col] + 28, $cropY, 134, $cropH)
    $icon = $src.Clone($crop, $src.PixelFormat)

    # Quitar fondo blanco/gris de la tarjeta. Conserva negro, verde y grises de trazo.
    for ($x = 0; $x -lt $icon.Width; $x++) {
        for ($y = 0; $y -lt $icon.Height; $y++) {
            $p = $icon.GetPixel($x, $y)
            $max = [Math]::Max($p.R, [Math]::Max($p.G, $p.B))
            $min = [Math]::Min($p.R, [Math]::Min($p.G, $p.B))
            if ($min -gt 215 -and ($max - $min) -lt 22) {
                $icon.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
            }
        }
    }

    foreach ($target in $Targets) {
        $path = Join-Path $Root $target
        if (-not (Test-Path $path)) { continue }

        $old = [System.Drawing.Bitmap]::new($path)
        $tw = $old.Width
        $th = $old.Height
        $old.Dispose()

        $out = [System.Drawing.Bitmap]::new($tw, $th, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $pad = [Math]::Max(2, [Math]::Round([Math]::Min($tw, $th) * 0.08))
        $scale = [Math]::Min(($tw - $pad * 2) / $icon.Width, ($th - $pad * 2) / $icon.Height)
        $dw = [Math]::Round($icon.Width * $scale)
        $dh = [Math]::Round($icon.Height * $scale)
        $dx = [Math]::Round(($tw - $dw) / 2)
        $dy = [Math]::Round(($th - $dh) / 2)

        $g.DrawImage($icon, [System.Drawing.Rectangle]::new($dx, $dy, $dw, $dh))
        $g.Dispose()

        for ($x = 0; $x -lt $out.Width; $x++) {
            for ($y = 0; $y -lt $out.Height; $y++) {
                $p = $out.GetPixel($x, $y)
                $max = [Math]::Max($p.R, [Math]::Max($p.G, $p.B))
                $min = [Math]::Min($p.R, [Math]::Min($p.G, $p.B))
                if ($p.A -gt 0 -and $min -gt 200 -and ($max - $min) -lt 38) {
                    $out.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
                }
            }
        }

        $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Save-IconVariant $out $path "day" ([System.Drawing.Color]::FromArgb(0xF5, 0xF7, 0xF4)) ([System.Drawing.Color]::FromArgb(0xC5, 0xCF, 0xC5)) "day"
        Save-IconVariant $out $path "night" ([System.Drawing.Color]::FromArgb(0x10, 0x16, 0x12)) ([System.Drawing.Color]::FromArgb(0x36, 0x45, 0x39)) "night"
        Save-IconVariant $out $path "active" ([System.Drawing.Color]::FromArgb(0xDF, 0xF4, 0xDC)) ([System.Drawing.Color]::FromArgb(0x4A, 0xBA, 0x3E)) "day"
        $out.Dispose()
    }

    $icon.Dispose()
}

# 1 Home
Copy-ReferenceIcon 0 0 @(
    "SourceCode\GPS\btnImages\fileMenu.png"
)

# 2 Guidance
Copy-ReferenceIcon 1 0 @(
    "SourceCode\GPS\btnImages\NavigationSettings.png",
    "SourceCode\GPS\btnImages\AgIO.png",
    "SourceCode\AgIO\Source\btnImages\AgIOBtn.png"
)

# 3 Steering / Autosteer
Copy-ReferenceIcon 2 0 @(
    "SourceCode\GPS\btnImages\AutoSteerOn.png",
    "SourceCode\GPS\btnImages\AutoSteerOff.png",
    "SourceCode\GPS\btnImages\AutoSteerConf.png",
    "SourceCode\AgIO\Source\btnImages\B_Autosteer.png"
)

# 4 AB Line
Copy-ReferenceIcon 3 0 @(
    "SourceCode\GPS\btnImages\ABTracks.png",
    "SourceCode\GPS\btnImages\TrackLine.png",
    "SourceCode\GPS\btnImages\ABTrackAB.png"
)

# 5 U-turn
Copy-ReferenceIcon 4 0 @(
    "SourceCode\GPS\btnImages\YouTurn80.png",
    "SourceCode\GPS\btnImages\YouTurnU.png",
    "SourceCode\GPS\btnImages\Youturn80.png"
)

# 6 Section control
Copy-ReferenceIcon 5 0 @(
    "SourceCode\GPS\btnImages\SectionMasterOn.png",
    "SourceCode\GPS\btnImages\SectionMasterOff.png",
    "SourceCode\GPS\btnImages\SectionMapping.png"
)

# 7 Row-by-row
Copy-ReferenceIcon 0 1 @(
    "SourceCode\GPS\btnImages\TrackOn.png"
)

# 8 Variable rate
Copy-ReferenceIcon 1 1 @(
    "SourceCode\GPS\btnImages\FieldStats.png"
)

# 13 RTK / GPS signal
Copy-ReferenceIcon 0 2 @(
    "SourceCode\GPS\btnImages\GPSQuality.png",
    "SourceCode\GPS\btnImages\Con_SourcesRTKAlarm.png",
    "SourceCode\AgIO\Source\btnImages\B_GPS.png",
    "SourceCode\AgIO\Source\btnImages\NTRIP_Client.png",
    "SourceCode\AgIO\Source\btnImages\NTRIP_Serial.png"
)

# 14 Router / connectivity
Copy-ReferenceIcon 1 2 @(
    "SourceCode\AgIO\Source\btnImages\B_UDP.png",
    "SourceCode\AgIO\Source\btnImages\B_MQTT.png",
    "SourceCode\AgIO\Source\btnImages\RadioSettings.png"
)

# 15 Map / layers
Copy-ReferenceIcon 2 2 @(
    "SourceCode\GPS\btnImages\FieldTools.png"
)

# 16 Boundary
Copy-ReferenceIcon 3 2 @(
    "SourceCode\GPS\btnImages\Boundary.png",
    "SourceCode\GPS\btnImages\BoundaryOuter.png"
)

# 18 Marker / flag
Copy-ReferenceIcon 5 2 @(
    "SourceCode\GPS\btnImages\FlagGrn.png"
)

# 19 Settings
Copy-ReferenceIcon 0 3 @(
    "SourceCode\GPS\btnImages\Settings48.png",
    "SourceCode\AgIO\Source\btnImages\Settings48.png"
)

# 20 Calibration / wrench
Copy-ReferenceIcon 1 3 @(
    "SourceCode\GPS\btnImages\ToolAcceptChange.png"
)

# 21 Warning
Copy-ReferenceIcon 2 3 @(
    "SourceCode\GPS\btnImages\Warning.png"
)

# 22 Info
Copy-ReferenceIcon 3 3 @(
    "SourceCode\GPS\btnImages\Info.png",
    "SourceCode\AgIO\Source\btnImages\Help.png"
)

# 23 Import / export
Copy-ReferenceIcon 4 3 @(
    "SourceCode\GPS\btnImages\FileSave.png",
    "SourceCode\GPS\btnImages\FileOpen.png",
    "SourceCode\GPS\btnImages\FileSaveAs.png"
)

# 24 Cloud sync
Copy-ReferenceIcon 5 3 @(
    "SourceCode\GPS\btnImages\SaveToCloud.png"
)

$src.Dispose()
Write-Host "Agro Parallel reference icons cropped and applied."
