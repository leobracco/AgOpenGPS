// ============================================================================
// perfiles.js — gestión de perfiles de vehículo (pages/perfiles.html).
// Reemplaza a las forms nativas FormNewProfile/FormLoadProfile.
// API: /api/aog/perfiles (GET lista, POST cargar/nuevo/copiar/borrar/
// proteger/desproteger, body JSON snake_case) + POST /api/config/backup
// (mismo backup general que sistema.html — incluye los perfiles).
// ============================================================================
(function () {
  'use strict';

  var lista = document.getElementById('lista');
  var estado = document.getElementById('estado');
  var botones = {
    cargar: document.getElementById('btnCargar'),
    copiar: document.getElementById('btnCopiar'),
    proteger: document.getElementById('btnProteger'),
    desproteger: document.getElementById('btnDesproteger'),
    borrar: document.getElementById('btnBorrar')
  };

  var perfiles = [];      // [{nombre, protegido, activo}]
  var seleccionado = null;
  var jobAbierto = false;

  function setEstado(msg, cls) {
    estado.textContent = msg || '';
    estado.className = cls || '';
  }

  async function api(path, body) {
    var res = await fetch('/api/aog/perfiles' + path, body ? {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    } : { cache: 'no-store' });
    return await res.json();
  }

  function render() {
    lista.innerHTML = '';
    if (!perfiles.length) {
      lista.innerHTML = '<div class="vacio">No hay perfiles todavía — creá uno con «Sumar perfil».</div>';
    }
    perfiles.forEach(function (p) {
      var fila = document.createElement('div');
      fila.className = 'fila' + (p.nombre === seleccionado ? ' sel' : '');
      var candado = p.protegido ? '<span class="tag lock">🔒 protegido</span>' : '';
      var uso = p.activo ? '<span class="tag uso">EN USO</span>' : '';
      fila.innerHTML = '<span class="nombre"></span>' + candado + uso;
      fila.querySelector('.nombre').textContent = p.nombre;
      fila.addEventListener('click', function () {
        seleccionado = (seleccionado === p.nombre) ? null : p.nombre;
        render();
      });
      lista.appendChild(fila);
    });

    var p = perfiles.find(function (x) { return x.nombre === seleccionado; });
    // cargar/borrar no aplican al perfil en uso; cargar tampoco con lote abierto
    botones.cargar.disabled = !p || p.activo || jobAbierto;
    botones.copiar.disabled = !p;
    botones.proteger.disabled = !p || p.protegido;
    botones.desproteger.disabled = !p || !p.protegido;
    botones.borrar.disabled = !p || p.activo;
  }

  async function refrescar() {
    try {
      var r = await api('');
      if (!r.ok) { setEstado('Servicio de perfiles no disponible', 'err'); return; }
      perfiles = r.perfiles || [];
      jobAbierto = !!r.is_job_started;
      if (seleccionado && !perfiles.some(function (p) { return p.nombre === seleccionado; })) {
        seleccionado = null;
      }
      render();
      if (jobAbierto) setEstado('Hay un lote abierto: cerralo para cambiar de perfil.', '');
    } catch (e) {
      setEstado('Sin conexión: ' + e.message, 'err');
    }
  }

  // ---- diálogo genérico -----------------------------------------------
  var velo = document.getElementById('velo');
  var dlgTitulo = document.getElementById('dlgTitulo');
  var dlgNota = document.getElementById('dlgNota');
  var campoNombre = document.getElementById('campoNombre');
  var campoDesde = document.getElementById('campoDesde');
  var campoClave = document.getElementById('campoClave');
  var inNombre = document.getElementById('inNombre');
  var inDesde = document.getElementById('inDesde');
  var inClave = document.getElementById('inClave');
  var dlgAccion = null;

  function abrirDialogo(opts) {
    dlgTitulo.textContent = opts.titulo;
    dlgNota.textContent = opts.nota || '';
    campoNombre.hidden = !opts.nombre;
    campoDesde.hidden = !opts.desde;
    campoClave.hidden = !opts.clave;
    inNombre.value = '';
    inClave.value = '';
    if (opts.desde) {
      inDesde.innerHTML = '<option value="">(en blanco — configuración de fábrica)</option>';
      perfiles.forEach(function (p) {
        var o = document.createElement('option');
        o.value = p.nombre;
        o.textContent = p.nombre + (p.activo ? ' (en uso)' : '');
        if (p.activo) o.selected = true;
        inDesde.appendChild(o);
      });
    }
    dlgAccion = opts.aceptar;
    velo.hidden = false;
    if (opts.nombre) inNombre.focus();
    else if (opts.clave) inClave.focus();
  }

  function cerrarDialogo() {
    velo.hidden = true;
    dlgAccion = null;
    // el teclado virtual se cierra solo al perder foco
  }

  document.getElementById('dlgCancelar').addEventListener('click', cerrarDialogo);
  document.getElementById('dlgOk').addEventListener('click', async function () {
    if (!dlgAccion) return;
    var fn = dlgAccion;
    var r;
    try { r = await fn(); } catch (e) { setEstado('Error: ' + e.message, 'err'); return; }
    if (r && r.ok === false) {
      setEstado(r.error || 'La acción falló', 'err');
      return; // dejar el diálogo abierto para corregir
    }
    cerrarDialogo();
    refrescar();
  });

  // ---- acciones --------------------------------------------------------
  document.getElementById('btnSumar').addEventListener('click', function () {
    abrirDialogo({
      titulo: 'Sumar perfil',
      nota: 'Copia TODAS las configuraciones del vehículo elegido (o arranca en blanco) y lo deja en uso.',
      nombre: true, desde: true,
      aceptar: function () {
        var n = inNombre.value.trim();
        if (!n) { setEstado('Poné un nombre', 'err'); return Promise.resolve({ ok: false, error: 'Poné un nombre' }); }
        setEstado('Creando «' + n + '»…');
        return api('/nuevo', { nombre: n, desde: inDesde.value });
      }
    });
  });

  botones.cargar.addEventListener('click', function () {
    if (!seleccionado) return;
    setEstado('Cargando «' + seleccionado + '»…');
    api('/cargar', { nombre: seleccionado }).then(function (r) {
      setEstado(r.ok ? 'Perfil «' + seleccionado + '» cargado.' : (r.error || 'No se pudo cargar'), r.ok ? 'ok' : 'err');
      refrescar();
    });
  });

  botones.copiar.addEventListener('click', function () {
    if (!seleccionado) return;
    var origen = seleccionado;
    abrirDialogo({
      titulo: 'Copiar «' + origen + '»',
      nota: 'Duplica el perfil con TODAS sus configuraciones. No cambia el perfil en uso.',
      nombre: true,
      aceptar: function () {
        var n = inNombre.value.trim();
        if (!n) return Promise.resolve({ ok: false, error: 'Poné un nombre' });
        setEstado('Copiando…');
        return api('/copiar', { origen: origen, nuevo: n });
      }
    });
  });

  botones.proteger.addEventListener('click', function () {
    if (!seleccionado) return;
    var nombre = seleccionado;
    abrirDialogo({
      titulo: 'Proteger «' + nombre + '»',
      nota: 'Elegí una clave. Sin ella nadie va a poder borrar este perfil. Guardala bien: no se puede recuperar.',
      clave: true,
      aceptar: function () {
        return api('/proteger', { nombre: nombre, clave: inClave.value });
      }
    });
  });

  botones.desproteger.addEventListener('click', function () {
    if (!seleccionado) return;
    var nombre = seleccionado;
    abrirDialogo({
      titulo: 'Desproteger «' + nombre + '»',
      nota: 'Ingresá la clave con la que se protegió.',
      clave: true,
      aceptar: function () {
        return api('/desproteger', { nombre: nombre, clave: inClave.value });
      }
    });
  });

  botones.borrar.addEventListener('click', function () {
    if (!seleccionado) return;
    var nombre = seleccionado;
    var p = perfiles.find(function (x) { return x.nombre === nombre; });
    var protegido = p && p.protegido;
    abrirDialogo({
      titulo: 'Borrar «' + nombre + '»',
      nota: protegido
        ? 'Perfil PROTEGIDO: ingresá la clave para borrarlo. Se pierde toda su configuración.'
        : 'Se borra el perfil con toda su configuración. Esta acción no se puede deshacer.',
      clave: protegido,
      aceptar: function () {
        setEstado('Borrando…');
        return api('/borrar', { nombre: nombre, clave: inClave.value });
      }
    });
  });

  document.getElementById('btnBackup').addEventListener('click', async function () {
    var btn = this;
    btn.disabled = true;
    setEstado('Generando backup…');
    try {
      // mismo endpoint que sistema.html (query-string, no body)
      var res = await fetch('/api/config/backup', { method: 'POST' });
      var r = await res.json();
      setEstado(r.ok ? ('Backup creado' + (r.nombre ? ': ' + r.nombre : '.')) : ('No se pudo crear el backup' + (r.detail ? ': ' + r.detail : '.')), r.ok ? 'ok' : 'err');
    } catch (e) {
      setEstado('Error creando backup: ' + e.message, 'err');
    } finally {
      btn.disabled = false;
    }
  });

  refrescar();
  setInterval(refrescar, 5000);
})();
