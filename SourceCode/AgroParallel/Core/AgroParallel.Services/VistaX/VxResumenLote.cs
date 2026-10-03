// ============================================================================
// VxResumenLote.cs — promedios por surco de todo el lote (registro VistaX).
//
// Se arma sumando los tramos (VxRegistroTramos) del lote:
//   · sem/m: ponderado por DISTANCIA (semillas totales / metros),
//   · dobles / fallas: ponderados por cantidad de espacios → son exactamente
//     los % sobre todos los espacios del lote,
//   · singulación = 100 − dobles − fallas,
//   · CV: promedio de los CV de cada tramo ponderado por espacios. No es el
//     CV "agrupado" exacto (para eso harían falta las sumas de cuadrados de
//     cada tramo) pero con tramos de 10 m la diferencia es de décimas.
//
// Sale como resumen.json (lo sube OrbitXSync, subtipo vistax_resumen) y como
// tabla en el informe de la Tarea + CSV para planilla.
// Clase pura: VxRegistroArchivoTests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgroParallel.Services.VistaX
{
    public sealed class VxResumenSurco
    {
        public int Tren;
        public int Bajada;
        public double DistM;
        /// <summary>-1 = sin lecturas de sem/m.</summary>
        public double SemM = -1;
        public long NEspacios;
        public double SingulacionPct;
        public double DoblesPct;
        public double FallasPct;
        public double CvPct;

        // acumuladores
        internal double SumSemMxD;
        internal double SumDSemM;
        internal double SumDobxN, SumFalxN, SumCvxN;
    }

    public sealed class VxResumenLote
    {
        public const string Schema = "agp.vistax.resumen/1";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string Lote = "";
        public int Tramos;
        /// <summary>Metros recorridos sembrando (centro de la máquina).</summary>
        public double DistM;
        public DateTime DesdeUtc = DateTime.MinValue;
        public DateTime HastaUtc = DateTime.MinValue;

        private readonly Dictionary<long, VxResumenSurco> _surcos = new Dictionary<long, VxResumenSurco>();

        /// <summary>Surcos ordenados por bajada (y tren).</summary>
        public List<VxResumenSurco> Surcos
        {
            get
            {
                var l = new List<VxResumenSurco>(_surcos.Values);
                l.Sort((x, y) => x.Bajada != y.Bajada ? x.Bajada.CompareTo(y.Bajada) : x.Tren.CompareTo(y.Tren));
                return l;
            }
        }

        public void Sumar(VxTramo t)
        {
            if (t == null) return;
            Tramos++;
            DistM += t.DistM;
            if (DesdeUtc == DateTime.MinValue || t.InicioUtc < DesdeUtc) DesdeUtc = t.InicioUtc;
            if (t.FinUtc > HastaUtc) HastaUtc = t.FinUtc;
            foreach (var s in t.Surcos)
            {
                long k = ((long)s.Tren << 32) | (uint)s.Bajada;
                VxResumenSurco r;
                if (!_surcos.TryGetValue(k, out r))
                {
                    r = new VxResumenSurco { Tren = s.Tren, Bajada = s.Bajada };
                    _surcos[k] = r;
                }
                if (s.SemM >= 0 && t.DistM > 0)
                {
                    r.SumSemMxD += s.SemM * t.DistM;
                    r.SumDSemM += t.DistM;
                    r.DistM += t.DistM;
                    r.SemM = r.SumSemMxD / r.SumDSemM;
                }
                var e = s.Esp;
                if (e != null && e.NEspacios > 0)
                {
                    r.NEspacios += e.NEspacios;
                    r.SumDobxN += e.DoblesPct * e.NEspacios;
                    r.SumFalxN += e.FallasPct * e.NEspacios;
                    r.SumCvxN += e.CvPct * e.NEspacios;
                    r.DoblesPct = r.SumDobxN / r.NEspacios;
                    r.FallasPct = r.SumFalxN / r.NEspacios;
                    r.SingulacionPct = 100.0 - r.DoblesPct - r.FallasPct;
                    r.CvPct = r.SumCvxN / r.NEspacios;
                }
            }
        }

        /// <summary>Promedio simple entre surcos (cada surco pesa igual, como
        /// la franja de promedios del panel). NaN si ningún surco tiene dato.</summary>
        public double PromedioSemM() { return Prom(s => s.SemM >= 0, s => s.SemM); }
        public double PromedioSingulacion() { return Prom(s => s.NEspacios > 0, s => s.SingulacionPct); }
        public double PromedioDobles() { return Prom(s => s.NEspacios > 0, s => s.DoblesPct); }
        public double PromedioFallas() { return Prom(s => s.NEspacios > 0, s => s.FallasPct); }
        public double PromedioCv() { return Prom(s => s.NEspacios > 0, s => s.CvPct); }

        public bool HayEspaciamiento
        {
            get { foreach (var s in _surcos.Values) if (s.NEspacios > 0) return true; return false; }
        }

        private double Prom(Func<VxResumenSurco, bool> hay, Func<VxResumenSurco, double> v)
        {
            double sum = 0; int n = 0;
            foreach (var s in _surcos.Values) if (hay(s)) { sum += v(s); n++; }
            return n > 0 ? sum / n : double.NaN;
        }

        // ── JSON ────────────────────────────────────────────────────────────

        public string ToJson(DateTime generadoUtc)
        {
            var sb = new StringBuilder(512 + _surcos.Count * 120);
            sb.Append("{\"schema\":\"").Append(Schema).Append("\",\"lote\":");
            VxRegistroArchivo.AppendJsonString(sb, Lote ?? "");
            sb.Append(",\"generado\":\"").Append(Iso(generadoUtc)).Append('"');
            sb.Append(",\"desde\":\"").Append(Iso(DesdeUtc)).Append('"');
            sb.Append(",\"hasta\":\"").Append(Iso(HastaUtc)).Append('"');
            sb.Append(",\"tramos\":").Append(Tramos.ToString(Inv));
            sb.Append(",\"dist_m\":").Append(VxRegistroArchivo.Num(DistM, 0));
            sb.Append(",\"prom\":{\"sem_m\":").Append(NumONull(PromedioSemM(), 2))
              .Append(",\"sing\":").Append(NumONull(PromedioSingulacion(), 1))
              .Append(",\"dob\":").Append(NumONull(PromedioDobles(), 1))
              .Append(",\"fal\":").Append(NumONull(PromedioFallas(), 1))
              .Append(",\"cv\":").Append(NumONull(PromedioCv(), 1)).Append('}');
            sb.Append(",\"surcos\":[");
            bool primero = true;
            foreach (var s in Surcos)
            {
                if (!primero) sb.Append(',');
                primero = false;
                bool hay = s.NEspacios > 0;
                sb.Append("{\"tren\":").Append(s.Tren.ToString(Inv))
                  .Append(",\"surco\":").Append(s.Bajada.ToString(Inv))
                  .Append(",\"dist_m\":").Append(VxRegistroArchivo.Num(s.DistM, 0))
                  .Append(",\"sem_m\":").Append(s.SemM >= 0 ? VxRegistroArchivo.Num(s.SemM, 2) : "null")
                  .Append(",\"n\":").Append(s.NEspacios.ToString(Inv))
                  .Append(",\"sing\":").Append(hay ? VxRegistroArchivo.Num(s.SingulacionPct, 1) : "null")
                  .Append(",\"dob\":").Append(hay ? VxRegistroArchivo.Num(s.DoblesPct, 1) : "null")
                  .Append(",\"fal\":").Append(hay ? VxRegistroArchivo.Num(s.FallasPct, 1) : "null")
                  .Append(",\"cv\":").Append(hay ? VxRegistroArchivo.Num(s.CvPct, 1) : "null")
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Lee un resumen.json. null si no se puede leer.</summary>
        public static VxResumenLote FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    var res = new VxResumenLote();
                    JsonElement e;
                    if (r.TryGetProperty("lote", out e) && e.ValueKind == JsonValueKind.String) res.Lote = e.GetString();
                    res.Tramos = (int)Dbl(r, "tramos", 0);
                    res.DistM = Dbl(r, "dist_m", 0);
                    res.DesdeUtc = Fecha(r, "desde");
                    res.HastaUtc = Fecha(r, "hasta");
                    if (r.TryGetProperty("surcos", out e) && e.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var j in e.EnumerateArray())
                        {
                            var s = new VxResumenSurco
                            {
                                Tren = (int)Dbl(j, "tren", 0),
                                Bajada = (int)Dbl(j, "surco", 0),
                                DistM = Dbl(j, "dist_m", 0),
                                SemM = Dbl(j, "sem_m", -1),
                                NEspacios = (long)Dbl(j, "n", 0),
                                SingulacionPct = Dbl(j, "sing", 0),
                                DoblesPct = Dbl(j, "dob", 0),
                                FallasPct = Dbl(j, "fal", 0),
                                CvPct = Dbl(j, "cv", 0),
                            };
                            res._surcos[((long)s.Tren << 32) | (uint)s.Bajada] = s;
                        }
                    }
                    return res;
                }
            }
            catch (JsonException) { return null; }
        }

        // ── CSV (planilla) ──────────────────────────────────────────────────

        /// <summary>
        /// CSV para abrir en planilla en castellano: separador ';', coma
        /// decimal (NumberFormatInfo propio, nunca la cultura del sistema).
        /// </summary>
        public string ToCsv()
        {
            var nfi = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = "" };
            var sb = new StringBuilder(256 + _surcos.Count * 64);
            sb.Append("Tren;Surco;Metros;sem/m;Singulacion %;Dobles %;Fallas %;CV %;Espacios\r\n");
            foreach (var s in Surcos)
            {
                bool hay = s.NEspacios > 0;
                sb.Append(s.Tren.ToString(Inv)).Append(';')
                  .Append(s.Bajada.ToString(Inv)).Append(';')
                  .Append(s.DistM.ToString("0", nfi)).Append(';')
                  .Append(s.SemM >= 0 ? s.SemM.ToString("0.00", nfi) : "").Append(';')
                  .Append(hay ? s.SingulacionPct.ToString("0.0", nfi) : "").Append(';')
                  .Append(hay ? s.DoblesPct.ToString("0.0", nfi) : "").Append(';')
                  .Append(hay ? s.FallasPct.ToString("0.0", nfi) : "").Append(';')
                  .Append(hay ? s.CvPct.ToString("0.0", nfi) : "").Append(';')
                  .Append(s.NEspacios.ToString(Inv)).Append("\r\n");
            }
            return sb.ToString();
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static string NumONull(double v, int dec)
        {
            return double.IsNaN(v) ? "null" : VxRegistroArchivo.Num(v, dec);
        }

        private static string Iso(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            return d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);
        }

        private static double Dbl(JsonElement r, string k, double porDefecto)
        {
            JsonElement e;
            double d;
            return r.TryGetProperty(k, out e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out d) ? d : porDefecto;
        }

        private static DateTime Fecha(JsonElement r, string k)
        {
            JsonElement e;
            DateTime d;
            if (r.TryGetProperty(k, out e) && e.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(e.GetString(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d;
            return DateTime.MinValue;
        }
    }
}
