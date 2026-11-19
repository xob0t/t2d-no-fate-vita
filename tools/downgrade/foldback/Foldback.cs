using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The fold-back passes: what IL2CPP inlined into the game's methods from Unity 6's engine and class libraries,
// and Rewind recovered as it was, goes back to the API Unity 2018.2 has (foldback/README.md has the table of
// every item and the rule that handles it).
//
//   Foldback.cs      the entry (FoldBack), the stack flow of a body, the IL helpers the folds share
//   Bindings.cs      the engine's binding calls: GetCachedPtr/m_Ptr handles, *_Injected calls, Unmarshal
//   ClassLibrary.cs  the class libraries' internals: String._firstChar, missing fields, missing local types,
//                    Number.Format* with a ReadOnlySpan format
//
// The rules are shapes, not methods: a fold matches what an inlined engine or library member looks like,
// wherever it is. A shape the rules don't know fails loudly (an UNRESOLVED "foldback:" line), and the method it is
// in is left as it was, all of it, so a half-folded body never reaches the build.
//
// T2D_FOLDBACK=0 skips them. FoldBack runs in Rewrite() after the gap repairs and the platform layer, before the type retargeting: it reads
// the Unity 6 names the game's references still have, and looks members up in the target's (2018.2's) assemblies
// by full name. It doesn't touch what the atlas passes match (BitmapData's texpack reads).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // Sites per fold rule (the pass's name and the member it handled), for the stats line and the README's table.
    static readonly Dictionary<string, int> folds = new Dictionary<string, int>();

    static void Folded(string rule, int sites = 1)
    {
        int sum;
        folds.TryGetValue(rule, out sum);
        folds[rule] = sum + sites;
    }

    static string FoldBack(ModuleDefinition module)
    {
        if (!IsGameAssembly(module)) return "";
        // T2D_FOLDBACK=0 skips the passes, to see what the game's code references as Rewind recovered it.
        if (Environment.GetEnvironmentVariable("T2D_FOLDBACK") == "0") return " foldback=off";
        folds.Clear();
        var references = new HashSet<AssemblyNameReference>(module.AssemblyReferences);
        int bindings = FoldBindings(module);
        int library = FoldClassLibrary(module);
        int rescoped = RescopeImports(module, references);
        Count("pass FoldBindings", bindings);
        Count("pass FoldClassLibrary", library);
        // One line per rule in the log, for the README's table.
        foreach (KeyValuePair<string, int> fold in folds.OrderBy(f => f.Key, StringComparer.Ordinal))
            Console.WriteLine("FOLDED " + fold.Value + " " + fold.Key);
        return " foldBindings=" + bindings + " foldClassLibrary=" + library + " foldRescoped=" + rescoped;
    }

    // What the passes import from the target's assemblies names their references: the PS Vita player's engine modules
    // and T2DCompat reference mscorlib 2.0.0.0, the game's assemblies 4.0.0.0. A reference added that way, whose name the
    // module already references, gives way to the module's own, and goes: one mscorlib, as before the passes. The
    // retargeting then sets the scopes as for every other reference. Returns the type references rescoped.
    static int RescopeImports(ModuleDefinition module, HashSet<AssemblyNameReference> before)
    {
        var replacements = new Dictionary<AssemblyNameReference, AssemblyNameReference>();
        foreach (AssemblyNameReference added in module.AssemblyReferences.Where(r => !before.Contains(r)))
        {
            AssemblyNameReference own = before.FirstOrDefault(r => r.Name == added.Name);
            if (own != null) replacements[added] = own;
        }
        if (replacements.Count == 0) return 0;
        int count = 0;
        var seen = new HashSet<TypeReference>();
        Action<TypeReference> rescope = null;
        rescope = type =>
        {
            if (type == null || !seen.Add(type)) return;
            var specification = type as TypeSpecification;
            if (specification != null)
            {
                rescope(specification.ElementType);
                var instance = type as GenericInstanceType;
                if (instance != null) foreach (TypeReference argument in instance.GenericArguments) rescope(argument);
                return;
            }
            if (type.IsGenericParameter) return;
            if (type.DeclaringType != null) { rescope(type.DeclaringType); return; }
            var scope = type.Scope as AssemblyNameReference;
            AssemblyNameReference own;
            if (scope != null && replacements.TryGetValue(scope, out own))
            {
                type.Scope = own;
                count++;
            }
        };
        Action<MethodReference> rescopeMethod = method =>
        {
            rescope(method.DeclaringType);
            rescope(method.ReturnType);
            foreach (ParameterDefinition parameter in method.Parameters) rescope(parameter.ParameterType);
            var instance = method as GenericInstanceMethod;
            if (instance != null)
            {
                rescope(instance.ElementMethod.DeclaringType);
                foreach (TypeReference argument in instance.GenericArguments) rescope(argument);
            }
        };
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods)
            {
                rescopeMethod(method);
                if (!method.HasBody) continue;
                foreach (VariableDefinition local in method.Body.Variables) rescope(local.VariableType);
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called != null) { rescopeMethod(called); continue; }
                    var field = instruction.Operand as FieldReference;
                    if (field != null) { rescope(field.DeclaringType); rescope(field.FieldType); continue; }
                    rescope(instruction.Operand as TypeReference);
                }
            }
        foreach (AssemblyNameReference added in replacements.Keys) module.AssemblyReferences.Remove(added);
        return count;
    }

    // A fold that can't be made: the method stays as it was, and the build learns why.
    static void FoldProblem(ModuleDefinition module, MethodDefinition method, string what)
    {
        problems.Add(module.Name + ": foldback: " + what + " in " + method.FullName);
    }

    // The stack flow of one body, before any edit: the values on the stack before each reachable instruction,
    // as the instructions that pushed them (Stack.cs), and who consumes each value.
    class Flow
    {
        public readonly MethodDefinition Method;
        public readonly Dictionary<Instruction, Instruction[]> Before;
        public readonly Dictionary<Instruction, List<KeyValuePair<Instruction, int>>> Consumers =
            new Dictionary<Instruction, List<KeyValuePair<Instruction, int>>>();

        public Flow(MethodDefinition method)
        {
            Method = method;
            Before = StackProducers(method, Consumers);
        }

        public bool Reached(Instruction at)
        {
            return Before.ContainsKey(at);
        }

        // The producer of operand i (0 is the first pushed) of an instruction that pops count values;
        // null where paths disagree.
        public Instruction Operand(Instruction at, int count, int i)
        {
            Instruction[] stack = Before[at];
            return stack[stack.Length - count + i];
        }

        public List<KeyValuePair<Instruction, int>> Uses(Instruction producer)
        {
            List<KeyValuePair<Instruction, int>> list;
            return Consumers.TryGetValue(producer, out list) ? list : new List<KeyValuePair<Instruction, int>>();
        }

        // The one consumer of a value, or null.
        public Instruction OnlyUse(Instruction producer)
        {
            List<KeyValuePair<Instruction, int>> uses = Uses(producer);
            return uses.Count == 1 ? uses[0].Key : null;
        }
    }

    static int Pops(MethodDefinition method, Instruction at, int depth)
    {
        int pops, pushes;
        StackEffect(method, at, depth, out pops, out pushes);
        return pops;
    }

    // ---- locals and arguments ----

    static VariableDefinition LocalOf(MethodBody body, Instruction i)
    {
        switch (i.OpCode.Code)
        {
            case Code.Ldloc_0: case Code.Stloc_0: return body.Variables[0];
            case Code.Ldloc_1: case Code.Stloc_1: return body.Variables[1];
            case Code.Ldloc_2: case Code.Stloc_2: return body.Variables[2];
            case Code.Ldloc_3: case Code.Stloc_3: return body.Variables[3];
            case Code.Ldloc: case Code.Ldloc_S: case Code.Stloc: case Code.Stloc_S: case Code.Ldloca: case Code.Ldloca_S:
                return (VariableDefinition)i.Operand;
            default: return null;
        }
    }

    static bool IsLdloc(Instruction i)
    {
        Code c = i.OpCode.Code;
        return c == Code.Ldloc || c == Code.Ldloc_S || (c >= Code.Ldloc_0 && c <= Code.Ldloc_3);
    }

    static bool IsStloc(Instruction i)
    {
        Code c = i.OpCode.Code;
        return c == Code.Stloc || c == Code.Stloc_S || (c >= Code.Stloc_0 && c <= Code.Stloc_3);
    }

    static bool IsLdloca(Instruction i)
    {
        return i.OpCode.Code == Code.Ldloca || i.OpCode.Code == Code.Ldloca_S;
    }

    // An instruction that pushes the address of a local, an argument or a field: the value's own load.
    static readonly Dictionary<Code, OpCode> AddressToValue = new Dictionary<Code, OpCode>
    {
        { Code.Ldloca, OpCodes.Ldloc }, { Code.Ldloca_S, OpCodes.Ldloc_S },
        { Code.Ldarga, OpCodes.Ldarg }, { Code.Ldarga_S, OpCodes.Ldarg_S },
        { Code.Ldflda, OpCodes.Ldfld }, { Code.Ldsflda, OpCodes.Ldsfld },
    };

    static bool IsAddressOf(Instruction i)
    {
        return AddressToValue.ContainsKey(i.OpCode.Code);
    }

    static ParameterDefinition ArgumentOf(MethodDefinition method, Instruction i)
    {
        int index;
        switch (i.OpCode.Code)
        {
            case Code.Ldarg_0: index = 0; break;
            case Code.Ldarg_1: index = 1; break;
            case Code.Ldarg_2: index = 2; break;
            case Code.Ldarg_3: index = 3; break;
            case Code.Ldarg: case Code.Ldarg_S: case Code.Ldarga: case Code.Ldarga_S: return (ParameterDefinition)i.Operand;
            default: return null;
        }
        if (method.HasThis) index--;
        return index < 0 ? null : method.Parameters[index];
    }

    // The static type of the value an instruction pushes, where the instruction says it; null otherwise.
    static TypeReference StaticType(Flow flow, Instruction i)
    {
        if (i == null) return null;
        if (IsLdloc(i)) return LocalOf(flow.Method.Body, i).VariableType;
        switch (i.OpCode.Code)
        {
            case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3: case Code.Ldarg: case Code.Ldarg_S:
                ParameterDefinition parameter = ArgumentOf(flow.Method, i);
                return parameter != null ? parameter.ParameterType : flow.Method.DeclaringType;
            case Code.Ldfld: case Code.Ldsfld: return ((FieldReference)i.Operand).FieldType;
            case Code.Ldelem_Any: case Code.Castclass: case Code.Isinst: return (TypeReference)i.Operand;
            case Code.Newobj: return ((MethodReference)i.Operand).DeclaringType;
            case Code.Call: case Code.Callvirt:
                TypeReference returned = ((MethodReference)i.Operand).ReturnType;
                return returned.IsGenericParameter ? null : returned;
            case Code.Dup:
                Instruction[] stack;
                return flow.Before.TryGetValue(i, out stack) ? StaticType(flow, stack[stack.Length - 1]) : null;
            default: return null;
        }
    }

    // ---- types against the target ----

    // The base type of a type of the game's or of the target's, or null.
    static TypeReference BaseOf(TypeReference type)
    {
        var definition = type as TypeDefinition;
        if (definition == null) definition = Lookup(type);
        return definition == null ? null : definition.BaseType;
    }

    static bool IsAssignable(TypeReference from, string to)
    {
        for (TypeReference current = from; current != null; current = BaseOf(current))
            if (current.FullName == to) return true;
        return false;
    }

    static bool IsUnityObject(TypeReference type)
    {
        return !(type is TypeSpecification) && IsAssignable(type, "UnityEngine.Object");
    }

    static bool IsIntPtr(TypeReference type)
    {
        return type.FullName == "System.IntPtr";
    }

    // A struct the engine passes to its bindings by reference: a value type that is not a primitive or an enum.
    static bool IsStruct(TypeReference type)
    {
        TypeDefinition definition = Lookup(type);
        return definition != null && definition.IsValueType && !definition.IsEnum && !definition.IsPrimitive;
    }

    static MethodReference TargetMethod(ModuleDefinition module, string type, string name, params string[] parameters)
    {
        MethodDefinition method = target[type].Methods.Single(m => m.Name == name &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters));
        return module.ImportReference(method);
    }

    // A method of a generic type, as a reference on one instantiation of it (ReadOnlySpan<char>.ToArray).
    static MethodReference OnInstance(MethodReference method, GenericInstanceType declaringType)
    {
        var reference = new MethodReference(method.Name, method.ReturnType, declaringType)
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention,
        };
        foreach (ParameterDefinition parameter in method.Parameters) reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        return reference;
    }

    static void MakeNop(Instruction i)
    {
        i.OpCode = OpCodes.Nop;
        i.Operand = null;
    }
}
