# =============================================================================
#  make-brand-icons.ps1 - Genera los iconos de botones de AgIO con la paleta
#                        Agro Parallel (verde marca #71B528, gris carbon).
#
#  Reemplaza:
#    SourceCode/AgIO/Source/btnImages/AgIOBtn.png    (64x64) - icono AgIO
#    SourceCode/AgIO/Source/btnImages/B_IMU.png      (94x64) - icono IMU
#    SourceCode/AgIO/Source/btnImages/B_Machine.png  (94x64) - icono Machine
#  Crea:
#    SourceCode/AgIO/Source/btnImages/B_MQTT.png     (94x64) - icono Broker MQTT
#
#  Estilo: fondo gris oscuro #2A2E33, glyph en verde marca #71B528 + blanco,
#  esquinas redondeadas. Coherente con el theme dark de Agro Parallel.
#
#  Uso (desde la raiz del repo o desde Tools/):
#     powershell -NoProfile -ExecutionPolicy Bypass -File Tools\make-brand-icons.ps1
# =============================================================================

[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Resolver carpeta destino
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
if (-not $OutDir) {
    $OutDir = Join-Path $RepoRoot "SourceCode\AgIO\Source\btnImages"
}
if (-not (Test-Path $OutDir)) { throw "No existe $OutDir" }
Write-Host "Output: $OutDir" -ForegroundColor Cyan

# Paleta Agro Parallel
$BgDark    = [System.Drawing.Color]::FromArgb(255, 0x2A, 0x2E, 0x33)  # #2A2E33
$BgLight   = [System.Drawing.Color]::FromArgb(255, 0x3A, 0x3F, 0x46)
$Accent    = [System.Drawing.Color]::FromArgb(255, 113, 181, 40)      # #71B528
$AccentDim = [System.Drawing.Color]::FromArgb(255,  75, 120,  27)
$White     = [System.Drawing.Color]::White
$WhiteDim  = [System.Drawing.Color]::FromArgb(220, 240, 240, 245)

function New-Bitmap([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode= [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint= [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @{ Bmp=$bmp; G=$g; W=$w; H=$h }
}

function Draw-RoundedBg($ctx) {
    $g = $ctx.G; $w = $ctx.W; $h = $ctx.H
    $r = 10  # radio esquinas
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r*2, $r*2, 180, 90)
    $path.AddArc($w-$r*2-1, 0, $r*2, $r*2, 270, 90)
    $path.AddArc($w-$r*2-1, $h-$r*2-1, $r*2, $r*2, 0, 90)
    $path.AddArc(0, $h-$r*2-1, $r*2, $r*2, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, $BgDark, $BgLight,
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $path)
    $brush.Dispose()

    $borderPen = New-Object System.Drawing.Pen $Accent, 1.5
    $g.DrawPath($borderPen, $path)
    $borderPen.Dispose()
    $path.Dispose()
}

function Save-Bitmap($ctx, [string]$path) {
    $ctx.G.Dispose()
    $ctx.Bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $ctx.Bmp.Dispose()
    Write-Host "  -> $path" -ForegroundColor Green
}

# ============================================================================
# ICONO AgIO (64x64) — anillo de chip + 'AgIO' centrado
# ============================================================================
function Make-AgIO {
    $ctx = New-Bitmap 64 64
    Draw-RoundedBg $ctx
    $g = $ctx.G

    # Anillo verde alrededor (chip vibe)
    $ringPen = New-Object System.Drawing.Pen $Accent, 2.5
    $g.DrawEllipse($ringPen, 8, 6, 48, 38)
    $ringPen.Dispose()

    # Texto "AgIO"
    $font = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $sf   = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 0, 8, 64, 36
    $br   = New-Object System.Drawing.SolidBrush $White
    $g.DrawString("AgIO", $font, $br, $rect, $sf)
    $br.Dispose(); $font.Dispose()

    # Subtitulo
    $font2 = New-Object System.Drawing.Font("Segoe UI", 6, [System.Drawing.FontStyle]::Regular)
    $rect2 = New-Object System.Drawing.RectangleF 0, 46, 64, 14
    $br2   = New-Object System.Drawing.SolidBrush $Accent
    $g.DrawString("AGRO PARALLEL", $font2, $br2, $rect2, $sf)
    $br2.Dispose(); $font2.Dispose()

    Save-Bitmap $ctx (Join-Path $OutDir "AgIOBtn.png")
}

# ============================================================================
# ICONO IMU (94x64) — brujula con flecha
# ============================================================================
function Make-IMU {
    $ctx = New-Bitmap 94 64
    Draw-RoundedBg $ctx
    $g = $ctx.G

    # Compass circle
    $cx = 26; $cy = 32; $r = 18
    $pen = New-Object System.Drawing.Pen $WhiteDim, 1.8
    $g.DrawEllipse($pen, $cx-$r, $cy-$r, $r*2, $r*2)
    $pen.Dispose()

    # Marcas N/S/E/W
    $tickPen = New-Object System.Drawing.Pen $WhiteDim, 1
    foreach ($a in @(0, 90, 180, 270)) {
        $rad = [Math]::PI * $a / 180.0
        $x1 = $cx + ($r-1) * [Math]::Sin($rad)
        $y1 = $cy - ($r-1) * [Math]::Cos($rad)
        $x2 = $cx + ($r-5) * [Math]::Sin($rad)
        $y2 = $cy - ($r-5) * [Math]::Cos($rad)
        $g.DrawLine($tickPen, $x1, $y1, $x2, $y2)
    }
    $tickPen.Dispose()

    # Aguja N (verde) / S (blanca dim)
    $needleN = @(
        (New-Object System.Drawing.PointF $cx, ($cy - $r + 4)),
        (New-Object System.Drawing.PointF ($cx - 4), $cy),
        (New-Object System.Drawing.PointF ($cx + 4), $cy)
    )
    $brushN = New-Object System.Drawing.SolidBrush $Accent
    $g.FillPolygon($brushN, $needleN)
    $brushN.Dispose()

    $needleS = @(
        (New-Object System.Drawing.PointF $cx, ($cy + $r - 4)),
        (New-Object System.Drawing.PointF ($cx - 4), $cy),
        (New-Object System.Drawing.PointF ($cx + 4), $cy)
    )
    $brushS = New-Object System.Drawing.SolidBrush $WhiteDim
    $g.FillPolygon($brushS, $needleS)
    $brushS.Dispose()

    # Texto "IMU"
    $font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
    $sf   = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 50, 14, 44, 24
    $br   = New-Object System.Drawing.SolidBrush $White
    $g.DrawString("IMU", $font, $br, $rect, $sf)
    $br.Dispose(); $font.Dispose()

    # Subtitulo verde
    $f2 = New-Object System.Drawing.Font("Segoe UI", 6, [System.Drawing.FontStyle]::Regular)
    $r2 = New-Object System.Drawing.RectangleF 50, 38, 44, 14
    $b2 = New-Object System.Drawing.SolidBrush $Accent
    $g.DrawString("9-DOF", $f2, $b2, $r2, $sf)
    $b2.Dispose(); $f2.Dispose()

    Save-Bitmap $ctx (Join-Path $OutDir "B_IMU.png")
}

# ============================================================================
# ICONO Machine (94x64) — engranaje + texto
# ============================================================================
function Make-Machine {
    $ctx = New-Bitmap 94 64
    Draw-RoundedBg $ctx
    $g = $ctx.G

    # Engranaje (8 dientes)
    $cx = 26; $cy = 32
    $rOut = 16; $rIn = 11; $rHole = 5
    $teeth = 8
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    for ($i = 0; $i -lt $teeth; $i++) {
        $a0 = ($i + 0.0) * 360 / $teeth
        $a1 = ($i + 0.3) * 360 / $teeth
        $a2 = ($i + 0.7) * 360 / $teeth
        $a3 = ($i + 1.0) * 360 / $teeth
        function Pt($cx, $cy, $r, $deg) {
            $rad = [Math]::PI * $deg / 180.0
            return New-Object System.Drawing.PointF ($cx + $r * [Math]::Cos($rad)), ($cy + $r * [Math]::Sin($rad))
        }
        if ($i -eq 0) { $path.StartFigure() }
        $path.AddLine((Pt $cx $cy $rIn $a0), (Pt $cx $cy $rOut $a1))
        $path.AddLine((Pt $cx $cy $rOut $a1), (Pt $cx $cy $rOut $a2))
        $path.AddLine((Pt $cx $cy $rOut $a2), (Pt $cx $cy $rIn $a3))
    }
    $path.CloseFigure()
    $brushG = New-Object System.Drawing.SolidBrush $Accent
    $g.FillPath($brushG, $path)
    $brushG.Dispose()

    # Hueco central
    $brushBg = New-Object System.Drawing.SolidBrush $BgDark
    $g.FillEllipse($brushBg, $cx-$rHole, $cy-$rHole, $rHole*2, $rHole*2)
    $brushBg.Dispose()

    # Texto "MACHINE" (uppercase, con StringFormat NoWrap)
    $font = New-Object System.Drawing.Font("Segoe UI", 8, [System.Drawing.FontStyle]::Bold)
    $sf   = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $sf.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap
    $sf.Trimming      = [System.Drawing.StringTrimming]::None
    $rect = New-Object System.Drawing.RectangleF 46, 14, 48, 22
    $br   = New-Object System.Drawing.SolidBrush $White
    $g.DrawString("MACHINE", $font, $br, $rect, $sf)
    $br.Dispose(); $font.Dispose()

    $f2 = New-Object System.Drawing.Font("Segoe UI", 6, [System.Drawing.FontStyle]::Regular)
    $r2 = New-Object System.Drawing.RectangleF 46, 36, 48, 14
    $b2 = New-Object System.Drawing.SolidBrush $Accent
    $g.DrawString("CONTROL", $f2, $b2, $r2, $sf)
    $b2.Dispose(); $f2.Dispose()

    Save-Bitmap $ctx (Join-Path $OutDir "B_Machine.png")
}

# ============================================================================
# ICONO MQTT (94x64) — nodo central + 3 satelites (broker pub/sub)
# ============================================================================
function Make-MQTT {
    $ctx = New-Bitmap 94 64
    Draw-RoundedBg $ctx
    $g = $ctx.G

    # Nodo central (broker)
    $cx = 26; $cy = 32
    $brushC = New-Object System.Drawing.SolidBrush $Accent
    $g.FillEllipse($brushC, $cx-7, $cy-7, 14, 14)
    $brushC.Dispose()

    # Satelites: arriba, izq-abajo, der-abajo
    $sats = @(
        @{ x = $cx - 16; y = $cy - 14 },
        @{ x = $cx - 16; y = $cy + 14 },
        @{ x = $cx + 16; y = $cy }
    )
    $linePen = New-Object System.Drawing.Pen $WhiteDim, 1.4
    $brushS  = New-Object System.Drawing.SolidBrush $White
    foreach ($s in $sats) {
        $g.DrawLine($linePen, $cx, $cy, $s.x, $s.y)
        $g.FillEllipse($brushS, $s.x - 4, $s.y - 4, 8, 8)
    }
    $linePen.Dispose(); $brushS.Dispose()

    # Texto "MQTT" (NoWrap forzado, font ajustado al ancho disponible)
    $font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $sf   = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $sf.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap
    $sf.Trimming      = [System.Drawing.StringTrimming]::None
    $rect = New-Object System.Drawing.RectangleF 46, 14, 48, 22
    $br   = New-Object System.Drawing.SolidBrush $White
    $g.DrawString("MQTT", $font, $br, $rect, $sf)
    $br.Dispose(); $font.Dispose()

    $f2 = New-Object System.Drawing.Font("Segoe UI", 6, [System.Drawing.FontStyle]::Regular)
    $r2 = New-Object System.Drawing.RectangleF 46, 36, 48, 14
    $b2 = New-Object System.Drawing.SolidBrush $Accent
    $g.DrawString("BROKER", $f2, $b2, $r2, $sf)
    $b2.Dispose(); $f2.Dispose()

    Save-Bitmap $ctx (Join-Path $OutDir "B_MQTT.png")
}

# ============================================================================
# ICONO MQTT small (90x36) — para btnMQTT en FormLoop (config secundaria)
# ============================================================================
function Make-MQTT-Small {
    $ctx = New-Bitmap 90 36
    Draw-RoundedBg $ctx
    $g = $ctx.G

    # Nodo + 2 satelites (compacto)
    $cx = 18; $cy = 18
    $brushC = New-Object System.Drawing.SolidBrush $Accent
    $g.FillEllipse($brushC, $cx-5, $cy-5, 10, 10)
    $brushC.Dispose()

    $sats = @(
        @{ x = $cx - 10; y = $cy - 9 },
        @{ x = $cx - 10; y = $cy + 9 },
        @{ x = $cx + 10; y = $cy }
    )
    $linePen = New-Object System.Drawing.Pen $WhiteDim, 1
    $brushS  = New-Object System.Drawing.SolidBrush $White
    foreach ($s in $sats) {
        $g.DrawLine($linePen, $cx, $cy, $s.x, $s.y)
        $g.FillEllipse($brushS, $s.x - 2.5, $s.y - 2.5, 5, 5)
    }
    $linePen.Dispose(); $brushS.Dispose()

    # "MQTT" sin subtitle (un solo renglon)
    $font = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $sf   = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $sf.FormatFlags   = [System.Drawing.StringFormatFlags]::NoWrap
    $rect = New-Object System.Drawing.RectangleF 35, 0, 55, 36
    $br   = New-Object System.Drawing.SolidBrush $White
    $g.DrawString("MQTT", $font, $br, $rect, $sf)
    $br.Dispose(); $font.Dispose()

    Save-Bitmap $ctx (Join-Path $OutDir "B_MQTT_small.png")
}

Write-Host "`nGenerando iconos Agro Parallel..." -ForegroundColor Cyan
Make-AgIO
Make-IMU
Make-Machine
Make-MQTT
Make-MQTT-Small
Write-Host "`n[OK] Iconos generados en $OutDir" -ForegroundColor Green
