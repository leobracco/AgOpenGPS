// ============================================================================
// RecPathManager.cs — recorded paths (port de FormRecordName + FormRecordPicker).
//
// Listar, cargar, borrar y nombrar los caminos grabados (.rec) del lote. El
// camino grabado es el que el tractor puede repetir solo — cargarlo mal o
// perderlo significa volver a manejarlo entero.
//
// Era `partial class FormGPS`: solo existía bajo WinForms y /api/recpath daba
// 404. Sexto de docs/RETIRAR-WINFORMS.md. La única dependencia de UI era
// esconder el panel de arrastre al apagar; entra como callback y en el motor
// queda no-op.
//
// No es thread-safe: cada host lo llama desde su propio hilo.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgLibrary.Logging;

namespace AgOpenGPS
{
    public sealed class RecPathManager
    {
        private readonly CRecordedPath _recPath;

        /// <summary>Carpeta del lote abierto, o null sin lote.</summary>
        private readonly Func<string> _dirLoteFn;

        /// <summary>Persistir RecPath.txt (sin nombre) o un .rec con nombre.</summary>
        private readonly Action _guardar;
        private readonly Action<string> _guardarComo;

        /// <summary>Esconder el panel de arrastre del host al apagar. Solo
        /// WinForms; en el motor queda no-op.</summary>
        private readonly Action _ocultarPanel;

        public RecPathManager(
            CRecordedPath recPath,
            Func<string> dirLote,
            Action guardar,
            Action<string> guardarComo,
            Action ocultarPanel = null)
        {
            _recPath = recPath;
            _dirLoteFn = dirLote ?? (() => null);
            _guardar = guardar ?? (() => { });
            _guardarComo = guardarComo ?? (_ => { });
            _ocultarPanel = ocultarPanel ?? (() => { });
        }

        private string _dirLote => _dirLoteFn();

        public List<string> ListFiles()
        {
            var list = new List<string>();
            string fieldDir = _dirLote;
            if (string.IsNullOrEmpty(fieldDir) || !Directory.Exists(fieldDir)) return list;

            foreach (string file in Directory.GetFiles(fieldDir, "*.rec"))
                list.Add(Path.GetFileNameWithoutExtension(file));

            return list;
        }

        public bool Load(string name)
        {
            string fieldDir = _dirLote;
            if (string.IsNullOrEmpty(fieldDir)) return false;
            string recFile = Path.Combine(fieldDir, name + ".rec");
            if (!File.Exists(recFile)) return false;

            try
            {
                // Copia a RecPath.txt: es el que se auto-carga al reabrir el lote.
                File.Copy(recFile, Path.Combine(fieldDir, "RecPath.txt"), true);

                using (StreamReader reader = new StreamReader(recFile))
                {
                    reader.ReadLine(); // header
                    string line = reader.ReadLine();
                    int numPoints = int.Parse(line);
                    _recPath.recList.Clear();

                    while (!reader.EndOfStream)
                    {
                        for (int v = 0; v < numPoints; v++)
                        {
                            line = reader.ReadLine();
                            string[] words = line.Split(',');
                            CRecPathPt point = new CRecPathPt(
                                double.Parse(words[0], CultureInfo.InvariantCulture),
                                double.Parse(words[1], CultureInfo.InvariantCulture),
                                double.Parse(words[2], CultureInfo.InvariantCulture),
                                double.Parse(words[3], CultureInfo.InvariantCulture),
                                bool.Parse(words[4]));
                            _recPath.recList.Add(point);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.EventWriter("Load Recorded Path " + ex.ToString());
                return false;
            }
        }

        public bool Delete(string name)
        {
            string fieldDir = _dirLote;
            if (string.IsNullOrEmpty(fieldDir)) return false;
            string recFile = Path.Combine(fieldDir, name + ".rec");
            if (!File.Exists(recFile)) return false;
            try { File.Delete(recFile); return true; }
            catch { return false; }
        }

        public void TurnOff()
        {
            _recPath.StopDrivingRecordedPath();
            _recPath.recList.Clear();
            _guardar();
            _ocultarPanel();
        }

        public bool SaveWithName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string filename = name.Trim() + ".rec";
            _guardar();
            _guardarComo(filename);
            return true;
        }

        public void Discard()
        {
            _recPath.recList.Clear();
        }
    }
}
