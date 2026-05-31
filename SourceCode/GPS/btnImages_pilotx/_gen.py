# =============================================================================
# Generador de iconos PilotX para la pantalla principal de AOG (64x64 RGBA).
# Paleta:
#   ACCENT  #4ABA3E  — estado activo / acento
#   NEUTRO  #535E54  — outline / icono normal
#   OFF     #9AA29C  — estado off / disabled
#   ALERT   #E25D2E  — peligro / parar (no se usa por defecto)
# Estilo: flat, stroke grueso (~5px), forma clara, sin gradients.
# Uso: python _gen.py  → genera todos los PNG en este directorio.
# =============================================================================
from PIL import Image, ImageDraw, ImageFont
import math, os

SIZE = 64
ACCENT = (74, 186, 62, 255)
NEUTRO = (83, 94, 84, 255)
OFF    = (154, 162, 156, 255)
ALERT  = (226, 93, 46, 255)
WHITE  = (255, 255, 255, 255)
HERE = os.path.dirname(os.path.abspath(__file__))

def canvas():
    return Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))

def save(im, name):
    im.save(os.path.join(HERE, name + '.png'))
    print('  ', name)

# ----- helpers de dibujo -----
def stroke_line(d, p1, p2, color, w=5):
    d.line([p1, p2], fill=color, width=w)
    # capuchones redondos manuales (PIL no soporta line caps directos)
    r = w // 2
    d.ellipse([p1[0]-r, p1[1]-r, p1[0]+r, p1[1]+r], fill=color)
    d.ellipse([p2[0]-r, p2[1]-r, p2[0]+r, p2[1]+r], fill=color)

def stroke_circle(d, cx, cy, r, color, w=4):
    d.ellipse([cx-r, cy-r, cx+r, cy+r], outline=color, width=w)

def stroke_rect(d, box, color, w=4, rounded=4):
    if rounded > 0 and hasattr(d, 'rounded_rectangle'):
        d.rounded_rectangle(box, radius=rounded, outline=color, width=w)
    else:
        d.rectangle(box, outline=color, width=w)

def fill_rect(d, box, color, rounded=4):
    if rounded > 0 and hasattr(d, 'rounded_rectangle'):
        d.rounded_rectangle(box, radius=rounded, fill=color)
    else:
        d.rectangle(box, fill=color)

# =============================================================================
# Iconos
# =============================================================================

def auto_steer(active):
    """Volante. Color verde si activo, gris si off."""
    im = canvas(); d = ImageDraw.Draw(im)
    c = ACCENT if active else OFF
    # volante exterior
    stroke_circle(d, 32, 32, 22, c, w=5)
    # cubo central
    d.ellipse([28, 28, 36, 36], fill=c)
    # 3 radios
    stroke_line(d, (32, 12), (32, 28), c, w=4)
    stroke_line(d, (14, 38), (29, 33), c, w=4)
    stroke_line(d, (50, 38), (35, 33), c, w=4)
    return im

def auto_youturn(active):
    """U-turn flecha curva."""
    im = canvas(); d = ImageDraw.Draw(im)
    c = ACCENT if active else OFF
    # arco superior 180° (forma de U invertida)
    d.arc([16, 14, 48, 46], start=180, end=360, fill=c, width=5)
    # bajadas
    stroke_line(d, (16, 30), (16, 52), c, w=5)
    stroke_line(d, (48, 30), (48, 52), c, w=5)
    # punta de flecha (abajo izq)
    d.polygon([(16, 56), (8, 46), (24, 46)], fill=c)
    return im

def section_master(state):
    """SectionMaster: 5 secciones lado a lado. state in {'off','manual','auto'}."""
    im = canvas(); d = ImageDraw.Draw(im)
    if state == 'auto':
        fill = ACCENT; out = ACCENT
    elif state == 'manual':
        fill = OFF; out = NEUTRO
    else:
        fill = (0,0,0,0); out = OFF
    # 5 secciones
    for i in range(5):
        x0 = 8 + i*10
        if state == 'auto':
            fill_rect(d, [x0, 30, x0+8, 50], ACCENT, rounded=2)
        elif state == 'manual':
            fill_rect(d, [x0, 30, x0+8, 50], NEUTRO, rounded=2)
        else:
            stroke_rect(d, [x0, 30, x0+8, 50], OFF, w=3, rounded=2)
    # barra superior (chasis del implemento)
    fill_rect(d, [4, 22, 60, 28], out, rounded=3)
    # tractor symbol (chico arriba)
    fill_rect(d, [28, 8, 36, 22], out, rounded=2)
    return im

def ab_line_cycle(reverse=False):
    """Ciclo entre líneas AB: 2 líneas paralelas con flecha circular."""
    im = canvas(); d = ImageDraw.Draw(im)
    # 2 líneas paralelas
    stroke_line(d, (18, 12), (18, 52), ACCENT, w=4)
    stroke_line(d, (46, 12), (46, 52), ACCENT, w=4)
    # marcadores A y B
    d.ellipse([14, 8, 22, 16], fill=ACCENT)
    d.ellipse([42, 8, 50, 16], fill=ACCENT)
    # flecha curva (cycle)
    if reverse:
        d.arc([22, 30, 42, 48], start=10, end=170, fill=NEUTRO, width=3)
        d.polygon([(22, 38), (18, 42), (24, 44)], fill=NEUTRO)
    else:
        d.arc([22, 30, 42, 48], start=10, end=170, fill=NEUTRO, width=3)
        d.polygon([(42, 38), (46, 42), (40, 44)], fill=NEUTRO)
    return im

def camera_2d():
    """Camera 2D = vista cenital, ojo desde arriba."""
    im = canvas(); d = ImageDraw.Draw(im)
    # marco
    stroke_rect(d, [8, 14, 56, 50], NEUTRO, w=4, rounded=6)
    # lente cenital (círculo grande)
    stroke_circle(d, 32, 32, 12, ACCENT, w=4)
    d.ellipse([28, 28, 36, 36], fill=ACCENT)
    # texto "2D"
    d.line([(38, 46), (44, 46)], fill=NEUTRO, width=2)  # underscore
    return im

def camera_3d():
    """Camera 3D = lente oblicua con perspectiva."""
    im = canvas(); d = ImageDraw.Draw(im)
    # cuerpo cámara perspectiva
    d.polygon([(10, 22), (50, 14), (54, 46), (14, 54)], outline=NEUTRO, width=4)
    # lente
    stroke_circle(d, 32, 34, 10, ACCENT, w=4)
    return im

def camera_north_2d():
    """Cámara con brújula (N arriba)."""
    im = camera_2d()
    d = ImageDraw.Draw(im)
    # flecha norte
    d.polygon([(32, 4), (28, 14), (36, 14)], fill=ACCENT)
    return im

def grid_rotate():
    """Grilla con flecha de rotación."""
    im = canvas(); d = ImageDraw.Draw(im)
    # grilla 3x3
    for i in range(1, 3):
        stroke_line(d, (12, 12+i*13), (52, 12+i*13), OFF, w=2)
        stroke_line(d, (12+i*13, 12), (12+i*13, 52), OFF, w=2)
    stroke_rect(d, [12, 12, 52, 52], NEUTRO, w=3, rounded=2)
    # flecha rotación
    d.arc([14, 14, 50, 50], start=300, end=60, fill=ACCENT, width=4)
    d.polygon([(46, 12), (54, 14), (50, 22)], fill=ACCENT)
    return im

def brightness(up=True):
    """Sol con + o -."""
    im = canvas(); d = ImageDraw.Draw(im)
    # sol central
    d.ellipse([24, 24, 40, 40], fill=ACCENT)
    # rayos
    for ang in range(0, 360, 45):
        rad = math.radians(ang)
        x1 = 32 + int(14*math.cos(rad)); y1 = 32 + int(14*math.sin(rad))
        x2 = 32 + int(22*math.cos(rad)); y2 = 32 + int(22*math.sin(rad))
        stroke_line(d, (x1,y1), (x2,y2), ACCENT, w=3)
    # signo abajo
    sign = ACCENT
    if up:
        stroke_line(d, (48, 54), (56, 54), sign, w=4)
        stroke_line(d, (52, 50), (52, 58), sign, w=4)
    else:
        stroke_line(d, (48, 54), (56, 54), sign, w=4)
    return im

def tilt(up=True):
    """Plano inclinado con flecha."""
    im = canvas(); d = ImageDraw.Draw(im)
    # piso
    stroke_line(d, (8, 50), (56, 50), OFF, w=4)
    # plano inclinado
    if up:
        d.polygon([(8, 50), (56, 50), (56, 20)], outline=ACCENT, width=4)
        # flecha arriba
        stroke_line(d, (44, 38), (44, 16), ACCENT, w=4)
        d.polygon([(44, 8), (38, 18), (50, 18)], fill=ACCENT)
    else:
        d.polygon([(8, 50), (56, 50), (8, 20)], outline=ACCENT, width=4)
        # flecha abajo
        stroke_line(d, (20, 16), (20, 38), ACCENT, w=4)
        d.polygon([(20, 46), (14, 36), (26, 36)], fill=ACCENT)
    return im

def night_mode():
    """Luna creciente."""
    im = canvas(); d = ImageDraw.Draw(im)
    d.ellipse([12, 12, 52, 52], fill=NEUTRO)
    d.ellipse([22, 8, 60, 46], fill=(0, 0, 0, 0))
    return im

def boundary_record():
    """Círculo punteado con punto rojo (REC)."""
    im = canvas(); d = ImageDraw.Draw(im)
    # contorno punteado (16 arcos)
    for ang in range(0, 360, 22):
        rad1 = math.radians(ang); rad2 = math.radians(ang+11)
        x1 = 32 + int(22*math.cos(rad1)); y1 = 32 + int(22*math.sin(rad1))
        x2 = 32 + int(22*math.cos(rad2)); y2 = 32 + int(22*math.sin(rad2))
        stroke_line(d, (x1,y1), (x2,y2), NEUTRO, w=3)
    # punto REC rojo
    d.ellipse([24, 24, 40, 40], fill=ALERT)
    return im

def boundary_play():
    """Círculo cerrado con play."""
    im = canvas(); d = ImageDraw.Draw(im)
    stroke_circle(d, 32, 32, 22, ACCENT, w=4)
    d.polygon([(26, 22), (44, 32), (26, 42)], fill=ACCENT)
    return im

def path_resume():
    """Punta de avance: triángulo + barra (resume)."""
    im = canvas(); d = ImageDraw.Draw(im)
    fill_rect(d, [12, 18, 18, 46], ACCENT, rounded=2)
    d.polygon([(22, 18), (52, 32), (22, 46)], fill=ACCENT)
    return im

def ab_swap():
    """A↔B con flechas opuestas."""
    im = canvas(); d = ImageDraw.Draw(im)
    # letras A y B simplificadas
    d.polygon([(10, 24), (16, 8), (22, 24)], outline=NEUTRO, width=3)
    stroke_line(d, (12, 18), (20, 18), NEUTRO, w=2)
    stroke_rect(d, [42, 8, 54, 24], NEUTRO, w=3, rounded=2)
    stroke_line(d, (42, 16), (54, 16), NEUTRO, w=2)
    # flechas dobles
    stroke_line(d, (16, 42), (48, 42), ACCENT, w=3)
    stroke_line(d, (16, 50), (48, 50), ACCENT, w=3)
    d.polygon([(50, 38), (58, 42), (50, 46)], fill=ACCENT)
    d.polygon([(14, 46), (6, 50), (14, 54)], fill=ACCENT)
    return im

def reset_tool():
    """Flecha circular completa (refresh)."""
    im = canvas(); d = ImageDraw.Draw(im)
    d.arc([12, 12, 52, 52], start=30, end=330, fill=ACCENT, width=5)
    d.polygon([(46, 8), (58, 16), (44, 22)], fill=ACCENT)
    return im

def flag_red():
    """Banderita."""
    im = canvas(); d = ImageDraw.Draw(im)
    # mástil
    stroke_line(d, (16, 8), (16, 56), NEUTRO, w=4)
    # bandera
    d.polygon([(16, 12), (48, 20), (16, 28)], fill=ALERT)
    return im

def section_mapping():
    """Trayectoria pintada con secciones."""
    im = canvas(); d = ImageDraw.Draw(im)
    # 3 franjas paralelas pintadas
    for i, x in enumerate([10, 26, 42]):
        col = ACCENT if i != 1 else OFF
        fill_rect(d, [x, 12, x+12, 52], col, rounded=2)
    return im

def hydraulic_lift(active):
    """Triángulo arriba (lift)."""
    im = canvas(); d = ImageDraw.Draw(im)
    c = ACCENT if active else OFF
    # base
    fill_rect(d, [8, 48, 56, 56], c, rounded=2)
    # flecha arriba
    d.polygon([(32, 8), (12, 36), (52, 36)], fill=c)
    stroke_line(d, (32, 36), (32, 48), c, w=4)
    return im

def contour():
    """Línea curva siguiendo contorno."""
    im = canvas(); d = ImageDraw.Draw(im)
    # curva sinusoidal
    pts = []
    for x in range(8, 56, 2):
        y = int(32 + 12*math.sin((x-8)/8))
        pts.append((x, y))
    for i in range(len(pts)-1):
        stroke_line(d, pts[i], pts[i+1], ACCENT, w=4)
    return im

def headland():
    """Forma de campo con cabecera punteada."""
    im = canvas(); d = ImageDraw.Draw(im)
    # contorno campo
    stroke_rect(d, [10, 14, 54, 50], NEUTRO, w=4, rounded=6)
    # cabecera punteada (offset interno)
    for x in range(16, 50, 4):
        d.line([(x, 22), (x+2, 22)], fill=ACCENT, width=2)
        d.line([(x, 44), (x+2, 44)], fill=ACCENT, width=2)
    return im

def tram():
    """Líneas guía paralelas (tramlines)."""
    im = canvas(); d = ImageDraw.Draw(im)
    for x in [16, 32, 48]:
        stroke_line(d, (x, 8), (x, 56), ACCENT, w=3)
    return im

def youskip():
    """Saltar pasada: flecha que esquiva una franja."""
    im = canvas(); d = ImageDraw.Draw(im)
    # franjas a saltar
    fill_rect(d, [12, 24, 22, 40], OFF, rounded=2)
    fill_rect(d, [42, 24, 52, 40], OFF, rounded=2)
    # flecha pasando por arriba (saltando)
    d.arc([12, 8, 52, 48], start=180, end=360, fill=ACCENT, width=4)
    d.polygon([(48, 28), (54, 22), (54, 34)], fill=ACCENT)
    return im

def isobus_section():
    """ISO icon: rectángulo con etiqueta 'ISO'."""
    im = canvas(); d = ImageDraw.Draw(im)
    stroke_rect(d, [8, 20, 56, 44], ACCENT, w=4, rounded=6)
    # 3 puntos dentro (sección)
    for x in [20, 32, 44]:
        d.ellipse([x-3, 30, x+3, 36], fill=ACCENT)
    return im

def auto_track():
    """Track + bolita (sigue trayectoria)."""
    im = canvas(); d = ImageDraw.Draw(im)
    # path zig-zag
    stroke_line(d, (10, 50), (24, 20), NEUTRO, w=3)
    stroke_line(d, (24, 20), (40, 44), NEUTRO, w=3)
    stroke_line(d, (40, 44), (54, 16), NEUTRO, w=3)
    # punto
    d.ellipse([18, 14, 30, 26], fill=ACCENT)
    return im

def color_unlocked():
    """Candado abierto sobre paleta de color."""
    im = canvas(); d = ImageDraw.Draw(im)
    # paleta (3 colores)
    fill_rect(d, [8, 28, 22, 56], ACCENT, rounded=2)
    fill_rect(d, [24, 28, 38, 56], NEUTRO, rounded=2)
    fill_rect(d, [40, 28, 54, 56], OFF, rounded=2)
    # candado abierto
    stroke_circle(d, 44, 18, 8, NEUTRO, w=3)
    fill_rect(d, [38, 22, 54, 30], NEUTRO, rounded=2)
    return im

def charge_indicator():
    """Batería."""
    im = canvas(); d = ImageDraw.Draw(im)
    stroke_rect(d, [8, 20, 50, 44], NEUTRO, w=4, rounded=4)
    fill_rect(d, [50, 26, 56, 38], NEUTRO, rounded=2)
    fill_rect(d, [12, 24, 36, 40], ACCENT, rounded=2)  # 70% full
    return im

def gps_quality():
    """Antena con ondas."""
    im = canvas(); d = ImageDraw.Draw(im)
    # antena vertical
    stroke_line(d, (32, 56), (32, 26), NEUTRO, w=4)
    d.ellipse([28, 22, 36, 30], fill=NEUTRO)
    # ondas
    d.arc([18, 14, 46, 42], start=210, end=330, fill=ACCENT, width=3)
    d.arc([10, 6,  54, 50], start=210, end=330, fill=ACCENT, width=3)
    return im

def field_stats():
    """Gráfico de barras."""
    im = canvas(); d = ImageDraw.Draw(im)
    fill_rect(d, [10, 36, 20, 54], NEUTRO, rounded=2)
    fill_rect(d, [24, 24, 34, 54], NEUTRO, rounded=2)
    fill_rect(d, [38, 12, 48, 54], ACCENT, rounded=2)
    # eje
    stroke_line(d, (8, 56), (56, 56), NEUTRO, w=2)
    return im

# =============================================================================
# Generación
# =============================================================================
def main():
    print('Generando iconos PilotX en', HERE)
    save(auto_steer(True),   'AutoSteerOn')
    save(auto_steer(False),  'AutoSteerOff')
    save(auto_youturn(True), 'AutoYouTurnOn')
    save(auto_youturn(False),'AutoYouTurnNo')
    save(section_master('auto'),   'SectionMasterAuto')
    save(section_master('manual'), 'SectionMasterManual')
    save(section_master('off'),    'SectionMasterOff')
    save(ab_line_cycle(False), 'ABLineCycle')
    save(ab_line_cycle(True),  'ABLineCycleBk')
    save(camera_2d(),       'Camera2D64')
    save(camera_3d(),       'Camera3D64')
    save(camera_north_2d(), 'CameraNorth2D')
    save(grid_rotate(),     'GridRotate')
    save(brightness(True),  'BrightnessUp')
    save(brightness(False), 'BrightnessDn')
    save(tilt(True),  'TiltUp')
    save(tilt(False), 'TiltDown')
    save(night_mode(),       'WindowNightMode')
    save(boundary_record(),  'BoundaryRecord')
    save(boundary_play(),    'boundaryPlay')
    save(path_resume(),      'pathResumeStart')
    save(ab_swap(),          'ABSwapPoints')
    save(reset_tool(),       'ResetTool')
    save(flag_red(),         'FlagRed')
    save(section_mapping(),  'SectionMapping')
    save(hydraulic_lift(False), 'HydraulicLiftOff')
    save(contour(),          'ContourOff')
    save(headland(),         'HeadlandOff')
    save(tram(),             'TramOff')
    save(youskip(),          'YouSkipOff')
    save(isobus_section(),   'IsobusSectionControl')
    save(auto_track(),       'AutoTrackOff')
    save(color_unlocked(),   'ColorUnlocked')
    save(charge_indicator(), 'ChargeIndicator')
    save(gps_quality(),      'GPSQuality')
    save(field_stats(),      'FieldStats')
    print('OK')

if __name__ == '__main__':
    main()
