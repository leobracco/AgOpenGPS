# deploy-taller.ps1 — publica PilotX.Desktop y lo despliega en la pantalla
# del taller (192.168.1.78) por WinRM. Un comando, ~2 min, y el taller queda
# corriendo el build nuevo en su sesion fisica.
#
#   powershell -ExecutionPolicy Bypass -File Tools\deploy-taller.ps1
#   powershell ... -File Tools\deploy-taller.ps1 -SinPublish   # solo re-desplegar
#
# Flujo local->taller (2026-08-05): aca se desarrolla, el taller prueba.
# Detalles del taller que hay que saber:
#   · WinRM :5985, usuario Admin sin password (red del taller, no exponer).
#   · Layout: C:\PilotX\{Desktop,Engine,logs} + Lanzar-PilotX.bat.
#   · La GUI tiene que nacer en la SESION FISICA: WinRM cae en sesion 0, por
#     eso el lanzamiento va via tarea programada con /it (lanzar-diag.bat,
#     que ademas redirige stderr a C:\PilotX\logs\stderr-diag.log — ahi
#     viven el latido del mapa, el espia y la caja negra).
#   · El Engine NO se toca por default: los cambios visuales viven en
#     Desktop. Si hace falta engine, deploy manual aparte.
#   · Lote de prueba: "Taller 100ha" (100 ha exactas, 3 franjas de
#     prescripcion DOSIS 4/5/6 sem/m — /api/prescripciones/dose las valida).

param(
    [switch]$SinPublish,
    [switch]$Forzar,
    [string]$Taller = "192.168.1.78"
)

$ErrorActionPreference = "Stop"

# Guard: si el taller tiene un lote ABIERTO, lo mas probable es que alguien
# (persona o script de ciclos) este corriendo una prueba — desplegar ahora la
# mataria a mitad de camino y el resultado no serviria. Se salta con -Forzar.
if (-not $Forzar) {
    try {
        $cur = (Invoke-WebRequest "http://${Taller}:5180/api/lotes/current" -UseBasicParsing -TimeoutSec 5).Content
        if ($cur -match '"name":"(?!null)[^"]+"') {
            Write-Host "El taller tiene un lote abierto ($cur — probable prueba en curso." -ForegroundColor Yellow
            Write-Host "No se despliega. Cuando termine, reintentar; o -Forzar si es a proposito." -ForegroundColor Yellow
            exit 2
        }
    } catch {
        # API caida no bloquea: puede ser justamente lo que el deploy arregla.
    }
}
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $env:TEMP "pilotx-deploy-taller"
$zip = Join-Path $env:TEMP "pilotx-deploy-taller.zip"

if (-not $SinPublish) {
    Write-Host "== publish PilotX.Desktop ==" -ForegroundColor Cyan
    dotnet publish "$root\SourceCode\PilotX.Desktop\PilotX.Desktop.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -o "$root\Build\Desktop" -v q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "publish FALLO" -ForegroundColor Red; exit 1 }
}

Write-Host "== staging (sin cache WebView2 ni logs) ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
# El cache de WebView2 esta lockeado si PilotX corre local, y no debe viajar.
robocopy "$root\Build\Desktop" $stage /E /XD "PilotX.Desktop.exe.WebView2" "Logs" /XF "*.log" /NFL /NDL /NJH /NJS | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("zip: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))

Write-Host "== deploy a $Taller ==" -ForegroundColor Cyan
$cred = New-Object System.Management.Automation.PSCredential(
    "Admin", (New-Object System.Security.SecureString))
$s = New-PSSession -ComputerName $Taller -Credential $cred

Copy-Item $zip -Destination "C:\PilotX\deploy.zip" -ToSession $s

Invoke-Command -Session $s -ScriptBlock {
    Get-Process PilotX.Desktop -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    # Un solo backup, el anterior se pisa: el taller no es archivo historico.
    $bak = "C:\PilotX\Desktop-anterior"
    if (Test-Path $bak) { Remove-Item $bak -Recurse -Force }
    if (Test-Path "C:\PilotX\Desktop") { Rename-Item "C:\PilotX\Desktop" $bak }
    Expand-Archive "C:\PilotX\deploy.zip" -DestinationPath "C:\PilotX\Desktop"
    Remove-Item "C:\PilotX\deploy.zip" -Force

    # Relanzar en la sesion fisica (la tarea ya existe desde el primer deploy;
    # si no, crearla apuntando al wrapper con stderr redirigido).
    if (-not (Test-Path "C:\PilotX\lanzar-diag.bat")) {
        Set-Content "C:\PilotX\lanzar-diag.bat" "@echo off`r`ncd /d C:\PilotX`r`ntasklist | find /i `"PilotX.GuidanceEngine.exe`" >nul || start `"PilotX Engine`" /min C:\PilotX\Engine\PilotX.GuidanceEngine.exe --webhost --corex`r`ntimeout /t 6 /nobreak >nul`r`nC:\PilotX\Desktop\PilotX.Desktop.exe 2> C:\PilotX\logs\stderr-diag.log" -Encoding ascii
    }
    schtasks /query /tn "PilotX-Diag" >$null 2>&1
    if ($LASTEXITCODE -ne 0) {
        schtasks /create /tn "PilotX-Diag" /tr "C:\PilotX\lanzar-diag.bat" /sc once /st 23:59 /it /f /rl highest | Out-Null
    }
    schtasks /run /tn "PilotX-Diag" | Out-Null
    Start-Sleep -Seconds 12
    $p = Get-Process PilotX.Desktop -EA SilentlyContinue
    if ($p) { "OK: PilotX.Desktop corriendo (PID $($p.Id))" }
    else { "ERROR: no arranco — mirar C:\PilotX\logs\stderr-diag.log" }
}

Remove-PSSession $s
Write-Host "== listo ==" -ForegroundColor Green
