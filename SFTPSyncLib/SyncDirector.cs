
using System.Text.RegularExpressions;

namespace SFTPSyncLib
{
    public class SyncDirector
    {
        FileSystemWatcher _fsw;
        List<(Regex, Action<FileSystemEventArgs>)> callbacks = new List<(Regex, Action<FileSystemEventArgs>)>();
        readonly List<Action<FileSystemEventArgs>> directoryDeleteCallbacks = new List<Action<FileSystemEventArgs>>();
        readonly List<Action<FileSystemEventArgs>> directoryCreateCallbacks = new List<Action<FileSystemEventArgs>>();
        readonly List<Action<RenamedEventArgs>> directoryRenameCallbacks = new List<Action<RenamedEventArgs>>();
        readonly HashSet<string> knownDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly object directoryLock = new object();
        readonly bool deleteEnabled;
        bool _disposed;

        public SyncDirector(string rootFolder, bool deleteEnabled = false)
        {
            this.deleteEnabled = deleteEnabled;
            _fsw = new FileSystemWatcher(rootFolder, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                // Max allowed size; helps reduce missed events during bursts.
                InternalBufferSize = 64 * 1024
            };

            if (deleteEnabled)
            {
                SeedKnownDirectories(rootFolder);
            }

            _fsw.Created += Fsw_Created;
            _fsw.Changed += Fsw_Changed;
            _fsw.Renamed += Fsw_Renamed;
            if (deleteEnabled)
            {
                _fsw.Deleted += Fsw_Deleted;
            }
            _fsw.Error += Fsw_Error;
        }

        public void Start()
        {
            _fsw.EnableRaisingEvents = true;
        }

        public void AddCallback(string match, Action<FileSystemEventArgs> handler)
        {
            string regexPattern = "^" + Regex.Escape(match)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            callbacks.Add((new Regex(regexPattern, RegexOptions.IgnoreCase), handler));
        }

        public void AddDirectoryDeleteCallback(Action<FileSystemEventArgs> handler)
        {
            directoryDeleteCallbacks.Add(handler);
        }

        public void AddDirectoryCreateCallback(Action<FileSystemEventArgs> handler)
        {
            directoryCreateCallbacks.Add(handler);
        }

        public void AddDirectoryRenameCallback(Action<RenamedEventArgs> handler)
        {
            directoryRenameCallbacks.Add(handler);
        }

        private void Fsw_Created(object sender, FileSystemEventArgs e)
        {
            if (deleteEnabled)
            {
                TrackDirectoryIfPresent(e.FullPath);
            }

            if (Directory.Exists(e.FullPath))
            {
                foreach (var callback in directoryCreateCallbacks)
                {
                    callback(e);
                }
            }

            var name = Path.GetFileName(e.FullPath);
            foreach (var (regex, callback) in callbacks)
            {
                if (regex.IsMatch(name))
                {
                    callback(e);
                }
            }
        }

        private void Fsw_Changed(object sender, FileSystemEventArgs e)
        {
            var name = Path.GetFileName(e.FullPath);
            foreach (var (regex, callback) in callbacks)
            {
                if (regex.IsMatch(name))
                {
                    callback(e);
                }
            }
        }

        private void Fsw_Renamed(object sender, RenamedEventArgs e)
        {
            if (deleteEnabled)
            {
                HandleRenameForDirectoryTracking(e.OldFullPath, e.FullPath);
            }

            if (Directory.Exists(e.FullPath))
            {
                foreach (var callback in directoryRenameCallbacks)
                {
                    callback(e);
                }
            }

            var name = Path.GetFileName(e.FullPath);
            foreach (var (regex, callback) in callbacks)
            {
                if (regex.IsMatch(name))
                {
                    callback(e);
                }
            }
        }

        private void Fsw_Deleted(object sender, FileSystemEventArgs e)
        {
            if (!deleteEnabled)
                return;

            if (TryRemoveKnownDirectory(e.FullPath))
            {
                foreach (var callback in directoryDeleteCallbacks)
                {
                    callback(e);
                }
                return;
            }

            var name = Path.GetFileName(e.FullPath);
            foreach (var (regex, callback) in callbacks)
            {
                if (regex.IsMatch(name))
                {
                    callback(e);
                }
            }
        }

        private void Fsw_Error(object sender, ErrorEventArgs e)
        {
            Logger.LogError($"FileSystemWatcher error: {e.GetException()?.Message}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }

        private void SeedKnownDirectories(string rootFolder)
        {
            try
            {
                var directories = Directory.EnumerateDirectories(rootFolder, "*", SearchOption.AllDirectories)
                    .Select(NormalizePath)
                    .ToList();

                lock (directoryLock)
                {
                    knownDirectories.Add(NormalizePath(rootFolder));
                    foreach (var dir in directories)
                    {
                        knownDirectories.Add(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to seed directory tracking. Exception: {ex.Message}");
            }
        }

        private void TrackDirectoryIfPresent(string path)
        {
            if (!Directory.Exists(path))
                return;

            var normalized = NormalizePath(path);
            lock (directoryLock)
            {
                knownDirectories.Add(normalized);
            }
        }

        private void HandleRenameForDirectoryTracking(string oldPath, string newPath)
        {
            var oldNormalized = NormalizePath(oldPath);
            bool wasDirectory;
            lock (directoryLock)
            {
                wasDirectory = knownDirectories.Remove(oldNormalized);
            }

            if (wasDirectory)
            {
                TrackDirectoryIfPresent(newPath);
            }
        }

        private bool TryRemoveKnownDirectory(string path)
        {
            var normalized = NormalizePath(path);
            lock (directoryLock)
            {
                return knownDirectories.Remove(normalized);
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
