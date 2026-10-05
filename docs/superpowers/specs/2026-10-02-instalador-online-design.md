# Instalador online de PilotX (aprobación desde OrbitX) — diseño

**Fecha:** 2026-10-02
**Estado:** aprobado para planificar
**Reemplaza (para pantallas remotas):** `Tools/provision-server` (instalador LAN, puerto 8090) + túnel AnyDesk/RustDesk

## 1. Problema

Hoy una pantalla nueva o que viene de una instalación anterior se instala con
el servidor LAN del taller (`Tools/provision-server`, `irm …:8090/instalar.ps1 | iex`).
Para pantallas en el campo hay que armar un túnel TCP de AnyDesk/RustDesk: es
frágil, depende de la PC de desarrollo prendida, y el "pedido" se arma a mano
antes. Además:

- El nombre del cliente viaja por shell y se rompe con acentos (caso
  "PEQUEÑOS TURPIALES", la Ñ quedó como U+FFFD en OrbitX).
- No hay herramienta para el caso Ethernet USB (red de nodos, sin salida) +
  WiFi (con salida): si el Ethernet tiene gateway, Windows manda internet por el
  cable y la pantalla queda sin nube. El helper `PilotXNetApply` fija IP y
  reparte métricas, pero no saca el gateway ni verifica salida real.

## 2. Objetivo

Un único `PilotX-Instalador.exe`, **igual para todos los clientes**, que se baja
de internet, se corre en la pantalla y:

1. respalda la instalación anterior sin perder lotes,
2. se vincula a OrbitX mostrando un código que **solo un superadmin acepta**,
3. baja de la nube su configuración y PilotX,
4. deja el equipo provisionado (usuarios, firewall, energía, red, runtimes,
   RustDesk, kiosko),
5. se autoverifica antes de activar el kiosko y reporta todo a OrbitX.

No lleva secretos: sin la aprobación en OrbitX el .exe no sirve para nada.

### Fuera de alcance

- Linux / tablets Android.
- Reemplazar el pairing que ya hace PilotX desde su menú (sigue igual).
- Retirar `Tools/provision-server`: queda para el taller hasta que el .exe esté
  validado en cabina.

## 3. Reglas de producto

- En la UI **nunca** se nombra el producto anterior: se dice "instalación
  anterior". Los nombres de carpetas/claves de registro que hay que leer quedan
  solo en el código.
- Castellano rioplatense, estilo visual de PilotX (claro, gris, táctil, botones
  grandes — skill `pilotx-ui-winforms`).
- Si el respaldo de datos falla, **no se instala nada**.
- El kiosko se activa **solo** si el self-test pasa.

## 4. Flujo en la pantalla

| # | Paso | Detalle | Si falla |
|---|---|---|---|
| 0 | Chequeo previo | Admin (manifest), Windows 10/11 x64, ≥ 2 GB libres en C:, reloj razonable (TLS) | Mensaje claro, no toca nada |
| 1 | Red | Diagnóstico y reparación (§6). Necesita salida a `orbitx.agroparallel.com` | Ofrece "Reparar red"; sin internet no sigue |
| 2 | Respaldo de la instalación anterior | Corre `Rescatar-AOG.ps1` embebido: lee la carpeta de trabajo del registro **antes** de que nada la borre, ZIP de Fields + Vehicles + config en `C:\Rescate-PilotX`. También detecta PilotX anterior en `C:\PilotX` y respalda su config y `Fields` | Aborta la instalación |
| 3 | Código de vinculación | `POST /api/devices/pair/init` con `origen:"instalador"` + resumen del equipo. Muestra 6 caracteres grandes y espera (polling `pair/status`, el código se renueva solo cada 10 min) | Reintenta; botón "Nuevo código" |
| 4 | Perfil de instalación | Con el token: `GET /api/devices/instalacion` → cliente, CUIT, versión, kiosko, clave `soporte`, clave RustDesk | Reintenta |
| 5 | Equipo | `Provision-Pantalla.ps1` embebido: limpieza, usuarios `pilotx`/`soporte`, energía, marca, red Privada + firewall, runtimes VC++ y WebView2, WinRM | Sigue y anota; los críticos (usuarios, firewall, runtimes) cuentan en el self-test |
| 6 | PilotX | Baja `GET /api/ota/firmware/PilotX/<ver>` con auth de equipo, verifica SHA-256 contra el catálogo, extrae en `C:\PilotX`, escribe `Engine\orbitX.json` (device_id, token, server), instala RustDesk y reporta su ID | Reintenta la descarga (retoma por rangos); aborta si el hash no coincide |
| 7 | Devolución de datos | Restaura lotes y vehículos del respaldo donde PilotX los lee | Aborta antes del kiosko |
| 8 | Self-test | §7 | No activa kiosko; informa |
| 9 | Kiosko + reinicio | `PilotX-KioskSetup.exe`, pide reiniciar | — |

**Retomar:** `C:\PilotX\instalador-estado.json` guarda el último paso completo,
el device_id y el token (cifrado con DPAPI LocalMachine). Volver a abrir el .exe
retoma; no pide código de nuevo si el token sigue valiendo.

**device_id:** mismo cálculo que `instalar.ps1` y PilotX (MD5 del MAC físico),
para que una reinstalación no duplique el equipo en OrbitX.

**Progreso:** cada paso hace `POST /api/devices/instalacion/progreso` (auth de
equipo) con paso, mensaje y estado; antes de tener token, el progreso queda en
el log local `C:\PilotX\instalador-log.txt`.

## 5. Lado OrbitX

### 5.1 Pairing con origen instalador

- `POST /api/devices/pair/init` acepta `origen: "instalador"` y un `resumen`
  (hostname, Windows, adaptadores, si hay instalación anterior y cuántos lotes).
- `POST /api/devices/pair/claim`: si el intent es `origen:"instalador"`,
  **requiere `rol_global === "superadmin"`** (403 para owners). El body suma
  `version` y `kiosko`.
- `GET /api/devices/pair/pendientes` (solo superadmin): lista intents vivos de
  origen instalador con su resumen, para el panel.

El pairing desde el menú de PilotX (sin `origen`) se comporta como hoy.

### 5.2 Perfil de instalación

`GET /api/devices/instalacion` (deviceAuth) devuelve:

```json
{ "cliente": "LOS PEQUEÑOS TURPIALES S.A.", "cuit": "30606933122",
  "estab_slug": "pequenos_turpiales_sa", "version": "1.0.87",
  "sha256": "…", "kiosko": true,
  "soporte_pass": "…", "rustdesk_pass": "…" }
```

- Cliente/CUIT salen del doc `org_<slug>` (suma campo `cuit` a la org).
- Las claves salen de la config global de superadmin (equivalente al
  `secretos.json` de hoy) y se entregan **una sola vez por instalación**: tras
  la primera lectura el doc `instalacion_<device_id>` marca `claves_entregadas`
  y las siguientes lecturas no las incluyen (el estado local las tiene).
- `POST /api/devices/instalacion/progreso` guarda los pasos (máx. 80) en
  `instalacion_<device_id>`; `POST /api/devices/instalacion/red` guarda el
  último diagnóstico de red.

### 5.3 Panel

- Tarjeta **"Pantallas esperando aprobación"** (solo superadmin): código,
  hostname, resumen, y formulario org + versión (default la última del OTA) +
  kiosko → Aprobar.
- En la ficha del dispositivo: avance de la instalación y último diagnóstico de
  red.
- Alta de org con CUIT (y edición de nombre/CUIT, que hoy no existe y hizo
  falta para corregir la Ñ).

### 5.4 Descarga del .exe

`GET /instalador` público, sirve el último `PilotX-Instalador.exe` (subido como
producto `PilotXInstalador` en `/firmwares`, mismo flujo que el resto del OTA).

## 6. Red PilotX — diagnóstico y reparación

Vive en el mismo .exe: como paso 1 del instalador y como modo suelto
(`PilotX-Instalador.exe --red`, botón "Revisar red") para pantallas ya
instaladas.

**Diagnóstico**, por adaptador físico Up (Ethernet USB, WiFi, celular):

- IP, prefijo, gateway, DNS, métrica de interfaz, DHCP/estático.
- Salida a internet **desde ese adaptador**: HTTP a
  `https://orbitx.agroparallel.com/health` con el socket ligado a la IP de
  origen del adaptador.
- Alcance LAN de nodos: conexión TCP al broker `:1883` local y barrido corto de
  la subred del Ethernet.
- Quién gana la ruta por defecto (`Find-NetRoute 0.0.0.0/0`).

**Diagnósticos con texto claro**, por ejemplo:
- "El cable USB se lleva el tráfico de internet y no tiene salida."
- "El WiFi está conectado pero sin internet."
- "Ningún adaptador sale a internet."

**Reparación** (botón, con antes/después):

1. Al Ethernet sin salida: quitar gateway por defecto (dejar IP/prefijo de la
   red de nodos; si es DHCP sin salida, pasar a estático con la misma IP).
2. Al adaptador con salida: métrica 10; resto 60 (`AutomaticMetric Disabled`).
3. DNS de respaldo en el adaptador con salida (1.1.1.1, 8.8.8.8) si los
   actuales no resuelven.
4. Re-probar y mostrar el resultado.

**Persistencia:** `NetApplyWatcher.ps1` (tarea SYSTEM `PilotXNetApply`) suma el
modo `reparar` con la misma lógica, y la re-evalúa al arranque y ante cambio de
red (`NetworkAddressChanged`), con anti-rebote de 30 s.

**Informe:** el diagnóstico (antes/después) va a OrbitX (§5.2), así se sabe qué
pantallas tuvieron el problema y por qué.

## 7. Self-test (antes del kiosko)

| Chequeo | Criterio |
|---|---|
| PilotX arranca | `GET http://127.0.0.1:5180/api/aog/state` responde en < 60 s |
| Broker MQTT | escucha en `:1883` |
| Firewall | reglas de PilotX presentes, ninguna de bloqueo |
| Usuarios | existen `pilotx` (sin clave) y `soporte` (admin) |
| Lotes | cantidad restaurada = cantidad del respaldo |
| OrbitX | heartbeat con el token nuevo = 200 y `asignado:true` |
| Red | sale a internet por el adaptador elegido; el Ethernet no tiene gateway si no tiene salida |
| RustDesk | servicio corriendo y ID reportado |

Resultado completo a OrbitX y en pantalla (verde/rojo por ítem).

## 8. Componentes

| Unidad | Responsabilidad | Depende de |
|---|---|---|
| `PilotX.Instalador` (WinForms .NET 4.8) | UI táctil, motor de pasos, estado/retomar | las de abajo |
| `Pasos/*` (una clase por paso, `IPaso { Ejecutar(ctx) }`) | lógica de cada paso | `ScriptRunner`, `OrbitXCliente` |
| `ScriptRunner` | extraer recursos `.ps1` a `C:\PilotX\kit` y correrlos capturando salida | — |
| `OrbitXCliente` | pair init/status, perfil, OTA, progreso, red | HTTP |
| `RedDiagnostico` / `RedReparacion` | §6, detrás de `IAdaptadores` para poder simularlo | WMI/NetTCPIP |
| `SelfTest` | §7 | `OrbitXCliente`, `RedDiagnostico` |
| OrbitX `routes/devices.js` + `routes/instalacion.js` | §5.1–5.2 | CouchDB |
| OrbitX panel | §5.3 | — |

Proyecto en `SourceCode/AgroParallel/Tools/PilotX.Instalador/`, junto a
`AgroParallel.Updater`. Recursos embebidos: `Provision-Pantalla.ps1`,
`Rescatar-AOG.ps1`, `NetApplyWatcher.ps1`. `PilotX-KioskSetup.exe` y branding
vienen dentro del paquete de PilotX (o como recurso si no están ahí).

## 9. Errores y seguridad

- Sin secretos en el .exe. El token del equipo solo se obtiene tras aprobación
  de un superadmin; el `device_secret` del pairing evita robo de token por
  quien vea el código.
- Claves de `soporte`/RustDesk: una sola entrega, guardadas localmente con DPAPI.
- Hash SHA-256 obligatorio del paquete antes de extraer.
- Todo texto de nombres viaja como JSON UTF-8 desde código (nunca por shell).
- Cada paso es idempotente: volver a correrlo no rompe (usuarios existentes,
  reglas de firewall `-Force`, etc.).

## 10. Pruebas

- **Unitarias C#** (proyecto `PilotX.Instalador.Tests`): motor de pasos
  (retomar desde estado, abortar si el respaldo falla, no kiosko si falla el
  self-test), cálculo de device_id igual a PilotX, `RedDiagnostico` con
  adaptadores simulados (cable con gateway sin salida → diagnóstico correcto;
  reparación produce las métricas/rutas esperadas).
- **OrbitX (Jest)**: claim de código `origen:instalador` → 403 para owner, OK
  para superadmin; perfil de instalación entrega claves una sola vez;
  pairing sin origen sigue igual.
- **Pester**: modo `reparar` de `NetApplyWatcher.ps1` con cmdlets mockeados.
- **Validación en cabina** (antes de dar por cerrado): una pantalla nueva, una
  con instalación anterior con lotes, y una con Ethernet USB + WiFi con el
  problema de gateway reproducido.
