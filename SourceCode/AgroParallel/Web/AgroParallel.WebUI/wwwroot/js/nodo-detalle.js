// ============================================================================
// nodo-detalle.js — Detalle de un nodo individual.
//
// Lee ?uid=... de la URL y renderiza:
//   · Identidad (UID, tipo, alias, IP, firmware, last_seen)
//   · Pills de estado (online/offline, broker on/off, firmware)
//   · Matriz diagnóstica (resalta la fila donde está parado el nodo)
//   · OTA: lista de versiones disponibles en cache + barra de progreso
//   · Acciones rápidas (reiniciar, borrar_wifi, estado, ping)
//
// Endpoints consumidos:
//   GET  /api/nodos/{uid}/estado
//   GET  /api/nodos/{uid}/firmwares
//   POST /api/nodos/{uid}/ota         body: {version}
//   GET  /api/nodos/{uid}/ota/progress
//   POST /api/nodos/{uid}/cmd         body: {cmd, extras?}
// ============================================================================

(function () {
  'use strict';

  // ── Parámetros ──────────────────────────────────────────────────────────
  var params = new URLSearchParams(window.location.search);
  var UID = params.get('uid') || '';

  // ── Elementos DOM ───────────────────────────────────────────────────────
  var elHdrTitle    = document.getElementById('hdrTitle');
  var elHdrSub      = document.getElementById('hdrSub');
  var elHdrPills    = document.getElementById('hdrPills');
  var elMetaUid     = document.getElementById('metaUid');
  var elMetaTipo    = document.getElementById('metaTipo');
  var elMetaAlias   = document.getElementById('metaAlias');
  var elMetaEstado  = document.getElementById('metaEstado');
  var elMetaIp      = document.getElementById('metaIp');
  var elMetaFw      = document.getElementById('metaFw');
  var elMetaLast    = document.getElementById('metaLastSeen');
  var elTblMatrix   = document.getElementById('tblMatrix');
  var elOtaStatus   = document.getElementById('otaStatus');
  var elOtaPct      = document.getElementById('otaPct');
  var elOtaBar      = document.getElementById('otaBar');
  var elOtaProg     = document.getElementById('otaProgress');
  var elOtaDetalle  = document.getElementById('otaDetalle');
  var elFwList      = document.getElementById('firmwaresList');
  var elFwMeta      = document.getElementById('firmwaresMeta');
  var elCmdRow      = document.getElementById('cmdRow');
  var elToast       = document.getElementById('toast');
  var elBootBadge   = document.getElementById('hdrBootBadge');
  var elPinImp      = document.getElementById('btnPinImplemento');

  // boot_reason → severidad (mismo set que /pages/nodos.html).
  var BOOT_REASON_CRIT = { task_wdt:1, int_wdt:1, panic:1, brownout:1, wdt:1 };
  var BOOT_REASON_WARN = { sdio:1, unknown:1 };

  var ultimoEstado   = null;  // último estado bueno
  var ultimoFirmware = null;  // {versiones, http_port, lan_ip, firmware_actual}
  var otaActivoUid   = null;  // hay un OTA corriendo? UID destino
  var pollOtaTimer   = null;

  // ── Helpers ─────────────────────────────────────────────────────────────
  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function lkeys(v) {
    if (v == null) return v;
    if (Array.isArray(v)) return v.map(lkeys);
    if (typeof v !== 'object') return v;
    var out = {};
    for (var k in v) {
      if (!Object.prototype.hasOwnProperty.call(v, k)) continue;
      var nk = k.length > 0 ? k.charAt(0).toLowerCase() + k.substring(1) : k;
      out[nk] = lkeys(v[k]);
    }
    return out;
  }

  function relTime(iso) {
    if (!iso) return '—';
    var t = Date.parse(iso);
    if (isNaN(t)) return '—';
    var secs = Math.floor((Date.now() - t) / 1000);
    if (secs < 5)    return 'ahora';
    if (secs < 60)   return 'hace ' + secs + ' s';
    if (secs < 3600) return 'hace ' + Math.floor(secs / 60) + ' min';
    if (secs < 86400) return 'hace ' + Math.floor(secs / 3600) + ' h';
    return 'hace ' + Math.floor(secs / 86400) + ' d';
  }

  var toastTimer = null;
  function toast(msg, kind) {
    if (!elToast) { return; }
    elToast.className = 'toast' + (kind ? ' ' + kind : '');
    elToast.textContent = msg;
    elToast.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { elToast.classList.remove('show'); }, 3500);
  }

  async function getJson(url) {
    var r = await fetch(url, { cache: 'no-store' });
    return lkeys(await r.json());
  }

  async function postJson(url, body) {
    var r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    });
    return lkeys(await r.json());
  }

  // ── Render: identidad ───────────────────────────────────────────────────
  function renderIdentidad(e) {
    if (!e) return;
    // Título: texto puro + badge HTML al lado (no usamos textContent porque queremos
    // el badge boot_reason renderizado como span).
    var titulo = escapeHtml(e.alias || e.uid || 'Nodo');
    elHdrTitle.innerHTML = titulo + ' <span id="hdrBootBadge">' + bootBadgeHtml(e.boot_reason) + '</span>';
    // El elemento se regenera, refrescamos la ref por las dudas.
    elBootBadge = document.getElementById('hdrBootBadge');

    var sub = (e.tipo || '—');
    if (e.alias && e.uid) sub += ' · ' + e.uid;
    elHdrSub.textContent = sub;

    renderPinImplemento(e);

    elMetaUid.textContent    = e.uid || '—';
    elMetaTipo.textContent   = e.tipo || '—';
    elMetaAlias.textContent  = e.alias || '—';
    elMetaEstado.textContent = e.estado_curado || '—';
    elMetaIp.textContent     = e.ip || '—';
    elMetaFw.textContent     = e.firmware || '—';
    elMetaLast.textContent   = relTime(e.last_seen_utc);
  }

  function renderPills(e) {
    if (!elHdrPills) return;
    var pills = [];
    // Online
    if (e.online) {
      pills.push('<span class="pill ok"><span class="dot"></span> online</span>');
    } else {
      pills.push('<span class="pill err"><span class="dot"></span> offline</span>');
    }
    // Broker MQTT
    if (e.broker_connected) {
      pills.push('<span class="pill ok"><span class="dot"></span> broker MQTT</span>');
    } else {
      pills.push('<span class="pill err"><span class="dot"></span> broker MQTT off</span>');
    }
    // Firmware
    if (e.firmware) {
      pills.push('<span class="pill muted"><span class="dot"></span> fw ' + escapeHtml(e.firmware) + '</span>');
    }
    // Tanda 2: pill safe-mode. Si está activo, el nodo sólo acepta ping +
    // clear_safe_mode — bloqueado para OTA/calibración hasta que el operario
    // confirme que el bug que lo causó está resuelto.
    if (e.safe_mode) {
      var cc = (e.crash_count != null) ? (' · ' + e.crash_count + ' crashes') : '';
      pills.push('<span class="pill err" title="El nodo crasheó ≥3 veces seguidas. OTA y cmds peligrosos rechazados. Resolvé la causa y usá &quot;Resetear safe-mode&quot;."><span class="dot"></span> safe-mode' + escapeHtml(cc) + '</span>');
    }
    // Tanda 2 #8: pill de sync desired/reported. Solo aparece si el tracker
    // tiene datos para el uid (PC publicó una config_desired al menos una vez).
    if (e.config_sync && e.config_sync.status) {
      var cs = String(e.config_sync.status).toLowerCase();
      var label, cls, title;
      switch (cs) {
        case 'in_sync':
          label = 'config OK'; cls = 'ok';
          title = 'Firmware reportó la última config que la PC le pidió.';
          break;
        case 'pending':
          label = 'config pendiente'; cls = 'muted';
          title = 'La PC publicó una config nueva y el firmware todavía no respondió (< 10 s).';
          break;
        case 'drift':
          label = 'config DRIFT'; cls = 'err';
          title = 'La PC publicó una config nueva pero el firmware nunca echó back. Verificá conectividad o que el firmware soporte /config/reported.';
          break;
        case 'no_report':
          label = 'config no_report'; cls = 'err';
          title = 'PC publicó pero el firmware no responde en /config/reported. Puede ser firmware legacy (no soporta echo-back) o nodo desconectado.';
          break;
        default:
          label = 'config ' + cs; cls = 'muted'; title = '';
      }
      pills.push('<span class="pill ' + cls + '" title="' + escapeHtml(title) + '"><span class="dot"></span> ' + escapeHtml(label) + '</span>');
    }
    elHdrPills.innerHTML = pills.join('');
  }

  function bootBadgeHtml(reason) {
    if (!reason) return '';
    var key = String(reason).toLowerCase();
    if (BOOT_REASON_CRIT[key])
      return '<span class="badge-boot" title="Último reset: ' + escapeHtml(key) + ' (anormal — revisar)">⚠ ' + escapeHtml(key) + '</span>';
    if (BOOT_REASON_WARN[key])
      return '<span class="badge-boot warn" title="Último reset: ' + escapeHtml(key) + '">' + escapeHtml(key) + '</span>';
    return '';
  }

  function renderPinImplemento(e) {
    if (!elPinImp) return;
    // El pin sólo tiene sentido para nodos aceptados / offline (no pendientes / ignorados).
    var puedeAsignar = e.estado_curado === 'aceptado' || e.estado_curado === 'offline';
    if (!puedeAsignar) { elPinImp.hidden = true; return; }
    elPinImp.hidden = false;
    var asignado = !!e.del_implemento_activo;
    elPinImp.classList.toggle('on', asignado);
    elPinImp.setAttribute('data-asignado', asignado ? '1' : '0');
    elPinImp.textContent = asignado ? '📌 En implemento activo' : '📍 Asignar al implemento activo';
    elPinImp.title = asignado
      ? 'Quitar del implemento activo (no disparará alarma offline)'
      : 'Asignar al implemento activo (alarma offline cuando caiga)';
  }

  function renderMatrix(matriz) {
    if (!elTblMatrix || !matriz) return;
    var rows = elTblMatrix.querySelectorAll('tbody tr');
    rows.forEach(function (tr) { tr.classList.remove('active'); });
    var row = matriz.row;
    if (!row) return;
    var hit = elTblMatrix.querySelector('tbody tr[data-row="' + row + '"]');
    if (hit) hit.classList.add('active');
  }

  // ── Render: comandos disponibles ────────────────────────────────────────
  function labelCmd(cmd) {
    switch (cmd) {
      case 'reiniciar':       return 'Reiniciar nodo';
      case 'borrar_wifi':     return 'Borrar WiFi guardado';
      case 'estado':          return 'Pedir estado';
      case 'ping':            return 'Ping';
      case 'clear_safe_mode': return 'Resetear safe-mode';
      default:                return cmd;
    }
  }

  function requiereConfirm(cmd) {
    return cmd === 'reiniciar' || cmd === 'borrar_wifi' || cmd === 'clear_safe_mode';
  }

  function confirmMsg(cmd) {
    if (cmd === 'reiniciar')   return 'Reiniciar el nodo ahora?\n\nQuedará offline ~5–10 s mientras vuelve a bootear.';
    if (cmd === 'borrar_wifi') return 'Borrar credenciales WiFi del nodo?\n\nEl nodo va a quedar en modo AP (SSID propio) hasta que lo reconfigures con el celular.';
    if (cmd === 'clear_safe_mode')
      return 'Resetear safe-mode?\n\nEl nodo borra su contador de crashes en NVS y vuelve a aceptar OTA/calibración. Si la causa de los crashes sigue, va a volver a entrar en safe-mode tras 3 reboots.';
    return 'Continuar?';
  }

  function renderCmds(comandos) {
    if (!elCmdRow) return;
    elCmdRow.innerHTML = '';
    (comandos || []).forEach(function (cmd) {
      var b = document.createElement('button');
      b.className = 'btn';
      b.textContent = labelCmd(cmd);
      b.setAttribute('data-cmd', cmd);
      if (cmd === 'borrar_wifi') b.style.color = 'var(--agp-state-bad)';
      // clear_safe_mode lo destacamos en accent porque es la acción esperada
      // cuando el pill de safe-mode aparece en el header.
      if (cmd === 'clear_safe_mode') b.classList.add('primary');
      elCmdRow.appendChild(b);
    });
  }

  // ── Render: firmwares disponibles ───────────────────────────────────────
  function renderFirmwares(fw) {
    if (!elFwList) return;
    if (!fw) { elFwList.innerHTML = '<div class="subtitle">—</div>'; return; }

    var lista = fw.versiones || [];
    var actual = (fw.firmware_actual || '').trim();

    if (lista.length === 0) {
      elFwList.innerHTML =
        '<div class="subtitle">No hay versiones en cache local para este producto. ' +
        'Subí o sincronizá un firmware desde OrbitX cloud y reintentá.</div>';
    } else {
      var html = '';
      lista.forEach(function (v) {
        var version = v.version || '';
        var changelog = v.changelog || '';
        var sha = v.hash_sha256 || '';
        var size = v.tamano_bytes != null ? v.tamano_bytes : '';
        var esActual = !!v.es_actual || (actual && actual === version);

        html +=
          '<div class="firmware-grid" style="border-bottom:1px solid var(--agp-border); padding: 8px 0">' +
            '<div>' +
              '<strong>v' + escapeHtml(version) + '</strong>' +
              (esActual ? '<span class="ver-actual">actual</span>' : '') +
              (changelog ? '<div class="changelog">' + escapeHtml(changelog) + '</div>' : '') +
            '</div>' +
            '<div class="uid-mono" style="font-size: var(--agp-fs-xs)">' +
              (size ? (Math.round(Number(size)/1024) + ' KB') : '—') +
            '</div>' +
            '<div class="uid-mono" style="font-size: var(--agp-fs-xs); overflow:hidden; text-overflow:ellipsis">' +
              (sha ? escapeHtml(sha.substring(0, 12)) + '…' : '—') +
            '</div>' +
            '<div>' +
              (esActual
                ? '<button class="btn" disabled>Ya instalada</button>'
                : '<button class="btn primary" data-ota="' + escapeHtml(version) + '">Aplicar v' + escapeHtml(version) + '</button>') +
            '</div>' +
          '</div>';
      });
      elFwList.innerHTML = html;
    }

    if (elFwMeta) {
      var srv = (fw.lan_ip ? fw.lan_ip : '?') + ':' + (fw.http_port ? fw.http_port : 8088);
      elFwMeta.textContent = 'Servidor LAN de firmwares: ' + srv;
    }
  }

  // ── Render: estado OTA en curso ─────────────────────────────────────────
  function renderOtaState(ota) {
    if (!elOtaStatus) return;
    if (!ota || !ota.status || ota.status === 'idle') {
      elOtaStatus.textContent = 'sin actualización en curso';
      elOtaPct.textContent = '—';
      elOtaBar.style.width = '0%';
      elOtaProg.classList.remove('err');
      elOtaDetalle.textContent = '';
      return;
    }
    var pct = Math.max(0, Math.min(100, Number(ota.progress_pct || ota.progressPct || 0)));
    var status = ota.status;
    var version = ota.version || '';
    var det = ota.detalle || '';

    var label = status;
    if (status === 'sent')      label = 'Comando enviado…';
    else if (status === 'iniciando') label = 'Descargando firmware…';
    else if (status === 'ok')   label = 'Actualizado correctamente';
    else if (status === 'error') label = 'Error';

    elOtaStatus.textContent = label + (version ? ' · v' + version : '');
    elOtaPct.textContent = pct + '%';
    elOtaBar.style.width = pct + '%';
    elOtaProg.classList.toggle('err', status === 'error');
    elOtaDetalle.textContent = det;
  }

  // ── Lecturas ────────────────────────────────────────────────────────────
  async function cargarEstado() {
    if (!UID) return;
    try {
      var data = await getJson('/api/nodos/' + encodeURIComponent(UID) + '/estado');
      if (!data || !data.ok) {
        toast('No se pudo leer el estado del nodo', 'err');
        return;
      }
      ultimoEstado = data;
      renderIdentidad(data);
      renderPills(data);
      renderMatrix(data.matriz);
      renderCmds(data.comandos_disponibles);
      if (data.ota) renderOtaState(data.ota);
    } catch (e) {
      toast('Error al leer estado: ' + e.message, 'err');
    }
  }

  async function cargarFirmwares() {
    if (!UID) return;
    try {
      var data = await getJson('/api/nodos/' + encodeURIComponent(UID) + '/firmwares');
      if (!data || !data.ok) {
        elFwList.innerHTML = '<div class="subtitle">No se pudo leer el catálogo local.</div>';
        return;
      }
      ultimoFirmware = data;
      renderFirmwares(data);
    } catch (e) {
      elFwList.innerHTML = '<div class="subtitle">Error: ' + escapeHtml(e.message) + '</div>';
    }
  }

  async function pollProgress() {
    if (!UID || !otaActivoUid) return;
    try {
      var data = await getJson('/api/nodos/' + encodeURIComponent(UID) + '/ota/progress');
      if (data && data.ok && data.ota) {
        renderOtaState(data.ota);
        if (data.ota.status === 'ok' || data.ota.status === 'error') {
          // OTA terminó
          if (pollOtaTimer) { clearInterval(pollOtaTimer); pollOtaTimer = null; }
          otaActivoUid = null;
          if (data.ota.status === 'ok') {
            toast('OTA completado: v' + (data.ota.version || ''), 'ok');
          } else {
            toast('OTA falló: ' + (data.ota.detalle || 'sin detalle'), 'err');
          }
          // refrescar firmware actual + estado
          setTimeout(function () {
            cargarEstado();
            cargarFirmwares();
          }, 1500);
        }
      }
    } catch (e) { /* silencio */ }
  }

  // ── Acciones ────────────────────────────────────────────────────────────
  async function dispararOta(version) {
    if (!UID || !version) return;
    if (otaActivoUid) {
      toast('Ya hay un OTA en curso para este nodo', 'err');
      return;
    }
    if (!window.confirm('Aplicar firmware v' + version + ' al nodo?\n\nEl nodo se va a reiniciar.')) return;

    try {
      var res = await postJson('/api/nodos/' + encodeURIComponent(UID) + '/ota', { version: version });
      if (!res || !res.ok) {
        toast('No se pudo enviar OTA: ' + ((res && res.error) || 'fallo'), 'err');
        return;
      }
      toast('OTA enviado al nodo', 'ok');
      otaActivoUid = UID;
      renderOtaState({ status: 'sent', progressPct: 5, version: version, detalle: 'Comando enviado al nodo' });
      // arranca polling
      if (pollOtaTimer) clearInterval(pollOtaTimer);
      pollOtaTimer = setInterval(pollProgress, 1000);
    } catch (e) {
      toast('Error: ' + e.message, 'err');
    }
  }

  async function dispararCmd(cmd) {
    if (!UID || !cmd) return;
    if (requiereConfirm(cmd) && !window.confirm(confirmMsg(cmd))) return;
    try {
      var res = await postJson('/api/nodos/' + encodeURIComponent(UID) + '/cmd', { cmd: cmd });
      if (!res || !res.ok) {
        toast('Comando rechazado: ' + ((res && res.error) || 'fallo'), 'err');
        return;
      }
      toast('Comando enviado: ' + labelCmd(cmd), 'ok');
    } catch (e) {
      toast('Error: ' + e.message, 'err');
    }
  }

  // ── Wiring de eventos ──────────────────────────────────────────────────
  if (elFwList) {
    elFwList.addEventListener('click', function (ev) {
      var btn = ev.target.closest && ev.target.closest('button[data-ota]');
      if (!btn) return;
      var v = btn.getAttribute('data-ota');
      dispararOta(v);
    });
  }

  if (elCmdRow) {
    elCmdRow.addEventListener('click', function (ev) {
      var btn = ev.target.closest && ev.target.closest('button[data-cmd]');
      if (!btn) return;
      var c = btn.getAttribute('data-cmd');
      dispararCmd(c);
    });
  }

  if (elPinImp) {
    elPinImp.addEventListener('click', async function () {
      if (!UID) return;
      var ya = elPinImp.getAttribute('data-asignado') === '1';
      var nuevo = !ya;
      elPinImp.disabled = true;
      try {
        var res = await postJson('/api/nodos/asignacion-implemento', { uid: UID, asignado: nuevo });
        if (res && res.ok) {
          toast(nuevo ? 'Agregado al implemento activo' : 'Quitado del implemento activo', 'ok');
          // Refresh inmediato para que el pin refleje el cambio + cualquier banner global.
          await cargarEstado();
        } else {
          toast('No se pudo cambiar la asignación: ' + ((res && res.error) || 'fallo'), 'err');
        }
      } catch (e) {
        toast('Error: ' + e.message, 'err');
      } finally {
        elPinImp.disabled = false;
      }
    });
  }

  // ── Arranque ───────────────────────────────────────────────────────────
  if (!UID) {
    elHdrTitle.textContent = 'Falta UID en la URL';
    elHdrSub.textContent = 'Volvé a Nodos y entrá clickeando una fila.';
    return;
  }

  cargarEstado();
  cargarFirmwares();
  // Refresh de estado cada 2 s (la página suele estar abierta poco rato)
  setInterval(function () {
    if (document.hidden) return;
    cargarEstado();
  }, 2000);
})();
