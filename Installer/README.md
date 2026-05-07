# Instalador AgroParallel Piloto

Instalador Windows para AgOpenGPS + AgIO. Resuelve los problemas habituales al
desplegar en una pantalla nueva (registry stale, carpetas de datos faltantes).

## Pre-requisitos

1. **Inno Setup 6** — https://jrsoftware.org/isdl.php (instalar con defaults)
2. **.NET SDK** que pueda compilar el proyecto (`dotnet build`)

## Como compilar

Desde esta carpeta (`Installer\`), en PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

Esto:
1. Corre `..\build.ps1` (compila AOG + AgIO en Release y los junta en `..\Build\`)
2. Compila `AgroParallelPilot.iss` con Inno Setup
3. Deja el `.exe` final en `Installer\Output\AgroParallel-Piloto-Setup-x.y.z.exe`

Para versionar:

```powershell
.\build-installer.ps1 -Version 1.2.3
```

Para saltear la compilación (si ya está la carpeta `Build/`):

```powershell
.\build-installer.ps1 -SkipBuild
```

## Que hace el instalador

| Paso | Detalle |
|---|---|
| Verifica .NET 4.8 | Lee `HKLM\...\NDP\v4\Full\Release >= 528040` y avisa si falta |
| Copia binarios | `Build\*` → `C:\Program Files\AgroParallel\Piloto\` |
| Crea carpetas de datos | `%USERPROFILE%\Documents\AgOpenGPS\{Fields,Vehicles,Logs,Tools}` (previene `DirectoryNotFoundException`) |
| Acceso directo escritorio | AOG (default), AgIO (opcional) |
| Inicio automatico | Shortcut en `commonstartup` para arrancar AOG con Windows (default ON) |
| Reset registry | Tarea opcional: borra `HKCU\SOFTWARE\AgOpenGPS\workingDirectory` (recomendado en pantalla nueva con instalación previa rota) |
| Desinstalador | Quita binarios pero **conserva** los lotes del usuario en `Documents\AgOpenGPS\` |

## Despliegue silencioso (en pantalla nueva)

```cmd
AgroParallel-Piloto-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS="desktopicon,autostart,resetregistry"
```

Flags útiles de Inno Setup:
- `/VERYSILENT` — sin UI
- `/NORESTART` — no reiniciar al final
- `/DIR="C:\AgroParallel\Piloto"` — cambiar carpeta destino
- `/TASKS="…"` — qué tareas marcar (lista separada por coma)
- `/LOG="install.log"` — guardar log

## Por qué Inno Setup y no WiX/MSI

- Single-file `.exe` que corre sin dependencias.
- Script `.iss` legible (~100 líneas vs 500 de WiX XML).
- Soporta tareas opcionales con checkboxes nativos.
- `[Code]` en Pascal para validaciones (verificar .NET, leer registry).
- Tractor PCs no necesitan deploy MSI corporativo.

## Notas de arquitectura

- **No bundlear Mosquitto.** El broker MQTT (puerto 1883) está embebido en
  AgIO con `MQTTnet`. Al instalar AgIO ya queda el broker disponible para
  los nodos ESP32 en LAN.
