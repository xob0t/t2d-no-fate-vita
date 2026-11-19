using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The stack before each instruction, for the rewriter's stack check (DowngradeAssemblies.CheckStacks).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // The stack before each instruction, as the instruction that pushed each value (null when paths
    // disagree), from the method's entry and its handlers; null when the method can't be followed.
    static Dictionary<Instruction, Instruction[]> StackProducers(MethodDefinition method, Dictionary<Instruction, List<KeyValuePair<Instruction, int>>> consumers)
    {
        var before = new Dictionary<Instruction, Instruction[]>();
        var work = new Queue<Instruction>();
        Action<Instruction, Instruction[]> reach = (at, stack) =>
        {
            Instruction[] known;
            if (!before.TryGetValue(at, out known))
            {
                before[at] = stack;
                work.Enqueue(at);
                return;
            }
            if (known.Length != stack.Length) throw new InvalidOperationException("stack depth differs at " + at);
            bool changed = false;
            var merged = (Instruction[])known.Clone();
            for (int i = 0; i < merged.Length; i++)
                if (merged[i] != stack[i] && merged[i] != null) { merged[i] = null; changed = true; }
            if (changed) { before[at] = merged; work.Enqueue(at); }
        };
        reach(method.Body.Instructions[0], new Instruction[0]);
        foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
        {
            bool takesException = handler.HandlerType == ExceptionHandlerType.Catch || handler.HandlerType == ExceptionHandlerType.Filter;
            reach(handler.HandlerStart, takesException ? new Instruction[] { null } : new Instruction[0]);
            if (handler.FilterStart != null) reach(handler.FilterStart, new Instruction[] { null });
        }
        while (work.Count > 0)
        {
            Instruction at = work.Dequeue();
            Instruction[] stack = before[at];
            int pops, pushes;
            StackEffect(method, at, stack.Length, out pops, out pushes);
            if (pops > stack.Length) throw new InvalidOperationException("stack underflow at " + at);
            if (consumers != null)
                for (int i = 0; i < pops; i++)
                {
                    Instruction producer = stack[stack.Length - pops + i];
                    if (producer == null) continue;
                    List<KeyValuePair<Instruction, int>> list;
                    if (!consumers.TryGetValue(producer, out list)) consumers[producer] = list = new List<KeyValuePair<Instruction, int>>();
                    if (!list.Any(p => p.Key == at && p.Value == i)) list.Add(new KeyValuePair<Instruction, int>(at, i));
                }
            var next = new List<Instruction>(stack.Take(stack.Length - pops));
            Instruction dupped = at.OpCode.Code == Code.Dup ? stack[stack.Length - 1] : at;
            for (int i = 0; i < pushes; i++) next.Add(dupped);
            Instruction[] after = next.ToArray();
            FlowControl flow = at.OpCode.FlowControl;
            if (at.OpCode.Code == Code.Leave || at.OpCode.Code == Code.Leave_S)
            {
                reach((Instruction)at.Operand, new Instruction[0]);
                continue;
            }
            if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch)
            {
                var targets = at.Operand as Instruction[];
                if (targets != null) foreach (Instruction target in targets) reach(target, after);
                else reach((Instruction)at.Operand, after);
            }
            if (flow == FlowControl.Branch || flow == FlowControl.Return || flow == FlowControl.Throw) continue;
            if (at.OpCode.Code == Code.Endfinally || at.OpCode.Code == Code.Endfilter) continue;
            if (at.Next != null) reach(at.Next, after);
        }
        return before;
    }

    static void StackEffect(MethodDefinition method, Instruction at, int depth, out int pops, out int pushes)
    {
        OpCode code = at.OpCode;
        var called = at.Operand as MethodReference;
        if (code.Code == Code.Call || code.Code == Code.Callvirt || code.Code == Code.Newobj)
        {
            pops = called.Parameters.Count + (called.HasThis && code.Code != Code.Newobj ? 1 : 0);
            pushes = code.Code == Code.Newobj || called.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
            return;
        }
        if (code.Code == Code.Calli)
        {
            // The call site's arguments, then the function pointer.
            var site = (CallSite)at.Operand;
            pops = site.Parameters.Count + (site.HasThis ? 1 : 0) + 1;
            pushes = site.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
            return;
        }
        if (code.Code == Code.Ret)
        {
            pops = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            pushes = 0;
            return;
        }
        if (code.Code == Code.Leave || code.Code == Code.Leave_S || code.Code == Code.Endfinally) { pops = depth; pushes = 0; return; }
        switch (code.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: pops = 0; break;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: pops = 1; break;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: pops = 2; break;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: pops = 3; break;
            case StackBehaviour.PopAll: pops = depth; break;
            default: throw new InvalidOperationException("stack behaviour " + code.StackBehaviourPop + " at " + at);
        }
        switch (code.StackBehaviourPush)
        {
            case StackBehaviour.Push0: pushes = 0; break;
            case StackBehaviour.Push1_push1: pushes = 2; break;
            case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4: case StackBehaviour.Pushr8:
            case StackBehaviour.Pushref: pushes = 1; break;
            default: throw new InvalidOperationException("stack behaviour " + code.StackBehaviourPush + " at " + at);
        }
    }
}
