@echo off
REM ========================================================================
REM setup_pilotx_lan.bat — Setup de red para AgIO/PilotX en pantalla del tractor
REM Auto-eleva a admin (UAC). Corre una sola vez por instalacion.
REM ========================================================================

REM ── Auto-elevar si no es admin ─────────────────────────────────────────
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Solicitando permisos de administrador...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo.
echo ============================================================
echo   Setup PilotX LAN — Firewall + URL ACL
echo ============================================================
echo.

REM ── 1. URL ACL para que AgIO pueda escuchar en 0.0.0.0:8080 ────────────
echo [1/4] Registrando URL ACL para puerto 8080...
netsh http delete urlacl url=http://+:8080/ >nul 2>&1
netsh http add urlacl url=http://+:8080/ user=Everyone
if %errorLevel% neq 0 (
    echo   ERROR: no se pudo agregar URL ACL.
) else (
    echo   OK
)

REM ── 2. Firewall: permitir puerto 8080 (panel web/PWA) ──────────────────
echo.
echo [2/4] Abriendo firewall TCP 8080 (panel PWA)...
netsh advfirewall firewall delete rule name="AgIO NodePanel 8080" >nul 2>&1
netsh advfirewall firewall add rule name="AgIO NodePanel 8080" dir=in action=allow protocol=TCP localport=8080
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 3. Firewall: permitir puerto 1883 (broker MQTT) ────────────────────
echo.
echo [3/4] Abriendo firewall TCP 1883 (broker MQTT)...
netsh advfirewall firewall delete rule name="AgIO MQTT 1883" >nul 2>&1
netsh advfirewall firewall add rule name="AgIO MQTT 1883" dir=in action=allow protocol=TCP localport=1883
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 4. Firewall: permitir ICMP (ping) ──────────────────────────────────
echo.
echo [4/4] Permitiendo ICMP (ping entrante)...
netsh advfirewall firewall delete rule name="ICMP Allow Inbound" >nul 2>&1
netsh advfirewall firewall add rule name="ICMP Allow Inbound" protocol=icmpv4:8,any dir=in action=allow
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

echo.
echo ============================================================
echo   Listo. Reinicia AgIO para que el panel quede en 0.0.0.0:8080
echo ============================================================
echo.
echo   Despues de reiniciar AgIO, deberias poder:
echo     - Ping a 192.168.5.10 desde el celular
echo     - Abrir http://192.168.5.10:8080 desde el celular
echo     - Los nodos ESP32 conectandose al broker en :1883
echo.
pause
