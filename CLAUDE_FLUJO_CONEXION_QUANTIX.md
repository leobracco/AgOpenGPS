# Flujo de conexión AOG ↔ QuantiX node — referencia de desarrollo

> Doc interno. **No** se sirve al operario. Vive en la raíz de AOG junto a los
> otros `CLAUDE_*.md`. Sirve para diagnosticar por qué un nodo “se cae”,
> “responde pero no recibe” o “se ve en la lista pero está muerto”.

Contexto disparador: incidente reportado el 2026-05-22 — ambos nodos QuantiX
perdieron conexión, ambos reiniciaron, y sólo uno volvió a responder. Esta
doc apunta a entender qué procesos intervienen y qué señales mirar (no logs
crudos, sino estado observable en la UI y en MQTT) para llegar a la causa raíz.

---

## 1 · Las 3 capas que tienen que estar arriba

Para ver dosis en pantalla, las tres capas siguientes tienen que estar OK,
**en este orden**. Si cualquiera se cae, las de arriba se caen en cascada.

| Capa | Quién la maneja | Falla típica |
|---|---|---|
| **WiFi STA** | firmware (`Network.cpp`) | SSID/pass mal · señal débil · router caído |
| **MQTT connect al broker** | firmware (`MQTT_Custom.cpp::mqttReconnect`) | broker IP mal · AgIO con MQTT off · ClientID duplicado |
| **Tráfico target↔status** | bridge AOG (`QuantiXMotorBridge`) ↔ firmware callback | bridge `IsRunning=false` · watchdog HW del nodo · suscripciones perdidas |

Detalle por capa:

### 1.1 WiFi STA — firmware

Boot del nodo (`Begin.cpp` lines ~152–211 en QuantiX, replicado en
`vistax-node/Network.cpp`):

```
WiFi.persistent(false);
WiFi.disconnect(true);
WiFi.mode(WIFI_OFF);
delay(500);

if (use_station && ssid[0] != 0) {
    WiFi.mode(WIFI_STA);
    WiFi.setHostname(uid);
    WiFi.begin(ssid, pass);
    // hasta 15 s de espera
}
if (!connected) {
    WiFi.mode(WIFI_AP);
    WiFi.softAP(uid, ap_password);   // AP puro, no AP_STA
    soloAP = true;
}
```

- Creds WiFi viven en LittleFS (`/network.json`), **no** en NVS — para evitar
  el viejo bug de WiFiManager pisando configs.
- `use_station=false` por defecto en nodo virgen → arranca directo en AP a
  `192.168.4.1`.
- AP SSID = el UID directo (`QX-XXXXXXXXXXXX` / `VX-XXXXXXXXXXXX`), sin prefijo
  de producto.

### 1.2 MQTT connect — firmware

`mqttReconnect()` en `Quantix2Motors/src/MQTT_Custom.cpp`:

- Broker IP desde `MDLnetwork.Broker0..3`; fallback hardcodeado
  `192.168.1.12:1883` si los 4 octetos están en 0.
- `client.connect(uid)` — el **ClientID es el UID**. Si dos nodos comparten
  UID, el broker tira al recién entrado.
- Tras `connected()` OK:
  - Suscribe a `agp/quantix/<UID>/{config,target,cmd,test,cal,debug,sections}`.
  - Publica **retained** en `agp/quantix/<UID>/announcement`
    `{uid, ip, version, hw, device:"QuantiX"}`.
  - Publica también `agp/quantix/announcement` (legacy, no retained).
- Fallo: reintenta cada 5 s. **10 fallos secuenciales → AP fallback**
  (`WIFI_AP_STA`) con grace period de **5 min mínimo** en AP, y sólo se
  desmonta el AP si `WiFi.softAPgetStationNum() == 0`.

### 1.3 Tráfico target ↔ status

#### PC → nodo (target loop)

`QuantiXMotorBridge.OnTick` cada **200 ms**:

```
foreach motor in config:
    target = dosis_kg_ha * 1000 * ancho_m * vel_m_s / 10000 / meterCal
    publish agp/quantix/<UID>/target {id, pps:target, seccion_on}
```

- Reload config cada 2 s.
- `seccion_on=false` → corte inmediato en firmware (no espera dosis 0).
- Si la PC deja de publicar **> 2 s** → watchdog en firmware baja PWM a 0.

#### Nodo → PC (status loop)

`sendMQTTStatus()` en firmware cada ~100 ms, **un mensaje por motor**:

```
publish agp/quantix/<UID>/status_live
        {id, rpm, pulsos, pwm, isr_total, isr_filtered, ppr,
         pulse_min, motor_type, pps_real, pps_target,
         meter_cal, calibrando, load_pct}
```

Bridge en AOG: `OnStatusReceived` parsea, guarda `pps_real` en dict
keyed por `uid-motorIdx`, actualiza “última señal”, dispara push a UI.

---

## 2 · Procesos clave en cada lado

### Firmware

| Proceso | Frecuencia | Responsabilidad |
|---|---|---|
| `mqttLoop()` | cada `loop()` | WiFi check **por tiempo** (1 s tick), MQTT reconnect cada 5 s, `client.loop()`. Contadores de fallos por tiempo, no por iteración. |
| `mqttReconnect()` | 5 s si caído | `client.connect(uid)` + re-suscripción + republish announcement retained. 10 fails → AP fallback. |
| `mqttCallback()` | onMsg | despacha `/config`, `/target`, `/cmd`, `/test`, `/cal`, `/debug`, `/sections` a sus handlers. |
| `sendMQTTStatus()` | ~100 ms | publica status_live por motor. |

### PC / AOG

| Proceso | Frecuencia | Responsabilidad |
|---|---|---|
| `QuantiXMotorBridge.OnTick` | 200 ms | publica target por motor. **Sólo corre si `IsRunning=true`**. |
| `QuantiXMotorBridge.OnStatusReceived` | event-driven | parsea status_live, actualiza dict ppsReal y last-seen. |
| `NodoRegistryService` | event-driven | suscripto a wildcard `agp/+/+/announcement`. Mantiene tabla de descubrimiento que alimenta `nodos.html`. |
| Broker MQTT en AgIO/CoreX | always-on | MQTTnet en proc, puerto 1883. Lifecycle en `AgIO/.../Forms/MQTT.Designer.cs` (`Start/StopMqttBroker`) integrado en `FormLoop.cs`. |

---

## 3 · Matriz de estados intermedios

Esta es la tabla que más vale en diagnóstico: las combinaciones que producen
“nodo medio prendido”.

| WiFi | MQTT | Target llega al nodo | Status llega a PC | Estado real | Señal en UI |
|---|---|---|---|---|---|
| ❌ off | — | — | — | **Apagado / fuera de LAN** | `Nodos`: Off-line (announcement retenido viejo). PilotX overlay en gris. |
| AP fallback | — | — | — | **Modo configuración** | No aparece online. SSID `QX-XXXX…` visible desde celu. Fix: ir a `192.168.4.1`. |
| ✅ | ❌ | — | — | **WiFi sí, broker no** | Diag MQTT: reintentos crecientes. `Nodos`: última señal congelada. Causa: broker AgIO apagado o IP mal en nodo. |
| ✅ | ✅ | ❌ | ✅ | **Nodo vivo, bridge dormido** | QuantiX page: RPM real pero `Target pps=0`. Causa: `Bridge.IsRunning=false`, motor desactivado en config, o sección no activa. |
| ✅ | ✅ | ✅ | ❌ | **Recibe, no responde** | QuantiX page: target sube, RPM congelado o `—`. Última señal > 3 s. Causa: watchdog HW, **colisión de ClientID**, o nodo trabado. |
| ✅ | ✅ | ✅ | ✅ | **OK pleno** | `Nodos`: Aceptado verde. QuantiX page actualizándose ~10 Hz. PilotX overlay chip verde. |

---

## 4 · Timeline de una conexión sana

```
t=0.0s   [QX-A1B2…] boot, lee /network.json
t=0.2s   [QX-A1B2…] WiFi.begin("Tractor", ******)
t=1.4s   [QX-A1B2…] STA conectado · IP=192.168.5.41
t=1.5s   [QX-A1B2…] client.connect(QX-A1B2…) → [broker] aceptado
t=1.5s   [QX-A1B2…] subscribe agp/quantix/QX-A1B2…/{config,target,cmd,test,cal,debug,sections}
t=1.6s   [QX-A1B2…] publish RETAINED agp/quantix/QX-A1B2…/announcement
t=1.6s   [broker]   → entrega retained a suscriptores activos
t=1.6s   [AOG/Hub]  NodoRegistryService: nuevo nodo QX-A1B2… (pendiente)
t=1.7s   [QX-A1B2…] publish agp/quantix/QX-A1B2…/status_live #0 {rpm:0, pwm:0, …}
t=1.7s   [AOG/Br]   OnStatusReceived → ppsReal["QX-A1B2…-0"]=0
t=1.8s   [AOG/Br]   OnTick: publish agp/quantix/QX-A1B2…/target {id:0, pps:0, seccion_on:false}
…
t=12.3s  [AOG]      operario activa siembra → seccion_on=true, pps=215
t=12.3s  [QX-A1B2…] callback target #0 → TargetUPM=215, FlowEnabled=true
t=12.5s  [QX-A1B2…] status_live #0 {rpm:1840, pwm:163, pps_real:208, calibrando:false}
t=12.5s  [AOG/UI]   overlay PilotX → motor 0 verde, RPM 1840
```

---

## 5 · Diagnóstico del incidente 2026-05-22

**Síntoma:** ambos nodos cayeron, ambos reiniciaron, sólo uno respondió.

Tres causas que producen exactamente este patrón, en orden de probabilidad:

### 5.1 AP fallback “pegajoso” en el silencioso

Si MQTT falló 10 veces seguidas (típico: AgIO reiniciado, broker tirado por
unos segundos), firmware entra en `WIFI_AP_STA` con grace period 5 min.
Aunque el WiFi vuelva, el nodo se queda en AP hasta que pasen **5 min Y**
`softAPgetStationNum()==0`. Si tu celu se conectó al AP a chequear, lo estás
manteniendo arriba sin querer.

**Cómo confirmar:**
- El nodo no aparece online en `Nodos`.
- Buscás redes desde el celu y ves `QX-XXXX…` o `VX-XXXX…`.

**Fix inmediato:** conectarse al AP, ir a `192.168.4.1`, verificar
SSID/broker, reiniciar.

### 5.2 Announcement retenido fantasma

El nodo “vivo” reconectó y republicó announcement retained → fresco.
El nodo “muerto” sigue teniendo su announcement viejo retenido en el broker,
entonces **aparece** en `Nodos` con su última info pero su `status_live`
nunca más llega.

**Cómo confirmar:**
- En `Nodos`: fila presente, columna “Última señal” crece (3 s, 10 s, 30 s…).
- En QuantiX page: motor con valores viejos, ningún update.

**Fix inmediato:** publicar `agp/quantix/<UID>/announcement` vacío retenido
para limpiar (o esperar a que el nodo real reaparezca y republique).

### 5.3 Colisión de ClientID

Si por bug de flasheo dos nodos quedan con el mismo UID
(p.ej. ambos con MAC default, o UID hardcodeado de prueba que sobrevivió),
el broker MQTT acepta sólo un ClientID por sesión y **tira al recién entrado
cada vez que reconecta**. Resultado: los dos turnándose, parece que “sólo
uno responde”.

**Cómo confirmar:**
- En `Diag MQTT` del Hub: ciclos de reconnect cada pocos segundos.
- Mirar el UID físico en cada nodo: deberían ser distintos
  (`QX-` + 12 hex de la MAC).

**Fix:** reflashear el nodo con MAC duplicada para forzar regenerar UID, o
confirmar que `obtenerUID()` está leyendo MAC real y no un fallback.

---

## 6 · Checklist de diagnóstico (orden estricto)

1. **Broker vivo.** `Hub → Nodos → Diagnóstico MQTT` → debe decir
   *conectado*. Si no, problema en AgIO/CoreX, no en los nodos.
2. **Lista de nodos.** ¿Cuántos UIDs distintos? Confirmá que no haya dos
   iguales (colisión de ClientID).
3. **Última señal por nodo.** Si crece para uno y se mantiene para otro,
   tenés un fantasma retenido.
4. **WiFi del celu.** Si ves `QX-XXXX…` / `VX-XXXX…` en redes disponibles,
   ese nodo está en AP fallback. Conectarse y reconfig.
5. **Alimentación / cableado físico.** Si no aparece SSID ni anuncio
   retenido fresco, chequear que esté energizado.

---

## 7 · Dónde mira cada estado la UI actual

| Quiero saber… | Ir a | Señal concreta |
|---|---|---|
| ¿Broker vivo? | `Hub → Nodos → Diagnóstico MQTT` | Pill verde *conectado* + intentos / última OK. |
| ¿Nodo descubierto? | `Hub → Nodos` (Pendientes/Aceptados) | Fila con UID + tipo *QuantiX*. |
| ¿Hace cuánto no habla? | `Hub → Nodos`, col. “Última señal” | < 3 s sano · > 10 s sospechoso · congelado = muerto. |
| ¿Bridge tira target? | `Hub → QuantiX`, card por motor | “Target pps” se mueve con velocidad + sección activa. |
| ¿Motor responde? | `Hub → QuantiX`, card por motor | RPM + PWM + Carga update ~10 Hz. |
| ¿Calibrando? | `Hub → QuantiX`, badge | Chip ámbar “calibrando”. |
| Vista operativa | `PilotX`, overlay QuantiX (toggle barra) | Por motor: verde=OK, ámbar=sin target, rojo=sin respuesta > 3 s. |

---

## 8 · Lecciones / TODO para reducir falsos positivos

Cosas que la UI **no** muestra hoy y vendría bien:

- **Edad del announcement retenido vs. último `status_live`** lado a lado en
  `Nodos`. Si difieren > 30 s, marcar el nodo como “fantasma”.
- **Contador de reconnects del lado broker** (sesiones por UID en la última
  hora). 5+ = ClientID colisionando o AP fallback en loop.
- **Botón “Forget retained”** por UID en `Nodos` que publique
  `agp/<prod>/<UID>/announcement` vacío con flag retain, para limpiar
  fantasmas a demanda.
- **Heartbeat del nodo silencioso al AP del PC**: si el nodo cae en AP
  fallback, no hay forma de saberlo desde AOG. Posible: hacer un scan WiFi
  periódico desde una NIC dedicada del PC y reportar SSIDs `QX-/VX-` vistos.

---

## Referencias rápidas

- Firmware QuantiX: `G:\AgroParallel\Productos\AGP-VR\Software\Firmware_Embebido\Quantix2Motors\src\MQTT_Custom.cpp` (lifecycle MQTT)
- Firmware QuantiX: `…\Quantix2Motors\src\Begin.cpp` líneas ~152–211 (flujo WiFi STA/AP)
- Firmware VistaX: `…\vistax-node\src\Network.cpp` (mismo flujo, replicado 1:1)
- Bridge AOG: `…\AgOpenGPS\SourceCode\GPS\AgroParallel\QuantiX\QuantiXMotorBridge.cs`
- Registry Hub: `…\AgOpenGPS\SourceCode\AgroParallel\Services\NodoRegistryService.cs`
- Broker AgIO: `…\AgOpenGPS\SourceCode\AgIO\Source\Forms\MQTT.Designer.cs` (`StartMqttBroker`/`StopMqttBroker`)
