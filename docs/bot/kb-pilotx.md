# Base de conocimiento de PilotX — soporte técnico de dirección y guiado

Documento de referencia para el bot de soporte. Todo lo de acá sale del código real
de PilotX (panel de Dirección, DTOs de configuración, manual y ayuda de cabina) y de
los valores por defecto reales del `steer-config.json`. Está pensado para responderle
a un operario/maquinista arriba del tractor, por teléfono/chat, en criollo.

Convención de este documento: cada setting se nombra SIEMPRE con el **label EXACTO que
ve el operario en pantalla** + la `clave_json` (snake_case) entre paréntesis. Cuando el
número que se muestra en pantalla NO es el número crudo que viaja en el JSON, se aclara
la escala de display.

---

## 0. Cómo funciona la pantalla de Dirección (contexto para el bot)

El panel de **Dirección** (menú izquierdo › Dirección; ícono AutoSteerConf) es una
tarjeta flotante sobre el mapa, con 6 pestañas:

| Pestaña | Qué contiene |
|---|---|
| **Probar** | Manejo libre (free drive): mover el volante a mano, tractor PARADO, para probar motor y sensor. |
| **Sensor** | Qué sensor mide el ángulo de rueda (WAS/RTY o encoder), cuentas por grado, poner en cero, invertir sensor/motor. |
| **Fuerza** | PWM mínimo, PWM máximo y Ganancia P del motor de dirección. |
| **Guiado** | Modo PP/Stanley y sus ganancias, ángulo máximo, Ackerman, zona muerta, compensaciones. |
| **Módulo** | Placa de dirección: activación del piloto, eje del IMU, relés, sensor de corte al agarrar el volante. |
| **Pantalla** | Velocidades de guiado, barra de guiado (grosor, enganche, mirada, cm por luz). |

Arriba del panel hay tres números EN VIVO (2 Hz): **OBJ** (ángulo objetivo que pide el
piloto), **ACT** (ángulo real que lee el sensor) y **ERR** (diferencia). Sirven para
afinar: si con el tractor parado en Probar el ACT no llega al OBJ o se pasa, hay que
tocar Fuerza/Sensor.

Reglas de la pantalla que hay que conocer:
- El POST de config **NO es merge**: se reenvía el objeto entero. (Relevante para el
  cloud: cualquier cambio propuesto se aplica sobre la config completa vigente.)
- **Al cerrar el panel se GUARDA lo que estaba tocado** (no se descarta). Para tirar los
  cambios está "Descartar" (doble toque anti-roce).
- Cerrar el panel (o abrir Guías/Lote, que lo tapan) **apaga el manejo libre** si quedó
  prendido: nunca queda el volante bajo control manual sin nadie mirando.
- **Trampa de calibración WAS**: el "Poner en cero" usa el `counts_per_degree`
  PERSISTIDO, no el que está en pantalla sin guardar. Por eso, si cambiaste las cuentas
  por grado, PilotX guarda primero y recién ahí cera. Ver runbook de WAS.

---

## 1. Diccionario de settings de dirección/guiado

Formato de cada entrada:
`label de pantalla` (`clave_json`) — pestaña — rango [crudo] y escala de display —
default real — qué hace — síntoma que ataca.

### 1.1 Ganancias del guiado (auto-aplicables por el bot)

Estas son las claves de la **lista blanca**: el bot SÍ puede proponerlas como cambio de
config (`propuesta_config`). El operario las acepta/rechaza en pantalla.

**Pure Pursuit (PP)** — pestaña Guiado, visibles solo con modo PP activo:

- **Qué tan adelante mira** (`hold_look_ahead`) — GUIADO-PP — crudo 10..70, se muestra
  ÷10 en segundos (crudo 25 → "2,5 s"). Default 25 (2,5 s). Cuántos segundos adelante
  mira el modo suave para decidir el giro. Más = anda tranquilo, curvas amplias, más
  lento para volver a la línea; menos = se pega a la línea pero puede serpentear.
  Ataca: **viboreo/serpenteo en PP** (subir) y **entra/vuelve lento a la línea** (bajar).
- **Multiplicador por velocidad** (`look_ahead_mult`) — GUIADO-PP — crudo 5..60, se
  muestra ÷10 (crudo 6 → "0,6"). Default 6 (0,6). Cuánto crece la mirada al subir la
  velocidad. Ataca: **serpentea SOLO a alta velocidad** (subir).
- **Entrada a la línea** (`acquire_factor`) — GUIADO-PP — crudo 20..300, se muestra ÷100
  (crudo 75 → "0,75"). Default 75 (0,75). Qué tan agresivo entra a la guía desde lejos.
  Alto = entra derecho y rápido (puede pasarse); bajo = entra en curva suave y larga.
  Ataca: **entra muy brusco** (bajar) o **tarda una eternidad en agarrar la línea** (subir).
- **Integral (PP)** (`integral_pp`) — GUIADO-PP — 0..100 (es % → 0,00..1,00 de ganancia
  integral). Default 20. Corrige el error que queda pegado (viento, ladera, implemento
  que tira). Ataca: **siembra corrido siempre para el mismo lado con PP** (subir un poco).
  Demasiado alto = balanceo lento de un lado a otro.

**Stanley** — pestaña Guiado, visibles solo con modo Stanley activo:

- **Ganancia Stanley** (`stanley_gain`) — GUIADO-Stanley — crudo 1..40, se muestra ÷10
  (crudo 10 → "1,0"). Default 10 (1,0). Cuánto pesa la distancia a la línea en el modo
  firme. Alto = vuelve rápido pero se pone nervioso/zigzaguea; bajo = vuelve lento.
  Ataca: **viboreo/zigzagueo en Stanley** (bajar) o **vuelve lento a la línea** (subir).
- **Ganancia de rumbo** (`heading_error_gain`) — GUIADO-Stanley — crudo 1..15, se
  muestra ÷10 (crudo 10 → "1,0"). Default 10 (1,0). Cuánto pesa el error de rumbo
  (apuntar torcido). Ataca: **cruza la línea en ángulo en vez de enderezarse antes**
  (subir). Muy alto también aporta nerviosismo.
- **Integral (Stanley)** (`integral_stanley`) — GUIADO-Stanley — 0..100 (% → 0,00..1,00).
  Default 0. Igual que la integral de PP pero para el modo firme: mata el corrimiento
  constante. Ataca: **corrimiento fijo para un lado con Stanley** (subir un poco).

**Avanzado (aplica a los dos modos)** — pestaña Guiado › Avanzado:

- **Zona muerta de rumbo** (`dead_zone_heading`) — GUIADO/Avanzado — double 0..5°, paso
  0,1, se muestra tal cual en grados. Default 1. Errores de rumbo más chicos que esto se
  ignoran. Ataca: **zigzagueo fino/tembleque cuando ya está arriba de la línea** (subir).
- **Demora de zona muerta** (`dead_zone_delay`) — GUIADO/Avanzado — 1..50 ciclos.
  Default 1. Cuántos ciclos espera antes de aplicar la zona muerta.
- **Compensación en cabecera (U)** (`u_turn_comp`) — GUIADO/Avanzado — en la pantalla de
  PilotX se muestra directo, rango **2 a 20** (sin offset). Default 5. Cuánto anticipa el
  giro en la vuelta en U de la cabecera (más alto = anticipa más). Ataca:
  **el giro automático abre o muerde la pasada siguiente**.
- **Compensación de ladera** (`side_hill_comp`) — GUIADO/Avanzado — crudo 0..30, se
  muestra ÷100 en grados (crudo 5 → "0,05°"). Default 5 (0,05°). Usa el rolido del IMU
  para compensar la deriva cuesta abajo en laderas. 0 = apagado. Ataca: **en ladera el
  tractor se corre para abajo de la pendiente** (subir).

**Fuerza** — pestaña Fuerza:

- **Fuerza de corrección (Ganancia P)** (`proportional_gain`) — FUERZA — 0..200, paso 5.
  Default 33. Cuánto empuja el motor por cada grado de error. Alta: llega rápido pero se
  pasa y oscila; baja: anda dormido y deja error. Se afina mirando el salto 0↔5° de
  Probar. Ataca: **el volante oscila/vibra al centrar** (bajar) o **el piloto queda
  flojo, no llega a la línea** (subir). Es la ÚNICA ganancia de "Fuerza" que el bot puede
  auto-proponer.

**Pantalla / velocidades** — pestaña Pantalla:

- **Velocidad mínima** (`min_steer_speed`) — PANTALLA — double 0..10 km/h, paso 0,5.
  Default 1. Debajo de esto el piloto no engancha: evita volantazos casi parado. Ataca:
  **el piloto se desengancha o pega volantazos a muy baja velocidad**.
- **Velocidad máxima** (`max_steer_speed`) — PANTALLA — double 1..40 km/h, paso 1.
  Default 12. Arriba de esto el piloto se apaga solo, por seguridad. Ataca: **el piloto
  se corta al acelerar** (subir, con criterio).
- **Límite de funciones de guiado** (`guidance_speed_limit`) — PANTALLA — double 1..40
  km/h. Default 7. Techo general de las funciones de guiado, INCLUYE el manejo libre de
  Probar. Ataca: **el manejo libre se apaga solo apenas el tractor se mueve** (es una
  guarda de seguridad; subir solo si la máquina realmente trabaja a esa velocidad).
- **Distancia de enganche** (`snap_distance`) — PANTALLA — double, UI 1..100, unidad cm.
  Default persistido 499. A menos de esta distancia de la guía, el piloto la engancha de
  un salto. Ataca: **cuesta que agarre la línea de al lado** (subir).
- **Mirada de la barra** (`guidance_look_ahead`) — PANTALLA — double 0,1..5 s, paso 0,1.
  Default 1. Cuántos segundos adelante calcula la BARRA el desvío que muestra (es del
  display del banderillero, no del lazo de control). Ataca: **la barra/banderillero
  reacciona con retraso o adelantado**.

> Recordatorio de la lista blanca (las ÚNICAS claves que el bot puede poner en
> `cambios[].clave` de una `propuesta_config`, EXACTAS en snake_case):
> `proportional_gain`, `max_steer_speed`, `min_steer_speed`, `dead_zone_heading`,
> `dead_zone_delay`, `look_ahead_mult`, `hold_look_ahead`, `acquire_factor`,
> `integral_pp`, `stanley_gain`, `heading_error_gain`, `integral_stanley`,
> `side_hill_comp`, `u_turn_comp`, `snap_distance`, `guidance_look_ahead`,
> `guidance_speed_limit`.
> Cualquier otro ajuste (calibración, geometría, inversiones, modo PP/Stanley, PWM, IMU,
> RTK, secciones) el bot lo EXPLICA y guía el procedimiento a mano, pero NO lo propone
> como cambio auto-aplicable.

### 1.6 Tabla maestra de settings de dirección/guiado

"Auto" = está en la lista blanca (el bot puede proponerlo). "Crudo" = lo que viaja en el
JSON; "Display" = lo que ve el operario en pantalla. Todos los defaults son los reales del
`steer-config.json` que corre hoy.

| Label pantalla | clave_json | Pestaña | Crudo (min..max, paso) | Display | Default (crudo→display) | Auto |
|---|---|---|---|---|---|---|
| Fuerza de corrección (Ganancia P) | `proportional_gain` | Fuerza | 0..200, 5 | igual | 33 | ✔ |
| Mínima para mover (PWM mín) | `min_pwm` | Fuerza | 0..255, 1 | igual | 22 | ✗ |
| Máxima (PWM alto) | `high_steer_pwm` | Fuerza | 20..255, 5 | igual | 180 | ✗ |
| Sensor RTY / Encoder del motor | `conv_type` | Sensor | Single/Differential | — | Single | ✗ |
| Cuentas por grado | `counts_per_degree` | Sensor | 1..255, 1 | igual | 20 | ✗ |
| Ajuste fino del cero | `was_offset` | Sensor | −4000..4000, 20 | ÷cuentas/grado (grados) | −154 | ✗ |
| Invertir sensor (WAS) | `invert_was` | Sensor | bool | — | false | ✗ |
| Invertir motor | `invert_steer` | Sensor | bool | — | false | ✗ |
| Modo Pure Pursuit / Stanley | `stanley_pure` | Guiado | bool | — | false (PP) | ✗ |
| Qué tan adelante mira | `hold_look_ahead` | Guiado-PP | 10..70, 1 | ÷10 s | 25 → 2,5 s | ✔ |
| Multiplicador por velocidad | `look_ahead_mult` | Guiado-PP | 5..60, 1 | ÷10 | 6 → 0,6 | ✔ |
| Entrada a la línea | `acquire_factor` | Guiado-PP | 20..300, 1 | ÷100 | 75 → 0,75 | ✔ |
| Integral (PP) | `integral_pp` | Guiado-PP | 0..100, 1 | % (÷100) | 20 → 0,20 | ✔ |
| Ganancia Stanley | `stanley_gain` | Guiado-Stanley | 1..40, 1 | ÷10 | 10 → 1,0 | ✔ |
| Ganancia de rumbo | `heading_error_gain` | Guiado-Stanley | 1..15, 1 | ÷10 | 10 → 1,0 | ✔ |
| Integral (Stanley) | `integral_stanley` | Guiado-Stanley | 0..100, 1 | % (÷100) | 0 → 0,00 | ✔ |
| Ángulo máximo de giro | `max_steer_angle` | Guiado-General | 10..80, 1 | ° | 40 | ✗ |
| Ackerman | `ackerman` | Guiado-General | 1..200, 1 | % | 100 | ✗ |
| Guiar en marcha atrás | `steer_in_reverse` | Guiado-General | bool | — | false | ✗ |
| Zona muerta de rumbo | `dead_zone_heading` | Guiado-Avanzado | 0..5, 0,1 (double) | ° | 1 | ✔ |
| Demora de zona muerta | `dead_zone_delay` | Guiado-Avanzado | 1..50, 1 | ciclos | 1 | ✔ |
| Compensación en cabecera (U) | `u_turn_comp` | Guiado-Avanzado | 2..20, 1 | directo | 5 | ✔ |
| Compensación de ladera | `side_hill_comp` | Guiado-Avanzado | 0..30, 1 | ÷100 ° | 5 → 0,05° | ✔ |
| Activación del piloto | `steer_enable` | Módulo | None/Switch/Button | — | None | ✗ |
| Eje del IMU | `imu_axis` | Módulo | X/Y | — | X | ✗ |
| Invertir relés | `invert_relays` | Módulo | bool | — | false | ✗ |
| Corte: encoder | `encoder` | Módulo | bool (excl.) | — | true | ✗ |
| Corte: presión | `pressure_sensor` | Módulo | bool (excl.) | — | false | ✗ |
| Corte: corriente | `current_sensor` | Módulo | bool (excl.) | — | false | ✗ |
| Cuentas máximas (encoder) | `max_counts` | Módulo | 1..255, 1 | igual | 15 | ✗ |
| Límite presión/corriente | `sensor_limit` | Módulo | 0..255, 1 | % del rango | 0 | ✗ |
| Velocidad mínima | `min_steer_speed` | Pantalla | 0..10, 0,5 (double) | km/h | 1 | ✔ |
| Velocidad máxima | `max_steer_speed` | Pantalla | 1..40, 1 (double) | km/h | 12 | ✔ |
| Límite de funciones de guiado | `guidance_speed_limit` | Pantalla | 1..40, 1 (double) | km/h | 7 | ✔ |
| Grosor de línea | `line_width` | Pantalla | 1..8, 1 | px | 1 | ✗ |
| Distancia de enganche | `snap_distance` | Pantalla | 1..100 (double) | cm | 499 (persistido) | ✔ |
| Mirada de la barra | `guidance_look_ahead` | Pantalla | 0,1..5, 0,1 (double) | s | 1 | ✔ |
| Centímetros por luz | `cm_per_pixel` | Pantalla | 2..20, 1 | cm | 3 | ✗ |

(✔ = auto-aplicable; ✗ = solo explicar/guiar a mano. `min_pwm` figura ✗: aunque es de
Fuerza, no está en la lista blanca — el bot explica cómo subirla de a 1 pero no la propone.)

### 1.7 Cómo convertir crudo ↔ display (para leer/proponer números)

La UI aplica: **display = (crudo + offset) × escala**. Al proponer un cambio, el bot habla
en el número de DISPLAY (lo que el operario ve), y así lo pone en `valor_nuevo`. Ejemplos:
- `hold_look_ahead`: escala 0,1. "Subir a 3,0 s" = crudo 30.
- `acquire_factor`: escala 0,01. "Poner 1,00" = crudo 100.
- `u_turn_comp`: directo (escala 1, sin offset). El operario ve 2..20 y así va en `valor_nuevo`.
- `side_hill_comp`: escala 0,01. "0,10°" = crudo 10.
- Los `*_speed`, `dead_zone_heading`, `snap_distance`, `guidance_look_ahead` son doubles
  reales (sin escala): el número de pantalla es el mismo que va al JSON.

---

## 2. Settings que el bot EXPLICA pero NO aplica

No están en la lista blanca. El bot guía el procedimiento manual; nunca los mete en una
`propuesta_config`.

### 2.1 Sensor de ángulo de rueda (WAS) y motor — pestaña Sensor

- **Sensor RTY (simple) / Encoder del motor** (`conv_type` = `"Single"` / `"Differential"`).
  RTY = sensor en el eje (modo simple, `Single`). Encoder del motor = cuenta vueltas del
  Keya (modo diferencial, `Differential`); andando a más de ~1,2 km/h se autocorrige
  contra el GPS. Es uno o el otro; al cambiar, poner en cero y recalibrar cuentas.
- **Cuentas por grado** (`counts_per_degree`) — 1..255. Default 20 (RTY típico 59). La
  escala del sensor: cuántas cuentas equivalen a 1° de rueda. Si el ángulo en pantalla
  exagera, subir; si se queda corto, bajar. En encoder se mide de tope a tope. Interfiere
  en TODO el guiado: mal escalado, el piloto gira de más o de menos.
- **Ajuste fino del cero** (`was_offset`) — crudo −4000..4000, paso 20, se muestra en
  grados (offset ÷ cuentas por grado). Default −154. Corre el cero de a poquito. Usalo
  cuando siembra SIEMPRE corrido para el mismo lado. Para el cero grueso está el botón
  "Poner en cero — con las ruedas derechas".
- **Invertir sensor (WAS)** (`invert_was`) — bool. Sensor invertido: girás a la derecha
  y el ángulo marca izquierda. En modo encoder, además apaga la corrección por GPS (banco).
- **Invertir motor** (`invert_steer`) — bool. Motor invertido: la flecha derecha mueve
  las ruedas a la izquierda. Con algo al revés el piloto empuja para el lado equivocado
  y se va al tope.

### 2.2 Fuerza del motor (PWM) — pestaña Fuerza

- **Mínima para mover (PWM mín)** (`min_pwm`) — 0..255. Default 22. Fuerza justa para que
  el motor ARRANQUE. Muy baja: se planta cerca del objetivo y no llega. Muy alta: tironea
  al centrar. (No está en la lista blanca: el bot explica cómo subirla de a 1, no la aplica.)
- **Máxima (PWM alto)** (`high_steer_pwm`) — 20..255, paso 5. Default 180. Tope de fuerza:
  limita qué tan violento gira el volante. El "low" del PGN se deriva como high/3.

### 2.3 Geometría y límites — pestaña Guiado › General

- **Ángulo máximo de giro** (`max_steer_angle`) — 10..80°. Default 40. Tope de giro que
  el piloto puede pedir. Ponerlo igual al tope físico real de las ruedas: más que eso, el
  motor empuja contra el tope mecánico.
- **Ackerman** (`ackerman`) — 1..200%. Default 100. Compensa que la rueda de adentro gira
  más que la de afuera. 100% = geometría ideal. Ajustar si midiendo el mismo giro a
  izquierda y derecha el ángulo difiere.
- **Guiar en marcha atrás** (`steer_in_reverse`) — bool. Default off. Permite guiar en
  reversa (maniobras de cabecera). El GPS detecta la reversa con menos certeza.

### 2.4 Geometría del vehículo — Configuración › Vehículo (otra pantalla, claves camelCase)

No se tocan desde el chat; el bot explica que se ajustan en Configuración › Vehículo:
- `wheelbase` (distancia entre ejes, m), `trackWidth` (trocha, m),
  `antennaHeight` (altura antena, m), `antennaPivot` (pivote eje trasero↔antena, + adelante),
  `antennaOffset` (offset lateral antena, + derecha), `maxSteerAngle` (°),
  `slowSpeedCutoff` (velocidad mínima para autosteer, km/h), `vehicleType`
  (0=Tractor, 1=Cosechadora, 2=Articulado).
- Herramienta (Configuración › Implemento): `width` (ancho total), `overlap` (solape),
  `offset` (offset lateral), `hitchLength`, secciones/zonas, y el **Timing** de secciones
  (`lookAheadOn`, `lookAheadOff`, `turnOffDelay`).
- Si las medidas del vehículo están mal (sobre todo wheelbase, offset de antena y ángulo
  máximo), el piloto NUNCA va a seguir bien la línea aunque las ganancias estén perfectas.

### 2.5 Módulo / placa de dirección — pestaña Módulo

- **Activación del piloto** (`steer_enable`) — `"None"` / `"Switch"` / `"Button"`.
  Default None. Cómo se prende el piloto desde el hardware: interruptor físico (cerrado =
  ON), botón (toggle) o solo desde pantalla.
- **Eje del IMU** (`imu_axis`) — `"X"` / `"Y"`. Default X. En qué eje quedó montada la
  placa del IMU. Si el rolido aparece como cabeceo (o al revés), cambiar el eje.
- **Invertir relés** (`invert_relays`) — bool. Invierte la lógica de los relés de sección
  (activo-alto ↔ activo-bajo). Solo si el corte de secciones anda al revés.
- **Corte al agarrar el volante** (uno solo, EXCLUYENTE): `encoder` / `pressure_sensor` /
  `current_sensor`. Default encoder=true. Es el sensor que APAGA el piloto cuando agarrás
  el volante. Encoder cuenta pulsos de giro; presión y corriente detectan el esfuerzo.
  Probarlo SIEMPRE antes de salir al lote.
  - **Cuentas máximas (encoder)** (`max_counts`) — 1..255. Default 15. Pulsos para cortar:
    menos = corta con un toque más suave.
  - **Límite presión/corriente** (`sensor_limit`) — crudo 0..255, se muestra en % del
    rango. Default 0. Umbral para cortar. Menos = más sensible al toque.

### 2.6 Modo de guiado — pestaña Guiado

- **Pure Pursuit / Stanley** (`stanley_pure`) — bool (`false` = PP suave, `true` = Stanley
  firme). Default false (PP). PP entra suave y perdona más el sensor de rueda; Stanley es
  más firme y preciso, pero más sensible a la calibración del WAS. **No está en la lista
  blanca**: el bot puede recomendar cambiar de modo y explicar cómo (pestaña Guiado,
  botón Pure Pursuit / Stanley), pero no lo manda como cambio auto-aplicable.

### 2.7 IMU / rumbo / RTK — Configuración › GPS/IMU (claves camelCase, otra pantalla)

El bot explica, no aplica:
- `headingSource` — `"Fix"` (GPS simple) o `"Dual"` (antena dual).
- `fusionGpsPercent` — peso del GPS en la fusión GPS/IMU (0..100%). Con antena simple el
  rumbo se apoya más en la IMU (fusión típica ~70/30).
- `dualHeadingOffset`, `dualReverseDistance` — para antena dual.
- `isReverseOn` — detección de reversa por IMU.
- `rollFilterPercent`, `invertRoll` — filtro y signo del rolido.
- `isRtkAlarm` (alarma al perder RTK) e `isRtkKillAutosteer` (cortar autosteer al perder
  RTK). **OJO: hoy estos dos NO se persisten todavía** — si el operario los prende, al
  reiniciar pueden volver a su estado anterior. No prometer que quedan guardados.
- `fixJumpAlarmDistance` — alarma de salto de fix (m, 0 = off).

---

## 3. Runbooks de troubleshooting

Cada runbook: síntoma → diagnóstico → pasos, citando el setting exacto. El bot pregunta
primero el MODO (PP o Stanley) y el estado del GPS/telemetría antes de proponer números.

### 3.1 Viborea / serpentea / zigzaguea

Primero preguntar: ¿a qué velocidad?, ¿modo PP o Stanley?, ¿fix RTK o señal libre?

- **En Stanley**: casi siempre `stanley_gain` muy alta. Bajarla de a 0,2–0,3 (crudo −2/−3)
  hasta que se calme. Si además cruza en ángulo, no tocar `heading_error_gain` para arriba.
- **En PP**: subir `hold_look_ahead` (mira más adelante, anda más tranquilo). Si serpentea
  SOLO a alta velocidad, subir `look_ahead_mult`.
- **Tembleque fino ya arriba de la línea** (cualquier modo): subir `dead_zone_heading` de
  a 0,1–0,2°.
- **Oscila el volante al centrar, con el tractor parado en Probar**: es Fuerza, no guiado.
  Bajar `proportional_gain` de a 5. Si oscila lento y amplio: puede ser integral alta
  (`integral_pp` / `integral_stanley`) → bajarla.
- Descartar causa física: GPS ruidoso (señal libre/float da saltos), WAS mal calibrado,
  presión de dirección baja.

### 3.2 No engancha el piloto (botón gris o no agarra)

- **Botón gris**: faltan las 3 condiciones → GPS con señal + lote abierto + guía elegida
  (o contorno activo). Preguntar cuál falta.
- **Engancha pero no sigue la línea**: verificar que la guía activa sea la de la pasada
  real (a veces quedó otra). Revisar medidas del vehículo (wheelbase, offset antena,
  `max_steer_angle`). Si viene muy cruzado, enderezar a mano unos metros y reenganchar.
- **No engancha a baja velocidad**: `min_steer_speed` demasiado alta → bajarla.
- **Se corta al acelerar**: `max_steer_speed` baja para esa máquina → subirla con criterio.
- **Mover el volante corta el guiado**: es la salida de emergencia de siempre, es normal.

### 3.3 Se va para un lado / siembra corrido (offset constante)

- Si es SIEMPRE el mismo lado y parejo: primero calibración de cero del WAS (ver 3.5).
- Ajuste fino sin recalibrar todo: `was_offset` (Ajuste fino del cero) de a pasitos hacia
  el lado que corrige.
- Si el WAS está bien y aun así corre por viento/ladera/implemento: subir la integral del
  modo activo (`integral_pp` o `integral_stanley`) de a poco. En ladera, `side_hill_comp`.
- Revisar `antennaOffset` (offset lateral de antena) en Configuración › Vehículo: un offset
  mal cargado siembra corrido siempre igual.

### 3.4 Se va al tope / invertido

- Síntoma: al enganchar (o en Probar con la flecha) las ruedas se van al tope y no frenan.
- Causa típica: **sensor o motor invertido**. En pestaña Sensor: girá el volante a la
  derecha y mirá el vúmetro — si la barra va a la izquierda, prendé `invert_was`. Si en
  Probar la flecha "+1°" (derecha) mueve las ruedas a la izquierda, prendé `invert_steer`.
- Con algo al revés el lazo empuja para el lado equivocado y clava el tope. Probar SIEMPRE
  con el tractor parado en Probar antes de salir.

### 3.5 Calibrar el sensor de ángulo (WAS) — poner en cero

Procedimiento (pestaña Sensor):
1. Poné las ruedas **bien derechas** (mirando la máquina, no la pantalla).
2. Si cambiaste **Cuentas por grado** (`counts_per_degree`), **guardá primero**. TRAMPA
   CONOCIDA: el "Poner en cero" usa el `counts_per_degree` PERSISTIDO, no el de pantalla;
   por eso PilotX guarda antes de cerar. Si el guardado falla, NO cera (mejor no cerar que
   cerar con la escala vieja y sembrar corrido toda la jornada).
3. Tocá **"Poner en cero — con las ruedas derechas"**.
4. Si sale **"el ángulo es excesivo, revisá el montaje"** (error `fuera-de-rango`,
   >±3900 cuentas): el sensor está mal montado/desalineado, no es un cero. Revisar montaje.
- El vúmetro es SOLO LECTURA (no se cera tocándolo; hay botón explícito para evitar que un
  roce con guante clave un cero corrido).

### 3.6 "Excessive steer angle" (ángulo excesivo)

- Aparece al poner en cero el WAS cuando el corrimiento supera ±3900 cuentas
  (`fuera-de-rango`). No es un cero válido: el sensor está mal montado o desalineado.
- No insistir con el cero: revisar el montaje mecánico del WAS/encoder y que las ruedas
  estén realmente derechas. Recién ahí volver a cerar.

### 3.7 Manejo libre (free drive / Probar) no prende o se apaga solo

Motivos que informa la pantalla:
- **"el tractor está andando"** (`velocidad`): el free drive solo prende con el tractor
  PARADO.
- **"PilotX no informa velocidad"** (`sin-velocidad`): no hay dato de velocidad del GPS.
- **"Se apagó solo: superó el límite de velocidad"**: arrancó el tractor. Es la seguridad.
  El techo lo pone `guidance_speed_limit`.
- **"Sin módulo de dirección conectado"** (`service-unavailable`): la ECU/placa no responde.
- Uso normal: Prender habilita las flechas; cada toque corre el objetivo 1° y el motor va
  hasta ahí y FRENA. "0↔5°" salta el objetivo para ver la respuesta. Si en vez de frenar se
  va al tope → sensor o motor invertido (3.4).

### 3.8 No va derecho sin RTK (señal libre / float / single)

- Sin RTK fijo el rumbo se apoya más en la IMU (fusión típica ~70/30) y el GPS da algo de
  salto/deriva. Se puede sembrar igual (señal libre = naranja = modo normal de Spark), pero
  esperá algo menos de precisión.
- Ayudan: modo **PP** (perdona más el ruido que Stanley), subir un poco `hold_look_ahead`,
  y que la IMU esté bien montada/calibrada (eje correcto en `imu_axis`).
- Si el tractor "cabecea" o el rumbo se va: revisar montaje/calibración de la IMU y el
  `imu_axis`.
- No forzar Stanley con GPS ruidoso: amplifica el zigzagueo.

### 3.9 Colores del GPS y por dónde entra (CoreX)

Arriba a la izquierda, el color del GPS dice la señal:

| Color | Estado | Significa |
|---|---|---|
| Verde | RTK FIJO | Corrección RTK enganchada, precisión de centímetros. |
| Naranja | GPS / DGPS / RTK FLOAT | **Señal libre**, modo normal de trabajo en Spark. Se siembra perfecto, NO es error. |
| Gris | SIMULADOR | Posición del simulador (BenchX), solo taller. |
| Rojo | SIN FIX | La antena todavía no tiene señal. Esperar a cielo abierto. |

- **Solo el rojo impide trabajar**: sin posición el piloto no engancha y varios botones
  quedan grises. Con naranja se trabaja normal; el verde es el plus de precisión.
- **Por dónde entra el GPS**: menú › **CoreX**. La antena entra por cable (serial: elegir
  puerto COM y velocidad en CoreX › Serial) o por red. En **CoreX › GPS** se ve lo que
  entra en vivo: si ahí no llega nada, el problema es de la antena/cable, no de PilotX.
- Códigos NMEA de fix (los que trae la telemetría del contexto): 0=sin fix, 1=single (sin
  corrección), 2=DGPS, 4=RTK fix, 5=RTK float.

### 3.10 Las secciones no cortan solas

- Tienen que estar en **Automático** (en Manual quedan siempre abiertas).
- Si no cortan en la cabecera: **Secciones controladas en cabecera** debe estar en **Sí**.
- Anti-solape viene activado: no vuelve a sembrar lo ya pintado (verde en el mapa).
- Si abren/cierran a destiempo: es el **Timing** (Implemento › Timing): Encendido (s),
  Apagado (s), Retardo de apagado (s). Son segundos × velocidad = distancia. Si queda un
  pedazo sin sembrar al entrar, subir el **Encendido**. Apagado y Retardo son excluyentes,
  y el Apagado no puede superar 0,8 × el Encendido.
- Si el corte de relés va al revés: `invert_relays` (pestaña Módulo).

### 3.11 Un nodo (ESP32) no aparece

- Los nodos (VistaX/QuantiX/SectionX/FlowX/StormX/ToolX/LineX) **se anuncian solos** por la
  red de la máquina; no se agregan a mano.
- Si falta uno: revisar que tenga corriente y esté en la red del tractor. Los nodos NO
  tienen internet propio: hablan por **MQTT en la LAN** contra el **broker embebido en el
  Engine de PilotX** (puerto 1883). Si "no aparece": WiFi del nodo, IP del broker
  configurada en el portal del propio nodo, y que el Engine esté corriendo.
- En **Configuración › Módulos › Nodos** se ve el último momento en que se lo escuchó
  (Pendientes / Aceptados / Off-line / Ignorados).
- Error relacionado: `AGP-MQTT-…` (problema hablando con los nodos).

### 3.12 Recetas rápidas de ajuste (valores concretos)

Puntos de partida sensatos; siempre validar en el lote y mover de a poco. El bot propone
UN cambio por vez cuando puede, para que el operario vea el efecto antes de seguir.

- **PP serpentea a velocidad de trabajo (8–12 km/h)**: `hold_look_ahead` de 2,5 → 3,0–3,5 s
  (crudo 30–35). Si empeora al acelerar además: `look_ahead_mult` de 0,6 → 0,8–1,0.
- **PP entra/vuelve muy lento a la línea**: `acquire_factor` de 0,75 → 1,0–1,2 (crudo
  100–120); si sigue perezoso, bajar `hold_look_ahead` de a 0,2.
- **Stanley zigzaguea**: `stanley_gain` de 1,0 → 0,7–0,8 (crudo 7–8). Nervioso pero además
  cruza en ángulo: dejar `heading_error_gain` en 1,0, no subirla.
- **Stanley cruza en diagonal y recién ahí endereza**: `heading_error_gain` de 1,0 → 1,2
  (crudo 12).
- **Corrimiento fijo para un lado, WAS ya calibrado**: integral del modo activo de a 5–10
  puntos (`integral_pp` o `integral_stanley` de 20 → 30). Si aparece un balanceo lento,
  te pasaste, bajala.
- **Tembleque fino ya sobre la línea**: `dead_zone_heading` de 1,0 → 1,2–1,5°.
- **Volante oscila al centrar (visto en Probar, tractor parado)**: `proportional_gain` de a
  −5 (33 → 28 → 23) hasta que el ACT llegue al OBJ sin pasarse.
- **Piloto flojo, no llega a la línea**: `proportional_gain` de a +5. Si además se planta
  cerca del objetivo: es `min_pwm` (subir de a 1, a mano).
- **U de cabecera abierta**: subí `u_turn_comp` de a 2–3 (de 5 hacia 7–8). U que muerde
  la pasada: bajalo de a 2–3 (de 5 hacia 3–2). Rango 2..20, se ve directo (default 5).
- **Se corre cuesta abajo en ladera**: `side_hill_comp` de 0,05 → 0,10–0,15° (crudo 10–15),
  requiere IMU con rolido válido.

### 3.13 Cómo leer la telemetría del contexto

El sistema le pasa al bot la última telemetría del equipo (si es de <15 min). Interpretación:
- `fix`: 4 = RTK fijo (óptimo), 5 = RTK float, 2 = DGPS, 1 = single, 0 = sin fix. Con 0–1–5
  esperá saltos y XTE grande; si el operario se queja de precisión y el fix no es 4, la
  causa raíz es la señal, no las ganancias.
- `xte` (cross-track error): distancia a la línea. Si es grande y estable → offset (ver
  3.3); si oscila → viboreo (ver 3.1).
- `steer_angle`: ángulo real del WAS. Si no se mueve con el volante o marca al revés →
  sensor (3.4/3.5).
- `autosteer`: enganchado/suelto. `speed`: velocidad. `field`: lote abierto.
- No inventar valores que no estén en el contexto; si falta un dato clave, preguntar.

---

## 4. Códigos de error

PilotX muestra un código corto junto al mensaje (para reportarlo por teléfono):

| Prefijo | Qué significa |
|---|---|
| `AGP-MQTT-…` | Problema hablando con los nodos (red MQTT de la máquina). |
| `AGP-NET-…` | Problema de red o de internet (nube/OrbitX). |
| `AGP-SYS-…` | Problema interno de la pantalla/Engine. Ej.: `AGP-SYS-009` al no poder cerar el WAS o completar una acción de dirección. |

- El detalle técnico está debajo del mensaje, desplegando **Detalles**.
- Versión y estado del equipo: **Configuración › Mantenimiento › Sistema** (incluye la IP
  de la pantalla, sección Red, y estado de batería).
- Registro de lo que fue pasando: **Configuración › Mantenimiento › Eventos**.
- WiFi de la pantalla: **Configuración › Mantenimiento › Red WiFi** (sin salir de PilotX).

---

## 5. Operación básica (una jornada)

Orden normal de trabajo (ayuda de cabina):
1. Esperar el **GPS** con señal (verde RTK, o naranja señal libre; rojo = esperar).
2. **Abrir el lote** (o crearlo en la posición actual si es la primera vez).
3. **Elegir o crear la guía**: AB Line (recta: tocar A, manejar derecho, tocar B) o
   AB+Curva (grabando el recorrido). Corregir con Izq/Centrar/Der si quedó corrida.
4. Si hace falta, marcar el **Lindero** (grabar manejando el perímetro) y construir la
   **Cabecera** (franja del borde para girar; necesita lindero).
5. Poner las **secciones en Automático**.
6. Enganchar el **Piloto** (botón se pone verde). Con **Giro** activado, gira solo en la
   cabecera. Mover el volante desengancha.
7. Al terminar, **cerrar el lote**: guarda todo y lo manda a OrbitX si está configurado.

Reglas de piloto: guía la dirección, el operario sigue a cargo. Nunca dejar la máquina
sola ni bajarse con el piloto enganchado. El trabajo se guarda aunque no haya internet;
la nube es respaldo que sincroniza cuando hay señal.

### 5.1 Leer el desvío: Banderillero vs Piloto

Menú izquierdo › Pantalla › Banderillero. Dos formas de leer cuánto te fuiste de la línea
(el modo es cómo LEÉS el desvío, no si usás el piloto — el piloto engancha igual en los dos):
- **Banderillero**: barra de luces. Se prenden **del lado al que tenés que ir** (no del lado
  al que te fuiste). Cuántas luces = cuánto te corriste. Todas apagadas + la del medio verde
  = estás en la línea. Colores de menos a más desvío: verde → amarillo → naranja → rojo.
  Abajo, la flecha y los centímetros exactos. La sensibilidad de cada luz la da
  `cm_per_pixel` (Centímetros por luz): menos cm = barra más sensible.
- **Piloto**: el recuadro **A LA LÍNEA** con los centímetros en grande.
- Lo elegido queda puesto hasta cambiarlo. Sin guía elegida no aparece nada (no hay línea
  contra la cual medir).

### 5.2 Qué se ve en el mapa

- Punto **celeste**: la antena GPS, sobre el tractor.
- Punto **naranja**: hacia dónde apunta el piloto. Si va sobre la línea, el guiado trabaja bien.
- **Verde**: lo ya trabajado (el pintado). Se limpia en LOTE › Borrar pintado (solo con
  secciones apagadas).

### 5.3 Otros módulos y pantallas (referencia rápida)

- **Timing de secciones** (Implemento › Timing): tres valores en segundos que PilotX pasa a
  distancia × velocidad. **Encendido (s)** = cuánto se adelanta para ABRIR; **Apagado (s)** =
  cuánto se adelanta para CERRAR; **Retardo de apagado (s)** = cuánto ESPERA antes de cerrar.
  Encendido ≈ tiempo desde que arranca el dosificador hasta que la semilla toca el suelo.
  Queda pedazo sin sembrar al entrar → subir Encendido. Tira antes de la línea → bajarlo.
  Apagado y Retardo son excluyentes; Apagado ≤ 0,8 × Encendido (lo fuerza la pantalla).
- **Secciones** (Configuración › Secciones): Individuales (≤16, ancho propio) o Simétricas/
  zonas (≤64 iguales en 2..8 zonas). Al cambiar la CANTIDAD se pisan todos los anchos con el
  valor por defecto: primero cantidad, después corregir uno por uno. El ancho total lo usa
  el guiado.
- **QuantiX** (dosificación de siembra): un motor por vez (pestañas M0/M1…). **Motor
  conectado**: destildá los canales sin motor cableado, si no el PID satura en PWM máximo y
  llena el overlay de ceros. Guardar + **Enviar a nodos** para que llegue al nodo. Síntoma
  clásico: "un motor marca PWM al máximo y no mueve nada" = canal sin motor → destildar.
- **VistaX** (monitoreo de siembra): muestra si cae semilla y a qué ritmo, surco por surco.
  La config del implemento acá es solo lectura (sale de la central). Puede haber más sensores
  que surcos (ej. 28 sensores en 14 surcos).
- **Nodos** (Configuración › Módulos › Nodos): aparecen solos al anunciarse; no se agregan a
  mano. Estados: Pendientes / Aceptados / Off-line / Ignorados.
- **OrbitX / nube** (Configuración › Cloud): sincroniza lotes y datos. El **código de
  dispositivo es único por pantalla**: si se clona una instalación sin cambiarlo, dos equipos
  aparecen como el mismo y se pisan. La dirección del servidor es fija (solo para verificar).
- **Firmware por USB** (Cloud › Firmwares › Flashear por USB): para un nodo recién soldado
  (virgen) o "ladrillado" que no levanta la red. Cable USB → elegir COM (si no aparece,
  "Instalar driver USB") → producto y versión → modo Completo (chip nuevo, requiere factory)
  o Solo app → Flashear. El flasheo por LAN/MQTT normal es para nodos que ya están en la red.
- **Simulador** (BenchX, `Build\BenchX\BenchX.exe`): simula el GPS para probar en taller;
  el GPS se ve gris. Herramienta de banco, no de trabajo.

---

## 7. Dosificación: QuantiX y la calibración del PID

> Esta sección se agregó el 2026-09-19. Hasta ese día la KB era solo de dirección
> y guiado, y a un operario que preguntaba por la dosis el bot no tenía con qué
> contestarle.

### 7.1 Qué es QuantiX, en criollo

**QuantiX** es el control de los motores que dosifican. Cada motor mueve un
dosificador (semilla o fertilizante) y PilotX le pide vueltas según la velocidad
y la dosis objetivo, para que la aplicación sea pareja aunque el tractor cambie
de velocidad.

Los motores son nodos ESP32 en la red de la máquina. Un rig grande puede tener
**14 motores**. Cada uno se identifica por el **UID del nodo** más su número de
motor dentro de ese nodo: el número solo NO alcanza, porque "motor 0" existe una
vez por cada nodo.

**Unidades que ve el operario:** kg/ha, sem/m, sem/ha y rpm. **Nunca hablarle de
pps** (pulsos por segundo): es interno del firmware.

### 7.2 Las tres preguntas de la dosificación

Cuando el operario dice "no dosifica bien", hay que separar tres cosas distintas
antes de tocar nada:

1. **¿La dosis PROMEDIO está bien?** Se arregla con la calibración del
   dosificador (semillas por vuelta, dientes del encoder, gramos por pulso). No
   es el PID.
2. **¿Las rpm son ESTABLES?** Si suben y bajan con la velocidad pareja, ahí sí es
   el PID. Ver 7.3.
3. **¿Las semillas quedan PAREJAS en el surco?** Eso es uniformidad de siembra y
   **hoy PilotX no la mide**. No prometer que la pantalla lo puede decir: haría
   falta que el nodo VistaX mande el tiempo entre semilla y semilla, que todavía
   no manda. Si el operario pregunta por esto, ser honesto.

### 7.3 Gráfico de PID — cómo se calibra un motor

**Dónde:** Menú de la izquierda › **Herramientas** › **Gráfico de PID**.

**Cómo se usa:** el operario arranca, tira unos **100 metros parejos en recta** y
para. La corrida se cierra sola a los 4 segundos de quietud y la pantalla muestra
el diagnóstico de ese motor.

En el gráfico hay tres curvas: **rpm real**, **rpm pedida** y la **velocidad del
motor**. Más un número de **carga del motor** en %.

**La regla de lectura, que es lo más importante de toda esta sección:**

| Lo que se ve | Qué significa | Qué hacer |
|---|---|---|
| Velocidad pareja + rpm oscilando alrededor de la pedida | El PID persigue el objetivo y se pasa | Bajar **Ki** |
| Rpm planchadas por debajo de la pedida, sin oscilar | Falta empuje | Subir **Ki**, o **Kp** si la diferencia es grande |
| **Carga en 100%** | El motor está contra el techo de fuerza | **NO tocar ganancias.** Es mecánico: cadena tensa, rodamiento, o dosis demasiado alta para ese motor |
| La velocidad del motor se movió mucho | Fue una curva | **No se puede diagnosticar.** Repetir la prueba en recta |

**Por qué la velocidad del motor y no la del tractor:** en una curva, la sección
externa va más rápido que la interna, así que a ese motor se le pide MÁS aunque
el GPS marque velocidad pareja. Culpar al PID ahí es el error clásico. Por eso la
pantalla grafica la velocidad de ese motor, no la del tractor.

**El botón Aplicar:** cuando hay una recomendación, el botón muestra de cuánto a
cuánto (por ejemplo `KI 30 → 21`). Se aplica **con el motor parado** — si está
girando, la pantalla lo rechaza a propósito, porque cambiar el PID en caliente le
pega un tirón al dosificador en pleno lote.

**Los ajustes son graduales** (20-30%) y **de a un parámetro por vez**. Si el bot
sugiere un salto grande, está mal: un salto arregla el síntoma y desarma un motor
que andaba.

### 7.4 El registro que queda

Cada corrida deja un archivo por motor con la curva completa a 5 veces por
segundo, más un `sesion.json` con **las ganancias que tenía ese motor en esa
corrida**. Eso es lo que permite comparar una prueba contra otra: si se empeoró,
se vuelve atrás mirando el archivo anterior.

Se guardan las últimas 100 corridas. Las que el operario marcó no se borran nunca.

Si el operario dice que un motor anda distinto de los otros, el `sesion.json`
trae un resumen por motor (desvío de rpm, error medio, carga promedio, % de
tiempo saturado): ahí se ve de un vistazo cuál se porta mal sin abrir los 14
archivos.

### 7.5 Qué NO puede hacer el bot con la dosificación

- **No puede cambiar ganancias a distancia.** Las acciones remotas que mueven la
  máquina están apagadas: falta la confirmación del operario en pantalla. El bot
  guía, el operario toca.
- **No inventar un valor de Kp o Ki.** Los números salen de la medición que hace
  la pantalla, que mide oscilación, error y saturación. Si no hay corrida, no hay
  número: pedirle al operario que tire los 100 metros y mire el gráfico.
- **No prometer uniformidad de siembra** (ver 7.2, punto 3).

---

## 8. Recordatorios finales para el bot

- Antes de proponer números, entender el síntoma y el **modo activo** (PP vs Stanley): las
  ganancias que ataca cada modo son distintas.
- Usar el CONTEXTO DEL EQUIPO (config actual, versión, telemetría de fix/XTE/steer_angle)
  para dar valores concretos a ESTE equipo; no inventar valores que no estén en el contexto.
- Cambios de config = SIEMPRE `propuesta_config` con `cambios[].clave` EXACTA de la lista
  blanca del punto 1.6. El operario acepta/rechaza en pantalla; el bot nunca "ya lo cambié".
- Calibración (WAS, cuentas por grado, Ackerman), geometría, inversiones, PWM, modo
  PP/Stanley, IMU/RTK y secciones: se EXPLICAN y se guía el procedimiento a mano, NO se
  auto-aplican.
- `isRtkAlarm` / `isRtkKillAutosteer` no se persisten todavía: no prometer que quedan
  guardados.
- **Dosificación:** antes de hablar de PID, descartar que sea calibración del
  dosificador o techo mecánico (sección 7.2). Y si el motor está al 100% de
  carga, ninguna ganancia lo arregla.
- **Uniformidad de siembra:** PilotX no la mide hoy. Decirlo, no estimarlo.
