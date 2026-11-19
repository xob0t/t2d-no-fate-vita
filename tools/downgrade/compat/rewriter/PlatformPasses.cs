using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The platform layer's pass: applies PlatformRules.cs to the game's assembly, so its store, language, sound and
// startup code runs offline on T2DCompat (compat/Store.cs, Sounds.cs). InControl needs no pass: the rewriter's type
// retargeting sends it to compat/InControl.cs once the roots leave InControl out ("-InControl").
//
// Part of the assembly rewriter (DowngradeAssemblies, a partial class across files): add this file and
// PlatformRules.cs to pipeline.REWRITER_SOURCES, and
//     new Pass("platform", Scope.Game, PlatformPatches),
// to DowngradeAssemblies.Passes, after the type retargeting's redirect pass and before ListRewindGaps (docs/platform.md).
// It uses the rewriter's compat, problems, Count, AllTypes and CompatMethod, and nothing else of it; its own helpers
// are named Platform*.
//
// Every rule counts its sites (Count): one that matched nothing in the game's assembly is a NO MATCH and fails the run.
// A rule that finds what it expects in a shape it can't change (a sequence a branch lands inside, a cut whose call isn't
// followed by a pop) is a problem (UNRESOLVED), and the method is left as it was.
// C# 5 and Mono.Cecil 0.10.0-beta6, like the rewriter.
partial class DowngradeAssemblies
{
    static int PlatformPatches(ModuleDefinition module)
    {
        if (module.GetType("com.twissgreen.unity.UnityMain") == null) return 0;
        // Cuts look for the Steam call that Calls redirects, so they run first.
        return PlatformCuts(module) + PlatformStubs(module) + PlatformSequences(module) + PlatformForwards(module) +
               PlatformSwitches(module) + PlatformSoundLoader(module) + PlatformCalls(module);
    }

    // ---- the rules ----

    static int PlatformStubs(ModuleDefinition module)
    {
        int total = 0;
        foreach (string[] rule in PlatformRules.Stubbed)
        {
            TypeDefinition type = module.GetType(rule[0]);
            int count = 0;
            if (type != null)
            {
                IEnumerable<TypeDefinition> types = rule[1] == "*" ? AllTypes(new[] { type }) : new[] { type };
                foreach (TypeDefinition t in types)
                    foreach (MethodDefinition method in t.Methods)
                    {
                        if (!method.HasBody || method.IsConstructor || (rule[1] != "*" && method.Name != rule[1])) continue;
                        string why = PlatformStub(method);
                        if (why != null) problems.Add(module.Name + ": platform stub " + method.FullName + ": " + why);
                        else count++;
                    }
            }
            Count("platform stub " + rule[0] + "::" + rule[1], count);
            total += count;
        }
        return total;
    }

    static int PlatformCalls(ModuleDefinition module)
    {
        int total = 0;
        foreach (string[] rule in PlatformRules.Calls)
        {
            MethodReference replacement = CompatMethod(module, rule[1], rule[2]);
            int count = 0;
            foreach (TypeDefinition type in AllTypes(module.Types))
                foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        var called = instruction.Operand as MethodReference;
                        if (called == null || called.FullName != rule[0]) continue;
                        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                        {
                            problems.Add(module.Name + ": platform call " + rule[0] + ": " + instruction.OpCode + " in " + method.FullName);
                            continue;
                        }
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = replacement;
                        count++;
                    }
            Count("platform call " + rule[0], count);
            total += count;
        }
        return total;
    }

    static int PlatformSequences(ModuleDefinition module)
    {
        int total = 0;
        foreach (string[] rule in PlatformRules.Sequences)
        {
            MethodDefinition method = PlatformMethod(module, rule[0]);
            int count = 0;
            if (method != null)
            {
                string[] wanted = rule.Skip(3).ToArray();
                List<int> starts = PlatformFind(method, wanted);
                if (starts.Count != 1)
                    problems.Add(module.Name + ": platform sequence in " + rule[0] + ": " + starts.Count + " matches, not 1");
                else
                {
                    IList<Instruction> all = method.Body.Instructions;
                    int at = starts[0];
                    var inner = new HashSet<Instruction>(Enumerable.Range(at + 1, wanted.Length - 1).Select(i => all[i]));
                    if (PlatformTargeted(method.Body, inner))
                        problems.Add(module.Name + ": platform sequence in " + rule[0] + ": a branch or handler lands inside it");
                    else
                    {
                        all[at].OpCode = OpCodes.Call;
                        all[at].Operand = CompatMethod(module, rule[1], rule[2]);
                        ILProcessor il = method.Body.GetILProcessor();
                        foreach (Instruction gone in inner) il.Remove(gone);
                        count++;
                    }
                }
            }
            Count("platform sequence " + rule[0] + " -> " + rule[1] + "." + rule[2], count);
            total += count;
        }
        return total;
    }

    static int PlatformForwards(ModuleDefinition module)
    {
        int total = 0;
        foreach (string[] rule in PlatformRules.Forwards)
        {
            MethodDefinition method = PlatformMethod(module, rule[0]);
            int count = 0;
            if (method != null)
            {
                MethodDefinition target = method.DeclaringType.Methods.FirstOrDefault(m => m.Name == rule[1] && m.IsStatic == method.IsStatic &&
                    m.ReturnType.FullName == method.ReturnType.FullName &&
                    m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => p.ParameterType.FullName)));
                if (target == null) problems.Add(module.Name + ": platform forward " + rule[0] + ": no " + rule[1] + " of the same signature");
                else
                {
                    ILProcessor il = PlatformNewBody(method);
                    if (method.HasThis) il.Emit(OpCodes.Ldarg_0);
                    foreach (ParameterDefinition parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
                    il.Emit(method.IsStatic ? OpCodes.Call : OpCodes.Callvirt, target);
                    il.Emit(OpCodes.Ret);
                    count++;
                }
            }
            Count("platform forward " + rule[0], count);
            total += count;
        }
        return total;
    }

    static int PlatformSwitches(ModuleDefinition module)
    {
        int total = 0;
        foreach (string[] rule in PlatformRules.Switches)
        {
            MethodDefinition method = PlatformMethod(module, rule[0]);
            int count = 0;
            if (method != null)
            {
                var stores = new List<KeyValuePair<FieldDefinition, MethodReference>>();
                for (int i = 2; i + 1 < rule.Length; i += 2)
                {
                    FieldDefinition field = method.DeclaringType.Fields.FirstOrDefault(f => f.Name == rule[i] && f.IsStatic &&
                        f.FieldType.MetadataType == MetadataType.Boolean);
                    if (field == null) problems.Add(module.Name + ": platform switches " + rule[0] + ": no static bool " + rule[i]);
                    else stores.Add(new KeyValuePair<FieldDefinition, MethodReference>(field, CompatMethod(module, rule[1], rule[i + 1])));
                }
                if (stores.Count == (rule.Length - 2) / 2 && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 0)
                {
                    ILProcessor il = PlatformNewBody(method);
                    foreach (KeyValuePair<FieldDefinition, MethodReference> store in stores)
                    {
                        il.Emit(OpCodes.Call, store.Value);
                        il.Emit(OpCodes.Stsfld, store.Key);
                    }
                    il.Emit(OpCodes.Ret);
                    count = stores.Count;
                }
            }
            Count("platform switches " + rule[0], count);
            total += count;
        }
        return total;
    }

    static int PlatformCuts(ModuleDefinition module)
    {
        int total = 0;
        foreach (string[] rule in PlatformRules.Cuts)
        {
            MethodDefinition method = PlatformMethod(module, rule[0]);
            int count = 0;
            if (method != null)
            {
                Instruction[] calls = method.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand is MethodReference && ((MethodReference)i.Operand).FullName == rule[1]).ToArray();
                if (method.ReturnType.MetadataType != MetadataType.Void)
                    problems.Add(module.Name + ": platform cut " + rule[0] + ": the method returns a value");
                else if (calls.Length != 1 || calls[0].Next == null || calls[0].Next.OpCode != OpCodes.Pop)
                    problems.Add(module.Name + ": platform cut " + rule[0] + ": " + calls.Length + " calls of " + rule[1] + " followed by a pop, not 1");
                else
                {
                    method.Body.GetILProcessor().InsertAfter(calls[0].Next, Instruction.Create(OpCodes.Ret));
                    int removed = PlatformRemoveUnreachable(method.Body);
                    count = 1;
                    if (removed < 0) problems.Add(module.Name + ": platform cut " + rule[0] + ": unreachable code a handler bounds was left");
                }
            }
            Count("platform cut " + rule[0], count);
            total += count;
        }
        return total;
    }

    static int PlatformSoundLoader(ModuleDefinition module)
    {
        string[] rule = PlatformRules.SoundLoader;
        MethodDefinition method = PlatformMethod(module, rule[0]);
        int count = 0;
        if (method != null)
        {
            TypeDefinition sound = method.DeclaringType;
            MethodDefinition constructor = sound.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.FullName == rule[1]);
            FieldDefinition name = sound.Fields.FirstOrDefault(f => f.Name == rule[2] && !f.IsStatic && f.FieldType.MetadataType == MetadataType.String);
            MethodReference clip = CompatMethod(module, rule[3], rule[4]);
            if (constructor == null || name == null || method.Parameters.Count != 1 || method.ReturnType.FullName != sound.FullName)
                problems.Add(module.Name + ": platform sound loader " + rule[0] + ": no " + rule[1] + " or " + rule[2] + ", or another signature");
            else
            {
                ILProcessor il = PlatformNewBody(method);
                var local = new VariableDefinition(clip.ReturnType);
                method.Body.Variables.Add(local);
                Instruction found = il.Create(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, clip);
                il.Emit(OpCodes.Stloc, local);
                il.Emit(OpCodes.Ldloc, local);
                il.Emit(OpCodes.Brtrue, found);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                il.Append(found);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stfld, name);
                il.Emit(OpCodes.Ret);
                count = 1;
            }
        }
        Count("platform sound loader " + rule[0], count);
        return count;
    }

    // ---- helpers ----

    // A method by "Type::name" (the only one of that name), or null; several of that name are a problem.
    static MethodDefinition PlatformMethod(ModuleDefinition module, string name)
    {
        int split = name.IndexOf("::", StringComparison.Ordinal);
        TypeDefinition type = module.GetType(name.Substring(0, split));
        if (type == null) return null;
        MethodDefinition[] found = type.Methods.Where(m => m.Name == name.Substring(split + 2) && m.HasBody).ToArray();
        if (found.Length > 1) problems.Add(module.Name + ": platform rule " + name + ": " + found.Length + " methods of that name");
        return found.Length == 1 ? found[0] : null;
    }

    // An instruction as the rules name it: its opcode, and its operand as Cecil prints it.
    static string PlatformShow(Instruction instruction)
    {
        var member = instruction.Operand as MemberReference;
        return instruction.OpCode.Name + (member != null ? " " + member.FullName : instruction.Operand != null ? " " + instruction.Operand : "");
    }

    // Where a run of instructions starts in a method.
    static List<int> PlatformFind(MethodDefinition method, string[] wanted)
    {
        var starts = new List<int>();
        IList<Instruction> all = method.Body.Instructions;
        for (int i = 0; i + wanted.Length <= all.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < wanted.Length && match; j++) match = PlatformShow(all[i + j]) == wanted[j];
            if (match) starts.Add(i);
        }
        return starts;
    }

    // Whether a branch, switch or handler bound points at one of the instructions.
    static bool PlatformTargeted(MethodBody body, HashSet<Instruction> instructions)
    {
        foreach (Instruction instruction in body.Instructions)
        {
            if (instruction.Operand is Instruction && instructions.Contains((Instruction)instruction.Operand)) return true;
            var targets = instruction.Operand as Instruction[];
            if (targets != null && targets.Any(instructions.Contains)) return true;
        }
        return body.ExceptionHandlers.Any(h => instructions.Contains(h.TryStart) || instructions.Contains(h.TryEnd) ||
            instructions.Contains(h.HandlerStart) || instructions.Contains(h.HandlerEnd) || (h.FilterStart != null && instructions.Contains(h.FilterStart)));
    }

    // An empty body for the method, its old locals and handlers gone.
    static ILProcessor PlatformNewBody(MethodDefinition method)
    {
        method.Body = new MethodBody(method) { InitLocals = true };
        return method.Body.GetILProcessor();
    }

    // The method returns its result type's default (StubAssembly's stub): null, false, 0, a zeroed struct; out
    // parameters are zeroed first. Returns why it can't, or null.
    static string PlatformStub(MethodDefinition method)
    {
        TypeReference returns = method.ReturnType;
        if (returns.IsByReference) return "it returns by reference";
        ILProcessor il = PlatformNewBody(method);
        foreach (ParameterDefinition parameter in method.Parameters)
        {
            if (!parameter.IsOut || !(parameter.ParameterType is ByReferenceType)) continue;
            il.Emit(OpCodes.Ldarg, parameter);
            il.Emit(OpCodes.Initobj, ((ByReferenceType)parameter.ParameterType).ElementType);
        }
        if (returns.IsValueType || returns.IsGenericParameter)
        {
            var local = new VariableDefinition(returns);
            method.Body.Variables.Add(local);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Initobj, returns);
            il.Emit(OpCodes.Ldloc, local);
        }
        else if (returns.MetadataType != MetadataType.Void) il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return null;
    }

    // Removes the instructions no path from the method's start reaches (a handler's start counts as reached when its
    // try block is). Handlers whose try block is all unreachable go too. Returns the number removed, or -1 when an
    // unreachable instruction is a bound of a handler that stays (then nothing is removed).
    static int PlatformRemoveUnreachable(MethodBody body)
    {
        IList<Instruction> all = body.Instructions;
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < all.Count; i++) index[all[i]] = i;
        var reached = new bool[all.Count];
        var pending = new Stack<int>();
        pending.Push(0);
        bool grew = true;
        while (grew)
        {
            while (pending.Count > 0)
            {
                int at = pending.Pop();
                if (at < 0 || at >= all.Count || reached[at]) continue;
                reached[at] = true;
                Instruction instruction = all[at];
                FlowControl flow = instruction.OpCode.FlowControl;
                var target = instruction.Operand as Instruction;
                if (target != null) pending.Push(index[target]);
                var targets = instruction.Operand as Instruction[];
                if (targets != null) foreach (Instruction t in targets) pending.Push(index[t]);
                if (flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw &&
                    instruction.OpCode != OpCodes.Endfinally && instruction.OpCode != OpCodes.Leave && instruction.OpCode != OpCodes.Leave_S)
                    pending.Push(at + 1);
            }
            grew = false;
            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                int start = index[handler.TryStart], end = handler.TryEnd == null ? all.Count : index[handler.TryEnd];
                bool tryReached = Enumerable.Range(start, end - start).Any(i => reached[i]);
                if (!tryReached) continue;
                foreach (Instruction entry in new[] { handler.HandlerStart, handler.FilterStart })
                    if (entry != null && !reached[index[entry]])
                    {
                        pending.Push(index[entry]);
                        grew = true;
                    }
            }
        }
        var dead = new HashSet<Instruction>(all.Where((instruction, i) => !reached[i]));
        if (dead.Count == 0) return 0;
        var keptHandlers = body.ExceptionHandlers.Where(h => !dead.Contains(h.TryStart)).ToList();
        foreach (ExceptionHandler handler in keptHandlers)
            foreach (Instruction bound in new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart })
                if (bound != null && dead.Contains(bound)) return -1;
        foreach (ExceptionHandler handler in body.ExceptionHandlers.ToArray())
            if (!keptHandlers.Contains(handler)) body.ExceptionHandlers.Remove(handler);
        ILProcessor il = body.GetILProcessor();
        foreach (Instruction instruction in dead) il.Remove(instruction);
        // Locals no instruction names any more stay: removing them would renumber the others.
        return dead.Count;
    }
}
