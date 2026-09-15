# Kit: pasar una pantalla de AgOpenGPS a PilotX sin perder los lotes

Para una pantalla que hoy corre **AgOpenGPS original** (v5/v6) y tiene que
quedar con PilotX, conservando los lotes del cliente y su perfil de vehículo.

Pensado para trabajar **por RustDesk**, con el técnico mirando la pantalla.

> **Equipo de esta migración:** Carrano e Hijos SRL — CUIT **30718026616**.

## Por qué se puede migrar copiando

PilotX usa exactamente las mismas carpetas y el mismo formato que AgOpenGPS:

| Qué | Dónde |
|---|---|
| Lotes | `<carpeta de trabajo>\AgOpenGPS\Fields\<nombre>\` |
| Perfiles de vehículo | `<carpeta de trabajo>\AgOpenGPS\Vehicles\<nombre>.XML` |

El perfil se lee **campo por campo por nombre**: lo que PilotX conoce entra, lo
que no reconoce lo ignora sin romper, y lo que el XML viejo no trae queda en el
valor por defecto (`XmlSettingsHandler.LoadXMLFile`). Por eso un perfil de
AOG 6.x entra casi entero y uno de 5.x entra a medias — y por eso **hay que
verificar la geometría en pantalla antes de salir a trabajar**.

## La trampa que justifica todo este kit

AgOpenGPS guarda la carpeta de trabajo en
`HKCU\SOFTWARE\AgOpenGPS\workingDirectory`, y **el instalador de PilotX borra
esa clave** (`AgroParallelPilot.iss`). Si el cliente no trabajaba en
Documentos, después de instalar queda un disco con los lotes y nadie sabe
dónde. `Rescatar-AOG.ps1` lee esa clave primero y la deja escrita dentro del
ZIP de rescate.

## Orden de los pasos

### 0. Antes de tocar nada

- Que la pantalla tenga internet (para bajar el paquete).
- Red en perfil **Privado**, o los módulos UDP no aparecen después.
- Si es una pantalla sin PilotX previo: hace falta **VC++ 2015-2022 x64** y
  **WebView2**. Sin VC++ redist, WebView2 falla con `0x80070490`.

### 1. Rescatar los lotes y el articulado (no toca nada)

```powershell
.\Rescatar-AOG.ps1
```

Deja `C:\Rescate-PilotX\rescate-<equipo>-<fecha>.zip` + un informe con qué
lotes y qué perfiles había, cuál estaba activo y cuáles son articulados
(`setVehicle_vehicleType = 2`).

Si no encuentra los datos:

```powershell
.\Rescatar-AOG.ps1 -Buscar                 # rastrea los discos
.\Rescatar-AOG.ps1 -Datos D:\AgOpenGPS     # o se la indicás a mano
```

**Bajate el ZIP a tu PC por RustDesk (transferencia de archivos) antes de
seguir.** Es el seguro de todo lo que viene después.

### 2. Bajar el paquete de PilotX a la pantalla

```powershell
.\Descargar-PilotX.ps1 -Email <tu mail de OrbitX> -Listar    # ver qué hay
.\Descargar-PilotX.ps1 -Email <tu mail de OrbitX>            # baja la última
```

Baja de `orbitx.agroparallel.com` y **verifica el SHA256** contra el que
publica el catálogo; si no coincide, borra el archivo. La pantalla no necesita
estar vinculada todavía: el endpoint de descarga acepta el token de un usuario
logueado, no sólo device-auth.

Si OrbitX no tiene el paquete publicado, copiá el ZIP por RustDesk y seguí con
el paso 3 apuntando a ese archivo.

### 3. Instalar PilotX

```powershell
..\..\instalar-pilotx.ps1 -Paquete C:\Paquetes-PilotX\PilotX-1.0.71.bin
```

Soporta instalación limpia (avisa "no habia instalacion previa"). Si algo sale
mal, se deshace solo y deja lo anterior andando. El último paso es abrir la
pantalla para comprobar que arranca.

### 4. Devolver los lotes y el perfil

```powershell
.\Restaurar-AOG.ps1 -Zip C:\Rescate-PilotX\rescate-....zip -Perfil "<el articulado>"
```

- Los lotes que ya existan en destino **no se pisan**: entran como
  `<nombre>~aog`. Con `-Pisar` se invierte.
- Deja el perfil indicado como activo en `C:\PilotX\aog_settings.json` (guarda
  el anterior como `.antes-migracion`).
- Con `-SoloVer` muestra qué haría sin copiar nada.

Al final imprime la geometría que trajo cada perfil: entre ejes, antena, ancho,
secciones. **Eso es lo que hay que comparar contra la máquina real.**

### 5. Dar de alta el equipo en OrbitX

Cliente: **Carrano e Hijos SRL — CUIT 30718026616**.

1. Panel `https://orbitx.agroparallel.com` con superadmin → nueva organización
   (o `POST /api/admin/establecimiento` con
   `{ "nombre": "Carrano e Hijos", "slug": "carrano-e-hijos", ... }`).
2. Equipo → Invitar al dueño por email, rol owner.
3. En PilotX: **Vincular por código**. Los equipos nunca se dan de alta a mano.
4. Dejar el CUIT en `C:\PilotX\cliente.json` (la org todavía no tiene campo
   propio para CUIT).

### 6. Verificación en cabina, antes de entregar

- [ ] Configuración: distancia entre ejes, antena (altura/pivote/lateral),
      ancho de herramienta y número de secciones **contra la máquina real**.
- [ ] Abrir un lote conocido: que estén el lindero y el pintado.
- [ ] GPS con fix y la barra de guía respondiendo.
- [ ] El equipo aparece vinculado en el panel de OrbitX.

## Qué NO hace este kit

- No convierte formatos: si la pantalla tiene AOG **5.x**, los lotes pueden no
  abrir igual y el perfil entra incompleto. Mirá el informe del paso 1: dice la
  versión del `AgOpenGPS.exe` que encontró.
- No borra nada de la pantalla vieja. El AOG original queda instalado; sacarlo
  es una decisión aparte, y conviene recién después de validar PilotX en campo.
