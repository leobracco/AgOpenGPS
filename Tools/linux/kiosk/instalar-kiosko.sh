#!/bin/bash
# ============================================================================
# instalar-kiosko.sh — deja una pantalla Linux en MODO KIOSKO PilotX.
# Equivalente del PilotX-KioskSetup.exe de Windows.
#
#   sudo bash instalar-kiosko.sh [/ruta/a/PilotX_linux_vX.tar.gz]
#
# Resultado: la máquina bootea directo a PilotX fullscreen. Sin escritorio,
# sin paneles, sin menú, sin atajos; si PilotX muere, la sesión se relanza
# sola (LightDM). Deshacer: rm /etc/lightdm/lightdm.conf.d/60-pilotx-kiosk.conf
# y reiniciar.
#
# Probado en Ubuntu/Xubuntu 24.04 (LightDM). Base esperada: instalación
# mínima con LightDM; si hay GDM, instalar lightdm y elegirlo como default.
# ============================================================================
set -e

[ "$(id -u)" = 0 ] || { echo "Correr con sudo"; exit 1; }
TARBALL="$1"

echo "== dependencias =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
    lightdm openbox unclutter xdotool x11-xserver-utils \
    pulseaudio-utils libwebkit2gtk-4.1-0 brightnessctl

echo "== usuario pilotx =="
id pilotx >/dev/null 2>&1 || useradd -m -s /bin/bash pilotx
usermod -aG dialout,video,audio pilotx

echo "== PilotX en /opt/pilotx =="
mkdir -p /opt/pilotx
if [ -n "$TARBALL" ] && [ -f "$TARBALL" ]; then
    tar -xzf "$TARBALL" -C /opt/pilotx
fi
mkdir -p /opt/pilotx/kiosk /opt/pilotx/logs
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"
cp "$SRC_DIR/sesion-kiosko.sh" /opt/pilotx/sesion-kiosko.sh
cp "$SRC_DIR/rc.xml" /opt/pilotx/kiosk/rc.xml
chmod +x /opt/pilotx/sesion-kiosko.sh /opt/pilotx/pilotx.sh 2>/dev/null || true
chown -R pilotx:pilotx /opt/pilotx

echo "== sesión X del kiosko =="
cat > /usr/share/xsessions/pilotx-kiosk.desktop <<'EOF'
[Desktop Entry]
Name=PilotX Kiosko
Exec=/opt/pilotx/sesion-kiosko.sh
Type=Application
EOF

echo "== autologin LightDM =="
mkdir -p /etc/lightdm/lightdm.conf.d
cat > /etc/lightdm/lightdm.conf.d/60-pilotx-kiosk.conf <<'EOF'
[Seat:*]
autologin-user=pilotx
autologin-user-timeout=0
autologin-session=pilotx-kiosk
user-session=pilotx-kiosk
EOF

echo "== blindaje de consola (sin cambio de TTY ni zap) =="
mkdir -p /etc/X11/xorg.conf.d
cat > /etc/X11/xorg.conf.d/60-pilotx-kiosk.conf <<'EOF'
Section "ServerFlags"
    Option "DontVTSwitch" "true"
    Option "DontZap" "true"
EndSection
EOF

echo
echo "Listo. Reiniciar: la pantalla bootea directo a PilotX."
echo "Deshacer: rm /etc/lightdm/lightdm.conf.d/60-pilotx-kiosk.conf && reboot"
