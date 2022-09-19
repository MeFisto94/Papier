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
            var sourceTypes = sourceSet.Select(source => ResolveType(assembly, source)).ToList();
            
            foreach (var type in sourceTypes)
            {
                var methodsToStub = new HashSet<MethodDefinition>();
                // TODO: Henn-Egg problem. We only need to stub classes that are referenced _by_ the source set
                // so limiting it seems like a good idea, but in theory, stubs could also expose new requirements
                // to be stubbed. This is however very unlikely, and also currently not detectable by FindTypesToStub
                // (that only checks method bodies, but there are none in stubs).
                
                FindTypesToStub(type, ref methodsToStub, stubbedTypes.Keys, out var typesThatNeedStubbing);

                var typesToCheck = new HashSet<TypeDefinition>(typesThatNeedStubbing);
                foreach (var md in methodsToStub)
                {
                    Logger.Trace($"STUB {md}, called from {type.Name}");
                    typesToCheck.Add(md.DeclaringType);
                }

                if (typesToCheck.Count == 0)
                {
                    continue;
                }
                
                InnerFindStubs(stubbedTypes, typesToCheck, sourceTypes, type, ref hadStubs);
            }

            return hadStubs;
        }

        private static void InnerFindStubs(IDictionary<TypeDefinition, HashSet<IMemberDefinition>> stubbedTypes, 
            HashSet<TypeDefinition> typesToCheck, List<TypeDefinition> sourceTypes, TypeDefinition sourceType, 
            ref bool hadStubs)
        {
            // sourceType(s) are those causing stubbing, those that are patched by Papier.
            // typesToCheck are the additions to the stubbedTypes. 
            foreach (var type in typesToCheck)
            {
                if (!stubbedTypes.ContainsKey(type))
                {
                    stubbedTypes[type] = new HashSet<IMemberDefinition>();
                    // We didn't have this type as stub yet, signal that to the caller, because that will require a
                    // complete re-scan for new stubs on the whole sourceSet
                    hadStubs = true;
                }

                var stubSet = stubbedTypes[type];
                foreach (var t in sourceTypes)
                {
                    FindStubContents(type, t, ref stubSet);
                }

                // yield return methods only return an instance of a new compiler-generated inner class
                // Thus (and potentially other reasons), we're basically adding all inner classes
                // of type as well.
                // TODO: Shouldn't that also apply to FindTypesToStub?
                if (sourceType.HasNestedTypes)
                {
                    foreach (var nt in sourceType.NestedTypes)
                    {
                        FindStubContents(type, nt, ref stubSet);
                    }
                }

                stubbedTypes[type] = stubSet;
            }
        }

        private static TypeDefinition ResolveType(AssemblyDefinition assembly, string source)
        {
            var ret = assembly.MainModule.GetType(source);
            if (ret == null)
            {
                Logger.Error($"Could not resolve Type {source}!");
                Environment.Exit(-1);
            }

            return ret;
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
        /// <param name="type">The type whose methods to search</param>
        /// <param name="methods">the set of methods that need stubbing (in and out)</param>
        /// <param name="stubbedTypes">(in) the types that are already stubbed.</param>
        /// <param name="typesThatNeedStubbing">Types that shall be additionally be stubbed, but don't need method stubbing</param>
        public static void FindTypesToStub(TypeDefinition type, ref HashSet<MethodDefinition> methods, 
            IEnumerable<TypeDefinition> stubbedTypes, out IEnumerable<TypeDefinition> typesThatNeedStubbing)
        {
            var stubbingList = new List<TypeDefinition>();
            typesThatNeedStubbing = stubbingList;
            
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
                            if (ins.OpCode == OpCodes.Callvirt)
                            {
                                // callvirt is a big problem for us. consider the following code:
                                // class Car {} new Car().ToString();
                                // This will translate to callvirt Object.ToString, with `this` somewhere on the stack.
                                // We, however, need to Stub both Car, but also Object, or at least make the stubbed
                                // methods virtual, so that the compiler emits a callvirt instruction again and doesn't
                                // change to call.
                                ResolveCallVirt(m, ins, mr, out var foundMethod, out var foundType);
                                if (foundMethod != null)
                                {
                                    methods.Add(foundMethod);
                                }
                                else if (foundType != null)
                                {
                                    stubbingList.Add(foundType);
                                } // else: in theory this should not happen, but we fail to find variables atm. 
                            }
                            
                            methods.Add(mr.Resolve());
                        }
                    }
                }
            }
        }

        private static void ResolveCallVirt(MethodDefinition method, Instruction instruction, MethodReference reference, out MethodDefinition? foundMethod, out TypeDefinition? foundType)
        {   
            foundMethod = null;
            foundType = null;
            var @this = instruction;
            for (var i = 0; i < reference.Parameters.Count + 1; i++)
            {
                // TODO: This is even more complex, consider: foo(bar(), 3);
                @this = @this.Previous;
            }

            var local = TryFindVariable(method, @this);
            if (local == null)
            {
                // Failed to find. If we we're halfway complete, this could be an exception, instead we try to
                // ignore it, until generated stubs don't compile anymore.
                return;
            }
            
            if (local.VariableType != reference.DeclaringType)
            {
                Logger.Info($"{reference.FullName} actually called on {local.VariableType} instead!");
                foundType = local.VariableType.Resolve();
                var meth = foundType.Methods.FirstOrDefault(m => m.Name == reference.Name && m.MethodReturnType == reference.MethodReturnType);
                
                if (meth != null) {
                    foundMethod = meth.Resolve(); // we override the method
                }  // else: we don't override the method, but need the class as stub.
            }
        }

        private static VariableDefinition? TryFindVariable(MethodDefinition method, Instruction @this)
        {
            if (@this.OpCode == OpCodes.Ldarg_0)
            {
                // We call a method on ourselves, so we should be safe
                return null; // TODO?
            }
            else if (@this.OpCode == OpCodes.Ldloc_S || @this.OpCode == OpCodes.Ldloc)
            {
                // local variable is being used for the call
                return (VariableDefinition)@this.Operand;
            }
            else if (@this.OpCode == OpCodes.Ldloc_0 || @this.OpCode == OpCodes.Ldloc_1 ||
                     @this.OpCode == OpCodes.Ldloc_2 || @this.OpCode == OpCodes.Ldloc_3)
            {
                // sadly, switch expressions don't work here because LdLoc_x aren't const.
                var idx = -1;
                if (@this.OpCode == OpCodes.Ldloc_0)
                {
                    idx = 0;
                }
                else if (@this.OpCode == OpCodes.Ldloc_1)
                {
                    idx = 1;
                }
                else if (@this.OpCode == OpCodes.Ldloc_2)
                {
                    idx = 2;
                }
                else if (@this.OpCode == OpCodes.Ldloc_3)
                {
                    idx = 3;
                }

                return method.Body.Variables[idx];
            }
            else
            {
                Logger.Warn($"Unknown Instruction {@this}. Ignoring, for now, we're mostly after local variables anyway.");
                // throw new NotImplementedException($"Unsupported IL {@this}");
            }

            return null;
        }
    }
}