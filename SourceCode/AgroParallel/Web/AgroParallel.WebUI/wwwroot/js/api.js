// ============================================================================
// api.js — wrapper mínimo para fetch + WebSocket contra AgpWebHost.
// ============================================================================
(function (root) {
  'use strict';

  function apiUrl(path) {
    if (path.startsWith('/')) path = path.substring(1);
    return '/api/' + path;
  }

  async function get(path) {
    const r = await fetch(apiUrl(path), { method: 'GET' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return await r.json();
  }

  async function post(path, query) {
    let url = apiUrl(path);
    if (query && typeof query === 'object') {
      const qs = new URLSearchParams();
      Object.keys(query).forEach(k => qs.append(k, query[k]));
      url += '?' + qs.toString();
    }
    const r = await fetch(url, { method: 'POST' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return await r.json();
  }

  function openTelemetry(onMessage) {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(proto + '//' + location.host + '/ws/telemetry');
    ws.onmessage = ev => {
      try { onMessage(JSON.parse(ev.data)); } catch (e) { /* ignore */ }
    };
    return ws;
  }

  root.agpApi = { get, post, openTelemetry };
})(window);
