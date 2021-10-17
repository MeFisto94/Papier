using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Papier;

namespace PapierILRepackRunner
{
    class Program
    {
        static void Main(string[] args)
        {
            var assemblyResolver = new DefaultAssemblyResolver();
            var sourceSet = new List<string>();
            string outputPath = Environment.CurrentDirectory;

            //assemblyResolver.AddSearchDirectory(buildData);

            foreach (var s in args)
            {
                // TODO: We need to populate sourceSet, otherwise no type can be replaced....
                Console.WriteLine($"Merging {s} into {outputPath}");
                var merger = new ILRepackAssemblyMerger(assemblyResolver, sourceSet, outputPath, s,
                    true, true);
                // This path is the source path, where the outputpath indeed is the output path...
                merger.MergeAssemblies(Path.Combine(outputPath, $"{s}.dll"));
            }
        }
    }
}