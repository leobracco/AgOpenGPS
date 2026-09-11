# ============================================================================
# rasterizar-iconos-vehiculo.py — pasa los 4 SVG del tipo de vehiculo (handoff
# 2026-08-10) a PNG para el panel nativo de Configuracion (Vehiculo > Tipo).
#
# Avalonia NO dibuja SVG sin paquete extra, y no vale la pena sumar Svg.Skia
# por cuatro iconos: se rasterizan una vez y quedan como AvaloniaResource en
# SourceCode/PilotX.UI/Assets/config/.
#
# Fondo BLANCO a proposito (no transparente): renderPM con bg=None deja el
# fondo NEGRO, no alfa. La card del panel pone la imagen adentro de un recuadro
# blanco, asi que el blanco del PNG no se nota ni siquiera con la card elegida
# (fondo verde claro).
#
# Requisitos: svglib + reportlab (ver memoria reference_rasterizar_svg_windows:
# hay que DESINSTALAR cairocffi o renderPM no arranca).
#
# Uso:  python Tools\rasterizar-iconos-vehiculo.py
# ============================================================================
import os
from svglib.svglib import svg2rlg
from reportlab.graphics import renderPM

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(RAIZ, "SourceCode", "AgroParallel", "Web",
                   "AgroParallel.WebUI", "wwwroot", "img", "config")
DST = os.path.join(RAIZ, "SourceCode", "PilotX.UI", "Assets", "config")
os.makedirs(DST, exist_ok=True)

NOMBRES = ["VehicleTractorRigid", "VehicleTractorArticulated",
           "VehicleHarvester", "VehicleSprayer"]
ESCALA = 4.0  # lienzo 120x90 -> 480x360 (holgado para los 110x72 de pantalla)

for n in NOMBRES:
    d = svg2rlg(os.path.join(SRC, n + ".svg"))
    d.scale(ESCALA, ESCALA)
    d.width *= ESCALA
    d.height *= ESCALA
    salida = os.path.join(DST, n + ".png")
    renderPM.drawToFile(d, salida, fmt="PNG", bg=0xFFFFFF)
    print(salida, os.path.getsize(salida))
