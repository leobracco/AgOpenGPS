# StormX — Interfaz web (Hub PilotX)

Estación meteorológica móvil de campo. Muestra viento, temperatura, humedad,
presión y Delta-T, y emite un veredicto de **pulverización OK / NO pulverizar**
según umbrales operativos.

**Archivos:** `pages/stormx.html` + `js/stormx.js`
**Backend:** `StormXLiveService` (`/api/stormx/*`)
**Persistencia:** `stormX.json` (umbrales + nodos)

> Estado actual: el wiring de la página está completo, pero los KPIs quedan en
> "—" hasta que el firmware StormX publique MQTT (`agp/storm/{uid}/status_live`).

---

## Qué hace la pantalla

### KPIs en vivo (`renderSample`)
- **Viento** (m/s) + dirección (°), **Temperatura** (°C) + Delta-T, **Humedad** (%)
  + presión (hPa).
- **Pulverización** — `verdict(sample, limits)`: compara la lectura contra los
  umbrales y devuelve `PULVERIZAR OK` o `NO PULVERIZAR · <motivos>` (viento alto,
  viento muy bajo, humedad baja, temp alta, Δ-T alto). El pill de cabecera refleja
  el color/estado. `ageStr` muestra la antigüedad de la última lectura.

### Umbrales operativos
Tarjeta con los límites (viento máx/mín, humedad mín, temp máx, Delta-T máx). Hoy
son de solo lectura — el editor con persistencia en `stormX.json` está en
"Próximamente".

### Nodos StormX (`renderNodos`)
Lista nodos LAN con online/uid/ip/firmware. `pollNodos` (`/api/stormx/nodos`)
refresca cada 3 s.

---

## Datos en vivo

- `loadCfg` → `GET /api/stormx/config`: trae los umbrales (`cfg.limits`).
- `pollLive` (`tick`, 1 Hz) → `GET /api/stormx/live`: snapshot por nodo; toma el
  primer nodo online como lectura activa (la página muestra un solo set de KPIs).
- `startPolling` / `stopPolling`: pausan al ocultar la pestaña (`visibilitychange`).
- `fmtNum` / `escapeHtml`: helpers de formato/seguridad.

---

## Endpoints consumidos

| Método | Ruta | Uso |
|---|---|---|
| GET | `/api/stormx/config` | umbrales operativos |
| GET | `/api/stormx/live` | telemetría por nodo |
| GET | `/api/stormx/nodos` | descubrimiento LAN |

## MQTT (vía CoreX, prefijo `agp/storm/`)

| Topic | Sentido |
|---|---|
| `{uid}/announcement` | ESP→PC |
| `{uid}/status_live` | ESP→PC (viento, temp, humedad, presión, Δ-T) |

## Pendiente (roadmap en la propia página)
- Editor de umbrales con persistencia.
- Histórico 24 h con gráficos.
- Bloqueo automático de FlowX en condiciones adversas.
- Logging a campo (CSV + sync OrbitX).
- Mapa de calor de viento sobre el lote.
