# diag-aog.ps1
# Diagnostica por que AgOpenGPS no levanta.
# Uso: powershell -ExecutionPolicy Bypass -File .\diag-aog.ps1

$aog = "C:\Program Files\AgroParallel\Piloto\PilotX.exe"

Write-Host "=== Diagnostico AgOpenGPS ===" -ForegroundColor Cyan
Write-Host ""

# 1. Existe?
Write-Host "[1] Existe el .exe?" -ForegroundColor Yellow
if (-not (Test-Path $aog)) {
    Write-Host "  NO - $aog no existe" -ForegroundColor Red
    exit 1
}
Write-Host "  OK - $aog" -ForegroundColor Green

# 2. Bloqueo SmartScreen
Write-Host ""
Write-Host "[2] Bloqueo SmartScreen / Zone.Identifier?" -ForegroundColor Yellow
$z = Get-Item $aog -Stream Zone.Identifier -ErrorAction SilentlyContinue
if ($z) {
    Write-Host "  SI - bloqueado por SmartScreen" -ForegroundColor Red
    Write-Host "  Solucion: Get-ChildItem 'C:\Program Files\AgroParallel\Piloto' -Recurse | Unblock-File"
} else {
    Write-Host "  No bloqueado" -ForegroundColor Green
}

# 3. Lanzar y capturar exit code
Write-Host ""
Write-Host "[3] Ejecutando AgOpenGPS y capturando exit code..." -ForegroundColor Yellow
$p = Start-Process $aog -WorkingDirectory (Split-Path $aog) -PassThru -Wait
$ec = $p.ExitCode
if ($ec -eq 0) {
    Write-Host "  ExitCode: 0 (cierre normal)" -ForegroundColor Green
} else {
    Write-Host "  ExitCode: $ec (CRASHEO)" -ForegroundColor Red
}

# 4. EventLog si crasheo
Write-Host ""
Write-Host "[4] Ultimos errores .NET / Application Error..." -ForegroundColor Yellow
Get-EventLog -LogName Application -Source ".NET Runtime" -Newest 2 -ErrorAction SilentlyContinue |
    Format-List TimeGenerated,Message
Get-EventLog -LogName Application -Source "Application Error" -Newest 2 -ErrorAction SilentlyContinue |
    Format-List TimeGenerated,Message

# 5. Registry + carpetas
Write-Host ""
Write-Host "[5] Estado del registry HKCU\SOFTWARE\AgOpenGPS y carpetas..." -ForegroundColor Yellow
$reg = Get-ItemProperty 'HKCU:\SOFTWARE\AgOpenGPS' -ErrorAction SilentlyContinue
if (-not $reg) {
    Write-Host "  Sin registry (instalacion limpia)" -ForegroundColor Green
} else {
    $wd = $reg.workingDirectory
    Write-Host "  workingDirectory: '$wd'"
    if ($wd) {
        if (Test-Path $wd) {
            Write-Host "    -> existe? SI" -ForegroundColor Green
        } else {
            Write-Host "    -> existe? NO (REGISTRY STALE - causa de crash)" -ForegroundColor Red
            Write-Host "    Solucion: Remove-ItemProperty 'HKCU:\SOFTWARE\AgOpenGPS' -Name workingDirectory"
        }
    }
    $vf = $reg.vehicleFileName
    if ($vf) {
        Write-Host "  vehicleFileName: '$vf'"
    }
}

$docs = "$env:USERPROFILE\Documents\AgOpenGPS"
foreach ($sub in @("Fields","Vehicles","Logs","Tools")) {
    $path = Join-Path $docs $sub
    $ok = Test-Path $path
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("  Documents\AgOpenGPS\{0,-9} : {1}" -f $sub, $ok) -ForegroundColor $color
}

Write-Host ""
Write-Host "=== Fin diagnostico ===" -ForegroundColor Cyan
