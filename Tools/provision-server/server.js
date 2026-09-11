// ============================================================================
// server.js — Instalador LAN de pantallas PilotX (servidor interno del taller).
//
// Corre en la PC de desarrollo y sirve por HTTP en la LAN:
//   · la página web de manejo (public/index.html): login a OrbitX, clientes
//     (orgs), pedidos de instalación, equipos y su asignación;
//   · el kit de aprovisionamiento (Provision-Pantalla.ps1, KioskSetup,
//     branding, runtimes) y los paquetes PilotX_v*.zip del repo;
//   · /instalar.ps1?p=<pedido>: el script que corre la tablet nueva con UN
//     comando y hace todo (kit + PilotX + orbitX.json + kiosko).
//
// Contra OrbitX habla con el JWT del superadmin que se loguea en la web
// (queda en estado.json de esta PC, nunca viaja a la tablet). A la tablet le
// llega solo SU token de dispositivo, generado por OrbitX en el alta.
//
// Sin dependencias: Node 18+ y nada más (http + fs). Arrancar con
// Iniciar-Servidor.ps1 o `node server.js`.
// ============================================================================
"use strict";
const http = require("http");
const https = require("https");
const fs = require("fs");
const path = require("path");
const os = require("os");
const crypto = require("crypto");
const { URL } = require("url");

const AQUI = __dirname;
const REPO = path.resolve(AQUI, "..", "..");
const CONFIG_PATH = path.join(AQUI, "config.json");
const ESTADO_PATH = path.join(AQUI, "estado.json");
const RUNTIMES_DIR = path.join(AQUI, "runtimes");

const cfg = Object.assign({
  puerto: 8090,
  orbitx_url: "https://orbitx.agroparallel.com",
  ip_preferida: "",           // vacío = la primera 192.168.x que no sea VirtualBox/Hyper-V
  // ViewX TabletTools (NetApplyWatcher.ps1 y compañía): hermano del repo en Productos.
  tablettools_dir: path.resolve(REPO, "..", "..", "..", "..", "ViewX", "Software", "TabletTools"),
}, leerJson(CONFIG_PATH, {}));

let estado = Object.assign({ jwt: null, usuario: null, pedidos: [], eventos: [] }, leerJson(ESTADO_PATH, {}));

function leerJson(p, def) { try { return JSON.parse(fs.readFileSync(p, "utf8")); } catch { return def; } }
function guardarEstado() { try { fs.writeFileSync(ESTADO_PATH, JSON.stringify(estado, null, 2)); } catch (e) { log("no pude guardar estado: " + e.message); } }
function log(m) {
  const linea = new Date().toISOString().slice(11, 19) + " " + m;
  console.log(linea);
  estado.eventos.unshift(linea);
  if (estado.eventos.length > 200) estado.eventos.length = 200;
}

// ---------------------------------------------------------------------------
// Inventario local: paquetes, kit, runtimes, IPs
// ---------------------------------------------------------------------------
function versionDe(nombre) { const m = /^PilotX_v(\d+)\.(\d+)\.(\d+)\.zip$/i.exec(nombre); return m ? [+m[1], +m[2], +m[3]] : null; }
function paquetes() {
  const vistos = new Map();
  for (const dir of [REPO, path.join(REPO, "Build")]) {
    let ents = []; try { ents = fs.readdirSync(dir); } catch { }
    for (const f of ents) {
      const v = versionDe(f); if (!v) continue;
      const full = path.join(dir, f); const st = fs.statSync(full);
      const key = v.join(".");
      if (!vistos.has(key) || vistos.get(key).mtime < st.mtimeMs)
        vistos.set(key, { version: key, archivo: f, ruta: full, bytes: st.size, mtime: st.mtimeMs, v });
    }
  }
  return [...vistos.values()].sort((a, b) => (b.v[0] - a.v[0]) || (b.v[1] - a.v[1]) || (b.v[2] - a.v[2]));
}
function kit() {
  const items = [
    { nombre: "Provision-Pantalla.ps1", ruta: path.join(REPO, "Tools", "provision-pantalla", "Provision-Pantalla.ps1"), req: true },
    // Helper de red de ViewX TabletTools (tarea SYSTEM PilotXNetApply): sin él
    // PilotX no puede aplicar la IP fija del Ethernet (tablet de Clancy, 2026-09-11).
    { nombre: "TabletTools/NetApplyWatcher.ps1", ruta: path.join(cfg.tablettools_dir, "NetApplyWatcher.ps1"), req: true },
    { nombre: "PilotX-KioskSetup.exe", ruta: path.join(REPO, "Build", "PilotX-KioskSetup.exe"), req: true },
    { nombre: "Branding/logo.png", ruta: path.join(REPO, "Build", "Branding", "logo.png") },
    { nombre: "Branding/logo-fondo-blanco.png", ruta: path.join(REPO, "Build", "Branding", "logo-fondo-blanco.png") },
    { nombre: "Branding/fondo.png", ruta: path.join(REPO, "Build", "AgroParallel", "wwwroot", "img", "fondo.png") },
    { nombre: "vc_redist.x64.exe", ruta: path.join(RUNTIMES_DIR, "vc_redist.x64.exe"), url: "https://aka.ms/vs/17/release/vc_redist.x64.exe" },
    { nombre: "MicrosoftEdgeWebview2Setup.exe", ruta: path.join(RUNTIMES_DIR, "MicrosoftEdgeWebview2Setup.exe"), url: "https://go.microsoft.com/fwlink/p/?LinkId=2124703" },
  ];
  return items.map(i => ({ ...i, existe: fs.existsSync(i.ruta), bytes: fs.existsSync(i.ruta) ? fs.statSync(i.ruta).size : 0 }));
}
function ips() {
  const out = [];
  for (const [alias, lista] of Object.entries(os.networkInterfaces()))
    for (const a of lista || [])
      if (a.family === "IPv4" && !a.internal) out.push({ alias, ip: a.address, virtual: /virtualbox|vethernet|hyper-v|vmware|wsl/i.test(alias) || a.address.startsWith("192.168.56.") });
  return out;
}
function ipServidor(req) {
  if (cfg.ip_preferida) return cfg.ip_preferida;
  const host = (req && req.headers.host || "").split(":")[0];
  if (host && host !== "localhost" && host !== "127.0.0.1") return host;
  const l = ips().filter(i => !i.virtual);
  return (l.find(i => i.ip.startsWith("192.168.1.")) || l[0] || { ip: "127.0.0.1" }).ip;
}

// ---------------------------------------------------------------------------
// OrbitX (JWT del superadmin)
// ---------------------------------------------------------------------------
function orbitx(metodo, ruta, body, conJwt = true) {
  return new Promise((resolve, reject) => {
    const u = new URL(ruta, cfg.orbitx_url);
    const datos = body ? JSON.stringify(body) : null;
    const opts = { method: metodo, headers: { "Content-Type": "application/json", "Accept": "application/json" } };
    if (datos) opts.headers["Content-Length"] = Buffer.byteLength(datos);
    if (conJwt && estado.jwt) opts.headers["Authorization"] = "Bearer " + estado.jwt;
    const mod = u.protocol === "https:" ? https : http;
    const r = mod.request(u, opts, res => {
      let t = ""; res.on("data", c => t += c);
      res.on("end", () => {
        let j = null; try { j = JSON.parse(t); } catch { j = { error: t.slice(0, 200) }; }
        if (res.statusCode >= 200 && res.statusCode < 300) resolve(j);
        else { const e = new Error((j && j.error) || ("HTTP " + res.statusCode)); e.status = res.statusCode; e.body = j; reject(e); }
      });
    });
    r.on("error", reject);
    r.setTimeout(20000, () => r.destroy(new Error("timeout OrbitX")));
    if (datos) r.write(datos);
    r.end();
  });
}

// ---------------------------------------------------------------------------
// Pedidos de instalación
// ---------------------------------------------------------------------------
function nuevoCodigo() { const abc = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; let s = ""; for (let i = 0; i < 6; i++) s += abc[crypto.randomInt(abc.length)]; return s; }
function buscarPedido(codigo) { return estado.pedidos.find(p => p.codigo === String(codigo || "").toUpperCase()); }
function slugDe(nombre) {
  return String(nombre || "").normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "").slice(0, 40);
}

// El script que corre la tablet: plantilla con el servidor y el pedido adentro.
function scriptInstalar(req, codigo) {
  const tpl = fs.readFileSync(path.join(AQUI, "instalar.ps1"), "utf8");
  const base = "http://" + ipServidor(req) + ":" + cfg.puerto;
  return tpl.replace(/__SERVIDOR__/g, base).replace(/__PEDIDO__/g, codigo || "");
}

// ---------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------
function leerBody(req) {
  return new Promise((resolve, reject) => {
    let t = ""; req.on("data", c => { t += c; if (t.length > 1e6) req.destroy(); });
    req.on("end", () => { try { resolve(t ? JSON.parse(t) : {}); } catch (e) { reject(new Error("JSON inválido")); } });
    req.on("error", reject);
  });
}
function json(res, code, obj) { const b = JSON.stringify(obj); res.writeHead(code, { "Content-Type": "application/json; charset=utf-8", "Content-Length": Buffer.byteLength(b), "Cache-Control": "no-store" }); res.end(b); }
function texto(res, code, t, tipo) { res.writeHead(code, { "Content-Type": (tipo || "text/plain") + "; charset=utf-8", "Cache-Control": "no-store" }); res.end(t); }
function archivo(res, ruta, nombre) {
  fs.stat(ruta, (err, st) => {
    if (err || !st.isFile()) return json(res, 404, { error: "no existe " + (nombre || path.basename(ruta)) });
    res.writeHead(200, { "Content-Type": "application/octet-stream", "Content-Length": st.size, "Content-Disposition": "attachment; filename=\"" + (nombre || path.basename(ruta)) + "\"" });
    fs.createReadStream(ruta).pipe(res);
  });
}
function descargar(url, destino) {
  return new Promise((resolve, reject) => {
    const ir = (u, n) => {
      const mod = u.startsWith("https:") ? https : http;
      mod.get(u, r => {
        if ([301, 302, 303, 307, 308].includes(r.statusCode) && r.headers.location && n < 6) { r.resume(); return ir(new URL(r.headers.location, u).toString(), n + 1); }
        if (r.statusCode !== 200) { r.resume(); return reject(new Error("HTTP " + r.statusCode + " bajando " + u)); }
        fs.mkdirSync(path.dirname(destino), { recursive: true });
        const w = fs.createWriteStream(destino + ".part");
        r.pipe(w); w.on("finish", () => { fs.renameSync(destino + ".part", destino); resolve(); }); w.on("error", reject);
      }).on("error", reject);
    };
    ir(url, 0);
  });
}

const rutas = {
  // ---- estado general ----
  "GET /api/estado": async (req) => ({
    ok: true, ip: ipServidor(req), ips: ips(), puerto: cfg.puerto, orbitx_url: cfg.orbitx_url,
    logueado: !!estado.jwt, usuario: estado.usuario, paquetes: paquetes().slice(0, 10).map(p => ({ version: p.version, archivo: p.archivo, bytes: p.bytes, mtime: p.mtime })),
    kit: kit().map(k => ({ nombre: k.nombre, existe: k.existe, bytes: k.bytes, req: !!k.req })),
    pedidos: estado.pedidos, eventos: estado.eventos.slice(0, 60),
  }),
  // ---- OrbitX ----
  "POST /api/login": async (req, body) => {
    const r = await orbitx("POST", "/api/auth/login", { email: body.email, password: body.password }, false);
    if (!r || !r.token) throw new Error("OrbitX no devolvió token");
    estado.jwt = r.token; estado.usuario = (r.user && (r.user.email || r.user.nombre)) || body.email; guardarEstado();
    log("login OrbitX como " + estado.usuario);
    return { ok: true, usuario: estado.usuario };
  },
  "POST /api/logout": async () => { estado.jwt = null; estado.usuario = null; guardarEstado(); return { ok: true }; },
  "GET /api/orgs": async () => ({ ok: true, orgs: await orbitx("GET", "/api/admin/orgs") }),
  "POST /api/orgs": async (req, body) => {
    const nombre = String(body.nombre || "").trim(); const slug = slugDe(body.slug || nombre);
    if (!nombre || !slug) throw new Error("Falta el nombre del cliente");
    const r = await orbitx("POST", "/api/admin/org", { nombre, slug, provincia: body.provincia || "", ciudad: body.ciudad || "" });
    log("org creada en OrbitX: " + slug + " (" + nombre + ")");
    return { ok: true, slug: r.slug || slug, nombre };
  },
  "GET /api/equipos": async () => {
    const r = await orbitx("GET", "/api/devices");
    const lista = Array.isArray(r) ? r : (r.devices || r.dispositivos || r.items || []);
    return { ok: true, equipos: lista };
  },
  "POST /api/equipos/asignar": async (req, body) => {
    if (!body.device_id || !body.estab_slug) throw new Error("Falta device_id o estab_slug");
    await orbitx("POST", "/api/devices/" + encodeURIComponent(body.device_id) + "/asignar", { estab_slug: body.estab_slug, nombre: body.nombre || undefined });
    log("equipo " + body.device_id + " asignado a " + body.estab_slug);
    return { ok: true };
  },
  // ---- pedidos ----
  "POST /api/pedidos": async (req, body) => {
    const cliente = String(body.cliente || "").trim();
    if (!cliente) throw new Error("Falta el cliente");
    if (!body.estab_slug) throw new Error("Elegí la organización de OrbitX (o creala)");
    const paq = paquetes(); const version = body.version || (paq[0] && paq[0].version);
    if (!paq.find(p => p.version === version)) throw new Error("No hay paquete " + version);
    let codigo = nuevoCodigo(); while (buscarPedido(codigo)) codigo = nuevoCodigo();
    const p = {
      codigo, cliente, cuit: String(body.cuit || "").trim(), estab_slug: body.estab_slug,
      nombre_equipo: String(body.nombre_equipo || "").trim(), version, kiosko: body.kiosko !== false,
      creado: Date.now(), estado: "esperando", device_id: null, hostname: null, pasos: [],
    };
    estado.pedidos.unshift(p); guardarEstado();
    log("pedido " + codigo + " para " + cliente + " (" + p.estab_slug + ", PilotX " + version + ")");
    return { ok: true, pedido: p, comando: comandoDe(req, codigo) };
  },
  "DELETE /api/pedidos": async (req, body) => {
    const i = estado.pedidos.findIndex(p => p.codigo === String(body.codigo || "").toUpperCase());
    if (i >= 0) { estado.pedidos.splice(i, 1); guardarEstado(); }
    return { ok: true };
  },
  // ---- lo que llama la tablet ----
  "POST /api/registrar": async (req, body) => {
    const p = buscarPedido(body.pedido);
    if (!p) throw new Error("Pedido " + body.pedido + " no existe. Crealo en la web del instalador.");
    if (!/^[A-Z]{2}-[0-9A-F]{12}$/i.test(String(body.device_id || ""))) throw new Error("device_id inválido: " + body.device_id);
    if (!estado.jwt) throw new Error("El instalador no está logueado en OrbitX: entrá a la web y logueate.");
    const device_id = body.device_id.toUpperCase();
    const nombre = p.nombre_equipo || ("Pantalla PilotX - " + p.cliente);
    let token = null;
    // Alta en OrbitX con el id que la tablet calculó (MD5 del MAC, igual que
    // PilotX). Si ya existía (reinstalación) se regenera el token.
    try {
      const r = await orbitx("POST", "/api/devices/nuevo", { nombre, device_id_personalizado: device_id });
      token = r.token;
    } catch (e) {
      if (e.status !== 409) throw e;
      const r = await orbitx("POST", "/api/devices/" + encodeURIComponent(device_id) + "/regenerar-token", {});
      token = r.token || (r.device && r.device.token);
      if (!token) throw new Error("OrbitX no devolvió token al regenerar");
      log("equipo " + device_id + " ya existía: token regenerado");
    }
    await orbitx("POST", "/api/devices/" + encodeURIComponent(device_id) + "/asignar", { estab_slug: p.estab_slug, nombre });
    p.device_id = device_id; p.hostname = body.hostname || null; p.estado = "registrado"; p.registrado = Date.now();
    p.pasos.push({ t: Date.now(), msg: "registrado en OrbitX y asignado a " + p.estab_slug });
    guardarEstado();
    log("pedido " + p.codigo + ": equipo " + device_id + " (" + (body.hostname || "?") + ") dado de alta y asignado a " + p.estab_slug);
    return {
      ok: true, device_id, device_token: token, estab_slug: p.estab_slug, server_url: cfg.orbitx_url,
      cliente: p.cliente, cuit: p.cuit, nombre_equipo: p.nombre_equipo, version: p.version, kiosko: p.kiosko,
      paquete: "/paquetes/PilotX_v" + p.version + ".zip",
    };
  },
  "POST /api/progreso": async (req, body) => {
    const p = buscarPedido(body.pedido); if (!p) return { ok: false };
    p.pasos.push({ t: Date.now(), msg: String(body.msg || "").slice(0, 300) });
    if (body.estado) p.estado = body.estado;
    if (p.pasos.length > 80) p.pasos.splice(0, p.pasos.length - 80);
    guardarEstado(); log("pedido " + p.codigo + ": " + body.msg);
    return { ok: true };
  },
  "POST /api/runtimes": async () => {
    const faltan = kit().filter(k => k.url && !k.existe);
    for (const k of faltan) { log("bajando " + k.nombre); await descargar(k.url, k.ruta); log("listo " + k.nombre); }
    return { ok: true, bajados: faltan.map(k => k.nombre) };
  },
};
function comandoDe(req, codigo) { return "irm http://" + ipServidor(req) + ":" + cfg.puerto + "/instalar.ps1?p=" + codigo + " | iex"; }

const MIME = { ".html": "text/html", ".js": "application/javascript", ".css": "text/css", ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon" };

const server = http.createServer(async (req, res) => {
  const u = new URL(req.url, "http://x");
  const clave = (req.method === "HEAD" ? "GET" : req.method) + " " + u.pathname;
  try {
    if (rutas[clave]) {
      const body = (req.method === "POST" || req.method === "DELETE") ? await leerBody(req) : {};
      return json(res, 200, await rutas[clave](req, body));
    }
    if (u.pathname === "/instalar.ps1") return texto(res, 200, scriptInstalar(req, u.searchParams.get("p") || ""));
    if (u.pathname === "/red.ps1") {
      const base = "http://" + ipServidor(req) + ":" + cfg.puerto;
      return texto(res, 200, fs.readFileSync(path.join(AQUI, "red.ps1"), "utf8").replace(/__SERVIDOR__/g, base));
    }
    if (u.pathname === "/comando") { const c = u.searchParams.get("p") || ""; return texto(res, 200, comandoDe(req, c)); }
    if (u.pathname.startsWith("/paquetes/")) {
      const nombre = decodeURIComponent(u.pathname.slice("/paquetes/".length));
      const p = paquetes().find(x => x.archivo.toLowerCase() === nombre.toLowerCase());
      return p ? archivo(res, p.ruta, p.archivo) : json(res, 404, { error: "no existe el paquete " + nombre });
    }
    if (u.pathname.startsWith("/kit/")) {
      const nombre = decodeURIComponent(u.pathname.slice("/kit/".length));
      const k = kit().find(x => x.nombre.toLowerCase() === nombre.toLowerCase());
      return k ? archivo(res, k.ruta, path.basename(k.nombre)) : json(res, 404, { error: "no existe en el kit: " + nombre });
    }
    // estático
    let rel = u.pathname === "/" ? "/index.html" : u.pathname;
    const f = path.join(AQUI, "public", path.normalize(rel).replace(/^([.][.][\\/])+/, ""));
    if (f.startsWith(path.join(AQUI, "public")) && fs.existsSync(f) && fs.statSync(f).isFile()) {
      res.writeHead(200, { "Content-Type": (MIME[path.extname(f)] || "application/octet-stream") + "; charset=utf-8", "Cache-Control": "no-store" });
      return fs.createReadStream(f).pipe(res);
    }
    json(res, 404, { error: "no existe " + u.pathname });
  } catch (e) {
    log("ERROR " + clave + ": " + e.message);
    json(res, e.status && e.status >= 400 && e.status < 500 ? e.status : 500, { ok: false, error: e.message });
  }
});

server.listen(cfg.puerto, "0.0.0.0", () => {
  const l = ips().filter(i => !i.virtual).map(i => "http://" + i.ip + ":" + cfg.puerto).join("  ");
  log("Instalador LAN de PilotX escuchando en " + l);
  log("repo: " + REPO + " · paquetes: " + paquetes().map(p => p.version).join(", "));
  if (!estado.jwt) log("sin login en OrbitX: entrá a la web y logueate para poder dar de alta equipos");
});
