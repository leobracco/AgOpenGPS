using AgLibrary.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgOpenGPS
{
    public static class RegKeys
    {
        public const string vehicleFileName = "VehicleFileName";
        public const string workingDirectory = "WorkingDirectory";
        public const string language = "Language";
    }

    // Archivo de settings de arranque (fuente primaria, ex backup del Registry).
    public class SettingsBackup
    {
        [JsonPropertyName("working_directory")]
        public string WorkingDirectory { get; set; }

        [JsonPropertyName("vehicle_file_name")]
        public string VehicleFileName { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; }
    }

    // Settings de arranque persistidas en JSON (aog_settings.json junto al exe).
    // El Registry de Windows quedó relegado a migración legacy de una sola vez
    // (traspaso portabilidad 2026-07-16): en un port a otra plataforma se
    // eliminan los métodos *LegacyRegistry* y el resto compila igual.
    public static class RegistrySettings
    {
        public const string defaultString = "Default";
        public static string culture = "en";
        public static string vehicleFileName = "";
        public static string workingDirectory = "Default";
        public static string vehiclesDirectory;
        public static string logsDirectory;
        public static string baseDirectory;
        public static string fieldsDirectory;

        // Valor de workingDirectory persistido en disco. Puede diferir del campo
        // en memoria: cambiar la carpeta de trabajo requiere reinicio, así que
        // Save() escribe el valor nuevo al JSON sin re-mapear los directorios.
        private static string persistedWorkingDirectory = "Default";

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "aog_settings.json");

        private static void SaveJson()
        {
            try
            {
                var data = new SettingsBackup
                {
                    WorkingDirectory = persistedWorkingDirectory,
                    VehicleFileName = vehicleFileName,
                    Language = culture
                };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                AgroParallel.Common.AtomicJson.Write(SettingsPath, JsonSerializer.Serialize(data, opts));
            }
            catch (Exception ex)
            {
                Log.EventWriter("Settings -> Unable to save " + SettingsPath + ": " + ex.ToString());
            }
        }

        private static SettingsBackup LoadJson()
        {
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return AgroParallel.Common.AtomicJson.Read<SettingsBackup>(SettingsPath, opts);
            }
            catch { }
            return null;
        }

        public static void Load()
        {
            var file = LoadJson();

            if (file != null)
            {
                if (!string.IsNullOrEmpty(file.WorkingDirectory)) workingDirectory = file.WorkingDirectory;
                if (!string.IsNullOrEmpty(file.VehicleFileName)) vehicleFileName = file.VehicleFileName;
                if (!string.IsNullOrEmpty(file.Language)) culture = file.Language;
            }
            else
            {
                // Primera corrida sin JSON: migrar una única vez desde el
                // Registry de Windows (instalaciones anteriores).
                TryMigrateFromLegacyRegistry();
            }

            persistedWorkingDirectory = workingDirectory;

            //make sure directories exist and are in right place if not default workingDir
            CreateDirectories();

            // Persistir (crea el archivo en primera corrida / migración).
            SaveJson();

            //keep below 500 kb
            Log.CheckLogSize(Path.Combine(logsDirectory, "AgOpenGPS_Events_Log.txt"), 1000000);

            Properties.Settings.Default.Load();
        }

        public static void Save(string name, string value)
        {
            if (name == RegKeys.vehicleFileName)
                vehicleFileName = value;
            else if (name == RegKeys.language)
                culture = value;
            else if (name == RegKeys.workingDirectory)
            {
                // MyDocuments se guarda como "Default" (comportamiento histórico).
                persistedWorkingDirectory =
                    (value == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                        ? defaultString : value;
            }

            SaveJson();
            Log.EventWriter("Settings -> Key " + name + " saved with value: "
                + (name == RegKeys.workingDirectory ? persistedWorkingDirectory : value));
        }

        public static void Reset()
        {
            try
            {
                if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
                Log.EventWriter("Settings -> Full Default Reset, deleted " + SettingsPath);
            }
            catch (Exception ex)
            {
                Log.EventWriter("Settings -> Catch, Problem Resetting settings file: " + ex.ToString());
            }

            // Borrar también la clave legacy para que una futura migración no
            // resucite los valores viejos.
            DeleteLegacyRegistry();
        }

        #region Legacy Windows Registry (eliminar en port a otra plataforma)

        private static void TryMigrateFromLegacyRegistry()
        {
            try
            {
                using (var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS"))
                {
                    if (regKey == null) return;

                    var wd = regKey.GetValue(RegKeys.workingDirectory) as string;
                    if (!string.IsNullOrEmpty(wd)) workingDirectory = wd;

                    var vf = regKey.GetValue(RegKeys.vehicleFileName) as string;
                    if (!string.IsNullOrEmpty(vf)) vehicleFileName = vf;

                    var lang = regKey.GetValue(RegKeys.language) as string;
                    if (!string.IsNullOrEmpty(lang)) culture = lang;

                    Log.EventWriter("Settings -> Migrated legacy Registry values to " + SettingsPath);
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Settings -> Legacy Registry migration failed (using defaults): " + ex.ToString());
            }
        }

        private static void DeleteLegacyRegistry()
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\AgOpenGPS", false);
            }
            catch { }
        }

        #endregion

        private static void CreateDirectories()
        {
            try
            {
                if (workingDirectory == defaultString)
                {
                    baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgOpenGPS");
                }
                else //user set to other
                {
                    baseDirectory = Path.Combine(workingDirectory, "AgOpenGPS");
                }

                // Instalación kiosko desde cero: el perfil recién booteado todavía
                // no tiene materializada la carpeta Documentos, así que
                // GetFolderPath(MyDocuments) devuelve "" y baseDirectory queda
                // RELATIVO ("AgOpenGPS"). Más tarde ApplicationModel hace
                // baseDirectory.CreateSubdirectory("Fields") SIN try/catch y
                // revienta con DirectoryNotFoundException. Forzamos una ruta
                // absoluta de respaldo (ProgramData) para que el arranque no caiga.
                if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathRooted(baseDirectory))
                {
                    string fallbackRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    if (string.IsNullOrWhiteSpace(fallbackRoot))
                        fallbackRoot = AppDomain.CurrentDomain.BaseDirectory;
                    baseDirectory = Path.Combine(fallbackRoot, "AgOpenGPS");
                    Log.EventWriter("WorkingDir -> MyDocuments vacío (kiosko), usando fallback: " + baseDirectory);
                }

                // ApplicationModel asume que baseDirectory ya existe (CreateSubdirectory
                // sin guarda). Lo creamos explícitamente acá; la creación de los
                // subdirectorios de abajo está envuelta en try/catch que sólo loguea.
                if (!Directory.Exists(baseDirectory))
                    Directory.CreateDirectory(baseDirectory);
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch, Serious Problem Making Working Directory: " + ex.ToString());

                if (workingDirectory != defaultString)
                {
                    workingDirectory = defaultString;
                    Save(RegKeys.workingDirectory, defaultString);
                    CreateDirectories();
                    return;
                }
                else//program will crash anyways!
                {
                    Log.FileSaveSystemEvents();
                    Environment.Exit(0);
                }
            }

            //get the vehicles directory, if not exist, create
            try
            {
                vehiclesDirectory = Path.Combine(baseDirectory, "Vehicles");
                if (!string.IsNullOrEmpty(vehiclesDirectory) && !Directory.Exists(vehiclesDirectory))
                {
                    Directory.CreateDirectory(vehiclesDirectory);
                    Log.EventWriter("Vehicles Dir Created");
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch, Serious Problem Making Vehicles Directory: " + ex.ToString());
            }

            //get the fields directory, if not exist, create
            try
            {
                fieldsDirectory = Path.Combine(baseDirectory, "Fields");
                if (!string.IsNullOrEmpty(fieldsDirectory) && !Directory.Exists(fieldsDirectory))
                {
                    Directory.CreateDirectory(fieldsDirectory);
                    Log.EventWriter("Fields Dir Created");
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch, Serious Problem Making Fields Directory: " + ex.ToString());
            }

            //get the logs directory, if not exist, create
            try
            {
                logsDirectory = Path.Combine(baseDirectory, "Logs");
                if (!string.IsNullOrEmpty(logsDirectory) && !Directory.Exists(logsDirectory))
                {
                    Directory.CreateDirectory(logsDirectory);
                    Log.EventWriter("Logs Dir Created");
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch, Serious Problem Making Logs Directory: " + ex.ToString());
            }
        }
    }
}
