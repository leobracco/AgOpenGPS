#!/bin/sh
# ============================================================================
# pilotx.sh — lanzador PilotX para Linux (equivalente de Lanzar-PilotX.bat).
# Doble clic / autostart y listo: levanta el Engine headless (broker MQTT
# :1883, API :5180, panel :5181) minimizado y después la pantalla Avalonia.
# ============================================================================
cd "$(dirname "$0")"

mkdir -p logs
chmod +x Engine/PilotX.GuidanceEngine Desktop/PilotX.Desktop \
         BarsHost/PilotX.Bars.Host AgroParallel.Updater 2>/dev/null

if ! pgrep -f "PilotX.GuidanceEngine" >/dev/null 2>&1; then
    nohup ./Engine/PilotX.GuidanceEngine --webhost --corex \
        >logs/engine.log 2>&1 &
    sleep 6
fi

exec ./Desktop/PilotX.Desktop 2>logs/desktop-stderr.log
