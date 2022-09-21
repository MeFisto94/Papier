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
                Logger.Info("Creating .gitignore file");
                File.WriteAllLines(Path.Combine(RepositoryPath, ".gitignore"), new []{ ".idea/", 
                    "obj/", "*.csproj"});
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
            
            // Three Way Merge: Unfortunately we can't use 3way merges, because we don't have any history of the target
            // application (or we don't want to generate multiple decompilation commits and especially! require the user
            // to supply multiple versions of the whole application).
            // Sadly, that complicates conflicts a bit, since you don't get the regular git mergetool inline conflicts,
            // in fact there are no conflicts, just a patch that didn't apply and you need to manually find out which
            // context doesn't match 100% (or use wiggle)
            // --whitespace=fix ignores and fixes whitespace problems coming from patches, leading to cleaner patches
            // after one round-trip. For some reason I had issues with patches having weird whitespaces at EOL, that
            // make patches troublesome to apply.
            
            var pInfo = new ProcessStartInfo("bash", $"-c \"git am --whitespace=fix {path}/*.patch\"");
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
                $"-c \"git format-patch --zero-commit --full-index --no-signature --no-stat -o {path} -N ^$(git rev-list HEAD --reverse | head -n 1)\"");
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
            if (!Directory.Exists(PatchPath))
            {
                Directory.CreateDirectory(PatchPath);
                return;
            }
            
            var files = Directory.GetFiles(PatchPath);

            foreach (var file in files)
            {
                if (file.EndsWith(".patch"))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
        }
        
        public List<string> GatherPatchedFiles()
        {
            var importsFile = Path.Combine(PatchPath, "imports.txt");
            // TODO: maybe copy/autogenerate the imports.txt file with some content?
            var imp = !File.Exists(importsFile) ? Enumerable.Empty<string>() : 
                File.ReadAllLines(importsFile)
                .Select(x => x.Trim())
                .Where(x => !x.StartsWith("#"));
            
            var pInfo = new ProcessStartInfo("bash","-c \"cat *.patch | grep 'diff --git a/'\"")
             {
                 WorkingDirectory = PatchPath,
                 RedirectStandardOutput = true,
                 RedirectStandardError = true
             };

            var p = Process.Start(pInfo);
            if (p == null)
            {
                Logger.Error($"Could not execute {pInfo.Arguments}");
                return imp.ToList();
            }
            
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                Logger.Error($"Could not execute {pInfo.Arguments}: Do you have WSL installed?");
                Logger.Error($"Standard-Error: {string.Join('\n', Program.ReadLines(p.StandardError))}");
                return imp.ToList();
            }

            var len = "diff --git a/".Length;
            var diffLines = Program.ReadLines(p.StandardOutput)
                .Where(x => !x.StartsWith("diff --git a/null")) // Skip freshly created files.
                .Select(x =>
            {
                var idx = x.IndexOf(' ', len);
                if (idx == -1)
                {
                    throw new ArgumentException($"Error when parsing patch line {x}: Could not find the space");
                }
                
                if (idx - len - 3 <= 0)
                {
                    throw new ArgumentException($"Error when parsing patch line {x}: idx={idx}, len={len}");
                }
                
                return x.Substring(len, idx - len - 3); // -3: ".cs"
            });

            return imp.Concat(diffLines).Distinct().ToList();
        }
    }
}