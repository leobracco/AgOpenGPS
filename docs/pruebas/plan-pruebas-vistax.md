# Plan de pruebas — VistaX (semillas/m)

> Plan definido el 2026-08-02. La columna **Estado** refleja qué se pudo
> verificar por simulación de software (`scratchpad/simular_siembra.py`:
> NMEA UDP :9999 + nodos MQTT simulados contra el Engine vivo). Lo demás
> requiere banco físico o campo.
>
> **Registrar siempre:** velocidad, espaciamiento, cultivo, temperatura,
> versión de firmware, UID de nodo.

## 1. Banco (sin sembradora, generador de pulsos)

| # | Prueba | Setup | Esperado | Criterio | Estado |
|---|---|---|---|---|---|
| B1 | Conteo exacto | 100 pulsos a 5 Hz | 100 semillas | 0 % error | pendiente banco |
| B2 | Conteo a alta frecuencia | 1000 pulsos a 40 Hz | 1000 semillas | ≤0,5 % error | pendiente banco |
| B3 | Límite superior | Rampa 10→100 Hz | Detectar Hz de quiebre | Documentar máx. real | pendiente banco |
| B4 | Ancho de pulso mínimo | 200 µs, 100 µs, 50 µs | Detecta según diseño | Definir ancho mínimo | pendiente banco |
| B5 | Debounce / rebote | Pulso con ringing | Sin doble conteo | 0 falsos positivos | pendiente banco |
| B6 | Pulsos simultáneos multi-canal | 8 canales a 30 Hz | Todos cuentan igual | ≤1 % dispersión | pendiente banco |
| B7 | Sin pulsos | Canal en silencio 30 s | Alarma línea tapada | Dispara <3 s | **SIMULADO OK** (2026-08-02: acum congelado → `tubo_sin_semilla` con sostenido 4 s configurable) |

## 2. Cálculo sem/m y sem/ha

| # | Prueba | Setup | Esperado | Estado |
|---|---|---|---|---|
| C1 | Conversión directa | 390 pulsos / 100 m, esp. 0,52 | 3,9 sem/m — 75.000 sem/ha | **SIMULADO OK** (6 sem/s @3,5 km/h → 6,17 sem/m en vivo) |
| C2 | Cambio de espaciamiento | Mismo pulso, 0,35 vs 0,52 | sem/ha escala correctamente | **SIMULADO OK** (distancia deriva de Secciones — fix 6697a4c8) |
| C3 | Ventana móvil | Ráfaga y pausa | Promedio estable, sin picos | pendiente banco (SPM por derivada de `acum`, ventana ≥1 s ya implementada) |
| C4 | Velocidad mínima | <1,5 km/h | Congela cálculo, no diverge | **SIMULADO OK** (VxObjetivoDinamico: v≤0,36 km/h → cae al fijo; tests unitarios) |
| C5 | Velocidad = 0 con pulsos | Vel 0, pulsos activos | No divide por cero, flag error | **SIMULADO OK** (guard vMs≤0.1 con test unitario) |
| C6 | Espaciamiento en semilla | 4 sem/m | 25 cm entre semillas | pendiente banco |
| C7 | CV | Pulsos con jitter conocido | CV calculado coincide ±2 % | pendiente (CV aún no implementado) |

## 3. Sensor físico (banco con disco)

| # | Prueba | Setup | Esperado | Estado |
|---|---|---|---|---|
| S1 | Semilla real maíz | 200 semillas por tubo | Error ≤2 % | pendiente banco |
| S2 | Semilla real soja | 500 semillas | Error ≤3 % | pendiente banco |
| S3 | Doble semilla | Soltar 2 juntas | Cuenta 2 (o marca doble) | pendiente banco |
| S4 | Grafito/talco | Sensor con polvo | Sigue contando, o alarma sucio | pendiente banco |
| S5 | Sol directo | Linterna IR / luz solar sobre receptor | Sin falsos pulsos | pendiente banco |
| S6 | Haz bloqueado | Tapar emisor | Alarma de falla de sensor ≠ línea tapada | pendiente banco (en soft: `no-data` ≠ `falla` ya distinguidos) |
| S7 | Tierra/humedad | Sensor rociado | Documentar degradación | pendiente banco |

## 4. Eléctrica y comunicación

| # | Prueba | Setup | Esperado | Estado |
|---|---|---|---|---|
| E1 | Caída de tensión | 12 V → 9 V | Nodo sigue operando o reinicia limpio | pendiente banco |
| E2 | Arranque de motor | Simular dip a 6 V | Recupera sin perder conteo de sesión | pendiente banco (soft: reboot detectado por `acum` que retrocede, SPM se mantiene) |
| E3 | Corto en canal | Puentear un canal | PTC abre, resto sigue | pendiente banco |
| E4 | Cable de nodo cortado | Desconectar bus | Alarma "nodo perdido" <5 s | **SIMULADO parcial** (timeout 3 s → sensores `no-data`; alarma offline por implemento ya existe en nodos.html) |
| E5 | Reconexión | Reconectar nodo | Re-descubre UID, retoma | **SIMULADO parcial** (announcement re-registra; verificado con nodo simulado) |
| E6 | Ruido alternador | Nodo cerca de alternador | Sin pulsos espurios | pendiente campo |
| E7 | Bus largo | Cable a máx. longitud | Sin pérdida de tramas | pendiente banco |

## 5. Integración / sesión

| # | Prueba | Setup | Esperado | Estado |
|---|---|---|---|---|
| I1 | Corte de energía en trabajo | Cortar 12 V a mitad | Recupera sesión al volver | pendiente cabina |
| I2 | Cabecera | Levantar sembradora | Pausa alarmas, no acumula ha | **SIMULADO OK** (sección cortada → surco gris, sin alarma; verificado con master OFF: 28/28 suprimidos, 0 disparos) |
| I3 | GPS sin fix | Cortar antena | Flag, usa última velocidad o pausa | **SIMULADO parcial** (sin NMEA la velocidad decae y el monitoreo se pausa) |
| I4 | Alta a baja velocidad | 12 → 3 km/h | sem/m estable, sem/ha coherente | **SIMULADO OK** (objetivo dinámico sigue la velocidad viva: obj=esperado exacto a 3,5 km/h) |
| I5 | Config errónea | N° líneas ≠ nodos presentes | Advertencia clara | pendiente UI |
| I6 | Multi-nodo | 3 nodos, 24 líneas | Todas visibles y sincronizadas | **SIMULADO parcial** (28 sensores/1 nodo, 2 por surco; multi-nodo pendiente) |
| I7 | OTA | Actualizar 1 nodo de 3 | Detecta versión desalineada | pendiente banco (OTA SHA-256 + anti-downgrade ya probado en QuantiX) |

## 6. Validación a campo (referencia)

| # | Prueba | Método | Criterio | Estado |
|---|---|---|---|---|
| F1 | Conteo manual | Desenterrar 10 m, contar semillas | Δ ≤5 % vs monitor | pendiente campo |
| F2 | Repetibilidad | 3 pasadas mismo lote | Dispersión ≤3 % | pendiente campo |
| F3 | Jornada completa | 8 h continuas | Sin cuelgues, sin deriva | pendiente campo |
| F4 | Comparativa | vs. monitor comercial | Δ ≤5 % | pendiente campo |

## Extras verificados por la simulación (2026-08-02, fuera del plan)

- **Objetivo dinámico QuantiX**: con el motor a 149 pps (24 sem/vta, 600 ppr)
  el surco alimentado alarma contra 6,13 sem/m (la consigna viva), y el surco
  sin motor contra el objetivo fijo del insumo (16). Exacto al 2 %.
- **`motor_parado` y `dosis_baja`**: motor con consigna y 0 rpm dispara ambas.
- **Supresión por sección**: la sección del surco se deriva sola del implemento
  central (surco→seccion_pilotx) — no hace falta cargar `seccion_aog` por sensor.
- **Bug crítico encontrado y arreglado**: el pipeline de fix del Engine corría
  en paralelo ante ráfagas UDP y corrompía el índice del anti-solape; las
  secciones quedaban CONGELADAS (el master no las apagaba). Serializado con
  lock. Además: ModSim.exe residente en :8888 arma un lazo de eco UDP con el
  broadcast de CoreX — matarlo antes de probar.
