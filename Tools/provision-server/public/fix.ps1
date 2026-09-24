# ============================================================================
#  fix.ps1 — saca la pantalla del bucle de reinicios del kiosko.
#
#  Para que existe: si el modo kiosko se activa ANTES de que PilotX pueda
#  arrancar (falta el VC++ redist, o WebView2, o el usuario 'pilotx' no se
#  creo), Windows queda sin escritorio al que volver: el Shell apunta a
#  PilotX.exe, PilotX no levanta, y la sesion se reinicia en bucle.
#
#  Se corre desde la consola del OOBE (Shift+F10) con UNA linea:
#      irm <IP>:8090/fix.ps1|iex
#
#  Es idempotente: se puede correr las veces que haga falta.
# ============================================================================
$ErrorActionPreference = "Continue"

Write-Host ""
Write-Host "=== Recuperacion de pantalla PilotX ===" -ForegroundColor Cyan
Write-Host ""

# --- 1) Devolver el escritorio de Windows -----------------------------------
$wl = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
try {
    Set-ItemProperty -Path $wl -Name "Shell" -Value "explorer.exe" -Type String -Force
    Write-Host "  [OK] Shell de Windows devuelto a explorer.exe" -ForegroundColor Green
} catch { Write-Host "  [!!] No se pudo tocar el Shell: $($_.Exception.Message)" -ForegroundColor Red }

try {
    Set-ItemProperty -Path $wl -Name "AutoAdminLogon" -Value "0" -Type String -Force
    Remove-ItemProperty -Path $wl -Name "DefaultPassword" -ErrorAction SilentlyContinue
    Write-Host "  [OK] Inicio de sesion automatico apagado" -ForegroundColor Green
} catch { Write-Host "  [!!] No se pudo apagar el autologon: $($_.Exception.Message)" -ForegroundColor Red }

# --- 2) Usuarios ------------------------------------------------------------
# 'soporte' es con el que se entra a arreglar; 'pilotx' es el del operario, y
# va SIN contraseña porque la pantalla del tractor tiene que arrancar sola.
function CrearUsuario($nombre, $pass, $grupo) {
    if (Get-LocalUser -Name $nombre -ErrorAction SilentlyContinue) {
        Write-Host "  [--] Usuario '$nombre' ya existia" -ForegroundColor DarkGray
    } else {
        try {
            if ($pass) {
                New-LocalUser -Name $nombre -Password (ConvertTo-SecureString $pass -AsPlainText -Force) `
                              -FullName $nombre -PasswordNeverExpires -AccountNeverExpires | Out-Null
            } else {
                # OJO: -NoPassword y -PasswordNeverExpires son INCOMPATIBLES
                # ("No se puede resolver el conjunto de parametros"). Sin
                # contraseña no hay caducidad que configurar. La caducidad se
                # apaga despues, por separado, que si funciona.
                New-LocalUser -Name $nombre -NoPassword -FullName $nombre `
                              -AccountNeverExpires | Out-Null
                try { Set-LocalUser -Name $nombre -PasswordNeverExpires $true } catch { }
            }
            Write-Host "  [OK] Usuario '$nombre' creado" -ForegroundColor Green
        } catch { Write-Host "  [!!] '$nombre': $($_.Exception.Message)" -ForegroundColor Red; return }
    }
    # El grupo cambia de nombre segun el idioma de Windows: se prueban los dos.
    foreach ($g in $grupo) {
        try { Add-LocalGroupMember -Group $g -Member $nombre -ErrorAction Stop | Out-Null; break } catch { }
    }
}

CrearUsuario "soporte" "Agro2026" @("Administradores", "Administrators")
CrearUsuario "pilotx"  $null      @("Usuarios", "Users")

# --- 3) Estado de lo instalado ---------------------------------------------
Write-Host ""
if (Test-Path "C:\PilotX\Desktop\PilotX.Desktop.exe") {
    Write-Host "  [OK] PilotX esta instalado en C:\PilotX (no hay que bajarlo de nuevo)" -ForegroundColor Green
} elseif (Test-Path "C:\PilotX") {
    Write-Host "  [!!] C:\PilotX existe pero le falta el ejecutable: la instalacion quedo a medias" -ForegroundColor Yellow
} else {
    Write-Host "  [--] PilotX no esta instalado" -ForegroundColor DarkGray
}

# --- 4) Dar por terminado el asistente de Windows ---------------------------
# Matar msoobe a secas deja la instalacion "a medias": Windows vuelve a
# arrancar el asistente, tira "Windows no puede completar la instalacion" y se
# queda en "Un momento..." para siempre. Estas claves son las que el propio
# asistente escribe cuando termina bien; poniendolas a mano, el proximo arranque
# va derecho a la pantalla de inicio de sesion.
$setup = "HKLM:\SYSTEM\Setup"
try {
    New-Item -Path "$setup\Status\ChildCompletion" -Force | Out-Null
    Set-ItemProperty -Path "$setup\Status\ChildCompletion" -Name "setup.exe" -Value 3 -Type DWord -Force
    Set-ItemProperty -Path "$setup\Status\ChildCompletion" -Name "audit.exe" -Value 3 -Type DWord -Force -ErrorAction SilentlyContinue
    foreach ($k in @("OOBEInProgress", "SystemSetupInProgress", "SetupType")) {
        Set-ItemProperty -Path $setup -Name $k -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    }
    Remove-ItemProperty -Path $setup -Name "CmdLine" -Force -ErrorAction SilentlyContinue
    Write-Host "  [OK] Asistente de Windows marcado como TERMINADO" -ForegroundColor Green
} catch {
    Write-Host "  [!!] No se pudo cerrar el asistente: $($_.Exception.Message)" -ForegroundColor Red
}

# Por las dudas: si el asistente sigue en pantalla, se lo cierra.
if (Get-Process msoobe -ErrorAction SilentlyContinue) {
    Stop-Process -Name msoobe -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "=== LISTO ===" -ForegroundColor Green
Write-Host "  Entra con el usuario 'soporte' y la clave Agro2026" -ForegroundColor Yellow
Write-Host "  El kiosko quedo APAGADO: Windows arranca normal." -ForegroundColor Yellow
Write-Host ""
Write-Host "  Reinicia con:  shutdown -r -t 0" -ForegroundColor Cyan
Write-Host ""
