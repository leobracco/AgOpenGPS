// ============================================================================
// hub.js — vista resumen del estado del piloto en tiempo real.
// Pollea /api/aog/state (1Hz) y /api/nodos (3s).
// ============================================================================

(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  function fmtNum(v, dec) {
    if (v == null || isNaN(v)) return '—';
    return Number(v).toFixed(dec == null ? 1 : dec);
  }

  function radToDeg(r) {
    if (r == null || isNaN(r)) return null;
    var d = r * 180 / Math.PI;
    d = ((d % 360) + 360) % 360;
    return d;
  }

  function fmtLatLon(lat, lon) {
    if (!lat || !lon || (lat === 0 && lon === 0)) return '—';
    return lat.toFixed(5) + '°, ' + lon.toFixed(5) + '°';
  }

  function setPill(el, cls, text) {
    if (!el) return;
    el.className = 'pill ' + cls;
    el.innerHTML = '<span class="dot"></span> ' + text;
  }

  function renderSections(numSections, sectionOnRequest) {
    var row = $('sectionsRow');
    if (!row) return;
    var n = Math.max(0, Math.min(16, numSections || 0));
    if (row.children.length !== n) {
      row.innerHTML = '';
      for (var i = 0; i < n; i++) row.appendChild(document.createElement('span'));
    }
    var on = 0;
    for (var i = 0; i < n; i++) {
      var active = sectionOnRequest && sectionOnRequest[i];
      row.children[i].classList.toggle('on', !!active);
      if (active) on++;
    }
    $('kpiSecCount').textContent = on + ' de ' + n + (n === 1 ? ' abierta' : ' abiertas');
  }

  async function refreshState() {
    try {
      var res = await fetch('/api/aog/state', { cache: 'no-store' });
      var s = await res.json();
      if (!s) return;

      $('kpiSpeed').textContent = fmtNum(s.AvgSpeed != null ? s.AvgSpeed : s.avgSpeed, 1);
      var hdg = radToDeg(s.Heading != null ? s.Heading : s.heading);
      $('kpiHeading').textContent = (hdg == null ? '— ' : Math.round(hdg)) + '°';

      var dose = s.ShapeCurrentDose != null ? s.ShapeCurrentDose : s.shapeCurrentDose;
      $('kpiDose').textContent = dose ? fmtNum(dose, 0) : '—';
      var inside = s.ShapeIsInside != null ? s.ShapeIsInside : s.shapeIsInside;
      if (dose && inside) setPill($('kpiShape'), 'ok', 'dentro de zona');
      else if (dose) setPill($('kpiShape'), 'warn', 'fuera de zona');
      else setPill($('kpiShape'), 'idle', 'sin shape');

      var numSec = s.NumSections != null ? s.NumSections : s.numSections;
      var secReq = s.SectionOnRequest != null ? s.SectionOnRequest : s.sectionOnRequest;
      renderSections(numSec, secReq);
      var w = s.ToolWidth != null ? s.ToolWidth : s.toolWidth;
      $('kpiToolWidth').textContent = (w ? fmtNum(w, 2) : '—') + ' m';

      var lat = s.Latitude != null ? s.Latitude : s.latitude;
      var lon = s.Longitude != null ? s.Longitude : s.longitude;
      $('kpiLatLon').textContent = fmtLatLon(lat, lon);

      var field = s.CurrentFieldDirectory != null ? s.CurrentFieldDirectory : s.currentFieldDirectory;
      $('kpiField').textContent = field || '—';

      var jobStarted = s.IsJobStarted != null ? s.IsJobStarted : s.isJobStarted;
      setPill($('pillJob'), jobStarted ? 'ok' : 'idle', jobStarted ? 'Trabajo activo' : 'Sin trabajo');
    } catch (e) {
      setPill($('pillJob'), 'bad', 'Piloto offline');
    }
  }

  async function refreshNodos() {
    try {
      var res = await fetch('/api/nodos', { cache: 'no-store' });
      var data = await res.json();
      var nodos = (data && data.nodos) || [];
      var online = nodos.filter(function (n) { return n.online; }).length;
      // brokerConnected viene del NodoRegistryService.GetDiagnostic().Connected;
      // distingue "CoreX broker arriba" (verde) de "no hay nodos todavía" (idle).
      var brokerOk = !!(data && data.brokerConnected);

      setPill($('pillBroker'),
        brokerOk ? 'ok' : 'bad',
        brokerOk ? 'Broker MQTT' : 'Broker MQTT offline');
      setPill($('pillNodos'), online > 0 ? 'ok' : 'idle', online + (online === 1 ? ' nodo' : ' nodos'));

      var list = $('hubNodeList');
      if (!list) return;
      if (!nodos.length) {
        list.innerHTML = '<div class="card" style="text-align:center; color:var(--agp-text-muted)">Sin nodos detectados todavía</div>';
        return;
      }
      list.innerHTML = nodos.slice(0, 6).map(function (n) {
        var stateClass = n.online ? '' : 'offline';
        var fwBadge = n.online
          ? '<span class="pill ok"><span class="dot"></span> ' + (n.firmware ? 'v' + esc(n.firmware) + ' · ' : '') + 'online</span>'
          : '<span class="pill idle"><span class="dot"></span> offline</span>';
        return '' +
          '<div class="node ' + stateClass + '">' +
            '<span class="led"></span>' +
            '<div><div class="name">' + esc(n.type || '?') + '</div><div class="uid">' + esc(n.uid) + '</div></div>' +
            '<div class="ip">' + esc(n.ip || '—') + '</div>' +
            fwBadge +
          '</div>';
      }).join('');
    } catch (e) {
      setPill($('pillBroker'), 'bad', 'Broker MQTT');
    }
  }

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  refreshState();
  refreshNodos();
  setInterval(refreshState, 1000);
  setInterval(refreshNodos, 3000);
})();
