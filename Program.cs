using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommandLine;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using NLog;
using NLog.Config;
using NLog.Targets;
using static NLog.LogLevel;
using LanguageVersion = ICSharpCode.Decompiler.CSharp.LanguageVersion;

namespace Papier
{
    class Program
    {
        private static void Main(string[] args)
        {
            var sw = new Stopwatch();
            sw.Start();
            
            Parser.Default.ParseArguments<ProgramOptions>(args).WithParsed(o =>
            {
                SetupLogging(o);
                switch (o.Method.ToLower())
                {
                    case "applypatches":
                    case "sln":
                    {
                        var apply = o.Method.ToLower().Equals("applypatches");
                        var buildData = GetAndCreateBuildDataFolder();
                        // TODO: Should check for presence of a few refs here (UnityEngine, mscorlib), but that's application dependent.
                        
                        Directory.CreateDirectory("repos");
                        var ar = new DefaultAssemblyResolver();
                        ar.AddSearchDirectory(buildData);

                        if (o.AssemblyWildcard == null)
                        {
                            o.AssemblyWildcard = "Assembly-CSharp";
                            Console.WriteLine("Using Assembly-CSharp as the wildcard");
                        }
                        
                        var assemblyStringList = Directory.EnumerateFiles(buildData, $"{o.AssemblyWildcard}.dll")
                            .Select(Path.GetFileName).ToList();

                        if (assemblyStringList.Count == 0)
                        {
                            Console.Error.WriteLine("No files match the wildcard");
                            Environment.Exit(-1);
                        }
                        
                        foreach (var s in assemblyStringList.Where(x => !File.Exists(Path.Combine(buildData, x))))
                        {
                            Console.Error.WriteLine($"Missing {s} in work/BuildData!");
                            Environment.Exit(-1);
                        }
                        
                        var assemblyList = assemblyStringList.AsParallel()
                            .Select(assemblyName => (assemblyName, assembly: AssemblyDefinition.ReadAssembly(
                                Path.Combine(buildData, assemblyName),
                                new ReaderParameters { AssemblyResolver = ar })))
                            .ToList();

                        assemblyList.ForEach(tuple =>
                        {
                            var (assemblyFile, assembly) = tuple;
                            var assemblyPath = Path.Combine(buildData, assemblyFile);
                            var assemblyNameWithoutExt = Path.GetFileNameWithoutExtension(assemblyFile);

                            if (assembly.Modules.Count > 1)
                            {
                                // TODO: Actually we only create one assembly per module, multiple modules are not supported yet.
                                Console.Error.WriteLine($"Error: Assembly {assembly} contains more than one module, " +
                                                        "will pick the Main Module.\nFound modules: " +
                                                        $"{string.Join(", ", assembly.Modules.Select(m => m.ToString()))}");
                            }

                            // Move into class: Decompiler or something.
                            var decompiler = new Decompiler(new CSharpDecompiler(assemblyPath, 
                                new UniversalAssemblyResolver(assemblyPath, !o.IgnoreUnresolvedAssemblies, null),
                                new DecompilerSettings(LanguageVersion.CSharp7)));
                            
                            if (!apply)
                            {
                                var slnPath = Path.Combine("work", "sln", $"{assemblyNameWithoutExt}-Full");
                                Directory.CreateDirectory(slnPath);
                                
                                decompiler.DecompileTypes(assembly.Modules.SelectMany(x => x.Types), slnPath);
                                var projB = new ProjectBuilder
                                {
                                    ProjectFolder = Path.Combine("work", "sln", $"{assemblyNameWithoutExt}-Full"),
                                    ProjectName = $"{assemblyNameWithoutExt}-Full",
                                    DebugBuild = true
                                };

                                foreach (var refPath in Directory.GetFiles(buildData))
                                {
                                    projB.WithReference(Path.GetFullPath(refPath));
                                }
                                projB.Build();
                                return;
                            }
                            
                            var patchRepo = new PatchRepository(Path.Combine("repos", assemblyNameWithoutExt), 
                                Path.Combine("patches", assemblyNameWithoutExt));

                            patchRepo.CleanRepo();
                            var patchedFiles = patchRepo.GatherPatchedFiles();
                            
                            // TODO: MainModule instead of Modules? Here we could easily support multiple modules...
                            decompiler.DecompileTypes(assembly.Modules.SelectMany(x => x.Types), 
                                patchRepo.RepositoryPath, patchedFiles);

                            // TODO: patchRepo.AddGitIgnore(); -- csproj files.
                            patchRepo.InitialCommit();
                            
                            Console.WriteLine("Applying patches to the working directory...");
                            if (!patchRepo.ApplyPatches())
                            {
                                Console.Error.WriteLine("Patches didn't apply cleanly. Inspect the situation at " +
                                                        $"{patchRepo.RepositoryPath} and {patchRepo.PatchPath}.\n" +
                                                        "Once resolved, call \"git am --continue\".\n" +
                                                        "Then call the build target or rebuild the patches directly.");
                                Environment.Exit(-1);
                            }

                            Console.WriteLine("Patches have been applied, now building the DLLs");
                            BuildWorkingDir(patchedFiles, assembly, assemblyFile, patchRepo.RepositoryPath,
                                buildData, o, assemblyPath, ar);
                            
                            Console.WriteLine("Writing the final csproj");
                            var pb = new ProjectBuilder
                            {
                                ProjectFolder = patchRepo.RepositoryPath,
                                ProjectName = "Assembly-CSharp-Patches",
                                DebugBuild = true
                            };

                            foreach (var refPath in Directory.GetFiles(buildData))
                            {
                                pb.WithReference(Path.GetFullPath(refPath));
                            }
                            
                            pb.Build();
                        });

                        if (!apply)
                        {
                            var solB = new SolutionBuilder
                            {
                                SolutionFolder = Path.Combine("work", "sln"),
                                SolutionName = "DecompiledModules",
                                Projects = assemblyList
                                    .Select(x => $"{Path.GetFileNameWithoutExtension(x.assemblyName)}-Full")
                                    .ToList()
                            };
                            solB.Build();

                            Console.WriteLine("Done creating a visual studio solution at work/sln");
                        }
                    }
                    break;

                    case "rebuildpatches": // TODO: Assembly Name
                        foreach (var dir in Directory.EnumerateDirectories("repos"))
                        {
                            var dirName = Path.GetFileName(dir)!;
                            var patchRepo = new PatchRepository(Path.Combine("repos", dirName), 
                                Path.Combine("patches", dirName));

                            if (patchRepo.GetRepository() == null)
                            {
                                Console.WriteLine($"No Git Repository found in {dirName}. Apply Patches first");
                                continue;
                            }

                            patchRepo.CleanPatches();
                            if (!patchRepo.RebuildPatches())
                            {
                                Console.WriteLine($"Rebuilding Patches failed for {dirName}");
                            }
                        }
                        break;

                    case "build":
                    {
                        var buildData = Path.Combine("work", "BuildData");
                        var ar = new DefaultAssemblyResolver();
                        ar.AddSearchDirectory(buildData);

                        var assemblyList = Directory.EnumerateDirectories("repos") // repos/Assembly-CSharp
                            .Select(x => Path.GetFileName(x)) // Cheat: Pick the uppermost folder, dir would be repos otherwise
                            .Select(x => $"{x}.dll")
                            .AsParallel()
                            .Select(assemblyName => (assemblyName, assembly: AssemblyDefinition.ReadAssembly(
                                Path.Combine(buildData, assemblyName),
                                new ReaderParameters { AssemblyResolver = ar })))
                            .ToList();

                        var hadErrors = assemblyList.Select(tuple =>
                        {
                            var (assemblyFile, assembly) = tuple;
                            var assemblyPath = Path.Combine(buildData, assemblyFile);
                            var moduleName = Path.GetFileNameWithoutExtension(assemblyFile);
                            var patchRepo = new PatchRepository(Path.Combine("repos", moduleName),
                                Path.Combine("patches", moduleName));

                            var patchedFiles = patchRepo.GatherPatchedFiles();
                            return BuildWorkingDir(patchedFiles, assembly, assemblyFile, 
                                patchRepo.RepositoryPath, buildData,  o, assemblyPath, ar);
                        }).Any(x => !x);

                        if (hadErrors)
                        {
                            Console.Error.WriteLine("Compilation failed for at least one assembly. Check the logs");
                            Environment.Exit(-1);
                        }
                    }
                    break;
                }
            });
            
            sw.Stop();
            Console.WriteLine($"Execution took {sw.Elapsed} seconds");
        }

        private static string GetAndCreateBuildDataFolder()
        {
            var buildData = Path.Combine("work", "BuildData");
            if (!Directory.Exists("work") || !Directory.Exists(buildData))
            {
                Directory.CreateDirectory(buildData);
            }

            return buildData;
        }

        private static void SetupLogging(ProgramOptions options)
        {
            var config = new LoggingConfiguration();

            // Targets where to log to: File and Console
            Target target = options.DisableColor ? new ConsoleTarget("logconsole") : 
                new ColoredConsoleTarget("logconsole");

            if (target is ColoredConsoleTarget col)
            {
                col.DetectConsoleAvailable = true;
                col.DetectOutputRedirected = true;
                col.UseDefaultRowHighlightingRules = true;
                //col.EnableAnsiOutput = false;
            }

            // Rules for mapping loggers to targets            
            config.AddRule(options.Verbose ? LogLevel.Debug : Info, Fatal, target);

            // Apply config           
            LogManager.Configuration = config;
        }

        private static bool BuildWorkingDir(IEnumerable<string> patchedFiles, AssemblyDefinition assembly, string assemblyFile,
            string assemblySourceCodeFolder, string buildData, ProgramOptions o, string assemblyPath,
            DefaultAssemblyResolver ar)
        {
            var moduleName = Path.GetFileNameWithoutExtension(assemblyFile)!;
            var stubPath = Path.Combine("work", "stubs", moduleName);
            var stubBuilder = new StubBuilder(stubPath, assembly);
            stubBuilder.Clean();
            
            var sourceSet = new HashSet<string>(patchedFiles);
            var stubTypes = stubBuilder.CreateStubs(sourceSet);
            
            // Save names, before they get changed in the stripping step
            var stubTypeNames = stubTypes.Keys.Select(x => x.Name).ToList();
            stubTypeNames.Add("PapierStub"); // The type that annotates every stub also needs to be part of the src
            
            // TODO: In the following, at least modulePath refers to the assembly, not the module.
            var asmBuilder = new AssemblyBuilder(o, ar, sourceSet, Path.Combine("work", "bin"), moduleName);
            asmBuilder.Clean();
            asmBuilder.StripAssembly(stubTypes.Keys, assembly);

            var sourceFiles = GenerateModuleSourceSet(sourceSet, assemblySourceCodeFolder);
            AddStubsToCompilation(stubTypeNames, stubPath, sourceFiles);

            var refs = Directory.GetFiles(buildData)
                .Where(x => !x.ToLower().EndsWith($"{moduleName}.dll".ToLower()))
                .Select(x => MetadataReference.CreateFromFile(x)).ToList();
            refs.Add(MetadataReference.CreateFromFile(Path.Combine(asmBuilder.OutputPath, $"{moduleName}-stripped.dll")));
            var comp = CSharpCompilation.Create($"{moduleName}-diff.dll", sourceFiles,
                refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    concurrentBuild: true, moduleName: $"{moduleName}-diff.dll"));
            
            if (!asmBuilder.EmitDll(comp))
            {
                Console.Error.WriteLine("Compilation failed.");
                return false;
            }
            
            asmBuilder.MergeAssemblies(assemblyPath);
            
            return true;
        }

        private static List<SyntaxTree> GenerateModuleSourceSet(HashSet<string> sourceSet, string modulePath)
        {
            var sourceFiles = sourceSet.Select(x =>
            {
                var st = SourceText.From(File.Open(Path.Combine(modulePath, $"{x}.cs"),
                    FileMode.Open, FileAccess.Read, FileShare.Read));
                return SyntaxFactory.ParseSyntaxTree(st,
                    new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp8), $"{x}.cs");
            }).ToList();
            return sourceFiles;
        }

        private static void AddStubsToCompilation(IEnumerable<string> stubTypes, string stubPath, List<SyntaxTree> sourceFiles)
        {
            // TODO: Namespaces.
            foreach (var stub in stubTypes.Select(x => Path.Combine(stubPath, $"{x}.cs")))
            {
                var st = SourceText.From(File.Open(stub, FileMode.Open,
                    FileAccess.Read, FileShare.Read));
                sourceFiles.Add(SyntaxFactory.ParseSyntaxTree(st,
                    new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp8), $"{Path.GetFileName(stub)}"));
            }
        }
        
        public static void DeleteDirectory(string targetDir)
        {
            File.SetAttributes(targetDir, FileAttributes.Normal);

            var files = Directory.GetFiles(targetDir);
            var dirs = Directory.GetDirectories(targetDir);

            foreach (var file in files)
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (var dir in dirs)
            {
                DeleteDirectory(dir);
            }

            Directory.Delete(targetDir, false);
        }
        
        public static IEnumerable<string> ReadLines(StreamReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }
    }
}
