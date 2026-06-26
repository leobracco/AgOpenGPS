// ============================================================================
// OverlayAutoOpener.cs
//
// Cuando el operario activa un implemento (perfil) que tiene nodos asignados
// (ImplementoDto.NodosUids), encendemos automáticamente el overlay del
// producto correspondiente sobre el mapa de PilotX, así no tiene que ir al
// Hub a habilitarlo. Solo PRENDE — nunca apaga (si el operario lo cerró a
// mano, respetamos esa decisión hasta que vuelva a cambiar de perfil).
//
// Mapeo: por cada UID asignado al implemento activo, buscamos su Tipo en
// nodos.json (NodosCuratedService.Load → Aceptados). El match es
// case-insensitive y por substring (mismo criterio que
// WidgetQuantiXController: "n.Type.IndexOf("quantix", IgnoreCase) >= 0").
//
//   tipo contiene "quantix"  → enciende QXOverlay
//   tipo contiene "vistax"   → enciende VxOverlay
//   tipo contiene "flowx"    → enciende FXOverlay
//
// FormGPS releé overlayPrefs.json cada 250ms, así que basta con Save y los
// widgets aparecen sin reiniciar (ver doc en OverlayPrefsController).
// ============================================================================

using System;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public static class OverlayAutoOpener
    {
        /// <summary>
        /// Lee el implemento activo y enciende los overlays de los productos
        /// presentes en NodosUids. No-op si no hay implemento activo, no hay
        /// servicios, o ya estaban encendidos. Atrapa cualquier excepción —
        /// auto-abrir overlays nunca debe romper el flujo de SetActive.
        /// </summary>
        public static void EnsureForActiveImplemento(
            IImplementoService implementos,
            INodosCuratedService curated)
        {
            if (implementos == null || curated == null) return;
            try
            {
                ImplementoDto imp = implementos.GetImplemento();
                if (imp == null || imp.NodosUids == null || imp.NodosUids.Count == 0) return;

                NodosCuratedDto curado = curated.Load();
                if (curado == null || curado.Aceptados == null) return;

                bool needQx = false, needVx = false, needFx = false;

                foreach (string uid in imp.NodosUids)
                {
                    if (string.IsNullOrEmpty(uid)) continue;
                    // O(n*m) — flotas reales tienen <30 nodos y <30 UIDs por implemento.
                    foreach (NodoAceptadoDto a in curado.Aceptados)
                    {
                        if (a == null || a.Uid == null) continue;
                        if (!string.Equals(a.Uid, uid, StringComparison.OrdinalIgnoreCase)) continue;
                        string tipo = a.Tipo ?? "";
                        if (tipo.IndexOf("quantix", StringComparison.OrdinalIgnoreCase) >= 0) needQx = true;
                        else if (tipo.IndexOf("vistax", StringComparison.OrdinalIgnoreCase) >= 0) needVx = true;
                        else if (tipo.IndexOf("flowx",  StringComparison.OrdinalIgnoreCase) >= 0) needFx = true;
                        break;
                    }
                    if (needQx && needVx && needFx) break; // ya prendimos todo lo posible
                }

                if (!needQx && !needVx && !needFx) return;

                OverlayPrefsDto prefs = OverlayPrefsService.Instance.Load();
                if (prefs == null) prefs = new OverlayPrefsDto();

                bool changed = false;
                if (needQx && !prefs.QXOverlay) { prefs.QXOverlay = true; changed = true; }
                if (needVx && !prefs.VxOverlay) { prefs.VxOverlay = true; changed = true; }
                if (needFx && !prefs.FXOverlay) { prefs.FXOverlay = true; changed = true; }

                if (changed) OverlayPrefsService.Instance.Save(prefs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[OverlayAutoOpener] " + ex.Message);
            }
        }
    }
}
