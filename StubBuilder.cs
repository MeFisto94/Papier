using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace Papier
{
    public class StubBuilder
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public string StubPath { get; }
        public AssemblyDefinition Assembly { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="stubPath">Needs to exist or will be implicitly created by Clean().</param>
        /// <param name="assembly"></param>
        public StubBuilder(string stubPath, AssemblyDefinition assembly)
        {
            StubPath = stubPath;
            Assembly = assembly;
        }

        public void Clean()
        {
            if (Directory.Exists(StubPath))
            {
                Logger.Info("Cleaning stub folder");
                Program.DeleteDirectory(StubPath);
            }
            Directory.CreateDirectory(StubPath);
        }

        public Dictionary<TypeDefinition, HashSet<IMemberDefinition>> CreateStubs(IReadOnlyCollection<string> sourceSet)
        {
            Logger.Info("Creating Stubs");
            var stubTypes = new Dictionary<TypeDefinition, HashSet<IMemberDefinition>>();
            var foundNewStubs = true;

            Logger.Debug("Looking for classes that need stubbing because they are called from the sourceset...");
            while (foundNewStubs)
            {
                foundNewStubs = StubDetection.FindStubsThatAreCalledFromTheSourceSet(Assembly, sourceSet, ref stubTypes);
                if (foundNewStubs)
                {
                    Logger.Debug("================ Discovered new stubs, repeating scan");
                }
            }

            foreach (var (key, value) in stubTypes)
            {
                Logger.Info($"Creating a stub for {key.Name} with {value.Count} Elements");
                var file = File.Open(Path.Combine(StubPath, key.Name + ".cs"), FileMode.Create,
                    FileAccess.Write, FileShare.Read);
                StubGenerator.CreateStringStub(file, key, value).ConfigureAwait(false).GetAwaiter().GetResult();
            }

            using (var sw = new StreamWriter(File.Open(Path.Combine(StubPath, "PapierStub.cs"), FileMode.Create,
                FileAccess.Write, FileShare.Read)))
            {
                StubGenerator.WriteStubAttribute(sw);
            }

            return stubTypes;
        }
    }
}