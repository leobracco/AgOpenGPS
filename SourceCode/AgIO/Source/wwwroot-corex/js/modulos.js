// ============================================================================
// modulos.js — Página de módulos CoreX (IMU / Steer / Machine).
//
// Lógica:
//   · GET /api/corex/config/modulos para cargar el estado inicial (configured).
//   · Cambio de toggle → POST inmediato con los 3 estados → releer GET.
//   · Polling @2s de GET /api/corex/status para refrescar dots hello vivos.
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  var togSteer   = $('tog-steer');
  var togMachine = $('tog-machine');
  var togImu     = $('tog-imu');
  var dotSteer   = $('dot-steer');
  var dotMachine = $('dot-machine');
  var dotImu     = $('dot-imu');

  var busy = false; // evita requests en paralelo mientras el usuario hace clic rápido

  // ── Dot helper ─────────────────────────────────────────────────────────────
  function setDot(el, state) {
    if (!el) return;
    el.classList.remove('on', 'bad');
    if (state === true)  el.classList.add('on');
    else if (state === false) el.classList.add('bad');
    // null/undefined → gris neutro
  }

  // ── Bloquear / desbloquear toggles durante requests ───────────────────────
  function lockToggles(locked) {
    togSteer.disabled   = locked;
    togMachine.disabled = locked;
    togImu.disabled     = locked;
  }

  // ── Cargar estado configurado inicial ─────────────────────────────────────
  // Los toggles arrancan bloqueados y solo se habilitan cuando el GET inicial
  // trae el estado real. Sin esto, un GET fallido dejaría los 3 toggles en
  // false y el primer toque apagaría módulos que estaban prendidos.
  var configLoaded = false;

  function loadConfig() {
    fetch('/api/corex/config/modulos', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        togSteer.checked   = !!d.steer;
        togMachine.checked = !!d.machine;
        togImu.checked     = !!d.imu;
        configLoaded = true;
        lockToggles(false);
      })
      .catch(function () {
        AgpModal.alert('Error de red',
          'No se pudo cargar la configuración de módulos. Reintentando…');
        setTimeout(loadConfig, 3000);
      });
  }

  // ── Guardar los 3 estados en POST y releer ────────────────────────────────
  function saveAndRefresh() {
    if (busy || !configLoaded) return;
    busy = true;
    lockToggles(true);

    var payload = {
      imu:     togImu.checked,
      steer:   togSteer.checked,
      machine: togMachine.checked,
    };

    fetch('/api/corex/config/modulos', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
      .then(function (r) { return r.json(); })
      .then(function () {
        // Releer para confirmar el estado persistido.
        return fetch('/api/corex/config/modulos', { cache: 'no-store' });
      })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        togSteer.checked   = !!d.steer;
        togMachine.checked = !!d.machine;
        togImu.checked     = !!d.imu;
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo guardar la configuración de módulos.');
      })
      .finally(function () {
        busy = false;
        lockToggles(false);
      });
  }

  // ── Listeners: un toggle cambiado → POST inmediato con los 3 valores ──────
  togSteer.addEventListener('change',   saveAndRefresh);
  togMachine.addEventListener('change', saveAndRefresh);
  togImu.addEventListener('change',     saveAndRefresh);

  // ── Polling del status live para los dots hello ───────────────────────────
  function pollStatus() {
    fetch('/api/corex/status', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d || !d.ok) return;
        var mods = d.modules || {};
        // dot: verde si configurado Y hello recibido; rojo si configurado Y sin hello; gris si no configurado
        setDot(dotSteer,   mods.steer_configured   ? !!mods.steer_hello   : null);
        setDot(dotMachine, mods.machine_configured ? !!mods.machine_hello : null);
        setDot(dotImu,     mods.imu_configured     ? !!mods.imu_hello     : null);
      })
      .catch(function () { /* offline: el próximo tick reintenta */ });
  }

  // ── Init ──────────────────────────────────────────────────────────────────
  lockToggles(true); // bloqueados hasta que loadConfig traiga el estado real
  loadConfig();
  pollStatus();
  var statusInterval = setInterval(pollStatus, 2000);
  window.addEventListener('pagehide', function () { clearInterval(statusInterval); });

}());
