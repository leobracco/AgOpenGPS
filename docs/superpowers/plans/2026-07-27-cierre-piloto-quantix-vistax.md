# Cierre piloto + QuantiX + VistaX — plan de 3 días

> **Para quien ejecute:** este plan es para DOS personas en paralelo (Leonardo/UI,
> Santiago/engine). Cada tarea tiene archivos exactos y un criterio de aceptación
> verificable. Los checkbox (`- [ ]`) son para ir tachando.

**Objetivo:** que en la pantalla de la cabina el operario pueda **guiar, sembrar
con QuantiX y monitorear con VistaX** de punta a punta, sin tocar nada nativo
viejo y sin datos congelados.

**Arquitectura:** un solo backend (`PilotX.GuidanceEngine --webhost`, API `:5180`)
+ una sola UI (`PilotX.UI`, Avalonia, compartida Desktop/Android). CoreX aporta
bridge UDP y broker MQTT. Los dos carriles hablan **solo** por la API `:5180`
(snake_case) y por MQTT `agp/...` — por eso se puede avanzar en paralelo.

**Stack:** .NET 9 / Avalonia (UI), netstandard2.0 (servicios), EmbedIO (web host),
MQTTnet (broker), NUnit (tests).

## Restricciones globales

- **Plataforma objetivo del cierre: Windows/Desktop.** Android queda para después
  (acordado). Todo lo que se toque en `PilotX.UI` viaja gratis a Android igual.
- **Wire snake_case** en toda la API (`AgpJson`/`AgpControllerBase`).
- **Nodos solo por descubrimiento MQTT** — prohibido alta manual en UI.
- **Unidades al operario:** kg/ha, sem/m, sem/ha, rpm. **Nunca PPS.**
- **Branding:** en UI/logs/comentarios nuevos: PilotX / Agro Parallel / CoreX.
  No tocar namespaces ni clases.
- **Nadie toca el carril del otro** sin dejar PEDIDO en `COORDINACION-SESIONES.md`.
- **Todo cambio cierra con:** build 0 errores + `dotnet test AgOpenGPS.sln` verde +
  commit chico.

---

## Nota de alcance (leer antes de arrancar)

Una advertencia honesta: **"100%" del inventario completo no entra en 3 días.**
`docs/INVENTARIO-UI-ICONOS.md` tiene todavía ítems grandes que son proyectos en sí
mismos (constructores de lindero/cabecera/tramline, ruta grabada, ISOBUS, import
de guías). Meterlos apurados es la receta para romper lo que ya anda.

Entonces defino **100% = "el tractor trabaja un lote de punta a punta"**:

| Entra en los 3 días | Queda afuera (lista explícita) |
|---|---|
| Guiar sobre una guía existente, con secciones, cabecera y U-turn | Constructores de lindero/cabecera/tram desde cero |
| Secciones individuales y zonas, snap/nudge, salteo de surcos | Ruta grabada (grabar/reproducir) |
| Banderas | ISOBUS (el botón nativo ni siquiera tiene handler) |
| QuantiX sembrando con dosis real y calibración | Import de guías de otro lote |
| VistaX monitoreando con alarmas y overlay | Controles de cámara 3D/norte/tilt |
| Ningún dato congelado en la UI | Android |

Si algo de la derecha es imprescindible para vos, decilo ahora y reacomodo los
días — pero entonces sale otra cosa de la izquierda.

**Otra cosa:** son 3 subsistemas independientes. Este documento es el plan
maestro; si alguno se complica, se parte en su propio plan sin arrastrar a los
otros.

---

## Reparto de carriles

> **CAMBIO 2026-07-27 (decisión de Leonardo):** se invirtieron los carriles.
> Santiago toma la parte **visual** sobre Windows e itera **ícono por ícono**
> contra `docs/INVENTARIO-UI-ICONOS.md`; Leonardo toma motor, servicios y
> empaquetado. Las indicaciones completas para Santiago están en
> `COORDINACION-SESIONES.md` (sección "SANTIAGO — ARRANCÁ ACÁ").

| | **SANTIAGO** (otra PC) | **LEONARDO** (esta PC) |
|---|---|---|
| **Rol** | UI Avalonia + páginas HTML, ícono por ícono | Engine + servicios + comandos + empaquetado |
| **Archivos** | `PilotX.UI/*`, `PilotX.Cockpit.Bars/*`, `wwwroot/*` | `PilotX.GuidanceEngine*/*`, `AgroParallel.Services/*`, `AgOpenGPS.Core/*`, `build.ps1` |
| **NO toca** | `PilotX.GuidanceEngine*`, `AgroParallel.Services/*`, `build.ps1` | `PilotX.UI/*`, `PilotX.Cockpit.Bars/*` |
| **Verifica con** | pantalla + capturas (el EFECTO, no el botón) | `curl` a `:5180` + tests |

**Consecuencia en las tareas de abajo:** las L* (UI) pasan a Santiago y las S*
(motor) a Leonardo.

> **L1 SE CANCELA — la premisa era falsa (verificado 2026-07-27).** Di por
> sentado que los 9 paneles de `Views/*` tenían el mismo `catch` fatal que los
> pollers. **No lo tienen:** ahí el `catch (OperationCanceledException)` envuelve
> solo al `Task.Delay(…, ct)` (que únicamente lanza si la cancelación es real) y
> la llamada HTTP vive dentro de `TickAsync` → cada cliente
> (`QuantiXClient`, `VistaXClient`, …) la captura con su propio
> `catch { return null; }` y el tick siguiente reintenta. Comprobado en los 9
> clientes: todos capturan por método. **No hay nada que arreglar.** Lección para
> el resto del plan: el patrón `catch (OperationCanceledException) { return; }`
> solo es fatal **si envuelve la llamada HTTP** — hay que mirar qué envuelve
> antes de tocarlo.

**Punto de encuentro diario:** al terminar el día, cada uno deja su entrada en
`COORDINACION-SESIONES.md` y pushea. El del otro lado hace merge al arrancar.

---

# DÍA 1 — Fundaciones: que nada se congele y que el piloto opere

El objetivo del día 1 es **confiabilidad**: hoy hay dos clases de bug que hacen
parecer roto lo que funciona (datos congelados, comandos que no existen). Sin
esto, cualquier prueba de QuantiX/VistaX del día 2 va a dar falsos negativos.

## P0 — Que la pantalla arranque el stack Avalonia (Leonardo) · PRIMERO, 2 h

**Esto va antes que todo lo demás y es el mayor riesgo del plan.** Hoy
`build.ps1` publica el `PilotX.exe` **WinForms** + `BarsHost`, y **no empaqueta
`PilotX.Desktop` ni `PilotX.GuidanceEngine`**. O sea: lo que se instala en la
cabina sigue siendo el viejo. Si el cierre es "Windows sobre Avalonia", el
paquete tiene que llevar el stack nuevo — y hay que probarlo en la pantalla
**el día 1**, no el día 3, para tener margen si falla.

**Archivos:**
- Modificar: `build.ps1`
- Revisar: `PilotX-KioskSetup` (qué ejecutable lanza al arrancar)

**Cadena de arranque objetivo:** `CoreX.exe` → `PilotX.GuidanceEngine.exe --webhost`
→ `PilotX.Desktop.exe`. (El engine sin `--corex`: CoreX ya provee broker y bridge;
con `--corex` chocan en el 1883.)

- [ ] **Paso 1: publicar los dos que faltan**

En `build.ps1`, junto al publish de `PilotX.Bars.Host`:

```powershell
dotnet publish "$root\SourceCode\PilotX.GuidanceEngine\PilotX.GuidanceEngine.csproj" `
    -c Release -r win-x64 --self-contained false `
    -p:PublishReadyToRun=true -o "$OutDir\Engine" $verArg

dotnet publish "$root\SourceCode\PilotX.Desktop\PilotX.Desktop.csproj" `
    -c Release -r win-x64 --self-contained false `
    -p:PublishReadyToRun=true -o "$OutDir\Desktop" $verArg
```

- [ ] **Paso 2: el perfil de vehículo tiene que viajar**

Verificar que `Engine\aog_settings.json` queda con el `vehicle_file_name` real.
**Sin eso el motor corre con la geometría por defecto** (antena, ancho, ganancias)
y el guiado sale mal de una forma difícil de diagnosticar. El engine ahora lo
avisa en consola: `Perfil de vehículo: <nombre> → Ok` vs `(ninguno) MissingFile`.

- [ ] **Paso 3: decidir qué lanza el kiosco**

Elegir explícitamente: ¿la pantalla arranca el stack Avalonia o sigue con el
WinForms? **Es decisión de Leonardo, no la tomo yo** — pero el plan asume Avalonia.
Dejar el WinForms instalado como salida de emergencia hasta que T2 pase.

- [ ] **Paso 4: prueba en la pantalla de la cabina (el paso que importa)**

Instalar y arrancar la cadena completa **en la pantalla**, no en la PC de
desarrollo. Recordar: VC++ redist obligatorio; si hay WebView de por medio, exige
sesión física con doble clic.
**Criterio de aceptación:** PilotX.Desktop abre, el mapa dibuja y la barra
superior muestra velocidad real. Si esto no pasa el día 1, **se frena el plan y
se replantea el alcance** — no tiene sentido cerrar features sobre una base que
no arranca donde tiene que arrancar.

- [ ] **Paso 5: commit**

## L1 — Matar el `catch` que congela los paneles (Leonardo) · 45 min

Mismo bug que ya se arregló en los 7 pollers de datos en vivo (commit `045dbcfd`):
`HttpClient.Timeout` lanza `TaskCanceledException`, que hereda de
`OperationCanceledException`; el `catch` lo trata como "me pidieron parar" y el
loop muere **para siempre**. Los paneles de QuantiX y VistaX lo tienen igual — o
sea que **hoy el panel de siembra se congela solo** y parece un problema de nodos.

**Archivos (modificar):**
- `SourceCode/PilotX.UI/Views/QuantiXPanel.axaml.cs:85`
- `SourceCode/PilotX.UI/Views/VistaXPanel.axaml.cs:91`
- `SourceCode/PilotX.UI/Views/FlowXPanel.axaml.cs:86`
- `SourceCode/PilotX.UI/Views/NodosPanel.axaml.cs:78`
- `SourceCode/PilotX.UI/Views/SectionXPanel.axaml.cs:83`
- `SourceCode/PilotX.UI/Views/StormXPanel.axaml.cs:69`
- `SourceCode/PilotX.UI/Views/CamarasPanel.axaml.cs:298`
- `SourceCode/PilotX.UI/Views/CoreXEcuPanel.axaml.cs:82`
- `SourceCode/PilotX.UI/Views/ActualizarPanel.axaml.cs:64`

- [ ] **Paso 1: aplicar el guard en los 9 paneles**

En cada archivo, reemplazar:

```csharp
catch (OperationCanceledException) { return; }
```

por (usando el nombre real del token de cancelación de ese loop):

```csharp
// HttpClient.Timeout lanza TaskCanceledException (hereda de
// OperationCanceledException): sin este guard, UN request lento mata el
// refresco del panel para siempre y queda congelado en sus defaults.
catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
```

Solo el `catch` que está **inmediatamente antes de un `catch (Exception`** (ese es
el del request). El que envuelve al `Task.Delay` se deja como está: ahí la
cancelación siempre es real.

- [ ] **Paso 2: verificar que compila**

```
dotnet build SourceCode/PilotX.UI/PilotX.UI.csproj -v q --nologo
```
Esperado: `Compilación correcta. 0 Errores`

- [ ] **Paso 3: probar en pantalla**

Con PilotX.Desktop abierto, abrir el panel de QuantiX y dejarlo 5 minutos.
**Criterio de aceptación:** los valores siguen refrescando (el reloj/los KPIs
cambian). Antes de este fix, un pico de latencia lo dejaba clavado.

- [ ] **Paso 4: commit**

```
git commit -am "fix(ui): el refresco de los paneles moria al primer timeout HTTP"
```

## S1 — Comandos de secciones individuales y zonas (Santiago) · medio día

Hoy `ExecuteCommand` no tiene cómo prender/apagar una sección suelta ni una zona:
en el inventario están como ❌ y son **imprescindibles para sembrar** (levantar un
cuerpo en una punta, cortar media barra en una cuña).

**Archivos:**
- Modificar: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Commands.cs`
- Referencia a copiar (solo leer): `SourceCode/GPS/Forms/Sections.Designer.cs`
  (los handlers de los botones numerados) y `GuidanceEngineHost.SectionsRuntime.cs`
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/` (nuevo archivo)

**Interfaces:**
- Produce: comandos `seccion_<n>` con n=1..16 y `zona_<n>` con n=1..8, que ciclan
  `Off → Auto → On` igual que el nativo, y se reflejan en
  `GET /api/aog/sections` (`is_auto`, `is_manual_on`) y en el estado por sección.

- [ ] **Paso 1: test que falla**

Contra `PilotX.GuidanceEngine.exe --sim --webhost`, con lote abierto:

```
curl -s -X POST -H "Content-Type: application/json" \
  -d '{"cmd":"seccion_3"}' http://127.0.0.1:5180/api/aog/guidance/command
```
Esperado HOY: `{"ok":false}` / `unknown` → eso es el "test rojo".

- [ ] **Paso 2: implementar el ciclado**

En el `switch` de `ExecuteCommand`, agregar el prefijo `seccion_` y `zona_`
(parseando el índice), replicando **exactamente** el ciclo de estados del nativo
(`btnStates.Off → Auto → On`) y respetando el mutex con el master auto/manual.

- [ ] **Paso 3: verificar en runtime**

```
curl -s -X POST -d '{"cmd":"seccion_3"}' ... && curl -s http://127.0.0.1:5180/api/aog/sections
```
**Criterio de aceptación:** el estado de la sección 3 cambia Off→Auto→On en
llamadas sucesivas y vuelve a Off; las demás secciones no se mueven.

- [ ] **Paso 4: 3 comandos que faltan del piloto**

Mismo archivo, mismo patrón, copiando del handler nativo correspondiente:
`bandera` (dejar bandera en la posición actual — requiere portar `FlagsFiles`),
`snap_pivote` / `nudge_ref`, `youskip` (cicla Normal→Alternado→Ignorar trabajados),
`hyd_lift`. Cada uno con su verificación por `curl` + estado.

- [ ] **Paso 5: commit + entrada en `COORDINACION-SESIONES.md`**

## L2 — Que esos comandos tengan botón (Leonardo) · medio día

**Archivos:**
- Modificar: `SourceCode/PilotX.Cockpit.Bars/Views/BarraAbajo.axaml` (+ `.cs`)
- Modificar: `SourceCode/PilotX.Cockpit.Bars/ViewModels/BarraAbajoViewModel.cs`
- Test: `SourceCode/PilotX.Cockpit.Bars.Tests/BarraDerechaAbajoViewModelTests.cs`

- [ ] **Paso 1: test del ViewModel primero**

Agregar un test que aplique un `CockpitSnapshot` con N secciones y verifique que
el VM expone N botones con el estado correcto (Off/Auto/On) y que tocar el
botón 3 manda `seccion_3`.

- [ ] **Paso 2: implementar la fila de secciones en la barra de abajo**

Botones numerados 1..N (N = `numOfSections` real del implemento), colores
rojo/verde/ámbar como el nativo. Respetar el diseño: claro, suave, gris,
funcional; target táctil ≥40 px.

- [ ] **Paso 3: verificación en pantalla**

Con lote abierto y secciones configuradas: tocar el botón 3 y ver que **en el
mapa** esa sección deja de pintar cobertura. **Ese es el criterio, no que el
botón se ponga verde.**

- [ ] **Paso 4: commit**

## Cierre del día 1

- [ ] Los dos pushean y dejan entrada en `COORDINACION-SESIONES.md`.
- [ ] **Prueba conjunta (15 min):** lote abierto, guía elegida, piloto enganchado,
      secciones en auto, manejar 2 minutos en el simulador. Nada congelado, la
      cobertura se pinta, las secciones responden.

---

# DÍA 2 — QuantiX y VistaX de punta a punta

## S2 — QuantiX: dosis real contra el nodo (Santiago) · medio día

**Archivos:**
- Modificar: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXMotorBridge.cs`
- Modificar: `SourceCode/AgroParallel/Core/AgroParallel.Services/QuantiX/QuantiXSender.cs`
- Test: `SourceCode/AgOpenGPS.Tests/QuantiX/QxDoseResolverTests.cs` (ya existe, ampliar)

- [ ] **Paso 1: tests de la cadena de dosis**

Ampliar `QxDoseResolverTests` con los casos que hoy no cubre y que son los que
rompen en campo:
- velocidad 0 (tractor parado) → el motor **no** debe girar
- velocidad por encima del límite → dosis clampeada, no extrapolada
- cambio de insumo con calibración distinta → sem/m correcto
- sección apagada → ese tren para

Correr: `dotnet test SourceCode/AgOpenGPS.Tests/AgOpenGPS.Tests.csproj`

- [ ] **Paso 2: implementar lo que falte para que pasen**

- [ ] **Paso 3: verificación con nodo real o simulado por MQTT**

Publicar un `announcement` de un nodo QuantiX y verificar que
`GET /api/quantix` lo lista, que al mandar dosis el nodo recibe el set point en
`agp/quantix/<UID>/cmd/...`, y que el `status_live` vuelve.
**Criterio:** rpm reportadas coherentes con la dosis pedida a la velocidad actual.

## L3 — QuantiX: panel y overlay (Leonardo) · medio día

**Archivos:**
- Modificar: `SourceCode/PilotX.UI/Views/QuantiXPanel.axaml(.cs)`
- Modificar: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/quantix.html` (si hace falta)

- [ ] **Paso 1:** que el panel muestre por tren: **rpm, dosis real, dosis
      objetivo, PWM** — nunca PPS. Si el tractor está parado, decir "sin
      velocidad", no mostrar 0 como si fuera una dosis.
- [ ] **Paso 2:** estado de nodo offline visible y con código de error
      (`AgpErrorMapper`), no un valor viejo pegado.
- [ ] **Paso 3: verificación en pantalla** con el nodo simulado del paso S2.
- [ ] **Paso 4: commit**

## S3 — VistaX: alarmas y cortes (Santiago) · medio día

**Archivos:**
- Modificar: `SourceCode/AgroParallel/Core/AgroParallel.Services/VistaX/` (servicio live)
- Test: `SourceCode/AgroParallel/Core/AgroParallel.Services.Tests/SiembraStateMachineTests.cs` (ampliar)

- [ ] **Paso 1: tests de la máquina de estados de siembra**

Casos que importan en campo: sensor tapado, sensor sin señal, implemento
levantado (no debe alarmar), arranque de pasada (no alarmar hasta que estabilice),
sección apagada (ese cuerpo no alarma).

- [ ] **Paso 2: implementar** hasta que pasen.
- [ ] **Paso 3: verificación** publicando `status_live` simulados por MQTT y
      viendo que `GET /api/vistax/...` refleja el estado y que la alarma se
      levanta y se baja cuando corresponde.

## L4 — VistaX: overlay en la pantalla del piloto (Leonardo) · medio día

**Archivos:**
- Modificar: `SourceCode/PilotX.UI/Views/VistaXPanel.axaml(.cs)`
- Modificar: `SourceCode/PilotX.UI/Views/CabinaAlarmasOverlay.axaml(.cs)`

- [ ] **Paso 1:** el overlay tiene que ser legible **de reojo** a 1 m: cuerpo con
      problema en rojo, número de cuerpo grande, sin texto largo.
- [ ] **Paso 2:** que el overlay se prenda solo al activar un implemento con nodos
      VistaX (ya existe `overlayPrefs.json`) y que se pueda ocultar con un toque.
- [ ] **Paso 3: verificación en pantalla** con los sensores simulados de S3.
- [ ] **Paso 4: commit**

## Cierre del día 2

- [ ] Push + bitácora de los dos.
- [ ] **Prueba conjunta (30 min):** sembrar un lote simulado con QuantiX dosificando
      y VistaX monitoreando, con una sección apagada a mano y un sensor "tapado"
      forzado. Ver que la dosis acompaña la velocidad y que la alarma aparece.

---

# DÍA 3 — Pruebas, cierre y provisioning

## T1 — Suite de regresión (los dos, mañana) · 3 h

- [ ] **Leonardo:** tests de ViewModel para lo tocado (barras + paneles QX/VX) en
      `PilotX.Cockpit.Bars.Tests`. Mínimo: un test por comportamiento nuevo del día 1-2.
- [ ] **Santiago:** tests de servicio para dosis y alarmas. Mínimo: los casos de
      borde de S2/S3.
- [ ] **Los dos:** `dotnet test AgOpenGPS.sln` **verde entero** (hoy: 142).
      Ningún test nuevo que dependa de hardware o de timing real.

## T2 — Prueba de campo simulada, guionada (los dos, tarde) · 2 h

Correr este guion completo **sin tocar código**, anotando cada desvío:

- [ ] Arrancar de cero: CoreX → engine → PilotX.Desktop (así arranca en la cabina).
- [ ] Crear un lote nuevo. Crear una guía AB. Elegirla.
- [ ] Enganchar el piloto. Verificar que sigue la línea (lightbar y XTE en el
      gráfico de dirección se mueven como corresponde).
- [ ] Secciones en auto. Manejar y ver la cobertura pintarse.
- [ ] Apagar la sección 3 a mano. Ver el hueco en la cobertura.
- [ ] Cabecera: entrar y ver que corta.
- [ ] U-turn al final de la pasada.
- [ ] QuantiX dosificando: cambiar la velocidad y ver acompañar la dosis.
- [ ] VistaX: forzar un sensor tapado y ver la alarma en el overlay.
- [ ] Dejar 20 minutos corriendo. **Nada congelado, sin fugas de memoria
      visibles, sin que se ponga lenta la UI.**

**Criterio de aceptación del proyecto:** el guion entero pasa sin intervención.

## T3 — Cierre (Leonardo, última hora) · 1 h

- [ ] `build.ps1` completo y verificar el paquete.
- [ ] Instalar en la pantalla de la cabina y correr el guion T2 **ahí**, no en la
      PC de desarrollo (recordar: VC++ redist obligatorio, y WebView2 exige sesión
      física con doble clic).
- [ ] Actualizar `docs/INVENTARIO-UI-ICONOS.md`: tachar lo cerrado, dejar
      explícito lo diferido.
- [ ] Entrada final en `COORDINACION-SESIONES.md` con el estado real.

---

## Riesgos concretos (y qué hacer)

| Riesgo | Señal temprana | Plan B |
|---|---|---|
| **La pantalla no arranca el stack Avalonia** (riesgo #1) | P0 falla el día 1 | Frenar el plan y replantear alcance. No cerrar features sobre una base que no arranca donde tiene que arrancar |
| El WinForms sigue siendo lo instalado y nadie lo nota | T3 "anda" pero es el viejo | Verificar siempre por el proceso (`PilotX.Desktop.exe`), no por "se ve parecido" |
| Los comandos de secciones tocan más lógica de la esperada | S1 pasa de medio día | Cerrar solo secciones individuales, dejar zonas para después |
| No hay nodo QuantiX/VistaX físico para probar | Día 2 a la mañana | Simular por MQTT (ya está el patrón); marcar explícitamente qué quedó sin hardware |
| Aparece otro bug de datos congelados | Un panel se clava en T2 | Buscar primero el patrón del `catch`: es el sospechoso #1 |
| El guion T2 falla en la pantalla pero no en la PC | T3 | La pantalla es más lenta: sospechar timeouts y render, no lógica |

## Definición de terminado

- [ ] El guion T2 corre entero en la **pantalla de la cabina**.
- [ ] `dotnet test AgOpenGPS.sln` verde.
- [ ] Build completo 0 errores / 0 warnings.
- [ ] Inventario y bitácora actualizados con lo hecho **y lo diferido**.
