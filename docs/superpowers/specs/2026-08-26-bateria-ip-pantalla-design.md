# Batería en el HUD + IP en Sistema — diseño

Fecha: 2026-08-26 · Estado: aprobado

## Qué se pide

En la pantalla de cabina hay que poder ver la IP de la máquina y el estado de
carga de la batería de la pantalla. Decisión del usuario: la batería siempre a
la vista (chip en el HUD), la IP en el panel Sistema (se consulta cada tanto).

## 1. Chip de batería en la BarraSuperior del cockpit (`PilotX.Cockpit.Bars`)

> Corrección post-diseño: el `HudBar` de `MainWindow.axaml` está SIEMPRE
> oculto (lo reemplazaron las barras del cockpit). La barra visible es
> `BarraSuperior.axaml`, y el chip va ahí, junto a KM/H · SEÑAL · TIEMPO · HA.

- Un chip más junto a los existentes, mismo estilo visual (Border blanco,
  borde `#D9E0D9`, radio 10, label `BAT`): texto `85%`, con `⚡` agregado
  cuando está enchufada y cargando.
- Lectura **local** con P/Invoke `GetSystemPowerStatus` (kernel32) desde
  PilotX.UI — la UI corre en la misma máquina, no se pasa por el Engine.
- Poll con `DispatcherTimer` cada 10 s (UI thread, no hace falta Dispatcher
  manual).
- Sin batería (`BatteryFlag = 128` / NoSystemBattery) o llamada fallida
  (Linux, error de P/Invoke) → el chip queda `IsVisible = false`. Nunca
  muestra datos inventados.
- Colores del chip:
  - **Gris (normal)**: enchufada (ACLineStatus = 1).
  - **Ámbar**: a batería (ACLineStatus = 0), cualquier porcentaje ≥ 15.
  - **Rojo**: a batería y < 15 %.
- Sin popups sobre el mapa, sin sonidos (regla del proyecto: nada tapa el
  mapa; alerta = solo color).

Componente nuevo: `Services/BateriaLector.cs` (struct + P/Invoke + método
`Leer()` que devuelve `(bool tiene, int pct, bool cargando, bool enchufada)`).
El timer y el update del chip viven en `MainWindow.axaml.cs`, donde ya está el
resto del HUD.

## 2. Sección "Red" en `SistemaPanel.axaml`

- Nueva sección entre **Brillo** y **Energía**, mismo estilo de card
  (`PilotXPanelSurface`).
- Lista de interfaces activas con su IPv4: `Ethernet — 192.168.5.10`, una por
  línea. Fuente: `NetworkInterface.GetAllNetworkInterfaces()`, filtrando:
  - `OperationalStatus == Up`
  - no loopback
  - solo IPv4, excluyendo APIPA (`169.254.x.x`)
- Si no hay ninguna: texto "Sin red conectada".
- Debajo, detalle de batería en texto: `Batería: 85 % — cargando` /
  `— a batería` / `Sin batería` (reusa `BateriaLector`).
- Refresco cada 5 s con `DispatcherTimer` que corre **solo mientras el panel
  está visible** (arranca en `AttachedToVisualTree` / se frena en
  `DetachedFromVisualTree`, patrón ya usado en otros paneles).

## 3. Manual

`ayuda.html` se actualiza en el mismo commit: el chip de batería del HUD y la
ruta `Sistema › Red` para ver la IP. Rutas verificadas contra los .axaml.

## Descartado

- Exponer batería/IP vía API del Engine: vueltas de más para datos locales.
- WMI (`Win32_Battery`): más pesado que el P/Invoke.
- Sonidos / popups de alerta: el aviso es el color del chip.

## Verificación

- Compila `PilotX.UI` (build.ps1).
- En la PC de desarrollo (sin batería): el chip NO aparece; Sistema muestra
  las IPs reales y "Sin batería".
- En notebook/tablet: chip con %, rayito al enchufar, ámbar al desenchufar.
