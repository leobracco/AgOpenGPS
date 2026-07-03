---
name: pilotx-ui-html
description: Sistema de diseño visual de PilotX / Agro Parallel para pantallas HTML/CSS del Hub (pages/*.html, js/*.js, theme.css/layout.css). Usar SIEMPRE que se diseñe, retoque o cree UI web del tractor — KPIs, tabs, tarjetas, formularios, badges, overlays. Garantiza una interfaz clara, simple, suave, gris y funcional, táctil y on-brand, evitando estética genérica/oscura/decorativa.
---

# PilotX · Sistema de diseño HTML/CSS (Agro Parallel)

UI web del Hub de PilotX (WebView2 sobre EmbedIO :5180), de uso operativo en cabina/campo.
Herramienta profesional agrícola: técnica, confiable, **clara y usable con guantes**.

## Dirección visual (la regla de oro)

Interfaz **clara, simple, suave, gris y funcional**. Si algo se ve **demasiado oscuro,
demasiado grande, demasiado decorativo o desperdicia espacio → simplificar y compactar.**

- **Fondo claro** (`#F5F7F4`), superficies **blancas / blanco humo**.
- **Verde Agro Parallel como ACENTO** de acción/estado, nunca como color dominante.
- **Grises suaves** = lenguaje estructural (bordes, divisores, fondos secundarios, neutros).
- **Texto carbón/negro suave** para máxima legibilidad.
- Tipografía limpia, bordes suaves, mucha claridad, **cero exceso decorativo**.

### Prohibido
- Apariencia **oscura**, gamer, industrial pesada o dashboard genérico.
- Degradados fuertes, brillos, sombras grandes, colores saturados.
- Tarjetas gigantes para datos simples. Botones/tabs enormes. Espacio desperdiciado.

## Tokens — NUNCA hardcodear color/tipografía

Todo color, fuente y tamaño sale de variables `--agp-*` definidas **solo en `theme.css`**.
`theme.css` es la única fuente de verdad de los valores. En markup/JS/otros CSS se usan los
tokens, jamás un hex suelto.

Tokens base (nombres reales): `--agp-bg`, `--agp-bg-soft`, `--agp-surface`,
`--agp-surface-2/3`, `--agp-border`, `--agp-border-strong`, `--agp-text`,
`--agp-text-muted`, `--agp-text-dim`, `--agp-accent` (+`-hover/-press/-soft`),
`--agp-state-ok/warn/bad/idle`, fuentes `--agp-font-sans/-mono`, tamaños `--agp-fs-*`,
pesos `--agp-fw-*`, line-height `--agp-lh-*`.

> Nota de estado: el `theme.css` vivo todavía está en versión oscura ("cockpit"). La
> dirección oficial es CLARA. Al migrar valores, cambian los **valores** de los tokens,
> nunca sus **nombres** ni el markup que los consume.

### Reparto de capas con Codex
Diseño/presentación (theme.css, layout.css, keyboard.css, markup + CSS embebido de
pages/*.html) lo maneja **Codex**. Lógica (js/*.js, controllers, services, DTOs) la
maneja **Claude**. Antes de tocar UI leer `COORDINACION-UI.md` (raíz AgOpenGPS) y anotar
en la bitácora qué pantalla se toca. IDs y atributos `data-*` están **congelados** (§4 del
doc): no renombrar ni quitar; son el contrato JS↔markup.

## Componentes (densidad operativa)

- **Tarjetas** blancas, **compactas**, borde gris claro (`--agp-border`), sombra **muy
  sutil**. Nada de cards grandes para un dato.
- **Botones** táctiles pero **no gigantes**: claros, funcionales, jerarquía evidente
  (primario verde acento, secundarios neutros gris).
- **Tabs** compactos tipo herramienta operativa (no botones enormes). Mecanismo de tabs
  intacto (data-tab); no romper el cambio de pestaña.
- **Inputs/selects** altura moderada, limpios, borde gris claro.
- **Badges/pills** pequeños de estado: verde OK (`--agp-state-ok`), amarillo advertencia
  (`--agp-state-warn`), rojo error (`--agp-state-bad`).
- **Iconografía** lineal, limpia, técnica.

### Densidad
Mucha info visible sin scroll innecesario. Paneles compactos pero legibles. Grillas
ordenadas. Las pestañas **PID live, Calibración y Prueba** deben ser **densas, claras y
fáciles de escanear**.

## Reglas de producto (no negociables)

- **Táctil, sin teclado**: nada importante depende de atajos de teclado; todo en
  sidebar/botón visible. El operario usa los dedos.
- **Teclado virtual propio**: la UI trae su teclado HTML; **nunca** invocar `osk.exe`.
- **Unidades agronómicas, nunca PPS al operario**: mostrar kg/h, sem/m, sem/10m, sem/ha,
  rpm, km/h, PWM. PPS/pulsos solo en diagnóstico técnico, no en pantallas de operación.
- **Nombres de producto, no de hardware**: "nodo QuantiX", nunca "ESP32/PCB-8560/Teensy".
- **Branding**: en textos/UI usar PilotX / Agro Parallel / CoreX (no AOG/AgOpenGPS/AgIO).
  No tocar namespaces/clases/csproj.
- **Errores**: mostrar código AGP + mensaje amistoso; el detalle técnico va en `<details>`.

## Disciplina build/cache

PilotX RELEASE **cachea el `wwwroot` en RAM al arrancar**. Editar JS/HTML/CSS no se ve
hasta: parar PilotX.exe/CoreX.exe → `build.ps1` (copia wwwroot a `Build/`) → relanzar
CoreX, esperar ~3s, luego PilotX. Si Codex tocó CSS/HTML hay que reconstruir antes de
lanzar desde `Build/`, sino se ve la versión vieja.

## Checklist antes de dar por hecho un diseño

1. ¿Colores/fuentes vienen de tokens `--agp-*`? (cero hex suelto)
2. ¿Se ve claro, gris-estructural, verde solo como acento? ¿Nada oscuro/saturado?
3. ¿Compacto y denso, sin desperdiciar espacio ni cards gigantes?
4. ¿Táctil, sin depender de teclado, con teclado virtual propio si hace falta input?
5. ¿Unidades agronómicas (no PPS), nombres de producto, branding PilotX?
6. ¿IDs/`data-*` intactos? ¿Tabs siguen funcionando? ¿Anotado en COORDINACION-UI.md?
