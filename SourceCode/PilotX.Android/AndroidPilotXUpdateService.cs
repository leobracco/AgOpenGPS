// ============================================================================
// AndroidPilotXUpdateService.cs
// Implementación real de IPilotXUpdateService para Android (bloque 12,
// self-update). Reemplaza StubPilotXUpdateService.
//
// Reusa el motor portable PilotXSelfUpdate (AgroParallel.Services/OrbitX/,
// mismo que usa Windows) para Check/Download — catálogo OTA + descarga +
// verificación SHA256, sin duplicar esa lógica. Solo cambian 3 cosas
// respecto a Windows:
//   - product: "PilotXAndroid" en vez de "PilotX" (catálogo separado en
//     OrbitX — alguien tiene que dar de alta ese producto del lado server
//     y subir un APK antes de que Check encuentre algo).
//   - staging: Context.FilesDir/Updates/<version>/payload.apk en vez de
//     <install>/AgroParallel/Updates/<version>/payload.zip.
//   - Apply: Android no tiene "Updater.exe externo que espera el PID y
//     reemplaza el ZIP". Sin device-owner/MDM no hay silent-install: lo
//     único disponible es REQUEST_INSTALL_PACKAGES + lanzar el instalador
//     del sistema via Intent.ACTION_VIEW sobre un content:// (FileProvider,
//     ver AndroidManifest.xml + Resources/xml/file_paths.xml) — el usuario
//     confirma "Instalar" en un diálogo nativo. Fase queda en "Applying"
//     hasta que el usuario reabra la app ya actualizada; no hay forma de
//     saber desde acá si aceptó o canceló.
// ============================================================================

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using AgroParallel.Models;
using AgroParallel.OrbitX;
using AgroParallel.Services.Abstractions;
using AndroidX.Core.Content;

namespace PilotX.Droid
{
    internal sealed class AndroidPilotXUpdateService : IPilotXUpdateService, IDisposable
    {
        private const string Product = "PilotXAndroid";
        private const string PayloadFileName = "payload.apk";

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private readonly string _dataDir;

        public AndroidPilotXUpdateService(string dataDir)
        {
            _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
            PilotXSelfUpdate.SetCurrentVersion(GetInstalledVersionName());
        }

        private string StagingRoot() => Path.Combine(_dataDir, "Updates");

        private static OrbitXConfig LoadCfg()
        {
            try { return OrbitXConfig.Load(); }
            catch { return new OrbitXConfig(); }
        }

        private static string GetInstalledVersionName()
        {
            try
            {
                var ctx = global::Android.App.Application.Context;
                var info = ctx.PackageManager.GetPackageInfo(ctx.PackageName, PackageInfoFlags.MatchAll);
                return info?.VersionName;
            }
            catch { return null; }
        }

        public PilotXUpdateStatus GetStatus() => PilotXSelfUpdate.Snapshot();

        public Task<PilotXUpdateStatus> CheckAsync()
            => PilotXSelfUpdate.CheckAsync(_http, LoadCfg(), Product, StagingRoot(), PayloadFileName);

        public Task<PilotXUpdateStatus> DownloadAsync()
            => PilotXSelfUpdate.DownloadAsync(_http, LoadCfg(), Product, StagingRoot(), PayloadFileName);

        public Task<PilotXUpdateStatus> ApplyAsync()
        {
            var status = PilotXSelfUpdate.Snapshot();
            if (string.IsNullOrEmpty(status.AvailableVersion))
                throw new InvalidOperationException("No hay versión staged.");

            string apkPath = Path.Combine(StagingRoot(), status.AvailableVersion, PayloadFileName);
            if (!File.Exists(apkPath))
                throw new FileNotFoundException("APK no está en staging: " + apkPath);

            var ctx = global::Android.App.Application.Context;
            var apkFile = new Java.IO.File(apkPath);
            var uri = FileProvider.GetUriForFile(ctx, ctx.PackageName + ".fileprovider", apkFile);

            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            ctx.StartActivity(intent);
            PilotXSelfUpdate.MarkApplying();

            // No hay forma de saber desde acá si el usuario confirmó o
            // canceló el diálogo del instalador — a diferencia de Windows
            // (donde ApplyAsync espera un PID real), esto solo dispara el
            // diálogo y devuelve. Queda en "Applying" hasta que el usuario
            // reabra la app (ya sea la versión vieja si canceló, o la
            // nueva si instaló).
            return Task.FromResult(PilotXSelfUpdate.Snapshot());
        }

        public void Dispose() => _http.Dispose();
    }
}
