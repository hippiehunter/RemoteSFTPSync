
using Microsoft.Extensions.Configuration;
using SFTPSyncLib;
using System.CommandLine;

namespace SFTPSync
{
    class SFTPSync
    {
        public static RootCommand GetRootCommand(Microsoft.Extensions.Configuration.IConfiguration? config = null)
        {
            var rootCommand = new RootCommand("SFTP Sync is a utility application that can synchronize a local directory\nstructure and files to a remote OpenVMS system via the Secure FTP protocol. ");
            var hostArg = new Option<string>("--host", "-h") { Description = "The SFTP host to connect to" };
            var userNameArg = new Option<string>("--username", "-u") { Description = "The username for SFTP authentication" };
            var passwordArg = new Option<string>("--password") { Description = "The password for SFTP authentication" };
            var localRootDirArg = new Option<string>("--localRootDir", "-l") { Description = "The local root directory to sync from" };
            var remoteRootDirArg = new Option<string>("--remoteRootDir", "-r") { Description = "The remote root directory to sync to" };
            var searchPatternArg = new Option<string>("--searchPattern", "-s") { Description = "The semicolon seperated search pattern for files to sync (e.g. *.txt or *.jpg;*.png)" };
            var oneTimeOption = new Option<bool>("--one-time", "-o") { Description = "Perform a one-time sync and exit" };
            var identityOption = new Option<string>("--identity", "-i", "-id") { Description = "Path to the identity file for authentication" };
            rootCommand.Add(hostArg);
            rootCommand.Add(userNameArg);
            rootCommand.Add(passwordArg);
            rootCommand.Add(localRootDirArg);
            rootCommand.Add(remoteRootDirArg);
            rootCommand.Add(searchPatternArg);
            rootCommand.Add(oneTimeOption);
            rootCommand.Add(identityOption);



            rootCommand.SetAction(async (parseResult) =>
            {
                // Map SFTPSyncUI config keys to SFTPSync expected keys
                static string? GetConfigValue(Microsoft.Extensions.Configuration.IConfiguration? config, params string[] keys)
                {
                    if (config == null) return null;
                    foreach (var key in keys)
                    {
                        var val = config[key];
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                    return null;
                }


                string? GetOptionOrConfig(Option<string> opt, params string[] configKeys)
                {
                    var val = parseResult.GetValue(opt);
                    if (!string.IsNullOrEmpty(val)) return val!;
                    var configVal = GetConfigValue(config, configKeys.Length > 0 ? configKeys : new[] { opt.Name.TrimStart('-') });
                    if (!string.IsNullOrEmpty(configVal)) return configVal!;
                    return null;
                }

                // Map SFTPSyncUI keys to SFTPSync args/options
                string? host = GetOptionOrConfig(hostArg, "host", "RemoteHost");
                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new Exception("No Host specified.");
                }
                string? username = GetOptionOrConfig(userNameArg, "username", "RemoteUsername");
                if (string.IsNullOrWhiteSpace(username))
                {
                    throw new Exception("No Username specified.");
                }
                string? password = GetOptionOrConfig(passwordArg, "password", "RemotePassword");
                string? localRootDir = GetOptionOrConfig(localRootDirArg, "localRootDir", "LocalPath");
                if (string.IsNullOrWhiteSpace(localRootDir))
                {
                    throw new Exception("No Local Root Directory specified.");
                }
                string? remoteRootDir = GetOptionOrConfig(remoteRootDirArg, "remoteRootDir", "RemotePath");
                if (string.IsNullOrWhiteSpace(remoteRootDir))
                {
                    throw new Exception("No Remote Root Directory specified.");
                }
                string? searchPattern = GetOptionOrConfig(searchPatternArg, "searchPattern", "LocalSearchPattern");
                bool oneTime = parseResult.GetValue(oneTimeOption);
                string? identityFile = GetOptionOrConfig(identityOption, "identity");
                var excludedDirs = config?.GetSection("ExcludedDirectories").Get<string[]>() ?? [];
                var director = new SyncDirector(localRootDir);
                List<RemoteSync> remoteSyncWorkers = [];

                Logger.LogInfo("Starting initial sync...");

                foreach (var pattern in searchPattern?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
                {
                    if (remoteSyncWorkers.Count > 0)
                    {
                        await remoteSyncWorkers[0].DoneMakingFolders;
                    }
                    remoteSyncWorkers.Add(new RemoteSync(host, username, password, localRootDir, remoteRootDir, pattern, remoteSyncWorkers.Count == 0, director, [.. excludedDirs], false, remoteSyncWorkers.Count == 0, identityFile));

                    Logger.LogInfo($"Started sync worker {remoteSyncWorkers.Count} for pattern {pattern}");
                }

                //Wait for all sync workers to finish initial sync then tell the user
                await Task.WhenAll(remoteSyncWorkers.Select(rsw => rsw.DoneInitialSync));

                if (oneTime)
                {
                    Logger.LogInfo("Sync Complete");
                    return;
                }
                Logger.LogInfo("Initial sync complete, real-time sync active");

                Console.Write("Press Ctrl+C to exit: ");
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C)
                    {
                        Console.WriteLine();
                        break;
                    }
                }

                foreach (var remoteSync in remoteSyncWorkers)
                {
                    try
                    {
                        remoteSync.Dispose();
                    }
                    catch { }
                }
            });
            return rootCommand;
        }
    }
}
