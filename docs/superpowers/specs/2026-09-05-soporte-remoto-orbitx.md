# Soporte remoto PilotX ⇄ OrbitX — diseño

Fecha: 2026-09-05
Estado: aprobado (brainstorming), pendiente de plan e implementación

## Objetivo

Poder **diagnosticar y arreglar una pantalla en el campo sin depender de
RustDesk** ni de que haya alguien sentado en la cabina: ver logs, revisar red y
firewall, saber qué versión corre, reiniciar PilotX, y —cuando de verdad haga
falta— correr un comando puntual.

Hoy todo eso exige una sesión interactiva de RustDesk: alguien tiene que estar,
la conexión tiene que aguantar, y no queda registro de qué se tocó.

## REVISIÓN 2026-09-05 — el alcance es soporte **y telemetría de flota**

El diseño original (solo HTTP) quedaba corto: con telemetría continua de varias
máquinas, el polling no escala y no da estado en vivo. Se pasa a **MQTT como
canal primario, con HTTP como respaldo**.

**Viabilidad verificada en el droplet** (no supuesta): 3,8 GB de RAM con 1,8
libres, carga 0,35, puertos 1883/8883 libres, Mosquitto no instalado.
Mosquitto usa 10-20 MB: entra sin comprometer las ~25 apps que ya corren.

### Broker

**Mosquitto** en el droplet, **TLS en 8883**. NO confundir con el broker
embebido del Engine: ese es LOCAL del tractor y atiende a los ESP32. Acá PilotX
es **cliente** de un broker cloud distinto. Son dos cosas separadas y no se
mezclan.

**Autenticación**: usuario = `device_id`, contraseña = `device_token`, los que
ya existen en CouchDB. El `password_file` de Mosquitto se genera desde CouchDB
con un script y se recarga al dar de alta un equipo. **ACL por dispositivo**:
cada equipo sólo puede escribir en su propia rama y leer sus propios comandos —
sin esto, un cliente podría espiar o comandar las máquinas de otro.

### Tópicos

| Tópico | Sentido | Uso |
|---|---|---|
| `agp/flota/{device_id}/telemetria` | PC→cloud | posición, velocidad, labor, alarmas |
| `agp/flota/{device_id}/estado` | PC→cloud | online/offline por **LWT** |
| `agp/flota/{device_id}/cmd` | cloud→PC | comando de soporte (QoS 1) |
| `agp/flota/{device_id}/resultado` | PC→cloud | salida del comando (QoS 1) |

El **LWT** es la ganancia grande para la flota: el broker avisa solo cuando una
pantalla se cae, sin esperar a que falte un heartbeat.

### Telemetría

Cada **5 s** en movimiento, cada **60 s** parada (no tiene sentido repetir la
misma posición). Payload chico: posición, rumbo, velocidad, si está sembrando,
ha trabajadas, alarmas activas, versión. Del lado del cloud se persiste con los
**buckets por minuto** que ya se usan para tracking (60× menos documentos).

### HTTP no se tira: queda de respaldo

Los endpoints HTTP del diseño original **se implementan igual** y el agente cae
a polling si MQTT no conecta. Motivo concreto de campo: el 8883 saliente lo
bloquean algunas redes de terceros, y el soporte se necesita justo cuando algo
anda mal. HTTPS al 443 pasa siempre.

## Por qué contra OrbitX y no un puerto abierto

El canal es **SALIENTE**: la pantalla le pregunta al cloud si tiene algo
pendiente, igual que ya hace con el heartbeat, las prescripciones y el OTA.
Consecuencias, todas buenas:

- **No hay que abrir puertos ni tocar el firewall entrante** de la pantalla.
- Funciona detrás de NAT, de un router ajeno o de un módem 4G.
- Reusa la autenticación que ya existe (`X-Device-ID` + `X-Auth-Token`).
- Si la pantalla está apagada o sin señal, el comando queda encolado y se
  ejecuta cuando vuelve.

## Decisiones tomadas (brainstorming)

1. **Catálogo de acciones + shell bajo pedido.** El día a día se resuelve con
   acciones ya escritas y probadas, que no pueden romper nada. El shell libre
   existe pero está **apagado por defecto** y se habilita **por equipo** cuando
   hace falta.
2. **Contraseña de administrador igual en toda la flota** (decisión del
   usuario, advertencia dada). **El canal de soporte NO la usa**: se autentica
   con el token del equipo. Esa contraseña queda para acceso físico/RustDesk.
   No va en el repo ni en el ZIP de release.

## Arquitectura

```
   PANTALLA (tractor)                     CLOUD                    NOSOTROS
 ┌────────────────────────┐        ┌──────────────────┐      ┌──────────────┐
 │ PilotX                 │        │ OrbitX           │      │ Panel web    │
 │  SoporteRemotoService  │        │                  │      │  · elegir    │
 │   · poll cada 20 s ────┼───────▶│ GET  /api/soporte│◀─────┤    equipo    │
 │     (o al toque por    │  HTTPS │      /pendientes │ JWT  │  · elegir    │
 │      push del sync)    │ device │                  │      │    acción    │
 │   · ejecuta la acción  │  token │ POST /api/soporte│      │  · ver salida│
 │   · manda salida ──────┼───────▶│      /resultado  │─────▶│  · historial │
 │                        │        │                  │      └──────────────┘
 │ Catálogo (seguro)      │        │ CouchDB:         │
 │ Shell (off por equipo) │        │  soporte_cmd_*   │
 └────────────────────────┘        └──────────────────┘
```

## Catálogo de acciones (lo que cubre el 95% del soporte)

Cada acción es código nuestro, con su salida acotada. No reciben comandos: a lo
sumo parámetros validados (por ejemplo cuántas líneas de log).

| Acción | Qué devuelve |
|---|---|
| `estado` | versión de PilotX, uptime, si el Engine responde, perfil activo |
| `logs_pilotx` | últimas N líneas del log de eventos (N ≤ 500) |
| `logs_engine` | últimas N líneas de la salida del Engine |
| `sistema` | Windows, RAM libre, disco libre, CPU |
| `red` | IPs, gateway, DNS, si hay internet, si llega a OrbitX |
| `puertos` | quién escucha en 5180 / 1883 / 8888 (las trampas conocidas) |
| `firewall` | estado de los perfiles y las reglas de PilotX |
| `procesos` | si corren Desktop / Engine / BarsHost y desde qué ruta |
| `nodos` | nodos MQTT vistos y su última señal |
| `reiniciar_pilotx` | cierra y relanza PilotX (acción, no consulta) |
| `reiniciar_equipo` | reinicia Windows (acción, pide confirmación en el panel) |

Las dos últimas son **acciones**, no consultas: van marcadas distinto en el
panel y piden confirmación explícita.

## Shell libre (apagado por defecto)

- Se habilita **por equipo** desde el panel (`device.soporte_shell = true`),
  idealmente con vencimiento (ej. 24 h) para que no quede prendido para siempre.
- Con el flag apagado, el agente **rechaza** el comando y lo registra: el
  rechazo también queda auditado.
- Corre PowerShell **sin sesión interactiva**, con timeout y salida acotada.

## Límites y seguridad del agente

- **Un comando a la vez** por equipo.
- **Timeout** por comando (default 60 s, tope 300).
- **Salida acotada** (256 KB); si se pasa, se trunca y se avisa.
- **Nada de credenciales en la salida**: el agente filtra el token de OrbitX y
  el contenido de `orbitX.json` de cualquier respuesta.
- **Auditoría completa**: quién lo mandó (uid del panel), a qué equipo, qué
  acción, cuándo, resultado y salida. Queda en CouchDB.
- El agente **no acepta comandos de nadie más**: solo del `server_url` fijo y
  con su propio token.

## Modelo de datos (CouchDB)

`soporte_cmd_<ts>_<device_id>`:

```json
{
  "tipo": "soporte_cmd",
  "device_id": "VX-...",
  "estab_slug": "...",
  "accion": "logs_pilotx",
  "params": { "lineas": 200 },
  "estado": "pendiente | tomado | ok | error | rechazado",
  "pedido_por_uid": "usr_...",
  "creado_at": 0, "tomado_at": 0, "terminado_at": 0,
  "exit_code": 0,
  "salida": "…(truncada a 256 KB)…",
  "error": ""
}
```

## Endpoints

**Device (token del equipo):**
- `GET  /api/soporte/pendientes` → el comando más viejo en `pendiente`, y lo
  marca `tomado` (para que dos polls no lo ejecuten dos veces).
- `POST /api/soporte/resultado` → `{cmd_id, exit_code, salida, error}`.

**Panel (JWT, con guard de organización):**
- `POST /api/soporte/comando` → encola. Valida que la acción exista y que, si
  es `shell`, el equipo lo tenga habilitado.
- `GET  /api/soporte/historial?device_id=&limit=` → últimos comandos y salidas.
- `POST /api/soporte/shell` → habilita/deshabilita el shell de un equipo.

## Latencia

Poll cada **20 s** en reposo. Para que no se sienta lento durante una sesión de
soporte, el poll baja a **3 s** durante los 5 minutos siguientes a recibir un
comando (se "despierta" mientras estás trabajando y vuelve a dormirse solo).

## Sub-proyectos

- **S0 — Broker**: Mosquitto en el droplet, TLS 8883, password_file desde
  CouchDB, ACL por dispositivo, systemd. Infra, no código de producto.
- **S1 — OrbitX**: modelo, endpoints HTTP (respaldo), suscriptor MQTT que
  persiste telemetría y resultados, guard de organización, auditoría.
- **S2 — Agente PilotX**: cliente MQTT al cloud (+ fallback HTTP),
  `SoporteRemotoService` con el catálogo y sus límites, publicación de
  telemetría y LWT.
- **S3 — Panel web**: pantalla de soporte (equipo/acción/salida/historial) y
  vista de flota en vivo (mapa + estado online por LWT).
- **S4 — Shell**: flag por equipo con vencimiento + rechazo auditado.

**MVP = S0 + S1 + S2**: con eso ya hay soporte remoto y telemetría llegando,
aunque se consulte con curl. S3 lo hace usable; S4 al final.

**Orden de riesgo**: S0 toca el droplet de producción (25 apps corriendo) — se
hace con backup y verificando que nada más se caiga. El resto es código nuevo
que no pisa lo existente.

## Riesgos

- **El canal es poder real sobre la flota.** Todo el diseño se apoya en que
  OrbitX no esté comprometido; por eso el catálogo es el camino normal y el
  shell es la excepción con flag.
- **Comandos peligrosos** (reiniciar el equipo) durante una labor: el panel
  debe mostrar si la máquina está trabajando antes de mandarlos.
- **Salidas con datos sensibles**: el filtro de credenciales hay que
  mantenerlo cuando se agreguen acciones nuevas.
