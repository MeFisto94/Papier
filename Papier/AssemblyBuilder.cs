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
        private readonly IEnumerable<string> sourceSet;
        public string OutputPath { get; }
        private string ModuleName { get; } 

        public AssemblyBuilder(IEnumerable<string> sourceSet, string outputPath, string moduleName)
        {
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
    }
}