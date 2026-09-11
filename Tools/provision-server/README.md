# Instalador LAN de PilotX (servidor del taller)

Servidor web interno para instalar PilotX en pantallas y tablets nuevas con
**un solo comando**, y dejarlas **asignadas al cliente en OrbitX** sin tocar
nada a mano. Corre en la PC de desarrollo y se maneja desde el navegador.

Reutiliza el kit de `Tools/provision-pantalla` (usuarios locales, limpieza,
energía, firewall, WinRM, runtimes, cliente.json, nombre de equipo) y le
agrega lo que ahí quedaba manual: bajar e instalar el paquete, escribir
`orbitX.json` con identidad propia y vincular el equipo a la organización.

## Arrancar

```powershell
cd Tools\provision-server
node server.js
```

Sin dependencias (Node 18+). La primera vez Windows pregunta si deja a
`node.exe` recibir conexiones: **permitir en redes privadas**, si no la
tablet no llega. La web queda en `http://localhost:8090` y, para la tablet,
en `http://<IP de esta PC>:8090` (la muestra arriba a la derecha).

Opcional `config.json`:

```json
{ "puerto": 8090, "orbitx_url": "https://orbitx.agroparallel.com", "ip_preferida": "192.168.1.12" }
```

## Uso

1. **OrbitX**: entrar con el usuario superadmin. El JWT queda en `estado.json`
   de esta PC (no viaja a la tablet; a la tablet solo le llega su token de
   equipo).
2. **Cliente**: si no existe, "Crear cliente" (crea la org en OrbitX con
   `POST /api/admin/org` y su base `orbitx_<slug>`).
3. **Pedido**: elegir cliente, versión de PilotX (los `PilotX_v*.zip` del
   repo o de `Build\`) y kiosko. Sale el comando para la tablet:

   ```powershell
   irm http://192.168.1.12:8090/instalar.ps1?p=ABC123 | iex
   ```

4. En la tablet (misma red, PowerShell): pegar el comando. Pide admin, baja el
   kit y el paquete, calcula el `device_id` igual que PilotX (MD5 del MAC), se
   registra en el servidor —que lo da de alta en OrbitX y lo asigna al
   cliente—, corre `Provision-Pantalla.ps1`, extrae PilotX en `C:\PilotX`,
   escribe `C:\PilotX\Engine\orbitX.json`, activa el kiosko y pide reiniciar.
   El avance se ve en la tabla de pedidos.
5. **Equipos**: lista los dispositivos de OrbitX y permite reasignarlos a
   otra organización.

## Archivos que sirve

- `/kit/<nombre>`: `Provision-Pantalla.ps1`, `PilotX-KioskSetup.exe`
  (`Build\`), branding (`Build\Branding`, `wwwroot\img\fondo.png`) y los
  runtimes `vc_redist.x64.exe` / `MicrosoftEdgeWebview2Setup.exe` desde
  `runtimes\` (botón "Bajar runtimes" los descarga una vez).
- `/paquetes/PilotX_v<ver>.zip`.
- `/instalar.ps1?p=<pedido>`: el script de la tablet con el servidor y el
  pedido ya puestos.

`estado.json` y `runtimes\` no van al repo.
