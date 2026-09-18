// ============================================================================
// EngineRecPathService.cs — caminos grabados para el motor headless.
//
// RecPathController se registra solo `if (_recPath != null)` y EngineWebHost
// nunca inyectaba el servicio: /api/recpath daba 404. Sexto de
// docs/RETIRAR-WINFORMS.md.
//
// El camino grabado es el que el tractor puede repetir solo. La logica es
// RecPathManager (AgOpenGPS.Core), la misma que usa FormGPS; el guardado va
// por RecPathFiles, el streamer que el host ya usaba al cerrar el lote.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using AgLibrary.Logging;
using AgOpenGPS.IO;
using AgroParallel.Services.Abstractions;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;

    public sealed class EngineRecPathService : IRecPathService
    {
        private readonly GuidanceEngineHost _host;
        private RecPathManager _mgr;

        public EngineRecPathService(GuidanceEngineHost host) { _host = host; }

        private string DirLote()
            => string.IsNullOrEmpty(_host.currentFieldDirectory)
                ? null
                : Path.Combine(RegistrySettings.fieldsDirectory, _host.currentFieldDirectory);

        private RecPathManager Mgr => _mgr ??= new RecPathManager(
            _host.RecPath,
            dirLote: DirLote,
            guardar: () => GuardarComo("RecPath.txt"),
            guardarComo: GuardarComo);

        private void GuardarComo(string nombre)
        {
            try
            {
                string dir = DirLote();
                if (dir != null && Directory.Exists(dir))
                    RecPathFiles.Save(dir, _host.RecPath.recList, nombre);
            }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: no se pudo guardar " + nombre + ": " + ex.Message);
            }
        }

        public List<string> ListPaths()
        {
            try { return Mgr.ListFiles(); }
            catch (Exception ex)
            {
                Log.EventWriter("GuidanceEngine: recpath list: " + ex.Message);
                return new List<string>();
            }
        }

        public bool LoadPath(string name)
        {
            try { return Mgr.Load(name); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: recpath load: " + ex.Message); return false; }
        }

        public bool DeletePath(string name)
        {
            try { return Mgr.Delete(name); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: recpath delete: " + ex.Message); return false; }
        }

        public void TurnOff()
        {
            try { Mgr.TurnOff(); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: recpath off: " + ex.Message); }
        }

        public bool SaveWithName(string name)
        {
            try { return Mgr.SaveWithName(name); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: recpath save: " + ex.Message); return false; }
        }

        public void DiscardRecording()
        {
            try { Mgr.Discard(); }
            catch (Exception ex) { Log.EventWriter("GuidanceEngine: recpath discard: " + ex.Message); }
        }
    }
}
