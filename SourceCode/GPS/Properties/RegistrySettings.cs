using AgLibrary.Logging;
using AgOpenGPS.Forms;
using Microsoft.Win32;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public static class RegKeys
    {
        public const string vehicleFileName = "VehicleFileName";
        public const string workingDirectory = "WorkingDirectory";
        public const string language = "Language";
    }

    // JSON backup para las settings que el registro pierde en apagados forzados.
    public class SettingsBackup
    {
        [JsonPropertyName("working_directory")]
        public string WorkingDirectory { get; set; }

        [JsonPropertyName("vehicle_file_name")]
        public string VehicleFileName { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; }
    }

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

        private static readonly string BackupPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "aog_settings.json");

        private static void SaveBackupJson()
        {
            try
            {
                var backup = new SettingsBackup
                {
                    WorkingDirectory = workingDirectory,
                    VehicleFileName = vehicleFileName,
                    Language = culture
                };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, opts));
            }
            catch { }
        }

        private static SettingsBackup LoadBackupJson()
        {
            try
            {
                if (File.Exists(BackupPath))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<SettingsBackup>(
                        File.ReadAllText(BackupPath), opts);
                }
            }
            catch { }
            return null;
        }

        public static void Load()
        {
            bool registryOk = false;

            try
            {
                //opening the subkey
                RegistryKey regKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\AgOpenGPS");

                if (regKey.GetValue(RegKeys.workingDirectory) == null || regKey.GetValue(RegKeys.workingDirectory).ToString() == "")
                {
                    regKey.SetValue(RegKeys.workingDirectory, defaultString);
                    Log.EventWriter("Registry -> Key workingDirectory was null");
                }
                workingDirectory = regKey.GetValue(RegKeys.workingDirectory).ToString();

                //Vehicle File Name Registry Key
                if (regKey.GetValue(RegKeys.vehicleFileName) == null)
                {
                    regKey.SetValue(RegKeys.vehicleFileName, "");
                    Log.EventWriter("Registry -> Key vehicleFileName was null");
                }
                vehicleFileName = regKey.GetValue(RegKeys.vehicleFileName).ToString();

                //Language Registry Key
                if (regKey.GetValue(RegKeys.language) == null || regKey.GetValue(RegKeys.language).ToString() == "")
                {
                    regKey.SetValue(RegKeys.language, "en");
                    Log.EventWriter("Registry -> Key language was null");
                }
                culture = regKey.GetValue(RegKeys.language).ToString();

                //close registry
                regKey.Close();
                registryOk = true;
            }
            catch (Exception ex)
            {
                Log.EventWriter("Registry -> Catch, Serious Problem Creating Registry keys: " + ex.ToString());
            }

            // Si el registro falló o el workingDirectory quedó en Default sin
            // vehículo, intentar recuperar desde el backup JSON.
            if (!registryOk
                || (workingDirectory == defaultString && string.IsNullOrEmpty(vehicleFileName)))
            {
                var backup = LoadBackupJson();
                if (backup != null)
                {
                    bool restored = false;

                    if (!string.IsNullOrEmpty(backup.WorkingDirectory)
                        && (workingDirectory == defaultString || !registryOk))
                    {
                        workingDirectory = backup.WorkingDirectory;
                        restored = true;
                    }

                    if (!string.IsNullOrEmpty(backup.VehicleFileName)
                        && string.IsNullOrEmpty(vehicleFileName))
                    {
                        vehicleFileName = backup.VehicleFileName;
                        restored = true;
                    }

                    if (!string.IsNullOrEmpty(backup.Language)
                        && (culture == "en" || !registryOk))
                    {
                        culture = backup.Language;
                        restored = true;
                    }

                    if (restored)
                    {
                        Log.EventWriter("Settings restored from JSON backup: " + BackupPath);
                        // Re-escribir al registro para que quede sincronizado.
                        try
                        {
                            RegistryKey fixKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\AgOpenGPS");
                            fixKey.SetValue(RegKeys.workingDirectory, workingDirectory);
                            fixKey.SetValue(RegKeys.vehicleFileName, vehicleFileName);
                            fixKey.SetValue(RegKeys.language, culture);
                            fixKey.Close();
                        }
                        catch { }
                    }
                }
            }

            if (!registryOk && workingDirectory == defaultString)
                Reset();

            //make sure directories exist and are in right place if not default workingDir
            CreateDirectories();

            // Guardar backup JSON con los valores actuales.
            SaveBackupJson();

            //keep below 500 kb
            Log.CheckLogSize(Path.Combine(logsDirectory, "AgOpenGPS_Events_Log.txt"), 1000000);

            Properties.Settings.Default.Load();
        }

        public static void Save(string name, string value)
        {
            try
            {
                //adding or editing "Language" subkey to the "SOFTWARE" subkey  
                RegistryKey key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\AgOpenGPS");

                if (name == RegKeys.vehicleFileName)
                    vehicleFileName = value;
                else if (name == RegKeys.language)
                    culture = value;

                if (name == RegKeys.workingDirectory && value == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                {
                    key.SetValue(name, defaultString);
                    Log.EventWriter("Registry -> Key " + name + " Saved to registry key with value: " + defaultString);
                }
                else//storing the value
                {
                    key.SetValue(name, value);
                    Log.EventWriter("Registry -> Key " + name + " Saved to registry key with value: " + value);
                }

                key.Close();

                // Mantener el backup JSON sincronizado.
                SaveBackupJson();
            }
            catch (Exception ex)
            {
                Log.EventWriter("Registry -> Catch, Unable to save " + name + ": " + ex.ToString());
            }
        }

        public static void Reset()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\AgOpenGPS");

                Log.EventWriter("Registry -> Resetting Registry SubKey Tree and Full Default Reset");
            }
            catch (Exception ex)//program will crash anyways!
            {
                Log.EventWriter("Registry -> Catch, Serious Problem Resetting Registry keys: " + ex.ToString());

                Log.FileSaveSystemEvents();

                // Show critical registry error
                FormDialog.Show(
                    "Critical Registry Error",
                    "Can't delete the Registry SubKeyTree",
                    DialogSeverity.Error);


                Environment.Exit(0);
            }
        }

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
