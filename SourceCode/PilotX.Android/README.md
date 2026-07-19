# PilotX.Android — shell Fase 1 (Hub sin guiado)

Bloque 7 de `docs/2026-07-18-android-readiness-matrix.md`. Una Activity con
WebView pantalla completa contra el WebHost embebido (127.0.0.1:5180) y un
Foreground Service que mantiene vivos el **broker MQTT (:1883)** y el
**AgpWebHost** aunque la app quede en background. Los ESP32 de la LAN se
conectan igual que contra la PC (portal del nodo → broker = IP del tablet).

## Qué funciona en Fase 1

- Hub HTML completo (68 páginas) servido desde assets extraídos a `files/wwwroot`.
- Nodos: announcement MQTT, registry, diagnóstico, OTA local (firmwares).
- VistaX/QuantiX/FlowX/StormX/LineX: config + live vía broker propio.
- OrbitX: config + sync cloud (`OrbitXSync` es netstandard).
- Datos de usuario en `getExternalFilesDir()` vía `RegistrySettings.DataRootOverride`.

## Qué NO (stubs Fase 1 — ver `Fase1Stubs.cs`)

- Guiado, lotes, vehículo/implemento nativo, cobertura, shapefile, gráficos:
  las páginas cargan pero muestran datos vacíos (los respalda FormGPS en
  Windows). Llegan con la Fase 2 (bloques 6/9 de la matriz).
- Puente JS nativo (close-hub/postMessage): las páginas guardan el
  `window.chrome.webview` con try/catch, así que degradan solas.

## Limitación conocida (próximo paso)

Varios `*ConfigService` escriben sus JSON en `AppDomain.CurrentDomain.BaseDirectory`,
que en Android **no es escribible**. Hay que introducir un `ConfigRoot`
inyectable (apuntado a `FilesDir`) en `AgroParallel.Services` antes de que
guardar configs funcione en el tablet. Leer configs default y el live MQTT
funcionan igual.

## Build

Requiere workload `android` del SDK 9 (el repo pinnea SDK 9 en `global.json`)
y el Android SDK+JDK. Primera vez:

```powershell
dotnet workload install android
dotnet build SourceCode/PilotX.Android/PilotX.Android.csproj -t:InstallAndroidDependencies `
  -f net9.0-android -p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk `
  -p:JavaSdkDirectory=$env:LOCALAPPDATA\Android\Jdk -p:AcceptAndroidSDKLicenses=True
```

Después:

```powershell
dotnet build SourceCode/PilotX.Android/PilotX.Android.csproj -c Release
# APK en bin/Release/net9.0-android/
# instalar en tablet con depuración USB:
dotnet build SourceCode/PilotX.Android/PilotX.Android.csproj -c Release -t:Install
```
