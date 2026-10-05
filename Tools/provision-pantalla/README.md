# Kit de aprovisionamiento de pantallas PilotX

Todo lo necesario para dejar una pantalla nueva lista para un cliente,
**sin cuenta Microsoft en ningún paso**.

## 0. Saltear la cuenta Microsoft en el asistente inicial (OOBE)

Si la pantalla está en el asistente de Windows pidiendo cuenta Microsoft:

**Windows 11 (23H2/24H2 y posteriores):**
1. `Shift + F10` (abre una consola). En pantallas sin teclado, enchufar uno USB.
2. Escribir: `start ms-cxh:localonly`
3. Se abre el diálogo viejo de cuenta LOCAL: crear el usuario `soporte`
   con contraseña. Listo — nunca pide Microsoft.

Si `ms-cxh:localonly` no anda (builds más viejos):
1. `Shift + F10` → `oobe\bypassnro` → reinicia el asistente.
2. En la selección de red elegir **"No tengo Internet"** → **"Continuar con
   configuración limitada"** → crea cuenta local.

Alternativa sin teclado: **no conectar la pantalla a internet** hasta pasar
el OOBE — sin red, Windows ofrece cuenta local solo.

> El resto de los usuarios (`pilotx` operario) los crea el script — no crear
> más cuentas a mano en el OOBE.

## 1. Armar el USB del kit

Correr en la PC de desarrollo (con el USB en `E:` por ejemplo):

```powershell
powershell -ExecutionPolicy Bypass -File .\Armar-KitUSB.ps1 -Destino E:\KitPilotX
```

Copia al USB:
- `Provision-Pantalla.ps1` (este kit)
- `Branding\` (logo + fondo, desde `Build\Branding` y `Build\AgroParallel\wwwroot\img\fondo.png`)
- `PilotX-KioskSetup.exe` (modo kiosko: autologon + shell = PilotX)
- `PilotX_v<ver>.zip` (el último build, si existe en `Build\`)
- `vc_redist.x64.exe` y `MicrosoftEdgeWebview2Setup.exe` (los baja si hay internet)

## 2. Correr en la pantalla

Como Administrador (usuario `soporte` creado en el OOBE):

```powershell
# Primero SOLO mirar qué tiene el equipo (no toca nada):
powershell -ExecutionPolicy Bypass -File .\Provision-Pantalla.ps1 `
    -Cliente "LAS GRINGAS" -Cuit "33716946539" -SoloRevisar

# Revisar C:\PilotX\provision-report.txt y después aplicar todo:
powershell -ExecutionPolicy Bypass -File .\Provision-Pantalla.ps1 `
    -Cliente "LAS GRINGAS" -Cuit "33716946539"
```

Después:
1. Extraer el ZIP de PilotX en `C:\PilotX` (¡verificar que `orbitX.json` NO
   traiga el `device_id` de la PC de desarrollo — trampa conocida!).
2. Volver a correr el script (re-crea las reglas de firewall ahora que los
   exes existen) o correr solo la sección 6 a mano.
3. `PilotX-KioskSetup.exe /yes` → autologon `pilotx` + arranque directo en
   PilotX. Reiniciar.

## 3. Alta del cliente en OrbitX (cloud)

Se hace desde el panel `https://orbitx.agroparallel.com` con el usuario
superadmin (o por API con su JWT):

1. **Crear la org**: panel de admin → nueva organización, o
   `POST /api/admin/establecimiento` con
   `{ "nombre": "Las Gringas", "slug": "las-gringas", "provincia": "..." }`.
   El CUIT (33716946539) hoy no tiene campo propio en la org — queda
   registrado en `C:\PilotX\cliente.json` de la pantalla y en la ficha
   administrativa del cliente.
2. **Invitar al dueño**: dentro de la org → Equipo → Invitar (email del
   cliente, rol owner). El cliente activa su cuenta desde el mail.
3. **Vincular la pantalla**: en PilotX → "Vincular por código"; el código se
   carga en el panel y el equipo queda asignado a `las-gringas`
   (los devices NUNCA se dan de alta a mano, siempre por vínculo).

## Dependencias runtime (trampas conocidas)

- Sin **VC++ 2015-2022 x64** → WebView2 falla con `0x80070490`.
- WebView2 necesita **sesión interactiva sin elevar** (por eso el kiosko
  lanza PilotX como shell del usuario `pilotx`, no como tarea elevada).
- Red en perfil **Privado** o los módulos UDP no aparecen.
