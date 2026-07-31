// ============================================================================
// EngineShapeService.cs — la prescripcion (.shp) en el motor headless.
//
// Hasta ahora el motor tenia TODO el camino del shape muerto: GetShape()
// devolvia null, GetShapeFieldDose() devolvia 0 y el upload no existia. Eso
// significaba QuantiX y FlowX dosificando con 0 contra el motor — la
// prescripcion es el corazon de los dos — y VistaX sin base para sus mapas.
//
// Este servicio es tres cosas en una, porque las tres comparten LA MISMA capa:
//   1. IShapefileService — subir .shp+.shx+.dbf desde la pantalla y cargarlos.
//   2. Carga automatica al abrir el lote (ShapefilePersistence, igual que
//      FormGPS.TryAutoLoadShapefileForCurrentField).
//   3. Fuente para el estado: poligonos para el mapa (/api/aog/shape), campos
//      DBF para los dropdowns, y DOSIS por posicion (SamplePosition en cada
//      lectura del state, que va a 10 Hz).
//
// La geometria es ShapefileLayer (AgroParallel/Adapters, linkeada aca y en
// WinForms): mismo parseo, misma triangulacion, mismo point-in-polygon.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgLibrary.Logging;
using AgroParallel.Common;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgroParallel.Services.Common;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineShapeService : IShapefileService
    {
        private static readonly string[] RequiredExt = { ".shp", ".shx", ".dbf" };
        private static readonly string[] AcceptedExt = { ".shp", ".shx", ".dbf", ".prj", ".cpg" };

        private readonly GuidanceEngineHost _host;
        private readonly object _lock = new object();

        private ShapefileLayer _layer;
        private string _loadedForField;

        public EngineShapeService(GuidanceEngineHost host) { _host = host; }

        private string DirLote()
            => string.IsNullOrEmpty(_host.currentFieldDirectory)
                ? null
                : Path.Combine(RegistrySettings.fieldsDirectory, _host.currentFieldDirectory);

        // ---- capa activa ---------------------------------------------------

        /// <summary>
        /// La capa del lote abierto, cargandola si el lote cambio desde la
        /// ultima vez. Se resuelve aca y no en un evento porque el motor no
        /// tiene un "field opened" observable — y asi la carga tambien cubre
        /// el caso de abrir el motor con el lote ya abierto.
        /// </summary>
        public ShapefileLayer Capa
        {
            get
            {
                lock (_lock)
                {
                    string field = _host.currentFieldDirectory ?? "";
                    if (field != _loadedForField)
                    {
                        _loadedForField = field;
                        _layer = CargarDeDisco();
                    }
                    return _layer;
                }
            }
        }

        private ShapefileLayer CargarDeDisco()
        {
            try
            {
                string dir = DirLote();
                if (dir == null || !Directory.Exists(dir)) return null;

                var cfg = ShapefilePersistence.Load(dir);
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.ShpPath) || !File.Exists(cfg.ShpPath))
                    return null;

                var result = ShapefileReader.ReadPolygons(cfg.ShpPath);
                var layer = new ShapefileLayer(result, Path.GetFileName(cfg.ShpPath))
                {
                    SourceFullPath = cfg.ShpPath,
                };
                AplicarEstilo(layer, cfg.StyleField);

                Log.EventWriter($"GuidanceEngine: shape cargado ({layer.PolygonCount} poligonos, campo dosis: {layer.StyleField ?? "-"})");
                return layer;
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo cargar el shape: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Colorea las zonas por el campo pedido; si no hay pedido, por el
        /// PRIMER campo numerico del DBF. Una prescripcion tipica tiene una
        /// sola columna de dosis, y sin este default el shape subia gris plano
        /// hasta que alguien eligiera el campo a mano.
        /// </summary>
        private static void AplicarEstilo(ShapefileLayer layer, string campoPedido)
        {
            if (!string.IsNullOrEmpty(campoPedido))
            {
                layer.ApplyColorByField(campoPedido);
                return;
            }
            var nombres = layer.FieldNames;
            if (nombres == null) return;
            for (int i = 0; i < nombres.Count; i++)
            {
                double min, max;
                int count;
                if (layer.TryGetFieldStats(nombres[i], out min, out max, out count) && count > 0)
                {
                    layer.ApplyColorByField(nombres[i]);
                    return;
                }
            }
        }

        // ---- dosis por posicion (lo llama el state en cada lectura) --------

        public void MuestrearPosicion(double easting, double northing)
        {
            var layer = Capa;
            if (layer == null || layer.IsEmpty) return;
            try
            {
                layer.EnsureProjected(_host.AppModelField.LocalPlane);
                layer.SamplePosition(easting, northing);
            }
            catch { /* una muestra fallida no puede voltear el state */ }
        }

        // ---- IShapefileService (upload desde la pantalla) ------------------

        public Task<ShapefileUploadResult> UploadAsync(IReadOnlyList<ShapefileUploadFile> files)
        {
            var r = new ShapefileUploadResult();
            try
            {
                string dir = DirLote();
                if (dir == null)
                {
                    r.Ok = false; r.Error = "Abrí un lote antes de cargar el shapefile.";
                    return Task.FromResult(r);
                }

                var validos = (files ?? new List<ShapefileUploadFile>())
                    .Where(f => f?.Name != null
                             && AcceptedExt.Contains(Path.GetExtension(f.Name).ToLowerInvariant()))
                    .ToList();

                var exts = validos.Select(f => Path.GetExtension(f.Name).ToLowerInvariant()).ToList();
                if (RequiredExt.Any(e => !exts.Contains(e)))
                {
                    r.Ok = false; r.Error = "Faltan archivos: el set necesita .shp + .shx + .dbf.";
                    return Task.FromResult(r);
                }

                string shapeDir = Path.Combine(dir, "Shapefile");
                Directory.CreateDirectory(shapeDir);

                string shpPath = null;
                foreach (var f in validos)
                {
                    string destino = Path.Combine(shapeDir, Path.GetFileName(f.Name));
                    File.WriteAllBytes(destino, f.Bytes);
                    if (Path.GetExtension(destino).ToLowerInvariant() == ".shp") shpPath = destino;
                }

                var result = ShapefileReader.ReadPolygons(shpPath);
                var layer = new ShapefileLayer(result, Path.GetFileName(shpPath))
                {
                    SourceFullPath = shpPath,
                };
                AplicarEstilo(layer, null);

                lock (_lock)
                {
                    _layer = layer;
                    _loadedForField = _host.currentFieldDirectory ?? "";
                }

                ShapefilePersistence.Save(dir, new ShapefileFieldConfig
                {
                    ShpPath = shpPath,
                    Visible = true,
                    ShowFill = true,
                    ShowOutline = true,
                });

                r.Ok = true;
                r.FileName = Path.GetFileName(shpPath);
                r.PolygonCount = layer.PolygonCount;
                r.Fields = layer.FieldNames?.ToList();
                Log.EventWriter($"GuidanceEngine: shape subido ({layer.PolygonCount} poligonos)");
            }
            catch (Exception ex)
            {
                r.Ok = false; r.Error = ex.Message;
                Log.EventWriter("GuidanceEngine: shape upload: " + ex.Message);
            }
            return Task.FromResult(r);
        }

        public Task<bool> RemoveAsync()
        {
            try
            {
                lock (_lock) { _layer = null; }
                string dir = DirLote();
                if (dir != null) ShapefilePersistence.Delete(dir);
                return Task.FromResult(true);
            }
            catch { return Task.FromResult(false); }
        }
    }
}
