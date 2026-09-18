# Calculadora de semillas/vuelta (motor no directo) — QuantiX WebUI

Fecha: 2026-08-22 · Estado: aprobado (diseño validado en sesión de banco)

## Problema

Cuando el motor no está acoplado directo al dosificador (ej. piñón 11 dientes en
el motor, corona 22 en el dosificador), el campo **sem/vuelta** de la config del
motor debe cargarse con las semillas por vuelta **del eje del motor/encoder**,
no de la placa: `alvéolos × (dientes_motor / dientes_dosificador)`. Hoy esa
cuenta se hace a mano y es fácil equivocarse (cargar los alvéolos directo =
dosis del doble con reducción 2:1).

## Solución

Botón `🧮` junto al campo sem/vuelta en la página QuantiX (visible con unidad
sem/m). Despliega un mini-form inline (estilo de la página, sin modal):

1. Alvéolos de la placa (sem por vuelta de placa)
2. Dientes en el motor (piñón)
3. Dientes en el dosificador (corona)

Vista previa en vivo: `1 vuelta de motor = X vueltas de placa → alvéolos × X =
Y sem/vuelta`. Botón **Usar** pega Y en el campo sem/vuelta (queda dirty y se
guarda/empuja con el guardar normal de la página). Cancelar no toca nada.

## Decisiones

- **Una sola etapa** de transmisión (piñón/corona). Etapas múltiples: YAGNI.
- **No se persisten** los dientes ni los alvéolos (decisión del usuario):
  calculadora al paso, arranca vacía cada vez.
- Validación: 3 enteros > 0; sin eso, "Usar" deshabilitado.
- Solo UI (`wwwroot/js/quantix.js` + estilos si hacen falta en `quantix.html`).
  Sin cambios de backend, DTOs ni firmware: el resultado viaja por el campo
  `semillas_vuelta` existente.

## Verificación

Manual en la página QuantiX: 26 alvéolos, 11/22 → 13.0; "Usar" deja 13 en el
campo; guardar empuja la config al nodo (visible en topic `config`/`desired`).
