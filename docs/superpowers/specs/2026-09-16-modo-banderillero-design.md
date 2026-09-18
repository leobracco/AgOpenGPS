# Modo banderillero / Modo piloto

Fecha: 2026-09-16
Repo: PilotX (`CentriX-Spark/.../AgOpenGPS`)

Darle nombre y un lugar decente a algo que ya existe: elegir **cómo se lee el
desvío** arriba del mapa. Hoy es un tilde técnico enterrado en calibración.

---

## 0. De dónde viene esto

El trabajo de las luces de banderillero (spec 2026-09-15) dejó funcionando una
barra de 15 luces que se prende con el tilde **"Mostrar barra en pantalla"**, en
Configuración › Dirección › Pantalla › Barra de guiado.

Dos problemas de producto con eso:

- **El nombre no dice para qué sirve.** "Mostrar barra en pantalla" describe un
  efecto, no un uso. El operario no sabe por qué la querría.
- **El lugar es el equivocado.** Está entre la *distancia de enganche* y el
  *grosor de línea*: cosas que se tocan una vez al instalar la máquina, no en la
  jornada.

Pedido del usuario (2026-09-16): que sea un **modo con nombre** — "banderillero"
o "piloto" — y que **quede puesto hasta que alguien lo cambie**.

---

## 1. Qué es el modo, y qué NO es

El modo cambia **solamente lo que se ve arriba del mapa**:

| Modo | Qué se muestra |
|---|---|
| **Banderillero** | La barra de 15 luces, con la flecha y los centímetros |
| **Piloto** | El recuadro "A LA LÍNEA" de siempre, con el número grande |

**El piloto automático se engancha igual en los dos.** El modo es cómo querés
*leer* el desvío, no si usás el piloto. Decisión del usuario, 2026-09-16.

Eso crea una tensión que hay que atender de frente: los nombres son buenos para
el operario —dicen en qué situación sirve cada uno— pero **prometen algo que no
hacen**. Por eso el selector lleva debajo una línea que lo aclara, y esa línea no
es decorativa: es lo que evita que el nombre mienta.

---

## 2. El dato no cambia

Sigue siendo `setMenu_isLightbarOn`:

- **Banderillero** = `true`
- **Piloto** = `false`

**Sin migración y sin tocar el default.** Ese setting ya persiste, que es
exactamente lo que el usuario pidió: si alguien pone Banderillero, queda hasta
que lo cambie.

De fábrica queda en `false` (Piloto), decidido el 2026-09-15: nadie que instala
tiene que encontrarse una pantalla que no pidió.

**El caso de los equipos ya instalados, dicho con precisión.** En una PC que ya
tenía PilotX, `setMenu_isLightbarOn` está guardado en `true` — pero por el
*default viejo*, no porque alguien lo haya elegido. Esos equipos van a arrancar
en Banderillero. Verificado en vivo el 2026-09-16: `/api/aog/state` de un equipo
real devolvió `"mostrar_luces": true` pese al default nuevo.

Se acepta a propósito y no se migra:

- el modo es reversible con dos toques y queda puesto;
- una migración que apague el modo le pisaría la elección a quien sí lo haya
  elegido, y no hay forma de distinguir un caso del otro;
- el operario que ve luces donde antes veía un número tiene el selector a dos
  toques y el manual explicándolo.

---

## 3. El selector

Va en el submenú **Pantalla** del menú izquierdo del cockpit, que hoy tiene
día/noche y brillo. Es la misma familia — el propio comentario del código
(`MenuIzquierda.axaml:182-183`) dice que ese grupo es *"puro pantalla"*.

Queda a dos toques desde el mapa, sin entrar a Configuración.

```
Menú izquierdo › Pantalla

    Día / Noche          [ Día ] [ Noche ]
    Brillo               [-]  70%  [+]

    Cómo leer el desvío
       [ BANDERILLERO ]  [    Piloto    ]

    Banderillero: barra de luces para manejar a mano.
    Piloto: el número de centímetros.
    El piloto se engancha igual en los dos.
```

El rótulo del grupo es **"Cómo leer el desvío"** — describe la decisión, no el
widget.

---

## 4. Un control, un lugar

El mismo tilde vive hoy en **dos** pantallas de Dirección: la nativa
(`DireccionPanel.cs:480`) y la web (`direccion.html:625`). Si se agrega el
selector sin sacar esos, quedan **tres** controles para lo mismo con **dos**
nombres distintos.

Se saca de las dos. El selector de Pantalla queda como único lugar.

La sección "Barra de guiado" de Dirección conserva lo que sí es calibración y
tiene consumidor real (verificado, ver sección 5): grosor de línea, distancia de
enganche, mirada de la barra y sensibilidad.

---

## 5. Lo que no hace nada, se saca

Instrucción del usuario (2026-09-16): *"lo que no hace nada sacalo"*. Se aplicó
con verificación, no por sospecha. Consumidores reales de cada setting de esa
sección, fuera del servicio que los lee y escribe a sí mismo:

| Control | Consumidor real | Destino |
|---|---|---|
| **Tipo de barra** (Lightbar / Steer Bar) | **ninguno** | **se saca** |
| Grosor de línea | `CABLine.cs:71` | se queda |
| Mirada de la barra | `GuidanceEngineHost.Commands.cs:550` | se queda |
| Distancia de enganche | 8 sitios | se queda |
| Sensibilidad de la barra | el control de luces | se queda |

**Sólo el selector "Tipo de barra" está muerto.** `setMenu_isLightbarNotSteerBar`
aparece únicamente en `SteerConfigService` (que lo lee y lo escribe) y en su DTO.
Nadie decide nada con él. Era una mentira invisible mientras no se dibujaba
ninguna barra; ahora que las luces sí aparecen, un selector que dice "Lightbar"
al lado y no las controla es peor.

Se saca de las dos pantallas de Dirección. El campo en `Settings` **se deja**: es
estado guardado en equipos reales y borrarlo no aporta nada. Se marca como sin
uso con un comentario.

---

## 6. Un texto que quedó mintiendo

`DireccionPanel.cs:488` y `direccion.html:614` describen la sensibilidad como
**"cm/px … cuántos cm de desvío representa cada pixel"**. Desde las luces, ese
número son **centímetros por luz**. Se corrige el rótulo, la unidad y la ayuda en
las dos pantallas.

---

## 7. El manual

`ayuda.html` habla hoy del tilde y de su ruta vieja. Se actualiza:

- la ruta pasa a ser el submenú Pantalla;
- se explica el modo con los dos nombres;
- se mantiene explícito que **el piloto anda en los dos modos**, que es lo que el
  nombre no dice solo.

---

## 8. Fuera de alcance

- **Implementar Steer Bar.** Se saca el selector porque no hace nada; construir
  el widget es otro trabajo.
- **Cambiar el default de fábrica.** Queda en Piloto, como se decidió el
  2026-09-15.
- **Migrar equipos existentes.** Los que tengan el setting en `true` arrancan en
  Banderillero. Es lo que el usuario pidió: si está activo, queda activo.
