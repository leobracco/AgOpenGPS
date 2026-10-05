// ============================================================================
// pid-lab.js — Laboratorio PID: captura el lazo en vivo, mide cómo responde y
// dice QUÉ parámetro tocar.
//
// Nació de un caso real: 6,1 km/h clavados, objetivo 2,3 sem/m constante, y la
// dosis variando todo el tiempo — o sea el lazo oscilando alrededor de un
// target quieto. Mirar el número saltar no dice si es Kp alto, filtro corto o
// PWM saturado; capturar 60 s y medir el período de la oscilación, sí.
//
// GENÉRICO por diseño: la captura y el análisis trabajan sobre series
// {t, target, real, out, extra} que entrega un ADAPTADOR de fuente. Hoy hay
// adaptadores para los motores QuantiX (pps_target/pps_real/pwm del live);
// agregar FlowX u otro lazo es escribir un adaptador de ~20 líneas acá abajo.
//
// El análisis es heurístico pero honesto — reglas de manual de sintonía:
//   · error medio persistente (bias)            → falta integral (subir Ki)
//   · oscilación sostenida con período corto    → sobra ganancia (bajar Kp/Ki,
//     o amortiguar con Kd si el período lo permite)
//   · PWM saturado en el techo                  → el motor no llega (max_hz /
//     mecánica), el PID no tiene la culpa
//   · real ruidoso con PWM nervioso             → filtro del encoder corto
//     (alpha) o Kd amplificando ruido
// ============================================================================
(function () {
  'use strict';

  var el = function (id) { return document.getElementById(id); };
  var TICK_MS = 250;                 // 4 Hz: alcanza para lazos de dosificación
  var MAX_MUESTRAS = 2400;           // 10 min

  // ── Adaptadores de fuente ────────────────────────────────────────────────
  // Cada adaptador: { id, label, leer: async () => {target, real, out, vel} }
  // target/real en la unidad del lazo; out = salida cruda (PWM 0-4095).

  function adaptadoresQuantiX(nodos) {
    var out = [];
    (nodos || []).forEach(function (n) {
      (n.motors_live || []).forEach(function (m) {
        var mi = m.id | 0;
        out.push({
          id: 'qx:' + n.uid + ':' + mi,
          label: 'QuantiX ' + n.uid.slice(-4) + ' · M' + mi + ' (pps)',
          outMax: 4095,
          leer: async function () {
            var d = await jget('/api/quantix/live');
            var vel = 0;
            try { vel = (await jget('/api/aog/state')).avg_speed || 0; } catch (e) {}
            var nodo = (d.nodos || []).filter(function (x) { return x.uid === n.uid; })[0];
            var mot = nodo ? (nodo.motors_live || []).filter(function (x) { return (x.id | 0) === mi; })[0] : null;
            if (!mot) return null;
            return { target: mot.pps_target || 0, real: mot.pps_real || 0,
                     out: mot.pwm || 0, vel: vel };
          }
        });
      });
    });
    return out;
  }
  // FUTURO FlowX: mismo shape con /api/flowx/live (target/caudal_real/pwm).

  async function jget(u) {
    var r = await fetch(u, { cache: 'no-store' });
    var txt = await r.text();
    return JSON.parse(txt.replace(/^﻿/, ''));
  }

  // ── Estado de captura ────────────────────────────────────────────────────
  var S = { fuente: null, timer: null, datos: [], t0: 0 };

  function setEstado(txt, cls) {
    el('plEstado').className = 'pill ' + (cls || 'idle');
    el('plEstadoTxt').textContent = txt;
  }

  async function poblarFuentes() {
    var sel = el('plFuente');
    sel.innerHTML = '';
    var fuentes = [];
    try {
      var d = await jget('/api/quantix/live');
      fuentes = adaptadoresQuantiX(d.nodos);
    } catch (e) {}
    if (!fuentes.length) {
      sel.innerHTML = '<option value="">— sin lazos en línea —</option>';
      return;
    }
    fuentes.forEach(function (f, i) {
      var o = document.createElement('option');
      o.value = i; o.textContent = f.label;
      sel.appendChild(o);
    });
    sel._fuentes = fuentes;
  }

  function fuenteElegida() {
    var sel = el('plFuente');
    return (sel._fuentes || [])[parseInt(sel.value, 10)] || null;
  }

  async function tick() {
    if (!S.fuente) return;
    try {
      var m = await S.fuente.leer();
      if (m) {
        m.t = (Date.now() - S.t0) / 1000;
        S.datos.push(m);
        if (S.datos.length > MAX_MUESTRAS) S.datos.shift();
        pintar();
        if (S.datos.length % 8 === 0) analizar();   // cada ~2 s
      }
    } catch (e) { /* tick perdido: el próximo sigue */ }
  }

  el('plStart').addEventListener('click', function () {
    var f = fuenteElegida();
    if (!f) { el('plMsg').textContent = '✕ elegí un lazo'; el('plMsg').className = 'send-msg err'; return; }
    S.fuente = f; S.datos = []; S.t0 = Date.now();
    if (S.timer) clearInterval(S.timer);
    S.timer = setInterval(tick, TICK_MS);
    setEstado('Capturando ' + f.label, 'ok');
    el('plMsg').textContent = '';
  });
  el('plStop').addEventListener('click', function () {
    if (S.timer) { clearInterval(S.timer); S.timer = null; }
    setEstado('Parado (' + S.datos.length + ' muestras)', 'idle');
    analizar();
  });
  el('plClear').addEventListener('click', function () {
    S.datos = []; pintar(); analizar();
  });
  el('plCsv').addEventListener('click', function () {
    if (!S.datos.length) return;
    var filas = ['t_seg;objetivo;real;pwm;vel_kmh'];
    S.datos.forEach(function (m) {
      filas.push([m.t.toFixed(2), m.target, m.real, m.out, (m.vel || 0).toFixed(2)].join(';'));
    });
    var blob = new Blob([filas.join('\n')], { type: 'text/csv' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'pid-captura-' + new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-') + '.csv';
    a.click();
  });

  // ── Gráfico (canvas pelado, sin librerías) ───────────────────────────────
  function pintar() {
    var cv = el('plChart');
    var W = cv.clientWidth, H = cv.clientHeight;
    if (cv.width !== W * 2) { cv.width = W * 2; cv.height = H * 2; } // retina
    var g = cv.getContext('2d');
    g.setTransform(2, 0, 0, 2, 0, 0);
    g.clearRect(0, 0, W, H);
    var d = S.datos;
    if (d.length < 2) return;

    // escala Y sobre target/real; PWM y velocidad se re-escalan aparte.
    var maxY = 1;
    d.forEach(function (m) { maxY = Math.max(maxY, m.target, m.real); });
    maxY *= 1.15;
    var t0 = d[0].t, t1 = d[d.length - 1].t, dt = Math.max(t1 - t0, 1);
    var X = function (t) { return (t - t0) / dt * (W - 10) + 5; };
    var Y = function (v) { return H - 6 - (v / maxY) * (H - 12); };

    function linea(get, color, escala) {
      g.strokeStyle = color; g.lineWidth = 1.5; g.beginPath();
      d.forEach(function (m, i) {
        var v = get(m) * (escala || 1);
        if (i === 0) g.moveTo(X(m.t), Y(v)); else g.lineTo(X(m.t), Y(v));
      });
      g.stroke();
    }
    var outMax = (S.fuente && S.fuente.outMax) || 4095;
    linea(function (m) { return m.out; }, '#5B8DD9', maxY / outMax); // PWM escalado a la banda
    linea(function (m) { return (m.vel || 0) * 10; }, '#C5CFC5', 1);
    linea(function (m) { return m.target; }, '#4ABA3E', 1);
    linea(function (m) { return m.real; }, '#101612', 1);
  }

  // ── Análisis ─────────────────────────────────────────────────────────────
  function stats(arr) {
    if (!arr.length) return { mean: 0, sd: 0 };
    var mean = arr.reduce(function (a, b) { return a + b; }, 0) / arr.length;
    var v = arr.reduce(function (a, b) { return a + (b - mean) * (b - mean); }, 0) / arr.length;
    return { mean: mean, sd: Math.sqrt(v) };
  }

  function analizar() {
    var d = S.datos.filter(function (m) { return m.target > 0.5; }); // solo lazo trabajando
    el('kN').textContent = d.length;
    var tb = el('plTabla');
    var recos = el('plRecos');
    if (d.length < 40) {  // ~10 s
      tb.innerHTML = '<tr><td colspan="7" style="text-align:center;color:var(--agp-text-muted)">Sin datos suficientes (lazo trabajando)</td></tr>';
      return;
    }

    var errores = d.map(function (m) { return m.real - m.target; });
    var errPct = d.map(function (m) { return (m.real - m.target) / m.target * 100; });
    var e = stats(errores), ep = stats(errPct);
    var pwms = d.map(function (m) { return m.out; });
    var p = stats(pwms);
    var outMax = (S.fuente && S.fuente.outMax) || 4095;
    var satPct = pwms.filter(function (v) { return v >= outMax * 0.98; }).length / pwms.length * 100;

    // Oscilación: cruces por cero del error (descontando banda muerta de ruido).
    var banda = Math.max(e.sd * 0.3, 0.5);
    var cruces = [];
    var signo = 0;
    d.forEach(function (m, i) {
      var err = errores[i];
      var s = err > banda ? 1 : (err < -banda ? -1 : 0);
      if (s !== 0 && signo !== 0 && s !== signo) cruces.push(m.t);
      if (s !== 0) signo = s;
    });
    var periodo = 0;
    if (cruces.length >= 4) {
      var difs = [];
      for (var i = 1; i < cruces.length; i++) difs.push(cruces[i] - cruces[i - 1]);
      periodo = stats(difs).mean * 2;  // dos cruces = medio ciclo
    }
    var amplitudPct = ep.sd * 1.41;   // aproximación seno: sd·√2

    el('kBias').textContent = ep.mean.toFixed(1) + ' %';
    el('kRms').textContent = Math.sqrt(ep.mean * ep.mean + ep.sd * ep.sd).toFixed(1) + ' %';
    el('kOsc').textContent = amplitudPct.toFixed(1) + ' %';
    el('kPer').textContent = periodo > 0 ? periodo.toFixed(1) + ' s' : '—';
    el('kSat').textContent = satPct.toFixed(0) + ' %';

    // Tabla por ventanas de 15 s.
    tb.innerHTML = '';
    var ventana = 15;
    var t0 = d[0].t;
    for (var w = 0; ; w++) {
      var seg = d.filter(function (m) { return m.t >= t0 + w * ventana && m.t < t0 + (w + 1) * ventana; });
      if (!seg.length) break;
      var st = stats(seg.map(function (m) { return m.target; }));
      var sr = stats(seg.map(function (m) { return m.real; }));
      var se = stats(seg.map(function (m) { return (m.real - m.target) / m.target * 100; }));
      var sp = stats(seg.map(function (m) { return m.out; }));
      var tr = document.createElement('tr');
      tr.innerHTML = '<td>' + (w * ventana) + '–' + ((w + 1) * ventana) + ' s</td>' +
        '<td>' + st.mean.toFixed(1) + '</td><td>' + sr.mean.toFixed(1) + '</td>' +
        '<td>' + se.mean.toFixed(1) + ' %</td><td>±' + se.sd.toFixed(1) + ' %</td>' +
        '<td>' + sp.mean.toFixed(0) + '</td><td>±' + sp.sd.toFixed(0) + '</td>';
      tb.appendChild(tr);
      if (w > 60) break;
    }

    // ── Recomendaciones ────────────────────────────────────────────────────
    var lista = [];
    if (satPct > 20) {
      lista.push({
        titulo: 'El PWM está saturado el ' + satPct.toFixed(0) + '% del tiempo — el motor NO llega al objetivo',
        motivo: 'No es un problema de PID: el lazo pide más de lo que el motor da. Revisá el tope real ' +
          '(Medir tope en QuantiX → Motores), la mecánica (carga, tensión) o bajá la dosis/velocidad.'
      });
    }
    if (Math.abs(ep.mean) > 4 && amplitudPct < Math.abs(ep.mean)) {
      lista.push({
        titulo: 'Error sostenido del ' + ep.mean.toFixed(1) + '% — falta integral',
        motivo: 'El real queda sistemáticamente ' + (ep.mean > 0 ? 'ARRIBA' : 'ABAJO') + ' del objetivo sin oscilar: ' +
          'la parte integral no alcanza a cerrar la diferencia. Subí Ki de a 20-30% y volvé a capturar.'
      });
    }
    if (amplitudPct > 6 && periodo > 0) {
      if (periodo < 3) {
        lista.push({
          titulo: 'Oscilación RÁPIDA (±' + amplitudPct.toFixed(1) + '% cada ' + periodo.toFixed(1) + ' s) — sobra ganancia proporcional',
          motivo: 'El lazo se corrige de más y rebota. Bajá Kp un 25-30%. Si al bajar Kp aparece error sostenido, compensá con un toque de Ki.'
        });
      } else {
        lista.push({
          titulo: 'Oscilación LENTA (±' + amplitudPct.toFixed(1) + '% cada ' + periodo.toFixed(1) + ' s) — sobra integral',
          motivo: 'Vaivén largo típico de Ki alto: la integral acumula de más y tarda en soltar. Bajá Ki un 30% ' +
            'o subí un poco Kd para amortiguar (de a 0.5). El período largo descarta que sea Kp.'
        });
      }
    }
    if (amplitudPct > 3 && periodo === 0 && p.sd > outMax * 0.06) {
      lista.push({
        titulo: 'Real ruidoso sin período claro y PWM nervioso (±' + p.sd.toFixed(0) + ') — puede ser el filtro del sensor',
        motivo: 'Sin oscilación regular, el vaivén parece ruido de medición amplificado. Subí el alpha del filtro ' +
          '(suaviza la lectura) o bajá Kd si lo tenés alto — el derivativo amplifica ruido.'
      });
    }
    if (!lista.length) {
      lista.push(amplitudPct <= 3 && Math.abs(ep.mean) <= 3
        ? { titulo: 'El lazo está sano: error medio ' + ep.mean.toFixed(1) + '%, vaivén ±' + amplitudPct.toFixed(1) + '%',
            motivo: 'Dentro de lo esperable para dosificación. Si igual se ve variar el número en pantalla, es la lectura instantánea — mirá el promedio.' }
        : { titulo: 'Sin patrón claro todavía',
            motivo: 'Capturá más tiempo (60 s o más) con velocidad y dosis estables para que el diagnóstico tenga de dónde agarrarse.' });
    }
    recos.innerHTML = lista.map(function (r) {
      return '<div class="pl-reco"><h4>' + r.titulo + '</h4><div class="motivo">' + r.motivo + '</div></div>';
    }).join('');
  }

  document.addEventListener('DOMContentLoaded', poblarFuentes);
})();
