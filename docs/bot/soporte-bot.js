// ============================================================
//  services/soporte-bot.js — OrbitX Cloud
//  Capa 2 del chat de soporte: bot de IA de soporte tecnico de
//  PilotX/AgroParallel.
//
//  Que hace:
//    Dado un device_id y el historial del chat, arma un prompt con
//    (a) el system prompt de soporte tecnico, (b) contexto REAL del
//    equipo (ultima config conocida, version de firmware y telemetria
//    de guiado reciente) y (c) los ultimos mensajes del chat. Devuelve
//    la respuesta del bot ya estructurada.
//
//  Regla dura (ver memoria chat-soporte-ia-aceptacion):
//    Si el bot sugiere un CAMBIO DE CONFIGURACION, NO se aplica solo.
//    Se emite como un mensaje tipo "propuesta_config" con estado
//    "pendiente" para que el operario lo Acepte/Rechace en la pantalla.
//    Los mensajes normales son tipo "texto".
//
//  Credenciales:
//    Reusa el mismo mecanismo que routes/agraria_chat.js — la API key
//    sale de process.env.ANTHROPIC_API_KEY. No se duplica ni se hardcodea.
//
//  Este modulo NO decide cuando responder (eso es el gating en
//  routes/soporte.js: solo si el chat tiene bot_activo === true). Aca
//  solo se genera el texto.
// ============================================================
"use strict";

const fs   = require("fs");
const path = require("path");

const API_URL = "https://api.anthropic.com/v1/messages";
// Opus 5 (2026-09-19): el operario esta arriba del tractor con un problema
// concreto y el bot tiene que razonar sobre config real, no tirar generalidades.
// En Opus 5 el razonamiento viene ACTIVO por defecto — ver las dos trampas que
// eso trae, resueltas en callClaude(): el presupuesto de tokens y de donde se
// saca el texto de la respuesta.
const MODEL   = "claude-opus-5";

// Cuantos mensajes del chat le pasamos al modelo (los mas recientes).
const MAX_HIST_BOT = 12;

// ── Cliente Claude (mismo patron que agraria_chat.js) ─────────
// El system se manda SIEMPRE como bloque de texto con cache_control ephemeral:
// el system prompt + la KB de PilotX son grandes y no cambian entre mensajes,
// asi que se cachean (prompt caching) y no se pagan completos en cada turno.
// 4000 y no 700: en Opus 5 los tokens de razonamiento salen del MISMO
// presupuesto que la respuesta. Con 700 el modelo se los gasta pensando y
// devuelve vacio — el bot quedaba mudo sin tirar un solo error.
async function callClaude(system, messages, max_tokens = 4000) {
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (!apiKey) throw new Error("ANTHROPIC_API_KEY no configurada");
  const systemBlocks = Array.isArray(system)
    ? system
    : [{ type: "text", text: String(system || ""), cache_control: { type: "ephemeral" } }];
  const r = await fetch(API_URL, {
    method: "POST",
    // Timeout defensivo: una respuesta colgada no debe dejar el handler
    // pendiente para siempre.
    signal: AbortSignal.timeout(60_000),
    headers: {
      "Content-Type":      "application/json",
      "x-api-key":         apiKey,
      // El prompt caching ya es GA: el header de beta no hace falta. El
      // cache_control de los bloques de system sigue igual.
      "anthropic-version": "2023-06-01",
    },
    body: JSON.stringify({ model: MODEL, max_tokens, system: systemBlocks, messages }),
  });
  if (!r.ok) {
    const err = await r.json().catch(() => ({}));
    throw new Error(err.error?.message || `Claude API ${r.status}`);
  }
  const data = await r.json();
  // El texto se BUSCA por tipo, no se toma de content[0]: con razonamiento
  // activo el primer bloque es de tipo "thinking" y no tiene .text, asi que
  // content[0].text daba undefined y el bot contestaba en blanco.
  const bloque = (data.content || []).find((b) => b && b.type === "text");
  return bloque?.text?.trim() || "";
}

// ── System prompt de soporte tecnico ─────────────────────────
const SYSTEM_SOPORTE = `Sos el asistente de soporte tecnico de PilotX (AgOpenGPS / CentriX-Spark) de Agro Parallel.
Ayudas por chat a un operario/maquinista que esta ARRIBA del tractor, en el campo, con la pantalla de PilotX.
Respondes en espanol rioplatense, tono directo y campero, sin markdown ni asteriscos. Se concreto: el tipo esta laburando.

Que sabes de PilotX:
- Guiado y autopiloto: modos Pure Pursuit (PP) y Stanley. PP es mas suave y aguanta mejor antena simple; Stanley pega mas al AB pero puede zigzaguear si la ganancia esta alta o el GPS es ruidoso.
- GPS/GNSS: calidad de fix (single/DGPS/float/RTK fix). RTK fix es lo ideal; float o single dan saltos y cross-track (XTE) grande. Sin RTK, el rumbo se apoya mas en la IMU (fusion tipica 70/30).
- IMU: da rumbo cuando la antena es simple o hay poca senal. Si el tractor "cabecea" o el rumbo se va, revisar montaje/calibracion de la IMU.
- WAS (sensor de angulo de rueda): si el volante no llega o se pasa, suele ser conteo/limites del WAS o ganancia de direccion (steer gain / Kp), no siempre el WAS fisico.
- Secciones (SectionX): corte automatico por solape/boundary; el work switch tambien puede venir de ToolX.
- Nodos ESP32 (VistaX/QuantiX/SectionX/FlowX/StormX/ToolX): NO tienen internet propio, hablan por MQTT en la LAN contra el broker EMBEBIDO en el Engine de PilotX (puerto 1883). Si un nodo "no aparece", revisar WiFi del nodo, IP del broker configurada en el portal del nodo, y que el Engine este corriendo.
- La PC del tractor sincroniza con OrbitX (cloud) por HTTPS; los lotes viven en Fields/<Nombre>/.

Como respondes:
- Primero entende el sintoma. Si falta info clave, PREGUNTA (una o dos preguntas puntuales), no adivines.
- Usa el CONTEXTO DEL EQUIPO que te paso el sistema (config actual, version, telemetria) para dar una respuesta concreta a ESTE equipo.
- No inventes valores del equipo que no esten en el contexto.
- Nunca prometas aplicar cambios vos mismo: vos proponés, el operario decide en la pantalla.

REGLA CRITICA DE CAMBIOS DE CONFIGURACION:
- Si tu recomendacion implica CAMBIAR UN PARAMETRO de configuracion del equipo (ej. ganancia de direccion, modo PP/Stanley, look-ahead, limites del WAS, anticipacion de secciones, IP de broker, etc.), NO lo presentes como hecho ni digas "ya lo cambie".
- En ese caso tenes que devolver una PROPUESTA para que el operario la acepte o rechace en pantalla.

FORMATO DE SALIDA (obligatorio):
Respondes EXCLUSIVAMENTE con un objeto JSON valido, sin texto antes ni despues, sin backticks. Dos formas posibles:

1) Mensaje normal (explicacion, pregunta, diagnostico sin tocar config):
{"tipo":"texto","texto":"<tu respuesta para el operario>"}

2) Cuando sugeris uno o mas cambios de configuracion:
{"tipo":"propuesta_config","texto":"<explicacion breve de por que y que cambia>","cambios":[{"clave":"<nombre del parametro>","valor_actual":"<valor actual o null si no lo sabes>","valor_nuevo":"<valor propuesto>"}]}

Reglas del JSON:
- "texto" siempre presente y no vacio.
- "cambios" solo en propuesta_config, con 1 a 5 items. "valor_actual" usa el valor real del contexto si lo tenes; si no, null.
- No agregues otras claves. No uses saltos de linea sin escapar dentro de los strings.

CLAVES VALIDAS PARA "cambios[].clave" (lista blanca — usalas EXACTAS, en snake_case):
proportional_gain, max_steer_speed, min_steer_speed, dead_zone_heading, dead_zone_delay,
look_ahead_mult, hold_look_ahead, acquire_factor, integral_pp, stanley_gain,
heading_error_gain, integral_stanley, side_hill_comp, u_turn_comp, snap_distance,
guidance_look_ahead, guidance_speed_limit.
Cualquier otro ajuste (calibracion del WAS, cuentas por grado, geometria, inversiones,
PWM, modo PP/Stanley, IMU/RTK, secciones) se EXPLICA y se guia a mano, NUNCA va en una
propuesta_config. El valor de "valor_nuevo" es el numero que ve el operario en pantalla
(display, con coma decimal como en la pantalla), segun la Base de conocimiento.

RANGOS VALIDOS (en display; NO propongas fuera de estos rangos):
- proportional_gain: 1 a 200 (entero)      - stanley_gain: 0,1 a 4,0
- max_steer_speed: 1 a 40 km/h             - heading_error_gain: 0,1 a 1,5
- min_steer_speed: 0 a 10 km/h             - integral_stanley: 0 a 100
- dead_zone_heading: 0 a 5 grados          - side_hill_comp: 0,00 a 0,30 grados
- dead_zone_delay: 1 a 50 (entero)         - u_turn_comp: 2 a 20 (entero)
- look_ahead_mult: 0,5 a 6,0               - snap_distance: 1 a 100
- hold_look_ahead: 1,0 a 7,0 s             - guidance_look_ahead: 0,1 a 5,0 s
- acquire_factor: 0,20 a 3,00              - guidance_speed_limit: 1 a 40 km/h
- integral_pp: 0 a 100
"valor_actual" tambien en display. Si proponés un cambio, movete de a poco dentro del rango.`;

// ── Base de conocimiento de PilotX ───────────────────────────
// Se lee UNA sola vez al cargar el modulo (archivo junto a este .js). Se anexa
// al system prompt para que el modelo la use como referencia. Al viajar en el
// bloque de system con cache_control, se cachea y no se paga en cada mensaje.
const KB_PILOTX = (() => {
  try {
    return fs.readFileSync(path.join(__dirname, "kb-pilotx.md"), "utf8");
  } catch (e) {
    console.error("[soporte-bot] no se pudo cargar kb-pilotx.md:", e.message);
    return "";
  }
})();

// System completo = prompt de soporte + KB. Es lo que se cachea.
const SYSTEM_SOPORTE_FULL = KB_PILOTX
  ? `${SYSTEM_SOPORTE}\n\n## Base de conocimiento de PilotX (referencia)\n\n${KB_PILOTX}`
  : SYSTEM_SOPORTE;

// ── Helpers de contexto ──────────────────────────────────────

// Ultima config conocida del equipo (config_backup_<device_id> en global).
async function getConfigBackup(globalDB, deviceId) {
  try {
    const doc = await globalDB.get(`config_backup_${deviceId}`).catch(() => null);
    if (!doc || !Array.isArray(doc.versiones) || !doc.versiones.length) return null;
    const ult = doc.versiones[doc.versiones.length - 1];
    return {
      version_pilotx: ult.version_pilotx || null,
      ts:             ult.ts || null,
      config:         ult.config,
    };
  } catch { return null; }
}

// Telemetria de guiado reciente (tracking_live_<device_id> en global).
// Solo se considera util si es de los ultimos 15 min.
async function getTelemetria(globalDB, deviceId) {
  try {
    const doc = await globalDB.get(`tracking_live_${deviceId}`).catch(() => null);
    if (!doc) return null;
    const edadMs = Date.now() - (doc.ts || 0);
    if (edadMs > 15 * 60 * 1000) return { stale: true, edad_min: Math.round(edadMs / 60000) };
    return {
      stale:       false,
      edad_min:    Math.round(edadMs / 60000),
      fix:         doc.fix ?? null,          // calidad de fix (0=none..4=RTK fix segun NMEA)
      xte:         doc.xte ?? null,          // cross-track error
      steer_angle: doc.steer_angle ?? null,  // angulo de direccion (WAS)
      autosteer:   doc.autosteer ?? null,    // piloto enganchado
      speed:       doc.speed ?? null,
      field:       doc.field || null,
      modules:     doc.modules || null,
    };
  } catch { return null; }
}

// Traduce el codigo NMEA de fix a algo legible para el prompt.
function nombreFix(fix) {
  const M = { 0: "sin fix", 1: "single (sin correccion)", 2: "DGPS", 4: "RTK fix", 5: "RTK float" };
  if (fix === null || fix === undefined) return "desconocido";
  return M[fix] || `codigo ${fix}`;
}

// Arma el bloque de contexto REAL del equipo para inyectar en el primer
// mensaje del usuario. Todo lo que sea null/desconocido se omite o se marca.
function armarContextoEquipo({ device, backup, telem }) {
  const L = [];
  L.push("=== CONTEXTO DEL EQUIPO (datos reales, usalos) ===");
  if (device) {
    L.push(`Equipo: ${device.hostname || device.device_id}`);
    L.push(`Version de firmware/PilotX (device): ${device.version || "desconocida"}`);
    if (device.estab_slug) L.push(`Establecimiento: ${device.estab_slug}`);
  }

  if (backup) {
    if (backup.version_pilotx) L.push(`Version PilotX (ultimo backup de config): ${backup.version_pilotx}`);
    let cfgTxt = "";
    try { cfgTxt = JSON.stringify(backup.config); } catch { cfgTxt = ""; }
    if (cfgTxt) {
      if (cfgTxt.length > 2500) cfgTxt = cfgTxt.slice(0, 2500) + " …[recortado]";
      L.push("Ultima configuracion conocida del equipo (JSON):");
      L.push(cfgTxt);
    }
  } else {
    L.push("Config del equipo: no hay backup disponible.");
  }

  if (telem && !telem.stale) {
    L.push(`Telemetria de guiado (hace ${telem.edad_min} min):`);
    L.push(`  Fix GPS: ${nombreFix(telem.fix)}`);
    if (telem.xte !== null)         L.push(`  Cross-track (XTE): ${telem.xte}`);
    if (telem.steer_angle !== null) L.push(`  Angulo de direccion (WAS): ${telem.steer_angle}`);
    if (telem.autosteer !== null)   L.push(`  Autosteer: ${telem.autosteer ? "enganchado" : "suelto"}`);
    if (telem.speed !== null)       L.push(`  Velocidad: ${telem.speed}`);
    if (telem.field)                L.push(`  Lote abierto: ${telem.field}`);
  } else if (telem && telem.stale) {
    L.push(`Telemetria de guiado: sin datos frescos (ultimo hace ${telem.edad_min} min).`);
  } else {
    L.push("Telemetria de guiado: no disponible.");
  }
  L.push("=== FIN CONTEXTO ===");
  return L.join("\n");
}

// ── Parseo de la salida del modelo ───────────────────────────
// El modelo debe devolver JSON. Somos defensivos: si viene con backticks,
// texto alrededor o directamente no parsea, degradamos a mensaje de texto.
function parsearSalida(raw) {
  const fallbackTexto = (t) => ({ tipo: "texto", texto: String(t || "").trim() || "No pude generar una respuesta." });
  if (!raw) return fallbackTexto("");

  let txt = String(raw).trim();
  // Sacar fences ```json ... ``` si el modelo los agrego igual.
  txt = txt.replace(/^```(?:json)?\s*/i, "").replace(/\s*```$/i, "").trim();

  // Recortar al primer objeto {...} balanceado por si viene con prosa alrededor.
  const ini = txt.indexOf("{");
  const fin = txt.lastIndexOf("}");
  let candidato = txt;
  if (ini !== -1 && fin !== -1 && fin > ini) candidato = txt.slice(ini, fin + 1);

  let obj;
  try { obj = JSON.parse(candidato); }
  catch { return fallbackTexto(txt); }

  const texto = typeof obj.texto === "string" ? obj.texto.trim() : "";

  if (obj.tipo === "propuesta_config" && Array.isArray(obj.cambios) && obj.cambios.length) {
    const cambios = obj.cambios.slice(0, 5).map((c) => ({
      clave:        String(c && c.clave != null ? c.clave : "").slice(0, 120),
      valor_actual: c && c.valor_actual !== undefined ? c.valor_actual : null,
      valor_nuevo:  c && c.valor_nuevo  !== undefined ? c.valor_nuevo  : null,
    })).filter((c) => c.clave);

    if (cambios.length) {
      return {
        tipo:  "propuesta_config",
        texto: texto || "Te propongo un ajuste de configuracion. Revisalo y aceptalo o rechazalo.",
        payload: { cambios, estado: "pendiente" },
      };
    }
  }

  // Cualquier otra cosa (incluido tipo "texto") -> mensaje normal.
  return fallbackTexto(texto || txt);
}

// ── API principal ────────────────────────────────────────────
// generarRespuestaBot(globalDB, deviceId, mensajes) -> { tipo, texto, payload? }
//   globalDB  : nano db handle de orbitx_global (req.app.locals.globalDB)
//   deviceId  : id del equipo
//   mensajes  : array de mensajes del chat [{rol, texto, ts, tipo?, payload?}]
//
// Devuelve SIEMPRE un objeto listo para persistir como mensaje del bot.
// No persiste nada ni decide el gating: eso es responsabilidad del caller.
async function generarRespuestaBot(globalDB, deviceId, mensajes) {
  // Contexto real del equipo (en paralelo).
  const [device, backup, telem] = await Promise.all([
    globalDB.get(`device_${deviceId}`).catch(() => null),
    getConfigBackup(globalDB, deviceId),
    getTelemetria(globalDB, deviceId),
  ]);

  const contexto = armarContextoEquipo({ device, backup, telem });

  // Historial reciente -> mensajes para Claude. Roles del chat:
  // operario -> user ; bot/soporte -> assistant. El bloque de contexto se
  // antepone como texto del primer turno de usuario.
  const hist = (mensajes || []).slice(-MAX_HIST_BOT);
  const claudeMsgs = [];
  claudeMsgs.push({
    role: "user",
    content: contexto + "\n\n(Lo que sigue es la conversacion con el operario. Respondé al ultimo mensaje del operario en el formato JSON indicado.)",
  });
  // Claude exige alternancia user/assistant; agrupamos por rol para no romperla.
  let ultimoRol = "user";
  for (const m of hist) {
    const rol = m.rol === "operario" ? "user" : "assistant";
    const txt = String(m.texto || "").trim();
    if (!txt) continue;
    if (rol === ultimoRol && claudeMsgs.length) {
      claudeMsgs[claudeMsgs.length - 1].content += "\n" + txt;
    } else {
      claudeMsgs.push({ role: rol, content: txt });
      ultimoRol = rol;
    }
  }
  // Debe terminar en un turno de usuario para que el modelo conteste.
  if (ultimoRol !== "user") {
    claudeMsgs.push({ role: "user", content: "(Respondé al ultimo mensaje del operario.)" });
  }

  const raw = await callClaude(SYSTEM_SOPORTE_FULL, claudeMsgs, 4000);
  return parsearSalida(raw);
}

module.exports = {
  generarRespuestaBot,
  // Exportados para test unitario sin pegarle a la API ni a CouchDB.
  parsearSalida,
  armarContextoEquipo,
  nombreFix,
  SYSTEM_SOPORTE,
  SYSTEM_SOPORTE_FULL,
  KB_PILOTX,
  MODEL,
};
