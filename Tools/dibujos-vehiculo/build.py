# Genera los dibujos Px*.png de Configuración › Vehículo (Tipo, Dimensiones,
# Antena) con Chrome headless, a partir de los mismos datos de dibujo que la
# guía "Dónde va cada pieza" (datos-dibujos.html, copia de
# agroparallel.com/herramientas/instalacion-pilotx.html con el capó del rígido
# acortado). Las cotas, signos y colores salen de render.js.
#   python build.py      -> PNG a PilotX.UI/Assets/config
#   python gen_guia.py   -> wwwroot/pages/instalacion.html
import os, subprocess
HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, 'datos-dibujos.html'), encoding='utf-8').read()
js = src[src.index('<script>') + len('<script>'):src.index('/* ── Estado y render')]
html = ('<!DOCTYPE html><html><head><meta charset="utf-8"><style>html,body{margin:0;background:transparent;overflow:hidden}</style></head><body>'
        '<script>' + js + '</script><script src="render.js"></script></body></html>')
open(os.path.join(HERE, 'gen.html'), 'w', encoding='utf-8').write(html)  # temporal, se borra al final

CH = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
# Salida directa a los assets de la Configuración nativa.
OUT = os.path.normpath(os.path.join(HERE, '..', '..', 'SourceCode', 'PilotX.UI', 'Assets', 'config'))
os.makedirs(OUT, exist_ok=True)
JOBS = [
    ('tipo', 'tractor', 'PxTipoRigido', 480, 210),
    ('tipo', 'articulado', 'PxTipoArticulado', 480, 210),
    ('tipo', 'cosechadora', 'PxTipoCosechadora', 480, 210),
    ('tipo', 'pulverizadora', 'PxTipoPulverizadora', 480, 210),
    ('antena', 'tractor', 'PxAntenaTractor', 760, 380),
    ('antena', 'articulado', 'PxAntenaArticulado', 760, 380),
    ('antena', 'cosechadora', 'PxAntenaCosechadora', 760, 380),
    ('offset', 'tractor', 'PxOffsetTractor', 640, 340),
    ('offset', 'articulado', 'PxOffsetArticulado', 640, 340),
    ('offset', 'cosechadora', 'PxOffsetCosechadora', 640, 340),
    ('dim', 'tractor', 'PxDimTractor', 760, 360),
    ('dim', 'articulado', 'PxDimArticulado', 760, 360),
    ('dim', 'cosechadora', 'PxDimCosechadora', 760, 360),
]
url = 'file:///' + os.path.join(HERE, 'gen.html').replace('\\', '/')
for v, m, nombre, w, h in JOBS:
    png = os.path.join(OUT, nombre + '.png')
    subprocess.run([CH, '--headless=new', '--disable-gpu', '--hide-scrollbars',
                    '--default-background-color=00000000', '--force-device-scale-factor=1',
                    f'--window-size={w},{h}', f'--screenshot={png}', f'{url}?v={v}&m={m}'],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
    print(nombre, os.path.exists(png))

os.remove(os.path.join(HERE, 'gen.html'))
