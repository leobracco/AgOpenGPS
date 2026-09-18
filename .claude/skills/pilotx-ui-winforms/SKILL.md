---
name: pilotx-ui-winforms
description: Convenciones de diseño nativo de PilotX / Agro Parallel para la capa C# WinForms (Forms, overlays live sobre la pantalla principal, controles nativos AOG). Usar al crear o retocar UI nativa — toolbars, botones, overlays de productos X-*, popups. Mantiene la misma identidad clara/suave/gris/funcional que el Hub web y respeta branding y reglas de producto.
---

# PilotX · Diseño nativo WinForms (Agro Parallel)

La parte nativa de PilotX (WinForms sobre AOG) convive con el Hub web. Debe transmitir la
**misma identidad**: clara, simple, suave, gris y funcional. El Hub WebView es para config
de productos X-*/cloud; el guiado, settings de vehículo/implemento y los **overlays live**
sobre la pantalla principal se quedan en la UI nativa.

## Identidad visual (espejo del sistema web)

- Misma paleta conceptual: **fondo claro, superficies blancas, verde Agro Parallel como
  acento, grises suaves como estructura, texto carbón**. Evitar oscuro/saturado/decorativo.
- Verde de acento de referencia: `#4BA63F` (acciones/estado OK). Estados: verde OK,
  amarillo advertencia, rojo error, gris idle.
- Centralizar colores/fuentes en un punto reutilizable (constantes/helper de tema), no
  esparcir hex mágicos por cada Form.
- Controles **compactos y legibles**, jerarquía clara. Nada gigante ni con sombras pesadas.

## Overlays live (productos X-*)

- Cada producto X-* (QuantiX/VistaX/FlowX...) tiene **config en el Hub** y un **toggle en la
  pantalla principal de PilotX** para mostrar/ocultar su **overlay live**.
- El overlay se auto-prende al activar un implemento con nodos de ese perfil (nunca se
  apaga solo). `FormGPS` relee `overlayPrefs.json` periódicamente.
- Overlays = lectura rápida en movimiento: alto contraste sobre el mapa, datos grandes
  pero sin tapar el guiado, **unidades agronómicas** (nunca PPS).

## Toolbar / iconos (reglas vigentes)

- Engranaje (settings) **off** si no hay GPS.
- FieldTools **off** si no hay lote abierto.
- Tools (SpecialFunctions) **SIEMPRE activo**.
- Los botones de productos X-* / acceso al Hub van dentro de **Tools**.

## Reglas de producto (idénticas al Hub)

- **Táctil**: targets cómodos para dedo/guante; nada crítico escondido en menús finos o
  atajos de teclado.
- **Teclado virtual propio** cuando se necesite texto; **nunca** `osk.exe`.
- **Unidades agronómicas, nunca PPS** al operario (kg/h, sem/m, sem/ha, rpm, km/h, PWM).
- **Nombres de producto, no de hardware** ("nodo QuantiX", no "ESP32/Teensy/PCB").
- **Errores**: código AGP + mensaje amistoso; detalle técnico colapsado/secundario.
  Usar el mapeo `AgpErrorMapper.FromException`.

## Branding (crítico)

En **textos visibles, comentarios nuevos, logs y mensajes**: AOG → **PilotX**,
AgOpenGPS → **Agro Parallel**, AgIO → **CoreX**. **NO** tocar namespaces, nombres de
clase, ni .csproj (el rebrand es de cara al usuario, no estructural).

## Notas de arquitectura

- PilotX.Desktop (rewrite nativo Avalonia) está **pausado/spike**: los productos X-* siguen
  sobre **AOG WinForms + CoreX**. No migrar a Avalonia sin confirmación explícita.
- Idioma: castellano rioplatense en todo lo de cara al usuario y comentarios nuevos;
  terminología técnica (funciones, variables, librerías) en inglés.

## Checklist antes de dar por hecho un cambio nativo

1. ¿Look claro/suave/gris, verde solo de acento? ¿Nada oscuro/pesado?
2. ¿Colores/fuentes desde un punto central, sin hex mágicos repartidos?
3. ¿Compacto y legible, jerarquía clara, táctil?
4. ¿Overlays: auto-open por perfil, no tapan guiado, unidades agronómicas?
5. ¿Toolbar respeta los gates (GPS/lote/Tools-siempre)?
6. ¿Branding PilotX en textos/logs sin tocar namespaces? ¿Nombres de producto?
