// ============================================================================
// EnginePerfilService.cs — perfiles de vehículo para el motor headless.
//
// Gemelo de FormGpsPerfilService (GPS/AgroParallel/Common), que sirve la misma
// página pages/perfiles.html pero contra FormGPS. Sin esto, PerfilesController
// no se registra (se registra solo `if (_perfiles != null)`) y /api/aog/perfiles
// devuelve 404: en el stack Avalonia la pantalla de perfiles no lista NADA.
//
// La diferencia con el gemelo del form es que acá no hay hilo de UI al que
// hacer Invoke. Cambiar de perfil es: apuntar RegistrySettings al XML nuevo,
// releer Settings, y reconstruir vehículo + herramienta en el host. Ese último
// paso incluye AplicarGeometriaDeSecciones(), que reparte el ancho entre las
// secciones — saltearlo deja todas las secciones encimadas y la cobertura sale
// de ancho cero.
//
// Las operaciones de archivo (copiar, borrar, proteger) son idénticas y usan
// PerfilGuard, que por eso se movió a AgroParallel.Services: lo necesitan los
// dos motores.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgroParallel.Models;
using AgroParallel.Services.Abstractions;
using AgOpenGPS.Properties;

namespace PilotX.GuidanceEngine.Adapters
{
    using AgOpenGPS;
    using AgroParallel.Adapters;   // PerfilGuard

    public sealed class EnginePerfilService : IPerfilVehiculoService
    {
        private readonly GuidanceEngineHost _host;

        public EnginePerfilService(GuidanceEngineHost host) { _host = host; }

        private static string Dir { get { return RegistrySettings.vehiclesDirectory; } }
        private static string XmlPath(string n) { return Path.Combine(Dir, n + ".XML"); }

        /// <summary>Nombre de archivo sano: sin separadores ni traversal.</summary>
        private static string Sanitizar(string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre)) return "";
            nombre = nombre.Trim();
            if (nombre.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "";
            if (nombre.Contains("..")) return "";
            return nombre;
        }

        // ---- lectura -------------------------------------------------------

        public PerfilesSnapshotDto GetSnapshot()
        {
            var snap = new PerfilesSnapshotDto
            {
                Activo = RegistrySettings.vehicleFileName,
                IsJobStarted = false,
                Perfiles = new List<PerfilItemDto>()
            };
            try { snap.IsJobStarted = _host != null && _host.IsJobStarted; }
            catch { }

            try
            {
                var dir = new DirectoryInfo(Dir);
                if (!dir.Exists) return snap;
                foreach (var f in dir.GetFiles("*.xml").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
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
                Console.Error.WriteLine("[Perfiles] error listando: " + ex.Message);
            }
            return snap;
        }

        // ---- cambio de perfil ----------------------------------------------

        public PerfilResultDto Cargar(string nombre)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("No existe el perfil '" + nombre + "'");
            return Activar(nombre);
        }

        public PerfilResultDto Nuevo(string nombre, string desde)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("Ya existe un perfil '" + nombre + "'");

            desde = Sanitizar(desde);
            if (string.IsNullOrEmpty(desde))
            {
                // Perfil en blanco: resetear todo bajo el nombre nuevo.
                if (JobAbierto()) return PerfilResultDto.Fallo("Cerrá el lote antes de cambiar de perfil");
                try
                {
                    RegistrySettings.Save(RegKeys.vehicleFileName, nombre);
                    Settings.Default.Reset();       // Reset() ya hace Save() al XML nuevo
                    RecargarVehiculo();
                    return PerfilResultDto.Exito();
                }
                catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo crear: " + ex.Message); }
            }

            if (!File.Exists(XmlPath(desde))) return PerfilResultDto.Fallo("No existe el perfil origen '" + desde + "'");
            var flush = FlushSiEsActivo(desde);
            if (flush != null) return flush;
            try { File.Copy(XmlPath(desde), XmlPath(nombre)); }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo copiar: " + ex.Message); }
            return Activar(nombre);
        }

        public PerfilResultDto Copiar(string origen, string nuevo)
        {
            origen = Sanitizar(origen);
            nuevo = Sanitizar(nuevo);
            if (string.IsNullOrEmpty(origen) || string.IsNullOrEmpty(nuevo))
                return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(origen))) return PerfilResultDto.Fallo("No existe el perfil '" + origen + "'");
            if (File.Exists(XmlPath(nuevo))) return PerfilResultDto.Fallo("Ya existe un perfil '" + nuevo + "'");

            var flush = FlushSiEsActivo(origen);
            if (flush != null) return flush;
            try { File.Copy(XmlPath(origen), XmlPath(nuevo)); }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo copiar: " + ex.Message); }
            return PerfilResultDto.Exito();
        }

        public PerfilResultDto Borrar(string nombre, string clave)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("No existe el perfil '" + nombre + "'");

            // Borrar el perfil en uso dejaría al motor corriendo contra un XML
            // que ya no existe: la próxima escritura de settings se pierde sin
            // aviso.
            if (string.Equals(nombre, RegistrySettings.vehicleFileName, StringComparison.OrdinalIgnoreCase))
                return PerfilResultDto.Fallo("No se puede borrar el perfil activo");

            if (PerfilGuard.EstaProtegido(Dir, nombre) && !PerfilGuard.VerificarClave(Dir, nombre, clave))
                return PerfilResultDto.Fallo("Clave incorrecta");

            try
            {
                File.Delete(XmlPath(nombre));
                PerfilGuard.Desproteger(Dir, nombre);
                return PerfilResultDto.Exito();
            }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo borrar: " + ex.Message); }
        }

        public PerfilResultDto Proteger(string nombre, string clave)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!File.Exists(XmlPath(nombre))) return PerfilResultDto.Fallo("No existe el perfil '" + nombre + "'");
            if (string.IsNullOrEmpty(clave)) return PerfilResultDto.Fallo("Hace falta una clave");
            try { PerfilGuard.Proteger(Dir, nombre, clave); return PerfilResultDto.Exito(); }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo proteger: " + ex.Message); }
        }

        public PerfilResultDto Desproteger(string nombre, string clave)
        {
            nombre = Sanitizar(nombre);
            if (string.IsNullOrEmpty(nombre)) return PerfilResultDto.Fallo("Nombre de perfil inválido");
            if (!PerfilGuard.EstaProtegido(Dir, nombre)) return PerfilResultDto.Exito();
            if (!PerfilGuard.VerificarClave(Dir, nombre, clave)) return PerfilResultDto.Fallo("Clave incorrecta");
            try { PerfilGuard.Desproteger(Dir, nombre); return PerfilResultDto.Exito(); }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo desproteger: " + ex.Message); }
        }

        // ---- internos ------------------------------------------------------

        private bool JobAbierto()
        {
            try { return _host != null && _host.IsJobStarted; } catch { return false; }
        }

        /// <summary>
        /// Si el perfil que se va a copiar es el activo, hay que bajar a disco lo
        /// que esté solo en memoria: si no, la copia sale con los valores de la
        /// última vez que se guardó, no con los que el operario ve en pantalla.
        /// </summary>
        private PerfilResultDto FlushSiEsActivo(string nombre)
        {
            if (!string.Equals(nombre, RegistrySettings.vehicleFileName, StringComparison.OrdinalIgnoreCase))
                return null;
            try { Settings.Default.Save(); return null; }
            catch (Exception ex) { return PerfilResultDto.Fallo("No se pudo guardar el perfil activo: " + ex.Message); }
        }

        private PerfilResultDto Activar(string nombre)
        {
            if (JobAbierto()) return PerfilResultDto.Fallo("Cerrá el lote antes de cambiar de perfil");
            try
            {
                RegistrySettings.Save(RegKeys.vehicleFileName, nombre);
                Settings.Default.Load();
                RecargarVehiculo();
                Console.Error.WriteLine("[Perfiles] activado: " + nombre);
                return PerfilResultDto.Exito();
            }
            catch (Exception ex)
            {
                return PerfilResultDto.Fallo("No se pudo activar: " + ex.Message);
            }
        }

        /// <summary>
        /// Reconstruye vehículo y herramienta con los settings recién leídos.
        /// AplicarGeometriaDeSecciones NO es opcional: reparte el ancho entre las
        /// secciones, y sin eso quedan todas encimadas en el default de CSection
        /// y la huella sale de ancho cero.
        /// </summary>
        private void RecargarVehiculo()
        {
            _host.Vehicle = new CVehicle(_host);
            _host.Tool = new CTool(_host);
            _host.AplicarGeometriaDeSecciones();
            try { _host.SettingsSender.SendSettings(); } catch { /* sin módulos conectados */ }
        }
    }
}
