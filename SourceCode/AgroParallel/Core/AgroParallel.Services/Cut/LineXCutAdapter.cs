// ============================================================================
// LineXCutAdapter.cs - Adapter de corte para LineX (corte surco por surco).
//
// Traduce el estado de corte de PilotX al bitmask que el firmware LineX consume
// en agp/linex/{uid}/sections. El firmware acepta array [1,0,...] (índice = surco)
// o {lo,hi}; usamos el array por consistencia con SectionX.
//
// El mapeo surco→sección de PilotX vive en lineX.json (LxSurcoDto.SeccionAOG).
// invert/failsafe los resuelve el firmware (Sections[i].Invert/FailsafeOpen),
// así que acá mandamos la intención cruda de apertura por surco.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Cut
{
    public sealed class LineXCutAdapter : ICutAdapter
    {
        private const int MaxSections = 16; // = MAX_SECTIONS del firmware

        private readonly ILineXConfigService _svc;
        private LineXConfigDto _config;

        public string Product { get { return "linex"; } }
        public int NodeCount { get { var c = _config; return c != null && c.Nodos != null ? c.Nodos.Count : 0; } }

        public LineXCutAdapter() : this(new LineXConfigService()) { }

        public LineXCutAdapter(ILineXConfigService svc)
        {
            _svc = svc ?? new LineXConfigService();
            Reload();
        }

        public void Reload()
        {
            try { _config = _svc.Load(); }
            catch { if (_config == null) _config = new LineXConfigDto(); }
        }

        private static string TopicFor(string uid)
        {
            return "agp/linex/" + uid + "/sections";
        }

        public IEnumerable<CutCommand> ComputePublishes(AogStateSnapshot snap, PositionHistory hist)
        {
            var cfg = _config;
            if (cfg == null || cfg.Nodos == null || snap == null) yield break;
            bool[] secAOG = snap.SectionOnRequest;
            if (secAOG == null) yield break;

            foreach (var nodo in cfg.Nodos)
            {
                if (nodo == null || !nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;

                int width = WidthFor(nodo);
                if (width <= 0) continue;

                var bits = new bool[width];
                if (nodo.Surcos != null)
                {
                    foreach (var surco in nodo.Surcos)
                    {
                        if (surco == null) continue;
                        if (surco.Idx < 0 || surco.Idx >= width) continue;
                        if (surco.SeccionAOG < 1) continue; // surco sin asignar → cerrado
                        int secIdx = surco.SeccionAOG - 1;
                        bits[surco.Idx] = secIdx < secAOG.Length && secAOG[secIdx];
                    }
                }

                yield return new CutCommand(nodo.Uid, TopicFor(nodo.Uid),
                    BuildPayload(bits), BitsToInt(bits));
            }
        }

        public IEnumerable<CutCommand> OffCommands()
        {
            var cfg = _config;
            if (cfg == null || cfg.Nodos == null) yield break;
            foreach (var nodo in cfg.Nodos)
            {
                if (nodo == null || string.IsNullOrEmpty(nodo.Uid)) continue;
                int width = WidthFor(nodo);
                if (width <= 0) continue;
                var zeros = new bool[width];
                yield return new CutCommand(nodo.Uid, TopicFor(nodo.Uid),
                    BuildPayload(zeros), BitsToInt(zeros));
            }
        }

        // Ancho del array = section_count del nodo, clamp 1..MAX_SECTIONS. Si la
        // config quedó en 0, derivamos del mayor idx de surco para no enmudecer.
        private static int WidthFor(LxNodoConfigDto nodo)
        {
            int w = nodo.SectionCount;
            if (w <= 0 && nodo.Surcos != null)
            {
                foreach (var s in nodo.Surcos)
                    if (s != null && s.Idx + 1 > w) w = s.Idx + 1;
            }
            if (w > MaxSections) w = MaxSections;
            return w;
        }

        private static string BuildPayload(bool[] bits)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < bits.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(bits[i] ? '1' : '0');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static int[] BitsToInt(bool[] bits)
        {
            var arr = new int[bits.Length];
            for (int i = 0; i < bits.Length; i++) arr[i] = bits[i] ? 1 : 0;
            return arr;
        }
    }
}
