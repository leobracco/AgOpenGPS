// Render de una vista suelta para rasterizar con Chrome headless.
// ?v=tipo|antena|dim  &m=tractor|articulado|cosechadora|pulverizadora
const qs = new URLSearchParams(location.search);
const V = qs.get('v'), M = MAQ.find(x => x.id === qs.get('m'));

const VERDE = '#2F7F27', AZUL = '#2F6FB0', NARANJA = '#D2601F', ROJO = '#D9342B';

// Sin textos (la imagen se usa en es/en/pt), sin pines ni cables.
const limpiar = s => s
  .replace(/<g class="pin"[\s\S]*?<\/g>/g, '')
  .replace(/<text[\s\S]*?<\/text>/g, '');

// Planta derecha: sin el chasis girado del articulado ni las ruedas dobladas.
const plantaRecta = m => limpiar(planta(m, false))
  .replace(/rotate\(-10 /g, 'rotate(0 ')
  .replace(/rotate\(14 /g, 'rotate(0 ')
  .replace(/^<svg[^>]*>/, '').replace(/<\/svg>$/, '')
  .replace(/var\(--body\)/g, '#3B4855').replace(/var\(--body2\)/g, '#4A5866').replace(/var\(--vidrio\)/g, '#1B3A46')
  .replace(/stroke="#2E3944"/g, 'stroke="#9AA59B"');

// Flecha de cota con puntas en los dos extremos.
function cota(x1, y1, x2, y2, color, w = 4){
  const a = Math.atan2(y2 - y1, x2 - x1), L = 16, ab = 0.45;
  const punta = (x, y, ang) => `<path d="M${x} ${y} L${x - L*Math.cos(ang - ab)} ${y - L*Math.sin(ang - ab)} L${x - L*Math.cos(ang + ab)} ${y - L*Math.sin(ang + ab)} Z" fill="${color}"/>`;
  return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${color}" stroke-width="${w}"/>` + punta(x2, y2, a) + punta(x1, y1, a + Math.PI);
}
const guia = (x1, y1, x2, y2, color) => `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${color}" stroke-width="2.5" stroke-dasharray="8 6"/>`;
const antena = (x, y) => `<rect x="${x-16}" y="${y-2}" width="32" height="8" rx="3" fill="#2C3742"/><path d="M${x-13} ${y} A13 11 0 0 1 ${x+13} ${y} Z" fill="${ROJO}"/>`;

// Pivote que usa el motor (setVehicle_antennaPivot): eje trasero en el rígido,
// eje DELANTERO en cosechadora y articulado (igual que los diagramas de AOG).
// Antena corrida del pivote a propósito para que la cota se vea.
const GEO = {
  tractor:     { piv: 300, ax: 400, ay: 82,  xAlt: 850, vb: '60 20 760 390' },
  articulado:  { piv: 790, ax: 588, ay: 74,  xAlt: 965, vb: '80 20 910 390' },
  cosechadora: { piv: 640, ax: 712, ay: 38,  xAlt: 120, vb: '70 0 935 405' },
  pulverizadora:{ piv: 255, ax: 736, ay: 28, xAlt: 900, vb: '80 0 900 405' },
};

let svg = '', vb = '';
if (V === 'tipo') {
  svg = DEFS + limpiar(M.perfil);
  vb = GEO[M.id].vb;
} else if (V === 'antena') {
  const g = GEO[M.id];
  let s = DEFS + limpiar(M.perfil);
  // Pivote: línea punteada vertical del eje al techo.
  s += guia(g.piv, 392, g.piv, g.ay - 40, AZUL);
  s += guia(g.ax, g.ay - 4, g.ax, g.ay - 40, AZUL);
  s += cota(g.piv, g.ay - 32, g.ax, g.ay - 32, AZUL);
  // Signo de la distancia al pivote (convención del motor): antena DELANTE
  // del eje = +, DETRÁS = −. Avance hacia la derecha.
  const signo = (x, txt) => `<circle cx="${x}" cy="${g.ay - 70}" r="17" fill="#fff" stroke="${AZUL}" stroke-width="3"/>`
    + `<text x="${x}" y="${g.ay - 70}" fill="${AZUL}" font-size="30" font-weight="700" font-family="Segoe UI, Arial, sans-serif" text-anchor="middle" dominant-baseline="central">${txt}</text>`;
  s += signo(g.piv - 34, '−') + signo(g.piv + 34, '+');
  s += guia(g.piv, g.ay - 40, g.piv, g.ay - 92, AZUL);
  // Altura: del centro de la antena al piso.
  s += guia(g.ax + 16, g.ay, g.xAlt + (g.xAlt > g.ax ? 14 : -14), g.ay, VERDE);
  s += cota(g.xAlt, g.ay, g.xAlt, 392, VERDE);
  s += antena(g.ax, g.ay);
  // Vista de arriba debajo, con el offset lateral.
  const t = M.top, Y = 150, dy = 470, off = 46;
  let p = plantaRecta(M);
  const axP = M.id === 'articulado' ? t.ant : t.ant;
  p += cota(axP + 60, Y, axP + 60, Y - off, NARANJA);
  p += guia(axP, Y - off, axP + 70, Y - off, NARANJA);
  p += `<circle cx="${axP}" cy="${Y - off}" r="15" fill="${ROJO}" stroke="#fff" stroke-width="3"/>`;
  s += `<g transform="translate(0 ${dy})">${p}</g>`;
  svg = s;
  vb = `${g.vb.split(' ')[0]} ${Math.min(+g.vb.split(' ')[1], g.ay - 100)} ${g.vb.split(' ')[2]} ${dy + 340 - Math.min(+g.vb.split(' ')[1], g.ay - 100)}`;
} else if (V === 'dim') {
  const t = M.top, Y = 150;
  let s = plantaRecta(M);
  // Entre ejes: abajo, de eje a eje.
  const yE = Y + Math.max(t.trT + t.rT[1]/2, t.trD + t.rD[1]/2) + 30;
  s += guia(t.ejeT, Y + t.trT + t.rT[1]/2 + 4, t.ejeT, yE + 12, VERDE) + guia(t.ejeD, Y + t.trD + t.rD[1]/2 + 4, t.ejeD, yE + 12, VERDE);
  s += cota(t.ejeT, yE, t.ejeD, yE, VERDE);
  // Trocha: de centro a centro de rueda, en el eje que dobla (o el delantero).
  const ej = t.dir === 'tras' ? t.ejeT : t.ejeD, tr = t.dir === 'tras' ? t.trT : t.trD, rw = t.dir === 'tras' ? t.rT[0] : t.rD[0];
  const xT = ej + rw/2 + 34;
  s += guia(ej, Y - tr, xT + 12, Y - tr, AZUL) + guia(ej, Y + tr, xT + 12, Y + tr, AZUL);
  s += cota(xT, Y - tr, xT, Y + tr, AZUL);
  // Avance
  const xFin = Math.max(t.L[1], xT + 30, t.cabezal ? t.cabezal[1] : 0) + 40;
  s += `<line x1="${xFin - 90}" y1="12" x2="${xFin - 14}" y2="12" stroke="#9AA59B" stroke-width="3"/><path d="M${xFin} 12 L${xFin - 16} 4 L${xFin - 16} 20 Z" fill="#9AA59B"/>`;
  svg = s;
  vb = `${t.L[0] - 40} -30 ${xFin - t.L[0] + 50} ${yE + 50}`;
}
document.body.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}" preserveAspectRatio="xMidYMid meet" style="display:block;width:100vw;height:100vh">${svg}</svg>`;
