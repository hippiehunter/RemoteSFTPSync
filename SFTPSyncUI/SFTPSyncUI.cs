
using Microsoft.Win32;
using SFTPSyncLib;
using System.IO.Pipes;
using System.Reflection;
using System.Linq;

namespace SFTPSyncUI
{
    internal static class SFTPSyncUI
    {
        public static string ExecutableFile = String.Empty;

        private static AppSettings? settings;
        private static MainForm? mainForm;

        private const string autoRunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string pipeName = "SFTPSyncUIPipe";

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            string? processPath = Environment.ProcessPath;
            if (processPath != null)
                ExecutableFile = Path.ChangeExtension(processPath, ".exe");

            Application.ThreadException += (sender, args) =>
            {
                Logger.LogError($"Unhandled UI exception: {args.Exception.Message}");
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    Logger.LogError($"Unhandled exception: {ex.Message}");
                else
                    Logger.LogError("Unhandled exception: unknown error");
            };
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Logger.LogError($"Unobserved task exception: {args.Exception.Message}");
                args.SetObserved();
            };

            // Check if another instance is already running and if so, tell it to show its window, then exit
            bool createdNew;
            Mutex mutex = new Mutex(true, $"Global\\SFTPSyncUI", out createdNew);

            if (!createdNew)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                    {
                        client.Connect(1000); // 1 second timeout
                        using (var writer = new StreamWriter(client) { AutoFlush = true })
                        {
                            writer.WriteLine("SHOW");
                        }
                    }
                }
                catch
                {
                    // If the pipe isn't available, ignore error
                }

                // Then exit this instance
                Application.Exit();
                return;
            }

            // Load settings
            try
            {
                settings = AppSettings.LoadFromFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }

            // Do we have settings?
            if (settings == null)
            {
                MessageBox.Show("Failed to load user settings!", Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }

            // Check the run at startup status is correct
            if (settings.StartAtLogin && !IsProgramInStartup())
                AddProgramToStartup();
            else if (!settings.StartAtLogin && IsProgramInStartup())
                RemoveProgramFromStartup();

            // Create an OpenPipe server to listen for messages from other instances
            StartPipeServer();

            // Run the main form (it will start hidden, but put an icon in the system tray)
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(mainForm = new MainForm(settings));
        }

        /// <summary>
        /// Start the named pipe server to listen for messages from other instances
        /// </summary>
        private static void StartPipeServer()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    using (var server = new NamedPipeServerStream(pipeName, PipeDirection.In))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server))
                        {
                            var command = reader.ReadLine();
                            if (command == "SHOW")
                            {
                                ShowMainWindow();
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Show the main window
        /// </summary>
        private static void ShowMainWindow()
        {
            if (mainForm == null)
                return;

            // Needs to be run on UI thread
            mainForm.BeginInvoke((Action)(() =>
            {
                if (mainForm.WindowState == FormWindowState.Minimized)
                    mainForm.WindowState = FormWindowState.Normal;

                mainForm.Show();
                mainForm.BringToFront();
                mainForm.Activate();
            }));
        }

        /// <summary>
        /// Check whether the program is set to run at startup
        /// </summary>
        /// <returns></returns>
        static bool IsProgramInStartup()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(autoRunRegistryKey, false))
            {
                return key?.GetValue(Application.ProductName) != null;
            }
        }

        /// <summary>
        /// Add the program to run at startup
        /// </summary>
        static void AddProgramToStartup()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(autoRunRegistryKey, true))
            {
                if (key != null)
                {
                    key.SetValue(Application.ProductName, $"\"{ExecutableFile}\"");
                }
            }
        }

        /// <summary>
        /// Remove the program from running at startup
        /// </summary>
        static void RemoveProgramFromStartup()
        {
            if (Application.ProductName != null)
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(autoRunRegistryKey, true))
                {
                    if (key?.GetValue(Application.ProductName) != null)
                    {
                        key.DeleteValue(Application.ProductName);
                    }
                }
            }
        }

        public static List<RemoteSync> RemoteSyncWorkers = new List<RemoteSync>();
        private static SyncDirector? activeDirector;
        private static Task? syncTask;
        private static CancellationTokenSource? syncCts;

        private static readonly object DirectorLock = new object();
        private static readonly object SyncStateLock = new object();
        private static readonly object RemoteSyncLock = new object();

        /// <summary>
        /// Start syncing files between local and remote
        /// </summary>
        /// <param name="loggerAction"></param>
        public static async void StartSync(Action<string> loggerAction)
        {
            // Make sure we have settings
            if (settings == null)
                return;

            Logger.LogUpdated += loggerAction;
            Logger.LogInfo("Starting sync workers...");

            var setStatus = new Action<string>(status =>
            {
                mainForm?.BeginInvoke((Action)(() => mainForm.SetStatusBarText(status)));
            });

            setStatus("Performing initial sync... DO NOT ALTER SYNCED FILES UNTIL COMPLETE");

            CancellationToken token;

            lock (SyncStateLock)
            {
                syncCts?.Cancel();
                syncCts?.Dispose();
                syncCts = new CancellationTokenSource();
                token = syncCts.Token;
            }

            try
            {
                syncTask = Task.Run(async () =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    var director = new SyncDirector(settings.LocalPath, settings.DeleteEnabled);
                    lock (DirectorLock)
                    {
                        activeDirector = director;
                    }

                    var patterns = settings.LocalSearchPattern
                        .Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(pattern => pattern.Trim())
                        .Where(pattern => pattern.Length > 0)
                        .ToArray();

                    if (patterns.Length == 0)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Logger.LogError("No valid search patterns were configured.");
                            setStatus("Invalid search patterns");
                        }
                        return;
                    }

                    var initialSyncTcs = new TaskCompletionSource<object?>();
                    var initialSyncTask = initialSyncTcs.Task;

                    foreach (var pattern in patterns)
                    {
                        if (token.IsCancellationRequested)
                            return;

                        try
                        {
                            int workerIndex;
                            RemoteSync worker;
                            lock (RemoteSyncLock)
                            {
                                workerIndex = RemoteSyncWorkers.Count;
                                worker = new RemoteSync(
                                    settings.RemoteHost,
                                    settings.RemoteUsername,
                                    DPAPIEncryption.Decrypt(settings.RemotePassword),
                                    settings.LocalPath,
                                    settings.RemotePath,
                                    pattern,
                                    director,
                                    settings.ExcludedDirectories,
                                    initialSyncTask,
                                    settings.DeleteEnabled,
                                    workerIndex == 0);
                                RemoteSyncWorkers.Add(worker);
                            }

                            Logger.LogInfo($"Started sync worker {workerIndex + 1} for pattern {pattern}");
                        }
                        catch (Exception)
                        {
                            if (!token.IsCancellationRequested)
                                Logger.LogError($"Failed to start sync worker for pattern {pattern}");
                        }
                    }

                    director.Start();

                    var connectTasks = new List<Task>();

                    RemoteSync[] workerSnapshot;
                    lock (RemoteSyncLock)
                    {
                        workerSnapshot = RemoteSyncWorkers.ToArray();
                    }

                    if (token.IsCancellationRequested)
                        return;

                    Logger.LogInfo($"Establishing SFTP connections for {workerSnapshot.Length} workers");

                    for (int i = 0; i < workerSnapshot.Length; i++)
                    {
                        connectTasks.Add(workerSnapshot[i].ConnectAsync());
                    }
                    await Task.WhenAll(connectTasks);

                    Task runInitialSyncTask;

                    try
                    {
                        runInitialSyncTask = RemoteSync.RunSharedInitialSyncAsync(
                            settings.RemoteHost,
                            settings.RemoteUsername,
                            DPAPIEncryption.Decrypt(settings.RemotePassword),
                            settings.LocalPath,
                            settings.RemotePath,
                            patterns,
                            settings.ExcludedDirectories,
                            patterns.Length);
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Logger.LogError($"Failed to start initial sync. Exception: {ex.Message}");
                            setStatus("Initial sync failed");
                        }
                        return;
                    }

                    if (token.IsCancellationRequested)
                        return;

                    await runInitialSyncTask.ContinueWith(t =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        if (t.IsFaulted && t.Exception != null)
                        {
                            initialSyncTcs.TrySetException(t.Exception.InnerExceptions);
                        }
                        else if (t.IsCanceled)
                        {
                            initialSyncTcs.TrySetCanceled();
                        }
                        else
                        {
                            initialSyncTcs.TrySetResult(null);
                        }
                    }, TaskScheduler.Default);

                    //Wait for all sync workers to finish initial sync then tell the user

                    try
                    {
                        await runInitialSyncTask;
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Logger.LogError($"Initial sync failed. Exception: {ex.Message}");
                            setStatus("Initial sync failed");
                        }
                        return;
                    }

                    if (!token.IsCancellationRequested)
                    {
                        Logger.LogInfo("Initial sync complete, real-time sync active");
                        setStatus("Sync active");
                    }
                });

                await syncTask;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Logger.LogError($"Failed to start sync. Exception: {ex.Message}");
                    setStatus("Sync failed");
                }
                return;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="loggerAction"></param>
        public static async void StopSync(Action<string> loggerAction)
        {
            var setStatus = new Action<string>(status =>
            {
                mainForm?.BeginInvoke((Action)(() => mainForm.SetStatusBarText(status)));
            });

            setStatus("Stopping sync...");

            lock (SyncStateLock)
            {
                syncCts?.Cancel();
                syncCts?.Dispose();
                syncCts = null;
            }

            Logger.LogUpdated -= loggerAction;

            RemoteSync[] workerSnapshot;
            lock (RemoteSyncLock)
            {
                workerSnapshot = RemoteSyncWorkers.ToArray();
                RemoteSyncWorkers.Clear();
            }

            foreach (var remoteSync in workerSnapshot)
            {
                try
                {
                    remoteSync.Dispose();
                }
                catch { /* Swallow any exceptions */ }
            }

            lock (DirectorLock)
            {
                activeDirector?.Dispose();
                activeDirector = null;
            }

            var task = syncTask;
            if (task != null)
            {
                try
                {
                    await task;
                }
                catch
                {
                }
            }

            loggerAction($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} INF: Sync stopped");

            setStatus("Sync inactive");
        }
    }
}
