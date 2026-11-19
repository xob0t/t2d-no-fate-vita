using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The class libraries' internals, inlined. Unity 6's mscorlib is .NET's (CoreCLR's class libraries), 2018.2's is
// Mono's; IL2CPP inlined small members of the first into the game, with their private fields and helpers.
// In the order they run:
//
// 1. Two strings compared by the address of their first character (string.Equals' "same string" test, inlined):
//        ldloc a; ldflda String::_firstChar; ldloc b; ldflda String::_firstChar; ceq
//    is the references compared, exactly: the two ldflda go.
// 2. A one-character string made in place (char.ToString, inlined):
//        ldc.i4.1; call String::FastAllocateString(int); stloc s; ldloc s; <char>; stfld String::_firstChar
//    is new string(<char>, 1), stored to s after the character is computed.
// 3. A field 2018.2 doesn't have, by the naming convention of Compat2018.cs extended to fields: a load becomes a
//    call to T2DCompat.<Owner>.get_<Field>, a store set_<Field>, with the instance first (by ref for a struct whose
//    address is on the stack); static fields without it. A field with no such shim may have the same meaning under
//    another name in 2018.2 (FieldRenames, checked against the target: same declaring type and field type).
// 4. A local whose type is a class library type 2018.2 lacks (StringComparer.Ordinal's OrdinalCaseSensitiveComparer)
//    gets the nearest base type 2018.2 has, from the source class library.
// 5. Number.Format<Int32|UInt32|Int64|UInt64>(value, ReadOnlySpan<char> format, IFormatProvider): T2DCompat can't
//    take a ReadOnlySpan (C# 5 rejects ref structs), so the pass writes the helper itself, into a class __Foldback of
//    the game's assembly: value.ToString(provider), or value.ToString(new string(format.ToArray()), provider).
//
// Missing members of types 2018.2 has (Random.GenerateSeed, CompareInfo.IndexOfOrdinal) go to T2DCompat shims by the
// existing convention (RedirectMembers), and missing internal types (ThrowHelper, SpanHelpers) to T2DCompat stand-ins
// of the same name (Unity6Shims.cs); neither needs a pass.
partial class DowngradeAssemblies
{
    // Fields 2018.2's class libraries have under another name, with the same meaning: Unity 6's name -> 2018.2's.
    static readonly Dictionary<string, string> FieldRenames = new Dictionary<string, string>
    {
        // The string's first character, where the game takes its address (character reads by pointer).
        { "System.String::_firstChar", "m_firstChar" },
    };

    const string HelperClass = "__Foldback";

    static int FoldClassLibrary(ModuleDefinition module)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types).ToList())
            foreach (MethodDefinition method in type.Methods.ToList())
            {
                if (!method.HasBody) continue;
                if (method.Body.Instructions.Any(i => IsFirstChar(i.Operand)))
                    count += FoldFirstChar(module, method);
                count += FoldMissingFields(module, method);
                count += RetypeMissingLocals(module, method);
                count += FoldNumberFormat(module, method);
            }
        return count;
    }

    static bool IsFirstChar(object operand)
    {
        var field = operand as FieldReference;
        return field != null && field.Name == "_firstChar" && field.DeclaringType.FullName == "System.String";
    }

    // 1 and 2.
    static int FoldFirstChar(ModuleDefinition module, MethodDefinition method)
    {
        Flow flow;
        try { flow = new Flow(method); }
        catch (InvalidOperationException e)
        {
            FoldProblem(module, method, "_firstChar: the stack can't be followed (" + e.Message + ")");
            return 0;
        }
        MethodBody body = method.Body;
        int count = 0;
        foreach (Instruction compare in body.Instructions.Where(i => i.OpCode.Code == Code.Ceq || i.OpCode.Code == Code.Beq ||
                                                                    i.OpCode.Code == Code.Bne_Un).ToList())
        {
            if (!flow.Reached(compare)) continue;
            Instruction left = flow.Operand(compare, 2, 0), right = flow.Operand(compare, 2, 1);
            if (left == null || right == null || left.OpCode != OpCodes.Ldflda || right.OpCode != OpCodes.Ldflda) continue;
            if (!IsFirstChar(left.Operand) || !IsFirstChar(right.Operand)) continue;
            if (flow.Uses(left).Count != 1 || flow.Uses(right).Count != 1) continue;
            MakeNop(left);
            MakeNop(right);
            Folded("strings compared by &_firstChar -> references compared");
            count++;
        }
        foreach (Instruction store in body.Instructions.Where(i => i.OpCode == OpCodes.Stfld && IsFirstChar(i.Operand)).ToList())
            if (FoldOneCharString(module, flow, store))
            {
                Folded("FastAllocateString(1) and _firstChar store -> new string(char, 1)");
                count++;
            }
        return count;
    }

    static bool FoldOneCharString(ModuleDefinition module, Flow flow, Instruction store)
    {
        MethodBody body = flow.Method.Body;
        if (!flow.Reached(store)) return false;
        Instruction receiver = flow.Operand(store, 2, 0);
        if (receiver == null || !IsLdloc(receiver)) return false;
        VariableDefinition local = LocalOf(body, receiver);
        List<Instruction> stores = body.Instructions.Where(i => IsStloc(i) && LocalOf(body, i) == local).ToList();
        if (stores.Count != 1 || stores[0].Next != receiver || body.Instructions.Any(i => IsLdloca(i) && LocalOf(body, i) == local)) return false;
        Instruction allocate = flow.Reached(stores[0]) ? flow.Operand(stores[0], 1, 0) : null;
        var called = allocate == null ? null : allocate.Operand as MethodReference;
        if (called == null || called.Name != "FastAllocateString" || called.DeclaringType.FullName != "System.String") return false;
        Instruction length = flow.Operand(allocate, 1, 0);
        if (length == null || length.OpCode != OpCodes.Ldc_I4_1 || flow.Uses(length).Count != 1) return false;
        // Straight-line from the load to the store, with no other read of the string between, and nothing branching in.
        var span = new HashSet<Instruction>();
        for (Instruction i = receiver.Next; i != store; i = i.Next)
        {
            if (i == null || IsLdloc(i) && LocalOf(body, i) == local) return false;
            span.Add(i);
        }
        span.Add(store);
        foreach (Instruction i in body.Instructions)
        {
            if (i.Operand is Instruction && span.Contains((Instruction)i.Operand)) return false;
            var targets = i.Operand as Instruction[];
            if (targets != null && targets.Any(span.Contains)) return false;
        }
        if (body.ExceptionHandlers.Any(h => span.Contains(h.TryStart) || span.Contains(h.HandlerStart) || span.Contains(h.TryEnd) || span.Contains(h.HandlerEnd))) return false;
        MakeNop(length);
        MakeNop(allocate);
        Instruction stored = stores[0];
        MakeNop(stored);
        MakeNop(receiver);
        store.OpCode = OpCodes.Ldc_I4_1;
        store.Operand = null;
        ILProcessor il = body.GetILProcessor();
        Instruction create = Instruction.Create(OpCodes.Newobj, TargetMethod(module, "System.String", ".ctor", "System.Char", "System.Int32"));
        il.InsertAfter(store, create);
        il.InsertAfter(create, Instruction.Create(OpCodes.Stloc, local));
        return true;
    }

    // 3.
    static int FoldMissingFields(ModuleDefinition module, MethodDefinition method)
    {
        MethodBody body = method.Body;
        List<Instruction> sites = body.Instructions.Where(i => i.Operand is FieldReference && IsMissing((FieldReference)i.Operand)).ToList();
        if (sites.Count == 0) return 0;
        Flow flow = null;
        int count = 0;
        foreach (Instruction site in sites)
        {
            var field = (FieldReference)site.Operand;
            TypeReference owner = field.DeclaringType;
            bool isStatic = site.OpCode == OpCodes.Ldsfld || site.OpCode == OpCodes.Stsfld || site.OpCode == OpCodes.Ldsflda;
            bool load = site.OpCode == OpCodes.Ldfld || site.OpCode == OpCodes.Ldsfld;
            bool storeTo = site.OpCode == OpCodes.Stfld || site.OpCode == OpCodes.Stsfld;
            MethodDefinition shim = null;
            if ((load || storeTo) && !(owner is TypeSpecification))
            {
                // A struct's instance comes by ref when the stack has its address (always, for a store).
                bool byRef = false;
                TypeDefinition ownerDefinition = Lookup(owner);
                if (!isStatic && ownerDefinition != null && ownerDefinition.IsValueType)
                {
                    if (storeTo) byRef = true;
                    else
                    {
                        if (flow == null)
                        {
                            try { flow = new Flow(method); }
                            catch (InvalidOperationException e)
                            {
                                FoldProblem(module, method, "field " + field.FullName + ": the stack can't be followed (" + e.Message + ")");
                                return count;
                            }
                        }
                        Instruction receiver = flow.Reached(site) ? flow.Operand(site, 1, 0) : null;
                        if (receiver == null)
                        {
                            FoldProblem(module, method, "field " + field.FullName + ": its instance comes from more than one place");
                            continue;
                        }
                        byRef = IsAddressOf(receiver);
                    }
                }
                shim = FieldShim(field, load, isStatic, byRef);
            }
            if (shim != null)
            {
                site.OpCode = OpCodes.Call;
                site.Operand = module.ImportReference(shim);
                Folded("field " + field.FullName + " -> T2DCompat " + shim.DeclaringType.Name + "." + shim.Name);
                count++;
                continue;
            }
            string renamed;
            if (FieldRenames.TryGetValue(owner.FullName + "::" + field.Name, out renamed))
            {
                TypeDefinition ownerDefinition = Lookup(owner);
                FieldDefinition existing = ownerDefinition == null ? null : ownerDefinition.Fields.FirstOrDefault(f => f.Name == renamed);
                if (existing == null || existing.FieldType.FullName != field.FieldType.FullName || existing.IsStatic != isStatic)
                {
                    FoldProblem(module, method, "field rename " + field.FullName + " -> " + renamed + ": 2018.2 has no such field of that type");
                    continue;
                }
                site.Operand = new FieldReference(renamed, field.FieldType, owner);
                Folded("field " + field.FullName + " -> 2018.2's " + renamed);
                count++;
                continue;
            }
            // Left for RedirectMembers to report, as before.
        }
        return count;
    }

    static MethodDefinition FieldShim(FieldReference field, bool load, bool isStatic, bool byRef)
    {
        TypeDefinition shims;
        if (!compat.TryGetValue(CompatName(field.DeclaringType.FullName), out shims)) return null;
        string name = (load ? "get_" : "set_") + field.Name;
        string owner = Shape(field.DeclaringType) + (byRef ? "&" : "");
        foreach (MethodDefinition candidate in shims.Methods.Where(m => m.IsStatic && m.IsPublic && m.Name == name))
        {
            var expected = new List<string>();
            if (!isStatic) expected.Add(owner);
            if (!load) expected.Add(Shape(field.FieldType));
            if (!candidate.Parameters.Select(p => Shape(p.ParameterType)).SequenceEqual(expected)) continue;
            if (Shape(candidate.ReturnType) != (load ? Shape(field.FieldType) : "System.Void")) continue;
            return candidate;
        }
        return null;
    }

    // 4.
    static int RetypeMissingLocals(ModuleDefinition module, MethodDefinition method)
    {
        int count = 0;
        foreach (VariableDefinition local in method.Body.Variables)
        {
            TypeReference type = local.VariableType;
            if (type is TypeSpecification || type.IsGenericParameter || type.DeclaringType != null) continue;
            if (!type.Namespace.StartsWith("System", StringComparison.Ordinal) || !IsExternal(type)) continue;
            if (target.ContainsKey(type.FullName) || compat.ContainsKey(CompatName(type.FullName))) continue;
            TypeDefinition definition;
            try { definition = type.Resolve(); }
            catch (AssemblyResolutionException) { definition = null; }
            if (definition == null || definition.IsValueType) continue;
            TypeReference current = definition.BaseType;
            while (current != null && !target.ContainsKey(current.FullName))
            {
                TypeDefinition next;
                try { next = current.Resolve(); }
                catch (AssemblyResolutionException) { next = null; }
                current = next == null ? null : next.BaseType;
            }
            if (current == null) continue;
            local.VariableType = module.ImportReference(target[current.FullName]);
            Folded("local of " + type.FullName + " -> " + current.FullName);
            count++;
        }
        return count;
    }

    // 5.
    static readonly string[] FormattedIntegers = { "System.Int32", "System.UInt32", "System.Int64", "System.UInt64" };

    static int FoldNumberFormat(ModuleDefinition module, MethodDefinition method)
    {
        int count = 0;
        foreach (Instruction site in method.Body.Instructions)
        {
            var called = site.Operand as MethodReference;
            if (called == null || site.OpCode != OpCodes.Call || called.DeclaringType.FullName != "System.Number" ||
                !called.Name.StartsWith("Format", StringComparison.Ordinal) || called.Parameters.Count != 3) continue;
            string value = called.Parameters[0].ParameterType.FullName;
            if (!FormattedIntegers.Contains(value) || called.Parameters[1].ParameterType.FullName != "System.ReadOnlySpan`1<System.Char>" ||
                called.Parameters[2].ParameterType.FullName != "System.IFormatProvider" || called.ReturnType.FullName != "System.String")
            {
                FoldProblem(module, method, "no fold for " + called.FullName);
                continue;
            }
            site.Operand = FormatHelper(module, called);
            Folded("System.Number::" + called.Name + "(" + value + ", ReadOnlySpan<char>, IFormatProvider) -> __Foldback helper");
            count++;
        }
        return count;
    }

    static MethodDefinition FormatHelper(ModuleDefinition module, MethodReference called)
    {
        TypeDefinition helpers = module.GetType(HelperClass);
        if (helpers == null)
        {
            helpers = new TypeDefinition("", HelperClass, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                                                              TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
            module.Types.Add(helpers);
        }
        string name = called.Name;
        MethodDefinition helper = helpers.Methods.FirstOrDefault(m => m.Name == name);
        if (helper != null) return helper;
        helper = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.String);
        var value = new ParameterDefinition("value", ParameterAttributes.None, called.Parameters[0].ParameterType);
        var format = new ParameterDefinition("format", ParameterAttributes.None, called.Parameters[1].ParameterType);
        var provider = new ParameterDefinition("provider", ParameterAttributes.None, called.Parameters[2].ParameterType);
        helper.Parameters.Add(value);
        helper.Parameters.Add(format);
        helper.Parameters.Add(provider);
        TypeDefinition span = target["System.ReadOnlySpan`1"];
        var chars = new GenericInstanceType(module.ImportReference(span));
        chars.GenericArguments.Add(module.ImportReference(target["System.Char"]));
        MethodReference isEmpty = OnInstance(module.ImportReference(span.Methods.Single(m => m.Name == "get_IsEmpty")), chars);
        MethodReference toArray = OnInstance(module.ImportReference(span.Methods.Single(m => m.Name == "ToArray")), chars);
        string integer = called.Parameters[0].ParameterType.FullName;
        MethodReference plain = TargetMethod(module, integer, "ToString", "System.IFormatProvider");
        MethodReference formatted = TargetMethod(module, integer, "ToString", "System.String", "System.IFormatProvider");
        MethodReference fromChars = TargetMethod(module, "System.String", ".ctor", "System.Char[]");
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction withFormat = il.Create(OpCodes.Ldarga, value);
        il.Append(il.Create(OpCodes.Ldarga, format));
        il.Append(il.Create(OpCodes.Call, isEmpty));
        il.Append(il.Create(OpCodes.Brfalse, withFormat));
        il.Append(il.Create(OpCodes.Ldarga, value));
        il.Append(il.Create(OpCodes.Ldarg, provider));
        il.Append(il.Create(OpCodes.Call, plain));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(withFormat);
        il.Append(il.Create(OpCodes.Ldarga, format));
        il.Append(il.Create(OpCodes.Call, toArray));
        il.Append(il.Create(OpCodes.Newobj, fromChars));
        il.Append(il.Create(OpCodes.Ldarg, provider));
        il.Append(il.Create(OpCodes.Call, formatted));
        il.Append(il.Create(OpCodes.Ret));
        helpers.Methods.Add(helper);
        return helper;
    }
}
