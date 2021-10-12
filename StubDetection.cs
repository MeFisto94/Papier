using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NLog;

namespace Papier
{
    public static class StubDetection
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger(); 
        public static bool FindStubsThatAreCalledFromTheSourceSet(AssemblyDefinition assembly,
            IEnumerable<string> sourceSet, ref Dictionary<TypeDefinition, HashSet<IMemberDefinition>> stubbedTypes)
        {
            var hadStubs = false;
            var sourceTypes = sourceSet.Select(source =>
            {
                var ret = assembly.MainModule.GetType(source);
                if (ret == null)
                {
                    Logger.Error($"Could not resolve Type {source}!");
                    Environment.Exit(-1);
                }

                return ret;
            }).ToList();
            foreach (var type in sourceTypes)
            {
                var methodsToStub = new HashSet<MethodDefinition>();
                // TODO: Henn-Egg problem. We only need to stub classes that are referenced _by_ the source set
                // so limiting it seems like a good idea, but in theory, stubs could also expose new requirements
                // to be stubbed. This is however very unlikely, and also currently not detectable by FindTypesToStub
                // (that only checks method bodies, but there are none in stubs).
                
                FindTypesToStub(type, ref methodsToStub, stubbedTypes.Keys);

                if (methodsToStub.Count == 0)
                {
                    continue;
                }
                
                foreach (var md in methodsToStub)
                {
                    Console.WriteLine($"STUB {md}, called from {type.Name}");
                    if (!stubbedTypes.ContainsKey(md.DeclaringType))
                    {
                        stubbedTypes[md.DeclaringType] = new HashSet<IMemberDefinition>();
                        // We didn't have md.DT as stub yet, signal that to the caller, because that will require a
                        // complete re-scan for new stubs on the whole sourceSet
                        hadStubs = true;
                    }

                    var stubSet = stubbedTypes[md.DeclaringType];
                    foreach (var t in sourceTypes)
                    {
                        FindStubContents(md.DeclaringType, t, ref stubSet);
                    }
                    
                    // yield return methods only return an instance of a new compiler-generated inner class
                    // Thus (and potentially other reasons), we're basically adding all innter classes
                    // of type as well.
                    // TODO: Shouldn't that also apply to FindTypesToStub?
                    if (type.HasNestedTypes)
                    {
                        foreach (var nt in type.NestedTypes)
                        {
                            FindStubContents(md.DeclaringType, nt, ref stubSet);
                        }
                    }

                    stubbedTypes[md.DeclaringType] = stubSet;
                }
            }

            return hadStubs;
        }

        /// <summary>
        /// This method searches for all references from type to stubType, in order to find all members that need to
        /// be stubbed.
        /// </summary>
        /// <param name="stubType"></param>
        /// <param name="type"></param>
        /// <param name="content"></param>
        public static void FindStubContents(TypeDefinition stubType, TypeDefinition type, ref HashSet<IMemberDefinition> content)
        {
            if (type.HasMethods)
            {
                foreach (var m in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Calli || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj)
                        {
                            var mr = (MethodReference)ins.Operand;
                            if (stubType.Equals(mr.DeclaringType.Resolve()))
                            {
                                var meth = mr.Resolve();
                                
                                if (stubType.HasProperties && meth.IsSpecialName && !meth.IsConstructor)
                                {
                                    if (meth.Name.StartsWith("get_"))
                                    {
                                        content.Add(stubType.Properties.First(x => meth.Equals(x.GetMethod)));
                                    } else if (meth.Name.StartsWith("set_"))
                                    {
                                        content.Add(stubType.Properties.First(x => meth.Equals(x.SetMethod)));
                                    }
                                    else
                                    {
                                        Console.Error.WriteLine($"Unknown special method {meth.Name}, that is no property");
                                    }
                                }
                                else
                                {
                                    content.Add(meth);
                                }
                            }
                        } else if (ins.OpCode == OpCodes.Ldfld || ins.OpCode == OpCodes.Stfld || ins.OpCode == OpCodes.Ldsfld || ins.OpCode == OpCodes.Stsfld)
                        {
                            var fd = (FieldReference)ins.Operand;
                            if (stubType.Equals(fd.DeclaringType.Resolve()))
                            {
                                content.Add(fd.Resolve());
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This method searches a given type to find where the type/references are leaked (e.g. passing this to a foreign
        /// class). These references/classes need to be stubbed for the compiler, because the types would otherwise not
        /// match anymore after "deleting" the type in the assembly.<br />
        /// Since this is a complex subject, this method is probably far from perfect and needs a few iterations once
        /// problems arise.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="methods"></param>
        /// <param name="stubbedTypes"></param>
        public static void FindTypesToStub(TypeDefinition type, ref HashSet<MethodDefinition> methods, 
            IEnumerable<TypeDefinition> stubbedTypes)
        {
            if (!type.HasMethods)
            {
                return;
            }
            
            foreach (var m in type.Methods.Where(x => x.HasBody))
            {
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Calli || ins.OpCode == OpCodes.Callvirt || 
                        ins.OpCode == OpCodes.Newobj)
                    {
                        var mr = (MethodReference)ins.Operand;
                        // TODO: If DT is any of the sourceSet + stubbedTypes, we don't need to report this method
                        // because that means we already have that class in source code form, non-assembly, so we
                        // can't stub it.
                        // TODO: If we skip based on that however, stubs would disappear (a call causes a stub, then it's removed, then it's added again)
                        // TODO: Shouldn't that filtering happen at a later place, so that methods are the true methods?
                        if (!mr.HasParameters || mr.DeclaringType.Equals(type) || mr.DeclaringType.IsNested && mr.DeclaringType.DeclaringType.Equals(type))
                        {
                            continue;
                        }
                            
                        if (mr.Parameters.Select(p => p.ParameterType.Resolve())
                            .Where(p => p != null)
                            .Any(pt => type.Equals(pt) || stubbedTypes.Contains(pt)))
                        {
                            // Found a method call that contains a related type (type or any stub) as param.
                            methods.Add(mr.Resolve());
                        }
                    }
                }
            }
        }
    }
}