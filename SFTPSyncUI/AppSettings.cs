
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SFTPSyncUI
{
    public class AppSettings
    {
        private static string DefaultSettingsFile = Path.Combine(Path.GetDirectoryName(SFTPSyncUI.ExecutableFile) ?? "", "appsettings.json");
        private static string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SFTPSync.json");
        private static bool initialLoadSettings = true;

        public static AppSettings? LoadFromFile()
        {
            AppSettings? settings = null;

            // Do we have a user settings file?
            bool justCreated = false;

            if (!File.Exists(SettingsFile))
            {
                try
                {
                    File.Copy(DefaultSettingsFile, SettingsFile);
                    justCreated = true;
                }
                catch (Exception ex)
                {
                    throw new ApplicationException($"Failed to create user settings file: {ex.Message}");
                }
            }

            // Load the settings file

            try
            {
                string jsonString = File.ReadAllText(SettingsFile);
                settings = JsonSerializer.Deserialize<AppSettings>(jsonString);
                if (settings != null)
                {
                    initialLoadSettings = false;
                    if (justCreated)
                    {
                        settings.SaveToFile();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Failed to load settings: {ex.Message}");
            }

            // Verify settings
            if (settings != null)
            {
                var setingsChanged = false;

                if (!string.IsNullOrWhiteSpace(settings.LocalPath)
                    && !Directory.Exists(settings.LocalPath))
                {
                    settings.LocalPath = String.Empty;
                    setingsChanged = true;
                }

                if (settings.ExcludedDirectories.RemoveAll(dir => !Directory.Exists(dir)) > 0)
                    setingsChanged = true;

                if (settings.AccessVerified &&
                    (string.IsNullOrWhiteSpace(settings.RemoteHost) ||
                     string.IsNullOrWhiteSpace(settings.RemoteUsername) ||
                     string.IsNullOrWhiteSpace(settings.RemotePassword)))
                {
                    settings.AccessVerified = false;
                    setingsChanged = true;
                }

                if (setingsChanged)
                {
                    settings.SaveToFile();
                }

            }

            return settings ?? null;
        }

        public bool SaveToFile()
        {
            bool saved = false;

            if (initialLoadSettings == true)
                return false;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            try
            {
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFile, json);
                saved = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return saved;
        }

        // Run at login

        private bool startAtLogin = false;
        public bool StartAtLogin
        {
            get => startAtLogin;
            set
            {
                if (startAtLogin != value)
                {
                    startAtLogin = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        // Start in tray

        private bool startInTray = false;
        public bool StartInTray
        {
            get => startInTray;
            set
            {
                if (startInTray != value)
                {
                    startInTray = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        // Auto start sync

        private bool autoStartSync = false;
        public bool AutoStartSync
        {
            get => autoStartSync;
            set
            {
                if (autoStartSync != value)
                {
                    autoStartSync = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        // Access verified

        private bool accessVerified = false;
        public bool AccessVerified
        {
            get => accessVerified;
            set
            {
                if (accessVerified != value)
                {
                    accessVerified = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        // Support delted files and folders

        private bool deleteEnabled = false;
        public bool DeleteEnabled
        {
            get => deleteEnabled;
            set
            {
                if (deleteEnabled != value)
                {
                    deleteEnabled = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Local path

        private string localPath = String.Empty;

        public string LocalPath
        {
            get => localPath;
            set
            {
                if (localPath != value)
                {
                    localPath = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Local search pattern

        private string localSearchPattern = String.Empty;

        public string LocalSearchPattern
        {
            get => localSearchPattern;
            set
            {
                if (localSearchPattern != value)
                {
                    localSearchPattern = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Remote host

        private string remoteHost = String.Empty;

        public string RemoteHost
        {
            get => remoteHost;
            set
            {
                if (remoteHost != value)
                {
                    remoteHost = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Remote username

        private string remoteUsername = String.Empty;

        public string RemoteUsername
        {
            get => remoteUsername;
            set
            {
                if (remoteUsername != value)
                {
                    remoteUsername = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Remote password

        private string remotePassword = String.Empty;

        public string RemotePassword
        {
            get => remotePassword;
            set
            {
                if (remotePassword != value)
                {
                    remotePassword = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Remote path

        private string remotePath = String.Empty;

        public string RemotePath
        {
            get => remotePath;
            set
            {
                if (remotePath != value)
                {
                    remotePath = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }

        //Excluded directories

        private List<string> excludedDirectories = new List<string>();

        public List<string> ExcludedDirectories
        {
            get => excludedDirectories;
            set
            {
                if (excludedDirectories != value)
                {
                    excludedDirectories = value;
                    if (!initialLoadSettings)
                    {
                        SaveToFile();
                    }
                }
            }
        }
    }
}