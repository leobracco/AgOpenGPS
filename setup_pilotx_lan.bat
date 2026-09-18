@echo off
REM ========================================================================
REM setup_pilotx_lan.bat — Setup de red para CoreX/PilotX en pantalla del tractor
REM Auto-eleva a admin (UAC). Corre una sola vez por instalacion.
REM
REM 2026-09-04: agregado UDP 9999 (PGN de los modulos LAN) y TCP 5180/5181.
REM Faltaba el 9999 desde siempre: como el broker MQTT (TCP 1883) SI tenia
REM regla, los nodos ESP32 aparecian conectados en el panel pero su PGN no
REM entraba nunca — el firewall lo comia sin avisar. Sintoma real en campo
REM (ToolX, 2026-09-04): el nodo se veia online, el switch cambiaba en su
REM portal, y PilotX no pintaba. Vale igual para el GPS/AutoSteer por WiFi
REM y para el CoreX-ECU: todo eso entra por UDP 9999.
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

REM ── 1. URL ACL para que CoreX pueda escuchar en 0.0.0.0:8080 ────────────
echo [1/9] Registrando URL ACL para puerto 8080...
netsh http delete urlacl url=http://+:8080/ >nul 2>&1
netsh http add urlacl url=http://+:8080/ user=Everyone
if %errorLevel% neq 0 (
    echo   ERROR: no se pudo agregar URL ACL.
) else (
    echo   OK
)

REM ── 2. Firewall: permitir puerto 8080 (panel web/PWA) ──────────────────
echo.
echo [2/9] Abriendo firewall TCP 8080 (panel PWA)...
netsh advfirewall firewall delete rule name="AgIO NodePanel 8080" >nul 2>&1
netsh advfirewall firewall add rule name="AgIO NodePanel 8080" dir=in action=allow protocol=TCP localport=8080
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 3. Firewall: permitir puerto 1883 (broker MQTT) ────────────────────
echo.
echo [3/9] Abriendo firewall TCP 1883 (broker MQTT)...
netsh advfirewall firewall delete rule name="AgIO MQTT 1883" >nul 2>&1
netsh advfirewall firewall add rule name="AgIO MQTT 1883" dir=in action=allow protocol=TCP localport=1883
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 4. Firewall: permitir UDP 9999 (PGN de los modulos LAN) ────────────
REM    ESTE ES EL QUE FALTABA. Por aca entra TODO el PGN de los modulos que
REM    hablan por WiFi en vez de serie: GPS/NMEA, AutoSteer, CoreX-ECU y el
REM    switch de herramienta ToolX (PGN 253). El bridge escucha en
REM    0.0.0.0:9999 (UdpBridgeService.StartUdp); sin esta regla Windows
REM    descarta los datagramas y el sintoma es mudo: el nodo se ve "online"
REM    por MQTT y su dato nunca llega al motor.
echo.
echo [4/9] Abriendo firewall UDP 9999 (PGN de modulos: GPS, AutoSteer, CoreX-ECU, ToolX)...
netsh advfirewall firewall delete rule name="PilotX PGN UDP 9999" >nul 2>&1
netsh advfirewall firewall add rule name="PilotX PGN UDP 9999" dir=in action=allow protocol=UDP localport=9999
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 5. Firewall: permitir TCP 5180 (Hub / API del motor) ───────────────
REM    Es el AgpWebHost que sirve el Hub y /api/aog/*; lo abre el celular y
REM    lo usa PilotX.Desktop. El 8080 de arriba es el panel legacy de AgIO.
echo.
echo [5/9] Abriendo firewall TCP 5180 (Hub y API de PilotX)...
netsh advfirewall firewall delete rule name="PilotX Hub 5180" >nul 2>&1
netsh advfirewall firewall add rule name="PilotX Hub 5180" dir=in action=allow protocol=TCP localport=5180
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 6. Firewall: permitir TCP 5181 (panel CoreX integrado) ─────────────
echo.
echo [6/9] Abriendo firewall TCP 5181 (panel CoreX)...
netsh advfirewall firewall delete rule name="PilotX CoreX 5181" >nul 2>&1
netsh advfirewall firewall add rule name="PilotX CoreX 5181" dir=in action=allow protocol=TCP localport=5181
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)

REM ── 7. Firewall: permitir ICMP (ping) ──────────────────────────────────
echo.
echo [7/9] Permitiendo ICMP (ping entrante)...
netsh advfirewall firewall delete rule name="ICMP Allow Inbound" >nul 2>&1
netsh advfirewall firewall add rule name="ICMP Allow Inbound" protocol=icmpv4:8,any dir=in action=allow
if %errorLevel% neq 0 (echo   ERROR) else (echo   OK)


REM -- 8. Borrar las reglas de BLOQUEO que crea Windows solo ---------------
REM    Cuando una actualizacion reemplaza el .exe, Windows pregunta "Permitir
REM    el acceso?" y si nadie acepta crea una regla de BLOQUEO con el nombre
REM    del ejecutable (una por protocolo). El bloqueo LE GANA al permiso: con
REM    esa regla puesta, abrir el puerto 9999 no sirve de nada.
echo.
echo [8/9] Borrando reglas de bloqueo automaticas de Windows...
netsh advfirewall firewall delete rule name="PilotX.GuidanceEngine" >nul 2>&1
netsh advfirewall firewall delete rule name="PilotX.Desktop" >nul 2>&1
netsh advfirewall firewall delete rule name="PilotX.Bars.Host" >nul 2>&1
netsh advfirewall firewall delete rule name="BenchX" >nul 2>&1
echo   OK (si no habia ninguna, tambien esta bien)

REM -- 9. Permiso POR PROGRAMA: evita que Windows vuelva a preguntar -------
REM    Esta es la regla que hace que el problema NO se repita. Mientras exista
REM    un permiso explicito para el ejecutable, Windows no muestra el cartel y
REM    por lo tanto no crea el bloqueo. La regla es por RUTA, asi que sobrevive
REM    a que la actualizacion reemplace el archivo en el mismo lugar.
echo.
echo [9/9] Permitiendo los ejecutables de PilotX (entrada, todos los perfiles)...
set "PX=%~dp0"
netsh advfirewall firewall delete rule name="PilotX Engine IN" >nul 2>&1
netsh advfirewall firewall add rule name="PilotX Engine IN" dir=in action=allow enable=yes profile=any program="%PX%Engine\PilotX.GuidanceEngine.exe"
if %errorLevel% neq 0 (echo   ERROR Engine) else (echo   OK Engine)
netsh advfirewall firewall delete rule name="PilotX Desktop IN" >nul 2>&1
netsh advfirewall firewall add rule name="PilotX Desktop IN" dir=in action=allow enable=yes profile=any program="%PX%Desktop\PilotX.Desktop.exe"
if %errorLevel% neq 0 (echo   ERROR Desktop) else (echo   OK Desktop)

echo.
echo ============================================================
echo   Listo. Reinicia CoreX para que el panel quede en 0.0.0.0:8080
echo ============================================================
echo.
echo   Despues de reiniciar CoreX, deberias poder:
echo     - Ping a 192.168.5.10 desde el celular
echo     - Abrir http://192.168.5.10:8080 desde el celular
echo     - Los nodos ESP32 conectandose al broker en :1883
echo     - Los modulos por WiFi (GPS, AutoSteer, ToolX) mandando PGN a :9999
echo.
echo   OJO: si la red WiFi del tractor figura como PUBLICA en Windows, el
echo   firewall bloquea igual. Pasala a PRIVADA en Configuracion de red.
echo.
echo   Para verificar que no quedo ninguna regla de bloqueo:
echo     netsh advfirewall firewall show rule name="PilotX.GuidanceEngine"
echo   Tiene que decir que no coincide ninguna regla.
echo.
pause
