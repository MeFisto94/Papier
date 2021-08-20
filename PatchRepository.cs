using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NLog;

namespace Papier
{
    public class PatchRepository
    {
        public string RepositoryPath { get; }
        public string PatchPath { get; }
        
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        
        /// <summary>
        ///
        /// If any of the paths do not yet exist, attempts to create a directory.
        /// </summary>
        /// <param name="repositoryPath"></param>
        /// <param name="patchPath"></param>
        public PatchRepository(string repositoryPath, string patchPath)
        {
            RepositoryPath = repositoryPath;
            PatchPath = patchPath;

            if (!Directory.Exists(RepositoryPath))
            {
                Directory.CreateDirectory(RepositoryPath);
            }
            
            if (!Directory.Exists(PatchPath))
            {
                Directory.CreateDirectory(PatchPath);
            }
        }

        public IRepository? GetRepository()
        {
            return !Repository.IsValid(RepositoryPath) ? null : new Repository(RepositoryPath);
        }

        private IRepository GetOrCreateRepository()
        {
            if (!Repository.IsValid(RepositoryPath))
            {
                Logger.Info($"Creating a new git repository at {RepositoryPath}");
                Repository.Init(RepositoryPath);
            }

            return new Repository(RepositoryPath);
        }
        
        public void InitialCommit()
        {
            using (var repo = GetOrCreateRepository())
            {
                Logger.Info("Commiting the initial decompilation");
                repo.Config.Set("core.autocrlf", true); // Windows is fun
                Commands.Stage(repo, "*"); // TODO: Does this also work for subdirectories?
                var sign = new Signature("Papier Tool", "papier@no.reply", DateTimeOffset.Now);
                repo.Commit("Initial Commit of decompiled sources (auto-generated!)", sign, sign);
            }
        }
        
        public bool ApplyPatches()
        {
            if (Directory.GetFiles(PatchPath, "*.patch").Length == 0)
            {
                Logger.Info("No patches to apply");
                return true;
            }
            
            // Force unix paths on windows.
            var path = Path.GetRelativePath(RepositoryPath, PatchPath).Replace('\\', '/');

            // git format-patch ^$(git rev-list HEAD --reverse | head -n 1) unfortunately only works in a real bash
            // but also we can't manually call rev-list and then pipe them to format-patch, because windows has a limit
            // for the command line length.
            var pInfo = new ProcessStartInfo("bash", $"-c \"git am -3 {path}/*.patch\"");
            pInfo.WorkingDirectory = RepositoryPath;
            
            var p = Process.Start(pInfo);
            if (p == null)
            {
                Logger.Error($"Could not execute {pInfo}");
                return false;
            }
            
            // TODO: bash -c doesn't seem to propagate the exit code.
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        
        public bool RebuildPatches()
        {
            // TODO: support a commit range/branch a until the point where branch b starts etc.
            // Force unix paths on windows.
            var path = Path.GetRelativePath(RepositoryPath, PatchPath).Replace('\\', '/');
            
            // git format-patch ^$(git rev-list HEAD --reverse | head -n 1) unfortunately only works in a real bash
            // but also we can't manually call rev-list and then pipe them to format-patch, because windows has a limit
            // for the command line length.
            var pInfo = new ProcessStartInfo("bash",
                $"-c \"git format-patch -o {path} -N ^$(git rev-list HEAD --reverse | head -n 1)\"");
            pInfo.WorkingDirectory = RepositoryPath;
            
            var p = Process.Start(pInfo);
            if (p == null)
            {
                Logger.Error($"Could not execute {pInfo}");
                return false;
            }
            
            // TODO: bash -c doesn't seem to propagate the exit code.
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        public void CleanRepo()
        {
            Program.DeleteDirectory(RepositoryPath);
            Directory.CreateDirectory(RepositoryPath);
        }

        public void CleanPatches()
        {
            Program.DeleteDirectory(PatchPath);
            Directory.CreateDirectory(PatchPath);
        }
        
        public List<string> GatherPatchedFiles(string assemblyName)
        {
            // TODO: Check the folders for patches etc.
            var imp = File.ReadAllLines(Path.Combine("work", "imports.txt"))
                .Select(x => x.Trim())
                .Where(x => !x.StartsWith("#"));
                // TODO: Filter by assemblyName: "assemblyName TypeName"
            
            var pInfo = new ProcessStartInfo("bash","-c \"cat *.patch | grep 'diff --git a/'\"");
            pInfo.WorkingDirectory = PatchPath;
            pInfo.RedirectStandardOutput = true;
        
            var p = Process.Start(pInfo);
            if (p == null)
            {
                Logger.Error($"Could not execute {pInfo.Arguments}");
                return imp.ToList();
            }

            // TODO: Exit Code?
            p.WaitForExit();

            var len = "diff --git a/".Length;
            var diffLines = Program.ReadLines(p.StandardOutput).Select(x =>
            {
                var idx = x.IndexOf(' ', len);
                return x.Substring(len, idx - len - 3); // -3: ".cs"
            });

            return imp.Concat(diffLines).Distinct().ToList();
        }
    }
}