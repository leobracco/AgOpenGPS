# Registro de PID de QuantiX — diseño

**Fecha:** 2026-09-19
**Estado:** aprobado, pendiente de plan de implementación
**Motivo:** calibración de los 14 motores en el campo de LAS GRINGAS

## El problema

Hoy los motores de QuantiX dosifican bien en promedio, pero no se puede saber
*por qué* un motor se porta distinto de otro. El único registro que deja
QuantiX es `qx_bridge.log`: texto plano de diagnóstico, sin GPS, que se pisa.
Al cloud sube únicamente la configuración (`quantiX.json`, `motores.json`,
`OrbitXSync.cs:354`), ningún dato de trabajo.

La pregunta concreta que hay que poder contestar en el lote es: **si la
velocidad se mantiene estable y las rpm del motor suben y bajan, hay que
corregir el PID.** Sin registro, eso se diagnostica de memoria y mirando el
tablero, que es como se viene haciendo.

## Qué hay ya, y qué falta

Todo el dato necesario **ya llega y ya está en memoria en cada tick**. No hace
falta tocar el firmware.

El nodo publica `agp/quantix/{uid}/status_live` cada 200 ms
(`Send.cpp:6`, `SendTime = 200`) y el bridge tickea a los mismos 200 ms
(`QuantiXMotorBridge.cs:159`). **5 Hz es el techo real** sin cambiar firmware.

`MotorLive` (lo que se parsea hoy) trae `PpsTarget`, `PpsReal`, `Pwm`, `Rpm`,
`Pulsos`. El nodo además manda `load_pct`, `isr_total`, `isr_filtered`, `ppr`,
`pulse_min` y `meter_cal` (`MQTT_Custom.cpp:792` `sendMQTTStatus`), que hoy se
descartan.

`load_pct` (% de PWM sobre 4095) es imprescindible para este diagnóstico: si el
PWM está clavado al 100%, el motor **no puede** seguir al target y el problema
no se arregla con Kp — es techo mecánico. Sin ese dato se toquetean ganancias
para arreglar algo que no es de ganancias.

Del lado de PilotX, el mismo tick ya calculó `pps`, `rpmTarget`,
`velMotorKmh` y `dosisEfectiva`.

Falta: persistirlo con tiempo, identificarlo bien y poder mirarlo.

## Alcance

- Graba **los 14 motores siempre**, sin filtro. Un motor quieto mientras los
  otros giran también es dato: lo que se busca es justamente **comparar
  motores entre sí** — el que tiene más tensión mecánica tira más PWM para la
  misma rpm.
- Grabación **automática**, sin botón que la habilite. El botón solo **marca**.
- Panel **en vivo** sobre el mapa, leyendo del mismo buffer que se escribe.
- Retención por **cantidad**: las últimas 20 sesiones. Las marcadas no se
  borran.

Fuera de alcance (se agrega después sin rehacer nada si en el campo hace
falta): selector de "grabar solo estos motores", sync del registro a OrbitX,
análisis de espaciamiento semilla a semilla (eso necesita cambio de firmware en
VistaX y es otro proyecto).

## Arquitectura

```
nodo ESP32 ──status_live 200ms──► NodoRegistry ──► MotorLive{rpm,pwm,load_pct,pps_real}
                                                        │
estado PilotX (vel GPS, dosis) ──────────────────┐      │
                                                 ▼      ▼
                              QuantiXMotorBridge.OnTick (200 ms)
                                                 │
                                    QxPidRecorder.Registrar(muestra)
                                                 │
                            ┌────────────────────┴────────────────────┐
                            ▼                                         ▼
                   cola en memoria                          buffer rodante (últimos N)
                            │                                         │
                   flush a CSV cada 1 s                    GET /api/quantix/graph-pid
                            │                                         │
                   <sesion>/<uid>_m<i>.csv                  GraficoQuantiXPanel
```

### Componentes

**1. `QxPidRecorder`** — `AgroParallel.Services/QuantiX/QxPidRecorder.cs` (nuevo)

Todo el registro, aislado. Recibe muestras, arma la sesión, escribe, rota,
marca, calcula el resumen al cerrar. No sabe de MQTT ni de GPS: se le pasa un
struct. Es la única pieza con lógica propia y la única que necesita tests.

Superficie:

```csharp
void Registrar(QxPidSample m);   // no bloquea nunca
void Marcar(string texto);       // clava la marca en los 14 a la vez
void AbrirSesion();
void CerrarSesion();             // escribe el resumen en sesion.json
```

**2. `MotorLive.LoadPct`** — `AgroParallel.Models/MotorLive.cs` + parseo en
`NodoRegistryService.cs`. Dos líneas: el campo ya viaja en el wire.

**3. Enganche en `QuantiXMotorBridge.OnTick`** — una llamada. Arma la muestra
con lo que el tick ya calculó. No se computa nada nuevo.

**4. `GraficoQuantiXPanel`** — `PilotX.UI/Views/`, sobre el molde de los cuatro
paneles que ya existen (Dirección, Rumbo, XTE, Corrección).

### La regla que no se negocia: el tick nunca espera al disco

El tick de 200 ms del bridge es el que **comanda los motores**. Un
`File.Append` sincrónico adentro del tick frena el comando del motor si el
pendrive se traba o Windows decide hacer algo raro — en el medio del lote.

Por eso `Registrar()` **solo copia la muestra a una cola en memoria y vuelve**.
Un timer aparte, cada 1 segundo, vacía la cola a disco. Si el disco falla, se
pierde el registro y **el motor sigue andando**. Ese es el orden de
prioridades.

(Nota aparte: el `Log()` actual del bridge, `QuantiXMotorBridge.cs:130`, sí hace
`File.AppendAllText` sincrónico. Es esporádico, no urgente, pero está en la
misma categoría de riesgo. No se toca en este trabajo.)

## Formato

### Un archivo por motor

```
2026-09-19_0830/
  sesion.json
  A4CF12AB9E30_m0.csv
  A4CF12AB9E30_m1.csv
  7B2E0091CC14_m0.csv
  ...
```

Un solo CSV con los 14 mezclados serían 70 filas por segundo que hay que
filtrar antes de ver la primera curva. Un archivo por motor resuelve tres cosas
a la vez:

1. **Identificación sin ambigüedad.** `mi` es el índice **dentro del nodo**
   (`QuantiXMotorBridge.cs:350`), así que "M0" existe una vez por nodo. Con el
   UID en el nombre del archivo se sabe exactamente qué motor configurar.
2. **Se grafica directo** en Excel o LibreOffice, sin filtrar.
3. **Pesa menos.** El UID no se repite en cada fila (12 bytes × 70 filas/s
   sería puro desperdicio); queda solo en el nombre y en el sidecar.

Ubicación: bajo `AgpPaths.ConfigRoot`, con el mismo patrón de partición
alternativa configurable que usa `VistaXFieldLogger` (`LogOutputDrive`).

### Columnas

```
t_s,rpm_real,rpm_target,pps_real,pps_target,pwm,load_pct,vel_motor,vel_gps,dosis,sec_on,marca
0.0,44,45,15.0,15.2,2050,50,6.20,6.20,7.0,1,
0.2,45,45,15.1,15.2,2048,50,6.21,6.20,7.0,1,
0.4,47,45,15.8,15.2,2210,54,6.19,6.20,7.0,1,kp=3
```

`t_s` es relativo al inicio de la sesión (el arranque absoluto va en el
sidecar), para graficarlo sin tocar nada.

**Las dos columnas de velocidad son el corazón del diseño.** `vel_gps` es la
del tractor; `vel_motor` es la de las secciones que ese motor cubre, que en
curva **no es la misma** (`MotorSpeedKmh`, `QuantiXMotorBridge.cs:630`): el
motor de la punta externa va más rápido que el de la interna. Registrando solo
la del GPS, en cada curva se verían las rpm moverse con velocidad "estable" y
se culparía al PID de algo que hizo bien — el target realmente cambió.

Con las dos columnas la pregunta se contesta sola: **si `vel_motor` está
planchada y `rpm_real` oscila alrededor de `rpm_target`, ahí sí es el PID.**

### `sesion.json`

Al abrir: fecha y hora absoluta, versión de PilotX, y por cada motor su
`Kp`/`Ki`/`Kd`, `PwmMin`/`PwmMax`, `Deadband`, `SlewRate`, `FFGain`, `Alpha`,
`MaxIntegral`, `TargetSlewHzPerSec`, `PIDTime`, `DientesEngranaje`,
`SemillasVuelta`, `MeterCal` (todo de `QxMotorConfig`) y la versión de firmware
del nodo.

Es lo que permite afirmar con certeza "esta curva salió con Kp=2" tres semanas
después.

### Resumen por motor, al cerrar

Nadie va a abrir 14 archivos para encontrar cuál se porta distinto. Al cerrar
la sesión se escribe en `sesion.json`:

```json
"motores": [
  { "uid": "A4CF12AB9E30", "m": 0, "nombre": "Cuerpo 1",
    "rpm_prom": 44.8, "rpm_desvio": 1.2,
    "error_medio_pct": 2.1, "load_prom_pct": 47, "tiempo_saturado_pct": 0 },
  { "uid": "A4CF12AB9E30", "m": 1, "nombre": "Cuerpo 2",
    "rpm_prom": 45.1, "rpm_desvio": 6.8,
    "error_medio_pct": 9.4, "load_prom_pct": 86, "tiempo_saturado_pct": 31 }
]
```

De un vistazo se ve que el cuerpo 2 tiene el triple de desvío y vive al 86% de
PWM con un tercio del tiempo saturado: **eso no se arregla con Kp**, es fuerza
mecánica de más. Recién ahí se abre *ese* CSV. Los otros 13 no se tocan.

## Ciclo de vida de una sesión

Una sesión **arranca** cuando arranca el bridge (al abrir PilotX) y **se corta
sola cada 1 hora de grabación**, abriendo la siguiente. También cierra al parar
el bridge.

El corte horario no es cosmético: sin él, una jornada de 8 horas sería una sola
sesión de ~56 MB, y veinte de esas serían **1,1 GB** — no los 140 MB de la
tabla de abajo. Con el corte, "20 sesiones" significa "las últimas 20 horas de
registro", que es un techo predecible y además la unidad con la que se
analiza. Es el mismo patrón de rotación que ya usa `DebugLogService.cs:334`.

Dentro de una sesión, las corridas se separan con las **marcas**, no con
archivos distintos.

## Volumen y retención

| Escenario | Volumen |
|---|---|
| Sembrando con los 14 motores | ~7 MB/h |
| Una sesión (1 h por definición) | ~7 MB |
| Techo con 20 sesiones | ~140 MB |

Retención **por cantidad**: se guardan las últimas 20 sesiones; al crear una
nueva se borra la más vieja. Una sesión **marcada queda protegida de la purga**
— si alguien se tomó el trabajo de clavar la marca, esa corrida importa.

Las sesiones marcadas no cuentan para el tope de 20, así que el techo real es
"140 MB más lo que se haya marcado a propósito". Es la única forma de que el
tope no termine borrando justo lo que se quiso guardar.

Se eligió por cantidad y no por antigüedad porque en una jornada de calibración
se generan muchas sesiones cortas, y lo que se quiere es "tengo las últimas 20
pruebas a mano", no "tengo dos semanas de algo". Por antigüedad se borraría la
prueba buena si pasan 15 días entre el campo y el análisis.

### La frecuencia no se toca

Bajar de 5 Hz a 1 Hz haría el archivo cinco veces más chico y arruinaría el
diagnóstico: un PID que oscila a 1,5 Hz muestreado a 1 Hz no aparece como
oscilación sino como una línea casi plana, o peor, como una onda lenta que no
existe (aliasing). Mentiría de forma convincente.

**Los 5 Hz son la resolución del diagnóstico; los motores son redundancia. Si
hay que achicar, se achican motores, nunca hertz.**

## Pantalla

`GraficoQuantiXPanel` sobre `GraficoLineal`, igual que los cuatro paneles
existentes. Tres series: **rpm real, rpm target y vel_motor**, con selector de
motor arriba (14 curvas juntas no se leen).

`MaxPuntos` es hoy `const int = 120` (`GraficoLineal.cs:85`), que a 5 Hz son 24
segundos — poco para ver un ciclo de oscilación lento. Se vuelve configurable
por instancia y el panel de QuantiX lo pone en 300 (60 s). Los otros cuatro
paneles quedan igual.

El **botón de marca** va en ese panel. Se aprieta después de cambiar un
parámetro y la marca queda en la columna `marca` de los 14 archivos **en el
mismo instante**, para alinear "acá toqué Kp" con lo que hicieron todos los
motores a la vez.

Endpoint: `GET /api/quantix/graph-pid`, servido por el WebHost desde el buffer
rodante del recorder, en snake_case vía `AgpJson` como todo el resto del wire.

## Errores

| Situación | Comportamiento |
|---|---|
| Disco lleno / pendrive desconectado | Se corta el registro, se avisa **una vez** en el log, **los motores siguen comandados** |
| Nodo deja de publicar | Fila con `rpm_real` **vacío**, no cero — cero significa "el motor está quieto", que es otra cosa |
| Sesión sin ninguna muestra | No deja carpeta huérfana |

## Tests

En `AgroParallel.Services.Tests`, junto a los de `QxPulseCalculator`:

- La rotación borra la vigésimo-primera sesión y **nunca** una marcada.
- Las columnas del CSV salen en orden y con el formato declarado.
- Un nodo caído escribe vacío, no cero.
- `Registrar()` no bloquea cuando el disco falla.
- El resumen por motor calcula desvío y `tiempo_saturado_pct` correctamente.

## Lo que este trabajo NO resuelve

El espaciamiento **centímetro a centímetro** entre semillas (CV de uniformidad,
fallas, dobles) sigue sin medirse. Ni QuantiX ni VistaX lo pueden calcular hoy:
VistaX recibe del nodo un `Spm` y un `count` **ya agregados por ventana**
(`SeedDataModels.cs:352,392`), sin los tiempos de cada semilla. No falta la
cuenta — **falta el dato**, y conseguirlo es cambio de firmware en el nodo
VistaX (que mande el delta en ms entre pulsos, o un histograma por ventana).

Este registro cierra la dosis y el comportamiento del motor. La uniformidad de
siembra es un proyecto aparte.
