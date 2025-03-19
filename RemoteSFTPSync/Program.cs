﻿using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteSFTPSync
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 6)
            {
                Console.WriteLine("usage: RemoteSFTPSync.exe host username password localRootDir remoteRootDir searchPattern");
            }
            else
            {
                List<RemoteSync> remoteSyncs = new List<RemoteSync>();
                foreach (var split in args[5].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    remoteSyncs.Add(new RemoteSync(args[0], args[1], args[2], args[3], args[4], split));
                }
                
                Console.WriteLine("Press any key to exit remote sftp sync");
                Console.ReadKey();

                foreach (var remoteSync in remoteSyncs)
                {
                    try
                    {
                        remoteSync.Dispose();
                    }
                    catch { }
                }
            }
        }

        
    }
}
