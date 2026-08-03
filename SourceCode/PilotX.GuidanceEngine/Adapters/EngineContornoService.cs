// ============================================================================
// EngineContornoService.cs — contorno (lindero) para el motor headless.
//
// ContornoController se registra solo `if (_contorno != null)` y EngineWebHost
// nunca inyectaba el servicio: contra PilotX.Desktop /api/contorno daba 404 y
// la pantalla mostraba "Sin conexion con PilotX". Misma clase de hueco que
// banderas y perfiles.
//
// Port de FormGPS.Contorno (partial del WinForms) sin UI. La grabacion
// manejando NO se reimplementa: los puntos los agrega solo CPositionUpdater
// (AgOpenGPS.Core) cuando Bnd.isOkToAddPoints y el tractor avanzo mas de 1 m —
// el mismo codigo que corre bajo FormGPS. Aca solo se prenden/apagan las
// banderas de estado que ese loop mira.
//
// Diferencia con el gemelo de FormGPS: los cuatro caminos que abren ventana
// nativa (KML, Google Earth, mapa satelital, desde tracks) devuelven error
// explicito en vez de fingir. Grabar manejando —que es como se hace el lindero
// en el lote— anda completo.
// ============================================================================

using System;
using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineContornoService : IContornoService
    {
        private readonly GuidanceEngineHost _host;

        public EngineContornoService(GuidanceEngineHost host) { _host = host; }

        // ---- estado --------------------------------------------------------

        private ContornoStateDto Estado(string error = null)
        {
            var dto = new ContornoStateDto
            {
                Ok = error == null,
                JobStarted = _host.IsJobStarted,
                ToolWidth = _host.Tool.width,
                Recording = _host.Bnd.isBndBeingMade,
                Error = error,
            };

            for (int i = 0; i < _host.Bnd.bndList.Count; i++)
            {
                // Igual que FormBoundary.UpdateChart: el exterior nunca es
                // drive-thru (por definicion, es el limite del lote).
                if (i == 0) _host.Bnd.bndList[i].isDriveThru = false;

                dto.Boundaries.Add(new ContornoInfo
                {
                    Index = i,
                    IsOuter = i == 0,
                    AreaHa = Math.Round(_host.Bnd.bndList[i].area * 0.0001, 3),
                    IsDriveThru = _host.Bnd.bndList[i].isDriveThru,
                    Points = _host.Bnd.bndList[i].fenceLine.Count,
                });
            }
            return dto;
        }

        public ContornoStateDto GetState() => Estado();

        // ---- lista ---------------------------------------------------------

        public ContornoStateDto SetDriveThru(int index, bool value)
        {
            if (index > 0 && index < _host.Bnd.bndList.Count)
            {
                _host.Bnd.bndList[index].isDriveThru = value;
                _host.Bnd.BuildTurnLines();
                _host.GuardarLinderos();
            }
            return Estado();
        }

        public ContornoStateDto Delete(int index)
        {
            if (index < 0 || index >= _host.Bnd.bndList.Count)
                return Estado("indice-invalido");

            // El exterior solo se puede borrar si es el unico que queda: sin
            // limite exterior los internos no tienen contra que recortarse.
            if (index == 0 && _host.Bnd.bndList.Count > 1)
                return Estado("borrar-internos-primero");

            _host.Bnd.bndList[index].hdLine?.Clear();
            _host.Bnd.bndList.RemoveAt(index);

            _host.GuardarLinderos();
            _host.Fd.UpdateFieldBoundaryGUIAreas();
            _host.Bnd.BuildTurnLines();
            return Estado();
        }

        public ContornoStateDto DeleteAll()
        {
            _host.Bnd.bndList.Clear();
            _host.GuardarLinderos();
            _host.Fd.UpdateFieldBoundaryGUIAreas();
            _host.Bnd.BuildTurnLines();
            return Estado();
        }

        // ---- import de KML por upload --------------------------------------
        //
        // El unico de los cuatro caminos "con ventana nativa" que SI tiene
        // sentido headless: la pantalla sube el contenido del archivo y aca se
        // arma el lindero, sin dialogo de por medio. Los polígonos llegan en
        // WGS84 y se convierten al plano local del lote ABIERTO (por eso exige
        // lote: sin plano local no hay a que convertir).
        public ContornoStateDto ImportKmlUpload(string kmlContenido, bool multi)
        {
            if (!_host.IsJobStarted) return Estado("sin-lote");

            try
            {
                var anillos = AgOpenGPS.IO.KmlBoundaryReader.ReadRings(kmlContenido);
                if (anillos.Count == 0) return Estado("kml-invalido");

                // multi reemplaza TODO (la pantalla ya confirmo); single agrega
                // el primer poligono a lo que hay — igual que los dos botones
                // del FormBoundary nativo.
                if (multi) _host.Bnd.bndList.Clear();
                else if (anillos.Count > 1) anillos.RemoveRange(1, anillos.Count - 1);

                foreach (var anillo in anillos)
                {
                    var linde = new CBoundaryList();
                    foreach (var p in anillo)
                    {
                        var geo = _host.AppModelField.LocalPlane.ConvertWgs84ToGeoCoord(p);
                        linde.fenceLine.Add(new vec3(geo));
                    }
                    // Horario exterior / antihorario interiores + cierre de la
                    // linea: mismas dos llamadas que el nativo y que el import
                    // de lote.
                    linde.CalculateFenceArea(_host.Bnd.bndList.Count);
                    linde.FixFenceLine(_host.Bnd.bndList.Count);
                    _host.Bnd.bndList.Add(linde);
                }

                _host.GuardarLinderos();
                _host.Fd.UpdateFieldBoundaryGUIAreas();
                _host.Bnd.BuildTurnLines();
                return Estado();
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: contorno import KML: " + ex.Message);
                return Estado("kml-error: " + ex.Message);
            }
        }

        // ---- grabacion manejando -------------------------------------------

        private ContornoRecordDto Rec(string error = null)
        {
            // Area shoelace de lo que se lleva recorrido, para que el operario
            // vea crecer la hectarea mientras da la vuelta.
            var pts = _host.Bnd.bndBeingMadePts;
            int n = pts.Count;
            double area = 0;
            if (n > 0)
            {
                int j = n - 1;
                for (int i = 0; i < n; j = i++)
                {
                    area += (pts[j].easting + pts[i].easting)
                          * (pts[j].northing - pts[i].northing);
                }
                area = Math.Abs(area / 2);
            }

            return new ContornoRecordDto
            {
                Ok = error == null,
                Active = _host.Bnd.isBndBeingMade,
                Paused = !_host.Bnd.isOkToAddPoints,
                Points = n,
                AreaHa = Math.Round(area * 0.0001, 2),
                OffsetCm = Math.Round(_host.Bnd.createBndOffset * 100.0),
                RightSide = _host.Bnd.isDrawRightSide,
                AtPivot = _host.Bnd.isDrawAtPivot,
                SectionRec = _host.Bnd.isRecBoundaryWhenSectionOn,
                Error = error,
            };
        }

        public ContornoRecordDto RecordStatus() => Rec();

        public ContornoRecordDto RecordStart()
        {
            if (!_host.IsJobStarted) return Rec("sin-lote");

            // Sin ancho de herramienta el offset lateral es cero y el lindero
            // saldria por el eje del tractor en vez de por el borde del apero.
            if (_host.Tool.width < 0.2)
            {
                Log.EventWriter("GuidanceEngine: contorno, herramienta demasiado angosta");
                return Rec("herramienta-angosta");
            }

            _host.Bnd.bndBeingMadePts.Clear();
            _host.Bnd.createBndOffset = _host.Tool.width * 0.5;
            _host.Bnd.isDrawAtPivot = AgOpenGPS.Properties.Settings.Default.setBnd_isDrawPivot;
            _host.Bnd.isBndBeingMade = true;

            // Arranca EN PAUSA, igual que el player nativo: el operario tiene
            // que ubicarse en el borde antes de que empiece a tomar puntos.
            _host.Bnd.isOkToAddPoints = false;
            return Rec();
        }

        public ContornoRecordDto RecordSet(double? offsetCm, bool? rightSide, bool? atPivot, bool? sectionRec)
        {
            if (offsetCm.HasValue)
            {
                double cm = offsetCm.Value;
                if (cm < 0) cm = 0;
                if (cm > 4999) cm = 4999;   // mismo tope que el nud metrico
                _host.Bnd.createBndOffset = cm * 0.01;
            }
            if (rightSide.HasValue) _host.Bnd.isDrawRightSide = rightSide.Value;
            if (atPivot.HasValue)
            {
                _host.Bnd.isDrawAtPivot = atPivot.Value;
                AgOpenGPS.Properties.Settings.Default.setBnd_isDrawPivot = atPivot.Value;
            }
            if (sectionRec.HasValue) _host.Bnd.isRecBoundaryWhenSectionOn = sectionRec.Value;
            return Rec();
        }

        public ContornoRecordDto RecordPause()
        {
            if (!_host.Bnd.isBndBeingMade) return Rec("sin-grabacion");
            _host.Bnd.isOkToAddPoints = !_host.Bnd.isOkToAddPoints;
            return Rec();
        }

        public ContornoRecordDto RecordAddPoint()
        {
            if (!_host.Bnd.isBndBeingMade) return Rec("sin-grabacion");

            // Punto manual: solo tiene sentido en pausa. Se prende la bandera
            // el tiempo justo para que el updater tome UN punto y se apaga.
            if (!_host.Bnd.isOkToAddPoints)
            {
                _host.Bnd.isOkToAddPoints = true;
                _host.PositionUpdater.AddBoundaryPoint();
                _host.Bnd.isOkToAddPoints = false;
            }
            return Rec();
        }

        public ContornoRecordDto RecordUndo()
        {
            int n = _host.Bnd.bndBeingMadePts.Count;
            if (n > 0) _host.Bnd.bndBeingMadePts.RemoveAt(n - 1);
            return Rec();
        }

        public ContornoRecordDto RecordRestart()
        {
            _host.Bnd.bndBeingMadePts?.Clear();
            return Rec();
        }

        public ContornoRecordDto RecordSave()
        {
            if (!_host.Bnd.isBndBeingMade) return Rec("sin-grabacion");

            string error = null;
            if (_host.Bnd.bndBeingMadePts.Count > 2)
            {
                var nuevo = new CBoundaryList();
                for (int i = 0; i < _host.Bnd.bndBeingMadePts.Count; i++)
                    nuevo.fenceLine.Add(_host.Bnd.bndBeingMadePts[i]);

                nuevo.CalculateFenceArea(_host.Bnd.bndList.Count);
                nuevo.FixFenceLine(_host.Bnd.bndList.Count);
                _host.Bnd.bndList.Add(nuevo);

                _host.Fd.UpdateFieldBoundaryGUIAreas();
                _host.GuardarLinderos();
                _host.Bnd.BuildTurnLines();

                Log.EventWriter("GuidanceEngine: contorno grabado, area ha: "
                    + (nuevo.area * 0.0001).ToString("0.00",
                        System.Globalization.CultureInfo.InvariantCulture));

                // El lindero 0 es el LIMITE del lote; del 1 en adelante son
                // islas adentro. Una isla mas grande que el exterior es
                // geometricamente imposible, y el efecto no se ve al guardarla:
                // el area del lote pasa a ser negativa y el pivote queda
                // "dentro de una isla" en casi todo el lote, asi que el giro en
                // cabecera nunca se arma y el corte por lindero queda al reves.
                // Paso de verdad en cabina y costo una jornada de no entender
                // por que no giraba.
                //
                // No se bloquea el guardado —el operario acaba de recorrer el
                // perimetro y esos puntos no se tiran— pero se avisa fuerte.
                int idx = _host.Bnd.bndList.Count - 1;
                if (idx > 0 && nuevo.area > _host.Bnd.bndList[0].area)
                {
                    Log.EventWriter("GuidanceEngine: OJO, el lindero interno "
                        + idx + " es MAS GRANDE que el exterior — el lote queda con area negativa");
                    error = "interno-mas-grande";
                }
            }
            else
            {
                // Con 2 puntos o menos no hay poligono que cerrar. Se avisa y
                // NO se descarta lo grabado hasta que el operario decida.
                error = "pocos-puntos";
                _host.Bnd.isOkToAddPoints = false;
                return Rec(error);
            }

            _host.Bnd.isOkToAddPoints = false;
            _host.Bnd.isBndBeingMade = false;
            _host.Bnd.bndBeingMadePts.Clear();
            return Rec(error);
        }

        public ContornoRecordDto RecordCancel()
        {
            _host.Bnd.isOkToAddPoints = false;
            _host.Bnd.isBndBeingMade = false;
            _host.Bnd.bndBeingMadePts.Clear();
            return Rec();
        }

        // ---- lo que necesita ventana nativa --------------------------------
        //
        // Los tres abren un dialogo o un form de WinForms (OpenFileDialog,
        // Process.Start, FormMap). En el motor headless no hay donde
        // mostrarlos, asi que se dice explicitamente: devolver Ok y no hacer
        // nada seria peor.

        public ContornoStateDto ImportKml(bool multi) => Estado("no-disponible-sin-ui");

        public ContornoStateDto OpenGoogleEarth() => Estado("no-disponible-sin-ui");

        public ContornoStateDto OpenMapa() => Estado("no-disponible-sin-ui");

        // ---- cerco desde los tracks ----------------------------------------
        //
        // Port headless de FormBuildBoundaryFromTracks: el algoritmo (extender
        // tracks, intersectar, recortar segmentos y cerrar el poligono) vive en
        // AgOpenGPS.Core/BoundaryBuilder y se usa tal cual. Lo que el form
        // resolvia con checkboxes y ajustes a mano aca se resuelve como el
        // boton "Autofind" del original: todos los tracks del lote, extendidos
        // 50 m para garantizar las intersecciones. Mismo resultado final que
        // Build + Save del form: bndList reemplazado + Boundary.txt escrito.
        public ContornoStateDto BuildFromTracks()
        {
            if (!_host.IsJobStarted) return Estado("sin-lote");
            try
            {
                var tracks = new System.Collections.Generic.List<CTrk>();
                if (_host.Trk != null && _host.Trk.gArr != null)
                    foreach (var t in _host.Trk.gArr)
                        if (t != null) tracks.Add(t);
                if (tracks.Count < 2) return Estado("se-necesitan-2-tracks");

                var builder = new AgOpenGPS.Classes.BoundaryBuilder();
                builder.SetTracks(tracks);
                builder.ExtendAllTracks(50.0);   // el "Autofind" del form
                builder.BuildSegments();

                var pts = builder.BuildTrimmedBoundary();
                var finalized = builder.FinalizedBoundary;
                if (pts == null || pts.Count < 3 || finalized == null)
                    return Estado("sin-cerco-valido");

                // Igual que UpdateMainApplicationBoundary + SaveBoundary del form.
                _host.Bnd.bndList.Clear();
                _host.Bnd.bndList.Add(finalized);
                _host.Bnd.BuildTurnLines();

                string dir = System.IO.Path.Combine(
                    RegistrySettings.fieldsDirectory, _host.currentFieldDirectory);
                if (!builder.SaveToBoundaryFile(dir))
                    return Estado("cerco-aplicado-pero-no-guardado");

                Log.EventWriter("GuidanceEngine: cerco desde tracks — " +
                    pts.Count + " puntos, " + tracks.Count + " tracks");
                return Estado();
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: cerco desde tracks fallo: " + ex.Message);
                return Estado("error: " + ex.Message);
            }
        }
    }
}
