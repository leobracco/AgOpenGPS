// ============================================================================
// AgroParallel Field PWA — m.js
// Helpers compartidos por todas las pantallas /m/*.html:
//   * fetch JSON + manejo de errores tipico
//   * toast no bloqueante
//   * PIN gate (client-side, suficiente porque la LAN del tractor es trusted)
//   * registro de service worker para instalabilidad
// ============================================================================

(function (g) {
  'use strict';

  // ---------------------------------------------------------------------------
  // PIN gate: si APP_PIN esta guardado y no hay 'auth' valido en sessionStorage,
  // redirige a /m/login.html. La PWA no hace auth en server: protege contra
  // alguien que entra al wifi del tractor y abre la URL desde su celular.
  // ---------------------------------------------------------------------------
  function pinSet() { return !!localStorage.getItem('apf_pin'); }
  function pinOk()  { return sessionStorage.getItem('apf_auth') === '1'; }
  function pinRequire() {
    if (location.pathname.indexOf('/m/login.html') >= 0) return;
    if (pinSet() && !pinOk()) { location.replace('/m/login.html'); }
  }

  function setPin(p) { localStorage.setItem('apf_pin', p); sessionStorage.setItem('apf_auth', '1'); }
  function checkPin(p) {
    if (p === localStorage.getItem('apf_pin')) {
      sessionStorage.setItem('apf_auth', '1'); return true;
    }
    return false;
  }
  function logout() { sessionStorage.removeItem('apf_auth'); location.replace('/m/login.html'); }
  function clearPin() { localStorage.removeItem('apf_pin'); sessionStorage.removeItem('apf_auth'); }

  // ---------------------------------------------------------------------------
  // Fetch helpers — TIMEOUT obligatorio porque si el wifi se cae, los comandos
  // se cuelgan y el operario no sabe si llego o no.
  // ---------------------------------------------------------------------------
  function withTimeout(p, ms) {
    return new Promise(function (resolve, reject) {
      var t = setTimeout(function () { reject(new Error('timeout')); }, ms || 4000);
      p.then(function (v) { clearTimeout(t); resolve(v); },
             function (e) { clearTimeout(t); reject(e); });
    });
  }
  function getJSON(url, ms) {
    return withTimeout(fetch(url, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    }), ms);
  }
  function postJSON(url, body, ms) {
    return withTimeout(fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: body == null ? '{}' : (typeof body === 'string' ? body : JSON.stringify(body))
    }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json().catch(function () { return { ok: true }; });
    }), ms);
  }
  function putJSON(url, body, ms) {
    return withTimeout(fetch(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: typeof body === 'string' ? body : JSON.stringify(body)
    }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json().catch(function () { return { ok: true }; });
    }), ms);
  }

  // ---------------------------------------------------------------------------
  // Toast — no usa alert() porque rompe el flujo tactil
  // ---------------------------------------------------------------------------
  function toast(msg, kind) {
    var el = document.getElementById('toast');
    if (!el) { el = document.createElement('div'); el.id = 'toast'; document.body.appendChild(el); }
    el.textContent = msg;
    el.className = 'show ' + (kind || '');
    clearTimeout(toast._t);
    toast._t = setTimeout(function () { el.className = ''; }, 2200);
  }

  // ---------------------------------------------------------------------------
  // Status pill en el header — refleja conectividad al WebHost
  // ---------------------------------------------------------------------------
  function setStatus(ok, text) {
    var el = document.querySelector('.hdr .status');
    if (!el) return;
    el.textContent = text || (ok ? 'En linea' : 'Sin conexion');
    el.className = 'status ' + (ok ? 'ok' : 'bad');
  }

  // Ping periodico al WebHost — confirma que la PC del tractor responde
  function startHeartbeat(intervalMs) {
    intervalMs = intervalMs || 5000;
    var tick = function () {
      getJSON('/api/aog/state', 2500)
        .then(function () { setStatus(true); })
        .catch(function () { setStatus(false); });
    };
    tick(); setInterval(tick, intervalMs);
  }

  // ---------------------------------------------------------------------------
  // Boot: registrar SW + aplicar PIN gate
  // ---------------------------------------------------------------------------
  if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/m/sw.js').catch(function () {});
  }
  pinRequire();

  // Exponer API global
  g.APF = {
    getJSON: getJSON,
    postJSON: postJSON,
    putJSON: putJSON,
    toast: toast,
    setStatus: setStatus,
    startHeartbeat: startHeartbeat,
    setPin: setPin,
    checkPin: checkPin,
    pinSet: pinSet,
    pinOk: pinOk,
    logout: logout,
    clearPin: clearPin
  };

})(window);
