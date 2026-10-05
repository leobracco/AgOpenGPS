# Modo banderillero / Modo piloto — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el operario elija cómo leer el desvío —barra de luces o número— desde un lugar a mano y con un nombre que se entienda, en vez de un tilde técnico enterrado en calibración.

**Architecture:** El dato no cambia: sigue siendo `setMenu_isLightbarOn`, que ya persiste y ya viaja en `/api/aog/state`. Se agrega un comando en el engine que lo escribe, dos botones en el submenú Pantalla del cockpit que lo disparan, y se saca el tilde duplicado de las dos pantallas de Dirección junto con un selector que no hace nada.

**Tech Stack:** C# / .NET 9, Avalonia, NUnit.

## Global Constraints

- Castellano rioplatense en UI, logs, comentarios nuevos y mensajes de commit. Código y términos técnicos en inglés.
- En UI y logs se dice **PilotX / OrbitX / Agro Parallel**, nunca AgOpenGPS/AOG/AgIO.
- **El modo cambia SÓLO lo que se ve.** El piloto automático se engancha igual en los dos: nada de este trabajo toca `isBtnAutoSteerOn` ni la habilitación del botón Piloto.
- **Sin migración y sin tocar el default de fábrica** (`false` = Piloto). Un equipo que hoy tenga el setting en `true` arranca en Banderillero, y se acepta a propósito.
- Los dos nombres son **"Banderillero"** y **"Piloto"**, exactos.
- Es código de una máquina que siembra: un error cuesta plata en el lote.

---

## Cómo viaja un toque hasta el setting (verificado, 2026-09-16)

Vale la pena tenerlo claro porque el camino tiene una escala intermedia:

```
Botón del submenú Pantalla
   → SendCommand (BarViewModelBase)
   → GuidanceCommandClient.SendAsync
   → LocalHandler  ── ¿lo maneja MainWindow? ──► sí: return true, termina acá
                                                 (es lo que pasa con dia_noche
                                                  y brillo_up/dn)
   → no: POST /api/aog/guidance/command {cmd}
   → GuidanceEngineHost.Commands.cs, el switch donde está "autosteer" (línea 184)
   → escribe setMenu_isLightbarOn + Save()
   → /api/aog/state lo emite como mostrar_luces
   → HudPoller → MainWindow muestra u oculta las luces
```

`modo_banderillero` / `modo_piloto` **tienen que llegar al engine**, porque ahí
vive el `Settings`. Verificado que llegan: el `switch` de
`MainWindow.RouteCockpitCommand` no tiene `default`, y su cola termina en
`return false` (`MainWindow.axaml.cs:6698`), así que todo comando que no
reconozca cae al POST. No hay que tocar `RouteCockpitCommand`.

El ida y vuelta ya funciona hoy: es exactamente el camino del tilde de
Dirección.

## Una decisión que se aparta del spec: los botones no se marcan

El boceto del spec (§3) muestra el modo activo resaltado. **No se hace, y la
razón es que hacerlo bien sale caro y hacerlo a medias miente.**

`MenuIzquierdaViewModel` se instancia en **tres** hosts —
`PilotX.Bars.Host/App.axaml.cs:41`, `PilotX.UI/MainWindow.axaml.cs:6214` y
`PilotX.UI/Views/MainView.axaml.cs:172` — y **ninguno le pasa snapshots**: su
`Apply` está declarado como no-op a propósito (`MenuIzquierdaViewModel.cs:75`,
*"no hace polling de estado"*). Además los dos hosts de `PilotX.UI` no usan
`CockpitSnapshot` sino `HudSnapshot`. Marcar el botón bien significa cablear
estado en los tres, en dos tipos distintos. Cablearlo en uno solo deja a los
otros dos mostrando un modo que puede no ser el que está puesto — un botón que
dice "estás en Piloto" cuando no lo estás es peor que ninguno.

**Y no hace falta:** el efecto del modo está a 30 cm, en la misma pantalla. Las
luces están o no están. Ese es el indicador, y es más honesto que un botón
teñido.

Consecuencia práctica para Task 2: **no se agrega `Classes.active` a los botones
nuevos.** Ojo que además **no existe un estilo `Button.sbtn.active`** en
`MenuIzquierda.axaml` (sólo `Button.mbtn.active`, línea 36), así que un
`Classes.active` en un `sbtn` no pintaría nada y no avisaría en compilación.

---

### Task 1: El comando que elige el modo

**Files:**
- Modify: `SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Commands.cs`

**Interfaces:**
- Consumes: nada.
- Produces: los comandos de texto `modo_banderillero` y `modo_piloto`, aceptados por el mismo `switch` que `autosteer`. Task 2 los manda desde la UI.

- [ ] **Step 1: Agregar los dos casos al switch de comandos**

En `GuidanceEngineHost.Commands.cs`, dentro del `switch` donde está
`case "autosteer":` (línea 184), agregar:

```csharp
                // Modo de lectura del desvío. NO toca el piloto: sólo elige qué
                // se dibuja arriba del mapa (la barra de luces o el recuadro
                // "A LA LÍNEA"). El valor vuelve al cliente como mostrar_luces
                // en /api/aog/state, y lo consume MainWindow.
                case "modo_banderillero":
                    AgOpenGPS.Properties.Settings.Default.setMenu_isLightbarOn = true;
                    AgOpenGPS.Properties.Settings.Default.Save();
                    Log.EventWriter("PilotX: modo de lectura = banderillero (luces)");
                    return true;
                case "modo_piloto":
                    AgOpenGPS.Properties.Settings.Default.setMenu_isLightbarOn = false;
                    AgOpenGPS.Properties.Settings.Default.Save();
                    Log.EventWriter("PilotX: modo de lectura = piloto (numero)");
                    return true;
```

Ajustá el tipo de retorno y la indentación a los casos vecinos: si los `case`
de alrededor hacen `break` en vez de `return true`, seguí lo que hagan ellos.
El patrón de escribir un setting y persistirlo dentro de un comando ya está en
este archivo — buscá `set_youSkipWidth` y mirá las dos líneas que le siguen.

- [ ] **Step 2: Compilar**

Run: `dotnet build "SourceCode/PilotX.GuidanceEngine.Core/PilotX.GuidanceEngine.Core.csproj" -c Debug`
Expected: Build succeeded, 0 errores, 0 advertencias nuevas.

- [ ] **Step 3: Verificar contra el engine corriendo**

Levantá el engine:

```
SourceCode/PilotX.GuidanceEngine/bin/Debug/net9.0/PilotX.GuidanceEngine.exe --corex --webhost
```

Mandá cada comando por el mismo endpoint que usa el cockpit y mirá el estado:

```bash
curl -s -X POST http://127.0.0.1:5180/api/aog/guidance/command \
     -H "Content-Type: application/json" -d '{"cmd":"modo_piloto"}'
curl -s http://127.0.0.1:5180/api/aog/state | grep -o '"mostrar_luces":[^,]*'

curl -s -X POST http://127.0.0.1:5180/api/aog/guidance/command \
     -H "Content-Type: application/json" -d '{"cmd":"modo_banderillero"}'
curl -s http://127.0.0.1:5180/api/aog/state | grep -o '"mostrar_luces":[^,]*'
```

Expected: `"mostrar_luces":false` primero, `"mostrar_luces":true` después.

**Si no podés levantar el engine en este entorno, decilo en el reporte y dejá
este paso sin marcar.** No lo declares hecho.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/PilotX.GuidanceEngine.Core/GuidanceEngineHost.Commands.cs
git commit -m "feat(cabina): comandos para elegir el modo de lectura del desvio

modo_banderillero y modo_piloto escriben setMenu_isLightbarOn y lo persisten,
asi que la eleccion queda puesta hasta que alguien la cambie.

NO tocan el piloto: el modo elige que se dibuja arriba del mapa (la barra de
luces o el recuadro A LA LINEA), y el piloto se engancha igual en los dos.

Prueba: Lightbar"
```

---

### Task 2: El selector en el submenú Pantalla

**Files:**
- Modify: `SourceCode/PilotX.Cockpit.Bars/Views/MenuIzquierda.axaml` (el `Border` del submenú `navegacion`, líneas 223-247)

**Interfaces:**
- Consumes: los comandos `modo_banderillero` y `modo_piloto` (Task 1).
- Produces: nada.

Hoy el submenú es `Border > DockPanel > [TextBlock "PANTALLA"] + ScrollViewer >
UniformGrid Columns="2"` con tres botones `Classes="sbtn"`.

Los dos botones nuevos necesitan un rótulo de grupo arriba y una línea de ayuda
abajo, y **un `UniformGrid` no permite que un hijo ocupe las dos columnas**. Así
que el contenido del `ScrollViewer` pasa a ser un `StackPanel` que contiene el
grid viejo, el rótulo, el grid nuevo y la ayuda. Nada más cambia.

- [ ] **Step 1: Reestructurar el contenido del submenú**

Reemplazar el `<ScrollViewer>…</ScrollViewer>` del submenú `navegacion`
(**sólo ese**, el que está dentro del `Border` con
`ConverterParameter=navegacion`) por:

```xml
          <ScrollViewer>
            <StackPanel>
              <UniformGrid Columns="2" VerticalAlignment="Top">
                <!-- "2D/3D/Norte 2D/Inclinar +/−" sacados (2026-07-29, pedido del
                     usuario): el pitch de cámara y el heading-up/north-up toggle
                     quedan afuera del menú. SetHeadingUp/SetPitchDeg/TiltBy
                     siguen vivos en MapGlSurface/MapPanel + el wiring en
                     RouteCockpitCommand, por si se los vuelve a colgar de un
                     botón más adelante — mismo criterio que "Grilla". -->
                <!-- "Grilla" sacado (2026-07-28): el cuadriculado de fondo se
                     quitó del mapa a pedido del usuario (no marcaba referencias
                     y se redibujaba entero cada frame) — el botón quedaba sin
                     ningún efecto. DrawGrid/_gridOn siguen vivos en MapGlSurface
                     por si algún día hace falta volver a colgarlo de un toggle. -->
                <Button Classes="sbtn" Command="{Binding SendCommand}" CommandParameter="dia_noche"><StackPanel><Image Classes="sico" Source="/Assets/menu/WindowNightMode.png"/><TextBlock Classes="slbl" Text="Día / Noche"/></StackPanel></Button>
                <Button Classes="sbtn" Command="{Binding SendCommand}" CommandParameter="brillo_up"><StackPanel><Image Classes="sico" Source="/Assets/menu/BrightnessUp.png"/><TextBlock Classes="slbl" Text="Brillo +"/></StackPanel></Button>
                <Button Classes="sbtn" Command="{Binding SendCommand}" CommandParameter="brillo_dn"><StackPanel><Image Classes="sico" Source="/Assets/menu/BrightnessDn.png"/><TextBlock Classes="slbl" Text="Brillo −"/></StackPanel></Button>
              </UniformGrid>

              <!-- Cómo leer el desvío. Va acá y no en Configuración porque es de
                   la misma familia que día/noche y brillo: cómo se ve la
                   pantalla. Antes era un tilde llamado "Mostrar barra en
                   pantalla", enterrado entre la distancia de enganche y el
                   grosor de línea.
                   Los botones NO se marcan como activos a propósito: el VM de
                   este menú no recibe snapshots (Apply es no-op) y se instancia
                   en tres hosts, dos de ellos con HudSnapshot en vez de
                   CockpitSnapshot. El indicador de qué modo está puesto son las
                   luces en el mapa, que se ven o no se ven. -->
              <TextBlock Classes="subtitle" Margin="2,12,0,6" Text="CÓMO LEER EL DESVÍO"/>
              <UniformGrid Columns="2" VerticalAlignment="Top">
                <Button Classes="sbtn" Command="{Binding SendCommand}" CommandParameter="modo_banderillero"><StackPanel><Image Classes="sico" Source="/Assets/menu/FlagRed.png"/><TextBlock Classes="slbl" Text="Banderillero"/></StackPanel></Button>
                <Button Classes="sbtn" Command="{Binding SendCommand}" CommandParameter="modo_piloto"><StackPanel><Image Classes="sico" Source="/Assets/menu/AutoSteerOn.png"/><TextBlock Classes="slbl" Text="Piloto"/></StackPanel></Button>
              </UniformGrid>
              <TextBlock TextWrapping="Wrap" FontSize="11" Foreground="#6F786F" Margin="2,8,2,0"
                         Text="Banderillero: barra de luces para manejar a mano. Piloto: el número de centímetros. El piloto automático se engancha igual en los dos."/>
            </StackPanel>
          </ScrollViewer>
```

Tres cosas que ya están verificadas y no hace falta que revises:

- `FlagRed.png` y `AutoSteerOn.png` **existen** en
  `SourceCode/PilotX.Cockpit.Bars/Assets/menu/`. (Avalonia no avisa en
  compilación cuando un `Source` apunta a un archivo que no está: el botón sale
  sin ícono y listo. Por eso van dos que existen.)
- El estilo `TextBlock.subtitle` existe (línea 113) — es el mismo del título
  "PANTALLA". Se le pisa el `Margin` para despegarlo del grid de arriba.
- Los botones `sbtn` **no cierran el submenú** al tocarlos: el code-behind
  (`MenuIzquierda.axaml.cs`) excluye a propósito la clase `sbtn`, porque hay
  acciones que se repiten (brillo +/−). El menú queda abierto después de elegir
  el modo, que es lo que se quiere.

- [ ] **Step 2: Compilar**

Run: `dotnet build "SourceCode/PilotX.Cockpit.Bars/PilotX.Cockpit.Bars.csproj" -c Debug`
Expected: Build succeeded, 0 errores, 0 advertencias nuevas.

- [ ] **Step 3: Correr la batería del proyecto**

Run: `dotnet test "SourceCode/PilotX.Cockpit.Bars.Tests/PilotX.Cockpit.Bars.Tests.csproj"`
Expected: PASS. Ningún test existente se rompe.

- [ ] **Step 4: Commit**

```bash
git add SourceCode/PilotX.Cockpit.Bars/Views/MenuIzquierda.axaml
git commit -m "feat(cabina): elegir el modo de lectura desde el submenu Pantalla

Dos botones, Banderillero y Piloto, junto a dia/noche y brillo — la misma
familia: como se ve la pantalla. Queda a dos toques del mapa, sin entrar a
Configuracion. El contenido del submenu pasa a un StackPanel porque un
UniformGrid no deja que el rotulo del grupo ocupe las dos columnas.

La linea de ayuda de abajo no es decoracion: los nombres le dicen al operario
en que situacion sirve cada uno, pero prometen algo que el modo NO hace (el
piloto se engancha en los dos), y esa linea es lo que evita que el nombre
mienta.

Los botones no se marcan como activos: el VM de este menu no recibe snapshots
y se instancia en tres hosts, dos con otro tipo de snapshot. Marcarlo en uno
solo dejaria a los otros mostrando un modo que puede no ser el puesto. El
indicador son las luces en el mapa.

Prueba: Lightbar"
```

---

### Task 3: Sacar lo viejo y lo que no hace nada

**Files:**
- Modify: `SourceCode/PilotX.UI/Views/DireccionPanel.cs` (la sección "Barra de guiado", alrededor de las líneas 475-489)
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/direccion.html` (alrededor de las líneas 610-626)
- Modify: `SourceCode/AgOpenGPS.Core/Properties/Settings.cs` (el campo `setMenu_isLightbarNotSteerBar`)

**Interfaces:**
- Consumes: nada.
- Produces: nada.

Tres cosas en el mismo lugar:

1. **El tilde "Mostrar barra en pantalla"** se saca de las dos pantallas. Ahora
   vive en el submenú Pantalla; si quedara acá habría **tres** controles para lo
   mismo con **dos** nombres.
2. **El selector "Tipo de barra" (Lightbar / Steer Bar)** se saca: **no hace
   nada**. Verificado — `setMenu_isLightbarNotSteerBar` aparece sólo en
   `SteerConfigService` (que lo lee y lo escribe) y en su DTO. Nadie decide nada
   con él. Era una mentira invisible mientras no se dibujaba ninguna barra;
   ahora que las luces sí aparecen, un selector que dice "Lightbar" al lado y no
   las controla es peor.
3. **El texto de la sensibilidad** dice "cm/px … cada **pixel**". Desde las luces
   ese número son **centímetros por luz**.

**Lo que NO se saca**, porque sí tiene consumidor (verificado): grosor de línea
(`CABLine.cs:71`), mirada de la barra (`GuidanceEngineHost.Commands.cs:550`) y
distancia de enganche (8 sitios).

- [ ] **Step 1: Sacar los dos controles de la pantalla nativa**

En `DireccionPanel.cs`, borrar el bloque del selector de tipo de barra:

```csharp
        _scPantalla.Children.Add(FilaSeg("Tipo de barra", "guidance_bar",
            new[] { ("lightbar", "Lightbar"), ("steerbar", "Steer Bar") },
            "Lightbar: luces de desvío clásicas (a cuántos cm estás de la línea). Steer Bar: muestra " +
            "además el ángulo que el piloto está pidiendo."));
```

y el del tilde:

```csharp
        _scPantalla.Children.Add(FilaToggle("Mostrar barra en pantalla", "display_lightbar",
            "Muestra u oculta la barra de guiado arriba del mapa."));
```

- [ ] **Step 2: Corregir el texto de la sensibilidad en la pantalla nativa**

Reemplazar:

```csharp
        _scPantalla.Children.Add(FilaAjuste("Sensibilidad de la barra", "cm_per_pixel", 2, 20, 1, 0, "cm/px",
            "Cuántos cm de desvío representa cada pixel de la barra. Menos = barra más sensible."));
```

por:

```csharp
        _scPantalla.Children.Add(FilaAjuste("Centímetros por luz", "cm_per_pixel", 2, 20, 1, 0, "cm",
            "Cuántos centímetros de desvío representa cada luz de la barra del modo Banderillero. Menos = barra más sensible."));
```

La clave `cm_per_pixel` **no se toca**: es el contrato con el DTO y con
`Settings`. Lo que cambia es lo que lee el operario.

- [ ] **Step 3: Hacer lo mismo en la pantalla web**

En `direccion.html`, sacar el bloque del segmentado "Tipo de barra" y el del
tilde, y corregir el texto de la sensibilidad igual que en el Step 2.

Ubicalos primero:

```
grep -n "guidance_bar\|display_lightbar\|displayLightbar\|cm_per_pixel\|cm/px" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/direccion.html
```

Mirá el HTML alrededor antes de borrar para no dejar un contenedor vacío ni un
separador huérfano.

- [ ] **Step 4: Marcar el setting como sin uso**

El campo **se deja** en `Settings.cs` —es estado guardado en equipos reales y
borrarlo no aporta— pero se anota. Agregar arriba de su declaración:

```csharp
        // SIN USO desde 2026-09-16: el selector "Tipo de barra" se saco de la UI
        // porque no hacia nada (solo se leia y se escribia a si mismo, via
        // SteerConfigService). Se conserva el campo para no romper los settings
        // ya guardados en equipos reales. Si algun dia se implementa Steer Bar,
        // aca esta el flag.
```

`SteerConfigService` y el DTO **se dejan como están**: siguen leyendo y
escribiendo el campo, y tocarlos no aporta nada a este trabajo.

- [ ] **Step 5: Compilar**

Run: `dotnet build "SourceCode/PilotX.UI/PilotX.UI.csproj" -c Debug`
Expected: Build succeeded, 0 errores. Hay **16 advertencias preexistentes** en
otros archivos de ese proyecto; ninguna nueva en `DireccionPanel.cs`.

Run: `dotnet build "SourceCode/AgOpenGPS.Core/AgOpenGPS.Core.csproj" -c Debug`
Expected: Build succeeded, 0 errores, 0 advertencias nuevas.

- [ ] **Step 6: Commit**

```bash
git add SourceCode/PilotX.UI/Views/DireccionPanel.cs SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/direccion.html SourceCode/AgOpenGPS.Core/Properties/Settings.cs
git commit -m "refactor(direccion): sacar el tilde duplicado y el selector que no hacia nada

 - Mostrar barra en pantalla sale de las dos pantallas de Direccion: ahora el
   modo se elige en el submenu Pantalla. Si quedaba, habia TRES controles para
   lo mismo con DOS nombres.
 - Tipo de barra (Lightbar/Steer Bar) sale: verificado que no tiene ningun
   consumidor, solo se lee y se escribe a si mismo. Era una mentira invisible
   mientras no se dibujaba ninguna barra; ahora que las luces SI aparecen, un
   selector que dice Lightbar al lado y no las controla es peor.
 - Sensibilidad de la barra (cm/px, cada pixel) pasa a Centimetros por luz:
   desde las luces de banderillero ese numero son cm por LUZ.

Grosor de linea, mirada de la barra y distancia de enganche SI tienen consumidor
y se quedan. El campo del selector se conserva en Settings, anotado como sin uso,
para no romper los settings ya guardados en equipos reales.

Prueba: Lightbar"
```

---

### Task 4: Manual de cabina

**Files:**
- Modify: `SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

**Interfaces:**
- Consumes: el comportamiento de las tareas 1 a 3.
- Produces: nada.

Lo dispara la regla del `CLAUDE.md`: **la ruta del manual cambió** (el control se
movió de pantalla) y aparece un concepto nuevo que el operario ve.

- [ ] **Step 1: Encontrar lo que hoy dice el manual**

Run: `grep -n -i "banderillero\|Mostrar barra en pantalla\|Barra de guiado" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

La sección de las luces ya existe (la agregó el trabajo del 2026-09-15) y trae la
ruta vieja. Esa ruta **ya no existe**: hay que reemplazarla, no agregar una
segunda.

- [ ] **Step 2: Actualizar la ruta y explicar el modo**

Reemplazar la línea `<p class="ruta">` de la sección de luces por:

```html
          <p class="ruta"><b>Menú izquierdo</b> <i>›</i> <b>Pantalla</b> <i>›</i> <b>Banderillero</b></p>
```

Y agregar, al principio de esa sección, la explicación del modo:

```html
          <p>Hay dos formas de leer cuánto te fuiste de la línea, y elegís cuál querés desde <strong>Menú izquierdo › Pantalla</strong>:</p>
          <ul>
            <li><strong>Banderillero</strong> — la barra de luces, para manejar a mano.</li>
            <li><strong>Piloto</strong> — el recuadro <strong>A LA LÍNEA</strong> con los centímetros en grande.</li>
          </ul>
          <div class="clave">Lo que elegís <strong>queda puesto</strong> hasta que lo cambies. Y el <strong>piloto automático se engancha igual en los dos</strong>: el modo es cómo querés leer el desvío, no si usás el piloto.</div>
```

Si la clase `clave` no existe en `ayuda.html`, usá la que esa página ya use para
destacar una idea (mirá las otras secciones) y decí en el reporte cuál usaste.

- [ ] **Step 3: Verificar que el manual no quede mintiendo en otro lado**

Run: `grep -n -i "Mostrar barra en pantalla\|cm/px\|Tipo de barra\|Steer Bar" SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`

Cada resultado apunta a algo que este trabajo sacó o renombró. Corregí los que
aparezcan y decí en el reporte cuáles encontraste.

- [ ] **Step 4: Confirmar la ruta contra la UI real**

Los rótulos del manual tienen que coincidir con lo que muestra la app.
Verificá **"PANTALLA"**, **"Banderillero"** y **"Piloto"** en
`SourceCode/PilotX.Cockpit.Bars/Views/MenuIzquierda.axaml`.

Si alguno difiere, escribí el de la UI y reportá la diferencia. En este trabajo
ya hubo dos briefs con rutas mal.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html
git commit -m "docs(ayuda): modo banderillero / modo piloto"
```

---

## Verificación final (en banco, antes de declarar nada)

1. Entrar a **Menú izquierdo › Pantalla**: aparecen los dos botones bajo el
   rótulo "CÓMO LEER EL DESVÍO", con la línea de ayuda debajo, y el submenú no
   queda desbordado en la resolución de la pantalla de cabina.
2. Tocar **Banderillero** con una guía activa → aparecen las luces y desaparece
   el recuadro "A LA LÍNEA". Tocar **Piloto** → al revés.
3. **Cerrar PilotX y volver a abrirlo** → el modo elegido sigue puesto. Es lo
   que se pidió y lo único que no se puede verificar sin reiniciar.
4. Con el piloto **enganchado**, cambiar de modo → el piloto **sigue enganchado**
   en los dos. Si se desengancha, es un bug.
5. Entrar a **Configuración › Dirección › Pantalla** → ya no está el tilde ni el
   selector de tipo de barra, y la sensibilidad dice "Centímetros por luz".
