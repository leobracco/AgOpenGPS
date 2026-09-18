# =============================================================================
#  generar-prescripcion-texto.py — Mapa de prescripción de prueba que DIBUJA
#  un texto ("AGRO PARALLEL" por defecto) con zonas de dosis distinta.
#
#  Para qué sirve: validar de un vistazo todo el camino de prescripciones sin
#  depender del cloud. Si el mapa se ve escrito y derecho sobre el lote, el
#  parseo, el georreferenciado y el render están bien. Si sale espejado, rotado
#  o con las dosis cruzadas, se ve al toque — que es justamente lo que un
#  archivo de zonas cualquiera NO te deja ver.
#
#  Salidas (las dos, mismo contenido):
#    · <salida>.geojson              → lo que consume PilotX
#                                      (AgroParallel.Services/PrescripcionService)
#    · <salida>.shp/.shx/.dbf/.prj   → shapefile WGS84 para QGIS / OrbitX
#                                      (lo lee AgroParallel.Common/ShapefileReader)
#
#  Uso:
#    python Tools/generar-prescripcion-texto.py
#    python Tools/generar-prescripcion-texto.py --texto "LA PALOMA" --celda 15
#    python Tools/generar-prescripcion-texto.py --origen -34.2596694,-59.4586153
#
#  El origen por defecto es el StartFix del lote "La Paloma". Para otro lote,
#  sacá el StartFix de <Fields>/<lote>/Field.txt y pasalo con --origen.
# =============================================================================

import argparse
import json
import math
import os

# --- Tipografía 5x7 -----------------------------------------------------------
# Blocky a propósito: cada celda encendida termina siendo una zona rectangular
# de varias decenas de metros. Un contorno de letra "lindo" daría polígonos con
# curvas que no se parecen en nada a una prescripción real.
FONT = {
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "G": ["01110", "10001", "10000", "10111", "10001", "10001", "01110"],
    "L": ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    "O": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "X": ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
    "I": ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "M": ["10001", "11011", "10101", "10001", "10001", "10001", "10001"],
    "N": ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "C": ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "V": ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
    " ": ["00000"] * 7,
}

GLIFO_ANCHO = 5
GLIFO_ALTO = 7
ESPACIO_LETRA = 1     # celdas entre letras
ESPACIO_RENGLON = 2   # celdas entre renglones
MARGEN_FONDO = 3      # celdas de margen del fondo cuando el lote no tiene contorno

# Dosis por letra, en kg/ha. Se cicla: letras vecinas siempre quedan distintas,
# que es lo que hace visible si el render pinta la zona equivocada.
DOSIS_CICLO = [55.0, 70.0, 85.0, 100.0, 115.0, 130.0]

# StartFix del lote "La Paloma" (Fields/La Paloma/Field.txt).
ORIGEN_DEFECTO = (-38.2999299, -60.3812982)


def celdas_de_renglon(texto):
    """Ancho en celdas de un renglón ya escrito."""
    if not texto:
        return 0
    return len(texto) * GLIFO_ANCHO + (len(texto) - 1) * ESPACIO_LETRA


def rects_del_texto(renglones):
    """Convierte los renglones en rectángulos (cx, cy, ancho, alto, letra, idx).

    Las celdas encendidas contiguas de una misma fila se fusionan en un solo
    rectángulo (run-length). Sin esto, "PARALLEL" solo daría ~130 polígonos de
    una celda cada uno: mismo dibujo, prescripción imposible de leer.
    """
    ancho_bloque = max(celdas_de_renglon(r) for r in renglones)
    alto_bloque = len(renglones) * GLIFO_ALTO + (len(renglones) - 1) * ESPACIO_RENGLON

    rects = []
    idx_letra = 0
    for i_reng, renglon in enumerate(renglones):
        # Centrado horizontal del renglón dentro del bloque.
        x0_reng = (ancho_bloque - celdas_de_renglon(renglon)) // 2
        y0_reng = i_reng * (GLIFO_ALTO + ESPACIO_RENGLON)

        for i_letra, ch in enumerate(renglon):
            glifo = FONT.get(ch.upper())
            if glifo is None:
                raise SystemExit(
                    "No tengo el caracter '%s' en la tipografia 5x7. "
                    "Agregalo al dict FONT o cambia el texto." % ch)

            x0 = x0_reng + i_letra * (GLIFO_ANCHO + ESPACIO_LETRA)
            if ch != " ":
                idx_letra += 1

            for fila, bits in enumerate(glifo):
                col = 0
                while col < len(bits):
                    if bits[col] == "1":
                        largo = 0
                        while col + largo < len(bits) and bits[col + largo] == "1":
                            largo += 1
                        rects.append((x0 + col, y0_reng + fila, largo, 1, ch, idx_letra))
                        col += largo
                    else:
                        col += 1

    return rects, ancho_bloque, alto_bloque


def leer_lote(dir_lote):
    """Devuelve (lat0, lon0, contorno) de un lote de PilotX.

    El StartFix de Field.txt es el origen local; Boundary.txt viene en metros
    (este, norte, rumbo) RELATIVOS a ese origen — misma convención que usa este
    script, así que el contorno se puede comparar directo contra los rectángulos
    del texto sin convertir nada.
    """
    ruta_field = os.path.join(dir_lote, "Field.txt")
    if not os.path.isfile(ruta_field):
        raise SystemExit("No encuentro Field.txt en %s" % dir_lote)

    lat0 = lon0 = None
    lineas = open(ruta_field, encoding="utf-8", errors="replace").read().splitlines()
    for i, l in enumerate(lineas):
        if l.strip() == "StartFix" and i + 1 < len(lineas):
            lat0, lon0 = [float(v) for v in lineas[i + 1].split(",")[:2]]
            break
    if lat0 is None:
        raise SystemExit("Field.txt sin StartFix: %s" % ruta_field)

    contorno = []
    ruta_bnd = os.path.join(dir_lote, "Boundary.txt")
    if os.path.isfile(ruta_bnd):
        for l in open(ruta_bnd, encoding="utf-8", errors="replace").read().splitlines()[3:]:
            l = l.strip()
            if not l or l.startswith("$"):
                continue
            p = l.split(",")
            if len(p) >= 2:
                try:
                    contorno.append((float(p[0]), float(p[1])))
                except ValueError:
                    pass

    return lat0, lon0, contorno


def simplificar(poly, tol_m):
    """Douglas-Peucker sobre el contorno del lote.

    El anillo exterior del fondo lo triangula el mapa con EarClipper, que es
    O(n^3): el while corre hasta n^2+10 veces y adentro AnyPointInside recorre
    el anillo entero. Con los 640 puntos crudos de un Boundary.txt eso son
    ~263 millones de operaciones y el shape tarda SEGUNDOS en aparecer.
    Bajando a unas decenas de puntos es instantáneo.

    El costo es que el borde del fondo se corre hasta `tol_m` respecto del
    lindero real. Con 1 m no cambia nada operativo — las secciones las corta
    el lindero de verdad, no esta zona.
    """
    if tol_m <= 0 or len(poly) < 3:
        return poly

    def dist(p, a, b):
        (px, py), (ax, ay), (bx, by) = p, a, b
        dx, dy = bx - ax, by - ay
        if dx == 0 and dy == 0:
            return math.hypot(px - ax, py - ay)
        t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)
        t = max(0.0, min(1.0, t))
        return math.hypot(px - (ax + t * dx), py - (ay + t * dy))

    def dp(pts):
        if len(pts) < 3:
            return pts
        peor, idx = 0.0, 0
        for i in range(1, len(pts) - 1):
            d = dist(pts[i], pts[0], pts[-1])
            if d > peor:
                peor, idx = d, i
        if peor <= tol_m:
            return [pts[0], pts[-1]]
        return dp(pts[:idx + 1])[:-1] + dp(pts[idx:])

    # Se parte en dos mitades para no depender del punto de arranque del anillo.
    m = len(poly) // 2
    out = dp(poly[:m + 1])[:-1] + dp(poly[m:])
    return out if len(out) >= 3 else poly


def punto_adentro(x, y, poly):
    """Ray casting. Mismo criterio que PrescripcionService.PointInPolygon."""
    adentro = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]
        xj, yj = poly[j]
        if (yi > y) != (yj > y):
            if x < (xj - xi) * (y - yi) / (yj - yi) + xi:
                adentro = not adentro
        j = i
    return adentro


def centroide(poly):
    """Centroide de area (no el promedio de vertices: en un lote con un lado
    lleno de puntos y otro con dos, el promedio se va para el lado denso)."""
    a = cx = cy = 0.0
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % n]
        cross = x0 * y1 - x1 * y0
        a += cross
        cx += (x0 + x1) * cross
        cy += (y0 + y1) * cross
    if abs(a) < 1e-9:
        return (sum(p[0] for p in poly) / n, sum(p[1] for p in poly) / n)
    a *= 0.5
    return (cx / (6 * a), cy / (6 * a))


def entra_todo(rects, ancho_bloque, alto_bloque, celda, cx, cy, contorno):
    """True si las 4 esquinas de cada zona caen adentro del contorno."""
    for (rx, ry, rw, rh, _, _) in rects:
        e0 = cx + (rx - ancho_bloque / 2.0) * celda
        e1 = cx + (rx + rw - ancho_bloque / 2.0) * celda
        n1 = cy + (alto_bloque / 2.0 - ry) * celda
        n0 = cy + (alto_bloque / 2.0 - (ry + rh)) * celda
        for (e, n) in ((e0, n0), (e0, n1), (e1, n0), (e1, n1)):
            if not punto_adentro(e, n, contorno):
                return False
    return True


def _m_por_grado_lat(lat0):
    r = math.radians(lat0)
    return (111132.92
            - 559.82 * math.cos(2.0 * r)
            + 1.175 * math.cos(4.0 * r)
            - 0.0023 * math.cos(6.0 * r))


def _m_por_grado_lon(lat):
    r = math.radians(lat)
    return (111412.84 * math.cos(r)
            - 93.5 * math.cos(3.0 * r)
            + 0.118 * math.cos(5.0 * r))


def a_latlon(este_m, norte_m, lat0, lon0):
    """ENU local → WGS84, con la MISMA serie que usa PilotX.

    Tiene que ser idéntica a LocalPlane.ConvertGeoCoordToWgs84
    (AgOpenGPS.Core/Models/Base/LocalPlane.cs): si acá se usa el clásico
    111320 m/grado plano, las zonas caen 0,3-0,5 m corridas respecto de donde
    PilotX cree que están. Sobre el mapa no se nota; en la sembradora medio
    metro es media máquina de desfase en el borde de la zona.

    Ojo con el orden: los metros por grado de longitud se evalúan con la
    latitud DEL PUNTO, no con la del origen — así lo hace LocalPlane.
    """
    lat = lat0 + norte_m / _m_por_grado_lat(lat0)
    lon = lon0 + este_m / _m_por_grado_lon(lat)
    return lon, lat


def main():
    ap = argparse.ArgumentParser(
        description="Genera una prescripcion de prueba que dibuja un texto.")
    ap.add_argument("--texto", default="AGRO|PARALLEL",
                    help="Renglones separados por '|' (default: AGRO|PARALLEL)")
    ap.add_argument("--celda", type=float, default=12.0,
                    help="Metros por celda de la tipografia (default: 12)")
    ap.add_argument("--origen", default=None,
                    help="lat,lon del centro del bloque (default: lote La Paloma)")
    ap.add_argument("--lote", default=None,
                    help="Carpeta del lote (Field.txt + Boundary.txt). Ubica y "
                         "escala el texto para que entre ADENTRO del contorno; "
                         "ignora --origen y usa --celda solo como maximo.")
    ap.add_argument("--salida", default=None,
                    help="Ruta base sin extension (default: Build/data/prescripciones/<slug>)")
    ap.add_argument("--insumo", default="Semilla",
                    help="Nombre del insumo, va en el nombre de zona")
    ap.add_argument("--tol-contorno", type=float, default=1.0,
                    help="Metros de tolerancia al simplificar el contorno del "
                         "fondo. El mapa lo triangula con un ear-clipping O(n^3): "
                         "640 puntos crudos tardan segundos. 0 = sin simplificar "
                         "(default: 1.0)")
    ap.add_argument("--fondo", type=float, default=40.0,
                    help="Dosis kg/ha del fondo (todo el lote menos las letras), "
                         "para que no queden huecos sin prescripcion. 0 = sin "
                         "fondo. Necesita --lote con Boundary.txt (default: 40)")
    args = ap.parse_args()

    renglones = [r for r in args.texto.split("|") if r != ""]
    if not renglones:
        raise SystemExit("--texto vacio")

    rects, ancho_bloque, alto_bloque = rects_del_texto(renglones)

    # Offset del centro del bloque respecto del origen local del lote, en metros.
    off_e = off_n = 0.0
    contorno = []

    if args.lote:
        lat0, lon0, contorno = leer_lote(args.lote)
        if not contorno:
            print("AVISO: el lote no tiene Boundary.txt; centro en el StartFix "
                  "y no puedo garantizar que entre.")
        else:
            off_e, off_n = centroide(contorno)
            # Busqueda binaria del tamano de celda mas grande que entre entero.
            # Achicar es lo unico que puedo hacer sin deformar las letras: el
            # bloque tiene proporcion fija.
            lo, hi = 0.5, args.celda
            if not entra_todo(rects, ancho_bloque, alto_bloque, lo, off_e, off_n, contorno):
                raise SystemExit(
                    "Ni con celdas de 0,5 m entra el texto en el contorno del lote. "
                    "Probá un texto mas corto o revisa el Boundary.txt.")
            if entra_todo(rects, ancho_bloque, alto_bloque, hi, off_e, off_n, contorno):
                celda_fit = hi
            else:
                for _ in range(40):
                    mid = (lo + hi) / 2.0
                    if entra_todo(rects, ancho_bloque, alto_bloque, mid,
                                  off_e, off_n, contorno):
                        lo = mid
                    else:
                        hi = mid
                celda_fit = lo
            args.celda = celda_fit
    elif args.origen:
        try:
            lat0, lon0 = [float(v) for v in args.origen.split(",")]
        except ValueError:
            raise SystemExit("--origen debe ser 'lat,lon' (ej: -38.2999,-60.3813)")
    else:
        lat0, lon0 = ORIGEN_DEFECTO

    raiz = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if args.salida:
        base = os.path.abspath(args.salida)
    else:
        slug = "-".join(renglones).lower().replace(" ", "-")
        base = os.path.join(raiz, "Build", "data", "prescripciones", slug)
    os.makedirs(os.path.dirname(base), exist_ok=True)

    celda = args.celda
    ancho_m = ancho_bloque * celda
    alto_m = alto_bloque * celda

    features = []
    filas_shp = []

    for (cx, cy, cw, ch_, letra, idx) in rects:
        # Celda (fila 0 arriba) → metros con el bloque centrado en el origen.
        este_izq = off_e + (cx - ancho_bloque / 2.0) * celda
        este_der = off_e + (cx + cw - ancho_bloque / 2.0) * celda
        norte_sup = off_n + (alto_bloque / 2.0 - cy) * celda
        norte_inf = off_n + (alto_bloque / 2.0 - (cy + ch_)) * celda

        si = a_latlon(este_izq, norte_sup, lat0, lon0)   # sup-izq
        sd = a_latlon(este_der, norte_sup, lat0, lon0)   # sup-der
        id_ = a_latlon(este_der, norte_inf, lat0, lon0)  # inf-der
        ii = a_latlon(este_izq, norte_inf, lat0, lon0)   # inf-izq

        dosis = DOSIS_CICLO[(idx - 1) % len(DOSIS_CICLO)] if idx else DOSIS_CICLO[0]
        zona = "%s %d · %s" % (args.insumo, idx, letra)

        # GeoJSON RFC 7946: anillo exterior antihorario.
        anillo_ccw = [list(si), list(ii), list(id_), list(sd), list(si)]
        features.append({
            "type": "Feature",
            "properties": {"dosis": dosis, "zona": zona, "letra": letra},
            "geometry": {"type": "Polygon", "coordinates": [anillo_ccw]},
        })

        # Shapefile: anillo exterior HORARIO (al reves que GeoJSON). Cada fila
        # guarda una LISTA de anillos porque el fondo trae outer + 120 agujeros.
        filas_shp.append(([[list(si), list(sd), list(id_), list(ii), list(si)]],
                          dosis, zona, letra))

    # --- Fondo: el lote entero con las letras caladas ------------------------
    # Va ÚLTIMO y con las zonas como agujeros. Las dos cosas, a propósito:
    #   · agujeros → correcto para QGIS/OrbitX y para PointInPolygon, que da
    #     "adentro" solo si el punto está en el anillo exterior Y en ningún hole.
    #   · último   → GetDose devuelve el PRIMER feature que contiene el punto,
    #     así que aunque un consumidor ignore los agujeros, las letras ganan.
    # Sin esto el 85% del lote queda en dosis 0 y la sembradora cierra.
    if args.fondo > 0:
        if contorno:
            # Se simplifica SOLO para el dibujo del fondo. El chequeo de que el
            # texto entre usa el contorno crudo, que es el lindero de verdad.
            cont_simple = simplificar(contorno, args.tol_contorno)
            anillo_ext = [list(a_latlon(e, n, lat0, lon0)) for (e, n) in cont_simple]
            forma_fondo = ("contorno del lote, %d puntos (de %d, tol %.1f m)"
                           % (len(cont_simple), len(contorno), args.tol_contorno))
        else:
            # Sin Boundary.txt no hay a qué recortar: caja alrededor del texto
            # con un margen de MARGEN_FONDO celdas. Cubre igual todo lo que la
            # prescripción abarca, que es lo que evita el hueco en dosis 0.
            mx = (ancho_bloque / 2.0 + MARGEN_FONDO) * celda
            my = (alto_bloque / 2.0 + MARGEN_FONDO) * celda
            anillo_ext = [list(a_latlon(off_e + ex, off_n + ny, lat0, lon0))
                          for (ex, ny) in ((-mx, my), (mx, my), (mx, -my), (-mx, -my))]
            forma_fondo = "caja %.0f x %.0f m (el lote no tiene contorno)" % (2 * mx, 2 * my)
        if anillo_ext[0] != anillo_ext[-1]:
            anillo_ext.append(list(anillo_ext[0]))

        def area_firmada(anillo):
            s = 0.0
            for i in range(len(anillo) - 1):
                x0, y0 = anillo[i]
                x1, y1 = anillo[i + 1]
                s += x0 * y1 - x1 * y0
            return s / 2.0

        # GeoJSON RFC 7946: exterior antihorario (area > 0), holes horarios.
        ext_ccw = anillo_ext if area_firmada(anillo_ext) > 0 else anillo_ext[::-1]
        # Las letras ya se guardaron antihorarias para el GeoJSON: como agujero
        # van al reves.
        holes_cw = [f["geometry"]["coordinates"][0][::-1] for f in features]

        # PRIMERO, no último. Son dos consumidores con reglas distintas:
        #
        #   · el MAPA (ShapeGeometryPoller) triangula SOLO el anillo exterior y
        #     los agujeros los deja como contorno — o sea que el fondo se pinta
        #     macizo. Dibujando en orden, si va último tapa las letras.
        #   · la DOSIS (PrescripcionService.GetDose) sí respeta agujeros y
        #     devuelve el PRIMER feature que contiene el punto.
        #
        # Con el fondo primero Y con agujeros, los dos quedan bien: el mapa lo
        # pinta abajo y las letras encima, y el lookup se saltea el fondo
        # dentro de cada letra porque ahí el punto cae en un agujero.
        features.insert(0, {
            "type": "Feature",
            "properties": {"dosis": args.fondo,
                           "zona": "%s fondo" % args.insumo,
                           "letra": ""},
            "geometry": {"type": "Polygon",
                         "coordinates": [ext_ccw] + holes_cw},
        })

        # Shapefile: al reves que GeoJSON — exterior horario, holes antihorarios.
        ext_cw = ext_ccw[::-1]
        holes_ccw = [fila[0][0][::-1] for fila in filas_shp]
        filas_shp.insert(0, ([ext_cw] + holes_ccw,
                             args.fondo, "%s fondo" % args.insumo, ""))

    geojson = {
        "type": "FeatureCollection",
        "name": " ".join(renglones),
        "crs": {"type": "name",
                "properties": {"name": "urn:ogc:def:crs:OGC:1.3:CRS84"}},
        "features": features,
    }
    ruta_geojson = base + ".geojson"
    with open(ruta_geojson, "w", encoding="utf-8") as f:
        json.dump(geojson, f, ensure_ascii=False, indent=1)

    # --- Shapefile ------------------------------------------------------------
    import shapefile  # pyshp
    w = shapefile.Writer(base, shapeType=shapefile.POLYGON)
    w.field("dosis", "N", 10, 2)
    w.field("zona", "C", 32)
    w.field("letra", "C", 2)
    for anillos, dosis, zona, letra in filas_shp:
        w.poly(anillos)
        w.record(dosis, zona, letra)
    w.close()

    # ShapefileReader.LooksLikeWgs84 busca "WGS_1984" en el .prj.
    prj = ('GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",'
           'SPHEROID["WGS_1984",6378137.0,298.257223563]],'
           'PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]]')
    with open(base + ".prj", "w", encoding="utf-8") as f:
        f.write(prj)

    ha = (ancho_m * alto_m) / 10000.0
    dosis_usadas = sorted({d for _, d, _, _ in filas_shp})
    print("Texto      : %s" % " / ".join(renglones))
    hay_fondo = args.fondo > 0
    print("Zonas      : %d poligonos%s"
          % (len(features),
             " (%d de letras + 1 fondo con esas %d caladas como agujeros)"
             % (len(rects), len(rects)) if hay_fondo else ""))
    if hay_fondo:
        print("Fondo      : %.0f kg/ha sobre %s" % (args.fondo, forma_fondo))
    print("Celda      : %.1f m  ->  bloque %.0f x %.0f m (%.1f ha de caja)"
          % (celda, ancho_m, alto_m, ha))
    if contorno:
        print("Lote       : contorno de %d puntos; bloque centrado en "
              "(E %.1f, N %.1f) m del StartFix" % (len(contorno), off_e, off_n))
    print("Origen     : %.7f, %.7f (StartFix del lote)" % (lat0, lon0))
    print("Dosis      : %s kg/ha" % ", ".join("%.0f" % d for d in dosis_usadas))
    print("GeoJSON    : %s" % ruta_geojson)
    print("Shapefile  : %s.shp (+ .shx .dbf .prj)" % base)


if __name__ == "__main__":
    main()
