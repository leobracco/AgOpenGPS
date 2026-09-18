# PilotX en Linux

Paquete `linux-x64` del stack PilotX (Desktop + Engine + BarsHost), mismo
layout que la instalación Windows:

```
PilotX/
├── pilotx.sh              ← lanzador (Engine + pantalla)
├── Desktop/PilotX.Desktop
├── Engine/PilotX.GuidanceEngine   (CoreX embebido: MQTT :1883, API :5180, panel :5181)
├── BarsHost/PilotX.Bars.Host
└── AgroParallel/wwwroot   ← páginas del Hub
```

## Dependencias del sistema

| Para qué | Paquete (Debian/Ubuntu) | ¿Obligatorio? |
|---|---|---|
| Pantallas HTML del Hub (WebView) | `libwebkit2gtk-4.1-0` (o `libwebkit2gtk-4.0-37`) | Sí, para las páginas web |
| Teclado en pantalla sobre campos nativos | `xdotool` | Recomendado |
| Alarmas sonoras | `pulseaudio-utils` (paplay) o `alsa-utils` (aplay) | Recomendado |
| Brillo desde la UI | `brightnessctl` (panel integrado) y/o `ddcutil` (monitor externo) | Opcional |
| GPS/módulos por puerto serie | usuario en grupo `dialout` | Sí, si hay serie |

```sh
sudo apt install libwebkit2gtk-4.1-0 xdotool pulseaudio-utils brightnessctl
sudo usermod -aG dialout,video $USER    # serie + brillo sysfs
```

## Primer arranque

```sh
chmod +x pilotx.sh
./pilotx.sh
```

## Autostart (modo cabina)

Equivalente del PilotX-KioskSetup de Windows — sesión gráfica que arranca
PilotX sola (autostart XDG):

```sh
mkdir -p ~/.config/autostart
cat > ~/.config/autostart/pilotx.desktop <<EOF
[Desktop Entry]
Type=Application
Name=PilotX
Exec=/ruta/a/PilotX/pilotx.sh
X-GNOME-Autostart-enabled=true
EOF
```

## Banco en VirtualBox

Para probar el kiosko en una VM antes de la pantalla real:

- Instalación desatendida: `kiosk/autoinstall-pilotx.yaml` sobre el ISO de
  **Ubuntu Server** (el de escritorio ignora el autoinstall y bootea a la
  sesión live).
- **Gráfica: VBoxSVGA, no VMSVGA.** Con VMSVGA la VM se cuelga en "Begin:
  Loading essential drivers" apenas se instala Xorg (el initramfs carga
  `vmwgfx` y no arranca). Con `VBoxManage modifyvm <vm> --graphicscontroller
  vboxsvga` bootea normal (medido 2026-08-15).
- La ventana "separate" de VirtualBox no es necesaria: conviene `--type
  headless` y entrar por SSH (port-forward), así el guest no depende del
  visor.

## Notas

- El broker MQTT vive en el Engine (`--corex`) — NO instalar Mosquitto.
- RustDesk: si el paquete trae `RustDesk/` (cliente con server/clave en el
  nombre + `clave.txt`), PilotX lo levanta solo; el `.deb` pide autorización
  (pkexec) una única vez.
- Apagar/reiniciar desde la UI usa `systemctl` (puede pedir polkit según la
  distro).
