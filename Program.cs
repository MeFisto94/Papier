using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommandLine;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ILRepacking;
using LibGit2Sharp;
using Microsoft.Build.Construction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using NLog.Targets;
using static NLog.LogLevel;
using AssemblyNameReference = Mono.Cecil.AssemblyNameReference;
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

                        var assemblyStringList = new List<string> { "Assembly-CSharp.dll" };
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

                            PatchRepository patchRepo;
                            if (!apply)
                            {
                                patchRepo = new PatchRepository(Path.Combine("work", "sln", assemblyNameWithoutExt), 
                                    Path.Combine("patches", assemblyNameWithoutExt));
                                decompiler.DecompileTypes(assembly.Modules.SelectMany(x => x.Types), patchRepo.RepositoryPath);
                                WriteCSProjAndSln(assembly, buildData, Path.Combine("work", "sln"));
                                Console.WriteLine("Done creating a visual studio solution at work/sln");
                                return;
                            }
                            
                            patchRepo = new PatchRepository(Path.Combine("repos", assemblyNameWithoutExt), 
                                Path.Combine("patches", assemblyNameWithoutExt));

                            patchRepo.CleanRepo();
                            var patchedFiles = patchRepo.GatherPatchedFiles(assemblyFile);
                            
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
                            WriteCSProjAndSln(assembly, buildData, "repos");
                        });
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

                        assemblyList.ForEach(tuple =>
                        {
                            var (assemblyFile, assembly) = tuple;
                            var assemblyPath = Path.Combine(buildData, assemblyFile);
                            var moduleName = Path.GetFileNameWithoutExtension(assemblyFile);
                            var patchRepo = new PatchRepository(Path.Combine("repos", moduleName),
                                Path.Combine("patches", moduleName));

                            var patchedFiles = patchRepo.GatherPatchedFiles(assemblyFile);
                            BuildWorkingDir(patchedFiles, assembly, assemblyFile, 
                                patchRepo.RepositoryPath, buildData,  o, assemblyPath, ar);
                        });
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
            var config = new NLog.Config.LoggingConfiguration();

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
            config.AddRule(options.Verbose ? NLog.LogLevel.Debug : Info, Fatal, target);

            // Apply config           
            NLog.LogManager.Configuration = config;
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

        private static void WriteCSProjAndSln(AssemblyDefinition assembly, string buildData, string projectFolder)
        {
            foreach (var module in assembly.Modules)
            {
                var moduleName = Path.GetFileNameWithoutExtension(module.FileName) ?? module.FileName;
                Console.WriteLine($"Building {moduleName}.csproj");
                var root = ProjectRootElement.Create();
                root.DefaultTargets = "Build";
                var group = root.AddPropertyGroup();
                group.AddProperty("Configuration", "Debug");
                group.AddProperty("Platform", "x64");
                /*group.AddProperty("DefineConstants",
                    "UNITY_2020_3_12;UNITY_2020_3;UNITY_2020;UNITY_5_3_OR_NEWER;UNITY_5_4_OR_NEWER;UNITY_5_5_OR_NEWER;UNITY_5_6_OR_NEWER;UNITY_2017_1_OR_NEWER;UNITY_2017_2_OR_NEWER;UNITY_2017_3_OR_NEWER;UNITY_2017_4_OR_NEWER;UNITY_2018_1_OR_NEWER;UNITY_2018_2_OR_NEWER;UNITY_2018_3_OR_NEWER;UNITY_2018_4_OR_NEWER;UNITY_2019_1_OR_NEWER;UNITY_2019_2_OR_NEWER;UNITY_2019_3_OR_NEWER;UNITY_2019_4_OR_NEWER;UNITY_2020_1_OR_NEWER;UNITY_2020_2_OR_NEWER;UNITY_2020_3_OR_NEWER;PLATFORM_ARCH_64;UNITY_64;UNITY_INCLUDE_TESTS;USE_SEARCH_ENGINE_API;SCENE_TEMPLATE_MODULE;ENABLE_AR;ENABLE_AUDIO;ENABLE_CACHING;ENABLE_CLOTH;ENABLE_EVENT_QUEUE;ENABLE_MICROPHONE;ENABLE_MULTIPLE_DISPLAYS;ENABLE_PHYSICS;ENABLE_TEXTURE_STREAMING;ENABLE_VIRTUALTEXTURING;ENABLE_UNET;ENABLE_LZMA;ENABLE_UNITYEVENTS;ENABLE_VR;ENABLE_WEBCAM;ENABLE_UNITYWEBREQUEST;ENABLE_WWW;ENABLE_CLOUD_SERVICES;ENABLE_CLOUD_SERVICES_COLLAB;ENABLE_CLOUD_SERVICES_COLLAB_SOFTLOCKS;ENABLE_CLOUD_SERVICES_ADS;ENABLE_CLOUD_SERVICES_USE_WEBREQUEST;ENABLE_CLOUD_SERVICES_CRASH_REPORTING;ENABLE_CLOUD_SERVICES_PURCHASING;ENABLE_CLOUD_SERVICES_ANALYTICS;ENABLE_CLOUD_SERVICES_UNET;ENABLE_CLOUD_SERVICES_BUILD;ENABLE_CLOUD_LICENSE;ENABLE_EDITOR_HUB_LICENSE;ENABLE_WEBSOCKET_CLIENT;ENABLE_DIRECTOR_AUDIO;ENABLE_DIRECTOR_TEXTURE;ENABLE_MANAGED_JOBS;ENABLE_MANAGED_TRANSFORM_JOBS;ENABLE_MANAGED_ANIMATION_JOBS;ENABLE_MANAGED_AUDIO_JOBS;INCLUDE_DYNAMIC_GI;ENABLE_MONO_BDWGC;ENABLE_SCRIPTING_GC_WBARRIERS;PLATFORM_SUPPORTS_MONO;RENDER_SOFTWARE_CURSOR;ENABLE_VIDEO;PLATFORM_STANDALONE;PLATFORM_STANDALONE_WIN;UNITY_STANDALONE_WIN;UNITY_STANDALONE;ENABLE_RUNTIME_GI;ENABLE_MOVIES;ENABLE_NETWORK;ENABLE_CRUNCH_TEXTURE_COMPRESSION;ENABLE_OUT_OF_PROCESS_CRASH_HANDLER;ENABLE_CLUSTER_SYNC;ENABLE_CLUSTERINPUT;PLATFORM_UPDATES_TIME_OUTSIDE_OF_PLAYER_LOOP;GFXDEVICE_WAITFOREVENT_MESSAGEPUMP;ENABLE_WEBSOCKET_HOST;ENABLE_MONO;NET_4_6;ENABLE_PROFILER;DEBUG;TRACE;UNITY_ASSERTIONS;UNITY_EDITOR;UNITY_EDITOR_64;UNITY_EDITOR_WIN;ENABLE_UNITY_COLLECTIONS_CHECKS;ENABLE_BURST_AOT;UNITY_TEAM_LICENSE;ENABLE_CUSTOM_RENDER_TEXTURE;ENABLE_DIRECTOR;ENABLE_LOCALIZATION;ENABLE_SPRITES;ENABLE_TERRAIN;ENABLE_TILEMAP;ENABLE_TIMELINE;ENABLE_LEGACY_INPUT_MANAGER;CSHARP_7_OR_LATER;CSHARP_7_3_OR_NEWER");*/
                group.AddProperty("OutputType", "Library");
                group.AddProperty("OutputPath", "bin\\");
                group.AddProperty("AssemblyName", moduleName);
                group.AddProperty("TargetFrameworkVersion", "v4.7.1"); // ??
                group.AddProperty("LangVersion", "8"); // ??
                
                var compileSet = root.AddItemGroup();
                /*module.Types.Where(type => !type.Name.StartsWith("<") || !type.Name.EndsWith(">"))
                    .Select(x => $"{x.Name}.cs")
                    .ToList().ForEach(x => compileSet.AddItem("Compile", x));*/
                compileSet.AddItem("Compile", "*.cs");

                var referenceItemGroup = root.AddItemGroup();
                foreach (var refPath in Directory.GetFiles(buildData))
                {
                    referenceItemGroup.AddItem("Reference", Path.GetFileNameWithoutExtension(refPath))
                        .AddMetadata("HintPath", Path.GetFullPath(refPath));
                }

                /*var target = root.AddTarget("Build");
                var task = target.AddTask("Csc");
                task.SetParameter("Sources", "@(Compile)");
                task.SetParameter("OutputAssembly", module.Name);*/
                root.AddImport(@"$(MSBuildToolsPath)\Microsoft.CSharp.targets");
                root.Save(Path.Combine(projectFolder, moduleName, $"{moduleName}.csproj"));
            }

            // TODO: Solution building doesn't work properly.
            /*Console.WriteLine("Building Raft.sln");
            SolutionCreator.WriteSolutionFile(Path.GetFullPath(Path.Combine(upstreamPath, "Raft.sln")),
                assembly.Modules.Select(x =>
                {
                    var moduleName = Path.GetFileNameWithoutExtension(x.FileName) ?? x.FileName;
                    var projName = Path.Combine(upstreamPath, moduleName, $"{x.Name}.csproj");
                    return new ProjectItem(Path.GetFullPath(projName),
                        "x64", Guid.NewGuid(),
                        ProjectTypeGuids.CSharpCore);
                }));*/
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
