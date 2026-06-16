// ============================================================================
// SectionXCutAdapter.cs - Adapter de corte para SectionX (relays PCA9685).
//
// Porta la lógica que vivía en SectionXBridge.OnTick: mapeo explícito
// cable→sección de PilotX, desfase de tren trasero vía PositionHistory, y el
// armado del bitmask que el firmware QuantiX-relay consume en
// agp/quantix/{uid}/sections (array [1,0,...], índice = cable-1).
//
// El transporte/dedup/timing quedan en el CutDispatcher. Acá solo se traduce
// estado de PilotX -> intención de corte (CutCommand).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.SectionX;

namespace AgroParallel.Cut
{
    public sealed class SectionXCutAdapter : ICutAdapter
    {
        private SectionXConfig _config;

        public string Product { get { return "sectionx"; } }
        public int NodeCount { get { var c = _config; return c != null ? c.Nodos.Count : 0; } }

        public SectionXCutAdapter()
        {
            Reload();
        }

        public void Reload()
        {
            try { _config = SectionXConfig.Load(); }
            catch { if (_config == null) _config = new SectionXConfig(); }
        }

        private static string TopicFor(string uid)
        {
            return "agp/quantix/" + uid + "/sections";
        }

        public IEnumerable<CutCommand> ComputePublishes(AogStateSnapshot snap, PositionHistory hist)
        {
            var cfg = _config;
            if (cfg == null || snap == null) yield break;
            bool[] secAOG = snap.SectionOnRequest;
            if (secAOG == null) yield break;

            // Cache de secciones del tren trasero por distancia: varios nodos pueden
            // compartir DistanciaEntreTrenes; el cálculo (caro) se hace una vez por
            // distancia en este tick.
            Dictionary<double, bool[]> secTraseroCache = null;

            foreach (var nodo in cfg.Nodos)
            {
                if (nodo == null || !nodo.Habilitado || string.IsNullOrEmpty(nodo.Uid)) continue;

                bool[] secTrasero = secAOG;
                if (nodo.DistanciaEntreTrenes > 0.05 && hist != null)
                {
                    if (secTraseroCache == null) secTraseroCache = new Dictionary<double, bool[]>();
                    bool[] cached;
                    if (!secTraseroCache.TryGetValue(nodo.DistanciaEntreTrenes, out cached))
                    {
                        cached = hist.GetSectionsAtDistanceBack(nodo.DistanciaEntreTrenes) ?? secAOG;
                        secTraseroCache[nodo.DistanciaEntreTrenes] = cached;
                    }
                    secTrasero = cached;
                }

                // Armar los 16 bits de relay según el mapeo cable->seccion.
                var bits = new bool[16];
                int maxCable = 0;
                foreach (var cable in nodo.Cables)
                {
                    if (cable == null) continue;
                    if (cable.Cable > maxCable) maxCable = cable.Cable;
                    if (cable.Cable < 1 || cable.Cable > 16) continue;
                    if (cable.SeccionAOG < 1) continue;

                    int secIdx = cable.SeccionAOG - 1;
                    bool[] fuente = (cable.Tren == 0) ? secAOG : secTrasero;
                    bits[cable.Cable - 1] = secIdx < fuente.Length && fuente[secIdx];
                }

                int width = Math.Max(maxCable, 8);
                yield return new CutCommand(nodo.Uid, TopicFor(nodo.Uid),
                    BuildPayload(bits, width), BitsToInt(bits, width));
            }
        }

        public IEnumerable<CutCommand> OffCommands()
        {
            var cfg = _config;
            if (cfg == null) yield break;
            foreach (var n in cfg.Nodos)
            {
                if (n == null || string.IsNullOrEmpty(n.Uid)) continue;
                int width = 14; // ancho histórico del SendAllOff legacy
                var zeros = new bool[width];
                yield return new CutCommand(n.Uid, TopicFor(n.Uid),
                    BuildPayload(zeros, width), BitsToInt(zeros, width));
            }
        }

        /// <summary>Secuencia de test de relés: un cable activo a la vez + apagado
        /// final. El CutDispatcher la publica con su cliente MQTT respetando stepMs.</summary>
        public List<CutCommand> BuildTestSequence(string uid, int[] cables)
        {
            var steps = new List<CutCommand>();
            if (string.IsNullOrEmpty(uid) || cables == null || cables.Length == 0) return steps;

            int maxCable = 0;
            foreach (var c in cables) if (c > maxCable) maxCable = c;
            int width = Math.Max(maxCable, 8);
            string topic = TopicFor(uid);

            foreach (var cable in cables)
            {
                if (cable < 1 || cable > 16) continue;
                var bits = new bool[width];
                bits[cable - 1] = true;
                steps.Add(new CutCommand(uid, topic, BuildPayload(bits, width), BitsToInt(bits, width)));
            }
            // Apagar todo al final.
            var off = new bool[width];
            steps.Add(new CutCommand(uid, topic, BuildPayload(off, width), BitsToInt(off, width)));
            return steps;
        }

        private static string BuildPayload(bool[] bits, int width)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < width; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append((i < bits.Length && bits[i]) ? '1' : '0');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static int[] BitsToInt(bool[] bits, int width)
        {
            var arr = new int[width];
            for (int i = 0; i < width; i++) arr[i] = (i < bits.Length && bits[i]) ? 1 : 0;
            return arr;
        }
    }
}
