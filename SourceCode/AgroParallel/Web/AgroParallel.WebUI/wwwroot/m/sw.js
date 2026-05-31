// ============================================================================
// AgroParallel Field PWA — service worker
// Cachea SOLO el app-shell (HTML/CSS/JS estatico). Todo /api/* y /telemetry
// va directo a la red — los comandos al tractor NUNCA deben servirse del cache.
// ============================================================================

const CACHE = 'ap-field-v1';
const SHELL = [
  '/m/',
  '/m/index.html',
  '/m/login.html',
  '/m/quantix.html',
  '/m/secciones.html',
  '/m/flowx.html',
  '/m/vistax.html',
  '/m/m.css',
  '/m/m.js',
  '/m/manifest.webmanifest'
];

self.addEventListener('install', function (e) {
  self.skipWaiting();
  e.waitUntil(caches.open(CACHE).then(function (c) { return c.addAll(SHELL).catch(function () {}); }));
});

self.addEventListener('activate', function (e) {
  e.waitUntil(
    caches.keys().then(function (names) {
      return Promise.all(names.map(function (n) { if (n !== CACHE) return caches.delete(n); }));
    }).then(function () { return self.clients.claim(); })
  );
});

self.addEventListener('fetch', function (e) {
  var url = new URL(e.request.url);
  // API y WebSocket → red siempre (NO cachear comandos).
  if (url.pathname.indexOf('/api/') === 0 || url.pathname.indexOf('/telemetry') === 0) {
    return; // default network handling
  }
  // Shell estatico → cache-first.
  if (e.request.method === 'GET' && url.pathname.indexOf('/m/') === 0) {
    e.respondWith(
      caches.match(e.request).then(function (hit) {
        return hit || fetch(e.request).then(function (resp) {
          var copy = resp.clone();
          caches.open(CACHE).then(function (c) { c.put(e.request, copy).catch(function () {}); });
          return resp;
        }).catch(function () { return caches.match('/m/index.html'); });
      })
    );
  }
});
