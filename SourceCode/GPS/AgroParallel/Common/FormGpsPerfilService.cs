// ============================================================================
// FormGpsPerfilService.cs
// Adaptador IPerfilVehiculoService → PilotX. Gestiona los perfiles de vehículo
// (XML completos en RegistrySettings.vehiclesDirectory) para la página HTML
// pages/perfiles.html, reemplazando a las forms nativas FormNewProfile /
// FormLoadProfile. Las acciones que tocan estado vivo de FormGPS (cargar /
// crear perfil) corren en el hilo UI vía Invoke; las de archivos (copiar,
// borrar, proteger) son puras de disco. La protección con clave vive en
// PerfilGuard (sidecar <nombre>.clave con SHA-256).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgLibrary.Settings;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;

namespace AgroParallel.Adapters
{
    using AgOpenGPS;
    using AgOpenGPS.Properties;

    public sealed class FormGpsPerfilService : IPerfilVehiculoService
    {
        private static readonly Regex InvalidFileRegex =
            new Regex(string.Format("[{0}]", Regex.Escape("<>:\"/\\|?*")));

        private readonly FormGPS _form;

        public FormGpsPerfilService(FormGPS form) { _form = form; }

        private static string Dir { get { return RegistrySettings.vehiclesDirectory; } }

        private static string XmlPath(string nombre) { return Path.Combine(Dir, nombre + ".xml"); }

        private static string Sanitizar(string nombre)
        {
            nombre = InvalidFileRegex.Replace((nombre ?? "").Trim(), "").Trim();
            // sin traversal ni nombres reservados de sidecar
            if (nombre.Contains("..")) return "";
            return nombre;
        }

        public PerfilesSnapshotDto GetSnapshot()
        {
            var snap = new PerfilesSnapshotDto
            {
                Activo = RegistrySettings.vehicleFileName,
                IsJobStarted = false,
                Perfiles = new List<PerfilItemDto>()
            };
            try { snap.IsJobStarted = _form != null && _form.isJobStarted; } catch { }
            try
            {
                var dir = new DirectoryInfo(Dir);
                foreach (var f in dir.GetFiles("*.xml").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string nombre = Path.GetFileNameWithoutExtension(f.Name);
                    snap.Perfiles.Add(new PerfilItemDto
                    {
                        Nombre = nombre,
                        Protegido = PerfilGuard.EstaProtegido(Dir, nombre),
                        Activo = string.Equals(nombre, snap.Activo, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Perfiles: error listando (" + ex.Message + ")");
            }
            return snap;
        }

        public PerfilResultDto Cargar(string nombre)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("No existe el perfil '" + nombre + "'");
            return EnUi(() => CargarEnUi(nombre));
        }

        public PerfilResultDto Nuevo(string nombre, string desde)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("Ya existe un perfil '" + nombre + "'");

            desde = Sanitizar(desde);
            if (string.IsNullOrEmpty(desde))
            {
                // arranca vacío: reset total de settings bajo el nombre nuevo
                return EnUi(() =>
                {
                    if (_form.isJobStarted) return PerfilResultDto.Fallo("Cerrá el lote antes de cambiar de perfil");
                    RegistrySettings.Save(RegKeys.vehicleFileName, nombre);
                    Settings.Default.Reset();
                    Settings.Default.Save();
                    Log.EventWriter("New profile created: " + nombre + ".xml");
                    RecargarVehiculoEnUi();
                    _form.TimedMessageBox(2500, "Perfil '" + nombre + "' creado", "Configuración reiniciada");
                    return PerfilResultDto.Exito();
                });
            }

            // desde un perfil existente: copia TODAS las configuraciones (XML entero)
            if (!File.Exists(XmlPath(desde))) return PerfilResultDto.Fallo("No existe el perfil origen '" + desde + "'");
            var flush = FlushSiEsActivo(desde);
            if (flush != null) return flush;
            try { File.Copy(XmlPath(desde), XmlPath(nombre)); }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo copiar: " + ex.Message); }
            return EnUi(() => CargarEnUi(nombre));
        }

        public PerfilResultDto Copiar(string origen, string nuevo)
        {
            origen = Sanitizar(origen);
            nuevo = Sanitizar(nuevo);
            if (string.IsNullOrEmpty(origen) || string.IsNullOrEmpty(nuevo))
                return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(origen))) return PerfilResultDto.Fallo("No existe el perfil '" + origen + "'");
            if (File.Exists(XmlPath(nuevo))) return PerfilResultDto.Fallo("Ya existe un perfil '" + nuevo + "'");

            // si copiamos el activo, persistir primero lo último en memoria para
            // que la copia lleve TODAS las configuraciones actuales del vehículo
            var flush = FlushSiEsActivo(origen);
            if (flush != null) return flush;

            try
            {
                File.Copy(XmlPath(origen), XmlPath(nuevo));
                // la copia nace desprotegida (la clave no se hereda a propósito)
                Log.EventWriter("Profile copied: " + origen + ".xml -> " + nuevo + ".xml");
                return PerfilResultDto.Exito();
            }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo copiar: " + ex.Message); }
        }

        public PerfilResultDto Borrar(string nombre, string clave)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("No existe el perfil '" + nombre + "'");
            if (string.Equals(nombre, RegistrySettings.vehicleFileName, StringComparison.OrdinalIgnoreCase))
                return PerfilResultDto.Fallo("Es el perfil en uso — cargá otro antes de borrarlo");
            if (PerfilGuard.EstaProtegido(Dir, nombre) && !PerfilGuard.VerificarClave(Dir, nombre, clave))
                return PerfilResultDto.Fallo("Clave incorrecta");
            try
            {
                File.Delete(XmlPath(nombre));
                PerfilGuard.Desproteger(Dir, nombre); // limpia el sidecar huérfano
                Log.EventWriter("Profile deleted: " + nombre + ".xml");
                return PerfilResultDto.Exito();
            }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo borrar: " + ex.Message); }
        }

        public PerfilResultDto Proteger(string nombre, string clave)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (string.IsNullOrWhiteSpace(clave)) return PerfilResultDto.Fallo("Elegí una clave (no puede ser vacía)");
            if (!File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("No existe el perfil '" + nombre + "'");
            if (PerfilGuard.EstaProtegido(Dir, nombre))
                return PerfilResultDto.Fallo("Ya está protegido — desprotegelo primero para cambiar la clave");
            try
            {
                PerfilGuard.Proteger(Dir, nombre, clave.Trim());
                Log.EventWriter("Profile protected: " + nombre + ".xml");
                return PerfilResultDto.Exito();
            }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo proteger: " + ex.Message); }
        }

        public PerfilResultDto Desproteger(string nombre, string clave)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!PerfilGuard.EstaProtegido(Dir, nombre)) return PerfilResultDto.Exito();
            if (!PerfilGuard.VerificarClave(Dir, nombre, clave)) return PerfilResultDto.Fallo("Clave incorrecta");
            try
            {
                PerfilGuard.Desproteger(Dir, nombre);
                Log.EventWriter("Profile unprotected: " + nombre + ".xml");
                return PerfilResultDto.Exito();
            }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo desproteger: " + ex.Message); }
        }

        // -------------------------------------------------------------------

        /// <summary>Si el perfil es el activo, guarda las settings en memoria a
        /// disco (en el hilo UI) para que copias/backups lleven lo último.
        /// Devuelve null si todo ok, o el fallo a propagar.</summary>
        private PerfilResultDto FlushSiEsActivo(string nombre)
        {
            if (!string.Equals(nombre, RegistrySettings.vehicleFileName, StringComparison.OrdinalIgnoreCase))
                return null;
            var r = EnUi(() => { Settings.Default.Save(); return PerfilResultDto.Exito(); });
            return r.Ok ? null : r;
        }

        /// <summary>Mismo flujo que FormLoadProfile.LoadProfile: activa el perfil
        /// y recarga vehicle/tool/settings. SIEMPRE en hilo UI.</summary>
        private PerfilResultDto CargarEnUi(string nombre)
        {
            if (_form.isJobStarted) return PerfilResultDto.Fallo("Cerrá el lote antes de cambiar de perfil");

            RegistrySettings.Save(RegKeys.vehicleFileName, nombre);

            var result = Settings.Default.Load();
            if (result != LoadResult.Ok)
                Log.EventWriter("Error loading profile " + nombre + ".xml (" + result + ")");
            else
                Log.EventWriter("Profile loaded: " + nombre + ".xml");

            RecargarVehiculoEnUi();
            _form.TimedMessageBox(2500, "Perfil '" + nombre + "' cargado", "Se reinició la configuración de dirección");
            return PerfilResultDto.Exito();
        }

        private void RecargarVehiculoEnUi()
        {
            _form.vehicle = new CVehicle(_form);
            _form.tool = new CTool(_form);
            _form.LoadSettings();
            _form.SendSettings();
            _form.SendRelaySettingsToMachineModule();
        }

        private PerfilResultDto EnUi(Func<PerfilResultDto> fn)
        {
            try
            {
                if (_form == null || _form.IsDisposed || !_form.IsHandleCreated)
                    return PerfilResultDto.Fallo("PilotX no está listo todavía");
                if (_form.InvokeRequired)
                {
                    PerfilResultDto r = null;
                    _form.Invoke((MethodInvoker)delegate
                    {
                        try { r = fn(); }
                        catch (Exception ex) { r = PerfilResultDto.Fallo("Error interno: " + ex.Message); }
                    });
                    return r ?? PerfilResultDto.Fallo("Sin respuesta del hilo UI");
                }
                return fn();
            }
            catch (Exception ex)
            {
                return PerfilResultDto.Fallo("Error interno: " + ex.Message);
            }
        }
    }
}
