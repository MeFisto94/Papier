using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler.Solution;

namespace Papier
{
    public class SolutionBuilder
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private bool _built = false;
        public string SolutionFolder;
        public string SolutionName;
        public List<string> Projects;
        
        public void Build()
        {
            if (_built)
            {
                throw new InvalidOperationException("Has already been built");
            }
            
            Logger.Info($"Building {SolutionName}.sln with {Projects.Count} project(s)");
            var pItems = new List<ProjectItem>();
            foreach (var x in Projects)
            {
                var guid = Guid.NewGuid();
                Logger.Info($"Adding project {x} with random GUID {guid}");
                pItems.Add(new ProjectItem(Path.GetFullPath(Path.Combine(SolutionFolder, x, $"{x}.csproj")),
                    "x64", guid, ProjectTypeGuids.CSharpCore));
            }

            SolutionCreator.WriteSolutionFile(Path.GetFullPath(Path.Combine(SolutionFolder, $"{SolutionName}.sln")), pItems);
        }
    }
}