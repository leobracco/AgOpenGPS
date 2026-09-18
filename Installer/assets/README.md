# Installer/assets

Carpeta para binarios externos que se bundlean con el installer pero **no** se
commitean al repo.

## ffmpeg.exe

Necesario para el módulo de Cámaras (push RTSP a MediaMTX). Para bajarlo:

```powershell
cd Installer
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\download-ffmpeg.ps1
```

Esto baja el build "essentials" de gyan.dev, verifica SHA256 contra el sidecar
oficial y extrae solo `ffmpeg.exe` (~85 MB).

## Antes de compilar el .iss

Asegurate de tener `ffmpeg.exe` en esta carpeta. El installer lo copia a
`{app}\ffmpeg.exe`. Si no está, Inno Setup falla con "File not found".

## CI / build reproducible

`build-installer.ps1` ejecuta `download-ffmpeg.ps1` automáticamente si el
binario falta. No hace falta tenerlo a mano la primera vez.
