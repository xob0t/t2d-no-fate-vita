using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The engine's binding calls, inlined. Unity 6 implements an engine property or method as a managed wrapper that
// takes the object's native pointer and calls a static extern X_Injected; IL2CPP inlined the wrapper, so the game
// has, for camera.pixelWidth:
//
//     ldloc camera; callvirt Object::GetCachedPtr(); stloc ptr      the handle
//     ldloc ptr; ldc.i8 0; ceq; brtrue <throw>                      the wrapper's null check
//     ldloc ptr; call Camera::get_pixelWidth_Injected(IntPtr)       the binding call
//     ...
//     <throw>: ldloc camera; call Bindings.ThrowHelper::ThrowNullReferenceException(object)
//
// FoldBindings makes the handle the object again and the binding call the public member:
//
//     ldloc camera; nop; stloc ptr                                  ptr is retyped to Camera
//     ldloc ptr; ldnull; call Object::op_Equality; brtrue <throw>   Unity's own "destroyed is null"
//     ldloc ptr; callvirt Camera::get_pixelWidth()
//
// A handle is the result of Object.GetCachedPtr() or of a load of a binding class's m_Ptr (MaterialPropertyBlock).
// Each must end in an IntPtr local or a comparison with zero, and each load of such a local must be a comparison
// with zero or a binding call's handle argument.
//
// The binding call's target is found from its name and signature (ResolveBinding), and must exist in the target's
// engine: X_Injected(IntPtr self, ...) is the instance member X, or a static X whose first parameter is the
// object; XImpl is also X (Material.SetColorImpl is SetColor's). Its arguments map as the bindings marshal them:
//   - a struct the member takes by value comes by reference (ref Color): ldloca V becomes ldloc V;
//   - a struct the member returns comes back through a last out parameter: ldloca V goes, stloc V follows the call;
//   - an engine object comes and goes as its native pointer: as a handle argument, or as an IntPtr result that
//     Unmarshal.UnmarshalUnityObject<T> turns back into the object, a call that then goes.
// A T2DCompat shim of the X_Injected's name without the suffix (UnityEngine_AudioSource.Play(AudioSource, double))
// wins over the engine's member: it is there for members 2018.2 lacks or keeps private. Static binding calls with no
// handle (Quaternion.AngleAxis_Injected) fold the same way when 2018.2 has a public member for them, else they stay.
partial class DowngradeAssemblies
{
    // Bindings whose public member has another name in 2018.2, by owner::member (the X of X_Injected).
    static readonly Dictionary<string, string> BindingNames = new Dictionary<string, string>
    {
        { "UnityEngine.GL::GLClear", "Clear" },
    };

    enum BindingArg { Same, Deref, Handle, Out }

    class Binding
    {
        public MethodDefinition Target;
        public bool Shim;
        public BindingArg[] Args;
        public bool ReturnsHandle;
        public string Error;
        // A binding with no handle whose member 2018.2 keeps private: left as it is, as the linker resolves it.
        public bool Keep;
    }

    static readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();

    static bool IsInjected(MethodReference method)
    {
        return method.Name.EndsWith("_Injected", StringComparison.Ordinal) && method.DeclaringType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal);
    }

    static bool IsHandleSource(Instruction i)
    {
        var method = i.Operand as MethodReference;
        if (method != null)
            return (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) && method.Name == "GetCachedPtr" &&
                   method.DeclaringType.FullName == "UnityEngine.Object" && method.Parameters.Count == 0;
        var field = i.Operand as FieldReference;
        return field != null && i.OpCode == OpCodes.Ldfld && (field.Name == "m_Ptr" || field.Name == "m_CachedPtr") &&
               IsIntPtr(field.FieldType) && field.DeclaringType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal);
    }

    static Binding ResolveBinding(MethodReference injected)
    {
        Binding binding;
        if (bindings.TryGetValue(injected.FullName, out binding)) return binding;
        binding = new Binding();
        bindings[injected.FullName] = binding;
        string owner = injected.DeclaringType.FullName;
        string member = injected.Name.Substring(0, injected.Name.Length - "_Injected".Length);
        var names = new List<string>();
        string renamed;
        if (BindingNames.TryGetValue(owner + "::" + member, out renamed)) names.Add(renamed);
        names.Add(member);
        if (member.EndsWith("Impl", StringComparison.Ordinal)) names.Add(member.Substring(0, member.Length - 4));
        TypeDefinition ownerDefinition = Lookup(injected.DeclaringType);
        if (ownerDefinition == null)
        {
            binding.Error = "no " + owner + " in the target for " + injected.FullName;
            return binding;
        }
        bool handle = injected.Parameters.Count > 0 && IsIntPtr(injected.Parameters[0].ParameterType);
        // 1. A T2DCompat shim: static, the object first.
        TypeDefinition shims;
        if (compat.TryGetValue(CompatName(owner), out shims))
            foreach (string name in names)
                foreach (MethodDefinition candidate in shims.Methods.Where(m => m.IsStatic && m.IsPublic && m.Name == name))
                    if (FitBinding(candidate, injected, false, binding))
                    {
                        binding.Shim = true;
                        return binding;
                    }
        // 2. The target's public member, then 3. a non-public one.
        foreach (bool wantPublic in new[] { true, false })
            for (TypeDefinition type = ownerDefinition; type != null; type = type.BaseType == null ? null : Lookup(type.BaseType))
                foreach (string name in names)
                    foreach (MethodDefinition candidate in type.Methods.Where(m => m.Name == name && m.IsPublic == wantPublic && !Removed(m)))
                        if ((candidate.HasThis && FitBinding(candidate, injected, true, binding)) ||
                            (!candidate.HasThis && FitBinding(candidate, injected, false, binding)))
                        {
                            if (!wantPublic)
                            {
                                if (handle) binding.Error = "2018.2 keeps " + candidate.FullName + " non-public (write a T2DCompat shim " + CompatName(owner) + "." + member + ")";
                                else binding.Keep = true;
                            }
                            return binding;
                        }
        if (handle) binding.Error = "no member of " + owner + " in the target fits " + injected.FullName;
        else binding.Keep = true;
        return binding;
    }

    // Whether candidate is the member behind the binding call: instance (self is the handle, the first parameter) or
    // static (a shim, or a static engine member), with the arguments mapped as the bindings marshal them.
    static bool FitBinding(MethodDefinition candidate, MethodReference injected, bool instance, Binding binding)
    {
        List<TypeReference> given = injected.Parameters.Select(p => p.ParameterType).ToList();
        List<TypeReference> wanted = candidate.Parameters.Select(p => p.ParameterType).ToList();
        var args = new BindingArg[given.Count];
        int k = 0;
        if (instance)
        {
            if (given.Count == 0 || !IsIntPtr(given[0]) || !IsUnityObjectOrBinding(candidate.DeclaringType)) return false;
            args[0] = BindingArg.Handle;
            k = 1;
        }
        foreach (TypeReference parameter in wanted)
        {
            if (k >= given.Count) return false;
            TypeReference argument = given[k];
            if (Shape(parameter) == Shape(argument)) args[k] = BindingArg.Same;
            else if (argument is ByReferenceType && !(parameter is ByReferenceType) && IsStruct(parameter) &&
                     Shape(parameter) == Shape(((ByReferenceType)argument).ElementType)) args[k] = BindingArg.Deref;
            else if (IsIntPtr(argument) && IsUnityObjectOrBinding(parameter)) args[k] = BindingArg.Handle;
            else return false;
            k++;
        }
        bool returnsHandle = false;
        TypeReference returned = candidate.ReturnType;
        if (returned.MetadataType == MetadataType.Void)
        {
            if (injected.ReturnType.MetadataType != MetadataType.Void) return false;
        }
        else if (Shape(returned) == Shape(injected.ReturnType)) { }
        else if (IsIntPtr(injected.ReturnType) && IsUnityObject(returned)) returnsHandle = true;
        else if (injected.ReturnType.MetadataType == MetadataType.Void && k == given.Count - 1 && given[k] is ByReferenceType &&
                 Shape(((ByReferenceType)given[k]).ElementType) == Shape(returned))
            args[k++] = BindingArg.Out;
        else return false;
        if (k != given.Count) return false;
        binding.Target = candidate;
        binding.Args = args;
        binding.ReturnsHandle = returnsHandle;
        return true;
    }

    // An engine object, or one of the engine's classes that hold a native pointer of their own (MaterialPropertyBlock).
    static bool IsUnityObjectOrBinding(TypeReference type)
    {
        if (IsUnityObject(type)) return true;
        TypeDefinition definition = Lookup(type);
        return definition != null && !definition.IsValueType && definition.Fields.Any(f => f.Name == "m_Ptr" && IsIntPtr(f.FieldType));
    }

    static int FoldBindings(ModuleDefinition module)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                bool any = method.Body.Instructions.Any(i =>
                {
                    var called = i.Operand as MethodReference;
                    return IsHandleSource(i) || (called != null && IsInjected(called));
                });
                if (any) count += FoldBindingsIn(module, method);
            }
        return count;
    }

    // One method, all or nothing: the edits are planned first, and made only when every handle and binding call in
    // the body fits.
    static int FoldBindingsIn(ModuleDefinition module, MethodDefinition method)
    {
        Flow flow;
        try { flow = new Flow(method); }
        catch (InvalidOperationException e)
        {
            FoldProblem(module, method, "binding fold: the stack can't be followed (" + e.Message + ")");
            return 0;
        }
        MethodBody body = method.Body;
        var errors = new List<string>();
        var edits = new List<Action>();
        var rules = new List<string>();
        var sources = new HashSet<Instruction>(body.Instructions.Where(IsHandleSource));
        // The object type each handle local holds, from its sources' receivers.
        var handleLocals = new Dictionary<VariableDefinition, List<TypeReference>>();
        // Zero comparisons of a handle: the comparison, and the zero.
        var nullChecks = new List<KeyValuePair<Instruction, Instruction>>();
        // Handle values (sources and local loads) and the object type they carry, filled as they are found.
        var handleValues = new Dictionary<Instruction, VariableDefinition>();
        var sourceTypes = new Dictionary<Instruction, TypeReference>();
        TypeReference unityObject = null;
        Func<TypeReference> objectType = () => unityObject ?? (unityObject = module.ImportReference(target["UnityEngine.Object"]));

        foreach (Instruction source in sources)
        {
            if (!flow.Reached(source)) { errors.Add("unreachable handle " + source); continue; }
            TypeReference receiver = StaticType(flow, flow.Operand(source, 1, 0));
            if (receiver == null || receiver is TypeSpecification) receiver = source.Operand is FieldReference ? ((FieldReference)source.Operand).DeclaringType : objectType();
            sourceTypes[source] = receiver;
            List<KeyValuePair<Instruction, int>> uses = flow.Uses(source);
            if (uses.Count != 1) { errors.Add("handle " + source + " used " + uses.Count + " times"); continue; }
            Instruction use = uses[0].Key;
            if (IsStloc(use))
            {
                VariableDefinition local = LocalOf(body, use);
                List<TypeReference> types;
                if (!handleLocals.TryGetValue(local, out types)) handleLocals[local] = types = new List<TypeReference>();
                types.Add(receiver);
            }
            else if (!PlanNullCheck(flow, source, uses[0], nullChecks))
            {
                var called = use.Operand as MethodReference;
                if (called == null || !IsInjected(called)) { errors.Add("handle " + source + " goes to " + use); continue; }
            }
            Instruction removed = source;
            edits.Add(() => MakeNop(removed));
        }
        // Copies: a handle local loaded and stored to another IntPtr local makes that one a handle local too, holding
        // the same objects (UnityMain.ScreenStuff keeps the camera's handle in a second local for a native call).
        for (bool grew = true; grew; )
        {
            grew = false;
            foreach (VariableDefinition local in handleLocals.Keys.ToList())
                foreach (Instruction load in body.Instructions.Where(i => IsLdloc(i) && LocalOf(body, i) == local && flow.Reached(i)).ToList())
                    foreach (KeyValuePair<Instruction, int> use in flow.Uses(load))
                    {
                        if (!IsStloc(use.Key)) continue;
                        VariableDefinition copy = LocalOf(body, use.Key);
                        if (copy == local || !IsIntPtr(copy.VariableType)) continue;
                        List<TypeReference> types;
                        if (!handleLocals.TryGetValue(copy, out types))
                        {
                            handleLocals[copy] = types = new List<TypeReference>();
                            grew = true;
                        }
                        foreach (TypeReference held in handleLocals[local].ToList())
                            if (!types.Any(t => t.FullName == held.FullName))
                            {
                                types.Add(held);
                                grew = true;
                            }
                    }
        }
        foreach (KeyValuePair<VariableDefinition, List<TypeReference>> pair in handleLocals)
        {
            VariableDefinition local = pair.Key;
            foreach (Instruction i in body.Instructions)
            {
                if (LocalOf(body, i) != local) continue;
                if (IsLdloca(i)) { errors.Add("address of handle local V" + local.Index + " taken"); break; }
                if (IsStloc(i))
                {
                    Instruction stored = flow.Reached(i) ? flow.Operand(i, 1, 0) : null;
                    bool copied = stored != null && IsLdloc(stored) && handleLocals.ContainsKey(LocalOf(body, stored));
                    if (stored == null || !(sources.Contains(stored) || copied)) errors.Add("handle local V" + local.Index + " stored from " + i);
                    continue;
                }
                if (!flow.Reached(i)) { errors.Add("unreachable load of handle local V" + local.Index); continue; }
                handleValues[i] = local;
                foreach (KeyValuePair<Instruction, int> use in flow.Uses(i))
                {
                    if (PlanNullCheck(flow, i, use, nullChecks)) continue;
                    if (IsStloc(use.Key) && handleLocals.ContainsKey(LocalOf(body, use.Key))) continue;
                    var called = use.Key.Operand as MethodReference;
                    if (called == null || !IsInjected(called)) errors.Add("handle local V" + local.Index + " goes to " + use.Key);
                }
            }
            List<string> names = pair.Value.Select(t => t.FullName).Distinct().ToList();
            TypeReference held = names.Count == 1 ? pair.Value[0] : objectType();
            if (names.Count > 1 && !pair.Value.All(IsUnityObject)) errors.Add("handle local V" + local.Index + " holds " + string.Join(", ", names));
            edits.Add(() => local.VariableType = held);
        }
        Func<Instruction, TypeReference> heldBy = value =>
        {
            VariableDefinition local;
            if (handleValues.TryGetValue(value, out local))
            {
                List<string> names = handleLocals[local].Select(t => t.FullName).Distinct().ToList();
                return names.Count == 1 ? handleLocals[local][0] : objectType();
            }
            TypeReference direct;
            return sourceTypes.TryGetValue(value, out direct) ? direct : null;
        };
        foreach (KeyValuePair<Instruction, Instruction> check in nullChecks)
        {
            Instruction compare = check.Key, zero = check.Value;
            Instruction[] stack = flow.Before[compare];
            Instruction handle = stack[stack.Length - 2] == zero ? stack[stack.Length - 1] : stack[stack.Length - 2];
            TypeReference held = heldBy(handle);
            bool unity = held != null && IsUnityObject(held);
            string op = compare.OpCode.Code == Code.Ceq ? "op_Equality" : "op_Inequality";
            edits.Add(() =>
            {
                zero.OpCode = OpCodes.Ldnull;
                zero.Operand = null;
                // Unity's own comparison: a destroyed object is null, as its native pointer was zero.
                if (unity)
                {
                    compare.OpCode = OpCodes.Call;
                    compare.Operand = TargetMethod(module, "UnityEngine.Object", op, "UnityEngine.Object", "UnityEngine.Object");
                }
            });
        }
        foreach (Instruction call in body.Instructions.Where(i => i.Operand is MethodReference && IsInjected((MethodReference)i.Operand)).ToList())
        {
            var injected = (MethodReference)call.Operand;
            Binding binding = ResolveBinding(injected);
            if (binding.Keep) continue;
            if (binding.Error != null) { errors.Add(binding.Error); continue; }
            if (!flow.Reached(call)) { errors.Add("unreachable " + injected.Name); continue; }
            int n = injected.Parameters.Count;
            MethodDefinition targetMethod = binding.Target;
            for (int a = 0; a < n; a++)
            {
                Instruction producer = flow.Operand(call, n, a);
                if (producer == null) { errors.Add(injected.Name + " argument " + a + " comes from more than one place"); continue; }
                switch (binding.Args[a])
                {
                    case BindingArg.Handle:
                        TypeReference held = heldBy(producer);
                        if (held == null) { errors.Add(injected.Name + " argument " + a + " is not a handle (" + producer + ")"); break; }
                        // What the member wants there: its declaring type for an instance's self, else the parameter's type.
                        int index = targetMethod.HasThis ? a - 1 : a;
                        TypeReference wanted = index < 0 ? targetMethod.DeclaringType : targetMethod.Parameters[index].ParameterType;
                        if (!IsAssignable(held, wanted.FullName))
                        {
                            Instruction after = producer;
                            TypeReference cast = module.ImportReference(Lookup(wanted) ?? wanted.Resolve());
                            edits.Add(() => body.GetILProcessor().InsertAfter(after, Instruction.Create(OpCodes.Castclass, cast)));
                        }
                        break;
                    case BindingArg.Deref:
                        if (!IsAddressOf(producer) || flow.Uses(producer).Count != 1) { errors.Add(injected.Name + " argument " + a + " is not a plain address (" + producer + ")"); break; }
                        Instruction address = producer;
                        edits.Add(() => address.OpCode = AddressToValue[address.OpCode.Code]);
                        break;
                    case BindingArg.Out:
                        if (flow.Uses(producer).Count != 1 || !(IsLdloca(producer) || producer.OpCode == OpCodes.Ldsflda))
                        {
                            errors.Add(injected.Name + " result goes to " + producer + ", not a local or a static field");
                            break;
                        }
                        Instruction result = producer;
                        Instruction store = IsLdloca(producer)
                            ? Instruction.Create(OpCodes.Stloc, LocalOf(body, producer))
                            : Instruction.Create(OpCodes.Stsfld, (FieldReference)producer.Operand);
                        Instruction at = call;
                        edits.Add(() =>
                        {
                            MakeNop(result);
                            body.GetILProcessor().InsertAfter(at, store);
                        });
                        break;
                }
            }
            if (binding.ReturnsHandle)
            {
                Instruction unmarshal = flow.OnlyUse(call);
                var called = unmarshal == null ? null : unmarshal.Operand as GenericInstanceMethod;
                if (called == null || called.Name != "UnmarshalUnityObject" || called.DeclaringType.FullName != "UnityEngine.Bindings.Unmarshal")
                {
                    errors.Add(injected.Name + " result goes to " + unmarshal + ", not Unmarshal.UnmarshalUnityObject");
                    continue;
                }
                TypeReference wantedType = called.GenericArguments[0];
                if (wantedType.FullName == targetMethod.ReturnType.FullName) edits.Add(() => MakeNop(unmarshal));
                else edits.Add(() =>
                {
                    unmarshal.OpCode = OpCodes.Castclass;
                    unmarshal.Operand = wantedType;
                });
            }
            Instruction site = call;
            MethodDefinition member = targetMethod;
            edits.Add(() =>
            {
                site.OpCode = member.HasThis ? OpCodes.Callvirt : OpCodes.Call;
                site.Operand = module.ImportReference(member);
            });
            rules.Add("bindings " + injected.DeclaringType.FullName + "::" + injected.Name + " -> " + (binding.Shim ? "T2DCompat " : "") +
                      member.DeclaringType.Name + "::" + member.Name);
        }
        if (errors.Count > 0)
        {
            foreach (string error in errors.Distinct()) FoldProblem(module, method, "binding fold: " + error);
            return 0;
        }
        foreach (Action edit in edits) edit();
        foreach (string rule in rules) Folded(rule);
        if (sources.Count > 0) Folded("bindings handles (GetCachedPtr, m_Ptr) -> the object", sources.Count);
        return rules.Count + sources.Count;
    }

    // A comparison of a handle with zero (ceq, or cgt.un for "not zero"), whose zero is a constant used only there.
    static bool PlanNullCheck(Flow flow, Instruction handle, KeyValuePair<Instruction, int> use, List<KeyValuePair<Instruction, Instruction>> checks)
    {
        Instruction compare = use.Key;
        if (compare.OpCode.Code != Code.Ceq && compare.OpCode.Code != Code.Cgt_Un) return false;
        // cgt.un is "handle > 0", with the handle first.
        if (compare.OpCode.Code == Code.Cgt_Un && use.Value != 0) return false;
        Instruction zero = flow.Operand(compare, 2, 1 - use.Value);
        if (zero == null || flow.Uses(zero).Count != 1) return false;
        bool isZero = (zero.OpCode == OpCodes.Ldc_I8 && (long)zero.Operand == 0) || zero.OpCode == OpCodes.Ldc_I4_0;
        if (!isZero) return false;
        if (!checks.Any(c => c.Key == compare)) checks.Add(new KeyValuePair<Instruction, Instruction>(compare, zero));
        return true;
    }
}
