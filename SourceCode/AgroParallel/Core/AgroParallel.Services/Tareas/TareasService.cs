// ============================================================================
// TareasService.cs — la tarea del lote abierto: crear, pausar, reanudar,
// cerrar, exportar. Es lo que atiende /api/tareas/* (TareasController).
//
// No conoce al motor: todo lo que necesita del lote entra por delegados que
// arma el host (EngineWebHost) — carpeta del lote abierto, área del lote
// (WorkedAreaTotalM2, el mismo número de HA que ve el operario arriba), insumo
// activo del catálogo, cobertura y la conversión del plano local a lat/lon.
// Así se testea entero con fakes y no ata a nadie (Android incluido).
//
// Reglas de producto (decisiones conservadoras, ver reporte del commit):
//   · UNA tarea abierta (activa o en pausa) por lote. Para arrancar otra hay
//     que cerrar la anterior.
//   · Cerrar el lote PAUSA la tarea activa (AntesDeCerrarLote): sin lote
//     abierto no hay contador de área que mirar, y el área no puede quedar
//     corriendo contra el lote siguiente.
//   · Exportar solo tareas CERRADAS (el informe es definitivo) y con el lote
//     abierto. La cobertura exportada es la del lote AL MOMENTO de exportar:
//     PilotX guarda una sola cobertura por lote (Sections.txt) y no sabe qué
//     triángulo pintó qué tarea. Con el anti-solape prendido una tarea nueva
//     sobre un lote pintado no siembra nada, así que en la práctica lo pintado
//     del lote ES lo de la tarea; el informe lo aclara igual.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AgroParallel.Models;

namespace AgroParallel.Services.Tareas
{
    public sealed class TareaCrearPedido
    {
        public string Cultivo { get; set; }
        public string TipoTrabajo { get; set; }
        public string Notas { get; set; }
    }

    /// <summary>Una tarea lista para mostrar: los textos ya vienen formateados
    /// (la pantalla no rehace cuentas ni unidades).</summary>
    public sealed class TareaVista
    {
        public string Id { get; set; }
        public string Estado { get; set; }
        public string EstadoTexto { get; set; }
        public string TipoTrabajo { get; set; }
        public string TipoTexto { get; set; }
        public string Cultivo { get; set; }
        public string Notas { get; set; }
        public string Insumo { get; set; }
        public string DosisTexto { get; set; }
        public string InicioTexto { get; set; }
        public string FinTexto { get; set; }
        public double AreaHa { get; set; }
        public string AreaTexto { get; set; }
        public string DuracionTexto { get; set; }
        /// <summary>Nombre de archivo sugerido para el export (sin extensión).</summary>
        public string ArchivoSugerido { get; set; }
    }

    public sealed class TareasEstado
    {
        public bool Ok { get; set; } = true;
        public string Error { get; set; }
        public bool HayLote { get; set; }
        public string Lote { get; set; }
        /// <summary>La tarea activa o en pausa del lote; null si no hay.</summary>
        public TareaVista Abierta { get; set; }
        /// <summary>Cerradas, la más nueva primero (hasta 20).</summary>
        public List<TareaVista> Cerradas { get; set; } = new List<TareaVista>();

        // Para precargar "Nueva tarea" con lo que ya está configurado.
        public string InsumoActivo { get; set; }
        public string InsumoDosisTexto { get; set; }
        public string CultivoSugerido { get; set; }
        public string TipoSugerido { get; set; }
    }

    public sealed class TareaExportResultado
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Carpeta { get; set; }
        public List<string> Archivos { get; set; } = new List<string>();
        public int Poligonos { get; set; }
    }

    public sealed class TareasService
    {
        private const int MaxCerradasEnEstado = 20;

        private readonly Func<string> _loteDir;
        private readonly Func<double> _areaM2;
        private readonly Func<InsumoDto> _insumo;
        private readonly Func<CoverageSnapshot> _cobertura;
        private readonly Func<double, double, double[]> _aLatLon;
        private readonly Func<DateTime> _reloj;
        private readonly object _lock = new object();

        // Cache del Tareas.json del lote abierto: UltimaAreaVistaM2 vive acá
        // entre escrituras (no se escribe el archivo cada 5 s mientras se
        // trabaja — desgaste de la memoria de la pantalla por nada).
        private string _cacheDir;
        private TareasArchivo _cache;

        public TareasService(Func<string> loteDir, Func<double> areaTrabajadaM2, Func<InsumoDto> insumoActivo,
                             Func<CoverageSnapshot> cobertura, Func<double, double, double[]> aLatLon,
                             Func<DateTime> reloj = null)
        {
            _loteDir = loteDir ?? throw new ArgumentNullException(nameof(loteDir));
            _areaM2 = areaTrabajadaM2 ?? throw new ArgumentNullException(nameof(areaTrabajadaM2));
            _insumo = insumoActivo ?? (() => null);
            _cobertura = cobertura;
            _aLatLon = aLatLon;
            _reloj = reloj ?? (() => DateTime.Now);
        }

        // =====================================================================
        //  API
        // =====================================================================

        public TareasEstado Estado()
        {
            lock (_lock)
            {
                string dir = Dir();
                if (dir == null) return SinLote(null);
                var a = Archivo(dir);
                var t = TareaReglas.Abierta(a.Tareas);
                if (t != null && TareaReglas.Observar(t, Area())) Persistir(dir, a);
                return Armar(dir, a, null);
            }
        }

        public TareasEstado Crear(TareaCrearPedido pedido)
        {
            lock (_lock)
            {
                string dir = Dir();
                if (dir == null) return SinLote("Abrí un lote para empezar una tarea.");
                var a = Archivo(dir);
                if (TareaReglas.Abierta(a.Tareas) != null)
                    return Armar(dir, a, "Ya hay una tarea abierta en este lote: cerrala antes de empezar otra.");

                DateTime ahora = _reloj();
                InsumoDto insumo = null;
                try { insumo = _insumo(); } catch { }
                var t = TareaReglas.Crear(NuevoId(a, ahora), NombreLote(dir), pedido?.Cultivo,
                                          pedido?.TipoTrabajo, pedido?.Notas, insumo, ahora, Area());
                a.Tareas.Add(t);
                string err = Persistir(dir, a);
                if (err != null) { a.Tareas.Remove(t); return Armar(dir, a, err); }
                AgpLog.Info("Tareas", "Tarea iniciada: " + TipoTrabajo.Etiqueta(t.TipoTrabajo) + " en " + t.Lote + " (" + t.Id + ")");
                return Armar(dir, a, null);
            }
        }

        public TareasEstado Pausar() => Transicion("pausada", (t, ahora, area) =>
        {
            TareaReglas.Pausar(t, ahora, area, out string e); return e;
        });

        public TareasEstado Reanudar() => Transicion("reanudada", (t, ahora, area) =>
        {
            TareaReglas.Reanudar(t, ahora, area, out string e); return e;
        });

        public TareasEstado Cerrar() => Transicion("cerrada", (t, ahora, area) =>
        {
            TareaReglas.Cerrar(t, ahora, area, out string e); return e;
        });

        /// <summary>
        /// Lo llama el host JUSTO antes de cerrar el lote (todavía abierto, con
        /// su área): la tarea activa queda en pausa con lo trabajado hasta ahí.
        /// </summary>
        public void AntesDeCerrarLote()
        {
            try
            {
                lock (_lock)
                {
                    string dir = Dir();
                    if (dir == null) return;
                    var a = Archivo(dir);
                    var t = TareaReglas.Abierta(a.Tareas);
                    if (t == null || t.Estado != EstadoTarea.Activa) return;
                    if (TareaReglas.Pausar(t, _reloj(), Area(), out _))
                    {
                        Persistir(dir, a);
                        AgpLog.Info("Tareas", "Lote cerrado con la tarea en curso: quedó en pausa (" + t.Id + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                // Nunca puede frenar el cierre del lote.
                AgpLog.Warn("Tareas", "AntesDeCerrarLote", ex);
            }
        }

        /// <summary>
        /// Vigilancia periódica (cada pocos segundos, desde el host): registra el
        /// área para no perder lo trabajado si borran el pintado con la tarea
        /// activa. Solo escribe el archivo cuando detecta ese borrado.
        /// </summary>
        public void Tick()
        {
            try
            {
                lock (_lock)
                {
                    string dir = Dir();
                    if (dir == null) return;
                    var a = Archivo(dir);
                    var t = TareaReglas.Abierta(a.Tareas);
                    if (t != null && TareaReglas.Observar(t, Area())) Persistir(dir, a);
                }
            }
            catch (Exception ex) { AgpLog.Warn("Tareas", "Tick", ex); }
        }

        /// <summary>
        /// Exporta una tarea CERRADA del lote abierto: informe HTML en
        /// <paramref name="destinoHtml"/> y, al lado, &lt;nombre&gt;_cobertura.shp
        /// (+ .shx .dbf .prj). Sin destino, va a Documentos\PilotX\Tareas.
        /// </summary>
        public TareaExportResultado Exportar(string id, string destinoHtml)
        {
            lock (_lock)
            {
                var r = new TareaExportResultado();
                string dir = Dir();
                if (dir == null) { r.Error = "Abrí el lote de la tarea para exportarla."; return r; }
                var a = Archivo(dir);
                Tarea t = null;
                foreach (var x in a.Tareas) if (x.Id == id) { t = x; break; }
                if (t == null) { r.Error = "No se encontró la tarea en este lote."; return r; }
                if (t.Estado != EstadoTarea.Cerrada) { r.Error = "Cerrá la tarea antes de exportarla."; return r; }

                try
                {
                    string html = ResolverDestino(destinoHtml, t);
                    string carpeta = Path.GetDirectoryName(html);
                    if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);
                    string baseNombre = Path.Combine(carpeta ?? "", Path.GetFileNameWithoutExtension(html));
                    string shp = baseNombre + "_cobertura.shp";

                    int poligonos = 0;
                    if (_cobertura != null && _aLatLon != null)
                    {
                        CoverageSnapshot cob = null;
                        try { cob = _cobertura(); } catch (Exception ex) { AgpLog.Warn("Tareas", "cobertura", ex); }
                        if (cob != null) poligonos = TareaCoberturaShp.Exportar(cob, _aLatLon, shp, t.Id);
                    }

                    // Calidad de siembra por surco (registro VistaX del lote,
                    // solo los tramos sembrados con la tarea en curso).
                    AgroParallel.Services.VistaX.VxResumenLote vx = null;
                    string csvVx = null;
                    try
                    {
                        vx = AgroParallel.Services.VistaX.VxRegistroArchivo.ResumenDeCarpeta(
                            AgroParallel.Services.VistaX.VxRegistroArchivo.DirDeLote(dir), t.Lote,
                            fin => TareaReglas.EnCurso(t, fin, _reloj()));
                        if (vx != null)
                        {
                            csvVx = baseNombre + "_vistax_surcos.csv";
                            // BOM: la planilla reconoce el UTF-8 y los acentos.
                            File.WriteAllText(csvVx, vx.ToCsv(), new UTF8Encoding(true));
                        }
                    }
                    catch (Exception ex) { AgpLog.Warn("Tareas", "resumen VistaX", ex); vx = null; csvVx = null; }

                    string informe = TareaInforme.ArmarHtml(t, _reloj(), poligonos > 0 ? Path.GetFileName(shp) : null,
                        vx, csvVx != null ? Path.GetFileName(csvVx) : null);
                    File.WriteAllText(html, informe, new UTF8Encoding(false));

                    r.Ok = true;
                    r.Carpeta = carpeta;
                    r.Poligonos = poligonos;
                    r.Archivos.Add(Path.GetFileName(html));
                    if (poligonos > 0)
                        foreach (var ext in new[] { ".shp", ".shx", ".dbf", ".prj" })
                        {
                            string f = Path.ChangeExtension(shp, ext);
                            if (File.Exists(f)) r.Archivos.Add(Path.GetFileName(f));
                        }
                    if (csvVx != null) r.Archivos.Add(Path.GetFileName(csvVx));
                    AgpLog.Info("Tareas", "Tarea " + t.Id + " exportada a " + carpeta + " (" + poligonos + " polígonos)");
                    return r;
                }
                catch (Exception ex)
                {
                    AgpLog.Warn("Tareas", "Exportar", ex);
                    r.Ok = false;
                    r.Error = "No se pudo escribir en el destino: " + ex.Message;
                    return r;
                }
            }
        }

        // =====================================================================
        //  internos
        // =====================================================================

        private TareasEstado Transicion(string verbo, Func<Tarea, DateTime, double, string> accion)
        {
            lock (_lock)
            {
                string dir = Dir();
                if (dir == null) return SinLote("No hay lote abierto.");
                var a = Archivo(dir);
                var t = TareaReglas.Abierta(a.Tareas);
                if (t == null) return Armar(dir, a, "No hay una tarea abierta en este lote.");

                string err = accion(t, _reloj(), Area());
                if (err != null) return Armar(dir, a, err);
                err = Persistir(dir, a);
                if (err == null) AgpLog.Info("Tareas", "Tarea " + verbo + ": " + t.Id);
                return Armar(dir, a, err);
            }
        }

        private string Dir()
        {
            string d = null;
            try { d = _loteDir(); } catch { }
            if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) return null;
            return d;
        }

        private double Area()
        {
            try { return _areaM2(); } catch { return 0; }
        }

        private TareasArchivo Archivo(string dir)
        {
            if (_cache == null || !string.Equals(_cacheDir, dir, StringComparison.OrdinalIgnoreCase))
            {
                _cache = TareasStore.Cargar(dir);
                _cacheDir = dir;
            }
            return _cache;
        }

        /// <summary>null = ok; si no, el motivo para la pantalla. Si falla, la
        /// cache se descarta para que la próxima lectura vuelva a lo que quedó
        /// en disco (no se le muestra al operario algo que no se guardó).</summary>
        private string Persistir(string dir, TareasArchivo a)
        {
            try
            {
                TareasStore.Guardar(dir, a);
                return null;
            }
            catch (Exception ex)
            {
                AgpLog.Warn("Tareas", "No se pudo guardar Tareas.json", ex);
                _cache = null;
                return "No se pudo guardar la tarea en el lote: " + ex.Message;
            }
        }

        private TareasEstado SinLote(string error) =>
            new TareasEstado { Ok = error == null, Error = error, HayLote = false };

        private TareasEstado Armar(string dir, TareasArchivo a, string error)
        {
            double area = Area();
            DateTime ahora = _reloj();
            var e = new TareasEstado
            {
                Ok = error == null,
                Error = error,
                HayLote = true,
                Lote = NombreLote(dir),
            };
            var abierta = TareaReglas.Abierta(a.Tareas);
            if (abierta != null) e.Abierta = Vista(abierta, area, ahora);
            for (int i = a.Tareas.Count - 1; i >= 0 && e.Cerradas.Count < MaxCerradasEnEstado; i--)
                if (a.Tareas[i].Estado == EstadoTarea.Cerrada) e.Cerradas.Add(Vista(a.Tareas[i], area, ahora));

            InsumoDto ins = null;
            try { ins = _insumo(); } catch { }
            if (ins != null)
            {
                e.InsumoActivo = ins.Nombre;
                var muestra = TareaReglas.Crear("", "", "", "", "", ins, ahora, 0);
                e.InsumoDosisTexto = TareaFormato.Dosis(muestra.Dosis, muestra.DosisUnidad);
                e.CultivoSugerido = ins.Cultivo ?? "";
                e.TipoSugerido = TipoTrabajo.DesdeTipoInsumo(ins.Tipo);
            }
            else
            {
                e.CultivoSugerido = "";
                e.TipoSugerido = TipoTrabajo.Siembra;
            }
            return e;
        }

        private static TareaVista Vista(Tarea t, double areaLote, DateTime ahora)
        {
            double m2 = TareaReglas.AreaTrabajadaM2(t, areaLote);
            return new TareaVista
            {
                Id = t.Id,
                Estado = t.Estado,
                EstadoTexto = EstadoTarea.Etiqueta(t.Estado),
                TipoTrabajo = t.TipoTrabajo,
                TipoTexto = TipoTrabajo.Etiqueta(t.TipoTrabajo),
                Cultivo = t.Cultivo,
                Notas = t.Notas,
                Insumo = t.InsumoNombre,
                DosisTexto = TareaFormato.Dosis(t.Dosis, t.DosisUnidad),
                InicioTexto = TareaFormato.Fecha(t.Inicio),
                FinTexto = TareaFormato.Fecha(t.Fin),
                AreaHa = m2 / 10000.0,
                AreaTexto = TareaFormato.Hectareas(m2),
                DuracionTexto = TareaFormato.Duracion(TareaReglas.TiempoEfectivo(t, t.Fin ?? ahora)),
                ArchivoSugerido = NombreArchivo(t),
            };
        }

        private static string NombreLote(string dir) =>
            Path.GetFileName((dir ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        private static string NuevoId(TareasArchivo a, DateTime ahora)
        {
            string baseId = "t" + ahora.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string id = baseId;
            int n = 2;
            while (a.Tareas.Exists(x => x.Id == id)) id = baseId + "-" + (n++).ToString(CultureInfo.InvariantCulture);
            return id;
        }

        private static string ResolverDestino(string destinoHtml, Tarea t)
        {
            string d = (destinoHtml ?? "").Trim();
            if (d.Length == 0)
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(docs)) docs = Path.GetTempPath();
                d = Path.Combine(docs, "PilotX", "Tareas", NombreArchivo(t) + ".html");
            }
            else if (!d.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                     !d.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                d += ".html";
            }
            return Path.GetFullPath(d);
        }

        /// <summary>"Siembra La Loma 2026-10-01" sin caracteres inválidos.</summary>
        public static string NombreArchivo(Tarea t)
        {
            string s = TipoTrabajo.Etiqueta(t.TipoTrabajo) + " " + t.Lote + " "
                     + t.Inicio.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }
    }
}
