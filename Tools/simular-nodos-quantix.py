#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
# simular-nodos-quantix.py
#
# Simula N nodos (tolvas) QuantiX con M motores cada uno para probar el planter
# unificado del Hub (PilotX). Hace DOS cosas:
#
#   1) CONFIG (siempre): arma la lista de nodos/motores y la persiste vía
#      PUT /api/quantix/motores. Esto es lo que alimenta el planter de la
#      pantalla de siembra (1 surco = 1 motor en todo el conjunto). Sin esto
#      los motores NO aparecen, porque el planter lee quantiX_motores.json y
#      el sistema NO mergea nodos descubiertos por MQTT a esa config.
#
#   2) LIVE (--live, opcional): publica al broker MQTT de CoreX (:1883) los
#      announcements (retained) + status_live por motor, para que los nodos se
#      vean ONLINE y con PPS/RPM en vivo (igual que un ESP32 real). Usa un
#      cliente MQTT 3.1.1 mínimo embebido (sin dependencias / sin pip).
#
# Uso típico:
#   python tools/simular-nodos-quantix.py                 # 7 nodos x 2 = 14 motores
#   python tools/simular-nodos-quantix.py --live          # + telemetría en vivo
#   python tools/simular-nodos-quantix.py --agregar       # no pisa los nodos reales
#   python tools/simular-nodos-quantix.py --limpiar       # saca los nodos simulados
#
# Los nodos simulados llevan UID con prefijo "QX-SIM" para poder limpiarlos sin
# tocar los nodos reales del tractor.
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

SIM_PREFIX = "QX-SIM"
DEFAULT_BASE = "http://127.0.0.1:5180"
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


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
# Generación de config (nodos/motores)
# ----------------------------------------------------------------------------
def default_motor(nombre, dosis_fija, cortes):
    # Espejo de defaultMotor() del Hub + QxMotorConfig (snake_case).
    return {
        "nombre": nombre,
        "dosis_fija": dosis_fija,
        "manual_mode": False,
        "manual_dosis": 0.0,
        "campo_dosis": "",
        "kp": 80, "ki": 30, "kd": 0,
        "pwm_min": 600, "pwm_max": 4095,
        "meter_cal": 50,
        "max_integral": 1200,
        "deadband": 2, "slew_rate": 40,
        "dientes_engranaje": 20,
        "motor_type": 0,
        "max_hz": 40, "ff_gain": 1.0, "alpha": 0.4,
        "slew_rate_per_sec": 5000, "pid_time": 50,
        "cortes": cortes,
        "tren": 0,
    }


def build_sim_nodos(n_nodos, motores_por_nodo):
    """Arma n_nodos tolvas con M motores cada una. Asigna los surcos en orden
    para que cada surco pertenezca a UN solo motor (1 surco = 1 motor)."""
    nodos = []
    surco = 1  # contador global de surcos (1-based)
    for ni in range(n_nodos):
        uid = "%s%02d" % (SIM_PREFIX, ni + 1)
        motores = []
        for mi in range(motores_por_nodo):
            nombre = "Producto %d" % (mi + 1) if motores_por_nodo <= 2 else "Motor %d" % (mi + 1)
            dosis = 45.0 + 5.0 * ((ni + mi) % 6)  # variar dosis para ver pastillas "fija"
            motores.append(default_motor(nombre, round(dosis, 1), [surco]))
            surco += 1
        nodos.append({
            "uid": uid,
            "nombre": "Tolva %d" % (ni + 1),
            "habilitado": True,
            "distancia_entre_trenes": 0.0,
            "motores": motores,
        })
    return nodos


def aplicar_config(base, n_nodos, motores_por_nodo, agregar):
    url = base.rstrip("/") + "/api/quantix/motores"
    actual = http_get_json(url)
    cfg = (actual or {}).get("config") or {}
    if not isinstance(cfg.get("nodos"), list):
        cfg["nodos"] = []

    # Backup de la config actual (antes de tocar nada).
    ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    bak = os.path.join(SCRIPT_DIR, "quantiX_motores.backup-%s.json" % ts)
    try:
        with open(bak, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
        print("Backup de la config actual -> %s" % bak)
    except OSError as e:
        print("Aviso: no se pudo escribir backup (%s)" % e)

    # Sacar siempre los nodos simulados previos (idempotente).
    base_nodos = [x for x in cfg["nodos"]
                  if not str(x.get("uid", "")).startswith(SIM_PREFIX)]
    sim_nodos = build_sim_nodos(n_nodos, motores_por_nodo)

    if agregar:
        cfg["nodos"] = base_nodos + sim_nodos
    else:
        cfg["nodos"] = sim_nodos  # reemplaza todo por los simulados

    if not isinstance(cfg.get("ignorados"), list):
        cfg["ignorados"] = []

    resp = http_put_json(url, cfg)
    if not resp.get("ok"):
        print("ERROR al guardar config: %s" % resp)
        return None, []

    total_motores = sum(len(x["motores"]) for x in sim_nodos)
    total_surcos = total_motores  # 1 surco por motor
    print("Config aplicada: %d nodo(s) simulado(s), %d motor(es), %d surco(s)."
          % (len(sim_nodos), total_motores, total_surcos))
    if agregar and base_nodos:
        print("  (+ %d nodo(s) real(es) conservado(s))" % len(base_nodos))
    return cfg, sim_nodos


def limpiar_config(base):
    url = base.rstrip("/") + "/api/quantix/motores"
    actual = http_get_json(url)
    cfg = (actual or {}).get("config") or {}
    nodos = cfg.get("nodos") or []
    antes = len(nodos)
    cfg["nodos"] = [x for x in nodos
                    if not str(x.get("uid", "")).startswith(SIM_PREFIX)]
    quitados = antes - len(cfg["nodos"])
    if not isinstance(cfg.get("ignorados"), list):
        cfg["ignorados"] = []
    resp = http_put_json(url, cfg)
    if resp.get("ok"):
        print("Limpieza OK: %d nodo(s) simulado(s) quitado(s)." % quitados)
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
        # CONNECT: protocol "MQTT", level 4, clean session.
        vh = _mqtt_str("MQTT") + bytes([0x04, 0x02]) + struct.pack("!H", keepalive)
        payload = _mqtt_str(self.client_id)
        body = vh + payload
        pkt = bytes([0x10]) + _mqtt_remaining_length(len(body)) + body
        self.sock.sendall(pkt)
        # Leer CONNACK (4 bytes): 0x20 0x02 <flags> <rc>
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


def loop_live(broker, puerto, sim_nodos, hz):
    cli = MiniMqtt(broker, puerto, "qx-sim-" + str(random.randint(1000, 9999)))
    try:
        cli.connect()
    except OSError as e:
        print("No se pudo conectar al broker %s:%d (%s)." % (broker, puerto, e))
        print("¿Está CoreX corriendo? El broker MQTT vive dentro de CoreX (:1883).")
        return

    print("Conectado al broker %s:%d. Publicando telemetría %.0f Hz. Ctrl+C para parar."
          % (broker, puerto, hz))

    # Announcements (retained) — el registry los toma al (re)suscribirse.
    t0 = time.time()
    for ni, nodo in enumerate(sim_nodos):
        ann = {
            "ip": "192.168.5.%d" % (50 + ni),
            "fw": "1.0.0-sim",
            "motors": len(nodo["motores"]),
            "uptime": 1,
            "boot_reason": "sim",
            "device": "quantix",
        }
        cli.publish("agp/quantix/%s/announcement" % nodo["uid"],
                    json.dumps(ann), retain=True)

    period = 1.0 / hz if hz > 0 else 1.0
    pulsos = {}
    try:
        while True:
            up = int(time.time() - t0) + 1
            for nodo in sim_nodos:
                for mi, m in enumerate(nodo["motores"]):
                    # Objetivo plausible a partir de la dosis fija; real con ruido.
                    target = 18.0 + (m["dosis_fija"] % 20)
                    real = max(0.0, target + random.uniform(-1.5, 1.5))
                    rpm = int(real * 9)
                    key = "%s-%d" % (nodo["uid"], mi)
                    pulsos[key] = pulsos.get(key, 0) + int(real)
                    sl = {
                        "id": mi,
                        "pps_target": round(target, 1),
                        "pps_real": round(real, 1),
                        "pwm": 1200 + int(real * 30),
                        "rpm": rpm,
                        "pulsos": pulsos[key],
                    }
                    cli.publish("agp/quantix/%s/status_live" % nodo["uid"],
                                json.dumps(sl), retain=False)
            # Re-anunciar cada ~8s para refrescar uptime y mantener "online".
            if up % 8 == 0:
                for ni, nodo in enumerate(sim_nodos):
                    ann = {"ip": "192.168.5.%d" % (50 + ni), "fw": "1.0.0-sim",
                           "motors": len(nodo["motores"]), "uptime": up,
                           "boot_reason": "sim", "device": "quantix"}
                    cli.publish("agp/quantix/%s/announcement" % nodo["uid"],
                                json.dumps(ann), retain=True)
            time.sleep(period)
    except KeyboardInterrupt:
        print("\nCortado por el usuario.")
    finally:
        cli.disconnect()
        print("Desconectado del broker.")


# ----------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(
        description="Simula nodos QuantiX (tolvas) con N motores para probar el planter del Hub.")
    ap.add_argument("--base", default=DEFAULT_BASE,
                    help="URL base del Hub PilotX (default %s)" % DEFAULT_BASE)
    ap.add_argument("--nodos", type=int, default=7, help="cantidad de nodos/tolvas (default 7)")
    ap.add_argument("--motores", type=int, default=2, help="motores por nodo (default 2)")
    ap.add_argument("--agregar", action="store_true",
                    help="conserva los nodos reales y agrega los simulados (default: reemplaza)")
    ap.add_argument("--limpiar", action="store_true",
                    help="quita los nodos simulados (QX-SIM*) y sale")
    ap.add_argument("--live", action="store_true",
                    help="publica telemetría MQTT en vivo (online + PPS/RPM)")
    ap.add_argument("--broker", default="127.0.0.1", help="IP del broker MQTT (default 127.0.0.1)")
    ap.add_argument("--puerto", type=int, default=1883, help="puerto MQTT (default 1883)")
    ap.add_argument("--hz", type=float, default=2.0, help="frecuencia de status_live (default 2 Hz)")
    args = ap.parse_args()

    if args.limpiar:
        limpiar_config(args.base)
        return

    if args.nodos < 1 or args.motores < 1:
        print("nodos y motores deben ser >= 1")
        sys.exit(2)

    # El nodo QuantiX (ESP32) maneja 2 motores físicos. Avisamos si se pide más
    # para no simular una topología que el fierro real no soporta.
    if args.motores > 2:
        print("AVISO: el nodo QuantiX acepta 2 motores físicos; %d es solo simulación."
              % args.motores)

    cfg, sim_nodos = aplicar_config(args.base, args.nodos, args.motores, args.agregar)
    if cfg is None:
        sys.exit(1)

    print("Listo. Abrí QuantiX -> Siembra en el Hub para ver los motores.")
    if args.live:
        loop_live(args.broker, args.puerto, sim_nodos, args.hz)
    else:
        print("Tip: agregá --live para ver los nodos ONLINE con PPS/RPM en vivo.")


if __name__ == "__main__":
    main()
