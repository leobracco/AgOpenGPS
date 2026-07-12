// ============================================================================
// perfil.js — Gestión de perfiles de CoreX (espejo web de FormProfiles).
//   · Guardar: persiste la config actual en el XML del perfil activo.
//   · Cargar: cambia el perfil activo y REINICIA CoreX.
//   · Crear: copia de la config actual (sin reinicio) o de fábrica (reinicia).
//
// Wire: snake_case en ambas direcciones (AgpJson + [JsonPropertyName]).
// ============================================================================

(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  var activo = '';

  // ── Carga inicial: GET /api/corex/config/perfiles ────────────────────────

  function load() {
    fetch('/api/corex/config/perfiles')
      .then(function (r) { return r.json(); })
      .then(function (d) {
        activo = d.activo || '';
        $('perfil-activo').textContent = activo || '(sin perfil)';

        var sel = $('sel-perfil');
        while (sel.firstChild) sel.removeChild(sel.firstChild);

        var otros = (d.perfiles || []).filter(function (p) { return p !== activo; });
        otros.forEach(function (p) {
          var opt = document.createElement('option');
          opt.value = p;
          opt.textContent = p;
          sel.appendChild(opt);
        });

        var hayOtros = otros.length > 0;
        sel.disabled = !hayOtros;
        $('btn-cargar').disabled = !hayOtros;
        if (!hayOtros) {
          var opt = document.createElement('option');
          opt.textContent = '(no hay otros perfiles)';
          sel.appendChild(opt);
        }
      })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo cargar la lista de perfiles.');
      });
  }

  // ── Guardar en el perfil activo ───────────────────────────────────────────

  function onGuardar() {
    var btn = $('btn-guardar');
    btn.disabled = true;

    fetch('/api/corex/perfil/guardar', { method: 'POST' })
      .then(function (r) {
        return r.json().then(function (d) { return { ok: r.ok, data: d }; });
      })
      .then(function (res) {
        btn.disabled = false;
        if (!res.ok) {
          AgpModal.alert('Error', (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
          return;
        }
        AgpModal.alert('Guardado', 'Configuración guardada en el perfil "' + activo + '".');
      })
      .catch(function () {
        btn.disabled = false;
        AgpModal.alert('Error de red', 'No se pudo guardar el perfil.');
      });
  }

  // ── Cargar otro perfil (reinicia CoreX) ───────────────────────────────────

  function onCargar() {
    var nombre = $('sel-perfil').value;
    if (!nombre) return;

    AgpModal.confirm(
      'Cargar perfil',
      'CoreX se reinicia para aplicar el perfil "' + nombre + '". ' +
      'Los cambios sin guardar del perfil actual se pierden. ¿Continuar?'
    ).then(function (confirmado) {
      if (!confirmado) return;

      var btn = $('btn-cargar');
      btn.disabled = true;

      fetch('/api/corex/perfil/cargar', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ nombre: nombre })
      })
        .then(function (r) {
          return r.json().then(function (d) { return { ok: r.ok, data: d }; });
        })
        .then(function (res) {
          btn.disabled = false;
          if (!res.ok) {
            AgpModal.alert('Error', (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
            return;
          }
          waitForRestart();
        })
        .catch(function () {
          btn.disabled = false;
          AgpModal.alert('Error de red', 'No se pudo cargar el perfil.');
        });
    });
  }

  // ── Crear perfil nuevo ────────────────────────────────────────────────────

  function onCrear() {
    // Misma sanitización que el server (FormProfiles.SanitizeFileName).
    var nombre = $('inp-nombre').value.replace(/[<>:"/\\|?*]/g, '').trim();
    if (!nombre) {
      AgpModal.alert('Falta el nombre', 'Ingresá un nombre para el perfil nuevo.');
      return;
    }

    var fabrica = $('chk-fabrica').checked;
    var detalle = fabrica
      ? 'Se crea "' + nombre + '" con valores de fábrica y CoreX se reinicia. ¿Continuar?'
      : 'Se crea "' + nombre + '" como copia de la configuración actual y queda activo. ¿Continuar?';

    AgpModal.confirm('Crear perfil', detalle).then(function (confirmado) {
      if (!confirmado) return;

      var btn = $('btn-crear');
      btn.disabled = true;

      fetch('/api/corex/perfil/crear', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ nombre: nombre, desde_actual: !fabrica })
      })
        .then(function (r) {
          return r.json().then(function (d) { return { ok: r.ok, data: d }; });
        })
        .then(function (res) {
          btn.disabled = false;
          if (!res.ok) {
            AgpModal.alert('Error', (res.data && (res.data.mensaje || res.data.friendly)) || 'Error desconocido.');
            return;
          }
          if (res.data.restart) {
            waitForRestart();
          } else {
            $('inp-nombre').value = '';
            load();
            AgpModal.alert('Perfil creado',
              '"' + nombre + '" creado con la configuración actual y activo.');
          }
        })
        .catch(function () {
          btn.disabled = false;
          AgpModal.alert('Error de red', 'No se pudo crear el perfil.');
        });
    });
  }

  // ── Esperar reinicio: poll /api/corex/status (mismo patrón que red.js) ────

  function waitForRestart() {
    AgpModal.alert('Reiniciando', 'CoreX se está reiniciando… esperá unos segundos.');
    var attempts = 0;
    var interval = setInterval(function () {
      attempts++;
      if (attempts > 30) {
        clearInterval(interval);
        AgpModal.alert('CoreX no responde',
          'El reinicio está tardando más de lo esperado. Cerrá y volvé a abrir CoreX.');
        return;
      }
      fetch('/api/corex/status')
        .then(function (r) {
          if (r.ok) {
            clearInterval(interval);
            location.reload();
          }
        })
        .catch(function () {
          // Sigue esperando; CoreX todavía está reiniciando.
        });
    }, 2000);
  }

  // ── Sistema (port de FormAdvancedSettings) ────────────────────────────────
  // Dos toggles que se guardan al instante, sin reinicio. Arrancan
  // bloqueados hasta que el GET trae el estado real (mismo criterio que
  // modulos.js: un GET fallido no debe pisar la config con false).

  var avanzadoLoaded = false;

  function lockAvanzado(locked) {
    $('chk-start-min').disabled = locked;
    $('chk-auto-gpsout').disabled = locked;
  }

  function loadAvanzado() {
    fetch('/api/corex/config/avanzado', { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        $('chk-start-min').checked = !!d.start_minimized;
        $('chk-auto-gpsout').checked = !!d.auto_gps_out;
        avanzadoLoaded = true;
        lockAvanzado(false);
      })
      .catch(function () { setTimeout(loadAvanzado, 3000); });
  }

  function saveAvanzado() {
    if (!avanzadoLoaded) return;
    lockAvanzado(true);
    fetch('/api/corex/config/avanzado', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        start_minimized: $('chk-start-min').checked,
        auto_gps_out: $('chk-auto-gpsout').checked,
      }),
    })
      .catch(function () {
        AgpModal.alert('Error de red', 'No se pudo guardar la configuración de sistema.');
      })
      .finally(function () { lockAvanzado(false); });
  }

  // ── Inicialización ────────────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', function () {
    load();
    $('btn-guardar').addEventListener('click', onGuardar);
    $('btn-cargar').addEventListener('click', onCargar);
    $('btn-crear').addEventListener('click', onCrear);

    lockAvanzado(true);
    loadAvanzado();
    $('chk-start-min').addEventListener('change', saveAvanzado);
    $('chk-auto-gpsout').addEventListener('change', saveAvanzado);

    // Ciclo de vida (modo demonio: CoreX no tiene ventana propia).
    $('btn-reiniciar').addEventListener('click', function () {
      AgpModal.confirm('Reiniciar CoreX',
        '¿Reiniciar CoreX ahora? Se corta la corrección unos segundos.')
        .then(function (si) {
          if (!si) return;
          fetch('/api/corex/reiniciar', { method: 'POST' })
            .then(function () { waitForRestart(); })
            .catch(function () {
              AgpModal.alert('Error de red', 'No se pudo pedir el reinicio.');
            });
        });
    });

    $('btn-apagar').addEventListener('click', function () {
      AgpModal.confirm('Apagar CoreX',
        '¿Apagar CoreX? Deja de haber GPS y corrección hasta que se vuelva a iniciar.')
        .then(function (si) {
          if (!si) return;
          fetch('/api/corex/apagar', { method: 'POST' })
            .then(function () {
              AgpModal.alert('CoreX apagado',
                'CoreX se está cerrando. Esta página va a dejar de responder.');
            })
            .catch(function () {
              AgpModal.alert('Error de red', 'No se pudo pedir el apagado.');
            });
        });
    });
  });

})();
