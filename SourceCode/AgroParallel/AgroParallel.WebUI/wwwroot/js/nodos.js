// ============================================================================
// nodos.js — vista en vivo de nodos AgroParallel (MQTT discovery).
// Pollea /api/nodos cada 3s y renderiza la lista con filtros por tipo.
// ============================================================================

(function () {
  'use strict';

  var listEl = document.getElementById('nodeList');
  var filterEl = document.getElementById('filterBar');
  var statsEl = document.getElementById('headerStats');
  var rescanBtn = document.getElementById('rescanBtn');

  var currentFilter = 'all';
  var nodos = [];

  function typeColor(t) {
    var k = (t || '').toLowerCase();
    if (k.indexOf('quantix') >= 0) return '#4ABA3E';
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

  function render() {
    if (!listEl) return;

    var filtered = nodos.filter(function (n) {
      if (currentFilter === 'all') return true;
      var t = (n.type || '').toLowerCase();
      return t.indexOf(currentFilter) >= 0;
    });

    // Stats header
    var online = nodos.filter(function (n) { return n.online; }).length;
    var offline = nodos.length - online;
    if (statsEl) {
      statsEl.innerHTML =
        '<span class="pill ok"><span class="dot"></span> ' + online + ' online</span>' +
        '<span class="pill idle"><span class="dot"></span> ' + offline + ' offline</span>';
    }

    // Filter bar counts
    if (filterEl) {
      var counts = { all: nodos.length };
      nodos.forEach(function (n) {
        var t = (n.type || '').toLowerCase();
        counts[t] = (counts[t] || 0) + 1;
      });
      filterEl.querySelectorAll('button[data-filter]').forEach(function (btn) {
        var key = btn.getAttribute('data-filter');
        var label = btn.getAttribute('data-label') || key;
        var c = key === 'all' ? counts.all : (counts[key] || 0);
        btn.textContent = label + ' (' + c + ')';
        btn.classList.toggle('active', currentFilter === key);
      });
    }

    if (filtered.length === 0) {
      listEl.innerHTML =
        '<div class="card" style="text-align:center; color:var(--agp-text-muted)">' +
        'Buscando nodos en la red MQTT…<br>' +
        '<span style="font-size:var(--agp-fs-sm)">Verificá que el broker MQTT (AgIO) esté corriendo.</span>' +
        '</div>';
      return;
    }

    listEl.innerHTML = filtered.map(function (n) {
      var color = typeColor(n.type);
      var stateClass = n.online ? '' : 'offline';
      var statePill = n.online
        ? '<span class="pill ok"><span class="dot"></span> ' + (n.firmware ? 'v' + escapeHtml(n.firmware) + ' · ' : '') + 'online</span>'
        : '<span class="pill idle"><span class="dot"></span> ' + fmtAgo(n.lastSeenUtc) + '</span>';

      var ip = n.ip ? escapeHtml(n.ip) : '—';
      var uptime = fmtUptime(n.uptime);
      var motors = n.motors > 0 ? ' · ' + n.motors + ' motor' + (n.motors > 1 ? 'es' : '') : '';

      return '' +
        '<div class="node ' + stateClass + '" data-uid="' + escapeHtml(n.uid) + '">' +
          '<span class="led" style="background:' + (n.online ? '#4ABA3E' : '#707070') + '"></span>' +
          '<div>' +
            '<div class="name" style="color:' + color + '">' + escapeHtml(n.type || '?') + motors + '</div>' +
            '<div class="uid">' + escapeHtml(n.uid) + '</div>' +
          '</div>' +
          '<div class="ip">' + ip + '</div>' +
          '<div style="font-size:var(--agp-fs-sm); color:var(--agp-text-muted)">up ' + uptime + '</div>' +
          statePill +
        '</div>';
    }).join('');
  }

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  async function refresh() {
    try {
      var res = await fetch('/api/nodos', { cache: 'no-store' });
      var data = await res.json();
      nodos = (data && data.nodos) || [];
    } catch (e) {
      nodos = [];
    }
    render();
  }

  // Bind filter clicks
  if (filterEl) {
    filterEl.addEventListener('click', function (ev) {
      var btn = ev.target.closest('button[data-filter]');
      if (!btn) return;
      currentFilter = btn.getAttribute('data-filter');
      render();
    });
  }

  if (rescanBtn) {
    rescanBtn.addEventListener('click', refresh);
  }

  refresh();
  setInterval(refresh, 3000);
})();
