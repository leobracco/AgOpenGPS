# Luces de banderillero

Fecha: 2026-09-15
Repo: PilotX (`CentriX-Spark/.../AgOpenGPS`)

Barra de luces para manejar a mano siguiendo la guía, como las barras de
banderillero. Se prende con un tilde, muestra los centímetros de desvío y hacia
qué lado corregir.

---

## 0. Qué hay hoy (y por qué esto no es empezar de cero)

**Las luces ya están escritas, pero nunca se dibujan.**
`MapGlSurface.DrawLightbar()` existe y hace lo correcto: pista con ticks,
marcador que se mueve al lado opuesto al error, verde/amarillo/rojo. Está muerto
por la regla de exclusión de `MainWindow.axaml.cs:6805`:

```csharp
_mapHost?.SetLightbarVisible(!visible);   // visible = hayGuia
```

- **Con** guía → se muestra el cluster "A LA LÍNEA", el lightbar se apaga.
- **Sin** guía → el lightbar se prende, pero `_xte` es `NaN` y `DrawLightbar`
  corta en su primera línea (`MapGlSurface.cs:1810`).

Las dos ramas terminan sin luces, siempre.

**Tres settings huérfanos.** `setMenu_isLightbarOn`, `setDisplay_lightbarCmPerPixel`
y `setMenu_isLightbarNotSteerBar` se leen y escriben en `SteerConfigService.cs`
y tienen controles en la pantalla de Dirección — pero **nadie los consume**.
`DrawLightbar` usa constantes hardcodeadas (`fullScale = 0.5`, `halfW = 0.42`).
El tilde "Mostrar barra en pantalla" que el operario ve hoy no hace nada.

**Lo que sí funciona** es el cluster "A LA LÍNEA" (`MainWindow.axaml:563-584`):
número grande de cm, flecha del lado a corregir, colores por umbral. Es Avalonia,
no GL.

---

## 1. El tilde manda solo

Las luces se prenden y se apagan **con el tilde, y nada más**. No dependen del
estado del piloto.

Esto corrige un malentendido de la primera pasada del diseño: "cuando no es
piloto automático" es el **para qué** de la función (manejar a mano siguiendo la
guía), no una condición de código. Si el operario tilda las luces teniendo el
piloto puesto, las ve.

El tilde reusa **`setMenu_isLightbarOn`** — el "Mostrar barra en pantalla" que ya
existe en Configuración › Dirección › Barra guía y hoy miente. Hacerlo funcionar
arregla la config huérfana en vez de sumar un control nuevo al lado de uno roto.

Fuera de alcance: el selector **lightbar/steerbar**
(`setMenu_isLightbarNotSteerBar`). "Steerbar" es otro widget que tampoco existe;
meterlo acá duplica el trabajo. Queda huérfano y anotado.

---

## 2. El widget va en Avalonia, no en OpenGL

El lightbar actual vive en GL, y dibujar texto en GL es caro y feo. Los
centímetros son un requisito, así que el widget nuevo se hace en **Avalonia**,
como control hermano del cluster "A LA LÍNEA", que ya vive ahí y ya está
posicionado sobre el mapa.

Gana: los cm salen con un `TextBlock`, el widget es testeable, y no se toca el
pipeline de render.

**Consecuencia: se borra el lightbar GL.** `DrawLightbar()`
(`MapGlSurface.cs:1803-1860`), `SetLightbarVisible` en `MapGlSurface.cs:258`,
`MapPanel.cs:389-394` y su llamada en `MainWindow.axaml.cs:6805`, más el campo
`_lightbarOn` de las dos clases. Es código muerto **verificado** (sección 0), y
es el código que este trabajo reemplaza — no es refactor ajeno.

---

## 3. Las luces

**15 luces**: 7 + centro + 7.

Se encienden **desde el centro hacia el lado al que hay que ir**. Si el tractor
se fue a la derecha de la línea, se prenden las luces de la **izquierda**: le
dicen al operario para dónde corregir, no dónde está. Es el mismo criterio que
ya usa la flecha del cluster y el lightbar GL, y el de las barras físicas.

**5 cm por luz**, de `setDisplay_lightbarCmPerPixel` (default 5) — el segundo
setting huérfano, que así revive y queda configurable desde Dirección. A fondo de
escala son 35 cm por lado. Más allá de eso las 7 quedan prendidas y el número
sigue subiendo.

**La luz central es aparte y no cuenta como luz de desvío.** Marca el cero, está
siempre visible, y se pinta:

- **verde** cuando el desvío es menor a una luz (< 5 cm): es la señal de
  "estás en la línea", y en ese estado no hay ninguna lateral prendida;
- **apagada** (gris de pista) en cuanto se prende la primera lateral, para que
  el ojo siga al grupo que se mueve y no al centro.

Debajo, la flecha y el número: `◀ 23 cm`. Bajo 100 cm va en centímetros sin
decimales; de ahí en adelante, en metros con un decimal — mismo criterio que ya
usa el cluster (`MainWindow.axaml.cs:6924-6936`), para no inventar un formato
nuevo.

---

## 4. Escala de cuatro colores

Cuatro bandas. Con la escala por defecto (5 cm por luz) los cortes caen justo
**al prenderse una luz** y no en el medio, que es como tiene que verse:

| Desvío | Laterales prendidas | Color | Hex |
|---|---|---|---|
| 0-5 cm | ninguna (sólo la central) | Verde | `#4ABA3E` |
| 5-15 cm | 1 a 2 | Amarillo | `#E8C81E` |
| 15-25 cm | 3 a 4 | Naranja | `#F07E12` |
| +25 cm | 5 a 7 | Rojo | `#ED4848` |

Cantidad de laterales = `min(7, floor(|cm| / cmPorLuz))`. Con el default de
5 cm por luz, los cortes de color caen al prenderse la luz 1, la 3 y la 5.
Si el operario cambia los cm por luz, **los cortes de color siguen expresados en
centímetros** (5/15/25), no en número de luces: el color dice cuánto te fuiste de
verdad, y no cambia de significado al reconfigurar la escala.

El verde y el rojo son los que ya usa el cluster. El amarillo se corre de
`#D9A916` (que tira a mostaza) a `#E8C81E`, más limpio, para separarlo del
naranja nuevo.

**Por qué cuatro bandas son seguras acá.** Amarillo y naranja son los dos tonos
más difíciles de distinguir de reojo con sol, y los que se confunden en el
daltonismo rojo-verde (~8% de los hombres). En un número suelto serían
riesgosos. En esta barra no, porque **el color no es el único canal**: la
cantidad de luces prendidas ya codifica la magnitud y se lee sin distinguir un
solo tono. El color es refuerzo.

**El número usa la misma escala que las luces.** Si no, el número diría
"amarillo" mientras las luces están en naranja.

**El cluster "A LA LÍNEA" se alinea a los mismos cuatro cortes.** Hoy tiene tres
(`verde<5, amarillo<20, rojo>=20`, `MainWindow.axaml.cs:6937`). Los dos widgets
nunca se ven juntos, pero al tildar y destildar las luces el mismo desvío
cambiaría de color, y eso desconcierta. Los cortes viven en **un solo lugar**,
compartido.

---

## 5. Dónde vive la lógica

`PilotX.UI` referencia **únicamente** `PilotX.Cockpit.Bars`, y ese proyecto tiene
su propio proyecto de tests (`PilotX.Cockpit.Bars.Tests`). Así que la lógica pura
va ahí:

```
PilotX.Cockpit.Bars/EscalaDesvio.cs
```

Responsabilidad única: dado el XTE en metros y los cm por luz, decir **cuántas
luces** se prenden, **de qué lado** y **de qué color**. Sin nada de Avalonia
adentro, para que se pueda testear sin levantar UI.

El control visual (`LucesBanderillero`) la consume y sólo se ocupa de pintar.

Esta separación es la misma que se usó en el trabajo de linderos y por la misma
razón: la lógica testeable no puede vivir en un proyecto que el test no
referencia.

---

## 6. Convivencia

| Tilde | Qué se ve |
|---|---|
| Puesto | Luces con la flecha y los cm. El cluster "A LA LÍNEA" se oculta. |
| Sin poner | El cluster como hoy, sin ningún cambio de comportamiento. |

Sin guía activa no hay XTE (`NaN`) y no se dibuja nada, con tilde o sin él —
igual que hoy.

---

## 7. El manual se actualiza en el mismo commit

Regla del `CLAUDE.md` del repo: aparece una pantalla/flujo nuevo que el operario
ve, y cambia el comportamiento de un tilde que hoy no hace nada.

`SourceCode/AgroParallel/Web/AgroParallel.WebUI/wwwroot/pages/ayuda.html`:
- qué son las luces y cómo se leen (cantidad = cuánto, lado = para dónde ir);
- dónde se prenden, con la ruta real verificada en la UI;
- que andan con o sin piloto puesto.

---

## 8. Qué queda fuera

- El selector **lightbar/steerbar** y el widget "steerbar" (sección 1).
- El barrido de los ~25 `FontSize` sueltos del resto de los paneles, ya anotado
  como fuera de alcance en el spec del 2026-09-14.
- Cualquier cambio a los **umbrales de color del piloto automático**: esto es
  presentación, no control.
