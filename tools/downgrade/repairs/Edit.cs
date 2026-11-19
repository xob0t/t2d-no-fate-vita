using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The engine's IL surgery: replacing a run of instructions in place, removing unreachable code,
// dropping Rewind's notes, and a stack-depth check.
namespace Repairs
{
    public static class Edit
    {
        // Replaces all[first..last] with added. The first instruction keeps its identity (it takes the
        // first added opcode, or becomes a nop), so branches and handler bounds that point at it stay
        // valid; anything that points inside the run is an error.
        public static void ReplaceRange(MethodBody body, int first, int last, IList<Instruction> added, string key)
        {
            IList<Instruction> all = body.Instructions;
            if (first < 0 || last >= all.Count || first > last) throw new RepairException(key + ": replacement outside the method");
            var inner = new HashSet<Instruction>();
            for (int i = first + 1; i <= last; i++) inner.Add(all[i]);
            foreach (Instruction ins in all)
            {
                if (inner.Contains(ins)) continue;
                foreach (Instruction t in Targets(ins))
                    if (inner.Contains(t)) throw new RepairException(key + ": IL_" + ins.Offset.ToString("x4") + " branches into the replaced run");
            }
            foreach (ExceptionHandler h in body.ExceptionHandlers)
                foreach (Instruction b in new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart })
                    if (b != null && inner.Contains(b)) throw new RepairException(key + ": an exception handler's bound is inside the replaced run");
            Instruction head = all[first];
            for (int i = last; i > first; i--) body.Instructions.RemoveAt(i);
            if (added.Count == 0)
            {
                head.OpCode = OpCodes.Nop;
                head.Operand = null;
                return;
            }
            Instruction lead = added[0];
            head.OpCode = lead.OpCode;
            head.Operand = lead.Operand;
            // branches the builder made to its own first instruction now go to head
            foreach (Instruction ins in added) Retarget(ins, lead, head);
            for (int i = 1; i < added.Count; i++) body.Instructions.Insert(first + i, added[i]);
        }

        public static IEnumerable<Instruction> Targets(Instruction ins)
        {
            var one = ins.Operand as Instruction;
            if (one != null) yield return one;
            var many = ins.Operand as Instruction[];
            if (many != null) foreach (Instruction t in many) yield return t;
        }

        static void Retarget(Instruction ins, Instruction from, Instruction to)
        {
            if (ins.Operand == from) ins.Operand = to;
            var many = ins.Operand as Instruction[];
            if (many != null) for (int i = 0; i < many.Length; i++) if (many[i] == from) many[i] = to;
        }
    }

    public static class Cleanup
    {
        // Removes instructions no path reaches (a write barrier behind a branch made unconditional);
        // returns the texts of the markers removed with them.
        public static List<string> RemoveUnreachable(MethodDefinition method)
        {
            MethodBody body = method.Body;
            IList<Instruction> all = body.Instructions;
            var index = new Dictionary<Instruction, int>();
            for (int i = 0; i < all.Count; i++) index[all[i]] = i;
            var reached = new bool[all.Count];
            var work = new Stack<int>();
            work.Push(0);
            foreach (ExceptionHandler h in body.ExceptionHandlers)
            {
                work.Push(index[h.HandlerStart]);
                if (h.FilterStart != null) work.Push(index[h.FilterStart]);
            }
            while (work.Count > 0)
            {
                int i = work.Pop();
                if (i >= all.Count || reached[i]) continue;
                reached[i] = true;
                Instruction ins = all[i];
                foreach (Instruction t in Edit.Targets(ins)) work.Push(index[t]);
                FlowControl f = ins.OpCode.FlowControl;
                if (f == FlowControl.Branch || f == FlowControl.Return || f == FlowControl.Throw) continue;
                if (ins.OpCode == OpCodes.Endfinally || ins.OpCode == OpCodes.Endfilter) continue;
                work.Push(i + 1);
            }
            var removed = new List<string>();
            if (reached.All(r => r)) return removed;
            // handler bounds on removed instructions move to the next kept one
            Func<Instruction, Instruction> keep = b =>
            {
                if (b == null) return null;
                for (int i = index[b]; i < all.Count; i++) if (reached[i]) return all[i];
                return null;
            };
            foreach (ExceptionHandler h in body.ExceptionHandlers.ToList())
            {
                h.TryStart = keep(h.TryStart);
                h.TryEnd = keep(h.TryEnd);
                h.HandlerEnd = keep(h.HandlerEnd);
                if (h.TryStart == null || h.TryStart == h.TryEnd) body.ExceptionHandlers.Remove(h);
            }
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if (reached[i]) continue;
                if (Markers.IsMarkerCall(all[i]) && all[i].Previous != null && all[i].Previous.OpCode == OpCodes.Ldstr)
                    removed.Add((string)all[i].Previous.Operand);
                body.Instructions.RemoveAt(i);
            }
            return removed;
        }

        // Rewind's "N part(s) of this method were not recovered" note, once nothing is left unrecovered.
        public static void RemoveNotes(MethodDefinition method)
        {
            IList<Instruction> all = method.Body.Instructions;
            for (int i = all.Count - 1; i > 0; i--)
            {
                if (!Markers.IsNoteCall(all[i]) || all[i - 1].OpCode != OpCodes.Ldstr) continue;
                all[i - 1].OpCode = OpCodes.Nop;
                all[i - 1].Operand = null;
                method.Body.Instructions.RemoveAt(i);
            }
        }

        // Short branches to long ones, before any edit: Cecil 0.10 (huntdown's) writes an operand
        // as given, so code inserted between a br.s and its target could overflow it. The repaired
        // methods keep the long forms.
        public static void LongBranches(MethodBody body)
        {
            foreach (Instruction ins in body.Instructions)
            {
                OpCode op = ins.OpCode;
                if (op.OperandType != OperandType.ShortInlineBrTarget) continue;
                ins.OpCode = Long(op);
            }
        }

        static OpCode Long(OpCode op)
        {
            switch (op.Code)
            {
                case Code.Br_S: return OpCodes.Br;
                case Code.Brfalse_S: return OpCodes.Brfalse;
                case Code.Brtrue_S: return OpCodes.Brtrue;
                case Code.Beq_S: return OpCodes.Beq;
                case Code.Bge_S: return OpCodes.Bge;
                case Code.Bgt_S: return OpCodes.Bgt;
                case Code.Ble_S: return OpCodes.Ble;
                case Code.Blt_S: return OpCodes.Blt;
                case Code.Bne_Un_S: return OpCodes.Bne_Un;
                case Code.Bge_Un_S: return OpCodes.Bge_Un;
                case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
                case Code.Ble_Un_S: return OpCodes.Ble_Un;
                case Code.Blt_Un_S: return OpCodes.Blt_Un;
                case Code.Leave_S: return OpCodes.Leave;
            }
            throw new RepairException("no long form of " + op.Name);
        }
    }

    // The stack depth before each instruction, along every path: underflow, a depth that differs
    // where paths meet, or a ret with the wrong depth is an error. ILVerify checks types afterwards.
    public static class StackCheck
    {
        public static string Check(MethodDefinition method)
        {
            MethodBody body = method.Body;
            IList<Instruction> all = body.Instructions;
            var index = new Dictionary<Instruction, int>();
            for (int i = 0; i < all.Count; i++) index[all[i]] = i;
            var depth = new int?[all.Count];
            var work = new Stack<int>();
            Action<int, int, string> at = null;
            string error = null;
            at = (i, d, from) =>
            {
                if (i >= all.Count) { error = error ?? "falls off the end after " + from; return; }
                if (depth[i] == null) { depth[i] = d; work.Push(i); }
                else if (depth[i] != d) error = error ?? "IL_" + all[i].Offset.ToString("x4") + " reached with depth " + depth[i] + " and " + d + " (from " + from + ")";
            };
            at(0, 0, "entry");
            foreach (ExceptionHandler h in body.ExceptionHandlers)
            {
                at(index[h.HandlerStart], h.HandlerType == ExceptionHandlerType.Catch || h.HandlerType == ExceptionHandlerType.Filter ? 1 : 0, "handler");
                if (h.FilterStart != null) at(index[h.FilterStart], 1, "filter");
            }
            bool returns = method.ReturnType.FullName != "System.Void";
            while (work.Count > 0 && error == null)
            {
                int i = work.Pop();
                Instruction ins = all[i];
                int d = depth[i].Value - Pops(ins, method, returns);
                string here = "IL_" + ins.Offset.ToString("x4") + " " + ins.OpCode.Name;
                if (d < 0) return here + ": stack underflow";
                if (ins.OpCode == OpCodes.Ret)
                {
                    if (d != 0) return here + ": " + d + " value(s) left at ret";
                    continue;
                }
                d += Pushes(ins);
                if (ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S) d = 0;
                foreach (Instruction t in Edit.Targets(ins)) at(index[t], d, here);
                FlowControl f = ins.OpCode.FlowControl;
                if (f == FlowControl.Branch || f == FlowControl.Return || f == FlowControl.Throw) continue;
                if (ins.OpCode == OpCodes.Endfinally || ins.OpCode == OpCodes.Endfilter) continue;
                at(i + 1, d, here);
            }
            return error;
        }

        internal static int Pops(Instruction ins, MethodDefinition method, bool returns)
        {
            OpCode op = ins.OpCode;
            if (op == OpCodes.Ret) return returns ? 1 : 0;
            var m = ins.Operand as MethodReference;
            if (op.StackBehaviourPop == StackBehaviour.Varpop && m != null)
            {
                int n = m.Parameters.Count;
                if (m.HasThis && op != OpCodes.Newobj) n++;
                if (op == OpCodes.Calli) n++;
                return n;
            }
            switch (op.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
                case StackBehaviour.PopAll: return 0;
            }
            return 0;
        }

        internal static int Pushes(Instruction ins)
        {
            OpCode op = ins.OpCode;
            var m = ins.Operand as MethodReference;
            if (op.StackBehaviourPush == StackBehaviour.Varpush && m != null)
                return op == OpCodes.Newobj || m.ReturnType.FullName != "System.Void" ? 1 : 0;
            switch (op.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                default: return 1;
            }
        }
    }
}
