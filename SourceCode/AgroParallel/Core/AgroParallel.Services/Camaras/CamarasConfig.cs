// ============================================================================
// CamarasConfig.cs - modelo + persistencia (Documents\AgOpenGPS\camaras.json)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AgroParallel.Camaras
{
    public class Camara
    {
        public string nombre = "Cámara";
        public string ip = "192.168.1.64";
        public int puerto = 80;
        public int canal = 1;            // 1..N (NVR/DVR) o 1 si standalone
        public string usuario = "admin";
        public string clave = "";
        public bool activa = true;
    }

    public class CamarasConfig
    {
        public List<Camara> camaras = new List<Camara>();
        public int refrescoMs = 1000;    // 1s default

        private static string Path_
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string dir = Path.Combine(baseDir, "AgOpenGPS");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "camaras.json");
            }
        }

        public static CamarasConfig Load()
        {
            try
            {
                if (File.Exists(Path_))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };
                    var cfg = JsonSerializer.Deserialize<CamarasConfig>(File.ReadAllText(Path_), opts);
                    if (cfg != null)
                    {
                        if (cfg.camaras == null) cfg.camaras = new List<Camara>();
                        if (cfg.refrescoMs < 200) cfg.refrescoMs = 1000;
                        return cfg;
                    }
                }
            }
            catch { }

            // default: 4 cámaras placeholder
            var def = new CamarasConfig();
            for (int i = 1; i <= 4; i++)
                def.camaras.Add(new Camara { nombre = "Cámara " + i, canal = i });
            return def;
        }

        public void Save()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
                File.WriteAllText(Path_, JsonSerializer.Serialize(this, opts));
            }
            catch { }
        }

        public string SnapshotUrl(Camara c)
        {
            // Hikvision ISAPI standalone: /ISAPI/Streaming/channels/101/picture (canal 1)
            // En NVR/DVR: el canal va como NN01 (canal 1 → 101, canal 2 → 201, ...)
            int ch = c.canal <= 0 ? 1 : c.canal;
            int chCode = ch * 100 + 1;
            string host = c.ip + (c.puerto == 80 ? "" : (":" + c.puerto));
            return "http://" + host + "/ISAPI/Streaming/channels/" + chCode + "/picture";
        }

        public string RtspUrl(Camara c)
        {
            int ch = c.canal <= 0 ? 1 : c.canal;
            int chCode = ch * 100 + 1;
            string up = "";
            if (!string.IsNullOrEmpty(c.usuario))
                up = Uri.EscapeDataString(c.usuario) + ":" + Uri.EscapeDataString(c.clave ?? "") + "@";
            return "rtsp://" + up + c.ip + ":554/Streaming/Channels/" + chCode;
        }
    }
}
