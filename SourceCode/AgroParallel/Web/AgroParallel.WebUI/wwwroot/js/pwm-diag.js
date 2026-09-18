// ============================================================================
// pwm-diag.js — Diagnóstico: PWM manual a un motor QuantiX y vueltas en vivo.
//
// Para qué existe: cuando "Medir Max Hz" da un número bajo hay dos culpables
// posibles — el PWM no llega al motor, o el motor no responde al PWM. Esta
// pantalla los separa: manda un PWM fijo y muestra lado a lado el PWM pedido,
// el PWM que el nodo dice haber aplicado y las vueltas medidas. Si pedido y
// aplicado coinciden pero las vueltas no suben, el problema es del motor / su
// alimentación, no del software.
//
// Usa los mismos endpoints que la pestaña Prueba del Hub (verb=test), así que
// el formato del mensaje MQTT es exactamente el que espera el firmware.
// ============================================================================
(function () {
  'use strict';

  var API = '/api/quantix/';
  var el = function (id) { return document.getElementById(id); };

  var state = {
    uid: null,
    mi: 0,
    pedido: null,     // último PWM enviado (null = motor parado)
    corriendo: false, // barrido en curso
    nodos: []
  };

  // ── Comunicación ──────────────────────────────────────────────────────────

  async function live() {
    var res = await fetch(API + 'live', { cache: 'no-store' });
    return await res.json();
  }

  // start/stop con el mismo payload que usa la prueba del Hub.
  async function enviarPwm(pwm) {
    var payload = pwm > 0
      ? { cmd: 'start', id: state.mi, pwm: pwm }
      : { cmd: 'stop', id: state.mi, pwm: 0 };
    var res = await fetch(API + encodeURIComponent(state.uid) + '/cmd?verb=test&retain=false', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    return await res.json();
  }

  function motorLive(data) {
    if (!data || !data.nodos) return null;
    for (var i = 0; i < data.nodos.length; i++) {
      if (data.nodos[i].uid !== state.uid) continue;
      var ms = data.nodos[i].motors_live || [];
      for (var k = 0; k < ms.length; k++)
        if ((ms[k].id | 0) === state.mi) return ms[k];
    }
    return null;
  }

  // ── Pantalla ──────────────────────────────────────────────────────────────

  function setEstado(texto, clase) {
    var pill = el('pdEstado');
    pill.className = 'pill ' + (clase || 'idle');
    el('pdEstadoText').textContent = texto;
  }

  function setPwmCampo(v) {
    v = Math.max(0, Math.min(4095, Math.round(v || 0)));
    el('pdPwm').value = v;
    el('pdSlider').value = v;
    return v;
  }

  function pintarLive(m) {
    el('pdKpiPedido').textContent = state.pedido == null ? '—' : state.pedido;
    if (!m) {
      el('pdKpiAplicado').textContent = '—';
      el('pdKpiRpm').textContent = '—';
      el('pdKpiPps').textContent = '—';
      return;
    }
    el('pdKpiAplicado').textContent = (m.pwm | 0);
    el('pdKpiRpm').textContent = (m.rpm | 0);
    el('pdKpiPps').textContent = (m.pps_real || 0).toFixed(0);
  }

  var filas = [];

  function pintarTabla() {
    var tb = el('pdTabla');
    if (!filas.length) {
      tb.innerHTML = '<tr><td colspan="4" style="text-align:center;color:var(--agp-text-muted)">' +
        'Sin mediciones todavía</td></tr>';
      return;
    }
    var mejor = 0;
    filas.forEach(function (f) { if (f.rpm > mejor) mejor = f.rpm; });
    tb.innerHTML = filas.map(function (f) {
      var best = (f.rpm === mejor && mejor > 0) ? ' class="best"' : '';
      return '<tr' + best + '><td>' + f.pedido + '</td><td>' + f.aplicado +
        '</td><td>' + f.rpm + '</td><td>' + f.pps.toFixed(0) + '</td></tr>';
    }).join('');
  }

  function anotar(pedido, m) {
    filas.push({
      pedido: pedido,
      aplicado: m ? (m.pwm | 0) : -1,
      rpm: m ? (m.rpm | 0) : 0,
      pps: m ? (m.pps_real || 0) : 0
    });
    pintarTabla();
  }

  // ── Acciones ──────────────────────────────────────────────────────────────

  async function accionEnviar(pwm) {
    if (!state.uid) { setEstado('Elegí un nodo', 'warn'); return; }
    pwm = setPwmCampo(pwm != null ? pwm : parseInt(el('pdPwm').value, 10));
    try {
      var r = await enviarPwm(pwm);
      if (r && r.ok === false) { setEstado('No se pudo enviar', 'bad'); return; }
      state.pedido = pwm;
      setEstado(pwm > 0 ? 'Motor girando a ' + pwm : 'Motor parado', pwm > 0 ? 'ok' : 'idle');
    } catch (e) {
      setEstado('Error de red', 'bad');
    }
  }

  async function accionParar() {
    try { await enviarPwm(0); } catch (e) {}
    state.pedido = null;
    state.corriendo = false;
    setEstado('Motor parado', 'idle');
  }

  // Barrido: escalón, esperar a que estabilice, anotar el pico del escalón.
  async function accionBarrido() {
    if (state.corriendo) return;
    if (!state.uid) { setEstado('Elegí un nodo', 'warn'); return; }

    var desde = parseInt(el('pdDesde').value, 10) || 0;
    var hasta = parseInt(el('pdHasta').value, 10) || 4095;
    var paso = Math.max(25, parseInt(el('pdPaso').value, 10) || 250);
    var dwell = Math.max(1, parseInt(el('pdDwell').value, 10) || 3);
    if (hasta < desde) { setEstado('El "hasta" tiene que ser mayor', 'warn'); return; }

    state.corriendo = true;
    el('pdBarrer').disabled = true;
    filas = [];
    pintarTabla();

    try {
      for (var pwm = desde; pwm <= hasta && state.corriendo; pwm += paso) {
        await accionEnviar(pwm);
        setEstado('Barriendo… PWM ' + pwm, 'ok');
        // 1s de arranque + dwell de muestreo, nos quedamos con el mejor.
        await new Promise(function (r) { setTimeout(r, 1000); });
        var mejor = null;
        for (var t = 0; t < dwell * 3 && state.corriendo; t++) {
          await new Promise(function (r) { setTimeout(r, 333); });
          var m = motorLive(await live());
          if (m && (!mejor || (m.rpm | 0) > (mejor.rpm | 0))) mejor = m;
        }
        anotar(pwm, mejor);
      }
    } catch (e) {
      setEstado('Barrido cortado: ' + e.message, 'bad');
    } finally {
      el('pdBarrer').disabled = false;
      await accionParar();
    }
  }

  // ── Arranque ──────────────────────────────────────────────────────────────

  async function cargarNodos() {
    var data;
    try { data = await live(); } catch (e) { setEstado('Sin conexión con PilotX', 'bad'); return; }
    state.nodos = (data && data.nodos) || [];
    var sel = el('pdNodo');
    if (!state.nodos.length) {
      sel.innerHTML = '<option value="">— no hay nodos en línea —</option>';
      setEstado('Sin nodos QuantiX en línea', 'warn');
      return;
    }
    sel.innerHTML = state.nodos.map(function (n) {
      return '<option value="' + n.uid + '">' + n.uid + (n.online ? '' : ' (offline)') + '</option>';
    }).join('');
    state.uid = state.nodos[0].uid;
  }

  function conectarEventos() {
    el('pdNodo').addEventListener('change', function () { state.uid = this.value; });
    el('pdMotor').addEventListener('change', function () { state.mi = parseInt(this.value, 10) || 0; });

    el('pdSlider').addEventListener('input', function () { el('pdPwm').value = this.value; });
    el('pdPwm').addEventListener('input', function () { el('pdSlider').value = this.value; });

    el('pdEnviar').addEventListener('click', function () { accionEnviar(); });
    el('pdParar').addEventListener('click', function () { accionParar(); });
    el('pdBarrer').addEventListener('click', function () { accionBarrido(); });
    el('pdLimpiar').addEventListener('click', function () { filas = []; pintarTabla(); });

    document.querySelectorAll('[data-delta]').forEach(function (b) {
      b.addEventListener('click', function () {
        var v = setPwmCampo((parseInt(el('pdPwm').value, 10) || 0) + parseInt(this.getAttribute('data-delta'), 10));
        // Si el motor ya está girando, el paso se aplica en el acto: así se
        // busca el punto donde deja de responder sin frenar y arrancar.
        if (state.pedido != null) accionEnviar(v);
      });
    });
    document.querySelectorAll('[data-set]').forEach(function (b) {
      b.addEventListener('click', function () {
        var v = setPwmCampo(parseInt(this.getAttribute('data-set'), 10));
        if (state.pedido != null) accionEnviar(v);
      });
    });

    // Red de seguridad: si se cierra o se navega afuera, el motor se para.
    window.addEventListener('beforeunload', function () {
      if (state.pedido != null && navigator.sendBeacon) {
        navigator.sendBeacon(API + encodeURIComponent(state.uid) + '/cmd?verb=test&retain=false',
          new Blob([JSON.stringify({ cmd: 'stop', id: state.mi, pwm: 0 })], { type: 'application/json' }));
      }
    });
  }

  async function tick() {
    try { pintarLive(motorLive(await live())); } catch (e) {}
  }

  document.addEventListener('DOMContentLoaded', async function () {
    await cargarNodos();
    conectarEventos();
    setInterval(tick, 400);
    tick();
  });
})();
