# stop-all.ps1
# Baja todos los procesos AgroParallel (AOG, AgIO + broker MQTT, utilidades).
# Uso:
#   powershell -ExecutionPolicy Bypass -File .\stop-all.ps1
#   powershell -ExecutionPolicy Bypass -File .\stop-all.ps1 -Verbose

[CmdletBinding()]
param()

function Stop-IfRunning($name) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | ForEach-Object {
            Write-Host ("  Killing {0} (PID {1})" -f $_.ProcessName, $_.Id) -ForegroundColor Yellow
            try { $_ | Stop-Process -Force -ErrorAction Stop } catch { Write-Host "    fallo: $_" -ForegroundColor Red }
        }
    } else {
        Write-Verbose "  $name no esta corriendo"
    }
}

Write-Host "=== Bajando procesos AgroParallel ===" -ForegroundColor Cyan

# Procesos .NET de la suite
foreach ($p in @("AgOpenGPS","AgIO","GPS_Out","AgDiag","ModSim","CoreX")) {
    Stop-IfRunning $p
}

# Servidores Node legacy (VistaX/OrbitX-Sync) - matamos solo los que tengan
# en su CommandLine un path AgroParallel/vistax/orbitx
$nodeProcs = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue
foreach ($n in $nodeProcs) {
    $cmd = $n.CommandLine
    if ($cmd -match "AgroParallel|vistax|orbitx" ) {
        Write-Host ("  Killing node.exe (PID {0})" -f $n.ProcessId) -ForegroundColor Yellow
        Write-Verbose ("    cmd: {0}" -f $cmd)
        try { Stop-Process -Id $n.ProcessId -Force -ErrorAction Stop } catch { Write-Host "    fallo: $_" -ForegroundColor Red }
    }
}

# Verificar que el puerto 1883 (MQTT broker) quedo libre
Start-Sleep -Milliseconds 500
$mqttListener = Get-NetTCPConnection -LocalPort 1883 -State Listen -ErrorAction SilentlyContinue
if ($mqttListener) {
    Write-Host "  ATENCION: puerto 1883 sigue ocupado por PID(s):" -ForegroundColor Red
    $mqttListener | ForEach-Object {
        $owner = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        Write-Host ("    PID {0} - {1}" -f $_.OwningProcess, $owner.ProcessName)
    }
} else {
    Write-Host "  Puerto 1883 (MQTT broker) liberado" -ForegroundColor Green
}

Write-Host "=== OK ===" -ForegroundColor Green
