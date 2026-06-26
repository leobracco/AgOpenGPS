// ============================================================================
// CamarasConfigService.cs
// Lee/escribe camaras.json en MyDocuments\AgOpenGPS (mismo path que el
// legacy CamarasConfig.cs para mantener configs byte-idénticas).
// Snapshot proxy: HttpWebRequest + NetworkCredential + PreAuthenticate
// (cubre Basic y, vía challenge-response, Digest de Hikvision).
// ============================================================================

using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Services
{
    public sealed class CamarasConfigService : ICamarasConfigService
    {
        private const string FileName = "camaras.json";

        private static readonly JsonSerializerOptions ReadOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static string Path_
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string dir = System.IO.Path.Combine(baseDir, "AgOpenGPS");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, FileName);
            }
        }

        public CamarasConfigDto GetConfig()
        {
            try
            {
                if (File.Exists(Path_))
                {
                    var cfg = JsonSerializer.Deserialize<CamarasConfigDto>(File.ReadAllText(Path_), ReadOpts);
                    if (cfg != null)
                    {
                        if (cfg.Camaras == null) cfg.Camaras = new System.Collections.Generic.List<CamaraDto>();
                        if (cfg.RefrescoMs < 200) cfg.RefrescoMs = 1000;
                        return cfg;
                    }
                }
            }
            catch { }

            // Default: 4 cámaras placeholder
            var def = new CamarasConfigDto();
            for (int i = 1; i <= 4; i++)
                def.Camaras.Add(new CamaraDto { Nombre = "Cámara " + i, Canal = i });
            return def;
        }

        public void SaveConfig(CamarasConfigDto cfg)
        {
            if (cfg == null) return;
            try { File.WriteAllText(Path_, JsonSerializer.Serialize(cfg, WriteOpts)); }
            catch { }
        }

        public async Task<CameraSnapshot> FetchSnapshotAsync(int index, CancellationToken ct)
        {
            var cfg = GetConfig();
            if (cfg.Camaras == null || index < 0 || index >= cfg.Camaras.Count)
                return new CameraSnapshot { Error = "out-of-range" };

            var c = cfg.Camaras[index];
            if (c == null) return new CameraSnapshot { Error = "null-camera" };

            int ch = c.Canal <= 0 ? 1 : c.Canal;
            int chCode = ch * 100 + 1;
            string host = c.Ip + (c.Puerto == 80 ? "" : (":" + c.Puerto));
            string url = "http://" + host + "/ISAPI/Streaming/channels/" + chCode + "/picture";

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Credentials = new NetworkCredential(c.Usuario ?? "", c.Clave ?? "");
                req.PreAuthenticate = true;
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                req.UserAgent = "PilotX/1.0";

                using (ct.Register(() => { try { req.Abort(); } catch { } }))
                using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    if (s != null) await s.CopyToAsync(ms, 8192, ct).ConfigureAwait(false);
                    return new CameraSnapshot
                    {
                        Bytes = ms.ToArray(),
                        ContentType = string.IsNullOrEmpty(resp.ContentType) ? "image/jpeg" : resp.ContentType
                    };
                }
            }
            catch (Exception ex)
            {
                return new CameraSnapshot { Error = ex.Message };
            }
        }
    }
}
