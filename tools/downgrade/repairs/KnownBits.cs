using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Which bits of each integer value a method's IL computes are known, at every instruction: a forward
// dataflow over constants, locals and the stack (and, or, xor, shifts, conversions; anything else is
// unknown). Patterns ask it for a value Rewind kept only as bit arithmetic, such as the low half of
// `0 | (ulong)(uint)x << 32`. Arguments, fields, calls and markers are unknown, and a local whose
// address is taken is never known.
namespace Repairs
{
    public struct Bits
    {
        public ulong Mask, Value;   // Mask: the bits known; Value: their values (0 where unknown)
        public bool Wide;           // an int64 on the stack (else an int32, whose upper half doesn't count)

        public static readonly Bits Unknown = new Bits();
        public static Bits Const(long v, bool wide) { return Norm(new Bits { Mask = ~0UL, Value = (ulong)v, Wide = wide }); }

        // An int32 counts only its low 32 bits.
        public static Bits Norm(Bits b)
        {
            if (!b.Wide) { b.Mask &= 0xFFFFFFFFUL; b.Value &= 0xFFFFFFFFUL; }
            b.Value &= b.Mask;
            return b;
        }

        public bool KnownLow32 { get { return (Mask & 0xFFFFFFFFUL) == 0xFFFFFFFFUL; } }
        public int Low32 { get { return (int)(uint)Value; } }

        public static Bits Meet(Bits a, Bits b)
        {
            ulong mask = a.Mask & b.Mask & ~(a.Value ^ b.Value);
            return new Bits { Mask = mask, Value = a.Value & mask, Wide = a.Wide || b.Wide };
        }

        public bool Same(Bits o) { return Mask == o.Mask && Value == o.Value && Wide == o.Wide; }
    }

    public sealed class KnownBits
    {
        sealed class State
        {
            public Bits[] Locals;
            public List<Bits> Stack;
            public State Copy() { return new State { Locals = (Bits[])Locals.Clone(), Stack = new List<Bits>(Stack) }; }
        }

        readonly MethodBody body;
        readonly Dictionary<Instruction, int> index = new Dictionary<Instruction, int>();
        readonly State[] before;
        readonly HashSet<VariableDefinition> addressed = new HashSet<VariableDefinition>();

        // null if the stack can't be followed (paths meeting with different depths).
        public static KnownBits Of(MethodDefinition method)
        {
            var k = new KnownBits(method);
            return k.Solve() ? k : null;
        }

        KnownBits(MethodDefinition method)
        {
            body = method.Body;
            IList<Instruction> all = body.Instructions;
            for (int i = 0; i < all.Count; i++) index[all[i]] = i;
            before = new State[all.Count];
            foreach (Instruction ins in all)
                if (ins.OpCode.Code == Code.Ldloca || ins.OpCode.Code == Code.Ldloca_S) addressed.Add((VariableDefinition)ins.Operand);
        }

        // The stack before ins, top last; null where no path reaches it.
        public IList<Bits> StackBefore(Instruction ins)
        {
            State s = before[index[ins]];
            return s == null ? null : s.Stack;
        }

        bool Solve()
        {
            IList<Instruction> all = body.Instructions;
            var work = new Stack<int>();
            var entry = new State { Locals = new Bits[body.Variables.Count], Stack = new List<Bits>() };
            for (int i = 0; i < entry.Locals.Length; i++) entry.Locals[i] = Initial(body.Variables[i]);
            if (!Merge(0, entry, work)) return false;
            foreach (ExceptionHandler h in body.ExceptionHandlers)
            {
                var s = new State { Locals = new Bits[body.Variables.Count], Stack = new List<Bits>() };
                if (h.HandlerType == ExceptionHandlerType.Catch || h.HandlerType == ExceptionHandlerType.Filter) s.Stack.Add(Bits.Unknown);
                if (!Merge(index[h.HandlerStart], s, work)) return false;
                if (h.FilterStart != null && !Merge(index[h.FilterStart], s.Copy(), work)) return false;
            }
            while (work.Count > 0)
            {
                int i = work.Pop();
                Instruction ins = all[i];
                State s = before[i].Copy();
                if (!Step(ins, s)) return false;
                if (ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S) s.Stack.Clear();
                foreach (Instruction t in Edit.Targets(ins))
                    if (!Merge(index[t], s.Copy(), work)) return false;
                FlowControl f = ins.OpCode.FlowControl;
                if (f == FlowControl.Branch || f == FlowControl.Return || f == FlowControl.Throw) continue;
                if (ins.OpCode == OpCodes.Endfinally || ins.OpCode == OpCodes.Endfilter) continue;
                if (i + 1 < all.Count && !Merge(i + 1, s, work)) return false;
            }
            return true;
        }

        Bits Initial(VariableDefinition v)
        {
            // locals start zeroed (Rewind's methods are initlocals), but a pattern shouldn't lean on it
            return Bits.Unknown;
        }

        bool Merge(int i, State s, Stack<int> work)
        {
            State old = before[i];
            if (old == null)
            {
                before[i] = s;
                work.Push(i);
                return true;
            }
            if (old.Stack.Count != s.Stack.Count) return false;
            bool changed = false;
            for (int k = 0; k < old.Locals.Length; k++)
            {
                Bits m = Bits.Meet(old.Locals[k], s.Locals[k]);
                if (!m.Same(old.Locals[k])) { old.Locals[k] = m; changed = true; }
            }
            for (int k = 0; k < old.Stack.Count; k++)
            {
                Bits m = Bits.Meet(old.Stack[k], s.Stack[k]);
                if (!m.Same(old.Stack[k])) { old.Stack[k] = m; changed = true; }
            }
            if (changed) work.Push(i);
            return true;
        }

        static bool IsWide(TypeReference t)
        {
            string n = t.FullName;
            return n == "System.Int64" || n == "System.UInt64";
        }

        Bits Pop(State s)
        {
            if (s.Stack.Count == 0) throw new RepairException("stack underflow");
            Bits b = s.Stack[s.Stack.Count - 1];
            s.Stack.RemoveAt(s.Stack.Count - 1);
            return b;
        }

        bool Step(Instruction ins, State s)
        {
            try { return Transfer(ins, s); }
            catch (RepairException) { return false; }
        }

        bool Transfer(Instruction ins, State s)
        {
            Code c = ins.OpCode.Code;
            int? i4 = Markers.ConstI4(ins);
            if (i4 != null) { s.Stack.Add(Bits.Const(i4.Value, false)); return true; }
            switch (c)
            {
                case Code.Nop: return true;
                case Code.Ldc_I8: s.Stack.Add(Bits.Const((long)ins.Operand, true)); return true;
                case Code.Dup: { Bits b = Pop(s); s.Stack.Add(b); s.Stack.Add(b); return true; }
                case Code.Pop: Pop(s); return true;
                case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                {
                    VariableDefinition v = Fingerprint.Local(ins, body);
                    Bits b = addressed.Contains(v) ? Bits.Unknown : s.Locals[v.Index];
                    b.Wide = IsWide(v.VariableType);
                    s.Stack.Add(Bits.Norm(b));
                    return true;
                }
                case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                {
                    VariableDefinition v = Fingerprint.Local(ins, body);
                    Bits b = Pop(s);
                    bool wide = IsWide(v.VariableType);
                    if (!wide) b.Wide = false;
                    else if (!b.Wide) b = Widen(b, true);   // an int32 into an int64 local: sign-extended
                    s.Locals[v.Index] = Bits.Norm(b);
                    return true;
                }
                case Code.Conv_I8: { Bits b = Pop(s); s.Stack.Add(b.Wide ? b : Widen(b, true)); return true; }
                case Code.Conv_U8: { Bits b = Pop(s); s.Stack.Add(b.Wide ? b : Widen(b, false)); return true; }
                case Code.Conv_I4: case Code.Conv_U4: { Bits b = Pop(s); b.Wide = false; s.Stack.Add(Bits.Norm(b)); return true; }
                case Code.And: case Code.Or: case Code.Xor:
                {
                    Bits b = Pop(s), a = Pop(s);
                    ulong a1 = a.Mask & a.Value, a0 = a.Mask & ~a.Value, b1 = b.Mask & b.Value, b0 = b.Mask & ~b.Value;
                    Bits r = new Bits { Wide = a.Wide || b.Wide };
                    if (c == Code.And) { r.Value = a1 & b1; r.Mask = (a1 & b1) | a0 | b0; }
                    else if (c == Code.Or) { r.Value = a1 | b1; r.Mask = a1 | b1 | (a0 & b0); }
                    else { r.Mask = a.Mask & b.Mask; r.Value = (a.Value ^ b.Value) & r.Mask; }
                    s.Stack.Add(Bits.Norm(r));
                    return true;
                }
                case Code.Shl: case Code.Shr_Un:
                {
                    Bits n = Pop(s), a = Pop(s);
                    if (!n.KnownLow32) { s.Stack.Add(new Bits { Wide = a.Wide }); return true; }
                    int k = n.Low32 & (a.Wide ? 63 : 31);
                    ulong width = a.Wide ? ~0UL : 0xFFFFFFFFUL;
                    Bits r = new Bits { Wide = a.Wide };
                    if (c == Code.Shl) { r.Mask = ((a.Mask << k) | ((1UL << k) - 1)) & width; r.Value = (a.Value << k) & width; }
                    else { r.Mask = (((a.Mask & width) >> k) | ~(width >> k)) & width; r.Value = ((a.Value & width) >> k) & width; }
                    s.Stack.Add(Bits.Norm(r));
                    return true;
                }
            }
            // anything else: its pops, then unknown pushes
            int pops = StackCheck.Pops(ins, null, false);
            if (c == Code.Ret) pops = s.Stack.Count;
            for (int k = 0; k < pops; k++) Pop(s);
            int pushes = StackCheck.Pushes(ins);
            var m = ins.Operand as MethodReference;
            for (int k = 0; k < pushes; k++)
                s.Stack.Add(new Bits { Wide = m != null && IsWide(ReturnType(m)) || c == Code.Ldind_I8 || c == Code.Ldelem_I8 });
            return true;
        }

        static TypeReference ReturnType(MethodReference m)
        {
            var g = m as GenericInstanceMethod;
            if (g != null && m.ReturnType.IsGenericParameter && g.GenericArguments.Count == 1) return g.GenericArguments[0];
            return m.ReturnType;
        }

        // int32 to int64: the upper half known when bit 31 is (sign) or always 0 (unsigned).
        static Bits Widen(Bits b, bool signed)
        {
            var r = new Bits { Mask = b.Mask & 0xFFFFFFFFUL, Value = b.Value & 0xFFFFFFFFUL, Wide = true };
            if (!signed) r.Mask |= 0xFFFFFFFF00000000UL;
            else if ((b.Mask & 0x80000000UL) != 0)
            {
                r.Mask |= 0xFFFFFFFF00000000UL;
                if ((b.Value & 0x80000000UL) != 0) r.Value |= 0xFFFFFFFF00000000UL;
            }
            return r;
        }
    }
}
