import re, os
HERE = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(HERE, 'datos-dibujos.html'), encoding='utf-8').read()
js = src[src.index('<script>') + len('<script>'):src.rindex('</script>')]

def rep(old, new, count=1):
    global js
    n = js.count(old)
    assert n == count, (old, n)
    js = js.replace(old, new)

# Links a la web -> nada (la pantalla no tiene internet)
rep(' <a href="/herramientas/adaptador-volante.html">Ver qué adaptador lleva</a>.', '')
rep(' <a href="/herramientas/sensor-angulo-rueda.html">Verificar la geometría del varillaje</a>.', '')
rep('en la pantalla se elige rumbo dual.', 'en <span class="ruta">Configuración › GPS / IMU › Rumbo</span> se elige rumbo dual.')
rep("'En los Zanello hay una modificación de cableado específica: consultá la guía de Zanello en Herramientas.'",
    "'En los Zanello hay una modificación de cableado específica: consultala con soporte (<span class=\"ruta\">Configuración › Cloud › Soporte</span>) antes de cablear.'")
# Rutas reales de PilotX en los recuadros "En la pantalla"
TIPO = '`<span class="ruta">Configuración › Vehículo › Tipo</span>: '
rep('`Tipo de vehículo <span class="ruta">Tractor</span>`', TIPO + 'Tractor`', 3)
rep('`Tipo de vehículo <span class="ruta">Articulado</span>`', TIPO + 'Articulado`')
rep('`Tipo de vehículo <span class="ruta">Cosechadora</span>`', TIPO + 'Cosechadora`')
DIM = '<span class="ruta">Vehículo › Dimensiones</span>'
rep("'Entre ejes y trocha medidos con cinta.'", "'Entre ejes y trocha medidos con cinta: " + DIM + ".'")
ANT = '<span class="ruta">Vehículo › Antena</span>'
rep("'Distancia de la antena al <b>eje trasero</b>.'", "'Distancia de la antena al <b>eje trasero</b>: " + ANT + " › Distancia al pivote.'")
rep("'Distancia de la antena al eje trasero.'", "'Distancia de la antena al eje trasero: " + ANT + " › Distancia al pivote.'")
rep("'Distancia de la antena al <b>eje delantero</b>.'", "'Distancia de la antena al <b>eje delantero</b>: " + ANT + " › Distancia al pivote.'")
rep("'<b>Altura de antena</b> medida con cinta, del piso al centro de la antena.'",
    "'<b>Altura de antena</b> medida con cinta, del piso al centro de la antena: " + ANT + ".'")
rep("'Botalón trasero: enganche <b>fijo trasero</b>. Botalón delantero: enganche <b>frontal</b>.'",
    "'Botalón trasero: <span class=\"ruta\">Implemento › Enganche</span> Fijo trasero. Botalón delantero: Frontal.'")
rep("'El cabezal se carga como implemento <b>frontal</b>, con el ancho de corte real.'",
    "'El cabezal va como implemento <b>frontal</b> (PilotX lo pone solo con Cosechadora), con el ancho de corte real.'")
# Colores de la planta: el verde lima de la web no se lee sobre fondo claro
rep('stroke="#A4BA3E" stroke-width="1.5"', 'stroke="#3E8F33" stroke-width="1.5"')
rep('fill="#A4BA3E" font-size="12"', 'fill="#3E8F33" font-size="12"')
rep('fill="#A4BA3E" font-size="13" font-weight="700">×2', 'fill="#3E8F33" font-size="13" font-weight="700">×2')
rep('stroke="#A4BA3E" stroke-dasharray="3 4"', 'stroke="#3E8F33" stroke-dasharray="3 4"', 2)
rep('fill="#7C8896" font-size="12" text-anchor="end">avance', 'fill="#535E54" font-size="12" text-anchor="end">avance', 2)
rep('<line x1="20" y1="${Y}" x2="980" y2="${Y}" stroke="#2E3944"', '<line x1="20" y1="${Y}" x2="980" y2="${Y}" stroke="#9AA59B"')
rep('const piso = \'<line x1="20" y1="392" x2="980" y2="392" stroke="#2E3944"', 'const piso = \'<line x1="20" y1="392" x2="980" y2="392" stroke="#9AA59B"')
rep('stroke="#1A2026" stroke-width="6" stroke-dasharray="2 14"', 'stroke="#C5CFC5" stroke-width="6" stroke-dasharray="2 14"')
# Sin impresora en la cabina
rep("$('imprimir').onclick = () => window.print();\n", '')
rep("// Link directo a una máquina: …/instalacion-pilotx.html#cosechadora o #pulverizadora-2ant",
    "// Link directo a una máquina: instalacion.html#cosechadora o #pulverizadora-2ant.\n// Vehículo › Antena abre con #tractor / #cosechadora / #articulado según el tipo.")

head = open(os.path.join(HERE, 'guia_head.html'), encoding='utf-8').read()
out = head + js + '</script>\n<script src="/js/i18n.js"></script>\n</body>\n</html>\n'
dst = os.path.normpath(os.path.join(HERE, '..', '..', 'SourceCode', 'AgroParallel', 'Web', 'AgroParallel.WebUI', 'wwwroot', 'pages', 'instalacion.html'))
open(dst, 'w', encoding='utf-8', newline='\n').write(out)
print('ok', len(out))
