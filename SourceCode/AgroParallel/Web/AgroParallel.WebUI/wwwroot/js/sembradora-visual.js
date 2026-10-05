// ============================================================================
// sembradora-visual.js — Render dinámico de la sembradora desde ImplementoDto.
//
// Dibuja una vista superior con:
//  · Tractor + enganche
//  · 1 o 2 trenes (filas trasera/delantera) según dto.trenes
//  · Surcos numerados, coloreados por TORRE (agrupamiento físico)
//  · Borde/marca por SECCIÓN PilotX
//  · Resumen estadístico al pie
//
// Reglas de agrupamiento por torre:
//  · Si dto.numero_torres > 0  → reparte surcos en N torres consecutivas.
//    Si hay 2 trenes, divide las torres entre ambos en proporción a la cantidad
//    de surcos por tren.
//  · Si dto.numero_torres == 0 → una sola "torre virtual" por tren (sin color).
//
// API pública:
//   window.SembradoraVisual.render(implDto, containerId)
//
// Diseño: NO escribe en state.impl, solo lee. Es seguro llamarlo en cada
// render() de la página activa.
// ============================================================================
(function () {
  'use strict';

  // Paleta de 8 colores rotativa por torre. Match con la línea PilotX
  // (verde acento + acompañamiento). Pensada para sembradoras de hasta 8 torres.
  var TORRE_COLORS = [
    '#4BA63F', // accent verde
    '#F5C542', // amarillo
    '#3B82F6', // azul
    '#F97316', // naranja
    '#22D3EE', // cian
    '#A855F7', // violeta
    '#EC4899', // rosa
    '#6DC231'  // verde lima
  ];

  // ---- helpers --------------------------------------------------------

  function num(v, def) { return (typeof v === 'number' && isFinite(v)) ? v : def; }
  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

  // Devuelve trenes ordenados por distancia_m ascendente (0 = trasero, mayor = delantero).
  function trenesOrdenados(dto) {
    var ts = (dto.trenes || []).slice();
    ts.sort(function (a, b) { return (a.distancia_m || 0) - (b.distancia_m || 0); });
    return ts;
  }

  // Para un set de surcos de un tren, calcula a qué torre pertenece cada uno.
  // Retorna array paralelo a `surcos` con el id de torre (1-based global).
  function asignarTorres(surcosTren, torreOffset, surcosPorTorre) {
    var result = [];
    if (surcosPorTorre <= 0) {
      for (var i = 0; i < surcosTren.length; i++) result.push(torreOffset + 1);
      return result;
    }
    for (var k = 0; k < surcosTren.length; k++) {
      var idx = Math.floor(k / surcosPorTorre);
      result.push(torreOffset + idx + 1);
    }
    return result;
  }

  // ---- render principal ----------------------------------------------

  function render(dto, containerId) {
    var host = document.getElementById(containerId);
    if (!host) return;
    if (!dto) {
      host.innerHTML = '<div class="semb-empty">Sin implemento cargado</div>';
      return;
    }

    var trenes = trenesOrdenados(dto);
    if (trenes.length === 0) {
      host.innerHTML = '<div class="semb-empty">Sin trenes definidos</div>';
      return;
    }

    var totalSurcos = (dto.surcos || []).length;
    if (totalSurcos === 0) {
      host.innerHTML =
        '<div class="semb-empty">Sin surcos. Cargá la cantidad de surcos en la pestaña <b>Surcos</b>.</div>';
      return;
    }

    var totalTorres = Math.max(0, dto.numero_torres | 0);

    // Reparte surcos entre trenes según TrenId.
    var porTren = {};
    trenes.forEach(function (t) { porTren[t.id] = []; });
    (dto.surcos || []).slice()
      .sort(function (a, b) { return (a.numero || 0) - (b.numero || 0); })
      .forEach(function (s) {
        var tid = s.tren_id != null ? s.tren_id : trenes[0].id;
        if (!porTren[tid]) porTren[tid] = [];
        porTren[tid].push(s);
      });

    // Reparte torres entre trenes en proporción a surcos por tren.
    var torresPorTren = {};   // tren_id → cantidad torres
    var torreOffset = {};     // tren_id → offset (id base 0)
    if (totalTorres > 0 && trenes.length > 0) {
      var asignadas = 0;
      trenes.forEach(function (t, idx) {
        var n = (porTren[t.id] || []).length;
        var prop = totalSurcos > 0 ? (n / totalSurcos) : (1 / trenes.length);
        var cant = (idx === trenes.length - 1)
          ? (totalTorres - asignadas)
          : Math.round(totalTorres * prop);
        cant = Math.max(0, cant);
        torresPorTren[t.id] = cant;
        torreOffset[t.id] = asignadas;
        asignadas += cant;
      });
    } else {
      trenes.forEach(function (t, idx) {
        torresPorTren[t.id] = 1;     // 1 torre virtual por tren
        torreOffset[t.id] = idx;
      });
    }

    // Para cada surco calculá su torre.
    var surcoTorre = {};
    trenes.forEach(function (t) {
      var lst = porTren[t.id] || [];
      var nTorres = torresPorTren[t.id] || 1;
      var perTorre = nTorres > 0 ? Math.ceil(lst.length / nTorres) : lst.length;
      var asign = asignarTorres(lst, torreOffset[t.id], perTorre);
      lst.forEach(function (s, i) { surcoTorre[s.numero] = asign[i]; });
    });

    // ---- HTML del componente ----
    var trenesHtml = trenes.slice().reverse().map(function (t) {
      // .reverse(): mostrar delantero arriba, trasero abajo (vista superior).
      return renderFila(t, porTren[t.id] || [], surcoTorre, totalTorres);
    }).join('');

    var resumen = renderResumen(dto, totalSurcos, totalTorres, trenes, porTren);

    host.innerHTML =
      '<div class="semb-vis">' +
        '<div class="semb-tractor">' +
          '<div class="semb-tractor-body">' +
            '<div class="semb-tractor-cab"></div>' +
            '<div class="semb-tractor-lbl">TRACTOR</div>' +
          '</div>' +
        '</div>' +
        '<div class="semb-hitch"></div>' +
        '<div class="semb-frame">' +
          '<div class="semb-frame-head">' +
            '<span>' + escapeHtml(dto.nombre || 'Implemento') + '</span>' +
            '<span class="semb-frame-meta">' + totalSurcos + ' surcos · ' +
              trenes.length + (trenes.length > 1 ? ' trenes' : ' tren') +
              (totalTorres > 0 ? ' · ' + totalTorres + ' torres' : '') + '</span>' +
          '</div>' +
          trenesHtml +
          resumen +
        '</div>' +
      '</div>';
  }

  function renderFila(tren, surcosTren, surcoTorre, totalTorres) {
    if (surcosTren.length === 0) {
      return '<div class="semb-fila">' +
        '<div class="semb-fila-head">' + escapeHtml(tren.nombre || ('Tren ' + tren.id)) +
        ' · sin surcos</div></div>';
    }
    var primer = surcosTren[0].numero;
    var ultimo = surcosTren[surcosTren.length - 1].numero;

    // Cuadraditos de surcos
    var cells = surcosTren.map(function (s) {
      var torreId = surcoTorre[s.numero] || 1;
      var color = totalTorres > 0
        ? TORRE_COLORS[(torreId - 1) % TORRE_COLORS.length]
        : 'var(--agp-accent)';
      var sec = s.seccion_pilotx || 0;
      var secAttr = sec > 0 ? ' data-sec="' + sec + '"' : '';
      return '<div class="semb-surco" style="background:' + color + '"' +
        ' title="Surco ' + s.numero + ' · Torre ' + torreId +
        (sec > 0 ? ' · Sec ' + sec : '') + '"' + secAttr + '>' +
        '<span>' + s.numero + '</span></div>';
    }).join('');

    // Barras de torres por debajo
    var torres = {};
    surcosTren.forEach(function (s) {
      var t = surcoTorre[s.numero] || 1;
      if (!torres[t]) torres[t] = { id: t, from: s.numero, to: s.numero, count: 0 };
      torres[t].from = Math.min(torres[t].from, s.numero);
      torres[t].to = Math.max(torres[t].to, s.numero);
      torres[t].count++;
    });
    var torresArr = Object.keys(torres).map(function (k) { return torres[k]; })
      .sort(function (a, b) { return a.from - b.from; });

    var torresHtml = totalTorres > 0 ? torresArr.map(function (tr) {
      var color = TORRE_COLORS[(tr.id - 1) % TORRE_COLORS.length];
      return '<div class="semb-torre" style="flex:' + tr.count +
        ';border-color:' + color + ';color:' + color + '">' +
        '<div class="semb-torre-bar" style="background:' + color + '"></div>' +
        '<div class="semb-torre-lbl">T' + tr.id + ' · ' + tr.count + ' surcos</div>' +
        '</div>';
    }).join('') : '';

    return '<div class="semb-fila">' +
      '<div class="semb-fila-head">' +
        '<span class="semb-fila-name">' + escapeHtml(tren.nombre || ('Tren ' + tren.id)) + '</span>' +
        '<span class="semb-fila-rng">surcos ' + primer + '–' + ultimo +
        ' (' + surcosTren.length + ')</span>' +
      '</div>' +
      '<div class="semb-row">' + cells + '</div>' +
      (torresHtml ? '<div class="semb-torres">' + torresHtml + '</div>' : '') +
    '</div>';
  }

  function renderResumen(dto, totalSurcos, totalTorres, trenes, porTren) {
    // Stats por sección PilotX
    var porSec = {};
    (dto.surcos || []).forEach(function (s) {
      var k = s.seccion_pilotx || 0;
      porSec[k] = (porSec[k] || 0) + 1;
    });
    var nSec = (dto.secciones || []).length;
    var asignados = totalSurcos - (porSec[0] || 0);
    var ancho = num(dto.ancho_total_m, 0);
    var dist = num(dto.distancia_entre_surcos_m, 0);

    var stats = [];
    stats.push(stat(totalSurcos, 'surcos'));
    stats.push(stat(trenes.length, trenes.length === 1 ? 'tren' : 'trenes'));
    if (totalTorres > 0) stats.push(stat(totalTorres, 'torres'));
    if (nSec > 0) stats.push(stat(nSec, nSec === 1 ? 'sección' : 'secciones'));
    if (nSec > 0 && totalSurcos > 0) {
      var pct = Math.round(100 * asignados / totalSurcos);
      stats.push(stat(asignados + '/' + totalSurcos, 'surcos asignados (' + pct + '%)'));
    }
    if (ancho > 0) stats.push(stat(ancho.toFixed(2) + ' m', 'ancho total'));
    if (dist > 0) stats.push(stat((dist * 100).toFixed(1) + ' cm', 'entre surcos'));

    return '<div class="semb-stats">' + stats.join('') + '</div>';
  }

  function stat(v, l) {
    return '<div class="semb-stat"><div class="semb-stat-v">' + v + '</div>' +
      '<div class="semb-stat-l">' + l + '</div></div>';
  }

  function escapeHtml(s) {
    return String(s || '').replace(/[&<>"']/g, function (c) {
      return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c];
    });
  }

  // ---- export ---------------------------------------------------------

  window.SembradoraVisual = { render: render };
})();
