# QuantiX — N motores eléctricos con dosis y corte por surco

Fecha: 2026-06-25
Estado: aprobado (diseño). Scope = UI + modelo + bridge. Firmware diferido.

## Problema

Hoy QuantiX asume **exactamente 2 motores fijos por nodo**, cada uno empujando
todo su ancho. El producto evoluciona a **N motores eléctricos** (24, escalando a
36/48) donde:

- Cada motor **dosifica** (PID sobre PPS) y **corta surco por surco**.
- Cada motor puede tener una **dosis independiente** (fija o desde mapa).
- Un motor maneja **1..N surcos** (asignación libre del operario).

La UI actual (`quantix.html`, "Control PID · 2 motores por nodo") y el modelo
(`QuantiXMotoresConfig` que siembra 2 motores en el constructor) no escalan a
esto. Hay que rediseñar UI + modelo + bridge para N motores.

## Decisiones de definición (del usuario)

- **Modelo físico motor↔surco:** Configurable. Un motor maneja 1..N surcos, el
  operario asigna libremente (opción C del brainstorm). No es 1:1 fijo ni bloques fijos.
- **Origen de dosis por motor:** Cascada **mapa manda, si no hay cae a fija**.
  (mapa = prescripción/shapefile; fija = valor editable por motor).
- **Granularidad de corte:** **a nivel motor**. Un motor frena cuando **todos**
  sus surcos están cortados. Si tiene un solo surco asignado y ese se corta, el
  motor frena. (Conserva el modelo "eje solidario" actual, pero por surcos del motor.)
- **Cantidad de motores:** hasta 24, escalando a 36/48.
- **Scope:** UI + modelo + bridge ahora. **Firmware se ve después** (con OK
  explícito para flashear).
- **Layout:** A (planter + lista) como vista principal, B (tabla densa) como
  alternativa para 36/48.

## Hallazgo clave (qué necesita firmware y qué no)

- **N motores en el modelo, asignación motor↔surco, cascada de dosis, corte a
  nivel motor, publicación MQTT por motor** → NO necesitan firmware nuevo. El
  bridge ya publica un JSON por motor a `agp/quantix/{uid}/target` con
  `{"id":N,"pps":...,"seccion_on":...}`. Hoy el loop va 0..1; pasarlo a 0..N es
  cambio de PC puro. El firmware ya direcciona motores por `id`.
- **El firmware actual maneja 2 motores físicos por nodo.** Para >2 motores reales
  por nodo (o N nodos de 1 motor) hay que definir el modelo de hardware. Eso es
  **Fase firmware diferida**. La UI/bridge ya quedan listos para emitir target por
  cualquier `id`; el firmware lo consumirá cuando se defina el fierro.

Decisión de scope: **esta fase = todo PC (sin flashear). Firmware = fase aparte.**

## Modelo de datos

### Cambio central: de array fijo a lista

`Services/QuantiX/QuantiXMotoresConfig.cs` hoy:

```csharp
Motores = new QxMotorConfig[] { new(), new() }; // 2 fijos
```

pasa a una **lista variable** sembrada vacía o con 1, y el operario agrega con
"+ Motor". Cada `QxMotorConfig` ya tiene los campos necesarios:
`nombre, dosis_fija, manual_mode, manual_dosis, campo_dosis, kp/ki/kd,
pwm_min/max, meter_cal, tren, cortes[], motor_type`.

- **Asignación motor↔surco** se modela con el `cortes[]` existente (lista de
  índices de surco asignados a ese motor). No se inventa estructura nueva: un
  "surco" es un corte/sección. Se respeta el invariante: un surco pertenece a
  **un solo** motor a la vez (al pintar con el pincel de otro motor, se quita del
  motor anterior).
- **Cantidad de surcos** (ancho de la sembradora) se deriva del máximo de cortes
  configurados / del implemento activo. La tira de surcos renderiza ese total.
- Persistencia: sigue en `quantiX_motores.json`. El DTO `QxMotorConfigDto` +
  `Motores[]` ya serializa lista de tamaño arbitrario; no hay cambio de esquema,
  solo deja de asumir length 2.

### Validaciones (en boundary de UI/servicio)

- Un surco no puede quedar sin motor **silenciosamente**: la UI muestra surcos
  sin asignar en gris-vacío y avisa antes de guardar si hay surcos huérfanos.
- Un surco no puede pertenecer a dos motores: pintar reasigna.
- Borrar un motor libera sus surcos (quedan sin asignar, visibles en gris).

## UI — `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/quantix.html`

### Objeto central: la tira de surcos (planter strip)

Un único componente que se usa **idéntico** en Configurar y En marcha. Render:
una fila de celdas (surcos) coloreadas por el color del motor dueño. Escala a 48
con wrap. Paleta de motores: c1..c6 cicladas (#4ABA3E, #7F6BE0, #E0A33E, #3E9BE0,
#E06B8B, #46C5B0) respetando el verde de marca para Motor 1.

### Estado "Configurar"

- **Pincel = motor activo.** Tocar un motor en la lista lo selecciona como pincel.
  Arrastrar el dedo por la tira pinta esos surcos con ese motor (táctil, sin teclado).
- **Toolbar:** `+ Motor` (accent verde), `Auto-repartir ▾` (1 motor/surco, o N
  grupos parejos), `Quitar surco` (deja huérfano el surco tocado).
- **Lista de motores:** color + nombre + surcos asignados (resumen "1,2,9,10") +
  **dosis fija editable** (input táctil, abre teclado HTML propio) + pastilla de
  **dosis efectiva** (`mapa 62.0` azul / `fija` ámbar).

### Estado "En marcha" (mismo diagrama)

- Surcos **cortados = gris** en vivo (`coff`).
- Caption con velocidad y ha (`9.2 km/h · 14.3 ha`).
- Lista de motores: objetivo, **PPS real / objetivo**, barra de cumplimiento,
  badge **OK / corte / desvío**. Desvío = barra ámbar cuando real se aparta del
  objetivo más de un umbral.

### Toggle Planter / Tabla

Segmented arriba a la derecha. **Tabla** = vista B densa, una fila por motor
(Motor, Surcos, Dosis fija, Efectiva, PPS, Estado). Para 36/48 motores donde el
planter se vuelve denso y el operario experto quiere velocidad.

### Reemplazo de tabs

La estructura actual de tabs (Monitor, Motores, Shape, PID, Calibración, Prueba)
se reorganiza: **Monitor** y **Motores** se fusionan en la tira unificada
(Configurar/En marcha es el mismo objeto). Shape, PID, Calibración, Prueba se
conservan como secciones (PID/Calibración por-motor siguen existiendo).

## Bridge — `Services/QuantiX/QuantiXMotorBridge.cs`

Cambio acotado: el loop hoy itera **0..1** (líneas ~239-337). Pasa a iterar
**0..Motores.Count-1**. Por cada motor:

1. Resolver dosis: **mapa (CampoDosis/shapefile) manda → si no hay, DosisFija**.
   (Hoy el orden es DosisFija → CampoDosis; se invierte para cumplir "mapa manda".)
2. Mapear `Cortes[]` del motor → estado de surcos/secciones.
3. **Eje solidario por surcos del motor:** el motor dosifica si **algún** surco
   suyo está activo; frena cuando **todos** sus surcos están cortados.
4. Calcular PPS y publicar un JSON por motor a `agp/quantix/{uid}/target`:
   `{"id":<motorIndex>,"pps":...,"seccion_on":...}`. (Sin cambio de payload.)

No cambia el contrato MQTT. Solo deja de asumir 2 y respeta la cascada mapa>fija.

## Lo que NO se hace en esta fase (YAGNI / diferido)

- **Firmware multi-motor real** (>2 motores físicos por nodo / topología de
  nodos). Se define cuando se cierre el fierro. La UI/bridge ya emiten target por
  `id` arbitrario.
- **Corte sub-motor (surco por surco real dentro de un motor).** Confirmado:
  granularidad = motor. Si en el futuro se quiere apagar un solo surco de un motor
  multi-surco, es otra fase.
- **Auto-repartir avanzado** (patrones irregulares). Esta fase: 1 motor/surco o N
  grupos parejos.

## Archivos afectados

| Archivo | Cambio |
|---|---|
Rutas relativas a `SourceCode/`.

| Archivo | Cambio |
|---|---|
| `AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotoresConfig.cs` | Array fijo de 2 → lista variable; +Motor/quitar; validación surcos huérfanos |
| `AgroParallel/Core/AgroParallel.Models/QuantiXDtos.cs` | `Motores[]` deja de asumir length 2 (esquema ya soporta lista) |
| `AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs` | Loop 0..1 → 0..N; cascada mapa>fija; eje solidario por surcos del motor |
| `AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/quantix.html` | Tira de surcos unificada (Configurar/En marcha), pincel, +Motor/Auto-repartir, lista con dosis fija+efectiva, toggle Planter/Tabla |
| `AgroParallel/Web/AgroParallel.WebUI/wwwroot/js/quantix.js` | Lógica de pincel, asignación táctil, render live, toggle vista |
| Controller QuantiX (WebHost) | Endpoints config ya soportan lista; verificar que no clampeen a 2 |

## Criterios de aceptación

1. Se pueden agregar/quitar motores (1..24) desde la UI; persiste en `quantiX_motores.json`.
2. Cada surco se asigna a un solo motor pintando con el pincel; surcos huérfanos
   se ven y se avisan antes de guardar.
3. Cada motor toma dosis de mapa si hay, si no de su dosis fija.
4. En marcha, los surcos cortados se ven grises y cada motor muestra PPS real/obj
   con badge OK/corte/desvío.
5. Un motor frena solo cuando **todos** sus surcos están cortados.
6. El bridge publica un target MQTT por motor con su `id`, sin romper el contrato actual.
7. Toggle Planter/Tabla funciona; la tabla lista una fila por motor.
