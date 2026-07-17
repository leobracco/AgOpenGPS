using AgLibrary.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgIO
{
    public static class RegKeys
    {
        public const string profileName = "ProfileName";
        public const string workingDirectory = "WorkingDirectory";
        public const string language = "Language";
    }

    // Archivo de settings de arranque de CoreX (fuente primaria).
    public class CoreXSettingsFile
    {
        [JsonPropertyName("working_directory")]
        public string WorkingDirectory { get; set; }

        [JsonPropertyName("profile_name")]
        public string ProfileName { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; }
    }

    // Settings de arranque persistidas en JSON (corex_settings.json junto al exe).
    // El Registry de Windows quedó relegado a migración legacy de una sola vez
    // (traspaso portabilidad 2026-07-16): en un port a otra plataforma se
    // eliminan los métodos *LegacyRegistry* y el resto compila igual.
    public static class RegistrySettings
    {
        public const string defaultString = "Default";

        public static string culture = "en";
        public static string workingDirectory = "Default";
        public static string baseDirectory;
        public static string profileDirectory;
        public static string logsDirectory;
        public static string profileName = "";

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "corex_settings.json");

        private static void SaveJson()
        {
            try
            {
                var data = new CoreXSettingsFile
                {
                    WorkingDirectory = workingDirectory,
                    ProfileName = profileName,
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

        private static CoreXSettingsFile LoadJson()
        {
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return AgroParallel.Common.AtomicJson.Read<CoreXSettingsFile>(SettingsPath, opts);
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
                if (!string.IsNullOrEmpty(file.ProfileName)) profileName = file.ProfileName;
                if (!string.IsNullOrEmpty(file.Language)) culture = file.Language;
            }
            else
            {
                // Primera corrida sin JSON: migrar una única vez desde el
                // Registry de Windows (instalaciones anteriores).
                TryMigrateFromLegacyRegistry();
            }

            //make sure directories exist and are in right place if not default workingDir
            CreateDirectories();

            // Persistir (crea el archivo en primera corrida / migración).
            SaveJson();

            //keep below 500 kb
            Log.CheckLogSize(Path.Combine(logsDirectory, "AgIO_Events_Log.txt"), 1000000);

            Properties.Settings.Default.Load();
        }

        public static void Save(string name, string value)
        {
            if (name == RegKeys.profileName)
                profileName = value;
            else if (name == RegKeys.language)
                culture = value;
            else if (name == RegKeys.workingDirectory)
                workingDirectory = value;

            SaveJson();
            Log.EventWriter("Settings -> Key " + name + " saved with value: " + value);
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

                    var pf = regKey.GetValue(RegKeys.profileName) as string;
                    if (!string.IsNullOrEmpty(pf)) profileName = pf;

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

                // Perfil kiosko sin Documentos materializada: GetFolderPath puede
                // devolver "" y baseDirectory quedaría relativo. Forzar ruta
                // absoluta de respaldo (mismo criterio que PilotX).
                if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathRooted(baseDirectory))
                {
                    string fallbackRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    if (string.IsNullOrWhiteSpace(fallbackRoot))
                        fallbackRoot = AppDomain.CurrentDomain.BaseDirectory;
                    baseDirectory = Path.Combine(fallbackRoot, "AgOpenGPS");
                    Log.EventWriter("WorkingDir -> MyDocuments vacío (kiosko), usando fallback: " + baseDirectory);
                }
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

            try
            {
                logsDirectory = Path.Combine(baseDirectory, "Logs");
                //create Logs directory if not exist
                if (!string.IsNullOrEmpty(logsDirectory) && !Directory.Exists(logsDirectory))
                {
                    Directory.CreateDirectory(logsDirectory);
                    Log.EventWriter("Logs Dir Created\r");
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch, Serious Problem Making Logs Directory: " + ex.ToString());
            }

            try
            {
                //get the Documents directory, if not exist, create
                profileDirectory = Path.Combine(baseDirectory, "AgIO");

                //create Logs directory if not exist
                if (!string.IsNullOrEmpty(profileDirectory) && !Directory.Exists(profileDirectory))
                {
                    Directory.CreateDirectory(profileDirectory);
                    Log.EventWriter("Profile Dir Created\r");
                }
            }
            catch (Exception ex)
            {
                Log.EventWriter("Catch, Serious Problem Making Profile Directory: " + ex.ToString());
            }
        }
    }
}
