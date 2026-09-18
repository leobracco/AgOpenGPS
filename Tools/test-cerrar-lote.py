# =============================================================================
#  test-cerrar-lote.py — Caso de prueba de "cerrar lote deja cosas prendidas".
#
#  Cerrar el lote tiene que dejar la máquina en frío. Lo que se verifica:
#
#    1. el motor deja de reportar lote abierto
#    2. el PILOTO queda apagado          <- seguridad: autosteer sin lote NO va
#    3. el giro automático queda apagado
#    4. la lista de guías queda vacía
#    5. la GEOMETRÍA de guiado queda en "Off" y sin puntos
#       (esto es lo que dibuja el mapa; si queda, las guías se siguen viendo
#        aunque la lista esté vacía)
#
#  El punto 5 es el que hay que mirar: /api/aog/tracks puede devolver [] y aun
#  así /api/aog/guidance/geometry seguir sirviendo la línea AB activa.
#
#  Uso (con PilotX corriendo):
#    python Tools/test-cerrar-lote.py "El de atras"
# =============================================================================

import json
import sys
import time
import urllib.parse
import urllib.request

BASE = "http://127.0.0.1:5180/api"


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=15) as r:
        return json.loads(r.read().decode("utf-8-sig"))


def post(path, body=b""):
    req = urllib.request.Request(BASE + path, data=body, method="POST")
    if body:
        req.add_header("Content-Type", "application/json")
    t = time.perf_counter()
    with urllib.request.urlopen(req, timeout=60) as r:
        data = r.read().decode("utf-8-sig")
    return time.perf_counter() - t, data


def main():
    lote = sys.argv[1] if len(sys.argv) > 1 else "El de atras"

    print("Abriendo lote %r..." % lote)
    post("/lotes/open?name=" + urllib.parse.quote(lote))
    time.sleep(2)

    st = get("/aog/state")
    if not st.get("is_job_started"):
        print("NO SE PUDO ABRIR el lote; el test no prueba nada. Abortando.")
        return 2

    # Enganchar el piloto: sin esto el test pasaría por defecto y no probaría
    # lo que importa (que CERRAR lo apaga), solo que ya estaba apagado.
    if not st.get("is_auto_steer_on"):
        post("/aog/guidance/command", b'{"cmd":"autosteer"}')
        time.sleep(1)
        st = get("/aog/state")
    if not st.get("is_auto_steer_on"):
        print("AVISO: no logre enganchar el piloto; el chequeo 2 queda flojo.")

    geo = get("/aog/guidance/geometry")["snapshot"]
    print("  antes de cerrar: piloto=%s  guias=%s  geom=%s (%d puntos)"
          % (st.get("is_auto_steer_on"), st.get("tracks_total"),
             geo.get("mode"), len(geo.get("points") or [])))

    seg, _ = post("/lotes/close")
    time.sleep(2)   # margen para que el motor asiente el cierre

    st = get("/aog/state")
    tr = get("/aog/tracks")
    geo = get("/aog/guidance/geometry")["snapshot"]
    pts = geo.get("points") or []

    checks = [
        ("1. lote cerrado",        st.get("is_job_started") is False),
        ("2. PILOTO apagado",      st.get("is_auto_steer_on") is False),
        ("3. giro apagado",        st.get("is_you_turn_on") is False),
        ("4. lista de guias vacia", len(tr.get("tracks") or []) == 0),
        ("5. geometria en Off",    geo.get("mode") == "Off" and len(pts) == 0),
    ]

    print("\nCerrar tardo %.3f s en el motor\n" % seg)
    ok = True
    for nombre, paso in checks:
        print("  [%s] %s" % ("OK" if paso else "FALLA", nombre))
        if not paso:
            ok = False
    if not checks[4][1]:
        print("       -> geom sigue en %r con %d puntos: el mapa las sigue dibujando"
              % (geo.get("mode"), len(pts)))

    print("\n%s" % ("TODO OK" if ok else "HAY FALLAS"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
