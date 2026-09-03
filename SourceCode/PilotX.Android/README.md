# PilotX.Android — la misma PilotX, en tablet

Un solo proceso: `MainActivity` (Avalonia, la UI nativa compartida de
`PilotX.UI`: mapa GL + barras + paneles) + `HubForegroundService`, que corre
lo que en Windows corren `PilotX.GuidanceEngine --webhost --corex`: motor de
guiado headless, WebHost `:5180`, broker MQTT `:1883`, bridge UDP LAN
`:9999/:8888`, NTRIP, bridges de productos y sync cloud. `HubActivity`
(WebView contra `:5180`) queda para las pantallas HTML del Hub que todavía no
son nativas.

Estado al 2026-09-03: **paridad funcional con Windows salvo hardware**.

## Regla de mantenimiento (leer antes de tocar)

`HubBootstrap.cs` es el **gemelo** de `EngineWebHost.Start()` +
`CoreXEngineHost.StartServices()`. Los adaptadores `Engine*` NO se copian:
`PilotX.Android.csproj` linkea por archivo `..\PilotX.GuidanceEngine\Adapters\*.cs`
(y `AntiSolapeSecciones.cs`, `SteerConfigService.cs`, `ShapefileLayer.cs`,
`TramLineMapper.cs`). Si en Windows se agrega un servicio al WebHost, hay que
agregarlo también en `HubBootstrap`; si se agrega un adaptador que usa APIs
sólo-Windows, hay que excluirlo en el csproj y escribir el gemelo Android.

Entre julio y septiembre nadie compiló el APK y dos interfaces se le
adelantaron. Para que no vuelva a pasar: **`build-android.ps1` va en cada
release, junto con `build.ps1`.**

## Qué funciona

- Guiado completo (AB, curvas, contorno, cabecera, tramlines, secciones,
  cobertura, anti-solape, lotes, perfiles, config de vehículo/herramienta/
  IMU/dirección) — los mismos adaptadores que Windows.
- Nodos MQTT (VistaX, QuantiX + bridge de motores, FlowX, SectionX/LineX
  corte, StormX), OrbitX sync + prescripciones, alarmas de cabina (audio por
  `MediaPlayer`), self-update por APK (producto OrbitX `PilotXAndroid`).
- GPS/IMU/dirección **por WiFi/UDP**: NMEA crudo (`$GPGGA/$GPVTG/$PANDA`) o
  PGN envuelto a `:9999`, como un CoreX-ECU o un receptor de red. Hello
  PGN 200 a 1 Hz para que los módulos (y ToolX) aprendan la IP de la tablet.
- NTRIP: sección `ntrip` de `corex-integrado.json` en el `FilesDir` de la
  app (mismo esquema que el panel CoreX de Windows); el RTCM sale por UDP
  `:2233` a la subred.
- Plataforma: brillo (ventana siempre; sistema si se autoriza "Modificar
  ajustes del sistema"), WiFi desde el Hub (`wifi.html`), arranque automático
  al bootear (`BootReceiver`), kiosko por lock task si la app es device-owner,
  salir de la app desde Sistema.

## Qué NO (hardware o decisión)

- Puertos serie / USB-OTG (`System.IO.Ports` no existe en Android).
- Cámaras RTSP: falta reproductor nativo (ExoPlayer/LibVLC).
- Apagar/reiniciar la tablet: sólo si PilotX es device-owner
  (`adb shell dpm set-device-owner com.agroparallel.pilotx/.PilotXDeviceAdmin`).
- Conectar a una red WiFi en Android 10+: el sistema pide confirmación una
  vez (sugerencia) y se abre el panel de WiFi como respaldo.
- Shapefile: mismo `EngineShapeService` que Windows (carga desde el Hub).

## Permisos que pide

`INTERNET`, red/WiFi, `FOREGROUND_SERVICE`, `POST_NOTIFICATIONS`,
`REQUEST_INSTALL_PACKAGES` (self-update), `RECEIVE_BOOT_COMPLETED`,
`CHANGE_WIFI_STATE`, `ACCESS_FINE_LOCATION` (escaneo WiFi, concederla en
runtime), `WRITE_SETTINGS` (brillo del sistema, se autoriza desde Ajustes).

## Build

Requiere workload `android` del SDK 9, Android SDK (API 35) y JDK 17 en
`%LOCALAPPDATA%\Android\{Sdk,Jdk}` (o `-AndroidSdk`/`-JavaSdk`). Primera vez:

```powershell
dotnet workload install android
dotnet build SourceCode/PilotX.Android/PilotX.Android.csproj -t:InstallAndroidDependencies `
  -f net9.0-android -p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk `
  -p:JavaSdkDirectory=$env:LOCALAPPDATA\Android\Jdk -p:AcceptAndroidSDKLicenses=True
```

Release (desde la raíz del repo):

```powershell
.\build-android.ps1            # PilotX_android_v<VERSION>.apk + .sha256 en la raíz
.\build-android.ps1 -Install   # además lo instala en la tablet por USB (adb)
```

La versión sale de `Installer/VERSION` (la misma que el ZIP de Windows);
`versionCode = MAJOR*10000 + MINOR*100 + PATCH`.
