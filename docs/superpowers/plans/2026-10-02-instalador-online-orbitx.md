# Instalador online — Parte A: OrbitX — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que OrbitX soporte el `PilotX-Instalador.exe`: códigos de vinculación de origen instalador aprobados solo por superadmin, perfil de instalación con claves de una sola entrega, progreso y diagnóstico de red, CUIT/edición de org, panel de aprobación y descarga pública del .exe.

**Architecture:** La lógica nueva vive en módulos puros (`lib/pairing.js`, `lib/instalacion.js`, `lib/org-datos.js`) testeados con `node --test` y un CouchDB falso en memoria; las rutas Express quedan finas y delegan. Estado de instalación en el doc `instalacion_<device_id>` de `orbitx_global`. Spec: `AgOpenGPS/docs/superpowers/specs/2026-10-02-instalador-online-design.md`.

**Tech Stack:** Node.js + Express (CommonJS), CouchDB vía `nano` (`services/couchdb.js`), EJS (panel), `node:test` + `node:assert/strict` (tests `.mjs` con `createRequire`).

## Global Constraints

- Repo: `G:\AgroParallel\Productos\OrbitX\Software\App_PC\OrbitX-Server`. **La copia de trabajo local está divergida de `origin/main` (producción). NO trabajar sobre ella**: todo en un worktree nuevo desde `origin/main` (Task 0).
- Castellano rioplatense en mensajes, logs y comentarios nuevos; nombres de código existentes en inglés/castellano como están.
- En textos visibles nunca se nombra el producto anterior de la pantalla: se dice "instalación anterior".
- Los códigos de origen `instalador` los aprueba **solo** `rol_global === "superadmin"`. El pairing sin `origen` (menú de PilotX) se comporta exactamente como hoy.
- Las claves `soporte`/RustDesk se entregan **una sola vez** por instalación.
- Doc ids nuevos: `instalacion_<device_id>` (tipo `"instalacion"`), en `orbitx_global`.
- Tests: `node --test tests/**/*.test.mjs` (script `npm test`). Sin dependencias nuevas.
- No reiniciar Docker/CouchDB. Deploy solo con confirmación del usuario (Task 9).

## File Structure

| Archivo | Responsabilidad |
|---|---|
| Create `lib/pairing.js` | Constantes y reglas puras del pairing: validar código, hash de secreto, decidir claim, sanear resumen, listar pendientes |
| Create `lib/instalacion.js` | Doc de instalación: crear, armar perfil (claves una vez), agregar paso, guardar red, elegir último binario |
| Create `lib/org-datos.js` | Validar/normalizar CUIT y cambios de org |
| Create `routes/instalacion.js` | `GET /`, `POST /progreso`, `POST /red` con auth de equipo |
| Modify `routes/devices.js` | usar `lib/pairing`, origen instalador en init/claim, `GET /pair/pendientes`, `GET /instalaciones`, `GET /:deviceId/instalacion` |
| Modify `routes/admin.js` | `cuit` en `POST /org`, nuevo `PUT /org/:slug` |
| Modify `routes/ota_publico.js` | `GET /instalador` (descarga del .exe) |
| Modify `lib/firmware.js` | producto `PilotXInstalador` |
| Modify `services/config_sistema.js` | claves `INSTALADOR_SOPORTE_PASS`, `INSTALADOR_RUSTDESK_PASS` |
| Modify `server.js` | montar `/api/devices/instalacion` y `GET /instalador` |
| Modify `routes/panel.js` + `views/pages/dispositivos.ejs` | tarjeta de aprobación, modal de vincular con org |
| Create `tests/lib/fake-db.mjs` | CouchDB falso en memoria para tests |
| Create `tests/lib/pairing.test.mjs`, `tests/lib/instalacion.test.mjs`, `tests/lib/org-datos.test.mjs` | tests |

---

### Task 0: Worktree limpio desde producción

**Files:** ninguno (preparación).

- [ ] **Step 1: Crear el worktree desde `origin/main`**

```bash
cd /g/AgroParallel/Productos/OrbitX/Software/App_PC/OrbitX-Server
git fetch origin
git worktree add ../OrbitX-Server-instalador -b feat/instalador-online origin/main
cd ../OrbitX-Server-instalador
git log --oneline -1
```

Expected: el último commit es el mismo que `git log --oneline -1 origin/main` (al 2026-10-02: `b50b3ae feat(lluvias): …`).

- [ ] **Step 2: Correr los tests existentes como línea base**

Run: `npm test`
Expected: PASS (todos los `tests/app/*.test.mjs`). Si `node_modules` no está en el worktree, correr `npm ci` antes.

**Todas las rutas de las tasks siguientes son relativas a `OrbitX-Server-instalador/`.**

---

### Task 1: `lib/pairing.js` — reglas puras del pairing

**Files:**
- Create: `lib/pairing.js`
- Create: `tests/lib/pairing.test.mjs`

**Interfaces:**
- Produces:
  - `PAIRING_TTL_MS: number` (600000), `PAIR_ALPHABET: string`
  - `validPairCode(c: any): boolean`
  - `hashSecret(s: string): string` (sha256 hex)
  - `decidirClaim({ intent, user, estabBody }): { ok: true, estab_slug } | { ok: false, status, error }`
  - `limpiarResumen(r: any): object|null`
  - `listarPendientes(map: Map, now: number): Array<{ code, device_id, hostname, version, resumen, ts, expira_en_ms }>`

- [ ] **Step 1: Escribir los tests**

`tests/lib/pairing.test.mjs`:

```js
// Reglas del pairing por código. Lo crítico: un código que viene del
// instalador (pantalla nueva, sin dueño todavía) solo lo aprueba Agro
// Parallel; el pairing desde el menú de PilotX sigue como siempre.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const p = require("../../lib/pairing.js");

const SA    = { rol_global: "superadmin", estabSlug: null };
const OWNER = { rol_global: "user", estabSlug: "campo_x" };

test("validPairCode acepta 6 chars del alfabeto y rechaza confusos", () => {
  assert.equal(p.validPairCode("ABC234"), true);
  assert.equal(p.validPairCode("abc234"), true);
  assert.equal(p.validPairCode("ABCD1O"), false);
  assert.equal(p.validPairCode("ABC23"), false);
  assert.equal(p.validPairCode(null), false);
});

test("hashSecret es sha256 hex estable", () => {
  assert.equal(p.hashSecret("x"), p.hashSecret("x"));
  assert.match(p.hashSecret("x"), /^[0-9a-f]{64}$/);
});

test("instalador: owner no puede aprobar", () => {
  const r = p.decidirClaim({ intent: { origen: "instalador" }, user: OWNER, estabBody: "campo_x" });
  assert.equal(r.ok, false);
  assert.equal(r.status, 403);
});

test("instalador: superadmin tiene que elegir org", () => {
  const r = p.decidirClaim({ intent: { origen: "instalador" }, user: SA, estabBody: "" });
  assert.deepEqual([r.ok, r.status], [false, 400]);
});

test("instalador: superadmin con org aprueba", () => {
  const r = p.decidirClaim({ intent: { origen: "instalador" }, user: SA, estabBody: "campo_x" });
  assert.deepEqual(r, { ok: true, estab_slug: "campo_x" });
});

test("sin origen: owner vincula a su org (como hoy)", () => {
  const r = p.decidirClaim({ intent: {}, user: OWNER, estabBody: "otra" });
  assert.deepEqual(r, { ok: true, estab_slug: "campo_x" });
});

test("sin origen: sin org activa es 403 (como hoy)", () => {
  const r = p.decidirClaim({ intent: {}, user: { rol_global: "user" }, estabBody: "" });
  assert.deepEqual([r.ok, r.status], [false, 403]);
});

test("limpiarResumen deja solo campos conocidos y acota tamaños", () => {
  const r = p.limpiarResumen({
    hostname: "TABLET-1", windows: "Windows 11 Pro 23H2", instalacion_anterior: true, lotes: 12,
    malicioso: "x", adaptadores: Array.from({ length: 20 }, (_, i) => ({ nombre: "eth" + i, tipo: "ethernet", ip: "192.168.5." + i, internet: false, extra: 1 })),
  });
  assert.equal(r.malicioso, undefined);
  assert.equal(r.adaptadores.length, 8);
  assert.equal(r.adaptadores[0].extra, undefined);
  assert.equal(r.lotes, 12);
  assert.equal(p.limpiarResumen(null), null);
});

test("listarPendientes muestra solo instalador, sin reclamar y vigentes", () => {
  const now = 1_000_000;
  const m = new Map([
    ["AAAAAA", { origen: "instalador", device_id: "OX-1", ts: now - 1000, claimed: false }],
    ["BBBBBB", { origen: "instalador", device_id: "OX-2", ts: now - 1000, claimed: true }],
    ["CCCCCC", { device_id: "OX-3", ts: now - 1000, claimed: false }],
    ["DDDDDD", { origen: "instalador", device_id: "OX-4", ts: now - p.PAIRING_TTL_MS - 1, claimed: false }],
  ]);
  const l = p.listarPendientes(m, now);
  assert.deepEqual(l.map(x => x.code), ["AAAAAA"]);
  assert.equal(l[0].expira_en_ms, p.PAIRING_TTL_MS - 1000);
});
```

- [ ] **Step 2: Correr y ver que falla**

Run: `node --test tests/lib/pairing.test.mjs`
Expected: FAIL con `Cannot find module '../../lib/pairing.js'`.

- [ ] **Step 3: Implementar**

`lib/pairing.js`:

```js
"use strict";
// Reglas puras del pairing por código (sin Express ni CouchDB) para poder
// testearlas. routes/devices.js mantiene el Map en memoria y las usa.
const crypto = require("crypto");

const PAIRING_TTL_MS = 10 * 60 * 1000;
// Sin I/O/0/1/L: se confunden en la fuente de la pantalla del tractor.
const PAIR_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

function validPairCode(c) {
  if (typeof c !== "string") return false;
  c = c.toUpperCase();
  if (c.length !== 6) return false;
  for (const ch of c) if (!PAIR_ALPHABET.includes(ch)) return false;
  return true;
}

function hashSecret(s) {
  return crypto.createHash("sha256").update(String(s || "")).digest("hex");
}

// Quién puede reclamar un código y a qué org va.
// - origen "instalador": solo superadmin, y la org es obligatoria.
// - sin origen (menú de PilotX): igual que siempre.
function decidirClaim({ intent, user, estabBody }) {
  const esSA = user?.rol_global === "superadmin";
  if (intent?.origen === "instalador") {
    if (!esSA) return { ok: false, status: 403, error: "Las pantallas del instalador las aprueba solo Agro Parallel." };
    if (!estabBody) return { ok: false, status: 400, error: "Elegí la organización." };
    return { ok: true, estab_slug: estabBody };
  }
  const estab_slug = esSA ? (estabBody || user?.estabSlug || null) : user?.estabSlug;
  if (!estab_slug) return { ok: false, status: 403, error: "Necesitás una org activa para vincular un tractor." };
  return { ok: true, estab_slug };
}

const str = (v, n) => (v == null ? null : String(v).slice(0, n));

// El resumen lo manda un endpoint público: lista blanca y tamaños acotados.
function limpiarResumen(r) {
  if (!r || typeof r !== "object") return null;
  const adaptadores = Array.isArray(r.adaptadores) ? r.adaptadores.slice(0, 8).map(a => ({
    nombre:   str(a?.nombre, 60),
    tipo:     str(a?.tipo, 20),
    ip:       str(a?.ip, 45),
    internet: !!a?.internet,
  })) : [];
  return {
    hostname:             str(r.hostname, 60),
    windows:              str(r.windows, 80),
    instalacion_anterior: !!r.instalacion_anterior,
    lotes:                Number.isFinite(r.lotes) ? Math.max(0, Math.min(100000, Math.trunc(r.lotes))) : 0,
    adaptadores,
  };
}

function listarPendientes(map, now) {
  const out = [];
  for (const [code, p] of map) {
    if (p.origen !== "instalador" || p.claimed) continue;
    const edad = now - p.ts;
    if (edad > PAIRING_TTL_MS) continue;
    out.push({ code, device_id: p.device_id, hostname: p.hostname || null, version: p.version || null,
               resumen: p.resumen || null, ts: p.ts, expira_en_ms: PAIRING_TTL_MS - edad });
  }
  return out.sort((a, b) => b.ts - a.ts);
}

module.exports = { PAIRING_TTL_MS, PAIR_ALPHABET, validPairCode, hashSecret, decidirClaim, limpiarResumen, listarPendientes };
```

- [ ] **Step 4: Correr y ver que pasa**

Run: `node --test tests/lib/pairing.test.mjs`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add lib/pairing.js tests/lib/pairing.test.mjs
git commit -m "feat(pairing): reglas puras del pairing con origen instalador"
```

---

### Task 2: `lib/instalacion.js` + CouchDB falso

**Files:**
- Create: `tests/lib/fake-db.mjs`
- Create: `lib/instalacion.js`
- Create: `tests/lib/instalacion.test.mjs`
- Modify: `services/config_sistema.js` (agregar claves a `KEYS` y `SECRET_KEYS`)

**Interfaces:**
- Produces:
  - `idInstalacion(deviceId: string): string` → `"instalacion_<id>"`
  - `nuevaInstalacion({ device_id, estab_slug, version, kiosko, por, now, previo? }): object` (doc listo para `insert`; si `previo` se pasa, conserva su `_rev`)
  - `armarPerfil({ gdb, cfg, deviceId, now }): Promise<Perfil>`; lanza `{ status, message }`
  - `agregarPaso(inst, { paso, msg, estado }, now): inst` (muta)
  - `guardarRed(inst, diag, now): inst` (muta; lanza `{status:413}` si el JSON > 16 KB)
  - `elegirUltimo(docs): doc|null` (mayor `ts`)
  - `Perfil = { cliente, cuit, estab_slug, version, sha256, tamano_bytes, kiosko, rustdesk: { host, key, version, sha256 } | null, soporte_pass?, rustdesk_pass? }`
    (`rustdesk` sale de `cfg` `INSTALADOR_RUSTDESK_HOST`/`INSTALADOR_RUSTDESK_KEY` y del binario más nuevo del producto OTA `RustDesk`; `null` si falta host, key o binario)
- Consumes: `cfg.get(key): Promise<string>` de `services/config_sistema.js`.

- [ ] **Step 1: CouchDB falso**

`tests/lib/fake-db.mjs`:

```js
// CouchDB mínimo en memoria con la misma forma que nano (get/insert/find)
// para testear lib/ sin servidor. get de algo inexistente rechaza como nano.
export function fakeDb(docs = []) {
  const m = new Map(docs.map(d => [d._id, { ...d, _rev: d._rev || "1-a" }]));
  let n = 1;
  return {
    _m: m,
    async get(id) {
      if (!m.has(id)) { const e = new Error("missing"); e.statusCode = 404; throw e; }
      return structuredClone(m.get(id));
    },
    async insert(doc) {
      const actual = m.get(doc._id);
      if (actual && actual._rev !== doc._rev) { const e = new Error("conflict"); e.statusCode = 409; throw e; }
      const nuevo = { ...structuredClone(doc), _rev: `${++n}-x` };
      m.set(doc._id, nuevo);
      return { ok: true, id: doc._id, rev: nuevo._rev };
    },
    async find({ selector }) {
      const ok = d => Object.entries(selector).every(([k, v]) => d[k] === v);
      return { docs: [...m.values()].filter(ok).map(d => structuredClone(d)) };
    },
  };
}
```

- [ ] **Step 2: Escribir los tests**

`tests/lib/instalacion.test.mjs`:

```js
// Perfil de instalación: lo que la pantalla baja de la nube después de que
// Agro Parallel la aprobó. Las claves de soporte/RustDesk salen UNA sola vez.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fakeDb } from "./fake-db.mjs";
const require = createRequire(import.meta.url);
const inst = require("../../lib/instalacion.js");

const cfg = (vals) => ({ async get(k) { return vals[k] || ""; } });
const base = () => fakeDb([
  { _id: "org_turpiales", tipo: "org", slug: "turpiales", nombre: "LOS PEQUEÑOS TURPIALES S.A.", cuit: "30606933122" },
  { _id: "firmware_PilotX_1.0.87", tipo: "firmware", producto: "PilotX", version: "1.0.87", hash_sha256: "ab".repeat(32), tamano_bytes: 201730160, ts: 5 },
  inst.nuevaInstalacion({ device_id: "OX-AAA", estab_slug: "turpiales", version: "1.0.87", kiosko: true, por: "usr_1", now: 1 }),
]);

test("idInstalacion", () => assert.equal(inst.idInstalacion("OX-1"), "instalacion_OX-1"));

test("primer perfil trae claves; el segundo no", async () => {
  const gdb = base();
  const c = cfg({ INSTALADOR_SOPORTE_PASS: "s3cr3t", INSTALADOR_RUSTDESK_PASS: "rd" });
  const p1 = await inst.armarPerfil({ gdb, cfg: c, deviceId: "OX-AAA", now: 10 });
  assert.equal(p1.cliente, "LOS PEQUEÑOS TURPIALES S.A.");
  assert.equal(p1.cuit, "30606933122");
  assert.equal(p1.sha256, "ab".repeat(32));
  assert.equal(p1.kiosko, true);
  assert.equal(p1.soporte_pass, "s3cr3t");
  assert.equal(p1.rustdesk_pass, "rd");
  const p2 = await inst.armarPerfil({ gdb, cfg: c, deviceId: "OX-AAA", now: 11 });
  assert.equal(p2.soporte_pass, undefined);
  assert.equal(p2.rustdesk_pass, undefined);
  assert.equal(p2.version, "1.0.87");
});

test("perfil trae RustDesk (servidor, clave y binario más nuevo) si está configurado", async () => {
  const gdb = base();
  await gdb.insert({ _id: "firmware_RustDesk_1.4.1", tipo: "firmware", producto: "RustDesk", version: "1.4.1", hash_sha256: "cd".repeat(32), ts: 9 });
  await gdb.insert({ _id: "firmware_RustDesk_1.3.0", tipo: "firmware", producto: "RustDesk", version: "1.3.0", hash_sha256: "ef".repeat(32), ts: 2 });
  const c = cfg({ INSTALADOR_SOPORTE_PASS: "s", INSTALADOR_RUSTDESK_HOST: "asistx.agroparallel.com", INSTALADOR_RUSTDESK_KEY: "k=" });
  const p = await inst.armarPerfil({ gdb, cfg: c, deviceId: "OX-AAA", now: 10 });
  assert.deepEqual(p.rustdesk, { host: "asistx.agroparallel.com", key: "k=", version: "1.4.1", sha256: "cd".repeat(32) });
});

test("perfil sin RustDesk configurado trae rustdesk null", async () => {
  const p = await inst.armarPerfil({ gdb: base(), cfg: cfg({ INSTALADOR_SOPORTE_PASS: "s" }), deviceId: "OX-AAA", now: 10 });
  assert.equal(p.rustdesk, null);
});

test("rustdesk_pass cae a soporte_pass si no está configurada", async () => {
  const p = await inst.armarPerfil({ gdb: base(), cfg: cfg({ INSTALADOR_SOPORTE_PASS: "s" }), deviceId: "OX-AAA", now: 10 });
  assert.equal(p.rustdesk_pass, "s");
});

test("sin clave de soporte configurada es 503 y no marca entregadas", async () => {
  const gdb = base();
  await assert.rejects(inst.armarPerfil({ gdb, cfg: cfg({}), deviceId: "OX-AAA", now: 10 }), e => e.status === 503);
  const doc = await gdb.get("instalacion_OX-AAA");
  assert.equal(doc.claves_entregadas, false);
});

test("sin instalación aprobada es 404", async () => {
  await assert.rejects(inst.armarPerfil({ gdb: base(), cfg: cfg({ INSTALADOR_SOPORTE_PASS: "s" }), deviceId: "OX-ZZZ", now: 10 }), e => e.status === 404);
});

test("versión sin binario en OTA es 409", async () => {
  const gdb = base();
  await gdb.insert(inst.nuevaInstalacion({ device_id: "OX-BBB", estab_slug: "turpiales", version: "9.9.9", kiosko: true, por: "u", now: 1 }));
  await assert.rejects(inst.armarPerfil({ gdb, cfg: cfg({ INSTALADOR_SOPORTE_PASS: "s" }), deviceId: "OX-BBB", now: 10 }), e => e.status === 409);
});

test("nuevaInstalacion sobre una previa conserva _rev y resetea claves", () => {
  const previo = { _id: "instalacion_OX-1", _rev: "7-z", claves_entregadas: true, pasos: [{ t: 1 }] };
  const d = inst.nuevaInstalacion({ device_id: "OX-1", estab_slug: "x", version: "1.0.87", kiosko: false, por: "u", now: 2, previo });
  assert.equal(d._rev, "7-z");
  assert.equal(d.claves_entregadas, false);
  assert.deepEqual(d.pasos, []);
  assert.equal(d.estado, "aprobada");
});

test("agregarPaso acota a 80 y actualiza estado", () => {
  const d = inst.nuevaInstalacion({ device_id: "OX-1", estab_slug: "x", version: "1", kiosko: true, por: "u", now: 1 });
  for (let i = 0; i < 90; i++) inst.agregarPaso(d, { paso: "red", msg: "m" + i }, 100 + i);
  inst.agregarPaso(d, { paso: "kiosko", msg: "listo", estado: "instalado" }, 500);
  assert.equal(d.pasos.length, 80);
  assert.equal(d.pasos.at(-1).msg, "listo");
  assert.equal(d.estado, "instalado");
  assert.equal(d.updated_at, 500);
});

test("agregarPaso ignora estados desconocidos", () => {
  const d = inst.nuevaInstalacion({ device_id: "OX-1", estab_slug: "x", version: "1", kiosko: true, por: "u", now: 1 });
  inst.agregarPaso(d, { paso: "x", msg: "m", estado: "hackeado" }, 2);
  assert.equal(d.estado, "aprobada");
});

test("guardarRed guarda y rechaza > 16 KB", () => {
  const d = inst.nuevaInstalacion({ device_id: "OX-1", estab_slug: "x", version: "1", kiosko: true, por: "u", now: 1 });
  inst.guardarRed(d, { antes: { ok: false }, despues: { ok: true } }, 9);
  assert.deepEqual(d.red, { ts: 9, diag: { antes: { ok: false }, despues: { ok: true } } });
  assert.throws(() => inst.guardarRed(d, { x: "a".repeat(17000) }, 10), e => e.status === 413);
});

test("elegirUltimo toma el de mayor ts", () => {
  assert.equal(inst.elegirUltimo([{ version: "a", ts: 1 }, { version: "b", ts: 3 }, { version: "c", ts: 2 }]).version, "b");
  assert.equal(inst.elegirUltimo([]), null);
});
```

- [ ] **Step 3: Correr y ver que falla**

Run: `node --test tests/lib/instalacion.test.mjs`
Expected: FAIL con `Cannot find module '../../lib/instalacion.js'`.

- [ ] **Step 4: Implementar `lib/instalacion.js`**

```js
"use strict";
// Doc instalacion_<device_id> (orbitx_global): qué aprobó Agro Parallel para
// una pantalla (org, versión, kiosko), el avance que reporta el instalador y
// el último diagnóstico de red. Funciones puras + gdb inyectado (testeable).

const ESTADOS = ["aprobada", "instalando", "instalado", "fallo"];
const MAX_PASOS = 80;
const MAX_RED_BYTES = 16 * 1024;

const idInstalacion = (deviceId) => `instalacion_${deviceId}`;

function nuevaInstalacion({ device_id, estab_slug, version, kiosko, por, now, previo }) {
  return {
    _id: idInstalacion(device_id),
    ...(previo?._rev ? { _rev: previo._rev } : {}),
    tipo: "instalacion",
    device_id, estab_slug, version, kiosko: !!kiosko,
    estado: "aprobada",
    claves_entregadas: false, claves_entregadas_at: null,
    pasos: [], red: null,
    aprobado_por: por, created_at: now, updated_at: now,
  };
}

// Servidor/clave pública de RustDesk (no son secretos) + el binario más nuevo
// subido al OTA como producto "RustDesk". null si falta algo: el instalador
// sigue sin soporte remoto y lo anota.
async function perfilRustdesk(gdb, cfg) {
  const host = await cfg.get("INSTALADOR_RUSTDESK_HOST");
  const key  = await cfg.get("INSTALADOR_RUSTDESK_KEY");
  if (!host || !key) return null;
  const r = await gdb.find({ selector: { tipo: "firmware", producto: "RustDesk" } }).catch(() => ({ docs: [] }));
  const ult = elegirUltimo(r.docs || []);
  if (!ult) return null;
  return { host, key, version: ult.version, sha256: ult.hash_sha256 };
}

async function armarPerfil({ gdb, cfg, deviceId, now }) {
  const inst = await gdb.get(idInstalacion(deviceId)).catch(() => null);
  if (!inst) throw { status: 404, message: "Esta pantalla no tiene una instalación aprobada" };
  const fw = await gdb.get(`firmware_PilotX_${inst.version}`).catch(() => null);
  if (!fw) throw { status: 409, message: `PilotX ${inst.version} no está en el OTA` };
  const org = await gdb.get(`org_${inst.estab_slug}`).catch(() => null);

  const perfil = {
    cliente: org?.nombre || inst.estab_slug,
    cuit: org?.cuit || "",
    estab_slug: inst.estab_slug,
    version: inst.version,
    sha256: fw.hash_sha256,
    tamano_bytes: fw.tamano_bytes,
    kiosko: !!inst.kiosko,
    rustdesk: await perfilRustdesk(gdb, cfg),
  };

  if (!inst.claves_entregadas) {
    const soporte = await cfg.get("INSTALADOR_SOPORTE_PASS");
    if (!soporte) throw { status: 503, message: "Falta INSTALADOR_SOPORTE_PASS en Configuración del sistema" };
    perfil.soporte_pass = soporte;
    perfil.rustdesk_pass = (await cfg.get("INSTALADOR_RUSTDESK_PASS")) || soporte;
    // Se marca ANTES de responder: si la respuesta se pierde, la pantalla
    // reintenta sin claves y el técnico las regenera re-aprobando.
    inst.claves_entregadas = true;
    inst.claves_entregadas_at = now;
    inst.updated_at = now;
    await gdb.insert(inst);
  }
  return perfil;
}

function agregarPaso(inst, { paso, msg, estado }, now) {
  inst.pasos = inst.pasos || [];
  inst.pasos.push({ t: now, paso: String(paso || "").slice(0, 40), msg: String(msg || "").slice(0, 300) });
  if (inst.pasos.length > MAX_PASOS) inst.pasos.splice(0, inst.pasos.length - MAX_PASOS);
  if (ESTADOS.includes(estado)) inst.estado = estado;
  inst.updated_at = now;
  return inst;
}

function guardarRed(inst, diag, now) {
  if (Buffer.byteLength(JSON.stringify(diag ?? null)) > MAX_RED_BYTES)
    throw { status: 413, message: "Diagnóstico de red demasiado grande" };
  inst.red = { ts: now, diag };
  inst.updated_at = now;
  return inst;
}

function elegirUltimo(docs) {
  if (!Array.isArray(docs) || !docs.length) return null;
  return [...docs].sort((a, b) => (b.ts || 0) - (a.ts || 0))[0];
}

module.exports = { ESTADOS, idInstalacion, nuevaInstalacion, armarPerfil, agregarPaso, guardarRed, elegirUltimo };
```

- [ ] **Step 5: Registrar las claves en la configuración del sistema**

En `services/config_sistema.js`, agregar al final del array `KEYS` (L15-41):

```js
  "INSTALADOR_SOPORTE_PASS",
  "INSTALADOR_RUSTDESK_PASS",
  "INSTALADOR_RUSTDESK_HOST",
  "INSTALADOR_RUSTDESK_KEY",
```

y al final del array `SECRET_KEYS` (L43-51):

```js
  "INSTALADOR_SOPORTE_PASS",
  "INSTALADOR_RUSTDESK_PASS",
```

- [ ] **Step 6: Correr y ver que pasa**

Run: `node --test tests/lib/instalacion.test.mjs`
Expected: PASS (13 tests). Luego `npm test` → todo PASS.

- [ ] **Step 7: Commit**

```bash
git add lib/instalacion.js tests/lib/fake-db.mjs tests/lib/instalacion.test.mjs services/config_sistema.js
git commit -m "feat(instalacion): perfil de instalación con claves de una sola entrega"
```

---

### Task 3: `lib/org-datos.js` + CUIT y edición de org

**Files:**
- Create: `lib/org-datos.js`
- Create: `tests/lib/org-datos.test.mjs`
- Modify: `routes/admin.js` (`POST /org` L39-70; nuevo `PUT /org/:slug` a continuación)

**Interfaces:**
- Produces:
  - `normalizarCuit(v: any): string` → 11 dígitos o `""`; lanza `{status:400}` si no es válido
  - `validarCambiosOrg(body: any): { nombre?: string, cuit?: string }`; lanza `{status:400}`

- [ ] **Step 1: Escribir los tests**

`tests/lib/org-datos.test.mjs`:

```js
// Datos editables de una org. Nació de "PEQUE�OS TURPIALES": la org se creó
// con la Ñ rota y no había forma de corregirla sin tocar CouchDB a mano.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const o = require("../../lib/org-datos.js");

test("normalizarCuit acepta guiones y espacios", () => {
  assert.equal(o.normalizarCuit("30-60693312-2"), "30606933122");
  assert.equal(o.normalizarCuit(" 20233224759 "), "20233224759");
  assert.equal(o.normalizarCuit(""), "");
  assert.equal(o.normalizarCuit(undefined), "");
});

test("normalizarCuit rechaza largo inválido", () => {
  assert.throws(() => o.normalizarCuit("123"), e => e.status === 400);
});

test("validarCambiosOrg toma nombre y cuit", () => {
  assert.deepEqual(o.validarCambiosOrg({ nombre: "  LOS PEQUEÑOS TURPIALES S.A. ", cuit: "30606933122", slug: "x" }),
    { nombre: "LOS PEQUEÑOS TURPIALES S.A.", cuit: "30606933122" });
});

test("validarCambiosOrg rechaza el carácter de reemplazo U+FFFD", () => {
  assert.throws(() => o.validarCambiosOrg({ nombre: "PEQUE\uFFFDOS" }), e => e.status === 400);
});

test("validarCambiosOrg sin cambios es 400", () => {
  assert.throws(() => o.validarCambiosOrg({}), e => e.status === 400);
  assert.throws(() => o.validarCambiosOrg({ nombre: "   " }), e => e.status === 400);
});
```

- [ ] **Step 2: Correr y ver que falla**

Run: `node --test tests/lib/org-datos.test.mjs`
Expected: FAIL con `Cannot find module`.

- [ ] **Step 3: Implementar `lib/org-datos.js`**

```js
"use strict";
// Validación de los datos editables de una org (nombre, CUIT).

function normalizarCuit(v) {
  const s = String(v ?? "").replace(/[\s-]/g, "");
  if (!s) return "";
  if (!/^\d{11}$/.test(s)) throw { status: 400, message: "CUIT inválido: tienen que ser 11 dígitos" };
  return s;
}

function validarCambiosOrg(body) {
  const cambios = {};
  if (body && body.nombre !== undefined) {
    const nombre = String(body.nombre).trim();
    if (!nombre) throw { status: 400, message: "El nombre no puede quedar vacío" };
    // U+FFFD = un acento que llegó roto (encoding de la consola). Mejor frenar.
    if (nombre.includes("\uFFFD")) throw { status: 400, message: "El nombre tiene un carácter roto (�): revisá los acentos" };
    cambios.nombre = nombre.slice(0, 160);
  }
  if (body && body.cuit !== undefined) cambios.cuit = normalizarCuit(body.cuit);
  if (!Object.keys(cambios).length) throw { status: 400, message: "No hay nada para cambiar" };
  return cambios;
}

module.exports = { normalizarCuit, validarCambiosOrg };
```

- [ ] **Step 4: Correr y ver que pasa**

Run: `node --test tests/lib/org-datos.test.mjs`
Expected: PASS (5 tests).

- [ ] **Step 5: Usar en `routes/admin.js`**

Arriba del archivo, junto a los otros `require`:

```js
const { normalizarCuit, validarCambiosOrg } = require("../lib/org-datos");
```

En `POST /org`, reemplazar la línea `const { nombre, slug, provincia, ciudad } = req.body || {};` por:

```js
    const { nombre, slug, provincia, ciudad } = req.body || {};
    let cuit = "";
    try { cuit = normalizarCuit(req.body?.cuit); }
    catch (e) { return res.status(e.status).json({ error: e.message }); }
    if (String(nombre || "").includes("\uFFFD"))
      return res.status(400).json({ error: "El nombre tiene un carácter roto (�): revisá los acentos" });
```

y en el `gdb.insert({...})` agregar el campo `cuit,` después de `nombre, slug,`.

Después del cierre de `router.post("/org", …)`, agregar:

```js
// PUT /api/admin/org/:slug — corregir nombre y/o CUIT de una org (superadmin).
router.put("/org/:slug", soloSuperadmin, async (req, res) => {
  try {
    const cambios = validarCambiosOrg(req.body || {});
    const gdb = db.getDB("global");
    const org = await gdb.get(`org_${req.params.slug}`).catch(() => null);
    if (!org) return res.status(404).json({ error: "No existe la organización" });
    Object.assign(org, cambios, { updated_at: Date.now() });
    await gdb.insert(org);
    console.log(`[Admin] Org ${req.params.slug} actualizada: ${Object.keys(cambios).join(", ")}`);
    res.json({ ok: true, slug: org.slug, nombre: org.nombre, cuit: org.cuit || "" });
  } catch (e) {
    res.status(e.status || 500).json({ error: e.message });
  }
});
```

- [ ] **Step 6: Verificar que carga**

Run: `node -e "require('./routes/admin.js'); console.log('ok')"`
Expected: `ok`. Luego `npm test` → PASS.

- [ ] **Step 7: Commit**

```bash
git add lib/org-datos.js tests/lib/org-datos.test.mjs routes/admin.js
git commit -m "feat(admin): CUIT en orgs y PUT /org/:slug para corregir nombre y CUIT"
```

---

### Task 4: Pairing de origen instalador en `routes/devices.js`

**Files:**
- Modify: `routes/devices.js` (L33-57 helpers, L477-508 init, L515-600 claim; rutas nuevas)

**Interfaces:**
- Consumes: `lib/pairing.js` (Task 1), `lib/instalacion.js` → `nuevaInstalacion`, `idInstalacion` (Task 2), `soloSuperadmin` de `middleware/auth.js`.
- Produces (HTTP):
  - `POST /api/devices/pair/init` acepta además `origen` (`"instalador"`) y `resumen`.
  - `POST /api/devices/pair/claim` acepta además `version`, `kiosko`; responde igual que hoy.
  - `GET /api/devices/pair/pendientes` (superadmin) → `{ pendientes: [...] }` (forma de `listarPendientes`).
  - `GET /api/devices/instalaciones` (superadmin) → `{ instalaciones: [{ device_id, estab_slug, version, estado, updated_at, ultimo_paso }] }` (20 más recientes).
  - `GET /api/devices/:deviceId/instalacion` (JWT, `puedeTocarDevice`) → doc sin `_rev`.

- [ ] **Step 1: Reemplazar los helpers por `lib/pairing`**

Borrar las definiciones locales de `PAIRING_TTL_MS`, `PAIR_ALPHABET`, `_validPairCode` y `_hashSecret` (L33, L44, L50-57) y poner en su lugar:

```js
const { PAIRING_TTL_MS, validPairCode: _validPairCode, hashSecret: _hashSecret,
        decidirClaim, limpiarResumen, listarPendientes } = require("../lib/pairing");
const { nuevaInstalacion, idInstalacion } = require("../lib/instalacion");
const { soloSuperadmin } = require("../middleware/auth");
```

(Se mantienen `pendingPairings` y el `setInterval` de limpieza como están; el comentario del Map suma `origen, resumen`.)

- [ ] **Step 2: `pair/init` guarda origen y resumen**

En el handler de `POST /pair/init`, cambiar la desestructuración:

```js
  const { code, device_secret, device_id, hostname, version, origen, resumen } = req.body || {};
```

y en el objeto de `pendingPairings.set(codeU, { … })` agregar:

```js
    origen:  origen === "instalador" ? "instalador" : (existing?.origen || null),
    resumen: limpiarResumen(resumen) || existing?.resumen || null,
```

- [ ] **Step 3: `pair/claim` usa `decidirClaim` y crea la instalación**

En `POST /pair/claim`, cambiar la desestructuración a:

```js
  const { code, nombre, estab_slug: estabBody, version, kiosko } = req.body || {};
```

Reemplazar el bloque L528-532 (`const esSA … if (!estab_slug) return …`) por:

```js
  const esSA   = req.user?.rol_global === "superadmin";
  const miSlug = req.user?.estabSlug;
  const dec = decidirClaim({ intent: p, user: req.user, estabBody });
  if (!dec.ok) return res.status(dec.status).json({ error: dec.error });
  const estab_slug = dec.estab_slug;
  const esInstalador = p.origen === "instalador";
  if (esInstalador) {
    const gdb0 = req.app.locals.globalDB;
    const org = await gdb0.get(`org_${estab_slug}`).catch(() => null);
    if (!org) return res.status(400).json({ error: "No existe esa organización" });
    if (!version) return res.status(400).json({ error: "Elegí la versión de PilotX" });
    const fwDoc = await gdb0.get(`firmware_PilotX_${version}`).catch(() => null);
    if (!fwDoc) return res.status(400).json({ error: `PilotX ${version} no está en el OTA` });
  }
```

Justo después de que se inserta/actualiza el doc del device (antes de `p.claimed = true;`), agregar:

```js
    if (esInstalador) {
      const previo = await globalDB.get(idInstalacion(p.device_id)).catch(() => null);
      await globalDB.insert(nuevaInstalacion({
        device_id: p.device_id, estab_slug, version, kiosko: kiosko !== false,
        por: uid(req), now: Date.now(), previo,
      }));
    }
```

y en el `registrarAudit(...)` sumar `origen: p.origen || "pilotx"` al detalle.

- [ ] **Step 4: Rutas de consulta para el panel**

Antes de `module.exports` agregar:

```js
// Pantallas del instalador esperando que Agro Parallel las apruebe.
router.get("/pair/pendientes", noDevices, soloSuperadmin, (req, res) => {
  res.json({ pendientes: listarPendientes(pendingPairings, Date.now()) });
});

// Últimas instalaciones (aprobadas, en curso o terminadas).
router.get("/instalaciones", noDevices, soloSuperadmin, async (req, res) => {
  try {
    const r = await req.app.locals.globalDB.find({ selector: { tipo: "instalacion" }, limit: 200 });
    const lista = (r.docs || [])
      .sort((a, b) => (b.updated_at || 0) - (a.updated_at || 0))
      .slice(0, 20)
      .map(d => ({ device_id: d.device_id, estab_slug: d.estab_slug, version: d.version, estado: d.estado,
                   updated_at: d.updated_at, ultimo_paso: (d.pasos || []).at(-1) || null }));
    res.json({ instalaciones: lista });
  } catch (e) { res.status(500).json({ error: e.message }); }
});

router.get("/:deviceId/instalacion", noDevices, async (req, res) => {
  try {
    const gdb = req.app.locals.globalDB;
    const dev = await gdb.get(`device_${req.params.deviceId}`).catch(() => null);
    if (!puedeTocarDevice(req, dev)) return res.status(404).json({ error: "No existe" });
    const d = await gdb.get(idInstalacion(req.params.deviceId)).catch(() => null);
    if (!d) return res.status(404).json({ error: "Sin instalación registrada" });
    const { _rev, ...limpio } = d;
    res.json(limpio);
  } catch (e) { res.status(500).json({ error: e.message }); }
});
```

- [ ] **Step 5: Verificar que carga y los tests siguen pasando**

Run: `node -e "require('./routes/devices.js'); console.log('ok')"` → `ok`
Run: `npm test` → PASS.

- [ ] **Step 6: Commit**

```bash
git add routes/devices.js
git commit -m "feat(devices): códigos del instalador solo los aprueba superadmin y crean la instalación"
```

---

### Task 5: `routes/instalacion.js` (auth de equipo) + montaje

**Files:**
- Create: `routes/instalacion.js`
- Modify: `server.js` (antes del `app.use("/api/devices", …)` de L301)

**Interfaces:**
- Consumes: `armarPerfil`, `agregarPaso`, `guardarRed`, `idInstalacion` (Task 2); `deviceAuth` (exportado por `routes/devices.js`); `services/config_sistema.js`.
- Produces (HTTP, headers `X-Device-ID` + `X-Auth-Token`):
  - `GET /api/devices/instalacion` → `Perfil`
  - `POST /api/devices/instalacion/progreso` body `{ paso, msg, estado? }` → `{ ok: true }`
  - `POST /api/devices/instalacion/red` body `{ antes, despues, reparado }` → `{ ok: true }`

- [ ] **Step 1: Crear la ruta**

`routes/instalacion.js`:

```js
"use strict";
// Lo que llama PilotX-Instalador.exe con su token de equipo, después de que
// Agro Parallel aprobó la pantalla: perfil, avance y diagnóstico de red.
const router = require("express").Router();
const cfg  = require("../services/config_sistema");
const inst = require("../lib/instalacion");

const fallo = (res, e) => res.status(e.status || 500).json({ error: e.message || String(e) });

router.get("/", async (req, res) => {
  try {
    res.json(await inst.armarPerfil({ gdb: req.app.locals.globalDB, cfg, deviceId: req.deviceId, now: Date.now() }));
  } catch (e) { fallo(res, e); }
});

async function actualizar(req, fn) {
  const gdb = req.app.locals.globalDB;
  const d = await gdb.get(inst.idInstalacion(req.deviceId)).catch(() => null);
  if (!d) throw { status: 404, message: "Esta pantalla no tiene una instalación aprobada" };
  fn(d);
  await gdb.insert(d);
}

router.post("/progreso", async (req, res) => {
  try {
    await actualizar(req, d => inst.agregarPaso(d, req.body || {}, Date.now()));
    res.json({ ok: true });
  } catch (e) { fallo(res, e); }
});

router.post("/red", async (req, res) => {
  try {
    await actualizar(req, d => inst.guardarRed(d, req.body || null, Date.now()));
    res.json({ ok: true });
  } catch (e) { fallo(res, e); }
});

module.exports = router;
```

- [ ] **Step 2: Montar en `server.js`**

Junto a L24, cambiar el import a:

```js
const { router: routeDevices, deviceAuth } = require("./routes/devices");
```

e inmediatamente **antes** de `app.use("/api/devices", (req, res, next) => {` (L301) agregar:

```js
// Instalador online: endpoints con token de equipo (no JWT). Va antes del
// montaje general de /api/devices para que auth.required no lo intercepte.
app.use("/api/devices/instalacion", deviceAuth, require("./routes/instalacion"));
```

- [ ] **Step 3: Verificar arranque local**

Run: `node -e "require('./routes/instalacion.js'); console.log('ok')"` → `ok`
Run: `node --check server.js` → sin salida (OK).

- [ ] **Step 4: Commit**

```bash
git add routes/instalacion.js server.js
git commit -m "feat(instalacion): perfil, progreso y red con auth de equipo"
```

---

### Task 6: Descarga pública del instalador

**Files:**
- Modify: `lib/firmware.js` (`PRODUCTOS`, L10-35)
- Modify: `routes/ota_publico.js` (nueva ruta antes de `module.exports`, L137)
- Modify: `server.js` (junto a L240, el redirect de `/releases`)

**Interfaces:**
- Consumes: `elegirUltimo` (Task 2), `fw.existeBin`, `fw.rutaBin`, `limiteDescargas`, `ipCliente` (ya en `ota_publico.js`).
- Produces: `GET /instalador` → descarga `PilotX-Instalador.exe` (última versión subida de `PilotXInstalador`); 404 si no hay.

- [ ] **Step 1: Producto nuevo**

En `lib/firmware.js`, agregar `"PilotXInstalador"` y `"RustDesk"` al array `PRODUCTOS` (después de `"PilotX"`). El instalador baja RustDesk con su token por `GET /api/ota/firmware/RustDesk/<version>` (ruta ya existente). Así se sube por `/firmwares` como cualquier binario (el `.exe` se sube con su nombre; se guarda como `<version>.bin`).

- [ ] **Step 2: Ruta en `ota_publico.js`**

Arriba: `const { elegirUltimo } = require("../lib/instalacion");`. Antes de `module.exports = router;`:

```js
// El instalador de PilotX es público a propósito: no lleva secretos y sin la
// aprobación de Agro Parallel en OrbitX no sirve para nada.
router.get("/instalador", async (req, res) => {
  try {
    if (!limiteDescargas.permitir(ipCliente(req)))
      return res.status(429).json({ error: "Demasiadas descargas, probá en un rato" });
    const r = await couch.getDB("global").find({
      selector: { tipo: "firmware", producto: "PilotXInstalador" },
      fields: ["version", "ts", "created_at"], limit: 50,
    });
    const docs = (r.docs || []).map(d => ({ ...d, ts: d.ts || d.created_at || 0 }))
      .filter(d => fw.existeBin("PilotXInstalador", d.version));
    const ult = elegirUltimo(docs);
    if (!ult) return res.status(404).json({ error: "Todavía no hay instalador publicado" });
    res.download(fw.rutaBin("PilotXInstalador", ult.version), "PilotX-Instalador.exe");
  } catch (e) {
    res.status(500).json({ error: "Error interno" });
  }
});
```

Verificar el nombre de la variable del cliente CouchDB ya importado en `ota_publico.js` (lo usa `cargarCatalogo()`); si se llama distinto de `couch`, usar ese nombre.

- [ ] **Step 3: Atajo `/instalador`**

En `server.js`, junto al redirect de `/releases` (L240):

```js
app.get("/instalador", (req, res) => res.redirect("/api/ota/publico/instalador"));
```

- [ ] **Step 4: Verificar**

Run: `node -e "require('./routes/ota_publico.js'); console.log('ok')"` → `ok`; `node --check server.js`; `npm test` → PASS.

- [ ] **Step 5: Commit**

```bash
git add lib/firmware.js routes/ota_publico.js server.js
git commit -m "feat(ota): descarga pública de PilotX-Instalador.exe en /instalador"
```

---

### Task 7: Panel — aprobación de pantallas y vincular con org

**Files:**
- Modify: `routes/panel.js` (handler `GET /dispositivos`, L396-449)
- Modify: `views/pages/dispositivos.ejs` (botón L10, modal `#modal-pair` L343-375, script L377-404)

**Interfaces:**
- Consumes (HTTP): `GET /api/devices/pair/pendientes`, `GET /api/devices/instalaciones`, `POST /api/devices/pair/claim` (Task 4); `Auth.get`/`Auth.post` de `public/js/auth.js` (verificar que `Auth.get` exista; si no, usar `fetch` con el mismo header que arma `Auth.post`).
- Produces: variable EJS `versionesPilotX: string[]` (más nueva primero).

- [ ] **Step 1: Versiones de PilotX para el formulario**

En `routes/panel.js`, dentro del handler `/dispositivos`, antes del `res.render`:

```js
    const rFw = await db.find({ selector: { tipo: "firmware", producto: "PilotX" }, fields: ["version", "ts", "created_at"], limit: 50 })
      .catch(() => ({ docs: [] }));
    const versionesPilotX = (rFw.docs || [])
      .sort((a, b) => (b.ts || b.created_at || 0) - (a.ts || a.created_at || 0))
      .map(d => d.version);
```

y sumar `versionesPilotX` al objeto que se pasa a `res.render("layout", { … })`.

- [ ] **Step 2: Tarjeta "Pantallas esperando aprobación" (solo SA)**

En `views/pages/dispositivos.ejs`, después de la barra de botones de arriba (L10), agregar:

```ejs
<% if (isSA) { %>
<div class="card" id="card-aprobar" style="margin:16px 0">
  <h3>Pantallas esperando aprobación</h3>
  <p class="muted">Pantallas que corrieron PilotX-Instalador.exe. Aprobá solo las que estás instalando vos.</p>
  <table class="table"><thead><tr><th>Código</th><th>Equipo</th><th>Instalación anterior</th><th>Red</th><th>Vence</th><th></th></tr></thead>
    <tbody id="tb-pendientes"><tr><td colspan="6" class="muted">Buscando…</td></tr></tbody></table>
  <h4 style="margin-top:16px">Instalaciones recientes</h4>
  <table class="table"><thead><tr><th>Equipo</th><th>Org</th><th>Versión</th><th>Estado</th><th>Último paso</th></tr></thead>
    <tbody id="tb-instalaciones"></tbody></table>
</div>

<div class="modal" id="modal-aprobar">
  <div class="modal-box">
    <h3>Aprobar pantalla <span id="ap-code"></span></h3>
    <label>Organización
      <select id="ap-org"><% establecimientos.forEach(e => { %><option value="<%= e.slug %>"><%= e.nombre %></option><% }) %></select>
    </label>
    <label>Versión de PilotX
      <select id="ap-version"><% versionesPilotX.forEach(v => { %><option value="<%= v %>"><%= v %></option><% }) %></select>
    </label>
    <label>Nombre del equipo <input id="ap-nombre" maxlength="80" placeholder="Pantalla PilotX - Cliente"></label>
    <label><input type="checkbox" id="ap-kiosko" checked> Modo kiosko</label>
    <div id="ap-alert"></div>
    <button class="btn btn-primary" onclick="confirmarAprobar()">Aprobar</button>
    <button class="btn" onclick="closeModal('modal-aprobar')">Cancelar</button>
  </div>
</div>
<% } %>
```

- [ ] **Step 3: Org en el modal "Vincular por código" para superadmin**

Dentro de `#modal-pair`, debajo de `#pair-nombre`, agregar:

```ejs
<% if (isSA) { %>
<label>Organización
  <select id="pair-org"><% establecimientos.forEach(e => { %><option value="<%= e.slug %>"><%= e.nombre %></option><% }) %></select>
</label>
<% } %>
```

y en `confirmarPair()` reemplazar la línea del `Auth.post` por:

```js
  const orgSel = document.getElementById('pair-org');
  const d = await Auth.post('/api/devices/pair/claim', { code, nombre: nombre || undefined, estab_slug: orgSel ? orgSel.value : undefined });
```

- [ ] **Step 4: Script de la tarjeta**

Al final del `<script>` de la página (después de `confirmarPair`):

```js
<% if (isSA) { %>
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
let apCode = null;
async function cargarPendientes() {
  try {
    const [p, i] = await Promise.all([Auth.get('/api/devices/pair/pendientes'), Auth.get('/api/devices/instalaciones')]);
    const tb = document.getElementById('tb-pendientes');
    tb.innerHTML = p.pendientes.length ? p.pendientes.map(x => {
      const r = x.resumen || {};
      const ant = r.instalacion_anterior ? `Sí (${r.lotes || 0} lotes)` : 'No';
      const red = (r.adaptadores || []).map(a => `${esc(a.tipo)} ${esc(a.ip)}${a.internet ? ' ✓' : ''}`).join('<br>');
      return `<tr><td><b>${esc(x.code)}</b></td><td>${esc(r.hostname || x.hostname)}<br><small>${esc(r.windows)}</small></td>
        <td>${ant}</td><td><small>${red}</small></td><td>${Math.ceil(x.expira_en_ms / 60000)} min</td>
        <td><button class="btn btn-primary" onclick="abrirAprobar('${esc(x.code)}','${esc(r.hostname || '')}')">Aprobar</button></td></tr>`;
    }).join('') : '<tr><td colspan="6" class="muted">No hay pantallas esperando.</td></tr>';
    document.getElementById('tb-instalaciones').innerHTML = i.instalaciones.map(x =>
      `<tr><td>${esc(x.device_id)}</td><td>${esc(x.estab_slug)}</td><td>${esc(x.version)}</td><td>${esc(x.estado)}</td>
       <td><small>${esc(x.ultimo_paso ? x.ultimo_paso.msg : '')}</small></td></tr>`).join('');
  } catch (e) { console.warn('[aprobar]', e.message); }
}
function abrirAprobar(code, hostname) {
  apCode = code;
  document.getElementById('ap-code').textContent = code;
  document.getElementById('ap-nombre').value = hostname ? 'Pantalla PilotX - ' + hostname : '';
  openModal('modal-aprobar');
}
async function confirmarAprobar() {
  try {
    const d = await Auth.post('/api/devices/pair/claim', {
      code: apCode,
      estab_slug: document.getElementById('ap-org').value,
      version: document.getElementById('ap-version').value,
      kiosko: document.getElementById('ap-kiosko').checked,
      nombre: document.getElementById('ap-nombre').value.trim() || undefined,
    });
    toast('✓ Pantalla aprobada', d.device_id + ' → ' + d.estab_slug, 'lime');
    closeModal('modal-aprobar');
    cargarPendientes();
  } catch (e) { showAlert('ap-alert', e.message || 'Error aprobando'); }
}
cargarPendientes();
setInterval(cargarPendientes, 5000);
<% } %>
```

- [ ] **Step 5: Verificar que la vista compila**

Run:

```bash
node -e "const ejs=require('ejs');ejs.renderFile('views/pages/dispositivos.ejs',{isSA:true,isAdmin:true,dispositivos:[],establecimientos:[{slug:'a',nombre:'Á'}],versionesPilotX:['1.0.87'],ultimasFw:{},backups:[],fmtDate:x=>x,user:{},rol:'superadmin',locals:{}},(e,h)=>{if(e){console.error(e.message);process.exit(1)}console.log('ok',h.includes('card-aprobar'))})"
```

Expected: `ok true`. Si la vista usa otras variables y tira `X is not defined`, agregarlas al objeto con un valor vacío y repetir.

- [ ] **Step 6: Commit**

```bash
git add routes/panel.js views/pages/dispositivos.ejs
git commit -m "feat(panel): aprobar pantallas del instalador y elegir org al vincular"
```

---

### Task 8: Prueba integrada local contra CouchDB de desarrollo

**Files:** ninguno (verificación manual con `curl`).

- [ ] **Step 1: Levantar OrbitX local** (con el `.env` de desarrollo del worktree; nunca el de producción)

Run: `node server.js` (otra terminal). Expected: log de arranque sin errores.

- [ ] **Step 2: Simular el instalador**

```bash
B=http://localhost:4000
curl -s -X POST $B/api/devices/pair/init -H 'Content-Type: application/json' \
  -d '{"code":"TEST23","device_secret":"0123456789abcdef0123456789abcdef","device_id":"OX-TEST00000001","hostname":"TABLET-TEST","origen":"instalador","resumen":{"hostname":"TABLET-TEST","instalacion_anterior":true,"lotes":3}}'
```

Expected: `{"ok":true,"expires_in":600000}`.

- [ ] **Step 3: Owner no puede aprobar; superadmin sí**

Con JWT de un owner: `POST /api/devices/pair/claim {"code":"TEST23","estab_slug":"<org>","version":"<ver en OTA>"}` → `403`.
Con JWT de superadmin: mismo body → `{"ok":true,"device_id":"OX-TEST00000001",...}`.

- [ ] **Step 4: Token y perfil**

```bash
curl -s "$B/api/devices/pair/status/TEST23?secret=0123456789abcdef0123456789abcdef"
```

Expected: `{"status":"claimed","token":"…",…}`. Con ese token:

```bash
curl -s $B/api/devices/instalacion -H "X-Device-ID: OX-TEST00000001" -H "X-Auth-Token: <token>"
```

Expected: primera vez con `soporte_pass` (o `503` si no se cargó `INSTALADOR_SOPORTE_PASS`: cargarla en Configuración del sistema y repetir); segunda vez sin `soporte_pass`.

- [ ] **Step 5: Limpiar** el device y la instalación de prueba de la base de desarrollo (`DELETE /api/devices/OX-TEST00000001` con JWT de superadmin).

---

### Task 9: Merge y deploy a producción (con confirmación del usuario)

**Files:** ninguno.

- [ ] **Step 1: Push de la rama y PR** (o merge a `main`, según prefiera el usuario)

```bash
git push -u origin feat/instalador-online
```

- [ ] **Step 2: Pedir confirmación explícita al usuario antes de tocar producción.** El servidor tiene cambios sin commitear en `app/*` y `views/pages/lotes.ejs`; el `git pull` no los toca si no hay conflicto, pero hay que verificarlo antes.

- [ ] **Step 3: Deploy** (solo con OK)

```bash
ssh do "cd /opt/AgroParallel/OrbitX && git status --short && git pull --ff-only origin main && pm2 restart OrbitX && pm2 logs OrbitX --lines 30 --nostream"
```

Expected: pull fast-forward, OrbitX `online`, sin errores en logs.

- [ ] **Step 4: Cargar la configuración** con JWT de superadmin: `PUT /api/config-sistema {"values":{"INSTALADOR_SOPORTE_PASS":"…","INSTALADOR_RUSTDESK_PASS":"…","INSTALADOR_RUSTDESK_HOST":"asistx.agroparallel.com","INSTALADOR_RUSTDESK_KEY":"…"}}`. Claves: las de `Tools/provision-server/secretos.json`. Host y key: los de `Tools/provision-server/server.js` (`rustdesk_host`, `rustdesk_key`). Subir el instalador de RustDesk de `Tools/RustDesk/*.exe` en `/firmwares` como producto `RustDesk` con su versión.

- [ ] **Step 5: Humo en producción:** `curl -s -o /dev/null -w "%{http_code}" https://orbitx.agroparallel.com/instalador` → `404` (todavía no hay .exe publicado; confirma que la ruta existe) y `/health` → `200`.
