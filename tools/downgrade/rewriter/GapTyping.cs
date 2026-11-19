using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Values of the wrong kind inside Rewind's gaps, made well-typed so IL2CPP's C++ compiles.
//
// A method with a Rewind marker throws when it reaches one (RewindGaps.cs), but its IL must still become
// valid C++. Around an unrecovered part Rewind sometimes leaves a value of the wrong kind: a number (the
// bits of an inlined box, a stack slot read as a long) where an object is taken, an object where a more
// derived one is stored, or a field's address shifted as a number (a write barrier's card index). Mono
// runs such IL; psp2snc rejects the C++ IL2CPP makes of it.
//
// In methods that still have a marker, and only there, this pass gives each such value a type:
//   - a number or an address where something else is taken: the value is dropped and a marker of the right
//     type stands for it, __Rewind.Opaque<T>("type: ..."), which throws like Rewind's own when reached;
//   - an object where a class derived from its type is taken: castclass to that class (the check the native
//     code made and Rewind took out).
// A value with more than one consumer, or whose type isn't known, is left and reported (GAP TYPING SKIPPED).
// It changes nothing in methods without a marker, so a repair that completes a method takes it out of
// this pass's reach. The parallel repair patterns (repairs/Patterns.cs) run first, on Rewind's own IL.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    enum ValueKind { Unknown, Number, Address, Reference, Null }

    static int TypeGapValues(ModuleDefinition module)
    {
        TypeDefinition markers = module.Types.FirstOrDefault(t => t.Name == RewindMarkers && t.Namespace == "");
        MethodDefinition opaque = markers == null ? null : markers.Methods.FirstOrDefault(m =>
            m.Name == "Opaque" && m.HasGenericParameters && m.Parameters.Count == 1);
        if (opaque == null) return 0;
        int sites = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            if (type == markers) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                if (!method.Body.Instructions.Any(i => i.Operand is MethodReference && ((MethodReference)i.Operand).DeclaringType.Name == RewindMarkers &&
                                                       ((MethodReference)i.Operand).Name != "Note"))
                    continue;
                sites += TypeGapValues(module, method, opaque);
            }
        }
        return sites;
    }

    static int TypeGapValues(ModuleDefinition module, MethodDefinition method, MethodDefinition opaque)
    {
        var consumers = new Dictionary<Instruction, List<KeyValuePair<Instruction, int>>>();
        Dictionary<Instruction, Instruction[]> before;
        try
        {
            before = StackProducers(method, consumers);
        }
        catch (Exception e)
        {
            Console.WriteLine("GAP TYPING SKIPPED " + method.FullName + ": " + e.Message);
            return 0;
        }
        var fixes = new List<Tuple<Instruction, TypeReference, string>>();
        foreach (KeyValuePair<Instruction, List<KeyValuePair<Instruction, int>>> entry in consumers)
        {
            Instruction producer = entry.Key;
            ValueKind kind;
            TypeReference produced = ProducedType(method, producer, out kind);
            foreach (KeyValuePair<Instruction, int> use in entry.Value)
            {
                Instruction at = use.Key;
                Instruction[] stack;
                if (!before.TryGetValue(at, out stack)) continue;
                bool wantsNumber;
                TypeReference wanted = ExpectedType(method, at, use.Value, stack.Length, out wantsNumber);
                string why = null;
                TypeReference fix = null;
                if (wanted != null && IsReferenceType(wanted) && (kind == ValueKind.Number || kind == ValueKind.Address))
                {
                    why = (kind == ValueKind.Number ? "a number" : "an address") + " where a " + wanted.Name + " is taken";
                    fix = wanted;
                }
                else if (wantsNumber && kind == ValueKind.Address)
                {
                    why = "an address used as a number";
                    fix = module.TypeSystem.Int64;
                }
                else if (wanted != null && kind == ValueKind.Reference && produced != null && IsReferenceType(wanted) &&
                         wanted.FullName != "System.Object" && NeedsDowncast(produced, wanted))
                {
                    why = "a " + produced.Name + " stored where a " + wanted.Name + " is";
                    fix = wanted;
                }
                if (why == null) continue;
                if (entry.Value.Count != 1)
                {
                    Console.WriteLine("GAP TYPING SKIPPED " + method.FullName + ": " + why + " (value used " + entry.Value.Count + " times) at IL_" + producer.Offset.ToString("x4"));
                    continue;
                }
                fixes.Add(Tuple.Create(producer, fix, why));
            }
        }
        if (fixes.Count == 0) return 0;
        ILProcessor il = method.Body.GetILProcessor();
        foreach (Tuple<Instruction, TypeReference, string> f in fixes)
        {
            Instruction producer = f.Item1;
            TypeReference type = module.ImportReference(f.Item2);
            bool downcast = f.Item3.StartsWith("a ", StringComparison.Ordinal) && f.Item3.Contains(" stored where ");
            if (downcast)
            {
                il.InsertAfter(producer, il.Create(OpCodes.Castclass, type));
            }
            else
            {
                var marker = new GenericInstanceMethod(module.ImportReference(opaque));
                marker.GenericArguments.Add(type);
                Instruction pop = il.Create(OpCodes.Pop);
                Instruction text = il.Create(OpCodes.Ldstr, "type: " + f.Item3);
                Instruction call = il.Create(OpCodes.Call, marker);
                il.InsertAfter(producer, pop);
                il.InsertAfter(pop, text);
                il.InsertAfter(text, call);
            }
            Console.WriteLine("GAP TYPED " + method.FullName + ": " + f.Item3);
        }
        return fixes.Count;
    }

    static bool IsNumber(TypeReference type)
    {
        switch (type.MetadataType)
        {
            case MetadataType.Boolean: case MetadataType.Char: case MetadataType.SByte: case MetadataType.Byte:
            case MetadataType.Int16: case MetadataType.UInt16: case MetadataType.Int32: case MetadataType.UInt32:
            case MetadataType.Int64: case MetadataType.UInt64: case MetadataType.IntPtr: case MetadataType.UIntPtr:
                return true;
        }
        return false;
    }

    static bool IsReferenceType(TypeReference type)
    {
        if (type is GenericParameter || type is ByReferenceType || type is PointerType || type.MetadataType == MetadataType.Void) return false;
        if (type is ArrayType || type.MetadataType == MetadataType.String || type.MetadataType == MetadataType.Object) return true;
        if (IsNumber(type) || type.MetadataType == MetadataType.Single || type.MetadataType == MetadataType.Double) return false;
        TypeDefinition definition;
        try { definition = type.Resolve(); }
        catch { return false; }
        return definition != null && !definition.IsValueType;
    }

    // Whether a value of type from can't be stored where to is taken without a cast (from's classes don't
    // include to). Unknown (unresolved) is not a reason to cast.
    static bool NeedsDowncast(TypeReference from, TypeReference to)
    {
        if (from.FullName == to.FullName || from is GenericParameter || to is GenericParameter || to is ArrayType || from is ArrayType) return false;
        TypeDefinition target, current;
        try
        {
            target = to.Resolve();
            current = from.Resolve();
        }
        catch { return false; }
        if (target == null || current == null || target.IsInterface) return false;
        for (int depth = 0; current != null && depth < 64; depth++)
        {
            if (current.FullName == target.FullName) return false;
            if (current.BaseType == null) break;
            try { current = current.BaseType.Resolve(); }
            catch { return false; }
        }
        // to must derive from from, or the cast is no fix either.
        for (TypeDefinition t = target; t != null && t.BaseType != null;)
        {
            if (t.BaseType.FullName == from.FullName) return true;
            try { t = t.BaseType.Resolve(); }
            catch { return false; }
        }
        return false;
    }

    static TypeReference Substitute(TypeReference type, MethodReference called)
    {
        var parameter = type as GenericParameter;
        if (parameter == null) return type;
        var genericMethod = called as GenericInstanceMethod;
        if (parameter.Type == GenericParameterType.Method && genericMethod != null && parameter.Position < genericMethod.GenericArguments.Count)
            return genericMethod.GenericArguments[parameter.Position];
        var genericType = called == null ? null : called.DeclaringType as GenericInstanceType;
        if (parameter.Type == GenericParameterType.Type && genericType != null && parameter.Position < genericType.GenericArguments.Count)
            return genericType.GenericArguments[parameter.Position];
        return null;
    }

    static TypeReference ProducedType(MethodDefinition method, Instruction at, out ValueKind kind)
    {
        kind = ValueKind.Unknown;
        TypeReference type = null;
        switch (at.OpCode.Code)
        {
            case Code.Ldnull: kind = ValueKind.Null; return null;
            case Code.Ldc_I4: case Code.Ldc_I4_S: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3:
            case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
            case Code.Ldc_I8:
            case Code.Add: case Code.Sub: case Code.Mul: case Code.And: case Code.Or: case Code.Xor: case Code.Shl: case Code.Shr: case Code.Shr_Un:
            case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_I8: case Code.Conv_U1: case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_U8:
            case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
                kind = ValueKind.Number;
                return null;
            case Code.Ldflda: case Code.Ldsflda: case Code.Ldelema: case Code.Ldloca: case Code.Ldloca_S: case Code.Ldarga: case Code.Ldarga_S:
                kind = ValueKind.Address;
                return null;
            case Code.Ldstr: type = method.Module.TypeSystem.String; break;
            case Code.Ldloc: case Code.Ldloc_S: type = ((VariableDefinition)at.Operand).VariableType; break;
            case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                type = method.Body.Variables[at.OpCode.Code - Code.Ldloc_0].VariableType;
                break;
            case Code.Ldfld: case Code.Ldsfld: type = ((FieldReference)at.Operand).FieldType; break;
            case Code.Ldelem_Any: type = (TypeReference)at.Operand; break;
            case Code.Newobj: type = ((MethodReference)at.Operand).DeclaringType; break;
            case Code.Castclass: case Code.Isinst: type = (TypeReference)at.Operand; break;
            case Code.Call: case Code.Callvirt:
            {
                var called = (MethodReference)at.Operand;
                type = Substitute(called.ReturnType, called);
                break;
            }
        }
        if (type == null || type is GenericParameter || type is GenericInstanceType && type.ContainsGenericParameter) return null;
        if (type is ByReferenceType || type is PointerType) kind = ValueKind.Address;
        else if (IsNumber(type)) kind = ValueKind.Number;
        else if (IsReferenceType(type)) kind = ValueKind.Reference;
        return type;
    }

    // The type the instruction takes as its input'th popped value (0: the deepest), or null when it isn't
    // one type; wantsNumber when the instruction does arithmetic on it.
    static TypeReference ExpectedType(MethodDefinition method, Instruction at, int input, int depth, out bool wantsNumber)
    {
        wantsNumber = false;
        switch (at.OpCode.Code)
        {
            case Code.Call: case Code.Callvirt: case Code.Newobj:
            {
                var called = (MethodReference)at.Operand;
                int self = called.HasThis && at.OpCode.Code != Code.Newobj ? 1 : 0;
                if (input < self) return null;
                int index = input - self;
                if (index >= called.Parameters.Count) return null;
                return Substitute(called.Parameters[index].ParameterType, called);
            }
            case Code.Stfld: return input == 1 ? ((FieldReference)at.Operand).FieldType : null;
            case Code.Stsfld: return ((FieldReference)at.Operand).FieldType;
            case Code.Stloc: case Code.Stloc_S: return ((VariableDefinition)at.Operand).VariableType;
            case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                return method.Body.Variables[at.OpCode.Code - Code.Stloc_0].VariableType;
            case Code.Ret: return method.ReturnType.MetadataType == MetadataType.Void ? null : method.ReturnType;
            case Code.Stelem_Any: return input == 2 ? (TypeReference)at.Operand : null;
            case Code.Stelem_Ref: return input == 2 ? method.Module.TypeSystem.Object : null;
            case Code.Shr: case Code.Shr_Un: case Code.Mul: case Code.Div: case Code.Div_Un: case Code.Rem: case Code.Rem_Un:
            case Code.And: case Code.Or: case Code.Xor: case Code.Shl:
            case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_I8: case Code.Conv_U1: case Code.Conv_U2: case Code.Conv_U4: case Code.Conv_U8:
                wantsNumber = true;
                return null;
        }
        return null;
    }
}
