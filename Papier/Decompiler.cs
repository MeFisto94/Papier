using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Mono.Cecil;
using NLog;

namespace Papier
{
    public class Decompiler
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private CSharpDecompiler _decompiler;

        // TODO: Maybe build the CSharpDecompiler manually based on Language Version etc
        public Decompiler(CSharpDecompiler decompiler)
        {
            _decompiler = decompiler;
        }

        public async Task DecompileClass(TypeDefinition type, string outputSourceDirectory, bool skipExisting = true)
        {
            // Skip special names
            if (type.Name.StartsWith("<") && type.Name.EndsWith(">"))
            {
                Logger.Warn($"Skipping {type.Name}");
                return;
            }

            if (type.Name.Contains("<") || type.Name.Contains(">"))
            {
                Logger.Warn($"Skipping {type.Name}, because it's not a valid filename (TODO: string replacements)");
                return;
            }

            var typeFullFileName = $"{type.FullName}.cs";

            if (type.Name.Contains("`"))
            {
                typeFullFileName = typeFullFileName.Replace('`', '_');
            }
            
            // TODO: Remove´1 suffix from generics, but that probably fails at later lookups though.
            if (skipExisting && File.Exists(Path.Combine(outputSourceDirectory, typeFullFileName)))
            {
                //Console.WriteLine($"UP-TO-DATE {type.Name}.cs");
                return;
            }
            
            Logger.Info($"Decompiling {typeFullFileName}");
            await File.WriteAllTextAsync(Path.Combine(outputSourceDirectory, typeFullFileName), 
                _decompiler.DecompileTypeAsString(new FullTypeName(type.FullName)));
        }

        /// <summary>
        /// Decompiles every type of the supplied enumerable, filtered by filter, if non-null.
        /// Note: If decompilation fails, the file is skipped.
        /// </summary>
        /// <param name="types"></param>
        /// <param name="outputSourceDirectory"></param>
        /// <param name="failedDecompilations"></param>
        /// <param name="filter"></param>
        public void DecompileTypes(IEnumerable<TypeDefinition> types, string outputSourceDirectory, out List<TypeDefinition> failedDecompilations, List<string>? filter = null)
        {
            var failed = new List<TypeDefinition>();
            var tasks = types
                .Where(x => filter?.Contains(x.Name) ?? true)
                .Select(async x =>
                {
                    try
                    {
                        await DecompileClass(x, outputSourceDirectory);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(x);
                        Logger.Error(ex, "Failed to decompile file {}", x.Name);
                    }
                })
                .ToArray();
            
            Task.WaitAll(tasks);
            failedDecompilations = failed;
        }
    }
}