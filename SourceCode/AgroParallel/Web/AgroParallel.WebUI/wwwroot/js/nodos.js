// ============================================================================
// nodos.js — vista en vivo de nodos AgroParallel (MQTT discovery).
// Pollea /api/nodos cada 3s y renderiza la lista con filtros por tipo.
// ============================================================================

(function () {
  'use strict';

  // ----- vista curada (tabs Pendientes/Aceptados/Offline/Ignorados) -----
  var statsEl = document.getElementById('headerStats');
  var btnAsistente = document.getElementById('btnAsistente');
  var tabsEl   = document.getElementById('estadoTabs');
  var tabBtns  = tabsEl ? tabsEl.querySelectorAll('.tab') : [];
  var tbody    = document.querySelector('#tblNodos tbody');
  var emptyMsg = document.getElementById('emptyMsg');
  var cntEl = {
    pendiente: document.getElementById('cntPendiente'),
    aceptado:  document.getElementById('cntAceptado'),
    offline:   document.getElementById('cntOffline'),
    ignorado:  document.getElementById('cntIgnorado')
  };
  var activeTab = 'pendiente';
  var unified = [];
  var implementoSlug = '';

  // Banner de alarmas (nodos del implemento activo offline)
  var alertBanner   = document.getElementById('alertBanner');
  var alertLista    = document.getElementById('alertLista');
  var alertSlug     = document.getElementById('alertSlug');
  var btnSilenciar  = document.getElementById('btnSilenciar');
  // UIDs ya alarmados (para no beepear en cada refresh). Se purga cuando vuelven online.
  var alertedUids = Object.create(null);
  // Silenciador manual: timestamp hasta el que no sonamos.
  var silencedUntil = 0;

  // boot_reason → severidad/color. Las críticas son las que indican cuelgue
  // (task_wdt, int_wdt, panic, brownout, wdt). poweron/sw_reset/ext son benignas.
  var BOOT_REASON_CRIT = { task_wdt:1, int_wdt:1, panic:1, brownout:1, wdt:1 };
  var BOOT_REASON_WARN = { sdio:1, unknown:1 };

  var rescanBtn = document.getElementById('rescanBtn');

  // Diagnóstico MQTT
  var mqttPill     = document.getElementById('mqttPill');
  var diagBroker   = document.getElementById('diagBroker');
  var diagCount    = document.getElementById('diagCount');
  var diagSubs     = document.getElementById('diagSubs');
  var diagWild     = document.getElementById('diagWild');
  var diagAttempts = document.getElementById('diagAttempts');
  var diagLastOk   = document.getElementById('diagLastOk');
  var diagSeqGaps   = document.getElementById('diagSeqGaps');
  var diagSeqResets = document.getElementById('diagSeqResets');
  var diagErrorBox       = document.getElementById('diagErrorBox');
  var diagError          = document.getElementById('diagError');
  var diagErrorCode      = document.getElementById('diagErrorCode');
  var diagErrorCodeRepeat= document.getElementById('diagErrorCodeRepeat');
  var diagErrorTs        = document.getElementById('diagErrorTs');
  var diagErrorTech      = document.getElementById('diagErrorTech');
  var diagErrorTechBox   = document.getElementById('diagErrorTechBox');
  var btnRefresh   = document.getElementById('btnRefresh');
  var btnWild      = document.getElementById('btnWild');
  var btnReconnect = document.getElementById('btnReconnect');
  var msgLog       = document.getElementById('msgLog');
  var wildOn       = false;

  var nodos = [];

  function typeColor(t) {
    var k = (t || '').toLowerCase();
    if (k.indexOf('quantix') >= 0) return '#4BA63F';
    if (k.indexOf('vistax')  >= 0) return '#3D87C6';
    if (k.indexOf('section') >= 0) return '#DC8C1E';
    if (k.indexOf('storm')   >= 0) return '#A06FBF';
    if (k.indexOf('flow')    >= 0) return '#2BB8B8';
    return '#707070';
  }

  function fmtUptime(s) {
    s = Number(s) || 0;
    if (s <= 0) return '—';
    var d = Math.floor(s / 86400);
    var h = Math.floor((s % 86400) / 3600);
    var m = Math.floor((s % 3600) / 60);
    if (d > 0) return d + 'd ' + h + 'h';
    if (h > 0) return h + 'h ' + m + 'm';
    return m + 'm';
  }

  function fmtAgo(iso) {
    try {
      var t = new Date(iso).getTime();
      var diff = Math.max(0, (Date.now() - t) / 1000);
      if (diff < 60) return Math.floor(diff) + 's atrás';
      if (diff < 3600) return Math.floor(diff / 60) + 'm atrás';
      return Math.floor(diff / 3600) + 'h atrás';
    } catch (e) { return ''; }
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

  function setActiveTab(tab) {
    activeTab = tab;
    if (tabBtns) tabBtns.forEach(function (t) {
      t.classList.toggle('active', t.getAttribute('data-tab') === tab);
    });
    render();
  }

  function actionsFor(n) {
    var btns = [];
    var uidAttr = 'data-uid="' + escapeHtml(n.uid) + '"';
    var tipoAttr = 'data-tipo="' + escapeHtml(n.tipo || '') + '"';
    if (n.estado === 'pendiente') {
      btns.push('<button class="btn" data-act="aceptar" ' + uidAttr + ' ' + tipoAttr + '>Aceptar</button>');
      btns.push('<button class="btn" data-act="ignorar" ' + uidAttr + '>Ignorar</button>');
    } else if (n.estado === 'aceptado' || n.estado === 'offline') {
      // Toggle de pertenencia al implemento ACTIVO. Solo los nodos con esta
      // marca disparan banner de alarma cuando caen offline — los demás
      // pueden ser de otros implementos guardados y no nos molestan.
      var pinOn = !!n.del_implemento_activo;
      btns.push('<button class="pin-imp ' + (pinOn ? 'on' : '') + '" ' +
                'data-act="implemento" ' + uidAttr +
                ' data-asignado="' + (pinOn ? '1' : '0') + '" ' +
                'title="' + (pinOn ? 'Quitar del implemento activo' : 'Asignar al implemento activo') + '">' +
                (pinOn ? '📌 En implemento' : '📍 Asignar') +
                '</button>');
      if (n.estado === 'aceptado')
        btns.push('<button class="btn" data-act="configurar" ' + uidAttr + ' ' + tipoAttr + '>Configurar</button>');
      btns.push('<button class="btn" data-act="renombrar" ' + uidAttr + '>Renombrar</button>');
      btns.push('<button class="btn" data-act="eliminar"  ' + uidAttr + ' style="color:var(--agp-state-bad)">Eliminar</button>');
    } else if (n.estado === 'ignorado') {
      btns.push('<button class="btn" data-act="restaurar" ' + uidAttr + '>Restaurar</button>');
    }
    return btns.join(' ');
  }

  // Decora el alias con el badge boot_reason si el último reset fue anormal.
  function bootBadge(reason) {
    if (!reason) return '';
    var key = String(reason).toLowerCase();
    if (BOOT_REASON_CRIT[key])
      return ' <span class="badge-boot" title="Último reset: ' + escapeHtml(key) + ' (anormal — revisar)">⚠ ' + escapeHtml(key) + '</span>';
    if (BOOT_REASON_WARN[key])
      return ' <span class="badge-boot warn" title="Último reset: ' + escapeHtml(key) + '">' + escapeHtml(key) + '</span>';
    return ''; // poweron / sw_reset / ext / deepsleep → silencio
  }

  // Beep corto para alarmas. Usa WebAudio (no requiere asset). Se usa una sola
  // vez por nodo que TRANSICIONA a offline para no molestar al operario.
  function beep() {
    try {
      var ctx = beep._ctx || (beep._ctx = new (window.AudioContext || window.webkitAudioContext)());
      var o = ctx.createOscillator();
      var g = ctx.createGain();
      o.type = 'square';
      o.frequency.value = 880;
      g.gain.value = 0.0001;
      o.connect(g); g.connect(ctx.destination);
      var t = ctx.currentTime;
      g.gain.exponentialRampToValueAtTime(0.18, t + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, t + 0.45);
      o.start(t); o.stop(t + 0.5);
    } catch (e) { /* silent */ }
  }

  function refreshAlerts() {
    if (!alertBanner) return;
    // Sólo nodos del implemento activo que están offline (o aceptados-no-online).
    var offlineDelImp = unified.filter(function (n) {
      return n.del_implemento_activo && !n.online;
    });

    if (alertSlug) alertSlug.textContent = implementoSlug ? '(' + implementoSlug + ')' : '';

    if (offlineDelImp.length === 0) {
      alertBanner.classList.remove('visible');
      alertedUids = Object.create(null);
      return;
    }

    // Render lista.
    alertLista.innerHTML = offlineDelImp.map(function (n) {
      var label = n.alias ? escapeHtml(n.alias) : escapeHtml(n.uid);
      var tipo = n.tipo ? ' <span class="slug">' + escapeHtml(n.tipo) + '</span>' : '';
      var hace = n.last_seen_utc ? ' — última señal ' + relTime(n.last_seen_utc) : ' — nunca visto';
      return '<li>' + label + tipo + hace + '</li>';
    }).join('');
    alertBanner.classList.add('visible');

    // Beep sólo por UIDs nuevos en alarma, y respetando silenciado manual.
    var nowMs = Date.now();
    var hayNuevos = false;
    var alivos = Object.create(null);
    offlineDelImp.forEach(function (n) {
      alivos[n.uid] = true;
      if (!alertedUids[n.uid]) hayNuevos = true;
    });
    // Purga UIDs que volvieron a online o ya no están en la lista.
    Object.keys(alertedUids).forEach(function (k) { if (!alivos[k]) delete alertedUids[k]; });
    // Marca los actuales.
    Object.keys(alivos).forEach(function (k) { alertedUids[k] = true; });

    if (hayNuevos && nowMs >= silencedUntil) beep();
  }

  if (btnSilenciar) {
    btnSilenciar.addEventListener('click', function () {
      // Silenciamos 10 min — vuelve solo si aparece otro nodo nuevo offline después.
      silencedUntil = Date.now() + 10 * 60 * 1000;
      if (alertBanner) alertBanner.classList.remove('visible');
    });
  }

  function pageForTipo(tipo) {
    if (!tipo) return null;
    var t = tipo.toLowerCase();
    if (t.indexOf('quantix')  >= 0) return 'quantix.html';
    if (t.indexOf('vistax')   >= 0) return 'vistax.html';
    if (t.indexOf('sectionx') >= 0) return 'sectionx.html';
    if (t.indexOf('flowx')    >= 0 || t.indexOf('flow')  >= 0) return 'flowx.html';
    if (t.indexOf('stormx')   >= 0 || t.indexOf('storm') >= 0) return 'stormx.html';
    return null;
  }

  function render() {
    // Stats globales (header)
    var online = unified.filter(function (n) { return n.online; }).length;
    var offlineCnt = unified.length - online;
    if (statsEl) {
      statsEl.innerHTML =
        '<span class="pill ok"><span class="dot"></span> ' + online + ' online</span>' +
        '<span class="pill idle"><span class="dot"></span> ' + offlineCnt + ' offline</span>';
    }

    // Contadores por tab
    var counts = { pendiente: 0, aceptado: 0, offline: 0, ignorado: 0 };
    unified.forEach(function (n) { if (counts[n.estado] != null) counts[n.estado]++; });
    Object.keys(counts).forEach(function (k) { if (cntEl[k]) cntEl[k].textContent = counts[k]; });

    // Refresca el banner de alarmas en cada render.
    refreshAlerts();

    if (!tbody) return;
    var rows = unified.filter(function (n) { return n.estado === activeTab; });
    tbody.innerHTML = '';
    if (rows.length === 0) {
      if (emptyMsg) emptyMsg.hidden = false;
      return;
    }
    if (emptyMsg) emptyMsg.hidden = true;

    rows.forEach(function (n) {
      var tr = document.createElement('tr');
      var dot = n.online
        ? '<span class="dot-on">●</span>'
        : '<span class="dot-off">○</span>';
      var bootB = bootBadge(n.boot_reason);
      var aliasOrUid = n.alias
        ? escapeHtml(n.alias) + bootB + '<div class="uid-mono">' + escapeHtml(n.uid) + '</div>'
        : '<span class="uid-mono">' + escapeHtml(n.uid) + '</span>' + bootB;
      // La fila es clickeable: lleva al detalle del nodo. Los botones de
      // acción dentro de .actions cortan la propagación más abajo.
      tr.setAttribute('data-uid', n.uid || '');
      tr.classList.add('row-link');
      tr.innerHTML =
        '<td>' + dot + '</td>' +
        '<td>' + aliasOrUid + '</td>' +
        '<td><span class="tipo-pill" style="color:' + typeColor(n.tipo) + '">' + escapeHtml(n.tipo || '—') + '</span></td>' +
        '<td>' + escapeHtml(n.ip || '—') + '</td>' +
        '<td>' + escapeHtml(n.firmware || '—') + '</td>' +
        '<td>' + relTime(n.last_seen_utc) + '</td>' +
        '<td class="actions">' + actionsFor(n) + '</td>';
      tbody.appendChild(tr);
    });
  }

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // Normaliza un objeto/array recursivamente a keys camelCase de primer char en
  // minúscula. El backend serializa con PascalCase (Nodos, Uid, Type, Online…)
  // y la UI espera lowercase. Aplicar siempre al parsear respuesta.
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

  async function refresh() {
    try {
      // /api/nodos/unified ya viene en snake_case (lkeys lo deja igual o
      // sólo cambia las raíces). Filtramos null defensivamente.
      var res = await fetch('/api/nodos/unified', { cache: 'no-store' });
      var data = lkeys(await res.json());
      unified = (data && data.nodos) || [];
      implementoSlug = (data && data.implementoSlug) || '';
      nodos = unified;  // mantengo `nodos` por compatibilidad con otras funciones
    } catch (e) {
      unified = []; nodos = [];
    }
    render();
  }

  async function postJson(url, body) {
    try {
      var res = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
      });
      var data = await res.json();
      return data && data.ok;
    } catch (e) { return false; }
  }

  async function deleteUid(uid) {
    try {
      var res = await fetch('/api/nodos/' + encodeURIComponent(uid), { method: 'DELETE' });
      var data = await res.json();
      return data && data.ok;
    } catch (e) { return false; }
  }

  if (tabBtns) tabBtns.forEach(function (t) {
    t.addEventListener('click', function () { setActiveTab(t.getAttribute('data-tab')); });
  });

  if (btnAsistente) {
    btnAsistente.addEventListener('click', function () { window.location.href = 'setup.html'; });
  }

  // Acciones por fila (delegación de eventos)
  if (tbody) {
    tbody.addEventListener('click', async function (e) {
      // El pin del implemento NO es <button class="btn"> sino <button class="pin-imp">,
      // así que matcheamos por [data-act] que ambos comparten.
      var btn = e.target.closest && e.target.closest('[data-act]');
      if (!btn) {
        // Click en la fila pero fuera de un botón → ir al detalle.
        var tr = e.target.closest && e.target.closest('tr[data-uid]');
        if (tr) {
          var uidRow = tr.getAttribute('data-uid');
          if (uidRow) window.location.href = 'nodo-detalle.html?uid=' + encodeURIComponent(uidRow);
        }
        return;
      }
      var act = btn.getAttribute('data-act');
      var uid = btn.getAttribute('data-uid');
      var tipo = btn.getAttribute('data-tipo') || '';
      if (!uid) return;

      if (act === 'aceptar') {
        var alias = window.prompt('Alias humano para el nodo (ej: "Motor izquierdo"):', 'Nodo ' + uid);
        if (alias == null) return;
        alias = alias.trim();
        if (!alias) return;
        if (await postJson('/api/nodos/aceptar', { uid: uid, tipo: tipo, alias: alias })) {
          setActiveTab('aceptado'); await refresh();
        }
      } else if (act === 'ignorar') {
        if (!window.confirm('Ignorar este nodo? Dejará de aparecer en Pendientes.')) return;
        if (await postJson('/api/nodos/ignorar', { uid: uid })) await refresh();
      } else if (act === 'restaurar') {
        if (await postJson('/api/nodos/restaurar', { uid: uid })) await refresh();
      } else if (act === 'renombrar') {
        var actual = '';
        unified.some(function (n) { if (n.uid === uid) { actual = n.alias || ''; return true; } return false; });
        var nuevo = window.prompt('Nuevo alias:', actual);
        if (nuevo == null) return;
        nuevo = nuevo.trim();
        if (!nuevo) return;
        if (await postJson('/api/nodos/renombrar', { uid: uid, alias: nuevo })) await refresh();
      } else if (act === 'eliminar') {
        if (!window.confirm('Eliminar este nodo de la configuración?\n\nUID: ' + uid + '\n\nSe pierde el alias. Si vuelve a anunciarse, aparecerá como Pendiente.')) return;
        if (await deleteUid(uid)) await refresh();
      } else if (act === 'configurar') {
        var page = pageForTipo(tipo);
        if (page) window.location.href = page;
        else window.alert('No hay página de configuración para este tipo de nodo todavía.');
      } else if (act === 'implemento') {
        // Toggle de pertenencia al implemento ACTIVO. Si pasa a desasignado y
        // estaba alarmado offline, despejamos su entrada para que no quede en
        // alertedUids con un beep stale.
        var ya = btn.getAttribute('data-asignado') === '1';
        var nuevo = !ya;
        var ok = await postJson('/api/nodos/asignacion-implemento', { uid: uid, asignado: nuevo });
        if (ok) {
          if (!nuevo && alertedUids[uid]) delete alertedUids[uid];
          await refresh();
        }
      }
    });
  }

  if (rescanBtn) {
    rescanBtn.addEventListener('click', refresh);
  }

  function fmtHms(iso) {
    try {
      var d = new Date(iso);
      var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
      return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
    } catch (e) { return ''; }
  }

  async function refreshDiag() {
    if (!mqttPill) return;
    try {
      var r = await fetch('/api/nodos/diagnostic', { cache: 'no-store' });
      var data = lkeys(await r.json());
      if (!data || !data.ok || !data.diag) {
        mqttPill.className = 'pill err';
        mqttPill.innerHTML = '<span class="dot"></span> sin servicio';
        return;
      }
      var d = data.diag;
      wildOn = !!d.wildcardCaptureOn;

      if (d.connected) {
        mqttPill.className = 'pill ok';
        mqttPill.innerHTML = '<span class="dot"></span> CONECTADO';
      } else {
        mqttPill.className = 'pill err';
        mqttPill.innerHTML = '<span class="dot"></span> DESCONECTADO';
      }
      diagBroker.textContent   = (d.brokerAddress || '?') + ':' + (d.brokerPort || 0);
      diagCount.textContent    = d.knownNodesCount;
      diagSubs.innerHTML       = (d.subscriptions || []).map(function (s) { return '<code>' + escapeHtml(s) + '</code>'; }).join(' ');
      diagWild.textContent     = wildOn ? 'ON' : 'off';
      diagAttempts.textContent = d.connectAttempts != null ? d.connectAttempts : '—';
      diagLastOk.textContent   = d.lastConnectedUtc ? new Date(d.lastConnectedUtc).toLocaleString() : '— nunca —';
      if (diagSeqGaps) {
        var gaps = d.seqGapCount != null ? d.seqGapCount : 0;
        var lastGap = (d.recentSeqGaps && d.recentSeqGaps.length > 0) ? d.recentSeqGaps[0] : null;
        if (lastGap) {
          diagSeqGaps.textContent = gaps + ' · último: ' + lastGap.uid + ' (' + lastGap.missed + ' msg, ' + fmtHms(lastGap.timestampUtc) + ')';
        } else {
          diagSeqGaps.textContent = gaps + '';
        }
      }
      if (diagSeqResets) diagSeqResets.textContent = d.seqResetCount != null ? d.seqResetCount : 0;
      if (btnWild) btnWild.textContent = wildOn ? 'Desactivar captura wildcard' : 'Activar captura wildcard';

      if (d.lastError && !d.connected) {
        diagErrorBox.style.display = '';
        var code = d.lastErrorCode || 'AGP-SYS-009';
        if (diagErrorCode)       diagErrorCode.textContent = code;
        if (diagErrorCodeRepeat) diagErrorCodeRepeat.textContent = code;
        if (diagErrorTs)         diagErrorTs.textContent = d.lastErrorUtc ? fmtHms(d.lastErrorUtc) : '';
        diagError.textContent = d.lastError;
        if (diagErrorTechBox && diagErrorTech) {
          if (d.lastErrorTechnical) {
            diagErrorTechBox.style.display = '';
            diagErrorTech.textContent = d.lastErrorTechnical;
          } else {
            diagErrorTechBox.style.display = 'none';
            diagErrorTech.textContent = '';
          }
        }
      } else {
        diagErrorBox.style.display = 'none';
      }

      if (msgLog) {
        var msgs = d.recentMessages || [];
        if (msgs.length === 0) {
          msgLog.innerHTML = '<div class="row" style="opacity:.5">— sin mensajes aún —</div>';
        } else {
          msgLog.innerHTML = msgs.map(function (m) {
            return '<div class="row"><span class="ts">' + fmtHms(m.timestampUtc) + '</span>' +
                   '<span class="topic">' + escapeHtml(m.topic) + '</span>  ' +
                   escapeHtml(m.payload) + '</div>';
          }).join('');
        }
      }
    } catch (e) {
      mqttPill.className = 'pill err';
      mqttPill.innerHTML = '<span class="dot"></span> error';
    }
  }

  if (btnRefresh) btnRefresh.addEventListener('click', refreshDiag);
  if (btnReconnect) {
    btnReconnect.addEventListener('click', async function () {
      btnReconnect.disabled = true;
      btnReconnect.textContent = 'Reconectando…';
      try {
        await fetch('/api/nodos/reconnect', { method: 'POST' });
      } catch (e) {}
      btnReconnect.disabled = false;
      btnReconnect.textContent = 'Reconectar al broker';
      refreshDiag();
    });
  }
  if (btnWild) {
    btnWild.addEventListener('click', async function () {
      btnWild.disabled = true;
      try {
        await fetch('/api/nodos/wildcard?on=' + (wildOn ? 'false' : 'true'), { method: 'POST' });
      } catch (e) {}
      btnWild.disabled = false;
      refreshDiag();
    });
  }

  refresh();
  refreshDiag();
  setInterval(refresh, 3000);
  setInterval(refreshDiag, 2000);
})();
