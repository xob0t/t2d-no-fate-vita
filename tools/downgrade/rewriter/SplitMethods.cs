using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Very long straight-line methods cut into parts, for the PS Vita's C++ compiler.
//
// psp2snc at -O3 exits with code 1 and no message on one C++ function of about 67,000 lines (a static
// initializer that builds a table of int arrays, one RuntimeHelpers.InitializeArray after another); at -O0,
// or with that function alone at O=0, the file compiles. Functions of 12,000 lines compile at -O3.
// So a method with no branches, no exception handlers and more than SplitAbove instructions is cut, at
// points where the evaluation stack is empty, into parts of at most SplitPart instructions. Each part is a
// private static method of the same type, "<name>$part<n>", that the method calls in order:
//   - the method's own parameters go to every part by value (a method that stores to them, or takes their
//     address, is left alone);
//   - a local used by more than one part stays the method's, and each part that uses it takes it by
//     reference: copied into a local of the part at its start and written back at its end;
//   - a local only one part uses moves into that part.
// The parts run one after the other, as the instructions did; nothing else calls them.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    const int SplitAbove = 16000;
    const int SplitPart = 6000;

    static int SplitLongMethods(ModuleDefinition module)
    {
        int split = 0;
        foreach (TypeDefinition type in AllTypes(module.Types).ToList())
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody && m.Body.Instructions.Count > SplitAbove).ToList())
            {
                string why = SplitMethod(method);
                if (why == null)
                {
                    split++;
                    Console.WriteLine("SPLIT " + method.FullName);
                }
                else Console.WriteLine("SPLIT SKIPPED " + method.FullName + ": " + why);
            }
        return split;
    }

    static string SplitMethod(MethodDefinition method)
    {
        MethodBody body = method.Body;
        if (body.HasExceptionHandlers) return "exception handlers";
        if (method.HasThis) return "an instance method";
        if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters) return "generic";
        var instructions = body.Instructions.ToList();
        Instruction last = instructions[instructions.Count - 1];
        if (last.OpCode.Code != Code.Ret || method.ReturnType.MetadataType != MetadataType.Void) return "doesn't end in a void return";
        foreach (Instruction i in instructions.Take(instructions.Count - 1))
        {
            FlowControl flow = i.OpCode.FlowControl;
            if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch || flow == FlowControl.Return || flow == FlowControl.Throw)
                return "control flow at IL_" + i.Offset.ToString("x4");
            Code c = i.OpCode.Code;
            if (c == Code.Starg || c == Code.Starg_S || c == Code.Ldarga || c == Code.Ldarga_S) return "stores to or takes the address of a parameter";
            if (c == Code.Localloc || c == Code.Jmp || c == Code.Tail || c == Code.Calli) return "uses " + i.OpCode.Name;
        }

        // Cut points: before an instruction where the stack is empty.
        var cuts = new List<int> { 0 };
        int depth = 0, since = 0;
        for (int k = 0; k < instructions.Count - 1; k++)
        {
            if (depth == 0 && since >= SplitPart) { cuts.Add(k); since = 0; }
            int pops, pushes;
            StackEffect(method, instructions[k], depth, out pops, out pushes);
            depth += pushes - pops;
            if (depth < 0) return "stack underflow";
            since++;
        }
        if (depth != 0) return "stack not empty at the end";
        if (cuts.Count < 2) return "no place to cut";
        cuts.Add(instructions.Count - 1);

        // Which parts use each local; a local whose address is taken and is used by more than one part is refused.
        var parts = new List<List<Instruction>>();
        for (int p = 0; p + 1 < cuts.Count; p++) parts.Add(instructions.GetRange(cuts[p], cuts[p + 1] - cuts[p]));
        var usedBy = new Dictionary<VariableDefinition, HashSet<int>>();
        var addressed = new HashSet<VariableDefinition>();
        for (int p = 0; p < parts.Count; p++)
            foreach (Instruction i in parts[p])
            {
                VariableDefinition v = PartLocalOf(body, i);
                if (v == null) continue;
                HashSet<int> set;
                if (!usedBy.TryGetValue(v, out set)) usedBy[v] = set = new HashSet<int>();
                set.Add(p);
                if (i.OpCode.Code == Code.Ldloca || i.OpCode.Code == Code.Ldloca_S) addressed.Add(v);
            }
        foreach (VariableDefinition v in addressed)
            if (usedBy[v].Count > 1) return "the address of a shared local is taken";

        ModuleDefinition module = method.Module;
        var calls = new List<Instruction>();
        for (int p = 0; p < parts.Count; p++)
        {
            var part = new MethodDefinition(method.Name + "$part" + (p + 1), MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            foreach (ParameterDefinition parameter in method.Parameters)
                part.Parameters.Add(new ParameterDefinition(parameter.Name, ParameterAttributes.None, parameter.ParameterType));
            var shared = usedBy.Where(kv => kv.Value.Contains(p) && kv.Value.Count > 1).Select(kv => kv.Key).OrderBy(v => v.Index).ToList();
            var byRef = new Dictionary<VariableDefinition, ParameterDefinition>();
            foreach (VariableDefinition v in shared)
            {
                var parameter = new ParameterDefinition("local" + v.Index, ParameterAttributes.None, new ByReferenceType(v.VariableType));
                part.Parameters.Add(parameter);
                byRef[v] = parameter;
            }
            var locals = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (VariableDefinition v in usedBy.Where(kv => kv.Value.Contains(p)).Select(kv => kv.Key).OrderBy(v => v.Index))
            {
                var copy = new VariableDefinition(v.VariableType);
                part.Body.Variables.Add(copy);
                locals[v] = copy;
            }
            part.Body.InitLocals = body.InitLocals;
            ILProcessor il = part.Body.GetILProcessor();
            foreach (VariableDefinition v in shared)
            {
                il.Append(il.Create(OpCodes.Ldarg, byRef[v]));
                il.Append(il.Create(OpCodes.Ldobj, v.VariableType));
                il.Append(il.Create(OpCodes.Stloc, locals[v]));
            }
            foreach (Instruction i in parts[p]) il.Append(CopyInstruction(il, i, body, method, part, locals));
            foreach (VariableDefinition v in shared)
            {
                il.Append(il.Create(OpCodes.Ldarg, byRef[v]));
                il.Append(il.Create(OpCodes.Ldloc, locals[v]));
                il.Append(il.Create(OpCodes.Stobj, v.VariableType));
            }
            il.Append(il.Create(OpCodes.Ret));
            part.Body.MaxStackSize = Math.Max(body.MaxStackSize, 2);
            method.DeclaringType.Methods.Add(part);

            // The call in the method: its parameters, then the shared locals' addresses.
            foreach (ParameterDefinition parameter in method.Parameters) calls.Add(Instruction.Create(OpCodes.Ldarg, parameter));
            foreach (VariableDefinition v in shared) calls.Add(Instruction.Create(OpCodes.Ldloca, v));
            calls.Add(Instruction.Create(OpCodes.Call, part));
        }

        // The method keeps its shared locals and becomes the calls.
        var keep = usedBy.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        body.Instructions.Clear();
        foreach (VariableDefinition v in body.Variables.ToList())
            if (!keep.Contains(v)) body.Variables.Remove(v);
        ILProcessor top = body.GetILProcessor();
        foreach (Instruction call in calls) top.Append(call);
        top.Append(top.Create(OpCodes.Ret));
        return null;
    }

    static VariableDefinition PartLocalOf(MethodBody body, Instruction i)
    {
        switch (i.OpCode.Code)
        {
            case Code.Ldloc_0: case Code.Stloc_0: return body.Variables[0];
            case Code.Ldloc_1: case Code.Stloc_1: return body.Variables[1];
            case Code.Ldloc_2: case Code.Stloc_2: return body.Variables[2];
            case Code.Ldloc_3: case Code.Stloc_3: return body.Variables[3];
        }
        return i.Operand as VariableDefinition;
    }

    static Instruction CopyInstruction(ILProcessor il, Instruction i, MethodBody body, MethodDefinition from, MethodDefinition to,
        Dictionary<VariableDefinition, VariableDefinition> locals)
    {
        VariableDefinition v = PartLocalOf(body, i);
        if (v != null)
        {
            Code c = i.OpCode.Code;
            bool store = c == Code.Stloc || c == Code.Stloc_S || c == Code.Stloc_0 || c == Code.Stloc_1 || c == Code.Stloc_2 || c == Code.Stloc_3;
            bool address = c == Code.Ldloca || c == Code.Ldloca_S;
            return il.Create(store ? OpCodes.Stloc : address ? OpCodes.Ldloca : OpCodes.Ldloc, locals[v]);
        }
        switch (i.OpCode.Code)
        {
            case Code.Ldarg_0: return il.Create(OpCodes.Ldarg, to.Parameters[0]);
            case Code.Ldarg_1: return il.Create(OpCodes.Ldarg, to.Parameters[1]);
            case Code.Ldarg_2: return il.Create(OpCodes.Ldarg, to.Parameters[2]);
            case Code.Ldarg_3: return il.Create(OpCodes.Ldarg, to.Parameters[3]);
            case Code.Ldarg: case Code.Ldarg_S:
                return il.Create(OpCodes.Ldarg, to.Parameters[((ParameterDefinition)i.Operand).Index]);
        }
        // Everything else keeps its operand (types, members, constants, strings, tokens); no branches here.
        var copy = Instruction.Create(OpCodes.Nop);
        copy.OpCode = i.OpCode;
        copy.Operand = i.Operand;
        return copy;
    }
}
