using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace SFTPSyncLib
{
    public class RemoteSync : IDisposable
    {
        string _host;
        string _username;
        string? _password;
        string _searchPattern;
        string _localRootDirectory;
        string _remoteRootDirectory;
        readonly List<string>? _excludedFolders;
        readonly bool _deleteEnabled;
        readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        readonly object _disposeLock = new object();
        Task<bool>? _connectTask;

        SftpClient _sftp;
        SyncDirector _director;
        HashSet<string> _activeDirSync = new HashSet<string>();
        readonly SemaphoreSlim _sftpLock = new SemaphoreSlim(1, 1);
        bool _disposed;

        // VS writes to a temp file (8 alphanum chars + "." + 1-4 alphanum chars + "~") then renames it to the real file.
        // The temp was never synced remotely, so suppress the "not found" cleanup warning for these names.
        private static readonly Regex _vsTempFilePattern = new Regex(@"^[a-zA-Z0-9]{8}\.[a-zA-Z0-9]{1,4}~$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static bool IsVsTempFile(string path) => _vsTempFilePattern.IsMatch(Path.GetFileName(path));

        
        public Task DoneMakingFolders { get; }

        public Task DoneInitialSync { get; }

        private SftpClient GetSftpClient(string host, string username, string? password, string? identityFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                var defPath = string.IsNullOrEmpty(identityFilePath);
                if (defPath)
                {
                    identityFilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.ssh/id_rsa";
                }
                if (!File.Exists(identityFilePath))
                {
                    string message;
                    if (defPath)
                    {
                        message = "No password or identity key provided.";
                    }
                    else
                    {
                        message = $"No password provided and identity file not found at {identityFilePath}";
                    }
                    Logger.LogError(message);
                    throw new InvalidOperationException(message);
                }
                var key = new Renci.SshNet.PrivateKeyFile(identityFilePath);    
                return new SftpClient(host, username, key);
            }
            else
            {
                return new SftpClient(host, username, password);
            }
        }

        public RemoteSync(string host, string username, string? password,
            string localRootDirectory, string remoteRootDirectory, 
            string searchPattern, bool createFolders, SyncDirector director, List<string>? excludedFolders, bool deleteEnabled, bool handleDirectoryDeletes, string? identityFile = null)
        {
            _host = host;
            _username = username;
            _password = password;
            _searchPattern = searchPattern;
            _localRootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRootDirectory));
            _remoteRootDirectory = remoteRootDirectory.TrimEnd('/', '\\');
            _director = director;
            _excludedFolders = excludedFolders ?? new List<string>();
            _deleteEnabled = deleteEnabled;
            _sftp = GetSftpClient(host, username, password, identityFile);
            //The first instance is responsible for creating ALL of the the directories.
            //Subsequent instances will not be created until this one completes.

            DoneMakingFolders = createFolders
                ? CreateDirectories(_localRootDirectory, _remoteRootDirectory)
                : Task.CompletedTask;

            //Now perform the initial sync for the pattern this instance is responsible for

            DoneInitialSync = InitialSync(_localRootDirectory, _remoteRootDirectory);

            // Register callbacks immediately; handler will ignore events until initial sync completes.
            _director.AddCallback(searchPattern, (args) => Fsw_Changed(null, args));
            if (deleteEnabled && handleDirectoryDeletes)
            {
                _director.AddDirectoryDeleteCallback((args) => Fsw_DirectoryDeleted(null, args));
            }
            if (handleDirectoryDeletes)
            {
                _director.AddDirectoryCreateCallback((args) => Fsw_DirectoryCreated(null, args));
                _director.AddDirectoryRenameCallback((args) => Fsw_DirectoryRenamed(null, args));
            }
        }

        public RemoteSync(string host, string username, string? password,
            string localRootDirectory, string remoteRootDirectory,
            string searchPattern, SyncDirector director, List<string>? excludedFolders, Task initialSyncTask, bool deleteEnabled, bool handleDirectoryDeletes, string? identityFile = null)
        {
            _host = host;
            _username = username;
            _password = password;
            _searchPattern = searchPattern;
            _localRootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRootDirectory));
            _remoteRootDirectory = remoteRootDirectory.TrimEnd('/', '\\');
            _director = director;
            _excludedFolders = excludedFolders ?? new List<string>();
            _deleteEnabled = deleteEnabled;
            _sftp = GetSftpClient(host, username, password, identityFile);
            DoneMakingFolders = Task.CompletedTask;

            DoneInitialSync = initialSyncTask;

            // Register callbacks immediately; handler will ignore events until initial sync completes.
            _director.AddCallback(searchPattern, (args) => Fsw_Changed(null, args));
            if (deleteEnabled && handleDirectoryDeletes)
            {
                _director.AddDirectoryDeleteCallback((args) => Fsw_DirectoryDeleted(null, args));
            }
            if (handleDirectoryDeletes)
            {
                _director.AddDirectoryCreateCallback((args) => Fsw_DirectoryCreated(null, args));
                _director.AddDirectoryRenameCallback((args) => Fsw_DirectoryRenamed(null, args));
            }
        }

        public static async Task RunSharedInitialSyncAsync(
            string host, string username, string password,
            string localRootDirectory, string remoteRootDirectory,
            string[] searchPatterns, List<string>? excludedFolders, int workerCount)
        {
            if (workerCount <= 0 || searchPatterns.Length == 0)
            {
                return;
            }

            localRootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRootDirectory));
            remoteRootDirectory = NormalizeRemoteRootDirectory(remoteRootDirectory);

            try
            {
                using (var sftp = new SftpClient(host, username, password))
                {
                    sftp.Connect();
                    await CreateDirectoriesInternal(sftp, localRootDirectory, localRootDirectory, remoteRootDirectory, excludedFolders);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Create directories failed. Exception: {ex.Message}");
                throw;
            }

            var workQueue = new ConcurrentQueue<SyncWorkItem>();

            foreach (var pair in EnumerateLocalDirectories(localRootDirectory, remoteRootDirectory, excludedFolders))
            {
                foreach (var pattern in searchPatterns)
                {
                    workQueue.Enqueue(new SyncWorkItem(pair.LocalPath, pair.RemotePath, pattern));
                }
            }

            var workers = new List<Task>();

            for (int i = 0; i < workerCount; i++)
            {
                workers.Add(Task.Run(async () =>
                {
                    using (var sftp = new SftpClient(host, username, password))
                    {
                        sftp.Connect();
                        while (workQueue.TryDequeue(out var item))
                        {
                            try
                            {
                                await SyncDirectoryAsync(sftp, item.LocalPath, item.RemotePath, item.SearchPattern);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError($"Sync failed for {item.LocalPath} ({item.SearchPattern}). Exception: {ex.Message}");
                            }
                        }
                    }
                }));
            }

            await Task.WhenAll(workers);
        }
        // regex to find \r\n at the end of a line
        private static readonly Regex LineEndingRegex = new Regex(@"\r\n|\r|\n", RegexOptions.Compiled);
        /// <summary>
        /// Sync changes for a file. This is only used for changes AFTER the initial sync has completed.
        /// </summary>
        /// <param name="sftp">SFTP client</param>
        /// <param name="sourcePath">Path to the source file</param>
        /// <param name="destinationPath">Path to the destination file</param>
        public static void SyncFile(SftpClient sftp, string sourcePath, string destinationPath)
        {
            Logger.LogInfo($"Syncing {sourcePath}");

            int retryCount = 0;
            const int maxRetries = 5;
            const int delayBetweenRetriesMs = 500;

            while (retryCount < maxRetries)
            {
                try
                {
                    // Because the VMS SFTP server uses Posix not RMS, and some older servers do not correctly
                    // handle truncating files when the content gets shorter (resulting in corruption at the end of the file),
                    // we delete the file first if it exists, before writing new content.
                    if (sftp.Exists(destinationPath))
                    {
                        sftp.DeleteFile(destinationPath);
                    }

                    // Read the local file content
                    var localFileContent = File.ReadAllText(sourcePath);
                    using var fs =  File.OpenRead(sourcePath);
                    using var sr = new StreamReader(fs);
                    var sb = new StringBuilder();
                    if (sftp.Exists(destinationPath)) sftp.Delete(destinationPath);
                    var sw = sftp.CreateText(destinationPath);
                    string? line; 
                    while((line = sr.ReadLine()) is not null)
                    {
                        sw.WriteLine(line.Replace("\r\n", "\n").Replace("\r", "\n"));
                    }
                    sw.Flush();
                    sw.Close();
                    sr.Close();
                    fs.Close();

                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.LogError($"Sync failed after {maxRetries} retries. Exception: {ex.Message}");
                        return;
                    }

                    Logger.LogInfo($"Retry {retryCount}/{maxRetries} after exception: {ex.Message}");
                    Thread.Sleep(delayBetweenRetriesMs);
                }
            }
        }

        public static Task<IEnumerable<FileInfo>> SyncDirectoryAsync(SftpClient sftp, string sourcePath, string destinationPath, string searchPattern)
        {
            if (new DirectoryInfo(sourcePath).EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly).Any())
            {
                Logger.LogInfo($"Initial sync started for {sourcePath}\\{searchPattern}");

                return Task<IEnumerable<FileInfo>>.Factory.FromAsync(sftp.BeginSynchronizeDirectories,
                                                   sftp.EndSynchronizeDirectories, sourcePath,
                                                   destinationPath, searchPattern, null);
            }
            else
            {
                return Task.FromResult(Enumerable.Empty<FileInfo>());
            }
        }

        private string[] FilteredDirectories(string localPath)
        {
            return FilteredDirectories(_localRootDirectory, localPath, _excludedFolders);
        }

        public async Task CreateDirectories(string localPath, string remotePath)
        {
            Logger.LogInfo($"Creating directory {remotePath}");

            try
            {
                if (!EnsureConnectedSafe())
                    return;

                await CreateDirectoriesInternal(_sftp, _localRootDirectory, localPath, remotePath, _excludedFolders);
            }
            catch (Exception)
            {
                Logger.LogError("Failed to create directories. Check the remote target directory exists.");

                Environment.Exit(-1);
            }
        }

        /// <summary>
        /// For the pattern we are responsible for, sync the files in the local directory to the remote directory.
        /// </summary>
        /// <param name="localPath">Local directory path</param>
        /// <param name="remotePath">Remote directory path</param>
        /// <returns></returns>
        public async Task InitialSync(string localPath, string remotePath)
        {
            //Wait for the folders to be created before starting the initial sync
            await DoneMakingFolders;

            if (!EnsureConnectedSafe())
                return;

            //Get the local directories to sync
            var localDirectories = FilteredDirectories(localPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            //Get the remote directories to sync, removing the .DIR suffix if it exists
            var remoteDirectories = (await ListDirectoryAsync(_sftp, remotePath)).Where(item => item.IsDirectory).ToDictionary(item =>
            {
                if (item.Name.Contains(".DIR", StringComparison.OrdinalIgnoreCase))
                    return item.Name.Remove(item.Name.IndexOf(".DIR", StringComparison.OrdinalIgnoreCase));
                else
                    return item.Name;
            });

            //
            foreach (var item in localDirectories)
            {
                var directoryName = item.Split(Path.DirectorySeparatorChar).Last();
                var nextRemotePath = AppendRemotePath(remotePath, directoryName);
                await InitialSync(localPath + "\\" + directoryName, nextRemotePath);
            }

            await SyncDirectoryAsync(_sftp, localPath, remotePath, _searchPattern);
        }

        private static string[] FilteredDirectories(string localRootDirectory, string localPath, List<string>? excludedFolders)
        {
            return Directory.GetDirectories(localPath).Where(path =>
            {
                var relativePath = path.Substring(localRootDirectory.Length);

                bool isExcluded = relativePath.EndsWith(".git")
                                  || relativePath.EndsWith(".vs")
                                  || relativePath.EndsWith("bin")
                                  || relativePath.EndsWith("obj")
                                  || relativePath.Contains(".");

                if (!isExcluded && excludedFolders != null && excludedFolders.Count > 0)
                {
                    string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                    isExcluded = excludedFolders.Any(excluded =>
                    {
                        var excludedFullPath = Path.GetFullPath(excluded).TrimEnd(Path.DirectorySeparatorChar);
                        return string.Equals(fullPath, excludedFullPath, StringComparison.OrdinalIgnoreCase);
                    });
                }

                return !isExcluded;
            }).ToArray();
        }

        private static IEnumerable<(string LocalPath, string RemotePath)> EnumerateLocalDirectories(
            string localRootDirectory,
            string remoteRootDirectory,
            List<string>? excludedFolders)
        {
            var queue = new Queue<(string LocalPath, string RemotePath)>();
            queue.Enqueue((localRootDirectory, remoteRootDirectory));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                yield return current;

                foreach (var directory in FilteredDirectories(localRootDirectory, current.LocalPath, excludedFolders)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var directoryName = directory.Split(Path.DirectorySeparatorChar).Last();
                    var remotePath = AppendRemotePath(current.RemotePath, directoryName);
                    queue.Enqueue((directory, remotePath));
                }
            }
        }

        private static async Task CreateDirectoriesInternal(
            SftpClient sftp,
            string localRootDirectory,
            string localPath,
            string remotePath,
            List<string>? excludedFolders)
        {
            var localDirectories = FilteredDirectories(localRootDirectory, localPath, excludedFolders)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var remoteDirectories = (await ListDirectoryAsync(sftp, remotePath)).Where(item => item.IsDirectory).ToDictionary(item =>
            {
                if (item.Name.Contains(".DIR", StringComparison.OrdinalIgnoreCase))
                    return item.Name.Remove(item.Name.IndexOf(".DIR", StringComparison.OrdinalIgnoreCase));
                else
                    return item.Name;
            });

            foreach (var item in localDirectories)
            {
                var directoryName = item.Split(Path.DirectorySeparatorChar).Last();
                var targetRemotePath = AppendRemotePath(remotePath, directoryName);
                if (!remoteDirectories.ContainsKey(directoryName))
                {
                    Logger.LogInfo($"Creating remote directory {targetRemotePath}");
                    sftp.CreateDirectory(targetRemotePath);
                }
                await CreateDirectoriesInternal(sftp, localRootDirectory, localPath + "\\" + directoryName, targetRemotePath, excludedFolders);
            }
        }


        public static Task UploadFileAsync(SftpClient sftp, Stream file, string destination)
        {
            Func<Stream, string, AsyncCallback, object?, IAsyncResult> begin = (stream, path, callback, state) => sftp.BeginUploadFile(stream, path, true, callback, state, null);
            return Task.Factory.FromAsync(begin, sftp.EndUploadFile, file, destination, null);
        }


        public static Task<IEnumerable<ISftpFile>> ListDirectoryAsync(SftpClient sftp, string path)
        {
            Func<string, AsyncCallback, object?, IAsyncResult> begin = (bpath, callback, state) => sftp.BeginListDirectory(bpath, callback, state, null);
            return Task.Factory.FromAsync(begin, sftp.EndListDirectory, path, null);
        }


        public static bool IsFileReady(String sFilename)
        {
            // If the file can be opened for exclusive access it means that the file
            // is no longer locked by another process.
            try
            {
                using (FileStream inputStream = File.Open(sFilename, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (inputStream.Length > 0)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }

                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string GetRemotePathForLocal(string localPath)
        {
            var relativePath = Path.GetRelativePath(_localRootDirectory, localPath);
            if (relativePath == "." || string.IsNullOrEmpty(relativePath))
                return _remoteRootDirectory;

            relativePath = relativePath.Replace('\\', '/').TrimStart('/');
            if (relativePath.Length == 0)
                return _remoteRootDirectory;

            return _remoteRootDirectory + "/" + relativePath;
        }

        private static string NormalizeRemoteRootDirectory(string remoteRootDirectory)
        {
            if (remoteRootDirectory == "/")
                return "/";

            return remoteRootDirectory.TrimEnd('/', '\\');
        }

        private static string AppendRemotePath(string remotePath, string segment)
        {
            if (string.IsNullOrEmpty(segment))
                return remotePath;

            var normalizedSegment = segment.Replace('\\', '/').Trim('/');
            if (normalizedSegment.Length == 0)
                return remotePath;

            if (remotePath == "/")
                return "/" + normalizedSegment;

            if (string.IsNullOrEmpty(remotePath))
                return normalizedSegment;

            return remotePath + "/" + normalizedSegment;
        }

        private void EnsureConnected()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RemoteSync));

            if (_sftp.IsConnected)
                return;

            _sftp.Connect();
        }

        private bool EnsureConnectedSafe()
        {
            try
            {
                EnsureConnected();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"SFTP connection error: {ex.Message}");
                return false;
            }
        }

        public Task<bool> ConnectAsync()
        {
            var task = Task.Run(() => EnsureConnectedSafe());
            _connectTask = task;
            return task;
        }


        private async void Fsw_Changed(object? sender, FileSystemEventArgs arg)
        {
            try
            {
                if (_shutdownCts.IsCancellationRequested)
                    return;

                if (!DoneInitialSync.IsCompleted)
                    return;

                if (_deleteEnabled && arg.ChangeType == WatcherChangeTypes.Deleted)
                {
                    await SyncFileDeleteAsync(arg);
                    return;
                }

                if (arg.ChangeType == WatcherChangeTypes.Changed || arg.ChangeType == WatcherChangeTypes.Created
                    || arg.ChangeType == WatcherChangeTypes.Renamed)
                {
                    var renamedArgs = arg as RenamedEventArgs;
                    var changedPath = Path.GetDirectoryName(arg.FullPath);
                    var fullRemotePath = GetRemotePathForLocal(changedPath ?? _localRootDirectory);
                    var fullRemoteFilePath = GetRemotePathForLocal(arg.FullPath);
                    var oldRemoteFilePath = renamedArgs != null
                        ? GetRemotePathForLocal(renamedArgs.OldFullPath)
                        : null;
                    await Task.Yield();
                    bool makeDirectory = true;
                    lock (_activeDirSync)
                    {
                        if (changedPath == null)
                            return;
                        if (_activeDirSync.Contains(changedPath))
                            makeDirectory = false;
                        else
                            _activeDirSync.Add(changedPath);
                    }

                    bool connectionOk;
                    await _sftpLock.WaitAsync();
                    try
                    {
                        connectionOk = EnsureConnectedSafe();
                        if (connectionOk)
                        {
                            //check if we're a new directory
                            if (makeDirectory && changedPath != null && Directory.Exists(changedPath) && !_sftp.Exists(fullRemotePath))
                            {
                                _sftp.CreateDirectory(fullRemotePath);
                            }
                        }
                    }
                    finally
                    {
                        _sftpLock.Release();
                    }

                    if (!connectionOk)
                    {
                        if (makeDirectory)
                        {
                            lock (_activeDirSync)
                            {
                                _activeDirSync.Remove(changedPath);
                            }
                        }
                        return;
                    }

                    if (makeDirectory)
                    {
                        lock (_activeDirSync)
                        {
                            _activeDirSync.Remove(changedPath);
                        }
                    }

                    if (Directory.Exists(arg.FullPath))
                        return;

                    var waitStart = DateTime.UtcNow;
                    while (!IsFileReady(arg.FullPath))
                    {
                        if (!File.Exists(arg.FullPath))
                            return;
                        if (DateTime.UtcNow - waitStart > TimeSpan.FromSeconds(30))
                        {
                            Logger.LogWarnig($"Timeout waiting for file ready: {arg.FullPath}");
                            return;
                        }
                        await Task.Delay(25);
                    }

                    lock (_activeDirSync)
                    {
                        if (_activeDirSync.Contains(arg.FullPath))
                            return;
                        else
                            _activeDirSync.Add(arg.FullPath);
                    }

                    bool fileConnectionOk;
                    await _sftpLock.WaitAsync();
                    try
                    {
                        fileConnectionOk = EnsureConnectedSafe();
                        if (fileConnectionOk)
                        {
                            SyncFile(_sftp, arg.FullPath, fullRemoteFilePath);

                            if (_deleteEnabled && oldRemoteFilePath != null
                                && !IsVsTempFile(renamedArgs!.OldFullPath)
                                && !string.Equals(oldRemoteFilePath, fullRemoteFilePath, StringComparison.OrdinalIgnoreCase))
                            {
                                if (_sftp.Exists(oldRemoteFilePath))
                                {
                                    var attributes = _sftp.GetAttributes(oldRemoteFilePath);
                                    if (!attributes.IsDirectory)
                                    {
                                        _sftp.DeleteFile(oldRemoteFilePath);
                                    }
                                    else
                                    {
                                        Logger.LogWarnig($"Rename cleanup skipped (directory): {oldRemoteFilePath}");
                                    }
                                }
                                else
                                {
                                    Logger.LogWarnig($"Rename cleanup skipped (not found): {oldRemoteFilePath}");
                                }
                            }
                            else if (_deleteEnabled && oldRemoteFilePath == null)
                            {
                                Logger.LogWarnig("Rename cleanup skipped (missing old path).");
                            }
                        }
                    }
                    finally
                    {
                        _sftpLock.Release();
                        lock (_activeDirSync)
                        {
                            _activeDirSync.Remove(arg.FullPath);
                        }
                    }

                    if (!fileConnectionOk)
                        return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unhandled file sync handler exception: {ex.Message}");
            }
        }

        private async void Fsw_DirectoryDeleted(object? sender, FileSystemEventArgs arg)
        {
            try
            {
                if (_shutdownCts.IsCancellationRequested)
                    return;

                if (!DoneInitialSync.IsCompleted)
                    return;

                if (IsExcludedDirectoryPath(arg.FullPath))
                    return;

                var remotePath = GetRemotePathForLocal(arg.FullPath);
                await _sftpLock.WaitAsync();
                try
                {
                    if (EnsureConnectedSafe())
                    {
                        if (!_sftp.Exists(remotePath) && !_sftp.Exists(remotePath + ".DIR"))
                        {
                            Logger.LogWarnig($"Deleting directory skipped (not found): {remotePath}");
                        }
                        else
                        {
                            Logger.LogInfo($"Deleting directory {remotePath}");
                            DeleteRemotePathRecursive(_sftp, remotePath);
                        }
                    }
                }
                finally
                {
                    _sftpLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unhandled directory delete handler exception: {ex.Message}");
            }
        }

        private async void Fsw_DirectoryCreated(object? sender, FileSystemEventArgs arg)
        {
            try
            {
                if (_shutdownCts.IsCancellationRequested)
                    return;

                if (!DoneInitialSync.IsCompleted)
                    return;

                if (IsExcludedDirectoryPath(arg.FullPath))
                    return;

                var remotePath = GetRemotePathForLocal(arg.FullPath);
                await _sftpLock.WaitAsync();
                try
                {
                    if (EnsureConnectedSafe())
                    {
                        if (!_sftp.Exists(remotePath))
                        {
                            Logger.LogInfo($"Creating directory {remotePath}");
                            _sftp.CreateDirectory(remotePath);
                        }
                    }
                }
                finally
                {
                    _sftpLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unhandled directory create handler exception: {ex.Message}");
            }
        }

        private async void Fsw_DirectoryRenamed(object? sender, RenamedEventArgs arg)
        {
            try
            {
                if (_shutdownCts.IsCancellationRequested)
                    return;

                if (!DoneInitialSync.IsCompleted)
                    return;

                if (IsExcludedDirectoryPath(arg.OldFullPath) || IsExcludedDirectoryPath(arg.FullPath))
                    return;

                var oldRemotePath = GetRemotePathForLocal(arg.OldFullPath);
                var newRemotePath = GetRemotePathForLocal(arg.FullPath);

                await _sftpLock.WaitAsync();
                try
                {
                    if (!EnsureConnectedSafe())
                        return;

                    if (TryRenameRemoteDirectory(_sftp, oldRemotePath, newRemotePath))
                    {
                        Logger.LogInfo($"Renamed directory {oldRemotePath} -> {newRemotePath}");
                        return;
                    }

                    Logger.LogWarnig($"Directory rename failed. Attempting fallback: {oldRemotePath} -> {newRemotePath}");

                    MoveRemoteDirectoryRecursive(_sftp, oldRemotePath, newRemotePath);

                    Logger.LogInfo($"Fallback directory rename completed: {oldRemotePath} -> {newRemotePath}");
                }
                finally
                {
                    _sftpLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Unhandled directory rename handler exception: {ex.Message}");
            }
        }

        private async Task SyncFileDeleteAsync(FileSystemEventArgs arg)
        {
            if (_shutdownCts.IsCancellationRequested)
                return;

            var remotePath = GetRemotePathForLocal(arg.FullPath);

            await _sftpLock.WaitAsync();

            try
            {
                if (!EnsureConnectedSafe())
                    return;

                if (_sftp.Exists(remotePath))
                {
                    var attributes = _sftp.GetAttributes(remotePath);

                    if (!attributes.IsDirectory)
                    {
                        Logger.LogInfo($"Syncing delete {arg.FullPath}");
                        _sftp.DeleteFile(remotePath);
                    }
                    else
                    {
                        Logger.LogWarnig($"Syncing delete skipped (directory): {remotePath}");
                    }
                }
                else
                {
                    Logger.LogWarnig($"Syncing delete {remotePath} not found");
                }
            }
            finally
            {
                _sftpLock.Release();
            }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                    return;

                if (!_shutdownCts.IsCancellationRequested)
                {
                    _shutdownCts.Cancel();
                }

                var connectTask = _connectTask;
                if (connectTask != null && !connectTask.IsCompleted)
                {
                    _ = connectTask.ContinueWith(_ => DisposeNow(), TaskScheduler.Default);
                    return;
                }

                DisposeNow();
            }
        }

        private void DisposeNow()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _shutdownCts.Dispose();
                _sftp.Dispose();
            }
        }

        private sealed class SyncWorkItem
        {
            public SyncWorkItem(string localPath, string remotePath, string searchPattern)
            {
                LocalPath = localPath;
                RemotePath = remotePath;
                SearchPattern = searchPattern;
            }

            public string LocalPath { get; }
            public string RemotePath { get; }
            public string SearchPattern { get; }
        }

        private bool IsExcludedDirectoryPath(string localPath)
        {
            var fullPath = Path.GetFullPath(localPath).TrimEnd(Path.DirectorySeparatorChar);
            if (!fullPath.StartsWith(_localRootDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            var relativePath = fullPath.Substring(_localRootDirectory.Length);
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            bool isExcluded = relativePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                              || relativePath.EndsWith(".vs", StringComparison.OrdinalIgnoreCase)
                              || relativePath.EndsWith("bin", StringComparison.OrdinalIgnoreCase)
                              || relativePath.EndsWith("obj", StringComparison.OrdinalIgnoreCase)
                              || relativePath.Contains(".");

            if (!isExcluded && _excludedFolders != null && _excludedFolders.Count > 0)
            {
                isExcluded = _excludedFolders.Any(excluded =>
                {
                    var excludedFullPath = Path.GetFullPath(excluded).TrimEnd(Path.DirectorySeparatorChar);
                    return string.Equals(fullPath, excludedFullPath, StringComparison.OrdinalIgnoreCase)
                           || fullPath.StartsWith(excludedFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                });
            }

            return isExcluded;
        }

        private static void DeleteRemotePathRecursive(SftpClient sftp, string remotePath)
        {
            if (!sftp.Exists(remotePath))
            {
                var vmsDirectoryPath = remotePath + ".DIR";
                if (!sftp.Exists(vmsDirectoryPath))
                    return;
                remotePath = vmsDirectoryPath;
            }

            var attributes = sftp.GetAttributes(remotePath);
            if (!attributes.IsDirectory)
            {
                sftp.DeleteFile(remotePath);
                return;
            }

            foreach (var entry in sftp.ListDirectory(remotePath))
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                if (entry.IsDirectory)
                {
                    DeleteRemotePathRecursive(sftp, entry.FullName);
                }
                else
                {
                    sftp.DeleteFile(entry.FullName);
                }
            }

            sftp.DeleteDirectory(remotePath);
        }

        private static bool TryRenameRemoteDirectory(SftpClient sftp, string oldRemotePath, string newRemotePath)
        {
            string? actualOldPath = null;
            string? actualNewPath = null;

            if (sftp.Exists(oldRemotePath))
            {
                actualOldPath = oldRemotePath;
                actualNewPath = newRemotePath;
            }
            else if (sftp.Exists(oldRemotePath + ".DIR"))
            {
                actualOldPath = oldRemotePath + ".DIR";
                actualNewPath = newRemotePath + ".DIR";
            }

            if (actualOldPath == null || actualNewPath == null)
                return false;

            try
            {
                sftp.RenameFile(actualOldPath, actualNewPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Directory rename failed: {ex.Message}");
                return false;
            }
        }

        private static void MoveRemoteDirectoryRecursive(SftpClient sftp, string oldRemotePath, string newRemotePath)
        {
            string actualOldPath;
            string actualNewPath = newRemotePath;

            if (sftp.Exists(oldRemotePath))
            {
                actualOldPath = oldRemotePath;
            }
            else if (sftp.Exists(oldRemotePath + ".DIR"))
            {
                actualOldPath = oldRemotePath + ".DIR";
                actualNewPath = newRemotePath + ".DIR";
            }
            else
            {
                Logger.LogError($"Directory not found for rename: {oldRemotePath}");
                return;
            }

            if (!sftp.Exists(actualNewPath))
            {
                sftp.CreateDirectory(actualNewPath);
            }

            foreach (var entry in sftp.ListDirectory(actualOldPath))
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                var destinationPath = actualNewPath + "/" + entry.Name;
                try
                {
                    sftp.RenameFile(entry.FullName, destinationPath);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Remote file move failed for {entry.FullName}: {ex.Message}");
                    if (entry.IsDirectory)
                    {
                        MoveRemoteDirectoryRecursive(sftp, entry.FullName, destinationPath);
                    }
                }
            }

            DeleteRemotePathRecursive(sftp, actualOldPath);
        }
    }
}
