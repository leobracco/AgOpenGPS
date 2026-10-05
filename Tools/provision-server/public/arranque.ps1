# ============================================================================
#  arranque.ps1 — saca a AgOpenGPS del arranque de Windows y deja PilotX.
#
#  Para qué: en una pantalla migrada, AOG suele quedar arrancando solo (carpeta
#  Inicio, clave Run, o una tarea programada). Si eso queda, al prender la
#  máquina se abren los dos: pelean por el puerto del GPS, por el 9999 de los
#  módulos y por la pantalla completa. El operario ve el programa viejo y cree
#  que no se instaló nada.
#
#  Se corre con UNA línea:
#      irm <IP>:8090/arranque.ps1|iex
#
#  Solo toca entradas que apuntan a AgOpenGPS/AgIO o a C:\AgroParallel. Deja
#  constancia de lo que sacó en C:\PilotX\arranque-viejo.txt, así se puede
#  volver atrás a mano si hiciera falta.
# ============================================================================
$ErrorActionPreference = "Continue"
try { Set-ExecutionPolicy Bypass -Scope Process -Force -ErrorAction Stop } catch { }

function Ok($t)    { Write-Host "  [OK] $t" -ForegroundColor Green }
function Aviso($t) { Write-Host "  [--] $t" -ForegroundColor DarkGray }
function Mal($t)   { Write-Host "  [!!] $t" -ForegroundColor Red }

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { Mal "Hay que correrlo como ADMINISTRADOR."; return }

Write-Host ""
Write-Host "=== Arranque de Windows: sacar AgOpenGPS, dejar PilotX ===" -ForegroundColor Cyan
Write-Host ""

# Qué cuenta como "arranque viejo". Se busca por la RUTA del ejecutable, no por
# el nombre de la entrada, que cada instalación bautizó como quiso.
$esViejo = {
    param($cmd)
    if (-not $cmd) { return $false }
    $c = $cmd.ToLower()
    # OJO: "pilotx" adentro de C:\AgroParallel NO existe; la instalación nueva
    # vive en C:\PilotX. Aun así se excluye explícitamente para no suicidarse.
    if ($c -match 'c:\\pilotx') { return $false }
    return ($c -match 'agopengps\.exe' -or $c -match 'agio\.exe' -or
            $c -match 'c:\\agroparallel' -or $c -match 'ratecontrollerapp' -or
            $c -match 'widgetx')
}

$sacados = @()

# --- 1. Claves Run / RunOnce, de la máquina y de cada usuario ---------------
$claves = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
)
foreach ($k in $claves) {
    if (-not (Test-Path $k)) { continue }
    $p = Get-ItemProperty $k
    foreach ($n in $p.PSObject.Properties.Name) {
        if ($n -like "PS*") { continue }
        $valor = [string]$p.$n
        if (& $esViejo $valor) {
            $sacados += "Run  $k  $n = $valor"
            try { Remove-ItemProperty -Path $k -Name $n -Force; Ok "sacado de Run: $n" }
            catch { Mal "no se pudo sacar $n : $($_.Exception.Message)" }
        }
    }
}

# --- 2. Carpetas Inicio (la del usuario y la de todos) ----------------------
$inicios = @(
    [Environment]::GetFolderPath("Startup"),
    [Environment]::GetFolderPath("CommonStartup")
)
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in $inicios) {
    if (-not $dir -or -not (Test-Path $dir)) { continue }
    foreach ($f in Get-ChildItem $dir -File -EA 0) {
        $destino = $f.FullName
        if ($f.Extension -ieq ".lnk") {
            try { $destino = $ws.CreateShortcut($f.FullName).TargetPath } catch { }
        }
        if ((& $esViejo $destino) -or (& $esViejo $f.Name)) {
            $sacados += "Inicio  $($f.FullName)  ->  $destino"
            try { Remove-Item $f.FullName -Force; Ok "sacado del Inicio: $($f.Name)" }
            catch { Mal "no se pudo borrar $($f.Name)" }
        }
    }
}

# --- 3. Tareas programadas --------------------------------------------------
foreach ($t in (Get-ScheduledTask -EA 0)) {
    $acciones = @($t.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" })
    foreach ($a in $acciones) {
        if (& $esViejo $a) {
            $sacados += "Tarea  $($t.TaskPath)$($t.TaskName)  ->  $a"
            try { Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false
                  Ok "tarea eliminada: $($t.TaskName)" }
            catch { Mal "no se pudo eliminar la tarea $($t.TaskName)" }
            break
        }
    }
}

# --- 4. Shell de Winlogon ---------------------------------------------------
# Si alguna instalación vieja dejó AOG como shell, la pantalla no tiene
# escritorio y PilotX nunca se ve.
$wl = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
$shell = (Get-ItemProperty $wl -EA 0).Shell
if ($shell -and (& $esViejo $shell)) {
    $sacados += "Shell  $shell"
    Set-ItemProperty $wl -Name "Shell" -Value "explorer.exe" -Type String -Force
    Ok "Shell de Windows devuelto a explorer.exe (estaba en el programa viejo)"
}

# --- 5. Dejar PilotX arrancando ---------------------------------------------
# Si el kiosko está puesto, el Shell YA es PilotX y no hay que agregar nada:
# un acceso directo en Inicio abriría una segunda copia.
$shell = (Get-ItemProperty $wl -EA 0).Shell
$kiosko = $shell -and ($shell -like "*PilotX*")
$lanzador = "C:\PilotX\Lanzar-PilotX.bat"

if ($kiosko) {
    Aviso "modo kiosko activo: PilotX ya es el shell, no hace falta nada en Inicio"
}
elseif (-not (Test-Path $lanzador)) {
    Mal "no está $lanzador — PilotX no parece instalado"
}
else {
    $dst = Join-Path ([Environment]::GetFolderPath("CommonStartup")) "PilotX.lnk"
    try {
        $lnk = $ws.CreateShortcut($dst)
        $lnk.TargetPath = $lanzador
        $lnk.WorkingDirectory = "C:\PilotX"
        $lnk.WindowStyle = 7
        $lnk.Description = "PilotX - Agro Parallel"
        if (Test-Path "C:\PilotX\Desktop\PilotX.Desktop.exe") {
            $lnk.IconLocation = "C:\PilotX\Desktop\PilotX.Desktop.exe,0"
        }
        $lnk.Save()
        Ok "PilotX queda arrancando con Windows (Inicio de todos los usuarios)"
    } catch { Mal "no se pudo crear el acceso en Inicio: $($_.Exception.Message)" }
}

# --- Constancia -------------------------------------------------------------
Write-Host ""
if ($sacados.Count -eq 0) {
    Aviso "no habia ningun arranque viejo: no se saco nada"
} else {
    New-Item -ItemType Directory -Path "C:\PilotX" -Force | Out-Null
    $log = "C:\PilotX\arranque-viejo.txt"
    ("Sacados el $(Get-Date)" , "") + $sacados | Add-Content $log -Encoding UTF8
    Write-Host "  Se sacaron $($sacados.Count) entradas. Quedo constancia en $log" -ForegroundColor Cyan
    foreach ($s in $sacados) { Write-Host "    $s" -ForegroundColor DarkGray }
}
Write-Host ""
Write-Host "  Reinicia para comprobar que arranca PilotX y no el programa viejo." -ForegroundColor Yellow
Write-Host ""
