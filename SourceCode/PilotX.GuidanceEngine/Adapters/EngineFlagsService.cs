// ============================================================================
// EngineFlagsService.cs — banderas para el motor headless.
//
// FlagsController se registra solo `if (_flags != null)` y EngineWebHost nunca
// inyectaba ese servicio: en el stack Avalonia /api/flags daba 404 y la
// pantalla de banderas no andaba. Misma clase de hueco que perfiles y que la
// cobertura.
//
// Las banderas son dato del OPERARIO: marca una piedra, un pozo, un alambrado
// caído. Perderlas no es perder una config, es perder lo que vio en el lote.
//
// Diferencia con el gemelo de FormGPS: no hay hilo de UI ni diálogos nativos,
// asi que Import/Export (que en el form abren un OpenFileDialog) devuelven
// error explicito en vez de fingir que hicieron algo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgOpenGPS.Core.Models;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineFlagsService : IFlagsService
    {
        private readonly GuidanceEngineHost _host;

        public EngineFlagsService(GuidanceEngineHost host) { _host = host; }

        // ---- lectura -------------------------------------------------------

        public FlagsStateDto GetState() => Armar();

        private FlagsStateDto Armar(string error = null)
        {
            var dto = new FlagsStateDto
            {
                Ok = error == null,
                Error = error,
                HasField = _host.IsJobStarted,
                Picked = _host.FlagPicked,
                CurLat = _host.AppModelField.CurrentLatLon.Latitude,
                CurLon = _host.AppModelField.CurrentLatLon.Longitude,
                Flags = new List<FlagItemDto>(),
            };

            double pe = _host.pivotAxlePos.easting;
            double pn = _host.pivotAxlePos.northing;

            for (int i = 0; i < _host.FlagPts.Count; i++)
            {
                var f = _host.FlagPts[i];
                double dx = f.easting - pe, dy = f.northing - pn;
                dto.Flags.Add(new FlagItemDto
                {
                    Number = i + 1,          // la UI las numera desde 1
                    Id = f.ID,
                    Color = f.color,
                    Notes = f.notes,
                    Lat = f.latitude,
                    Lon = f.longitude,
                    DistanceM = Math.Sqrt(dx * dx + dy * dy),
                });
            }
            return dto;
        }

        // ---- acciones ------------------------------------------------------

        public FlagsStateDto Pick(int number)
        {
            if (number < 0 || number > _host.FlagPts.Count) return Armar("numero-invalido");
            _host.FlagPicked = number;
            return Armar();
        }

        public FlagsStateDto Delete()
        {
            int n = _host.FlagPicked;
            if (n < 1 || n > _host.FlagPts.Count) return Armar("sin-seleccion");
            _host.FlagPts.RemoveAt(n - 1);
            _host.FlagPicked = 0;
            _host.GuardarBanderas();     // a disco en el acto: es dato del operario
            return Armar();
        }

        public FlagsStateDto SetNotes(string notes)
        {
            int n = _host.FlagPicked;
            if (n < 1 || n > _host.FlagPts.Count) return Armar("sin-seleccion");
            _host.FlagPts[n - 1].notes = notes ?? "";
            _host.GuardarBanderas();
            return Armar();
        }

        public FlagsStateDto Add(double lat, double lon, int color, bool useCurrent)
        {
            if (!_host.IsJobStarted) return Armar("sin-lote");

            double la = useCurrent ? _host.AppModelField.CurrentLatLon.Latitude : lat;
            double lo = useCurrent ? _host.AppModelField.CurrentLatLon.Longitude : lon;

            double este, norte;
            if (useCurrent)
            {
                // Con el tractor: se usa el pivote, que es donde el operario
                // siente que esta parado, no la antena.
                este = _host.pivotAxlePos.easting;
                norte = _host.pivotAxlePos.northing;
            }
            else
            {
                // Por lat/lon: hay que proyectar al plano local del lote. Sin
                // esto la bandera se dibuja en cualquier lado.
                try
                {
                    var geo = _host.AppModelField.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(la, lo));
                    este = geo.Easting;
                    norte = geo.Northing;
                }
                catch (Exception ex) { return Armar("latlon-invalida: " + ex.Message); }
            }

            // ID incremental sobre el maximo existente: reusar un ID borrado
            // haria que dos banderas distintas compartan identidad en el
            // historial del lote.
            int id = _host.FlagPts.Count == 0 ? 1 : _host.FlagPts.Max(f => f.ID) + 1;

            _host.FlagPts.Add(new CFlag(la, lo, este, norte, _host.fixHeading, color, id, ""));
            _host.FlagPicked = _host.FlagPts.Count;
            _host.GuardarBanderas();
            return Armar();
        }

        public FlagsStateDto CloseSession()
        {
            _host.FlagPicked = 0;
            _host.GuardarBanderas();
            return Armar();
        }

        // ---- lo que el motor headless no puede hacer -----------------------
        //
        // En FormGPS estos abren un diálogo nativo de archivo. Acá no hay UI,
        // asi que se dice explicitamente en vez de devolver Ok y no hacer nada:
        // un "ok" mentiroso es peor que un error claro.

        public FlagsStateDto Import() => Armar("no-disponible-sin-ui");

        public FlagsStateDto Export() => Armar("no-disponible-sin-ui");
    }
}
