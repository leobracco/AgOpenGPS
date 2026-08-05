# =============================================================================
#  generar-iconos-cockpit.py — Aplica el handoff de diseño de los íconos de la
#  barra de control de dirección y dosis.
#
#  FUENTE (no editar acá los dibujos):
#     Diseños/design_handoff_iconos_agro_parallel/icons/*.svg
#
#  Rasteriza esos SVG a los MISMOS nombres de PNG que ya bindea
#  BarraDerechaViewModel, así que aplicar el diseño no toca ni una línea de
#  XAML ni de C#. Si el diseño cambia, se pisan los SVG del handoff y se
#  vuelve a correr esto.
#
#  Trazo: el handoff viene en stroke-width 2.4 (pensado para 48 px) y su propia
#  spec dice "subir a 3 cuando se dibuja a 30px o menos". En el overlay los
#  íconos se muestran a 26 px, así que por defecto se sube a 3.0 — es seguir la
#  spec, no apartarse de ella. Con --trazo se fuerza otro valor.
#
#  Uso:
#    python Tools/generar-iconos-cockpit.py            (aplica a Assets/)
#    python Tools/generar-iconos-cockpit.py --hoja     (solo hoja de prueba)
#    python Tools/generar-iconos-cockpit.py --trazo 2.4
# =============================================================================

import argparse
import os
import re

# SVG del handoff  ->  PNG que ya bindea el ViewModel.
#
# AutoSteer*SnapToPivot NO viene en el handoff: el diseño entregó un solo par
# on/off para el piloto. Se apuntan al mismo dibujo para que el binding no
# quede sin imagen; el sub-estado snap-to-pivot queda sin distinguir
# visualmente hasta que diseño mande esas dos piezas.
MAPA = {
    "izquierda":            "barra-abajo/SnapLeft.png",
    "centrar":              "barra-abajo/SnapToPivot.png",
    "derecha":              "barra-abajo/SnapRight.png",

    "autosteer-off":        "barra-derecha/AutoSteerOff.png",
    "autosteer-on":         "barra-derecha/AutoSteerOn.png",
    "autosteer-off@pivot":  "barra-derecha/AutoSteerOffSnapToPivot.png",
    "autosteer-on@pivot":   "barra-derecha/AutoSteerOnSnapToPivot.png",

    "youturn-off":          "barra-derecha/YouTurnNo.png",
    "youturn-on":           "barra-derecha/YouTurn80.png",

    "secciones-manual-off": "barra-derecha/ManualOff.png",
    "secciones-manual-on":  "barra-derecha/ManualOn.png",

    "secciones-auto-off":   "barra-derecha/SectionMasterOff.png",
    "secciones-auto-on":    "barra-derecha/SectionMasterOn.png",

    # ---- Barra vertical derecha (2do handoff) --------------------------------
    "contorno-off":         "barra-derecha/ContourOff.png",
    "contorno-on":          "barra-derecha/ContourOn.png",
    "linea-siguiente":      "barra-derecha/ABLineCycle.png",
    "linea-anterior":       "barra-derecha/ABLineCycleBk.png",
    "autotrack-off":        "barra-derecha/AutoTrackOff.png",
    "autotrack-on":         "barra-derecha/AutoTrack.png",
    "cabecera-off":         "barra-abajo/HeadlandOff.png",
    "cabecera-on":          "barra-abajo/HeadlandOn.png",
    "enderezar-implemento": "barra-abajo/ResetTool.png",

    # Candado del contorno. El set grande trae los DOS estados dibujados, así
    # que se fue la derivación por color que había acá. `fijar-linea.svg` es
    # otra cosa (círculo con flecha sobre la línea) y NO va en este botón.
    "color-unlocked":       "barra-derecha/ColorUnlocked.png",
    "color-locked":         "barra-derecha/ColorLocked.png",

    # Tram / huellas: 4 modos de dibujo. `huellas-off` es el apagado y los tres
    # `tram-*` son cada modo (TramDisplayMode 1=All, 2=Lines, 3=Outer).
    "huellas-off":          "barra-abajo/TramOff.png",
    "tram-multi":           "barra-abajo/TramAll.png",
    "tram-lines":           "barra-abajo/TramLines.png",
    "tram-outer":           "barra-abajo/TramOuter.png",

    # Bandera: el color ES el estado. `marca` es la roja; el ámbar (#D89A2B)
    # aparece en los tokens de este set, que era lo que faltaba antes.
    "marca":                "barra-abajo/FlagRed.png",
    "flag-yellow":          "barra-abajo/FlagYel.png",
    "flag-green":           "barra-abajo/FlagGrn.png",

    "isobus-section-off":   "barra-derecha/IsobusSectionControlOff.png",
    "isobus-section-on":    "barra-derecha/IsobusSectionControlOn.png",

    "hydraulic-lift-off":   "barra-abajo/HydraulicLiftOff.png",
    "hydraulic-lift-on":    "barra-abajo/HydraulicLiftOn.png",

    # Corte de secciones por cabecera: cruz roja / tilde verde sobre la barra.
    "section-off-boundary": "barra-abajo/HeadlandSectionOff.png",
    "section-on-boundary":  "barra-abajo/HeadlandSectionOn.png",

    # Salto de pasada. Faltan los "ya trabajadas" (3er estado), ver SIN_APLICAR.
    "salto-off":            "barra-abajo/YouSkipOff.png",
    "salto-on":             "barra-abajo/YouSkipOn.png",

    # Botón "Guías" (abre el selector de líneas). Elección mía: el handoff no
    # dice a qué botón va cada archivo y `track-line` es el más literal.
    "track-line":           "barra-abajo/TrackOn.png",

    # ---- Menú izquierdo (MenuIzquierda, íconos a 24 px) ----------------------
    # El set de 197 traía diseño para casi todo el menú y nunca se había
    # aplicado: seguía con los PNG heredados de AOG. Mapeo por FUNCIÓN del
    # botón (verificada contra el CommandParameter y el label del axaml), no
    # por parecido de nombre — dos trampas reales: AutoManualIsAuto.png es el
    # botón "Gráfico XTE" (→ chart), y Headache.png es "Cabecera (Build)"
    # avanzada (→ headland-menu), no el toggle de cabecera.
    "ab-smooth":            "menu/ABSmooth.png",
    "ab-track-ab":          "menu/ABTracks.png",
    "chart":                "menu/AutoManualIsAuto.png",
    "autosteer-config":     "menu/AutoSteerConf.png",
    "autosteer-on@menu":    "menu/AutoSteerOn.png",
    "boundary":             "menu/Boundary.png",
    "brightness-down":      "menu/BrightnessDn.png",
    "brightness-up":        "menu/BrightnessUp.png",
    "field-tools":          "menu/FieldTools.png",
    "file-new":             "menu/FileNew.png",
    "file-open":            "menu/FileOpen.png",
    "marca@menu":           "menu/FlagRed.png",
    "headland-menu":        "menu/Headache.png",
    "headland-build":       "menu/HeadlandBuild.png",
    # El riel "LOTE" usaba job-active (tilde verde): decía "OK", no "lote".
    # boundary-outer son dos cuadrados anidados = la parcela con su lindero —
    # familia del "boundary" simple que usa el botón Lindero de adentro, y esa
    # familiaridad es correcta: un lote ES su lindero. (El archivo se sigue
    # llamando JobActive.png porque así lo bindea el axaml; renombrar el asset
    # es cambio de XAML y va aparte si molesta.)
    "boundary-outer":       "menu/JobActive.png",
    "navigation-settings":  "menu/NavigationSettings.png",
    "rec-path":             "menu/RecPath.png",
    # `settings` del set ES un sol de rayos: va al riel "Pantalla" (ex
    # Navegación: día/noche + brillo), donde el sol dice exactamente eso.
    # Configuración usa los sliders de EXTRAS.
    "settings":             "menu/Pantalla.png",
    # special-functions (cruceta+plus) quedó desplazado del riel Herramientas
    # por la caja de EXTRAS; no se mapea a ningún destino por ahora.
    "switch-off":           "menu/SwitchOff.png",
    "tram-lines@menu":      "menu/TramAll.png",
    "tram-multi@menu":      "menu/TramMulti.png",
    # Dos botones destructivos distintos, un solo dibujo de tacho en el set.
    # Acá compartirlo NO esconde estado (son botones separados con label
    # propio), a diferencia de los toggles de SIN_APLICAR.
    "trash@applied":        "menu/TrashApplied.png",
    "trash@contour":        "menu/TrashContourRef.png",
    "webcam":               "menu/Webcam.png",
    "window-night-mode":    "menu/WindowNightMode.png",
    "youturn-reverse":      "menu/YouTurnReverse.png",
}

# Sustituciones de color por variante, para derivar un estado que el handoff no
# mandó — solo si el color sale de la paleta documentada. Vacío: el set grande
# trae todos los estados dibujados y no hay que derivar nada.
SUSTITUCIONES = {}

# SVG propios en el LENGUAJE del handoff (48x48, trazo 2.4, #535E54, acento
# #4ABA3E, puntas redondas) para glifos que el set no trae. PROVISORIOS: si
# diseño manda el suyo, entra al MAPA y esto se borra.
#
# ajustes-sliders: el set no tiene engranaje clásico — su `settings` es un sol
# de rayos (quedó para "Pantalla", donde el sol es correcto: brillo/día/noche)
# y el engranaje chico de autosteer-config agrandado vuelve a ser un sol.
# Sliders = metáfora universal de ajustes, inconfundible a 24 px.
EXTRAS = {
    "ajustes-sliders": (
        "menu/Settings48.png",
        '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" '
        'viewBox="0 0 48 48" fill="none" stroke="#535E54" stroke-width="2.4" '
        'stroke-linecap="round" stroke-linejoin="round">'
        '<path d="M8 14 H40"/><circle cx="29" cy="14" r="4" fill="#F5F7F4"/>'
        '<path d="M8 24 H40" stroke="#4ABA3E"/>'
        '<circle cx="17" cy="24" r="4" stroke="#4ABA3E" fill="#F5F7F4"/>'
        '<path d="M8 34 H40"/><circle cx="33" cy="34" r="4" fill="#F5F7F4"/>'
        '</svg>'),

    # caja-herramientas: el riel "Herramientas" usaba special-functions
    # (cruceta con un +) que no dice "herramientas". El set tampoco trae caja
    # (hay martillo suelto, carpeta y varita). Caja clásica: cuerpo, tapa,
    # manija en arco (acento verde) y traba central.
    "caja-herramientas": (
        "menu/SpecialFunctions.png",
        '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" '
        'viewBox="0 0 48 48" fill="none" stroke="#535E54" stroke-width="2.4" '
        'stroke-linecap="round" stroke-linejoin="round">'
        '<rect x="8" y="19" width="32" height="19" rx="2.5"/>'
        '<path d="M8 27 H40"/>'
        '<path d="M18 19 v-3 a6 5 0 0 1 12 0 v3" stroke="#4ABA3E"/>'
        '<path d="M21.5 27 v-3.5 h5 V27" stroke="#4ABA3E"/>'
        '</svg>'),
}

# Íconos del handoff que NO se aplican todavía, y por qué. Se listan acá y no en
# MAPA a propósito: aplicarlos ESCONDERÍA información que el operario hoy ve.
#
# El ViewModel elige la imagen según el estado; si varios estados apuntan al
# mismo dibujo, el estado deja de leerse en pantalla. Mejor un ícono viejo que
# uno lindo que miente.
SIN_APLICAR = {
    "(salto 3er estado)": ("YouSkipWorkedTracks",
                           "el boton cicla 3 estados y el set trae 2 (off/on). Sin "
                           "icono para 'saltear las YA TRABAJADAS', ese estado queda "
                           "con el bitmap viejo: mapearlo al de 'on' lo volveria "
                           "indistinguible"),
}

# Botones que el handoff no cubre.
SIN_DISENO = [
    ("Piloto snap-to-pivot", "AutoSteerOffSnapToPivot / AutoSteerOnSnapToPivot"),
    # Menú izquierdo — quedan con el PNG heredado hasta que diseño los mande:
    ("CoreX (logo de producto)", "menu/CoreX.png"),
    ("Fuente de rumbo",          "menu/ConS_SourcesHeading.png"),
    ("Fuente de rolido",         "menu/ConS_SourcesRoll.png"),
]

# Orden de la hoja de prueba: como salen en la barra.
ORDEN = ["izquierda", "centrar", "derecha",
         "autosteer-off", "autosteer-on",
         "youturn-off", "youturn-on",
         "secciones-manual-off", "secciones-manual-on",
         "secciones-auto-off", "secciones-auto-on"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--px", type=int, default=96,
                    help="Lado del PNG (default: 96; se muestra a 26)")
    ap.add_argument("--trazo", type=float, default=3.0,
                    help="stroke-width a aplicar (default: 3.0, spec para <=30px)")
    ap.add_argument("--hoja", action="store_true",
                    help="Solo hoja de prueba, no pisa los PNG de Assets/")
    args = ap.parse_args()

    raiz = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    # Set COMPLETO (197 archivos): es el más nuevo y manda. Ojo, no es un
    # superset byte a byte de las entregas anteriores — `cabecera-*` y
    # `enderezar-implemento` están REDIBUJADOS acá (el lote pasó de rectángulo a
    # forma orgánica). Si algún día hay que volver atrás, la entrega vieja sigue
    # en Diseños/Iconos estilo Agro Parallel+More/.
    origen = os.path.join(raiz, "Diseños",
                          "design_handoff_iconos_agro_parallel", "icons")
    assets = os.path.join(raiz, "SourceCode", "PilotX.Cockpit.Bars", "Assets")
    tmp = os.path.join(raiz, "SourceCode", "PilotX.Cockpit.Bars", "Assets", "_handoff")
    os.makedirs(tmp, exist_ok=True)

    if not os.path.isdir(origen):
        raise SystemExit("No encuentro el handoff en %s" % origen)

    from svglib.svglib import svg2rlg
    from reportlab.graphics import renderPM
    from PIL import Image

    # EXTRAS: mismos pasos que el MAPA pero el SVG viene inline, no del handoff.
    for key, (destino, svg_inline) in EXTRAS.items():
        ruta_svg = os.path.join(tmp, key + ".svg")
        open(ruta_svg, "w", encoding="utf-8").write(svg_inline)

    hechos, faltan = [], []
    entradas = list(MAPA.items()) + [(k, d) for k, (d, _) in EXTRAS.items()]
    for key, destino in entradas:
        base = key.split("@")[0]
        # Los EXTRAS ya quedaron escritos en tmp; el resto sale del handoff.
        ruta_svg = os.path.join(tmp if base in EXTRAS else origen, base + ".svg")
        if not os.path.isfile(ruta_svg):
            faltan.append(base + ".svg")
            continue

        svg = open(ruta_svg, encoding="utf-8").read()
        for viejo, nuevo in SUSTITUCIONES.get(key, {}).items():
            svg = svg.replace(viejo, nuevo)
        if args.trazo > 0:
            svg = re.sub(r'stroke-width="[\d.]+"',
                         'stroke-width="%.1f"' % args.trazo, svg)

        ruta_tmp = os.path.join(tmp, key.replace("@", "-") + ".svg")
        open(ruta_tmp, "w", encoding="utf-8").write(svg)

        dibujo = svg2rlg(ruta_tmp)
        if dibujo is None:
            faltan.append(base + ".svg (no parseo)")
            continue
        esc = args.px / 48.0          # el handoff es viewBox 48x48
        dibujo.width = dibujo.height = args.px
        dibujo.scale(esc, esc)
        png_tmp = os.path.join(tmp, key.replace("@", "-") + ".png")
        renderPM.drawToFile(dibujo, png_tmp, fmt="PNG", bg=0xFFFFFF)

        # Blanco -> transparente: renderPM no escribe alfa, y sobre el fondo
        # del botón un cuadrado blanco se nota.
        im = Image.open(png_tmp).convert("RGBA")
        pix = im.load()
        for y in range(im.height):
            for x in range(im.width):
                r, g, b, a = pix[x, y]
                if r > 246 and g > 246 and b > 246:
                    pix[x, y] = (r, g, b, 0)
        im.save(png_tmp)

        if not args.hoja:
            ruta_png = os.path.join(assets, destino.replace("/", os.sep))
            os.makedirs(os.path.dirname(ruta_png), exist_ok=True)
            im.save(ruta_png)
            hechos.append((key, destino))

    print("Handoff    : %s" % origen)
    print("Trazo      : %.1f (el handoff trae 2.4; su spec pide 3.0 a <=30px)" % args.trazo)
    if faltan:
        print("FALTAN     : %s" % ", ".join(faltan))
    if hechos:
        print("PNG        : %d aplicados" % len(hechos))
        for k, d in hechos:
            print("             %-22s -> %s" % (k, d))
    else:
        print("PNG        : (modo --hoja, no se toco Assets/)")

    print("")
    print("NO aplicados (aplicarlos esconderia estado que hoy se ve):")
    for k, (destino, motivo) in SIN_APLICAR.items():
        print("   %-14s -> %-44s %s" % (k, destino, motivo))
    print("")
    print("Sin diseno todavia (siguen con el icono viejo de AgOpenGPS):")
    for nombre, destino in SIN_DISENO:
        print("   %-14s    %s" % (nombre, destino))


if __name__ == "__main__":
    main()
