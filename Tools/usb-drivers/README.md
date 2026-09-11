# Drivers USB-serial bundleados (flasheo por USB)

Estos drivers viajan en el build (los copia `build.ps1` a
`Build/Engine/tools/usb-drivers/`) y el panel "Flashear por USB" de PilotX los
instala con `pnputil /add-driver <inf> /install /subdirs` (elevado). La pantalla
NO baja nada de internet.

## cp210x/  — Silicon Labs CP210x VCP  ✅ presente
- Es el conversor del **esp32doit-devkit-v1 (FlowX)** → el driver crítico.
- Versión 11.5.0 (2025-12-31), firmado (`silabser.cat`).
- Origen oficial: https://www.silabs.com/documents/public/software/CP210x_Universal_Windows_Driver.zip
- Instalar con: `pnputil /add-driver silabser.inf /install /subdirs`

## ch340/  — WCH CH340/CH341  ⚠️ PENDIENTE (dir vacío)
- Solo hace falta para **placas ESP32 clon** con chip CH340 (el FlowX NO lo usa).
- No se pudo automatizar la descarga: www.wch-ic.com es una SPA que sirve el
  archivo por una API JS. Bajar a mano desde el navegador:
  https://www.wch-ic.com/downloads/CH341SER_ZIP.html → extraer `CH341SER.INF`,
  `CH341S64.SYS` (y `.CAT`) acá dentro.
- Alternativa: en Windows 10/11 modernos el CH340 suele instalarse solo por
  Windows Update al conectar el cable.
