using System.Collections.Generic;
using System.IO;
using System.Linq;
using ILRepacking;
using Mono.Cecil;

namespace Papier
{
    public class ILRepackAssemblyMerger
    {
        private readonly DefaultAssemblyResolver _assemblyResolver;
        private readonly IEnumerable<string> _sourceSet;
        private readonly string _outputPath;
        private readonly string _moduleName;
        private readonly bool _forceReplaceAssembly;
        private readonly bool _verbose;

        public ILRepackAssemblyMerger(DefaultAssemblyResolver assemblyResolver, IEnumerable<string> sourceSet, string outputPath, string moduleName, bool forceReplaceAssembly, bool verbose)
        {
            _assemblyResolver = assemblyResolver;
            _sourceSet = sourceSet;
            _outputPath = outputPath;
            _moduleName = moduleName;
            _forceReplaceAssembly = forceReplaceAssembly;
            _verbose = verbose;
        }

        public void MergeAssemblies(string assemblyPath)
        {
            // TODO: Fix. otherwise the stubs override the original. ILRepack probably needs a fix,
            // because FRA causes the types to be excluded from both dlls, which is not what we want. 
            if (_forceReplaceAssembly)
            {
                DoForceReplaceAssembly(assemblyPath);
            }
            else
            {
                File.Copy(assemblyPath, Path.Combine(_outputPath, $"{_moduleName}-base.dll"), true);
            }

            var rpo = new RepackOptions
            {
                Log = true,
                DebugInfo = _verbose,
                TargetKind = ILRepack.Kind.SameAsPrimaryAssembly,
                Parallel = true,
                // the other assemblies only represent things that _can_ be added, but won't override.
                // Order is strange here: the target assembly is what takes precedence, so basically
                InputAssemblies = new[]
                {
                    Path.Combine(_outputPath, $"{_moduleName}-diff.dll"),
                    Path.Combine(_outputPath, $"{_moduleName}-base.dll")
                },
                OutputFile = Path.Combine(_outputPath, $"{_moduleName}-compiled.dll"),
                SearchDirectories = _assemblyResolver.GetSearchDirectories(),
                LogVerbose = false,
                RepackDropAttribute = "PapierStubAttribute",
                CopyAttributes = true
            };

            foreach (var s in _sourceSet)
            {
                rpo.AllowedDuplicateTypes.Add(s, s);
            }

            if (_forceReplaceAssembly)
            {
                rpo.AllowedDuplicateTypes.Add(rpo.RepackDropAttribute, rpo.RepackDropAttribute);
            }

            new ILRepack(rpo).Repack();
        }
        
        private void DoForceReplaceAssembly(string assemblyPath)
        {
            var originalAssembly = AssemblyDefinition.ReadAssembly(assemblyPath,
                new ReaderParameters { AssemblyResolver = _assemblyResolver });

            var papierStub =
                new TypeDefinition("", "PapierStubAttribute",
                    TypeAttributes.AutoClass | TypeAttributes.AnsiClass |
                    TypeAttributes.Public | TypeAttributes.BeforeFieldInit)
                {
                    // TODO: Somehow that doesn't work properly??
                    BaseType = _assemblyResolver.Resolve(AssemblyNameReference.Parse("mscorlib")).MainModule
                        .GetType("System.Attribute")
                };

            const MethodAttributes attrs = MethodAttributes.Public | MethodAttributes.HideBySig | 
                                           MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            var cPapierStub = new MethodDefinition(".ctor", attrs, originalAssembly.MainModule.TypeSystem.Void);
            papierStub.Methods.Add(cPapierStub);

            foreach (var type in _sourceSet.Select(s => originalAssembly.MainModule.GetType(s)))
            {
                type.CustomAttributes.Add(new CustomAttribute(cPapierStub));
            }

            originalAssembly.MainModule.Types.Add(papierStub);
            originalAssembly.Write(Path.Combine(_outputPath, $"{_moduleName}-base.dll"));
        }
    }
}