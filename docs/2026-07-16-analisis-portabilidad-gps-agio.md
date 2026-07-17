# Análisis de portabilidad — GPS (PilotX) + AgIO (CoreX)

**Fecha:** 2026-07-16
**Objetivo:** relevar archivo por archivo qué hace cada .cs y qué falta para poder compilar en Android / Linux / iOS.
**Alcance:** solo nuestro código (`SourceCode/GPS` y `SourceCode/AgIO`). Sin referencia a proyectos externos.

## Criterio de veredicto

| Veredicto | Significado |
|---|---|
| **PORTABLE** | Lógica pura, compila en .NET multiplataforma tal cual o casi. |
| **ADAPTABLE** | Lógica de dominio valiosa acoplada a WinForms/Drawing/Settings/FormGPS — hay que abstraerla, pero se rescata. |
| **REESCRIBIR** | Esencialmente UI WinForms o dependiente de Windows (Registry, WMI, GDI+, DirectShow). La lógica, si hay, es trivial. |

## Resumen ejecutivo

| Grupo | Archivos | PORTABLE | ADAPTABLE | REESCRIBIR |
|---|---|---|---|---|
| GPS/Classes | 39 | 11 | 24 | 4 |
| GPS/Forms (raíz) + Guidance | 59 | 1 | 13 | 45 |
| GPS/Forms (Field, Settings, Pickers, Inputs, Config, Profiles) | ~80 | 2 | 13 | ~65 |
| GPS infra (IO, ISOBUS, Helpers, Controls, Properties, Program) | ~36 | 21 | 4 | 9 |
| GPS/AgroParallel (Common, VistaX, FlowX) | 40 | 5 | 17 | 18 |
| AgIO completo | 66 | 14 | 19 | 33 |
| **TOTAL aprox.** | **~320** | **~54 (17%)** | **~90 (28%)** | **~174 (55%)** |

**Lectura:** el 45% del código (portable + adaptable) es el activo real — geometría, guiado, NMEA/PGN, UDP/MQTT/NTRIP, I/O de campos, servicios AgroParallel. El otro 55% es UI WinForms que en cualquier port se reemplaza, no se traduce.

### Bloqueadores duros por plataforma

| Bloqueador | Dónde | Android | Linux | iOS |
|---|---|---|---|---|
| `System.Windows.Forms` (UseWindowsForms=true) | toda la UI de ambos proyectos | reescribir UI | reescribir UI | reescribir UI |
| `OpenTK.GLControl` (windowing WinForms) — **AISLADO 2026-07-17** en FormGPS: el render habla con `IOpenGLSurface` (Width/Height/Left/Top/AspectRatio/MakeCurrent/SwapBuffers/Refresh, `Classes/GlSurface.cs`); los GLControl viven en los campos `ogl*Control` del Designer detrás de `WinFormsGlSurface` | OpenGL.Designer.cs, GeoViewport.cs, editores oglSelf | impl nueva de la surface + host de eventos (las llamadas GL puras son portables vía GLES/ANGLE) | ídem | ídem |
| ~~`Microsoft.Win32.Registry`~~ **RESUELTO 2026-07-16**: RegistrySettings (ambos proyectos) ahora persiste en JSON (`aog_settings.json` / `corex_settings.json`); Registry quedó aislado en métodos `*LegacyRegistry` (migración one-shot, se eliminan en el port) | RegistrySettings.cs ×2 | listo | listo | listo |
| ~~`Settings.Default`~~ **NO BLOQUEA** (verificado 2026-07-16): es un POCO propio serializado por XmlSettingsHandler (no ApplicationSettings); solo usa Point/Size/Color, que en .NET moderno viven en System.Drawing.Primitives (multiplataforma). El bloqueador real es System.Drawing.**Common** (Bitmap/Graphics), no estos primitivos. **2026-07-17: `Settings` y `RegistrySettings` del GPS ya viven en `AgOpenGPS.Core`** (los de AgIO siguen en su proyecto) | Settings.cs ×2 | listo | listo | listo |
| `System.IO.Ports` (serie GPS/steer/machine en AgIO) | SerialComm, FormCommSetGPS, NTRIP pass | requiere USB host API (driver serial USB) | funciona (`/dev/tty*`) | **no hay puerto serie accesible** — bloqueante salvo BLE/red |
| `Microsoft.Web.WebView2` | Hub, overlays AgroParallel | reemplazar por WebView Android | **sin soporte oficial Linux** — reemplazar (CEF/WebKitGTK) o UI nativa | WKWebView |
| ~~`System.Management` (WMI)~~ **AISLADO 2026-07-17**: FormGPS habla solo con la interfaz `IBrightnessController`; la impl WMI (`CWindowsSettingsBrightnessController`) es la pieza a reemplazar por plataforma (sysfs backlight en Linux, WindowManager en Android) | CBrightness.cs | impl nueva | impl nueva | impl nueva |
| `System.Media.SoundPlayer` | CSound.cs | reemplazar audio | reemplazar | reemplazar |
| `Accord.Video.DirectShow` (webcam) | FormWebCam.cs | reemplazar | reemplazar | reemplazar |
| `System.Windows.Forms.DataVisualization.Charting` | 4 Form*Graph | reemplazar charts | ídem | ídem |
| `GMap.NET.WinForms` (Bing maps) | FormMap.cs | reemplazar | ídem | ídem |
| Mutex single-instance + `Application.Run` | Program.cs (ambos) | ciclo de vida Android | adaptable | ciclo de vida iOS |
| `Process.Start` de .exe externos (ISOBUS TaskController, Updater) | FormISOBUS, self-update | no aplica | adaptable | no aplica |

### Qué falta, en orden, para compilar en otra plataforma

1. **Extraer el core a una librería sin referencias UI**: AVANZADO 2026-07-17 — primera tanda movida a `AgOpenGPS.Core` (namespace `AgOpenGPS` intacto, cero churn en consumidores): IO/ completo (12), Protocols/ISOBUS (4, `ISO11783_TaskFile` desacoplado de `CTrack`/`Program` — ahora recibe `List<CTrk>`), y 13 archivos de Classes/ (vec3, CFlag, CHeadLine, CBoundaryList+CFenceLine+CTurnLines —partial reunida—, CFeatureSettings, TrackCopier, BoundaryBuilder —desacoplado de RegistrySettings—, ColorExtensions, CGLM) + POCOs extraídos `CTrk`/`TrackMode` (ex CTrack.cs) y `CRecPathPt` (ex CRecordedPath.cs). **Segunda tanda (2026-07-17):** `Settings` + `RegistrySettings` (Properties/) se movieron a Core — `Settings.Default` dejó de ser dependencia problemática para TODAS las clases; `AtomicJson` bajó de AgroParallel.Services a AgroParallel.Models (netstandard2.0) y Core lo referencia; con eso CAHRS y CDubins se movieron a Core tal cual. **Falta**: las ~22 clases de guiado/estado acopladas a `FormGPS` inyectado por constructor → reemplazar por interfaces (el patrón ya existe en `AgroParallel/Common/FormGps*Service.cs`), y el parsing NMEA/PGN de AgIO.
2. **Abstraer persistencia**: HECHO 2026-07-16. RegistrySettings → JSON primario (Registry solo migración legacy aislada). Settings.Default ya era POCO + XmlSettingsHandler y sus Point/Size/Color son System.Drawing.Primitives (portables) — no requiere cambios. Además AgLibrary quedó sin WinForms/Accord (los controles RepeatButton/VideoSourcePlayer se movieron a Keypad y GPS): Log + Settings son ahora una base 100% portable.
3. **Reemplazar el host OpenGL**: AVANZADO 2026-07-17. El render de FormGPS (oglMain/oglBack/oglZoom) ya habla solo con `IOpenGLSurface`; en el port se escribe otra impl de la interfaz (EGL/SDL en Linux, GLSurfaceView en Android) + el wiring de eventos Load/Paint/Resize del host. Quedan los `oglSelf` de los editores (FormABDraw/FormHeadLine/etc., que son UI a reescribir de todos modos) y GeoViewport. Las llamadas `GL.*` migran casi directo a GLES2/ANGLE.
4. **Reescribir UI**: los ~174 archivos REESCRIBIR son formularios; gran parte de la config ya migró a HTML (config.html, perfiles, nodos, Hub) servida por EmbedIO — ese camino (backend EmbedIO + frontend web) es el que menos reescritura exige para Linux/Android.
5. **Plataforma específica**: serie (Android USB host / iOS sin serie), audio, brillo, webcam, WebView.

### Estado global del trabajo estructural (actualizado 2026-07-17)

Avance de los 5 puntos del roadmap. El % pondera solo el trabajo que se hace **en este repo antes del port** (extraer/aislar); la UI nueva y las impls por plataforma se escriben recién en el port.

| # | Punto | Estado | % | Qué falta |
|---|---|---|---|---|
| 1 | Extraer core a librería sin UI | AVANZADO | ~40% | ~21 clases de Classes/ acopladas a `FormGPS` por constructor (CABCurve, CABLine, CContour, CGuidance, CYouTurn, CVehicle, CTool, CTrack, CRecordedPath, CBoundary, CFence, CNMEA, CSim, CHead, CPatches, CSection, CTurn, CTram, CISOBUS…; CAHRS/CDubins/CFieldData/CModuleComm ya movidas) → interfaces estilo `FormGps*Service`; y el parsing NMEA/PGN de AgIO (NMEA.Designer, PGN.Designer, UDP) |
| 2 | Abstraer persistencia | **HECHO** | 100% | — (Registry solo migración legacy aislada; en el port se borra el `#region Legacy`) |
| 3 | Aislar host OpenGL | AVANZADO | ~70% | `oglSelf` de los editores (FormABDraw/FormHeadLine/etc., UI a reescribir igual) y GeoViewport; la impl EGL/SDL/GLSurfaceView es trabajo del port |
| 4 | Reescribir UI | EN CURSO (vía web) | ~25% | ~174 archivos WinForms; cada pantalla que migra a HTML/EmbedIO (config, perfiles, nodos, firmwares, datos lote/GPS ya migradas) baja este costo |
| 5 | Plataforma específica | PARCIAL | ~40% | Serie (Android USB host), webcam, WebView2. Audio (`AgpSoundPlayer`), brillo (`IBrightnessController`) y texturas (`Texture2D`) ya quedaron detrás de un punto único |

**Lectura global:** del trabajo estructural pre-port (puntos 1–3+5), está hecho **≈ 50%**. Ya viven en `AgOpenGPS.Core` (sin WinForms, 34 tests verdes): IO/ completo, Protocols/ISOBUS, 13 clases portables + POCOs `CTrk`/`CRecPathPt`. El grueso restante es uno solo y bien definido: **invertir el acoplamiento `FormGPS` de las ~24 clases de guiado/estado** (punto 1), que arrastra consigo casi todo lo demás.

---

# GPS — SourceCode/GPS

## GPS/Classes (39)

| Archivo | Líneas | Qué hace | Dependencias problemáticas | Veredicto |
|---|---|---|---|---|
| Brands.cs | 105 | Mapeo de marcas de tractores/cosechadoras a imágenes Bitmap | System.Drawing (Bitmap) | ADAPTABLE |
| CAHRS.cs | 52 | Datos IMU (inclinación, rumbo) y configuración | ~~Settings.Default~~ (ya en Core) — **movido a Core 2026-07-17** | PORTABLE |
| CABCurve.cs | 1811 | Generación de curvas AB y trazado de curvas suaves | OpenTK GL calls, FormGPS (acoplamiento fuerte) | ADAPTABLE |
| CABLine.cs | 568 | Lógica de líneas AB para guiado | FormGPS, GL calls puros | ADAPTABLE |
| CBoundary.cs | 24 | Gestión de límites de campo, punto-en-área | FormGPS, Settings.Default | ADAPTABLE |
| CBoundaryList.cs | 28 | Lista de límites con coordenadas y áreas | — | PORTABLE |
| CBrightness.cs | 97 | Brillo de monitor: interfaz `IBrightnessController` + impl WMI | WMI aislado en la impl Windows (2026-07-17); en el port se escribe otra impl de la interfaz | ADAPTABLE |
| CContour.cs | 671 | Líneas de contorno de campo | FormGPS, GL calls puros | ADAPTABLE |
| CDubins.cs | 636 | Curvas Dubins para rutas óptimas | ~~Settings.Default~~ (ya en Core) — **movido a Core 2026-07-17** | PORTABLE |
| ~~CExtensionMethods.cs~~ (movido a Controls/ 2026-07-17) | 60 | Helpers WinForms (NudlessNumericUpDown, TramModeBitmaps, ProgressBar) — ya no vive en Classes/ | System.Windows.Forms (es UI, se reescribe con la UI) | — |
| ColorExtensions.cs | 26 | CheckColorFor255 (Color puro, ex CExtensionMethods) | System.Drawing.Primitives (portable) | PORTABLE |
| CFeatureSettings.cs | 59 | Flags de features (POCO) | — | PORTABLE |
| CFence.cs | 168 | Cerca/valla con render OpenGL | FormGPS, GL calls puros | ADAPTABLE |
| CFenceLine.cs | 300 | Procesamiento de líneas de cerca | — | PORTABLE |
| CFieldData.cs | 160 | Área trabajada, distancia, estadísticas de campo | ~~FormGPS~~ invertida con `IFieldDataHost` — **movida a Core 2026-07-17** | PORTABLE |
| CFlag.cs | 39 | Datos de banderas (POCO) | — | PORTABLE |
| CGLM.cs | 427 | Matemática GLM (vectores, matrices, polígonos) | Solo OpenTK GL (~~Drawing.Imaging~~ → `MakeGrayscale3` movido a FormMap.cs, su único consumidor, traspaso 2026-07-16) | PORTABLE (GL inmediato: en móvil vía GLES/ANGLE) |
| CGuidance.cs | 412 | Guiado Stanley / Pure Pursuit / PID | FormGPS (fuerte), Settings.Default | ADAPTABLE |
| CHead.cs | 167 | Cabecera (posición, velocidad) | — | PORTABLE |
| CHeadLine.cs | 33 | Rutas de cabecera | — | PORTABLE |
| CISOBUS.cs | 180 | Protocolo ISOBUS para secciones | FormGPS | ADAPTABLE |
| CModuleComm.cs | 123 | Comunicación de módulos, switches trabajo/dirección | ~~`btn*.PerformClick()` directo~~ → delegados `Action` (2026-07-16); invertida con `IModuleCommHost` + enum `btnStates` extraído a Core — **movida a Core 2026-07-17** | PORTABLE |
| CNMEA.cs | 55 | Parseo NMEA | FormGPS, Settings.Default | ADAPTABLE |
| CPatches.cs | 162 | Parches/áreas trabajadas (triángulos) | FormGPS, colores UI | ADAPTABLE |
| CRecordedPath.cs | 849 | Grabación/replay de rutas Dubins | FormGPS, GL calls puros | ADAPTABLE |
| CSection.cs | 75 | Datos de secciones (POCO + timers) | — | PORTABLE |
| CSim.cs | 124 | Simulador de movimiento GPS | FormGPS, Settings.Default | ADAPTABLE |
| CSound.cs | 37 | Sonidos de alarma | ~~System.Media directo~~ → aislado en `AgpSoundPlayer` (único punto a reimplementar por plataforma, traspaso 2026-07-16) | ADAPTABLE |
| CTool.cs | 383 | Configuración de implemento (ancho, solape, ejes) | FormGPS, OpenTK, System.Drawing, Settings.Default | ADAPTABLE |
| CTrack.cs | 355 | Pistas de referencia AB/Curva | FormGPS, OpenTK (~~usings WinForms/Drawing muertos~~ eliminados 2026-07-16) | ADAPTABLE |
| CTram.cs | 261 | Tramlines | FormGPS, OpenTK (~~Accord/Bitmap~~ → `GetModeBitmap` movido a `TramModeBitmaps` en la capa UI, traspaso 2026-07-16) | ADAPTABLE |
| CTurn.cs | 192 | Lógica de giros | — | PORTABLE |
| CTurnLines.cs | 108 | Líneas de giro | — | PORTABLE |
| CVehicle.cs | 386 | Configuración de vehículo (ruedas, antena, PID) | FormGPS, Settings.Default | ADAPTABLE |
| CYouTurn.cs | 2958 | Giros en U (generación y ejecución) | FormGPS (fuerte), GL calls puros | ADAPTABLE |
| ScreenTextures.cs | 245 | Caché lazy de texturas de pantalla | Ninguna directa: delega en `Texture2D` (Core.DrawLib), que es EL punto de aislamiento Bitmap→GL a reimplementar por plataforma | ADAPTABLE |
| TrackCopier.cs | 192 | Copiar/convertir pistas entre campos | — | PORTABLE |
| vec3.cs | 172 | Structs vec2/vec3 y utilidades geométricas | — | PORTABLE |
| VehicleTextures.cs | 86 | Caché lazy de texturas de vehículo | Ninguna directa: delega en `Texture2D` (~~using Drawing muerto~~ eliminado 2026-07-16) | ADAPTABLE |
| BoundaryBuilder.cs | 614 | Constructor de límites desde pistas (segmentación, intersecciones, recorte) | System.IO (portable) | ADAPTABLE |

**Nota 2026-07-17:** los archivos PORTABLE de esta tabla (vec3, CFlag, CHeadLine, CBoundaryList, CFenceLine, CTurnLines, CFeatureSettings, TrackCopier, BoundaryBuilder, ColorExtensions, CGLM, y desde la 2ª tanda CAHRS y CDubins) **ya viven en `AgOpenGPS.Core/Classes/`** con namespace `AgOpenGPS` intacto; además se extrajeron los POCOs `CTrk`/`TrackMode` (de CTrack.cs) y `CRecPathPt` (de CRecordedPath.cs), y `Settings`/`RegistrySettings` viven en `AgOpenGPS.Core/Properties/`. **Tercera tanda (2026-07-17):** `CFieldData` se movió a Core invertida con la interfaz `IFieldDataHost` (Core/Interfaces/) que FormGPS implementa en `FormGps.FieldDataHost.cs` — patrón a repetir para las demás clases acopladas. `CModuleComm` siguió el mismo camino con `IModuleCommHost` (y el enum `btnStates` se extrajo de Sections.Designer.cs a Core/Classes/BtnStates.cs).

**Subtotal Classes: 17 PORTABLE · 22 ADAPTABLE · 0 REESCRIBIR.** Classes/ quedó sin archivos a reescribir: CModuleComm, CSound y CGLM se destrabaron con los traspasos del 2026-07-16; CBrightness quedó detrás de `IBrightnessController` el 2026-07-17 (la impl WMI se reemplaza por plataforma); y CExtensionMethods se partió el 2026-07-17 — los helpers WinForms se mudaron a Controls/ (UI, se reescribe con la UI) y `CheckColorFor255` quedó portable en ColorExtensions.cs. **Texturas:** todo el camino Bitmap→GL quedó concentrado en `Texture2D` (Core.DrawLib) — en un port se reimplementa esa clase (decoder PNG + GLES) y Brands/ScreenTextures/VehicleTextures no se tocan.

## GPS/Forms raíz + partials FormGPS (35) y Forms/Guidance (24)

| Archivo | Líneas | Qué hace | Dependencias problemáticas | Veredicto |
|---|---|---|---|---|
| FormGPS.cs | 4508 | God-class principal: cámara/viewport OpenGL, posición GPS, render de mapa, UDP, orquestación de partials, integraciones VistaX/OrbitX/QuantiX/FlowX/cámaras | WinForms, Drawing, OpenTK, sockets, P/Invoke User32, Registry, Settings.Default | ADAPTABLE |
| OpenGL.Designer.cs | 2968 | Render mapa 2D/3D, zoom/pan, matrices GL, unprojection de mouse | GLControl aislado detrás de `IOpenGLSurface` (2026-07-17); quedan WinForms residuales (fonts, cursores) | ADAPTABLE |
| Controls.Designer.cs | 2155 | Controles táctiles custom (numéricos sin flechas, keypad, sliders) | WinForms, Drawing | ADAPTABLE |
| GUI.Designer.cs | 1667 | Layout de paneles de control (inferior, derecho, flotantes) | WinForms, Drawing | REESCRIBIR |
| Position.designer.cs | 1616 | Ciclo de posición: lat/lon → easting/northing, heading, velocidad, aceleración | WinForms (host), lógica densa de posición | ADAPTABLE* |
| GUI.FloatingMenu.cs | 1339 | Menú flotante jerárquico + barras HTML dockeadas | WinForms, Drawing, Settings.Default | ADAPTABLE |
| UDPComm.Designer.cs | 1230 | UDP con módulos: parseo NMEA/PGN, monitoreo de conexión | Sockets (portables), WinForms host | ADAPTABLE |
| SaveOpen.Designer.cs | 984 | Guardado/apertura de campos (KML, ISOXML, boundaries, tracks) | WinForms host, System.IO | ADAPTABLE* |
| Sections.Designer.cs | 962 | Panel de secciones: toggles, barras de estado, velocidad/dosis | WinForms, Drawing | REESCRIBIR |
| PGN.Designer.cs | 499 | Parser/serializador de PGNs (telemetría, motor, secciones) | — | PORTABLE |
| FormTermsAndConditions.cs | 327 | Pantalla de términos con branding PilotX | WinForms, GDI+ | REESCRIBIR |
| Form_Keys.cs | 167 | Configurador de hotkeys | WinForms, Settings.Default | REESCRIBIR |
| FormDialog.cs | 91 | Diálogo modal con severidad | WinForms, Drawing | REESCRIBIR |
| FormShiftPos.cs | 96 | Compensación de deriva GPS (offset N/E) | WinForms | REESCRIBIR |
| FormPan.cs | 77 | Paneo de cámara | WinForms | REESCRIBIR |
| FormGPSData.cs | 71 | Telemetría en vivo (Hz, posición, sats) — ya reemplazado por datos-gps.html | WinForms | REESCRIBIR |
| FormSaving.cs | 61 | Progreso de guardado | WinForms | REESCRIBIR |
| FormWebCam.cs | 59 | Webcam vía DirectShow | Accord.Video.DirectShow | REESCRIBIR |
| FormEventViewer.cs | 53 | Visor de logs | WinForms | REESCRIBIR |
| FormHelp.cs | 50 | Diálogo de ayuda con URLs | WinForms, Process.Start | REESCRIBIR |
| FormTimedMessage.cs | 46 | Notificación temporal | WinForms | REESCRIBIR |
| FormYes.cs | 13 | Diálogo OK/Cancelar | WinForms | REESCRIBIR |
| *(+ ~13 .Designer.cs de los diálogos anteriores — layout puro)* | | | WinForms, Drawing | REESCRIBIR |

\* Partials de FormGPS con lógica de dominio densa (posición, guardado de campos) que hay que rescatar aunque el archivo sea WinForms.

**Guidance/**

| Archivo | Líneas | Qué hace | Dependencias problemáticas | Veredicto |
|---|---|---|---|---|
| FormBuildTracks.cs | 1550 | Constructor de trazas (AB, Curve, A+, Lat/Lon, Pivot), import/export KML | WinForms, Drawing, XML, Registry, Settings | ADAPTABLE |
| FormHeadLine.cs | 1022 | Editor de líneas de cabecera con zoom/pan, generación desde fenceline | WinForms, OpenTK, Settings | ADAPTABLE |
| FormHeadAche.cs | 983 | Constructor de headland desde límite, clip de trazas, secciones | WinForms, OpenTK, Settings | ADAPTABLE |
| FormTramLine.cs | 959 | Editor avanzado de tramlines con viewport GL | WinForms, OpenTK, Settings | ADAPTABLE |
| FormABDraw.cs | 930 | Editor visual de líneas AB (dibujo, snap, zoom) | WinForms, OpenTK, Settings | ADAPTABLE |
| FormQuickAB.cs | 570 | Wizard AB rápido (2 clicks o rumbo manual) | WinForms, Settings | ADAPTABLE |
| FormTram.cs | 256 | Generador simple de tram (pasadas, modo, ancho) | WinForms, Settings | ADAPTABLE |
| FormNudge.cs | 211 | Nudge de traza (offset izq/der) | WinForms, Settings | REESCRIBIR |
| FormGrid.cs | 173 | Editor de grilla (alineación a 2 puntos) | WinForms, Settings | ADAPTABLE |
| FormRefNudge.cs | 138 | Nudge de traza de referencia | WinForms, Settings | REESCRIBIR |
| FormRecordName.cs | 84 | Nombrado de trazas grabadas | WinForms, Regex | REESCRIBIR |
| FormSmoothAB.cs | 81 | Suavizado de curva AB (slider) | WinForms | REESCRIBIR |
| *(+ ~12 .Designer.cs correspondientes)* | | Layout puro | WinForms, Drawing | REESCRIBIR |

**Subtotal Forms raíz + Guidance (59): 1 PORTABLE · 13 ADAPTABLE · 45 REESCRIBIR.** Lo crítico a rescatar: FormGPS y sus partials (Position, SaveOpen, UDPComm, OpenGL) + los editores de guidance, que mezclan geometría valiosa con UI.

## GPS/Forms — Field, Settings, Pickers, Inputs, Config, Profiles (~80)

**Field/ (~30)**

| Archivo | Líneas | Qué hace | Dependencias problemáticas | Veredicto |
|---|---|---|---|---|
| FormBndTool.cs | 1096 | Procesamiento avanzado de límites: K-NN clustering, suavizado, detección de perímetro | WinForms, Drawing, OpenTK | ADAPTABLE |
| FormBuildBoundaryFromTracks.cs | 741 | Límites desde pistas: intersección de segmentos, trimado | WinForms, OpenTK, geometría computacional | ADAPTABLE |
| FormFieldExisting.cs | 638 | Selector de campos con distancia GPS y área de boundary | WinForms, RegistrySettings | ADAPTABLE |
| FormBoundary.cs | 458 | Gestor de límites outer/inner, drive-thru, áreas | WinForms, geometría de polígonos | ADAPTABLE |
| FormMap.cs | 423 | Editor de límites sobre mapa Bing (GMap.NET) | GMap.NET.WinForms, Bing API | REESCRIBIR |
| FormFieldISOXML.cs | 414 | Import de campos ISOXML (ISOBUS Taskdata) | WinForms, XmlDocument | ADAPTABLE |
| FormFieldKML.cs | 414 | Campo desde KML con origen local | WinForms, conversión WGS84 | ADAPTABLE |
| FormCopyTracks.cs | 271 | Copia de pistas entre campos con conversión de origen | WinForms, TrackCopier | ADAPTABLE |
| FormBoundaryPlayer.cs | 236 | Grabación de límite conduciendo, área en vivo | WinForms, Settings | ADAPTABLE |
| FormEnterFlag.cs | 200 | Banderas por lat/lon con import/export CSV | WinForms, dialogs de archivo | ADAPTABLE |
| FormFieldDir.cs | 141 | Nuevo directorio de campo con validación | WinForms, RegistrySettings | ADAPTABLE |
| FormFlags.cs | 126 | Editor de banderas | WinForms | PORTABLE (lógica) |
| FormFieldData.cs | 122 | Estadísticas de campo en vivo — ya reemplazado por datos-lote.html | WinForms, Settings | PORTABLE (lógica) |
| FormJob.cs | 100 | Menú de inicio de trabajo (nuevo/reanudar/abrir) | WinForms, RegistrySettings | REESCRIBIR |
| *(+ ~16 .Designer.cs)* | | Layout puro | WinForms | REESCRIBIR |

**Settings/ (~30):** FormSteer.cs (config AutoSteer: PID, PWM, offset — **ADAPTABLE**, parámetros + PGNs rescatables); FormCorrection/FormGraphHeading/FormGraphSteer/FormGraphXTE (gráficos con DataVisualization.Charting — REESCRIBIR); FormAllSettings, FormButtonsRightPanel, FormColor, FormColorSection, FormConfig (ya replicada en config.html), FormSimCoords, FormSteerWiz + todos los .Designer.cs — REESCRIBIR.

**Pickers/ (8):** FormColorPicker (lib WinForms de terceros), FormFilePicker, FormRecordPicker, FormDrivePicker + Designers — REESCRIBIR (lógica de listado trivial).

**Inputs/ (4):** FormKeyboard, FormNumeric + Designers — REESCRIBIR (la UI web ya tiene teclado HTML propio).

**Config/ (4):** ConfigSummaryControl (PORTABLE en lógica), ConfigVehicleControl + Designers — REESCRIBIR.

**Profiles/ (4):** FormLoadProfile, FormNewProfile (**ADAPTABLE** — gestión de perfiles XML + PerfilGuard, ya expuesta vía PerfilesController) + Designers REESCRIBIR.

**Subtotal grupo: 2 PORTABLE · 13 ADAPTABLE · ~65 REESCRIBIR.** El núcleo geométrico de Field (BndTool, Boundary*, BuildBoundaryFromTracks) es de lo más valioso del proyecto.

## GPS infra — IO, Protocols, Helpers, Controls, Properties, Program (~36)

**IO/ (12) — 12/12 PORTABLE.** Puro I/O de archivos de texto geoespaciales, cero dependencias de plataforma: BoundaryFiles, ContourFiles, Elevation, FieldPlaneFiles, FileIOUtils, FlagFiles, HeadlandFiles, HeadlinesFiles, RecPathFiles, SectionsFiles, TrackFiles, TramFiles.

**Protocols/ISOBUS/ (4):** CurveCABTools, IsoXmlFieldImporter, IsoXmlParserHelpers — PORTABLE. ISO11783_TaskFile — ADAPTABLE (depende del NuGet Dev4Agriculture, que es multiplataforma pero conviene verificar en runtime móvil).

**Helpers/ (3):** GeoRefactorHelper PORTABLE; ScreenHelper ADAPTABLE (Screen.AllScreens); HotkeyMessageFilter REESCRIBIR (message loop Win32).

**Controls/ (3):** NudlessNumericUpDownExtensions, TextBoxExtensions, DraggableControlExtension — 3/3 REESCRIBIR (extensiones WinForms).

**WinForms/GeoViewport.cs:** ADAPTABLE — adaptador de viewport sobre GLControl; reemplazar el host, conservar la matemática.

**Visuals/SectionsVisual.cs:** PORTABLE — usa el wrapper interno GLW, sin Drawing.

**Properties/ (3):** Settings.cs y Resources.Designer.cs PORTABLES/autogenerados; **RegistrySettings.cs PORTABLE** (2026-07-16: JSON primario `aog_settings.json`; Registry aislado en `#region Legacy` que se elimina en el port).

**Program.cs:** ADAPTABLE — Mutex single-instance + Application.Run + crash logging; cada plataforma necesita su entry point.

**csproj (AgOpenGPS.csproj):**
- `UseWindowsForms=true`, `OutputType=WinExe`, `RuntimeIdentifier=win-x64` → **bloqueante**, define el proyecto como Windows Desktop.
- NuGets Windows-only: `GMap.NET.WinForms`, `MechanikaDesign ColorPicker`, `OpenTK.GLControl`, `Microsoft.Web.WebView2` (sin Linux oficial).
- NuGets multiplataforma OK: `MQTTnet`, `System.Text.Json`, `SQLite`, `NetTopologySuite.IO.Esri.Shapefile`, `Dev4Agriculture.ISO11783.ISOXML`, `System.Memory`.
- References Windows-only: `System.Management` (WMI, usado solo por la impl Windows de `IBrightnessController` — se elimina junto con ella en el port), `System.Windows.Forms.DataVisualization`.

**Subtotal infra: 21 PORTABLE · 4 ADAPTABLE · 9 REESCRIBIR.** Es la zona más portable de todo el repo. **Nota 2026-07-17:** IO/ (12 archivos) y Protocols/ISOBUS (4) ya se movieron a `AgOpenGPS.Core` — la librería compila sin WinForms y sus 34 tests pasan.

## GPS/AgroParallel — Common, VistaX, FlowX (40)

**Common/ (28)**

| Archivo | Qué hace | Dependencias problemáticas | Veredicto |
|---|---|---|---|
| AdaptiveStep.cs | Pasos adaptativos para dosis/PID | — | PORTABLE |
| EarClipper.cs | Triangulación ear-clipping de polígonos | PointF (trivial) | PORTABLE |
| PerfilGuard.cs | Protección SHA-256 de perfiles | — | PORTABLE |
| FormGpsPilotXUpdateService.cs | Self-update vía HttpClient | — | PORTABLE |
| AgroParallelModulesConfig.cs | Config extensible de módulos X-* (JSON) | Color, rutas AppDomain | ADAPTABLE |
| FormGps*Service.cs (×15: Config, Coverage, Guidance, ImuCalibracion, Lotes, Perfil, QuantiXRuntime, SectionControl, Shapefile, StateProvider, ToolGeometry, TrackList, Tram, VehicleTool…) | Adaptadores que exponen FormGPS a la UI web (patrón interfaz + marshalling `Invoke` al hilo UI) | WinForms Invoke, Settings.Default, RegistrySettings | ADAPTABLE |
| ShapefileLayer.cs | Capa shapefile en OpenGL (reproyección + triangulación + DBF styling) | OpenTK GL, PointF | ADAPTABLE |
| AgroParallelTheme.cs | Paleta de temas + helpers GDI+ | WinForms, GDI+ | REESCRIBIR |
| AlertaNodosWebOverlayControl.cs / EstadoModulosOverlayControl.cs / QuantiXWidgetWebView2Control.cs / VistaXWebOverlayPanel.cs | Overlays WebView2 sobre el mapa | WinForms UserControl + WebView2 | REESCRIBIR |
| OverlayDragger.cs / OverlayRegionHelper.cs | Drag y region redondeada de controles | WinForms, GDI+ | REESCRIBIR |
| ShapefileLegendControl.cs | Widget nativo dosis QuantiX | WinForms, GDI+ | REESCRIBIR |

**VistaX/ (11):** FormVistaXConfig/Mapeo/Nodos/Perfiles/Popup/Prueba/Sensores/Simulator/Sonidos/Trenes + VistaXNativePanel — **11/11 REESCRIBIR** como archivos (Forms + GDI+), pero la lógica MQTT/JSON embebida es portable y gran parte ya migró a la UI web.

**FlowX/ (1):** FlowXLegendControl — REESCRIBIR (UserControl GDI+).

**Subtotal AgroParallel: 5 PORTABLE · 17 ADAPTABLE · 18 REESCRIBIR.** Los `FormGps*Service` son justamente la capa de abstracción que un port necesita: ya definen interfaces (`IConfigVehiculoService`, etc.) — solo hay que reimplementarlas contra un core sin WinForms.

---

# AgIO (CoreX) — SourceCode/AgIO (66)

## Núcleo (Classes, partials de FormLoop, Web)

| Archivo | Líneas | Qué hace | Dependencias problemáticas | Veredicto |
|---|---|---|---|---|
| CGLM.cs | 36 | Utilitarios matemáticos (Haversine, validaciones) | — | PORTABLE |
| CoreXState.cs | ~50 | Snapshot thread-safe @1Hz para el web host :5181 | — | PORTABLE |
| CRadioChannel.cs | 10 | POCO canal de radio | — | PORTABLE |
| ListViewColumnSorterExt.cs | ~60 | Sorter de ListView | WinForms | REESCRIBIR |
| FormLoop.cs | 805 | God-class: init de puertos serie, UDP, timers | WinForms, sockets, SerialPort, DllImport User32, RegistrySettings | ADAPTABLE |
| FormLoop.CoreXSnapshot.cs | 769 | Publica snapshot @1Hz + captura web-driven de NMEA/UDP con keep-alive | — | PORTABLE |
| UDP.designer.cs | 470 | **Lógica UDP**: broadcast, multicast, scan de red, hello a módulos | Sockets estándar (portables), host WinForms | ADAPTABLE |
| MQTT.Designer.cs | ~150 | **Broker MQTT embebido (MQTTnet)**: Start/Stop, handlers, :1883 | MQTTnet portable; updates de labels WinForms | ADAPTABLE |
| SerialComm.Designer.cs | ~200 | **6 puertos serie** (GPS×2, RTCM, IMU, Steer, Machine): callbacks, envío | System.IO.Ports (Android: USB host; iOS: sin serie) | ADAPTABLE |
| NMEA.Designer.cs | ~300 | **Parsing NMEA completo** (checksums, fix, sats, heading, IMU) | — | PORTABLE |
| NTRIPComm.Designer.cs | ~250 | **Cliente NTRIP**: TCP al caster, auth HTTP, RTCM, GGA interval | TCP portable; salida a serie requiere adaptación | ADAPTABLE |
| CoreXWebHost.cs | ~80 | Host EmbedIO :5181 (API + estáticos) | EmbedIO (portable) | PORTABLE |
| CoreXStatusController.cs | 15 | GET /api/corex/status | — | PORTABLE |
| CoreXCommandController.cs | 60 | Toggles MQTT/NTRIP, reiniciar/apagar | MethodInvoker (marshalling UI) | ADAPTABLE |
| CoreXConfigController.cs | ~150 | Config serial/ntrip/red vía web | IO.Ports, RegistrySettings, RunOnUiAsync | ADAPTABLE |
| CoreXDiagController.cs | ~150 | Diagnóstico web (eventos, monitor UDP/GPS) | RegistrySettings | ADAPTABLE |
| CoreXPerfilesController.cs | ~100 | Perfiles XML vía web | RegistrySettings | ADAPTABLE |
| CoreXRadioController.cs | 204 | Config radio RTCM y paso serial vía web | RegistrySettings, RunOnUiAsync | ADAPTABLE |

## Diálogos y soporte (todo REESCRIBIR salvo indicado)

- **ADAPTABLE:** FormCommSetGPS.cs (582 — selector de puertos con lógica de enumeración serie).
- **REESCRIBIR (diálogos):** FormAdvancedSettings, FormEthernet, FormUDP, FormNtrip, FormGPSData, FormEventViewer, FormSerialMonitor (usa Dispatcher WPF), FormSerialPass, FormRadio, FormRadioChannel, FormISOBUS (Registry + Process.Start + DllImport), FormProfiles, FormUDPMonitor, FormSource, FormKeyboard, FormNumeric, FormPGN, FormYes, FormTimedMessage.
- **REESCRIBIR (infra Windows):** Program.cs (Mutex + Application.Run), NumericUpDownExtensions, TextBoxExtensions, Resources.Designer.cs. RegistrySettings.cs pasó a PORTABLE (2026-07-16: JSON primario `corex_settings.json`; Registry aislado en `#region Legacy`).
- **REESCRIBIR (~22 .Designer.cs):** layout autogenerado de todos los forms.
- **PORTABLE:** Settings.cs (singleton de settings planos, con salvedad ApplicationSettings).

**csproj AgIO:** .NET Framework 4.8, WinExe x64. NuGets portables: MQTTnet 4.3.6, System.Text.Json 9, System.Memory. Referencias a AgLibrary (logging) y AgroParallel.WebHost (EmbedIO) — ambas portables.

**Subtotal AgIO: 14 PORTABLE · 19 ADAPTABLE · 33 REESCRIBIR.** El corazón de AgIO (UDP + MQTT + NTRIP + NMEA + serie) es ~70% extraíble a una librería sin UI; la UI vieja es descartable porque la interfaz real ya es el web host :5181.

---

# Conclusión

1. **Linux** es el destino más barato: .NET 8 corre nativo, sockets/MQTT/EmbedIO/serie funcionan; los bloqueos son WinForms (reemplazar UI), GLControl (host GL) y Registry (JSON). La UI web existente (:5180/:5181) ya cubre config, perfiles, nodos, firmwares, datos de lote/GPS.
2. **Android** agrega: ciclo de vida de app, USB host para serie, WebView nativo, GLES (las llamadas GL migran bien). Viable para una app "cabina" que hable con nodos por MQTT/WiFi.
3. **iOS** es el más hostil: sin puerto serie accesible (adiós GPS/steer por USB directo), políticas de background estrictas. Solo tendría sentido como cliente de monitoreo, no como cerebro del autopiloto.
4. **La ruta de menor esfuerzo** ya está en marcha sin buscarla: cada pantalla que migra a HTML servido por EmbedIO reduce el costo del port, porque el frontend web es portable por definición. El trabajo estructural pendiente es extraer Classes/IO/Protocols/NMEA/PGN/UDP/MQTT/NTRIP a proyectos netstandard sin referencia a WinForms, invirtiendo el acoplamiento `FormGPS`/`FormLoop` detrás de interfaces (patrón que los `FormGps*Service` ya usan).
