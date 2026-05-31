// ============================================================================
// steps.js — paso adaptativo para controles de dosis y PID.
//
// Por qué: no es lo mismo ajustar 5 kg/ha (queremos pasos de 100 g) que
// 150 kg/ha (queremos pasos de 1 kg). Igual para PID: Kd=0.05 pide pasos de
// 0.01 y Kp=120 pide pasos de 1 ó 5.
//
// Uso (vainilla, sin build step):
//   <script src="../js/steps.js"></script>
//
//   var d = AGPSteps.dose(currentValue);  // → 0.1 | 0.25 | 0.5 | 1
//   var p = AGPSteps.pid(currentValue);   // → 0.01 | 0.1 | 1 | 5
//   AGPSteps.bumpDose(value, +1)          // suma un paso adaptativo
//   AGPSteps.fmt(value, step)             // string con los decimales justos
//   AGPSteps.attachAdaptive(inputEl, 'dose')  // engancha un <input>
//
// IMPORTANTE: leer "value" no "step" del input para decidir el paso —
// queremos que la sensibilidad siga el valor *actual*, no el *anterior*.
// ============================================================================

(function (root) {
  'use strict';

  // ── Tablas de paso ────────────────────────────────────────────────────────
  // Editables sin tocar el resto del código. Cada entrada: [umbral, paso].
  // El primer umbral cuyo valor.abs sea menor decide el paso.
  var DOSE_TABLE = [
    [10,   0.1],   //   0–10 kg/ha    → 100 g
    [50,   0.25],  //  10–50 kg/ha    → 250 g
    [150,  0.5],   //  50–150 kg/ha   → 500 g
    [Infinity, 1]  //  150+ kg/ha     →   1 kg
  ];

  var PID_TABLE = [
    [1,    0.01],
    [10,   0.1],
    [100,  1],
    [Infinity, 5]
  ];

  function lookup(table, value) {
    var v = Math.abs(Number(value) || 0);
    for (var i = 0; i < table.length; i++) {
      if (v < table[i][0]) return table[i][1];
    }
    return table[table.length - 1][1];
  }

  function dose(value) { return lookup(DOSE_TABLE, value); }
  function pid(value)  { return lookup(PID_TABLE,  value); }

  // Decimales que tiene sentido mostrar para un paso dado.
  // step 0.01 → 2, 0.1 → 1, 0.25 → 2, 0.5 → 1, ≥1 → 0.
  function decimalsFor(step) {
    if (step >= 1)    return 0;
    if (step >= 0.1)  return 1;
    if (step >= 0.01) return 2;
    return 3;
  }

  function fmt(value, step) {
    var d = decimalsFor(step);
    var n = Number(value);
    if (!isFinite(n)) return String(value);
    return n.toFixed(d);
  }

  // Suma/resta un paso adaptativo, redondeando al múltiplo del paso para que
  // los clicks sucesivos no acumulen drift de coma flotante.
  function bump(table, value, direction) {
    var v = Number(value) || 0;
    var step = lookup(table, v);
    var next = v + Math.sign(direction || 0) * step;
    if (next < 0) next = 0;
    // Snap al múltiplo del paso para evitar 5.0000001.
    var snapped = Math.round(next / step) * step;
    return parseFloat(snapped.toFixed(6));
  }

  function bumpDose(value, dir) { return bump(DOSE_TABLE, value, dir); }
  function bumpPid(value, dir)  { return bump(PID_TABLE,  value, dir); }

  // Engancha un <input type="range" | "number"> a este sistema:
  //  · al editar el valor, ajusta el atributo step según el nuevo valor
  //  · expone .agpAdaptiveMode='dose'|'pid' para introspección
  // No reemplaza handlers existentes — solo actualiza step.
  function attachAdaptive(input, mode) {
    if (!input) return;
    var fn = mode === 'pid' ? pid : dose;
    input.agpAdaptiveMode = mode;
    var update = function () {
      var s = fn(input.value);
      // step="any" sería más fiel, pero rompe los flechitos del number nativo.
      // Mejor un step concreto que el browser usa para flechas/PageUp/PageDown.
      if (parseFloat(input.step) !== s) input.step = String(s);
    };
    input.addEventListener('input', update);
    input.addEventListener('change', update);
    // Set inicial.
    update();
  }

  // ── Stepper [−] value [+] ──────────────────────────────────────────────────
  // Reemplazo touch-friendly del <input type="range">. Las flechitas son fáciles
  // de tocar en la pantalla del tractor; los sliders no. El paso se calcula
  // sobre el VALOR ACTUAL (adaptativo) para PID/dose, o fijo para 'int'.
  //
  // stepperHTML(opts) devuelve el HTML; bindSteppers(root) engancha los clicks.
  // El value real vive en un <input type="hidden"> que conserva los data-*
  // attrs originales (data-tune, data-f, data-int, etc.) — el resto del código
  // que leía `qsa('input[data-tune="kp"]').value` sigue funcionando igual.
  //
  // opts:
  //   value      Number
  //   min, max   Number (opcional, clamp)
  //   mode       'pid' | 'dose' | 'int' (default 'int')
  //   step       Number (sólo en 'int'; default 1)
  //   attrs      String — atributos HTML extra para el input oculto
  //              ej: 'data-tune="kp"' o 'data-f="pwm_min" data-int="1"'
  function stepperHTML(opts) {
    opts = opts || {};
    var v = Number(opts.value) || 0;
    var mode = opts.mode || 'int';
    var fixedStep = (mode === 'int') ? (Number(opts.step) || 1) : null;
    var curStep = (mode === 'pid') ? pid(v)
                : (mode === 'dose') ? dose(v)
                : fixedStep;
    var minAttr = opts.min != null ? ' min="' + opts.min + '"' : '';
    var maxAttr = opts.max != null ? ' max="' + opts.max + '"' : '';
    var stepAttr = (mode === 'int') ? ' data-fixed-step="' + fixedStep + '"' : '';
    var formatted = (mode === 'int') ? Math.round(v) : fmt(v, curStep);
    return (
      '<div class="agp-stepper" data-mode="' + mode + '"' + stepAttr + '>' +
        '<button type="button" class="agp-step-btn" data-step-dir="-1" aria-label="Bajar">−</button>' +
        '<input type="hidden" ' + (opts.attrs || '') + minAttr + maxAttr + ' value="' + v + '">' +
        '<span class="agp-step-val">' + formatted + '</span>' +
        '<button type="button" class="agp-step-btn" data-step-dir="+1" aria-label="Subir">+</button>' +
      '</div>'
    );
  }

  function stepOnce(stepper, dir) {
    var inp = stepper.querySelector('input');
    var rv  = stepper.querySelector('.agp-step-val');
    var mode = stepper.getAttribute('data-mode') || 'int';
    var cur = parseFloat(inp.value) || 0;
    var next;
    if (mode === 'pid')      next = bumpPid(cur, dir);
    else if (mode === 'dose') next = bumpDose(cur, dir);
    else {
      var fs = parseFloat(stepper.getAttribute('data-fixed-step')) || 1;
      next = cur + dir * fs;
    }
    var minA = inp.getAttribute('min'); if (minA !== null) next = Math.max(parseFloat(minA), next);
    var maxA = inp.getAttribute('max'); if (maxA !== null) next = Math.min(parseFloat(maxA), next);
    inp.value = String(next);
    var stepNow = (mode === 'pid') ? pid(next)
                : (mode === 'dose') ? dose(next)
                : (parseFloat(stepper.getAttribute('data-fixed-step')) || 1);
    if (rv) rv.textContent = (mode === 'int') ? Math.round(next) : fmt(next, stepNow);
    // Disparar 'input' para que cualquier listener delegado (data-show, range-out,
    // commit a state, etc.) reaccione igual que con un slider real.
    inp.dispatchEvent(new Event('input', { bubbles: true }));
    inp.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Idempotente: si el root ya tiene listeners, no los duplicamos.
  function bindSteppers(root) {
    root = root || document;
    if (root.__agpStepperBound) return;
    root.__agpStepperBound = true;

    root.addEventListener('click', function (ev) {
      var btn = ev.target.closest && ev.target.closest('.agp-step-btn');
      if (!btn || !root.contains(btn)) return;
      var stepper = btn.closest('.agp-stepper');
      if (!stepper) return;
      // Si veníamos de autorepeat por pointerdown, no contamos el click final.
      if (stepper.__agpHeld) { stepper.__agpHeld = false; return; }
      var dir = btn.getAttribute('data-step-dir') === '-1' ? -1 : +1;
      stepOnce(stepper, dir);
    });

    // Autorepeat al mantener apretado: primer paso normal por el click, después
    // de 400ms empezamos a repetir cada 80ms. Útil cuando hay que bajar PWM
    // desde 4000 a 200.
    root.addEventListener('pointerdown', function (ev) {
      var btn = ev.target.closest && ev.target.closest('.agp-step-btn');
      if (!btn || !root.contains(btn)) return;
      var stepper = btn.closest('.agp-stepper');
      if (!stepper) return;
      var dir = btn.getAttribute('data-step-dir') === '-1' ? -1 : +1;
      var firstDelay = 400, repeatDelay = 80;
      var t = setTimeout(function tick() {
        stepOnce(stepper, dir);
        stepper.__agpHeld = true;  // skipea el click final asociado a este press
        t = setTimeout(tick, repeatDelay);
      }, firstDelay);
      var cancel = function () {
        clearTimeout(t);
        document.removeEventListener('pointerup', cancel, true);
        document.removeEventListener('pointercancel', cancel, true);
        btn.removeEventListener('pointerleave', cancel, true);
      };
      document.addEventListener('pointerup', cancel, true);
      document.addEventListener('pointercancel', cancel, true);
      btn.addEventListener('pointerleave', cancel, true);
    });
  }

  root.AGPSteps = {
    dose: dose,
    pid: pid,
    decimalsFor: decimalsFor,
    fmt: fmt,
    bumpDose: bumpDose,
    bumpPid: bumpPid,
    attachAdaptive: attachAdaptive,
    stepperHTML: stepperHTML,
    bindSteppers: bindSteppers
  };
})(window);
