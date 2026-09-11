#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
# simular-nodos-vistax.py
#
# Simula 3 nodos VistaX (ESP32 con sensores de siembra) para probar el monitor
# del Hub (PilotX) sin fierro. Cada nodo tiene 7 cables (NUM_SENSORES del
# firmware real) y ANUNCIA esa capacidad en el announcement — el nodo es la
# fuente de verdad de cuántos sensores admite, la PC no lo hardcodea.
# Topología default (19 sensores):
#
#   VX-SIM01 (7/7 cables): semilla surcos 1..7
#   VX-SIM02 (7/7 cables): semilla surcos 8..14
#   VX-SIM03 (5/7 cables): bajada herramienta + rotación fertilizante
#                          + turbina semilla (RPM) + tolva semilla
#                          + tolva fertilizante (tolva_vacia)
#
# Hace DOS cosas:
#
#   1) CONFIG (siempre): reemplaza el mapeo_sensores del implemento VistaX vía
#      PUT /api/vistax/implemento (los sensores llevan UID "VX-SIM*" para
#      poder limpiarlos sin tocar los reales). También fija el objetivo del
#      tren 1 coherente con el flujo simulado, para que los surcos pinten OK.
#
#   2) LIVE (--live, opcional): publica al broker MQTT de CoreX (:1883):
#        - announcement retained en agp/vistax/{UID}/announcement
#          (device:"vistax" → NodoRegistry los ve online)
#        - telemetría 4 Hz en vistax/{UID}/telemetria con el MISMO payload
#          que el firmware real v2.8+:
#            pulse: { cable, valor(pps), raw, acum }   (acum monotónico)
#            state: { cable, valor 0|1, raw, modo:"state" }
#
# Uso típico:
#   python tools/simular-nodos-vistax.py               # solo config
#   python tools/simular-nodos-vistax.py --live        # + telemetría en vivo
#   python tools/simular-nodos-vistax.py --live --falla 2      # 2 surcos tapados
#   python tools/simular-nodos-vistax.py --live --tolva-vacia  # alarma tolva fert.
#   python tools/simular-nodos-vistax.py --agregar     # conserva sensores reales
#   python tools/simular-nodos-vistax.py --limpiar     # saca los VX-SIM* y sale
# ============================================================================

import argparse
import datetime
import json
import os
import random
import socket
import struct
import sys
import time
import urllib.request

SIM_PREFIX = "VX-SIM"
DEFAULT_BASE = "http://127.0.0.1:5180"
# Capacidad de cables por nodo — espejo de NUM_SENSORES del firmware real.
# Se anuncia en el announcement ("cables") para que el Hub sepa cuántos
# sensores admite cada nodo.
CABLES_POR_NODO = 7
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# Flujo base de semilla: 35 pulsos/seg ≈ 2100 sem/min (objetivo del tren).
PPS_SEMILLA = 35.0
# Rotación de eje fertilizante: ~0.8 vueltas/seg (48 rpm de eje dosificador).
PPS_ROTACION = 0.8
# Turbina: ~63 pulsos/seg ≈ 3800 rpm (1 pulso por vuelta).
PPS_TURBINA = 63.0


# ----------------------------------------------------------------------------
# HTTP helpers (solo stdlib)
# ----------------------------------------------------------------------------
def http_get_json(url, timeout=8):
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        # El WebHost puede emitir BOM UTF-8 al frente; utf-8-sig lo tolera.
        return json.loads(r.read().decode("utf-8-sig"))


def http_put_json(url, body, timeout=8):
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        url, data=data, method="PUT",
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8-sig"))


# ----------------------------------------------------------------------------
# Topología simulada (config + spec de telemetría por cable)
# ----------------------------------------------------------------------------
def sensor_cfg(uid, cable, tipo, bajada, nombre, tren=1):
    # Espejo de VistaXSensorConfigDto (snake_case).
    return {
        "uid": uid,
        "cable": cable,
        "pin": 0,
        "bajada": bajada,
        "surco_desde": bajada if tipo == "semilla" else 0,
        "surco_hasta": bajada if tipo == "semilla" else 0,
        "tipo": tipo,
        "nombre": nombre,
        "tren": tren,
        "is_active": True,
        "seccion_aog": 0,
        "muted": False,
        "objetivo": 0,
    }


def build_topologia(surcos=14, fert=False):
    """Devuelve (mapeo, nodos). mapeo = lista VistaXSensorConfigDto;
    nodos = [{uid, cables:[{cable, tipo, pps|estado}]}] para el loop live.
    surcos = cantidad de surcos de semilla; se reparten de a 7 por nodo
    (capacidad de cables del firmware real). fert=True agrega un tren 2 de
    fertilizante con la misma cantidad de surcos (una fila más en el strip)."""
    mapeo = []
    nodos = []

    def agregar_tren(tipo, tren, desde_nodo):
        """Reparte `surcos` sensores de `tipo` en nodos de 7 cables.
        Devuelve la cantidad de nodos usados."""
        n_nodos = (surcos + CABLES_POR_NODO - 1) // CABLES_POR_NODO
        surco = 0
        for ni in range(n_nodos):
            uid = "%s%02d" % (SIM_PREFIX, desde_nodo + ni)
            cables = []
            for c in range(1, CABLES_POR_NODO + 1):
                if surco >= surcos:
                    break
                surco += 1
                nombre = ("Surco %d" if tipo == "semilla" else "Ferti %d") % surco
                cfg = sensor_cfg(uid, c, tipo, surco, nombre, tren=tren)
                cfg["surco_desde"] = surco
                cfg["surco_hasta"] = surco
                mapeo.append(cfg)
                cables.append({"cable": c, "tipo": tipo, "pps": PPS_SEMILLA})
            nodos.append({"uid": uid, "cables": cables})
        return n_nodos

    # --- Tren 1: semilla (VX-SIM01, VX-SIM02, ...) ---
    usados = agregar_tren("semilla", 1, 1)
    # --- Tren 2 (opcional): fertilizante ---
    if fert:
        usados += agregar_tren("fertilizante", 2, usados + 1)

    # --- Último nodo: bajada + rotación + turbina + tolvas (5/7 cables) ---
    uid3 = "%s%02d" % (SIM_PREFIX, usados + 1)
    cables3 = []
    mapeo.append(sensor_cfg(uid3, 1, "bajada_herramienta", 0, "Bajada herramienta"))
    cables3.append({"cable": 1, "tipo": "bajada_herramienta", "estado": 1})
    mapeo.append(sensor_cfg(uid3, 2, "rotacion_eje", 0, "Rotación fertilizante"))
    cables3.append({"cable": 2, "tipo": "rotacion_eje", "pps": PPS_ROTACION})
    mapeo.append(sensor_cfg(uid3, 3, "turbina", 0, "Turbina semilla"))
    cables3.append({"cable": 3, "tipo": "turbina", "pps": PPS_TURBINA})
    mapeo.append(sensor_cfg(uid3, 4, "tolva_vacia", 0, "Tolva semilla"))
    cables3.append({"cable": 4, "tipo": "tolva_vacia", "estado": 0})
    mapeo.append(sensor_cfg(uid3, 5, "tolva_vacia", 0, "Tolva fertilizante"))
    cables3.append({"cable": 5, "tipo": "tolva_vacia", "estado": 0})
    nodos.append({"uid": uid3, "cables": cables3})

    return mapeo, nodos


def aplicar_config(base, agregar, surcos=14, fert=False):
    url = base.rstrip("/") + "/api/vistax/implemento"
    actual = http_get_json(url)
    # GET /api/vistax/implemento envuelve el DTO; tolerar ambas formas.
    imp = actual.get("implemento") if isinstance(actual, dict) else None
    if not isinstance(imp, dict):
        imp = actual if isinstance(actual, dict) else {}

    # Backup del implemento actual (antes de tocar nada).
    ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    bak = os.path.join(SCRIPT_DIR, "vistaX_implemento.backup-%s.json" % ts)
    try:
        with open(bak, "w", encoding="utf-8") as f:
            json.dump(imp, f, ensure_ascii=False, indent=2)
        print("Backup del implemento actual -> %s" % bak)
    except OSError as e:
        print("Aviso: no se pudo escribir backup (%s)" % e)

    mapeo_sim, nodos = build_topologia(surcos, fert)
    mapeo_actual = imp.get("mapeo_sensores") or []
    reales = [s for s in mapeo_actual
              if not str(s.get("uid", "")).startswith(SIM_PREFIX)]

    if agregar:
        imp["mapeo_sensores"] = reales + mapeo_sim
    else:
        imp["mapeo_sensores"] = mapeo_sim

    # Objetivo del tren 1 coherente con el flujo simulado (sem/min) para que
    # los surcos pinten "ok" y las fallas inyectadas pinten "tapado".
    setup = imp.get("setup") or {}
    objetivos = setup.get("objetivos_tren") or {}
    objetivos["1"] = round(PPS_SEMILLA * 60.0)
    if fert:
        objetivos["2"] = round(PPS_SEMILLA * 60.0)
    setup["objetivos_tren"] = objetivos
    # Tolerancia de desvío 15%: sin esto queda 0 y CUALQUIER desvío del ruido
    # simulado (±7%) pinta bajo/exceso en vez de ok.
    if not setup.get("tolerancia_desvio"):
        setup["tolerancia_desvio"] = 15
    imp["setup"] = setup

    resp = http_put_json(url, imp)
    if not resp.get("ok"):
        print("ERROR al guardar implemento: %s" % resp)
        return None
    total = len(mapeo_sim)
    semillas = sum(1 for s in mapeo_sim if s["tipo"] == "semilla")
    print("Config aplicada: %d nodo(s) simulado(s), %d sensor(es) "
          "(%d semilla + bajada + rotación fert. + turbina + 2 tolvas)."
          % (len(nodos), total, semillas))
    if agregar and reales:
        print("  (+ %d sensor(es) real(es) conservado(s))" % len(reales))
    return nodos


def limpiar_config(base):
    url = base.rstrip("/") + "/api/vistax/implemento"
    actual = http_get_json(url)
    imp = actual.get("implemento") if isinstance(actual, dict) else None
    if not isinstance(imp, dict):
        imp = actual if isinstance(actual, dict) else {}
    mapeo = imp.get("mapeo_sensores") or []
    antes = len(mapeo)
    imp["mapeo_sensores"] = [s for s in mapeo
                             if not str(s.get("uid", "")).startswith(SIM_PREFIX)]
    quitados = antes - len(imp["mapeo_sensores"])
    resp = http_put_json(url, imp)
    if resp.get("ok"):
        print("Limpieza OK: %d sensor(es) simulado(s) quitado(s)." % quitados)
    else:
        print("ERROR al limpiar: %s" % resp)


# ----------------------------------------------------------------------------
# Cliente MQTT 3.1.1 mínimo (sin dependencias) — solo CONNECT + PUBLISH QoS0
# ----------------------------------------------------------------------------
def _mqtt_remaining_length(n):
    out = bytearray()
    while True:
        b = n % 128
        n //= 128
        if n > 0:
            b |= 0x80
        out.append(b)
        if n == 0:
            break
    return bytes(out)


def _mqtt_str(s):
    b = s.encode("utf-8")
    return struct.pack("!H", len(b)) + b


class MiniMqtt:
    def __init__(self, host, port, client_id):
        self.host = host
        self.port = port
        self.client_id = client_id
        self.sock = None

    def connect(self, keepalive=60):
        self.sock = socket.create_connection((self.host, self.port), timeout=6)
        vh = _mqtt_str("MQTT") + bytes([0x04, 0x02]) + struct.pack("!H", keepalive)
        payload = _mqtt_str(self.client_id)
        body = vh + payload
        pkt = bytes([0x10]) + _mqtt_remaining_length(len(body)) + body
        self.sock.sendall(pkt)
        ack = self.sock.recv(4)
        if len(ack) >= 4 and ack[0] == 0x20 and ack[3] != 0x00:
            raise RuntimeError("CONNACK rechazado, rc=%d" % ack[3])

    def publish(self, topic, payload, retain=False):
        flags = 0x30 | (0x01 if retain else 0x00)  # QoS0
        body = _mqtt_str(topic) + payload.encode("utf-8")
        pkt = bytes([flags]) + _mqtt_remaining_length(len(body)) + body
        self.sock.sendall(pkt)

    def disconnect(self):
        try:
            if self.sock:
                self.sock.sendall(bytes([0xE0, 0x00]))  # DISCONNECT
        except OSError:
            pass
        finally:
            try:
                if self.sock:
                    self.sock.close()
            except OSError:
                pass
            self.sock = None


# ----------------------------------------------------------------------------
# Loop live: announcement + telemetría con el payload del firmware real
# ----------------------------------------------------------------------------
def publicar_announcements(cli, nodos, seq, uptime):
    for ni, nodo in enumerate(nodos):
        ann = {
            "schema": "agp.vistax.announcement/1",
            "seq": seq,
            "uid": nodo["uid"],
            "ip": "192.168.5.%d" % (60 + ni),
            "fw": "2.9.0-sim",
            "hw": "esp32",
            "device": "vistax",
            # Capacidad de cables del nodo (NO los usados) — igual que el
            # firmware real, que anuncia NUM_SENSORES.
            "cables": CABLES_POR_NODO,
            "rssi": -45 - ni,
            "uptime": uptime,
            "boot_reason": "sim",
            "safe_mode": False,
            "crash_count": 0,
        }
        cli.publish("agp/vistax/%s/announcement" % nodo["uid"],
                    json.dumps(ann), retain=True)


def loop_live(broker, puerto, nodos, hz, fallas, tolva_vacia):
    cli = MiniMqtt(broker, puerto, "vx-sim-" + str(random.randint(1000, 9999)))
    try:
        cli.connect()
    except OSError as e:
        print("No se pudo conectar al broker %s:%d (%s)." % (broker, puerto, e))
        print("¿Está CoreX corriendo? El broker MQTT vive dentro de CoreX (:1883).")
        return

    print("Conectado al broker %s:%d. Telemetría %.0f Hz. Ctrl+C para parar."
          % (broker, puerto, hz))
    if fallas > 0:
        print("Simulando %d surco(s) TAPADO(s) (flujo 0)." % fallas)
    if tolva_vacia:
        print("Simulando TOLVA FERTILIZANTE VACÍA (alarma).")

    # Fallas: marcar los primeros N cables de semilla como tapados.
    marcados = 0
    for nodo in nodos:
        for c in nodo["cables"]:
            if c["tipo"] == "semilla" and marcados < fallas:
                c["tapado"] = True
                marcados += 1
    # Tolva fertilizante = VX-SIM03 cable 5.
    if tolva_vacia:
        for nodo in nodos:
            for c in nodo["cables"]:
                if c["tipo"] == "tolva_vacia" and c["cable"] == 5:
                    c["estado"] = 1

    t0 = time.time()
    seq = {"ann": 0, "tel": 0}
    acum = {}  # (uid, cable) → pulsos acumulados (float para arrastre)
    period = 1.0 / hz if hz > 0 else 0.25
    ultimo_ann = 0.0

    try:
        while True:
            now = time.time()
            up = int(now - t0) + 1

            # Announcement retained cada 10 s (igual que el firmware).
            if now - ultimo_ann >= 10.0:
                seq["ann"] += 1
                publicar_announcements(cli, nodos, seq["ann"], up)
                ultimo_ann = now

            for nodo in nodos:
                seq["tel"] += 1
                sensores = []
                for c in nodo["cables"]:
                    if "pps" in c:  # PULSE: {cable, valor, raw, acum}
                        pps = 0.0 if c.get("tapado") else \
                            max(0.0, c["pps"] * random.uniform(0.93, 1.07))
                        key = (nodo["uid"], c["cable"])
                        acum[key] = acum.get(key, 0.0) + pps * period
                        sensores.append({
                            "cable": c["cable"],
                            "valor": round(pps, 2),
                            "raw": int(round(pps * period)),
                            "acum": int(acum[key]),
                        })
                    else:  # STATE: {cable, valor, raw, modo}
                        sensores.append({
                            "cable": c["cable"],
                            "valor": c["estado"],
                            "raw": c["estado"],
                            "modo": "state",
                        })
                tel = {
                    "schema": "agp.vistax.telemetry/1",
                    "seq": seq["tel"],
                    "uid": nodo["uid"],
                    "sensores": sensores,
                }
                cli.publish("vistax/%s/telemetria" % nodo["uid"],
                            json.dumps(tel), retain=False)
            time.sleep(period)
    except KeyboardInterrupt:
        print("\nCortado por el usuario.")
    finally:
        cli.disconnect()
        print("Desconectado del broker.")


# ----------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(
        description="Simula 3 nodos VistaX (14 semilla + bajada + rotación "
                    "fert. + turbina + 2 tolvas) para probar el monitor del Hub.")
    ap.add_argument("--base", default=DEFAULT_BASE,
                    help="URL base del Hub PilotX (default %s)" % DEFAULT_BASE)
    ap.add_argument("--agregar", action="store_true",
                    help="conserva los sensores reales y agrega los simulados (default: reemplaza)")
    ap.add_argument("--limpiar", action="store_true",
                    help="quita los sensores simulados (VX-SIM*) y sale")
    ap.add_argument("--live", action="store_true",
                    help="publica telemetría MQTT en vivo (nodos online + flujo)")
    ap.add_argument("--surcos", type=int, default=14, metavar="N",
                    help="cantidad de surcos de semilla a simular (default 14; se reparten de a 7 por nodo)")
    ap.add_argument("--fert", action="store_true",
                    help="agrega un tren 2 de fertilizante con la misma cantidad de surcos")
    ap.add_argument("--falla", type=int, default=0, metavar="N",
                    help="simula N surcos de semilla tapados (flujo 0)")
    ap.add_argument("--tolva-vacia", action="store_true",
                    help="simula la tolva de fertilizante vacía (alarma)")
    ap.add_argument("--broker", default="127.0.0.1", help="IP del broker MQTT (default 127.0.0.1)")
    ap.add_argument("--puerto", type=int, default=1883, help="puerto MQTT (default 1883)")
    ap.add_argument("--hz", type=float, default=4.0,
                    help="frecuencia de telemetría (default 4 Hz ≈ firmware 250 ms)")
    args = ap.parse_args()

    if args.limpiar:
        limpiar_config(args.base)
        return

    nodos = aplicar_config(args.base, args.agregar, max(1, args.surcos), args.fert)
    if nodos is None:
        sys.exit(1)

    print("Listo. Abrí VistaX en el Hub (o el monitor live) para ver los sensores.")
    if args.live:
        loop_live(args.broker, args.puerto, nodos, args.hz,
                  max(0, args.falla), args.tolva_vacia)
    else:
        print("Tip: agregá --live para ver los nodos ONLINE con flujo en vivo.")


if __name__ == "__main__":
    main()
