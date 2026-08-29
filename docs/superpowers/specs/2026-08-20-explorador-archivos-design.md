# Explorador de archivos propio de PilotX — 2026-08-20

## Problema
Cargar firmwares, mapas/shapefiles, sonidos o guardar exports abre el
explorador de WINDOWS (`StorageProvider`): fuera de branding, no táctil, y en
kiosko es una vía de escape de la app. El operario además no sabe si el equipo
"vio" el USB que enchufó.

## Decisión (aprobada)

### 1. Card nativa "Elegir/Guardar archivo" (overlay PilotX)
- Estilo card de los paneles nativos (paleta clara `PilotXPanel*`, cabecera
  22 SemiBold + subtítulo, ✕ 48×48, radios 8/10, scrim si aplica).
- **Dos columnas**: LUGARES (izq) + ARCHIVOS (der).
- LUGARES: los **USB enchufados arriba de todo** (nombre del volumen + tamaño,
  ícono distinto) y las carpetas de PilotX: **Firmwares** (cache local de
  FirmwareMirror), **Lotes** (`Fields\`), **Documentos**
  (`Documentos\AgOpenGPS`), **Descargas** del usuario. NADA más (sin "Este
  equipo", sin C:\ crudo).
- ARCHIVOS: filas táctiles ≥48 px; solo las extensiones que pide el llamador;
  carpetas navegables con fila `▸ ..` para subir; orden carpetas primero y
  después **por fecha desc** (lo último copiado al USB arriba). Nombre + fecha
  + tamaño.
- **Modo guardar**: campo de nombre con el teclado propio
  (`POST /api/teclado/abrir|cerrar`, jamás osk.exe), botón Guardar,
  confirmación si pisa un archivo existente.
- **USB en vivo**: con la card abierta, poll de `DriveInfo` cada 2 s actualiza
  la lista de lugares (enchufar/sacar se refleja solo). Sin WMI/registro.

### 2. Aviso global de USB
- Watcher en MainWindow (mismo poll 2 s, arranca con la app): USB nuevo →
  toast "USB conectado: <VOLUMEN> (<tamaño>)"; retirado → "USB retirado".
  Corre siempre, independiente del explorador.

### 3. Servicio de integración
- `ExploradorArchivos` con firma async simple:
  - `ElegirAsync(titulo, extensiones[], multiple=false)` → ruta(s) o null.
  - `GuardarAsync(titulo, nombreSugerido, extension)` → ruta o null.
- Reemplaza al `StorageProvider` en los 4 puntos actuales:
  `FirmwaresPanel.axaml.cs:630`, `QuantiXEditor/ShapeTab.cs:123`,
  `SonidosPanel.cs:762`, `DebugPanel.axaml.cs:683`.
- El flujo de prescripciones conserva su truco: elegir el `.shp` arrastra los
  `.dbf/.shx` hermanos (eso ya lo hace el llamador; el picker solo devuelve la
  ruta).

## Fuera de alcance
Borrar/renombrar/copiar (es picker, no administrador). Red/carpetas
compartidas. Android/Linux (Windows primero; el servicio puede caer a
StorageProvider en otras plataformas).

## Notas técnicas
- Detección USB: `DriveInfo.GetDrives()` con `DriveType.Removable` e `IsReady`.
- La card es un overlay más de MainWindow (exclusión mutua como el resto) o un
  diálogo modal propio sobre el panel llamador — decidir en implementación
  según qué moleste menos al mapa (regla: el mapa siempre se ve).
- Nada de ComboBox/Flyout sobre el mapa GL.
