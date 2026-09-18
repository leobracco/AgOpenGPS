#!/bin/sh
# ============================================================================
# sesion-kiosko.sh — la SESIÓN X del kiosko PilotX. No hay escritorio: esta
# sesión ES PilotX. La lanza LightDM (autologin) vía pilotx-kiosk.desktop.
#
# openbox corre solo como window manager mudo (stacking de teclado, barras y
# diálogos) con el rc.xml pelado de al lado: sin menú de click derecho, sin
# atajos de teclado, sin escritorios virtuales — el operario no tiene a dónde
# escaparse.
#
# Supervisión gratis: pilotx.sh corre con exec — si PilotX muere, muere la
# sesión y LightDM la relanza sola (mismo rol que el supervisor .bat de
# Windows, sin proceso extra).
# ============================================================================

# Pantalla siempre despierta: sin blanking, sin DPMS, sin salvapantallas.
xset s off -dpms s noblank 2>/dev/null

# Cursor oculto tras 1 s de inactividad si unclutter está instalado (táctil).
command -v unclutter >/dev/null && unclutter -idle 1 -root &

openbox --config-file /opt/pilotx/kiosk/rc.xml &

# Modo kiosko: PilotX esconde los botones de ventana (−/☐/✕) y bloquea el
# cierre — el operario no puede cerrar ni minimizar la pantalla.
export PILOTX_KIOSKO=1

exec /opt/pilotx/pilotx.sh
