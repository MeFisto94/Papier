using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ILRepacking;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;

namespace Papier
{
    public class AssemblyBuilder
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly ProgramOptions o;
        private readonly DefaultAssemblyResolver ar;
        private readonly IEnumerable<string> sourceSet;
        public string OutputPath { get; }
        public string ModuleName { get; } 

        public AssemblyBuilder(ProgramOptions o, DefaultAssemblyResolver ar, IEnumerable<string> sourceSet, 
            string outputPath, string moduleName)
        {
            this.o = o;
            this.ar = ar;
            this.sourceSet = sourceSet;
            OutputPath = outputPath;
            ModuleName = moduleName;

            Directory.CreateDirectory(outputPath);
        }

        public void Clean()
        {
            Logger.Info("Cleaning Assemblies from previous compilations");
            File.Delete(Path.Combine(OutputPath, $"{ModuleName}-diff.dll"));
            File.Delete(Path.Combine(OutputPath, $"{ModuleName}-compiled.dll"));
            File.Delete(Path.Combine(OutputPath, $"{ModuleName}-base.dll"));
            File.Delete(Path.Combine(OutputPath, $"{ModuleName}-stripped.dll"));
        }
        
        public bool EmitDll(CSharpCompilation comp)
        {
            Logger.Info($"Compiling the assembly {ModuleName}-diff.dll");
            using (var fs = File.Open(Path.Combine(OutputPath, $"{ModuleName}-diff.dll"), FileMode.Create,
                FileAccess.Write, FileShare.Read))
            {
                // TODO: PDB seems lost when merging, but would probably help
                // File.Open(Path.Combine(upstreamPath, $"{ModuleName}-diff.pdb"), FileMode.Create, FileAccess.Write)
                var res = comp.Emit(fs);

                // Some assembly version mismatch, caused by mono and stuff.
                foreach (var diag in res.Diagnostics.Where(diag => diag.Descriptor.Id != "CS1701"))
                {
                    if (diag.Descriptor.DefaultSeverity == DiagnosticSeverity.Error)
                    {
                        Logger.Error(diag.ToString);
                    }
                    else
                    {
                        Logger.Warn(diag.ToString);
                    }
                }

                return res.Success;
            }
        }
        
        public void StripAssembly(IEnumerable<IMemberDefinition> stubTypes, 
            AssemblyDefinition assembly)
        {
            Logger.Info("Stripping stubbed types from the assembly");
            foreach (var source in sourceSet.Union(stubTypes.Select(x => x.Name)))
            {
                var type = assembly.MainModule.GetType(source);
                // Rename the type so that it's "invisible" to the compiler.
                type.Name += "_hidden";
                // Remove internal specifications on other types, allowing type to work properly.
                AntiInternalize(type);
            }

            assembly.Write(Path.Combine(OutputPath, $"{assembly.Name.Name}-stripped.dll"), new WriterParameters());
        }
        
        /// <summary>
        /// Change every types visibility from internal to public(?), that is required/referenced by this type.
        /// </summary>
        /// <param name="type"></param>
        public static void AntiInternalize(TypeDefinition type)
        {
            if (type.HasInterfaces)
            {
                foreach (var ii in type.Interfaces)
                {
                    var it = ii.InterfaceType.Resolve();
                    // In this case, internal -> protected internal may be more precise, but functionally equal.
                    if (it.IsNotPublic)
                    {
                        it.IsPublic = true;
                    }
                }
            }

            if (type.BaseType != null)
            {
                var bt = type.BaseType.Resolve();
                if (bt.IsNotPublic)
                {
                    bt.IsPublic = true;
                }
            }
        }

        public void MergeAssemblies(string assemblyPath)
        {
            Logger.Info($"Merging the compilation with the original assembly into {ModuleName}-compiled.dll");
            
            // TODO: Fix. otherwise the stubs override the original. ILRepack probably needs a fix,
            // because FRA causes the types to be excluded from both dlls, which is not what we want. 
            if (o.ForceReplaceAssembly)
            {
                ForceReplaceAssembly(assemblyPath);
            }
            else
            {
                File.Copy(assemblyPath, Path.Combine(OutputPath, $"{ModuleName}-base.dll"), true);
            }

            var rpo = new RepackOptions();
            rpo.Log = true;
            rpo.DebugInfo = o.Verbose;
            rpo.TargetKind = ILRepack.Kind.SameAsPrimaryAssembly;
            rpo.Parallel = true;
            // Order is strange here: the target assembly is what takes precedence, so basically
            // the other assemblies only represent things that _can_ be added, but won't override.
            rpo.InputAssemblies = new[]
            {
                Path.Combine(OutputPath, $"{ModuleName}-diff.dll"),
                Path.Combine(OutputPath, $"{ModuleName}-base.dll")
            };
            rpo.OutputFile = Path.Combine(OutputPath, $"{ModuleName}-compiled.dll");
            rpo.SearchDirectories = ar.GetSearchDirectories();
            rpo.LogVerbose = false;
            rpo.RepackDropAttribute = "PapierStubAttribute";
            rpo.CopyAttributes = true;
            rpo.LineIndexation = o.EnableLineIndexing;

            foreach (var s in sourceSet)
            {
                rpo.AllowedDuplicateTypes.Add(s, s);
            }

            if (o.ForceReplaceAssembly)
            {
                rpo.AllowedDuplicateTypes.Add(rpo.RepackDropAttribute, rpo.RepackDropAttribute);
            }

            new ILRepack(rpo).Repack();
        }
        
        private void ForceReplaceAssembly(string assemblyPath)
        {
            var originalAssembly = AssemblyDefinition.ReadAssembly(assemblyPath,
                new ReaderParameters { AssemblyResolver = ar });

            var papierStub =
                new TypeDefinition("", "PapierStubAttribute",
                    TypeAttributes.AutoClass | TypeAttributes.AnsiClass |
                    TypeAttributes.Public | TypeAttributes.BeforeFieldInit);

            // TODO: Somehow that doesn't work properly??
            papierStub.BaseType = ar.Resolve(AssemblyNameReference.Parse("mscorlib")).MainModule
                .GetType("System.Attribute");

            const MethodAttributes attrs =
                MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            var cPapierStub = new MethodDefinition(".ctor", attrs,
                originalAssembly.MainModule.TypeSystem.Void);
            papierStub.Methods.Add(cPapierStub);

            foreach (var type in sourceSet.Select(s => originalAssembly.MainModule.GetType(s)))
            {
                type.CustomAttributes.Add(new CustomAttribute(cPapierStub));
            }

            originalAssembly.MainModule.Types.Add(papierStub);
            originalAssembly.Write(Path.Combine(OutputPath, $"{ModuleName}-base.dll"));
        }
    }
}