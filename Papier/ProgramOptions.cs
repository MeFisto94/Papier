using CommandLine;

namespace Papier
{
    public class ProgramOptions
    {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }
        
        [Value(0, MetaName = "method", Required = true, HelpText = "The method to execute")]
        public string Method { get; set; }
        
        [Option("ignore-unresolved-assemblies", Required = false, Default = false, HelpText = "Ignore Assemblies that could not be found during the decompilation process.")]
        public bool IgnoreUnresolvedAssemblies { get; set; }
        
        [Option("ignore-stubs", Required = false, Default = false, HelpText = "Do not create stubs for the not " +
                                                                              "imported assembly classes, instead use the" +
                                                                              " full decompilation (may contain errors," +
                                                                              " but is the only available option, if stub" +
                                                                              " generation fails")]
        public bool IgnoreStubs { get; set; }
        
        [Option("generate-all-stubs", Required = false, Default = false, HelpText = "Force generate stubs for " +
            "every type in the assembly. Without this option, the dependency graph is derived and only the required " +
            "classes are stubbed.")]
        public bool GenerateAllStubs { get; set; }
        
        [Option("force-replace-assembly", Default = true, HelpText = "Whether to force replacing the whole type " +
                                                                      "assembly instead of attempting a clever merge")]
        public bool ForceReplaceAssembly { get; set; }
        
        [Option("disable-color", Default = false, HelpText = "Disable colors in the log output.")]
        public bool DisableColor { get; set; }
        
        [Option("enable-line-indexer", Default = false, HelpText = "Enable ILRepacks IKVM based line indexer." +
                                                                   " Currently not supported by the CI")]
        public bool EnableLineIndexing { get; set; }
        
        [Value(1, MetaName = "Assembly Wildcard", MetaValue = "*")]
        public string AssemblyWildcard { get; set; }
    }
}