// ============================================================================
// EnginePlanimetriaService.cs — planimetría fase 3 en la cabina, atada al
// motor headless.
//
// APAGADA de fábrica (planimetria.json → habilitada=false): apagada no lee
// Elevation.txt, no calcula y no sirve capa. Prendida:
//   · calcula el mapa de alturas del lote abierto EN SEGUNDO PLANO (hilo de
//     prioridad baja: el pipeline de fix no espera a nadie) con el port C# de
//     OrbitX (AgOpenGPS.Core/Classes/Planimetria.cs). Arranca solo la primera
//     vez que se abre el lote con la función prendida; después, con
//     "Recalcular".
//   · sirve la capa para el mapa ya en el plano local del lote;
//   · clasifica loma / media loma / bajo y, cuando el operario lo pide,
//     escribe la prescripción GeoJSON en la carpeta de prescripciones y la
//     activa con PrescripcionService (la MISMA maquinaria que una prescripción
//     de OrbitX: QuantiX la toma por GetDoseAt / la capa Shape del lote);
//   · cuando el operario lo pide, crea y activa una guía curva sobre una curva
//     de nivel (GuidanceEngineHost.PlanimetriaInstalarGuiaCurva).
//
// Ni el mapa ni los ambientes tocan el guiado ni la dosis: solo las dos
// acciones explícitas (Crear guía, Generar prescripción).
//
// Por qué no se baja el mapa de OrbitX: GET /api/aog/lotes/:nombre/planimetria
// es solo JWT (panel web); con la autenticación de equipo responde 401. Ver la
// cabecera de Planimetria.cs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using AgLibrary.Logging;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services;
using AgroParallel.Services.Abstractions;

namespace AgOpenGPS
{
    public sealed class EnginePlanimetriaService : IPlanimetriaService
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string ArchivoPrefs = "planimetria.json";

        private readonly GuidanceEngineHost _host;
        private readonly object _lock = new object();
        private Prefs _prefs;

        // ---- cálculo (todo bajo _lock) ----
        private string _loteCalc;              // lote del último cálculo pedido
        private string _estado = "sin_calcular";
        private string _motivo;
        private bool _calculando;
        private ResultadoPlanimetria _res;
        private ResultadoAmbientacion _amb;
        private DateTime _calcUtc;
        private int _rev;
        private PlanimetriaCapaDto _capa;
        private int _capaRev = -1;
        private double? _cotaGuia;
        private string _ultimaGuia, _ultimaPresc;

        public EnginePlanimetriaService(GuidanceEngineHost host)
        {
            _host = host;
            _prefs = CargarPrefs();
        }

        // =====================================================================
        //  IPlanimetriaService
        // =====================================================================

        public PlanimetriaEstadoDto Estado()
        {
            lock (_lock)
            {
                string lote = LoteAbierto();
                SincronizarLote(lote);
                // Primera vez en este lote con la función prendida: calcular solo.
                if (_prefs.Habilitada && lote != null && _estado == "sin_calcular" && !_calculando) ArrancarCalculo(lote);
                return Dto(lote);
            }
        }

        public PlanimetriaCapaDto Capa()
        {
            lock (_lock)
            {
                if (!_prefs.Habilitada) return new PlanimetriaCapaDto { Ok = false, Error = "apagado" };
                SincronizarLote(LoteAbierto());
                if (_res == null || !_res.Ok) return new PlanimetriaCapaDto { Ok = false, Error = "sin_mapa", Rev = _rev };
                if (_capa != null && _capaRev == _rev) return _capa;
                _capa = ArmarCapaDto();
                _capaRev = _rev;
                return _capa;
            }
        }

        public PlanimetriaEstadoDto Configurar(PlanimetriaConfigRequest req)
        {
            lock (_lock)
            {
                var p = _prefs;
                bool reclasificar = false, guardar = false;
                if (req.Habilitada.HasValue && req.Habilitada.Value != p.Habilitada)
                {
                    p.Habilitada = req.Habilitada.Value;
                    guardar = true;
                    Log.EventWriter("Planimetria en la cabina: " + (p.Habilitada ? "prendida" : "apagada"));
                    if (!p.Habilitada) { p.CapaVisible = false; _cotaGuia = null; }
                    _rev++;
                }
                if (req.CapaVisible.HasValue && req.CapaVisible.Value != p.CapaVisible)
                {
                    p.CapaVisible = req.CapaVisible.Value && p.Habilitada;
                    guardar = true;
                    _rev++;
                }
                if (req.ModoCapa == "alturas" || req.ModoCapa == "ambientes")
                {
                    if (req.ModoCapa != p.ModoCapa) { p.ModoCapa = req.ModoCapa; guardar = true; _rev++; }
                }
                if (req.ModoAmbientes == "percentil" || req.ModoAmbientes == "desnivel")
                {
                    if (req.ModoAmbientes != p.ModoAmbientes) { p.ModoAmbientes = req.ModoAmbientes; reclasificar = true; }
                }
                if (req.PBajo.HasValue) { p.PBajo = Recortar(req.PBajo.Value, 1, 49); reclasificar = true; }
                if (req.PLoma.HasValue) { p.PLoma = Recortar(req.PLoma.Value, 51, 99); reclasificar = true; }
                if (req.DBajoM.HasValue) { p.DBajoM = Recortar(req.DBajoM.Value, 0.02, 5); reclasificar = true; }
                if (req.DLomaM.HasValue) { p.DLomaM = Recortar(req.DLomaM.Value, 0.02, 5); reclasificar = true; }
                if (req.DosisBajo.HasValue) { p.DosisBajo = Recortar(req.DosisBajo.Value, 0, 1e6); guardar = true; }
                if (req.DosisMedia.HasValue) { p.DosisMedia = Recortar(req.DosisMedia.Value, 0, 1e6); guardar = true; }
                if (req.DosisLoma.HasValue) { p.DosisLoma = Recortar(req.DosisLoma.Value, 0, 1e6); guardar = true; }
                if (req.SinCotaGuia) { if (_cotaGuia != null) { _cotaGuia = null; _rev++; } }
                else if (req.CotaGuiaM.HasValue && !double.IsNaN(req.CotaGuiaM.Value))
                {
                    _cotaGuia = Math.Round(req.CotaGuiaM.Value, 3);
                    _rev++;
                }
                if (reclasificar)
                {
                    guardar = true;
                    Reclasificar();
                }
                if (guardar) GuardarPrefs();
                string lote = LoteAbierto();
                SincronizarLote(lote);
                if (p.Habilitada && lote != null && _estado == "sin_calcular" && !_calculando) ArrancarCalculo(lote);
                return Dto(lote);
            }
        }

        public PlanimetriaEstadoDto Calcular()
        {
            lock (_lock)
            {
                string lote = LoteAbierto();
                if (!_prefs.Habilitada) { var d = Dto(lote); d.Ok = false; d.Error = "apagado"; return d; }
                SincronizarLote(lote);
                if (lote != null && !_calculando) ArrancarCalculo(lote);
                return Dto(lote);
            }
        }

        public PlanimetriaAccionDto CrearGuia(PlanimetriaGuiaRequest req)
        {
            ResultadoPlanimetria R;
            lock (_lock)
            {
                var err = Precondiciones();
                if (err != null) return err;
                R = _res;
            }

            double e0, n0, lat, lon;
            bool hayPos = _host.PlanimetriaPosicion(out e0, out n0, out lat, out lon);
            double cota = req.CotaM ?? (hayPos ? PlanimetriaCabina.CotaEn(R, lat, lon) : double.NaN);
            if (double.IsNaN(cota) || double.IsInfinity(cota))
                return Falla("sin_cota", "Sin GPS o con el tractor fuera del mapa: elegí la cota con − / +.");

            var nivel = Planimetria.CurvaDeCota(R.Grilla, cota);
            var lineas = PlanimetriaCabina.LineasLocales(R, nivel, _host.PlanimetriaALocal);
            if (!hayPos)
            {
                // Sin posición: la referencia es el centro del mapa (gana la línea más cercana al centro).
                double la, lo;
                R.CeldaALatLon((R.Grilla.Nx - 1) / 2.0, (R.Grilla.Ny - 1) / 2.0, out la, out lo);
                _host.PlanimetriaALocal(la, lo, out e0, out n0);
            }
            var pts = GuiaCurvaNivel.Preparar(lineas, e0, n0);
            if (pts == null)
                return Falla("sin_curva", "En la cota " + Metros(cota) + " no hay una curva de al menos "
                    + GuiaCurvaNivel.LargoMinimoPorDefecto.ToString("0", Inv) + " m.");

            string nombre = string.IsNullOrWhiteSpace(req.Nombre) ? "Curva de nivel " + Metros(cota) : req.Nombre.Trim();
            string error = _host.PlanimetriaInstalarGuiaCurva(pts, nombre);
            if (error != null)
            {
                string msg = error == "smartpath" ? "Apagá \"Guía por última pasada\": reemplazaría esta guía en la próxima cabecera."
                    : error == "sin_lote" ? "Abrí un lote primero."
                    : "La curva quedó demasiado corta para una guía.";
                return Falla(error, msg);
            }
            lock (_lock)
            {
                _ultimaGuia = nombre;
                _cotaGuia = Math.Round(cota, 3);
                _rev++;
            }
            return new PlanimetriaAccionDto
            {
                Ok = true,
                Nombre = nombre,
                CotaM = Math.Round(cota, 3),
                Puntos = pts.Count,
                Mensaje = "Guía \"" + nombre + "\" creada y activa.",
            };
        }

        public PlanimetriaAccionDto CrearPrescripcion()
        {
            ResultadoPlanimetria R;
            ResultadoAmbientacion A;
            double[] dosis;
            string lote;
            lock (_lock)
            {
                var err = Precondiciones();
                if (err != null) return err;
                R = _res;
                A = _amb;
                dosis = new[] { 0, _prefs.DosisBajo, _prefs.DosisMedia, _prefs.DosisLoma };
                lote = _loteCalc;
            }
            if (A == null) return Falla("sin_mapa", "Todavía no hay ambientes calculados.");
            if (dosis[1] <= 0 && dosis[2] <= 0 && dosis[3] <= 0)
                return Falla("sin_dosis", "Cargá la dosis de al menos un ambiente.");

            string geo = AmbientacionAltimetria.PrescripcionGeoJson(R, A, dosis);
            int poligonos = AmbientacionAltimetria.Rectangulos(A.Zona, R.Grilla.Nx, R.Grilla.Ny).Count;
            string archivo = "Altimetria " + NombreSeguro(lote) + ".geojson";
            string dir = Path.Combine(AgpPaths.ConfigRoot, "data", "prescripciones");
            try
            {
                Directory.CreateDirectory(dir);
                string destino = Path.Combine(dir, archivo);
                string tmp = destino + ".tmp";
                File.WriteAllText(tmp, geo, new UTF8Encoding(false));
                if (File.Exists(destino)) File.Replace(tmp, destino, null);
                else File.Move(tmp, destino);
            }
            catch (Exception ex)
            {
                Log.EventWriter("Planimetria: no se pudo escribir la prescripcion: " + ex.Message);
                return Falla("disco", "No se pudo guardar la prescripción: " + ex.Message);
            }

            var svc = new PrescripcionService();
            string id = null;
            foreach (var it in svc.ListAvailable())
                if (string.Equals(it.Archivo, archivo, StringComparison.OrdinalIgnoreCase)) { id = it.Id; break; }
            if (id == null || !svc.SetActive(id, "dosis"))
                return Falla("activar", "Se guardó \"" + archivo + "\" pero no se pudo activar. Activala desde QuantiX › Configurar › Shape.");

            lock (_lock) { _ultimaPresc = archivo; }
            Log.EventWriter(string.Format(Inv, "Planimetria: prescripcion por ambientes '{0}' activa ({1} poligonos; bajo {2}, media {3}, loma {4})",
                archivo, poligonos, dosis[1], dosis[2], dosis[3]));
            return new PlanimetriaAccionDto
            {
                Ok = true,
                Nombre = archivo,
                PrescripcionId = id,
                Poligonos = poligonos,
                Mensaje = "Prescripción \"" + archivo + "\" activa en el lote.",
            };
        }

        // =====================================================================
        //  Cálculo en segundo plano
        // =====================================================================

        private void ArrancarCalculo(string lote)
        {
            _calculando = true;
            _estado = "calculando";
            _motivo = null;
            var opciones = new OpcionesPlanimetria { Res = 3, IntervaloAuto = true };
            var hilo = new Thread(() => Calcular(lote, opciones))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "PlanimetriaCalculo",
            };
            hilo.Start();
        }

        private void Calcular(string lote, OpcionesPlanimetria opciones)
        {
            ResultadoPlanimetria R = null;
            string estado, motivo = null;
            try
            {
                string ruta = _host.PlanimetriaArchivoElevacion();
                if (ruta == null) { estado = "sin_calcular"; }
                else if (!File.Exists(ruta))
                {
                    estado = "sin_datos";
                    motivo = "El lote no tiene alturas registradas. Prendé \"Registrar elevación\" y trabajá con RTK fijo.";
                }
                else
                {
                    var acc = new AcumuladorElevacion();
                    using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) acc.AgregarLineas(sr);
                    R = Planimetria.CalcularDesdePuntos(acc.Resultado(), opciones);
                    if (R.Ok) estado = "listo";
                    else { estado = "sin_datos"; motivo = MotivoCorto(R.Motivo); }
                }
            }
            catch (Exception ex)
            {
                estado = "error";
                motivo = ex.Message;
                Log.EventWriter("Planimetria: error calculando el mapa de " + lote + ": " + ex.Message);
            }

            lock (_lock)
            {
                _calculando = false;
                if (!string.Equals(_loteCalc, lote, StringComparison.Ordinal)) return;   // cambiaron de lote en el medio
                _estado = estado;
                _motivo = motivo;
                if (R != null && R.Ok)
                {
                    _res = R;
                    _calcUtc = DateTime.UtcNow;
                    Reclasificar();
                    Log.EventWriter(string.Format(Inv, "Planimetria: mapa de {0} listo — {1} puntos, {2} pasadas, desnivel {3} m, {4} ms",
                        lote, R.Stats.PuntosUsados, R.Stats.Pasadas, R.Stats.DesnivelM, R.Stats.Ms));
                }
                _rev++;
            }
        }

        private static string MotivoCorto(string m)
        {
            if (m == "sin puntos con RTK fijo") return "Elevation.txt no tiene puntos con RTK fijo.";
            if (m == "pocos puntos con RTK fijo") return "Todavía hay muy pocos puntos con RTK fijo (hacen falta 50).";
            return m;
        }

        // =====================================================================
        //  Internos (bajo _lock)
        // =====================================================================

        private string LoteAbierto()
            => _host.IsJobStarted && !string.IsNullOrEmpty(_host.currentFieldDirectory) ? _host.currentFieldDirectory : null;

        /// <summary>Otro lote (o ninguno): se suelta el mapa del anterior.</summary>
        private void SincronizarLote(string lote)
        {
            if (string.Equals(lote, _loteCalc, StringComparison.Ordinal)) return;
            _loteCalc = lote;
            _res = null;
            _amb = null;
            _estado = "sin_calcular";
            _motivo = null;
            _cotaGuia = null;
            _capa = null;
            _rev++;
        }

        private void Reclasificar()
        {
            if (_res == null || !_res.Ok) { _amb = null; return; }
            _amb = AmbientacionAltimetria.Clasificar(_res.Grilla, new ConfigAmbientacion
            {
                Modo = _prefs.ModoAmbientes == "desnivel" ? ModoAmbientacion.Desnivel : ModoAmbientacion.Percentil,
                PBajo = _prefs.PBajo,
                PLoma = _prefs.PLoma,
                DBajoM = _prefs.DBajoM,
                DLomaM = _prefs.DLomaM,
            });
            _rev++;
        }

        private PlanimetriaAccionDto Precondiciones()
        {
            if (!_prefs.Habilitada) return Falla("apagado", "La planimetría está apagada.");
            string lote = LoteAbierto();
            SincronizarLote(lote);
            if (lote == null) return Falla("sin_lote", "Abrí un lote primero.");
            if (_res == null || !_res.Ok) return Falla("sin_mapa", "Todavía no hay mapa de alturas de este lote.");
            return null;
        }

        private static PlanimetriaAccionDto Falla(string error, string mensaje)
            => new PlanimetriaAccionDto { Ok = false, Error = error, Mensaje = mensaje };

        private PlanimetriaEstadoDto Dto(string lote)
        {
            var p = _prefs;
            var d = new PlanimetriaEstadoDto
            {
                Habilitada = p.Habilitada,
                CapaVisible = p.CapaVisible && p.Habilitada,
                ModoCapa = p.ModoCapa,
                Lote = lote,
                Rev = _rev,
                CotaGuiaM = _cotaGuia,
                UltimaGuia = _ultimaGuia,
                UltimaPrescripcion = _ultimaPresc,
                Dosis = new PlanimetriaDosisDto { Bajo = p.DosisBajo, Media = p.DosisMedia, Loma = p.DosisLoma },
                Ambientes = new PlanimetriaAmbientesDto
                {
                    Modo = p.ModoAmbientes,
                    PBajo = p.PBajo,
                    PLoma = p.PLoma,
                    DBajoM = p.DBajoM,
                    DLomaM = p.DLomaM,
                },
            };
            if (!p.Habilitada) { d.Estado = "apagado"; return d; }
            if (lote == null) { d.Estado = "sin_lote"; return d; }
            d.Estado = _estado == "sin_calcular" ? (_calculando ? "calculando" : "sin_datos") : _estado;
            d.Motivo = _motivo;
            var R = _res;
            if (R != null && R.Ok)
            {
                var st = R.Stats;
                d.PuntosRtk = st.PuntosRtk;
                d.Pasadas = st.Pasadas;
                d.ResM = R.Res;
                d.IntervaloM = R.Intervalo;
                d.ZMinM = st.ZMinM;
                d.ZMaxM = st.ZMaxM;
                d.DesnivelM = st.DesnivelM;
                d.AreaHa = st.AreaCubiertaHa;
                d.PendienteMediaPct = st.PendienteMediaPct;
                d.PendienteMaxPct = st.PendienteMaxPct;
                d.BajosCantidad = st.BajosCantidad;
                d.BajosAreaHa = st.BajosAreaHa;
                d.NivelacionAplicada = st.NivelacionAplicada;
                d.SesgoAntesCm = st.SesgoRmsAntesCm;
                d.SesgoDespuesCm = st.SesgoRmsDespuesCm;
                d.CalculoMs = st.Ms;
                d.CalculadoUtc = _calcUtc.ToString("o", Inv);
                if (_calculando) d.Estado = "calculando";   // recalculando: el mapa viejo sigue a la vista
                double e, n, lat, lon;
                if (_host.PlanimetriaPosicion(out e, out n, out lat, out lon))
                {
                    double c = PlanimetriaCabina.CotaEn(R, lat, lon);
                    if (!double.IsNaN(c)) d.CotaTractorM = Math.Round(c, 3);
                }
                if (_amb != null)
                {
                    d.Ambientes.CotaBajoM = Math.Round(_amb.CotaBajo, 3);
                    d.Ambientes.CotaLomaM = Math.Round(_amb.CotaLoma, 3);
                    d.Ambientes.AreaBajoHa = Math.Round(_amb.AreaHa[1], 2);
                    d.Ambientes.AreaMediaHa = Math.Round(_amb.AreaHa[2], 2);
                    d.Ambientes.AreaLomaHa = Math.Round(_amb.AreaHa[3], 2);
                }
            }
            return d;
        }

        private PlanimetriaCapaDto ArmarCapaDto()
        {
            var R = _res;
            var capa = PlanimetriaCabina.ArmarCapa(R, _amb, _host.PlanimetriaALocal);
            var bz = new byte[capa.ZCm.Length * 2];
            for (int i = 0; i < capa.ZCm.Length; i++)
            {
                bz[2 * i] = (byte)(capa.ZCm[i] & 0xFF);
                bz[2 * i + 1] = (byte)(capa.ZCm[i] >> 8);
            }
            var dto = new PlanimetriaCapaDto
            {
                Ok = true,
                Rev = _rev,
                Nx = capa.Nx,
                Ny = capa.Ny,
                EOeste = capa.EOeste,
                EEste = capa.EEste,
                NSur = capa.NSur,
                NNorte = capa.NNorte,
                ZBase = capa.ZBase,
                ZMin = capa.ZMin,
                ZMax = capa.ZMax,
                ZCm = Convert.ToBase64String(bz),
                Zona = capa.Zona != null ? Convert.ToBase64String(capa.Zona) : null,
                IntervaloM = capa.Intervalo,
                ModoCapa = _prefs.ModoCapa,
                CotaGuiaM = _cotaGuia,
            };
            foreach (var c in capa.Curvas)
                dto.Curvas.Add(new PlanimetriaCurvaDto { Elev = c.Elev, Maestra = c.Maestra, Lineas = c.Lineas });
            if (_cotaGuia.HasValue)
            {
                var nivel = Planimetria.CurvaDeCota(R.Grilla, _cotaGuia.Value);
                var cg = new PlanimetriaCurvaDto { Elev = _cotaGuia.Value, Maestra = true };
                foreach (var l in nivel.Lineas)
                {
                    var s = Planimetria.Simplificar(l, PlanimetriaCabina.TolSimplificar);
                    var arr = new float[s.Count * 2];
                    for (int i = 0; i < s.Count; i++)
                    {
                        double la, lo, e, n;
                        R.CeldaALatLon(s[i].C, s[i].F, out la, out lo);
                        _host.PlanimetriaALocal(la, lo, out e, out n);
                        arr[2 * i] = (float)e;
                        arr[2 * i + 1] = (float)n;
                    }
                    if (arr.Length >= 4) cg.Lineas.Add(arr);
                }
                dto.CurvaGuia = cg;
            }
            return dto;
        }

        private static string Metros(double v) => v.ToString("0.00", Inv).Replace('.', ',') + " m";

        private static double Recortar(double v, double min, double max)
            => double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));

        private static string NombreSeguro(string lote)
        {
            if (string.IsNullOrWhiteSpace(lote)) return "lote";
            var sb = new StringBuilder();
            foreach (char c in lote) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        // =====================================================================
        //  Preferencias (planimetria.json en la raíz de configuración)
        // =====================================================================

        private sealed class Prefs
        {
            [JsonPropertyName("habilitada")] public bool Habilitada { get; set; }
            [JsonPropertyName("capa_visible")] public bool CapaVisible { get; set; }
            [JsonPropertyName("modo_capa")] public string ModoCapa { get; set; } = "alturas";
            [JsonPropertyName("modo_ambientes")] public string ModoAmbientes { get; set; } = "percentil";
            [JsonPropertyName("p_bajo")] public double PBajo { get; set; } = 25;
            [JsonPropertyName("p_loma")] public double PLoma { get; set; } = 75;
            [JsonPropertyName("d_bajo_m")] public double DBajoM { get; set; } = 0.30;
            [JsonPropertyName("d_loma_m")] public double DLomaM { get; set; } = 0.30;
            [JsonPropertyName("dosis_bajo")] public double DosisBajo { get; set; }
            [JsonPropertyName("dosis_media")] public double DosisMedia { get; set; }
            [JsonPropertyName("dosis_loma")] public double DosisLoma { get; set; }
        }

        private static string RutaPrefs() => Path.Combine(AgpPaths.ConfigRoot, ArchivoPrefs);

        private static Prefs CargarPrefs()
        {
            try
            {
                var p = AtomicJson.Read<Prefs>(RutaPrefs(), AgpJson.Options) ?? new Prefs();
                if (p.ModoCapa != "alturas" && p.ModoCapa != "ambientes") p.ModoCapa = "alturas";
                if (p.ModoAmbientes != "percentil" && p.ModoAmbientes != "desnivel") p.ModoAmbientes = "percentil";
                if (!p.Habilitada) p.CapaVisible = false;
                return p;
            }
            catch { return new Prefs(); }
        }

        private void GuardarPrefs()
        {
            try { AtomicJson.Write(RutaPrefs(), AgpJson.Serialize(_prefs)); }
            catch (Exception ex) { Log.EventWriter("Planimetria: no se pudo guardar " + ArchivoPrefs + ": " + ex.Message); }
        }
    }
}
