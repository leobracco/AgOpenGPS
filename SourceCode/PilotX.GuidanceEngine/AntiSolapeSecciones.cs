// ============================================================================
// AntiSolapeSecciones.cs — implementación del corte por área ya trabajada.
//
// Une las tres piezas de AgroParallel.Coverage con el motor:
//   · CoverageIndex     — dónde se trabajó, indexado para consultar rápido
//   · CoverageGeometry  — cuánto del ancho de la sección cae encima
//   · SolapeEvaluator   — con ese número, ¿corta o no?
//
// Vive acá y no en PilotX.GuidanceEngine.Core a propósito: este proyecto ya
// referencia AgroParallel.Services (lo usa EngineCoverageService), mientras que
// el core de guiado no debería arrastrar MQTTnet ni NetTopologySuite. Ver
// IAntiSolapeSecciones.
//
// NADA de esto reemplaza al lindero ni a la cabecera: el motor ya resolvió esas
// condiciones antes de preguntar acá. Esto responde una sola cosa: si el terreno
// que la sección está por pisar ya está sembrado.
// ============================================================================

using System;
using System.Collections.Generic;
using AgroParallel.Coverage;

namespace PilotX.GuidanceEngine
{
    using AgOpenGPS;

    public sealed class AntiSolapeSecciones : IAntiSolapeSecciones
    {
        private readonly GuidanceEngineHost _host;
        private readonly CoverageIndex _indice;

        // Cursor por tira: hasta dónde ya se consumió patchList. Sin esto habría
        // que releer toda la cobertura en cada fix, que es justo lo que no
        // escala en una jornada larga.
        private readonly List<int> _curParche = new List<int>();
        private readonly List<int> _curVertice = new List<int>();

        /// <summary>
        /// Apagado por defecto. Esto decide si una sección siembra o no; hasta
        /// validarlo en el lote, el default seguro es no intervenir.
        /// </summary>
        public bool Habilitado { get; set; }

        /// <summary>Log de una de cada 40 decisiones, para diagnosticar en cabina.</summary>
        public bool Diagnostico { get; set; }

        private int _llamadas;

        public AntiSolapeSecciones(GuidanceEngineHost host, double tamCeldaM = 4.0)
        {
            _host = host;
            _indice = new CoverageIndex(tamCeldaM);
        }

        /// <summary>Triángulos conocidos. Para diagnóstico.</summary>
        public int TrianguloCount { get { return _indice.CantidadTriangulos; } }

        public void Reiniciar()
        {
            _indice.Limpiar();
            _curParche.Clear();
            _curVertice.Clear();
        }

        // -------------------------------------------------------------------
        // Alimentación incremental del índice
        // -------------------------------------------------------------------

        public void Sincronizar()
        {
            var tiras = _host != null ? _host.TriStripField : null;
            if (tiras == null) return;

            while (_curParche.Count < tiras.Count) { _curParche.Add(0); _curVertice.Add(1); }

            for (int j = 0; j < tiras.Count; j++)
            {
                var tira = tiras[j];
                if (tira == null || tira.patchList == null) continue;
                var parches = tira.patchList;

                int p = _curParche[j];
                int v = _curVertice[j];

                // Si el lote se cerró y patchList se vació, el cursor quedó más
                // adelante que la lista: hay que volver al principio o se
                // perdería toda la cobertura nueva.
                if (p > parches.Count) { p = 0; v = 1; }

                while (p < parches.Count)
                {
                    var parche = parches[p];
                    if (parche == null) { p++; v = 1; continue; }

                    // parche[0] es el HEADER de color, no una coordenada. La
                    // geometría real arranca en [1] — incluir el header dibuja
                    // un triángulo desde ~el origen.
                    if (v < 1) v = 1;

                    // Tira de triángulos: los vértices consecutivos v, v+1, v+2
                    // forman un triángulo, y el siguiente arranca en v+1.
                    while (v + 2 <= parche.Count - 1)
                    {
                        _indice.AgregarTriangulo(
                            parche[v].easting,
                            parche[v].northing,
                            parche[v + 1].easting,
                            parche[v + 1].northing,
                            parche[v + 2].easting,
                            parche[v + 2].northing);
                        v++;
                    }

                    // El último parche es el que sigue creciendo: se deja el
                    // cursor ahí y se sigue la próxima vuelta. Los anteriores ya
                    // están cerrados (PilotX corta a los 61 triángulos y abre uno
                    // nuevo sembrado con color + los dos últimos puntos).
                    if (p == parches.Count - 1) break;

                    p++;
                    v = 1;
                }

                _curParche[j] = p;
                _curVertice[j] = v;
            }
        }

        // -------------------------------------------------------------------
        // Decisión
        // -------------------------------------------------------------------

        public bool SeccionRequeridaOn(
            double izqE, double izqN,
            double derE, double derN,
            double heading,
            double velocidadKmh,
            bool estabaEncendida)
        {
            double dx = derE - izqE;
            double dy = derN - izqN;
            double ancho = Math.Sqrt(dx * dx + dy * dy);

            // Sección sin ancho: no hay nada que decidir, que siga como estaba.
            if (ancho < 0.05) return true;

            double medioAncho = ancho * 0.5;
            double centroE = (izqE + derE) * 0.5;
            double centroN = (izqN + derN) * 0.5;

            // Las distancias de anticipación salen de la MISMA configuración de
            // look-ahead que ya usa PilotX (segundos que tarda la máquina en abrir
            // y en cerrar), convertidos a metros por la velocidad. Así el corte
            // cae donde corresponde y no unos metros tarde.
            double ms = velocidadKmh / 3.6;
            var tool = _host != null ? _host.Tool : null;
            double segApagar = tool != null ? tool.lookAheadOffSetting : 0.0;
            double segEncender = tool != null ? tool.lookAheadOnSetting : 0.0;

            var solapeApagado = _indice.Consultar(
                centroE, centroN, heading, medioAncho, ms * segApagar);
            var solapeEncendido = _indice.Consultar(
                centroE, centroN, heading, medioAncho, ms * segEncender);

            // Umbral de corte = "Cobertura mínima" de Configuración › Secciones
            // (setVehicle_minCoverage). Ver SolapeEvaluator.UmbralApagarDesdeCobertura.
            bool on = SolapeEvaluator.RequeridaOn(SolapeEvaluator.ConCobertura(
                solapeApagado.CoveragePercent,
                solapeEncendido.CoveragePercent,
                estabaEncendida,
                tool != null ? tool.minCoverage : 100));

            if (Diagnostico && ++_llamadas % 40 == 0)
            {
                Console.Error.WriteLine(string.Format(
                    "[AntiSolape] tri={0} centro=({1:F1},{2:F1}) hdg={3:F2} medioAncho={4:F2} " +
                    "lookOff={5:F2}m lookOn={6:F2}m solapeOff={7:F3} solapeOn={8:F3} -> {9}",
                    _indice.CantidadTriangulos, centroE, centroN, heading, medioAncho,
                    ms * segApagar, ms * segEncender,
                    solapeApagado.CoveragePercent, solapeEncendido.CoveragePercent,
                    on ? "ON" : "OFF"));
            }

            return on;
        }
    }
}
